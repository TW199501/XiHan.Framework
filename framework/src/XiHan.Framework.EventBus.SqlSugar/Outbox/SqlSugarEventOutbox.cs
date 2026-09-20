// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Mapping;
using XiHan.Framework.EventBus.SqlSugar.Options;

namespace XiHan.Framework.EventBus.SqlSugar.Outbox;

/// <summary>
/// 发件箱的 SqlSugar 实现
/// </summary>
public class SqlSugarEventOutbox : IEventOutbox
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly XiHanSqlSugarEventBoxOptions _options;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="options">收发件箱存储配置</param>
    public SqlSugarEventOutbox(
        ISqlSugarClientResolver clientResolver,
        IOptions<XiHanSqlSugarEventBoxOptions> options)
    {
        _clientResolver = clientResolver;
        _options = options.Value;
    }

    /// <summary>
    /// 将事件信息添加到发件箱
    /// </summary>
    /// <param name="outgoingEvent">出站事件信息</param>
    public async Task EnqueueAsync(OutgoingEventInfo outgoingEvent)
    {
        ArgumentNullException.ThrowIfNull(outgoingEvent);

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Insertable(EventOutboxMapper.ToEntity(outgoingEvent)).ExecuteCommandAsync();
    }

    /// <summary>
    /// 领取一批待发送的事件信息
    /// </summary>
    /// <remarks>
    /// 本方法在返回前会把记录标记为已领取，不是纯查询。
    /// 领取超时后记录可被重新领取，超时时长由 <see cref="XiHanSqlSugarEventBoxOptions.ClaimTimeout"/> 配置。
    /// </remarks>
    /// <param name="maxCount">最大数量</param>
    /// <param name="filter">过滤条件，本实现不支持，传入非空值将抛出异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本次领取到的事件信息</returns>
    /// <exception cref="NotSupportedException"><paramref name="filter"/> 不为空</exception>
    public async Task<List<OutgoingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IOutgoingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (filter is not null)
        {
            throw new NotSupportedException(
                "SqlSugar 发件箱暂不支持 filter 参数，请改为在消费端筛选。");
        }

        if (maxCount <= 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - _options.ClaimTimeout;
        var claimToken = Guid.NewGuid().ToString("N");

        var candidateIds = await client.Queryable<SysEventOutbox>()
            .Where(item => item.Status == SysEventOutbox.StatusPending
                || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore))
            .OrderBy(item => item.CreatedTime)
            .Take(maxCount)
            .Select(item => item.BasicId)
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return [];
        }

        await client.Updateable<SysEventOutbox>()
            .SetColumns(item => new SysEventOutbox
            {
                Status = SysEventOutbox.StatusClaimed,
                ClaimToken = claimToken,
                ClaimTime = now
            })
            .Where(item => candidateIds.Contains(item.BasicId)
                && (item.Status == SysEventOutbox.StatusPending
                    || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore)))
            .ExecuteCommandAsync(cancellationToken);

        var claimed = await client.Queryable<SysEventOutbox>()
            .Where(item => item.ClaimToken == claimToken)
            .OrderBy(item => item.CreatedTime)
            .ToListAsync(cancellationToken);

        return [.. claimed.Select(EventOutboxMapper.ToEventInfo)];
    }

    /// <summary>
    /// 删除指定的事件信息
    /// </summary>
    /// <param name="id">事件唯一标识符</param>
    public async Task DeleteAsync(Guid id)
    {
        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Deleteable<SysEventOutbox>().In(id).ExecuteCommandAsync();
    }

    /// <summary>
    /// 批量删除事件信息
    /// </summary>
    /// <param name="ids">事件唯一标识符集合</param>
    public async Task DeleteManyAsync(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Deleteable<SysEventOutbox>().In(idList).ExecuteCommandAsync();
    }
}
