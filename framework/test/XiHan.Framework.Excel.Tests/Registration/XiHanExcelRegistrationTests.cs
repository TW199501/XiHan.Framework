// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Extensions.DependencyInjection;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Registration;

/// <summary>
/// Excel 服务注册测试：契约绑定、预设实作可被替换、重复注册幂等，以及配置面真的接进了实现
/// </summary>
/// <remarks>
/// <para>
/// 注册一律用 <c>TryAddSingleton</c>，因此「应用层先注册自己的实现」必须赢过后调的 <c>AddXiHanExcel</c>——
/// 换掉某一家 Excel 库正是应用层的正当选择，注册顺序不该把它覆盖掉。
/// </para>
/// <para>
/// 解析契约时都要先 <c>AddLogging</c>：文字与定宽两个实现构造函数收 <see cref="Microsoft.Extensions.Logging.ILogger{T}" />，
/// 本包不替宿主装日志器（装配日志由核心模块负责），这里按仓库既有惯例由用例显式补上。
/// </para>
/// </remarks>
public class XiHanExcelRegistrationTests
{
    /// <summary>
    /// 三个契约各自绑到本包的预设实作，且解析出的就是那一个类型
    /// </summary>
    [Fact]
    public void 注册全部契约与预设实作()
    {
        using var built = NewServices().AddXiHanExcel().BuildServiceProvider();

        Assert.IsType<ExcelExporter>(built.GetRequiredService<IExcelExporter>());
        Assert.IsType<ExcelImporter>(built.GetRequiredService<IExcelImporter>());
        Assert.IsType<MiniExcelTemplateRenderer>(built.GetRequiredService<IExcelTemplateRenderer>());
        Assert.NotNull(built.GetRequiredService<IOptions<XiHanExcelOptions>>().Value);
    }

    /// <summary>
    /// 裸选项与四个实现都单独可解析，分派器与导入门面拿得到它们
    /// </summary>
    /// <remarks>
    /// 裸 <see cref="XiHanExcelOptions"/> 这一条最容易漏：<c>AddOptions</c> 只交回 <c>IOptions&lt;T&gt;</c>，
    /// 而本包的 Provider 收的是选项对象本身。漏了它，解析 <see cref="IExcelExporter"/> 时才报一句
    /// 「无法解析 XiHanExcelOptions」，看不出错在注册表少一行。
    /// </remarks>
    [Fact]
    public void 裸选项与各个实现都单独可解析()
    {
        using var built = NewServices().AddXiHanExcel().BuildServiceProvider();

        var options = built.GetRequiredService<XiHanExcelOptions>();
        var exporter = built.GetRequiredService<IExcelExporter>();

        Assert.NotNull(built.GetRequiredService<ClosedXmlExporter>());
        Assert.NotNull(built.GetRequiredService<MiniExcelStreamExporter>());
        Assert.NotNull(built.GetRequiredService<DelimitedTextExporter>());
        Assert.NotNull(built.GetRequiredService<ExcelDataReaderImporter>());
        Assert.NotNull(built.GetRequiredService<FixedWidthTextImporter>());
        Assert.Same(options, built.GetRequiredService<IOptions<XiHanExcelOptions>>().Value);

        // 门面拿到的两个读实现就是容器里的那两个单例，不在门面里另造
        var importer = Assert.IsType<ExcelImporter>(built.GetRequiredService<IExcelImporter>());
        Assert.NotNull(importer);
        Assert.Same(built.GetRequiredService<IExcelExporter>(), exporter);
    }

    /// <summary>
    /// 应用层先注册的实现不被覆盖，三个契约都换得掉
    /// </summary>
    [Fact]
    public void 可被应用层替换预设实作()
    {
        var services = NewServices();
        var exporter = new FakeExporter();
        var importer = new FakeImporter();
        var renderer = new FakeTemplateRenderer();

        services.AddSingleton<IExcelExporter>(exporter);
        services.AddSingleton<IExcelImporter>(importer);
        services.AddSingleton<IExcelTemplateRenderer>(renderer);
        services.AddXiHanExcel();

        using var built = services.BuildServiceProvider();

        Assert.Same(exporter, built.GetRequiredService<IExcelExporter>());
        Assert.Same(importer, built.GetRequiredService<IExcelImporter>());
        Assert.Same(renderer, built.GetRequiredService<IExcelTemplateRenderer>());
    }

