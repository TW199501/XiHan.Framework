// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Abstractions.Exporting;

/// <summary>
/// 导出结果
/// </summary>
/// <remarks>
/// <para>
/// 样式相关两个字段的配对关系固定为：当且仅当 <see cref="StylingApplied"/> 为 <c>false</c> 时
/// <see cref="StylingSkipReason"/> 才有值。请经由 <see cref="Styled"/> 与 <see cref="Degraded"/> 两个工厂构造，
/// 不要在调用点自己拼这两个字段：<see cref="Degraded"/> 当场拒掉空的理由，配对关系因此不是靠自觉维持的。
/// </para>
/// <para>
/// 文本档（<c>.csv</c>、<c>.txt</c>）本身没有样式概念，写出这些档式时 <c>StylingApplied</c> 为 <c>true</c>
/// 表示「没有丢弃任何请求过的样式」，不是「样式已生效」。
/// </para>
/// </remarks>
/// <param name="Format">实际写出的目标格式</param>
/// <param name="FileExtension">实际写出的文件扩展名（含点，如 <c>.xlsx</c>）</param>
/// <param name="ContentType">实际写出的内容类型</param>
/// <param name="StylingApplied">请求的样式是否全部落地</param>
/// <param name="StylingSkipReason">样式被跳过的原因，仅在 <paramref name="StylingApplied"/> 为 <c>false</c> 时非空</param>
public sealed record ExcelExportResult(
    ExcelFormat Format,
    string FileExtension,
    string ContentType,
    bool StylingApplied,
    string? StylingSkipReason)
{
    /// <summary>
    /// 构造一个样式全部落地的结果
    /// </summary>
    /// <param name="format">实际写出的目标格式</param>
    /// <param name="fileExtension">实际写出的文件扩展名（含点）</param>
    /// <param name="contentType">实际写出的内容类型</param>
    /// <returns>导出结果，样式跳过原因为空</returns>
    public static ExcelExportResult Styled(ExcelFormat format, string fileExtension, string contentType)
        => new(format, fileExtension, contentType, true, null);

    /// <summary>
    /// 构造一个样式被降级的结果，必须说明降级原因
    /// </summary>
    /// <param name="format">实际写出的目标格式</param>
    /// <param name="fileExtension">实际写出的文件扩展名（含点）</param>
    /// <param name="contentType">实际写出的内容类型</param>
    /// <param name="reason">样式被跳过的原因，必须说出丢了什么</param>
    /// <returns>导出结果，样式已落地标记为 <c>false</c></returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> 为 <c>null</c>、空串或仅含空白字符：
    /// 一个没有理由的降级结果与成功结果读起来一样体面，调用方再也问不出丢了什么</exception>
    public static ExcelExportResult Degraded(ExcelFormat format, string fileExtension, string contentType, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ExcelExportResult(format, fileExtension, contentType, false, reason);
    }
}
