// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Text;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Importing;

/// <summary>
/// 文字档解码编码的解析与自动判别测试
/// </summary>
/// <remarks>
/// <para>
/// 两条线索分别钉住：<b>指名</b>那一支不另起编码名表，一律交回导出侧既有的
/// <see cref="TextWriterHelper.ResolveEncoding(string)"/>；<b>自动判别</b>那一支是 BOM → 严格 UTF-8 试探 → Big5，
/// 且读档头之后必须把流位置复原。
/// </para>
/// </remarks>
public class TextEncodingResolverTests
{
    /// <summary>
    /// 严格 UTF-8 试探窗口是 32KB，切在多字节字符中间不算解码失败
    /// </summary>
    private const int ProbeWindowBytes = 32 * 1024;

    /// <summary>
    /// 指名编码时交回的编码与导出侧既有解析同源，不做第二份判定
    /// </summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData(" utf-8-bom ")]
    [InlineData("big5")]
    [InlineData("Big5")]
    [InlineData("csBig5")]
    [InlineData("gb18030")]
    public void 指名编码交回既有解析的结果(string encodingName)
    {
        using var input = ImportFixtures.Text("a\r\n");

        var resolved = TextEncodingResolver.Resolve(encodingName, input);
        var shared = TextWriterHelper.ResolveEncoding(encodingName);

        Assert.Equal(shared.WebName, resolved.WebName);
        Assert.Equal(shared.EncoderFallback.GetType(), resolved.EncoderFallback.GetType());
        Assert.Equal(shared.DecoderFallback.GetType(), resolved.DecoderFallback.GetType());
        Assert.Equal(shared.GetPreamble(), resolved.GetPreamble());
    }

    /// <summary>
    /// 代码页编号不是编码名：两侧都解析不到
    /// </summary>
    /// <remarks>
    /// <c>Encoding.GetEncoding(string)</c> 只认名称与别名，<c>"950"</c> 取不到 Big5。
    /// </remarks>
    [Fact]
    public void 代码页编号不是编码名()
    {
        using var input = ImportFixtures.Text("a\r\n");

        Assert.Throws<ArgumentException>(() => TextEncodingResolver.Resolve("950", input));
        Assert.Throws<ArgumentException>(() => TextWriterHelper.ResolveEncoding("950"));
    }

    /// <summary>
    /// 差异探测：直接构造的严格 UTF-8 与既有解析对同一批边界输入逐条对跑，分歧必须为零
    /// </summary>
    /// <remarks>
    /// 对跑的两侧是「<see cref="UTF8Encoding"/> 直接构造」与「<see cref="TextWriterHelper.ResolveEncoding(string)"/> 取到的」，
    /// 每个输入比四件事：整段解码的结果或抛出的异常型别、<b>试探实际用的</b> <c>GetDecoder().GetCharCount</c>、
    /// BOM 前导字节、以及编码器对孤立代理字尾的行为。分歧清单非空即失败，并把分歧条数报出来。
    /// </remarks>
    [Fact]
    public void 严格UTF8两个来源逐条对跑零分歧()
    {
        var literal = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var shared = TextWriterHelper.ResolveEncoding("utf-8");

        var cases = Utf8BoundaryCases();
        var divergences = new List<string>();
        var comparisons = 0;

        foreach (var (name, bytes) in cases)
        {
            // 1. 整段解码：结果或异常型别
            comparisons++;
            Compare(nameof(Decode) + "/" + name, () => Decode(literal, bytes), () => Decode(shared, bytes), divergences);

            // 2. 试探真正用的那一步：状态化解码器的 GetCharCount（不 flush）
            comparisons++;
            Compare("GetCharCount/" + name, () => CharCount(literal, bytes), () => CharCount(shared, bytes), divergences);

            // 3. 前导字节
            comparisons++;
            Compare(nameof(Encoding.GetPreamble) + "/" + name,
                () => Convert.ToHexString(literal.GetPreamble()),
                () => Convert.ToHexString(shared.GetPreamble()),
                divergences);
        }

        // 4. 编码器侧：孤立代理字尾必须两侧同形态
        foreach (var text in new[] { "\uD800", "\uDC00", "曦寒物流", "提單號", "😀", "\uD800x" })
        {
            comparisons++;
            Compare("encode/" + text, () => Encode(literal, text), () => Encode(shared, text), divergences);
        }

        Assert.True(divergences.Count == 0, $"分歧 {divergences.Count}/{comparisons} 条：{string.Join(" ; ", divergences)}");
        Assert.True(comparisons >= 40, $"对跑样本过少（{comparisons} 条），不足以支撑等价结论");
    }

