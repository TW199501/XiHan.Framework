// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.EventBus.SqlSugar.Options;

/// <summary>
/// 事件收发件箱 SqlSugar 存储配置
/// </summary>
public class XiHanSqlSugarEventBoxOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:EventBus:SqlSugar";

    /// <summary>
    /// 领取超时，超过该时长仍未删除的已领取记录可被重新领取
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
