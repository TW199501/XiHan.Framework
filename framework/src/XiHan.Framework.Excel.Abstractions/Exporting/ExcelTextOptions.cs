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
    /// <see cref="Delimiter"/> 与 <see cref="Quote"/> 无效
    /// </summary>
    public ExcelTextLayout Layout { get; init; } = ExcelTextLayout.Delimited;

    /// <summary>
    /// 字段分隔符。为 <c>null</c> 表示按目标格式取默认值：<c>.csv</c> 用 <c>,</c>，<c>.txt</c> 用制表符
    /// </summary>
    public char? Delimiter { get; init; }

    /// <summary>
    /// 输出编码名称，默认 <c>"utf-8-bom"</c>
    /// </summary>
    /// <remarks>
    /// 常用取值：<c>"utf-8-bom"</c> 写带 BOM 的 UTF-8；<c>"utf-8"</c> 写不带 BOM 的 UTF-8；<c>"big5"</c> 写大五码
    /// （繁体中文往来档常用，无 BOM）。其余取值按 <see cref="System.Text.Encoding.GetEncoding(string)"/> 的名称或代码页解析，
    /// 大小写不敏感。BOM 由解析出的编码自身写出，导出器不再手写，因此不会写两遍。
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
    /// 引号策略，默认 <see cref="ExcelTextQuote.Minimal"/>
    /// </summary>
    /// <remarks>
    /// <see cref="ExcelTextQuote.None"/> 下字段值内的分隔符与换行无法原样写出，导出器把它们改写为空格并记
    /// Warning 日志，不静默产出坏数据。
    /// </remarks>
    public ExcelTextQuote Quote { get; init; } = ExcelTextQuote.Minimal;

    /// <summary>
    /// 固定宽度布局下内容超出列宽的处置，默认 <see cref="ExcelTextOverflow.Throw"/>；分隔符布局不解释本设置
    /// </summary>
    public ExcelTextOverflow Overflow { get; init; } = ExcelTextOverflow.Throw;

    /// <summary>
    /// 是否对以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 开头的字段值加单引号前缀，默认开启
    /// </summary>
    /// <remarks>
    /// 开启后表格软件读取该档时不会把文本当公式执行。交给机器逐字段解析的 <c>.txt</c> 应关掉，避免原始数据被改写。
    /// 表头行不受本设置影响，表头文案由开发者提供，不是外来数据。
    /// </remarks>
    public bool EscapeFormulaPrefix { get; init; } = true;
}
