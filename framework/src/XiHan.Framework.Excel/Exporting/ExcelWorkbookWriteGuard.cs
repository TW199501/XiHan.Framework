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
/// 单元格取值同样有硬界：日期只接受不早于 <see cref="EarliestDate"/> 的那一天，更早的值本组件不依赖写出库
/// 与表格软件各自的宽容度去落格；数值格只有有限十进制数，<c>NaN</c> 与 <c>±∞</c> 没有对应形态；
/// <see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/>、<see cref="double"/>、<see cref="float"/>
/// 的有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位时，本组件不再承诺这一格交回
/// 呼叫端给的那个数；<see cref="long"/>、<see cref="ulong"/>、<see cref="decimal"/> 的绝对值超过
/// <see cref="MaxExactIntegerMagnitude"/> 时同样交不回原值——数值格是双精度，越过那道界之后整数不再是逐个可表示的；
/// 字串格的上限是 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符。越界的值一律抛出而不改写——把
/// <c>NaN</c> 写成 <c>"NaN"</c> 会让读回的数值列多出字串，把 <c>∞</c> 夹成最大有限数是凭空造数，把早于下限的
/// 日期照落等于把「这一格读回来是哪一天」交给写出库与表格软件各自的宽容度，把多于承诺位数的数值照落会交出被舍短的
/// 另一份数，把超出双精度整数界的整数照落会交出被挪到邻近可表示值的另一份数，截断超长字串会丢弃数据，
/// 六种都是「交出看不出问题的坏档」。
/// </para>
/// <para>
/// 这套判据放在一处是因为两条 xlsx 写出路径共用同一把尺，而不是为了把两边判成同一个样子：排版路径会把
/// 越界值与坏名字交给工作簿去拒，流式路径却会把它们直接落进档里——日期落进装不下它的格子、<c>NaN</c> 与
/// 超长字串写出回读不了的档、多于承诺位数的数值被两条路径各自舍成不同的较短形式、名字里的控制字符被转义成
/// 另一个名字。分派器按行数把同一份规格送到其中一条路径，
/// 「装不下」的取值集合不该由走哪条决定，因此这里只留一份，两边都调它，不在各自的路径里各写一遍。
/// 数值位数这一道尤其要只有一份：它判的是「本组件承诺交回呼叫端给的那个数」，而这份承诺只能按取值本身说，
/// 不能按某个写出库恰好舍到第几位说。整数大小那一道同理：流式路径把整串数字原样写进档、全量路径写成指数形式，
/// 两边读回的都是被挪过的另一个整数，因此判据也按取值本身说，不按哪条路径写出的字串较长说。
/// </para>
/// <para>
/// 模板路径通过 <see cref="DescribeTemplateUnwritable"/> 额外拒绝 XML 1.0 非法 C0 字符。
/// 两条 xlsx 导出路径支持 OOXML 转义，不受该限制。
/// </para>
/// <para>
/// 日期下限也只有一把尺，三种日期型别一起归它管：<see cref="DateTime"/>、<see cref="DateOnly"/> 与
/// <see cref="DateTimeOffset"/> 早于 <see cref="EarliestDate"/> 时两条路径一起拒。这三个型别在两条路径落进的
/// 格位并不相同——<see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/> 在流式路径落日期格、在全量路径落
/// 文本格（值原样留在文字里）——但「哪一天之前不能写」不由落进哪种格子决定：分派器按行数替调用方选路径，
/// 若下限跟着格位走，「同一份规格能不能导」就成了走哪条的副产品。
/// 同一个值落成不同格位而值不被改写的那些型别差异（<see cref="byte"/>、<see cref="TimeOnly"/> 等）
/// 不在本文件的范围内，由两条路径各自的文档说明。
/// </para>
/// </remarks>
internal static class ExcelWorkbookWriteGuard
{
    /// <summary>
    /// 本组件接受的最早日期，早于它的 <see cref="DateTime"/>、<see cref="DateOnly"/> 与
    /// <see cref="DateTimeOffset"/> 在两条 xlsx 写出路径一律拒写
    /// </summary>
    /// <remarks>
    /// 取 xlsx 的 1900 日期系统的起点当日：这是格式自己能按同一口径数出来的第一天，也是本组件对「哪一天之前
    /// 不写」给出的唯一一把尺。更早的日期不是全都写不进档，而是能不能读回原值取决于写出库与表格软件各自的
    /// 宽容度——本组件不赌这份宽容，也不按取值远近划分「哪一段安全」，早于该日的整段一起拒。
    /// </remarks>
    internal static readonly DateTime EarliestDate = new(1900, 1, 1);