    /// <summary>
    /// 严格 UTF-8 的边界输入清单：合法序列、截断序列、非法前导、过短形式、代理区与 BOM
    /// </summary>
    private static List<(string Name, byte[] Bytes)> Utf8BoundaryCases() =>
    [
        ("空档", []),
        ("纯ASCII", "abc"u8.ToArray()),
        ("ASCII含CR", "a\r\nb"u8.ToArray()),
        ("合法2字节", "é"u8.ToArray()),
        ("合法3字节", "提"u8.ToArray()),
        ("合法4字节emoji", "😀"u8.ToArray()),
        ("BOM加内容", [0xEF, 0xBB, 0xBF, .. "a"u8.ToArray()]),
        ("只有BOM", [0xEF, 0xBB, 0xBF]),
        ("截断的3字节", [0xE6, 0x8F]),
        ("截断的4字节", [0xF0, 0x9F]),
        ("孤立后续字节", [0x8F]),
        ("非法前导C0", [0xC0, 0x80]),
        ("非法前导C1", [0xC1, 0x80]),
        ("过短形式E0", [0xE0, 0x80, 0x80]),
        ("非法前导F5", [0xF5, 0x80, 0x80, 0x80]),
        ("非法前导FE", [0xFE, 0xFF]),
        ("Big5的提單號", ImportFixtures.StrictBig5.GetBytes("提單號")),
        ("Big5的AWB", ImportFixtures.StrictBig5.GetBytes("AWB")),
        ("UTF16LE的BOM头", [0xFF, 0xFE, 0x00, 0x00]),
        ("NUL字节", [0x00, 0x41]),
        ("DEL字节", [0x7F, 0x41]),
        ("高位单字节", [0x80]),
        ("窗口尺寸的ASCII", new byte[ProbeWindowBytes])
    ];

    private static string Decode(Encoding encoding, byte[] bytes)
    {
        try
        {
            return "ok:" + encoding.GetString(bytes);
        }
        catch (Exception ex)
        {
            return "throw:" + ex.GetType().Name;
        }
    }

