// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Logging;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Text;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 文字档导出器，产出 <c>.csv</c> 与 <c>.txt</c>，按 <see cref="ExcelTextOptions.Layout"/> 走分隔符或固定宽度布局
/// </summary>
/// <remarks>
/// <para>
/// 逐行写入 <see cref="StreamWriter"/>，不物化整个结果集：行集合是 <see cref="ExcelSheetSpec.Rows"/> 给的非泛型
/// 序列，可以是惰性游标，导出规模不受内存里的行数限制，因此也没有流式与否的阈值判断。取消令牌在每行写前、
/// 整档 flush 之前与回传结果之前各检查一次：最后一笔取值期间才发生的取消由后两处拦下，此时缓冲已经落进流里，
/// 本类只主张不交出成功结果，不主张零字节。
/// </para>
/// <para>
/// 行集合的元素按 <see cref="ExcelSheetSpec.RowType"/> 逐笔校验，判据与 xlsx 路径共用
/// <see cref="ExcelRowTypeGuard"/>，两边不各写一份：声明为 <c>null</c> 属于非法声明，在写出第一格之前的预检里就抛；
/// 取到的每一笔都要比对。异型行经列的取值方法只会交回 <c>null</c>，放行就是写出一份表头齐全、数据全空的档。
/// </para>
/// <para>
/// 文字档没有样式概念，因此 <see cref="ExcelSheetSpec"/> 上的工作簿排版项（标题行、冻结、筛选、表头加粗与底色、
/// 边框）与列上的宽度、对齐、换行、数字格式全部忽略且不记日志，返回值一律走
/// <see cref="ExcelExportResult.Styled(ExcelFormat, string, string)"/>，其中「样式已落地」表示没有丢弃任何请求的
/// 样式，不是样式已生效。列宽与数字格式在文字档上的对应物是 <see cref="ExcelColumn.TextFormat"/>。
/// </para>
/// <para>
/// 表头行与数据行走同一套公式注入防护：判据与写出入口都只有一份。表头文案是调用方在运行时给出的
/// <c>required string</c>，框架无从证明它出自开发者而不是终端使用者，因此不按「来源可信」豁免；
/// 以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 开头的标题会多出一个可见的 <c>'</c>，这一条代价写在
/// <see cref="ExcelTextOptions.EscapeFormulaPrefix"/> 上，关掉本设置时表头与数据一并保留原值。
/// </para>
/// <para>
/// <see cref="ExcelTextQuote.None"/> 下值内的分隔符与换行无法原样写出，本类把它们替换为空格并记一条 Warning
/// 日志，说明是哪一行哪一列被改写，不静默产出坏数据。分隔符本身的取值另有三条被拒（全部排在写出任何字节之前，
/// 判据是「这个组合写出去就读不回来」，不是「不推荐」）：空格配 <see cref="ExcelTextQuote.None"/> 时值内空格与
/// 分隔符不可区分、替换成空格又是 no-op，列数直接错位；<c>\r</c> 与 <c>\n</c> 本身就是行分隔符，两栏会被写成两行；
/// 引号字符 <c>"</c> 作分隔符时 <see cref="ExcelTextQuote.Minimal"/>／<see cref="ExcelTextQuote.All"/> 用来包住
/// 字段的引号与分隔符成了同一个字符，取 <see cref="ExcelTextQuote.None"/> 时值内的引号又与分隔符不可区分。
/// 这三类都抛出而不是代为改写。
/// </para>
/// <para>
/// <see cref="ExcelTextOptions.EscapeFormulaPrefix"/> 默认开启，被加前缀的字段也与原始值不同，同属改数据；但负数
/// 在业务档里极常见，逐格记日志会把真正要看的信号淹掉，因此整份文件累计一条 Warning，报出改写数量与第一个
/// 触发的行列，可定位即可。
/// </para>
/// <para>
/// <see cref="ExcelTextLayout.FixedWidth"/> 布局另走一条写出路径：字段不加引号、不做转义、不设公式前缀，
/// 每格由 <see cref="ExcelColumn.FixedWidth"/> 指定的字节宽度补位，读档方按字节位置切列。该布局下
/// <see cref="ExcelTextOptions.Delimiter"/>、<see cref="ExcelTextOptions.Quote"/> 与
/// <see cref="ExcelTextOptions.EscapeFormulaPrefix"/> 都不参与写出，且不接受值内换行：
/// <c>\r</c> 与 <c>\n</c> 没有可以包住它们的引号，写出会让一档被读成错行的两档，取到该行即抛。
/// </para>
/// <para>
/// 两种布局共用一份行写出骨架（<see cref="WriteTextAsync"/>）：行序、每行写前查取消令牌、行尾拼接、flush 与
/// 聚合留痕的时机都只有一份实现，布局差异只留在「一格写出什么」与「格间插什么」上。
/// </para>
/// </remarks>
/// <param name="logger">本导出器的日志器，用于记录改写数据的决定</param>
public sealed class DelimitedTextExporter(ILogger<DelimitedTextExporter> logger)
{
    /// <summary>
    /// 免引号策略下与列名一起出现在日志里的行位置标签
    /// </summary>
    private const string HeaderPosition = "表头行";

