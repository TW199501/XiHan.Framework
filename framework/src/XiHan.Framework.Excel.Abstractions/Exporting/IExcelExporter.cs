// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 导出契约：按目标格式把表规格写成一份档，并把「样式有没有落地」写在返回值上
/// </summary>
/// <remarks>
/// <para>
/// 调用方交回一份 <see cref="ExcelSheetSpec"/> 与目标 <see cref="ExcelFormat"/>，由实现决定交给哪个写出者。
/// 目标为 <see cref="ExcelFormat.Xlsx"/> 时，是否流式由 <see cref="ExcelSheetSpec.ExpectedRowCount"/> 与
/// <see cref="ExcelSheetSpec.ForceStreaming"/> 决定，两个都不给时实现<u>抛出</u>。
/// </para>
/// <para>
/// 降级只发生在 <see cref="ExcelFormat.Xlsx"/> 的流式模式：返回值的 <see cref="ExcelExportResult.StylingApplied"/>
/// 为 <c>false</c>，并附上说明丢了什么的 <see cref="ExcelExportResult.StylingSkipReason"/>。写文字档不构成降级，见
/// <see cref="ExcelExportResult"/> 的说明。
/// </para>
/// <para>
/// 输出流的所有权在调用方：实现只写入，不关闭，也不复位流位置；写出从调用方留下的位置开始，写完停在末尾。
/// </para>
/// </remarks>
public interface IExcelExporter
{
    /// <summary>
    /// 把一张表写成一份档
    /// </summary>
    /// <param name="output">输出流，只写入不关闭，位置不由实现管理</param>
    /// <param name="sheet">表规格</param>
    /// <param name="format">目标格式；<see cref="ExcelFormat.Csv"/> 与 <see cref="ExcelFormat.Txt"/> 走文字档，
    /// <see cref="ExcelFormat.Xlsx"/> 按流式表态分流</param>
    /// <param name="textOptions">文字档选项，只在 <paramref name="format"/> 是文字档时解释；
    /// 传 <c>null</c> 等同于使用 <see cref="ExcelTextOptions"/> 的默认值。目标格式是
    /// <see cref="ExcelFormat.Xlsx"/> 时本参数不生效，也不报错</param>
    /// <param name="cancellationToken">取消令牌，逐行检查</param>
    /// <returns>导出结果，含实际写出的扩展名、内容类型与样式落地情况</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为
    /// <c>null</c>；或表规格的 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="sheet"/> 的
    /// <see cref="ExcelSheetSpec.Columns"/> 是 <c>null</c> 或一列都没有（<see
    /// cref="ArgumentException.ParamName"/> 为 <c>Columns</c>）；或 <see cref="ExcelSheetSpec.SheetName"/> 落不进工作簿
    /// （<see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>，仅在目标格式是
    /// <see cref="ExcelFormat.Xlsx"/> 时判）；或文字档的选项组合不成立（如分隔符取换行符、
    /// 取引号字符、或免引号策略配空格分隔符，<see cref="ArgumentException.ParamName"/> 为
    /// <c>textOptions</c>）；或 <see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的十六进制颜色串（只在目标格式是
    /// <see cref="ExcelFormat.Xlsx"/> 且走全量工作簿路径时判）；
    /// 或某列的 <see cref="ExcelColumn.Header"/>、<see cref="ExcelSheetSpec.Title"/> 长过
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 个字符（<see cref="ArgumentException.ParamName"/> 分别为
    /// <c>Header</c> 与 <c>Title</c>，只在目标格式是 <see cref="ExcelFormat.Xlsx"/> 时判，流式模式同样判
    /// <c>Title</c>；判定排在写出第一格之前，抛出时输出流零字节、行集合未被枚举）</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不在
    /// <see cref="ExcelFormat"/> 的定义范围内；或 <see cref="ExcelSheetSpec.ExpectedRowCount"/> 为负数
    /// （不分格式）；或某列的 <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255 的有限数、某列的
    /// <see cref="ExcelColumn.Alignment"/> 不在定义范围内（这两条只在全量工作簿路径判）</exception>
    /// <exception cref="InvalidOperationException">目标格式是 <see cref="ExcelFormat.Xlsx"/> 而
    /// <see cref="ExcelSheetSpec.ForceStreaming"/> 与 <see cref="ExcelSheetSpec.ExpectedRowCount"/>
    /// 两个都没给；或行集合里有某笔元素与 <see cref="ExcelSheetSpec.RowType"/> 不符；或某个行值是工作簿
    /// 装不下的（早于 1900-01-01 的 <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、
    /// <c>NaN</c> 或 <c>±∞</c>、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <c>long</c>／<c>ulong</c>／
    /// <c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992（2 的 53 次方）的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过单元格上限的字串），这几类只在两条 xlsx 路径判；
    /// 或某个取值委托交回了工作簿不接受的东西；或（仅流式模式）两列共用了同一个
    /// <see cref="ExcelColumn.Key"/>；或（仅两条 xlsx 路径）标题行、表头行与数据行加起来要落到第
    /// <see cref="ExcelConstants.MaxSheetRows"/> 行以后，上限按每张工作表各自计，全量路径计入标题行、
    /// 流式模式不写标题行，消息点出上限值与「分成多张表或改用文字档」两条出路。
    /// 各项消息都点名实际成因</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 入参检查（流、表规格、列清单、流式表态、表名、列设置）全部排在写出第一个字节之前，这类失败不留半份文件。
    /// 要等取到行才知道的失败（行型不符、取值越出工作簿可表示范围、编码收不下字符）抛出时，
    /// 前面的内容可能已经落进流里。
    /// </para>
    /// <para>
    /// 取消被观察到时一律抛 <see cref="OperationCanceledException"/>，不交出 <see cref="ExcelExportResult"/>，
    /// 也不承诺失败原子性。落点按取消发生的时机分三种：
    /// <list type="bullet">
    /// <item>入口就被观察到：输出流零字节；</item>
    /// <item>写出中途被观察到：输出流可能已有内容——文字档是前半部分行，工作簿流式模式是一份没收尾的档；</item>
    /// <item>写出收尾之后才被观察到：输出流可能已是一份完整的档，交回的仍然是异常。</item>
    /// </list>
    /// 抛出之后调用方须丢弃这条流的内容。
    /// </para>
    /// </remarks>
    Task<ExcelExportResult> ExportAsync(
        Stream output,
        ExcelSheetSpec sheet,
        ExcelFormat format,
        ExcelTextOptions? textOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把多张表写成同一个 xlsx 工作簿
    /// </summary>
    /// <param name="output">输出流，只写入不关闭，位置不由实现管理</param>
    /// <param name="sheets">表清单，顺序就是工作表的落位顺序，至少一张且每张都要有列</param>
    /// <param name="cancellationToken">取消令牌，逐行与逐表检查</param>
    /// <returns>导出结果，多表恒为带样式的工作簿</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheets"/> 为
    /// <c>null</c>；或清单里有 <c>null</c> 项（<see cref="ArgumentException.ParamName"/> 为
    /// <c>sheets</c>，消息点名是第几项）</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> 为空清单；或清单里某张表的
    /// <see cref="ExcelSheetSpec.Columns"/> 是 <c>null</c> 或一列都没有（点名第几张）；或某张表的表名不可用、
    /// 或与清单里更早那张重名（判重不区分大小写）；或某张表的 <see cref="ExcelSheetSpec.HeaderFill"/>
    /// 不是合法的十六进制颜色串；或某张表某列的 <see cref="ExcelColumn.Header"/>、某张表的
    /// <see cref="ExcelSheetSpec.Title"/> 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符
    /// （<see cref="ArgumentException.ParamName"/> 分别为 <c>Header</c> 与 <c>Title</c>）。这几类都排在写第一格之前，
    /// 输出流零字节、那张表的行集合未被枚举</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0
    /// 且不高于 255 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内；
    /// 或某张表的 <see cref="ExcelSheetSpec.ExpectedRowCount"/> 为负数（点名第几张）</exception>
    /// <exception cref="InvalidOperationException">清单里有哪张表的 <see cref="ExcelSheetSpec.ForceStreaming"/>
    /// 为 <c>true</c>；或某张表的行集合里有某笔元素与其 <see cref="ExcelSheetSpec.RowType"/> 不符；
    /// 或某个行值是工作簿装不下的（判据与 <see cref="ExportAsync"/> 相同，含有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值）；或某张表的标题行、表头行与
    /// 数据行加起来要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后，上限按<u>每张工作表各自</u>计、
    /// 不做整簿累计，消息点出触线那张表的表名、上限值与「分成多张表或改用文字档」两条出路</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 本入口<u>恒走全量工作簿</u>，没有阈值分流；清单里任何一张表要求流式即整个请求被拒。
    /// </para>
    /// <para>
    /// 取消口径与 <see cref="ExportAsync"/> 相同：一律抛出、不交出结果，不承诺失败原子性。整份档先在内存里
    /// 建好再落盘，落盘之前抛出的失败在输出流里零字节；落盘之后才被观察到的取消会留下一份完整的档，
    /// 交回的仍然是异常。
    /// </para>
    /// </remarks>
    Task<ExcelExportResult> ExportAllAsync(
        Stream output,
        IReadOnlyList<ExcelSheetSpec> sheets,
        CancellationToken cancellationToken = default);
}
