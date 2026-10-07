// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using XiHan.Framework.Excel.Text;

namespace XiHan.Framework.Excel.Importing;

/// <summary>
/// 文字档解码编码的解析与自动判别
/// </summary>
/// <remarks>
/// <para>
/// 本类型只管导入侧的编码判定，按「谁来解码」分两条路：
/// <list type="bullet">
/// <item><see cref="Resolve"/> 交回一个 <see cref="Encoding"/>，给自己解码的调用方用（固定宽度导入）。
/// 无 BOM 时按档头 <c>32KB</c> 的窗口严格试解 UTF-8，失败才回退 Big5。</item>
/// <item><see cref="ResolveForReader"/> 交回要交给 ExcelDataReader 的流与回退编码，给分隔文字档路径用
/// （<c>.csv</c>／<c>.txt</c>）。无 BOM 又未指名时回退编码给严格 Big5；指名编码时按指名编码转码成无 BOM UTF-8 再交出。</item>
/// </list>
/// 编码名到 <see cref="Encoding"/> 的解析一律交给 <see cref="TextWriterHelper.ResolveEncoding(string)"/>
/// （大小写与首尾空白不敏感、两侧严格回退、未知名抛 <see cref="ArgumentException"/>）。
/// </para>
/// <para>
/// 取到的编码一律带 <see cref="DecoderExceptionFallback"/>：
/// <list type="bullet">
/// <item>UTF-8 遇到非法序列抛 <see cref="DecoderFallbackException"/>；</item>
/// <item>Big5 把配不上对的高位字节解成私有区字符 <c>U+F8F8</c> 而不抛出，出现 <c>U+F8F8</c> 表示源档那一处字节已损坏。</item>
/// </list>
/// </para>
/// <para>
/// 自动判别只保证按判出的编码解码不会撞到解码错误，不保证那是原档真正的编码；没有 BOM 的 UTF-16 认不出来。
/// 指名 <see cref="Abstractions.Importing.ExcelImportOptions.TextEncodingName"/> 时两条路都按指名的编码解码。
/// </para>
/// <para>
/// <see cref="Resolve"/> 的窗口只有 <c>32KB</c>：前段是纯 ASCII 的 Big5 档会被判成 UTF-8，
/// 到第一个非 UTF-8 字节才抛 <see cref="DecoderFallbackException"/>，此前的行已经交出。
/// </para>
/// </remarks>
internal static class TextEncodingResolver
{
    /// <summary>
    /// 严格 UTF-8 试探解码用的窗口字节数
    /// </summary>
    /// <remarks>
    /// 只服务 <see cref="Resolve"/> 那一支，窗口之外的字节不在判别范围内。
    /// </remarks>
    private const int ProbeByteCount = 32 * 1024;

    /// <summary>
    /// 认前导字节要读的档头字节数（UTF-32 的前导字节是四字节，判定要容得下它）
    /// </summary>
    private const int PreambleProbeByteCount = 4;

    /// <summary>
    /// 转码时逐块搬运的字符数
    /// </summary>
    private const int TranscodeChunkChars = 64 * 1024;

    /// <summary>
    /// 不带 BOM 的 UTF-8 名称，与 <see cref="TextWriterHelper.ResolveEncoding(string)"/> 的取值口径一致
    /// </summary>
    private const string Utf8Name = "utf-8";

    /// <summary>
    /// 试探失败后的回退编码名称
    /// </summary>
    private const string Big5Name = "big5";

