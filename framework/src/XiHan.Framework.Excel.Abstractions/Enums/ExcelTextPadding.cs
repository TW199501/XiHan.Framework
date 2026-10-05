// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 固定宽度布局下的补位方向，取值指定的是补字符落在哪一侧
/// </summary>
public enum ExcelTextPadding
{
    /// <summary>
    /// 左侧补字符：内容靠右显示
    /// </summary>
    Left,

    /// <summary>
    /// 右侧补字符：内容靠左显示
    /// </summary>
    Right
}
