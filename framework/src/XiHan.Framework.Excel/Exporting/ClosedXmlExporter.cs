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
/// 逐行数据。仅含空白的 <see cref="ExcelSheetSpec.Title"/> 视为未填，不写标题行。
/// 列的写出顺序就是 <see cref="ExcelSheetSpec.Columns"/> 给出的顺序，不按 <see cref="ExcelColumn.Order"/> 再排。
/// </para>
/// <para>
/// 行集合的元素按 <see cref="ExcelSheetSpec.RowType"/> 逐笔校验：声明为 <c>null</c> 时在写出第一格之前抛出，
/// 任何一笔类型不符即抛出。
/// </para>
/// <para>
/// <see cref="ExcelSheetSpec.SheetName"/> 在写第一格之前校验：长度不超过 31（按 UTF-16 代码单元，代理对算两个）、
/// 不含工作簿不接受的字符、不以单引号开头或结尾；多表路径还要求名字互不重复，判重用
/// <see cref="StringComparer.OrdinalIgnoreCase"/>。不合规的名字一律抛出，不改名。
/// </para>
/// <para>
/// <see cref="ExcelColumn.Width"/> 是工作簿显示宽度：<c>null</c> 走自适应列宽，其余取值必须是大于 0 且不高于
/// 255 的有限数。xlsx 路径不读 <see cref="ExcelColumn.FixedWidth"/>、<see cref="ExcelColumn.PadChar"/> 与
/// <see cref="ExcelColumn.Padding"/>。
/// </para>
/// <para>
/// 自适应列宽从表头行起取样，到「表头行 + min(实际写出的行数,
/// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/>）」止，读的是已写入工作表的单元格，不再枚举行集合。
/// 标题行不参与取样。
/// </para>
/// <para>
/// <see cref="ExcelColumn.NumberFormat"/> 是 Excel 的数字/日期格式串：数值格写进
/// <see cref="IXLStyle.NumberFormat"/>，日期格写进 <see cref="IXLStyle.DateFormat"/>；文本、空白、布尔与时长格不套格式。
/// 格式串原样交给 ClosedXML。
/// </para>
/// <para>
/// 以下取值在该格抛 <see cref="InvalidOperationException"/> 并点名行位置、表头与列键：早于 1900-01-01 的
/// <c>DateTime</c>、<c>DateOnly</c> 与 <c>DateTimeOffset</c>；<c>double</c> 与 <c>float</c> 的 <c>NaN</c>、<c>±∞</c>；
/// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的 <see cref="long"/>、
/// <see cref="ulong"/>、<see cref="decimal"/>、<see cref="double"/>、<see cref="float"/>；绝对值超过
/// 9007199254740992（2 的 53 次方）的 <see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>；
/// 长过 32767 个字符的字串。这些取值域判据与表名判据由两条 xlsx 写出路径共用。
/// 颜色串必须是 <c>#RGB</c> 或 <c>#RRGGBB</c>，<c>null</c> 表示未设置；工作簿解析不了的串转译成框架异常，
/// 库的 <see cref="FormatException"/> 作为内部异常保留。
/// <see cref="ExcelColumn.Header"/> 与 <see cref="ExcelSheetSpec.Title"/> 长过
/// <see cref="ExcelConstants.MaxCellTextLength"/> 时在写出第一格之前抛 <see cref="ArgumentException"/>
/// （<see cref="ArgumentException.ParamName"/> 为 <c>Header</c>／<c>Title</c>），判据由 <see cref="ExcelCellTextGuard"/> 持有。
/// 单张工作表的行数上限为 <see cref="ExcelConstants.MaxSheetRows"/>（含标题行与表头行）。
/// 工作簿自身的存盘失败（流不可写、容器损坏、磁盘满）按库的异常形态交回。
/// </para>
/// <para>
/// 落格类型由取值的运行期型别决定（按 <see cref="ClosedXML.Excel.XLCellValue"/>）：
/// <c>DateTime</c> 落日期格，<c>decimal</c>／<c>double</c>／<c>float</c> 等落数值格，<c>bool</c> 落布尔格，
/// <c>TimeSpan</c> 落时长格，<c>null</c> 落空格；<c>DateOnly</c>、<c>DateTimeOffset</c> 与 <c>Guid</c> 落文本格。
/// 与流式路径只承诺列顺序与表头文案一致，不承诺格位型别一致；有效数字在
/// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位以内的数值两条路径读回相同的值。
/// <c>DateTime</c> 按钟表时刻落格，不读 <see cref="DateTime.Kind"/>、不做时区换算，读回为
/// <see cref="DateTimeKind.Unspecified"/>。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类只写入，不对传入流调用 <c>Dispose</c>，存盘后流的位置停在末尾。
/// 整份档先在内存里建好，再以 <c>SaveAs</c> 一次落盘：存盘之前观察到的取消抛出时输出流为空；
/// 存盘之后观察到的取消会留下一份完整的档并抛出异常。
/// </para>
/// <para>
/// 写出全程同步；取消令牌在入口、逐行、每张表写完、存盘之前与回传结果之前各检查一次。
/// 不写 <c>.xls</c>，也不支持 <c>.xlsm</c> 宏、数据透视表与图表。
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
    /// xlsx 的列宽上限（按字符数计）
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
    internal ClosedXmlExporter(XiHanExcelOptions options, int maxSheetRows)
        : this(options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSheetRows, 1);

        _maxSheetRows = maxSheetRows;
    }

    /// <summary>
    /// 本导出器生效的单张工作表行数上限
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
    /// 或 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>）</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255
    /// 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内，或
    /// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/> 为负数</exception>
    /// <exception cref="ArgumentException"><see cref="ExcelSheetSpec.SheetName"/> 超过 31 个字符、含工作簿不接受的字符
    /// （<c>: \ / ? * [ ]</c> 与控制字符 <c>U+0000</c>、<c>U+0003</c>）或以单引号开头／结尾，此时
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>；<see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的
    /// 十六进制颜色串，此时 <see cref="ArgumentException.ParamName"/> 为 <c>HeaderFill</c>；某列的
    /// <see cref="ExcelColumn.Header"/> 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，此时
    /// <see cref="ArgumentException.ParamName"/> 为 <c>Header</c>；或 <see cref="ExcelSheetSpec.Title"/> 长过
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，此时 <see cref="ArgumentException.ParamName"/> 为
    /// <c>Title</c>。以上均在写出第一格之前抛出，输出流为零字节；工作簿解析不了的颜色串带库的
    /// <see cref="FormatException"/> 作为内部异常</exception>
    /// <exception cref="InvalidOperationException">行集合里有某笔元素与
    /// <see cref="ExcelSheetSpec.RowType"/> 不符；某个行值是工作簿装不下的（早于 1900-01-01 的
    /// <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、<c>NaN</c> 或 <c>±∞</c>、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992
    /// （2 的 53 次方）的 <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过单元格上限的字串）；
    /// 某列的 <see cref="ExcelColumn.CellStyle"/> 交回非法颜色串；
    /// 或标题行、表头行与数据行加起来要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后（按每张工作表各自计）。
    /// 消息点名行位置与成因</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// 值域检查（表名、列宽、对齐、表头底色、取样上限、<see cref="ExcelSheetSpec.RowType"/> 声明）全部排在写入第一格之前。
    /// 行集合按惰性枚举，逐行检查取消令牌与工作表行数上限；存盘之前与回传结果之前各再查一次取消。
    /// 存盘之前抛出时输出流为零字节，回传结果之前抛出时整份档已经落盘。
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

        // 落盘之前检查取消
        cancellationToken.ThrowIfCancellationRequested();

        // SaveAs 不关闭传入流，写完停在末尾
        workbook.SaveAs(output);

        // 回传结果之前再检查一次取消，此时档已落盘
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
    /// <exception cref="ArgumentException"><paramref name="sheets"/> 为空清单（<see cref="ArgumentException.ParamName"/>
    /// 为 <c>sheets</c>），或某张表的 <see cref="ExcelSheetSpec.SheetName"/> 不可用、或与清单里更早那张重名（判重不区分大小写），
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>；某张表的
    /// <see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的十六进制颜色串；某张表某列的
    /// <see cref="ExcelColumn.Header"/> 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，此时
    /// <see cref="ArgumentException.ParamName"/> 为 <c>Header</c>；或某张表的 <see cref="ExcelSheetSpec.Title"/>
    /// 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，此时 <see cref="ArgumentException.ParamName"/>
    /// 为 <c>Title</c>。以上均在落盘之前抛出，输出流为零字节</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255
    /// 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内，或
    /// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/> 为负数</exception>
    /// <exception cref="InvalidOperationException">某张表的行集合里有某笔元素与其
    /// <see cref="ExcelSheetSpec.RowType"/> 不符；某个行值是工作簿装不下的（早于 1900-01-01 的
    /// <c>DateTime</c>／<c>DateOnly</c>／<c>DateTimeOffset</c>、<c>NaN</c> 或 <c>±∞</c>、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992
    /// （2 的 53 次方）的 <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过单元格上限的字串）；
    /// 某列的 <see cref="ExcelColumn.CellStyle"/> 交回非法颜色串；或某张表的标题行、表头行与数据行加起来
    /// 要落到第 <see cref="ExcelConstants.MaxSheetRows"/> 行以后。行数上限按每张工作表各自计，不做整簿累计，
    /// 消息点出触线那张表的表名与上限值</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 每张表走与 <see cref="ExportAsync"/> 相同的逐表写出方法；本方法额外先确认清单非空，再确认表名可用且互不重名。
    /// </para>
    /// <para>
    /// 整份档先在内存里建好再落盘，任何一张表失败时 <paramref name="output"/> 为零字节。每张表写完之后检查一次取消；
    /// 取消在存盘之后才被观察到时，<paramref name="output"/> 里已是一份完整的档，本方法抛出异常。
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
            // 逐表写出复用单表路径的全部守卫
            WriteSheet(workbook, sheet, cancellationToken);

            // 每张表写完之后检查一次取消
            cancellationToken.ThrowIfCancellationRequested();
        }

        // SaveAs 不关闭传入流，写完停在末尾
        workbook.SaveAs(output);

        // 回传结果之前再检查一次取消，此时档已落盘
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
    /// 单表与多表共用本方法。预检（<see cref="ExcelSheetSpec.RowType"/> 声明、列值域与表头长度、标题长度、表头底色）
    /// 全部排在 <c>foreach</c> 之前；表名由两个入口在建工作簿之前校验。
    /// </remarks>
    private IXLWorksheet WriteSheet(IXLWorkbook workbook, ExcelSheetSpec sheet, CancellationToken cancellationToken)
    {
        var columns = sheet.Columns;
        var rowType = ExcelRowTypeGuard.ValidateDeclaration(sheet);
        ValidateColumns(columns);
        var headerFill = sheet.HeaderFill is null ? null : ParseSheetColor(sheet.HeaderFill, nameof(ExcelSheetSpec.HeaderFill));

        // 校验标题长度，排在建工作表与枚举行集合之前
        ExcelCellTextGuard.ValidateTitle(sheet.Title);

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

            // 逐行判定工作表行数上限
            var rowNumber = headerRowNumber + rowIndex;

            if (rowNumber > _maxSheetRows)
            {
                throw CreateRowLimitFailure(sheet.SheetName, rowIndex, rowNumber);
            }

            // 逐行校验行类型
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
    /// 只有一列时不合并。合并后只有左上角单元格有值。
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
    /// 取值先经 <see cref="ExcelWorkbookWriteGuard.EnsureWritable"/> 校验。数值套 <see cref="IXLStyle.NumberFormat"/>，
    /// 日期套 <see cref="IXLStyle.DateFormat"/>，其余类型不套格式。
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
    /// 零列时整段跳过。标题行不带边框；冻结行数等于标题行加表头行。
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
    /// 在写入第一格之前检查列级值域：表头长度、列宽与对齐
    /// </summary>
    /// <remarks>
    /// 表头长度先于列宽与对齐校验，判据由 <see cref="ExcelCellTextGuard.ValidateHeader"/> 持有。
    /// </remarks>
    /// <exception cref="ArgumentException">某列的 <see cref="ExcelColumn.Header"/> 长过
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，<see cref="ArgumentException.ParamName"/> 为
    /// <c>Header</c></exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 或
    /// <see cref="ExcelColumn.Alignment"/> 不在定义范围内</exception>
    private static void ValidateColumns(IReadOnlyList<ExcelColumn> columns)
    {
        foreach (var column in columns)
        {
            ExcelCellTextGuard.ValidateHeader(column);

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
    /// 抛出时整份档尚未存盘，输出流为零字节。
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
    /// 先由 <see cref="ValidateHelper.IsHexColor(string)"/> 校验形状，再由 <see cref="XLColor.FromHtml(string)"/> 解析；
    /// 库抛出的 <see cref="FormatException"/> 转成 <see cref="ArgumentException"/> 并保留为内部异常。
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
    /// 与表级同一套判定，失败时以格级异常点名行位置与列键，库的 <see cref="FormatException"/> 作为内部异常保留。
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
