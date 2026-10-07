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
    /// <para>
    /// 取值必须是正整数且不高于 <see cref="ExcelConstants.DefaultMaxImportRows"/>，越界的取值在构造点抛出，
    /// 不夹回上界。两条导入路径（容器与文字档、固定宽度）在构造时各取一次。
    /// </para>
    /// <para>
    /// 本项低于 <see cref="ExcelConstants.DefaultMaxImportRows"/> 时视为指定了上限：单次导入没有给
    /// <c>ExcelImportOptions.MaxRowCount</c> 时，档的数据行超过本值<u>按本值截断、不报错</u>。
    /// 本项等于 <see cref="ExcelConstants.DefaultMaxImportRows"/>（含未配置）时，档的数据行超过上限、
    /// 且<u>上限之后仍有数据行</u>，两条导入路径都抛 <see cref="InvalidOperationException"/>
    /// 并点名上限值。口径详见 <c>ExcelImportOptions.MaxRowCount</c> 的说明。
    /// </para>
    /// </remarks>
    public int MaxImportRows { get; set; } = ExcelConstants.DefaultMaxImportRows;

    /// <summary>
    /// 框架侧导入档大小上限（字节），单次导入的档超过它就拒收
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两条导入路径（容器与文字档、固定宽度）都在读第一个字节之前按本值判 <c>Stream.Length</c>，
    /// 超限抛 <see cref="InvalidOperationException"/> 并点名档的实际大小与本上限：拒收的是<u>整份档</u>，
    /// 不截断读取。默认值是 <see cref="ExcelConstants.DefaultMaxImportBytes"/>（256 MiB）。
    /// </para>
    /// <para>
    /// 本项管的是<u>压缩后</u>的档大小。容器路径另有三道解压侧上限（解压后总长、单个部件解压后长度、
    /// 单个部件的解压比，见 <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 一族与
    /// <see cref="MaxImportCompressionRatio"/>），在把工作簿交给读取器之前先按 zip 元数据判完；
    /// 调高本值不会放宽那三道界。
    /// </para>
    /// </remarks>
    public long MaxImportBytes { get; set; } = ExcelConstants.DefaultMaxImportBytes;

    /// <summary>
    /// xlsx 容器里单个部件的解压比上限（解压后长度 ÷ 压缩后长度的倍数），<c>0</c> 或负数表示不判解压比
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本项是启发式判据，不是内存界。解压后字节数由
    /// <see cref="ExcelConstants.MaxImportEntryDecompressedBytes"/> 与
    /// <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 两道绝对上限界定，两者不可配置、不受本项影响，
    /// 本项取 <c>0</c> 或负数时照样逐部件判，超限抛 <see cref="InvalidOperationException"/>。
    /// </para>
    /// <para>
    /// 高度重复的合法档可能越过默认值，此时可调高本项或设为 <c>0</c>。
    /// 默认值是 <see cref="ExcelConstants.MaxImportCompressionRatio"/>。
    /// </para>
    /// <para>
    /// 只对解压后长度不小于 1 MiB 的部件判。本项只对 <c>xlsx</c> 有意义，文字档与固定宽度档不参与判定。
    /// </para>
    /// </remarks>
    public int MaxImportCompressionRatio { get; set; } = ExcelConstants.MaxImportCompressionRatio;
}
