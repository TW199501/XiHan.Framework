// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Routing;
using XiHan.Framework.Data.SqlSugar.Tenanting;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Quotas.SqlSugar.Tests;

/// <summary>
/// 固定返回同一连接配置标识的租户连接解析器
/// </summary>
internal sealed class FixedTenantConnectionResolver : ISqlSugarTenantConnectionResolver
{
    private readonly string _configId;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    public FixedTenantConnectionResolver(string configId)
    {
        _configId = configId;
    }

    /// <summary>
    /// 解析当前租户连接配置标识
    /// </summary>
    /// <returns>固定的连接配置标识</returns>
    public string ResolveCurrentConfigId()
    {
        return _configId;
    }

    /// <summary>
    /// 根据租户标识解析连接配置标识
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="tenantName">租户名称</param>
    /// <returns>固定的连接配置标识</returns>
    public string ResolveConfigId(long? tenantId, string? tenantName = null)
    {
        return _configId;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>只含固定标识的集合</returns>
    public IReadOnlyCollection<string> GetConfigIds()
    {
        return [_configId];
    }

    /// <summary>
    /// 获取全部模块数据源名
    /// </summary>
    /// <returns>空集合</returns>
    public IReadOnlyCollection<string> GetModuleDataSourceNames()
    {
        return [];
    }
}

/// <summary>
/// 可切换的当前租户替身
    /// </summary>
internal sealed class MutableCurrentTenant : ICurrentTenant
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="id">初始租户标识</param>
    public MutableCurrentTenant(long? id = null)
    {
        Id = id;
    }

    /// <summary>
    /// 当前租户标识
    /// </summary>
    public long? Id { get; private set; }

    /// <summary>
    /// 当前租户名称
    /// </summary>
    public string? Name { get; private set; }

    /// <summary>
    /// 当前租户是否可用
    /// </summary>
    public bool IsAvailable => Id is > 0;

    /// <summary>
    /// 临时切换当前租户
    /// </summary>
    /// <param name="id">租户标识</param>
    /// <param name="name">租户名称</param>
    /// <returns>还原上一层上下文的释放器</returns>
    public IDisposable Change(long? id, string? name = null)
    {
        var previousId = Id;
        var previousName = Name;

        Id = id;
        Name = name;

        return new RestoreScope(this, previousId, previousName);
    }

    private sealed class RestoreScope(MutableCurrentTenant owner, long? previousId, string? previousName)
        : IDisposable
    {
        public void Dispose()
        {
            owner.Id = previousId;
            owner.Name = previousName;
        }
    }
}

/// <summary>
/// 不改写连接配置的配置器替身
/// </summary>
internal sealed class PassThroughConnectionConfigurator : ISqlSugarConnectionConfigurator
{
    /// <summary>
    /// 配置连接作用域，不做任何改写
    /// </summary>
    /// <param name="provider">连接作用域提供器</param>
    public void Configure(SqlSugarScopeProvider provider)
    {
    }

    /// <summary>
    /// 确保租户连接，本测试不涉及库隔离租户
    /// </summary>
    /// <param name="tenant">多连接容器</param>
    /// <param name="descriptor">租户连接描述符</param>
    /// <returns>不返回，始终抛出</returns>
    public SqlSugarScopeProvider EnsureTenantConnection(
        ITenant tenant, SqlSugarTenantConnection descriptor)
    {
        throw new NotSupportedException("配额存储测试不涉及库隔离租户的动态连接。");
    }
}

/// <summary>
/// 模块数据源连接解析器替身，被调用即说明路由走错了分支
/// </summary>
internal sealed class StubModuleDataSourceConnectionResolver : IModuleDataSourceConnectionResolver
{
    /// <summary>
    /// 解析模块数据源客户端，本测试不涉及
    /// </summary>
    /// <param name="moduleDataSource">模块数据源名</param>
    /// <param name="parentConfigId">父连接配置标识</param>
    /// <returns>不返回，始终抛出</returns>
    public ISqlSugarClient ResolveClient(string moduleDataSource, string parentConfigId)
    {
        throw new InvalidOperationException("配额存储不应触发模块数据源路由。");
    }
}
