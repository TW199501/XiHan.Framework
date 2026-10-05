// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 单元格样式
/// </summary>
/// <remarks>
/// 只描述框架承诺承载的三项样式，颜色一律用十六进制字符串表达，避免抽象包引入具体库的颜色类型。
/// 文本档（<c>.csv</c>、<c>.txt</c>）不承载样式，提供程序写出该档式时忽略本记录。
/// </remarks>
/// <param name="FontColor">字体颜色（形如 <c>#C00000</c>），<c>null</c> 表示不改变字体颜色</param>
/// <param name="Fill">单元格底色（形如 <c>#D9E1F2</c>），<c>null</c> 表示不设置底色</param>
/// <param name="Bold">是否粗体</param>
public sealed record ExcelTextStyle(
    string? FontColor = null,
    string? Fill = null,
    bool Bold = false);
