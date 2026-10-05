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
/// 与 <see cref="ClosedXmlExporter"/> 的分工是规模而不是格式：两者都产 <c>.xlsx</c>，全量路径先把整份档建在内存里
/// 再落盘，因此能落全部排版；本路径把行逐格交给写出器，内存不随行数增长，代价是承载不了整份排版。
/// 走哪条由调用方在表规格里表态（<see cref="ExcelSheetSpec.ForceStreaming"/>）或给出
/// <see cref="ExcelSheetSpec.ExpectedRowCount"/> 由分派器比阈值决定，本类自己不看这两个入参。
/// </para>
/// <para>
/// 行集合按 <see cref="ExcelSheetSpec.Rows"/> 原样惰性枚举，一次走完，不物化、不回头再枚举：
/// 每行投影成一份「列键到取值」的字典交给写出器，字典只在这一行内存在。列集来自
/// <see cref="ExcelSheetSpec.Columns"/> 而不是第一行的形状，因此每行交出的键完全相同——
/// 写出器按第一行的键定列集，后面的行缺键会当场抛，多出的键会被整列丢掉，这两条都不能留给运行时碰运气。
/// </para>
/// <para>
/// 落得下来的只有四项：表头文案（按 <see cref="ExcelColumn.Header"/> 写，不按属性名）、列顺序
/// （按 <see cref="ExcelSheetSpec.Columns"/> 的给出顺序）、<see cref="ExcelColumn.NumberFormat"/>
/// 落进数字与日期格的显示格式，以及 <see cref="ExcelSheetSpec.FreezeHeader"/> 与
/// <see cref="ExcelSheetSpec.AutoFilter"/> 两个开关。后两项必须显式交出：写出库的默认值是「都开」，
/// 不传就等于替关掉开关的调用方把功能又打开。格式串同样原样交给库，写出侧不解析也不改写它。
/// </para>
/// <para>
/// 落不下来的全部在返回值的降级理由里逐项列出：<see cref="ExcelSheetSpec.Title"/> 的标题行与合并、
/// <see cref="ExcelSheetSpec.HeaderBold"/> 的表头加粗、<see cref="ExcelSheetSpec.HeaderFill"/> 的表头底色、
/// <see cref="ExcelSheetSpec.Borders"/> 的边框、<see cref="ExcelColumn.CellStyle"/> 的逐格条件样式、
/// <see cref="ExcelColumn.Width"/> 的列宽（含未给宽度时的自适应列宽）、<see cref="ExcelColumn.Alignment"/>
/// 与 <see cref="ExcelColumn.Wrap"/>。底色与列宽不是库里没有开关，而是打开它们要连着引入库自己的预设表头样式
/// 与整档缓冲的宽度计算，与本路径「不额外给出没被要求的装饰、不物化行集合」的取向相反，因此按不承载处理并如实说出来。
/// 返回值一律是降级结果，<see cref="ExcelExportResult.StylingApplied"/> 为 <c>false</c>。
/// </para>
/// <para>
/// 表名与取值的判定不走库的那一套：表名按与全量路径共用的同一份判据先拒（库会拒一部分、
/// 又把另一部分控制字符转义成另一个名字，两者都不是明确契约）；早于 1899-12-30 的 <see cref="DateTime"/>、
/// 非有限的浮点数、有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值
/// 与长过单元格上限的字串同样先拒——这四类在本路径会被库直接落进档里，交出一份读不回的档、一个被改写的日期，
/// 或一串库按自己的形式写出的数字，而全量路径对它们是抛，两条路径必须判得一样。
/// 位数这一道尤其不能各判各的：本路径把取值的文本交给库落格，多于承诺位数的数值在这里能原样落进档、
/// 在全量路径却会被舍短，同一份规格走哪条得到哪个数就成了走哪条的副产品，因此两边一起拒。行集合元素按
/// <see cref="ExcelSheetSpec.RowType"/> 逐笔校验，判据与另两条路径同一份。取值委托自己抛出的异常原样上抛，
/// 本类不吞也不改写。
/// </para>
/// <para>
/// 表头与标题的长度也在声明层先拒，判据与全量路径共用 <see cref="ExcelCellTextGuard"/> 那一份，
/// 消息逐字相同：两者落的都是单元格，与数据格共用 <see cref="ExcelConstants.MaxCellTextLength"/> 那道界，
/// 超长时写出侧不截断。此前本路径对超长表头一声不响——照样写出一份表头超过单元格上限的档、照样回报降级成功，
/// 而全量路径对同一份声明是抛的；分派器按行数替调用方选路径，于是「能不能导」成了走哪条的副产品。
/// 标题这一项要单独说清：标题行在本路径属于上面那批「落不下来」的排版项，照
/// <see cref="ExcelSheetSpec.HeaderFill"/> 的先例本可以「不承载也不报错」，但全量路径对超长标题是抛的，
/// 因此这里也抛——「本路径不承载某个选项」与「这个选项的声明非法」是两件事，前者不报错，后者两条路径一起拒。
/// 长度合法的标题仍然照旧不落档，只在降级理由里点名。
/// </para>
/// <para>
/// 单张工作表的行数也按 <see cref="ExcelConstants.MaxSheetRows"/> 先拒：表头行与数据行加起来要落到那道上限
/// 之后时抛出，不接着写出行号超过工作表可表示范围的档。判定落在逐行投影那一趟里，用的就是刚数出来的行号，
/// 不为计数把行集合物化、也不回头再枚举一遍。上限按每张工作表各自计，不做整簿累计；本路径不写标题行，
/// 因此 <see cref="ExcelSheetSpec.Title"/> 不占行数——同一份带标题的规格在全量路径要占两行（标题行加表头行），
/// 在这里只占一行，能导的行数因此不同，这是落档形态带来的差别，不是两套上限。
/// </para>
/// <para>
/// 输出流的所有权在调用方：本类只写入，不关闭也不复位流位置，写完停在末尾。取消令牌在入口、每一行投影之前
/// 与回传结果之前各检查一次。取消在入口被观察到时输出流零字节；在写出的中途被观察到时，流里可能已经落了
/// 没收尾的字节，读不回来；在写出收尾之后才被观察到时，流里可能已是一份完整的档，而交回的仍然是异常。
/// 本类不承诺失败原子性，调用方在抛出后必须丢弃这条流的内容。
/// </para>
/// <para>
/// 行集合为空时写出库连表头行都不写（它要靠第一行确定列集），交回的是一张空表；这与全量路径「表头照样落档」
/// 不同，分派器给的预期行数低于阈值时本来就走全量，真要一张带表头的空档就别强制流式。
/// 值类型按写出库自己的形态落格，与全量路径可以不同：<see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/>
/// 在本路径落日期格（<c>DateTimeOffset</c> 取它的钟表时刻，偏移量不落格），在全量路径落文本格；
/// <see cref="byte"/> 相反，在本路径落文本格、在全量路径落数值格；<see cref="TimeOnly"/> 在本路径落时长格。
/// 这类同值异格不改变读回的值，因此不因格位不同而拒写。
/// <see cref="DateTime"/> 按它的<u>钟表时刻</u>落格，与全量路径同一副样子：本类不读
/// <see cref="DateTime.Kind"/>、不做时区换算，<c>Utc</c>／<c>Local</c> 的实例都照它显示的年月日时分秒落进
/// 日期格，读回来是 <see cref="DateTimeKind.Unspecified"/>；要按某个时区交代同一个瞬间，由呼叫端先换算再交值。
/// </para>
/// <para>
/// 日期格的下限是<b>本路径专属</b>的一道判定：本路径把 <see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/>
/// 落成日期格，而 1900 日期系统里早于 <c>1899-12-30</c> 的日期在这格里没有对应的计数，所以早于那一刻的
/// 这两个型别在本路径整段拒写（<see cref="DateTime"/> 两条路径都拒，判据同一把尺）。写出库对这一段的取值
/// 并不都报错，有些原样读得回来——那是库的宽容、不是格式的承诺，本组件不按取值远近赌哪一段安全。
/// 全量路径把这两个型别交给工作簿落成文本格、值与偏移量都原样留在文字里，因此不判这一条——同一份规格走哪条
/// 路径，早于该时刻的 <c>DateOnly</c> 能导或不能导并不相同，这是落格方式带来的差别，不是两套标准。
/// 要保住这类日期就用全量路径，或让该列取成文本。
/// 本类只承诺列顺序与表头文案一致，不承诺格位型别一致，也不承诺数值在档里写成哪一串字符：
/// 本路径按取值的文本落格，全量路径按工作簿的形式落格，同一份 15 位整数一边是整串数字、一边可能是
/// <c>1E+15</c> 这样的写法。能承诺的是读回的那个数——<see cref="ExcelConstants.MaxExactNumericSignificantDigits"/>
/// 位有效数字以内两条路径都原样交回，超出该位数的取值两条路径一起拒，不靠走哪条决定得到哪个数。
/// 不写 <c>.xls</c>，也不承诺宏与图表。
/// </para>
/// </remarks>
public sealed class MiniExcelStreamExporter
{
    /// <summary>
    /// 降级理由：逐项列出流式模式没有落地的排版与样式
    /// </summary>
    /// <remarks>
    /// 这条文字是对外承诺的一部分，按上一条注释说明的实际承载能力写成：写进去的每一项都真的没落，
    /// 落下来的四项（表头文案、列顺序、数字与日期格式、表头行冻结与筛选）不在里面，
    /// 免得调用方以为还有可谈的余地。
    /// </remarks>
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
    /// <remarks>
    /// 真实上限是一百多万行，逐行写到触线要产出十几 MB 的档，因此边界断言改在同一个判定上取一个小上限。
    /// 判定只有 <see cref="_maxSheetRows"/> 这一处读点，注入与不注入走的是同一段代码；
    /// 正式入口一律走公开构造函数，读到的就是那个常数。
    /// </remarks>
    internal MiniExcelStreamExporter(int maxSheetRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSheetRows, 1);