    /// <summary>
    /// 与 <see cref="ExcelTextQuote.None"/> 结构性冲突的分隔符
    /// </summary>
    private const char SpaceDelimiter = ' ';

    private readonly ILogger<DelimitedTextExporter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// 把一张表写成文字档，布局由 <see cref="ExcelTextOptions.Layout"/> 决定
    /// </summary>
    /// <param name="output">输出流，导出器只写入不关闭，由调用方拥有</param>
    /// <param name="sheet">表规格，列清单的顺序即写出顺序</param>
    /// <param name="format">目标格式，只接受 <see cref="ExcelFormat.Csv"/> 与 <see cref="ExcelFormat.Txt"/></param>
    /// <param name="textOptions">文字档选项，传 <c>null</c> 时使用 <see cref="ExcelTextOptions"/> 的默认值</param>
    /// <param name="cancellationToken">取消令牌，取消时不再写出后续行</param>
    /// <returns>导出结果，含实际写出的扩展名与内容类型</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为 <c>null</c>，
    /// 或 <see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>（<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>；
    /// 该属性是 <c>required</c> 非空成员，null 只会来自 <c>null!</c> 的非法声明，并在写出第一格之前就被拒）</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不是 <c>Csv</c> 或 <c>Txt</c></exception>
    /// <exception cref="ArgumentException">编码名无法解析；分隔符取 <c>\r</c> 或 <c>\n</c>（本身就是行分隔符，
    /// 两栏会被写成两行）或取引号字符 <c>"</c>（与 <see cref="ExcelTextQuote.Minimal"/>／<see cref="ExcelTextQuote.All"/>
    /// 的引号语义冲突，取 <see cref="ExcelTextQuote.None"/> 时又与值内引号不可区分）；或
    /// <see cref="ExcelTextQuote.None"/> 与空格分隔符组合；或固定宽度布局下某列的补位字符是换行符、在目标编码下不是单字节。
    /// 分隔符三类的 <see cref="ArgumentException.ParamName"/> 均为 <c>textOptions</c>，且都在写出任何字节之前抛出</exception>
    /// <exception cref="InvalidOperationException">行集合里有某笔元素与 <see cref="ExcelSheetSpec.RowType"/> 不符，
    /// 消息点名行位置与期望／实际两个类型全名；或固定宽度布局下某列未设置
    /// <see cref="ExcelColumn.FixedWidth"/>、列宽不是正整数，取值转出的文本含换行，
    /// 或内容超出列宽且 <see cref="ExcelTextOptions.Overflow"/> 为 <see cref="ExcelTextOverflow.Throw"/></exception>
    /// <exception cref="EncoderFallbackException">目标编码收不下待写出的字符（详见 <see cref="TextWriterHelper"/>）</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 分隔符取 <see cref="ExcelTextOptions.Delimiter"/>，未指定时按格式取默认值：<c>.csv</c> 用 <c>,</c>，
    /// <c>.txt</c> 用制表符。
    /// </para>
    /// <para>
    /// 格式、取消令牌、选项组合与编码名的检查全部在写出第一个字节之前完成，这几类非法输入不会留下半份文件。
    /// 目标编码收不下字符时抛 <see cref="EncoderFallbackException"/>；已写出的字节是否为零由缓冲区决定，
    /// 这里只保证不会产出「看起来成功」的坏档。固定宽度布局的列宽与补位字符同样排在预写校验里。
    /// </para>
    /// <para>
    /// 取消另在整档 flush 之前与回传结果之前各查一次（见 <see cref="WriteTextAsync"/>）：最后一笔取值期间才发生的
    /// 取消不会有下一轮的逐行检查可拦，这两处就是它的落点。抛出时前面的行可能已经落盘，本方法不主张零字节。
    /// </para>
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

