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
    /// 本项是应用可用的导入行数上限，取值必须是正整数且不高于
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/>：只允许在框架的绝对上界之内收紧，不允许放宽。
    /// 两条导入路径（容器与文字档、固定宽度）在构造时各取一次，配得更低就在更低处截断；越界的取值在构造点抛出，
    /// 不夹回上界。
    /// </para>
    /// <para>
    /// <b>把本项收紧就算「指名」了上限</b>：单次导入没有再给 <c>ExcelImportOptions.MaxRowCount</c> 时，
    /// 档的数据行超过本值<u>按本值截断、不报错</u>——「最多读这么多行」是应用写在配置里的请求，
    /// 与写在调用点上的 <c>MaxRowCount</c> 同一种性质。判据是「生效的上限低于框架硬上限」，
    /// 因此把本项配成与 <see cref="ExcelConstants.DefaultMaxImportRows"/> 相同的数与不配没有区别：
    /// 两者生效的都是框架那道保护性硬上限，而它不属于任何请求。这时档的数据行超过上限、
    /// 且<u>上限之后仍有数据行</u>，两条导入路径都抛 <see cref="InvalidOperationException"/>
    /// 并点名下限值，不静默少交行。口径详见 <c>ExcelImportOptions.MaxRowCount</c> 的说明。
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
    /// 不截断读取、也不「先读前面一段」，因为半份档交回的是看起来成功的数据损失。
    /// 默认值是 <see cref="ExcelConstants.DefaultMaxImportBytes"/>（256 MiB）。
    /// </para>
    /// <para>
    /// 本项管的是<u>压缩后</u>的档大小。xlsx 是 zip 容器，压缩后的大小与读它要付出的内存不成比例，
    /// 因此容器路径另有三道不可配置的解压侧上限（解压后总长、单个部件解压后长度、单个部件的解压比，
    /// 见 <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 一族），
    /// 在把工作簿交给读取器之前先按 zip 元数据判完；调高本值不会放宽那三道界。
    /// </para>
    /// <para>
    /// 输入流不可定位时判不了 <c>Length</c>，那种流在读档之前就已经被两条路径拒掉，
    /// 因此本项不需要为「不可定位的流」另定口径。
    /// </para>
    /// </remarks>
    public long MaxImportBytes { get; set; } = ExcelConstants.DefaultMaxImportBytes;
}