        _maxSheetRows = maxSheetRows;
    }

    /// <summary>
    /// 本导出器生效的单张工作表行数上限，供接线断言确认公开入口读的就是 <see cref="ExcelConstants.MaxSheetRows"/>
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
    /// 或 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>；
    /// 该属性是 <c>required</c> 非空成员，null 只会来自非法声明，并在写出第一格之前就被拒）</exception>
    /// <exception cref="ArgumentException"><see cref="ExcelSheetSpec.SheetName"/> 超过 31 个字符、含工作簿不接受的字符
    /// （<c>: \ / ? * [ ]</c> 与控制字符 <c>U+0000</c>、<c>U+0003</c>）或以单引号开头／结尾，此时
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c>；某列的 <see cref="ExcelColumn.Header"/>
    /// 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，此时 <see cref="ArgumentException.ParamName"/>
    /// 为 <c>Header</c>；或 <see cref="ExcelSheetSpec.Title"/> 长过 <see cref="ExcelConstants.MaxCellTextLength"/>
    /// 个字符，此时 <see cref="ArgumentException.ParamName"/> 为 <c>Title</c>。三条判据都与全量写出路径同一份
    /// （表名走 <see cref="ExcelWorkbookWriteGuard"/>，表头与标题的长度走 <see cref="ExcelCellTextGuard"/>），
    /// 消息逐字相同，不交给写出库去拒或转义。表头与标题的长度都排在调用写出库之前，抛出时输出流零字节、
    /// 行集合一次都没被枚举，写出侧<u>不截断</u>；标题在本路径不落档（见降级理由），但声明超长照拒——
    /// 放过就等于让「同一份规格能不能导」由走哪条路径决定，而分派器是按行数替调用方选的</exception>
    /// <exception cref="InvalidOperationException">行集合里有某笔元素与 <see cref="ExcelSheetSpec.RowType"/> 不符；
    /// 列清单里有两列用了同一个 <see cref="ExcelColumn.Key"/>；表头行与数据行加起来要落到第
    /// <see cref="ExcelConstants.MaxSheetRows"/> 行以后（单张工作表只有这么多行，超出的行没有可落的位置，
    /// 上限按每张工作表各自计、不做整簿累计，消息点出上限值、触线的那一行与「分成多张表或改用文字档」两条出路；
    /// 本路径不写标题行，因此 <see cref="ExcelSheetSpec.Title"/> 不占行数，这一点与全量路径不同）；
    /// 或某个行值装不进本路径要落的格子——
    /// 早于 1899-12-30 的 <see cref="DateTime"/>、<see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/>
    /// （后两者在本路径落日期格，故只在本路径拒；全量路径把它们落成文本格、值原样读回，不判这一条）、
    /// <c>NaN</c> 或 <c>±∞</c>、有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>／<c>double</c>／<c>float</c>（这一条与全量路径同判）、
    /// 长过单元格上限的字串。消息都点名实际成因：行型不符者报出行号与期望／实际
    /// 两个类型全名，重复列键者报出重复的键，取值越界者报出行位置、表头与列键。取值类与行数上限的判定只在
    /// 取到那一行时才做得出来，抛出时前面的行可能已经落进流里，调用方必须丢弃这条流的内容；
    /// 重复列键在调用写出库之前就被拒，输出流零字节</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消。取消在入口被观察到时
    /// 输出流零字节；在写出中途被观察到时流里可能已有没收尾的字节；在写出收尾之后才被观察到时流里可能已是一份
    /// 完整的档——三种落点交出的都是异常，调用方必须丢弃这条流的内容</exception>
    public async Task<ExcelExportResult> ExportAsync(
        Stream output,
        ExcelSheetSpec sheet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheet);

        cancellationToken.ThrowIfCancellationRequested();

        // 行型声明与表名都是声明级判定，排在投影与调用写出库之前：坏声明一个字节都不该进流
        var rowType = ExcelRowTypeGuard.ValidateDeclaration(sheet);
        ExcelWorkbookWriteGuard.ValidateSheetName(sheet.SheetName, 1);

        var columns = sheet.Columns;

        // 表头与标题的长度同样是声明级判定，判据与全量路径共用 ExcelCellTextGuard 那一份；
        // 排在调用写出库之前，因此超长文案一个字节都不会进流。标题在本路径不落档，但声明非法照拒，
        // 免得同一份规格走全量抛、走流式却静默交回一份档
        ExcelCellTextGuard.ValidateHeaders(columns);
        ExcelCellTextGuard.ValidateTitle(sheet.Title);

        EnsureDistinctKeys(columns);

        var configuration = new OpenXmlConfiguration
        {
            // 关掉库自带的表头预设样式：不关掉时表头会落上一层调用方没要过的底色
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

        // 回传结果之前再查一次：取消落在最后一行的投影或库的收尾期间时，逐行检查已经没有下一轮可拦。
        // 此时流里可能已经是一份完整的档，能主张的只有「不交出成功结果」，不主张零字节
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
    /// <remarks>
    /// 本路径把每行投影成「列键到取值」的字典，键就是列在这一行里的定位符：两个列共用一个键时后写的盖掉先写的，
    /// 交回的档里少一栏而结果仍写着成功。全量路径按列的下标落格、不受这个问题影响，因此判定归本类，
    /// 不上升到表规格——同一份规格走全量能导、走流式不能导，是由行模型的形状决定的，不是两套规矩。
    /// </remarks>
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
    /// 列宽不填：写出库的默认宽度是个定值，填与不填都是「调用方没要的宽度」，不如按未承载处理并在理由里说出。
    /// 每列的落位序号必须逐列给出去——它的缺省值会把多列挤到同一个序号上，落位顺序就由声明顺序变成了不确定的东西。
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
    /// 每行的键集与列清单完全一致、缺一键都不行：写出库按第一行的键定列集，后面的行缺键会当场抛，
    /// 少给一列就是把「列集由列清单决定」这件事交给了行的形状。取值走列自己的取值方法，
    /// <c>null</c> 与异型行按列的既有契约交回 <c>null</c>，异型行在本趟判定里已经先抛出。
    /// 行上限按「这一行数据要落到工作表的第几行」比对——本路径的表头占第 1 行，第 <c>n</c> 行数据落在第
    /// <c>1 + n</c> 行；判定就在这唯一的一趟循环里，不为计数把行集合物化、也不回头再枚举一遍。
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

            // 行上限判定就在这一趟循环里：用的正是刚数出来的行号，不为计数把行集合物化、也不回头再枚举一遍
            var rowNumber = 1 + rowIndex;

            if (rowNumber > maxSheetRows)
            {
                throw CreateRowLimitFailure(rowIndex, rowNumber, maxSheetRows);
            }

            // 每一行都判，判据与另两条路径同一份；用的就是刚取到的这一行，不物化行集合
            ExcelRowTypeGuard.ValidateRow(rowType, row, rowIndex, "xlsx 流式导出");

            var values = new Dictionary<string, object?>(columns.Count);

            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                var value = column.GetValue(row);

                // 取值域判定只交回成因文字，行位置在真要抛时才拼出来：每行每格都先分配一条消息，
                // 等于让正常路径替异常路径付钱
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
    /// 消息是政策陈述：只说本组件按 <see cref="ExcelConstants.MaxSheetRows"/> 拒写、上限按每张工作表各自计，
    /// 不描述写出库在这一步会做什么。本路径边枚举行边往流里吐字节，判定又落在逐行循环内，
    /// 因此抛出时流里可能已经落了没收尾的字节——能主张的只有「不交出成功结果」，不主张零字节，
    /// 调用方按既有口径丢弃这条流的内容。
    /// </remarks>
    private static InvalidOperationException CreateRowLimitFailure(int position, int rowNumber, int maxSheetRows)
        => new(
            $"xlsx 流式导出无法完成：第 {position} 行数据要落到工作表的第 {rowNumber} 行，" +
            $"超过单张工作表的上限 {maxSheetRows} 行（表头行也算在内）。" +
            "这道上限按每张工作表各自计，不做整簿累计；超出的行在 xlsx 里没有可落的位置，" +
            "请把数据分成多张表，或改用不带这道行数上限的文字档（CSV／定宽）。");
}
