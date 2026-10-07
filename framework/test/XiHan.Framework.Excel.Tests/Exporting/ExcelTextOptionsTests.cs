// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// 文字档导出选项测试
/// </summary>
public class ExcelTextOptionsTests
{
    /// <summary>
    /// 未赋值时八项设置取约定的默认值
    /// </summary>
    [Fact]
    public void 默认值_分隔符模式与带BOM的UTF8()
    {
        var options = new ExcelTextOptions();

        Assert.Equal(ExcelTextLayout.Delimited, options.Layout);
        Assert.Null(options.Delimiter);
        Assert.Equal("utf-8-bom", options.EncodingName);
        Assert.Equal("\r\n", options.NewLine);
        Assert.True(options.IncludeHeader);
        Assert.Equal(ExcelTextQuote.Minimal, options.Quote);
        Assert.Equal(ExcelTextOverflow.Throw, options.Overflow);
        Assert.True(options.EscapeFormulaPrefix);
    }

    /// <summary>
    /// 编码名与行尾为空时赋值即抛异常
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 编码名不能为空(string encodingName)
        => Assert.Throws<ArgumentException>(() => new ExcelTextOptions { EncodingName = encodingName });

    /// <summary>
    /// 编码名为 null 时同样抛参数异常
    /// </summary>
    [Fact]
    public void 编码名为null时抛异常()
        => Assert.Throws<ArgumentException>(() => new ExcelTextOptions { EncodingName = null! });

    /// <summary>
    /// 行尾为空字符串时抛异常，换行符本身不受空白检查影响
    /// </summary>
    [Fact]
    public void 行尾为空时抛异常()
    {
        Assert.Throws<ArgumentException>(() => new ExcelTextOptions { NewLine = string.Empty });
        Assert.Throws<ArgumentException>(() => new ExcelTextOptions { NewLine = null! });

        Assert.Equal("\n", new ExcelTextOptions { NewLine = "\n" }.NewLine);
    }

    /// <summary>
    /// 派生副本只改动指名的设置，其余设置原样带过去，原对象不被改动
    /// </summary>
    [Fact]
    public void with派生副本不改动原对象()
    {
        var original = new ExcelTextOptions { EncodingName = "big5", IncludeHeader = false, NewLine = "\n" };

        var derived = original with { Quote = ExcelTextQuote.None };

        Assert.NotSame(original, derived);
        Assert.Equal(ExcelTextQuote.None, derived.Quote);
        Assert.Equal(ExcelTextQuote.Minimal, original.Quote);

        Assert.Equal("big5", derived.EncodingName);
        Assert.False(derived.IncludeHeader);
        Assert.Equal("\n", derived.NewLine);
        Assert.Equal(ExcelTextLayout.Delimited, derived.Layout);
    }

    /// <summary>
    /// 派生副本同样走属性上的校验
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void with派生副本仍校验编码名(string encodingName)
        => Assert.Throws<ArgumentException>(() => new ExcelTextOptions { EncodingName = "big5" } with { EncodingName = encodingName });
}
