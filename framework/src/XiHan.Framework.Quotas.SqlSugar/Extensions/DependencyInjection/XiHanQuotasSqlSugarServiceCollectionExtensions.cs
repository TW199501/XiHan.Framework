// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.SqlSugar.Options;
using XiHan.Framework.Quotas.SqlSugar.Stores;

namespace XiHan.Framework.Quotas.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 曦寒配额 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanQuotasSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 把配额存储替换为 SqlSugar 持久化实现
    /// </summary>
    /// <remarks>
    /// 用 <c>Replace</c> 而不是 <c>TryAdd</c>：本包是消费方显式选择的持久化实现，必须覆盖
    /// XiHan.Framework.Quotas 注册的进程内默认存储。生命周期为 Scoped，
    /// 因为 SqlSugar 客户端解析器按请求作用域登记，配额操作又要独占一个事务型工作单元。
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    /// <exception cref="ArgumentNullException">服务集合或配置为 null</exception>
    public static IServiceCollection AddXiHanQuotasSqlSugar(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<XiHanQuotasSqlSugarOptions>()
            .Bind(configuration.GetSection(XiHanQuotasSqlSugarOptions.SectionName));

        services.Replace(ServiceDescriptor.Scoped<IQuotaStore, SqlSugarQuotaStore>());

        return services;
    }
}