    /// <summary>
    /// xlsx 数值格能逐个表示的整数上限，即 2^53；<see cref="long"/>、<see cref="ulong"/> 与 <see cref="decimal"/>
    /// 的绝对值超过它时，落进数值格的不再是呼叫端给的那个数
    /// </summary>
    /// <remarks>
    /// 数值格在档里就是一个双精度数，双精度只有 53 位二进制尾数，因此绝对值超过 2^53 之后相邻两个可表示值的
    /// 间距大于 1，整数不再逐个可表示。这一道与
    /// <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 那道并列，量的是同一件事的两种越界方式：
    /// 位数那道拦「写出来就被舍短」的取值，大小这道拦「位数看着不多、量级却已越过可逐个表示的界」的取值
    /// （例如末尾带一串零的 19 位整数只算 15 位有效数字，却已经落在间距 2048 的那一段里）。
    /// <see cref="double"/> 与 <see cref="float"/> 不归这一道管：呼叫端交出的本来就是双精度取值，
    /// 落进格子里的是同一个双精度，交回的也是它，中间没有第二个数。
    /// </remarks>
    private const long MaxExactIntegerMagnitude = 9007199254740992L;

    /// <summary>
    /// 工作表名的长度上限，按 UTF-16 代码单元计（代理对占两个），与工作簿的判据同一量纲
    /// </summary>
    private const int MaximumSheetNameLength = 31;

    /// <summary>
    /// 写进消息的表名长度上限，比名字本身的 31 个字符宽，留出「第几张表」的上下文
    /// </summary>
    private const int MaximumNameInMessage = 40;

    /// <summary>
    /// 工作表名不接受的字符：工作簿在名字上自己拒收的那几个，加 <c>U+0000</c> 与 <c>U+0003</c>
    /// </summary>
    /// <remarks>
    /// 表名与单元格文本的约束不同，不与模板的字符判据共用。
    /// </remarks>
    private static readonly char[] InvalidSheetNameCharacters = [':', '\\', '/', '?', '*', '[', ']', '\0', '\u0003'];

    /// <summary>
    /// 检查一张表的表名能否落进工作簿：长度、非法字符与首尾单引号
    /// </summary>
    /// <param name="sheetName">要检查的表名</param>
    /// <param name="position">表在清单里的位置，用于消息；单表路径固定为 1</param>
    /// <remarks>
    /// <para>
    /// 长度按 <see cref="string.Length"/>（UTF-16 代码单元）计，代理对占两个，上限 31 个字符；本判据不接受的字符是
    /// <c>: \ / ? * [ ]</c> 加 <c>U+0000</c> 与 <c>U+0003</c>，其余 ASCII（含竖线与尖括号）与全形字符的名字照样写；
    /// 首尾单引号被拒，而中间的撇号被收。
    /// 表名为空或纯空白由表规格的赋值守卫拦下，这里不重复判，只为「名字里第一个字符就要取撇号」那一条留空串保护。
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
    /// 日期下限三种型别一起在这里判：<see cref="DateTime"/> 两条路径都落日期格，
    /// <see cref="DateOnly"/> 与 <see cref="DateTimeOffset"/> 在流式路径落日期格、在全量路径落文本格，
    /// 三者量的都是同一把 <see cref="EarliestDate"/>，比的是真正要落进格里的那个钟表时刻
    /// （<see cref="DateOnly"/> 取当日零点，<see cref="DateTimeOffset"/> 用 <see cref="DateTimeOffset.DateTime"/>、
    /// 不看偏移量），因此两条路径同呼本方法就够，不再另立一份只管流式的判定。
    /// 数值的位数一道归 <see cref="NumericPrecisionReason"/>：它自己认得该管哪几个型别，
    /// <see cref="double"/> 与 <see cref="float"/> 的非有限形态已在更早的臂先拒，走到这一道的都是有限值。
    /// 整数大小一道归 <see cref="NumericMagnitudeReason"/>，排在位数之后：两道都命中时先报位数，
    /// 因为位数是呼叫端能从字面上直接数出来的那一道。
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
    /// 模板将非法 C0 字符写成无效 XML 实体；两条 xlsx 导出路径支持 OOXML 转义，不调用本方法。
    /// 制表符、换行与回车是 XML 1.0 合法字符，不在此处拒绝。
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
    /// 制表符、换行与回车虽然是控制字符，却在 XML 1.0 的合法集里，因此这三个刻意排除在外。
    /// </remarks>
    private static bool IsXmlIllegalCharacter(char character)
        => character <= '\u0008'
            || character is '\u000B' or '\u000C'
            || character is >= '\u000E' and <= '\u001F';

