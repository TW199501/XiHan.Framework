// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 单张表的导出规格：列清单 + 行集合 + 排版开关
/// </summary>
/// <remarks>
/// <para>
/// 行集合用非泛型 <see cref="IEnumerable"/>，让流式提供程序可以边生成边写出，不强制调用方先物化成列表。
/// </para>
/// <para>
/// <see cref="RowType"/> 由调用方显式给出：从 <see cref="Columns"/> 的实际泛型参数反推不可行（基类擦除了行类型，
/// 但每一列经 <see cref="ExcelColumn.RowType"/> 交出自己约定的类型，所以「列与声明是否同一行类型」这项比对做得出来）。
/// 行集合元素与 <see cref="RowType"/> 的一致性不在这里检查，推迟到提供程序，避免为一次类型检查牺牲多态能力；
/// 提供程序在写出任何内容之前先比 <see cref="Columns"/> 里每一列的 <see cref="ExcelColumn.RowType"/> 能否收下
/// <see cref="RowType"/>（不一致即在开始写出前抛 <see cref="ArgumentException"/>，消息点名列键与两个型别全名），
/// 再对枚举到的每一行比实际型别。列与声明不符等于「每一行都取不到值」，所以它归声明级预检，不等第一行。
/// </para>
/// </remarks>
public sealed class ExcelSheetSpec
{
    private string _sheetName = null!;

    private IEnumerable _rows = null!;

    /// <summary>
    /// 表名称，不允许为 <c>null</c> 或空白
    /// </summary>
    /// <exception cref="ArgumentException">表名为 <c>null</c> 或仅含空白字符</exception>
    public required string SheetName
    {
        get => _sheetName;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("表名称不能为空或仅含空白字符。", nameof(SheetName));
            }

            _sheetName = value;
        }
    }

    /// <summary>
    /// 列清单，顺序即写出顺序
    /// </summary>
    public required IReadOnlyList<ExcelColumn> Columns { get; init; }

    /// <summary>
    /// 行集合，允许为惰性序列
    /// </summary>
    /// <exception cref="ArgumentNullException">行集合为 <c>null</c></exception>
    public required IEnumerable Rows
    {
        get => _rows;
        init => _rows = value ?? throw new ArgumentNullException(nameof(Rows));
    }

    /// <summary>
    /// 行数据类型，供提供程序做取值与错误报表
    /// </summary>
    public required Type RowType { get; init; }

    /// <summary>
    /// 表标题，非空时由提供程序写在表头上方
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 是否冻结表头行，默认冻结
    /// </summary>
    public bool FreezeHeader { get; init; } = true;

    /// <summary>
    /// 是否自动开启筛选，默认开启
    /// </summary>
    public bool AutoFilter { get; init; } = true;

    /// <summary>
    /// 表头是否加粗，默认加粗
    /// </summary>
    public bool HeaderBold { get; init; } = true;

    /// <summary>
    /// 表头底色，默认 <c>#D9E1F2</c>；置为 <c>null</c> 表示不上底色
    /// </summary>
    public string? HeaderFill { get; init; } = "#D9E1F2";

    /// <summary>
    /// 是否画边框，默认画
    /// </summary>
    public bool Borders { get; init; } = true;

    /// <summary>
    /// 预期行数，供提供程序预先判断规模；不填表示未知
    /// </summary>
    public int? ExpectedRowCount { get; init; }

    /// <summary>
    /// 是否强制流式写出的三态标记：<c>null</c> 未表态、<c>true</c> 强制流式、<c>false</c> 强制全量
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExpectedRowCount"/> 一起构成导出分派器的分流输入。本规格对两者都不给默认值，
    /// 未表态时由分派器决定是否放行，不在这里猜测行数。
    /// </remarks>
    public bool? ForceStreaming { get; init; }
}
