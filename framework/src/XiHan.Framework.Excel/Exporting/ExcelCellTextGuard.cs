// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Exporting;

/// <summary>
/// 声明层文案长度守卫：<c>.xlsx</c> 全量与 <c>.xlsx</c> 流式两条写出路径共用的唯一一份表头／标题长度判据
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExcelColumn.Header"/> 与 <see cref="ExcelSheetSpec.Title"/> 与数据格共用
/// <see cref="ExcelConstants.MaxCellTextLength"/> 那一道界。判定排在写出第一格之前，抛出时输出流零字节、
/// 行集合未被枚举，与 <see cref="ExcelRowTypeGuard.ValidateDeclaration"/> 的行型预检同层。
/// </para>
/// <para>
/// 两个导出器都调这里，抛出的消息逐字相同。
/// </para>
/// <para>
/// 超长一律抛出而<u>不截断</u>；消息只报长度与列键，不嵌超长文案本身。
/// </para>
/// <para>
/// 标题全量路径写、流式路径不写，但长度判据两条路径都跑。
/// <see cref="ValidateTitle"/> 把 <c>null</c> 与仅含空白的标题当作未填，不判。
/// </para>
/// </remarks>
internal static class ExcelCellTextGuard
{
    /// <summary>
    /// 检查一列的表头长度，超过 <see cref="ExcelConstants.MaxCellTextLength"/> 即抛
    /// </summary>
    /// <param name="column">要检查的列</param>
    /// <exception cref="ArgumentException"><paramref name="column"/> 的 <see cref="ExcelColumn.Header"/>
    /// 长过 <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>Header</c></exception>
    internal static void ValidateHeader(ExcelColumn column)
    {
        if (column.Header.Length > ExcelConstants.MaxCellTextLength)
        {
            throw CreateOversizedFailure(
                $"键为 {column.Key} 的列，表头",
                column.Header.Length,
                "请缩短表头文案；写出侧不截断——截断会交回一份表头与声明不一致的档。",
                nameof(ExcelColumn.Header));
        }
    }

    /// <summary>
    /// 逐列检查表头长度，任何一列超过 <see cref="ExcelConstants.MaxCellTextLength"/> 即抛
    /// </summary>
    /// <param name="columns">列清单</param>
    /// <exception cref="ArgumentException">某列的 <see cref="ExcelColumn.Header"/> 长过
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>Header</c>，消息点名该列的
    /// <see cref="ExcelColumn.Key"/> 与实际长度</exception>
    internal static void ValidateHeaders(IReadOnlyList<ExcelColumn> columns)
    {
        foreach (var column in columns)
        {
            ValidateHeader(column);
        }
    }

    /// <summary>
    /// 检查标题长度，超过 <see cref="ExcelConstants.MaxCellTextLength"/> 即抛；<c>null</c> 与仅含空白的标题视为未填，不判
    /// </summary>
    /// <param name="title">表规格给的标题原文，尚未做「仅含空白视为未填」的归一</param>
    /// <remarks>
    /// 归一口径由本方法自己持有，调用方交原文进来。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="title"/> 长过
    /// <see cref="ExcelConstants.MaxCellTextLength"/> 个字符，
    /// <see cref="ArgumentException.ParamName"/> 为 <c>Title</c></exception>
    internal static void ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        if (title.Length > ExcelConstants.MaxCellTextLength)
        {
            throw CreateOversizedFailure(
                $"标题（{nameof(ExcelSheetSpec.Title)}）",
                title.Length,
                $"请缩短标题，不写标题行请把 {nameof(ExcelSheetSpec.Title)} 置为 null 或留空白；" +
                "写出侧不截断——截断会交回一份标题与声明不一致的档。",
                nameof(ExcelSheetSpec.Title));
        }
    }

    /// <summary>
    /// 造出文案超过单元格上限时的失败，表头与标题共用这一个消息骨架
    /// </summary>
    /// <param name="subject">主词，点出超长的是哪一项（表头还要带上列键）</param>
    /// <param name="length">实际字符数</param>
    /// <param name="remedy">出路那句</param>
    /// <param name="paramName">异常的 <see cref="ArgumentException.ParamName"/></param>
    /// <returns>可直接抛出的 <see cref="ArgumentException"/></returns>
    private static ArgumentException CreateOversizedFailure(string subject, int length, string remedy, string paramName)
        => new(
            $"{subject}有 {length} 个字符，超过单元格的上限 {ExcelConstants.MaxCellTextLength} 个字符：" +
            "这一项落的也是一格，装不下的文案在 xlsx 里没有对应形态；" +
            "两条 xlsx 写出路径对同一份声明给同一个答案，不按走哪条决定能不能导。" + remedy,
            paramName);
}
