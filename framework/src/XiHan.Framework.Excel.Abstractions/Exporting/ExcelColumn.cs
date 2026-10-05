// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 导出列模型基类
/// </summary>
/// <remarks>
/// 属性一律 <c>init</c>，构造完成后不可变，同一列实例可安全被多个导出请求复用。
/// 非泛型基类让提供程序在不知道行类型的前提下读取列元数据并取值；行类型由泛型派生类
/// <see cref="ExcelColumn{TRow}"/> 承担，并经 <see cref="RowType"/> 重新交出——基类擦除了 <c>TRow</c>，
/// 而表规格的行型一致性预检必须在取值之前就问到「这一列认哪个行类型」，只能靠这个成员。
/// </remarks>
public abstract class ExcelColumn
{
    /// <summary>
    /// 列的稳定标识，供错误报表与列匹配使用，不随表头文案变化
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// 本列约定的行类型，即 <see cref="ExcelColumn{TRow}"/> 的泛型参数
    /// </summary>
    /// <remarks>
    /// 取值只在 <see cref="GetValue"/> 收到该类型（或其派生类型）的实例时才成立。表规格的
    /// <see cref="ExcelSheetSpec.RowType"/> 与此不符意味着每一格取值都落到「行类型不符返回 <c>null</c>」，
    /// 交出的档表头齐全而数据全空，因此这一对型别在写出任何内容之前就被比对，见
    /// <see cref="ExcelSheetSpec"/> 与列取值处的说明。
    /// </remarks>
    public abstract Type RowType { get; }

    /// <summary>
    /// 表头显示文案
    /// </summary>
    public required string Header { get; init; }

    /// <summary>
    /// 列顺序，值小的排在前面
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// 列宽（工作簿单位）。为 <c>null</c> 时由提供程序按采样行自适应
    /// </summary>
    public double? Width { get; init; }

    /// <summary>
    /// Excel 数字或日期格式串（形如 <c>#,##0.00</c>、<c>yyyy-MM-dd</c>），只在 <c>.xlsx</c> 路径生效，
    /// 与 .NET 格式串不通用
    /// </summary>
    public string? NumberFormat { get; init; }

    /// <summary>
    /// .NET 文本格式串，供取值转字符串时使用，优先于 <see cref="NumberFormat"/>
    /// </summary>
    public string? TextFormat { get; init; }

    /// <summary>
    /// 水平对齐方式，默认 <see cref="ExcelAlignment.Auto"/> 表示不显式设置
    /// </summary>
    public ExcelAlignment Alignment { get; init; } = ExcelAlignment.Auto;

    /// <summary>
    /// 是否自动换行
    /// </summary>
    public bool Wrap { get; init; }

    /// <summary>
    /// 固定宽度文本布局下该列的字段宽度，由文本提供程序解释，<c>.xlsx</c> 路径不使用
    /// </summary>
    public int? FixedWidth { get; init; }

    /// <summary>
    /// 补位方向，默认 <see cref="ExcelTextPadding.Right"/>，即内容靠左、右侧补字符
    /// </summary>
    public ExcelTextPadding Padding { get; init; } = ExcelTextPadding.Right;

    /// <summary>
    /// 补位字符，默认为空格
    /// </summary>
    public char PadChar { get; init; } = ' ';

    /// <summary>
    /// 逐格样式委托，入参为当前行对象（可能为 <c>null</c>）；返回 <c>null</c> 表示该格不额外套样式
    /// </summary>
    public Func<object?, ExcelTextStyle?>? CellStyle { get; init; }

    /// <summary>
    /// 取该列在指定行上的值
    /// </summary>
    /// <param name="row">行对象，允许为 <c>null</c></param>
    /// <returns>单元格值；行对象为 <c>null</c> 或与列约定的行类型不符时返回 <c>null</c>，由写出侧变成空单元格</returns>
    public abstract object? GetValue(object? row);
}

/// <summary>
/// 强类型导出列，用取值委托描述列与行类型的对应关系
/// </summary>
/// <typeparam name="TRow">行数据类型</typeparam>
public sealed class ExcelColumn<TRow> : ExcelColumn
{
    /// <summary>
    /// 取值委托，从行对象取出本列的值
    /// </summary>
    public required Func<TRow, object?> Value { get; init; }

    /// <summary>
    /// 本列约定的行类型，即本类的泛型参数 <typeparamref name="TRow"/>
    /// </summary>
    public override Type RowType => typeof(TRow);

    /// <summary>
    /// 取该列在指定行上的值
    /// </summary>
    /// <param name="row">行对象，允许为 <c>null</c></param>
    /// <returns>行类型匹配时返回取值委托的结果，否则返回 <c>null</c></returns>
    /// <remarks>
    /// 行集合里的 <c>null</c> 元素与异型行都是合法输入，这里返回 <c>null</c> 而不是抛异常，
    /// 让导出侧写成空单元格；数据是否应当为空由调用方在构造行集合时决定。
    /// 框架自带的三条导出路径（<c>.xlsx</c> 全量、<c>.xlsx</c> 流式、文字档）不会把异型行交到这里，
    /// 也不会带着「列的 <see cref="ExcelColumn.RowType"/> 与 <see cref="ExcelSheetSpec.RowType"/> 不符」的规格走到这里：
    /// 前者由取值前的逐笔判定拦，后者由更靠前的声明级预检拦（两条判据见框架侧的行型一致性守卫），
    /// 因此「异型行取到 <c>null</c>」这一分支只剩调用方自己直接调用本方法时看得到。
    /// </remarks>
    public override object? GetValue(object? row)
        => row is TRow typed ? Value(typed) : null;
}
