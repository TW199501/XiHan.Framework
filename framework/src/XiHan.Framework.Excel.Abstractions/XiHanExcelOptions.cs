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
    /// 因此容器路径另有三道解压侧上限（解压后总长、单个部件解压后长度、单个部件的解压比，
    /// 见 <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 一族与
    /// <see cref="MaxImportCompressionRatio"/>），在把工作簿交给读取器之前先按 zip 元数据判完；
    /// 调高本值不会放宽那三道界。
    /// </para>
    /// <para>
    /// 输入流不可定位时判不了 <c>Length</c>，那种流在读档之前就已经被两条路径拒掉，
    /// 因此本项不需要为「不可定位的流」另定口径。
    /// </para>
    /// </remarks>
    public long MaxImportBytes { get; set; } = ExcelConstants.DefaultMaxImportBytes;

    /// <summary>
    /// xlsx 容器里单个部件的解压比上限（解压后长度 ÷ 压缩后长度的倍数），<c>0</c> 或负数表示不判解压比
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本项是启发式，不是内存界。</b>界定「一份档能让本进程吃多少<u>解压后字节</u>」的是两道绝对上限：
    /// 单个部件解压后长度 <see cref="ExcelConstants.MaxImportEntryDecompressedBytes"/> 与
    /// 解压后总长 <see cref="ExcelConstants.MaxImportDecompressedBytes"/>，两者都不可配置、也不受本项影响。
    /// 解压比只是在这两道绝对界之下多认一种形态：压缩后很小、展开后很大。
    /// </para>
    /// <para>
    /// <b>那两道绝对上限说的也不是托管占用。</b>解压后的内容进工作簿读取器还要按字符与解析结构再展开一遍，
    /// 实测一份解压后 387.6 MiB 的共享字串部件换来约 777.2 MiB 托管占用（约 2 倍，倍数不是常数，
    /// 随部件内容与读取器的缓冲方式变动）。按两道界当前的 1 GiB／2 GiB 取值，最坏情形的托管占用是
    /// <u>GiB 量级</u>：要按更低的内存预算部署，请把 <see cref="MaxImportBytes"/>（压缩后的档大小）收得更紧，
    /// 或调低 <see cref="MaxImportRows"/>，而不是指望这两道界把内存压在几十兆。换算关系详见
    /// <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 的说明。
    /// </para>
    /// <para>
    /// 之所以可配置：比值高低由<u>产出这份档的工具</u>决定，而不只由内容决定。工作表里 <c>row</c> 与
    /// <c>c</c> 元素的 <c>r</c> 属性在规格上是可选的，不写 <c>r</c> 的产出者会让整段 <c>sheetData</c>
    /// 逐字节重复，同样的数据压缩比可以差一个数量级。因此高度重复但完全合法的档可能越过默认值，
    /// 这时把本项调高、或设成 <c>0</c> 关掉比值判据即可，两道绝对上限照旧生效。
    /// </para>
    /// <para>
    /// <b><c>0</c> 与负数是有意允许的取值，不是漏掉的校验</b>：它们的含义是「不做解压比检查」，
    /// 给重复性极高的合法资料一条明示的出路。关掉比值判据<u>不等于</u>关掉解压侧防护——
    /// 上述两道绝对上限仍然逐部件判，超限照样抛 <see cref="InvalidOperationException"/>。
    /// 默认值是 <see cref="ExcelConstants.MaxImportCompressionRatio"/>。
    /// </para>
    /// <para>
    /// 只对解压后长度不小于 1 MiB 的部件判：更小的部件即使比值难看也占不了多少内存，
    /// 而它们的总量另有 <see cref="ExcelConstants.MaxImportDecompressedBytes"/> 兜住。
    /// 本项只对 <c>xlsx</c> 有意义，文字档与固定宽度档没有压缩容器，不参与判定。
    /// </para>
    /// </remarks>
    public int MaxImportCompressionRatio { get; set; } = ExcelConstants.MaxImportCompressionRatio;
}
