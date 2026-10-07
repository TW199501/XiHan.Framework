// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// MiniExcel 固定版式模板渲染测试
/// </summary>
/// <remarks>
/// 模板由 <see cref="TemplateFactory" /> 生成到内存流，每条测试各自建模板流。
/// 断言用 ClosedXML 回读渲染产物：集合占位从所在行原地起写，第一项落在占位行本身，模板下方的静态行整体下移。
/// </remarks>
public class MiniExcelTemplateRendererTests
{
    /// <summary>
    /// 单值占位与集合占位都被数据替换，集合占位原地展开
    /// </summary>
    [Fact]
    public async Task 模板占位符被数据替换()
    {
        var template = TemplateFactory.BuildInvoiceTemplate();
        var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            template,
            new { Company = "曦寒物流", Items = new[] { new { Name = "A", Qty = 2 } } },
            TestContext.Current.CancellationToken);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        Assert.Equal("曦寒物流", workbook.Worksheet(1).Cell("A1").GetString());
        Assert.Equal("A", workbook.Worksheet(1).Cell("A2").GetString());
    }

    /// <summary>
    /// 模板流为空时抛明确异常而不是产出空档
    /// </summary>
    [Fact]
    public async Task 模板流为空时抛明确异常而不是产出空档()
    {
        var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, new MemoryStream(), new { }, TestContext.Current.CancellationToken));

        Assert.Contains("模板内容为空", failure.Message, StringComparison.Ordinal);
        Assert.Equal("template", failure.ParamName);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 集合项从占位行原地向下展开，同一行的多列一起走，模板原有的静态行被下移
    /// </summary>
    [Fact]
    public async Task 集合占位原地展开并下移模板后续行()
    {
        var template = TemplateFactory.BuildInvoiceWithFooterTemplate();
        var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            template,
            new { Company = "曦寒物流", Items = new[] { new { Name = "A", Qty = 2 }, new { Name = "B", Qty = 3 } } },
            TestContext.Current.CancellationToken);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("曦寒物流", sheet.Cell("A1").GetString());
        Assert.Equal("A", sheet.Cell("A2").GetString());
        Assert.Equal("2", sheet.Cell("B2").GetString());
        Assert.Equal("B", sheet.Cell("A3").GetString());
        Assert.Equal("3", sheet.Cell("B3").GetString());

        // 模板里写在 A4 的「合计」被多出来的一行数据顶到 A5
        Assert.Equal("合计", sheet.Cell("A5").GetString());
    }

    /// <summary>
    /// 渲染保留模板自带的工作表名，版面来自模板而不是 CLR 型别
    /// </summary>
    [Fact]
    public async Task 渲染保留模板的工作表名()
    {
        var template = TemplateFactory.Build("运单存根", ("A1", "{{Company}}"));
        var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(output, template, new { Company = "曦寒物流" }, TestContext.Current.CancellationToken);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        Assert.Equal("运单存根", workbook.Worksheet(1).Name);
    }

    /// <summary>
    /// 数据里缺键时该格留空而不是抛，也不把占位符原文留下
    /// </summary>
    [Fact]
    public async Task 数据缺键时该格留空而不抛()
    {
        var template = TemplateFactory.Build(("A1", "{{Company}}"), ("A2", "{{Missing}}"));
        var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(output, template, new { Company = "曦寒物流" }, TestContext.Current.CancellationToken);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("曦寒物流", sheet.Cell("A1").GetString());
        Assert.True(sheet.Cell("A2").IsEmpty());
        Assert.DoesNotContain("{{", sheet.Cell("A2").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 数值与日期按库的形态落格：数值仍是数值，日期落成文本而不是日期格
    /// </summary>
    [Fact]
    public async Task 数值格保持数值而日期格落成文本()
    {
        var template = TemplateFactory.Build(("A1", "{{Weight}}"), ("A2", "{{Eta}}"));
        var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output, template,
            new { Weight = 1.5m, Eta = new DateTime(2026, 1, 2) },
            TestContext.Current.CancellationToken);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(XLDataType.Number, sheet.Cell("A1").DataType);
        Assert.Equal(1.5d, sheet.Cell("A1").GetDouble(), 2);
        Assert.Equal(XLDataType.Text, sheet.Cell("A2").DataType);
        Assert.StartsWith("2026-01-02", sheet.Cell("A2").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 三个参数为 null 各自抛 ArgumentNullException 并点名参数，且不产出半个字节
    /// </summary>
    [Fact]
    public async Task 入参为null时抛ArgumentNullException并点名参数()
    {
        var output = new MemoryStream();

        var nullOutput = await Assert.ThrowsAsync<ArgumentNullException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(null!, TemplateFactory.BuildInvoiceTemplate(), new { Company = "曦寒物流" }, TestContext.Current.CancellationToken));
        Assert.Equal("output", nullOutput.ParamName);

        var nullTemplate = await Assert.ThrowsAsync<ArgumentNullException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(new MemoryStream(), null!, new { Company = "曦寒物流" }, TestContext.Current.CancellationToken));
        Assert.Equal("template", nullTemplate.ParamName);
        Assert.Equal(0, output.Length);

        var nullData = await Assert.ThrowsAsync<ArgumentNullException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.BuildInvoiceTemplate(), null!, TestContext.Current.CancellationToken));
        Assert.Equal("data", nullData.ParamName);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 模板流不可读时抛 ArgumentException
    /// </summary>
    [Fact]
    public async Task 模板流不可读时抛ArgumentException()
    {
        var template = TemplateFactory.BuildInvoiceTemplate();
        template.Dispose();

        var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, template, new { Company = "曦寒物流" }, TestContext.Current.CancellationToken));

        Assert.Equal("template", failure.ParamName);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 模板流不可定位时抛 ArgumentException
    /// </summary>
    [Fact]
    public async Task 模板流不可定位时抛ArgumentException()
    {
        using var backing = TemplateFactory.BuildInvoiceTemplate();
        using var template = new ForwardOnlyStream(backing);
        var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, template, new { Company = "曦寒物流" }, TestContext.Current.CancellationToken));

        Assert.Equal("template", failure.ParamName);
        Assert.Contains("可定位", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 取消令牌已取消时在调用库之前停止，输出流零字节
    /// </summary>
    [Fact]
    public async Task 取消令牌已取消时不渲染()
    {
        using var source = new CancellationTokenSource();

        source.Cancel();

        var template = TemplateFactory.BuildInvoiceTemplate();
        var output = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, template, new { Company = "曦寒物流" }, source.Token));

        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 取消落在渲染期间时只抛异常，不交出「渲染完成」的假象
    /// </summary>
    /// <remarks>
    /// 夹具在输出流第一次被写入时把令牌取消。
    /// </remarks>
    [Fact]
    public async Task 渲染期间取消时抛异常而不正常返回()
    {
        using var source = new CancellationTokenSource();
        using var output = new CancelOnWriteStream(source);
        using var template = TemplateFactory.BuildInvoiceTemplate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, template, new { Company = "曦寒物流" }, source.Token));
    }

    /// <summary>
    /// 取消落在渲染完成之后、回传之前这一窗口时同样抛异常，不让调用方读到「渲染已完成」
    /// </summary>
    /// <remarks>
    /// 夹具在输出流最后一次 flush 时把令牌取消。
    /// </remarks>
    [Fact]
    public async Task 渲染完成后回传前取消也不交出完成()
    {
        using var source = new CancellationTokenSource();
        using var output = new CancelOnFlushStream(source);
        using var template = TemplateFactory.BuildInvoiceTemplate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, template, new { Company = "曦寒物流" }, source.Token));
    }

    /// <summary>
    /// 模板不是 xlsx 容器时库的容器异常原样透传，且输出流零字节
    /// </summary>
    [Fact]
    public async Task 损坏模板透传容器异常()
    {
        using var template = new MemoryStream(Encoding.UTF8.GetBytes("this is not a xlsx file"));
        var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, template, new { Company = "曦寒物流" }, TestContext.Current.CancellationToken));

        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 渲染后模板流被库关闭，重复渲染必须每次交回新的模板流
    /// </summary>
    [Fact]
    public async Task 渲染后模板流被关闭而产物流仍可用()
    {
        var template = TemplateFactory.BuildInvoiceTemplate();
        var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(output, template, new { Company = "一" }, TestContext.Current.CancellationToken);

        Assert.False(template.CanRead);
        Assert.True(output.CanWrite);
        Assert.True(output.Length > 0);

        // 复用已关闭的模板流会得到入口的 ArgumentException，而换一条新模板流照常渲染
        await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(new MemoryStream(), template, new { Company = "二" }, TestContext.Current.CancellationToken));

        var secondTemplate = TemplateFactory.BuildInvoiceTemplate();
        var secondOutput = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(secondOutput, secondTemplate, new { Company = "二" }, TestContext.Current.CancellationToken);

        secondOutput.Position = 0;
        using var workbook = new XLWorkbook(secondOutput);
        Assert.Equal("二", workbook.Worksheet(1).Cell("A1").GetString());
    }

    /// <summary>
    /// 渲染器实现模板渲染契约
    /// </summary>
    [Fact]
    public void 渲染器实现模板渲染契约()
    {
        Assert.IsAssignableFrom<IExcelTemplateRenderer>(new MiniExcelTemplateRenderer());
    }

    /// <summary>
    /// 六个公式起首字符与渲染库自己的公式指令前缀一律拒写，抛在写出任何字节之前并点名键路径
    /// </summary>
    /// <remarks>
    /// 六个起首字符是 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>、制表符、回车；<c>$=</c> 是渲染库的公式指令前缀。
    /// </remarks>
    /// <param name="value">要渲染的字串值</param>
    /// <param name="expectedHit">消息里应当点名的那一段起首形态</param>
    [Theory]
    [InlineData("=1+1", "=")]
    [InlineData("+1", "+")]
    [InlineData("-1", "-")]
    [InlineData("@SUM(1)", "@")]
    [InlineData("\t1", "U+0009")]
    [InlineData("\r1", "U+000D")]
    [InlineData("$=1+1", "$=")]
    [InlineData("$=HYPERLINK(\"http://evil\",\"点我\")", "$=")]
    [InlineData("$=WEBSERVICE(\"http://evil\")", "$=")]
    public async Task 公式起首的字串值一律拒写且输出流零字节(string value, string expectedHit)
    {
        using var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.Build(("A1", "{{Company}}")), new { Company = value }, TestContext.Current.CancellationToken));

        Assert.Equal("data", failure.ParamName);
        Assert.Contains("Company", failure.Message, StringComparison.Ordinal);
        Assert.Contains(expectedHit, failure.Message, StringComparison.Ordinal);
        Assert.Contains("起首", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 集合元素里的命中值点名带下标的键路径，形如 <c>Items[3].Name</c>
    /// </summary>
    /// <remarks>
    /// 下标从 0 起。
    /// </remarks>
    [Fact]
    public async Task 集合元素里的公式起首值点名带下标的键路径()
    {
        using var output = new MemoryStream();

        var data = new
        {
            Company = "曦寒物流",
            Items = new[]
            {
                new { Name = "第一项" },
                new { Name = "第二项" },
                new { Name = "第三项" },
                new { Name = "$=1+1" }
            }
        };

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.BuildInvoiceTemplate(), data, TestContext.Current.CancellationToken));

        Assert.Equal("data", failure.ParamName);
        Assert.Contains("Items[3].Name", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 字典数据按键名点名，集合项是字典时同样点得出来
    /// </summary>
    [Fact]
    public async Task 字典数据的命中值按键名点名()
    {
        using var output = new MemoryStream();

        var data = new Dictionary<string, object?>
        {
            ["Company"] = "曦寒物流",
            ["Items"] = new[]
            {
                new Dictionary<string, object?> { ["Name"] = "第一项" },
                new Dictionary<string, object?> { ["Name"] = "=1+1" }
            }
        };

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.BuildInvoiceTemplate(), data, TestContext.Current.CancellationToken));

        Assert.Contains("Items[1].Name", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 公式字符不在起首时照渲染：读回原值不变，产物里也没有公式元素
    /// </summary>
    /// <remarks>
    /// 读回用 <see cref="ExcelDataReaderImporter"/> 与产物 XML 判定。
    /// </remarks>
    /// <param name="value">公式字符不在起首的字串值</param>
    [Theory]
    [InlineData("曦寒物流")]
    [InlineData("合计=1+1")]
    [InlineData("合计$=1+1")]
    [InlineData("$$=1+1")]
    [InlineData("$ =1+1")]
    [InlineData("$")]
    [InlineData("1+1")]
    public async Task 公式字符不在起首时照渲染且产物里没有公式元素(string value)
    {
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{Company}}")),
            new { Company = value },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, CountFormulaElements(output));
        Assert.Equal(value, Assert.IsType<string>(await ImportSingleValueAsync(output)));
    }

    /// <summary>
    /// 超出单元格文字上限的字串拒写，点名键路径且不产出半个字节
    /// </summary>
    /// <param name="length">字串长度</param>
    [Theory]
    [InlineData(32_768)]
    [InlineData(40_000)]
    public async Task 超出单元格文字上限的字串拒写(int length)
    {
        using var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(
                output,
                TemplateFactory.Build(("A1", "{{Company}}")),
                new { Company = new string('x', length) },
                TestContext.Current.CancellationToken));

        Assert.Contains("Company", failure.Message, StringComparison.Ordinal);
        Assert.Contains(length.ToString(System.Globalization.CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 非有限浮点拒写
    /// </summary>
    [Fact]
    public async Task 非有限浮点拒写()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            using var output = new MemoryStream();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
                .RenderAsync(output, TemplateFactory.Build(("A1", "{{V}}")), new { V = value }, TestContext.Current.CancellationToken));

            Assert.Contains("V", failure.Message, StringComparison.Ordinal);
            Assert.Contains("不是有限数", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }
    }

    /// <summary>
    /// 早于日期下限的三种日期型别一起拒写
    /// </summary>
    [Fact]
    public async Task 早于日期下限的三种日期型别一起拒写()
    {
        var values = new object[]
        {
            new DateTime(1899, 12, 31),
            new DateOnly(1899, 12, 31),
            new DateTimeOffset(1899, 12, 31, 0, 0, 0, TimeSpan.Zero),
            new DateTime(1, 1, 1)
        };

        foreach (var value in values)
        {
            using var output = new MemoryStream();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
                .RenderAsync(output, TemplateFactory.Build(("A1", "{{Eta}}")), new { Eta = value }, TestContext.Current.CancellationToken));

            Assert.Contains("Eta", failure.Message, StringComparison.Ordinal);
            Assert.Contains("1900-01-01", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }
    }

    /// <summary>
    /// 有效数字多于十五位的数值拒写，五种数值型别一并覆盖
    /// </summary>
    /// <remarks>
    /// <see cref="float"/> 先展开成 <see cref="double"/> 再判定有效数字位数。
    /// </remarks>
    [Fact]
    public async Task 有效数字多于十五位的数值拒写()
    {
        var values = new object[]
        {
            1234567890123456L,
            1234567890123456UL,
            12345678901234.5678m,
            1.2345678901234567d,
            1.2345678901234567f,
            0.1f
        };

        foreach (var value in values)
        {
            using var output = new MemoryStream();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
                .RenderAsync(output, TemplateFactory.Build(("A1", "{{Amount}}")), new { Amount = value }, TestContext.Current.CancellationToken));

            Assert.Contains("Amount", failure.Message, StringComparison.Ordinal);
            Assert.Contains("位有效数字", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }
    }

    /// <summary>
    /// 绝对值超过双精度整数上限的 <see cref="long"/>／<see cref="ulong"/>／<see cref="decimal"/> 拒写
    /// </summary>
    /// <remarks>
    /// 用例一律取有效数字在十五位内、量级越过上限的形态（末尾带一串零），命中的是量级判定而不是位数判定。
    /// </remarks>
    [Fact]
    public async Task 超过双精度整数上限的整数拒写()
    {
        var values = new object[]
        {
            1000000000000000000L,
            -1000000000000000000L,
            10000000000000000000UL,
            1000000000000000000m,
            9007199254740990000L
        };

        foreach (var value in values)
        {
            using var output = new MemoryStream();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
                .RenderAsync(output, TemplateFactory.Build(("A1", "{{Amount}}")), new { Amount = value }, TestContext.Current.CancellationToken));

            Assert.Contains("Amount", failure.Message, StringComparison.Ordinal);
            Assert.Contains("9007199254740992", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("位有效数字", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }
    }

    /// <summary>
    /// 集合元素里的越界取值同样点名带下标的键路径
    /// </summary>
    [Fact]
    public async Task 集合元素里的越界取值点名带下标的键路径()
    {
        using var output = new MemoryStream();

        var data = new
        {
            Items = new[]
            {
                new { Name = "第一项", Weight = 1.5d },
                new { Name = "第二项", Weight = double.NaN }
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.BuildInvoiceWithFooterTemplate(), data, TestContext.Current.CancellationToken));

        Assert.Contains("Items[1].Weight", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 值里含 XML 1.0 不允许出现在文本内容里的字符时模板路径拒写，点名键路径与字符位置且不产出半个字节
    /// </summary>
    /// <param name="codePoint">要夹进值里的字符码点</param>
    /// <param name="display">消息里该点名的码点文字</param>
    [Theory]
    [InlineData(0x0000, "U+0000")]
    [InlineData(0x0001, "U+0001")]
    [InlineData(0x0003, "U+0003")]
    [InlineData(0x0008, "U+0008")]
    [InlineData(0x000B, "U+000B")]
    [InlineData(0x000C, "U+000C")]
    [InlineData(0x000E, "U+000E")]
    [InlineData(0x001F, "U+001F")]
    public async Task xml非法字符在模板路径拒写且不产出半个字节(int codePoint, string display)
    {
        using var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(
                output,
                TemplateFactory.Build(("A1", "{{Company}}")),
                new { Company = $"a{(char)codePoint}b" },
                TestContext.Current.CancellationToken));

        Assert.Contains("Company", failure.Message, StringComparison.Ordinal);
        Assert.Contains(display, failure.Message, StringComparison.Ordinal);
        Assert.Contains("第 2 个字符处", failure.Message, StringComparison.Ordinal);
        Assert.Contains("XML 1.0", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 集合元素里的 XML 非法字符同样点名带下标的键路径
    /// </summary>
    [Fact]
    public async Task 集合元素里的xml非法字符点名带下标的键路径()
    {
        using var output = new MemoryStream();

        var data = new
        {
            Items = new[]
            {
                new { Name = "第一项", Qty = 1 },
                new { Name = "第二\u0001项", Qty = 2 }
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.BuildInvoiceWithFooterTemplate(), data, TestContext.Current.CancellationToken));

        Assert.Contains("Items[1].Name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("U+0001", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// XML 1.0 合法的控制字符在模板路径照写并逐字读回
    /// </summary>
    /// <param name="value">含合法控制字符的字串值</param>
    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("多行\n单元格\t内容")]
    public async Task xml合法的控制字符在模板路径照写并逐字读回(string value)
    {
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{Company}}")),
            new { Company = value },
            TestContext.Current.CancellationToken);

        Assert.True(output.Length > 0);
        Assert.Equal(value, Assert.IsType<string>(await ImportSingleValueAsync(output)));
    }

    /// <summary>
    /// 值中间的回车在模板路径照写，读回时按 XML 行尾处理归一成换行
    /// </summary>
    [Fact]
    public async Task 值中间的回车在模板路径照写并归一化成换行()
    {
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{Company}}")),
            new { Company = "a\rb" },
            TestContext.Current.CancellationToken);

        Assert.Equal("a\nb", Assert.IsType<string>(await ImportSingleValueAsync(output)));
    }

    /// <summary>
    /// 模板没有引用的键同样被走访：数据里带着一个越界值就渲染不了
    /// </summary>
    /// <remarks>
    /// 走访不解析模板，覆盖数据的全部顶层成员与集合元素成员。
    /// </remarks>
    [Fact]
    public async Task 模板没有引用的键同样被走访()
    {
        using var output = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(
                output,
                TemplateFactory.Build(("A1", "{{Company}}")),
                new { Company = "曦寒物流", Unused = double.NaN },
                TestContext.Current.CancellationToken));

        Assert.Contains("Unused", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 边界内的取值照渲染，并用导入器读回原值
    /// </summary>
    /// <remarks>
    /// 覆盖恰等的边界：32767 个字符的字串、日期下限当天、十五位有效数字、
    /// 以及量级刚好在双精度整数上限之下的 <c>9007199254740990L</c>。读回走 <see cref="ExcelDataReaderImporter"/>。
    /// </remarks>
    [Fact]
    public async Task 边界内的取值照渲染并读回原值()
    {
        Assert.Equal(
            new string('y', 32_767),
            Assert.IsType<string>(await RenderSingleAsync(new string('y', 32_767))));

        Assert.Equal(123456789012345d, Assert.IsType<double>(await RenderSingleAsync(123456789012345L)), 0);

        Assert.Equal(1.5d, Assert.IsType<double>(await RenderSingleAsync(1.5m)), 2);

        Assert.Equal(1.5d, Assert.IsType<double>(await RenderSingleAsync(1.5f)), 2);

        Assert.Equal(9007199254740990d, Assert.IsType<double>(await RenderSingleAsync(9007199254740990L)), 0);

        Assert.Equal(-9007199254740990d, Assert.IsType<double>(await RenderSingleAsync(-9007199254740990L)), 0);

        // 日期下限当天照渲染，日期落成文字格，只断言年份
        var earliest = Assert.IsType<string>(await RenderSingleAsync(new DateTime(1900, 1, 1)));
        Assert.Contains("1900", earliest, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="double"/> 的大值不受整数上限约束
    /// </summary>
    [Fact]
    public async Task double大值不受整数上限约束()
    {
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{V}}")),
            new { V = 1e300 },
            TestContext.Current.CancellationToken);

        // 只断言不被拒写，不断言文字形态
        Assert.False(string.IsNullOrEmpty(await ImportSingleValueAsync(output) as string));
    }

    /// <summary>
    /// 走访把集合枚举一遍，且只有一遍
    /// </summary>
    /// <remarks>
    /// 模板里没有集合占位，渲染库不枚举这个集合。
    /// </remarks>
    [Fact]
    public async Task 走访只把集合枚举一遍()
    {
        var items = new CountingItems(3);
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{Company}}")),
            new { Company = "曦寒物流", Items = items },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, items.GetEnumeratorCalls);
        Assert.Equal(3, items.Yielded);
        Assert.True(output.Length > 0);
    }

    /// <summary>
    /// 有集合占位时的枚举次数是「走访一遍 + 渲染库自己两遍」，走访没有多走
    /// </summary>
    /// <remarks>
    /// 渲染库对同一个集合取两次枚举器（展开集合行一次、写值一次）。
    /// </remarks>
    [Fact]
    public async Task 走访一遍之外渲染库自己枚举两遍()
    {
        var items = new CountingItems(3);
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{Items.Name}}"), ("B1", "{{Items.Qty}}")),
            new { Items = items },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, items.GetEnumeratorCalls);
        Assert.Equal(9, items.Yielded);

        var rows = await ImportRowsAsync(output);
        Assert.Equal(3, rows.Count);
        Assert.Equal("第0项", rows[0]["Col1"]);
        Assert.Equal(2d, Assert.IsType<double>(rows[2]["Col2"]), 0);
    }

    /// <summary>
    /// 取消令牌已取消时在走访之前停下，输出流零字节
    /// </summary>
    [Fact]
    public async Task 取消令牌已取消时不走访数据()
    {
        using var source = new CancellationTokenSource();

        source.Cancel();

        var items = new CountingItems(3);
        using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new MiniExcelTemplateRenderer()
            .RenderAsync(output, TemplateFactory.Build(("A1", "{{Items.Name}}")), new { Items = items }, source.Token));

        Assert.Equal(0, items.GetEnumeratorCalls);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// 渲染一个单值并交回读回的那一格，判定器是本框架的导入器
    /// </summary>
    /// <param name="value">要填进 <c>{{V}}</c> 的值</param>
    /// <returns>读回的取值</returns>
    private static async Task<object?> RenderSingleAsync(object value)
    {
        using var output = new MemoryStream();

        await new MiniExcelTemplateRenderer().RenderAsync(
            output,
            TemplateFactory.Build(("A1", "{{V}}")),
            new { V = value },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, CountFormulaElements(output));

        return await ImportSingleValueAsync(output);
    }

    /// <summary>
    /// 用本框架的导入器把渲染产物读回，交出第一格的取值
    /// </summary>
    /// <param name="output">渲染后的流，本方法把它回到起点，不关闭也不释放</param>
    private static async Task<object?> ImportSingleValueAsync(MemoryStream output)
    {
        var rows = await ImportRowsAsync(output);
        var row = Assert.Single(rows);

        Assert.True(row.ContainsKey("Col1"));

        return row["Col1"];
    }

    /// <summary>
    /// 用本框架的导入器把渲染产物整份读回，不解释表头
    /// </summary>
    /// <param name="output">渲染后的流，本方法把它回到起点，不关闭也不释放</param>
    /// <returns>逐行的「列键到取值」映射</returns>
    private static async Task<List<IReadOnlyDictionary<string, object?>>> ImportRowsAsync(MemoryStream output)
    {
        output.Position = 0;

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await foreach (var row in new ExcelDataReaderImporter().ReadAsync(
            output,
            new ExcelImportOptions { Format = ExcelImportFormat.Xlsx, HasHeader = false },
            TestContext.Current.CancellationToken))
        {
            rows.Add(row.Values);
        }

        return rows;
    }

    /// <summary>
    /// 数产物工作表里的公式元素个数，用来证明某一格是文字格而不是公式格
    /// </summary>
    /// <param name="output">渲染后的流，本方法不关闭也不释放它</param>
    /// <returns>公式元素的个数</returns>
    /// <remarks>
    /// 直接读档里的 XML，不经工作簿对象模型。
    /// </remarks>
    private static int CountFormulaElements(MemoryStream output)
    {
        var xml = ReadSheetXml(output);
        var count = 0;

        foreach (var token in new[] { "<x:f>", "<x:f ", "<x:f/", "<f>", "<f ", "<f/" })
        {
            var index = 0;

            while ((index = xml.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
        }

        return count;
    }

    /// <summary>
    /// 读出产物里第一张工作表的 XML 文本
    /// </summary>
    private static string ReadSheetXml(MemoryStream output)
    {
        output.Position = 0;

        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(candidate => candidate.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("渲染产物里没有工作表部件");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// 记录被枚举次数的集合
    /// </summary>
    private sealed class CountingItems : IEnumerable<ItemRow>
    {
        private readonly int _total;

        public CountingItems(int total)
        {
            _total = total;
        }

        /// <summary>
        /// 至今被取过几次枚举器，跨多次枚举累计
        /// </summary>
        public int GetEnumeratorCalls { get; private set; }

        /// <summary>
        /// 至今被交出的元素个数，跨多次枚举累计
        /// </summary>
        public int Yielded { get; private set; }

        public IEnumerator<ItemRow> GetEnumerator()
        {
            GetEnumeratorCalls++;

            for (var index = 0; index < _total; index++)
            {
                Yielded++;

                yield return new ItemRow { Name = $"第{index}项", Qty = index };
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// 集合元素型别，只有公开属性
    /// </summary>
    private sealed class ItemRow
    {
        public string Name { get; set; } = string.Empty;

        public int Qty { get; set; }
    }

    /// <summary>
    /// 只能顺序读、不可定位的流
    /// </summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
