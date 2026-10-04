// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// 分派器测试：按格式与流式表态路由、不猜的缺省、以及「门面不绕过也不重写任何一道既有守卫」
/// </summary>
/// <remarks>
/// <para>
/// 三个 Provider 都用真身，不用替身：分派器唯一该被证明的是「把这份规格送到哪条路径」，而各条路径的守卫
/// （行型、列宽、底色、表名、取值域、分隔符）都长在 Provider 里。用替身就只剩「调了哪个方法」，
/// 门面偷偷吞掉或改写异常时测不出来。
/// </para>
/// <para>
/// 抛出后一律只看异常型别与消息，不看流的内容是否为零——零字节只在入口之前的失败上成立。
/// </para>
/// </remarks>
public class ExcelExporterDispatchTests
{
    /// <summary>
    /// 流式阈值的测试用值，配合行数把两条 xlsx 路径分开
    /// </summary>
    private const int Threshold = 10;

    /// <summary>
    /// 提单号与重量两列，重量带逐格条件样式，用来区分「样式落地」与「样式被降级」
    /// </summary>
    private static readonly ExcelColumn[] Columns =
    [
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.AwbNo),
            Header = "提单号",
            Value = row => row.AwbNo
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Weight),
            Header = "重量",
            NumberFormat = "0.00",
            CellStyle = row => row is SampleRow typed && typed.Weight > 10m
                ? new ExcelTextStyle("#C00000", null, true)
                : null,
            Value = row => row.Weight
        }
    ];

    /// <summary>
    /// <c>.csv</c> 与 <c>.txt</c> 一律路由到文字导出器，扩展名与内容类型跟着格式走
    /// </summary>
    [Theory]
    [InlineData(ExcelFormat.Csv, ExcelConstants.ExtensionCsv, ExcelConstants.CsvContentType)]
    [InlineData(ExcelFormat.Txt, ExcelConstants.ExtensionTxt, ExcelConstants.PlainTextContentType)]
    public async Task 文字格式路由到文字导出器(ExcelFormat format, string extension, string contentType)
    {
        var stream = new MemoryStream();

        var result = await NewExporter().ExportAsync(
            stream, BuildSpec(Rows(2)), format, null, TestContext.Current.CancellationToken);

        Assert.Equal(format, result.Format);
        Assert.Equal(extension, result.FileExtension);
        Assert.Equal(contentType, result.ContentType);
        Assert.True(result.StylingApplied);

        stream.Position = 0;
        var text = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
        Assert.Contains("提单号", text, StringComparison.Ordinal);
        Assert.Contains("AWB0", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 文字档的两种布局由选项决定，分派器把选项原样递下去
    /// </summary>
    [Fact]
    public async Task 固定宽度布局经分派器照样生效()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "单号",
                FixedWidth = 16,
                Value = row => row.AwbNo
            }
        };

        var stream = new MemoryStream();

        var result = await NewExporter().ExportAsync(
            stream,
            new ExcelSheetSpec { SheetName = "运单", RowType = typeof(SampleRow), Columns = columns, Rows = Rows(2) },
            ExcelFormat.Txt,
            new ExcelTextOptions { Layout = ExcelTextLayout.FixedWidth, EncodingName = "utf-8" },
            TestContext.Current.CancellationToken);

        Assert.Equal(ExcelConstants.ExtensionTxt, result.FileExtension);

        stream.Position = 0;
        var lines = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd()
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal(16, System.Text.Encoding.UTF8.GetByteCount(lines[0]));
        Assert.All(lines, line => Assert.Equal(16, System.Text.Encoding.UTF8.GetByteCount(line)));
    }

    /// <summary>
    /// xlsx 在流式与否都没表态时抛出而不是猜一个，行集合一次都不被枚举
    /// </summary>
    [Fact]
    public async Task xlsx未表态流式与否时抛不猜()
    {
        var rows = new CountingRows(2);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await NewExporter().ExportAsync(
            stream, BuildSpec(rows), ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ExcelSheetSpec.ExpectedRowCount), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ExcelSheetSpec.ForceStreaming), failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, rows.Count);
    }

    /// <summary>
    /// 达到阈值的预期行数走流式，样式缺失由返回值的理由说清楚
    /// </summary>
    [Fact]
    public async Task 预期行数达到阈值时走流式且降级带理由()
    {
        var stream = new MemoryStream();

        var result = await NewExporter().ExportAsync(
            stream,
            BuildSpec(Rows(2), expectedRowCount: Threshold),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
        Assert.Contains("条件样式", result.StylingSkipReason, StringComparison.Ordinal);

        using var workbook = Open(stream);
        Assert.Equal("提单号", workbook.Worksheet(1).Cell(1, 1).GetString());
    }

    /// <summary>
    /// 低于阈值的预期行数走全量，样式落地且不交降级理由
    /// </summary>
    [Fact]
    public async Task 预期行数低于阈值时走全量()
    {
        var stream = new MemoryStream();

        var result = await NewExporter().ExportAsync(
            stream,
            BuildSpec(Rows(2), expectedRowCount: Threshold - 1),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.StylingApplied);
        Assert.Null(result.StylingSkipReason);

        using var workbook = Open(stream);

        // 样式确实落地：表头底色是调用方给的那份，不是库的预设
        Assert.Equal(XLFillPatternValues.Solid, workbook.Worksheet(1).Cell(1, 2).Style.Fill.PatternType);
    }

    /// <summary>
    /// 流式阈值为 0 或负数时一律走流式，不当非法取值拒掉
    /// </summary>
    /// <remarks>
    /// 选项文档与分派器都承诺「0 或负数等于任何非负行数都达到阈值」，那是「一行都不想全量」的合法表达。
    /// 这条把承诺钉住：行数很小（低于默认阈值的量级）也照样降级，走的仍是流式路径的显式降级出口。
    /// 判据用返回值而不是内存：达到阈值与走的实现是同一件事的两端，样式没落地就是那条路径。
    /// </remarks>
    /// <param name="threshold">要试的流式阈值</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task 阈值非正时一律走流式(int threshold)
    {
        var exporter = NewExporter(new XiHanExcelOptions
        {
            StreamingThreshold = threshold
        });

        var result = await exporter.ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(2), expectedRowCount: 1),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
        Assert.NotNull(result.StylingSkipReason);
    }

    /// <summary>
    /// 显式要求全量时不因超过阈值而改判，显式表态优先于阈值
    /// </summary>
    [Fact]
    public async Task 显式要全量时不因超阈值而降级()
    {
        var result = await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(2), expectedRowCount: Threshold * 5, forceStreaming: false),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.StylingApplied);
    }

    /// <summary>
    /// 显式要求流式时即使预期行数远低于阈值也走流式
    /// </summary>
    [Fact]
    public async Task 显式要流式时不看阈值()
    {
        var result = await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(2), expectedRowCount: 1, forceStreaming: true),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
    }

    /// <summary>
    /// 显式要求流式时不必给预期行数
    /// </summary>
    [Fact]
    public async Task 显式要流式时不给预期行数也放行()
    {
        var result = await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1), forceStreaming: true),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
    }

    /// <summary>
    /// 负的预期行数被拒，且不因为没表态而先撞上「不猜」那道抛
    /// </summary>
    [Fact]
    public async Task 负预期行数被拒()
    {
        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1), expectedRowCount: -1),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.ExpectedRowCount), failure.ParamName);
    }

    /// <summary>
    /// 负的预期行数在文字档路径同样被拒：它是分派输入，不因目标格式用不到阈值就放过
    /// </summary>
    /// <remarks>
    /// 与上一条同一条判据，只是换目标格式。分派器把这项检查排在路由之前，所以 <c>Csv</c>／<c>Txt</c>
    /// 不会因为「这两档不看行数」而交回一份写好了的档——契约里 <c>ExpectedRowCount</c> 只有「未知」与
    /// 非负两种取值，负数在哪一档都是错的声明。
    /// </remarks>
    /// <param name="format">要试的文字格式</param>
    [Theory]
    [InlineData(ExcelFormat.Csv)]
    [InlineData(ExcelFormat.Txt)]
    public async Task 负预期行数在文字档路径也被拒(ExcelFormat format)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NewExporter().ExportAsync(
            stream,
            BuildSpec(Rows(1), expectedRowCount: -1),
            format,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.ExpectedRowCount), failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 多表恒走全量，遇到要求流式的那张表直接拒绝而不是偷偷改成全量
    /// </summary>
    [Fact]
    public async Task 多表遇到要求流式时拒绝()
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await NewExporter().ExportAllAsync(
            stream,
            [BuildSpec(Rows(1)), BuildSpec(Rows(1), forceStreaming: true)],
            TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ExcelSheetSpec.ForceStreaming), failure.Message, StringComparison.Ordinal);
        Assert.Contains("第 2 张表", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 多表写出两张表，两张都保留样式
    /// </summary>
    [Fact]
    public async Task 多表走全量并可回读两张()
    {
        var stream = new MemoryStream();

        var result = await NewExporter().ExportAllAsync(
            stream,
            [BuildSpec(Rows(1), sheetName: "第一表"), BuildSpec(Rows(1), sheetName: "第二表")],
            TestContext.Current.CancellationToken);

        Assert.True(result.StylingApplied);

        using var workbook = Open(stream);
        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Equal("第一表", workbook.Worksheet(1).Name);
        Assert.Equal("第二表", workbook.Worksheet(2).Name);
    }

    /// <summary>
    /// 一列都没有的规格在路由之前被拒，不交出一份每行空白或只有个空表的「成功档」
    /// </summary>
    /// <remarks>
    /// 三条路径对零列各自的现实都不一样（文字档写成每行一个空行、全量写一张空表、流式连表头都不写），
    /// 放行就是让同一份坏输入交出三种不同的半成品。分派器是唯一能一次管住全部路径的位置，因此这道判定归它，
    /// 各 Provider 不重复判。
    /// </remarks>
    /// <param name="format">要试的目标格式</param>
    [Theory]
    [InlineData(ExcelFormat.Csv)]
    [InlineData(ExcelFormat.Txt)]
    [InlineData(ExcelFormat.Xlsx)]
    public async Task 零列表规格在路由前被拒(ExcelFormat format)
    {
        var rows = new CountingRows(2);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await NewExporter().ExportAsync(
            stream,
            BuildSpec(rows, columns: [], forceStreaming: true),
            format,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.Columns), failure.ParamName);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, rows.Count);
    }

    /// <summary>
    /// 多表清单里某张表零列同样在路由前被拒，并点名是第几张
    /// </summary>
    [Fact]
    public async Task 多表里零列的那张被拒并点名位置()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await NewExporter().ExportAllAsync(
            new MemoryStream(),
            [BuildSpec(Rows(1)), BuildSpec(Rows(1), columns: [])],
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.Columns), failure.ParamName);
        Assert.Contains("第 2 张表", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列宽守卫长在 Provider 里，分派器不抄第二份、也不改写它交回的异常
    /// </summary>
    [Fact]
    public async Task 非法列宽按全量路径的原样抛出()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Width = 300,
                Value = row => row.AwbNo
            }
        };

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1), columns: columns, forceStreaming: false),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Width), failure.ParamName);
        Assert.Contains("255", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表头底色守卫交回的框架异常原样上抛，paramName 不被分派器改掉
    /// </summary>
    [Fact]
    public async Task 非法底色按全量路径的原样抛出()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1), forceStreaming: false, headerFill: "#GGG"),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.HeaderFill), failure.ParamName);
    }

    /// <summary>
    /// 两条 xlsx 路径对同一份坏表名交出同一条判据，分派器不会让走哪条变成「换一套规矩」
    /// </summary>
    /// <param name="forceStreaming">要试的路径：<c>true</c> 走流式，<c>false</c> 走全量</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 两条xlsx路径对坏表名同判据(bool forceStreaming)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1), sheetName: "a[b", forceStreaming: forceStreaming),
            ExcelFormat.Xlsx,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.SheetName), failure.ParamName);
        Assert.Contains("表名", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 异型行在两条 xlsx 路径都由共用守卫拦下，分派器不吞也不改写
    /// </summary>
    /// <param name="forceStreaming">要试的路径</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 异型行在两条xlsx路径都被拒(bool forceStreaming)
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { "不是行类型" },
            ForceStreaming = forceStreaming
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await NewExporter()
            .ExportAsync(new MemoryStream(), spec, ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken));

        Assert.Contains("System.String", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 文字导出器的分隔符禁令原样上抛，分派器不在自己这一层重判一遍
    /// </summary>
    [Fact]
    public async Task 坏分隔符按文字导出器的原样抛出()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1)),
            ExcelFormat.Csv,
            new ExcelTextOptions { Delimiter = '"' },
            TestContext.Current.CancellationToken));

        Assert.Equal("textOptions", failure.ParamName);
    }

    /// <summary>
    /// xlsx 不解释文字档选项：给了也不报错，但产出的仍是工作簿
    /// </summary>
    /// <remarks>
    /// 沿用本组件既有口径（设置了但不生效的选项一律不抛，也不静默改道）。判据写在返回值上：
    /// 扩展名与内容类型都还是工作簿那一份，选项没把格式拽成文字档。
    /// </remarks>
    [Fact]
    public async Task xlsx不解释文字档选项()
    {
        var result = await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1), forceStreaming: false),
            ExcelFormat.Xlsx,
            new ExcelTextOptions { Delimiter = ';', EncodingName = "big5" },
            TestContext.Current.CancellationToken);

        Assert.Equal(ExcelConstants.ExtensionXlsx, result.FileExtension);
        Assert.Equal(ExcelConstants.XlsxContentType, result.ContentType);
    }

    /// <summary>
    /// 未知格式取值被拒，不落到任何一条路径上
    /// </summary>
    [Fact]
    public async Task 未定义的格式取值被拒()
    {
        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(Rows(1)),
            (ExcelFormat)99,
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal("format", failure.ParamName);
    }

    /// <summary>
    /// 入参为 <c>null</c> 时在路由之前抛，不把工作簿那一句英文异常交出来
    /// </summary>
    [Fact]
    public async Task 空入参被拒()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await NewExporter().ExportAsync(
            null!, BuildSpec(Rows(1)), ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await NewExporter().ExportAsync(
            new MemoryStream(), null!, ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await NewExporter().ExportAllAsync(
            new MemoryStream(), null!, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 构造分派器时缺任一个 Provider 或选项即抛，不留下半套路由
    /// </summary>
    [Fact]
    public void 构造入参为null即抛()
    {
        var closed = new ClosedXmlExporter(new XiHanExcelOptions());
        var stream = new MiniExcelStreamExporter();
        var text = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);
        var options = new XiHanExcelOptions();

        Assert.Throws<ArgumentNullException>(() => new ExcelExporter(null!, stream, text, options));
        Assert.Throws<ArgumentNullException>(() => new ExcelExporter(closed, null!, text, options));
        Assert.Throws<ArgumentNullException>(() => new ExcelExporter(closed, stream, null!, options));
        Assert.Throws<ArgumentNullException>(() => new ExcelExporter(closed, stream, text, null!));
    }

    /// <summary>
    /// 分派器不动调用方留下的流位置，也不替它复位
    /// </summary>
    [Fact]
    public async Task 不接管调用方的流位置()
    {
        var stream = new MemoryStream();
        stream.WriteByte(0xAA);

        await NewExporter().ExportAsync(
            stream, BuildSpec(Rows(1), forceStreaming: false), ExcelFormat.Csv, null, TestContext.Current.CancellationToken);

        Assert.True(stream.Position > 1);
        Assert.Equal(0xAA, stream.ToArray()[0]);
    }

    /// <summary>
    /// 造一个用默认阈值的分派器
    /// </summary>
    private static ExcelExporter NewExporter() => NewExporter(new XiHanExcelOptions { StreamingThreshold = Threshold });

    /// <summary>
    /// 用真身 Provider 造分派器，避免用替身把「路径里的守卫」这条线测空
    /// </summary>
    private static ExcelExporter NewExporter(XiHanExcelOptions options) => new(
        new ClosedXmlExporter(options),
        new MiniExcelStreamExporter(),
        new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance),
        options);

    /// <summary>
    /// 造若干测试行
    /// </summary>
    private static SampleRow[] Rows(int count)
    {
        var rows = new SampleRow[count];

        for (var index = 0; index < count; index++)
        {
            rows[index] = new SampleRow
            {
                AwbNo = $"AWB{index}",
                Weight = index + 0.5m,
                Eta = new DateTime(2026, 1, 2).AddDays(index)
            };
        }

        return rows;
    }

    /// <summary>
    /// 构造表规格，流式相关的两个入参按用例给定
    /// </summary>
    private static ExcelSheetSpec BuildSpec(
        IEnumerable<SampleRow> rows,
        string sheetName = "运单",
        ExcelColumn[]? columns = null,
        int? expectedRowCount = null,
        bool? forceStreaming = null,
        string? headerFill = "#D9E1F2") => new()
    {
        SheetName = sheetName,
        RowType = typeof(SampleRow),
        Columns = columns ?? Columns,
        Rows = rows,
        ExpectedRowCount = expectedRowCount,
        ForceStreaming = forceStreaming,
        HeaderFill = headerFill
    };

    /// <summary>
    /// 把导出后的流回到起点并交回可读的工作簿
    /// </summary>
    private static XLWorkbook Open(MemoryStream stream)
    {
        stream.Position = 0;
        return new XLWorkbook(stream);
    }
}
