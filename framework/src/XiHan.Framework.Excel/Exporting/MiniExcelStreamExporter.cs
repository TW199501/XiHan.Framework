// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.OpenXml;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// xlsx 流式导出器，用 MiniExcel 边枚举行边写出，服务十万行级的档
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ClosedXmlExporter"/> 同产 <c>.xlsx</c>：全量路径先把整份档建在内存里再落盘，能落全部排版；
/// 本路径把行逐格交给写出器，内存不随行数增长，不承载整份排版。走哪条由分派器按
/// <see cref="ExcelSheetSpec.ForceStreaming"/> 或 <see cref="ExcelSheetSpec.ExpectedRowCount"/> 决定，本类不看这两个入参。
/// </para>
/// <para>
/// 行集合按 <see cref="ExcelSheetSpec.Rows"/> 惰性枚举一次，不物化：每行投影成一份「列键到取值」的字典交给写出器。
/// 列集来自 <see cref="ExcelSheetSpec.Columns"/>，每行交出的键完全相同。
/// </para>
/// <para>
/// 落档的只有：表头文案（按 <see cref="ExcelColumn.Header"/> 写，不按属性名）、列顺序
/// （按 <see cref="ExcelSheetSpec.Columns"/> 的给出顺序）、<see cref="ExcelColumn.NumberFormat"/>
/// 落进数字与日期格的显示格式，以及 <see cref="ExcelSheetSpec.FreezeHeader"/> 与
/// <see cref="ExcelSheetSpec.AutoFilter"/> 两个开关。格式串原样交给库。
/// </para>
/// <para>
/// 不落档的项在返回值的降级理由里逐项列出：<see cref="ExcelSheetSpec.Title"/> 的标题行与合并、
/// <see cref="ExcelSheetSpec.HeaderBold"/> 的表头加粗、<see cref="ExcelSheetSpec.HeaderFill"/> 的表头底色、
/// <see cref="ExcelSheetSpec.Borders"/> 的边框、<see cref="ExcelColumn.CellStyle"/> 的逐格条件样式、
/// <see cref="ExcelColumn.Width"/> 的列宽（含未给宽度时的自适应列宽）、<see cref="ExcelColumn.Alignment"/>
/// 与 <see cref="ExcelColumn.Wrap"/>。返回值一律是降级结果，<see cref="ExcelExportResult.StylingApplied"/> 为 <c>false</c>。
/// </para>
/// <para>
/// 表名判据与全量路径共用。早于 1900-01-01 的日期（<see cref="DateTime"/>、<see cref="DateOnly"/> 与
/// <see cref="DateTimeOffset"/> 三种型别同判）、非有限的浮点数、有效数字多于
/// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值、绝对值超过 9007199254740992
/// （2 的 53 次方）的 <see cref="long"/>／<see cref="ulong"/>／<see cref="decimal"/> 与长过单元格上限的字串一律抛出，
/// 判据与全量路径共用，成因句逐字相同。行集合元素按 <see cref="ExcelSheetSpec.RowType"/> 逐笔校验。
/// 取值委托自己抛出的异常原样上抛。
/// </para>
/// <para>
/// 表头与标题的长度在声明层校验，判据与全量路径共用 <see cref="ExcelCellTextGuard"/>，超长时抛出，不截断。
/// 标题在本路径不落档，长度合法的标题只在降级理由里点名。
/// </para>
/// <para>
/// 单张工作表的行数上限为 <see cref="ExcelConstants.MaxSheetRows"/>（含表头行），超过时抛出，判定在逐行投影中进行，
/// 按每张工作表各自计。本路径不写标题行，<see cref="ExcelSheetSpec.Title"/> 不占行数。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类只写入，不关闭也不复位流位置，写完停在末尾。取消令牌在入口、每一行投影之前
/// 与回传结果之前各检查一次。入口处抛出时输出流零字节；写出中途抛出时流里可能已有未收尾的字节；
/// 写出收尾之后抛出时流里可能已是一份完整的档。抛出后调用方必须丢弃这条流的内容。
/// </para>
/// <para>
/// 行集合为空时写出库不写表头行，交回一张空表。
/// 落格类型按写出库自己的形态：<see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/> 落日期格
/// （<c>DateTimeOffset</c> 取钟表时刻，偏移量不落格），<see cref="byte"/> 落文本格，<see cref="TimeOnly"/> 落时长格。
/// <see cref="DateTime"/> 按钟表时刻落格，不读 <see cref="DateTime.Kind"/>、不做时区换算，读回为
/// <see cref="DateTimeKind.Unspecified"/>。
/// </para>
/// <para>
/// 与全量路径只承诺列顺序与表头文案一致，不承诺格位型别一致，也不承诺数值在档里的写法；有效数字在
/// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位以内、且绝对值不超过 9007199254740992
/// 的取值两条路径读回相同的值。不写 <c>.xls</c>，也不支持宏与图表。
/// </para>
/// </remarks>
public sealed class MiniExcelStreamExporter
{
    /// <summary>
    /// 降级理由：逐项列出流式模式没有落地的排版与样式
    /// </summary>
    private const string StylingSkipReason =
        "流式模式（MiniExcel）不承载排版与样式：标题行及其合并、表头加粗、表头底色、边框、逐格条件样式、" +
        "列宽（含未指定宽度时的自适应列宽）、单元格水平对齐与自动换行全部未写出，已按请求忽略；" +
        "本档保留的只有表头文案、列顺序、数字与日期格式串，以及表头行冻结与自动筛选两个开关。";