    /// <summary>
    /// 解析文字档的解码编码，给<u>自己解码</u>的调用方用（固定宽度导入）
    /// </summary>
    /// <param name="explicitName">调用方指名的编码名；<c>null</c> 走自动判别</param>
    /// <param name="input">文字档输入流，自动判别时读它的前 <c>32KB</c> 与 BOM，读完把流位置复原</param>
    /// <returns>用于解码的 <see cref="Encoding"/>，一律带严格回退</returns>
    /// <remarks>
    /// <para>
    /// 自己解码的调用方须剥除前导字节，字节数取 <see cref="GetPreambleSkipBytes"/>。
    /// 交给 ExcelDataReader 解码的分隔文字档不用本方法，取 <see cref="ResolveForReader"/>。
    /// </para>
    /// <para>
    /// Big5 一支依赖 <c>CodePagesEncodingProvider</c> 已注册（由 <c>AddXiHanExcel</c> 完成）。未注册时
    /// 解析 Big5 抛的 <see cref="ArgumentException"/> 直接上抛。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="explicitName"/> 是空字串或仅含空白（要自动判别请传 <c>null</c>），或无法解析成任何已知编码；
    /// <paramref name="input"/> 不可读、不可定位；或回退用的 <c>Big5</c> 因编码提供程序未注册而无法解析。
    /// <see cref="ArgumentException.ParamName"/> 为 <c>TextEncodingName</c>（指名编码的两类问题）或 <c>input</c>（流的问题）
    /// </exception>
    internal static Encoding Resolve(string? explicitName, Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (explicitName is not null)
        {
            return ResolveExplicit(explicitName);
        }

        return ResolveAutomatic(input);
    }

    /// <summary>
    /// 解析「交给 ExcelDataReader 解码的分隔文字档」要怎么读，给 <c>.csv</c>／<c>.txt</c> 路径用
    /// </summary>
    /// <param name="explicitName">调用方指名的编码名；<c>null</c> 走自动判别</param>
    /// <param name="input">文字档输入流，只读档头认前导字节，读完把流位置复原；转码那一支会把它整档读过一遍</param>
    /// <returns>要交给读取器的流与回退编码</returns>
    /// <remarks>
    /// <para>
    /// 三种结果，除转码那一支另建一条流外都交出原流：
    /// <list type="bullet">
    /// <item>档带前导字节：交出原流，读取器按前导字节解码，指名的编码让位；</item>
    /// <item>无前导字节又未指名：交出原流，回退编码给严格 Big5，UTF-8 试解由读取器按整档进行；</item>
    /// <item>无前导字节且指名编码：按指名编码把整档解码，以无 BOM UTF-8 逐块写进一条新流交出，
    /// 回退编码给严格 UTF-8。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 转码那一支把整档的转码结果放在内存里，规模与档同量级；逐块搬运，不额外产生整档字串。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="explicitName"/> 是空字串或仅含空白，或无法解析成任何已知编码；
    /// <paramref name="input"/> 不可读、不可定位；或回退用的 <c>Big5</c> 因编码提供程序未注册而无法解析
    /// </exception>
    /// <exception cref="DecoderFallbackException">指名编码那一支：档的实际字节在指名的编码下解不开</exception>
    internal static TextReaderEncodingHandoff ResolveForReader(string? explicitName, Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 认前导字节复用 ReadHeader：它从流的起点读，读完把位置交回调用方留下的位置
        var preamble = DetectByPreamble(ExcelFormatProbe.ReadHeader(input, PreambleProbeByteCount));

        if (explicitName is null)
        {
            return new TextReaderEncodingHandoff(
                input,
                preamble ?? TextWriterHelper.ResolveEncoding(Big5Name),
                OwnsInput: false);
        }

        // 指名解析不了在这里就抛，档带前导字节时也一样
        var named = ResolveExplicit(explicitName);

        if (preamble is not null)
        {
            return new TextReaderEncodingHandoff(input, named, OwnsInput: false);
        }

        return new TextReaderEncodingHandoff(
            TranscodeToUtf8(input, named),
            TextWriterHelper.ResolveEncoding(Utf8Name),
            OwnsInput: true);
    }

