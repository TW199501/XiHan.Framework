// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Importing;

/// <summary>
/// 档头签章判别测试
/// </summary>
/// <remarks>
/// 判据只有两类固定签名，其余一律判不出来；嗅探绝不吃调用方的流，因此「位置复原」是本类型最要紧的一条断言。
/// </remarks>
public class ExcelFormatProbeTests
{
    /// <summary>
    /// OLE 复合文件签名判为旧版二进制工作簿
    /// </summary>
    /// <param name="header">档头字节</param>
    [Theory]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })]
    public void OLE复合文件签章判为xls(byte[] header)
        => Assert.Equal(ExcelImportFormat.Xls, ExcelFormatProbe.Detect(HeaderStream(header)));

    /// <summary>
    /// zip 签名判为 xlsx，四字节签名不必凑满八位
    /// </summary>
    [Fact]
    public void zip签章判为xlsx()
        => Assert.Equal(ExcelImportFormat.Xlsx, ExcelFormatProbe.Detect(HeaderStream([0x50, 0x4B, 0x03, 0x04])));

    /// <summary>
    /// 伪装成 Excel 的标记语言文本判不出来，返回 null 而不是猜一个格式
    /// </summary>
    /// <param name="prefix">档头前缀</param>
    [Theory]
    [InlineData("<html")]
    [InlineData("<table")]
    [InlineData("<?xml")]
    [InlineData("<workbook")]
    public void 伪装成xls的HTML或XML表格判不出来返回null(string prefix)
        => Assert.Null(ExcelFormatProbe.Detect(TextStream(prefix)));

    /// <summary>
    /// 档头不足八位不抛异常，返回 null
    /// </summary>
    [Fact]
    public void 档头不足八位不抛异常返回null()
        => Assert.Null(ExcelFormatProbe.Detect(HeaderStream([0xD0, 0xCF])));

    /// <summary>
    /// 空档不抛异常，返回 null
    /// </summary>
    [Fact]
    public void 空档返回null而不抛()
        => Assert.Null(ExcelFormatProbe.Detect(new MemoryStream()));

    /// <summary>
    /// OLE 签名只对上前四字节不算 xls：八字节全同才是复合容器
    /// </summary>
    [Fact]
    public void OLE签名部分相同不算xls()
        => Assert.Null(ExcelFormatProbe.Detect(HeaderStream([0xD0, 0xCF, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x00])));

    /// <summary>
    /// zip 签名后面接垃圾字节仍判为 xlsx：本类型只管签名，容器完整性由解析器负责
    /// </summary>
    [Fact]
    public void zip签名后接垃圾仍判为xlsx()
        => Assert.Equal(ExcelImportFormat.Xlsx, ExcelFormatProbe.Detect(HeaderStream([0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFE, 0x01, 0x02])));

    /// <summary>
    /// 嗅探读的是流起点的档头，并把位置复原到调用方留下的位置，不是复零、也不吃掉档头
    /// </summary>
    [Fact]
    public void 嗅探后流位置复原到原位置()
    {
        using var stream = HeaderStream([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00]);
        stream.Position = 3;

        // 位置停在中间也按流起点判：中间那段既不是文件身份也不是文档开头
        Assert.Equal(ExcelImportFormat.Xlsx, ExcelFormatProbe.Detect(stream));
        Assert.Equal(3, stream.Position);

        var buffer = new byte[8];
        var read = stream.Read(buffer, 0, 8);
        Assert.Equal(5, read);
        Assert.Equal([0x04, 0x00, 0x00, 0x00], buffer[..4]);
    }

    /// <summary>
    /// 不可定位的流直接拒绝：嗅探做不到复位，不能把调用方的流留在半读状态
    /// </summary>
    [Fact]
    public void 不可定位的流被拒并点名input()
    {
        using var backing = HeaderStream([0xD0, 0xCF, 0x11, 0xE0]);
        using var forward = new ForwardOnlyStream(backing);

        var failure = Assert.Throws<ArgumentException>(() => ExcelFormatProbe.Detect(forward));

        Assert.Equal("input", failure.ParamName);
        Assert.Contains("可定位", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 位置复原不只在成功路径成立：档尾字节不足时同样复位
    /// </summary>
    [Fact]
    public void 判不出来时也一样复原流位置()
    {
        using var stream = TextStream("<html><body>");

        Assert.Null(ExcelFormatProbe.Detect(stream));
        Assert.Equal(0, stream.Position);
        Assert.Equal('<', (char)stream.ReadByte());
    }

    /// <summary>
    /// 判别失败的消息要能把档头读出来：十六进制与可打印形式并列，不可打印字节以中点代替
    /// </summary>
    [Fact]
    public void 档头可读形式含十六进制与可打印字符()
    {
        var described = ExcelFormatProbe.DescribeHeader([0xD0, 0xCF, 0x41, 0x20]);

        Assert.Contains("D0 CF 41 20", described, StringComparison.Ordinal);
        Assert.Contains("··A", described, StringComparison.Ordinal);
        Assert.Equal("（档为空，没有任何字节可比对）", ExcelFormatProbe.DescribeHeader([]));
    }

    /// <summary>
    /// 标记语言伪装被认出来时交出起始标记，供消息点名 HTML 表格与 XML 表格
    /// </summary>
    [Theory]
    [InlineData("<html", "<html")]
    [InlineData("<?xml", "<?xml")]
    [InlineData("<table", "<table")]
    [InlineData("  <workbook", "<workbook")]
    public void 认出标记语言文本的起始标记(string text, string expected)
        => Assert.Equal(expected, ExcelFormatProbe.DescribeMarkupOpening(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// 非标记语言开头不认伪装，返回 null
    /// </summary>
    [Theory]
    [InlineData("a,b,c")]
    [InlineData("提單號")]
    public void 文字档开头不认作伪装(string text)
        => Assert.Null(ExcelFormatProbe.DescribeMarkupOpening(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// 取档头字节本身也要复位流位置，且短档只交回实际读到的字节
    /// </summary>
    [Fact]
    public void 取档头交出实际读到的字节数()
    {
        using var stream = HeaderStream([0x01, 0x02]);

        Assert.Equal(2, ExcelFormatProbe.ReadHeader(stream, 8).Length);
        Assert.Equal(0, stream.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExcelFormatProbe.ReadHeader(stream, 0));
        Assert.Throws<ArgumentNullException>(() => ExcelFormatProbe.ReadHeader(null!, 8));
    }

    /// <summary>
    /// 档头长度要容得下「最长前导字节 ＋ 起始标记」，否则带前导字节的伪装档认不出形态
    /// </summary>
    [Fact]
    public void 档头长度容得下前导字节与起始标记()
        => Assert.True(ExcelFormatProbe.HeaderByteCount >= 16, $"档头只取 {ExcelFormatProbe.HeaderByteCount} 字节");

    /// <summary>
    /// 带 UTF-8 前导字节的标记语言文本照样认出起始标记：前导字节不是文档内容
    /// </summary>
    /// <remarks>
    /// SpreadsheetML 多写成 <c>EF BB BF 3C 3F 78 6D 6C</c>。留着前导字节时第一个字符是 <c>U+FEFF</c>，
    /// 它不是空白字符，起始标记因此认不出来，消息只能说「判不出格式」而点不出伪装形态。
    /// </remarks>
    [Fact]
    public void 带UTF8前导字节时认出起始标记()
    {
        byte[] header = [0xEF, 0xBB, 0xBF, .. "<?xml version"u8.ToArray()];

        Assert.Equal("<?xml", ExcelFormatProbe.DescribeMarkupOpening(header));
    }

    /// <summary>
    /// UTF-16 的两个前导字节按 UTF-16 解码再认形态，不然尖括号后面每个字符都跟着一个 <c>0x00</c>
    /// </summary>
    [Fact]
    public void 带UTF16前导字节时认出起始标记()
    {
        byte[] littleEndian =
            [0xFF, 0xFE, .. new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("<html><body>")];

        byte[] bigEndian =
            [0xFE, 0xFF, .. new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes("<html><body>")];

        Assert.Equal("<html", ExcelFormatProbe.DescribeMarkupOpening(littleEndian));
        Assert.Equal("<html", ExcelFormatProbe.DescribeMarkupOpening(bigEndian));
    }

    /// <summary>
    /// UTF-32 的前导字节与 UTF-16 LE 前两位相同，判定要排在 UTF-16 之前；十六字节档头只容得下三个字符，
    /// 认出可见的那一段
    /// </summary>
    [Fact]
    public void 带UTF32前导字节时认出可见的那段标记()
    {
        byte[] header =
            [0xFF, 0xFE, 0x00, 0x00, .. new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetBytes("<html>")];

        Assert.Equal("<ht", ExcelFormatProbe.DescribeMarkupOpening(header.AsSpan(0, ExcelFormatProbe.HeaderByteCount)));
    }

    /// <summary>
    /// 前导字节之后不是标记时不认伪装：剥掉前导字节不等于放宽判定
    /// </summary>
    /// <param name="preamble">前导字节</param>
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    public void 带前导字节的文字档不认作伪装(byte[] preamble)
    {
        byte[] header = [.. preamble, .. "a,b,c"u8.ToArray()];

        Assert.Null(ExcelFormatProbe.DescribeMarkupOpening(header));
    }

    /// <summary>
    /// 档头只有前导字节、没有内容时不认伪装，也不抛
    /// </summary>
    [Fact]
    public void 只有前导字节时不认作伪装()
    {
        Assert.Null(ExcelFormatProbe.DescribeMarkupOpening([0xEF, 0xBB, 0xBF]));
        Assert.Null(ExcelFormatProbe.DescribeMarkupOpening([0xFF, 0xFE]));
    }

    private static MemoryStream HeaderStream(byte[] header) => new MemoryStream(header);

    private static MemoryStream TextStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));
}
