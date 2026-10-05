// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 工作簿可写性守卫，两条 xlsx 写出路径共用的唯一一份表名判据与单元格取值域判据
/// </summary>
/// <remarks>
/// <para>
/// 工作簿对表名有硬约束：长度不超过 31 个字符（按 UTF-16 代码单元，代理对算两个）、不含它不接受的字符、
/// 不以单引号开头或结尾；多表清单里还要求名字互不重复，判重不区分大小写——只有大小写不同的两个名字
/// 工作簿同样拒收。这套判据逐条对齐工作簿的实际约束，既不误杀它肯收的名字，也不放过它拒绝的名字，
/// 且不替调用方改名：被拒的名字一律抛出，不做去空格、截断、加后缀或转义这类静默兜底。
/// </para>
/// <para>
/// 单元格取值同样有硬界：<see cref="DateTime"/> 走 1900 日期系统，早于 <see cref="EarliestDate"/> 的日期
/// 会被夹到纪元时刻而变成另一个日期；数值格只有有限十进制数，<c>NaN</c> 与 <c>±∞</c> 没有对应形态；
/// <see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、<see cref="double"/>、<see cref="float"/>
/// 的有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位时，本组件不再承诺这一格交回
/// 呼叫端给的那个数；字串格的上限是 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符。越界的值一律抛出而不改写——把
/// <c>NaN</c> 写成 <c>"NaN"</c> 会让读回的数值列多出字串，把 <c>∞</c> 夹成最大有限数是凭空造数，把早于纪元的
/// 日期夹到纪元时刻会交回另一个日期，把多于承诺位数的数值照落会交出被舍短的另一份数，截断超长字串会丢弃数据，
/// 五种都是「交出看不出问题的坏档」。
/// </para>
/// <para>
/// 这套判据放在一处是因为两条 xlsx 写出路径共用同一把尺，而不是为了把两边判成同一个样子：排版路径会把
/// 越界值与坏名字交给工作簿去拒，流式路径却会把它们直接落进档里——日期落进装不下它的格子、<c>NaN</c> 与
/// 超长字串写出回读不了的档、多于承诺位数的数值被两条路径各自舍成不同的较短形式、名字里的控制字符被转义成
/// 另一个名字。分派器按行数把同一份规格送到其中一条路径，
/// 「装不下」的取值集合不该由走哪条决定，因此这里只留一份，两边都调它，不在各自的路径里各写一遍。
/// 数值位数这一道尤其要只有一份：它判的是「本组件承诺交回呼叫端给的那个数」，而这份承诺只能按取值本身说，
/// 不能按某个写出库恰好舍到第几位说。
/// </para>
/// <para>
/// 只有日期格的下限按各条路径实际的落格方式判：<see cref="DateTime"/> 两条路径都落日期格，故由本文件的
/// 共用判定管；<see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/> 在流式路径落日期格、在全量路径落文本格
/// （值原样留在文字里），因此只在流式路径拒，见 <see cref="DescribeUnwritableInStream"/>。
/// 同一个值落成不同格位而值不被改写的那些型别差异（<see cref="byte"/>、<see cref="TimeOnly"/> 等）
/// 不在本文件的范围内，由两条路径各自的文档说明。
/// </para>
/// </remarks>
internal static class ExcelWorkbookWriteGuard
{
    /// <summary>
    /// xlsx 的 1900 日期系统能表示的最早时刻，早于它的日期会被夹到这一时刻并改变数据
    /// </summary>
    internal static readonly DateTime EarliestDate = new(1899, 12, 30);

    /// <summary>
    /// 工作表名的长度上限，按 UTF-16 代码单元计（代理对占两个），与工作簿的判据同一量纲
    /// </summary>
    private const int MaximumSheetNameLength = 31;

    /// <summary>
    /// 写进消息的表名长度上限，比名字本身的 31 个字符宽，留出「第几张表」的上下文
    /// </summary>
    private const int MaximumNameInMessage = 40;

    /// <summary>
    /// 工作表名不接受的字符，与工作簿的实际约束逐条对齐：既不误杀它肯收的名字，也不放过它拒绝的名字
    /// </summary>
    private static readonly char[] InvalidSheetNameCharacters = [':', '\\', '/', '?', '*', '[', ']', '\0', '\u0003'];

