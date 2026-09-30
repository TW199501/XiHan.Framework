// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Quotas.Extensions.DependencyInjection;
using XiHan.Framework.Timing;

namespace XiHan.Framework.Quotas;

/// <summary>
/// 曦寒框架用量配额模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanQuotasModule))]</c> 即启用。
/// 本模块只定义用量配额的契约，并注册一律拒绝的安全默认实现；政策来源与持久化由应用替换。
/// 租户功能开关不在本模块，由 XiHan.Framework.MultiTenancy 的 ITenantFeatureChecker 提供。
/// </remarks>
[DependsOn(
    typeof(XiHanTimingModule)
)]
public class XiHanQuotasModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context">服务配置上下文</param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddXiHanQuotas(services.GetConfiguration());
    }
}
