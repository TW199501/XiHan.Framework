// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Importing;

/// <summary>
/// 固定宽度文字档的一列定义：取值用的键与该列占用的字节宽度
/// </summary>
/// <remarks>
/// <para>
/// 一列的宽度按<u>字节</u>而不是按字符计，与导出侧
/// <see cref="Abstractions.Exporting.ExcelColumn.FixedWidth"/> 同一口径：一个汉字在 Big5 下占 2 字节、
/// 在 UTF-8 下占 3 字节，按字符切列会让该列之后的每一列整体错位。因此这里的宽度必须与写这份档时用的编码一致，
/// 而那份编码由 <see cref="ExcelImportOptions.TextEncodingName"/> 给出（不指名时由导入侧自动判别，见该设置）。
/// </para>
/// <para>
/// 本类型只是取值载体，不做校验：键是否为空、宽度是否为正整数、多列之间键有没有重复，都由导入侧在
/// 首次取行时一次判完并抛出，理由见 <see cref="ExcelImportOptions.FixedColumns"/>。
/// 列的顺序就是读回一行的取值顺序，导入侧不按键重新排序。
/// </para>
/// <para>
/// 与导出侧的对称限制：<c>[ExcelColumn]</c> 特性不带字节宽度，因此固定宽度的列在两侧都只能由代码给出，
/// 不能靠标注属性得到。
/// </para>
/// </remarks>
/// <param name="Key">
/// 本列在 <see cref="ExcelImportRow.Values"/> 里的键，不能是空字串或仅含空白，且在同一次导入的列清单里必须唯一。
/// 固定宽度路径不读源档的任何行当表头，键名恒取这里的值
/// </param>
/// <param name="WidthBytes">
/// 本列占用的字节宽度，必须是正整数。行里凑不满这一格时取到的是<u>空字串</u>而不是 <c>null</c>，
/// 超出列宽的部分按列定义丢弃，两种情况各留一条 Debug 日志
/// </param>
public sealed record ExcelFixedWidthField(string Key, int WidthBytes);
