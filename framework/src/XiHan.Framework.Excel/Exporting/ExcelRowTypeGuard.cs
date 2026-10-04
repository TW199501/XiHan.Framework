// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 行型一致性守卫，xlsx 与文字档两条写出路径共用的唯一一份判据
/// </summary>
/// <remarks>
/// <para>
/// 两道判定。<see cref="ValidateDeclaration"/> 是声明级预检，排在写出第一格之前：
/// <see cref="ExcelSheetSpec.RowType"/> 是 <c>required</c> 非空成员，能取到 <c>null</c> 的只有 <c>null!</c>
/// 这种非法声明，此时没有可比对的类型，一行都不该取。<see cref="ValidateRow"/> 是逐笔判定，对枚举到的每一行都做，
/// 不做「只判第一笔」的抽样——第一笔过了就再不判时，后面的异型行经 <see cref="ExcelColumn.GetValue"/> 只会交回
/// <c>null</c>，放行就是交出一份表头齐全、数据全空、结果却写着成功的档。
/// </para>
/// <para>
/// 比对按 <see cref="Type.IsInstanceOfType(object)"/>，派生行类型放行；<c>null</c> 行本身没有类型可判，
/// 由列的既有契约写成空格，不算异型。行集合始终只枚举一遍：逐笔判定用的就是刚取到的那一行，不物化、不回头再枚举。
/// </para>
/// <para>
/// 两条路径的措辞只差在异常消息开头（由 <c>exportLabel</c> 交出），行位置与期望／实际两个类型全名的点名方式只有一份。
/// </para>
/// </remarks>
internal static class ExcelRowTypeGuard
{
    /// <summary>
    /// 在写出第一格之前确认表规格声明了行类型，交回非 <c>null</c> 的声明类型供逐笔比对
    /// </summary>
    /// <param name="sheet">表规格</param>
    /// <returns>声明的行类型</returns>
    /// <remarks>
    /// 判定放在预检而不是逐行路径上：零行的规格也要被拒——「一行都没有 + 声明缺失」同样是坏声明，
    /// 只有预检这一层同时覆盖零行。跳过判定等于替调用方把坏声明咽下。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><see cref="ExcelSheetSpec.RowType"/> 为 <c>null</c>，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>RowType</c></exception>
    internal static Type ValidateDeclaration(ExcelSheetSpec sheet)
    {
        return sheet.RowType ?? throw new ArgumentNullException(
            nameof(ExcelSheetSpec.RowType),
            $"{nameof(ExcelSheetSpec.RowType)} 为 null：行集合的元素没有可比对的声明类型。" +
            "请用行类型初始化表规格（RowType = typeof(TRow)），null 是非法状态而不是「未填」。");
    }

    /// <summary>
    /// 判定这一笔行对象的实际类型与声明的 <see cref="ExcelSheetSpec.RowType"/> 是否一致，不符即抛
    /// </summary>
    /// <param name="expected">声明的行类型，取自 <see cref="ValidateDeclaration"/></param>
    /// <param name="row">刚枚举到的行对象，允许为 <c>null</c></param>
    /// <param name="rowIndex">该行在行集合里的序号，用于消息里的行位置</param>
    /// <param name="exportLabel">消息开头的路径名（形如「xlsx 导出」），两条路径唯一有差的措辞</param>
    /// <exception cref="InvalidOperationException"><paramref name="row"/> 不是 <paramref name="expected"/> 的实例，
    /// 消息点名行位置与期望／实际两个类型全名</exception>
    internal static void ValidateRow(Type expected, object? row, int rowIndex, string exportLabel)
    {
        // null 行没有类型可判：列的取值方法按既有契约把它写成空格，不算异型
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
