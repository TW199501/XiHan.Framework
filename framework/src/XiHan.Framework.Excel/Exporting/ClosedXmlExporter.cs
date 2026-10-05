// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using ClosedXML.Excel;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Utils.Core;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// xlsx 工作簿导出器，用 ClosedXML 把一张或多张表写成带样式与排版的工作簿
/// </summary>
/// <remarks>
/// <para>
/// 写出顺序固定为：标题行（<see cref="ExcelSheetSpec.Title"/> 非空时占第一行并按列数合并）→ 表头行 →
/// 逐行数据。仅含空白的 <see cref="ExcelSheetSpec.Title"/> 视为未填，不写标题行也不抛——标题是可选项，
/// 这一口径与 <see cref="ExcelSheetSpec.SheetName"/> 不同（表名空白由该属性在 <c>init</c> 直接抛）。
/// 列的写出顺序就是 <see cref="ExcelSheetSpec.Columns"/> 给出的顺序，写出侧不按
/// <see cref="ExcelColumn.Order"/> 再排一次——构建器已把排定后的位置序号回写进 <c>Order</c>，
/// 那里读到的不是调用方的声明值。
/// </para>
/// <para>
/// 行集合的元素按 <see cref="ExcelSheetSpec.RowType"/> 逐笔校验：声明本身为 <c>null</c> 属于「非法声明」，
/// 在写出第一格之前的预检里就抛，一个行元素都不取；类型比对落在每一行上，任何一笔不符即抛，
/// 不为一次类型检查而物化整份行集合。只判第一笔时，后面那笔异型行经列的取值方法只会交回 <c>null</c>，
/// 放行就是写出一份表头齐全、数据全空的档。这条判据与流式、文字档两条写出路径共用一份，不各写一遍。
/// </para>
/// <para>
/// <see cref="ExcelSheetSpec.SheetName"/> 的可用性在写第一格之前判：长度不超过 31（按 UTF-16 代码单元，与工作簿同一量纲，
/// 代理对算两个）、不含工作簿不接受的字符、不以单引号开头或结尾；多表路径还要求名字互不重复，判重用
/// <see cref="StringComparer.OrdinalIgnoreCase"/>，与 Excel 和 <see cref="ClosedXML.Excel.IXLWorksheets"/> 的口径一致。
/// 这套判据逐条对齐工作簿的实际约束：既不误杀它肯收的名字，也不放过它拒绝的名字，且不替调用方改名——
/// 被拒的名字一律抛出，不做去空格、截断或加后缀这类静默兜底。
/// </para>
/// <para>
/// <see cref="ExcelColumn.Width"/> 是工作簿显示宽度：<c>null</c> 走自适应列宽，其余取值必须是大于 0 且不高于
/// 255 的有限数（超过上限会被工作簿夹成约 254.29，与负数、无穷同一口径：抛）。
/// xlsx 路径不读 <see cref="ExcelColumn.FixedWidth"/>、<see cref="ExcelColumn.PadChar"/> 与
/// <see cref="ExcelColumn.Padding"/>——那三项是文字档的字节宽度语义。
/// </para>
/// <para>
/// 自适应列宽按前若干行取样：取样范围从表头行起，到「表头行 + min(实际写出的行数,
/// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/>）」止。行集合是惰性序列，写出侧只枚举一遍，
/// 取样读的是已经落进工作表的单元格，不再回头枚举行集合。标题行不参与取样（它跨列合并，
/// 纳入会让每一列都按整条标题的宽度膨胀）。
/// </para>
/// <para>
/// <see cref="ExcelColumn.NumberFormat"/> 是 Excel 的数字/日期格式串，只在工作簿路径生效：数值格写进
/// <see cref="IXLStyle.NumberFormat"/>，日期格写进 <see cref="IXLStyle.DateFormat"/>（ClosedXML 里两者指向同一份
/// 数字格式）；文本、空白、布尔与时长格不套格式。格式串原样交给 ClosedXML，写出侧不解析也不改写它。
/// </para>
/// <para>
/// 值域超出工作簿可表示范围的输入一律抛出而不是改写：<c>DateTime</c> 早于 1899-12-30（xlsx 的 1900 日期系统起点）
/// 会被工作簿夹到纪元时刻、静默变成另一个日期；<c>double</c> 与 <c>float</c> 的 <c>NaN</c>、<c>±∞</c> 在数值格里
/// 没有对应形态；<see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、<see cref="double"/>、
/// <see cref="float"/> 的有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位时，
/// 落进数值格的会是工作簿舍短后的另一个数（16 位整数落档即回读成 15 位那个数），本类不承诺这一格交回呼叫端给的值；
/// 字串长过 32767 个字符时工作簿装不下它。
/// 四类都在该格抛 <see cref="InvalidOperationException"/> 并点名行位置、表头与列键，不改写成文本、不夹到边界值、
/// 也不截断。颜色串必须是 <c>#RGB</c> 或 <c>#RRGGBB</c>，<c>null</c> 才表示未设置——空串与非法串不会被当成「没填」。
/// 形状过关但工作簿仍解析不了的串（全形数字、阿拉伯-印度数字之类非 ASCII 位值）同样由本类转译成框架异常，
/// 库的 <see cref="FormatException"/> 只作内部异常保留。这四条取值域判据与表名判据由两条 xlsx 写出路径共用一份，
/// 这部分能导与不能导的输入集合与走哪条无关。<b>唯一的例外是日期格的下限</b>：<c>DateOnly</c> 与
/// <c>DateTimeOffset</c> 在本类落文本格、不送进日期格，因此本类不判它们早于 1899-12-30 的情形（照能导出），
/// 而流式路径把它们落成日期格、整段拒——同一份带这类日期的规格走哪条路径，结果并不相同，
/// 详见流式写出器的说明。这句承诺的范围是「本类显式检查过的失败面」：
/// 取值域、颜色解析、表名判据与单张工作表的行数上限（<see cref="ExcelConstants.MaxSheetRows"/>，含标题行与表头行）
/// 在内，工作簿自身的存盘失败（流不可写、容器损坏、磁盘满）不在内，那类按库的异常形态交回。
/// </para>
/// <para>
/// 本类按 <see cref="ClosedXML.Excel.XLCellValue"/> 自己的口径落格，落进哪一类格子由取值的运行期型别决定：
/// <c>DateTime</c> 落日期格，<c>decimal</c>／<c>double</c>／<c>float</c> 等落数值格，<c>bool</c> 落布尔格，
/// <c>TimeSpan</c> 落时长格，<c>null</c> 落空格；<c>DateOnly</c>、<c>DateTimeOffset</c> 与 <c>Guid</c> 这类
/// 工作簿没有对应格位的型别落文本格，读出的是它的文本形式而不是日期格。这与流式路径对同一些型别的落格
/// 可以不同（例如 <c>DateOnly</c> 在流式路径落日期格），两条路径只承诺列顺序与表头文案一致，不承诺格位型别一致。
/// <c>DateTime</c> 按它的<u>钟表时刻</u>落格：本类不读 <see cref="DateTime.Kind"/>、不做时区换算，
/// <c>Utc</c>／<c>Local</c> 的实例都照它显示的年月日时分秒落进日期格，读回来是
/// <see cref="DateTimeKind.Unspecified"/>（xlsx 的日期格本身只是一个带格式的数，没有容纳时区的地方）；
/// 要按某个时区交代同一个瞬间，由呼叫端先换算再交值。
/// 数值这一项两边各按自己的形式落档：本类把取值交给 <see cref="ClosedXML.Excel.XLCellValue"/>，
/// 流式路径把取值的文本交给写出库，落进档里的写法可以不同（同一份 15 位整数，一边写成 <c>1E+15</c> 这样的形式，
/// 一边写成整串数字）。能承诺的是读回的那个数：<see cref="ExcelConstants.MaxExactNumericSignificantDigits"/>
/// 位有效数字以内两边都原样交回（按本组件的导入器读回实测一致），超出该位数的取值两边一起拒，
/// 因此「同一份规格走哪条路径就得到哪个数」这件事不需要由调用方去猜。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类只写入，绝不对传入流调用 <c>Dispose</c>，
/// 存盘后流的位置停在末尾，调用方把位置回到 0 即可读回。整份档（单表是一张，多表是清单里全部）先在内存里建好再落盘，
/// 落盘只有 <c>SaveAs</c> 这一次：取消若在落盘之前被观察到（入口、逐行、每张表写完、存盘之前四处之一），
/// 抛出的那一刻输出流是空的；只有存盘之后才被观察到的取消会留下一份完整的档，而交出的是异常、不是成功结果。
/// 本类不承诺失败原子性，两种差别按取消被观察到的时机区分，不合并成一句保证。
/// </para>
/// <para>
/// 写出全程同步：ClosedXML 没有异步面，写出侧不伪装 <c>async</c>、不起线程池任务；取消令牌在入口、逐行、
/// 每张表写完、存盘之前与回传结果之前各检查一次，最后一笔取值期间的取消由存盘之前那一次拦下。
/// 不写 <c>.xls</c>，也不承诺 <c>.xlsm</c> 宏、数据透视表与图表。
/// </para>
/// </remarks>
/// <param name="options">Excel 选项，本导出器读取其中的 <see cref="XiHanExcelOptions.AutoWidthSampleRows"/>。</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <c>null</c></exception>
public sealed class ClosedXmlExporter(XiHanExcelOptions options)
{
    /// <summary>
    /// 表头行与数据行区块使用的边框线型
    /// </summary>
    private const XLBorderStyleValues TableBorder = XLBorderStyleValues.Thin;

