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
/// zip 的 4 字节 <c>50 4B 03 04</c>（<c>.xlsx</c> 容器）。其余一律返回 <c>null</c>，包括文字档与
/// HTML 表格、XML 表格等伪装成 Excel 的文本。
/// </para>
/// <para>
/// 副档名不参与判断：本类型只看字节，档名叫 <c>.xls</c> 而内容是 <c>.xlsx</c> 判为 <see cref="ExcelImportFormat.Xlsx"/>。
/// </para>
/// <para>
/// 嗅探要求流可定位：读完档头把位置复原，不可定位的流直接拒绝。
/// </para>
/// </remarks>
internal static class ExcelFormatProbe
{
    /// <summary>
    /// 判别与错误消息用到的档头长度
    /// </summary>
    /// <remarks>
    /// 十六字节容得下前导字节加起始标记：UTF-32 的前导字节占四位，UTF-16 的 <c>&lt;html</c>
    /// 占十位，UTF-8 的 <c>&lt;?xml</c> 占八位。
    /// </remarks>
    internal const int HeaderByteCount = 16;

    /// <summary>
    /// OLE 复合文件（旧版二进制工作簿容器）的 8 字节签名
    /// </summary>
    private static readonly byte[] OleCompoundSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    /// zip 容器（Office Open XML 工作簿）的 4 字节签名
    /// </summary>
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// 宽松解码的 UTF-16 little endian，只用于认标记形态
    /// </summary>
    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    /// <summary>
    /// 宽松解码的 UTF-16 big endian，只用于认标记形态
    /// </summary>
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);

    /// <summary>
    /// 宽松解码的 UTF-32 little endian，只用于认标记形态
    /// </summary>
    private static readonly Encoding Utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false);

    /// <summary>
    /// 宽松解码的 UTF-32 big endian，只用于认标记形态
    /// </summary>
    private static readonly Encoding Utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: false);

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
    /// 从流的起点（位置 0）开始读，而不是从调用方留下的位置读；读完把位置复原回原处。
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
            // 无论读到几个字节都复位到调用方留下的位置
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
    /// <para>
    /// 返回值只用于判别失败的消息，不参与任何解析路径。
    /// 档头截断在标记中间时返回可见的部分。
    /// </para>
    /// <para>
    /// 先剥掉档头的前导字节（BOM）再认形态，例如 SpreadsheetML 的 <c>EF BB BF 3C 3F 78 6D 6C</c>、
    /// UTF-16 HTML 的 <c>FF FE 3C 00 68 00</c>。
    /// UTF-32 的两个前导字节与 UTF-16 LE 的前两位相同，判定排在 UTF-16 之前。
    /// </para>
    /// <para>
    /// 这里按前导字节取的编码只用来认形态，一律宽松解码，与 <see cref="TextEncodingResolver"/> 无关。
    /// </para>
    /// </remarks>
    internal static string? DescribeMarkupOpening(ReadOnlySpan<byte> header)
    {
        var encoding = PreambleEncoding(header, out var preambleLength);
        var body = header[preambleLength..];

        if (body.IsEmpty)
        {
            return null;
        }

        // 按认出来的编码宽松取字：这段只用来认形态，非法字节换成替换字符不影响判定
        var text = encoding.GetString(body).AsSpan();
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

    /// <summary>
    /// UTF-8 的前导字节
    /// </summary>
    private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// UTF-16 little endian 的前导字节
    /// </summary>
    private static ReadOnlySpan<byte> Utf16LePreamble => [0xFF, 0xFE];

    /// <summary>
    /// UTF-16 big endian 的前导字节
    /// </summary>
    private static ReadOnlySpan<byte> Utf16BePreamble => [0xFE, 0xFF];

    /// <summary>
    /// UTF-32 little endian 的前导字节（前两位与 UTF-16 LE 相同，必须排在它前面判）
    /// </summary>
    private static ReadOnlySpan<byte> Utf32LePreamble => [0xFF, 0xFE, 0x00, 0x00];

    /// <summary>
    /// UTF-32 big endian 的前导字节
    /// </summary>
    private static ReadOnlySpan<byte> Utf32BePreamble => [0x00, 0x00, 0xFE, 0xFF];

    /// <summary>
    /// 认档头的前导字节，交出用它取字所用的宽松编码与该前导字节的长度
    /// </summary>
    /// <param name="header">档头字节</param>
    /// <param name="preambleLength">认出来的前导字节长度，没有可识别前导字节时为 <c>0</c></param>
    /// <returns>按前导字节选出的编码；没有可识别前导字节时是 <see cref="Encoding.UTF8"/></returns>
    private static Encoding PreambleEncoding(ReadOnlySpan<byte> header, out int preambleLength)
    {
        if (header.StartsWith(Utf32LePreamble))
        {
            preambleLength = Utf32LePreamble.Length;

            return Utf32Le;
        }

        if (header.StartsWith(Utf32BePreamble))
        {
            preambleLength = Utf32BePreamble.Length;

            return Utf32Be;
        }

        if (header.StartsWith(Utf8Preamble))
        {
            preambleLength = Utf8Preamble.Length;

            return Encoding.UTF8;
        }

        if (header.StartsWith(Utf16LePreamble))
        {
            preambleLength = Utf16LePreamble.Length;

            return Utf16Le;
        }

        if (header.StartsWith(Utf16BePreamble))
        {
            preambleLength = Utf16BePreamble.Length;

            return Utf16Be;
        }

        preambleLength = 0;

        return Encoding.UTF8;
    }
}
