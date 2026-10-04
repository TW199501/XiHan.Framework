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
/// 排序规则（同一行类型每次构建结果一致）：带 <see cref="ExcelColumnAttribute"/> 的属性先按特性
/// <c>Order</c> 升序，同值时按声明顺序；不带该特性的属性一律排在带特性的属性之后，按声明顺序。
/// 排定位置回写为每列的 <see cref="ExcelColumn.Order"/>，因此把结果再按 <c>Order</c> 升序排一次
/// 得到的仍是同一顺序。声明顺序取反射给出的属性顺序，同一程序集内稳定。
/// </para>
/// </remarks>
public static class ExcelColumnBuilder
{
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
        var entries = new List<(PropertyInfo Property, ExcelColumnAttribute? Column)>();

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

            entries.Add((property, property.GetCustomAttribute<ExcelColumnAttribute>()));
        }

        var columns = new List<ExcelColumn>(entries.Count);
        var position = 0;

        // 排序键：0 组是带特性的属性（按特性 Order 升序），1 组是没有特性的属性；两组内部都保持声明顺序
        foreach (var (property, column) in entries
                     .OrderBy(entry => entry.Column is null ? 1 : 0)
                     .ThenBy(entry => entry.Column?.Order ?? 0))
        {
            columns.Add(new ExcelColumn<TRow>
            {
                Key = property.Name,
                Header = column?.Header ?? property.GetDescription(),
                Order = position++,
                Width = column?.Width,
                NumberFormat = column?.NumberFormat,
                Alignment = column?.Alignment ?? ExcelAlignment.Auto,
                Wrap = column?.Wrap ?? false,
                Value = row => property.GetValue(row)
            });
        }

        return columns.AsReadOnly();
    }
}
