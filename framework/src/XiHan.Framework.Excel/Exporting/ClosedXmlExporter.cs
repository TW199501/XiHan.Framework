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
/// xlsx 工作簿导出器，用 ClosedXML 写单张表的表头、数据、样式与排版
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
/// 行集合的元素按 <see cref="ExcelSheetSpec.RowType"/> 校验一次：取到首个非 null 元素时比对实际类型，
/// 不符即抛，不为一次类型检查而物化整份行集合；<c>RowType</c> 本身为 null 同样抛，不跳过判定。
/// 放行异型行只会交出一份表头齐全、数据全空的档。
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
/// 会被工作簿夹到纪元时刻、静默变成另一个日期，因此该格直接抛 <see cref="InvalidOperationException"/> 并点名行位置、
/// 表头与列键。颜色串必须是 <c>#RGB</c> 或 <c>#RRGGBB</c>，<c>null</c> 才表示未设置——空串与非法串不会被当成「没填」。
/// 形状过关但工作簿仍解析不了的串（全形数字、阿拉伯-印度数字之类非 ASCII 位值）同样由本类转译成框架异常，
/// 库的 <see cref="FormatException"/> 只作内部异常保留，对外不出现未声明的类型。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类只写入，绝不对传入流调用 <c>Dispose</c>，
/// 存盘后流的位置停在末尾，调用方把位置回到 0 即可读回。整张表先在内存里建好再落盘，因此输入非法时
/// 流里不会留下半个字节；取消发生在逐行检查处，抛出的那一刻工作簿尚未存盘，输出流同样是空的。
/// </para>
/// <para>
/// 写出全程同步：ClosedXML 没有异步面，写出侧不伪装 <c>async</c>、不起线程池任务，取消令牌在入口与逐行处检查。
/// 不写 <c>.xls</c>，也不承诺 <c>.xlsm</c> 宏、数据透视表与图表。
/// </para>
/// </remarks>
/// <param name="options">Excel 选项，本导出器读取其中的 <see cref="XiHanExcelOptions.AutoWidthSampleRows"/>。</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <c>null</c></exception>
public sealed class ClosedXmlExporter(XiHanExcelOptions options)
{
    /// <summary>
    /// xlsx 的 1900 日期系统能表示的最早时刻，早于它的日期会被工作簿夹到这一时刻并改变数据
    /// </summary>
    private static readonly DateTime ExcelEarliestDate = new(1899, 12, 30);

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
    /// 把一张表写成 xlsx 工作簿
    /// </summary>
    /// <param name="output">输出流，导出器只写入不关闭，由调用方拥有</param>
    /// <param name="sheet">表规格，列清单的顺序即写出顺序</param>
    /// <param name="cancellationToken">取消令牌，取消时不再写出后续行</param>
    /// <returns>导出结果，格式为 <see cref="ExcelFormat.Xlsx"/>，<see cref="ExcelExportResult.StylingApplied"/> 为 <c>true</c></returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为 <c>null</c>，
    /// 或 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>；
    /// 该属性是 <c>required</c> 非空成员，null 只会来自 <c>null!</c> 的非法声明）</exception>
    /// <exception cref="ArgumentOutOfRangeException">某列的 <see cref="ExcelColumn.Width"/> 不是大于 0 且不高于 255
    /// 的有限数、某列的 <see cref="ExcelColumn.Alignment"/> 不在定义范围内，或
    /// <see cref="XiHanExcelOptions.AutoWidthSampleRows"/> 为负数</exception>
    /// <exception cref="ArgumentException"><see cref="ExcelSheetSpec.HeaderFill"/> 不是合法的十六进制颜色串，
    /// 或形状合法但工作簿解析不了（位值含非 ASCII 字符）；<see cref="Exception.InnerException"/> 为库抛出的
    /// <see cref="FormatException"/>，<see cref="ArgumentException.ParamName"/> 为 <c>HeaderFill</c></exception>
    /// <exception cref="InvalidOperationException">行集合首个非 null 元素与
    /// <see cref="ExcelSheetSpec.RowType"/> 不符；某个行值是 xlsx 表示不了的日期；或某列的
    /// <see cref="ExcelColumn.CellStyle"/> 交回非法颜色串（含形状合法但解析不了的串）。三者消息都点名行位置与
    /// 实际成因：行型不符者报出行号与期望／实际两个类型全名，后两者报出行号、表头与列键，解析不了的那类把库的
    /// <see cref="FormatException"/> 保留为内部异常</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// 值域检查（列宽、对齐、表头底色、取样上限）全部排在写入第一格之前，非法输入不会留下半份文件。
    /// 逐行检查取消令牌；行集合按惰性枚举，取到一行才写一行。
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

