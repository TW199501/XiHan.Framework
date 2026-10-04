// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using ClosedXML.Excel;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// MiniExcel 流式导出测试：表头与列顺序、可承载的排版项、显式降级与取消时机
/// </summary>
/// <remarks>
/// <para>
/// 断言一律走 <c>new XLWorkbook(stream)</c> 回读，检查档里真有什么。流式模式能写出什么不由配置对象上有没有
/// 那个属性决定，而由落进档的结果决定：列宽、底色这类设置在本路径不生效，也照这个样子钉住，
/// 免得日后被误读成「配置传进去了就一定生效」。
/// </para>
/// <para>
/// 行集合一律用只许枚举一次的夹具。本路径的价值就在于不物化结果集，二次枚举或先转成列表都会让十万行档
/// 变成十万行内存，这条契约钉在 CI 里，而不是靠读代码担保。
/// </para>
/// </remarks>
public class MiniExcelStreamExporterTests
{
    /// <summary>
    /// 预计到达、提单号、重量三列：声明顺序与列上的 <see cref="ExcelColumn.Order"/> 都不同于写出顺序，
    /// 用来证明落位取的是表规格给出的顺序
    /// </summary>
    private static readonly ExcelColumn[] Columns =
    [
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Eta),
            Header = "预计到达",
            Order = 2,
            NumberFormat = "yyyy-mm-dd",
            Value = row => row.Eta
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.AwbNo),
            Header = "提单号",
            Order = 0,
            Width = 30,
            Value = row => row.AwbNo
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Weight),
            Header = "重量",
            Order = 1,
            NumberFormat = "0.00",
            Value = row => row.Weight
        }
    ];

    /// <summary>
    /// 写出可回读的档，且列的落位就是 <see cref="ExcelSheetSpec.Columns"/> 给出的顺序
    /// </summary>
    [Fact]
    public async Task 流式写出可回读且逐列顺序正确()
    {
        var stream = new MemoryStream();

        var result = await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildSpec(Rows(2)), TestContext.Current.CancellationToken);

        Assert.Equal(ExcelFormat.Xlsx, result.Format);
        Assert.Equal(ExcelConstants.ExtensionXlsx, result.FileExtension);
        Assert.Equal(ExcelConstants.XlsxContentType, result.ContentType);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("运单", sheet.Name);
        Assert.Equal("预计到达", sheet.Cell(1, 1).GetString());
        Assert.Equal("提单号", sheet.Cell(1, 2).GetString());
        Assert.Equal("重量", sheet.Cell(1, 3).GetString());
        Assert.Equal("AWB0", sheet.Cell(2, 2).GetString());
        Assert.Equal("AWB1", sheet.Cell(3, 2).GetString());
        Assert.Equal(0.5d, sheet.Cell(2, 3).Value.GetNumber());
    }

    /// <summary>
    /// 列上的数字与日期格式串落进单元格，这是流式模式确实保留下来的排版项
    /// </summary>
    [Fact]
    public async Task 流式落数字格式与日期格式()
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildSpec(Rows(1)), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("0.00", sheet.Cell(2, 3).Style.NumberFormat.Format);
        Assert.Equal("yyyy-mm-dd", sheet.Cell(2, 1).Style.NumberFormat.Format);
        Assert.True(sheet.Cell(2, 1).Value.IsDateTime);
        Assert.Equal(new DateTime(2026, 1, 2), sheet.Cell(2, 1).Value.GetDateTime());
    }

    /// <summary>
    /// 表头行冻结与自动筛选按表规格的开关落位，不被库的默认值顶回来
    /// </summary>
    /// <remarks>
    /// 渲染库里这两项的默认取值都是「开」。调用方关掉时必须显式交出关闭的配置，否则关掉开关的人拿到一份
    /// 照样冻结、照样带筛选区的档，与 <see cref="ExcelSheetSpec.FreezeHeader"/>、<see cref="ExcelSheetSpec.AutoFilter"/>
    /// 的承诺相反。
    /// </remarks>
    /// <param name="freeze">是否冻结表头行</param>
    /// <param name="autoFilter">是否自动开启筛选</param>
    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(false, false, 0, false)]
    [InlineData(true, false, 1, false)]
    [InlineData(false, true, 0, true)]
    public async Task 冻结与筛选按开关落位(bool freeze, bool autoFilter, int expectedSplitRow, bool expectedFilter)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream,
            BuildSpec(Rows(2), freezeHeader: freeze, autoFilter: autoFilter),
            TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(expectedSplitRow, sheet.SheetView.SplitRow);
        Assert.Equal(expectedFilter, sheet.AutoFilter.IsEnabled);
    }

    /// <summary>
    /// 样式与排版项在流式模式一律不写出，降级必须由返回值显式说出来而不是静默少给
    /// </summary>
    /// <remarks>
    /// 断的不只是 <see cref="ExcelExportResult.StylingSkipReason"/> 非空，还断它说得出丢了哪几类：边框、底色、
    /// 加粗、逐格条件样式、标题行、列宽、对齐。少列一类，调用方就会以为那一类还在。
    /// </remarks>
    [Fact]
    public async Task 样式与排版项不写出并交出逐项点名的降级理由()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Width = 40,
                Alignment = ExcelAlignment.Right,
                Wrap = true,
                CellStyle = _ => new ExcelTextStyle("#C00000", "#FFFF00", true),
                Value = row => row.AwbNo
            }
        };

        var stream = new MemoryStream();

        var result = await new MiniExcelStreamExporter().ExportAsync(
            stream,
            new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = typeof(SampleRow),
                Columns = columns,
                Rows = Rows(1),
                Title = "航空货运出港舱单",
                HeaderBold = false,
                HeaderFill = "#112233",
                Borders = false
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
        Assert.NotNull(result.StylingSkipReason);

        foreach (var item in new[] { "边框", "底色", "加粗", "条件样式", "标题", "列宽", "对齐" })
        {
            Assert.Contains(item, result.StylingSkipReason, StringComparison.Ordinal);
        }

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        // 交出的是库自己的无样式形态：表头既没有底色也没有加粗，边框与逐格样式同样不落
        Assert.Equal(XLFillPatternValues.None, sheet.Cell(1, 1).Style.Fill.PatternType);
        Assert.False(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(XLBorderStyleValues.None, sheet.Cell(2, 1).Style.Border.LeftBorder);
        Assert.False(sheet.Cell(2, 1).Style.Font.Bold);
        Assert.Equal(XLFillPatternValues.None, sheet.Cell(2, 1).Style.Fill.PatternType);
        Assert.NotEqual(40d, sheet.Column(1).Width);

        // 标题行没写：第一行就是表头
        Assert.Equal("提单号", sheet.Cell(1, 1).GetString());
    }

    /// <summary>
    /// 行集合只被枚举一次，实现不物化也不回头再枚举
    /// </summary>
    [Fact]
    public async Task 行集合只枚举一次()
    {
        var rows = new OneShotRows(Rows(3));

        await new MiniExcelStreamExporter().ExportAsync(
            new MemoryStream(),
            BuildSpec(rows),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, rows.GetEnumeratorCalls);
        Assert.Equal(3, rows.Yielded);
    }

    /// <summary>
    /// 取消落在写出的中途时抛出取消，绝不交出成功结果
    /// </summary>
    /// <remarks>
    /// 抛出时流里可能已经有字节，而那段字节是没收尾的档，读不回来——本用例只断「抛出且没有返回结果」，
    /// 不断字节数：这一路径先落盘后收尾，主张零字节会是假的。
    /// </remarks>
    [Fact]
    public async Task 取消发生在中途时抛取消而不交出成功结果()
    {
        using var source = new CancellationTokenSource();
        var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(stream, BuildSpec(CancelAfterFirstRow(source)), source.Token));
    }

    /// <summary>
    /// 令牌在入口就已取消时一个字节都不写
    /// </summary>
    [Fact]
    public async Task 入口已取消时不写任何字节()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(stream, BuildSpec(Rows(2)), source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 取值委托抛出的业务异常原样上抛，本类不吞、不换成另一种型别
    /// </summary>
    [Fact]
    public async Task 取值委托的业务异常原样上抛()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Value = _ => throw new InvalidOperationException("取值失败")
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(
                new MemoryStream(),
                new ExcelSheetSpec { SheetName = "运单", RowType = typeof(SampleRow), Columns = columns, Rows = Rows(1) },
                TestContext.Current.CancellationToken));

        Assert.Equal("取值失败", failure.Message);
    }

    /// <summary>
    /// 行型一致性走三条导出路径共用的那份判据：声明缺失与异型行都被拒
    /// </summary>
    [Fact]
    public async Task 异型行与缺失行型声明都被拒()
    {
        var mismatched = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { "不是行类型" }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(new MemoryStream(), mismatched, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(SampleRow), failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", failure.Message, StringComparison.Ordinal);

        var undeclared = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = null!,
            Columns = Columns,
            Rows = Rows(1)
        };

        var declared = await Assert.ThrowsAsync<ArgumentNullException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(new MemoryStream(), undeclared, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), declared.ParamName);
    }

    /// <summary>
    /// 早于工作簿日期下限的取值在流式路径同样被拒，不交出一份日期被夹改的档
    /// </summary>
    /// <remarks>
    /// 这是两条 xlsx 路径的口径一致性检查：同一份规格走全量会抛，走流式若把日期夹成纪元时刻再报成功，
    /// 分派器按行数换路径就等于换了一套「什么能导」的判据。
    /// </remarks>
    [Fact]
    public async Task 早于工作簿日期下限的取值被拒()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.Eta),
                Header = "预计到达",
                Value = row => row.Eta
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(
                new MemoryStream(),
                new ExcelSheetSpec
                {
                    SheetName = "运单",
                    RowType = typeof(SampleRow),
                    Columns = columns,
                    Rows = new[] { new SampleRow { AwbNo = "AWB1" } }
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("预计到达", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1899-12-30", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DateOnly 在本路径落日期格且值原样读回，早于纪元也照能导出
    /// </summary>
    /// <remarks>
    /// <para>
    /// 钉的是实测落格形态，不是取舍：<c>DateOnly</c> 在本路径进日期格、在全量路径进文本格，
    /// 同一个值落成不同格位而值不被改写，所以两条路径都不拒；日期下限判据只管会被夹改的 <c>DateTime</c>。
    /// 日后有人把这条形态差异读成「数据损坏」而给 <c>DateOnly</c> 加回拒写，会先把这几条用例弄红。
    /// </para>
    /// <para>
    /// 取的三个点各挡一种改法：1899-12-29 是「早于纪元但读回仍是原日期」那一侧，
    /// 1899-12-30 是日期下限本身，1500-01-01 证明这条界不是「凡早于纪元都出事」。
    /// </para>
    /// </remarks>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(1899, 12, 29)]
    [InlineData(1899, 12, 30)]
    [InlineData(1500, 1, 1)]
    public async Task 早于纪元的DateOnly在本路径落日期格且值不变(int year, int month, int day)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(new DateOnly(year, month, day)), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.True(cell.Value.IsDateTime);
        Assert.Equal(new DateTime(year, month, day), cell.Value.GetDateTime());
    }

    /// <summary>
    /// DateTimeOffset 在本路径落日期格且只保留钟表时刻，偏移量不随行值落格
    /// </summary>
    /// <remarks>
    /// 与全量路径把 <c>DateTimeOffset</c> 落成带偏移量的文本格相对：本路径丢开偏移量，
    /// 读回的是钟表时刻。这一条把「同值异格」写实，免得只写「形态可能不同」而让人以为偏移量还在。
    /// </remarks>
    [Fact]
    public async Task 带偏移量的DateTimeOffset在本路径只保留钟表时刻()
    {
        var value = new DateTimeOffset(1899, 12, 29, 6, 30, 0, TimeSpan.FromHours(8));

        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.True(cell.Value.IsDateTime);
        Assert.Equal(new DateTime(1899, 12, 29, 6, 30, 0), cell.Value.GetDateTime());
    }

    /// <summary>
    /// 远早于纪元的 DateOnly 与 DateTimeOffset 被写出库夹到 1899-12-30，值改了而结果照样回报完成
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条钉的是写出库的既有行为，不是本组件认可它。</b>本路径的日期下限判据只管 <c>DateTime</c>，
    /// <c>DateOnly</c> 与 <c>DateTimeOffset</c> 落到日期格后，早到 0001-01-01 一类的取值被库夹到纪元时刻：
    /// 读回的是另一个日期，返回值却仍是一份正常的降级结果，没有任何一处报出这次改写。
    /// </para>
    /// <para>
    /// 写在这里是为了让这条风险在 CI 里可见，而不是留成只有读代码的人才知道的坑：要保住这类日期，
    /// 用全量路径（落文本格、值原样读回）或先把该列转成文本。是否改成像 <c>DateTime</c> 那样连这两个型别一起拒，
    /// 由后续任务裁定——裁定落下来时这条用例就是改动的起点，不是障碍。
    /// </para>
    /// </remarks>
    /// <param name="useDateOnly">用 <c>DateOnly.MinValue</c> 还是 <c>DateTimeOffset.MinValue</c></param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 远早于纪元的日期型别在本路径被夹到纪元时刻(bool useDateOnly)
    {
        object value = useDateOnly ? new DateOnly(1, 1, 1) : new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var stream = new MemoryStream();

        var result = await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        Assert.Equal(ExcelConstants.ExtensionXlsx, result.FileExtension);

        using var workbook = Open(stream);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.True(cell.Value.IsDateTime);
        Assert.Equal(new DateTime(1899, 12, 30), cell.Value.GetDateTime());
    }

    /// <summary>
    /// 非有限数值在流式路径同样被拒，不交出一份读不回的档
    /// </summary>
    /// <param name="value">要写进一格的双精度值</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task 非有限数值在流式路径被拒(double value)
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = "Value",
                Header = "取值",
                Value = _ => value
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(
                new MemoryStream(),
                new ExcelSheetSpec { SheetName = "运单", RowType = typeof(SampleRow), Columns = columns, Rows = Rows(1) },
                TestContext.Current.CancellationToken));

        Assert.Contains("取值", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 不可用的表名抛出的是本组件的框架异常，不是渲染库那句英文异常，也不是被转义后的另一个名字
    /// </summary>
    /// <remarks>
    /// 渲染库自己会拒一部分坏名字（英文消息），也会把某些控制字符转义成另一个名字写进档——那等于静默改名。
    /// 表名判据与全量路径共用一份，因此这里断的是同一句中文消息与同一个 <c>ParamName</c>。
    /// </remarks>
    /// <param name="sheetName">要试的表名</param>
    [Theory]
    [InlineData("a[b")]
    [InlineData("'Lead")]
    [InlineData("\0")]
    [InlineData("SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSS")]
    public async Task 不可用的表名按共用守卫抛框架异常(string sheetName)
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(
                new MemoryStream(),
                BuildSpec(Rows(1), sheetName: sheetName),
                TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.SheetName), failure.ParamName);
        Assert.Contains("表名", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 空行集合交出一张空表并仍回报降级，不抛
    /// </summary>
    /// <remarks>
    /// 渲染库要靠第一行确定列集，零行时连表头行都不写；这与全量路径（表头照样落档）不同，是本路径的现实。
    /// 把它钉成用例而不是只在文档里写一句，免得日后被当成缺陷改掉，或被读文档的人误以为两条路径完全一致。
    /// </remarks>
    [Fact]
    public async Task 空行集合交出空表并回报降级()
    {
        var stream = new MemoryStream();

        var result = await new MiniExcelStreamExporter().ExportAsync(
            stream,
            BuildSpec(Array.Empty<SampleRow>()),
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(0, sheet.LastRowUsed()?.RowNumber() ?? 0);
    }

    /// <summary>
    /// 两列共用一个列键时被拒，不交出少一栏却报成功的档
    /// </summary>
    /// <remarks>
    /// 流式模式把每行投影成按键取值的字典，键重复时后写的盖掉先写的；全量路径按列的下标落格、没这个问题，
    /// 因此这条判定归本路径，而不是塞进表规格。
    /// </remarks>
    [Fact]
    public async Task 重复列键在流式路径被拒()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = "dup",
                Header = "提单号",
                Value = row => row.AwbNo
            },
            new()
            {
                Key = "dup",
                Header = "重量",
                Value = row => row.Weight
            }
        };

        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(
                stream,
                new ExcelSheetSpec
                {
                    SheetName = "运单",
                    RowType = typeof(SampleRow),
                    Columns = columns,
                    Rows = Rows(2)
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("dup", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 输出流的所有权在调用方：写完不关流，位置停在末尾且能复位读回
    /// </summary>
    [Fact]
    public async Task 输出流不被关闭且位置停在末尾()
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildSpec(Rows(2)), TestContext.Current.CancellationToken);

        Assert.True(stream.CanWrite);
        Assert.Equal(stream.Length, stream.Position);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal("运单", workbook.Worksheet(1).Name);
    }

    /// <summary>
    /// 输出流或表规格为 <c>null</c> 时在动任何事之前抛
    /// </summary>
    /// <param name="useNullStream">是否把输出流传成 <c>null</c></param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 空入参被拒(bool useNullStream)
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(
                useNullStream ? null! : new MemoryStream(),
                useNullStream ? BuildSpec(Rows(1)) : null!,
                TestContext.Current.CancellationToken));
    }

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
    /// 交出两行的惰性序列，取到第二行之前先把取消令牌取消
    /// </summary>
    private static IEnumerable<SampleRow> CancelAfterFirstRow(CancellationTokenSource source)
    {
        yield return new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) };

        source.Cancel();

        yield return new SampleRow { AwbNo = "AWB2", Weight = 2.5m, Eta = new DateTime(2026, 1, 3) };
    }

    /// <summary>
    /// 构造表规格，排版开关按用例给定，不给就用带样式的默认值
    /// </summary>
    private static ExcelSheetSpec BuildSpec(
        IEnumerable<SampleRow> rows,
        string sheetName = "运单",
        bool freezeHeader = true,
        bool autoFilter = true) => new()
    {
        SheetName = sheetName,
        RowType = typeof(SampleRow),
        Columns = Columns,
        Rows = rows,
        FreezeHeader = freezeHeader,
        AutoFilter = autoFilter
    };

    /// <summary>
    /// 构造只有「取值」一列、把指定值原样交出的表规格，专走落格形态与取值域判定
    /// </summary>
    private static ExcelSheetSpec BuildValueSpec(object value) => new()
    {
        SheetName = "运单",
        RowType = typeof(SampleRow),
        Columns =
        [
            new ExcelColumn<SampleRow>
            {
                Key = "Value",
                Header = "取值",
                Value = _ => value
            }
        ],
        Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } }
    };

    /// <summary>
    /// 把导出后的流回到起点并交回可读的工作簿
    /// </summary>
    private static XLWorkbook Open(MemoryStream stream)
    {
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    /// <summary>
    /// 只允许枚举一次的行集合夹具：第二次取枚举器即抛
    /// </summary>
    private sealed class OneShotRows : IEnumerable<SampleRow>
    {
        private readonly IEnumerable<SampleRow> _inner;

        private int _calls;

        public OneShotRows(IEnumerable<SampleRow> inner) => _inner = inner;

        public int GetEnumeratorCalls => _calls;

        public int Yielded { get; private set; }

        public IEnumerator<SampleRow> GetEnumerator()
        {
            _calls++;

            if (_calls > 1)
            {
                throw new InvalidOperationException("行集合被第二次枚举，说明实现先物化或回头再读了一遍");
            }

            return Iterate();
        }

        private IEnumerator<SampleRow> Iterate()
        {
            foreach (var row in _inner)
            {
                Yielded++;
                yield return row;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