    /// <summary>
    /// 重复调用不产生重复注册，每个契约与实现都只剩一条描述符
    /// </summary>
    [Fact]
    public void 重复调用不产生重复注册()
    {
        var services = new ServiceCollection().AddXiHanExcel().AddXiHanExcel();

        Type[] registered =
        [
            typeof(IExcelExporter),
            typeof(IExcelImporter),
            typeof(IExcelTemplateRenderer),
            typeof(ClosedXmlExporter),
            typeof(MiniExcelStreamExporter),
            typeof(DelimitedTextExporter),
            typeof(ExcelDataReaderImporter),
            typeof(FixedWidthTextImporter),
            typeof(XiHanExcelOptions)
        ];

        foreach (var type in registered)
        {
            Assert.Single(services, descriptor => descriptor.ServiceType == type);
        }
    }

    /// <summary>
    /// 配置的行数上限经注册接进两个读实现，导入真正按配置截断
    /// </summary>
    /// <remarks>
    /// 上限判据共用一份不代表配置会自动生效：门面转给谁、谁拿到多少行，取决于注册时有没有把选项交给实现。
    /// 这条走完整的注册路径，因此它同时验到「裸选项来自配置」与「两个读实现都收到同一份」。
    /// </remarks>
    /// <param name="fixedColumns">是否走固定宽度路径</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 配置的行数上限经注册生效(bool fixedColumns)
    {
        using var built = NewServices()
            .AddXiHanExcel()
            .Configure<XiHanExcelOptions>(options => options.MaxImportRows = 2)
            .BuildServiceProvider();

        var importer = built.GetRequiredService<IExcelImporter>();
        using var input = fixedColumns
            ? ImportFixtures.Text("AW\r\nB\r\nC\r\n")
            : ImportFixtures.Csv("提单号", 5);

        var options = fixedColumns
            ? new ExcelImportOptions
            {
                Format = ExcelImportFormat.Txt,
                FixedColumns = [new ExcelFixedWidthField("码", 2)]
            }
            : new ExcelImportOptions { Format = ExcelImportFormat.Csv };

        var rows = await AsyncCollector.CollectAsync(
            importer.ReadAsync(input, options, TestContext.Current.CancellationToken));

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// 不配上限时注册路径仍按框架默认硬上限工作
    /// </summary>
    [Fact]
    public async Task 不配上限时注册路径按框架默认上限工作()
    {
        using var built = NewServices().AddXiHanExcel().BuildServiceProvider();

        var reader = built.GetRequiredService<ExcelDataReaderImporter>();
        using var input = ImportFixtures.Csv("提单号", 3);

        var rows = await AsyncCollector.CollectAsync(reader.ReadAsync(
            input,
            new ExcelImportOptions { Format = ExcelImportFormat.Csv, MaxRowCount = ExcelConstants.DefaultMaxImportRows },
            TestContext.Current.CancellationToken));

        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// 配置的上限高于框架硬上限时，解析读实现当场抛而不是静默按上限工作
    /// </summary>
    [Fact]
    public void 配置的上限高于框架硬上限时解析即抛()
    {
        using var built = NewServices()
            .AddXiHanExcel()
            .Configure<XiHanExcelOptions>(options => options.MaxImportRows = ExcelConstants.DefaultMaxImportRows + 1)
            .BuildServiceProvider();

        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => built.GetRequiredService<IExcelImporter>());

        Assert.Equal(nameof(XiHanExcelOptions.MaxImportRows), failure.ParamName);
    }

    /// <summary>
    /// 从注册路径拿到的导出器能真写出三种档，且分派按配置里的阈值走
    /// </summary>
    /// <remarks>
    /// 这一条把注册与分派连起来验：只断「解析出某个类型」会在实现里把 Provider 接错线时仍然全绿，
    /// 而档能不能读回、超阈值时是不是真降级，才是使用者会碰到的结果。
    /// </remarks>
    [Fact]
    public async Task 注册路径的导出器可写三种档()
    {
        using var built = NewServices()
            .AddXiHanExcel()
            .Configure<XiHanExcelOptions>(options => options.StreamingThreshold = 1)
            .BuildServiceProvider();

        var exporter = built.GetRequiredService<IExcelExporter>();

        var csv = new MemoryStream();
        var csvResult = await exporter.ExportAsync(csv, Spec(2), ExcelFormat.Csv, null, TestContext.Current.CancellationToken);
        Assert.Equal(ExcelConstants.ExtensionCsv, csvResult.FileExtension);

        var styled = new MemoryStream();
        var styledResult = await exporter.ExportAsync(styled, Spec(2, forceStreaming: false), ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken);
        Assert.True(styledResult.StylingApplied);

        var streamed = new MemoryStream();
        var streamedResult = await exporter.ExportAsync(streamed, Spec(2), ExcelFormat.Xlsx, null, TestContext.Current.CancellationToken);
        Assert.False(streamedResult.StylingApplied);
        Assert.NotNull(streamedResult.StylingSkipReason);

        foreach (var stream in new[] { styled, streamed })
        {
            stream.Position = 0;

            using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
            Assert.Equal("运单", workbook.Worksheet(1).Name);
        }
    }

    /// <summary>
    /// 本包不替宿主装日志器：没装时收日志器的实现解析不出，装了才可解析
    /// </summary>
    /// <remarks>
    /// 装配日志是核心模块与宿主的事，本包只声明依赖。这条把分工钉住，免得日后有人为了「单独调用也能跑」
    /// 在 <c>AddXiHanExcel</c> 里偷偷补一份日志注册，把宿主装的日志器盖掉。
    /// </remarks>
    [Fact]
    public void 未装日志器时收日志器的实现解析不出()
    {
        using var withoutLogging = new ServiceCollection().AddXiHanExcel().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => withoutLogging.GetRequiredService<DelimitedTextExporter>());
        Assert.Throws<InvalidOperationException>(() => withoutLogging.GetRequiredService<IExcelImporter>());

        using var withLogging = NewServices().AddXiHanExcel().BuildServiceProvider();

        Assert.NotNull(withLogging.GetRequiredService<DelimitedTextExporter>());
        Assert.NotNull(withLogging.GetRequiredService<IExcelImporter>());
    }

