// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱领取测试
/// </summary>
public class OutboxClaimTests
{
    /// <summary>
    /// 领取后记录被标记为已领取并带令牌
    /// </summary>
    [Fact]
    public async Task 领取后记录被标记并带令牌()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();
        await context.Outbox.EnqueueAsync(info);

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);

        var stored = context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).First();

        Assert.Equal(SysEventOutbox.StatusClaimed, stored.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.ClaimToken));
        Assert.NotNull(stored.ClaimTime);
    }

    /// <summary>
    /// 已领取的记录不会被再次领取
    /// </summary>
    [Fact]
    public async Task 已领取的记录不会被再次领取()
    {
        using var context = new OutboxTestContext();
        await context.Outbox.EnqueueAsync(NewEvent());

        var first = await context.Outbox.GetWaitingEventsAsync(10);
        var second = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(first);
        Assert.Empty(second);
    }

    /// <summary>
    /// 领取超时后记录可被重新领取
    /// </summary>
    [Fact]
    public async Task 领取超时后可被重新领取()
    {
        using var context = new OutboxTestContext(TimeSpan.FromMinutes(5));
        var info = NewEvent();
        await context.Outbox.EnqueueAsync(info);

        await context.Outbox.GetWaitingEventsAsync(10);

        context.Client.Updateable<SysEventOutbox>()
            .SetColumns(item => new SysEventOutbox { ClaimTime = DateTimeOffset.UtcNow.AddHours(-1) })
            .Where(item => item.BasicId == info.Id)
            .ExecuteCommand();

        var reclaimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(reclaimed);
        Assert.Equal(info.Id, reclaimed[0].Id);
    }

    /// <summary>
    /// 领取数量不超过上限且按创建时间升序
    /// </summary>
    [Fact]
    public async Task 领取数量不超过上限且按创建时间升序()
    {
        using var context = new OutboxTestContext();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        for (var index = 0; index < 5; index++)
        {
            await context.Outbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [(byte)index], baseTime.AddMinutes(index)));
        }

        var claimed = await context.Outbox.GetWaitingEventsAsync(3);

        Assert.Equal(3, claimed.Count);
        Assert.Equal([0, 1, 2], claimed.Select(item => item.EventData[0]).ToArray());
    }

    /// <summary>
    /// 传入过滤条件时明确抛出不支持
    /// </summary>
    [Fact]
    public async Task 传入过滤条件时抛出不支持()
    {
        using var context = new OutboxTestContext();
        Expression<Func<IOutgoingEventInfo, bool>> filter = item => item.EventName == "Order.Created";

        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Outbox.GetWaitingEventsAsync(10, filter));
    }

    /// <summary>
    /// 领取回的事件信息保真
    /// </summary>
    [Fact]
    public async Task 领取回的事件信息保真()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();
        info.SetCorrelationId("corr-claim");
        await context.Outbox.EnqueueAsync(info);

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);
        Assert.Equal("Order.Created", claimed[0].EventName);
        Assert.Equal(info.EventData, claimed[0].EventData);
        Assert.Equal("corr-claim", claimed[0].GetCorrelationId());
    }

    /// <summary>
    /// 两个库的待发记录都会被领到
    /// </summary>
    [Fact]
    public async Task 两个库的待发记录都会被领到()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var mainEvent = NewEvent();
        var moduleEvent = NewEvent();

        await context.Outbox.EnqueueAsync(mainEvent);

        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        await context.Outbox.EnqueueAsync(moduleEvent);
        context.Resolver.EnlistedConfigIds.Clear();

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Equal(2, claimed.Count);
        Assert.Contains(claimed, item => item.Id == mainEvent.Id);
        Assert.Contains(claimed, item => item.Id == moduleEvent.Id);
    }

    /// <summary>
    /// 单批总量不超过上限且配额按库平均分配
    /// </summary>
    [Fact]
    public async Task 单批总量不超过上限且配额按库平均分配()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        for (var index = 0; index < 10; index++)
        {
            await context.Outbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], baseTime.AddSeconds(index)));
        }

        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        for (var index = 0; index < 10; index++)
        {
            await context.Outbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [2], baseTime.AddSeconds(index)));
        }
        context.Resolver.EnlistedConfigIds.Clear();

        var claimed = await context.Outbox.GetWaitingEventsAsync(4);

        Assert.Equal(4, claimed.Count);
        Assert.Equal(2, claimed.Count(item => item.EventData[0] == 1));
        Assert.Equal(2, claimed.Count(item => item.EventData[0] == 2));
    }

    /// <summary>
    /// 单个库不可达时其余库照常领取
    /// </summary>
    [Fact]
    public async Task 单个库不可达时其余库照常领取()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var mainEvent = NewEvent();
        await context.Outbox.EnqueueAsync(mainEvent);

        context.Resolver.FaultyConfigIds[OutboxTestContext.ModuleConfigId] =
            new InvalidOperationException("模拟模块库不可达。");

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(mainEvent.Id, claimed[0].Id);
    }

    /// <summary>
    /// 已取消的令牌抛出取消异常
    /// </summary>
    [Fact]
    public async Task 已取消的令牌抛出取消异常()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        await context.Outbox.EnqueueAsync(NewEvent());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Outbox.GetWaitingEventsAsync(10, cancellationToken: cancellation.Token));
    }

    private static OutgoingEventInfo NewEvent()
    {
        return new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1, 2, 3], DateTime.UtcNow);
    }
}