    /// <summary>
    /// 本导出器生效的单张工作表行数上限（含表头行），公开入口恒为 <see cref="ExcelConstants.MaxSheetRows"/>
    /// </summary>
    private readonly int _maxSheetRows = ExcelConstants.MaxSheetRows;

    /// <summary>
    /// 构造一个流式导出器，单张工作表的行数上限取 <see cref="ExcelConstants.MaxSheetRows"/>
    /// </summary>
    public MiniExcelStreamExporter()
    {
    }

    /// <summary>
    /// 构造一个把单张工作表行数上限注入成 <paramref name="maxSheetRows"/> 的流式导出器，只供本组件的边界测试使用
    /// </summary>
    /// <param name="maxSheetRows">单张工作表的行数上限，含表头行，至少 1</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSheetRows"/> 小于 1</exception>
    internal MiniExcelStreamExporter(int maxSheetRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSheetRows, 1);

        _maxSheetRows = maxSheetRows;
    }

    /// <summary>
    /// 本导出器生效的单张工作表行数上限
    /// </summary>
    internal int MaxSheetRows => _maxSheetRows;

    /// <summary>
    /// 把一张表流式写成 xlsx
    /// </summary>
    /// <param name="output">输出流，本方法只写入不关闭，也不复位流位置；写出后位置停在末尾</param>
    /// <param name="sheet">表规格，列清单的顺序就是落位的列顺序</param>
    /// <param name="cancellationToken">取消令牌，入口、每一行投影之前与回传结果之前各检查一次</param>
    /// <returns>导出结果，格式为 <see cref="ExcelFormat.Xlsx"/>，<see cref="ExcelExportResult.StylingApplied"/> 为 <c>false</c>
    /// 并带逐项点名的 <see cref="ExcelExportResult.StylingSkipReason"/></returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为 <c>null</c>，
    /// 或 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>）</exception>
    /// <exception cref="ArgumentException"><see cref="ExcelSheetSpec.SheetName"/> 超过 31 个字符、含工作簿不接受的字符
    /// （<c>: \ / ? * [ ]</c> 与控制字符 <c>U+0000</c>、<c>U+0003</c>）或以单引号开头／结尾，此时
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>；某列的 <see cref="ExcelColumn.Header"/>
    /// 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，此时 <see cref="ArgumentException.ParamName"/>
    /// 为 <c>Header</c>；或 <see cref="ExcelSheetSpec.Title"/> 长过 <see cref="ExcelConstants.MaxCellTextLength"/>
    /// 个字符，此时 <see cref="ArgumentException.ParamName"/> 为 <c>Title</c>。判据与全量写出路径共用
    /// （表名走 <see cref="ExcelWorkbookWriteGuard"/>，表头与标题的长度走 <see cref="ExcelCellTextGuard"/>），
    /// 均在调用写出库之前抛出，输出流零字节</exception>
    /// <exception cref="InvalidOperationException">行集合里有某笔元素与 <see cref="ExcelSheetSpec.RowType"/> 不符；
    /// 列清单里有两列用了同一个 <see cref="ExcelColumn.Key"/>；表头行与数据行加起来要落到第
    /// <see cref="ExcelConstants.MaxSheetRows"/> 行以后（按每张工作表各自计，<see cref="ExcelSheetSpec.Title"/> 不占行数）；
    /// 或某个行值越出两条 xlsx 写出路径共用的取值域判据——早于 1900-01-01 的 <see cref="DateTime"/>、
    /// <see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/>、<c>NaN</c> 或 <c>±∞</c>、有效数字多于
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>／<c>double</c>／<c>float</c>、绝对值超过 9007199254740992
    /// （2 的 53 次方）的 <c>long</c>／<c>ulong</c>／<c>decimal</c>、长过单元格上限的字串。消息点名实际成因。
    /// 取值类与行数上限在取到那一行时才判定，抛出时前面的行可能已经落进流里，调用方必须丢弃这条流的内容；
    /// 重复列键在调用写出库之前就被拒，输出流零字节</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消在入口被观察到时
    /// 输出流零字节；其余时机流里可能已有内容，调用方必须丢弃这条流的内容</exception>
    public async Task<ExcelExportResult> ExportAsync(
        Stream output,
        ExcelSheetSpec sheet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheet);

        cancellationToken.ThrowIfCancellationRequested();

        // 声明级校验：行型声明与表名，排在投影与调用写出库之前
        var rowType = ExcelRowTypeGuard.ValidateDeclaration(sheet);
        ExcelWorkbookWriteGuard.ValidateSheetName(sheet.SheetName, 1);

        var columns = sheet.Columns;

        // 表头与标题长度的声明级校验，排在调用写出库之前
        ExcelCellTextGuard.ValidateHeaders(columns);
        ExcelCellTextGuard.ValidateTitle(sheet.Title);

        EnsureDistinctKeys(columns);

        var configuration = new OpenXmlConfiguration
        {
            // 关掉库自带的表头预设样式
            TableStyles = TableStyles.None,
            TrimColumnNames = false,
            EnableAutoWidth = false,
            AutoFilter = sheet.AutoFilter,
            FreezeRowCount = sheet.FreezeHeader ? 1 : 0,
            FreezeColumnCount = 0,
            DynamicColumns = BuildDynamicColumns(columns)
        };

        await MiniExcel.SaveAsAsync(
            output,
            ProjectRows(sheet.Rows, columns, rowType, _maxSheetRows, cancellationToken),
            printHeader: true,
            sheetName: sheet.SheetName,
            excelType: ExcelType.XLSX,
            configuration: configuration,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // 回传结果之前再检查一次取消
        cancellationToken.ThrowIfCancellationRequested();

        return ExcelExportResult.Degraded(
            ExcelFormat.Xlsx,
            ExcelConstants.ExtensionXlsx,
            ExcelConstants.XlsxContentType,
            StylingSkipReason);
    }

    /// <summary>
    /// 确认列键互不重复
    /// </summary>
    /// <param name="columns">列清单</param>
    /// <exception cref="InvalidOperationException">有列键重复，消息点名重复的键与它出现的次数</exception>
    private static void EnsureDistinctKeys(IReadOnlyList<ExcelColumn> columns)
    {
        HashSet<string>? seen = null;
        List<string>? duplicated = null;

        foreach (var column in columns)
        {
            seen ??= new HashSet<string>(StringComparer.Ordinal);

            if (!seen.Add(column.Key))
            {
                duplicated ??= [];

                if (!duplicated.Contains(column.Key, StringComparer.Ordinal))
                {
                    duplicated.Add(column.Key);
                }
            }
        }

        if (duplicated is not null)
        {
            throw new InvalidOperationException(
                "xlsx 流式导出无法完成：列键重复：" + string.Join("、", duplicated) + "。" +
                "流式导出把每行投影成按键取值的字典，两个列共用一个键时后一个会盖掉前一个，" +
                "交回的档少一栏而结果仍写着成功。请给每列唯一的 Key（要同一列写两遍就用不同的键并在取值委托里给同一个值），" +
                "或改用不带这个限制的全量导出。");
        }
    }

    /// <summary>
    /// 把列清单换成写出库的列配置：表头文案、落位序号与数字日期格式串
    /// </summary>
    /// <param name="columns">列清单，给出顺序即落位顺序</param>
    /// <returns>与列清单同序的列配置</returns>
    /// <remarks>
    /// 不设列宽；每列的落位序号逐列给出。
    /// </remarks>
    private static DynamicExcelColumn[] BuildDynamicColumns(IReadOnlyList<ExcelColumn> columns)
    {
        var dynamicColumns = new DynamicExcelColumn[columns.Count];

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];

            dynamicColumns[index] = new DynamicExcelColumn(column.Key)
            {
                Name = column.Header,
                Index = index,
                Format = column.NumberFormat
            };
        }

        return dynamicColumns;
    }

    /// <summary>
    /// 把行集合逐行投影成「列键到取值」的字典，并在同一趟里做行数上限、行型判定与取值可写性判定
    /// </summary>
    /// <param name="rows">表规格给的行集合，惰性枚举一次</param>
    /// <param name="columns">列清单，决定每行交出哪些键与键的顺序</param>
    /// <param name="rowType">声明的行类型，取自写出的第一格之前的声明级判定</param>
    /// <param name="maxSheetRows">单张工作表的行数上限，含写出库落下的那一行表头</param>
    /// <param name="cancellationToken">取消令牌，每行投影之前查一次</param>
    /// <returns>交给写出库的行序列</returns>
    /// <remarks>
    /// 每行的键集与列清单完全一致，取值走列自己的取值方法。本路径的表头占第 1 行，第 <c>n</c> 行数据落在第
    /// <c>1 + n</c> 行，按此比对行数上限。
    /// </remarks>
    /// <exception cref="InvalidOperationException">行号超过 <paramref name="maxSheetRows"/>，消息点出上限值与出路</exception>
    private static IEnumerable<IDictionary<string, object?>> ProjectRows(
        System.Collections.IEnumerable rows,
        IReadOnlyList<ExcelColumn> columns,
        Type rowType,
        int maxSheetRows,
        CancellationToken cancellationToken)
    {
        var rowIndex = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowIndex++;

            // 逐行判定工作表行数上限
            var rowNumber = 1 + rowIndex;

            if (rowNumber > maxSheetRows)
            {
                throw CreateRowLimitFailure(rowIndex, rowNumber, maxSheetRows);
            }

            // 逐行校验行类型
            ExcelRowTypeGuard.ValidateRow(rowType, row, rowIndex, "xlsx 流式导出");

            var values = new Dictionary<string, object?>(columns.Count);

            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                var value = column.GetValue(row);

                // 取值域判定只交回成因文字，行位置在抛出时才拼入
                if (value is not null && ExcelWorkbookWriteGuard.DescribeUnwritable(value) is { } reason)
                {
                    throw ExcelWorkbookWriteGuard.CreateFailure(column, $"第 {rowIndex} 行", reason);
                }

                values[column.Key] = value;
            }

            yield return values;
        }
    }

    /// <summary>
    /// 造出行数超过单张工作表上限时的失败，消息点出上限值、触线的那一行与两条出路
    /// </summary>
    /// <param name="position">触线那一行在数据行里的序号</param>
    /// <param name="rowNumber">该行要落进工作表的行号，已含表头行</param>
    /// <param name="maxSheetRows">生效的单张工作表行数上限</param>
    /// <remarks>
    /// 抛出时流里可能已经落了未收尾的字节。
    /// </remarks>
    private static InvalidOperationException CreateRowLimitFailure(int position, int rowNumber, int maxSheetRows)
        => new(
            $"xlsx 流式导出无法完成：第 {position} 行数据要落到工作表的第 {rowNumber} 行，" +
            $"超过单张工作表的上限 {maxSheetRows} 行（表头行也算在内）。" +
            "这道上限按每张工作表各自计，不做整簿累计；超出的行在 xlsx 里没有可落的位置，" +
            "请把数据分成多张表，或改用不带这道行数上限的文字档（CSV／定宽）。");
}
