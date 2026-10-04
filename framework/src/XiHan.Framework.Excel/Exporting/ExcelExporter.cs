// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 导出分派器：按目标格式与流式表态把表规格送到对应的写出器，自身不写一个字节
/// </summary>
/// <remarks>
/// <para>
/// 本类只做三件事：判分派输入、选一条路径、把那条路径交回的结果原样带出。守卫一律不抄第二份——
/// 行型一致性、列宽值域、颜色失败面、表名判据、取值可写性、文字档的分隔符禁令都长在各自的写出器里，
/// 本类既不重判一遍，也不吞掉或改写它们交回的异常型别与消息。分派输入的检查（格式取值、列清单、
/// 预期行数、流式表态、多表清单）是本类独有的、任何写出器都不判的东西，因此由本类在路由之前判完。
/// </para>
/// <para>
/// 分派规则。<see cref="ExcelFormat.Csv"/> 与 <see cref="ExcelFormat.Txt"/> 一律交文字档写出器，
/// 它内部按布局分分隔符与固定宽度两条。<see cref="ExcelFormat.Xlsx"/> 按流式表态分流：
/// <see cref="ExcelSheetSpec.ForceStreaming"/> 为 <c>true</c> 走流式，为 <c>false</c> 走全量——
/// 显式表态优先于阈值，预期行数再大也不改判；两者都没给而 <see cref="ExcelSheetSpec.ExpectedRowCount"/>
/// 也没给时抛出而不是猜一个，因为「达到阈值自动切流式」要求先知道行数，而行数不可预知时切过去连带静默丢样式，
/// 正是本组件禁止的静默降级。只有 <see cref="ExcelSheetSpec.ForceStreaming"/> 为 <c>null</c> 且给了预期行数时，
/// 才拿它与 <see cref="XiHanExcelOptions.StreamingThreshold"/> 比：<c>=</c> 即达阈值算流式（该阈值的语义是
/// 「达到该规模改用流式」），小于阈值算全量。阈值为 0 或负数时任何非负行数都算达到，因此一律走流式——
/// 这是「一行都不想全量」的合法表达，不当成非法取值拒掉。
/// </para>
/// <para>
/// 多表入口 <see cref="ExportAllAsync"/> 恒走全量工作簿：多表要共用一个工作簿、共用表名判重与落位顺序，
/// 流式模式承担不了，因此清单里任何一张表要求流式即整个请求被拒，不做「这张表改成全量」的改写。
/// </para>
/// <para>
/// <see cref="ExcelSheetSpec.Columns"/> 一列都没有的表在路由之前被拒。三条路径对零列的现实各不相同
/// （文字档写成每行一个空行、全量写一张只有空表格区域的表、流式连表头都不写），放行就是让同一份坏输入
/// 交出三种半成品；本类是唯一能一次管住全部路径的位置，因此这道判定归本类，各写出器不重复判。
/// 列清单为 <c>null</c> 同罪：那是 <c>required</c> 非空成员的非法声明，读它的数量只会撞出一个未声明的空引用异常。
/// </para>
/// <para>
/// 目标格式是 <see cref="ExcelFormat.Xlsx"/> 时 <c>textOptions</c> 不生效，也不报错——沿用本组件对
/// 「设置了但不生效的选项」的一贯口径。输出流的所有权在调用方：本类不关闭、不复位、不改动流位置，
/// 写出从调用方留下的位置开始。取消的口径由 <see cref="IExcelExporter"/> 定死，四条路径共用：
/// 取消被观察到时一律抛出、绝不交出结果，落盘时机决定流里剩下什么，调用方在抛出后必须丢弃该流的内容。
/// </para>
/// </remarks>
/// <param name="closedXmlExporter">全量工作簿写出器</param>
/// <param name="streamExporter">流式工作簿写出器</param>
/// <param name="textExporter">文字档写出器</param>
/// <param name="options">Excel 选项，本类读取其中的 <see cref="XiHanExcelOptions.StreamingThreshold"/></param>
/// <exception cref="ArgumentNullException"><paramref name="closedXmlExporter"/>、<paramref name="streamExporter"/>、
/// <paramref name="textExporter"/> 或 <paramref name="options"/> 为 <c>null</c></exception>
public sealed class ExcelExporter(
    ClosedXmlExporter closedXmlExporter,
    MiniExcelStreamExporter streamExporter,
    DelimitedTextExporter textExporter,
    XiHanExcelOptions options) : IExcelExporter
{
    private readonly ClosedXmlExporter _closedXmlExporter =
        closedXmlExporter ?? throw new ArgumentNullException(nameof(closedXmlExporter));

    private readonly MiniExcelStreamExporter _streamExporter =
        streamExporter ?? throw new ArgumentNullException(nameof(streamExporter));

    private readonly DelimitedTextExporter _textExporter =
        textExporter ?? throw new ArgumentNullException(nameof(textExporter));

    private readonly XiHanExcelOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    /// <remarks>
    /// 分派与入参检查全部排在写出第一个字节之前，因此非法的分派输入不留半份文件；
    /// 抛出的是被选中那条路径原本的异常，本类不改型别也不重写消息。
    /// </remarks>
    public async Task<ExcelExportResult> ExportAsync(
        Stream output,
        ExcelSheetSpec sheet,
        ExcelFormat format,
        ExcelTextOptions? textOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheet);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateColumns(sheet, 1);

        var useStreaming = format switch
        {
            ExcelFormat.Csv or ExcelFormat.Txt => false,
            ExcelFormat.Xlsx => ResolveStreaming(sheet),
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                $"不支持的目标格式「{format}」：本组件只导 {ExcelFormat.Xlsx}、{ExcelFormat.Csv} 与 {ExcelFormat.Txt}，" +
                "不导旧版二进制 .xls，也不导 .xlsm、宏、数据透视表与图表。")
        };

        return format switch
        {
            ExcelFormat.Csv or ExcelFormat.Txt => await _textExporter.ExportAsync(
                output, sheet, format, textOptions, cancellationToken).ConfigureAwait(false),
            ExcelFormat.Xlsx when useStreaming => await _streamExporter.ExportAsync(
                output, sheet, cancellationToken).ConfigureAwait(false),
            ExcelFormat.Xlsx => await _closedXmlExporter.ExportAsync(
                output, sheet, cancellationToken).ConfigureAwait(false),
            _ => throw new UnreachableFormatException(format)
        };
    }

    /// <inheritdoc />
    public async Task<ExcelExportResult> ExportAllAsync(
        Stream output,
        IReadOnlyList<ExcelSheetSpec> sheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheets);

        cancellationToken.ThrowIfCancellationRequested();

        for (var index = 0; index < sheets.Count; index++)
        {
            var position = index + 1;
            var sheet = sheets[index];

            // 清单里的 null 项不在这里判：那是全量写出器已经点名第几项的既有守卫，本类不抄第二份
            if (sheet is null)
            {
                continue;
            }

            ValidateColumns(sheet, position);

            if (sheet.ForceStreaming == true)
            {
                throw new InvalidOperationException(
                    $"xlsx 多表导出无法完成：第 {position} 张表把 {nameof(ExcelSheetSpec.ForceStreaming)} 置为 true。" +
                    $"多表恒走全量工作簿（{nameof(IExcelExporter.ExportAllAsync)} 没有流式形态），" +
                    "因为若干张表要共用一个工作簿、共用表名判重与落位顺序。" +
                    $"请去掉该项（要按 {nameof(XiHanExcelOptions.StreamingThreshold)} 分流就用 {nameof(ExcelExporter.ExportAsync)} 单表导），" +
                    "或把这张表单独走单表流式导出。");
            }
        }

        return await _closedXmlExporter.ExportAllAsync(output, sheets, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按流式表态与预期行数决定这张 xlsx 表走哪条路径
    /// </summary>
    /// <param name="sheet">表规格</param>
    /// <returns><c>true</c> 走流式，<c>false</c> 走全量</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExcelSheetSpec.ExpectedRowCount"/> 为负数</exception>
    /// <exception cref="InvalidOperationException"><see cref="ExcelSheetSpec.ForceStreaming"/> 与
    /// <see cref="ExcelSheetSpec.ExpectedRowCount"/> 都没给</exception>
    private bool ResolveStreaming(ExcelSheetSpec sheet)
    {
        // 负数先拒：它既不是「没有行数」也不是「行数为零」，放行等于让一个不成立的数字参与比阈值
        if (sheet.ExpectedRowCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExcelSheetSpec.ExpectedRowCount),
                sheet.ExpectedRowCount,
                $"{nameof(ExcelSheetSpec.ExpectedRowCount)} 不能为负数：行数只有「未知」（置 null，" +
                $"并靠 {nameof(ExcelSheetSpec.ForceStreaming)} 表态）与非负两种取值，" +
                "负数不是「未填」的另一种写法。");
        }

        switch (sheet.ForceStreaming)
        {
            case true:
                return true;

            case false:

                // 显式要全量就不看阈值：调用方已经承担了整份档建在内存里的代价换排版，分派器不替他改判
                return false;

            default:
                if (sheet.ExpectedRowCount is null)
                {
                    throw new InvalidOperationException(
                        $"xlsx 导出无法完成：{nameof(ExcelSheetSpec.ForceStreaming)} 与 " +
                        $"{nameof(ExcelSheetSpec.ExpectedRowCount)} 两个都没给，分派器不知道这张表该走全量还是流式，" +
                        "也不猜——猜过去会连带静默丢样式。三条出路：给 " +
                        $"{nameof(ExcelSheetSpec.ExpectedRowCount)}（由它与 {nameof(XiHanExcelOptions.StreamingThreshold)} " +
                        $"比出结果）、把 {nameof(ExcelSheetSpec.ForceStreaming)} 置为 true 或 false，" +
                        "或者把目标格式换成不丢样式的文字档。");
                }

                return sheet.ExpectedRowCount.Value >= _options.StreamingThreshold;
        }
    }

    /// <summary>
    /// 在路由之前确认这张表有列可写
    /// </summary>
    /// <param name="sheet">表规格</param>
    /// <param name="position">表在清单里的位置；单表路径固定为 1</param>
    /// <exception cref="ArgumentException">列清单为 <c>null</c> 或一列都没有，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>Columns</c></exception>
    private static void ValidateColumns(ExcelSheetSpec sheet, int position)
    {
        if (sheet.Columns is null || sheet.Columns.Count == 0)
        {
            var cause = sheet.Columns is null
                ? $"{nameof(ExcelSheetSpec.Columns)} 为 null：列清单是 required 非空成员，null 只会来自非法声明，" +
                  "读它的数量只会撞出一个没有文档过的空引用异常"
                : "一列都没有";

            throw new ArgumentException(
                $"导出无法完成：第 {position} 张表{cause}。没有列就没有表头，也没有一个取值落得进格，" +
                "三条写出路径对这种输入各交回一种半成品（每行一个空行、一张空表、连表头都不写的空表），" +
                "哪一种都不算导好了。请至少给出一列，或在这张表本来就没有可导内容时不要调用导出。",
                nameof(ExcelSheetSpec.Columns));
        }
    }

    /// <summary>
    /// 只在格式判定的两条 switch 已经穷尽时才可能到达，用来把「未知格式」留在抛出而不是落到某条路径
    /// </summary>
    /// <param name="format">没有被任何分支接住的格式取值</param>
    private sealed class UnreachableFormatException(ExcelFormat format) : Exception(
        $"xlsx 导出分派失败：目标格式 {format} 没有被任何一条路径接住。");
}
