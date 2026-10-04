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
/// 分隔符模式的文字档导出器，产出 <c>.csv</c> 与 <c>.txt</c>
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
/// 日志，说明是哪一行哪一列被改写，不静默产出坏数据。
/// </para>
/// </remarks>
/// <param name="logger">本导出器的日志器，用于记录改写数据的决定</param>
public sealed class DelimitedTextExporter(ILogger<DelimitedTextExporter> logger)
{
    /// <summary>
    /// 免引号策略下与列名一起出现在日志里的行位置标签
    /// </summary>
    private const string HeaderPosition = "表头行";

    private readonly ILogger<DelimitedTextExporter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// 把一张表按分隔符布局写成文字档
    /// </summary>
    /// <param name="output">输出流，导出器只写入不关闭，由调用方拥有</param>
    /// <param name="sheet">表规格，列清单的顺序即写出顺序</param>
    /// <param name="format">目标格式，只接受 <see cref="ExcelFormat.Csv"/> 与 <see cref="ExcelFormat.Txt"/></param>
    /// <param name="textOptions">文字档选项，传 <c>null</c> 时使用 <see cref="ExcelTextOptions"/> 的默认值</param>
    /// <param name="cancellationToken">取消令牌，取消时不再写出后续行</param>
    /// <returns>导出结果，含实际写出的扩展名与内容类型</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="sheet"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> 不是 <c>Csv</c> 或 <c>Txt</c></exception>
    /// <exception cref="NotSupportedException"><see cref="ExcelTextLayout.FixedWidth"/> 布局尚未支持</exception>
    /// <exception cref="ArgumentException">编码名无法解析</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 已取消</exception>
    /// <remarks>
    /// <para>
    /// 分隔符取 <see cref="ExcelTextOptions.Delimiter"/>，未指定时按格式取默认值：<c>.csv</c> 用 <c>,</c>，
    /// <c>.txt</c> 用制表符。
    /// </para>
    /// <para>
    /// 格式、布局、取消令牌与编码名的检查全部在写出第一个字节之前完成，这几类非法输入不会留下半份文件。
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

        if (options.Layout == ExcelTextLayout.FixedWidth)
        {
            throw new NotSupportedException("固定宽度导出由 FixedWidth 分支实现。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var delimiter = options.Delimiter ?? (format == ExcelFormat.Csv ? ',' : '\t');
        var encoding = TextWriterHelper.ResolveEncoding(options.EncodingName);
        var line = new StringBuilder();

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

        return format == ExcelFormat.Csv
            ? ExcelExportResult.Styled(format, ExcelConstants.ExtensionCsv, ExcelConstants.CsvContentType)
            : ExcelExportResult.Styled(format, ExcelConstants.ExtensionTxt, ExcelConstants.PlainTextContentType);

        string PrepareField(ExcelColumn column, string rawText, string position, bool escapeFormula)
        {
            var guarded = escapeFormula ? TextWriterHelper.EscapeFormula(rawText) : rawText;
            var field = TextWriterHelper.QuoteIfNeeded(guarded, delimiter, options.Quote);

            if (options.Quote == ExcelTextQuote.None && !string.Equals(field, guarded, StringComparison.Ordinal))
            {
                // 改数据必须留痕：免引号策略下不加引号又原样写出会破坏列数，替换成空格又不能让调用方毫不知情
                _logger.LogWarning(
                    "{Position}的「{Header}」列（键 {Key}）值内含分隔符「{Delimiter}」或换行，" +
                    "不加引号策略无法原样写出，已替换为空格，导出的文字档不能原样往返。",
                    position,
                    column.Header,
                    column.Key,
                    delimiter);
            }

            return field;
        }
    }
}