        using var workbook = new XLWorkbook();
        WriteSheet(workbook, sheet, cancellationToken);

        // SaveAs 不关闭传入流，写完停在末尾
        workbook.SaveAs(output);

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
    private IXLWorksheet WriteSheet(IXLWorkbook workbook, ExcelSheetSpec sheet, CancellationToken cancellationToken)
    {
        var columns = sheet.Columns;
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
        var rowTypeChecked = false;

        foreach (var row in sheet.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowIndex++;

            // 行型一致性只判首个非 null 元素，判过就不再判，行集合不物化
            if (!rowTypeChecked && row is not null)
            {
                ValidateRowType(sheet, row, rowIndex);
                rowTypeChecked = true;
            }

            WriteDataRow(worksheet, headerRowNumber + rowIndex, columns, row, rowIndex);
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
    /// 日期早于 <see cref="ExcelEarliestDate"/> 时直接抛：工作簿会把这样的值夹到纪元时刻，交回一个日期不同、
    /// 结果却写着成功的档。取值类型决定格式串落到哪一处——数值走 <see cref="IXLStyle.NumberFormat"/>，
    /// 日期走 <see cref="IXLStyle.DateFormat"/>，其余类型套了也不改变读出值，因此不套。
    /// </remarks>
    private static void WriteCellValue(IXLCell cell, ExcelColumn column, object value, string position)
    {
        if (value is DateTime date && date < ExcelEarliestDate)
        {
            throw CreateFieldFailure(
                column,
                position,
                $"日期「{date:yyyy-MM-dd HH:mm:ss}」早于 xlsx 的 1900 日期系统能表示的最早时刻 {ExcelEarliestDate:yyyy-MM-dd}，" +
                "工作簿会把它夹到纪元时刻并静默变成另一个日期。请给出该时刻之后的日期，或让该列取成文本。");
        }

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
    /// 判定首个行元素的实际类型与表规格声明的 <see cref="ExcelSheetSpec.RowType"/> 是否一致
    /// </summary>
    /// <remarks>
    /// 只判首个非 null 元素：行集合是惰性游标，为一次类型检查而物化会破坏流式契约；null 行按列的既有契约写成空格，
    /// 本身没有类型可判。派生行类型按 <see cref="Type.IsInstanceOfType(object)"/> 放行。
    /// <see cref="ExcelSheetSpec.RowType"/> 是 <c>required</c> 非空成员，能走到 null 的只有 <c>null!</c> 这种非法状态，
    /// 因此直接抛而不是跳过判定——跳过等于替调用方把坏声明咽下。
    /// </remarks>
    private static void ValidateRowType(ExcelSheetSpec sheet, object row, int rowIndex)
    {
        var expected = sheet.RowType;

        if (expected is null)
        {
            throw new ArgumentNullException(
                nameof(ExcelSheetSpec.RowType),
                $"{nameof(ExcelSheetSpec.RowType)} 为 null：行集合的元素没有可比对的声明类型。" +
                "请用行类型初始化表规格（RowType = typeof(TRow)），null 是非法状态而不是「未填」。");
        }

        if (expected.IsInstanceOfType(row))
        {
            return;
        }

        throw new InvalidOperationException(
            $"xlsx 导出无法完成：第 {rowIndex} 行的行对象与 {nameof(ExcelSheetSpec.RowType)} 不符，" +
            $"期望「{expected.FullName}」，实际是「{row.GetType().FullName}」。" +
            "请给出该类型的行集合，或把 RowType 改为实际行类型——异型行经列的取值方法只会得到 null，" +
            "放行就是交出一份表头齐全、数据全空的档。");
    }

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
            throw CreateFieldFailure(
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
            throw CreateFieldFailure(
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

    /// <summary>
    /// 组出格级失败：外层点名行位置、表头与列键，抛出的原因留在消息尾部
    /// </summary>
    /// <param name="column">出事的列</param>
    /// <param name="position">行位置标签</param>
    /// <param name="reason">要写在消息尾部的原因</param>
    /// <param name="innerException">库抛出的原始异常，转译时原样带上，不吞掉</param>
    private static InvalidOperationException CreateFieldFailure(
        ExcelColumn column,
        string position,
        string reason,
        Exception? innerException = null)
        => new($"xlsx 导出无法完成：{position}的「{column.Header}」列（键 {column.Key}）。{reason}", innerException);
}
