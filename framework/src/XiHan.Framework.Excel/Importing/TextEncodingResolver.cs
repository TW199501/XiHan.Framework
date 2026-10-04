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
/// 本类型只管导入侧独有的三件事：读 BOM、无 BOM 时的严格 UTF-8 试探、以及试探失败后回退 Big5。
/// <u>编码名到 <see cref="Encoding"/> 的解析不在这里重复实现</u>，一律交回
/// <see cref="TextWriterHelper.ResolveEncoding(string)"/>——它与导出侧同一份规则（大小写与首尾空白不敏感、
/// 两侧严格回退、未知名抛 <see cref="ArgumentException"/>），两边判定的编码不会分叉。
/// </para>
/// <para>
/// 取到的编码一律带 <see cref="DecoderExceptionFallback"/>，为的是让「编码指错」能被察觉而不是安静带过：
/// <see cref="Encoding.GetEncoding(string)"/> 默认的宽松解码永远不会失败，也就永远不会报判错。
/// 但这条<b>只挡得住一部分乱码</b>，两件事必须分开看：
/// <list type="bullet">
/// <item>UTF-8 一支挡得住——非法 UTF-8 序列抛 <see cref="DecoderFallbackException"/>，读档停下而不是交出替换字符；</item>
/// <item>Big5 一支挡不住——该代码页的解码器把配不上对的高位字节解成私有区字符 <c>U+F8F8</c> 而不是失败，
/// 换成异常回退也一样，因此「带严格回退」不等于「不会产出乱码」。判出来的字里出现 <c>U+F8F8</c> 就代表源档那一处
/// 字节已经损坏，调用方要自己决定要不要把它当错误。</item>
/// </list>
/// </para>
/// <para>
/// 自动判别是<u>保守</u>的而不是<u>准确</u>的：它只保证「按判出来的编码解不会撞到解码错误」，不保证那是原档真正的编码。
/// 纯 ASCII 档在 UTF-8 与 Big5 下都合法，一段 Big5 双字节序列也可能正好构成合法 UTF-8 序列，
/// 这时判出来的是另一种语言的字。<u>没有 BOM 的 UTF-16 更是会被判成 UTF-8</u>：ASCII 段在 UTF-16LE 下是
/// 每个可见字符后跟一个 <c>0x00</c>，而 <c>0x00</c> 本身是合法 UTF-8，严格试探因此不会失败，整份档会被按 UTF-8
/// 解出一串夹着 NUL 的字符。已知来源编码请指名
/// <see cref="Abstractions.Importing.ExcelImportOptions.TextEncodingName"/>，那是唯一确定的做法。
/// </para>
/// </remarks>
internal static class TextEncodingResolver
{
    /// <summary>
    /// 严格 UTF-8 试探解码用的窗口字节数
    /// </summary>
    private const int ProbeByteCount = 32 * 1024;

    /// <summary>
    /// 不带 BOM 的 UTF-8 名称，与 <see cref="TextWriterHelper.ResolveEncoding(string)"/> 的取值口径一致
    /// </summary>
    private const string Utf8Name = "utf-8";

    /// <summary>
    /// 试探失败后的回退编码名称（繁体中文往来档常用，无 BOM）
    /// </summary>
    private const string Big5Name = "big5";

    /// <summary>
    /// 解析文字档的解码编码
    /// </summary>
    /// <param name="explicitName">调用方指名的编码名；<c>null</c> 走自动判别</param>
    /// <param name="input">文字档输入流，自动判别时读它的前 <c>32KB</c> 与 BOM，读完把流位置复原</param>
    /// <returns>用于解码的 <see cref="Encoding"/>，一律带严格回退</returns>
    /// <remarks>
    /// <para>
    /// 返回的编码若 <see cref="Encoding.GetPreamble"/> 非空（指名 <c>"utf-8-bom"</c> 或档带 BOM），
    /// 需要剥除的字节数就是那段前导长度。交给 ExcelDataReader 的读档路径不必自己剥：读取器认得 BOM，
    /// 并且 BOM 优先于 <c>FallbackEncoding</c>。自己解码的调用方（固定宽度导入）必须剥，否则会多读进一个
    /// <c>U+FEFF</c> 或把 UTF-16 的起始字节解成控制字符。
    /// </para>
    /// <para>
    /// Big5 一支依赖 <c>CodePagesEncodingProvider</c> 已注册（正式路径由 <c>AddXiHanExcel</c> 完成）。未注册时
    /// 解析 Big5 抛的 <see cref="ArgumentException"/> 直接上抛，不改投 UTF-8、也不返回任何猜测值。
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
            // 不在这里重抄一份名称表：解析、大小写与空白口径、严格回退全部由 TextWriterHelper 那一份负责
            return TextWriterHelper.ResolveEncoding(explicitName);
        }
        catch (ArgumentException ex)
        {
            // 只把消息换点名导入侧选项，判定与内层异常保留，不改判也不降级
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
        // 读窗口与复位流位置的动作复用签章嗅探那一份，不另写一遍 try/finally
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

        // UTF-8 严格试解失败：繁体中文无 BOM 档是唯一常见情形，回退 Big5；再解析不动就由它抛
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
    /// 五个分支都交回 <see cref="TextWriterHelper.ResolveEncoding(string)"/> 取到的编码（连 UTF-16／UTF-32 也一样按名字取），
    /// 本类型不自己构造 <see cref="Encoding"/>：这样「指名编码」与「按 BOM 认编码」两条路拿到的是同一套解析规则与同一套严格回退，
    /// 不会出现 BOM 认出来的编码比指名的宽松一档这种事。UTF-32 的两个 BOM 排在 UTF-16 之前，因为前两位字节相同。
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
    /// 用 <see cref="Decoder"/> 的 <c>GetCharCount</c> 而不是 <see cref="Encoding.GetString(byte[])"/>：
    /// <u>窗口切在多字节字符中间</u>不算解码失败（半个字留在解码器状态里，不 flush 就不抛），只有真正的非法序列才算。
    /// 按 32KB 切开的中文档很常见，把「切到半个字」当成「不是 UTF-8」会把正常档误投进 Big5。
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
