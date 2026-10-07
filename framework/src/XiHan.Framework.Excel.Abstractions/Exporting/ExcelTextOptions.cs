// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 文字档（<c>.csv</c>／<c>.txt</c>）导出选项
/// </summary>
/// <remarks>
/// <para>
/// 属性用 <c>init</c> 访问器；传 <c>null</c> 选项对象给导出器等同于使用本类型的默认值。
/// </para>
/// <para>
/// 列宽、补位方向与补位字符是列级设置，放在 <see cref="ExcelColumn"/> 上；编码名、换行符与超宽策略是整份文件的
/// 设置，放在本类型上。
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
    /// <see cref="EncodingName"/> 的字节数计），超宽处置取 <see cref="Overflow"/>；
    /// <see cref="Delimiter"/>、<see cref="Quote"/> 与 <see cref="EscapeFormulaPrefix"/> 给出不报错也不生效。
    /// </para>
    /// <para>
    /// 本布局下列必须给出 <see cref="ExcelColumn.FixedWidth"/>；缺该设置的列在写出任何字节之前抛
    /// <see cref="InvalidOperationException"/> 并列出缺宽度的列键。
    /// </para>
    /// <para>
    /// 本布局不接受值内换行：<see cref="ExcelColumn"/> 取值转出的文本含 <c>\r</c> 或 <c>\n</c> 时抛
    /// <see cref="InvalidOperationException"/>，不清洗也不替换。该判定在取到那一行时才做，抛出时前面的行可能已经写出。
    /// 列上的 <see cref="ExcelColumn.PadChar"/> 不得是换行符，在写出任何字节之前就被拒绝。
    /// </para>
    /// <para>
    /// 本布局拒收带 BOM 的编码（含 <see cref="EncodingName"/> 的默认值 <c>"utf-8-bom"</c>），在写出任何字节之前抛
    /// <see cref="ArgumentException"/>（<see cref="ArgumentException.ParamName"/> 为 <c>EncodingName</c>），
    /// 不剥掉前导字节。写定宽档请指名不带 BOM 的编码（<c>"utf-8"</c>、<c>"big5"</c> 一类）。
    /// </para>
    /// </remarks>
    public ExcelTextLayout Layout { get; init; } = ExcelTextLayout.Delimited;

    /// <summary>
    /// 字段分隔符。为 <c>null</c> 表示按目标格式取默认值：<c>.csv</c> 用 <c>,</c>，<c>.txt</c> 用制表符；
    /// <see cref="ExcelTextLayout.FixedWidth"/> 布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分隔符布局下以下取值在写出任何字节之前抛 <see cref="ArgumentException"/>
    /// （<see cref="ArgumentException.ParamName"/> 为 <c>textOptions</c>），不代为改写：<c>\r</c>、<c>\n</c>、
    /// 引号字符 <c>"</c>，以及空格配 <see cref="ExcelTextQuote.None"/>。
    /// </para>
    /// <para>
    /// <see cref="ExcelTextLayout.FixedWidth"/> 布局不读本设置，上述取值在该布局下既不报错也不生效。
    /// </para>
    /// </remarks>
    public char? Delimiter { get; init; }

    /// <summary>
    /// 输出编码名称，默认 <c>"utf-8-bom"</c>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 常用取值：<c>"utf-8-bom"</c> 写带 BOM 的 UTF-8；<c>"utf-8"</c> 写不带 BOM 的 UTF-8；<c>"big5"</c> 写大五码
    /// （无 BOM）。其余取值按 <see cref="System.Text.Encoding.GetEncoding(string)"/> 的名称或代码页解析，
    /// 大小写不敏感。BOM 由解析出的编码自身写出。
    /// <see cref="Layout"/> 取 <see cref="ExcelTextLayout.FixedWidth"/> 时带 BOM 的编码在写出任何字节之前抛
    /// <see cref="ArgumentException"/>。
    /// </para>
    /// <para>
    /// 解析出的编码一律带严格回退：待写出的字符不在目标编码的字符集内时（例如简体字写进 <c>"big5"</c>）抛
    /// <see cref="System.Text.EncoderFallbackException"/>。
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
    /// <see cref="ExcelTextQuote.None"/> 下字段值内的分隔符与换行由导出器改写为空格并记 Warning 日志；
    /// 该策略与空格分隔符是非法组合，导出器在写出任何字节之前抛 <see cref="ArgumentException"/>。
    /// </remarks>
    public ExcelTextQuote Quote { get; init; } = ExcelTextQuote.Minimal;

    /// <summary>
    /// 固定宽度布局下内容超出列宽的处置，默认 <see cref="ExcelTextOverflow.Throw"/>；分隔符布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <see cref="ExcelTextOverflow.Truncate"/> 丢弃超出部分，导出器对整份文件记一条聚合 Warning
    /// （含被截断的字段数量与首个触发的行列）；列宽按 <see cref="EncodingName"/> 的字节数计，截断落在字素边界上。
    /// </remarks>
    public ExcelTextOverflow Overflow { get; init; } = ExcelTextOverflow.Throw;

    /// <summary>
    /// 防公式注入：对以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符（<c>\t</c>）或回车（<c>\r</c>）开头的字段值加单引号前缀，
    /// 默认开启；<see cref="ExcelTextLayout.FixedWidth"/> 布局不解释本设置
    /// </summary>
    /// <remarks>
    /// <para>
    /// 表头行与数据行同样受本设置约束；置为 <c>false</c> 时表头与数据一并保留原值。
    /// </para>
    /// <para>
    /// 本设置会改动数据：负数经 <c>TextFormat</c> 得到 <c>-5.00</c> 后会写成 <c>'-5.00</c>。
    /// 导出器对全份文件的改写记一条聚合 Warning（含改写数量与首个触发的行列）。
    /// </para>
    /// </remarks>
    public bool EscapeFormulaPrefix { get; init; } = true;
}
