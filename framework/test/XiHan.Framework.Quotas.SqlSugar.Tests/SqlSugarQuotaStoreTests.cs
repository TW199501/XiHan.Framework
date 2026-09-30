// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.SqlSugar.Entities;
using XiHan.Framework.Uow.Options;

namespace XiHan.Framework.Quotas.SqlSugar.Tests;

/// <summary>
/// 配额存储的 SqlSugar 实现测试
/// </summary>
public class SqlSugarQuotaStoreTests : IDisposable
{
    private readonly QuotaTestDatabase _database = QuotaTestDatabase.Create();

    /// <summary>
    /// 释放临时库
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 预留后只记已预留，不记已提交
    /// </summary>
    [Fact]
    public async Task 预留后只记已预留()
    {
        var result = await _database.Store.ReserveAsync(Reserve(1001, "op-1", 3));

        Assert.True(result.Allowed);
        Assert.Equal(0L, result.Usage?.Committed);
        Assert.Equal(3L, result.Usage?.Reserved);
        Assert.Equal(1, await _database.CountRawAsync<SysQuotaBucket>());
        Assert.Equal(1, await _database.CountRawAsync<SysQuotaReservation>());
    }

    /// <summary>
    /// 超出上限按超额拒绝且回滚掉已插入的预留行
    /// </summary>
    /// <remarks>
    /// 这条同时验证事务真的生效：插入预留行在扣额判定失败后必须被回滚抹掉，
    /// 否则失败请求会留下一条永远占着去重窗口的记录。
    /// </remarks>
    [Fact]
    public async Task 超出上限拒绝且不留预留行()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 10));

        var exceeded = await _database.Store.ReserveAsync(Reserve(1001, "op-2", 1));

        Assert.False(exceeded.Allowed);
        Assert.Equal(QuotaReserveStatus.Exceeded, exceeded.Status);
        Assert.Equal(10L, exceeded.Usage?.Reserved);
        Assert.Equal(1, await _database.CountRawAsync<SysQuotaReservation>());
    }

    /// <summary>
    /// 同一操作标识重试不重复扣额
    /// </summary>
    [Fact]
    public async Task 同一操作标识重试不重复扣额()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));

        var replay = await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));

        Assert.True(replay.Allowed);
        Assert.Equal(QuotaReserveStatus.Replayed, replay.Status);
        Assert.Equal(4L, replay.Usage?.Reserved);
        Assert.Equal(1, await _database.CountRawAsync<SysQuotaReservation>());
    }

    /// <summary>
    /// 同一操作标识换预留量被判冲突
    /// </summary>
    [Fact]
    public async Task 同一操作标识换预留量被判冲突()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));

        var conflict = await _database.Store.ReserveAsync(Reserve(1001, "op-1", 5));

        Assert.Equal(QuotaReserveStatus.Conflict, conflict.Status);
        Assert.Equal(4L, conflict.Usage?.Reserved);
    }

    /// <summary>
    /// 提交把预留量转为已用
    /// </summary>
    [Fact]
    public async Task 提交把预留量转为已用()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));

        var committed = await _database.Store.CommitAsync(Key(1001, "op-1"));

        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
        Assert.Equal(4L, committed.Usage?.Committed);
        Assert.Equal(0L, committed.Usage?.Reserved);
    }

    /// <summary>
    /// 释放归还额度
    /// </summary>
    [Fact]
    public async Task 释放归还额度()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 10));

        var released = await _database.Store.ReleaseAsync(Key(1001, "op-1"));
        var followUp = await _database.Store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.Equal(QuotaSettlementStatus.Released, released.Status);
        Assert.Equal(0L, released.Usage?.Reserved);
        Assert.True(followUp.Allowed);
    }

    /// <summary>
    /// 提交与释放互斥且终态不可回退
    /// </summary>
    [Fact]
    public async Task 提交与释放互斥且终态不可回退()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));
        await _database.Store.CommitAsync(Key(1001, "op-1"));

        var releaseAfterCommit = await _database.Store.ReleaseAsync(Key(1001, "op-1"));

        Assert.Equal(QuotaSettlementStatus.TerminalConflict, releaseAfterCommit.Status);
        Assert.Equal(4L, releaseAfterCommit.Usage?.Committed);

        await _database.Store.ReserveAsync(Reserve(1001, "op-2", 3));
        await _database.Store.ReleaseAsync(Key(1001, "op-2"));
        var commitAfterRelease = await _database.Store.CommitAsync(Key(1001, "op-2"));

        Assert.Equal(QuotaSettlementStatus.TerminalConflict, commitAfterRelease.Status);
        Assert.Equal(4L, commitAfterRelease.Usage?.Committed);
    }

    /// <summary>
    /// 重复提交与重复释放都幂等
    /// </summary>
    [Fact]
    public async Task 重复提交与重复释放都幂等()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));
        await _database.Store.CommitAsync(Key(1001, "op-1"));
        await _database.Store.ReserveAsync(Reserve(1001, "op-2", 2));
        await _database.Store.ReleaseAsync(Key(1001, "op-2"));

        var againCommit = await _database.Store.CommitAsync(Key(1001, "op-1"));
        var againRelease = await _database.Store.ReleaseAsync(Key(1001, "op-2"));

        Assert.Equal(QuotaSettlementStatus.AlreadyCommitted, againCommit.Status);
        Assert.Equal(4L, againCommit.Usage?.Committed);
        Assert.Equal(QuotaSettlementStatus.AlreadyReleased, againRelease.Status);
        Assert.Equal(4L, againRelease.Usage?.Committed);
    }

    /// <summary>
    /// 到期预留不能提交且额度已归还
    /// </summary>
    [Fact]
    public async Task 到期预留不能提交且额度已归还()
    {
        await _database.Store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 10, Limited(10), TimeSpan.FromMinutes(1)));

        _database.Clock.Advance(TimeSpan.FromMinutes(2));
        var commit = await _database.Store.CommitAsync(Key(1001, "op-1"));
        var retry = await _database.Store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.Equal(QuotaSettlementStatus.Expired, commit.Status);
        Assert.Equal(0L, commit.Usage?.Committed);
        Assert.True(retry.Allowed);
    }

    /// <summary>
    /// 到期未结算的预留在下次同桶预留时将额度归还
    /// </summary>
    [Fact]
    public async Task 到期未结算的预留被下次预留回收()
    {
        var store = _database.Store;
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "zombie", "op-1", 10, Limited(10), TimeSpan.FromMinutes(1)));

        _database.Clock.Advance(TimeSpan.FromMinutes(5));
        var followUp = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "zombie", "op-2", 10, Limited(10)));

        Assert.True(followUp.Allowed);
        Assert.Equal(0L, followUp.Usage?.Committed);
        Assert.Equal(10L, followUp.Usage?.Reserved);
    }

    /// <summary>
    /// 跨租户互不影响
    /// </summary>
    [Fact]
    public async Task 跨租户互不影响()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 10));

        var other = await _database.Store.ReserveAsync(Reserve(1002, "op-1", 10));

        Assert.True(other.Allowed);
        Assert.Equal(2, await _database.CountRawAsync<SysQuotaBucket>());
    }

    /// <summary>
    /// 周期滚动后新周期从零开始，迟到的提交只改动旧周期
    /// </summary>
    [Fact]
    public async Task 迟到的提交只改动所属旧周期()
    {
        await _database.Store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 6, Limited(10), TimeSpan.FromDays(2)));

        _database.Clock.Advance(TimeSpan.FromDays(1));
        var committed = await _database.Store.CommitAsync(Key(1001, "op-1"));
        var currentUsage = await _database.Store.FindUsageAsync(1001, "quota", Limited(10));
        var newPeriod = await _database.Store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
        Assert.Equal(6L, committed.Usage?.Committed);
        Assert.Equal(0L, currentUsage.Committed);
        Assert.True(newPeriod.Allowed);
    }

    /// <summary>
    /// 无限额政策放行且不建桶不记账
    /// </summary>
    [Fact]
    public async Task 无限额政策不建桶不记账()
    {
        var result = await _database.Store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 1_000_000, QuotaPolicy.Unlimited("v1")));

        Assert.Equal(QuotaReserveStatus.Unlimited, result.Status);
        Assert.True(result.Allowed);
        Assert.Null(result.Usage?.Limit);
        Assert.Equal(0L, result.Usage?.Reserved);
        Assert.Equal(0, await _database.CountRawAsync<SysQuotaBucket>());

        var committed = await _database.Store.CommitAsync(Key(1001, "op-1"));
        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
    }

    /// <summary>
    /// 结算一个不存在的预留返回未找到且不回报用量
    /// </summary>
    [Fact]
    public async Task 结算不存在的预留不回报用量()
    {
        var result = await _database.Store.CommitAsync(Key(1001, "missing"));

        Assert.Equal(QuotaSettlementStatus.NotFound, result.Status);
        Assert.Null(result.Usage);
    }

    /// <summary>
    /// 环境租户上下文不吞掉按显式租户标识的读写
    /// </summary>
    /// <remarks>
    /// 配额表不实现 IMultiTenantEntity，因此全局租户过滤器不该作用于它。
    /// 同一夹具里用一张实现该接口的探针表反证过滤器确实装着且在生效。
    /// </remarks>
    [Fact]
    public async Task 环境租户上下文不吞掉显式租户的配额读写()
    {
        await SeedProbeAsync(777);
        await SeedProbeAsync(888);

        _database.CurrentTenant.Change(777);

        var filteredProbe = await _database.Resolver.GetClient(QuotaTestDatabase.ConfigId)
            .Queryable<SysTenantProbe>().ToListAsync();
        Assert.All(filteredProbe, probe => Assert.Equal(777, probe.TenantId));
        Assert.Equal(2, await _database.CountRawAsync<SysTenantProbe>());

        var reserved = await _database.Store.ReserveAsync(Reserve(888, "op-1", 4));
        var usage = await _database.Store.FindUsageAsync(888, "quota", Limited(10));
        var committed = await _database.Store.CommitAsync(Key(888, "op-1"));

        Assert.True(reserved.Allowed);
        Assert.Equal(4L, usage.Reserved);
        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
        Assert.Equal(4L, committed.Usage?.Committed);
    }

    /// <summary>
    /// 平台态按显式租户标识读写配额正常
    /// </summary>
    [Fact]
    public async Task 平台态读写配额正常()
    {
        _database.CurrentTenant.Change(null);

        var reserved = await _database.Store.ReserveAsync(Reserve(0, "op-1", 4));
        var usage = await _database.Store.FindUsageAsync(0, "quota", Limited(10));
        var committed = await _database.Store.CommitAsync(Key(0, "op-1"));

        Assert.True(reserved.Allowed);
        Assert.Equal(4L, usage.Reserved);
        Assert.Equal(4L, committed.Usage?.Committed);
    }

    /// <summary>
    /// 重复建表不改动既有配额账本
    /// </summary>
    [Fact]
    public async Task 重复建表不改动既有账本()
    {
        await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));

        _database.Scope.GetConnectionScope(QuotaTestDatabase.ConfigId)
            .CodeFirst.InitTables([typeof(SysQuotaBucket), typeof(SysQuotaReservation)]);
        _database.Scope.GetConnectionScope(QuotaTestDatabase.ConfigId)
            .CodeFirst.InitTables([typeof(SysQuotaBucket), typeof(SysQuotaReservation)]);

        var usage = await _database.Store.FindUsageAsync(1001, "quota", Limited(10));

        Assert.Equal(4L, usage.Reserved);
    }

    /// <summary>
    /// 落库的数据带显式租户标识，不受环境租户改写
    /// </summary>
    [Fact]
    public async Task 落库数据保留显式租户标识()
    {
        _database.CurrentTenant.Change(777);
        await _database.Store.ReserveAsync(Reserve(888, "op-1", 1));

        var bucket = await _database.CreateProbeClient().Queryable<SysQuotaBucket>().FirstAsync();

        Assert.Equal(888, bucket.TenantId);
    }

    /// <summary>
    /// 种一行探针数据
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    private async Task SeedProbeAsync(long tenantId)
    {
        var client = _database.Resolver.GetClient(QuotaTestDatabase.ConfigId);

        await client.Insertable(new SysTenantProbe(tenantId * 100)
        {
            TenantId = tenantId,
            ProbeName = $"probe-{tenantId}"
        }).ExecuteCommandAsync();
    }

    /// <summary>
    /// 调用方事务回滚不带走已放行的预留
    /// </summary>
    /// <remarks>
    /// 契约规定实现不得把预留与结算写进调用方的工作单元：预留必须先于业务独立落账，
    /// 否则业务回滚会把已放行的额度一并退回去，「先占额度」失效。
    /// </remarks>
    [Fact]
    public async Task 调用方事务回滚不带走已放行的预留()
    {
        using var caller = _database.UnitOfWorkManager.Begin(
            new XiHanUnitOfWorkOptions(isTransactional: true));

        var reserved = await _database.Store.ReserveAsync(Reserve(1001, "op-1", 4));
        await caller.RollbackAsync();

        Assert.True(reserved.Allowed);
        Assert.Equal(1, await _database.CountRawAsync<SysQuotaReservation>());
        Assert.Equal(4L, (await _database.Store.FindUsageAsync(1001, "quota", Limited(10))).Reserved);
    }

    /// <summary>
    /// 创建按 UTC 日周期的限额政策
    /// </summary>
    /// <param name="limit">上限</param>
    /// <returns>政策</returns>
    private static QuotaPolicy Limited(long limit)
    {
        return QuotaPolicy.Limited(limit, QuotaPeriod.Day, "v1");
    }

    /// <summary>
    /// 创建预留请求
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="operationId">操作标识</param>
    /// <param name="amount">预留量</param>
    /// <returns>请求</returns>
    private static QuotaReserveRequest Reserve(long tenantId, string operationId, long amount)
    {
        return new QuotaReserveRequest(tenantId, "quota", operationId, amount, Limited(10));
    }

    /// <summary>
    /// 创建预留标识
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="operationId">操作标识</param>
    /// <returns>预留标识</returns>
    private static QuotaReservationKey Key(long tenantId, string operationId)
    {
        return new QuotaReservationKey(tenantId, "quota", operationId);
    }
}
