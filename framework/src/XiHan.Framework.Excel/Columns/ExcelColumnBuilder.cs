// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Linq.Expressions;
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
/// 取值委托在建列时编译成强型别调用（每个行类型的清单进缓存，只编译一次），不在逐格走反射：
/// 反射会把属性 getter 自己抛出的异常包成 <see cref="TargetInvocationException"/>，那与手写
/// <see cref="ExcelColumn{TRow}"/> 的 <c>r =&gt; r.X</c> 不是同一个型别，而 <c>TargetInvocationException</c>
/// 不在任何导出入口的 <c>&lt;exception&gt;</c> 清单里——调用方按文档化的型别去 <c>catch</c>，
/// 偏偏因为「列是谁建的」而接不到。
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
                Value = CompileGetter<TRow>(property)
            });
        }

        return columns.AsReadOnly();
    }

    /// <summary>
    /// 把属性的读取器编译成强型别取值委托
    /// </summary>
    /// <typeparam name="TRow">行数据类型</typeparam>
    /// <param name="property">已经过筛选、带公共读取器的属性</param>
    /// <returns>与手写 <c>r =&gt; r.X</c> 同形状的取值委托</returns>
    /// <remarks>
    /// <para>
    /// 不用 <c>PropertyInfo.GetValue</c>：反射会把读取器自己抛出的异常包成
    /// <see cref="TargetInvocationException"/>，于是同一契约的两条列来源（手写
    /// <see cref="ExcelColumn{TRow}"/> 与本构建器产出的列）交出不同的异常型别——调用方按导出入口文档化的型别
    /// <c>catch</c>，偏偏因为「列是谁建的」而接不到，而 <c>TargetInvocationException</c> 不在任何导出入口的
    /// <c>&lt;exception&gt;</c> 清单里。编译出的委托就是对读取器的一次直接调用，异常原样上抛。
    /// </para>
    /// <para>
    /// 顺带去掉每格一次反射 invoke 的代价；值型别仍在这里装箱一次，因为列的取值契约交回的就是 <c>object?</c>。
    /// 编译只做在建列时（每个行类型的清单进缓存，见 <see cref="CreateColumns{TRow}"/>），不在逐格路径上。
    /// </para>
    /// <para>
    /// 唯一的例外是 <c>ref</c> 返回的读取器（<c>public ref int Counter =&gt; ref _counter;</c>）：表达式树没有
    /// 「取引用所指的值」这个节点，<c>Expression.Convert</c> 对 <c>Int32&amp;</c> 到 <c>object</c> 在建列时就抛
    /// <see cref="InvalidOperationException"/>（探针 <c>t13-b3-probe-shapes2.txt</c> 实测），而反射读得出来。
    /// 这类属性照旧成列、照旧走 <c>GetValue</c>——判它「不成列」等于让原本能导出的行类型静默少一栏，
    /// 让建列时就抛等于新加一种失败，两条都不如留着反射这一条。代价如实记在这里：这种形状的 getter
    /// 抛出的异常仍被包成 <see cref="TargetInvocationException"/>，只有它一条如此。
    /// </para>
    /// </remarks>
    private static Func<TRow, object?> CompileGetter<TRow>(PropertyInfo property)
    {
        if (property.GetGetMethod()?.ReturnType.IsByRef == true)
        {
            return row => property.GetValue(row);
        }

        var row = Expression.Parameter(typeof(TRow), "row");
        var access = Expression.Convert(Expression.Property(row, property), typeof(object));

        return Expression.Lambda<Func<TRow, object?>>(access, row).Compile();
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
