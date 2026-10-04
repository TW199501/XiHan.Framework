// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Text;

namespace XiHan.Framework.Excel.Tests.Text;

/// <summary>
/// 固定宽度补位与截断算法测试
/// </summary>
/// <remarks>
/// 全部用例按字节判定，不按字符判定：固定宽度档由读档方按字节位置切列，字符数与字节数不等时（一个汉字在
/// Big5 下 2 字节、在 UTF-8 下 3 字节）按字符补位会直接让整行列位错位。编码一律取
/// <see cref="TextWriterHelper.ResolveEncoding"/> 产出的严格回退编码，与导出器实际用的那一份一致。
/// </remarks>
public class FixedWidthTests
{
    /// <summary>
    /// Big5 下按字节补位：一个汉字两字节，补位差额取字节数
    /// </summary>
    [Theory]
    [InlineData("中", 2, "中")]           // Big5 下「中」正好 2 字节，无需补位
    [InlineData("中", 4, "中  ")]         // 2 字节内容 + 2 个补位空格 = 4 字节
    [InlineData("AB", 4, "AB  ")]         // 半形 2 字节 + 2 个补位空格
    public void Big5按字节补位(string value, int width, string expectedPadded)
    {
        var encoding = TextWriterHelper.ResolveEncoding("big5");

        var result = TextWriterHelper.PadToWidth(
            value, width, encoding, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw);

        Assert.Equal(expectedPadded, result);
        Assert.Equal(width, encoding.GetByteCount(result));
    }

    /// <summary>
    /// 内容字节数不足列宽时补满到列宽：UTF-8 下一个汉字占 3 字节，按字符补位会少补 2 格
    /// </summary>
    [Fact]
    public void UTF8下按字节补满宽度差额()
    {
        var encoding = TextWriterHelper.ResolveEncoding("utf-8");

        var result = TextWriterHelper.PadToWidth("中", 8, encoding, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw);

        Assert.Equal("中" + new string(' ', 5), result);
        Assert.Equal(8, encoding.GetByteCount(result));
    }

