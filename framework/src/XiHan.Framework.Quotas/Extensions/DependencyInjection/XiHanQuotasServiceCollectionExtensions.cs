// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas.Providers;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.Stores;

namespace XiHan.Framework.Quotas.Extensions.DependencyInjection;

/// <summary>
/// 曦寒配额服务集合扩展
/// </summary>
public static class XiHanQuotasServiceCollectionExtensions
{
    /// <summary>
    /// 添加曦寒配额服务
    /// </summary>
    /// <remarks>
    /// 全部以 <c>TryAddSingleton</c> 注册安全默认实现，应用注册自己的实现即替换默认项。
    /// 传入配置时按 <see cref="XiHanQuotasOptions.SectionName"/> 绑定选项。
    /// 租户功能开关不由本模块提供，复用 XiHan.Framework.MultiTenancy 的 ITenantFeatureChecker。
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置，为 null 时不绑定配置节</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanQuotas(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<XiHanQuotasOptions>(
                configuration.GetSection(XiHanQuotasOptions.SectionName));
        }

        services.TryAddSingleton<IQuotaPolicyProvider, DefaultQuotaPolicyProvider>();
        services.TryAddSingleton<IQuotaStore, DefaultQuotaStore>();

        return services;
    }
}
