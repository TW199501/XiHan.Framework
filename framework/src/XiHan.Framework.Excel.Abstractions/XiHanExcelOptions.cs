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
    /// <remarks>
    /// 判定只在看表规格的 <see cref="XiHan.Framework.Excel.Abstractions.Exporting.ExcelSheetSpec.ForceStreaming"/>
    /// 为「未表态」时进行：<see cref="XiHan.Framework.Excel.Abstractions.Exporting.ExcelSheetSpec.ExpectedRowCount"/>
    /// 大于或等于本值即走流式。0 或负数等于「任何非负行数都达到阈值」，也就是一律走流式；
    /// 表规格里显式表态 <c>true</c> 或 <c>false</c> 时本值不参与判定。
    /// </remarks>
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
    /// <remarks>
    /// 本项是应用可用的导入行数上限，取值必须是正整数且不高于
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/>：只允许在框架的绝对上界之内收紧，不允许放宽。
    /// 两条导入路径（容器与文字档、固定宽度）在构造时各取一次，配得更低就在更低处截断；越界的取值在构造点抛出，
    /// 不夹回上界。
    /// </remarks>
    public int MaxImportRows { get; set; } = ExcelConstants.DefaultMaxImportRows;
}
