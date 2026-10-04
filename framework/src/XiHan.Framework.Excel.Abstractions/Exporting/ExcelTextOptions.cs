// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 文字档（<c>.csv</c>／<c>.txt</c>）导出选项
/// </summary>
/// <remarks>
/// <para>
/// 全部为写一次即可读的配置，属性用 <c>init</c> 访问器，构造完成后交给导出器读取。传 <c>null</c> 选项对象给导出器
/// 等同于使用本类型的默认值。
/// </para>
/// <para>
/// 取 <c>record</c> 而非 <c>class</c>：调用方要「在本份设置上改一项再导出」时，写 <c>options with { Quote = ... }</c>
/// 得到派生副本，原对象保持不变，不必整份重抄，也不会把共享的选项对象改坏。
/// </para>
/// <para>
/// 列宽、补位方向与补位字符是列级设置，放在 <see cref="ExcelColumn"/> 上；编码名、换行符与超宽策略是整份文件的
/// 设置，放在本类型上，两级设置不混用。
/// </para>
/// </remarks>
public sealed record ExcelTextOptions
{
    private string _encodingName = ExcelConstants.DefaultEncodingName;

    private string _newLine = "\r\n";

    /// <summary>
    /// 列布局方式，默认 <see cref="ExcelTextLayout.Delimited"/>；<see cref="ExcelTextLayout.FixedWidth"/> 时
    /// <see cref="Delimiter"/>、<see cref="Quote"/> 与 <see cref="EscapeFormulaPrefix"/> 无效
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ExcelTextLayout.FixedWidth"/> 下每格宽度取列上的 <see cref="ExcelColumn.FixedWidth"/>（按
    /// <see cref="EncodingName"/> 的字节数计），超宽处置取 <see cref="Overflow"/>，档按字节位置切列，因此
    /// <see cref="Delimiter"/>、<see cref="Quote"/> 与 <see cref="EscapeFormulaPrefix"/> 三项没有对应的解析位，
    /// 写出时一律不解释：<c>Delimiter</c> 与 <c>Quote</c> 是分隔符布局的字段边界手段，加公式前缀还会吃掉一格字节宽度、
    /// 让后续列位整体错位。给出这三项不报错也不生效，要逐字往返请留在默认的
    /// <see cref="ExcelTextLayout.Delimited"/> 布局。
    /// </para>
    /// <para>
    /// 列特性不带字节宽度，因此本布局下列必须由调用方构造并给出 <see cref="ExcelColumn.FixedWidth"/>；
    /// 缺该设置的列在写出任何字节之前抛 <see cref="InvalidOperationException"/> 并列出缺宽度的列键。
    /// </para>
    /// <para>
    /// 本布局不接受值内换行：<see cref="ExcelColumn"/> 取值转出的文本含 <c>\r</c> 或 <c>\n</c> 时抛
    /// <see cref="InvalidOperationException"/>，不清洗也不替换——定宽档没有可以包住换行的引号，写出去会让一档
    /// 被读成错行的两档。该判定要等取到那一行才做得出（行集合是惰性游标），所以抛出时前面的行可能已经落盘；
    /// 含换行的数据请用 <see cref="ExcelTextLayout.Delimited"/> 布局配 <see cref="Quote"/> 写出。
    /// 列上的 <see cref="ExcelColumn.PadChar"/> 同样不得是换行符，那一条与内容无关，在写出任何字节之前就被拒绝。
    /// </para>
    /// </remarks>
    public ExcelTextLayout Layout { get; init; } = ExcelTextLayout.Delimited;

    /// <summary>
    /// 字段分隔符。为 <c>null</c> 表示按目标格式取默认值：<c>.csv</c> 用 <c>,</c>，<c>.txt</c> 用制表符；
    /// <see cref="ExcelTextLayout.FixedWidth"/> 布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分隔符布局下有三类取值无法成立，导出器在写出任何字节之前抛 <see cref="ArgumentException"/>
    /// （<see cref="ArgumentException.ParamName"/> 为 <c>textOptions</c>），不代为改写也不静默降级：
    /// <c>\r</c> 与 <c>\n</c> 本身就是行分隔符，两栏会被直接写成两行；引号字符 <c>"</c> 作分隔符时，
    /// <see cref="ExcelTextQuote.Minimal"/>／<see cref="ExcelTextQuote.All"/> 用来包住字段的引号与分隔符成了同一个字符，
    /// 取 <see cref="ExcelTextQuote.None"/> 时值内的引号又与分隔符不可区分；空格配 <see cref="ExcelTextQuote.None"/>
    /// 时值内空格与分隔符不可区分。判据是「写出去就读不回来」，不是「不推荐」。
    /// </para>
    /// <para>
    /// <see cref="ExcelTextLayout.FixedWidth"/> 布局按字节位置切列、根本不读本设置，因此上述取值在该布局下既不报错
    /// 也不生效；校验因此由导出器承担而不是本类型——判据要连着布局才知道。
    /// </para>
    /// </remarks>
    public char? Delimiter { get; init; }