        if (format is not (ExcelFormat.Csv or ExcelFormat.Txt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                $"文字导出器只写文字档，不支持格式「{format}」，可选值为 {ExcelFormat.Csv} 与 {ExcelFormat.Txt}。");
        }

        var options = textOptions ?? new ExcelTextOptions();

        cancellationToken.ThrowIfCancellationRequested();

        if (options.Layout == ExcelTextLayout.FixedWidth)
        {
            return await ExportFixedWidthAsync(output, sheet, format, options, cancellationToken);
        }

        var delimiter = options.Delimiter ?? (format == ExcelFormat.Csv ? ',' : '\t');

        // 三条分隔符拒写全部排在写出任何字节之前：它们不是「不推荐的取值」，而是写了就读不回来的取值
        if (delimiter is '\r' or '\n')
        {
            throw new ArgumentException(
                $"以「{(delimiter == '\r' ? "\\r" : "\\n")}」作分隔符会破坏行结构：这两个字符本身就是行分隔符，" +
                "两栏会被直接写成两行，档读不回原列数却仍回报成功。请改用不会结束一行的字符作分隔符。",
                nameof(textOptions));
        }

        if (delimiter == TextWriterHelper.QuoteChar)
        {
            throw new ArgumentException(
                "以引号字符「\"」作分隔符时，引号策略无法成立：Minimal／All 用来包住字段的引号与分隔符是同一个字符，" +
                $"取 {ExcelTextQuote.None} 时值内的引号又与分隔符不可区分，写出的档按同一策略读不回原列数。" +
                "请改用其他字符作分隔符。",
                nameof(textOptions));
        }

        if (options.Quote == ExcelTextQuote.None && delimiter == SpaceDelimiter)
        {
            throw new ArgumentException(
                $"以空格作分隔符时，「不加引号」策略无法成立：值内的空格与分隔符无法区分，" +
                $"既不替换也不加引号会直接产出列数错位的坏档。请改用其他分隔符，或将引号策略设为 {ExcelTextQuote.Minimal}／{ExcelTextQuote.All}。",
                nameof(textOptions));
        }

        var encoding = TextWriterHelper.ResolveEncoding(options.EncodingName);

        // 公式前缀的改写计数与首个触发点：改数据必须留痕，但逐格记日志会淹掉真正要看的信号，故全份聚合成一条
        var escapedCount = 0;
        var firstEscapedPosition = string.Empty;
        var firstEscapedHeader = string.Empty;
        var firstEscapedKey = string.Empty;

        return await WriteTextAsync(
            output,
            sheet,
            format,
            options,
            encoding,
            delimiter.ToString(),
            PrepareField,
            ReportFormulaEscapes,
            cancellationToken);

        void ReportFormulaEscapes()
        {
            if (escapedCount > 0)
            {
                _logger.LogWarning(
                    "本次导出有 {Count} 个字段被加了公式注入前缀「'」，首个为{Position}的「{Header}」列（键 {Key}）：" +
                    "这些值读回会多出前缀，不能与原值逐字往返；机器逐字段解析本档时请关掉 {Option}。",
                    escapedCount,
                    firstEscapedPosition,
                    firstEscapedHeader,
                    firstEscapedKey,
                    nameof(ExcelTextOptions.EscapeFormulaPrefix));
            }
        }

