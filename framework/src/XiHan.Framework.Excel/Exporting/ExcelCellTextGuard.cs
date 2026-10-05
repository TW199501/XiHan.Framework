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
/// <see cref="ExcelColumn.Header"/> 与 <see cref="ExcelSheetSpec.Title"/> 落的都是单元格，因此与数据格共用
/// <see cref="ExcelConstants.MaxCellTextLength"/> 那一道界。它们是<u>声明</u>而不是行值：不需要取到任何一行就判得出来，
/// 所以判定排在写出第一格之前，抛出时输出流零字节、行集合一次都没被枚举，与
/// <see cref="ExcelRowTypeGuard.ValidateDeclaration"/> 的行型预检同层。
/// </para>
/// <para>
/// 判据只有一份，两个导出器都只调这里，不各写一遍字面比较。理由是分派器按行数把同一份规格送到其中一条路径，
/// 调用方并没有选择权：同一份声明在一条路径上被拒、在另一条上照写，等于让「能不能导」变成走哪条的副产品。
/// 两条路径抛出的消息因此逐字相同，测试也按逐字相同钉住。
/// </para>
/// <para>
/// 超长一律抛出而<u>不截断</u>：截断会交回一份文案与声明不一致的档，而结果仍写着成功。消息只报长度与列键，
/// 不嵌那串超长文案本身——四万个字符整段进异常消息，读到的是噪音而不是成因。消息是政策陈述，
/// 不描述工作簿或写出库在这一步会做什么。
/// </para>
/// <para>
/// 标题这一项两条路径的<u>处置</u>并不相同：全量路径写标题行，流式路径不写（它在降级理由里逐条点名）。
/// 但长度判据两边都跑——本路径不承载某个选项，与这个选项的声明非法是两件事，前者照既有口径不报错，
/// 后者两条路径一起拒，免得同一份带超长标题的规格走全量抛、走流式却静默交回一份档。
/// <see cref="ValidateTitle"/> 沿用全量路径既有的口径把 <c>null</c> 与仅含空白的标题当作未填，不判。
/// </para>
/// </remarks>
internal static class ExcelCellTextGuard
{
    /// <summary>
    /// 检查一列的表头长度，超过 <see cref="ExcelConstants.MaxCellTextLength"/> 即抛
    /// </summary>
    /// <param name="column">要检查的列</param>
    /// <remarks>
    /// 逐列给出而不是整份清单一次判完，为的是让调用方能把这一步嵌进自己既有的逐列循环里、
    /// 保持与其它列级判定的先后次序不变（全量路径的列宽与对齐消息要把表头原文嵌进去，
    /// 超长表头必须先被这一步拦下来，否则那句消息本身就带着整串文案抛出）。
    /// </remarks>
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
    /// 归一口径由本方法自己持有，两个调用方都交原文进来：全量路径另有一份用于决定标题行占不占位的归一，
    /// 两处必须给出同一个答案，否则「判过了却不写」与「没判却写了」都会出现。
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
    /// 造出文案超过单元格上限时的失败：两条判据共用这一个消息骨架，只在主词与出路上不同
    /// </summary>
    /// <param name="subject">主词，点出超长的是哪一项（表头还要带上列键）</param>
    /// <param name="length">实际字符数</param>
    /// <param name="remedy">出路那句</param>
    /// <param name="paramName">异常的 <see cref="ArgumentException.ParamName"/></param>
    /// <returns>可直接抛出的 <see cref="ArgumentException"/></returns>
    /// <remarks>
    /// 骨架里写明「两条 xlsx 写出路径对同一份声明给同一个答案」：这句是政策陈述，说的是本组件自己的口径，
    /// 不断言工作簿或写出库在这一步会做什么。
    /// </remarks>
    private static ArgumentException CreateOversizedFailure(string subject, int length, string remedy, string paramName)
        => new(
            $"{subject}有 {length} 个字符，超过单元格的上限 {ExcelConstants.MaxCellTextLength} 个字符：" +
            "这一项落的也是一格，装不下的文案在 xlsx 里没有对应形态；" +
            "两条 xlsx 写出路径对同一份声明给同一个答案，不按走哪条决定能不能导。" + remedy,
            paramName);
}
