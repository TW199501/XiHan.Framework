// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Enums;

/// <summary>
/// 导入来源格式
/// </summary>
/// <remarks>
/// 比 <see cref="ExcelFormat"/> 多出旧版二进制 <see cref="Xls"/>：框架能读不能写该格式。
/// 文字档没有可靠的文件签名，自动判别只对 <see cref="Xls"/> 与 <see cref="Xlsx"/> 有意义。
/// </remarks>
public enum ExcelImportFormat
{
    /// <summary>
    /// 旧版二进制工作簿（<c>.xls</c>，OLE 复合文件），仅支持导入
    /// </summary>
    Xls,

    /// <summary>
    /// Office Open XML 工作簿（<c>.xlsx</c>，zip 签名）
    /// </summary>
    Xlsx,

    /// <summary>
    /// 分隔符文本（<c>.csv</c>）
    /// </summary>
    Csv,

    /// <summary>
    /// 纯文本表格（<c>.txt</c>）
    /// </summary>
    Txt
}
