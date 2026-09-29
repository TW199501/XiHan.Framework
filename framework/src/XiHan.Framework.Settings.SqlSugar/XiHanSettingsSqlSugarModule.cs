// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Settings.SqlSugar;

/// <summary>
/// 曦寒框架设置管理 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSettingsSqlSugarModule))]</c> 即启用。
/// 本模块提供设置值的 SqlSugar 实体定义；存储实现的注册在后续任务提供。
/// </remarks>
[DependsOn(
    typeof(XiHanSettingsModule),
    typeof(XiHanDataModule)
)]
public class XiHanSettingsSqlSugarModule : XiHanModule
{
}
