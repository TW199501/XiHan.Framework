// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas;
using XiHan.Framework.Quotas.Stores;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 进程内默认配额存储测试
/// </summary>
public class QuotaStoreTests
{
    private readonly TestClock _clock = new();

    /// <summary>
    /// 限额内首次预留成功并占用额度
    /// </summary>
    [Fact]
    public async Task 限额内预留成功并占用额度()
    {
        var store = CreateStore();

        var result = await store.ReserveAsync(Reserve(1001, "op-1", 3));

        Assert.True(result.Allowed);
        Assert.Equal(QuotaReserveStatus.Reserved, result.Status);
        Assert.Equal(0, result.Usage.Committed);
        Assert.Equal(3, result.Usage.Reserved);
        Assert.Equal(7, result.Usage.Remaining);
    }

    /// <summary>
    /// 二十个并发预留争抢十限额时最多十个成功
    /// </summary>
    [Fact]
    public async Task 并发争抢限额时最多十个成功()
    {
        var store = CreateStore();
        var tasks = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => store.ReserveAsync(Reserve(1001, $"op-{index}", 1))))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Count(item => item.Allowed));
        Assert.Equal(10, results.Count(item => item.Status == QuotaReserveStatus.Exceeded));
        var usage = await store.FindUsageAsync(1001, "quota", Limited(10));
        Assert.Equal(10, usage.Reserved);
        Assert.Equal(0, usage.Committed);
    }

    /// <summary>
    /// 同一操作重试不重复扣额
    /// </summary>
    [Fact]
    public async Task 同一操作重试不重复扣额()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));

        var replay = await store.ReserveAsync(Reserve(1001, "op-1", 4));

        Assert.True(replay.Allowed);
        Assert.Equal(QuotaReserveStatus.Replayed, replay.Status);
        Assert.Equal(4, replay.Usage.Reserved);
    }

    /// <summary>
    /// 同一操作用不同预留量被判冲突
    /// </summary>
    [Fact]
    public async Task 同一操作用不同预留量被判冲突()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));

        var conflict = await store.ReserveAsync(Reserve(1001, "op-1", 5));

        Assert.False(conflict.Allowed);
        Assert.Equal(QuotaReserveStatus.Conflict, conflict.Status);
        Assert.Equal(4, conflict.Usage.Reserved);
    }

    /// <summary>
    /// 同一操作用不同政策版本被判冲突
    /// </summary>
    [Fact]
    public async Task 同一操作用不同政策版本被判冲突()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));

        var conflict = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "quota", "op-1", 4, Limited(10, "v2")));

        Assert.Equal(QuotaReserveStatus.Conflict, conflict.Status);
    }

    /// <summary>
    /// 提交把预留量转为已用
    /// </summary>
    [Fact]
    public async Task 提交把预留量转为已用()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));

        var committed = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
        Assert.Equal(4, committed.Usage.Committed);
        Assert.Equal(0, committed.Usage.Reserved);
    }

    /// <summary>
    /// 重复提交幂等且不再改动用量
    /// </summary>
    [Fact]
    public async Task 重复提交幂等且不再改动用量()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var again = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.AlreadyCommitted, again.Status);
        Assert.Equal(4, again.Usage.Committed);
        Assert.Equal(0, again.Usage.Reserved);
    }

    /// <summary>
    /// 释放归还额度且可继续预留
    /// </summary>
    [Fact]
    public async Task 释放归还额度且可继续预留()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 10));

        var released = await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.Released, released.Status);
        Assert.Equal(0, released.Usage.Reserved);
        var followUp = await store.ReserveAsync(Reserve(1001, "op-2", 10));
        Assert.True(followUp.Allowed);
    }

    /// <summary>
    /// 提交与释放互斥且终态不可回退
    /// </summary>
    [Fact]
    public async Task 提交与释放互斥且终态不可回退()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var releaseAfterCommit = await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-1"));
        Assert.Equal(QuotaSettlementStatus.TerminalConflict, releaseAfterCommit.Status);
        Assert.Equal(4, releaseAfterCommit.Usage.Committed);

        await store.ReserveAsync(Reserve(1001, "op-2", 3));
        await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-2"));
        var commitAfterRelease = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-2"));

        Assert.Equal(QuotaSettlementStatus.TerminalConflict, commitAfterRelease.Status);
        Assert.Equal(4, commitAfterRelease.Usage.Committed);
        Assert.Equal(0, commitAfterRelease.Usage.Reserved);
    }

    /// <summary>
    /// 超期预留不能提交且不重新扣额
    /// </summary>
    [Fact]
    public async Task 超期预留不能提交且不重新扣额()
    {
        var store = CreateStore();
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 10, Limited(10), TimeSpan.FromMinutes(1)));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var commit = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.Expired, commit.Status);
        Assert.Equal(0, commit.Usage.Committed);
        Assert.Equal(0, commit.Usage.Reserved);

        var retry = await store.ReserveAsync(Reserve(1001, "op-2", 10));
        Assert.True(retry.Allowed);
    }

    /// <summary>
    /// 跨租户互不影响
    /// </summary>
    [Fact]
    public async Task 跨租户互不影响()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 10));

        var other = await store.ReserveAsync(Reserve(1002, "op-1", 10));

        Assert.True(other.Allowed);
        Assert.Equal(10, (await store.FindUsageAsync(1001, "quota", Limited(10))).Reserved);
        Assert.Equal(10, (await store.FindUsageAsync(1002, "quota", Limited(10))).Reserved);
    }

    /// <summary>
    /// 周期滚动后新周期从零开始
    /// </summary>
    [Fact]
    public async Task 周期滚动后新周期从零开始()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 10));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        _clock.Advance(TimeSpan.FromDays(1));
        var usage = await store.FindUsageAsync(1001, "quota", Limited(10));
        var followUp = await store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.Equal(0, usage.Committed);
        Assert.True(followUp.Allowed);
    }

    /// <summary>
    /// 迟到的提交只改动它所属的旧周期
    /// </summary>
    [Fact]
    public async Task 迟到的提交只改动所属旧周期()
    {
        var store = CreateStore();
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 6, Limited(10), TimeSpan.FromDays(2)));
        _clock.Advance(TimeSpan.FromDays(1));

        var committed = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));
        var currentUsage = await store.FindUsageAsync(1001, "quota", Limited(10));
        var newPeriodReserve = await store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
        Assert.Equal(6, committed.Usage.Committed);
        Assert.Equal(0, currentUsage.Committed);
        Assert.True(newPeriodReserve.Allowed);
    }

    /// <summary>
    /// 政策降级阻止新增预留但不破坏已提交用量
    /// </summary>
    [Fact]
    public async Task 政策降级阻止新增但不破坏已提交()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 10));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var downgraded = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "quota", "op-2", 1, Limited(3)));

        Assert.Equal(QuotaReserveStatus.Exceeded, downgraded.Status);
        Assert.Equal(10, downgraded.Usage.Committed);
    }

    /// <summary>
    /// 达到条目上限时拒绝新预留且不驱逐活动预留
    /// </summary>
    [Fact]
    public async Task 达到条目上限拒绝新预留且不驱逐活动预留()
    {
        var store = CreateStore(static options => options.MaxTrackedReservations = 2);
        await store.ReserveAsync(Reserve(1001, "op-1", 1));
        await store.ReserveAsync(Reserve(1001, "op-2", 1));

        var rejected = await store.ReserveAsync(Reserve(1001, "op-3", 1));
        var committed = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaReserveStatus.CapacityExhausted, rejected.Status);
        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
    }

    /// <summary>
    /// 条目上限用满后可回收已过保留期的去重记录
    /// </summary>
    [Fact]
    public async Task 条目上限用满可回收过保留期的去重记录()
    {
        var store = CreateStore(static options =>
        {
            options.MaxTrackedReservations = 2;
            options.NonPeriodicTombstoneRetention = TimeSpan.FromHours(1);
        });
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.None, "v1");
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 1, policy));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-2", 1, policy));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-2"));

        var blocked = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-3", 1, policy));
        Assert.Equal(QuotaReserveStatus.CapacityExhausted, blocked.Status);

        _clock.Advance(TimeSpan.FromHours(2));
        var followUp = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-3", 1, policy));

        Assert.True(followUp.Allowed);
        Assert.Equal(2, followUp.Usage.Committed);
    }

    /// <summary>
    /// 到期后无人再触碰的预留不永久占用条目上限
    /// </summary>
    /// <remarks>
    /// 日周期政策下，周期滚到次日就不再触碰前一天的桶；
    /// 该桶里到期未结算的预留必须被全局清扫回收，否则条目上限会被僵尸记录永久占满。
    /// </remarks>
    [Fact]
    public async Task 到期未结算的预留不永久占用条目上限()
    {
        var store = CreateStore(static options => options.MaxTrackedReservations = 1);
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 10, Limited(10), TimeSpan.FromMinutes(1)));

        _clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(5)));
        var followUp = await store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.True(followUp.Allowed);
    }

    /// <summary>
    /// 到期后无人再触碰的预留不永久占用桶上限
    /// </summary>
    [Fact]
    public async Task 到期未结算的预留不永久占用桶上限()
    {
        var store = CreateStore(static options =>
        {
            options.MaxTrackedBuckets = 1;
            options.MaxTrackedReservations = 100;
        });
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 10, Limited(10), TimeSpan.FromMinutes(1)));

        _clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(5)));
        var followUp = await store.ReserveAsync(Reserve(1001, "op-2", 10));

        Assert.True(followUp.Allowed);
    }

    /// <summary>
    /// 去重记录在保留期内仍判冲突，过保留期后同标识可另起预留
    /// </summary>
    [Fact]
    public async Task 去重记录过保留期后同标识可另起预留()
    {
        var store = CreateStore(static options =>
        {
            options.NonPeriodicTombstoneRetention = TimeSpan.FromHours(1);
        });
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.None, "v1");
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 4, policy));
        await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var withinRetention = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 4, policy));
        Assert.Equal(QuotaReserveStatus.Conflict, withinRetention.Status);

        _clock.Advance(TimeSpan.FromHours(2));
        var afterRetention = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 4, policy));

        Assert.Equal(QuotaReserveStatus.Reserved, afterRetention.Status);
    }

    /// <summary>
    /// 无限额政策始终放行且不记账上限
    /// </summary>
    [Fact]
    public async Task 无限额政策始终放行()
    {
        var store = CreateStore();
        var unlimited = QuotaPolicy.Unlimited("v1");

        var first = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 1_000_000, unlimited));
        var second = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-2", 1_000_000, unlimited));

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Null(second.Usage.Limit);
        Assert.Null(second.Usage.Remaining);
    }

    /// <summary>
    /// 不周期政策跨自然日累计不清零
    /// </summary>
    [Fact]
    public async Task 不周期政策跨自然日累计不清零()
    {
        var store = CreateStore();
        var cumulative = QuotaPolicy.Limited(10, QuotaPeriod.None, "v1");
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 10, cumulative));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        _clock.Advance(TimeSpan.FromDays(30));
        var usage = await store.FindUsageAsync(1001, "quota", cumulative);
        var rejected = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-2", 1, cumulative));

        Assert.Equal(10, usage.Committed);
        Assert.Equal(QuotaReserveStatus.Exceeded, rejected.Status);
    }

    /// <summary>
    /// 配额桶上限用满时驱逐已结束且记录全部过保留期的桶
    /// </summary>
    [Fact]
    public async Task 配额桶上限用满驱逐已结束桶()
    {
        var store = CreateStore(static options =>
        {
            options.MaxTrackedBuckets = 1;
            options.MaxTrackedReservations = 100;
        });

        await store.ReserveAsync(Reserve(1001, "op-1", 1));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        _clock.Advance(TimeSpan.FromHours(23));
        var beforeRetention = await store.ReserveAsync(Reserve(1001, "op-2", 1));
        Assert.Equal(QuotaReserveStatus.CapacityExhausted, beforeRetention.Status);

        _clock.Advance(TimeSpan.FromHours(2));
        var afterEvict = await store.ReserveAsync(Reserve(1001, "op-3", 1));

        Assert.True(afterEvict.Allowed);
        Assert.Equal(0, afterEvict.Usage.Committed);
    }

    /// <summary>
    /// 无记录时用量为零
    /// </summary>
    [Fact]
    public async Task 无记录时用量为零()
    {
        var store = CreateStore();

        var usage = await store.FindUsageAsync(1001, "quota", Limited(10));

        Assert.Equal(0, usage.Committed);
        Assert.Equal(0, usage.Reserved);
        Assert.Equal(10, usage.Remaining);
    }

    /// <summary>
    /// 结算未知预留标识返回未找到
    /// </summary>
    [Fact]
    public async Task 结算未知预留标识返回未找到()
    {
        var store = CreateStore();

        var result = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "nope"));

        Assert.Equal(QuotaSettlementStatus.NotFound, result.Status);
        Assert.Null(result.Reservation);
    }

    /// <summary>
    /// 取消令牌已触发时预留快速失败
    /// </summary>
    [Fact]
    public async Task 取消令牌已触发时快速失败()
    {
        var store = CreateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReserveAsync(Reserve(1001, "op-1", 1), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-1"), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.FindUsageAsync(1001, "quota", Limited(10), cts.Token));
    }

    /// <summary>
    /// 非法选项在构造存储时快速失败
    /// </summary>
    [Fact]
    public void 非法选项构造存储时快速失败()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore(
            static options => options.DefaultReservationTtl = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore(
            static options => options.MaxTrackedReservations = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore(
            static options => options.NonPeriodicTombstoneRetention = TimeSpan.FromMinutes(-1)));
    }

    /// <summary>
    /// 预留量非正数快速失败
    /// </summary>
    [Fact]
    public void 预留量非正数快速失败()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new QuotaReserveRequest(1001, "quota", "op-1", 0, Limited(10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new QuotaReserveRequest(1001, "quota", "op-1", -5, Limited(10)));
    }

    /// <summary>
    /// 创建被测存储
    /// </summary>
    /// <param name="configure">选项调整</param>
    /// <returns>默认存储</returns>
    private DefaultQuotaStore CreateStore(Action<XiHanQuotasOptions>? configure = null)
    {
        var options = new XiHanQuotasOptions();
        configure?.Invoke(options);

        return new DefaultQuotaStore(_clock, Microsoft.Extensions.Options.Options.Create(options));
    }

    /// <summary>
    /// 创建按 UTC 日周期的限额政策
    /// </summary>
    /// <param name="limit">上限</param>
    /// <param name="version">版本</param>
    /// <returns>政策</returns>
    private static QuotaPolicy Limited(long limit, string version = "v1")
    {
        return QuotaPolicy.Limited(limit, QuotaPeriod.Day, version);
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
}
