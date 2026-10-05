// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Importing;
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
/// 不物化这条契约由两条用例分头钉住，各自挡一种改法：行集合用只许取一次枚举器的夹具
/// （<c>OneShotRows</c>），回头再枚举一遍即抛；先整份转成列表再交出去的做法只取一次枚举器，
/// 夹具抓不到，另由「行集合未耗尽前输出流已有字节」按写出顺序抓——物化的可观察特征就是行全走完才动笔。
/// 两种改法都会让十万行档变成十万行内存，所以两条都留在 CI 里，不靠读代码担保。
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
    /// 行集合只被取用一次枚举器，实现不回头再枚举一遍
    /// </summary>
    /// <remarks>
    /// 这一条只挡二次枚举。先整份 <c>ToList()</c> 再交出去的实现同样只取一次枚举器，抓不到它，
    /// 由 <see cref="行集合未耗尽前输出流已有字节"/> 那条按写出顺序挡。
    /// </remarks>
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
    /// 行集合还没走完，写出者就已经往输出流里落了字节——这条才真钉住「不物化」
    /// </summary>
    /// <remarks>
    /// <para>
    /// 物化的可观察特征是「行全走完才动笔」，所以断的是顺序而不是内存。数据量刻意给到写出侧的缓冲装不下
    /// （每行一格数百字符、共数千行 XML 已远超库的写出缓冲），真流式的实现落点远早于行数中点，
    /// 取中点而不是「最后一行之前」是不让这条断言靠缓冲大小取巧。
    /// </para>
    /// <para>
    /// 把行集合先 <c>ToList()</c> 再交给写出库的实现里，每次取行时采到的输出流长度都是 0，
    /// <c>FirstRowIndexWithBytesWritten</c> 保持 <c>null</c>，这条即红——
    /// 而上一条（只许取一次枚举器）在这种情况下仍然全绿，两条合起来才把契约钉住。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 行集合未耗尽前输出流已有字节()
    {
        const int rowCount = 4000;

        var stream = new MemoryStream();
        var rows = new OneShotRows(BulkyRows(rowCount), () => stream.Length);

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildSpec(rows), TestContext.Current.CancellationToken);

        Assert.Equal(1, rows.GetEnumeratorCalls);
        Assert.Equal(rowCount, rows.Yielded);

        // 首次落字节的行号一并写进断言消息：这条判据靠「本用例的数据量大于写出侧缓冲」成立，
        // 光看红讯看不出是「实现物化了」还是「库把缓冲加大了」，两者要的处理完全不同。
        var firstByteRow = rows.FirstRowIndexWithBytesWritten;
        var firstByteText = firstByteRow is null
            ? "整趟枚举结束前都没有落字节"
            : $"第 {firstByteRow.Value.ToString(CultureInfo.InvariantCulture)} 行";

        Assert.True(
            firstByteRow is not null && firstByteRow < rowCount / 2,
            $"首次观察到输出流落字节是{firstByteText}，断言要求它早于行数中点（第 {rowCount / 2} 行，" +
            $"行集合共 {rowCount} 行、每行约 400 字符）。要么实现把行集合攒完才动笔（那就是物化，十万行档变成十万行内存），" +
            "要么写出侧的内部缓冲被加大到吞得下这份数据——后者是本用例的体量假设失效，不代表实现有错，" +
            "此时应加大 rowCount 或每行字符数再跑，而不是改掉这条断言。");
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
    /// 列约定的行型别与 <see cref="ExcelSheetSpec.RowType"/> 不符时，本路径同样在调用写出库之前就把规格拒掉
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两种坏声明：列清单来自另一个行类型，以及把 <c>RowType</c> 写成 <see cref="object"/> 来「放宽」。本路径把每行
    /// 投影成「列键到取值」的字典，异型行经列的取值方法只会得到 <c>null</c>，放行就是一份表头齐全、数据全空的档，
    /// 而返回值里唯一的降级理由是「流式模式不支持样式」，看不出数据已被写空。
    /// </para>
    /// <para>
    /// <c>GetEnumeratorCalls == 0</c> 是这条用例的主要证据：判据必须在碰行集合之前就拦住。夹具
    /// <c>OneShotRows</c> 只许取一次枚举器，预检若挪进逐行路径，这里会先被枚举行集合、再在写出库里落一份空数据档。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 列行型别与声明不符时流式预检抛且零字节不枚举()
    {
        var exporter = new MiniExcelStreamExporter();

        var mismatchedRows = new OneShotRows(Rows(3));
        var mismatchedStream = new MemoryStream();

        var mismatched = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            mismatchedStream,
            new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = typeof(SampleRow),
                Columns = MismatchedColumns,
                Rows = mismatchedRows
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), mismatched.ParamName);
        Assert.Contains("anno-name", mismatched.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(AnnotatedRow).FullName!, mismatched.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(SampleRow).FullName!, mismatched.Message, StringComparison.Ordinal);
        Assert.Equal(0, mismatchedStream.Length);
        Assert.Equal(0, mismatchedRows.GetEnumeratorCalls);
        Assert.Equal(0, mismatchedRows.Yielded);

        var widenedRows = new OneShotRows(Rows(3));
        var widenedStream = new MemoryStream();

        var widened = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            widenedStream,
            new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = typeof(object),
                Columns = Columns,
                Rows = widenedRows
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), widened.ParamName);
        Assert.Contains("System.Object", widened.Message, StringComparison.Ordinal);
        Assert.Equal(0, widenedStream.Length);
        Assert.Equal(0, widenedRows.GetEnumeratorCalls);
    }

    /// <summary>
    /// <c>RowType</c> 是列约定行型别的派生型别时照常导出，流式预检不得多拒
    /// </summary>
    [Fact]
    public async Task RowType是列行型别的派生型别时流式照常导出()
    {
        var stream = new MemoryStream();

        var result = await new MiniExcelStreamExporter().ExportAsync(
            stream,
            new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = typeof(DerivedSampleRow),
                Columns = Columns,
                Rows = new[]
                {
                    new DerivedSampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ExcelFormat.Xlsx, result.Format);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("提单号", sheet.Cell(1, 2).GetString());
        Assert.Equal("AWB1", sheet.Cell(2, 2).GetString());
    }

    /// <summary>
    /// 只认 <see cref="AnnotatedRow"/> 的一列，用于「列清单与声明的行型别不符」的反例
    /// </summary>
    private static readonly ExcelColumn[] MismatchedColumns =
    [
        new ExcelColumn<AnnotatedRow>
        {
            Key = "anno-name",
            Header = "名称",
            Value = row => row.Name
        }
    ];

    /// <summary>
    /// <see cref="SampleRow"/> 的派生行类型，用于「<c>RowType</c> 比列的型别更严」的正例
    /// </summary>
    private sealed class DerivedSampleRow : SampleRow
    {
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
    /// 早于工作簿日期下限的 DateOnly 与 DateTimeOffset 在本路径被拒，不交出日期格装不下的取值
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本路径把 <c>DateOnly</c> 与 <c>DateTimeOffset</c> 落成日期格，而 1900 日期系统里早于 1899-12-30 的日期
    /// 在这一格里没有对应的计数。写出库对这一段的取值并不都报错，有些原样读得回来——那是库的宽容、
    /// 不是格式的承诺，本组件不按取值远近赌哪一段安全，早于该时刻的整段拒。判据与 <c>DateTime</c>
    /// 用的是同一把尺（工作簿的日期下限），不另立常量。
    /// </para>
    /// <para>
    /// 取的点分三层：1899-12-29 是下限的前一天、1500-01-01 与 0100-01-01 是中等早年与极端早年、
    /// <c>MinValue</c> 是库最容易挪动手脚的那一档。同一些日期在全量路径照能导出（见
    /// <c>ClosedXmlExporterTests</c>），那两条用例与下面的
    /// <see cref="纪元当日及其后的日期型别在本路径落日期格且值不变"/> 一起证明两边各拒在该拒的地方。
    /// </para>
    /// </remarks>
    /// <param name="useDateOnly">true 用 <c>DateOnly</c>，false 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(true, 1899, 12, 29)]
    [InlineData(true, 1500, 1, 1)]
    [InlineData(true, 100, 1, 1)]
    [InlineData(true, 1, 1, 1)]
    [InlineData(false, 1899, 12, 29)]
    [InlineData(false, 1500, 1, 1)]
    [InlineData(false, 1, 1, 1)]
    public async Task 早于纪元的DateOnly与DateTimeOffset在流式路径被拒(bool useDateOnly, int year, int month, int day)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(stream, BuildValueSpec(DateValue(useDateOnly, year, month, day)), TestContext.Current.CancellationToken));

        Assert.Contains("日期", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1899-12-30", failure.Message, StringComparison.Ordinal);
        Assert.Contains("全量", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DateOnly 与 DateTimeOffset 在本路径落日期格且值原样读回，纪元当日与之后的都不被拒
    /// </summary>
    /// <remarks>
    /// <para>
    /// 钉的是本路径确实保得住能表示的日期：<c>DateOnly</c> 与 <c>DateTimeOffset</c> 在本路径进日期格、
    /// 在全量路径进文本格，同一个值落成不同格位而值不被改写。取的点都在工作簿日期格可表示的范围之内
    /// （1899-12-30 是那道下限本身、1899-12-31 是它的下一天、2026-01-02 是常态取值），
    /// 用来证明拒写没有把边界当日与其后的值一起挡掉——那正是上一轮拒写被撤回的原因。
    /// </para>
    /// <para>
    /// 早于下限的取值改由 <see cref="早于纪元的DateOnly与DateTimeOffset在流式路径被拒"/> 断拒写；
    /// 同一些日期在全量路径照能导出（见 <c>ClosedXmlExporterTests</c> 的对应用例），
    /// 两边不对称由那两条用例一起钉住。
    /// </para>
    /// </remarks>
    /// <param name="useDateOnly">true 用 <c>DateOnly</c>，false 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(true, 1899, 12, 30)]
    [InlineData(true, 1899, 12, 31)]
    [InlineData(true, 2026, 1, 2)]
    [InlineData(false, 1899, 12, 30)]
    [InlineData(false, 1899, 12, 31)]
    [InlineData(false, 2026, 1, 2)]
    public async Task 纪元当日及其后的日期型别在本路径落日期格且值不变(bool useDateOnly, int year, int month, int day)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(DateValue(useDateOnly, year, month, day)), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.True(cell.Value.IsDateTime);
        Assert.Equal(new DateTime(year, month, day, useDateOnly ? 0 : 6, useDateOnly ? 0 : 30, 0), cell.Value.GetDateTime());
    }

    /// <summary>
    /// 按型别造一格日期取值：<c>DateOnly</c> 不带时刻，<c>DateTimeOffset</c> 带 <c>06:30</c> 与 <c>+08:00</c>
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeOffset"/> 的构造本身不接受「公元 1 年再加 +08:00」这种组合（换算到 UTC 会掉出可表示范围），
    /// 所以早到那个量级的取值只能按零偏移量造——要验的是日期格装不下早于纪元的日期，与偏移量无关。
    /// </remarks>
    private static object DateValue(bool useDateOnly, int year, int month, int day)
    {
        if (useDateOnly)
        {
            return new DateOnly(year, month, day);
        }

        var offset = year == 1 ? TimeSpan.Zero : TimeSpan.FromHours(8);

        return new DateTimeOffset(year, month, day, 6, 30, 0, offset);
    }

    /// <summary>
    /// DateTimeOffset 在本路径落日期格且只保留钟表时刻，偏移量不随行值落格
    /// </summary>
    /// <remarks>
    /// 与全量路径把 <c>DateTimeOffset</c> 落成带偏移量的文本格相对：本路径丢开偏移量，
    /// 读回的是钟表时刻。这一条把「同值异格」写实，免得只写「形态可能不同」而让人以为偏移量还在。
    /// 取的日子在日期格可表示的范围之内——早于纪元的 <c>DateTimeOffset</c> 已改由
    /// <see cref="早于纪元的DateOnly与DateTimeOffset在流式路径被拒"/> 断拒写（上一轮这条用的是 1899-12-29）。
    /// </remarks>
    [Fact]
    public async Task 带偏移量的DateTimeOffset在本路径只保留钟表时刻()
    {
        var value = new DateTimeOffset(2026, 1, 2, 6, 30, 0, TimeSpan.FromHours(8));

        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.True(cell.Value.IsDateTime);
        Assert.Equal(new DateTime(2026, 1, 2, 6, 30, 0), cell.Value.GetDateTime());
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
    /// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值在流式路径同样拒写
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本路径把取值的文本交给写出库落格，越界取值在这里能原样写进档里（16 位整数就是整串数字），
    /// 看起来比全量路径「更准」，实则两条路径对同一份规格交出两个不同的数：分派器按行数决定走哪条，
    /// 得到哪个数就成了走哪条的副产品。位数这一道按取值本身判，两条路径一起拒，判据只有 <c>ExcelWorkbookWriteGuard</c> 里那一份。
    /// </para>
    /// <para>
    /// <c>float</c> 是按同一判据从条文最初的四类型扩用进来的：落进数值格的是它展开成 <see cref="double"/>
    /// 后的那份形态，<c>0.1f</c> 展开后已是 17 位。
    /// </para>
    /// <para>
    /// 与全量路径不同，本路径的取值判定发生在逐行投影期间，此时表头与建档骨架已经落进流里，
    /// 因此这里断「抛出且没有成功结果」，不断零字节——全量路径才主张零字节（见 <c>ClosedXmlExporterTests</c>）。
    /// </para>
    /// </remarks>
    /// <param name="value">要落进一格的数值取值</param>
    /// <param name="reason">消息里该出现的成因片段（点明实际位数）</param>
    [Theory]
    [MemberData(nameof(OverPreciseNumericCases))]
    public async Task 有效数字多于十五位的数值在流式路径拒写(object value, string reason)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        Assert.Contains("取值", failure.Message, StringComparison.Ordinal);
        Assert.Contains("键 Value", failure.Message, StringComparison.Ordinal);
        Assert.Contains("第 1 行", failure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
        Assert.Contains("转成字符串栏位", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 上限内的数值在流式路径照写，并由本组件的导入器读回同一份数
    /// </summary>
    /// <remarks>
    /// 判尺不多拒：一位小数、恰为 15 位的整数、带负号带小数点的 15 位取值、展开后仍短的单精度都照写。
    /// 往返判定用 <see cref="ExcelDataReaderImporter"/>，不用 ClosedXML 读自己写的档——后者会按工作簿自己的
    /// 形式反算，把「档里被改写过」这件事掩盖掉。
    /// </remarks>
    /// <param name="value">要落进一格的数值取值</param>
    /// <param name="expected">读回来该是的那份数的不变文化文本</param>
    [Theory]
    [MemberData(nameof(WithinPrecisionNumericCases))]
    public async Task 上限内的数值在流式路径照写并由导入器读回原值(object value, string expected)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var back = await ImportValueAsync(stream);

        Assert.IsType<double>(back);
        Assert.Equal(expected, ((double)back!).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 越界数值案例：要落一格的取值，与消息里该出现的成因片段
    /// </summary>
    /// <remarks>
    /// 与 <c>ClosedXmlExporterTests</c> 的同名案例集取同一批字面量：两条路径各一对反例是这一批的规矩，
    /// 两处的取值保持相同才能说明判的是同一件事。
    /// </remarks>
    public static IEnumerable<object?[]> OverPreciseNumericCases()
    {
        yield return new object?[] { 1234567890123456L, "有 16 位有效数字" };
        yield return new object?[] { 12345678901234.5678m, "有 18 位有效数字" };
        yield return new object?[] { 12345678901234567.89m, "有 19 位有效数字" };
        yield return new object?[] { 1.0 / 3.0, "有 16 位有效数字" };
        yield return new object?[] { 0.1 + 0.2, "有 17 位有效数字" };
        yield return new object?[] { 0.1f, "有 17 位有效数字" };
        yield return new object?[] { 3.14f, "有 16 位有效数字" };
        yield return new object?[] { 12345678901234567890UL, "有 19 位有效数字" };
    }

    /// <summary>
    /// 上限内数值案例：要落一格的取值，与导入器读回来该是的那份数的不变文化文本
    /// </summary>
    public static IEnumerable<object?[]> WithinPrecisionNumericCases()
    {
        yield return new object?[] { 1.5m, "1.5" };
        yield return new object?[] { 123456789012345L, "123456789012345" };
        yield return new object?[] { 12345678901234.5m, "12345678901234.5" };
        yield return new object?[] { -12345678901234.5m, "-12345678901234.5" };
        yield return new object?[] { 1234567890123450L, "1234567890123450" };
        yield return new object?[] { 999999999999999m, "999999999999999" };
        yield return new object?[] { 1.5f, "1.5" };
        yield return new object?[] { 0m, "0" };
    }

    /// <summary>
    /// 公式起首的值在流式路径落文字格、逐字读回，且档里不会多出撇号前缀
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与全量路径同一件事实：本路径把字串按文本形态落格（档里是 <c>t="str"</c> 的文字值而不是公式），
    /// 写出库不解析 <c>=</c>、<c>+</c>、<c>@</c> 起首的内容，档里没有 <c>&lt;f&gt;</c>。
    /// 只有模板路径的 <c>$=</c> 占位会被当成公式解析（另一批处理）。
    /// </para>
    /// <para>
    /// 两条路径的格位形态本来就不同，这一对用例钉的是同一个承诺：起首像公式的字串照原样交回，
    /// 谁也不给它加 <c>'</c> 前缀——加了就是改写业务资料，逐字断言先红；改成会触发公式的写出方式，
    /// 无 <c>&lt;f&gt;</c> 那条红。
    /// </para>
    /// </remarks>
    /// <param name="value">以公式前缀起首的字串取值</param>
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(1)")]
    [InlineData("\t=1+1")]
    [InlineData("\n=1+1")]
    public async Task 公式起首的值在流式路径落文字格且逐字读回(string value)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        Assert.False(HasFormulaElement(stream));

        using (var workbook = Open(stream))
        {
            var cell = workbook.Worksheet(1).Cell(2, 1);

            Assert.False(cell.HasFormula);
            Assert.Equal(XLDataType.Text, cell.DataType);
        }

        var back = await ImportValueAsync(stream);

        Assert.IsType<string>(back);
        Assert.Equal(value, back);
        Assert.False(((string)back!).StartsWith('\''));
    }

    /// <summary>
    /// 值里的回车在档里按 XML 归一化成换行，其余逐字不变且仍是文字格
    /// </summary>
    /// <remarks>
    /// XML 1.0 不允许文本内容里出现裸 <c>U+000D</c>，写出库把它归一成 <c>U+000A</c>：
    /// <c>"\r=1+1"</c> 读回来是 <c>"\n=1+1"</c>。这一格依旧不落公式、不加撇号前缀。
    /// 与全量路径同一件事，两条各钉一条，免得日后只看见一边就以为另一边也成立。
    /// </remarks>
    [Fact]
    public async Task 值里的回车在流式档里归一化成换行()
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(stream, BuildValueSpec("\r=1+1"), TestContext.Current.CancellationToken);

        Assert.False(HasFormulaElement(stream));

        var back = await ImportValueAsync(stream);

        Assert.Equal("\n=1+1", back);
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
    /// 造若干行、每行提单号给数百字符，让整份行集合的 XML 远超写出侧的缓冲
    /// </summary>
    /// <remarks>
    /// 只给「不物化」那条用例用：数据量不够时，实现就算真的边取边写，字节也可能全压在库自己的缓冲里
    /// 没落到输出流上，那条断言就会靠缓冲大小而不是行为来判，失去意义。
    /// </remarks>
    private static SampleRow[] BulkyRows(int count)
    {
        var rows = new SampleRow[count];

        for (var index = 0; index < count; index++)
        {
            rows[index] = new SampleRow
            {
                AwbNo = $"AWB{index}-" + new string('X', 400),
                Weight = index + 0.5m,
                Eta = new DateTime(2026, 1, 2)
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
    /// 判断档里的工作表部件有没有公式元素（<c>&lt;f&gt;</c> 或带命名空间前缀的同名元素）
    /// </summary>
    /// <param name="stream">导出后的流</param>
    /// <remarks>
    /// 只看格子的 <c>HasFormula</c> 不足以证明「值没被当成公式解析」——那要读回档里真实落的元素。
    /// 匹配式要求 <c>f</c> 之后紧跟空白、<c>/</c> 或 <c>&gt;</c>，因此 <c>&lt;framePr&gt;</c>、
    /// <c>&lt;fextLdr&gt;</c> 这类同名前缀的元素不会被误判成公式。
    /// </remarks>
    private static bool HasFormulaElement(MemoryStream stream)
    {
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var entry = archive.GetEntry("xl/worksheets/sheet1.xml");

        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);

        return Regex.IsMatch(reader.ReadToEnd(), "<(?:x:)?f[\\s/>]");
    }

    /// <summary>
    /// 用本框架的导入器把导出的档读回，交出「取值」这一列的第一笔值
    /// </summary>
    /// <param name="stream">导出后的流，本方法把它回到起点，不关闭也不释放</param>
    /// <remarks>
    /// 往返断言的判定器一律走这里，不用 <see cref="XLWorkbook"/> 读自己写的档：工作簿会按自己的形式反算，
    /// 读回来的数看着与写进去的一致，恰好掩盖档里被改写过这件事。导入器对数值格交回
    /// <see cref="double"/>、对文字格交回 <see cref="string"/>，格位由档里真实落的东西决定。
    /// </remarks>
    private static async Task<object?> ImportValueAsync(MemoryStream stream)
    {
        stream.Position = 0;

        var rows = new List<ExcelImportRow>();

        await foreach (var row in new ExcelDataReaderImporter()
            .ReadAsync(stream, new ExcelImportOptions { Format = ExcelImportFormat.Xlsx }, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.True(rows[0].Values.ContainsKey("取值"));

        return rows[0].Values["取值"];
    }

    /// <summary>
    /// 只允许枚举一次的行集合夹具：第二次取枚举器即抛
    /// </summary>
    private sealed class OneShotRows : IEnumerable<SampleRow>
    {
        private readonly IEnumerable<SampleRow> _inner;

        private readonly Func<long>? _writtenBytes;

        private int _calls;

        public OneShotRows(IEnumerable<SampleRow> inner, Func<long>? writtenBytes = null)
        {
            _inner = inner;
            _writtenBytes = writtenBytes;
        }

        public int GetEnumeratorCalls => _calls;

        public int Yielded { get; private set; }

        /// <summary>
        /// 第一次采到输出流已有字节时已经交出去的行号，<c>null</c> 表示交完全部行仍是空的
        /// </summary>
        /// <remarks>
        /// 只在建了 <paramref name="writtenBytes"/> 采样器时才有值。它把「边取边写」与「先攒完再动笔」分开：
        /// 后者在每次取行时采到的输出流长度都是 0，这个属性也就一直是 <c>null</c>。
        /// </remarks>
        public int? FirstRowIndexWithBytesWritten { get; private set; }

        public IEnumerator<SampleRow> GetEnumerator()
        {
            _calls++;

            if (_calls > 1)
            {
                throw new InvalidOperationException("行集合被第二次枚举，说明实现回头又读了一遍");
            }

            return Iterate();
        }

        private IEnumerator<SampleRow> Iterate()
        {
            foreach (var row in _inner)
            {
                Yielded++;

                if (_writtenBytes is not null && FirstRowIndexWithBytesWritten is null && _writtenBytes() > 0)
                {
                    FirstRowIndexWithBytesWritten = Yielded;
                }

                yield return row;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