    /// <summary>
    /// 按指名编码把整档解码，再无 BOM UTF-8 逐块写进一条新流
    /// </summary>
    /// <param name="input">文字档输入流，从起点整档读过一遍，读完不关闭</param>
    /// <param name="named">指名解析出的编码，两侧回退都是严格的</param>
    /// <returns>位置在起点的无 BOM UTF-8 流，由调用方释放</returns>
    /// <exception cref="DecoderFallbackException">档的实际字节在 <paramref name="named"/> 下解不开</exception>
    /// <remarks>
    /// 逐块搬运，不产生整档字串。源档的 <c>\r\n</c>、裸 <c>\n</c> 与裸 <c>\r</c> 原样保留；
    /// 输出编码取 <c>utf-8</c>，转码结果不带前导字节。
    /// </remarks>
    private static MemoryStream TranscodeToUtf8(Stream input, Encoding named)
    {
        var output = new MemoryStream((int)Math.Min(input.Length, int.MaxValue));

        input.Position = 0;

        using (var reader = new StreamReader(
                   input,
                   named,
                   detectEncodingFromByteOrderMarks: false,
                   TranscodeChunkChars,
                   leaveOpen: true))
        using (var writer = new StreamWriter(
                   output,
                   TextWriterHelper.ResolveEncoding(Utf8Name),
                   TranscodeChunkChars,
                   leaveOpen: true))
        {
            var buffer = new char[TranscodeChunkChars];
            int read;

            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.Write(buffer, 0, read);
            }

            writer.Flush();
        }

        output.Position = 0;