    /// <summary>
    /// 检查一张表的表名能否落进工作簿：长度、非法字符与首尾单引号
    /// </summary>
    /// <param name="sheetName">要检查的表名</param>
    /// <param name="position">表在清单里的位置，用于消息；单表路径固定为 1</param>
    /// <remarks>
    /// 长度按 <see cref="string.Length"/>（UTF-16 代码单元）计，代理对占两个，上限 31 个字符；不接受的字符是
    /// <c>: \ / ? * [ ]</c> 加 <c>U+0000</c> 与 <c>U+0003</c>，其余 ASCII 与控制字符工作簿都肯收，因此这里不多拒
    /// （含竖线、尖括号、换行、全形字符的名字照样写）；首尾单引号被拒，而中间的撇号被收。
    /// 表名为空或纯空白由表规格的赋值守卫拦下，这里不重复判，只为「名字里第一个字符就要取撇号」那一条留空串保护。
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
    /// 判重按不区分大小写的口径，与 Excel 和工作簿自身一致：只有大小写不同的两个名字同样被拒。
    /// 清单里的 <c>null</c> 项点名第几项抛出——空项既没有表名也没有列，写不出任何东西，也不该被当成「跳过这张表」。
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
    /// 判定按型别分派，不做「先转字符串再看不像数字」这类猜测：<c>decimal</c> 没有非有限形态，
    /// <see cref="TimeSpan"/> 与 <see cref="Guid"/> 各有工作簿自己的格位，都不在这里拒。
    /// 日期下限在这里只判 <see cref="DateTime"/>——它是两条路径都落日期格、因而共担同一道界的那个型别；
    /// <see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/> 在全量路径落文本格（值原样读回）、
    /// 只在流式路径落日期格，故不归本方法，而由 <see cref="DescribeUnwritableInStream"/> 单独判。
    /// 数值的位数一道归 <see cref="NumericPrecisionReason"/>：它自己认得该管哪几个型别，
    /// <see cref="double"/> 与 <see cref="float"/> 的非有限形态已在更早的臂先拒，走到这一道的都是有限值。
    /// 成因只写值本身，行位置与列名由调用方在抛出时拼进去。
    /// </remarks>
    internal static string? DescribeUnwritable(object? value)
        => value switch
        {
            DateTime date when date < EarliestDate =>
                $"日期「{date:yyyy-MM-dd HH:mm:ss}」早于 xlsx 的 1900 日期系统能表示的最早时刻 {EarliestDate:yyyy-MM-dd}，" +
                "工作簿会把它夹到纪元时刻并静默变成另一个日期。请给出该时刻之后的日期，或让该列取成文本。",

            double number when !double.IsFinite(number) => NotFiniteNumberMessage(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),

            float number when !float.IsFinite(number) => NotFiniteNumberMessage(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),

            string text when text.Length > ExcelConstants.MaxCellTextLength =>
                $"字串有 {text.Length} 个字符，超过单元格的上限 {ExcelConstants.MaxCellTextLength} 个字符：" +
                "工作簿装不下它，写出只会得到一份读回不了的档。这里不截断——截断会丢弃数据，" +
                "请自己决定分段或改写字段后再导出。",

            _ when NumericPrecisionReason(value) is { } precision => precision,

            _ => null
        };

    /// <summary>
    /// 流式路径的取值域判定：共用判定之外，再加一条只管本路径的日期格下限
    /// </summary>
    /// <param name="value">刚取出的行值，<c>null</c> 表示空格，直接放过</param>
    /// <returns>不可写的原因文字，可写（含 <c>null</c> 值）时为 <c>null</c></returns>
    /// <remarks>
    /// <para>
    /// <see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/> 在流式路径都落成日期格（前者取当日零点，
    /// 后者取它的钟表时刻、偏移量不落格），而日期格按 1900 日期系统计数，早于 <see cref="EarliestDate"/>
    /// 的日期在系统里没有对应的计数。写出库对这一段并不都报错——有些取值照样原样读回——那是库的宽容，
    /// 不是格式的承诺：本组件不赌这份宽容，也不按取值远近分「哪一段安全」，早于该时刻的整段一起拒。
    /// </para>
    /// <para>
    /// 量的还是那把 <see cref="EarliestDate"/>，不另立常量；比的是真正落进格里的那个钟表时刻
    /// （<see cref="DateTimeOffset"/> 用 <see cref="DateTimeOffset.DateTime"/>，不看偏移量）。
    /// 全量路径把这两个型别交给工作簿落成文本格、不送进日期格，因此不判这一条：不对称是两条路径的落格方式不同，
    /// 不是两套标准。
    /// </para>
    /// </remarks>
    internal static string? DescribeUnwritableInStream(object? value)
        => DescribeUnwritable(value) ?? (value switch
        {
            DateOnly only when only.ToDateTime(TimeOnly.MinValue) < EarliestDate =>
                StreamDateCellMessage($"{only:yyyy-MM-dd} 00:00:00"),

            DateTimeOffset offset when offset.DateTime < EarliestDate =>
                StreamDateCellMessage($"{offset.DateTime:yyyy-MM-dd HH:mm:ss}"),

            _ => null
        });

