// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱入箱测试
/// </summary>
public class OutboxEnqueueTests
{
    /// <summary>
    /// 入箱后记录落库且状态为待发送
    /// </summary>
    [Fact]
    public async Task 入箱后记录落库且状态为待发送()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();

        await context.Outbox.EnqueueAsync(info);

        var stored = context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).First();

        Assert.NotNull(stored);
        Assert.Equal("Order.Created", stored.EventName);
        Assert.Equal(SysEventOutbox.StatusPending, stored.Status);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.ClaimTime);
    }

    /// <summary>
    /// 事务回滚后记录不落库
    /// </summary>
    [Fact]
    public async Task 事务回滚后记录不落库()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();

        context.Client.Ado.BeginTran();
        await context.Outbox.EnqueueAsync(info);
        context.Client.Ado.RollbackTran();

        var count = await context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).CountAsync();

        Assert.Equal(0, count);
    }

    /// <summary>
    /// 事务提交后记录落库
    /// </summary>
    [Fact]
    public async Task 事务提交后记录落库()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();

        context.Client.Ado.BeginTran();
        await context.Outbox.EnqueueAsync(info);
        context.Client.Ado.CommitTran();

        var count = await context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).CountAsync();

        Assert.Equal(1, count);
    }

    /// <summary>
    /// 按标识删除生效
    /// </summary>
    [Fact]
    public async Task 按标识删除生效()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();
        await context.Outbox.EnqueueAsync(info);

        await context.Outbox.DeleteAsync(info.Id);

        var count = await context.Client.Queryable<SysEventOutbox>().CountAsync();

        Assert.Equal(0, count);
    }

    /// <summary>
    /// 批量删除空集合不抛异常
    /// </summary>
    [Fact]
    public async Task 批量删除空集合不抛异常()
    {
        using var context = new OutboxTestContext();

        await context.Outbox.DeleteManyAsync([]);
    }

    /// <summary>
    /// 批量删除生效
    /// </summary>
    [Fact]
    public async Task 批量删除生效()
    {
        using var context = new OutboxTestContext();
        var first = NewEvent();
        var second = NewEvent();
        await context.Outbox.EnqueueAsync(first);
        await context.Outbox.EnqueueAsync(second);

        await context.Outbox.DeleteManyAsync([first.Id, second.Id]);

        var count = await context.Client.Queryable<SysEventOutbox>().CountAsync();

        Assert.Equal(0, count);
    }

    /// <summary>
    /// 已登记单个模块库时事件写进该库
    /// </summary>
    [Fact]
    public async Task 已登记单个模块库时事件写进该库()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        var info = NewEvent();

        await context.Outbox.EnqueueAsync(info);

        Assert.Equal(1, await context.ModuleClient.Queryable<SysEventOutbox>().CountAsync());
        Assert.Equal(0, await context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 未登记任何连接时事件写进当前库
    /// </summary>
    [Fact]
    public async Task 未登记任何连接时事件写进当前库()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var info = NewEvent();

        await context.Outbox.EnqueueAsync(info);

        Assert.Equal(1, await context.Client.Queryable<SysEventOutbox>().CountAsync());
        Assert.Equal(0, await context.ModuleClient.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 登记了多个连接时抛出无法确定落点
    /// </summary>
    [Fact]
    public async Task 登记了多个连接时抛出无法确定落点()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.MainConfigId);
        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Outbox.EnqueueAsync(NewEvent()));
    }

    private static OutgoingEventInfo NewEvent()
    {
        return new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1, 2, 3], DateTime.UtcNow);
    }
}