    /// <summary>
    /// xlsx 的列宽上限（按字符数计），超过它的取值会被工作簿夹成约 254.29 而不是报错
    /// </summary>
    private const double MaximumColumnWidth = 255.0;

    private readonly XiHanExcelOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// 本导出器生效的单张工作表行数上限（含标题行与表头行），公开入口恒为 <see cref="ExcelConstants.MaxSheetRows"/>
    /// </summary>
    private readonly int _maxSheetRows = ExcelConstants.MaxSheetRows;

    /// <summary>
    /// 构造一个把单张工作表行数上限注入成 <paramref name="maxSheetRows"/> 的导出器，只供本组件的边界测试使用
    /// </summary>
    /// <param name="options">Excel 选项，语义与公开构造函数相同</param>
    /// <param name="maxSheetRows">单张工作表的行数上限，含标题行与表头行，至少 1</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSheetRows"/> 小于 1</exception>
    /// <remarks>
    /// 真实上限是一百多万行，逐行写到触线要产出十几 MB 的档并把它整份建在内存里，因此边界断言改在同一个判定上
    /// 取一个小上限。判定只有 <see cref="_maxSheetRows"/> 这一处读点，注入与不注入走的是同一段代码；
    /// 正式入口一律走公开构造函数，读到的就是那个常数。
    /// </remarks>
    internal ClosedXmlExporter(XiHanExcelOptions options, int maxSheetRows)
        : this(options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSheetRows, 1);