    /// <summary>
    /// XML 1.0 非法字符的成因文字：点名是第几个字符与它的码点，只写政策与出路
    /// </summary>
    /// <param name="index">该字符在字串里的 0 起下标</param>
    /// <param name="character">命中的字符</param>
    private static string XmlIllegalCharacterMessage(int index, char character)
        => $"字串在第 {index + 1} 个字符处含有 U+{(int)character:X4}：XML 1.0 不允许该字符。" +
            "模板渲染拒绝写入，请移除该字符或替换为可见字符后重试。";


    /// <summary>
    /// 早于日期下限的成因文字：只写政策与出路，不替写出库与表格软件的行为下结论
    /// </summary>
    /// <param name="literal">要写进消息的日期文本，按真正要落进格里的那个钟表时刻排成不变文化形式</param>
    /// <remarks>
    /// 三种日期型别共用这一份文字，因此消息里不点名型别、也不说这一格会落成日期格还是文本格：
    /// 落格方式是两条路径各自的实现细节，能不能写与它无关。出路只留「改用不早于下限的日期」与
    /// 「让该列取成文本」两条——不再指点「换一条写出路径」，因为两条路径现在判得一样，那条出路已经作废。
    /// </remarks>
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
    /// <para>
    /// 「承诺位数以内原样交回」这句话的适用域是<b>位数</b>这一道，不是「原样交回」的全部条件：末尾带一串零的整数
    /// 位数看着不多，量级却可能已越过数值格能逐个表示的界，那一段由 <see cref="NumericMagnitudeReason"/> 另立一道拦。
    /// 两道都在同一个共用函数里，一起构成「本组件承诺这一格交回呼叫端给的那个数」的完整前提。
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
    /// 整数取值的绝对值超出数值格能逐个表示的范围时的成因文字，在范围内时交回 <c>null</c>
    /// </summary>
    /// <param name="value">刚取出的行值。<see cref="long"/>、<see cref="ulong"/> 与 <see cref="decimal"/>
    /// 之外的取值（含 <c>null</c>）不归本方法判，直接交回 <c>null</c>，因此调用方可以把它挂在共用判定的兜底臂上</param>
    /// <remarks>
    /// <para>
    /// 这一道补的是位数那道的漏：整数末尾的一串零不计入有效数字，于是一个 19 位、绝对值远超
    /// <see cref="MaxExactIntegerMagnitude"/> 的整数可以只算 15 位有效数字而被放行。位数与量级是两种独立的越界方式，
    /// 因此各立一道，都在同一个共用函数里，两条 xlsx 写出路径一起判。
    /// </para>
    /// <para>
    /// <see cref="double"/> 与 <see cref="float"/> 不在这一道里：呼叫端交出的本来就是双精度取值，
    /// 落进数值格的是同一个双精度、交回的也是它，中间不产生第二个数。<see cref="decimal"/> 在：它有 28 到 29 位
    /// 十进制精度，量级越过双精度的可逐个表示界之后交不回原值，有没有小数部分都一样。
    /// </para>
    /// <para>
    /// 判的是「绝对值超过 <see cref="MaxExactIntegerMagnitude"/>」而不是「这个取值恰好落在两个可表示值之间」：
    /// 后者要按每个量级的间距逐个算，且放过的那些取值只是恰好对齐，呼叫端从字面上看不出自己踩在哪一侧。
    /// 与位数那道同一取向——按一把简单可预测的尺判，不按实际会不会改值判，宁可多拒不静默改值。
    /// </para>
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
