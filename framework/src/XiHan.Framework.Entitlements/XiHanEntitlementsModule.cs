// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Entitlements.Extensions.DependencyInjection;

namespace XiHan.Framework.Entitlements;

/// <summary>
/// 曦寒框架租户功能授权与用量配额模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanEntitlementsModule))]</c> 即启用。
/// 本模块只定义功能授权与用量配额的契约，并注册一律拒绝的安全默认实现；政策来源与持久化由应用替换。
/// </remarks>
public class XiHanEntitlementsModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context">服务配置上下文</param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanEntitlements();
    }
}
