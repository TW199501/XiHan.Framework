// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Text;

/// <summary>
/// 文字导出共用的编码解析、引号处理与取值转换助手
/// </summary>
/// <remarks>
/// 这些变换全部由框架自己实现，不依赖第三方 CSV 库；分隔符模式与固定宽度模式共用同一套规则。
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
    /// 引号字符：给字段加引号与免引号策略下的分隔符判定共用这一个字符
    /// </summary>
    internal const char QuoteChar = '"';

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
    /// 异常点名是哪个编码名解析不了。
    /// </para>
    /// <para>
    /// 编码侧取 <see cref="EncoderExceptionFallback"/>：目标编码收不下的字符（例如 Big5 下的简体字）在编码器转换时抛
    /// <see cref="EncoderFallbackException"/>。
    /// </para>
    /// <para>
    /// 解码侧取 <see cref="DecoderExceptionFallback"/>：非法字节在解码时抛 <see cref="DecoderFallbackException"/>。
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
    /// <c>throwOnInvalidBytes: true</c> 为编码器配 <see cref="EncoderExceptionFallback"/>、为解码器配
    /// <see cref="DecoderExceptionFallback"/>，与非 UTF-8 路径显式传入的两个回退一致。
    /// </para>
    /// <para>
    /// 该参数只管回退，不动前导字节：BOM 语义由第一个参数决定。
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
    /// 检查二用的探测文字，按「该编码表示得出」的条件取第一组可用者
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>日本</c>／<c>中文</c>／<c>한글</c> 覆盖 ISO-2022-JP／CN／KR、HZ、UTF-7 等有状态编码；
    /// <c>éà</c> 用于表示不出 CJK 的单字节代码页（windows-1252、iso-8859-1 一类）；
    /// <c>AB</c> 用于纯 ASCII 码页（us-ascii 一类）。某组表示不出时换下一组，不当成拒收理由。
    /// </para>
    /// <para>
    /// 一组都取不到时按「无法证明无状态」拒收。
    /// </para>
    /// </remarks>
    private static readonly string[] FixedWidthStateProbes = ["日本", "中文", "한글", "éà", "AB"];

    /// <summary>
    /// 固定宽度布局的编码守卫：检查该编码能否按字节切列，导入与导出共用这一份判据
    /// </summary>
    /// <param name="encoding">定宽路径用来分行、切列与补位的编码，必须是带严格回退解析出来的那一份</param>
    /// <remarks>
    /// <para>
    /// 对编码当场探测两条性质：
    /// <b>检查一·行尾字节唯一可寻址</b>——<c>\r</c> 与 <c>\n</c> 必须各自编成单字节 <c>0x0D</c>、<c>0x0A</c>
    /// （UTF-16／UTF-32、EBCDIC 不满足）。
    /// <b>检查二·分段编码等于整体编码</b>——同一段文字拆开分别编码再串接，必须等于整体编码
    /// （ISO-2022 家族、HZ、UTF-7 一类有状态编码不满足）。消息点名不过的是哪一条。
    /// </para>
    /// <para>
    /// 检查二要求传入的编码带严格回退，由 <see cref="ResolveEncoding"/> 与 <c>TextEncodingResolver</c> 保证。
    /// </para>
    /// <para>
    /// 调用时机：导入侧在解析出编码之后、建立读取缓冲之前；导出侧在固定宽度布局写出任何字节之前。分隔符布局不调用本守卫。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> 为 <c>null</c></exception>
    /// <exception cref="InvalidOperationException">检查一或检查二不过，或一组可用探测文字都取不到、无法证明无状态</exception>
    internal static void ValidateFixedWidthEncoding(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        RejectUnaddressableLineBreaks(encoding);
        RejectStatefulEncoding(encoding);
    }

    /// <summary>
    /// 检查一：行尾必须各自编成单字节的 <c>0x0D</c> 与 <c>0x0A</c>
    /// </summary>
    /// <remarks>
    /// 与 <c>FixedWidthTextImporter</c> 的字节层分行器一致：只在 <c>0x0D</c>／<c>0x0A</c> 两个字节上切行。
    /// </remarks>
    /// <exception cref="InvalidOperationException">任一行尾编不成对应的那一个字节</exception>
    private static void RejectUnaddressableLineBreaks(Encoding encoding)
    {
        var carriageBytes = TryEncode(encoding, "\r") ?? [];
        var lineFeedBytes = TryEncode(encoding, "\n") ?? [];

        if (IsSingleByte(carriageBytes, (byte)'\r') && IsSingleByte(lineFeedBytes, (byte)'\n'))
        {
            return;
        }

        throw new InvalidOperationException(
            $"定宽路径的编码检查一不过（行尾字节不唯一可寻址）：编码「{encoding.WebName}」（代码页 {encoding.CodePage}）" +
            $"把 \\r 编成 {DescribeBytes(carriageBytes)}、把 \\n 编成 {DescribeBytes(lineFeedBytes)}，" +
            "而定宽档在字节层只认单字节的 0x0D 与 0x0A 来分行。这种编码要么让一个字符里藏得进行尾、把一档切成错位的" +
            "许多行，要么让整档找不到行尾、只交出一行，两种都读得出内容却回报成功。" +
            "请改用行尾字节唯一的编码（utf-8、big5、shift_jis、gb18030、windows-1252 一类的单字节前缀编码），" +
            "或先把档转成那种编码。");
    }

    /// <summary>
    /// 检查二：同一段文字整体编码的结果，必须等于分段编码后串接的结果
    /// </summary>
    /// <remarks>
    /// 探测取 <see cref="FixedWidthStateProbes"/> 里第一组该编码表示得出的文字，表示不出时换下一组。
    /// </remarks>
    /// <exception cref="InvalidOperationException">分段编码与整体编码不等，或一组可用探测文字都取不到</exception>
    private static void RejectStatefulEncoding(Encoding encoding)
    {
        foreach (var probe in FixedWidthStateProbes)
        {
            var wholeBytes = TryEncode(encoding, probe);
            var firstBytes = TryEncode(encoding, probe[..1]);
            var secondBytes = TryEncode(encoding, probe[1..]);

            if (wholeBytes is null || firstBytes is null || secondBytes is null)
            {
                continue;
            }

            byte[] splitBytes = [.. firstBytes, .. secondBytes];

            if (wholeBytes.SequenceEqual(splitBytes))
            {
                return;
            }

            throw new InvalidOperationException(
                $"定宽路径的编码检查二不过（该编码有状态）：编码「{encoding.WebName}」（代码页 {encoding.CodePage}）" +
                $"把「{probe}」整体编成 {wholeBytes.Length} 字节，分段编成 {firstBytes.Length} + {secondBytes.Length} = " +
                $"{splitBytes.Length} 字节。定宽档逐格补位、逐列解码，走的正是分段那条路；两者不相等说明该编码在段之间" +
                "插入状态切换序列（ISO-2022 家族、HZ、UTF-7 一类），按声明列宽写出的字节数与读档方切到的字节位置不一致，" +
                "从错的那一栏起整体错位。请改用无状态编码（utf-8、big5、shift_jis、gb18030 一类），或先把档转成那种编码。");
        }

        throw new InvalidOperationException(
            $"定宽路径的编码检查二无法证明：编码「{encoding.WebName}」（代码页 {encoding.CodePage}）连一组探测文字" +
            $"（{string.Join("／", FixedWidthStateProbes)}）都表示不出，框架无法验证它分段编码与整体编码是否等价。" +
            "定宽档依赖这条性质，验不了就拒收而不是放行；请改用 utf-8、big5、shift_jis、gb18030 一类常用编码。");
    }

    /// <summary>
    /// 用给定编码编出一段文字，编不出来时交回 <c>null</c> 而不是把探测本身变成失败
    /// </summary>
    /// <remarks>
    /// 严格回退编码遇到字符集外的文字抛 <see cref="EncoderFallbackException"/>，本方法捕获后返回 <c>null</c>。
    /// </remarks>
    private static byte[]? TryEncode(Encoding encoding, string text)
    {
        try
        {
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断编出的字节是否恰好是期望的那一个字节
    /// </summary>
    private static bool IsSingleByte(byte[] bytes, byte expected)
        => bytes.Length == 1 && bytes[0] == expected;

    /// <summary>
    /// 把探测到的字节序列写成消息里可核对的十六进制
    /// </summary>
    private static string DescribeBytes(byte[] bytes)
        => bytes.Length == 0
            ? "无法表示（严格编码器直接拒绝）"
            : $"{string.Join(" ", bytes.Select(static b => $"0x{b:X2}"))}（{bytes.Length} 字节）";

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
    /// <see cref="ExcelTextQuote.None"/> 不加引号，值内的分隔符与换行替换为空格，值内的引号保持原样。
    /// 改写会改动数据，调用方须为此记日志。
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
    /// 本判定是 <see cref="ExcelTextQuote.None"/> 是否会改写数据的依据：那三种字符在免引号策略下替换为空格，
    /// 调用方据此记日志。值内的引号不属于本判定。
    /// </para>
    /// <para>
    /// 分隔符本身是空格时，<c>"a b"</c> 判为 <c>true</c>；该组合由导出器在写出任何字节之前直接拒绝。
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
    /// <returns>以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符（<c>\t</c>）或回车（<c>\r</c>）开头时返回加了 <c>'</c> 前缀的值，其余原样返回</returns>
    /// <remarks>
    /// 本方法只做变换，是否套用由调用方按选项决定；空值原样返回，不会凭空写出一个孤立的单引号。
    /// </remarks>
    internal static string EscapeFormula(string value)
        => NeedsFormulaEscape(value) ? string.Concat("'", value) : value;

    /// <summary>
    /// 判断值是否会被公式注入防护改写
    /// </summary>
    /// <param name="value">字段值文本</param>
    /// <returns>以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符（<c>\t</c>）或回车（<c>\r</c>）开头时为 <c>true</c></returns>
    /// <remarks>
    /// 与 <see cref="EscapeFormula"/> 共用同一条前缀规则，供调用方统计被加前缀的字段数。
    /// </remarks>
    internal static bool NeedsFormulaEscape(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

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
    /// 与 .NET 格式串不通用，文字路径一律不套用它，只认 <paramref name="textFormat"/>。
    /// </para>
    /// <para>
    /// 同一份数据在 <c>.xlsx</c> 与 <c>.csv</c>／<c>.txt</c> 上的显示可以不同；要两边一致，就给同一列同时写
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

            // numberFormat 不参与，见方法注释
            return formattable.ToString(format, CultureInfo.InvariantCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// 把字段文本补齐到指定的字节宽度，超宽时按 <paramref name="overflow"/> 抛出或截断
    /// </summary>
    /// <param name="value">字段文本</param>
    /// <param name="widthBytes">列宽，以目标编码的字节数计</param>
    /// <param name="encoding">写出用的编码，字节宽度按它计算</param>
    /// <param name="padding">补位方向：<see cref="ExcelTextPadding.Right"/> 内容靠左、右侧补字符，<see cref="ExcelTextPadding.Left"/> 反之</param>
    /// <param name="padChar">补位字符，必须在 <paramref name="encoding"/> 下恰好占 1 字节，且不能是换行符</param>
    /// <param name="overflow">内容字节数超出列宽时的处置</param>
    /// <returns>在 <paramref name="encoding"/> 下恰好占 <paramref name="widthBytes"/> 字节的文本</returns>
    /// <remarks>
    /// <para>
    /// 宽度的单位是字节而不是字符：差额取 <see cref="Encoding.GetByteCount(string)"/> 的结果，补位字符必须是单字节，
    /// 使「差额字节数」与「补位字符个数」相等。
    /// </para>
    /// <para>
    /// 截断按字素取舍：逐个字素累计字节数，遇到使累计超过列宽的字素即停，不切开多字节字符或代理对。
    /// 截断结果可能比列宽少 1～3 字节，差额由补位字符填满，整格宽度不变。
    /// </para>
    /// <para>
    /// 内容在目标编码下收不下时抛 <see cref="EncoderFallbackException"/>，与分隔符路径一致。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 或 <paramref name="encoding"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException">列宽为负数，或补位方向、超宽策略取未定义的枚举值</exception>
    /// <exception cref="ArgumentException"><paramref name="padChar"/> 是换行符，在目标编码下不是单字节，或根本无法表示</exception>
    /// <exception cref="EncoderFallbackException"><paramref name="value"/> 在目标编码下无法表示</exception>
    /// <exception cref="InvalidOperationException">内容超出列宽且 <paramref name="overflow"/> 为 <see cref="ExcelTextOverflow.Throw"/></exception>
    internal static string PadToWidth(
        string value,
        int widthBytes,
        Encoding encoding,
        ExcelTextPadding padding,
        char padChar,
        ExcelTextOverflow overflow)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);

        if (widthBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthBytes), widthBytes, "固定宽度列宽以字节计，不能是负数。");
        }

        ValidatePadChar(padChar, encoding, null);

        var bytes = encoding.GetByteCount(value);

        if (bytes > widthBytes)
        {
            switch (overflow)
            {
                case ExcelTextOverflow.Throw:
                    throw new InvalidOperationException(
                        $"固定宽度内容超出列宽：实际 {bytes} 字节，列宽 {widthBytes} 字节。" +
                        $"请加大该列的 {nameof(ExcelColumn.FixedWidth)}，或将超宽策略 Overflow 设为 {ExcelTextOverflow.Truncate}（超出部分不写出）。");

                case ExcelTextOverflow.Truncate:
                    value = TruncateToWidth(value, widthBytes, encoding);
                    bytes = encoding.GetByteCount(value);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(overflow),
                        overflow,
                        $"超宽策略「{overflow}」不是受支持的取值，可选值为 {ExcelTextOverflow.Throw} 与 {ExcelTextOverflow.Truncate}。");
            }
        }

        // 差额按字节算，补位字符已验成单字节，因此「补几个」与「补几字节」是同一个数
        var pad = new string(padChar, widthBytes - bytes);

        return padding switch
        {
            ExcelTextPadding.Right => string.Concat(value, pad),
            ExcelTextPadding.Left => string.Concat(pad, value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(padding),
                padding,
                $"补位方向「{padding}」不是受支持的取值，可选值为 {ExcelTextPadding.Left} 与 {ExcelTextPadding.Right}。")
        };
    }

    /// <summary>
    /// 校验补位字符可用：在目标编码下恰好占 1 字节，且不是换行符
    /// </summary>
    /// <param name="padChar">补位字符</param>
    /// <param name="encoding">写出用的编码</param>
    /// <param name="location">用于定位该字符的说明（形如「（列 X，键 Y）」），由导出器传入；直接调用时可传 <c>null</c></param>
    /// <remarks>
    /// 导出器在写出任何字节之前按列调用本方法，<see cref="PadToWidth"/> 自己也在入口调用一次，两处共用同一条规则与
    /// 同一个异常类型。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException">补位字符是换行符，或在目标编码下不是单字节、根本无法表示</exception>
    internal static void ValidatePadChar(char padChar, Encoding encoding, string? location)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        // 换行符排在字节数判定之前单独检查
        if (padChar is '\r' or '\n')
        {
            throw new ArgumentException(
                $"固定宽度补位字符不能是换行符（\\r 或 \\n）：整格补位会凭空写出行尾，一档会被读成两档。{location}",
                nameof(padChar));
        }

        var text = padChar.ToString();
        var description = $"固定宽度补位字符「{padChar}」(U+{(int)padChar:X4})";

        int padBytes;

        try
        {
            // 取实际字节数，不可映射的补位字符在这里判出
            padBytes = encoding.GetBytes(text).Length;
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                $"{description} 无法用编码「{encoding.WebName}」表示：补位字符必须是单字节字符。{location}",
                nameof(padChar));
        }

        if (padBytes != 1)
        {
            throw new ArgumentException(
                $"{description} 在编码「{encoding.WebName}」下占 {padBytes} 字节：补位字符必须是单字节字符，" +
                $"否则按字节差额补位会超出列宽。{location}",
                nameof(padChar));
        }
    }

    /// <summary>
    /// 判断值内是否含行分隔符（<c>\r</c> 或 <c>\n</c>）
    /// </summary>
    /// <param name="value">字段值文本</param>
    /// <returns>含任一行分隔符时为 <c>true</c></returns>
    /// <remarks>
    /// 与 <see cref="ContainsUnquotable"/> 不同，本条只判有没有换行，不涉及分隔符，供固定宽度布局使用。
    /// </remarks>
    internal static bool ContainsLineBreak(string value)
    {
        foreach (var current in value)
        {
            if (current is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 按字素取舍，取到不超过 <paramref name="widthBytes"/> 字节的最长前缀
    /// </summary>
    /// <param name="value">字段文本</param>
    /// <param name="widthBytes">列宽（字节）</param>
    /// <param name="encoding">字节宽度按它计算</param>
    /// <remarks>
    /// 字素是取舍的最小单位：代理对与「基字 + 组合符」整体保留或整体丢弃，因此结果可能短于列宽，差额由补位字符填满。
    /// </remarks>
    private static string TruncateToWidth(string value, int widthBytes, Encoding encoding)
    {
        var builder = new StringBuilder();
        var taken = 0;

        // 字素起点表：代理对与「基字 + 组合符」都只算一个字素的起点，取舍以它为最小单位
        var starts = StringInfo.ParseCombiningCharacters(value);

        for (var index = 0; index < starts.Length; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Length ? starts[index + 1] : value.Length;
            var element = value.Substring(start, end - start);
            var elementBytes = encoding.GetByteCount(element);

            // 该字素放不下就停在这里，不做字节级切断
            if (taken + elementBytes > widthBytes)
            {
                break;
            }

            builder.Append(element);
            taken += elementBytes;
        }

        return builder.ToString();
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
    /// 分隔符本身是空格时跳过分隔符替换；该组合由 <c>DelimitedTextExporter</c> 直接拒绝。
    /// 是否发生了替换以 <see cref="ContainsUnquotable"/> 为准。
    /// </remarks>
    private static string ReplaceUnquotableChars(string value, char delimiter)
    {
        // 先合并 CRLF，避免一个行尾换成两个空格
        var result = value.Replace("\r\n", " ").Replace('\r', FillerChar).Replace('\n', FillerChar);

        return delimiter == FillerChar ? result : result.Replace(delimiter, FillerChar);
    }
}
