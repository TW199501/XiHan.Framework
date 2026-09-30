// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Timing;

namespace XiHan.Framework.Quotas.Stores;

/// <summary>
/// 配额用量存储的进程内默认实现
/// </summary>
/// <remarks>
/// <para>
/// 同一「租户 + 配额项 + 计量周期」桶内的预留、提交与释放互相原子可见，只保证本进程内有界原子性，
/// 不假装跨实例一致：多实例部署必须换用持久化存储。
/// </para>
/// <para>
/// 周期与过期一律按 UTC 判定，取自 <see cref="IClock.Now"/> 并按其 Kind 归一化，Kind 未标注时按 UTC 处理。
/// 记录数与桶数分别受 <see cref="XiHanQuotasOptions.MaxTrackedReservations"/> 与
/// <see cref="XiHanQuotasOptions.MaxTrackedBuckets"/> 约束：到达上限时先回收过期预留与超出保留期的去重记录，
/// 仍放不下则按容量不足拒绝新预留，绝不驱逐仍在占用额度的活动预留。
/// </para>
/// <para>
/// 提交与释放按预留标识定位到它原来所属的桶，因此周期滚动后的迟到结算只改动旧桶，不污染新周期。
/// 超额、冲突与终态不可回退都以类型化结果返回，不抛业务异常。
/// </para>
/// <para>
/// 显式无限额政策不参与用量计数：它只跟踪预留记录以维持状态机与去重，桶的已提交与已预留恒不计入该记录。
/// 因此把政策从无限额改成有限额后，用量从零点起算，无限额期间的消耗不计入新上限；
/// 反向改成无限额时，既有记录仍按它预留当时的记账判定结算或到期释放，不会被卡住。
/// 无限额配额的用量观测不属于配额计数器。
/// </para>
/// </remarks>
public class DefaultQuotaStore : IQuotaStore
{
    private readonly IClock _clock;
    private readonly XiHanQuotasOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<OperationKey, TrackedReservation> _operations = [];
    private readonly Dictionary<BucketKey, Bucket> _buckets = [];

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clock">时钟</param>
    /// <param name="options">配额选项</param>
    /// <exception cref="ArgumentNullException">时钟或选项为 null</exception>
    /// <exception cref="ArgumentOutOfRangeException">选项中的时长或容量不是正数</exception>
    public DefaultQuotaStore(IClock clock, IOptions<XiHanQuotasOptions> options)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

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

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.MaxTrackedReservations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.MaxTrackedBuckets);

        _clock = clock;
        _options = value;
    }

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

        var utcNow = UtcNow();
        lock (_sync)
        {
            var key = new OperationKey(request.TenantId, request.QuotaKey, request.OperationId);
            if (_operations.TryGetValue(key, out var existing))
            {
                if (IsDroppable(existing, utcNow))
                {
                    Retire(existing);
                }
                else
                {
                    return Task.FromResult(ReplayOrConflict(existing, request, utcNow));
                }
            }

            var periodStart = request.Policy.ResolvePeriodStart(utcNow);
            var bucket = FindBucket(request.TenantId, request.QuotaKey, request.Policy.Period, periodStart);
            if (bucket is null && !TryCreateBucket(request, periodStart, utcNow, out bucket))
            {
                return Task.FromResult(new QuotaReserveResult(
                    QuotaReserveStatus.CapacityExhausted, null, new QuotaUsage(request.Policy.Limit, 0, 0)));
            }

            ReclaimBucket(bucket!, utcNow);
            if (_operations.Count >= _options.MaxTrackedReservations)
            {
                ReclaimEverywhere(utcNow);
            }

            if (_operations.Count >= _options.MaxTrackedReservations)
            {
                return Task.FromResult(new QuotaReserveResult(
                    QuotaReserveStatus.CapacityExhausted, null, Snapshot(bucket!, request.Policy.Limit)));
            }

            var limit = request.Policy.Limit;
            if (limit is { } bounded)
            {
                // 先饱和求和再比剩余量：三项直接相加会在 long.MaxValue 附近环绕成负数而超额放行
                var used = SaturatingAdd(bucket!.Committed, bucket.Reserved);
                var remaining = used >= bounded ? 0 : bounded - used;
                if (request.Amount > remaining)
                {
                    return Task.FromResult(new QuotaReserveResult(
                        QuotaReserveStatus.Exceeded, null, Snapshot(bucket, bounded)));
                }

                bucket.Reserved += request.Amount;
            }

            var reservation = new QuotaReservation(
                request.TenantId,
                request.QuotaKey,
                bucket!.PeriodStart,
                request.OperationId,
                request.Amount,
                request.Policy.Version,
                utcNow.Add(request.ReservationTtl ?? _options.DefaultReservationTtl),
                QuotaReservationState.Reserved);

            var tracked = new TrackedReservation(reservation, bucket, limit is not null);
            bucket.Values.Add(tracked);
            bucket.LastTouchedUtc = utcNow;
            _operations[key] = tracked;

            return Task.FromResult(new QuotaReserveResult(
                limit is not null ? QuotaReserveStatus.Reserved : QuotaReserveStatus.Unlimited,
                reservation,
                Snapshot(bucket, limit)));
        }
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

        var utcNow = UtcNow();
        lock (_sync)
        {
            var bucket = FindBucket(tenantId, quotaKey, policy.Period, policy.ResolvePeriodStart(utcNow));
            if (bucket is null)
            {
                return Task.FromResult(new QuotaUsage(policy.Limit, 0, 0));
            }

            ReclaimBucket(bucket, utcNow);
            return Task.FromResult(Snapshot(bucket, policy.Limit));
        }
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

        var utcNow = UtcNow();
        lock (_sync)
        {
            var lookup = new OperationKey(key.TenantId, key.QuotaKey, key.OperationId);
            if (!_operations.TryGetValue(lookup, out var tracked))
            {
                return Task.FromResult(new QuotaSettlementResult(
                    QuotaSettlementStatus.NotFound, null, new QuotaUsage(null, 0, 0)));
            }

            if (tracked.Reservation.State == QuotaReservationState.Reserved && tracked.Reservation.ExpiresAt <= utcNow)
            {
                Expire(tracked, utcNow);
            }

            return Task.FromResult(
                target == QuotaReservationState.Committed ? Commit(tracked, utcNow) : Release(tracked, utcNow));
        }
    }

    /// <summary>
    /// 把预留转为已提交
    /// </summary>
    /// <param name="tracked">被跟踪的预留</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <returns>结算结果</returns>
    private static QuotaSettlementResult Commit(TrackedReservation tracked, DateTimeOffset utcNow)
    {
        var bucket = tracked.Bucket;
        var reservation = tracked.Reservation;

        switch (reservation.State)
        {
            case QuotaReservationState.Reserved:
                tracked.Reservation = reservation.WithState(QuotaReservationState.Committed);
                tracked.SettledUtc = utcNow;
                if (tracked.Accounted)
                {
                    bucket.Reserved -= reservation.Amount;
                    bucket.Committed += reservation.Amount;
                }

                bucket.LastTouchedUtc = utcNow;
                return Settled(QuotaSettlementStatus.Committed, tracked);
            case QuotaReservationState.Committed:
                return Settled(QuotaSettlementStatus.AlreadyCommitted, tracked);
            case QuotaReservationState.Expired:
                return Settled(QuotaSettlementStatus.Expired, tracked);
            default:
                return Settled(QuotaSettlementStatus.TerminalConflict, tracked);
        }
    }

    /// <summary>
    /// 把预留转为已释放
    /// </summary>
    /// <param name="tracked">被跟踪的预留</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <returns>结算结果</returns>
    private static QuotaSettlementResult Release(TrackedReservation tracked, DateTimeOffset utcNow)
    {
        var bucket = tracked.Bucket;
        var reservation = tracked.Reservation;

        switch (reservation.State)
    {
            case QuotaReservationState.Reserved:
                tracked.Reservation = reservation.WithState(QuotaReservationState.Released);
                tracked.SettledUtc = utcNow;
                if (tracked.Accounted)
                {
                    bucket.Reserved -= reservation.Amount;
                }

                bucket.LastTouchedUtc = utcNow;
                return Settled(QuotaSettlementStatus.Released, tracked);
            case QuotaReservationState.Released:
                return Settled(QuotaSettlementStatus.AlreadyReleased, tracked);
            case QuotaReservationState.Expired:
                return Settled(QuotaSettlementStatus.Expired, tracked);
            default:
                return Settled(QuotaSettlementStatus.TerminalConflict, tracked);
        }
    }

    /// <summary>
    /// 构造结算结果
    /// </summary>
    /// <param name="status">结算状态</param>
    /// <param name="tracked">被跟踪的预留</param>
    /// <returns>结算结果</returns>
    private static QuotaSettlementResult Settled(QuotaSettlementStatus status, TrackedReservation tracked)
    {
        return new QuotaSettlementResult(status, tracked.Reservation, Snapshot(tracked.Bucket, tracked.Bucket.Limit));
    }

    /// <summary>
    /// 重试或复用已存在的预留
    /// </summary>
    /// <param name="existing">已跟踪的预留</param>
    /// <param name="request">新的预留请求</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <returns>预留结果</returns>
    private QuotaReserveResult ReplayOrConflict(
        TrackedReservation existing, QuotaReserveRequest request, DateTimeOffset utcNow)
    {
        if (existing.Reservation.State == QuotaReservationState.Reserved && existing.Reservation.ExpiresAt <= utcNow)
        {
            Expire(existing, utcNow);
        }

        var sameOperation = existing.Reservation.Amount == request.Amount &&
            string.Equals(existing.Reservation.PolicyVersion, request.Policy.Version, StringComparison.Ordinal);

        var replayable = sameOperation &&
            existing.Reservation.State is QuotaReservationState.Reserved or QuotaReservationState.Committed;

        return new QuotaReserveResult(
            replayable ? QuotaReserveStatus.Replayed : QuotaReserveStatus.Conflict,
            existing.Reservation,
            Snapshot(existing.Bucket, request.Policy.Limit));
    }

    /// <summary>
    /// 让过期预留归还额度并留作去重记录
    /// </summary>
    /// <param name="tracked">被跟踪的预留</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    private static void Expire(TrackedReservation tracked, DateTimeOffset utcNow)
    {
        // 到期时刻才是预留真正进入终态的时刻：清扫可能远晚于它发生，用当前时刻会变相延长保留期
        tracked.SettledUtc = tracked.Reservation.ExpiresAt;
        tracked.Reservation = tracked.Reservation.WithState(QuotaReservationState.Expired);
        if (tracked.Accounted)
        {
            tracked.Bucket.Reserved -= tracked.Reservation.Amount;
        }

        tracked.Bucket.LastTouchedUtc = utcNow;
    }

    /// <summary>
    /// 新建桶
    /// </summary>
    /// <param name="request">预留请求</param>
    /// <param name="periodStart">周期起始</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <param name="bucket">新建的桶</param>
    /// <returns>新建成功返回 true；配额桶数量已达上限且无可回收桶时返回 false</returns>
    private bool TryCreateBucket(
        QuotaReserveRequest request, DateTimeOffset periodStart, DateTimeOffset utcNow, out Bucket? bucket)
    {
        bucket = null;
        if (_buckets.Count >= _options.MaxTrackedBuckets && !EvictRetiredBuckets(utcNow))
        {
            return false;
        }

        var periodEnd = request.Policy.ResolvePeriodEnd(periodStart);
        var created = new Bucket(
            periodStart,
            periodEnd,
            request.Policy.Limit,
            request.Policy.Period == QuotaPeriod.None
                ? _options.NonPeriodicTombstoneRetention
                : periodEnd - periodStart);
        _buckets[new BucketKey(request.TenantId, request.QuotaKey, request.Policy.Period, periodStart)] = created;
        bucket = created;
        return true;
    }

    /// <summary>
    /// 查找桶
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="period">计量周期</param>
    /// <param name="periodStart">周期起始</param>
    /// <returns>桶，未命中为 null</returns>
    private Bucket? FindBucket(
        long tenantId, string quotaKey, QuotaPeriod period, DateTimeOffset periodStart)
    {
        return _buckets.TryGetValue(new BucketKey(tenantId, quotaKey, period, periodStart), out var bucket)
            ? bucket
            : null;
    }

    /// <summary>
    /// 回收桶内过期预留与超出保留期的去重记录
    /// </summary>
    /// <param name="bucket">目标桶</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    private void ReclaimBucket(Bucket bucket, DateTimeOffset utcNow)
    {
        foreach (var tracked in bucket.Values)
        {
            if (tracked.Reservation.State == QuotaReservationState.Reserved && tracked.Reservation.ExpiresAt <= utcNow)
            {
                Expire(tracked, utcNow);
            }
        }

        DropRetired(bucket, utcNow);
    }

    /// <summary>
    /// 回收所有桶：先让到期预留过期，再丢弃超出保留期的去重记录
    /// </summary>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <remarks>
    /// 必须两步都做：只丢不转会让「到期但无人再结算」的记录永远停在预留中状态，
    /// 既不能被丢弃，也会让它所属的桶永远过不了驱逐判定。
    /// </remarks>
    private void ReclaimEverywhere(DateTimeOffset utcNow)
    {
        foreach (var bucket in _buckets.Values)
        {
            ReclaimBucket(bucket, utcNow);
        }
    }

    /// <summary>
    /// 丢弃单桶内已过保留期的去重记录
    /// </summary>
    /// <param name="bucket">目标桶</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    private void DropRetired(Bucket bucket, DateTimeOffset utcNow)
    {
        for (var index = bucket.Values.Count - 1; index >= 0; index--)
        {
            var tracked = bucket.Values[index];
            if (!IsDroppable(tracked, utcNow))
            {
                continue;
            }

            Retire(tracked);
        }
    }

    /// <summary>
    /// 把去重记录从其所属桶与索引中摘除
    /// </summary>
    /// <param name="tracked">被跟踪的预留</param>
    private void Retire(TrackedReservation tracked)
    {
        tracked.Bucket.Values.Remove(tracked);
        _operations.Remove(tracked.Key);
    }

    /// <summary>
    /// 判断记录是否可丢弃：已进入终态且已过保留期
    /// </summary>
    /// <param name="tracked">被跟踪的预留</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <returns>可丢弃返回 true</returns>
    private static bool IsDroppable(TrackedReservation tracked, DateTimeOffset utcNow)
    {
        return tracked.Reservation.State != QuotaReservationState.Reserved &&
               tracked.SettledUtc is { } settled &&
               settled + tracked.Bucket.Retention <= utcNow;
    }

    /// <summary>
    /// 驱逐周期已结束且记录全部过保留期的桶
    /// </summary>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <returns>是否至少驱逐了一个桶</returns>
    private bool EvictRetiredBuckets(DateTimeOffset utcNow)
    {
        var evicted = false;
        foreach (var pair in _buckets.ToArray())
        {
            // 先回收该桶：桶里若还留着到期未转终态的预留，驱逐判定会永远为假
            ReclaimBucket(pair.Value, utcNow);
            if (!IsRetiredBucket(pair.Value, utcNow))
            {
                continue;
            }

            foreach (var tracked in pair.Value.Values)
            {
                _operations.Remove(tracked.Key);
            }

            _buckets.Remove(pair.Key);
            evicted = true;
        }

        return evicted;
    }

    /// <summary>
    /// 判断桶能否整体丢弃
    /// </summary>
    /// <param name="bucket">目标桶</param>
    /// <param name="utcNow">当前 UTC 时刻</param>
    /// <returns>可整体丢弃返回 true</returns>
    /// <remarks>
    /// 周期未结束的桶仍在服务当前口径，不可丢弃；累计型桶承载跨周期不清零的已提交用量，永不丢弃。
    /// </remarks>
    private static bool IsRetiredBucket(Bucket bucket, DateTimeOffset utcNow)
    {
        if (bucket.PeriodEnd == DateTimeOffset.MaxValue || bucket.PeriodEnd > utcNow)
        {
            return false;
        }

        return bucket.Values.All(tracked => IsDroppable(tracked, utcNow));
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
    /// 非负long相加，溢出时钉在 long.MaxValue
    /// </summary>
    /// <param name="left">左值，非负</param>
    /// <param name="right">右值，非负</param>
    /// <returns>和不小于两者，溢出时为 <see cref="long.MaxValue"/></returns>
    private static long SaturatingAdd(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    /// 构造用量快照
    /// </summary>
    /// <param name="bucket">目标桶</param>
    /// <param name="limit">本次判定依据的上限</param>
    /// <returns>用量快照</returns>
    private static QuotaUsage Snapshot(Bucket bucket, long? limit)
    {
        return new QuotaUsage(limit, bucket.Committed, bucket.Reserved);
    }

    /// <summary>
    /// 预留标识
    /// </summary>
    /// <param name="TenantId">租户标识</param>
    /// <param name="QuotaKey">配额项标识</param>
    /// <param name="OperationId">操作标识</param>
    private readonly record struct OperationKey(long TenantId, string QuotaKey, string OperationId);

    /// <summary>
    /// 桶标识
    /// </summary>
    /// <param name="TenantId">租户标识</param>
    /// <param name="QuotaKey">配额项标识</param>
    /// <param name="Period">计量周期</param>
    /// <param name="PeriodStart">周期起始</param>
    /// <remarks>
    /// 必须带计量周期：日周期与月周期的起点在每月一日 00:00 是同一个瞬间，
    /// 只按起点定键会让两种节奏共用一个桶，月度记录被按日保留期回收。
    /// </remarks>
    private readonly record struct BucketKey(
        long TenantId, string QuotaKey, QuotaPeriod Period, DateTimeOffset PeriodStart);

    /// <summary>
    /// 被跟踪的预留
    /// </summary>
    /// <param name="reservation">预留记录</param>
    /// <param name="bucket">所属桶</param>
    /// <param name="accounted">预留当时是否参与用量计数</param>
    private sealed class TrackedReservation(QuotaReservation reservation, Bucket bucket, bool accounted)
    {
        /// <summary>
        /// 预留记录
        /// </summary>
        public QuotaReservation Reservation { get; set; } = reservation;

        /// <summary>
        /// 进入终态的时刻，仍在预留中时为 null
        /// </summary>
        public DateTimeOffset? SettledUtc { get; set; }

        /// <summary>
        /// 所属桶
        /// </summary>
        public Bucket Bucket { get; } = bucket;

        /// <summary>
        /// 预留当时是否参与用量计数，结算沿用该判定而不重新取当前政策
        /// </summary>
        public bool Accounted { get; } = accounted;

        /// <summary>
        /// 本记录对应的预留标识
        /// </summary>
        public OperationKey Key => new(Reservation.TenantId, Reservation.QuotaKey, Reservation.OperationId);
    }

    /// <summary>
    /// 计量周期桶
    /// </summary>
    /// <param name="periodStart">周期起始</param>
    /// <param name="periodEnd">周期结束，累计型为 <see cref="DateTimeOffset.MaxValue"/></param>
    /// <param name="limit">建立本桶时依据的上限，仅供回报用量</param>
    /// <param name="retention">去重记录保留时长</param>
    private sealed class Bucket(
        DateTimeOffset periodStart, DateTimeOffset periodEnd, long? limit, TimeSpan retention)
    {
        /// <summary>
        /// 已提交用量
        /// </summary>
        public long Committed { get; set; }

        /// <summary>
        /// 已预留未定稿用量
        /// </summary>
        public long Reserved { get; set; }

        /// <summary>
        /// 本桶内的跟踪记录
        /// </summary>
        public List<TrackedReservation> Values { get; } = [];

        /// <summary>
        /// 最后改动时刻
        /// </summary>
        public DateTimeOffset LastTouchedUtc { get; set; }

        /// <summary>
        /// 周期起始
        /// </summary>
        public DateTimeOffset PeriodStart { get; } = periodStart;

        /// <summary>
        /// 周期结束
        /// </summary>
        public DateTimeOffset PeriodEnd { get; } = periodEnd;

        /// <summary>
        /// 建立本桶时依据的上限
        /// </summary>
        public long? Limit { get; } = limit;

        /// <summary>
        /// 去重记录保留时长
        /// </summary>
        public TimeSpan Retention { get; } = retention;
    }
}
