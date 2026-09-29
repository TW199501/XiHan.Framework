// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Traffic.GrayRouting.Abstractions;
using XiHan.Framework.Traffic.GrayRouting.Models;
using XiHan.Framework.Traffic.SqlSugar.Entities;
using XiHan.Framework.Traffic.SqlSugar.Mapping;
using XiHan.Framework.Traffic.SqlSugar.Options;

namespace XiHan.Framework.Traffic.SqlSugar.Repositories;

/// <summary>
/// 灰度规则 SqlSugar 只读仓储
/// </summary>
/// <remarks>
/// 只负责查询与缓存，不提供写方法；规则的增删改由应用层直接对 sys_gray_rule 表操作。
/// </remarks>
public class SqlSugarGrayRuleRepository : IGrayRuleRepository
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly XiHanTrafficSqlSugarOptions _options;
    private readonly Lock _refreshLock = new();

    private volatile Dictionary<string, GrayRule> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastRefreshTime = DateTimeOffset.MinValue;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="scopeFactory">服务范围工厂，用于按需解析 Scoped 的客户端解析器</param>
    /// <param name="options">缓存刷新配置</param>
    public SqlSugarGrayRuleRepository(
        IServiceScopeFactory scopeFactory,
        IOptions<XiHanTrafficSqlSugarOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    /// <summary>
    /// 获取所有启用的灰度规则
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>规则列表</returns>
    public async Task<List<IGrayRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken);

        return [.. _cache.Values.Where(rule => rule.IsEnabled)];
    }

    /// <summary>
    /// 根据规则标识获取规则
    /// </summary>
    /// <param name="ruleId">规则标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>规则</returns>
    public async Task<IGrayRule?> GetRuleByIdAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken);

        return _cache.GetValueOrDefault(ruleId);
    }

    /// <summary>
    /// 强制从数据库重新加载全部规则
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var clientResolver = scope.ServiceProvider.GetRequiredService<ISqlSugarClientResolver>();
        var client = clientResolver.GetClientForEntity<SysGrayRule>();

        var entities = await client.Queryable<SysGrayRule>().ToListAsync(cancellationToken);

        var loaded = new Dictionary<string, GrayRule>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            loaded[entity.BasicId] = GrayRuleMapper.ToModel(entity);
        }

        lock (_refreshLock)
        {
            _cache = loaded;
            _lastRefreshTime = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// 缓存到期时刷新，未到期直接返回
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastRefreshTime < _options.RefreshInterval)
        {
            return;
        }

        await RefreshAsync(cancellationToken);
    }
}
