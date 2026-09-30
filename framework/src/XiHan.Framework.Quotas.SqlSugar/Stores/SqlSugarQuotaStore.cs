// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas.SqlSugar.Entities;
using XiHan.Framework.Quotas.SqlSugar.Options;
using XiHan.Framework.Timing;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Options;

namespace XiHan.Framework.Quotas.SqlSugar.Stores;

/// <summary>
/// 配额用量存储的 SqlSugar 实现
/// </summary>
/// <remarks>
/// <para>
/// 去重以 <c>sys_quota_reservation</c> 的唯一索引当闸门：先插预留行，再对桶做一条带条件的更新
/// （<c>Committed + Reserved &lt;= 上限 - 本次预留量</c>）在数据库内判定扣额，
/// 所以并发对手不会双双放行，同标识重试也不会多扣一笔。额度不足时整个工作单元回滚，不留半成品记录。
/// </para>
/// <para>
/// 每个操作都开一个独立的事务型工作单元（<c>requiresNew</c>），不加入调用方的工作单元：
/// 预留必须独立于业务事务先落账，否则业务回滚会把已放行的预留一并回滚，先占额度的语义失效。
/// 表只建在平台库，两张表都不实现 <c>IMultiTenantEntity</c>，读写一律按显式租户标识限定，
/// 因此不受环境租户过滤器影响。
/// </para>
/// <para>
/// 与进程内默认实现同口径的限制：去重窗口由所属周期的保留期决定，不周期用
/// <see cref="XiHanQuotasOptions.NonPeriodicTombstoneRetention"/> 兜底，超出窗口后同标识重用视为新预留；
/// 过期预留的回收发生在被访问的桶上（含用量查询），没有后台清理任务。
/// 累计型政策的周期起始落库为 <see cref="DateTime.MinValue"/>，该列需能表示这一时刻
/// （SQL Server 要用 datetime2 类型）。
/// </para>
/// </remarks>
public class SqlSugarQuotaStore : IQuotaStore
{
    private const int SweepBatchSize = 200;

