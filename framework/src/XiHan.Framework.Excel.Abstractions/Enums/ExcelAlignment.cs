// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 单元格水平对齐
/// </summary>
public enum ExcelAlignment
{
    /// <summary>
    /// 自动对齐：写出方不显式设置，沿用目标档格式自身的默认
    /// </summary>
    Auto,

    /// <summary>
    /// 左对齐
    /// </summary>
    Left,

    /// <summary>
    /// 居中对齐
    /// </summary>
    Center,

    /// <summary>
    /// 右对齐
    /// </summary>
    Right
}
