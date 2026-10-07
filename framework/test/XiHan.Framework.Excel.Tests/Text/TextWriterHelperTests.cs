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
    /// 免引号策略是否会改写该值由 helper 直接判定，不靠改写前后的字符串比较反推
    /// </summary>
    [Theory]
    [InlineData("a,b", ',', true)]        // 含分隔符
    [InlineData("a\nb", ',', true)]       // 含换行
    [InlineData("a\rb", ',', true)]       // 含回车
    [InlineData("abc", ',', false)]       // 干净值
    [InlineData("a\"b", ',', false)]      // 值内引号与分隔符无关，免引号策略原样保留
    [InlineData("a b", ' ', true)]        // 空格作分隔符，值内含空格
    public void 判定值内是否含不可原样写出的字符(string raw, char delimiter, bool expected)
        => Assert.Equal(expected, TextWriterHelper.ContainsUnquotable(raw, delimiter));

    /// <summary>
    /// 公式防护是否会改写该值由 helper 直接判定，且与变换结果一致
    /// </summary>
    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("-5", true)]
    [InlineData("+86123", true)]
    [InlineData("@a", true)]
    [InlineData("\t=1+1", true)]   // 制表符起首
    [InlineData("\r=1+1", true)]   // 回车起首
    [InlineData("正常", false)]
    [InlineData("\u0000x", false)] // NUL 起首不在集合内
    [InlineData("", false)]
    public void 判定值是否会被公式防护改写(string raw, bool expected)
    {
        Assert.Equal(expected, TextWriterHelper.NeedsFormulaEscape(raw));

        // 判定为真时变换必定改动该值，为假时必定原样返回
        Assert.Equal(expected, !string.Equals(TextWriterHelper.EscapeFormula(raw), raw, StringComparison.Ordinal));
    }

    /// <summary>
    /// 引号策略取到未定义的枚举值时抛异常，不静默按最小策略处理
    /// </summary>
    [Fact]
    public void 未定义的引号策略抛异常()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TextWriterHelper.QuoteIfNeeded("abc", ',', (ExcelTextQuote)99));

    /// <summary>
    /// 公式注入防护对六个起首字符加单引号，其余一律原样
    /// </summary>
    /// <remarks>
    /// 正例覆盖 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符、回车六个起首字符；反例除「干净值」外，
    /// 还有一条「含运算子但不在开头」与一条「NUL 起首」。
    /// </remarks>
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+86123", "'+86123")]
    [InlineData("-5", "'-5")]
    [InlineData("@a", "'@a")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("\r=1+1", "'\r=1+1")]
    [InlineData("正常", "正常")]
    [InlineData("1+1", "1+1")]
    [InlineData("\u0000x", "\u0000x")]
    public void 公式注入防护对六个起首字符加单引号(string raw, string expected)
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
        Assert.Equal(StrictUtf8(encoderShouldEmitBom: false), TextWriterHelper.ResolveEncoding("utf-8"));
        Assert.Equal(
            Encoding.GetEncoding("Big5", new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            TextWriterHelper.ResolveEncoding("big5"));
        Assert.Throws<ArgumentException>(() => TextWriterHelper.ResolveEncoding("utf-8-bom!"));
    }

    /// <summary>
    /// 解析出的编码两侧都是异常回退，不可映射字符抛出而非静默替换成问号
    /// </summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("big5")]
    public void 解析出的编码两侧都是异常回退(string encodingName)
    {
        var encoding = TextWriterHelper.ResolveEncoding(encodingName);

        Assert.IsType<EncoderExceptionFallback>(encoding.EncoderFallback);
        Assert.IsType<DecoderExceptionFallback>(encoding.DecoderFallback);
    }

    /// <summary>
    /// 目标编码收不下的字符在编码阶段就抛，产不出问号字节的坏档
    /// </summary>
    /// <remarks>
    /// 严格回退下抛 <see cref="EncoderFallbackException"/>；收得下的字符逐字节与宽松编码一致。
    /// </remarks>
    [Fact]
    public void 不可映射字符编码时抛异常()
    {
        var strictBig5 = TextWriterHelper.ResolveEncoding("big5");

        Assert.Throws<EncoderFallbackException>(() => strictBig5.GetBytes("提单号"));
        Assert.Equal(Encoding.GetEncoding("Big5").GetBytes("提單號"), strictBig5.GetBytes("提單號"));
    }

    /// <summary>
    /// 取与 ResolveEncoding 同形的严格回退 UTF-8，供相等断言取参照
    /// </summary>
    private static UTF8Encoding StrictUtf8(bool encoderShouldEmitBom)
        => new(encoderShouldEmitBom, throwOnInvalidBytes: true);

    /// <summary>
    /// BOM 预设名与解析出的 Encoding 前导字节一致
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
        => Assert.Equal(StrictUtf8(encoderShouldEmitBom: false), TextWriterHelper.ResolveEncoding(encodingName));

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
