// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 工作簿可写性守卫，两条 xlsx 写出路径共用的表名判据与单元格取值域判据
/// </summary>
/// <remarks>
/// <para>
/// 表名约束：长度不超过 31 个字符（按 UTF-16 代码单元，代理对算两个）、不含工作簿不接受的字符、
/// 不以单引号开头或结尾；多表清单里还要求名字互不重复，判重不区分大小写。被拒的名字一律抛出，不改名。
/// </para>
/// <para>
/// 单元格取值约束：日期不早于 <see cref="EarliestDate"/>（<see cref="DateTime"/>、<see cref="DateOnly"/> 与
/// <see cref="DateTimeOffset"/> 同判）；浮点数不得为 <c>NaN</c> 或 <c>±∞</c>；
/// <see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、<see cref="double"/>、<see cref="float"/>
/// 的有效数字不多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位；
/// <see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/> 的绝对值不超过 <see cref="MaxExactIntegerMagnitude"/>；
/// 字串不长于 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符。越界的值一律抛出，不改写。
/// 两条 xlsx 写出路径共用这些判据。
/// </para>
/// <para>
/// 模板路径通过 <see cref="DescribeTemplateUnwritable"/> 额外拒绝 XML 1.0 非法 C0 字符。
/// 两条 xlsx 导出路径支持 OOXML 转义，不受该限制。
/// </para>
/// </remarks>
internal static class ExcelWorkbookWriteGuard
{
    /// <summary>
    /// 本组件接受的最早日期，早于它的 <see cref="DateTime"/>、<see cref="DateOnly"/> 与
    /// <see cref="DateTimeOffset"/> 在两条 xlsx 写出路径一律拒写
    /// </summary>
    /// <remarks>
    /// 取 xlsx 1900 日期系统的起点当日。
    /// </remarks>
    internal static readonly DateTime EarliestDate = new(1900, 1, 1);

    /// <summary>
    /// xlsx 数值格能逐个表示的整数上限，即 2^53；<see cref="long"/>、<see cref="ulong"/> 与 <see cref="decimal"/>
    /// 的绝对值超过它时，落进数值格的不再是呼叫端给的那个数
    /// </summary>
    /// <remarks>
    /// 数值格在档里是双精度数，绝对值超过 2^53 后整数不再逐个可表示。<see cref="double"/> 与 <see cref="float"/>
    /// 不受此上限约束。
    /// </remarks>
    private const long MaxExactIntegerMagnitude = 9007199254740992L;

    /// <summary>
    /// 工作表名的长度上限，按 UTF-16 代码单元计（代理对占两个），与工作簿的判据同一量纲
    /// </summary>
    private const int MaximumSheetNameLength = 31;

    /// <summary>
    /// 写进消息的表名长度上限
    /// </summary>
    private const int MaximumNameInMessage = 40;

    /// <summary>
    /// 工作表名不接受的字符：工作簿在名字上自己拒收的那几个，加 <c>U+0000</c> 与 <c>U+0003</c>
    /// </summary>
    private static readonly char[] InvalidSheetNameCharacters = [':', '\\', '/', '?', '*', '[', ']', '\0', '\u0003'];

