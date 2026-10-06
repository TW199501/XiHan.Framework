// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Importing;

/// <summary>
/// 导入选项：来源格式、表头位置、空白处理、固定宽度列定义与行数上限
/// </summary>
/// <remarks>
/// <para>
/// 取 <c>record</c> 而非规格 §5 写的 <c>class</c>：调用方要「在这份设置上改一项再读」时写
/// <c>options with { MaxRowCount = 3 }</c> 得到派生副本，原对象不变，<c>class</c> + <c>init</c> 做不到
/// （这是对规格的一处形改，语义不变）。全部设置用 <c>init</c> 访问器，构造完成后交给导入器读取。
/// </para>
/// <para>
/// 各设置只作用于对应来源：<see cref="TextEncodingName"/> 与 <see cref="Delimiter"/> 只对
/// <see cref="ExcelImportFormat.Csv"/>／<see cref="ExcelImportFormat.Txt"/> 生效，
/// <see cref="SheetName"/> 只对 <see cref="ExcelImportFormat.Xls"/>／<see cref="ExcelImportFormat.Xlsx"/> 生效；
/// 其余来源忽略这些设置，不报错也不生效。
/// </para>
/// <para>
/// <see cref="FixedColumns"/> 不按来源划分而按<u>读法</u>划分：它非 <c>null</c> 就是把这份档当固定宽度文字档读，
/// 分隔符与工作簿路径不再参与；此时 <see cref="HasHeader"/> 与 <see cref="TrimHeaders"/> 按「无表头」处理。
/// </para>
/// </remarks>
public sealed record ExcelImportOptions
{
    private string? _textEncodingName;

    /// <summary>
    /// 来源格式，默认 <c>null</c> 表示按文件签章自动判别
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>null</c> 只对 <see cref="ExcelImportFormat.Xls"/> 与 <see cref="ExcelImportFormat.Xlsx"/> 有意义：
    /// 两者各有固定档头签章。文字档没有可靠签名，自动判别必然失败并抛 <see cref="InvalidOperationException"/>，
    /// 读 <see cref="ExcelImportFormat.Csv"/>／<see cref="ExcelImportFormat.Txt"/> 必须指名。
    /// </para>
    /// <para>
    /// 指名 <see cref="ExcelImportFormat.Xls"/> 或 <see cref="ExcelImportFormat.Xlsx"/> 时<u>不信任副档名</u>也不强判签章：
    /// 读取器仍按内容自行选容器解析器，因此档名叫 <c>.xls</c> 而内容是 <c>.xlsx</c> 可以正常读。
    /// 指名 <see cref="ExcelImportFormat.Csv"/> 或 <see cref="ExcelImportFormat.Txt"/> 则强制走文字解析器，
    /// 内容是二进制工作簿时按字串解析，不报错但取不到有意义的列。
    /// </para>
    /// </remarks>
    public ExcelImportFormat? Format { get; init; }

    /// <summary>
    /// 要读的工作表名，默认 <c>null</c> 表示只读第一张表
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一次导入只读一张表，不跨表续读：<see cref="ExcelImportRow.RowNumber"/> 的行号以该表自身的第一行起算，
    /// 跨表续读会让同一个行号落在两张表上，错误报表无法定位。要读多张表就按表名各调一次。
    /// </para>
    /// <para>
    /// 比对不区分大小写（与导出侧的表名判重口径一致）。表名在本工作簿里不存在时抛
    /// <see cref="InvalidOperationException"/> 并列出工作簿实际的表名，不做「那就读第一张」的降级。
    /// </para>
    /// </remarks>
    public string? SheetName { get; init; }

