// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.EventBus.SqlSugar;

/// <summary>
/// 曦寒框架分布式事件总线 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSqlSugarEventBusModule))]</c> 即启用。
/// 本模块提供收发件箱的 SqlSugar 实体定义；收发件箱实现在后续版本提供。
/// </remarks>
[DependsOn(
    typeof(XiHanEventBusModule),
    typeof(XiHanDataModule)
)]
public class XiHanSqlSugarEventBusModule : XiHanModule
{
}
