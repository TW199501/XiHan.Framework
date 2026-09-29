// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Authentication.SqlSugar;

/// <summary>
/// 曦寒框架认证存储 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]</c> 即启用。
/// </remarks>
[DependsOn(
    typeof(XiHanAuthenticationModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuthenticationSqlSugarModule : XiHanModule
{
}
