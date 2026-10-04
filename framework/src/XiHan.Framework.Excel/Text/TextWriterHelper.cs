// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Text;

/// <summary>
/// 文字导出共用的编码解析、引号处理与取值转换助手
/// </summary>
/// <remarks>
/// 文字档要求字节输出逐字节可控，因此这些变换全部由框架自己实现，不借第三方 CSV 库；分隔符模式与固定宽度模式
/// 共用同一套规则，避免同一个值在两种布局下走出两套语义。
/// </remarks>
internal static class TextWriterHelper
{
    /// <summary>
    /// 不带 BOM 的 UTF-8 名称
    /// </summary>
    private const string Utf8Name = "utf-8";

    /// <summary>
    /// 不带 BOM 的 UTF-8 别名写法
    /// </summary>
    private const string Utf8AliasName = "utf8";

    /// <summary>
    /// 带 BOM 的 UTF-8 名称
    /// </summary>
    private const string Utf8BomName = "utf-8-bom";

    /// <summary>
    /// 带 BOM 的 UTF-8 别名写法
    /// </summary>
    private const string Utf8BomAliasName = "utf8-bom";

    /// <summary>
    /// 引号字符
    /// </summary>
    private const char QuoteChar = '"';

    /// <summary>
    /// 不可原样写出的字符在免引号策略下的替换字符
    /// </summary>
    private const char FillerChar = ' ';

    /// <summary>
    /// 解析编码名对应的 <see cref="Encoding"/>
    /// </summary>
    /// <param name="encodingName">编码名称或代码页编号，大小写与首尾空白不参与判断</param>
    /// <returns>
    /// <c>"utf-8-bom"</c>／<c>"utf8-bom"</c> 得到会写 BOM 前导字节的 UTF-8；<c>"utf-8"</c>／<c>"utf8"</c> 得到
    /// 不写 BOM 的 UTF-8；其余按 <see cref="Encoding.GetEncoding(string, EncoderFallback, DecoderFallback)"/> 解析。
    /// 三者一律带 <see cref="EncoderExceptionFallback"/> 与 <see cref="DecoderExceptionFallback"/>
    /// </returns>
    /// <remarks>
    /// <para>
    /// BOM 一律由返回的编码自身写出（<see cref="StreamWriter"/> 在流起点写入前导字节），调用方不得再手写一遍。
    /// Big5 等代码页编码依赖 <c>CodePagesEncodingProvider</c>，由 <c>AddXiHanExcel</c> 注册；未注册时这里抛出的
    /// 异常直接点名是哪个编码名解析不了，不做静默降级。
    /// </para>
    /// <para>
    /// 两侧回退一律严格化，不用 .NET 默认的替换回退：<see cref="Encoding.GetEncoding(string)"/> 取到的 Big5 遇到
    /// 该代码页收不下的字符（例如简体字）会安静写出 <c>?</c> 字节，档已损坏却仍向调用方返回成功结果，属于
    /// 「把乱码当正常数据产出」。换成 <see cref="EncoderExceptionFallback"/> 后这类字符在编码器转换时就抛
    /// <see cref="EncoderFallbackException"/>，坏档产不出来。
    /// </para>
    /// <para>
    /// 解码侧同样取 <see cref="DecoderExceptionFallback"/>，是为了让「先按 UTF-8 严格试解、失败再按声明编码重解」
    /// 这种回退结构可行：宽松解码永远不会失败，也就永远察觉不到编码判错了。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">编码名为空或无法解析</exception>
    internal static Encoding ResolveEncoding(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            throw new ArgumentException("输出编码名不能为空或仅含空白字符。", nameof(encodingName));
        }

        var normalized = encodingName.Trim();

        if (IsBomPreset(normalized))
        {
            return StrictUtf8(encoderShouldEmitBom: true);
        }

        if (string.Equals(normalized, Utf8Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Utf8AliasName, StringComparison.OrdinalIgnoreCase))
        {
            return StrictUtf8(encoderShouldEmitBom: false);
        }