    /// <summary>
    /// 表头所在行在<u>本表</u>内的 0 起始偏移，默认 <c>0</c>（第一行即表头）；此偏移之前的行全部丢弃
    /// </summary>
    /// <remarks>
    /// 丢弃的前导行仍占 <see cref="ExcelImportRow.RowNumber"/>：设 <c>2</c> 表示前导两行不要，
    /// 第三行作表头，第一条数据行的行号是 <c>4</c>。负值在构造时抛
    /// <see cref="ArgumentOutOfRangeException"/>。
    /// 固定宽度路径（<see cref="FixedColumns"/> 非 <c>null</c>）里没有表头行可定位，本设置在那里收敛成
    /// 「丢掉前导这么多行」，丢掉的行照样占行号，第一条数据行的行号是 <c>HeaderRowIndex + 1</c>。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">给出的值为负数</exception>
    public int HeaderRowIndex
    {
        get => _headerRowIndex;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(HeaderRowIndex),
                    value,
                    "表头行偏移是 0 起始的行下标，不能是负数。");
            }

            _headerRowIndex = value;
        }
    }

    private int _headerRowIndex;

    /// <summary>
    /// 是否把一行用作表头，默认 <c>true</c>
    /// </summary>
    /// <remarks>
    /// <c>false</c> 时没有任何行被当作表头吃掉，键名按列序取 <c>Col1</c>、<c>Col2</c>…；
    /// <see cref="TrimHeaders"/> 在本情形下无对象可处理，不报错也不生效。
    /// 固定宽度路径（<see cref="FixedColumns"/> 非 <c>null</c>）把本设置<u>一律按 <c>false</c> 处理</u>：
    /// 定宽档的列名来自调用方给的列定义，源档没有可以吃掉的表头行，两种取值行为相同（见该设置的说明）。
    /// </remarks>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// 是否去掉表头文案的首尾空白，默认 <c>true</c>
    /// </summary>
    /// <remarks>
    /// 表头键由开发者用来取值，<c>" 提單號 "</c> 与 <c>"提單號"</c> 应当是同一个键，因此默认去掉空白。
    /// 表头文案本身不是业务数据，去掉空白不算改写数据。
    /// </remarks>
    public bool TrimHeaders { get; init; } = true;

    /// <summary>
    /// 是否去掉取值的首尾空白，默认 <c>false</c>
    /// </summary>
    /// <remarks>
    /// 默认不剥：值里的首尾空白是业务数据（条码、单号、定长文本字段常靠空格补位），去掉就与源档不再逐字往返。
    /// 这与本组件「改写数据必须显式要求」的口径一致，要剥空白请显式设为 <c>true</c>。
    /// 只剥 <see cref="string"/> 值，数值与日期原样交回。
    /// </remarks>
    public bool TrimValues { get; init; }

    /// <summary>
    /// 是否跳过整行皆空的记录，默认 <c>true</c>
    /// </summary>
    /// <remarks>
    /// 「整行皆空」指本行每个取值都是 <c>null</c> 或空字串（<see cref="TrimValues"/> 为 <c>true</c> 时按剥完空白判断）。
    /// 跳过的行仍占 <see cref="ExcelImportRow.RowNumber"/>，因此后续行的行号连续。
    /// 设为 <c>false</c> 时空行会以全空值集合交出，行数与源档一致。
    /// </remarks>
    public bool SkipEmptyRows { get; init; } = true;

    /// <summary>
    /// 文字档的解码编码名称，默认 <c>null</c> 表示自动判别
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对 <see cref="ExcelImportFormat.Csv"/>／<see cref="ExcelImportFormat.Txt"/> 生效。取 <c>null</c> 时的判别链见
    /// 导入实现的说明，两条读取路径不同：分隔文字档是「BOM → 整档按 UTF-8 试解 → 严格 Big5 回退」，
    /// 固定宽度档是「BOM → 前 <c>32KB</c> 按 UTF-8 试解 → Big5 回退」。自动判别只保证「按判出来的编码解不会撞到
    /// 解码错误」，<u>不保证那是原档真正的编码</u>：纯 ASCII 档在 UTF-8 与 Big5 下都合法，一段 Big5 字节也可能
    /// 正好构成合法 UTF-8 序列，判出来的会是另一种语言的字；<u>没有 BOM 的 UTF-16 更认不出来</u>——按 UTF-8 解
    /// 会得出一串夹着 NUL 的字符，落到 Big5 回退则多半当场抛解码异常。
    /// </para>
    /// <para>
    /// 指名编码时由导入实现自己按该编码解码，自动判别不再插手，读到的字由指名的编码唯一决定，因此上面这几类档
    /// 指名编码就能确定读出原字。<u>档带 BOM 时以 BOM 为准、指名让位</u>：分隔文字档的读取器认 BOM 且优先于
    /// 任何回退编码。指名编码的分隔文字档会整档转码成 UTF-8 再交给读取器，这一支的内存占用与档大小同量级，
    /// 档大小的上限见 <see cref="XiHan.Framework.Excel.Abstractions.XiHanExcelOptions.MaxImportBytes"/>。
    /// </para>
    /// <para>
    /// 常用取值：<c>"utf-8"</c>、<c>"utf-8-bom"</c>、<c>"big5"</c>、<c>"gb18030"</c>，大小写与首尾空白不参与判断。
    /// 取的是编码<u>名称或别名</u>（<c>"csBig5"</c> 这类别名可，<c>"BIG5"</c> 与 <c>"big5"</c> 同义），
    /// <u>不是代码页编号</u>：<c>"950"</c> 与 <c>"MS950"</c> 都解析不到，会抛 <see cref="ArgumentException"/>，
    /// 要按编号取编码请由调用方自己先取到名称。名称无法解析时同样抛 <see cref="ArgumentException"/> 并点名该名称，
    /// 不退回 UTF-8。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">值为空字串或仅含空白字符（要自动判别请传 <c>null</c>）</exception>
    public string? TextEncodingName
    {
        get => _textEncodingName;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "文字档编码名不能是空字串或仅含空白字符；要自动判别请传 null。",
                    nameof(TextEncodingName));
            }

            _textEncodingName = value;
        }
    }

    /// <summary>
    /// 文字档的字段分隔符，默认 <c>null</c> 表示按格式取默认值：<c>.csv</c> 用 <c>,</c>，<c>.txt</c> 用制表符
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对 <see cref="ExcelImportFormat.Csv"/>／<see cref="ExcelImportFormat.Txt"/> 生效。本设置是<u>钉死</u>分隔符而不是
    /// 候选：读取器在候选表只有一个字符时不再做多行嗅探，文件里另有别的常见分隔符也不会被采纳，
    /// 因此制表符档给了 <c>','</c> 就按逗号切出一列，不会自作主张改判。
    /// </para>
    /// <para>
    /// 引号规则固定为 RFC 4180：字段以 <c>"</c> 包住，值内的 <c>"</c> 写两遍，包住的换行原样属于值本身。
    /// 这个口径与导出侧的 <c>ExcelTextQuote.Minimal</c> 对应，两边可以逐字往返。
    /// </para>
    /// </remarks>
    public char? Delimiter { get; init; }

    /// <summary>
    /// 固定宽度文字档的列定义（键与字节宽度），默认 <c>null</c> 表示不按固定宽度切列
    /// </summary>
    /// <remarks>
    /// <para>
    /// 给了本设置就是请求按字节位置切列：导入侧不再按分隔符拆字段，也不认得任何表头文案。
    /// 读分隔符档（<c>csv</c> 与工作簿）时请保持 <c>null</c>，两条路径由导入门面按「有没有列定义」这一条规则分流，
    /// 不看 <see cref="Format"/>。
    /// </para>
    /// <para>
    /// <b>本路径把 <see cref="HasHeader"/> 按「无表头」处理，两种取值行为相同</b>：列名来自开发者给的
    /// <see cref="ExcelFixedWidthField.Key"/>，源档的每一行都是数据，第一条数据行的
    /// <see cref="ExcelImportRow.RowNumber"/> 是 <c>1</c>，<see cref="TrimHeaders"/> 在这里没有对象可处理。
    /// 定宽档通常就没有表头行，把表头语义套上去会凭空吃掉一条记录，而行号要留给错误报表定位。
    /// <see cref="HeaderRowIndex"/> 在本路径<u>仍然解释</u>：它说的那几行前导内容（档头说明、生成时间戳一类）
    /// 照旧丢掉且不参与切列，丢掉的行照样占 <see cref="ExcelImportRow.RowNumber"/>。
    /// </para>
    /// <para>
    /// 宽度单位是字节，必须与写这份档用的编码一致（见 <see cref="ExcelFixedWidthField.WidthBytes"/>）。
    /// <see cref="TextEncodingName"/> 在本路径照常生效；<see cref="Delimiter"/>、<see cref="SheetName"/>
    /// 与 <see cref="TrimHeaders"/> 在本路径不解释，不报错也不生效。
    /// <see cref="TrimValues"/> 与 <see cref="SkipEmptyRows"/> 照常生效，判「整行皆空」的口径与其他来源一致。
    /// </para>
    /// <para>
    /// 列定义本身的合法性在<u>首次取行</u>时一次判完：清单里的空项、空字串或仅含空白的键、非正整数的宽度、
    /// 重复的键、列宽总和超过单行列宽上限 <see cref="ExcelConstants.MaxFixedRowWidthBytes"/>，都抛
    /// <see cref="InvalidOperationException"/> 并点名是哪一项或哪一列，不改投默认宽度、也不把多出来的列忽略掉。
    /// 之所以不在 <c>init</c> 访问器里判：本设置交出的是<u>调用方持有的</u> <see cref="IReadOnlyList{T}"/>，
    /// <c>init</c> 里判完之后，那份清单照样可能被它背后的 <see cref="List{T}"/> 改坏（多加一列、改掉一个宽度、
    /// 换成另一批元素），选项对象却已经带着「曾经合格」的结论往下走。判据因此必须在真要切列之前，
    /// 按当时那份清单重新算一次，而不是相信构造时的那一眼。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// 首次取行时列定义不成立：清单里有空项、<c>Key</c> 为空、宽度不是正整数、键重复，
    /// 或列宽总和超过单行列宽上限 <see cref="ExcelConstants.MaxFixedRowWidthBytes"/>。集合为空的列定义同样落在
    /// 这里——一列都没有的定义切不出任何取值，不当成「整行当一列」的降级
    /// </exception>
    public IReadOnlyList<ExcelFixedWidthField>? FixedColumns { get; init; }

    /// <summary>
    /// 单次导入最多交出多少条数据行，默认 <c>null</c> 表示不指名、取框架硬上限
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只可低于硬上限、不可高于：高于上限抛 <see cref="ArgumentOutOfRangeException"/>。这条挡的是「调用方把内存上限
    /// 放大到无穷」，属仓库级硬约束，不由单个选项对象突破。
    /// </para>
    /// <para>
    /// 上限以<b>本次生效的那一道</b>为准：默认是 <see cref="XiHan.Framework.Excel.Abstractions.ExcelConstants.DefaultMaxImportRows"/>，应用把
    /// <see cref="XiHan.Framework.Excel.Abstractions.XiHanExcelOptions.MaxImportRows"/> 配得更低时以配置值为准，两条导入路径同判。
    /// 越界抛出的是生效中的那一道界，因此把上限配成 2 行时，报出的是 2 而不是框架默认值。
    /// </para>
    /// <para>
    /// 非正整数（<c>0</c> 与负数）同样抛 <see cref="ArgumentOutOfRangeException"/>：「最多读 0 行」不是合法的请求，
    /// 不静默当成「不限制」。
    /// </para>
    /// <para>
    /// <b>撞上上限时的行为取决于上限有没有被指名。</b>给了本设置，或应用把
    /// <see cref="XiHan.Framework.Excel.Abstractions.XiHanExcelOptions.MaxImportRows"/> 收紧到框架硬上限之下，
    /// 都算指名：「只取前 N 行」是请求本身的内容，档里还有更多行时<u>截断、不报错</u>，交出的就是前 N 行。
    /// 两处都没指名时（本设置为 <c>null</c>，且生效的上限正是框架硬上限
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/>——把配置项写成与它相同的数等同于没写），
    /// 撞上的那道界不属于任何请求：档的数据行超过它，两条导入路径都在<u>上限之后确实还有数据行</u>时
    /// 抛 <see cref="InvalidOperationException"/> 并点名下限值，不静默少交行——静默截断交回的是一份
    /// 看起来完整、其实缺尾的导入结果，调用方无从得知少了多少。判「还有没有数据行」要往下多读一行，
    /// 空行照 <see cref="SkipEmptyRows"/> 的口径不算数据行，因此档的行数<u>恰好等于</u>上限时不抛。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">值大于本次生效的行数上限，或为 <c>0</c> 与负数</exception>
    public int? MaxRowCount { get; init; }
}