        _maxSheetRows = maxSheetRows;
    }

    /// <summary>
    /// 本导出器生效的单张工作表行数上限，供接线断言确认公开入口读的就是 <see cref="ExcelConstants.MaxSheetRows"/>
    /// </summary>
    internal int MaxSheetRows => _maxSheetRows;

    /// <summary>
    /// 把一张表写成 xlsx 工作簿
    /// </summary>
    /// <param name="output">输出流，导出器只写入不关闭，由调用方拥有</param>
    /// <param name="sheet">表规格，列清单的顺序即写出顺序</param>
    /// <param name="cancellationToken">取消令牌，取消时不再写出后续行</param>
    /// <returns>导出结果，格式为 <see cref="ExcelFormat.Xlsx"/>，<see cref="ExcelExportResult.StylingApplied"/> 为 <c>true</c></returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为 <c>null</c>，
    /// 或 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>；
    /// 该属性是 <c>required</c> 非空成员，null 只会来自 <c>null!</c> 的非法声明，并在写出第一格之前就被拒）</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255
    /// 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内，或
    /// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/> 为负数</exception>
    /// <exception cref="ArgumentException"><see cref="ExcelSheetSpec.SheetName"/> 超过 31 个字符、含工作簿不接受的字符
    /// （<c>: \ / ? * [ ]</c> 与控制字符 <c>U+0000</c>、<c>U+0003</c>）或以单引号开头／结尾，此时
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>；或 <see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的
    /// 十六进制颜色串，此时 <see cref="ArgumentException.ParamName"/> 为 <c>HeaderFill</c>。颜色串里只有「形状合法但工作簿
    /// 解析不了（位值含非 ASCII 字符）」那一条带库的 <see cref="FormatException"/> 作为内部异常，形状本身不合法的那条
    /// 没有内部异常——按异常类型与 <see cref="ArgumentException.ParamName"/> 分流，不要靠读内部异常判断成因</exception>
    /// <exception cref="InvalidOperationException">行集合里有某笔元素与
    /// <see cref="ExcelSheetSpec.RowType"/> 不符；某个行值是工作簿装不下的（早于 1899-12-30 的 <c>DateTime</c>、
    /// <c>NaN</c> 或 <c>±∞</c>、有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>／<c>double</c>／<c>float</c>、长过单元格上限的字串）；
    /// 某列的 <see cref="ExcelColumn.CellStyle"/> 交回非法颜色串（含形状合法但解析不了的串）；
    /// 或标题行、表头行与数据行加起来要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后——单张工作表
    /// 只有这么多行，超出的行没有可落的位置，上限按每张工作表各自计、不做整簿累计，消息点出上限值、
    /// 触线的那一行与「分成多张表或改用文字档」两条出路。各类消息都点名行位置与实际成因：行型不符者报出行号与
    /// 期望／实际两个类型全名，取值与颜色两类报出行号、表头与列键，解析不了的那类把库的
    /// <see cref="FormatException"/> 保留为内部异常</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// 值域检查（表名、列宽、对齐、表头底色、取样上限、<see cref="ExcelSheetSpec.RowType"/> 声明）全部排在写入第一格之前，
    /// 非法输入不会留下半份文件。行集合按惰性枚举，取到一行才写一行，逐行检查取消令牌与工作表行数上限；
    /// 存盘之前与回传结果之前各再查一次取消——前者抛出时输出流仍是零字节，后者抛出时整份档已经落盘，
    /// 但不会交出成功结果。行数上限的判定也在逐行那一趟里，抛出时整份档尚未存盘，输出流同样是零字节。
    /// </remarks>
    public Task<ExcelExportResult> ExportAsync(
        Stream output,
        ExcelSheetSpec sheet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheet);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateSampleRows();
        ExcelWorkbookWriteGuard.ValidateSheetName(sheet.SheetName, 1);

        using var workbook = new XLWorkbook();
        WriteSheet(workbook, sheet, cancellationToken);

        // 落盘之前查一次：取消落在最后一笔的取值期间时，逐行检查已经没有下一轮可拦
        cancellationToken.ThrowIfCancellationRequested();

        // SaveAs 不关闭传入流，写完停在末尾
        workbook.SaveAs(output);

        // 回传结果之前再查一次：只在这里被观察到的取消，档已落盘但不会交出成功结果
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ExcelExportResult.Styled(
            ExcelFormat.Xlsx,
            ExcelConstants.ExtensionXlsx,
            ExcelConstants.XlsxContentType));
    }

    /// <summary>
    /// 把多张表写成同一个 xlsx 工作簿
    /// </summary>
    /// <param name="output">输出流，导出器只写入不关闭，由调用方拥有</param>
    /// <param name="sheets">表清单，顺序就是工作表的落位顺序，至少一张</param>
    /// <param name="cancellationToken">取消令牌，取消时不再写出后续行与后续的表</param>
    /// <returns>导出结果，格式为 <see cref="ExcelFormat.Xlsx"/>，<see cref="ExcelExportResult.StylingApplied"/> 为 <c>true</c></returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheets"/> 为 <c>null</c>，
    /// <paramref name="sheets"/> 里有 <c>null</c> 项（<see cref="ArgumentException.ParamName"/> 为 <c>sheets</c>，
    /// 消息点名是第几项），或清单里某张表的 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>
    /// （<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>）</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> 为空清单（工作簿至少要有一张表，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>sheets</c>），或某张表的 <see cref="ExcelSheetSpec.SheetName"/>
    /// 不可用、或与清单里更早那张重名（判重不区分大小写），
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>；或某张表的
    /// <see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的十六进制颜色串。空清单与表名两类都在建工作簿之前抛出，
    /// 底色一类排在写第一格之前</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255
    /// 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内，或
    /// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/> 为负数</exception>
    /// <exception cref="InvalidOperationException">某张表的行集合里有某笔元素与其
    /// <see cref="ExcelSheetSpec.RowType"/> 不符；某个行值是工作簿装不下的（早于 1899-12-30 的 <c>DateTime</c>、
    /// <c>NaN</c> 或 <c>±∞</c>、有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>／<c>double</c>／<c>float</c>、长过单元格上限的字串）；
    /// 某列的 <see cref="ExcelColumn.CellStyle"/> 交回非法颜色串；或某张表的标题行、表头行与数据行加起来
    /// 要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后。行数上限按<u>每张工作表各自</u>计——
    /// 每张表都有自己的标题行与表头行，各自数各自的，不做整簿累计，因此两张各占上限六成的表能同时写进一个工作簿，
    /// 而任何一张触线就整个请求被拒，消息点出触线那张表的表名、上限值与「分成多张表或改用文字档」两条出路</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 每张表走的写出路径与 <see cref="ExportAsync"/> 完全相同（同一个逐表方法），列宽、对齐、底色、行型这些守卫
    /// 只有那一份，本方法不重写任何一条。本方法额外只做两件清单级的事：先确认清单非空，再确认表名可用且互不重名。
    /// </para>
    /// <para>
    /// 整份档先在内存里建好再落盘，所以任何一张表失败（包括排在后面的表名非法、行型不符、令牌取消）都不会在
    /// <paramref name="output"/> 里留下半个字节；前面那些表已经写进内存工作簿的部分随异常一起被丢弃。
    /// 每张表写完之后查一次取消，因此某张表末尾才发生的取消不会让下一张表开始枚举行集合。
    /// </para>
    /// <para>
    /// 取消落在存盘之后被观察到时，<paramref name="output"/> 里已经是一份完整的档，本方法交出异常而不是成功结果；
    /// 这与「落盘之前抛出的失败零字节」是两种时机，写出侧不承诺失败原子性。
    /// </para>
    /// </remarks>
    public Task<ExcelExportResult> ExportAllAsync(
        Stream output,
        IReadOnlyList<ExcelSheetSpec> sheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheets);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateSampleRows();

        if (sheets.Count == 0)
        {
            throw new ArgumentException(
                $"{nameof(sheets)} 为空：xlsx 工作簿至少要有一张表。" +
                "交出空清单不会得到一份空档，只会得到工作簿「至少要有一张表」那句英文异常，" +
                "没有表可写就是没有可交出的档，请自己判掉这一类输入。",
                nameof(sheets));
        }

        ExcelWorkbookWriteGuard.ValidateSheetNames(sheets);

        using var workbook = new XLWorkbook();

        foreach (var sheet in sheets)
        {
            // 逐表写出复用单表路径的全部守卫，不在这里再判一遍
            WriteSheet(workbook, sheet, cancellationToken);

            // 每张表写完之后查一次：取消落在某张表最后一笔的取值期间时，下一张表连行集合都不该被枚举；
            // 清单写完这一次就是落盘之前的最后一次
            cancellationToken.ThrowIfCancellationRequested();
        }

        // SaveAs 不关闭传入流，写完停在末尾
        workbook.SaveAs(output);

        // 回传结果之前再查一次：只在这里被观察到的取消，档已落盘但不会交出成功结果
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ExcelExportResult.Styled(
            ExcelFormat.Xlsx,
            ExcelConstants.ExtensionXlsx,
            ExcelConstants.XlsxContentType));
    }

    /// <summary>
    /// 往工作簿里写一张表：标题行、表头行、数据行，再落列宽、边框、冻结与筛选
    /// </summary>
    /// <param name="workbook">目标工作簿</param>
    /// <param name="sheet">表规格</param>
    /// <param name="cancellationToken">取消令牌，逐行检查</param>
    /// <returns>写入完成的工作表</returns>
    /// <remarks>
    /// 单表与多表共用这一个方法，所以这里的预检（<see cref="ExcelSheetSpec.RowType"/> 声明、列值域、表头底色）
    /// 对两条路径同时生效；表名的可用性由两个入口在建工作簿之前判，不在这里判第二次。
    /// </remarks>
    private IXLWorksheet WriteSheet(IXLWorkbook workbook, ExcelSheetSpec sheet, CancellationToken cancellationToken)
    {
        var columns = sheet.Columns;
        var rowType = ExcelRowTypeGuard.ValidateDeclaration(sheet);
        ValidateColumns(columns);
        var headerFill = sheet.HeaderFill is null ? null : ParseSheetColor(sheet.HeaderFill, nameof(ExcelSheetSpec.HeaderFill));
        var title = string.IsNullOrWhiteSpace(sheet.Title) ? null : sheet.Title;

        // 标题行占第一行时，表头与数据区整体下移一行
        var headerRowNumber = title is null ? 1 : 2;
        var worksheet = workbook.Worksheets.Add(sheet.SheetName);

        if (title is not null)
        {
            WriteTitleRow(worksheet, title, columns.Count);
        }

        WriteHeaderRow(worksheet, headerRowNumber, columns, sheet.HeaderBold, headerFill);

        var rowIndex = 0;

        foreach (var row in sheet.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowIndex++;

            // 行上限判定就在这一趟循环里：用的正是刚数出来的行号，不为计数把行集合物化、也不回头再枚举一遍
            var rowNumber = headerRowNumber + rowIndex;

            if (rowNumber > _maxSheetRows)
            {
                throw CreateRowLimitFailure(sheet.SheetName, rowIndex, rowNumber);
            }

            // 每一行都判，判据与文字档路径同一份；用的就是刚取到的这一行，不物化行集合
            ExcelRowTypeGuard.ValidateRow(rowType, row, rowIndex, "xlsx 导出");

            WriteDataRow(worksheet, rowNumber, columns, row, rowIndex);
        }

        ApplyColumnWidths(worksheet, columns, headerRowNumber, Math.Min(rowIndex, _options.AutoWidthSampleRows));
        ApplyTableLayout(worksheet, sheet, columns.Count, headerRowNumber, headerRowNumber + rowIndex);

        return worksheet;
    }

    /// <summary>
    /// 在表头上方写标题行并合并前 N 列
    /// </summary>
    /// <remarks>
    /// 只有一列时不做合并：一格已经占满整行，1×1 的合并范围在工作簿里没有对应含义。
    /// 合并后只有左上角有值，其余格读到空——这是 xlsx 自身的语义，写出侧不补值。
    /// </remarks>
    private static void WriteTitleRow(IXLWorksheet worksheet, string title, int columnCount)
    {
        worksheet.Cell(1, 1).Value = title;

        if (columnCount > 1)
        {
            worksheet.Range(1, 1, 1, columnCount).Merge();
        }
    }

    /// <summary>
    /// 逐列写表头文案并套用表头加粗与底色
    /// </summary>
    private static void WriteHeaderRow(
        IXLWorksheet worksheet,
        int headerRowNumber,
        IReadOnlyList<ExcelColumn> columns,
        bool headerBold,
        XLColor? headerFill)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            var cell = worksheet.Cell(headerRowNumber, index + 1);

            cell.Value = columns[index].Header;
            cell.Style.Font.Bold = headerBold;

            if (headerFill is not null)
            {
                cell.Style.Fill.PatternType = XLFillPatternValues.Solid;
                cell.Style.Fill.BackgroundColor = headerFill;
            }
        }
    }

    /// <summary>
    /// 写一行数据：取值、落格式、套对齐与换行、求逐格样式
    /// </summary>
    /// <param name="worksheet">目标工作表</param>
    /// <param name="rowNumber">该行在工作表里的行号</param>
    /// <param name="columns">列清单</param>
    /// <param name="row">行对象，交给列的取值方法解释</param>
    /// <param name="position">数据行的序号，用于异常信息</param>
    private static void WriteDataRow(
        IXLWorksheet worksheet,
        int rowNumber,
        IReadOnlyList<ExcelColumn> columns,
        object? row,
        int position)
    {
        var positionLabel = $"第 {position} 行";

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var cell = worksheet.Cell(rowNumber, index + 1);
            var value = column.GetValue(row);

            if (value is not null)
            {
                WriteCellValue(cell, column, value, positionLabel);
            }

            if (column.Alignment != ExcelAlignment.Auto)
            {
                cell.Style.Alignment.Horizontal = MapAlignment(column.Alignment, column);
            }

            cell.Style.Alignment.WrapText = column.Wrap;

            var style = column.CellStyle?.Invoke(row);

            if (style is not null)
            {
                ApplyCellStyle(cell, column, style, positionLabel);
            }
        }
    }

    /// <summary>
    /// 写一格取值并按取值类型套 Excel 格式串
    /// </summary>
    /// <remarks>
    /// 取值先过一道可写性判定：早于 1899-12-30 的 <c>DateTime</c>、非有限的浮点数、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值与长过单元格上限的字串都会被拒，
    /// 工作簿对这四类要么夹改成另一个值、要么写出读不回的档。判定与流式路径共用同一份，两条路径判得一样。
    /// 取值类型决定格式串落到哪一处——数值走 <see cref="IXLStyle.NumberFormat"/>，
    /// 日期走 <see cref="IXLStyle.DateFormat"/>，其余类型套了也不改变读出值，因此不套。
    /// </remarks>
    private static void WriteCellValue(IXLCell cell, ExcelColumn column, object value, string position)
    {
        ExcelWorkbookWriteGuard.EnsureWritable(value, column, position);

        var cellValue = XLCellValue.FromObject(value, CultureInfo.InvariantCulture);

        cell.Value = cellValue;

        if (column.NumberFormat is { } numberFormat)
        {
            switch (cellValue.Type)
            {
                case XLDataType.Number:
                    cell.Style.NumberFormat.Format = numberFormat;
                    break;
                case XLDataType.DateTime:
                    cell.Style.DateFormat.Format = numberFormat;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 套一格的条件样式，<c>null</c> 的颜色项保持工作簿原样
    /// </summary>
    private static void ApplyCellStyle(IXLCell cell, ExcelColumn column, ExcelTextStyle style, string position)
    {
        cell.Style.Font.Bold = style.Bold;

        if (style.FontColor is not null)
        {
            cell.Style.Font.FontColor = ParseCellColor(style.FontColor, column, position, nameof(ExcelTextStyle.FontColor));
        }

        if (style.Fill is not null)
        {
            var fill = ParseCellColor(style.Fill, column, position, nameof(ExcelTextStyle.Fill));

            cell.Style.Fill.PatternType = XLFillPatternValues.Solid;
            cell.Style.Fill.BackgroundColor = fill;
        }
    }

    /// <summary>
    /// 逐列落宽度：给了 <see cref="ExcelColumn.Width"/> 的走固定宽度，未给的按取样行自适应
    /// </summary>
    /// <param name="worksheet">目标工作表</param>
    /// <param name="columns">列清单</param>
    /// <param name="headerRowNumber">表头行号，也是取样的起始行</param>
    /// <param name="sampleRows">参与自适应取样的数据行数，已按上限限量</param>
    private static void ApplyColumnWidths(
        IXLWorksheet worksheet,
        IReadOnlyList<ExcelColumn> columns,
        int headerRowNumber,
        int sampleRows)
    {
        var sampleEndRowNumber = headerRowNumber + sampleRows;

        for (var index = 0; index < columns.Count; index++)
        {
            var column = worksheet.Column(index + 1);

            if (columns[index].Width is { } width)
            {
                column.Width = width;
            }
            else
            {
                column.AdjustToContents(headerRowNumber, sampleEndRowNumber);
            }
        }
    }

    /// <summary>
    /// 落边框、冻结与筛选：作用区块从表头行起，到最后一行数据止
    /// </summary>
    /// <remarks>
    /// 零列时整段跳过：没有列就没有可框住的区块，边框与筛选的范围也无从给出。标题行不带边框，
    /// 它是区块上方的一条标题带；冻结行数等于标题行加表头行，数据区始终在冻结线之下。
    /// </remarks>
    private static void ApplyTableLayout(
        IXLWorksheet worksheet,
        ExcelSheetSpec sheet,
        int columnCount,
        int headerRowNumber,
        int lastRowNumber)
    {
        if (columnCount == 0)
        {
            return;
        }

        if (sheet.Borders)
        {
            var table = worksheet.Range(headerRowNumber, 1, lastRowNumber, columnCount);

            table.Style.Border.SetInsideBorder(TableBorder);
            table.Style.Border.SetOutsideBorder(TableBorder);
        }

        if (sheet.FreezeHeader)
        {
            worksheet.SheetView.FreezeRows(headerRowNumber);
        }

        if (sheet.AutoFilter)
        {
            worksheet.Range(headerRowNumber, 1, lastRowNumber, columnCount).SetAutoFilter();
        }
    }

    /// <summary>
    /// 在写入第一格之前检查列级值域：列宽与对齐
    /// </summary>
    private static void ValidateColumns(IReadOnlyList<ExcelColumn> columns)
    {
        foreach (var column in columns)
        {
            if (column.Width is { } width && (width <= 0 || !double.IsFinite(width) || width > MaximumColumnWidth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ExcelColumn.Width),
                    width,
                    $"列「{column.Header}」（键 {column.Key}）的列宽 {width} 非法：xlsx 的列宽必须是大于 0 且不高于 " +
                    $"{MaximumColumnWidth.ToString(CultureInfo.InvariantCulture)} 的有限数，" +
                    $"超出上限会被工作簿静默夹成约 254.29；自适应列宽请把 {nameof(ExcelColumn.Width)} 置为 null（0 与负数不是「未指定」的另一种写法）。");
            }

            if (!Enum.IsDefined(column.Alignment))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ExcelColumn.Alignment),
                    column.Alignment,
                    $"列「{column.Header}」（键 {column.Key}）的对齐「{column.Alignment}」不在 {nameof(ExcelAlignment)} 的定义范围内。");
            }
        }
    }

    /// <summary>
    /// 造出行数超过单张工作表上限时的失败，消息点出上限值、触线的那一行与两条出路
    /// </summary>
    /// <param name="sheetName">触线的工作表名</param>
    /// <param name="position">触线那一行在数据行里的序号</param>
    /// <param name="rowNumber">该行要落进工作表的行号，已含标题行与表头行</param>
    /// <remarks>
    /// 消息是政策陈述：只说本组件按 <see cref="ExcelConstants.MaxSheetRows"/> 拒写、上限按每张工作表各自计，
    /// 不描述工作簿在这一步会做什么。判定落在逐行循环内，因此抛出时行集合可能已经被枚举到触线那一行为止，
    /// 但整份档尚未存盘，输出流仍是零字节。
    /// </remarks>
    private InvalidOperationException CreateRowLimitFailure(string sheetName, int position, int rowNumber)
        => new(
            $"xlsx 导出无法完成：工作表「{sheetName}」的第 {position} 行数据要落到第 {rowNumber} 行，" +
            $"超过单张工作表的上限 {_maxSheetRows} 行（标题行与表头行都算在内）。" +
            "这道上限按每张工作表各自计，不做整簿累计；超出的行在 xlsx 里没有可落的位置，" +
            "请把数据分成多张表，或改用不带这道行数上限的文字档（CSV／定宽）。");

    /// <summary>
    /// 检查自适应列宽的取样上限
    /// </summary>
    private void ValidateSampleRows()
    {
        if (_options.AutoWidthSampleRows < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(XiHanExcelOptions.AutoWidthSampleRows),
                _options.AutoWidthSampleRows,
                $"{nameof(XiHanExcelOptions.AutoWidthSampleRows)} 不能为负数：0 表示自适应列宽只看表头行。");
        }
    }

    /// <summary>
    /// 把表级颜色串解析成工作簿颜色，非法串直接抛
    /// </summary>
    /// <remarks>
    /// 两道判定：形状先由 <see cref="ValidateHelper.IsHexColor(string)"/> 把关（其字符判定是 Unicode 感知的
    /// <c>char.IsDigit</c>，全形数字与阿拉伯-印度数字也算形状合法），再由 <see cref="XLColor.FromHtml(string)"/> 解析——
    /// 它只认 ASCII 位。库抛的 <see cref="FormatException"/> 在这里转成框架的 <see cref="ArgumentException"/>
    /// 并保留内部异常，对外只暴露文档化过的失败面。
    /// </remarks>
    private static XLColor ParseSheetColor(string color, string memberName)
    {
        if (!ValidateHelper.IsHexColor(color))
        {
            throw new ArgumentException(
                $"{memberName}「{color}」不是合法的十六进制颜色串（形如 #D9E1F2，接受 #RGB 与 #RRGGBB）；" +
                "不上底色请把该项置为 null，空串与非法串都不当成「未设置」。",
                memberName);
        }

        try
        {
            return XLColor.FromHtml(color);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"{memberName}「{color}」的形状是十六进制颜色串，但工作簿解析不了这种写法（形如 #D9E1F2，" +
                "位值只接受 ASCII 的 0-9 与 a-f）；不上底色请把该项置为 null。" +
                $"库的解析结果：{exception.Message}",
                memberName,
                exception);
        }
    }

    /// <summary>
    /// 把格级颜色串解析成工作簿颜色，非法串抛出并点名行列
    /// </summary>
    /// <remarks>
    /// 与表级同一套判定，差别只在异常形态：逐格样式的内容要取到行才知道，所以沿用格级失败体系点名行位置与列键，
    /// 库的 <see cref="FormatException"/> 作为内部异常保留。
    /// </remarks>
    private static XLColor ParseCellColor(string color, ExcelColumn column, string position, string memberName)
    {
        if (!ValidateHelper.IsHexColor(color))
        {
            throw ExcelWorkbookWriteGuard.CreateFailure(
                column,
                position,
                $"{memberName}「{color}」不是合法的十六进制颜色串（形如 #C00000，接受 #RGB 与 #RRGGBB）；" +
                "不改变该项请把对应参数置为 null。");
        }

        try
        {
            return XLColor.FromHtml(color);
        }
        catch (FormatException exception)
        {
            throw ExcelWorkbookWriteGuard.CreateFailure(
                column,
                position,
                $"{memberName}「{color}」的形状是十六进制颜色串，但工作簿解析不了这种写法" +
                $"（位值只接受 ASCII 的 0-9 与 a-f）。库的解析结果：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 把列的对齐映射成工作簿的水平对齐
    /// </summary>
    private static XLAlignmentHorizontalValues MapAlignment(ExcelAlignment alignment, ExcelColumn column)
        => alignment switch
        {
            ExcelAlignment.Left => XLAlignmentHorizontalValues.Left,
            ExcelAlignment.Center => XLAlignmentHorizontalValues.Center,
            ExcelAlignment.Right => XLAlignmentHorizontalValues.Right,
            _ => throw new ArgumentOutOfRangeException(
                nameof(alignment),
                alignment,
                $"列「{column.Header}」（键 {column.Key}）的对齐「{alignment}」没有对应的水平对齐，{nameof(ExcelAlignment.Auto)} 应由调用侧跳过。")
        };
}