        // index 由共用骨架交出（固定宽度布局要用它取该列列宽），分隔符布局不解释它
        string PrepareField(ExcelColumn column, int index, string rawText, string position)
        {
            var guarded = rawText;

            // 表头与数据同一判据、同一入口：表头文案是调用方运行时给出的，框架无法证明它不是外来数据
            if (options.EscapeFormulaPrefix && TextWriterHelper.NeedsFormulaEscape(rawText))
            {
                guarded = TextWriterHelper.EscapeFormula(rawText);
                escapedCount++;

                if (escapedCount == 1)
                {
                    firstEscapedPosition = position;
                    firstEscapedHeader = column.Header;
                    firstEscapedKey = column.Key;
                }
            }

            if (options.Quote == ExcelTextQuote.None && TextWriterHelper.ContainsUnquotable(guarded, delimiter))
            {
                // 改数据必须留痕：免引号策略下不加引号又原样写出会破坏列数，替换成空格又不能让调用方毫不知情。
                // 是否被改写以 helper 的判定为准，不用「改写前后字符串是否相等」反推：替换成空格可能恰好等长
                _logger.LogWarning(
                    "{Position}的「{Header}」列（键 {Key}）值内含分隔符「{Delimiter}」或换行，" +
                    "不加引号策略无法原样写出，已替换为空格，导出的文字档不能原样往返。",
                    position,
                    column.Header,
                    column.Key,
                    delimiter);
            }

            return TextWriterHelper.QuoteIfNeeded(guarded, delimiter, options.Quote);
        }
    }

    /// <summary>
    /// 按固定宽度布局把一张表写成文字档
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分隔符、引号策略与公式注入前缀在本布局下都不解释：档由读档方按字节位置切列，引号与分隔符没有对应的解析位，
    /// 前缀还会吃掉一格字节宽度、让后续列位整体错位。因此写出的是「原值补位到列宽」，取值仍走
    /// <see cref="TextWriterHelper.ValueToText"/>，只是不套引号与转义。
    /// </para>
    /// <para>
    /// 列宽与补位字符在写出第一个字节之前判完（<see cref="ValidateFixedWidthColumns"/>），非法布局不写出任何字节；
    /// 补位方向与超宽策略的取值在逐格补位时判定，与分隔符路径的引号策略同一时机。截断会丢弃数据，与免引号改写、
    /// 公式前缀同类，因此整份文件聚合成一条 Warning，报出数量与首个触发的行列。
    /// </para>
    /// <para>
    /// 本布局不接受值内换行：<c>\r</c> 与 <c>\n</c> 在定宽档里没有可以包住它们的引号，写出去会让一档被读成错行的
    /// 两档，因此取到该行即抛 <see cref="InvalidOperationException"/>，不清洗也不替换。行集合是惰性游标，
    /// 值含不含换行要等取出行才知道，所以这条不可能排在写出之前——抛出时前面的行可能已经落盘。
    /// </para>
    /// </remarks>
    private async Task<ExcelExportResult> ExportFixedWidthAsync(
        Stream output,
        ExcelSheetSpec sheet,
        ExcelFormat format,
        ExcelTextOptions options,
        CancellationToken cancellationToken)
    {
        var encoding = TextWriterHelper.ResolveEncoding(options.EncodingName);
        var columns = sheet.Columns;

        // 列宽与补位字符全部排在 new StreamWriter 之前：这几类输入不成立时一个字节都不进流
        var widths = ValidateFixedWidthColumns(columns, encoding);

        // 截断的丢弃计数与首个触发点：理由同分隔符路径的公式前缀，逐格记日志会淹掉真正要看的信号
        var truncatedCount = 0;
        var firstTruncatedPosition = string.Empty;
        var firstTruncatedHeader = string.Empty;
        var firstTruncatedKey = string.Empty;
        var firstTruncatedWidth = 0;

        // 定宽档的格与格之间不插任何字符：列宽已经把这一格占满
        return await WriteTextAsync(
            output,
            sheet,
            format,
            options,
            encoding,
            string.Empty,
            PadField,
            ReportTruncations,
            cancellationToken);

        void ReportTruncations()
        {
            if (truncatedCount > 0)
            {
                _logger.LogWarning(
                    "本次固定宽度导出有 {Count} 个字段超出列宽并被截断，首个为{Position}的「{Header}」列（键 {Key}，列宽 {Width} 字节）：" +
                    "超出部分没有写出，读回的档不能与原值逐字往返；请加大该列的 FixedWidth，或将超宽策略 Overflow 设为 Throw。",
                    truncatedCount,
                    firstTruncatedPosition,
                    firstTruncatedHeader,
                    firstTruncatedKey,
                    firstTruncatedWidth);
            }
        }

        // 表头与数据在本布局按同一列宽补位：布局不解释公式前缀，写出的一律是原值补到列宽
        string PadField(ExcelColumn column, int index, string rawText, string position)
        {
            var width = widths[index];

            // 值里的换行在本布局没有可以包住它的引号，写出去就把一档切成错行的两档；不清洗、不替换，取到即抛
            if (TextWriterHelper.ContainsLineBreak(rawText))
            {
                throw CreateFieldFailure(
                    column,
                    position,
                    "值内含换行（\\r 或 \\n），固定宽度布局没有可以包住它的引号，写出会让一档被读成错行的两档。" +
                    "请先清洗该值，或改用分隔符（Delimited）布局。");
            }

            // 是否被截断由字节数直接判出，不从「补位后的串」反推：补位永远把整格填到列宽，反推看不出丢弃过内容
            var isTruncated = options.Overflow == ExcelTextOverflow.Truncate && encoding.GetByteCount(rawText) > width;

            string padded;

            try
            {
                padded = TextWriterHelper.PadToWidth(rawText, width, encoding, column.Padding, column.PadChar, options.Overflow);
            }
            catch (InvalidOperationException ex)
            {
                // 只补定位信息，不改变处置：超宽到底是抛还是截断仍由选项决定，这里不替调用方兜住
                throw CreateFieldFailure(column, position, ex.Message, ex);
            }

            if (isTruncated)
            {
                truncatedCount++;

                if (truncatedCount == 1)
                {
                    firstTruncatedPosition = position;
                    firstTruncatedHeader = column.Header;
                    firstTruncatedKey = column.Key;
                    firstTruncatedWidth = width;
                }
            }

            return padded;
        }
    }

    /// <summary>
    /// 两种文字档布局共用的写出骨架：表头行、逐行取值、每行写前查取消令牌、行尾拼接、flush、聚合留痕、回传前再查一次与结果尾部
    /// </summary>
    /// <remarks>
    /// 布局之间的差异只留在两个委托与 <paramref name="fieldSeparator"/> 上：<c>renderField</c> 决定一格写出什么
    /// （分隔符布局套公式前缀与引号，固定宽度布局做字节补位），<c>fieldSeparator</c> 决定格间插什么（定宽为空字串）。
    /// 行序、取消时机与聚合 Warning 的落地时机（flush 之后、返回结果之前）因此只有一份实现，不会两条路径各漂一份。
    /// 取消检查共三处：每行写前、flush 之前、聚合留痕之后与回传结果之前。后两处抛出时缓冲已经落进流里，
    /// 本骨架不主张零字节，只主张不交出成功结果；改写过数据时的 Warning 在抛出之前已经记下。
    /// </remarks>
    private async Task<ExcelExportResult> WriteTextAsync(
        Stream output,
        ExcelSheetSpec sheet,
        ExcelFormat format,
        ExcelTextOptions options,
        Encoding encoding,
        string fieldSeparator,
        RenderField renderField,
        ReportAggregate reportAggregate,
        CancellationToken cancellationToken)
    {
        var columns = sheet.Columns;
        var line = new StringBuilder();

        // 声明级预检排在 new StreamWriter 之前：坏声明不写出任何字节，行集合一次都不被枚举
        var rowType = ExcelRowTypeGuard.ValidateDeclaration(sheet);

        using var writer = new StreamWriter(output, encoding, leaveOpen: true);

        if (options.IncludeHeader)
        {
            line.Clear();

            for (var index = 0; index < columns.Count; index++)
            {
                if (index > 0 && fieldSeparator.Length > 0)
                {
                    line.Append(fieldSeparator);
                }

                var column = columns[index];
                line.Append(renderField(column, index, column.Header, HeaderPosition));
            }

            line.Append(options.NewLine);
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken);
        }

        var rowIndex = 0;

        foreach (var row in sheet.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowIndex++;

            // 每一行都判行型，判据与 xlsx 路径同一份；用的就是刚取到的这一行，不物化行集合
            ExcelRowTypeGuard.ValidateRow(rowType, row, rowIndex, "文字导出");

            line.Clear();
            var position = $"第 {rowIndex} 行";

            for (var index = 0; index < columns.Count; index++)
            {
                if (index > 0 && fieldSeparator.Length > 0)
                {
                    line.Append(fieldSeparator);
                }

                var column = columns[index];
                var rawText = TextWriterHelper.ValueToText(column.GetValue(row), column.TextFormat, column.NumberFormat);
                line.Append(renderField(column, index, rawText, position));
            }

            line.Append(options.NewLine);
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken);
        }

        // 落盘之前查一次：取消落在最后一笔的取值期间时，逐行检查已经没有下一轮可拦
        cancellationToken.ThrowIfCancellationRequested();

        await writer.FlushAsync(cancellationToken);

        reportAggregate();

        // 回传结果之前再查一次：本路径的 StreamWriter 边写边缓冲，此处抛出时字节已经落进流里，
        // 能主张的只有「不交出成功结果」，不承诺零字节
        cancellationToken.ThrowIfCancellationRequested();

        return BuildTextResult(format);
    }

    /// <summary>
    /// 按目标格式给出文字档的结果尾部：扩展名与内容类型
    /// </summary>
    private static ExcelExportResult BuildTextResult(ExcelFormat format)
        => format == ExcelFormat.Csv
            ? ExcelExportResult.Styled(format, ExcelConstants.ExtensionCsv, ExcelConstants.CsvContentType)
            : ExcelExportResult.Styled(format, ExcelConstants.ExtensionTxt, ExcelConstants.PlainTextContentType);

    /// <summary>
    /// 组出固定宽度布局的格级失败：外层点名行位置、表头与列键，抛出的原因留在消息尾部
    /// </summary>
    /// <remarks>
    /// 固定宽度档的失败都发生在「已经交出某一行」之后（值含换行、内容超宽），档到这里已是半成品，
    /// 因此信息必须能定位到格，调用方才谈得上清洗该行或改列宽。本方法只补定位信息，不改变处置。
    /// </remarks>
    private static InvalidOperationException CreateFieldFailure(
        ExcelColumn column,
        string position,
        string reason,
        Exception? innerException = null)
        => new($"固定宽度导出无法完成：{position}的「{column.Header}」列（键 {column.Key}）。{reason}", innerException);

    /// <summary>
    /// 把一格的原文写成该写出的文本，并在此处累计本布局的改写留痕计数
    /// </summary>
    /// <param name="column">本列</param>
    /// <param name="index">本列在列清单中的位置，供按列取设置的布局使用</param>
    /// <param name="rawText">取值已转成的文本（表头行就是表头文案）</param>
    /// <param name="position">行位置标签，用于留痕与异常信息；表头行固定为「表头行」</param>
    /// <returns>可以直接拼进行里的文本</returns>
    /// <remarks>
    /// 表头行与数据行走同一个委托：同一串内容该写出什么，不因它落在表头还是数据而改变。
    /// </remarks>
    private delegate string RenderField(ExcelColumn column, int index, string rawText, string position);

    /// <summary>
    /// 在整档写出并 flush 之后、返回结果之前，落地本布局的聚合 Warning
    /// </summary>
    private delegate void ReportAggregate();

    /// <summary>
    /// 校验固定宽度布局的列设置，返回每列按目标编码成立的字节宽度
    /// </summary>
    /// <remarks>
    /// 本方法只查与内容无关的列级设置（每列有没有宽度、宽度是否为正、补位字符能不能用），值内的换行与逐格超宽
    /// 不在这里判，因为那要等取到行值才知道。校验顺序固定：先列宽，再补位字符，两处都排在写出任何字节之前。
    /// </remarks>
    private static int[] ValidateFixedWidthColumns(IReadOnlyList<ExcelColumn> columns, Encoding encoding)
    {
        var widths = new int[columns.Count];
        var missing = new List<string>();
        var nonPositive = new List<string>();

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];

            if (column.FixedWidth is null)
            {
                missing.Add(column.Key);
                continue;
            }

            // 0 与负数不是「未指定」的另一种写法：未指定是 null，非正数一律按非法取值报出
            if (column.FixedWidth.Value <= 0)
            {
                nonPositive.Add($"{column.Key}={column.FixedWidth.Value}");
                continue;
            }

            widths[index] = column.FixedWidth.Value;
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"固定宽度布局要求每列都设置 {nameof(ExcelColumn.FixedWidth)}（按目标编码的字节数计），以下列未设置：{string.Join("、", missing)}。" +
                $"列特性不带字节宽度，请用代码构造这些列并给出 {nameof(ExcelColumn.FixedWidth)}，或改用 {nameof(ExcelTextLayout.Delimited)} 布局。");
        }

        if (nonPositive.Count > 0)
        {
            throw new InvalidOperationException(
                $"固定宽度布局的列宽必须是正整数字节，以下列非法：{string.Join("、", nonPositive)}。");
        }

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];

            // 补位字符能不能用与内容无关，所以在写第一个字节之前就把每一列问过，不等第一格补位时才炸
            TextWriterHelper.ValidatePadChar(column.PadChar, encoding, $"（列「{column.Header}」，键 {column.Key}）");
        }

        return widths;
    }
}
