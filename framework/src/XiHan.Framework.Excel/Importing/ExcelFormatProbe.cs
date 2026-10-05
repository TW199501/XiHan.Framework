// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 按档头签章判别导入格式，供自动判别路径使用
/// </summary>
/// <remarks>
/// <para>
/// 判据只有两类可靠签名：OLE 复合文件的 8 字节 <c>D0 CF 11 E0 A1 B1 1A E1</c>（旧版 <c>.xls</c> 容器）与
/// zip 的 4 字节 <c>50 4B 03 04</c>（<c>.xlsx</c> 容器）。其余一律返回 <c>null</c>：文字档没有签名，
/// HTML 表格、XML 表格等「伪装成 Excel 的文本」也没有任何一段固定字节可以当成身份依据，
/// 猜一个格式就会把外来数据当正常数据交回调用方。
/// </para>
/// <para>
/// 副档名不参与判断：本类型只看字节，档名叫 <c>.xls</c> 而内容是 <c>.xlsx</c> 判为 <see cref="ExcelImportFormat.Xlsx"/>。
/// </para>
/// <para>
/// 嗅探要求流可定位，因为「不吃掉调用方的流」是硬要求：读完档头一定把位置复位。不可定位的流复位不了，
/// 只读掉前几字节就把格式判回来，等于把调用方的流留在半读状态，因此直接拒绝。
/// </para>
/// </remarks>
internal static class ExcelFormatProbe
{
    /// <summary>
    /// 判别与错误消息用到的档头长度
    /// </summary>
    internal const int HeaderByteCount = 8;

    /// <summary>
    /// OLE 复合文件（旧版二进制工作簿容器）的 8 字节签名
    /// </summary>
    private static readonly byte[] OleCompoundSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    /// zip 容器（Office Open XML 工作簿）的 4 字节签名
    /// </summary>
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// 读取档头并判别格式，嗅探后把流位置复原
    /// </summary>
    /// <param name="input">输入流，必须可读且可定位</param>
    /// <returns>判出的格式；判不出来返回 <c>null</c>（含文字档与任何伪装成 Excel 的文本），不抛</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位</exception>
    internal static ExcelImportFormat? Detect(Stream input)
        => Detect(ReadHeader(input, HeaderByteCount));

    /// <summary>
    /// 按已取到的档头字节判别格式
    /// </summary>
    /// <param name="header">档头字节，长度不足签名长度时判不出来</param>
    /// <returns>判出的格式；判不出来返回 <c>null</c></returns>
    internal static ExcelImportFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= OleCompoundSignature.Length && header.StartsWith(OleCompoundSignature))
        {
            return ExcelImportFormat.Xls;
        }

        if (header.Length >= ZipSignature.Length && header.StartsWith(ZipSignature))
        {
            return ExcelImportFormat.Xlsx;
        }

        return null;
    }

    /// <summary>
    /// 取档头字节，读完把流位置复原到调用方原来的位置
    /// </summary>
    /// <param name="input">输入流，必须可读且可定位</param>
    /// <param name="count">要读的字节数，不足时返回实际读到的字节</param>
    /// <returns>档头字节；空档返回长度 <c>0</c> 的数组，不抛</returns>
    /// <remarks>
    /// 「档头」永远在流的<u>起点</u>，所以本方法从 0 开始读，而不是从调用方留下的位置读：位置停在中间的流上
    /// 读出来的那段既不是文件身份也不是文档开头，判据在它上没有意义。读完把位置复原回原处，嗅探不吃调用方的流。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 不是正整数</exception>
    internal static byte[] ReadHeader(Stream input, int count)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.CanRead)
        {
            throw new ArgumentException("输入流不可读，无法判别导入格式。", nameof(input));
        }

        if (!input.CanSeek)
        {
            throw new ArgumentException(
                "格式嗅探要求输入流可定位：嗅探要读完档头把位置复原，不可定位的流做不到，" +
                "留在流里的半读状态会让后续解析少掉开头的字节。请交给可定位的流（例如 MemoryStream）。",
                nameof(input));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var origin = input.Position;
        var buffer = new byte[count];

        input.Position = 0;

        try
        {
            var filled = 0;
            while (filled < count)
            {
                // 单次 Read 不保证读满，循环补足到 count 或读到档尾
                var read = input.Read(buffer, filled, count - filled);
                if (read <= 0)
                {
                    break;
                }

                filled += read;
            }

            return filled == count ? buffer : buffer[..filled];
        }
        finally
        {
            // 无论读到几个字节都要复位到调用方留下的位置，嗅探不能吃调用方的流
            input.Position = origin;
        }
    }

    /// <summary>
    /// 把档头字节转成「十六进制 ＋ 可打印形式」，供判别失败的消息使用
    /// </summary>
    /// <param name="header">档头字节</param>
    /// <returns>形如 <c>D0 CF 11 E0 A1 B1 1A E1 ｜ ········</c> 的可读形式，不可打印字节以 <c>·</c> 代替</returns>
    internal static string DescribeHeader(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return "（档为空，没有任何字节可比对）";
        }

        var hex = new StringBuilder(header.Length * 3);
        var text = new StringBuilder(header.Length);

        foreach (var current in header)
        {
            hex.Append(current.ToString("X2")).Append(' ');
            text.Append(current is >= 0x20 and <= 0x7E ? (char)current : '·');
        }

        return $"{hex.ToString().TrimEnd()} ｜ {text}";
    }

    /// <summary>
    /// 识别档头是不是标记语言文本（HTML 表格、XML 表格这类伪装）
    /// </summary>
    /// <param name="header">档头字节</param>
    /// <returns>识别出来时返回起始标记（形如 <c>&lt;html</c>），否则返回 <c>null</c></returns>
    /// <remarks>
    /// 只做识别、不做猜测：返回值只出现在「判不出来、要求调用方指名格式」的消息里，不参与任何解析路径。
    /// 取不到完整标记时（档头截断在标记中间）返回可见的部分，消息照旧点名 HTML 表格与 XML 表格。
    /// </remarks>
    internal static string? DescribeMarkupOpening(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return null;
        }

        // 按 UTF-8 宽松取字：这段只用来认形态，非法字节换成替换字符不影响判定
        var text = Encoding.UTF8.GetString(header).AsSpan();
        var start = 0;

        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start == text.Length || text[start] != '<')
        {
            return null;
        }

        // 取到第一个空白、尖括号或斜杠为止，得到 <html、<?xml、<table 这类标记名
        var tag = text[start..];
        var end = tag.Length;

        for (var i = 1; i < tag.Length; i++)
        {
            if (char.IsWhiteSpace(tag[i]) || tag[i] is '>' or '/')
            {
                end = i;
                break;
            }
        }

        return tag[..end].ToString();
    }
}
