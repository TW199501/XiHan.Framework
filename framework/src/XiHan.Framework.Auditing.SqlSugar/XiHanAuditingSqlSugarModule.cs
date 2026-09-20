// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Auditing.SqlSugar;

/// <summary>
/// 曦寒框架审计日志 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuditingSqlSugarModule))]</c> 即启用。
/// 本模块提供审计日志的 SqlSugar 实体定义；日志写入器在后续版本提供。
/// </remarks>
[DependsOn(
    typeof(XiHanAuditingModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuditingSqlSugarModule : XiHanModule
{
}
