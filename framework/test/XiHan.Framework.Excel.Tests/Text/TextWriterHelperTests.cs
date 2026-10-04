// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Text;

namespace XiHan.Framework.Excel.Tests.Text;

/// <summary>
/// 文字导出助手测试
/// </summary>
public class TextWriterHelperTests
{
    /// <summary>
    /// 最小引号策略只在值内含分隔符、引号或换行时加引号，引号本身翻倍转义
    /// </summary>
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    public void 最小引号策略遵循RFC4180(string raw, string expected)
        => Assert.Equal(expected, TextWriterHelper.QuoteIfNeeded(raw, ',', ExcelTextQuote.Minimal));

    /// <summary>
    /// 全量引号策略给每个值裹一层引号
    /// </summary>
    [Fact]
    public void 全量引号包住每个值()
        => Assert.Equal("\"abc\"", TextWriterHelper.QuoteIfNeeded("abc", ',', ExcelTextQuote.All));

    /// <summary>
    /// 全量引号同样转义值内的引号
    /// </summary>
    [Fact]
    public void 全量引号转义值内引号()
        => Assert.Equal("\"a\"\"b\"", TextWriterHelper.QuoteIfNeeded("a\"b", ',', ExcelTextQuote.All));

    /// <summary>
    /// 不加引号策略将分隔符与换行改写为空格，值内与分隔符无关的字符原样保留
    /// </summary>
    [Theory]
    [InlineData("a,b", ',', "a b")]
    [InlineData("a;b", ',', "a;b")]
    [InlineData("a\tb", '\t', "a b")]
    [InlineData("a\nb", ',', "a b")]
    [InlineData("a\r\nb", ',', "a b")]
    [InlineData("a\"b", ',', "a\"b")]
    public void 不加引号策略改写分隔符与换行(string raw, char delimiter, string expected)
        => Assert.Equal(expected, TextWriterHelper.QuoteIfNeeded(raw, delimiter, ExcelTextQuote.None));

    /// <summary>
    /// 引号策略取到未定义的枚举值时抛异常，不静默按最小策略处理
    /// </summary>
    [Fact]
    public void 未定义的引号策略抛异常()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TextWriterHelper.QuoteIfNeeded("abc", ',', (ExcelTextQuote)99));

    /// <summary>
    /// 公式注入防护对四个前缀加单引号
    /// </summary>
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+86123", "'+86123")]
    [InlineData("-5", "'-5")]
    [InlineData("@a", "'@a")]
    [InlineData("正常", "正常")]
    public void 公式注入防护对四个前缀加单引号(string raw, string expected)
        => Assert.Equal(expected, TextWriterHelper.EscapeFormula(raw));

    /// <summary>
    /// 空字符串做公式防护时原样返回，不会只写出一个单引号
    /// </summary>
    [Fact]
    public void 空字符串不做公式注入防护()
        => Assert.Equal(string.Empty, TextWriterHelper.EscapeFormula(string.Empty));

    /// <summary>
    /// 编码名解析_bom变体不写BOM进Encoding而是单独处理
    /// </summary>
    [Fact]
    public void 编码名解析_bom变体不写BOM进Encoding而是单独处理()
    {
        Assert.Equal(new UTF8Encoding(false), TextWriterHelper.ResolveEncoding("utf-8"));
        Assert.Equal(Encoding.GetEncoding("Big5"), TextWriterHelper.ResolveEncoding("big5"));
        Assert.Throws<ArgumentException>(() => TextWriterHelper.ResolveEncoding("utf-8-bom!"));
    }

    /// <summary>
    /// BOM 预设名与解析出的 Encoding 前导字节一致，导出器据此不再手写 BOM
    /// </summary>
    [Fact]
    public void BOM预设名由Encoding自己写前导字节()
    {
        Assert.True(TextWriterHelper.IsBomPreset("utf-8-bom"));
        Assert.True(TextWriterHelper.IsBomPreset("UTF8-BOM"));
        Assert.False(TextWriterHelper.IsBomPreset("utf-8"));
        Assert.False(TextWriterHelper.IsBomPreset("big5"));
        Assert.False(TextWriterHelper.IsBomPreset(" "));

        Assert.Equal([0xEF, 0xBB, 0xBF], TextWriterHelper.ResolveEncoding("UTF-8-BOM").GetPreamble());
        Assert.Equal([], TextWriterHelper.ResolveEncoding("UTF-8").GetPreamble());
        Assert.Equal([], TextWriterHelper.ResolveEncoding("big5").GetPreamble());
    }

    /// <summary>
    /// 编码名大小写与首尾空白不影响解析
    /// </summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData(" UTF-8 ")]
    [InlineData("utf8")]
    public void 编码名解析大小写不敏感(string encodingName)
        => Assert.Equal(new UTF8Encoding(false), TextWriterHelper.ResolveEncoding(encodingName));

    /// <summary>
    /// 空编码名无法解析，直接抛异常，不退回默认编码
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void 空编码名解析抛异常(string encodingName)
        => Assert.Throws<ArgumentException>(() => TextWriterHelper.ResolveEncoding(encodingName));

    /// <summary>
    /// 值转文本：空值给空字串，数值与日期一律按不变文化输出
    /// </summary>
    [Fact]
    public void 值转文本用不变文化且空值为空字串()
    {
        Assert.Equal(string.Empty, TextWriterHelper.ValueToText(null, null, null));
        Assert.Equal("1.5", TextWriterHelper.ValueToText(1.5m, null, null));
        Assert.Equal("1234.5", TextWriterHelper.ValueToText(1234.5m, null, null));
        Assert.Equal("01/02/2026 00:00:00", TextWriterHelper.ValueToText(new DateTime(2026, 1, 2), null, null));
        Assert.Equal("2026-01-02", TextWriterHelper.ValueToText(new DateTime(2026, 1, 2), "yyyy-MM-dd", null));
        Assert.Equal("1.50", TextWriterHelper.ValueToText(1.5m, "0.00", null));
    }

    /// <summary>
    /// 值转文本：Excel 数字格式串不套用在文字档，只有文本格式生效
    /// </summary>
    [Fact]
    public void 值转文本不套用Excel数字格式()
    {
        Assert.Equal("1.5", TextWriterHelper.ValueToText(1.5m, null, "0.00"));
        Assert.Equal("01/02/2026 00:00:00", TextWriterHelper.ValueToText(new DateTime(2026, 1, 2), null, "yyyy-MM-dd"));

        // 文本格式与数字格式同时给出时，文本格式优先
        Assert.Equal("2026-01-02", TextWriterHelper.ValueToText(new DateTime(2026, 1, 2), "yyyy-MM-dd", "yyyy-MM-dd"));
    }

    /// <summary>
    /// 值转文本：不能套格式的值退回自身字符串表示
    /// </summary>
    [Fact]
    public void 值转文本对非可格式化值退回ToString()
    {
        Assert.Equal("ABC", TextWriterHelper.ValueToText("ABC", null, null));
        Assert.Equal("ABC", TextWriterHelper.ValueToText("ABC", "0.00", null));
    }

    /// <summary>
    /// 值转文本：非法格式串抛出转换异常，不吞掉后退回默认输出
    /// </summary>
    [Fact]
    public void 值转文本非法格式串抛异常()
    {
        Assert.Throws<FormatException>(() => TextWriterHelper.ValueToText(1.5m, "q", null));
        Assert.Throws<FormatException>(() => TextWriterHelper.ValueToText(new DateTime(2026, 1, 2), "q", null));
    }
}
