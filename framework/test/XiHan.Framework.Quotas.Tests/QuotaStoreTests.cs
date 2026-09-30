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
        Assert.Equal(0, UsageOf(result).Committed);
        Assert.Equal(3, UsageOf(result).Reserved);
        Assert.Equal(7, UsageOf(result).Remaining);
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
        Assert.Equal(4, UsageOf(replay).Reserved);
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
        Assert.Equal(4, UsageOf(conflict).Reserved);
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
        Assert.Equal(4L, committed.Usage?.Committed);
        Assert.Equal(0L, committed.Usage?.Reserved);
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
        Assert.Equal(4L, again.Usage?.Committed);
        Assert.Equal(0L, again.Usage?.Reserved);
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
        Assert.Equal(0L, released.Usage?.Reserved);
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
        Assert.Equal(4L, releaseAfterCommit.Usage?.Committed);
        Assert.Equal(QuotaReservationState.Committed, releaseAfterCommit.Reservation?.State);

        await store.ReserveAsync(Reserve(1001, "op-2", 3));
        await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-2"));
        var commitAfterRelease = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-2"));

        Assert.Equal(QuotaSettlementStatus.TerminalConflict, commitAfterRelease.Status);
        Assert.Equal(4L, commitAfterRelease.Usage?.Committed);
        Assert.Equal(0L, commitAfterRelease.Usage?.Reserved);
        Assert.Equal(QuotaReservationState.Released, commitAfterRelease.Reservation?.State);
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
        Assert.Equal(0L, commit.Usage?.Committed);
        Assert.Equal(0L, commit.Usage?.Reserved);

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
        Assert.Equal(6L, committed.Usage?.Committed);
        Assert.Equal(0L, currentUsage.Committed);
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
        Assert.Equal(10, UsageOf(downgraded).Committed);
        Assert.Equal(3, UsageOf(downgraded).Limit);
        Assert.Equal(0, UsageOf(downgraded).Remaining);

        var settled = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.AlreadyCommitted, settled.Status);
        Assert.Equal(10L, settled.Usage?.Committed);
        Assert.Equal(10, settled.Usage?.Limit);
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
        Assert.Equal(2, UsageOf(followUp).Committed);
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
        Assert.Null(UsageOf(second).Limit);
        Assert.Null(UsageOf(second).Remaining);
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
        Assert.Equal(0, UsageOf(afterEvict).Committed);
    }

    /// <summary>
    /// 无限额政策放行但不占用用量计数
    /// </summary>
    [Fact]
    public async Task 无限额政策不占用用量计数()
    {
        var store = CreateStore();
        var unlimited = QuotaPolicy.Unlimited("v1");

        var first = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 5, unlimited));
        var second = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-2", 5, unlimited));

        Assert.Equal(QuotaReserveStatus.Unlimited, first.Status);
        Assert.True(first.Allowed);
        Assert.Null(UsageOf(first).Limit);
        Assert.Equal(0, UsageOf(first).Reserved);
        Assert.Equal(0, UsageOf(second).Committed);
        Assert.Equal(0, UsageOf(second).Reserved);
    }

    /// <summary>
    /// 无限额改成有限额后用量从零点重新计，无限额期间的消耗不计入
    /// </summary>
    [Fact]
    public async Task 无限额转有限额后用量从零起算()
    {
        var store = CreateStore();
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 100, QuotaPolicy.Unlimited("v1")));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var limited = QuotaPolicy.Limited(10, QuotaPeriod.None, "v2");
        var withinLimit = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-2", 10, limited));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-2"));
        var overLimit = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-3", 1, limited));

        Assert.True(withinLimit.Allowed);
        Assert.Equal(QuotaReserveStatus.Exceeded, overLimit.Status);
        Assert.Equal(10, UsageOf(overLimit).Committed);
    }

    /// <summary>
    /// 有限额改成无限额后既有预留仍能正常结算，不被卡住
    /// </summary>
    [Fact]
    public async Task 有限额转无限额后既有预留仍可结算()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));

        var committed = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
        Assert.Equal(4L, committed.Usage?.Committed);
        Assert.Equal(0L, committed.Usage?.Reserved);

        var again = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "quota", "op-1", 4, QuotaPolicy.Unlimited("v1")));
        Assert.Equal(QuotaReserveStatus.Replayed, again.Status);
    }

    /// <summary>
    /// 有限额改成无限额后到期的既有预留仍能到期释放，不永久占额
    /// </summary>
    [Fact]
    public async Task 有限额转无限额后到期预留仍可回收()
    {
        var store = CreateStore(static options => options.MaxTrackedReservations = 1);
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 4, QuotaPolicy.Limited(10, QuotaPeriod.None, "v1"),
            TimeSpan.FromMinutes(1)));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var blocked = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "quota", "op-2", 4, QuotaPolicy.Unlimited("v2")));
        Assert.Equal(QuotaReserveStatus.CapacityExhausted, blocked.Status);

        _clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        var afterReclaim = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "quota", "op-2", 4, QuotaPolicy.Unlimited("v2")));

        Assert.Equal(QuotaReserveStatus.Unlimited, afterReclaim.Status);
    }

    /// <summary>
    /// 额度判定不因 long 加法溢出而超额放行
    /// </summary>
    [Fact]
    public async Task 额度判定不因long加法溢出而超额放行()
    {
        var store = CreateStore();
        var huge = QuotaPolicy.Limited(long.MaxValue, QuotaPeriod.Day, "v1");
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", 6_000_000_000_000_000_000, huge));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var second = await store.ReserveAsync(
            new QuotaReserveRequest(1001, "quota", "op-2", 5_000_000_000_000_000_000, huge));

        Assert.Equal(QuotaReserveStatus.Exceeded, second.Status);
        Assert.Equal(6_000_000_000_000_000_000, UsageOf(second).Committed);
    }

    /// <summary>
    /// 用满极长上限后继续预留按超额返回且不抛异常也不产生负计数
    /// </summary>
    [Fact]
    public async Task 极长上限用满后按超额返回且不产生负计数()
    {
        var store = CreateStore();
        var huge = QuotaPolicy.Limited(long.MaxValue, QuotaPeriod.Day, "v1");
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-1", long.MaxValue, huge));

        var followUp = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-2", 2, huge));
        var usage = await store.FindUsageAsync(1001, "quota", huge);

        Assert.Equal(QuotaReserveStatus.Exceeded, followUp.Status);
        Assert.Equal(long.MaxValue, usage.Reserved);
        Assert.Equal(0, usage.Committed);
    }

    /// <summary>
    /// 记录全部过保留期后，累计型空桶不再占用桶上限
    /// </summary>
    [Fact]
    public async Task 空的累计型桶可被驱逐()
    {
        var store = CreateStore(static options =>
        {
            options.MaxTrackedBuckets = 1;
            options.NonPeriodicTombstoneRetention = TimeSpan.FromHours(1);
        });
        var unlimited = QuotaPolicy.Unlimited("v1");
        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota-a", "op-1", 1, unlimited));

        _clock.Advance(TimeSpan.FromHours(2));
        var other = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota-b", "op-2", 1, unlimited));

        Assert.True(other.Allowed);
    }

    /// <summary>
    /// 日周期与月周期在每月一日不共用同一个桶
    /// </summary>
    /// <remarks>
    /// 日周期与月周期的起点都是当日 00:00，每月一日两者是同一个瞬间；
    /// 桶标识若不含计量周期，月度记录会落进日节奏的桶，被按 24 小时保留期回收。
    /// </remarks>
    [Fact]
    public async Task 日周期与月周期不共用同一个桶()
    {
        var store = CreateStore();
        _clock.Advance(TimeSpan.FromDays(1));
        var daily = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");
        var monthly = QuotaPolicy.Limited(100, QuotaPeriod.Month, "v1");

        await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-d", 6, daily));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-d"));

        var monthReserve = await store.ReserveAsync(new QuotaReserveRequest(1001, "quota", "op-m", 100, monthly));
        var dayUsage = await store.FindUsageAsync(1001, "quota", daily);
        var monthUsage = await store.FindUsageAsync(1001, "quota", monthly);

        Assert.True(monthReserve.Allowed);
        Assert.Equal(6, dayUsage.Committed);
        Assert.Equal(100, monthUsage.Reserved);
        Assert.Equal(0, monthUsage.Committed);
    }

    /// <summary>
    /// 超出可表示范围的预留存活时长被夹取而不抛异常
    /// </summary>
    [Fact]
    public async Task 超长预留存活时长被夹取()
    {
        var store = CreateStore();

        var result = await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 1, Limited(10), TimeSpan.MaxValue));
        var committed = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.True(result.Allowed);
        Assert.Equal(DateTimeOffset.MaxValue, result.Reservation!.ExpiresAt);
        Assert.Equal(QuotaSettlementStatus.Committed, committed.Status);
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
    /// 同一瞬时在 UTC 与本地两种标注下落进同一个桶
    /// </summary>
    /// <remarks>
    /// 关系式断言，不重抄实现的换算：两台时钟给出同一瞬时、只是 Kind 标注不同，
    /// 归一化正确就必须得到相同的周期起点与到期时刻。
    /// </remarks>
    /// <param name="kind">被测时钟的时间标注</param>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task 同一瞬时不同Kind的时钟落进同一个桶(DateTimeKind kind)
    {
        var reference = CreateStoreWithKind(DateTimeKind.Utc);
        var underTest = CreateStoreWithKind(kind);

        var expected = await reference.ReserveAsync(Reserve(1001, "op-1", 3));
        var actual = await underTest.ReserveAsync(Reserve(1001, "op-1", 3));

        Assert.Equal(expected.Reservation!.PeriodStart, actual.Reservation!.PeriodStart);
        Assert.Equal(expected.Reservation.ExpiresAt, actual.Reservation.ExpiresAt);
        Assert.Equal(UsageOf(expected).Reserved, UsageOf(actual).Reserved);
    }

    /// <summary>
    /// 重复释放幂等且不再改动用量
    /// </summary>
    [Fact]
    public async Task 重复释放幂等且不再改动用量()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 4));
        await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        var again = await store.ReleaseAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.Equal(QuotaSettlementStatus.AlreadyReleased, again.Status);
        Assert.Equal(0L, again.Usage?.Committed);
        Assert.Equal(0L, again.Usage?.Reserved);
    }

    /// <summary>
    /// 桶被驱逐后迟到结算返回未找到且不重建桶
    /// </summary>
    [Fact]
    public async Task 桶被驱逐后迟到结算返回未找到()
    {
        var store = CreateStore(static options =>
        {
            options.MaxTrackedBuckets = 1;
            options.MaxTrackedReservations = 100;
        });
        await store.ReserveAsync(Reserve(1001, "op-1", 6));
        await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        _clock.Advance(TimeSpan.FromDays(2));
        var evicting = await store.ReserveAsync(Reserve(1001, "op-2", 1));
        var late = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.True(evicting.Allowed);
        Assert.Equal(QuotaSettlementStatus.NotFound, late.Status);
        Assert.Null(late.Reservation);
        Assert.Null(late.Usage);
        Assert.Equal(1, UsageOf(evicting).Committed + UsageOf(evicting).Reserved);
    }

    /// <summary>
    /// 预留在同一周期被下一次预留惰性归还
    /// </summary>
    /// <remarks>
    /// 全程不调用提交或释放：到期预留必须在下次同桶预留时被回收，否则额度永久占用。
    /// </remarks>
    [Fact]
    public async Task 到期预留在下次同桶预留时惰性归还()
    {
        var store = CreateStore();
        await store.ReserveAsync(new QuotaReserveRequest(
            1001, "quota", "op-1", 10, Limited(10), TimeSpan.FromMinutes(1)));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var followUp = await store.ReserveAsync(Reserve(1001, "op-2", 10));
        var lateCommit = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"));

        Assert.True(followUp.Allowed);
        Assert.Equal(10, UsageOf(followUp).Reserved);
        Assert.Equal(0, UsageOf(followUp).Committed);
        Assert.Equal(QuotaSettlementStatus.Expired, lateCommit.Status);
    }

    /// <summary>
    /// 并发重试同一操作只扣一次额
    /// </summary>
    [Fact]
    public async Task 并发重试同一操作只扣一次额()
    {
        var store = CreateStore();
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.ReserveAsync(Reserve(1001, "op-1", 3))))
            .ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, results.Count(item => item.Status == QuotaReserveStatus.Reserved));
        Assert.Equal(15, results.Count(item => item.Status == QuotaReserveStatus.Replayed));
        Assert.Equal(3, UsageOf(results[^1]).Reserved);
    }

    /// <summary>
    /// 并发结算同一预留只提交一次
    /// </summary>
    [Fact]
    public async Task 并发结算同一预留只提交一次()
    {
        var store = CreateStore();
        await store.ReserveAsync(Reserve(1001, "op-1", 3));
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.CommitAsync(new QuotaReservationKey(1001, "quota", "op-1"))))
            .ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, results.Count(item => item.Status == QuotaSettlementStatus.Committed));
        Assert.Equal(15, results.Count(item => item.Status == QuotaSettlementStatus.AlreadyCommitted));
        Assert.Equal(3L, results[^1].Usage?.Committed);
        Assert.Equal(0L, results[^1].Usage?.Reserved);
    }

    /// <summary>
    /// 剩余量不为负
    /// </summary>
    [Fact]
    public void 政策降级后剩余量钳位为零()
    {
        var usage = new QuotaUsage(3, 10, 0);

        Assert.Equal(0, usage.Remaining);
    }

    /// <summary>
    /// 存储与契约对 null 依赖与非法参数快速失败
    /// </summary>
    [Fact]
    public async Task null依赖与非法参数快速失败()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new XiHanQuotasOptions());
        Assert.Throws<ArgumentNullException>(() => new DefaultQuotaStore(null!, options));
        Assert.Throws<ArgumentNullException>(
            () => new DefaultQuotaStore(new TestClock(), null!));

        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReserveAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CommitAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReleaseAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.FindUsageAsync(1001, "quota", null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.FindUsageAsync(-1, "quota", Limited(10)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.FindUsageAsync(1001, " ", Limited(10)));
    }

    /// <summary>
    /// 配额数值入参越界快速失败
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="operationId">操作标识</param>
    /// <param name="amount">预留量</param>
    [Theory]
    [InlineData(-1, "quota", "op", 1)]
    [InlineData(1001, "quota", "op", 0)]
    [InlineData(1001, "quota", "op", -1)]
    public void 配额数值入参越界快速失败(
        long tenantId, string quotaKey, string operationId, long amount)
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new QuotaReserveRequest(tenantId, quotaKey, operationId, amount, policy));
    }

    /// <summary>
    /// 预留标识的租户越界快速失败
    /// </summary>
    [Fact]
    public void 预留标识租户越界快速失败()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaReservationKey(-1, "quota", "op"));
    }

    /// <summary>
    /// 配额标识空白快速失败
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="operationId">操作标识</param>
    [Theory]
    [InlineData(1001, "", "op")]
    [InlineData(1001, "   ", "op")]
    [InlineData(1001, "quota", "")]
    [InlineData(1001, "quota", "  ")]
    public void 配额标识空白快速失败(long tenantId, string quotaKey, string operationId)
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");

        Assert.Throws<ArgumentException>(
            () => new QuotaReserveRequest(tenantId, quotaKey, operationId, 1, policy));
        Assert.Throws<ArgumentException>(() => new QuotaReservationKey(tenantId, quotaKey, operationId));
    }

    /// <summary>
    /// 预留存活时长非正与政策缺失快速失败
    /// </summary>
    [Fact]
    public void 预留时长非正与政策缺失快速失败()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");

        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaReserveRequest(
            1001, "quota", "op", 1, policy, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaReserveRequest(
            1001, "quota", "op", 1, policy, TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>(() => new QuotaReserveRequest(
            1001, "quota", "op", 1, null!));
        Assert.Throws<ArgumentNullException>(() => new QuotaReserveResult(
            QuotaReserveStatus.Exceeded, null, null!));
    }

    /// <summary>
    /// 用量快照不接受负数
    /// </summary>
    [Fact]
    public void 用量快照不接受负数()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaUsage(10, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaUsage(10, 0, -1));
    }

    /// <summary>
    /// 未找到预留时不回报用量快照
    /// </summary>
    /// <remarks>
    /// 合成零用量里的 Limit=null 与「显式无限额」同形，会被读成不限额；无从确定就不回报。
    /// </remarks>
    [Fact]
    public async Task 未找到预留时不回报用量快照()
    {
        var store = CreateStore();

        var result = await store.CommitAsync(new QuotaReservationKey(1001, "quota", "nope"));

        Assert.Equal(QuotaSettlementStatus.NotFound, result.Status);
        Assert.Null(result.Reservation);
        Assert.Null(result.Usage);
    }

    /// <summary>
    /// 创建指定时间标注时钟驱动的存储
    /// </summary>
    /// <param name="kind">时间标注</param>
    /// <returns>默认存储</returns>
    private DefaultQuotaStore CreateStoreWithKind(DateTimeKind kind)
    {
        return new DefaultQuotaStore(
            new TestClock(kind), Microsoft.Extensions.Options.Options.Create(new XiHanQuotasOptions()));
    }

    /// <summary>
    /// 取预留结果的用量快照，缺失即让测试失败而不是静默通过
    /// </summary>
    /// <param name="result">预留结果</param>
    /// <returns>用量快照</returns>
    private static QuotaUsage UsageOf(QuotaReserveResult result)
    {
        return result.Usage ?? throw new InvalidOperationException("预留结果未回报用量快照。");
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
