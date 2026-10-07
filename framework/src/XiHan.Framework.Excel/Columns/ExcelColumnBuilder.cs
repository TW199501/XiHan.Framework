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
/// 行类型是接口时，基接口声明的属性一并成列；同名属性（<c>new</c> 遮蔽出来的那一对）只留
/// <see cref="MemberInfo.DeclaringType"/> 最深的那一个。
/// </para>
/// <para>
/// 取值委托在建列时编译成强型别调用（每个行类型的清单进缓存，只编译一次），读取器自己的异常型别原样上抛。
/// 例外是 <c>ref</c> 返回的读取器（<c>public ref int Counter =&gt; ref _counter;</c>）：这类属性走反射
/// <c>GetValue</c>，它的 getter 抛出的异常被包成 <see cref="TargetInvocationException"/>。
/// </para>
/// <para>
/// 排序规则（同一行类型每次构建结果一致）：<see cref="ExcelColumnAttribute"/> 上显式写了非负
/// <c>Order</c>（含 <c>0</c>）的属性排在前面，按该值升序，同值时按声明顺序；<c>Order</c> 为负数
/// （默认 <c>-1</c>，即未指定）的属性与没有特性的属性同排在后一组，按声明顺序。
/// 行类型是接口时，并入的基接口属性排在本接口自己声明的属性之前；同一深度内互不派生的多个基接口之间的先后不作保证。
/// 排定位置回写为每列的 <see cref="ExcelColumn.Order"/>。类行类型的声明顺序取反射给出的属性顺序。
/// </para>
/// <para>
/// 特性上的 <c>Width</c> 为 <c>0</c> 表示「未指定 / 自动列宽」，构建器把它映射为列上的 <c>null</c>；
/// 非零的固定列宽必须是有限的正数，负数、<see cref="double.NaN"/> 与无穷大在构建时抛
/// <see cref="ArgumentOutOfRangeException"/> 并点出属性名。没有特性的属性同样得到 <c>null</c>。
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

        foreach (var property in ReadableProperties<TRow>())
        {
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
    /// 编译出的委托直接调用读取器，读取器抛出的异常原样上抛，不包成 <see cref="TargetInvocationException"/>。
    /// 值型别在这里装箱一次。
    /// </para>
    /// <para>
    /// 例外是 <c>ref</c> 返回的读取器（<c>public ref int Counter =&gt; ref _counter;</c>）：表达式树表达不出，
    /// 这类属性照旧成列并走 <c>PropertyInfo.GetValue</c>，它的 getter 抛出的异常被包成
    /// <see cref="TargetInvocationException"/>。
    /// </para>
    /// </remarks>
    private static Func<TRow, object?> CompileGetter<TRow>(PropertyInfo property)
    {
        // ref 返回的读取器表达不进表达式树，改走反射 GetValue
        if (property.GetGetMethod()?.ReturnType.IsByRef == true)
        {
            return row => property.GetValue(row);
        }

        var row = Expression.Parameter(typeof(TRow), "row");
        var access = Expression.Convert(Expression.Property(row, property), typeof(object));

        return Expression.Lambda<Func<TRow, object?>>(access, row).Compile();
    }

    /// <summary>
    /// 取指定行类型可以成列的属性清单：公共可读、非索引器、未标 <see cref="ExcelIgnoreAttribute"/>，
    /// 同名属性只留 <see cref="MemberInfo.DeclaringType"/> 最深的那一个
    /// </summary>
    /// <typeparam name="TRow">行数据类型</typeparam>
    /// <returns>按声明顺序给出的属性清单，位置即去重后第一次出现的位置</returns>
    /// <remarks>
    /// <para>
    /// 接口行类型另外并入基接口声明的属性，顺序是「基接口在前、本接口自己声明的在后」，多个基接口按继承深度
    /// 由远到近排（同一深度内互不派生的基接口之间取 <c>GetInterfaces()</c> 的返回序）；
    /// 类行类型取反射给出的属性原样进候选。
    /// </para>
    /// <para>
    /// 同名属性按名字去重，只留 <see cref="MemberInfo.DeclaringType"/> 最深的那个。
    /// </para>
    /// <para>
    /// 没有公共读取器与索引器的判定在去重之前；<see cref="ExcelIgnoreAttribute"/> 在去重之后判，
    /// 标在 CLR 看得见的那一个上就整键不成列，标在被盖住那一个上不参与判定。
    /// </para>
    /// </remarks>
    private static List<PropertyInfo> ReadableProperties<TRow>()
    {
        var rowType = typeof(TRow);

        // 接口不沿继承链交出属性：基接口的属性按继承深度由远到近并入，排在本接口自己声明的属性之前
        var candidates = rowType.IsInterface
            ? rowType.GetInterfaces()
                .OrderBy(static face => face.GetInterfaces().Length)
                .SelectMany(static face => face.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Concat(rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            : rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var visible = new List<PropertyInfo>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in candidates)
        {
            if (property.GetGetMethod() is null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (positions.TryGetValue(property.Name, out var position))
            {
                if (IsMoreDerived(property.DeclaringType, visible[position].DeclaringType))
                {
                    visible[position] = property;
                }

                continue;
            }

            positions.Add(property.Name, visible.Count);
            visible.Add(property);
        }

        // 去重之后再判忽略标记：只看代表这个键的那一个属性
        var readable = new List<PropertyInfo>(visible.Count);

        foreach (var property in visible)
        {
            if (property.IsDefined(typeof(ExcelIgnoreAttribute)))
            {
                continue;
            }

            readable.Add(property);
        }

        return readable;
    }

    /// <summary>
    /// 比较两个同名属性的归属，判断 candidate 是否比 current 更派生
    /// </summary>
    /// <param name="candidate">后来出现的同名属性的声明类型</param>
    /// <param name="current">已收下位置的属性的声明类型</param>
    /// <returns>candidate 派生自 current 时为 <c>true</c></returns>
    /// <remarks>
    /// 两个型别互不派生时（不相干的基接口声明了同名属性）交回 <c>false</c>，留先出现的那一个。
    /// </remarks>
    private static bool IsMoreDerived(Type? candidate, Type? current)
        => candidate is not null
           && current is not null
           && !ReferenceEquals(candidate, current)
           && current.IsAssignableFrom(candidate)
           && !candidate.IsAssignableFrom(current);

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
