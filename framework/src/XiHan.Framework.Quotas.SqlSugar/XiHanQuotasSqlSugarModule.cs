// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Quotas.Extensions.DependencyInjection;
using XiHan.Framework.Quotas.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Quotas.SqlSugar;

/// <summary>
/// 曦寒框架用量配额的 SqlSugar 持久化提供程序模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanQuotasSqlSugarModule))]</c> 即把配额存储换成 SqlSugar 实现。
/// 它必须在 <see cref="XiHanQuotasModule"/> 之后加载，才能替换掉进程内默认存储。
/// </remarks>
[DependsOn(
    typeof(XiHanQuotasModule),
    typeof(XiHanDataModule)
)]
public class XiHanQuotasSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context">服务配置上下文</param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddXiHanQuotasSqlSugar(services.GetConfiguration());
    }
}