    private readonly IClock _clock;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly XiHanQuotasOptions _options;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">SqlSugar 客户端解析器</param>
    /// <param name="unitOfWorkManager">工作单元管理器</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="clock">时钟</param>
    /// <param name="options">配额选项</param>
    /// <param name="coreOptions">数据访问配置</param>
    /// <param name="sqlSugarOptions">配额 SqlSugar 配置</param>
    /// <exception cref="ArgumentNullException">任一依赖为 null</exception>
    /// <exception cref="ArgumentOutOfRangeException">选项中的时长不是正数</exception>
    public SqlSugarQuotaStore(
        ISqlSugarClientResolver clientResolver,
        IUnitOfWorkManager unitOfWorkManager,
        IDistributedIdGenerator<long> idGenerator,
        IClock clock,
        IOptions<XiHanQuotasOptions> options,
        IOptions<XiHanSqlSugarCoreOptions> coreOptions,
        IOptions<XiHanQuotasSqlSugarOptions> sqlSugarOptions)
    {
        ArgumentNullException.ThrowIfNull(clientResolver);
        ArgumentNullException.ThrowIfNull(unitOfWorkManager);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(coreOptions);
        ArgumentNullException.ThrowIfNull(sqlSugarOptions);

        var value = options.Value;
        if (value.DefaultReservationTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), value.DefaultReservationTtl, "预留默认存活时长必须大于零。");
        }

        if (value.NonPeriodicTombstoneRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), value.NonPeriodicTombstoneRetention, "不周期去重保留时长必须大于零。");
        }

        var configuredConfigId = sqlSugarOptions.Value.ConfigId;
        ConfigId = string.IsNullOrWhiteSpace(configuredConfigId)
            ? coreOptions.Value.DefaultConfigId
            : configuredConfigId.Trim();

        _clientResolver = clientResolver;
        _unitOfWorkManager = unitOfWorkManager;
        _idGenerator = idGenerator;
        _clock = clock;
        _options = value;
    }

    /// <summary>
    /// 配额数据表所在连接的配置标识
    /// </summary>
    public string ConfigId { get; }

    /// <summary>
    /// 原子预留用量
    /// </summary>
    /// <param name="request">预留请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>预留结果</returns>
    /// <exception cref="ArgumentNullException">请求为 null</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    public Task<QuotaReserveResult> ReserveAsync(
        QuotaReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ExecuteAsync(async (client, unitOfWork) =>
        {
            var utcNow = UtcNow();
            var now = ToDbInstant(utcNow);
            var periodStart = request.Policy.ResolvePeriodStart(utcNow);
            var period = ToDbInstant(periodStart);

            var existing = await FindReservationAsync(client, request.TenantId, request.QuotaKey, request.OperationId)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return await ReplayOrConflictAsync(
                    client, existing, request.Amount, request.Policy.Version, request.Policy.Limit, now)
                    .ConfigureAwait(false);
            }

            var bucketId = 0L;
            if (request.Policy.Limit is not null)
            {
                var bucket = await EnsureBucketAsync(client, request, period).ConfigureAwait(false);
                bucketId = bucket.BasicId;
                await SweepExpiredAsync(client, bucketId, now).ConfigureAwait(false);
            }

            var row = NewReservation(
                request,
                bucketId,
                period,
                ToDbInstant(AddSaturating(utcNow, request.ReservationTtl ?? _options.DefaultReservationTtl)));

            var claimedBy = await TryInsertReservationAsync(client, row).ConfigureAwait(false);
            if (claimedBy is not null)
            {
                return await ReplayOrConflictAsync(
                    client, claimedBy, request.Amount, request.Policy.Version, request.Policy.Limit, now)
                    .ConfigureAwait(false);
            }

            if (request.Policy.Limit is not { } limit)
            {
                return new QuotaReserveResult(
                    QuotaReserveStatus.Unlimited, ToModel(row, periodStart), new QuotaUsage(null, 0, 0));
            }

            var reserved = await client.Updateable<SysQuotaBucket>()
                .SetColumns(item => item.Reserved == item.Reserved + request.Amount)
                .Where(item => item.BasicId == bucketId
                    && item.Committed + item.Reserved <= limit - request.Amount)
                .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);

            if (reserved == 0)
            {
                var snapshot = await client.Queryable<SysQuotaBucket>()
                    .FirstAsync(item => item.BasicId == bucketId, cancellationToken).ConfigureAwait(false);

                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return new QuotaReserveResult(QuotaReserveStatus.Exceeded, null, ToUsage(snapshot, limit));
            }

            var afterReserve = await client.Queryable<SysQuotaBucket>()
                .FirstAsync(item => item.BasicId == bucketId, cancellationToken).ConfigureAwait(false);

            return new QuotaReserveResult(
                QuotaReserveStatus.Reserved,
                ToModel(row, periodStart),
                ToUsage(afterReserve, limit));
        }, cancellationToken);
    }

    /// <summary>
    /// 提交预留，把预留量转为已用
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果；预留已过期时拒绝且不重新扣额</returns>
    /// <exception cref="ArgumentNullException">预留标识为 null</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    public Task<QuotaSettlementResult> CommitAsync(
        QuotaReservationKey key, CancellationToken cancellationToken = default)
    {
        return SettleAsync(key, QuotaReservationState.Committed, cancellationToken);
    }

    /// <summary>
    /// 释放预留，把预留量归还配额
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    /// <exception cref="ArgumentNullException">预留标识为 null</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    public Task<QuotaSettlementResult> ReleaseAsync(
        QuotaReservationKey key, CancellationToken cancellationToken = default)
    {
        return SettleAsync(key, QuotaReservationState.Released, cancellationToken);
    }

    /// <summary>
    /// 查询某租户在指定政策当前周期内的用量
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="policy">配额政策，决定统计哪个周期</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用量快照；该周期尚无记录时返回零用量</returns>
    /// <exception cref="ArgumentNullException">政策为 null</exception>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">配额项标识为空白</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    public Task<QuotaUsage> FindUsageAsync(
        long tenantId,
        string quotaKey,
        QuotaPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
        cancellationToken.ThrowIfCancellationRequested();

        if (policy.IsUnlimited)
        {
            return Task.FromResult(new QuotaUsage(null, 0, 0));
        }

        return ExecuteAsync(async (client, _) =>
        {
            var utcNow = UtcNow();
            var now = ToDbInstant(utcNow);
            var period = ToDbInstant(policy.ResolvePeriodStart(utcNow));
            var periodValue = (int)policy.Period;

            var bucket = await FindBucketByScopeAsync(client, tenantId, quotaKey, periodValue, period)
                .ConfigureAwait(false);
            if (bucket is null)
            {
                return new QuotaUsage(policy.Limit, 0, 0);
            }

            await SweepExpiredAsync(client, bucket.BasicId, now).ConfigureAwait(false);

            var fresh = await client.Queryable<SysQuotaBucket>()
                .FirstAsync(item => item.BasicId == bucket.BasicId, cancellationToken).ConfigureAwait(false);

            return ToUsage(fresh ?? bucket, policy.Limit);
        }, cancellationToken);
    }

    /// <summary>
    /// 提交与释放的公共结算
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="target">目标终态</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    private Task<QuotaSettlementResult> SettleAsync(
        QuotaReservationKey key, QuotaReservationState target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        return ExecuteAsync(async (client, _) =>
        {
            var now = ToDbInstant(UtcNow());
            var row = await FindReservationAsync(client, key.TenantId, key.QuotaKey, key.OperationId)
                .ConfigureAwait(false);
            if (row is null)
            {
                return new QuotaSettlementResult(QuotaSettlementStatus.NotFound, null, null);
            }

            if (row.State == (int)QuotaReservationState.Reserved && row.ExpiresAt <= now)
            {
                await ExpireAsync(client, row).ConfigureAwait(false);
            }

            return await FlipAsync(client, row, now, target, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// 把预留推进到目标终态
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="row">预留行</param>
    /// <param name="now">当前 UTC 时刻的落库值</param>
    /// <param name="target">目标终态</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    private static async Task<QuotaSettlementResult> FlipAsync(
        ISqlSugarClient client,
        SysQuotaReservation row,
        DateTime now,
        QuotaReservationState target,
        CancellationToken cancellationToken)
    {
        var current = (QuotaReservationState)row.State;
        var settledStatus = current switch
        {
            QuotaReservationState.Committed => target == QuotaReservationState.Committed
                ? QuotaSettlementStatus.AlreadyCommitted
                : QuotaSettlementStatus.TerminalConflict,
            QuotaReservationState.Released => target == QuotaReservationState.Released
                ? QuotaSettlementStatus.AlreadyReleased
                : QuotaSettlementStatus.TerminalConflict,
            QuotaReservationState.Expired => QuotaSettlementStatus.Expired,
            _ => (QuotaSettlementStatus?)null
        };

        if (settledStatus is { } already)
        {
            return await ReportAsync(client, row, already).ConfigureAwait(false);
        }

        var flipped = await client.Updateable<SysQuotaReservation>()
            .SetColumns(item => new SysQuotaReservation { State = (int)target, SettledAt = now })
            .Where(item => item.BasicId == row.BasicId && item.State == (int)QuotaReservationState.Reserved)
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);

        if (flipped == 0)
        {
            var fresh = await client.Queryable<SysQuotaReservation>()
                .FirstAsync(item => item.BasicId == row.BasicId, cancellationToken).ConfigureAwait(false);

            return fresh is null
                ? new QuotaSettlementResult(QuotaSettlementStatus.NotFound, null, null)
                : await FlipAsync(client, fresh, now, target, cancellationToken).ConfigureAwait(false);
        }

        row.State = (int)target;
        row.SettledAt = now;

        if (row.BucketId != 0)
        {
            if (target == QuotaReservationState.Committed)
            {
                await client.Updateable<SysQuotaBucket>()
                    .SetColumns(item => new SysQuotaBucket
                    {
                        Committed = item.Committed + row.Amount,
                        Reserved = item.Reserved - row.Amount
                    })
                    .Where(item => item.BasicId == row.BucketId)
                    .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.Updateable<SysQuotaBucket>()
                    .SetColumns(item => item.Reserved == item.Reserved - row.Amount)
                    .Where(item => item.BasicId == row.BucketId)
                    .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await ReportAsync(
            client,
            row,
            target == QuotaReservationState.Committed ? QuotaSettlementStatus.Committed : QuotaSettlementStatus.Released)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 让一条到期预留转终态并归还占用
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="row">预留行</param>
    private static async Task ExpireAsync(ISqlSugarClient client, SysQuotaReservation row)
    {
        var flipped = await client.Updateable<SysQuotaReservation>()
            .SetColumns(item => new SysQuotaReservation
            {
                State = (int)QuotaReservationState.Expired,
                SettledAt = row.ExpiresAt
            })
            .Where(item => item.BasicId == row.BasicId && item.State == (int)QuotaReservationState.Reserved)
            .ExecuteCommandAsync().ConfigureAwait(false);

        if (flipped == 0 || row.BucketId == 0)
        {
            return;
        }

        row.State = (int)QuotaReservationState.Expired;
        row.SettledAt = row.ExpiresAt;

        await client.Updateable<SysQuotaBucket>()
            .SetColumns(item => item.Reserved == item.Reserved - row.Amount)
            .Where(item => item.BasicId == row.BucketId)
            .ExecuteCommandAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 回收桶内到期未结算的预留
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="bucketId">桶标识</param>
    /// <param name="now">当前 UTC 时刻的落库值</param>
    private static async Task SweepExpiredAsync(ISqlSugarClient client, long bucketId, DateTime now)
    {
        var reservedState = (int)QuotaReservationState.Reserved;

        var due = await client.Queryable<SysQuotaReservation>()
            .Where(item => item.BucketId == bucketId && item.State == reservedState && item.ExpiresAt <= now)
            .OrderBy(item => item.BasicId)
            .Take(SweepBatchSize)
            .ToListAsync().ConfigureAwait(false);

        foreach (var row in due)
        {
            await ExpireAsync(client, row).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 重试或复用已存在的预留
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="existing">已存在的预留行</param>
    /// <param name="amount">本次请求的预留量</param>
    /// <param name="policyVersion">本次请求的政策版本</param>
    /// <param name="limit">本次请求的上限</param>
    /// <param name="now">当前 UTC 时刻的落库值</param>
    /// <returns>预留结果</returns>
    private static async Task<QuotaReserveResult> ReplayOrConflictAsync(
        ISqlSugarClient client,
        SysQuotaReservation existing,
        long amount,
        string policyVersion,
        long? limit,
        DateTime now)
    {
        if (existing.State == (int)QuotaReservationState.Reserved && existing.ExpiresAt <= now)
        {
            await ExpireAsync(client, existing).ConfigureAwait(false);
        }

        var replayable = existing.Amount == amount
            && string.Equals(existing.PolicyVersion, policyVersion, StringComparison.Ordinal)
            && existing.State is (int)QuotaReservationState.Reserved or (int)QuotaReservationState.Committed;

        return new QuotaReserveResult(
            replayable ? QuotaReserveStatus.Replayed : QuotaReserveStatus.Conflict,
            ToModel(existing, FromDbInstant(existing.PeriodStart)),
            await BucketUsageAsync(client, existing.BucketId, limit).ConfigureAwait(false));
    }

    /// <summary>
    /// 构造结算结果
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="row">预留行</param>
    /// <param name="status">结算状态</param>
    /// <returns>结算结果</returns>
    private static async Task<QuotaSettlementResult> ReportAsync(
        ISqlSugarClient client, SysQuotaReservation row, QuotaSettlementStatus status)
    {
        return new QuotaSettlementResult(
            status,
            ToModel(row, FromDbInstant(row.PeriodStart)),
            await BucketUsageAsync(client, row.BucketId, null).ConfigureAwait(false));
    }

    /// <summary>
    /// 读取某桶的用量快照
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="bucketId">桶标识</param>
    /// <param name="limit">本次判定依据的上限，null 时用建桶时记录的上限</param>
    /// <returns>用量快照</returns>
    private static async Task<QuotaUsage> BucketUsageAsync(
        ISqlSugarClient client, long bucketId, long? limit)
    {
        if (bucketId == 0)
        {
            return new QuotaUsage(limit, 0, 0);
        }

        var bucket = await client.Queryable<SysQuotaBucket>()
            .FirstAsync(item => item.BasicId == bucketId).ConfigureAwait(false);

        return ToUsage(bucket, limit ?? bucket?.RecordedLimit);
    }

    /// <summary>
    /// 尝试插入预留行
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="row">待插入的预留行</param>
    /// <returns>预留标识已被对手占走时返回对手的行，否则返回 null</returns>
    private static async Task<SysQuotaReservation?> TryInsertReservationAsync(
        ISqlSugarClient client, SysQuotaReservation row)
    {
        try
        {
            await client.Insertable(row).ExecuteCommandAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var winner = await FindReservationAsync(client, row.TenantId, row.QuotaKey, row.OperationId)
                .ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            _ = ex;
            return winner;
        }

        return null;
    }

    /// <summary>
    /// 取得或新建桶
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="request">预留请求</param>
    /// <param name="period">周期起始的落库值</param>
    /// <returns>桶行</returns>
    private async Task<SysQuotaBucket> EnsureBucketAsync(
        ISqlSugarClient client, QuotaReserveRequest request, DateTime period)
    {
        var periodValue = (int)request.Policy.Period;

        var existing = await FindBucketByScopeAsync(client, request.TenantId, request.QuotaKey, periodValue, period)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var created = new SysQuotaBucket(_idGenerator.NextId())
        {
            TenantId = request.TenantId,
            QuotaKey = request.QuotaKey,
            Period = periodValue,
            PeriodStart = period,
            RecordedLimit = request.Policy.Limit
        };

        try
        {
            await client.Insertable(created).ExecuteCommandAsync().ConfigureAwait(false);

            return created;
        }
        catch (Exception ex)
        {
            var winner = await FindBucketByScopeAsync(client, request.TenantId, request.QuotaKey, periodValue, period)
                .ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            _ = ex;
            return winner;
        }
    }

    /// <summary>
    /// 按作用域查找桶
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="period">计量周期</param>
    /// <param name="periodStart">周期起始</param>
    /// <returns>桶行，未命中为 null</returns>
    private static async Task<SysQuotaBucket?> FindBucketByScopeAsync(
        ISqlSugarClient client, long tenantId, string quotaKey, int period, DateTime periodStart)
    {
        return await client.Queryable<SysQuotaBucket>()
            .Where(item => item.TenantId == tenantId
                && item.QuotaKey == quotaKey
                && item.Period == period
                && item.PeriodStart == periodStart)
            .FirstAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 按预留标识查找预留行
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="operationId">操作标识</param>
    /// <returns>预留行，未命中为 null</returns>
    private static async Task<SysQuotaReservation?> FindReservationAsync(
        ISqlSugarClient client, long tenantId, string quotaKey, string operationId)
    {
        return await client.Queryable<SysQuotaReservation>()
            .Where(item => item.TenantId == tenantId
                && item.QuotaKey == quotaKey
                && item.OperationId == operationId)
            .FirstAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 构造预留行
    /// </summary>
    /// <param name="request">预留请求</param>
    /// <param name="bucketId">桶标识</param>
    /// <param name="period">周期起始的落库值</param>
    /// <param name="expiresAt">到期时刻的落库值</param>
    /// <returns>预留行</returns>
    private SysQuotaReservation NewReservation(
        QuotaReserveRequest request, long bucketId, DateTime period, DateTime expiresAt)
    {
        return new SysQuotaReservation(_idGenerator.NextId())
        {
            TenantId = request.TenantId,
            QuotaKey = request.QuotaKey,
            OperationId = request.OperationId,
            BucketId = bucketId,
            PeriodStart = period,
            Amount = request.Amount,
            PolicyVersion = request.Policy.Version,
            ExpiresAt = expiresAt,
            State = (int)QuotaReservationState.Reserved
        };
    }

    /// <summary>
    /// 在一个独立的事务型工作单元内执行一次存储操作
    /// </summary>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="operation">数据库操作，第二个参数是本操作独占的工作单元</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    private async Task<TResult> ExecuteAsync<TResult>(
        Func<ISqlSugarClient, IUnitOfWork, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var unitOfWork = _unitOfWorkManager.Begin(
            new XiHanUnitOfWorkOptions(isTransactional: true),
            requiresNew: true);

        var client = _clientResolver.GetClient(ConfigId);
        var result = await operation(client, unitOfWork).ConfigureAwait(false);

        // 操作可以自行回滚（额度不足时就这么做），回滚过的工作单元不能再提交
        if (!unitOfWork.IsRolledback)
        {
            await unitOfWork.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 构造用量快照
    /// </summary>
    /// <param name="bucket">桶行</param>
    /// <param name="limit">本次判定依据的上限</param>
    /// <returns>用量快照</returns>
    private static QuotaUsage ToUsage(SysQuotaBucket? bucket, long? limit)
    {
        return new QuotaUsage(limit, bucket?.Committed ?? 0, bucket?.Reserved ?? 0);
    }

    /// <summary>
    /// 把预留行转为契约模型
    /// </summary>
    /// <param name="row">预留行</param>
    /// <param name="periodStart">所属周期起始</param>
    /// <returns>预留模型</returns>
    private static QuotaReservation ToModel(SysQuotaReservation row, DateTimeOffset periodStart)
    {
        return new QuotaReservation(
            row.TenantId,
            row.QuotaKey,
            periodStart,
            row.OperationId,
            row.Amount,
            row.PolicyVersion,
            FromDbInstant(row.ExpiresAt),
            (QuotaReservationState)row.State);
    }

    /// <summary>
    /// 把 UTC 偏移时刻转成落库的 UTC 日期时间
    /// </summary>
    /// <param name="value">时刻</param>
    /// <returns>UTC 日期时间</returns>
    private static DateTime ToDbInstant(DateTimeOffset value)
    {
        return value == DateTimeOffset.MinValue
            ? DateTime.MinValue
            : DateTime.SpecifyKind(value.ToUniversalTime().DateTime, DateTimeKind.Utc);
    }

    /// <summary>
    /// 把落库的 UTC 日期时间转回 UTC 偏移时刻
    /// </summary>
    /// <param name="value">UTC 日期时间</param>
    /// <returns>时刻</returns>
    private static DateTimeOffset FromDbInstant(DateTime value)
    {
        return value == DateTime.MinValue
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
    }

    /// <summary>
    /// 取当前 UTC 时刻
    /// </summary>
    /// <returns>UTC 时刻</returns>
    private DateTimeOffset UtcNow()
    {
        var now = _clock.Now;

        return now.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(now, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(now.ToUniversalTime(), TimeSpan.Zero),
            _ => new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc))
        };
    }

    /// <summary>
    /// 时刻加正时长，超出可表示范围时夹到上界
    /// </summary>
    /// <param name="instant">起始时刻</param>
    /// <param name="duration">时长</param>
    /// <returns>相加后的时刻</returns>
    private static DateTimeOffset AddSaturating(DateTimeOffset instant, TimeSpan duration)
    {
        var room = DateTimeOffset.MaxValue.Ticks - instant.Ticks;

        return duration.Ticks > room ? DateTimeOffset.MaxValue : instant.Add(duration);
    }
}
