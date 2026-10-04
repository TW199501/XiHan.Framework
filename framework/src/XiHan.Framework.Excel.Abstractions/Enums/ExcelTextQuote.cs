// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 文本档的引号策略
/// </summary>
public enum ExcelTextQuote
{
    /// <summary>
    /// 最小引号：值内含分隔符、引号或换行时才加引号
    /// </summary>
    Minimal,

    /// <summary>
    /// 全部加引号：每个值都裹一层引号
    /// </summary>
    All,

    /// <summary>
    /// 不加引号：值内含分隔符或换行时写出结果无法原样往返，由导出器改写并留下日志，不静默产出坏数据
    /// </summary>
    None
}
