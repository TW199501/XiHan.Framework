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
    /// 选项基础设施由本扩展自己登记，不依赖其它模块先 <c>AddOptions</c>；非法选项在启动校验期即失败，
    /// 不必等到首次解析存储才暴露。全部服务以 <c>TryAddSingleton</c> 注册安全默认实现，
    /// 应用注册自己的实现即替换默认项。
    /// 租户功能开关不由本模块提供，复用 XiHan.Framework.MultiTenancy 的 ITenantFeatureChecker。
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置，为 null 时不绑定配置节</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanQuotas(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<XiHanQuotasOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(XiHanQuotasOptions.SectionName));
        }

        options.Validate(static value => value.DefaultReservationTtl > TimeSpan.Zero,
                "预留默认存活时长必须大于零。")
            .Validate(static value => value.NonPeriodicTombstoneRetention > TimeSpan.Zero,
                "不周期去重保留时长必须大于零。")
            .Validate(static value => value.MaxTrackedReservations > 0,
                "配额条目上限必须大于零。")
            .Validate(static value => value.MaxTrackedBuckets > 0,
                "配额桶上限必须大于零。")
            .ValidateOnStart();

        services.TryAddSingleton<IQuotaPolicyProvider, DefaultQuotaPolicyProvider>();
        services.TryAddSingleton<IQuotaStore, DefaultQuotaStore>();

        return services;
    }
}