    /// <summary>
    /// 检查一张表的表名能否落进工作簿：长度、非法字符与首尾单引号
    /// </summary>
    /// <param name="sheetName">要检查的表名</param>
    /// <param name="position">表在清单里的位置，用于消息；单表路径固定为 1</param>
    /// <remarks>
    /// <para>
    /// 长度按 <see cref="string.Length"/>（UTF-16 代码单元）计，代理对占两个，上限 31 个字符；不接受的字符是
    /// <c>: \ / ? * [ ]</c> 加 <c>U+0000</c> 与 <c>U+0003</c>；首尾单引号被拒，中间的撇号允许。
    /// 表名为空或纯空白由表规格的赋值守卫拦下，这里不重复判。
    /// </para>
    /// <para>
    /// 此判据未覆盖全部表名控制字符。部分非法 C0 字符在全量路径由库抛出异常，流式路径则保留转义文本；
    /// 制表符、换行与回车在全量路径原样保留，流式路径转为空格。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">表名超过 31 个字符、含工作簿不接受的字符，或以单引号开头／结尾，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c></exception>
    internal static void ValidateSheetName(string sheetName, int position)
    {
        if (sheetName.Length > MaximumSheetNameLength)
        {
            throw new ArgumentException(
                $"xlsx 导出无法完成：第 {position} 张表的表名「{TrimForMessage(sheetName)}」有 {sheetName.Length} 个字符，" +
                $"超过工作表名的上限 {MaximumSheetNameLength} 个（按 UTF-16 代码单元计，一个代理对占两个）。" +
                "工作簿不会截断它，只会拒掉，请自己缩短或分表。",
                nameof(ExcelSheetSpec.SheetName));
        }

        var invalidIndex = sheetName.IndexOfAny(InvalidSheetNameCharacters);

        if (invalidIndex >= 0)
        {
            throw new ArgumentException(
                $"xlsx 导出无法完成：第 {position} 张表的表名「{TrimForMessage(sheetName)}」在第 {invalidIndex + 1} 个字符处" +
                $"含有工作表名不接受的字符「{DescribeCharacter(sheetName[invalidIndex])}」" +
                $"（不接受的字符为 : \\ / ? * [ ] 与 U+0000、U+0003）。请改名字，写出侧不代为替换或删字符。",
                nameof(ExcelSheetSpec.SheetName));
        }

        if (sheetName.Length > 0 && (sheetName[0] == '\'' || sheetName[^1] == '\''))
        {
            throw new ArgumentException(
                $"xlsx 导出无法完成：第 {position} 张表的表名「{TrimForMessage(sheetName)}」以单引号开头或结尾，" +
                "工作簿不接受这种名字。请把撇号挪到名字中间或去掉。",
                nameof(ExcelSheetSpec.SheetName));
        }
    }

