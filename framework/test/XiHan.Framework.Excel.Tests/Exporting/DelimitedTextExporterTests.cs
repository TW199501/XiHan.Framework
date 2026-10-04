// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// 分隔符模式文字导出器测试
/// </summary>
public class DelimitedTextExporterTests
{
    /// <summary>
    /// 导出 csv 用预设逗号分隔与表头，并按指名的 Big5 编码写出
    /// </summary>
    /// <remarks>
    /// 表头用繁体（提單號／預計到達）：Big5（代码页 950）不收只在简体里出现的字符，<c>单</c>、<c>号</c>、<c>预</c>、
    /// <c>达</c> 编码不出时 .NET 的编码器以 <c>?</c> 顶替，字节层面无法原样往返。要在 Big5 档里保留简体字，
    /// 得改用 <c>gb2312</c>／<c>gb18030</c> 或 UTF-8。
    /// </remarks>
    [Fact]
    public async Task 导出csv_预设逗号表头与大五码()
    {
        var spec = BuildBig5Spec(new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) });
        var stream = new MemoryStream();
        var options = new ExcelTextOptions { EncodingName = "big5" };

        var result = await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(stream, spec, ExcelFormat.Csv, options, TestContext.Current.CancellationToken);

        Assert.Equal(".csv", result.FileExtension);
        Assert.Equal("text/csv", result.ContentType);

        var text = Encoding.GetEncoding("Big5").GetString(stream.ToArray());
        Assert.Equal("提單號,重量,預計到達" + "\r\n" + "AWB1,1.50,2026-01-02" + "\r\n", text);
        Assert.StartsWith("提單號", text);   // Big5 无 BOM
    }

    /// <summary>
    /// 导出 txt 用制表符作默认分隔符
    /// </summary>
    [Fact]
    public async Task 导出txt_预设制表符()
    {
        var spec = BuildSpec(new SampleRow { AwbNo = "AWB1" });
        var stream = new MemoryStream();

        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(stream, spec, ExcelFormat.Txt, new ExcelTextOptions(), TestContext.Current.CancellationToken);

        Assert.Contains("\t", BodyOf(stream));
    }

    /// <summary>
    /// 指定分隔符覆盖按格式取的默认值
    /// </summary>
    [Fact]
    public async Task 指定分隔符覆盖格式默认值()
    {
        var spec = BuildSpec(new SampleRow { AwbNo = "AWB1" });
        var stream = new MemoryStream();

        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(stream, spec, ExcelFormat.Csv, new ExcelTextOptions { Delimiter = ';' }, TestContext.Current.CancellationToken);

        Assert.Equal("提单号;重量;预计到达" + "\r\n" + "AWB1;0.00;0001-01-01" + "\r\n", BodyOf(stream));
    }

    /// <summary>
    /// UTF8 预设写 BOM，指名 utf-8 时不写 BOM
    /// </summary>
    /// <remarks>
    /// 测试名里的 <c>utf-8</c> 写成 <c>utf8</c>：C# 标识符不能含连字符，计划原文的 <c>指名utf-8不写BOM</c> 无法编译。
    /// </remarks>
    [Fact]
    public async Task UTF8预设写BOM_指名utf8不写BOM()
    {
        var bom = (await Export(new ExcelTextOptions { EncodingName = "utf-8-bom" })).ToArray();
        var plain = (await Export(new ExcelTextOptions { EncodingName = "utf-8" })).ToArray();

        Assert.Equal([0xEF, 0xBB, 0xBF], bom[..3]);
        Assert.NotEqual(0xEF, plain[0]);
    }

    /// <summary>
    /// 不含表头时只出行
    /// </summary>
    [Fact]
    public async Task 不含表头时只出行()
    {
        var text = await Export(new ExcelTextOptions { IncludeHeader = false });

        Assert.DoesNotContain("提单号", BodyOf(text));
        Assert.Equal("AWB1,0.00,0001-01-01" + "\r\n", BodyOf(text));
    }

    /// <summary>
    /// 值含换行在Minimal下往返相等
    /// </summary>
    [Fact]
    public async Task 值含换行在Minimal下往返相等()
    {
        var spec = BuildSpec(new SampleRow { AwbNo = "A\r\nB" });
        var text = await Export(new ExcelTextOptions(), spec: spec);

        Assert.Contains("\"A\r\nB\"", BodyOf(text));
    }

    /// <summary>
    /// None策略把值内换行转为空格并记一次Warning
    /// </summary>
    [Fact]
    public async Task None策略把值内换行转为空格并记一次Warning()
    {
        var sink = new FakeLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new SinkLoggerProvider(sink)));
        var exporter = new DelimitedTextExporter(factory.CreateLogger<DelimitedTextExporter>());
        var stream = new MemoryStream();

        await exporter.ExportAsync(stream, BuildSpec(new SampleRow { AwbNo = "A\nB" }), ExcelFormat.Csv,
            new ExcelTextOptions { Quote = ExcelTextQuote.None }, TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');
        Assert.Contains("A B", text);                       // 换行被替换，不是原样写出
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(1, sink.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    /// <summary>
    /// 固定宽度布局尚未实现时明确拒绝，且不写出任何字节
    /// </summary>
    /// <remarks>
    /// 本条测试钉住的是 Task 4 的占位分支：Task 5 实现固定宽度后，把它换成真实行为测试并删掉导出器里的抛出。
    /// </remarks>
    [Fact]
    public async Task 固定宽度布局在文字导出器显式拒绝()
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Txt,
            new ExcelTextOptions { Layout = ExcelTextLayout.FixedWidth }, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 文字导出器只认 csv 与 txt，拿到 xlsx 时点名拒绝
    /// </summary>
    [Fact]
    public async Task 文字导出器拒绝xlsx格式()
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 不传选项时按选项默认值写出
    /// </summary>
    [Fact]
    public async Task 不传选项时按默认值写出()
    {
        var stream = await Export(null);

        Assert.Equal([0xEF, 0xBB, 0xBF], stream.ToArray()[..3]);   // 默认 utf-8-bom
        Assert.Equal("提单号,重量,预计到达" + "\r\n" + "AWB1,0.00,0001-01-01" + "\r\n", BodyOf(stream));
    }

    /// <summary>
    /// 表头不做公式注入防护，只有数据值套防护前缀
    /// </summary>
    [Fact]
    public async Task 表头不做公式注入防护()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns =
            [
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.AwbNo),
                    Header = "=合计数",
                    Order = 0,
                    Value = row => row.AwbNo
                }
            ],
            Rows = new[] { new SampleRow { AwbNo = "=1+1" } }
        };

        Assert.Equal("=合计数" + "\r\n" + "'=1+1" + "\r\n", BodyOf(await Export(new ExcelTextOptions(), spec: spec)));
    }

    /// <summary>
    /// 关掉公式注入防护后原始值不被改写
    /// </summary>
    [Fact]
    public async Task 关闭公式注入防护时保留原始值()
    {
        var spec = BuildSpec(new SampleRow { AwbNo = "=1+1" });

        var text = BodyOf(await Export(new ExcelTextOptions { EscapeFormulaPrefix = false }, spec: spec));

        Assert.Contains("\r\n=1+1,", text);
        Assert.DoesNotContain("'=", text);
    }

    /// <summary>
    /// 表标题与工作簿排版项不写进文字档
    /// </summary>
    [Fact]
    public async Task 表标题不写进文字档()
    {
        var spec = BuildSpec("运单明细", new SampleRow { AwbNo = "AWB1" });

        Assert.Equal("提单号,重量,预计到达" + "\r\n" + "AWB1,0.00,0001-01-01" + "\r\n", BodyOf(await Export(new ExcelTextOptions(), spec: spec)));
    }

    /// <summary>
    /// 多行按列清单顺序逐行写出，行尾取选项设置
    /// </summary>
    [Fact]
    public async Task 多行逐行写出行尾可配()
    {
        var spec = BuildSpec(
            new SampleRow { AwbNo = "A1", Weight = 1m, Eta = new DateTime(2026, 1, 2) },
            new SampleRow { AwbNo = "A2", Weight = 2m, Eta = new DateTime(2026, 1, 3) });

        var text = BodyOf(await Export(new ExcelTextOptions { NewLine = "\n" }, spec: spec));

        Assert.Equal("提单号,重量,预计到达\nA1,1.00,2026-01-02\nA2,2.00,2026-01-03\n", text);
    }

    /// <summary>
    /// 行元素为空或字段值为空时仍写出完整列数，空值不吞掉分隔符
    /// </summary>
    [Fact]
    public async Task 空值与空行保持列数()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new SampleRow?[]
            {
                new() { AwbNo = "AWB1" },
                new() { AwbNo = string.Empty },
                null
            }
        };

        Assert.Equal(
            "提单号,重量,预计到达" + "\r\n" +
            "AWB1,0.00,0001-01-01" + "\r\n" +
            ",0.00,0001-01-01" + "\r\n" +
            ",," + "\r\n",
            BodyOf(await Export(new ExcelTextOptions(), spec: spec)));
    }

    /// <summary>
    /// 取消令牌已取消时一个字节都不写
    /// </summary>
    [Fact]
    public async Task 取消令牌已取消时不写出任何字节()
    {
        using var source = new CancellationTokenSource();

        source.Cancel();

        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Csv, new ExcelTextOptions(), source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 输出流与表规格为空时抛参数异常
    /// </summary>
    [Fact]
    public async Task 导出器拒绝空流与空规格()
    {
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter.ExportAsync(
            null!, BuildSpec(new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Csv, null, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter.ExportAsync(
            new MemoryStream(), null!, ExcelFormat.Csv, null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 构造导出器时日志器不能为空
    /// </summary>
    [Fact]
    public void 构造函数拒绝空logger()
        => Assert.Throws<ArgumentNullException>(() => new DelimitedTextExporter(null!));

    /// <summary>
    /// 用默认单行规格写出，返回结果流
    /// </summary>
    private static async Task<MemoryStream> Export(ExcelTextOptions? options, ExcelFormat format = ExcelFormat.Csv, ExcelSheetSpec? spec = null)
    {
        var stream = new MemoryStream();

        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(stream, spec ?? BuildSpec(new SampleRow { AwbNo = "AWB1" }), format, options, TestContext.Current.CancellationToken);

        return stream;
    }

    /// <summary>
    /// 取文字档正文，剥掉默认编码的 BOM
    /// </summary>
    private static string BodyOf(MemoryStream stream)
        => Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');

    /// <summary>
    /// 提单号、重量、预计到达三列
    /// </summary>
    /// <remarks>
    /// 重量与预计到达同时给出 <c>TextFormat</c> 与 Excel 的 <c>NumberFormat</c>：文字档只认前者，因此期望值是
    /// <c>1.50</c>／<c>2026-01-02</c> 而不是 <c>#,##0.00</c> 与 <c>yyyy-mm-dd</c> 的结果。
    /// </remarks>
    private static readonly ExcelColumn[] Columns =
    [
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.AwbNo),
            Header = "提单号",
            Order = 0,
            Value = row => row.AwbNo
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Weight),
            Header = "重量",
            Order = 1,
            TextFormat = "0.00",
            NumberFormat = "#,##0.00",
            Value = row => row.Weight
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Eta),
            Header = "预计到达",
            Order = 2,
            TextFormat = "yyyy-MM-dd",
            NumberFormat = "yyyy-mm-dd",
            Value = row => row.Eta
        }
    ];

    /// <summary>
    /// 构造三列规格
    /// </summary>
    private static ExcelSheetSpec BuildSpec(params SampleRow[] rows)
        => BuildSpec(null, rows);

    /// <summary>
    /// 构造 Big5 用例的三列规格，表头全部取 Big5 收得下的繁体字
    /// </summary>
    private static ExcelSheetSpec BuildBig5Spec(params SampleRow[] rows)
    {
        return new ExcelSheetSpec
        {
            SheetName = "運單",
            RowType = typeof(SampleRow),
            Columns =
            [
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.AwbNo),
                    Header = "提單號",
                    Order = 0,
                    Value = row => row.AwbNo
                },
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.Weight),
                    Header = "重量",
                    Order = 1,
                    TextFormat = "0.00",
                    NumberFormat = "#,##0.00",
                    Value = row => row.Weight
                },
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.Eta),
                    Header = "預計到達",
                    Order = 2,
                    TextFormat = "yyyy-MM-dd",
                    NumberFormat = "yyyy-mm-dd",
                    Value = row => row.Eta
                }
            ],
            Rows = rows
        };
    }

    /// <summary>
    /// 构造带表标题的三列规格
    /// </summary>
    private static ExcelSheetSpec BuildSpec(string? title, params SampleRow[] rows)
    {
        return new ExcelSheetSpec
        {
            SheetName = "运单",
            Title = title,
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = rows
        };
    }
}
