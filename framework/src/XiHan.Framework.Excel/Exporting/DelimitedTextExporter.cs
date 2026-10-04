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
/// 序列，可以是惰性游标，导出规模不受内存里的行数限制，因此也没有流式与否的阈值判断。每行写前检查取消令牌。
/// </para>
/// <para>
/// 文字档没有样式概念，因此 <see cref="ExcelSheetSpec"/> 上的工作簿排版项（标题行、冻结、筛选、表头加粗与底色、
/// 边框）与列上的宽度、对齐、换行、数字格式全部忽略且不记日志，返回值一律走
/// <see cref="ExcelExportResult.Styled(ExcelFormat, string, string)"/>，其中「样式已落地」表示没有丢弃任何请求的
/// 样式，不是样式已生效。列宽与数字格式在文字档上的对应物是 <see cref="ExcelColumn.TextFormat"/>。
/// </para>
/// <para>
/// 表头行不做公式注入防护：表头文案由开发者写在列模型上，不是外来数据，加前缀会让表头本身变形。
/// </para>
/// <para>
/// <see cref="ExcelTextQuote.None"/> 下值内的分隔符与换行无法原样写出，本类把它们替换为空格并记一条 Warning
/// 日志，说明是哪一行哪一列被改写，不静默产出坏数据。空格本身作分隔符时该策略在结构上无法成立（值内空格与
/// 分隔符不可区分，替换成空格又是 no-op，写出的档列数直接错位），本类在写出任何字节之前拒绝这一组合。
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
/// <see cref="ExcelTextOptions.EscapeFormulaPrefix"/> 都不参与写出。
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
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不是 <c>Csv</c> 或 <c>Txt</c></exception>
    /// <exception cref="ArgumentException">编码名无法解析，<see cref="ExcelTextQuote.None"/> 与空格分隔符组合，
    /// 或固定宽度布局下某列的补位字符不是单字节</exception>
    /// <exception cref="InvalidOperationException">固定宽度布局下某列未设置
    /// <see cref="ExcelColumn.FixedWidth"/>、列宽不是正整数，或内容超出列宽且
    /// <see cref="ExcelTextOptions.Overflow"/> 为 <see cref="ExcelTextOverflow.Throw"/></exception>
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

        if (options.Quote == ExcelTextQuote.None && delimiter == SpaceDelimiter)
        {
            throw new ArgumentException(
                $"以空格作分隔符时，「不加引号」策略无法成立：值内的空格与分隔符无法区分，" +
                $"既不替换也不加引号会直接产出列数错位的坏档。请改用其他分隔符，或将引号策略设为 {ExcelTextQuote.Minimal}／{ExcelTextQuote.All}。",
                nameof(textOptions));
        }

        var encoding = TextWriterHelper.ResolveEncoding(options.EncodingName);
        var line = new StringBuilder();

        // 公式前缀的改写计数与首个触发点：改数据必须留痕，但逐格记日志会淹掉真正要看的信号，故全份聚合成一条
        var escapedCount = 0;
        var firstEscapedPosition = string.Empty;
        var firstEscapedHeader = string.Empty;
        var firstEscapedKey = string.Empty;

        using var writer = new StreamWriter(output, encoding, leaveOpen: true);

        if (options.IncludeHeader)
        {
            line.Clear();

            for (var index = 0; index < sheet.Columns.Count; index++)
            {
                if (index > 0)
                {
                    line.Append(delimiter);
                }

                var column = sheet.Columns[index];
                line.Append(PrepareField(column, column.Header, HeaderPosition, escapeFormula: false));
            }

            line.Append(options.NewLine);
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken);
        }

        var rowIndex = 0;

        foreach (var row in sheet.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowIndex++;
            line.Clear();
            var position = $"第 {rowIndex} 行";

            for (var index = 0; index < sheet.Columns.Count; index++)
            {
                if (index > 0)
                {
                    line.Append(delimiter);
                }

                var column = sheet.Columns[index];
                var rawText = TextWriterHelper.ValueToText(column.GetValue(row), column.TextFormat, column.NumberFormat);
                line.Append(PrepareField(column, rawText, position, options.EscapeFormulaPrefix));
            }

            line.Append(options.NewLine);
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);

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

        return format == ExcelFormat.Csv
            ? ExcelExportResult.Styled(format, ExcelConstants.ExtensionCsv, ExcelConstants.CsvContentType)
            : ExcelExportResult.Styled(format, ExcelConstants.ExtensionTxt, ExcelConstants.PlainTextContentType);

        string PrepareField(ExcelColumn column, string rawText, string position, bool escapeFormula)
        {
            var guarded = rawText;

            if (escapeFormula && TextWriterHelper.NeedsFormulaEscape(rawText))
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

        var line = new StringBuilder();

        // 截断的丢弃计数与首个触发点：理由同分隔符路径的公式前缀，逐格记日志会淹掉真正要看的信号
        var truncatedCount = 0;
        var firstTruncatedPosition = string.Empty;
        var firstTruncatedHeader = string.Empty;
        var firstTruncatedKey = string.Empty;
        var firstTruncatedWidth = 0;

        using var writer = new StreamWriter(output, encoding, leaveOpen: true);

        if (options.IncludeHeader)
        {
            line.Clear();

            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                line.Append(PadField(column, widths[index], column.Header, HeaderPosition));
            }

            line.Append(options.NewLine);
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken);
        }

        var rowIndex = 0;

        foreach (var row in sheet.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            rowIndex++;
            line.Clear();
            var position = $"第 {rowIndex} 行";

            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                var rawText = TextWriterHelper.ValueToText(column.GetValue(row), column.TextFormat, column.NumberFormat);
                line.Append(PadField(column, widths[index], rawText, position));
            }

            line.Append(options.NewLine);
            await writer.WriteAsync(line.ToString().AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);

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

        return format == ExcelFormat.Csv
            ? ExcelExportResult.Styled(format, ExcelConstants.ExtensionCsv, ExcelConstants.CsvContentType)
            : ExcelExportResult.Styled(format, ExcelConstants.ExtensionTxt, ExcelConstants.PlainTextContentType);

        string PadField(ExcelColumn column, int width, string rawText, string position)
        {
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
                throw new InvalidOperationException(
                    $"固定宽度导出无法完成：{position}的「{column.Header}」列（键 {column.Key}）。{ex.Message}", ex);
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
    /// 校验固定宽度布局的列设置，返回每列按目标编码成立的字节宽度
    /// </summary>
    /// <remarks>
    /// 本方法只看档级结构（每列有没有宽度、宽度是否为正、补位字符是否单字节），逐格的超宽处置不在这里，
    /// 因为那要等取到行值才知道。校验顺序固定：先列宽，再补位字符，两处都排在写出任何字节之前。
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
            TextWriterHelper.EnsureSingleBytePadChar(column.PadChar, encoding, $"（列「{column.Header}」，键 {column.Key}）");
        }

        return widths;
    }
}