    /// <summary>
    /// 输出编码名称，默认 <c>"utf-8-bom"</c>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 常用取值：<c>"utf-8-bom"</c> 写带 BOM 的 UTF-8；<c>"utf-8"</c> 写不带 BOM 的 UTF-8；<c>"big5"</c> 写大五码
    /// （繁体中文往来档常用，无 BOM）。其余取值按 <see cref="System.Text.Encoding.GetEncoding(string)"/> 的名称或代码页解析，
    /// 大小写不敏感。BOM 由解析出的编码自身写出，导出器不再手写，因此不会写两遍。
    /// </para>
    /// <para>
    /// 解析出的编码一律带严格回退：待写出的字符不在目标编码的字符集内时（例如简体字写进 <c>"big5"</c>）抛
    /// <see cref="System.Text.EncoderFallbackException"/>，不会像 .NET 默认的替换回退那样静默产出 <c>?</c> 字节、
    /// 再把坏档当成功结果交回调用方。要写出某份文件，字符集必须真收得下它的全部内容：Big5 收不下简体字，
    /// 简体档请用 <c>"utf-8"</c>／<c>"utf-8-bom"</c>，或改用 <c>"gb18030"</c>。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">编码名为 <c>null</c> 或仅含空白字符</exception>
    public string EncodingName
    {
        get => _encodingName;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("输出编码名不能为空或仅含空白字符。", nameof(EncodingName));
            }

            _encodingName = value;
        }
    }

    /// <summary>
    /// 行尾序列，默认 <c>"\r\n"</c>；按目标系统要求可改为 <c>"\n"</c>
    /// </summary>
    /// <remarks>
    /// 这里的换行符是文件字节层面的行尾，与引号策略无关：字段值内部的换行属于数据本身，由
    /// <see cref="Quote"/> 决定加引号保留还是改写，不受本设置影响。
    /// </remarks>
    /// <exception cref="ArgumentException">换行符为 <c>null</c> 或空字符串</exception>
    public string NewLine
    {
        get => _newLine;
        init
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("行尾序列不能为空，换行至少要有一个字符。", nameof(NewLine));
            }

            _newLine = value;
        }
    }

    /// <summary>
    /// 是否写出表头行，默认写出
    /// </summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>
    /// 引号策略，默认 <see cref="ExcelTextQuote.Minimal"/>；<see cref="ExcelTextLayout.FixedWidth"/> 布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <see cref="ExcelTextQuote.None"/> 下字段值内的分隔符与换行无法原样写出，导出器把它们改写为空格并记
    /// Warning 日志，不静默产出坏数据；该策略与空格分隔符是非法组合，导出器在写出任何字节之前抛
    /// <see cref="ArgumentException"/>。固定宽度布局没有引号参与的解析位，本设置不参与写出。
    /// </remarks>
    public ExcelTextQuote Quote { get; init; } = ExcelTextQuote.Minimal;

    /// <summary>
    /// 固定宽度布局下内容超出列宽的处置，默认 <see cref="ExcelTextOverflow.Throw"/>；分隔符布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <see cref="ExcelTextOverflow.Truncate"/> 会丢弃超出部分，读回的档不能与原值逐字往返，因此导出器对整份文件
    /// 记一条聚合 Warning（含被截断的字段数量与首个触发的行列）；列宽按 <see cref="EncodingName"/> 的字节数计，
    /// 截断落在字素边界上，不会把一个多字节字符切成半个字节序列。
    /// </remarks>
    public ExcelTextOverflow Overflow { get; init; } = ExcelTextOverflow.Throw;

    /// <summary>
    /// 是否对以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 开头的字段值加单引号前缀，默认开启；
    /// <see cref="ExcelTextLayout.FixedWidth"/> 布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <para>
    /// 开启后表格软件读取该档时不会把文本当公式执行。交给机器逐字段解析的 <c>.txt</c> 应关掉，避免原始数据被改写。
    /// 表头行与数据行同样受本设置约束：表头文案是调用方在运行时给出的 <c>required string</c>，框架无法证明它
    /// 出自开发者而不是终端使用者，因此不按来源豁免。代价是以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 开头的
    /// 标题会多出一个可见的 <c>'</c>；置为 <c>false</c> 时表头与数据一并保留原值。
    /// </para>
    /// <para>
    /// 本设置同样改动数据，代价容易被忽略：负数经 <c>TextFormat</c> 得到 <c>-5.00</c> 后会被写成 <c>'-5.00</c>，
    /// 表格软件读回来是文本而不是数字。导出器对全份文件的改写记一条聚合 Warning（含改写数量与首个触发的行列），
    /// 不逐格刷日志；要逐字往返请置为 <c>false</c>。
    /// </para>
    /// <para>
    /// 固定宽度布局下本设置不生效：前缀多出的一格字节会挤掉内容、破坏按字节切列的宽度契约，该布局本来也不是
    /// 给表格软件重放的档。
    /// </para>
    /// </remarks>
    public bool EscapeFormulaPrefix { get; init; } = true;
}
