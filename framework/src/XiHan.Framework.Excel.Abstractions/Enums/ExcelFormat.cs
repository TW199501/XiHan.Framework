// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 导出目标格式
/// </summary>
/// <remarks>
/// 框架只导出这三类目标档，不导出旧版二进制 <c>.xls</c>；<c>.xls</c> 仅作为导入侧可识别的输入格式存在。
/// </remarks>
public enum ExcelFormat
{
    /// <summary>
    /// Office Open XML 工作簿（<c>.xlsx</c>），承载样式与数字格式
    /// </summary>
    Xlsx,

    /// <summary>
    /// 分隔符文本（<c>.csv</c>），不承载样式
    /// </summary>
    Csv,

    /// <summary>
    /// 纯文本表格（<c>.txt</c>），不承载样式
    /// </summary>
    Txt
}
