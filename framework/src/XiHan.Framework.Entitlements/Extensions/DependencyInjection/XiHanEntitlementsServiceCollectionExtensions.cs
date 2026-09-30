// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Entitlements.Features.Abstractions;
using XiHan.Framework.Entitlements.Options;
using XiHan.Framework.Entitlements.Providers;
using XiHan.Framework.Entitlements.Quotas.Abstractions;
using XiHan.Framework.Entitlements.Quotas.Stores;

namespace XiHan.Framework.Entitlements.Extensions.DependencyInjection;

/// <summary>
/// 曦寒功能授权与配额服务集合扩展
/// </summary>
public static class XiHanEntitlementsServiceCollectionExtensions
{
    /// <summary>
    /// 添加曦寒功能授权与配额服务
    /// </summary>
    /// <remarks>
    /// 全部以 <c>TryAddSingleton</c> 注册安全默认实现，应用注册自己的实现即替换默认项。
    /// 传入配置时按 <see cref="XiHanEntitlementsOptions.SectionName"/> 绑定选项。
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置，为 null 时不绑定配置节</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanEntitlements(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<XiHanEntitlementsOptions>(
                configuration.GetSection(XiHanEntitlementsOptions.SectionName));
        }

        services.TryAddSingleton<IFeatureEntitlementProvider, DefaultFeatureEntitlementProvider>();
        services.TryAddSingleton<IQuotaPolicyProvider, DefaultQuotaPolicyProvider>();
        services.TryAddSingleton<IQuotaStore, DefaultQuotaStore>();

        return services;
    }
}
