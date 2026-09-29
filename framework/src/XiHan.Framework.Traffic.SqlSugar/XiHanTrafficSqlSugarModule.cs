// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Traffic.SqlSugar;

/// <summary>
/// 曦寒框架流量治理 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanTrafficSqlSugarModule))]</c> 即启用。
/// 本模块提供灰度规则的 SqlSugar 只读仓储，替换主包的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanTrafficModule),
    typeof(XiHanDataModule)
)]
public class XiHanTrafficSqlSugarModule : XiHanModule
{
}