        return Encoding.GetEncoding(
            normalized,
            new EncoderExceptionFallback(),
            new DecoderExceptionFallback());
    }

    /// <summary>
    /// 取两侧都是异常回退的 UTF-8，BOM 语义由参数决定
    /// </summary>
    /// <param name="encoderShouldEmitBom">为 <c>true</c> 时前导字节写 BOM（<c>utf-8-bom</c> 预设）</param>
    /// <remarks>
    /// <para>
    /// net10 的 <see cref="UTF8Encoding"/> 没有「构造时一次传入两侧回退」的重载，实例本身还是只读的
    /// （给 <see cref="Encoding.EncoderFallback"/> 赋值会抛 <see cref="InvalidOperationException"/>），
    /// 严格回退只能从 <c>throwOnInvalidBytes</c> 这个入口拿到：它为编码器配 <see cref="EncoderExceptionFallback"/>、
    /// 为解码器配 <see cref="DecoderExceptionFallback"/>，与非 UTF-8 路径用的两个回退一致。
    /// </para>
    /// <para>
    /// 该参数只管回退，不动前导字节：BOM 语义仍由第一个参数决定，<c>utf-8-bom</c> 与 <c>utf-8</c> 的
    /// <see cref="Encoding.GetPreamble"/> 结果与严格化之前完全相同。
    /// </para>
    /// </remarks>
    private static UTF8Encoding StrictUtf8(bool encoderShouldEmitBom)
        => new(encoderShouldEmitBom, throwOnInvalidBytes: true);

    /// <summary>
    /// 判断编码名是否属于「BOM 已包含在编码内」的预设名
    /// </summary>
    /// <param name="encodingName">编码名称，允许为 <c>null</c></param>
    /// <returns>为 <c>true</c> 时 <see cref="ResolveEncoding"/> 返回的编码自带 BOM 前导字节，调用方不能再手写 BOM</returns>
    internal static bool IsBomPreset(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return false;
        }

        var normalized = encodingName.Trim();

        return string.Equals(normalized, Utf8BomName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, Utf8BomAliasName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按引号策略处理单个字段值
    /// </summary>
    /// <param name="value">字段值文本</param>
    /// <param name="delimiter">本份文件的字段分隔符</param>
    /// <param name="quote">引号策略</param>
    /// <returns>可直接写入文件的字段文本</returns>
    /// <remarks>
    /// <para>
    /// <see cref="ExcelTextQuote.Minimal"/> 遵循 RFC 4180：值内含分隔符、引号或换行才加引号，值内的引号翻倍转义；
    /// 换行被引号包住后原样写出，读档方可以原样往返。<see cref="ExcelTextQuote.All"/> 给每个值裹引号，转义规则相同。
    /// </para>
    /// <para>
    /// <see cref="ExcelTextQuote.None"/> 不加引号，此时值内的分隔符与换行会破坏文件结构，无法原样写出，因此把
    /// 它们替换为空格；值内的引号与分隔符无关，保持原样。改写会改动数据，调用方必须为此留下日志。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quote"/> 不是已定义的引号策略</exception>
    internal static string QuoteIfNeeded(string value, char delimiter, ExcelTextQuote quote)
    {
        switch (quote)
        {
            case ExcelTextQuote.Minimal:
                return NeedsQuotes(value, delimiter) ? WrapWithQuotes(value) : value;

            case ExcelTextQuote.All:
                return WrapWithQuotes(value);

            case ExcelTextQuote.None:
                return ReplaceUnquotableChars(value, delimiter);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(quote),
                    quote,
                    $"引号策略「{quote}」不是受支持的取值，可选值为 Minimal、All、None。");
        }
    }

    /// <summary>
    /// 判断值内是否含有「不加引号就写不出去」的字符
    /// </summary>
    /// <param name="value">字段值文本</param>
    /// <param name="delimiter">本份文件的字段分隔符</param>
    /// <returns>值内含分隔符、<c>\r</c> 或 <c>\n</c> 时为 <c>true</c></returns>
    /// <remarks>
    /// <para>
    /// 本判定是 <see cref="ExcelTextQuote.None"/> 是否会改写数据的唯一真源：那三种字符在免引号策略下必然被替换为
    /// 空格，调用方据此记日志，不必用「改写前后的字符串是否相等」反推——改写可能恰好等长，反推会把日志漏掉。
    /// 值内的引号不属于本判定：它与分隔符无关，免引号策略原样保留。
    /// </para>
    /// <para>
    /// 分隔符本身是空格时，<c>"a b"</c> 判为 <c>true</c>：此时替换成空格是 no-op，但值内空格与分隔符根本无法区分，
    /// 写出的档列数照样错位。该组合由导出器在写出任何字节之前直接拒绝。
    /// </para>
    /// </remarks>
    internal static bool ContainsUnquotable(string value, char delimiter)
    {
        foreach (var current in value)
        {
            if (current == delimiter || current is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 给可能被表格软件当公式执行的字段值加单引号前缀
    /// </summary>
    /// <param name="value">字段值文本</param>
    /// <returns>以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 开头时返回加了 <c>'</c> 前缀的值，其余原样返回</returns>
    /// <remarks>
    /// 本方法只做变换，是否套用由调用方按选项决定；空值原样返回，不会凭空写出一个孤立的单引号。
    /// </remarks>
    internal static string EscapeFormula(string value)
        => NeedsFormulaEscape(value) ? string.Concat("'", value) : value;

    /// <summary>
    /// 判断值是否会被公式注入防护改写
    /// </summary>
    /// <param name="value">字段值文本</param>
    /// <returns>以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 开头时为 <c>true</c></returns>
    /// <remarks>
    /// 与 <see cref="EscapeFormula"/> 共用同一条前缀规则，调用方要统计「有多少字段被加了前缀」时用本判定，
    /// 不要自己再抄一遍前缀字符表，也不要靠改写前后的字符串比较反推。
    /// </remarks>
    internal static bool NeedsFormulaEscape(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@';

    /// <summary>
    /// 把单元格值转成文字档使用的文本
    /// </summary>
    /// <param name="value">单元格值，允许为 <c>null</c></param>
    /// <param name="textFormat">.NET 格式串，空或空白视为未给出</param>
    /// <param name="numberFormat">Excel 格式串，本方法不解释其含义</param>
    /// <returns>
    /// <c>null</c> 返回空字串；实现 <see cref="IFormattable"/> 的值按 <paramref name="textFormat"/>（未给出时用默认格式）
    /// 与不变文化输出；其余值返回其 <c>ToString()</c> 结果，<c>ToString()</c> 为 <c>null</c> 时返回空字串
    /// </returns>
    /// <remarks>
    /// <para>
    /// <paramref name="numberFormat"/> 是 Excel 的数字／日期格式串（形如 <c>#,##0.00</c>、<c>yyyy-mm-dd</c>），
    /// 与 .NET 格式串不通用：同一段字符在两边含义不同（Excel 的 <c>mm</c> 是月份，.NET 的 <c>mm</c> 是分钟）。
    /// 把它直接交给 <see cref="IFormattable.ToString(string?, IFormatProvider?)"/> 会产出错值，因此文字路径一律
    /// 不套用 <paramref name="numberFormat"/>，只认 <paramref name="textFormat"/>。
    /// </para>
    /// <para>
    /// 结果是同一份数据在 <c>.xlsx</c> 与 <c>.csv</c>／<c>.txt</c> 上显示可以不同，这是刻意为之：文字档的取值格式由
    /// <paramref name="textFormat"/> 显式声明，数字格式只在 <c>.xlsx</c> 路径生效。要两边一致，就给同一列同时写
    /// 语义对应的 <c>TextFormat</c> 与 <c>NumberFormat</c>。
    /// </para>
    /// <para>
    /// 格式串非法时 <see cref="FormatException"/> 直接抛给调用方，不吞掉异常退回默认输出。
    /// </para>
    /// </remarks>
    internal static string ValueToText(object? value, string? textFormat, string? numberFormat)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is IFormattable formattable)
        {
            var format = string.IsNullOrWhiteSpace(textFormat) ? null : textFormat;

            // numberFormat 有意不参与：Excel 格式串不能当 .NET 格式串用，见方法注释
            return formattable.ToString(format, CultureInfo.InvariantCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// 判断值是否因为含分隔符、引号或换行而必须加引号
    /// </summary>
    private static bool NeedsQuotes(string value, char delimiter)
        => ContainsUnquotable(value, delimiter) || value.Contains(QuoteChar, StringComparison.Ordinal);

    /// <summary>
    /// 用引号包住值，并把值内的引号翻倍
    /// </summary>
    private static string WrapWithQuotes(string value)
        => string.Concat("\"", value.Replace("\"", "\"\""), "\"");

    /// <summary>
    /// 免引号策略下把破坏文件结构的字符替换为空格
    /// </summary>
    /// <remarks>
    /// 分隔符本身是空格时替换是 no-op，跳过即可；该组合的结构性坏档由导出器直接拒绝，见
    /// <c>DelimitedTextExporter</c>。是否发生了替换一律以 <see cref="ContainsUnquotable"/> 为准，不由本方法的
    /// 返回串反推。
    /// </remarks>
    private static string ReplaceUnquotableChars(string value, char delimiter)
    {
        // 先合并 CRLF，避免一个行尾换成两个空格
        var result = value.Replace("\r\n", " ").Replace('\r', FillerChar).Replace('\n', FillerChar);

        return delimiter == FillerChar ? result : result.Replace(delimiter, FillerChar);
    }
}
