// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Importing;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 两条导入路径共用的行数上限收敛与「整行皆空」判定
/// </summary>
/// <remarks>
/// <para>
/// 这两条判据与来源无关：<see cref="ExcelImportOptions.MaxRowCount"/> 的上界校验在容器路径与固定宽度路径
/// 必须是同一个数、同一种报法，<see cref="ExcelImportOptions.SkipEmptyRows"/> 说的「整行皆空」也不能一个来源
/// 只看 <c>null</c>、另一个来源把空格也算空。抽在这里，两个导入器共用一份，不再各写一遍。
/// </para>
/// <para>
/// 上限取 <see cref="ExcelConstants.DefaultMaxImportRows"/> 常量而不是
/// <c>XiHanExcelOptions.MaxImportRows</c>：两个导入器都按无参/日志器构造使用，裸选项对象进不来。
/// 应用把配置项调得更低时要在分派器那侧生效（配置面接入是既定的后续派工项），这里只是那道不可突破的上限。
/// </para>
/// </remarks>
internal static class ImportSharedRules
{
    /// <summary>
    /// 把单次导入的行数上限收敛到框架硬上限之内
    /// </summary>
    /// <param name="requested">调用方给的上限，<c>null</c> 表示用框架硬上限</param>
    /// <returns>本次实际可用的行数上限</returns>
    /// <exception cref="ArgumentOutOfRangeException">上限高于硬上限，或不是正整数</exception>
    internal static int ResolveMaxRowCount(int? requested)
    {
        if (requested is null)
        {
            return ExcelConstants.DefaultMaxImportRows;
        }

        var value = requested.Value;

        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExcelImportOptions.MaxRowCount),
                value,
                "MaxRowCount 必须是正整数；要按框架默认上限读请传 null，不要写 0。");
        }

        if (value > ExcelConstants.DefaultMaxImportRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExcelImportOptions.MaxRowCount),
                value,
                $"MaxRowCount 不能高于框架硬上限 {ExcelConstants.DefaultMaxImportRows} 行：" +
                $"这道上限挡住的是「调用方把内存里的行数放大到无穷」，应用只能收紧、不能放宽。");
        }

        return value;
    }

    /// <summary>
    /// 判断整行皆空
    /// </summary>
    /// <param name="values">本行取值</param>
    /// <remarks>
    /// 「空」只看 <c>null</c> 与空字串：<see cref="ExcelImportOptions.TrimValues"/> 为 <c>true</c> 时全空格行
    /// 已经剥成空字串，因此也算空行；为 <c>false</c> 时 <c>" "</c> 是实数据，不算空行。
    /// </remarks>
    internal static bool IsEmptyRow(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var value in values.Values)
        {
            if (value is not null && value is not "")
            {
                return false;
            }
        }

        return true;
    }
}