    private static string CharCount(Encoding encoding, byte[] bytes)
    {
        try
        {
            // 与实现里那一步同形：状态化解码器、不 flush
            return "ok:" + encoding.GetDecoder().GetCharCount(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            return "throw:" + ex.GetType().Name;
        }
    }

    private static string Encode(Encoding encoding, string text)
    {
        try
        {
            return "ok:" + Convert.ToHexString(encoding.GetBytes(text));
        }
        catch (Exception ex)
        {
            return "throw:" + ex.GetType().Name;
        }
    }

    private static void Compare(string label, Func<string> left, Func<string> right, List<string> divergences)
    {
        var a = left();
        var b = right();

        if (!string.Equals(a, b, StringComparison.Ordinal))
        {
            divergences.Add($"{label}: {a} vs {b}");
        }
    }

    /// <summary>
    /// 自动判别按 BOM 认编码，UTF-32 的两个 BOM 不被当成 UTF-16
    /// </summary>
    /// <param name="preamble">前导字节</param>
    /// <param name="body">前导之后的内容</param>
    /// <param name="expectedWebName">期望解出的编码名</param>
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF }, "提單號", "utf-8")]
    [InlineData(new byte[] { 0xFF, 0xFE }, "提單號", "utf-16")]
    [InlineData(new byte[] { 0xFE, 0xFF }, "提單號", "utf-16BE")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "提單號", "utf-32")]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "提單號", "utf-32BE")]
    public void 按BOM认编码(byte[] preamble, string body, string expectedWebName)
    {
        using var input = new MemoryStream([.. preamble, .. EncodeWithBomLess(preamble, body)]);

        Assert.Equal(expectedWebName, TextEncodingResolver.Resolve(null, input).WebName);
    }

    /// <summary>
    /// 无 BOM 时按严格 UTF-8 试解：能解就判 UTF-8，解不动才回退 Big5
    /// </summary>
    [Theory]
    [InlineData("提單號\r\nAWB1\r\n", false, "big5")]
    [InlineData("提单号\r\nAWB1\r\n", true, "utf-8")]
    [InlineData("AWB1\r\n", true, "utf-8")]
    [InlineData("", true, "utf-8")]
    public void 无BOM的试探在UTF8与Big5之间分岔(string text, bool isUtf8, string expectedWebName)
    {
        var bytes = isUtf8 ? ImportFixtures.Utf8NoBom.GetBytes(text) : ImportFixtures.Encode(ImportFixtures.StrictBig5, text);
        using var input = new MemoryStream(bytes);

        Assert.Equal(expectedWebName, TextEncodingResolver.Resolve(null, input).WebName);
    }

    /// <summary>
    /// 窗口切在多字节字符中间不算解码失败：整份档仍判 UTF-8，而不是误投 Big5
    /// </summary>
    [Fact]
    public void 窗口边界切到半个字不算非UTF8()
    {
        // 32KB 窗口正好落在「曦」的第一个字节之后，剩下半个字留在解码器状态里
        var text = string.Concat(new string('A', ProbeWindowBytes - 1), "曦\r\n");
        using var input = ImportFixtures.Text(text);

        Assert.Equal("utf-8", TextEncodingResolver.Resolve(null, input).WebName);
    }

    /// <summary>
    /// 窗口之外的非法字节不在判别范围内：判出来仍是 UTF-8，报错由解码那一步负责
    /// </summary>
    [Fact]
    public void 超出窗口的非法字节不影响判别()
    {
        using var input = new MemoryStream([.. ImportFixtures.Utf8NoBom.GetBytes(new string('A', ProbeWindowBytes)), 0xFF]);

        Assert.Equal("utf-8", TextEncodingResolver.Resolve(null, input).WebName);
    }

    /// <summary>
    /// 读档头之后必须把流位置复原，两条路径都一样
    /// </summary>
    [Fact]
    public void 判别后流位置复原()
    {
        using var input = ImportFixtures.Big5Text("提單號\r\nAWB1\r\n");
        input.Position = 2;

        TextEncodingResolver.Resolve(null, input);
        Assert.Equal(2, input.Position);

        TextEncodingResolver.Resolve("big5", input);
        Assert.Equal(2, input.Position);

        // 复位的原位置是调用方留下的，不是 0
        var buffer = new byte[2];
        Assert.Equal(2, input.Read(buffer, 0, 2));
    }

    /// <summary>
    /// 自动判别要求流可定位，不可定位的流点名 input 拒掉
    /// </summary>
    [Fact]
    public void 自动判别遇到不可定位的流时点名input()
    {
        using var backing = ImportFixtures.Text("a\r\n");
        using var forward = new ForwardOnlyStream(backing);

        var failure = Assert.Throws<ArgumentException>(() => TextEncodingResolver.Resolve(null, forward));
        Assert.Equal("input", failure.ParamName);
    }

    /// <summary>
    /// 指名为空（不是 null）时拒掉并点名 TextEncodingName，不静默走自动判别
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void 空编码名被拒(string encodingName)
    {
        using var input = ImportFixtures.Text("a\r\n");

        var failure = Assert.Throws<ArgumentException>(() => TextEncodingResolver.Resolve(encodingName, input));

        Assert.Equal(nameof(Abstractions.Importing.ExcelImportOptions.TextEncodingName), failure.ParamName);
        Assert.Contains("null", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 未知编码名解析失败时抛上去并点名，不降级为 UTF-8
    /// </summary>
    [Fact]
    public void 未知编码名抛ArgumentException并点名()
    {
        using var input = ImportFixtures.Text("a\r\n");

        var failure = Assert.Throws<ArgumentException>(() => TextEncodingResolver.Resolve("utf-99", input));

        Assert.Equal(nameof(Abstractions.Importing.ExcelImportOptions.TextEncodingName), failure.ParamName);
        Assert.Contains("utf-99", failure.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(failure.InnerException);
    }

    /// <summary>
    /// 输入流为 null 抛 ArgumentNullException，指名编码时也一样
    /// </summary>
    [Fact]
    public void 输入流为null时抛出()
    {
        Assert.Throws<ArgumentNullException>(() => TextEncodingResolver.Resolve("utf-8", null!));
    }

    /// <summary>
    /// 认出的编码一律带严格解码回退
    /// </summary>
    [Theory]
    [InlineData("big5")]
    [InlineData("utf-8")]
    public void 认出的编码带异常回退(string encodingName)
    {
        using var input = ImportFixtures.Text("a\r\n");

        var resolved = TextEncodingResolver.Resolve(encodingName, input);

        Assert.IsType<DecoderExceptionFallback>(resolved.DecoderFallback);
        Assert.IsType<EncoderExceptionFallback>(resolved.EncoderFallback);
    }

    /// <summary>
    /// 分隔文字档未指名编码、档又无 BOM 时交回原流，回退编码是严格 Big5
    /// </summary>
    /// <remarks>
    /// UTF-8 由读取器整档试解，本类型只给回退编码，且回退带严格回退。
    /// </remarks>
    [Fact]
    public void 分隔文字档未指名时无BOM的回退编码是严格Big5()
    {
        using var input = ImportFixtures.Text("a,b\r\n1,2\r\n");

        var handoff = TextEncodingResolver.ResolveForReader(null, input);

        Assert.Same(input, handoff.Input);
        Assert.False(handoff.OwnsInput);
        Assert.Equal("big5", handoff.FallbackEncoding.WebName);
        Assert.IsType<DecoderExceptionFallback>(handoff.FallbackEncoding.DecoderFallback);
        Assert.IsType<EncoderExceptionFallback>(handoff.FallbackEncoding.EncoderFallback);
    }

    /// <summary>
    /// 分隔文字档带 BOM 时回退编码按 BOM 取，不落到 Big5，也不另建流
    /// </summary>
    /// <param name="preamble">前导字节</param>
    /// <param name="expectedWebName">期望的编码名</param>
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF }, "utf-8")]
    [InlineData(new byte[] { 0xFF, 0xFE }, "utf-16")]
    [InlineData(new byte[] { 0xFE, 0xFF }, "utf-16BE")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "utf-32")]
    public void 分隔文字档带BOM时回退编码按BOM(byte[] preamble, string expectedWebName)
    {
        using var input = new MemoryStream([.. preamble, .. "a,b\r\n"u8.ToArray()]);

        var handoff = TextEncodingResolver.ResolveForReader(null, input);

        Assert.Same(input, handoff.Input);
        Assert.False(handoff.OwnsInput);
        Assert.Equal(expectedWebName, handoff.FallbackEncoding.WebName);
    }

    /// <summary>
    /// 指名编码且档无 BOM 时不交给读取器解码：自己按指名编码转码成无 BOM UTF-8，另建一条流
    /// </summary>
    [Fact]
    public void 指名编码且无BOM时转码成无BOM的UTF8()
    {
        using var input = ImportFixtures.Big5Text("提單號\r\nAWB1\r\n");

        var handoff = TextEncodingResolver.ResolveForReader("big5", input);

        Assert.True(handoff.OwnsInput);
        Assert.NotSame(input, handoff.Input);
        Assert.Equal("utf-8", handoff.FallbackEncoding.WebName);
        Assert.Equal(0, handoff.Input.Position);

        using var transcoded = handoff.Input;
        var bytes = ReadAllBytes(transcoded);

        // 转码结果不带 UTF-8 前导字节
        Assert.False(bytes.AsSpan().StartsWith(new UTF8Encoding(true).GetPreamble()));
        Assert.Equal("提單號\r\nAWB1\r\n", new UTF8Encoding(false, true).GetString(bytes));
    }

    /// <summary>
    /// 档带 BOM 时指名让位：不转码、原流交出去，回退编码仍是指名的那个
    /// </summary>
    [Fact]
    public void 档带BOM时指名编码让位且不转码()
    {
        using var input = ImportFixtures.TextWithUtf8Bom("提單號\r\nAWB1\r\n");

        var handoff = TextEncodingResolver.ResolveForReader("iso-8859-1", input);

        Assert.False(handoff.OwnsInput);
        Assert.Same(input, handoff.Input);
        Assert.Equal("iso-8859-1", handoff.FallbackEncoding.WebName);
    }

    /// <summary>
    /// 指名解析不了时在转码之前就抛并点名，档带不带 BOM 都一样
    /// </summary>
    /// <param name="encodingName">解析不了的编码名</param>
    [Theory]
    [InlineData("utf-99")]
    [InlineData("")]
    [InlineData(" \t ")]
    public void 分隔文字档的非法编码名在转码之前被拒(string encodingName)
    {
        using var input = ImportFixtures.Text("a\r\n");

        var failure = Assert.Throws<ArgumentException>(
            () => TextEncodingResolver.ResolveForReader(encodingName, input));

        Assert.Equal(nameof(Abstractions.Importing.ExcelImportOptions.TextEncodingName), failure.ParamName);
    }

    /// <summary>
    /// 指名编码但档的字节解不开时在转码阶段就抛解码异常，不产出替换字符当正常数据
    /// </summary>
    [Fact]
    public void 指名编码解不开时抛解码异常()
    {
        using var input = ImportFixtures.Big5Text("提單號\r\nAWB1\r\n");

        Assert.Throws<DecoderFallbackException>(() => TextEncodingResolver.ResolveForReader("utf-8", input));
    }

    /// <summary>
    /// 转码那一支也不吃掉调用方的流：读完仍可复位重读，取到的还是源档字节
    /// </summary>
    [Fact]
    public void 转码之后调用方的流仍归调用方()
    {
        using var input = ImportFixtures.Big5Text("提單號\r\nAWB1\r\n");

        var handoff = TextEncodingResolver.ResolveForReader("big5", input);
        using var transcoded = handoff.Input;

        Assert.True(input.CanRead);

        input.Position = 0;
        Assert.Equal(0xB4, input.ReadByte());
    }

    /// <summary>
    /// 转码逐块搬但不改换行形态：源档的 CRLF、裸 LF 与裸 CR 原样过去
    /// </summary>
    [Fact]
    public void 转码保留源档的换行形态()
    {
        using var input = ImportFixtures.Big5Text("提單號\r\n甲\n乙\r丙\r\n");

        var handoff = TextEncodingResolver.ResolveForReader("big5", input);
        using var transcoded = handoff.Input;

        Assert.Equal(
            "提單號\r\n甲\n乙\r丙\r\n",
            new UTF8Encoding(false, true).GetString(ReadAllBytes(transcoded)));
    }

    /// <summary>
    /// 从起点把一条流读成字节，供断言转码结果用
    /// </summary>
    /// <param name="stream">要读干的流，读完位置在档尾</param>
    private static byte[] ReadAllBytes(Stream stream)
    {
        stream.Position = 0;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    /// <summary>
    /// 这一支同样要求流可定位：认前导字节要读完复位
    /// </summary>
    [Fact]
    public void 分隔文字档遇到不可定位的流时点名input()
    {
        using var backing = ImportFixtures.Text("a\r\n");
        using var forward = new ForwardOnlyStream(backing);

        var failure = Assert.Throws<ArgumentException>(() => TextEncodingResolver.ResolveForReader(null, forward));

        Assert.Equal("input", failure.ParamName);
    }

    /// <summary>
    /// 输入流为 null 时抛 ArgumentNullException，指名编码时也一样
    /// </summary>
    [Fact]
    public void 分隔文字档的输入流为null时抛出()
    {
        Assert.Throws<ArgumentNullException>(() => TextEncodingResolver.ResolveForReader(null, null!));
        Assert.Throws<ArgumentNullException>(() => TextEncodingResolver.ResolveForReader("utf-8", null!));
    }

    /// <summary>
    /// 按前导字节选对应的无 BOM 编码写出正文，让上面的表可以逐条列
    /// </summary>
    /// <param name="preamble">前导字节</param>
    /// <param name="body">正文</param>
    private static byte[] EncodeWithBomLess(byte[] preamble, string body)
    {
        if (preamble.Length >= 3)
        {
            return ImportFixtures.Utf8NoBom.GetBytes(body);
        }

        if (preamble.Length == 2 && preamble[0] == 0xFF)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(body);
        }

        if (preamble.Length == 2 && preamble[0] == 0xFE)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes(body);
        }

        return preamble[0] == 0xFF
            ? new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetBytes(body)
            : new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes(body);
    }
}
