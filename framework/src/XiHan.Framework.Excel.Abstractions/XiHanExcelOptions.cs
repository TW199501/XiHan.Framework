// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions;

/// <summary>
/// 曦寒 Excel 导入导出配置选项
/// </summary>
public class XiHanExcelOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Excel";

    /// <summary>
    /// 流式写出阈值（行数），达到该规模改用流式导出
    /// </summary>
    public int StreamingThreshold { get; set; } = ExcelConstants.DefaultStreamingThreshold;

    /// <summary>
    /// 自适应列宽采样行数
    /// </summary>
    public int AutoWidthSampleRows { get; set; } = ExcelConstants.DefaultAutoWidthSampleRows;

    /// <summary>
    /// 默认文本编码名称
    /// </summary>
    public string DefaultEncodingName { get; set; } = ExcelConstants.DefaultEncodingName;

    /// <summary>
    /// 框架侧导入行数硬上限，单次导入的 <c>ExcelImportOptions.MaxRowCount</c> 只能设得更低
    /// </summary>
    public int MaxImportRows { get; set; } = ExcelConstants.DefaultMaxImportRows;
}
