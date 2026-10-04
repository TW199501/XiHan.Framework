// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Reflection;
using XiHan.Framework.Excel.Abstractions.Attributes;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Utils.Reflections;

namespace XiHan.Framework.Excel.Columns;

/// <summary>
/// 属性驱动的导出列构建器
/// </summary>
/// <remarks>
/// <para>
/// 列来源是行类型的公共实例属性：没有公共读取器的属性和索引器不成列，标了 <see cref="ExcelIgnoreAttribute"/>
/// 的属性不成列；标了 <see cref="ExcelColumnAttribute"/> 的属性按特性给出表头与呈现项，特性没给表头时
/// 取属性的描述信息；两个特性都没标的属性照常成列，表头同样取描述信息，呈现项取列的默认值。
/// </para>
/// <para>
/// 排序规则（同一行类型每次构建结果一致）：<see cref="ExcelColumnAttribute"/> 上显式写了非负
/// <c>Order</c>（含 <c>0</c>）的属性排在前面，按该值升序，同值时按声明顺序；<c>Order</c> 为负数
/// （默认 <c>-1</c>，即未指定）的属性与没有特性的属性同排在后一组，按声明顺序。
/// 排定位置回写为每列的 <see cref="ExcelColumn.Order"/>，因此把结果再按 <c>Order</c> 升序排一次
/// 得到的仍是同一顺序。声明顺序取反射给出的属性顺序，同一程序集内稳定。
/// </para>
/// <para>
/// 列宽的缺省标记：特性上的 <c>Width</c> 是 <c>double</c>（可空数值不能作特性参数），用特性设定列宽时，
/// <c>0</c> 表示「未指定 / 自动列宽」，构建器把它映射为列上的 <c>null</c>；非零的固定列宽必须是有限的正数，
/// 负数、<see cref="double.NaN"/> 与无穷大在构建时抛 <see cref="ArgumentOutOfRangeException"/> 并点出属性名，
/// 不会被当成合法的固定列宽收下。没有特性的属性同样得到 <c>null</c>。
/// </para>
/// </remarks>
public static class ExcelColumnBuilder
{
    /// <summary>
    /// 特性上「未指定列顺序」的取值，负数一律按未指定处理
    /// </summary>
    private const int UnspecifiedOrder = -1;

    /// <summary>
    /// 特性上「未指定列宽」的取值，对应列模型上的 <c>null</c>
    /// </summary>
    private const double UnspecifiedWidth = 0;

    /// <summary>
    /// 行类型到列清单的缓存
    /// </summary>
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<ExcelColumn>> Cache = new();

    /// <summary>
    /// 取得指定行类型的导出列清单，结果按行类型缓存
    /// </summary>
    /// <typeparam name="TRow">行数据类型</typeparam>
    /// <returns>按写出顺序排列的列清单；同一行类型重复调用返回同一引用</returns>
    public static IReadOnlyList<ExcelColumn> CreateColumns<TRow>()
    {
        return Cache.GetOrAdd(typeof(TRow), static _ => Build<TRow>());
    }

    /// <summary>
    /// 清除全部行类型的列缓存
    /// </summary>
    public static void ClearCache()
    {
        Cache.Clear();
    }

    /// <summary>
    /// 清除指定行类型的列缓存
    /// </summary>
    /// <param name="rowType">行数据类型</param>
    /// <exception cref="ArgumentNullException"><paramref name="rowType"/> 为 <c>null</c></exception>
    public static void ClearCache(Type rowType)
    {
        ArgumentNullException.ThrowIfNull(rowType);

        Cache.TryRemove(rowType, out _);
    }

    /// <summary>
    /// 反射构建指定行类型的列清单
    /// </summary>
    private static IReadOnlyList<ExcelColumn> Build<TRow>()
    {
        var entries = new List<(PropertyInfo Property, ExcelColumnAttribute? Column, bool OrderSpecified, int DeclaredOrder)>();

        foreach (var property in typeof(TRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetGetMethod() is null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.IsDefined(typeof(ExcelIgnoreAttribute)))
            {
                continue;
            }

            var column = property.GetCustomAttribute<ExcelColumnAttribute>();

            // 没有特性的属性与特性未指定顺序（负数）的属性同属「未指定」，一律取 UnspecifiedOrder
            var declaredOrder = column?.Order ?? UnspecifiedOrder;

            entries.Add((property, column, declaredOrder >= 0, declaredOrder));
        }

        var columns = new List<ExcelColumn>(entries.Count);
        var position = 0;

        // 排序键：0 组是显式写了非负 Order 的属性（按该值升序），1 组是未指定顺序的属性；
        // 后一组的次键取常量 0，让稳定排序保持属性的声明顺序
        foreach (var (property, column, _, _) in entries
                     .OrderBy(entry => entry.OrderSpecified ? 0 : 1)
                     .ThenBy(entry => entry.OrderSpecified ? entry.DeclaredOrder : 0))
        {
            columns.Add(new ExcelColumn<TRow>
            {
                Key = property.Name,
                Header = column?.Header ?? property.GetDescription(),
                Order = position++,
                Width = MapWidth(property.Name, column),
                NumberFormat = column?.NumberFormat,
                Alignment = column?.Alignment ?? ExcelAlignment.Auto,
                Wrap = column?.Wrap ?? false,
                Value = row => property.GetValue(row)
            });
        }

        return columns.AsReadOnly();
    }

    /// <summary>
    /// 把特性上的列宽映射为列模型的列宽，<see cref="UnspecifiedWidth"/> 映射为 <c>null</c>
    /// </summary>
    /// <param name="propertyName">属性名，出现在拒绝非法列宽的异常信息里</param>
    /// <param name="column">属性上的导出列特性，没有特性时为 <c>null</c></param>
    /// <returns>固定列宽，或表示「未指定 / 自动列宽」的 <c>null</c></returns>
    /// <exception cref="ArgumentOutOfRangeException">特性写的列宽是负数、<see cref="double.NaN"/> 或无穷大</exception>
    private static double? MapWidth(string propertyName, ExcelColumnAttribute? column)
    {
        if (column is null || column.Width == UnspecifiedWidth)
        {
            return null;
        }

        if (column.Width < 0 || !double.IsFinite(column.Width))
        {
            throw new ArgumentOutOfRangeException(
                nameof(column.Width),
                column.Width,
                $"属性「{propertyName}」的导出列宽 {column.Width} 非法：固定列宽必须是有限的正数，0 表示未指定（自动列宽）。");
        }

        return column.Width;
    }
}
