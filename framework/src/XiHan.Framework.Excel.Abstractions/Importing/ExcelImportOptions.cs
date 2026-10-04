// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Importing;

/// <summary>
/// 导入选项：来源格式、表头位置、空白处理与行数上限
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
    /// 文字档的解码编码名称，默认 <c>null</c> 表示自动判别（BOM → 严格 UTF-8 试探 → Big5）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对 <see cref="ExcelImportFormat.Csv"/>／<see cref="ExcelImportFormat.Txt"/> 生效。取 <c>null</c> 时的判别链见
    /// 导入实现的说明；自动判别只能保证「不产出解码失败的乱码」，<u>不能保证判对编码</u>：纯 ASCII 档在 UTF-8 与
    /// Big5 下都合法，一段 Big5 字节也可能正好是合法 UTF-8 序列，判出来的文本会是另一种语言的字。
    /// 已知来源编码时请指名，这是唯一确定的做法。
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
    /// 单次导入最多交出多少条数据行，默认 <c>null</c> 表示取框架硬上限
    /// <see cref="ExcelConstants.DefaultMaxImportRows"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只可低于硬上限、不可高于：高于上限抛 <see cref="ArgumentOutOfRangeException"/>。这条挡的是「调用方把内存上限
    /// 放大到无穷」，属仓库级硬约束，不由单个选项对象突破。
    /// </para>
    /// <para>
    /// 当前上限取自常量 <see cref="ExcelConstants.DefaultMaxImportRows"/> 而不是
    /// <c>XiHanExcelOptions.MaxImportRows</c>：本导入器按无参构造使用，裸选项对象进不来。应用把配置项调得更低时要在
    /// 分派器那侧生效（配置面接入是既定的后续派工项），本属性只是那道不可突破的上限。
    /// </para>
    /// <para>
    /// 非正整数（<c>0</c> 与负数）同样抛 <see cref="ArgumentOutOfRangeException"/>：「最多读 0 行」不是合法的请求，
    /// 不静默当成「不限制」。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">值大于框架硬上限，或为 <c>0</c> 与负数</exception>
    public int? MaxRowCount { get; init; }
}