    /// <summary>
    /// 逐张检查清单里的表名可用且互不重名
    /// </summary>
    /// <param name="sheets">表清单，调用方已确认清单本身非空</param>
    /// <remarks>
    /// 判重不区分大小写。清单里的 <c>null</c> 项点名第几项抛出。
    /// </remarks>
    /// <exception cref="ArgumentNullException">清单里有 <c>null</c> 项，<see cref="ArgumentException.ParamName"/> 为 <c>sheets</c></exception>
    /// <exception cref="ArgumentException">某张表的表名不可用，或与清单里更早那张重名，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>SheetName</c></exception>
    internal static void ValidateSheetNames(IReadOnlyList<ExcelSheetSpec> sheets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < sheets.Count; index++)
        {
            var position = index + 1;
            var sheet = sheets[index];

            if (sheet is null)
            {
                throw new ArgumentNullException(
                    nameof(sheets),
                    $"第 {position} 项是 null：清单里的每一项都必须是一张表的规格。" +
                    "空项既没有表名也没有列，写不出任何东西，也不会被当成「跳过这张表」。");
            }

            var sheetName = sheet.SheetName;

            ValidateSheetName(sheetName, position);

            if (!seen.Add(sheetName))
            {
                throw new ArgumentException(
                    $"xlsx 导出无法完成：第 {position} 张表的表名「{TrimForMessage(sheetName)}」与清单里更早那张重名。" +
                    "工作表名不区分大小写，判重按同一口径进行；工作簿遇到重名会直接拒掉而不是自动加后缀改名，" +
                    "所以这里先拦：请给出互不重复的表名。",
                    nameof(ExcelSheetSpec.SheetName));
            }
        }
    }

    /// <summary>
    /// 判断这一格取值能不能落进工作簿，交回不可写的成因；可写时交回 <c>null</c>
    /// </summary>
    /// <param name="value">刚取出的行值，<c>null</c> 表示空格，直接放过</param>
    /// <returns>不可写的原因文字，可写（含 <c>null</c> 值）时为 <c>null</c></returns>
    /// <remarks>
    /// 判定按型别分派：<c>decimal</c>、<see cref="TimeSpan"/> 与 <see cref="Guid"/> 不在这里拒。
    /// 日期下限对三种日期型别同判：<see cref="DateOnly"/> 取当日零点，<see cref="DateTimeOffset"/> 取
    /// <see cref="DateTimeOffset.DateTime"/>、不看偏移量。数值位数由 <see cref="NumericPrecisionReason"/> 判定，
    /// 整数大小由 <see cref="NumericMagnitudeReason"/> 判定，两者都命中时先报位数。
    /// 成因只写值本身，行位置与列名由调用方在抛出时拼进去。
    /// </remarks>
    internal static string? DescribeUnwritable(object? value)
        => value switch
        {
            DateTime date when date < EarliestDate =>
                DateCellMessage($"{date:yyyy-MM-dd HH:mm:ss}"),

            DateOnly only when only.ToDateTime(TimeOnly.MinValue) < EarliestDate =>
                DateCellMessage($"{only:yyyy-MM-dd} 00:00:00"),

            DateTimeOffset offset when offset.DateTime < EarliestDate =>
                DateCellMessage($"{offset.DateTime:yyyy-MM-dd HH:mm:ss}"),

            double number when !double.IsFinite(number) => NotFiniteNumberMessage(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),

            float number when !float.IsFinite(number) => NotFiniteNumberMessage(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),

            string text when text.Length > ExcelConstants.MaxCellTextLength =>
                $"字串有 {text.Length} 个字符，超过单元格的上限 {ExcelConstants.MaxCellTextLength} 个字符：" +
                "工作簿装不下它，写出只会得到一份读回不了的档。这里不截断——截断会丢弃数据，" +
                "请自己决定分段或改写字段后再导出。",

            _ when NumericPrecisionReason(value) is { } precision => precision,

            _ when NumericMagnitudeReason(value) is { } magnitude => magnitude,

            _ => null
        };

    /// <summary>
    /// 模板渲染路径的取值域判据：共用那份取值域判据之外，再加一道 XML 1.0 非法字符
    /// </summary>
    /// <param name="value">刚走访到的值，<c>null</c> 直接放过</param>
    /// <returns>不可写的原因文字，可写（含 <c>null</c> 值）时为 <c>null</c></returns>
    /// <remarks>
    /// 两条 xlsx 导出路径不调用本方法。制表符、换行与回车是 XML 1.0 合法字符，不在此处拒绝。
    /// </remarks>
    internal static string? DescribeTemplateUnwritable(object? value)
        => value is string text && DescribeXmlIllegalCharacter(text) is { } illegal
            ? illegal
            : DescribeUnwritable(value);

    /// <summary>
    /// 找出字串里第一个 XML 1.0 不允许出现在文本内容里的字符，交回成因文字；整串都合法时交回 <c>null</c>
    /// </summary>
    /// <param name="text">刚走访到的字串值</param>
    /// <remarks>
    /// 仅检查 C0 中的 29 个非法字符：U+0000–U+0008、U+000B、U+000C、U+000E–U+001F。
    /// </remarks>
    private static string? DescribeXmlIllegalCharacter(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (IsXmlIllegalCharacter(text[index]))
            {
                return XmlIllegalCharacterMessage(index, text[index]);
            }
        }

        return null;
    }

    /// <summary>
    /// 判断一个字符是不是 XML 1.0 不允许出现在文本内容里的那一类
    /// </summary>
    /// <param name="character">要判断的字符</param>
    /// <remarks>
    /// 制表符、换行与回车属于 XML 1.0 合法字符，不在其中。
    /// </remarks>
    private static bool IsXmlIllegalCharacter(char character)
        => character <= '\u0008'
            || character is '\u000B' or '\u000C'
            || character is >= '\u000E' and <= '\u001F';

    /// <summary>
    /// XML 1.0 非法字符的成因文字：点名是第几个字符与它的码点
    /// </summary>
    /// <param name="index">该字符在字串里的 0 起下标</param>
    /// <param name="character">命中的字符</param>
    private static string XmlIllegalCharacterMessage(int index, char character)
        => $"字串在第 {index + 1} 个字符处含有 U+{(int)character:X4}：XML 1.0 不允许该字符。" +
            "模板渲染拒绝写入，请移除该字符或替换为可见字符后重试。";


    /// <summary>
    /// 早于日期下限的成因文字
    /// </summary>
    /// <param name="literal">要写进消息的日期文本，按真正要落进格里的那个钟表时刻排成不变文化形式</param>
    private static string DateCellMessage(string literal)
        => $"日期「{literal}」早于本组件接受的最早日期 {EarliestDate:yyyy-MM-dd}。" +
            "本框架只接受不早于该日的日期，更早的值不依赖写出库与表格软件各自的宽容度，" +
            $"两条 xlsx 写出路径一律拒写。请给出不早于 {EarliestDate:yyyy-MM-dd} 的日期，或让该列取成文本。";

    /// <summary>
    /// 非有限数值的成因文字
    /// </summary>
    private static string NotFiniteNumberMessage(string literal)
        => $"数值「{literal}」不是有限数：工作簿的数值格只有有限十进制数，NaN 与无穷大都没有对应形态。" +
            "请把该列取成文本并自己决定写出什么，或先滤掉这类值。";

    /// <summary>
    /// 数值有效数字超出承诺上限时的成因文字，在承诺内时交回 <c>null</c>
    /// </summary>
    /// <param name="value">刚取出的行值。这五类型之外的取值（含 <c>null</c>）直接交回 <c>null</c></param>
    /// <remarks>
    /// 计数对象是取值的不变文化文本形态：<c>long</c>、<c>ulong</c> 与 <c>decimal</c> 取其数字本身，<c>double</c>
    /// 取能原样读回的最短形式，<see cref="float"/> 先展开成 <see cref="double"/> 再取。小数点、正负号与指数不参与计数，
    /// 前导零与整数末尾的零不计入。
    /// </remarks>
    private static string? NumericPrecisionReason(object? value)
    {
        if (value is not long and not ulong and not decimal and not double and not float)
        {
            return null;
        }

        var literal = value is float single
            ? ((double)single).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        var digits = CountSignificantDigits(literal);

        if (digits <= ExcelConstants.MaxExactNumericSignificantDigits)
        {
            return null;
        }

        return $"数值「{literal}」有 {digits} 位有效数字，多于本组件对 xlsx 数值格承诺的 " +
            $"{ExcelConstants.MaxExactNumericSignificantDigits} 位上限。两条 xlsx 写出路径对这类取值一律拒写：" +
            "不改写成较短的数、不降级成文本格，也不按走哪条路径交出两个不同的结果。" +
            "需要完整精度，请由呼叫端把该值转成字符串栏位。";
    }

    /// <summary>
    /// 整数取值的绝对值超出数值格能逐个表示的范围时的成因文字，在范围内时交回 <c>null</c>
    /// </summary>
    /// <param name="value">刚取出的行值。<see cref="long"/>、<see cref="ulong"/> 与 <see cref="decimal"/>
    /// 之外的取值（含 <c>null</c>）直接交回 <c>null</c></param>
    /// <remarks>
    /// 按绝对值是否超过 <see cref="MaxExactIntegerMagnitude"/> 判定；<see cref="double"/> 与 <see cref="float"/> 不在此判定内。
    /// </remarks>
    private static string? NumericMagnitudeReason(object? value)
    {
        var outside = value switch
        {
            long number => number > MaxExactIntegerMagnitude || number < -MaxExactIntegerMagnitude,
            ulong number => number > (ulong)MaxExactIntegerMagnitude,
            decimal number => number > MaxExactIntegerMagnitude || number < -MaxExactIntegerMagnitude,
            _ => false
        };

        if (!outside)
        {
            return null;
        }

        var literal = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        return $"数值「{literal}」的绝对值超过 xlsx 数值格能逐个表示的整数上限 {MaxExactIntegerMagnitude}（2 的 53 次方）。" +
            "两条 xlsx 写出路径对这一段的 long、ulong 与 decimal 一律拒写：不挪到邻近的可表示值、不降级成文本格，" +
            "也不按走哪条路径交出两个不同的结果。double 与 float 不受这一条约束。" +
            "需要完整精度，请由呼叫端把该值转成字符串栏位。";
    }

    /// <summary>
    /// 数一段数字文本里的有效数字位数：取第一个非零数字到最后一个非零数字之间的数字个数
    /// </summary>
    /// <param name="text">数值的文本形态，可含正负号、小数点与指数</param>
    /// <remarks>
    /// 指数部分（<c>E</c>／<c>e</c> 之后）不参与计数；全零（含 <c>0</c> 与 <c>0.0000</c>）算 1 位。
    /// </remarks>
    private static int CountSignificantDigits(string text)
    {
        var exponent = text.IndexOfAny(['E', 'e']);
        var mantissa = exponent >= 0 ? text[..exponent] : text;

        var firstSignificant = -1;
        var lastSignificant = -1;
        var position = 0;

        foreach (var character in mantissa)
        {
            if (!char.IsAsciiDigit(character))
            {
                continue;
            }

            if (character != '0')
            {
                if (firstSignificant < 0)
                {
                    firstSignificant = position;
                }

                lastSignificant = position;
            }

            position++;
        }

        return firstSignificant < 0 ? 1 : lastSignificant - firstSignificant + 1;
    }

    /// <summary>
    /// 确认这一格取值能落进工作簿，不能则抛出点名行列的框架异常
    /// </summary>
    /// <param name="value">刚取出的行值，<c>null</c> 表示空格，直接放过</param>
    /// <param name="column">本列，用于消息里的表头文案与列键</param>
    /// <param name="position">行位置标签，形如「第 3 行」</param>
    /// <exception cref="InvalidOperationException">取值是早于 <see cref="EarliestDate"/> 的 <see cref="DateTime"/>、
    /// <see cref="DateOnly"/> 或 <see cref="DateTimeOffset"/>、非有限的浮点数、
    /// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值、
    /// 绝对值超过 <see cref="MaxExactIntegerMagnitude"/> 的 <see cref="long"/>／<see cref="ulong"/>／<see cref="decimal"/>，
    /// 或长于 <see cref="ExcelConstants.MaxCellTextLength"/> 的字符串；消息点名行位置、表头与列键并给出成因</exception>
    internal static void EnsureWritable(object? value, ExcelColumn column, string position)
    {
        if (DescribeUnwritable(value) is { } reason)
        {
            throw CreateFailure(column, position, reason);
        }
    }

    /// <summary>
    /// 组出格级失败：外层点名行位置、表头与列键，抛出的原因留在消息尾部
    /// </summary>
    /// <param name="column">出事的列</param>
    /// <param name="position">行位置标签，形如「第 3 行」</param>
    /// <param name="reason">要写在消息尾部的原因</param>
    /// <param name="innerException">库抛出的原始异常，原样作为内部异常</param>
    internal static InvalidOperationException CreateFailure(
        ExcelColumn column,
        string position,
        string reason,
        Exception? innerException = null)
        => new($"xlsx 导出无法完成：{position}的「{column.Header}」列（键 {column.Key}）。{reason}", innerException);

    /// <summary>
    /// 把过长的名字截短后再写进异常消息
    /// </summary>
    private static string TrimForMessage(string sheetName)
        => sheetName.Length <= MaximumNameInMessage ? sheetName : sheetName[..MaximumNameInMessage] + "…";

    /// <summary>
    /// 描述一个字符，让控制字符在消息里也可读
    /// </summary>
    private static string DescribeCharacter(char character)
        => char.IsControl(character)
            ? $"U+{(int)character:X4}（控制字符，消息里显示为空格）"
            : character.ToString();
}