    /// <summary>
    /// 超宽时预设抛异常且信息带列宽与实际字节数
    /// </summary>
    [Fact]
    public void 超宽时预设抛异常且信息带列宽与实际字节数()
    {
        // 「中中中中」在 Big5 下 8 字节，列宽 4 字节；短横以下的字符一律取 Big5 收得下的汉字，
        // 免得用例被编码回退异常干扰（不可映射的行为另有专门用例）
        var ex = Assert.Throws<InvalidOperationException>(() => TextWriterHelper.PadToWidth(
            "中中中中", 4, TextWriterHelper.ResolveEncoding("big5"),
            ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw));

        Assert.Contains("实际 8 字节", ex.Message, StringComparison.Ordinal);
        Assert.Contains("列宽 4 字节", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 截断不切断多字节字符
    /// </summary>
    [Fact]
    public void 截断不切断多字节字符()
    {
        var encoding = TextWriterHelper.ResolveEncoding("big5");

        var result = TextWriterHelper.PadToWidth("中中中", 5, encoding, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Truncate);

        var bytes = encoding.GetBytes(result);
        Assert.Equal(5, bytes.Length);                       // 2 个汉字 + 1 个补位空格
        Assert.Equal("中中 ", result);
        Assert.DoesNotContain('\uFFFD', result);             // 没有出现替换字符，说明没切断多字节字符
    }

    /// <summary>
    /// 截断同样不切断代理对：字素整体取舍，不留半个代理
    /// </summary>
    [Fact]
    public void 截断不切断代理对()
    {
        var encoding = TextWriterHelper.ResolveEncoding("utf-8");

        // 「A😀B」= 1 + 4 + 1 = 6 字节，列宽 3 字节容不下😀，取舍结果只剩「A」再补 2 格
        var result = TextWriterHelper.PadToWidth("A😀B", 3, encoding, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Truncate);

        Assert.Equal("A  ", result);
        Assert.Equal(3, encoding.GetByteCount(result));
        Assert.DoesNotContain('\uD83D', result);             // 没有留下前代理字符的一半
    }

    /// <summary>
    /// 左对齐补位在右侧
    /// </summary>
    [Fact]
    public void 左对齐补位在右侧()
    {
        var result = TextWriterHelper.PadToWidth("A", 3, Encoding.UTF8, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw);

        Assert.Equal("A  ", result);
    }

    /// <summary>
    /// 数字靠右补零
    /// </summary>
    [Fact]
    public void 数字靠右补零()
    {
        var result = TextWriterHelper.PadToWidth("12", 4, Encoding.UTF8, ExcelTextPadding.Left, '0', ExcelTextOverflow.Throw);

        Assert.Equal("0012", result);
    }

    /// <summary>
    /// 补位字符在目标编码下不是单字节时抛异常：按字节差额填多字节字符会直接超出列宽
    /// </summary>
    [Theory]
    [InlineData("utf-8", '中')]                              // UTF-8 下 3 字节
    [InlineData("big5", '中')]                               // Big5 下 2 字节
    [InlineData("utf-8", '０')]                              // 全形零，UTF-8 下 3 字节
    public void 补位字符不是单字节时抛异常(string encodingName, char padChar)
    {
        var ex = Assert.Throws<ArgumentException>(() => TextWriterHelper.PadToWidth(
            "A", 6, TextWriterHelper.ResolveEncoding(encodingName),
            ExcelTextPadding.Right, padChar, ExcelTextOverflow.Throw));

        Assert.Contains("单字节", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 补位字符在目标编码里根本收不下时同样按非法补位字符拒绝，不交给编码器留半档
    /// </summary>
    [Fact]
    public void 补位字符不可映射时按非法补位字符抛异常()
    {
        // 「单」不在 Big5（代码页 950）字符集内，严格编码器会拒绝它
        var ex = Assert.Throws<ArgumentException>(() => TextWriterHelper.PadToWidth(
            "A", 6, TextWriterHelper.ResolveEncoding("big5"),
            ExcelTextPadding.Right, '单', ExcelTextOverflow.Throw));

        Assert.Contains("单字节", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 待写内容在目标编码收不下时抛编码回退异常，不静默替换成问号字节
    /// </summary>
    /// <remarks>
    /// 与分隔符路径同一标准：固定宽度按字节位置切列，一个被替换成 <c>?</c> 的字符会让该格字节数变化、后续列整体错位，
    /// 因此补位前算字节数用的就是严格回退编码，收不下直接抛。
    /// </remarks>
    [Fact]
    public void 内容不可映射时抛编码回退异常()
    {
        Assert.Throws<EncoderFallbackException>(() => TextWriterHelper.PadToWidth(
            "提单号", 12, TextWriterHelper.ResolveEncoding("big5"),
            ExcelTextPadding.Right, ' ', ExcelTextOverflow.Truncate));
    }

    /// <summary>
    /// 列宽为负数不是合法输入，直接抛，不猜宽度
    /// </summary>
    [Fact]
    public void 负的列宽抛异常()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TextWriterHelper.PadToWidth(
            "A", -1, TextWriterHelper.ResolveEncoding("utf-8"),
            ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw));

        Assert.Equal("widthBytes", ex.ParamName);
    }

    /// <summary>
    /// 补位方向取未定义值时抛异常，不静默按默认的右侧补位处理
    /// </summary>
    [Fact]
    public void 未定义的补位方向抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWriterHelper.PadToWidth(
            "A", 3, TextWriterHelper.ResolveEncoding("utf-8"),
            (ExcelTextPadding)99, ' ', ExcelTextOverflow.Throw));
    }

    /// <summary>
    /// 超宽策略取未定义值时抛异常，不静默按 Throw 或 Truncate 处理
    /// </summary>
    [Fact]
    public void 未定义的超宽策略抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWriterHelper.PadToWidth(
            "ABC", 1, TextWriterHelper.ResolveEncoding("utf-8"),
            ExcelTextPadding.Right, ' ', (ExcelTextOverflow)99));
    }

    /// <summary>
    /// 内容与编码是硬前提，缺失时抛参数异常，不退回默认编码或空字串
    /// </summary>
    [Fact]
    public void 空内容与空编码抛参数异常()
    {
        Assert.Throws<ArgumentNullException>(() => TextWriterHelper.PadToWidth(
            null!, 4, TextWriterHelper.ResolveEncoding("utf-8"),
            ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw));

        Assert.Throws<ArgumentNullException>(() => TextWriterHelper.PadToWidth(
            "A", 4, null!, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw));
    }

    /// <summary>
    /// 空内容不是非法输入：整格都是补位，字节数仍等于列宽
    /// </summary>
    [Fact]
    public void 空内容补满整格宽度()
    {
        var encoding = TextWriterHelper.ResolveEncoding("big5");

        var result = TextWriterHelper.PadToWidth(string.Empty, 4, encoding, ExcelTextPadding.Right, ' ', ExcelTextOverflow.Throw);

        Assert.Equal("    ", result);
        Assert.Equal(4, encoding.GetByteCount(result));
    }
}
