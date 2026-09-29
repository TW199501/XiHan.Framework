// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Security.SqlSugar;

/// <summary>
/// 曦寒框架安全模块 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSecuritySqlSugarModule))]</c> 即启用。
/// 本模块提供密码历史记录的 SqlSugar 落库实现，替换主包的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanSecurityModule),
    typeof(XiHanDataModule)
)]
public class XiHanSecuritySqlSugarModule : XiHanModule
{
}