        return output;
    }

    /// <summary>
    /// 取这份档在指定编码下应当剥掉的前导字节数，供自己解码的调用方（固定宽度导入）复位流位置用
    /// </summary>
    /// <param name="encoding">由 <see cref="Resolve"/> 取到的解码编码</param>
    /// <param name="input">文字档输入流，本方法只读档头并把流位置复原</param>
    /// <returns>
    /// 档头确实是该编码的前导字节时返回其长度，否则返回 <c>0</c>；<c>Big5</c> 这类没有前导字节的编码恒返回 <c>0</c>
    /// </returns>
    /// <remarks>
    /// <para>
    /// UTF-8 一支（含指名不带 BOM 的 <c>utf-8</c>）按档头是不是 <c>EF BB BF</c> 判定；
    /// 其余编码按 <see cref="Encoding.GetPreamble"/> 的声明比对。比对不上返回 <c>0</c>。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> 或 <paramref name="input"/> 为 <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> 不可读或不可定位</exception>
    internal static int GetPreambleSkipBytes(Encoding encoding, Stream input)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(input);

        // utf-8 与 utf-8-bom 解析出的编码 GetPreamble 不同（前者为空），但同一份带 BOM 的档都该剥掉那三个字节
        var preamble = encoding.CodePage == Utf8CodePage && encoding.GetPreamble().Length == 0
            ? Encoding.UTF8.GetPreamble()
            : encoding.GetPreamble();

        if (preamble.Length == 0)
        {
            return 0;
        }

        // 嗅探复用 ReadHeader：它从流的起点读，读完把位置交回调用方留下的位置
        return ExcelFormatProbe.ReadHeader(input, preamble.Length).AsSpan().StartsWith(preamble)
            ? preamble.Length
            : 0;
    }

    /// <summary>
    /// 不带 BOM 与带 BOM 的 UTF-8 共用的代码页编号
    /// </summary>
    private const int Utf8CodePage = 65001;

    /// <summary>
    /// 按调用方指名的编码名解析，规则全部交回导出侧那一份实现
    /// </summary>
    /// <param name="explicitName">指名的编码名</param>
    private static Encoding ResolveExplicit(string explicitName)
    {
        if (string.IsNullOrWhiteSpace(explicitName))
        {
            throw new ArgumentException(
                "文字档编码名不能是空字串或仅含空白字符；要自动判别请传 null。",
                nameof(Abstractions.Importing.ExcelImportOptions.TextEncodingName));
        }

        try
        {
            // 解析、大小写与空白口径、严格回退由 TextWriterHelper 负责
            return TextWriterHelper.ResolveEncoding(explicitName);
        }
        catch (ArgumentException ex)
        {
            // 消息改为点名导入侧选项，保留内层异常
            throw new ArgumentException(
                $"导入编码名「{explicitName}」无法解析：{ex.Message} 要自动判别请把 TextEncodingName 传 null。",
                nameof(Abstractions.Importing.ExcelImportOptions.TextEncodingName),
                ex);
        }
    }

    /// <summary>
    /// 自动判别：BOM → 严格 UTF-8 试探 → Big5 回退
    /// </summary>
    /// <param name="input">文字档输入流</param>
    private static Encoding ResolveAutomatic(Stream input)
    {
        // 读窗口并复位流位置
        var head = ExcelFormatProbe.ReadHeader(input, ProbeByteCount);

        var bomEncoding = DetectByPreamble(head);
        if (bomEncoding is not null)
        {
            return bomEncoding;
        }

        if (IsStrictUtf8Decodable(head))
        {
            return TextWriterHelper.ResolveEncoding(Utf8Name);
        }

        // UTF-8 严格试解失败时回退 Big5，Big5 解析不了时由它抛
        return TextWriterHelper.ResolveEncoding(Big5Name);
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
    /// 按前导字节认编码
    /// </summary>
    /// <param name="head">档头字节</param>
    /// <returns>认出来的编码，没有可识别 BOM 时返回 <c>null</c></returns>
    /// <remarks>
    /// 五个分支都按名字经 <see cref="TextWriterHelper.ResolveEncoding(string)"/> 取编码，与指名编码同一套解析规则与严格回退。
    /// UTF-32 的两个 BOM 排在 UTF-16 之前，因为前两位字节相同。
    /// </remarks>
    private static Encoding? DetectByPreamble(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Utf32LePreamble))
        {
            return TextWriterHelper.ResolveEncoding("utf-32");
        }

        if (head.StartsWith(Utf32BePreamble))
        {
            return TextWriterHelper.ResolveEncoding("utf-32BE");
        }

        if (head.StartsWith(Utf8Preamble))
        {
            return TextWriterHelper.ResolveEncoding(Utf8Name);
        }

        if (head.StartsWith(Utf16LePreamble))
        {
            return TextWriterHelper.ResolveEncoding("utf-16");
        }

        if (head.StartsWith(Utf16BePreamble))
        {
            return TextWriterHelper.ResolveEncoding("utf-16BE");
        }

        return null;
    }

    /// <summary>
    /// 用严格 UTF-8 试解整段档头，判断这份档能不能按 UTF-8 读
    /// </summary>
    /// <param name="head">档头字节</param>
    /// <returns>整段都能按 UTF-8 解码时为 <c>true</c></returns>
    /// <remarks>
    /// 用 <see cref="Decoder"/> 的 <c>GetCharCount</c> 且不 flush：窗口切在多字节字符中间不算解码失败，
    /// 只有非法序列才算。
    /// </remarks>
    private static bool IsStrictUtf8Decodable(byte[] head)
    {
        try
        {
            TextWriterHelper.ResolveEncoding(Utf8Name).GetDecoder().GetCharCount(head, 0, head.Length);

            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>
/// 交给底层读取器解码的文字档要怎么读
/// </summary>
/// <param name="Input">要交给读取器的流；转码那一支是本类型新建的流，不是调用方传进来的那条</param>
/// <param name="FallbackEncoding">给读取器的回退编码，一律带严格回退</param>
/// <param name="OwnsInput">
/// <see cref="Input"/> 是不是本类型新建的流。为 <c>true</c> 时调用方读完要自己释放它；
/// 为 <c>false</c> 时 <see cref="Input"/> 就是调用方传进来的那条，所有权始终在调用方，不得释放
/// </param>
/// <remarks>
/// 读取器一律以 <c>LeaveOpen = true</c> 建立，因此两种情况下它都不会替调用方关流。
/// </remarks>
internal readonly record struct TextReaderEncodingHandoff(Stream Input, Encoding FallbackEncoding, bool OwnsInput);