    /// <summary>
    /// 用例起手的服务集合：只补日志器，其余交给 <c>AddXiHanExcel</c>
    /// </summary>
    private static IServiceCollection NewServices() => new ServiceCollection().AddLogging();

    /// <summary>
    /// 构造一张两行的表规格
    /// </summary>
    private static ExcelSheetSpec Spec(int count, bool? forceStreaming = null)
    {
        var rows = new SampleRow[count];

        for (var index = 0; index < count; index++)
        {
            rows[index] = new SampleRow
            {
                AwbNo = $"AWB{index}",
                Weight = index + 0.5m,
                Eta = new DateTime(2026, 1, 2)
            };
        }

        return new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns =
            [
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.AwbNo),
                    Header = "提单号",
                    Value = row => row.AwbNo
                }
            ],
            Rows = rows,
            ExpectedRowCount = count,
            ForceStreaming = forceStreaming
        };
    }

    /// <summary>
    /// 应用层自己的导出实现，用来证明预设实作可被替换
    /// </summary>
    private sealed class FakeExporter : IExcelExporter
    {
        public Task<ExcelExportResult> ExportAsync(
            Stream output,
            ExcelSheetSpec sheet,
            ExcelFormat format,
            ExcelTextOptions? textOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExcelExportResult> ExportAllAsync(
            Stream output,
            IReadOnlyList<ExcelSheetSpec> sheets,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 应用层自己的导入实现
    /// </summary>
    private sealed class FakeImporter : IExcelImporter
    {
        public IAsyncEnumerable<ExcelImportRow> ReadAsync(
            Stream input,
            ExcelImportOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 应用层自己的模板渲染实现
    /// </summary>
    private sealed class FakeTemplateRenderer : IExcelTemplateRenderer
    {
        public Task RenderAsync(
            Stream output,
            Stream template,
            object data,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
