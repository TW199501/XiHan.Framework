// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using ClosedXML.Excel;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// MiniExcel 固定版式模板渲染测试
/// </summary>
/// <remarks>
/// <para>
/// 模板由 <see cref="TemplateFactory" /> 当场生成写进内存流，仓库里不留二进制模板档。
/// 断言一律 ClosedXML 回读渲染产物，格位按实测的展开方式写（取证见
/// <c>.superpowers/sdd/2026-10-04-excel/t7-probe-miniexcel-behavior.txt</c>）：集合占位从它所在那一行原地起写，
/// 第一项落在占位行本身，模板下方的静态行被整体下移。
/// </para>
/// <para>
/// 每条测试各自建模板流：实测 MiniExcel 渲染后会关掉传入的模板流，复用会让下一条用例拿到已关闭的流。
/// </para>
/// </remarks>
public class MiniExcelTemplateRendererTests
{
    /// <summary>
    /// 单值占位与集合占位都被数据替换，格位按实测的原地展开
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
    /// <remarks>
    /// 这是库的默认口径（<c>IgnoreTemplateParameterMissing</c> 默认为真），本组件不透出该配置，
    /// 因此把它钉成契约并写进接口文档：缺键交回空格，不报错也不猜值。
    /// </remarks>
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
    /// <remarks>
    /// 实测（<c>t7-probe-miniexcel-behavior.txt</c> 的 case18）日期占位渲染出来是 <c>Text</c> 格，
    /// 与 ClosedXML 导出路径的日期格不同——固定版式模板要拿它做日期运算的调用方必须自己转。
    /// </remarks>
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
    /// <remarks>
    /// 实测 MiniExcel 对 null 模板与 null 数据直接交回 <c>NullReferenceException</c>（case9a/case9c），
    /// 那是不能被当成契约的失败面，所以在入口自己判。
    /// </remarks>
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
    /// 模板流不可读时抛 ArgumentException，不走库的失败面
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
    /// 模板流不可定位时抛 ArgumentException：实测库要求模板可 seek，否则自己抛英文异常
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
    /// 输出流的所有权在调用方，本方法不能把它关掉，能主张的只有两件事：抛出 <see cref="OperationCanceledException"/>，
    /// 以及绝不正常返回。夹具在第一次被写入时就把令牌取消，正好落在「库已经动手写、还没写完」的那一段；
    /// 而渲染完成后、回传之前这一窗口的取消由本类在返回前补查一次拦住，否则调用方会拿到一份没人宣告完成的档。
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
    /// 夹具把取消压到最后一次 flush，此时渲染库已经写完、自己不会再查令牌；只有本方法在返回前补查一次，
    /// 这个窗口才不会交出成功。这与 <see cref="ClosedXmlExporter"/> 和 <see cref="DelimitedTextExporter"/>
    /// 的回传前检查是同一条口径。
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
    /// 模板不是 xlsx 容器时库的容器异常原样透传，且实测此时输出流没有半个字节
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
    /// <remarks>
    /// 这条把实测到的库行为钉成契约（<c>t7-probe-miniexcel-behavior.txt</c> 的 case1 与 case12），
    /// 提醒门面与调用方：组件不接管、也不归还模板流的所有权。
    /// </remarks>
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
    /// 渲染器实现抽象契约，供门面按接口分派
    /// </summary>
    [Fact]
    public void 渲染器实现模板渲染契约()
    {
        Assert.IsAssignableFrom<IExcelTemplateRenderer>(new MiniExcelTemplateRenderer());
    }

    /// <summary>
    /// 只能顺序读、不可定位的流，用来证明入口对模板流的可 seek 要求
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
