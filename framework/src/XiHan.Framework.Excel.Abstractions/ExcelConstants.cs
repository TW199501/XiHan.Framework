// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions;

/// <summary>
/// Excel 导入导出常量定义
/// </summary>
/// <remarks>
/// 这里是框架侧的默认值与固定字面量，供选项默认值和各个提供程序共用，不随配置变化。
/// </remarks>
public static class ExcelConstants
{
    /// <summary>
    /// 默认流式写出阈值（行数），达到该规模改用流式导出
    /// </summary>
    public const int DefaultStreamingThreshold = 50_000;

    /// <summary>
    /// 默认自适应列宽采样行数
    /// </summary>
    public const int DefaultAutoWidthSampleRows = 500;

    /// <summary>
    /// 默认文本编码名称
    /// </summary>
    public const string DefaultEncodingName = "utf-8-bom";

    /// <summary>
    /// 默认导入行数硬上限
    /// </summary>
    public const int DefaultMaxImportRows = 1_000_000;

    /// <summary>
    /// 一个单元格能承载的字符数上限
    /// </summary>
    /// <remarks>
    /// 这是 xlsx 自身的硬界，不是本组件自定的档位：超出的字串在工作簿里没有对应形态。两条 xlsx 写出路径
    /// 都在取值阶段拒掉超长字串并回报本常量，不截断（截断会丢弃数据）、也不交给工作簿去抛它那句英文异常。
    /// </remarks>
    public const int MaxCellTextLength = 32_767;

    /// <summary>
    /// xlsx 单张工作表的行数上限（标题行与表头行都算在内）
    /// </summary>
    /// <remarks>
    /// 这是 xlsx 自身的硬界，不是本组件自定的档位：一张工作表只有这么多行，再往下的行在档里没有可落的位置。
    /// 两条 xlsx 写出路径都在既有的逐行循环里按「这一行数据要落到工作表的第几行」比对，超出即<u>抛出</u>，
    /// 不丢行、不截断，也不接着写出一份读不回来的档。上限按<u>单张工作表</u>各自计——多表路径每张表都有自己的
    /// 标题行与表头行，各自数各自的，不做整簿累计，因此两张各占六成的表能同时写进一个工作簿。
    /// 这道界与 <see cref="MaxFixedRowWidthBytes"/> 数值相同，但一个数的是行、一个数的是单行字节，
    /// 两道界互不相干，不得互相代用。文字档路径没有这道上限。
    /// </remarks>
    public const int MaxSheetRows = 1_048_576;

    /// <summary>
    /// 本组件对 xlsx 数值格承诺能原样落格的有效数字位数上限
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是本组件自定的取值域档位，判据只有一份：<see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、
    /// <see cref="double"/>、<see cref="float"/> 五类型的取值，其不变文化文本形态的有效数字多于本常量时，
    /// 两条 xlsx 写出路径<u>一律抛出</u>，不写入、不降级，也不把值改写成较短的数。
    /// 之所以按「有效数字」而不是「双精度能不能装下」判：整数值 2^53 以内双精度装得下，
    /// 但落进档里的位数由写出库自己的格式决定，两条路径对同一取值给出的结果并不相同，
    /// 因此本组件把承诺收到一条两条路径都判得出的界上。
    /// </para>
    /// <para>
    /// <see cref="float"/> 不在这五类型的最初清单里，是本组件按同一判据扩用进来的：单精度值落数值格时先展开成
    /// <see cref="double"/>，展开后的形态多于本常量即拒（<c>0.1f</c> 就属于这一类）。
    /// 需要完整精度的值由呼叫端转成字符串栏位——字符串格保住逐字内容，数值格保住本常量以内的有效数字，
    /// 两者不互相代打。
    /// </para>
    /// </remarks>
    public const int MaxExactNumericSignificantDigits = 15;

    /// <summary>
    /// 固定宽度导入的单行列宽总和硬上限（字节）
    /// </summary>
    /// <remarks>
    /// 定宽档一行要先整行落进缓冲才能按字节切列，不设上界就等于让某一份档的一行决定本进程的内存占用，
    /// 与「默认实现不得无界增长」冲突。取 1 MiB：常见定长记录档一行不超过数 KB，这道界挡的是列宽配错
    /// （或恶意写宽）的请求，不影响正常档。列宽总和超过它时导入器<u>抛出</u>该上限与实际总和，
    /// 不夹改成较小的宽度、也不截断列清单——静默改写列布局会交回一份列位错开的档。
    /// </remarks>
    public const int MaxFixedRowWidthBytes = 1_048_576;

    /// <summary>
    /// CSV 内容类型
    /// </summary>
    public const string CsvContentType = "text/csv";

    /// <summary>
    /// 纯文本内容类型
    /// </summary>
    public const string PlainTextContentType = "text/plain";

    /// <summary>
    /// xlsx 内容类型
    /// </summary>
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// CSV 文件扩展名
    /// </summary>
    public const string ExtensionCsv = ".csv";

    /// <summary>
    /// 纯文本文件扩展名
    /// </summary>
    public const string ExtensionTxt = ".txt";

    /// <summary>
    /// xlsx 文件扩展名
    /// </summary>
    public const string ExtensionXlsx = ".xlsx";
}
