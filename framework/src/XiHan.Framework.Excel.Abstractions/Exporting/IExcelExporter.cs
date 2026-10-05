// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 导出契约：按目标格式把表规格写成一份档，并把「样式有没有落地」写在返回值上
/// </summary>
/// <remarks>
/// <para>
/// 本契约是导出的统一入口：调用方交回一份 <see cref="ExcelSheetSpec"/> 与目标
/// <see cref="ExcelFormat"/>，由实现决定交给哪个写出者。行数规模与是否流式由
/// <see cref="ExcelSheetSpec.ExpectedRowCount"/> 与 <see cref="ExcelSheetSpec.ForceStreaming"/> 两个入参决定，
/// 两个都不给时实现<u>抛出</u>而不是猜一个——行数不可预知时「自动切流式」做不到，而静默切过去会连带静默丢样式。
/// </para>
/// <para>
/// 降级只发生在 <see cref="ExcelFormat.Xlsx"/> 的流式模式：那一模式承载不了整份排版，返回值必须是
/// <see cref="ExcelExportResult.StylingApplied"/> 为 <c>false</c> 并附上说清丢了什么的
/// <see cref="ExcelExportResult.StylingSkipReason"/>。文字档没有样式概念，写它不构成降级，见
/// <see cref="ExcelExportResult"/> 的说明。
/// </para>
/// <para>
/// 输出流的所有权在调用方：实现只写入，绝不关闭它，也不复位流位置——写出从调用方留下的位置开始，
/// 写完停在末尾，要读回请自己回到 0。多份档共用一条流时这一点尤其要紧，实现不会替调用方把位置挪回去。
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
    /// <c>null</c>；或表规格的 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（该属性是
    /// <c>required</c> 非空成员，<c>null</c> 只会来自非法声明，并在写出第一格之前就被拒）</exception>
    /// <exception cref="ArgumentException"><paramref name="sheet"/> 的
    /// <see cref="ExcelSheetSpec.Columns"/> 是 <c>null</c> 或一列都没有（<see
    /// cref="ArgumentException.ParamName"/> 为 <c>Columns</c>，一条都没有就没有可写出的表头与取值，
    /// 交回的只会是一份每行空白或全空的档）；或 <see cref="ExcelSheetSpec.SheetName"/> 落不进工作簿
    /// （<see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>，仅在目标格式是
    /// <see cref="ExcelFormat.Xlsx"/> 时判）；或文字档的选项组合不成立（如分隔符取换行符、
    /// 取引号字符、或免引号策略配空格分隔符，<see cref="ArgumentException.ParamName"/> 为
    /// <c>textOptions</c>）；或 <see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的十六进制颜色串（只在目标格式是
    /// <see cref="ExcelFormat.Xlsx"/> 且走全量工作簿路径时判——流式模式根本不写表头底色，文字档也没有底色，
    /// 那两种场合这个值不参与写出，也不报错，与「设置了但本路径不承载的选项」的既有口径一致）；
    /// 或某列的 <see cref="ExcelColumn.Header"/>、<see cref="ExcelSheetSpec.Title"/> 长过
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 个字符（<see cref="ArgumentException.ParamName"/> 分别为
    /// <c>Header</c> 与 <c>Title</c>，只在目标格式是 <see cref="ExcelFormat.Xlsx"/> 时判，两条 xlsx 路径共用
    /// 同一份判据、抛出的消息逐字相同，文字档没有单元格因此没有这道上限——表头与标题落的也是单元格，
    /// 与数据格共用同一道上限，超长时写出侧不截断，判定排在写出第一格之前，抛出时输出流零字节、
    /// 行集合一次都没被枚举。<see cref="ExcelSheetSpec.Title"/> 在流式模式不落档，但声明超长照样拒：
    /// 「本路径不承载某个选项」与「这个选项的声明非法」是两件事，前者不报错，后者两条路径一起拒，
    /// 免得同一份规格能不能导由分派器按行数选了哪条路径决定）</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不在
    /// <see cref="ExcelFormat"/> 的定义范围内；或 <see cref="ExcelSheetSpec.ExpectedRowCount"/> 为负数
    /// （这一条不分格式：它是分派输入，任何目标格式下都不是合法的行数声明）；或某列的
    /// <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255 的有限数、某列的
    /// <see cref="ExcelColumn.Alignment"/> 不在定义范围内（列设置这两条只在全量工作簿路径判——
    /// 流式模式与文字档都不承载列宽与水平对齐，按上面的口径不判也不报错）</exception>
    /// <exception cref="InvalidOperationException">目标格式是 <see cref="ExcelFormat.Xlsx"/> 而
    /// <see cref="ExcelSheetSpec.ForceStreaming"/> 与 <see cref="ExcelSheetSpec.ExpectedRowCount"/>
    /// 两个都没给；或行集合里有某笔元素与 <see cref="ExcelSheetSpec.RowType"/> 不符；或某个行值是工作簿
    /// 装不下的（早于 1900-01-01 的 <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、
    /// <c>NaN</c> 或 <c>±∞</c>、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <c>long</c>／<c>ulong</c>／
    /// <c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992（2 的 53 次方）的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过单元格上限的字串）——五类都只在全量与流式两条 xlsx
    /// 路径判，且两条路径判得一样、抛出的成因句逐字相同（文字档路径不判这五类：它没有数值格也没有日期格，
    /// 取值一律按文本原样写出）；日期下限那一条三种型别同判，不按各条路径把该型别落成日期格还是文本格区分；
    /// 或某个取值委托交回了工作簿不接受的东西；或（仅流式模式）两列共用了同一个
    /// <see cref="ExcelColumn.Key"/>——那一模式的行模型按键取值，重复键会让后一列盖掉前一列；或（仅两条 xlsx 路径）
    /// 标题行、表头行与数据行加起来要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后——单张工作表
    /// 只有这么多行，超出的行没有可落的位置，上限按每张工作表各自计、不做整簿累计，文字档路径没有这道上限，
    /// 消息点出上限值与「分成多张表或改用文字档」两条出路；两条 xlsx 路径数的是各自要落的那一行行号，
    /// 全量路径把标题行算进去、流式模式不写标题行因此不算，能导的行数由落档形态决定，不是两套上限。
    /// 各项消息都点名实际成因</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 入参检查（流、表规格、列清单、流式表态、表名、列设置）全部排在写出第一个字节之前，这类失败不留半份文件。
    /// 要等取到行才知道的失败（行型不符、取值越出工作簿可表示范围、编码收不下字符）做不到这一点：
    /// 行集合是惰性序列，值没取到就判不出来，抛出时前面的内容可能已经落进流里。
    /// </para>
    /// <para>
    /// <b>取消的口径只有一条，四条写出路径共用：</b>取消被观察到时一律抛
    /// <see cref="OperationCanceledException"/>，绝不交出 <see cref="ExcelExportResult"/>——
    /// 「不谎报成功」是承诺；<c>Stream</c> 里剩什么不是承诺，也不承诺失败原子性。落点按取消发生的时机分三种：
    /// <list type="bullet">
    /// <item>入口就被观察到：输出流零字节；</item>
    /// <item>写出中途被观察到：输出流可能已有内容——文字档是前半部分行，工作簿流式模式是一份没收尾的档，
    /// 两者都读不回来；</item>
    /// <item>写出收尾之后才被观察到：输出流可能已是一份完整的档，交回的仍然是异常。</item>
    /// </list>
    /// 因此抛出之后调用方必须丢弃这条流的内容（不要读它、不要接着往上追加、也不要只把位置回到 0 就交给下游），
    /// 要重试就换一条新流。
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
    /// （<see cref="ArgumentException.ParamName"/> 分别为 <c>Header</c> 与 <c>Title</c>）——表头与标题落的也是单元格，
    /// 与数据格共用同一道上限，超长时写出侧不截断；这几类都排在写第一格之前，整份档建好才落盘一次，
    /// 因此任何一张表触发它们，输出流都是零字节、那张表的行集合一次都没被枚举</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0
    /// 且不高于 255 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内；
    /// 或某张表的 <see cref="ExcelSheetSpec.ExpectedRowCount"/> 为负数（点名第几张）——多表恒走全量工作簿，
    /// 这个值在这条入口只校验成立与否、不参与分流，也不因为用不到它就静默放过</exception>
    /// <exception cref="InvalidOperationException">清单里有哪张表的 <see cref="ExcelSheetSpec.ForceStreaming"/>
    /// 为 <c>true</c>（多表流式不在本组件的承诺范围内，冲突时拒绝而不是偷偷改走全量）；或某张表的行集合里有
    /// 某笔元素与其 <see cref="ExcelSheetSpec.RowType"/> 不符；或某个行值是工作簿装不下的（取值域判据与
    /// <see cref="ExportAsync"/> 共用同一份，含有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值）；或某张表的标题行、表头行与
    /// 数据行加起来要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后——行数上限按<u>每张工作表各自</u>计，
    /// 不做整簿累计，因此两张各占上限六成的表能同时写进一个工作簿，而任何一张触线就整个请求被拒，
    /// 消息点出触线那张表的表名、上限值与「分成多张表或改用文字档」两条出路</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 本入口<u>恒走全量工作簿</u>：多表要共用一个工作簿、共用表名判重与落位顺序，流式模式承担不了，
    /// 因此这里没有阈值分流。清单里任何一张表要求流式即整个请求被拒，不做「这张表改成全量」的改写。
    /// </para>
    /// <para>
    /// 取消口径与 <see cref="ExportAsync"/> 同一条：一律抛出、不交出结果，不承诺失败原子性。整份档先在内存里
    /// 建好再落盘，所以落盘之前抛出的失败在输出流里零字节；落盘之后才被观察到的取消会留下一份完整的档，
    /// 而交回的仍然是异常。
    /// </para>
    /// </remarks>
    Task<ExcelExportResult> ExportAllAsync(
        Stream output,
        IReadOnlyList<ExcelSheetSpec> sheets,
        CancellationToken cancellationToken = default);
}