    /// <summary>
    /// 早于日期格下限的成因文字：说政策与出路，不替写出库的宽容度下结论
    /// </summary>
    private static string StreamDateCellMessage(string literal)
        => $"日期「{literal}」早于 xlsx 的 1900 日期系统起点 {EarliestDate:yyyy-MM-dd}，而流式导出把这类取值落成日期格。" +
            "日期格能不能容住早于该时刻的值，取决于写出库与表格软件各自多宽容，本组件不依赖这份宽容，一律拒写。" +
            $"请给出不早于 {EarliestDate:yyyy-MM-dd} 的日期，或让该列取成文本；要原样保住早于该时刻的日期，" +
            "请改用全量导出——它把 DateOnly 与 DateTimeOffset 落成文本格，值与偏移量都留在文字里。";

    /// <summary>
    /// 非有限数值的成因文字
    /// </summary>
    private static string NotFiniteNumberMessage(string literal)
        => $"数值「{literal}」不是有限数：工作簿的数值格只有有限十进制数，NaN 与无穷大都没有对应形态。" +
            "请把该列取成文本并自己决定写出什么，或先滤掉这类值。";

    /// <summary>
    /// 数值有效数字超出承诺上限时的成因文字，在承诺内时交回 <c>null</c>
    /// </summary>
    /// <param name="value">刚取出的行值。这五类型之外的取值（含 <c>null</c>）不归本方法判，直接交回 <c>null</c>，
    /// 因此调用方可以把它挂在共用判定的兜底臂上</param>
    /// <remarks>
    /// <para>
    /// 量的对象是该取值的<u>不变文化文本形态</u>里的有效数字：<c>long</c>、<c>ulong</c> 与 <c>decimal</c> 的文本
    /// 就是它本身的数字，<c>double</c> 的文本是能把这个双精度值原读回来的最短形式。小数点、正负号与指数不参与计数，
    /// 小数点前的前导零不算，整数末尾的零可以并进指数所以也不算——这样恰好在承诺位数上的取值不会因为写了小数点
    /// 或负号而被误拒。
    /// </para>
    /// <para>
    /// <see cref="float"/> 先展开成 <see cref="double"/> 再量：落进数值格的是那份双精度，按单精度自己的最短文本量
    /// 会放过 <c>0.1f</c> 这类实际被改写成另一个数的取值。展开后落在承诺位数内的单精度值（<c>1.5f</c> 这类）
    /// 照写，不多拒。
    /// </para>
    /// <para>
    /// 拒写而不改短是政策：本组件不替呼叫端决定该舍到第几位，也不按走哪条路径给出两个不同的较短形式。
    /// </para>
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
    /// 数一段数字文本里的有效数字位数：取第一个非零数字到最后一个非零数字之间的数字个数
    /// </summary>
    /// <param name="text">数值的文本形态，可含正负号、小数点与指数</param>
    /// <remarks>
    /// 指数部分（<c>E</c>／<c>e</c> 之后）不参与——它移动的是小数点，不改变有效数字；全零（含 <c>0</c> 与
    /// <c>0.0000</c>）算 1 位而不是 0 位，免得零这个最普通的取值被判成越界。
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
    /// <exception cref="InvalidOperationException">取值是早于 1899-12-30 的 <see cref="DateTime"/>、非有限的浮点数、
    /// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值，
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
    /// <param name="innerException">库抛出的原始异常，转译时原样带上，不吞掉</param>
    internal static InvalidOperationException CreateFailure(
        ExcelColumn column,
        string position,
        string reason,
        Exception? innerException = null)
        => new($"xlsx 导出无法完成：{position}的「{column.Header}」列（键 {column.Key}）。{reason}", innerException);

    /// <summary>
    /// 把过长的名字截短后再写进异常消息，避免一条消息塞进整串无用的字符
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
