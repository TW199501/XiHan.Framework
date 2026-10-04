// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ComponentModel;
using XiHan.Framework.Excel.Abstractions.Attributes;
using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 带导出列特性的测试行类型：两列有特性、一列被标记忽略
/// </summary>
public class AnnotatedRow
{
    /// <summary>
    /// 名称，表头固定为「乙」并排在第 1 位
    /// </summary>
    [ExcelColumn("乙", Order = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 数量，表头固定为「甲」并排在第 10 位，列宽固定 20.5
    /// </summary>
    [ExcelColumn("甲", Order = 10, NumberFormat = "0", Width = 20.5)]
    public int Quantity { get; set; }

    /// <summary>
    /// 密文，不参与导出
    /// </summary>
    [ExcelIgnore]
    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// 完全不带导出特性的测试行类型，表头只能由描述信息回退得到
/// </summary>
public class PlainRow
{
    /// <summary>
    /// 标题，带 <c>Description</c>
    /// </summary>
    [Description("标题")]
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// 有特性列与无特性列混排的测试行类型
/// </summary>
/// <remarks>
/// <see cref="Annotated"/> 声明在两个无特性属性之间，只带特性不带标题，用于同时覆盖
/// 「未指定顺序的列排在显式顺序之后」「标题回退描述信息」和「特性元数据映射到列」三件事。
/// 特性上不写 <c>Width</c>，用于验证特性上的 <c>0</c> 映射成列的「未指定」。
/// </remarks>
public class MixedRow
{
    /// <summary>
    /// 无特性列，声明位置在最前
    /// </summary>
    public string First { get; set; } = string.Empty;

    /// <summary>
    /// 有特性列，标题交由描述信息提供，其余呈现项由特性给出
    /// </summary>
    [Description("甲")]
    [ExcelColumn(Order = 5, NumberFormat = "0.00", Alignment = ExcelAlignment.Center, Wrap = true)]
    public string Annotated { get; set; } = string.Empty;

    /// <summary>
    /// 无特性列，声明位置在最后
    /// </summary>
    public string Second { get; set; } = string.Empty;
}

/// <summary>
/// 显式顺序与未指定顺序混排的测试行类型
/// </summary>
/// <remarks>
/// <see cref="Unordered"/> 只写标题不写 <c>Order</c>，用于验证「未指定顺序的特性列排在所有显式指定顺序的列之后」；
/// <see cref="ZeroOrder"/> 写的是 <c>Order = 0</c>，证明 0 是合法的显式值，不再兼作「未指定」的默认值。
/// <see cref="Tail"/> 完全没有特性，与 <see cref="Unordered"/> 同组，按声明顺序排在它之后。
/// </remarks>
public class PartlyOrderedRow
{
    /// <summary>
    /// 特性只给标题，不给顺序，声明位置在最前
    /// </summary>
    [ExcelColumn("丙")]
    public string Unordered { get; set; } = string.Empty;

    /// <summary>
    /// 显式声明 <c>Order = 0</c> 的特性列
    /// </summary>
    [ExcelColumn("甲", Order = 0)]
    public string ZeroOrder { get; set; } = string.Empty;

    /// <summary>
    /// 显式声明 <c>Order = 1</c> 的特性列
    /// </summary>
    [ExcelColumn("乙", Order = 1)]
    public string Ordered { get; set; } = string.Empty;

    /// <summary>
    /// 完全没有特性的列
    /// </summary>
    public string Tail { get; set; } = string.Empty;
}

/// <summary>
/// 含不能成为列的成员的测试行类型
/// </summary>
public class NonColumnMemberRow
{
    /// <summary>
    /// 可正常成列的属性
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 索引器，没有与之对应的列
    /// </summary>
    public string this[int index]
    {
        get => index == 0 ? Name : string.Empty;
        set => Name = value;
    }

    /// <summary>
    /// 只写属性，取不出值
    /// </summary>
    public string WriteOnly
    {
        set => Name = value;
    }

    /// <summary>
    /// 读取器是私有的，公共读面上取不到值
    /// </summary>
    public string PrivateGetter { private get; set; } = string.Empty;
}
