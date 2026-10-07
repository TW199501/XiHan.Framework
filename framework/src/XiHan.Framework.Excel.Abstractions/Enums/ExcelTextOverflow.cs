// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 固定宽度布局下内容超出列宽时的处置
/// </summary>
public enum ExcelTextOverflow
{
    /// <summary>
    /// 抛出异常
    /// </summary>
    Throw,

    /// <summary>
    /// 截断到列宽：超出部分不写出
    /// </summary>
    Truncate
}
