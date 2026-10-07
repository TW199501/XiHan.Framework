// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 行型一致性守卫，<c>.xlsx</c> 全量、<c>.xlsx</c> 流式与文字档三条写出路径共用的唯一一份判据
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateDeclaration"/> 是声明级预检，排在写出第一格之前：<see cref="ExcelSheetSpec.RowType"/>
/// 为 <c>null</c> 时抛出，再逐列比对 <see cref="ExcelColumn.RowType"/> 与 <see cref="ExcelSheetSpec.RowType"/>。
/// <see cref="ValidateRow"/> 是逐笔判定，对枚举到的每一行都做。
/// </para>
/// <para>
/// 逐笔比对按 <see cref="Type.IsInstanceOfType(object)"/>，派生行类型放行；<c>null</c> 行不算异型。
/// 行集合只枚举一遍，不物化。
/// </para>
/// <para>
/// 列与声明的兼容性按 <see cref="Type.IsAssignableFrom(Type)"/> 判：声明 <c>RowType</c> 取列的
/// <see cref="ExcelColumn.RowType"/> 的派生类型放行；取不相关的类型或 <see cref="object"/> 一律拒。
/// </para>
/// <para>
/// 各路径的异常消息只在开头（由 <c>exportLabel</c> 交出）不同。
/// </para>
/// </remarks>
internal static class ExcelRowTypeGuard
{
    /// <summary>
    /// 在写出第一格之前确认表规格的行型声明站得住：声明非空，且每一列都认这个声明类型，交回声明类型供逐笔比对
    /// </summary>
    /// <param name="sheet">表规格</param>
    /// <returns>声明的行类型</returns>
    /// <remarks>
    /// 零行的规格同样判。逐列比对的是 <see cref="ExcelColumn.RowType"/> 能否收下 <see cref="ExcelSheetSpec.RowType"/>
    /// 的实例：列是 <c>ExcelColumn&lt;Base&gt;</c>、声明是 <c>Derived</c> 时放行。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>RowType</c></exception>
    /// <exception cref="ArgumentException">某列的 <see cref="ExcelColumn.RowType"/> 收不下
    /// <see cref="ExcelSheetSpec.RowType"/>，<see cref="ArgumentException.ParamName"/> 为 <c>RowType</c>，
    /// 消息点名该列的 <see cref="ExcelColumn.Key"/> 与两个型别全名</exception>
    internal static Type ValidateDeclaration(ExcelSheetSpec sheet)
    {
        var rowType = sheet.RowType ?? throw new ArgumentNullException(
            nameof(ExcelSheetSpec.RowType),
            $"{nameof(ExcelSheetSpec.RowType)} 为 null：行集合的元素没有可比对的声明类型。" +
            "请用行类型初始化表规格（RowType = typeof(TRow)），null 是非法状态而不是「未填」。");

        foreach (var column in sheet.Columns)
        {
            if (column.RowType.IsAssignableFrom(rowType))
            {
                continue;
            }

            throw new ArgumentException(
                $"{nameof(ExcelSheetSpec.RowType)} 与列「{column.Key}」约定的行类型不一致：" +
                $"这一列只认「{column.RowType.FullName}」的行，表规格声明的是「{rowType.FullName}」。" +
                "这样的列对每一行取值都只会得到 null，放行就是交出一份表头齐全、数据全空的档。" +
                "请用与行类型同源的列清单（ExcelColumnBuilder.CreateColumns<TRow>() 建出的列配同一 TRow 的 RowType），" +
                "或把 RowType 改为列约定的类型；写成 object 不算放宽，object 没有该列认识的成员。",
                nameof(ExcelSheetSpec.RowType));
        }

        return rowType;
    }

    /// <summary>
    /// 判定这一笔行对象的实际类型与声明的 <see cref="ExcelSheetSpec.RowType"/> 是否一致，不符即抛
    /// </summary>
    /// <param name="expected">声明的行类型，取自 <see cref="ValidateDeclaration"/></param>
    /// <param name="row">刚枚举到的行对象，允许为 <c>null</c></param>
    /// <param name="rowIndex">该行在行集合里的序号，用于消息里的行位置</param>
    /// <param name="exportLabel">消息开头的路径名（形如「xlsx 导出」）</param>
    /// <exception cref="InvalidOperationException"><paramref name="row"/> 不是 <paramref name="expected"/> 的实例，
    /// 消息点名行位置与期望／实际两个类型全名</exception>
    internal static void ValidateRow(Type expected, object? row, int rowIndex, string exportLabel)
    {
        // null 行不算异型：列的取值方法把它写成空格
        if (row is null || expected.IsInstanceOfType(row))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{exportLabel}无法完成：第 {rowIndex} 行的行对象与 {nameof(ExcelSheetSpec.RowType)} 不符，" +
            $"期望「{expected.FullName}」，实际是「{row.GetType().FullName}」。" +
            "请给出该类型的行集合，或把 RowType 改为实际行类型——异型行经列的取值方法只会得到 null，" +
            "放行就是交出一份表头齐全、数据全空的档。");
    }
}
