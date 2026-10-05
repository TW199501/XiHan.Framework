// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 文本档的列布局方式
/// </summary>
public enum ExcelTextLayout
{
    /// <summary>
    /// 分隔符布局：列之间用分隔符连接，列宽不固定
    /// </summary>
    Delimited,

    /// <summary>
    /// 固定宽度布局：每列占固定宽度，不足补位、超出按超宽策略处置
    /// </summary>
    FixedWidth
}
