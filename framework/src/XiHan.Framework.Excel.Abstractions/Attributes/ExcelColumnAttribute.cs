// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Attributes;

/// <summary>
/// 导出列特性，用于声明属性对应的表头与列呈现
/// </summary>
/// <remarks>
/// 带本特性的属性参与列构建，不带特性的属性同样参与，只是取用默认呈现；要排除某属性请改用
/// <see cref="ExcelIgnoreAttribute"/>。列的取值一律来自属性本身，特性只描述呈现，不描述取数逻辑。
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcelColumnAttribute : Attribute
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public ExcelColumnAttribute()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="header">表头显示文案</param>
    public ExcelColumnAttribute(string header)
    {
        Header = header;
    }

    /// <summary>
    /// 表头显示文案。为 <c>null</c> 时由列构建器回退到属性的描述信息
    /// </summary>
    public string? Header { get; init; }

    /// <summary>
    /// 列顺序，值小的排在前面；默认 <c>-1</c> 表示未指定，未指定的列排在所有显式指定顺序的列之后
    /// </summary>
    /// <remarks>
    /// 只有非负值是合法的显式顺序，<c>0</c> 就是排在最前。负数（含默认的 <c>-1</c>）一律按未指定处理，
    /// 由列构建器把这类列排在显式顺序的列之后，并保持属性的声明顺序。
    /// </remarks>
    public int Order { get; init; } = -1;

    /// <summary>
    /// 列宽（工作簿单位）；默认 <c>0</c> 表示「未指定 / 自动列宽」，由提供程序按采样行自适应
    /// </summary>
    /// <remarks>
    /// 用特性设定列宽时，<c>0</c> 表示「未指定 / 自动列宽」：特性的命名参数不支持可空数值类型，
    /// 因此这里用 <c>double</c> 并以 <c>0</c> 作缺省标记，由列构建器把它映射为列模型上的 <c>null</c>。
    /// <c>0</c> 本身不是可用的列宽，所以这种表达不损失语义；要设非零列宽直接写 <c>Width = 20.5</c> 这样的字面量。
    /// </remarks>
    public double Width { get; init; }

    /// <summary>
    /// Excel 数字或日期格式串（形如 <c>#,##0.00</c>、<c>yyyy-MM-dd</c>），只在 <c>.xlsx</c> 路径生效，
    /// 与 .NET 格式串不通用
    /// </summary>
    public string? NumberFormat { get; init; }

    /// <summary>
    /// 水平对齐方式，默认 <see cref="ExcelAlignment.Auto"/> 表示不显式设置
    /// </summary>
    public ExcelAlignment Alignment { get; init; } = ExcelAlignment.Auto;

    /// <summary>
    /// 是否自动换行
    /// </summary>
    public bool Wrap { get; init; }
}
