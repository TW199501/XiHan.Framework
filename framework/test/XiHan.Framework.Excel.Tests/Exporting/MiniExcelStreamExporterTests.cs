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
/// 断言一律用 <c>new XLWorkbook(stream)</c> 回读档内容。
/// </remarks>
public class MiniExcelStreamExporterTests
{
    /// <summary>
    /// 预计到达、提单号、重量三列：声明顺序与列上的 <see cref="ExcelColumn.Order"/> 都不同于写出顺序
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
    /// 表头行冻结与自动筛选按表规格的开关落位
    /// </summary>
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
    /// 样式与排版项在流式模式一律不写出，返回值交出降级理由
    /// </summary>
    /// <remarks>
    /// <see cref="ExcelExportResult.StylingSkipReason"/> 逐项点名边框、底色、加粗、逐格条件样式、标题行、列宽、对齐。
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

        // 表头既没有底色也没有加粗，边框与逐格样式同样不落
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
    /// 行集合只被取用一次枚举器
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
    /// 行集合还没走完，写出者就已经往输出流里落了字节
    /// </summary>
    /// <remarks>
    /// 每行一格约 400 字符、共 4000 行，断言首次落字节的行早于行数中点。
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
    /// 只断抛出取消，不断流里的字节数。
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
    /// 两种坏声明：列清单来自另一个行类型，以及把 <c>RowType</c> 写成 <see cref="object"/>。
    /// 断言抛出、输出流零字节且行集合未被枚举（<c>GetEnumeratorCalls == 0</c>）。
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
    /// <c>RowType</c> 是列约定行型别的派生型别时照常导出
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
    /// 早于日期下限的 DateTime 在流式路径同样被拒
    /// </summary>
    /// <remarks>
    /// 取 <c>SampleRow.Eta</c> 未赋值时的 <c>0001-01-01</c>，早于 1900-01-01 下限。
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
        Assert.Contains("1900-01-01", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 早于日期下限的 DateOnly 与 DateTimeOffset 在本路径被拒，与全量路径同判、成因句逐字相同
    /// </summary>
    /// <remarks>
    /// 取点：1899-12-31 与 1899-12-30 是下限的前一天与前两天、1899-12-29 再往前一天、
    /// 1500-01-01 与 0100-01-01 是中等早年与极端早年、<c>MinValue</c> 是最远的那一档。
    /// </remarks>
    /// <param name="useDateOnly">true 用 <c>DateOnly</c>，false 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(true, 1899, 12, 31)]
    [InlineData(true, 1899, 12, 30)]
    [InlineData(true, 1899, 12, 29)]
    [InlineData(true, 1500, 1, 1)]
    [InlineData(true, 100, 1, 1)]
    [InlineData(true, 1, 1, 1)]
    [InlineData(false, 1899, 12, 31)]
    [InlineData(false, 1899, 12, 30)]
    [InlineData(false, 1899, 12, 29)]
    [InlineData(false, 1500, 1, 1)]
    [InlineData(false, 1, 1, 1)]
    public async Task 早于日期下限的DateOnly与DateTimeOffset在流式路径被拒(bool useDateOnly, int year, int month, int day)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(stream, BuildValueSpec(DateValue(useDateOnly, year, month, day)), TestContext.Current.CancellationToken));

        Assert.Contains("日期", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1900-01-01", failure.Message, StringComparison.Ordinal);
        Assert.Contains("取值", failure.Message, StringComparison.Ordinal);
        Assert.Contains("键 Value", failure.Message, StringComparison.Ordinal);

        // 消息里不指点换一条写出路径
        Assert.DoesNotContain("全量", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 下限当日及其后的日期型别在本路径落日期格，并由本框架的导入器读回同一个钟表时刻
    /// </summary>
    /// <remarks>
    /// <c>1900-01-01</c> 是 1900 日期系统序号 1 的那一天，档里写成 <c>&lt;v&gt;1&lt;/v&gt;</c>。
    /// 用 <see cref="ImportValueAsync"/> 回读：导入器对日期格交回 <see cref="DateTime"/>，
    /// <c>DateOnly</c> 断当日零点，<c>DateTimeOffset</c> 断它的钟表时刻。
    /// </remarks>
    /// <param name="useDateOnly">true 用 <c>DateOnly</c>，false 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(true, 1900, 1, 1)]
    [InlineData(true, 2026, 1, 2)]
    [InlineData(false, 1900, 1, 1)]
    [InlineData(false, 2026, 1, 2)]
    public async Task 下限当日及其后的日期型别在本路径落日期格且值不变(bool useDateOnly, int year, int month, int day)
    {
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(DateValue(useDateOnly, year, month, day)), TestContext.Current.CancellationToken);

        var back = await ImportValueAsync(stream);

        Assert.IsType<DateTime>(back);
        Assert.Equal(new DateTime(year, month, day, useDateOnly ? 0 : 6, useDateOnly ? 0 : 30, 0), (DateTime)back!);
    }

    /// <summary>
    /// 按型别造一格日期取值：<c>DateOnly</c> 不带时刻，<c>DateTimeOffset</c> 带 <c>06:30</c> 与 <c>+08:00</c>
    /// </summary>
    /// <remarks>
    /// 公元 1 年按零偏移量造：<see cref="DateTimeOffset"/> 不接受公元 1 年加 +08:00。
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
    /// 用 <see cref="ImportValueAsync"/> 回读，断言交回不带偏移量的 <see cref="DateTime"/>。
    /// </remarks>
    [Fact]
    public async Task 带偏移量的DateTimeOffset在本路径只保留钟表时刻()
    {
        var value = new DateTimeOffset(2026, 1, 2, 6, 30, 0, TimeSpan.FromHours(8));

        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var back = await ImportValueAsync(stream);

        Assert.IsType<DateTime>(back);
        Assert.Equal(new DateTime(2026, 1, 2, 6, 30, 0), (DateTime)back!);
    }

    /// <summary>
    /// 非有限数值在流式路径同样被拒
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
    /// <c>float</c> 按展开成 <see cref="double"/> 后的形态计位数，<c>0.1f</c> 展开后是 17 位。
    /// 只断抛出，不断零字节。
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
    /// 一位小数、恰为 15 位的整数、带负号带小数点的 15 位取值、展开后仍短的单精度都照写。
    /// 用 <see cref="ExcelDataReaderImporter"/> 回读。
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
    /// 与 <c>ClosedXmlExporterTests</c> 的同名案例集取同一批字面量。
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
    /// 本路径把字串按文本形态落格（档里是 <c>t="str"</c> 的文字值而不是公式），
    /// 写出库不解析 <c>=</c>、<c>+</c>、<c>@</c> 起首的内容，档里没有 <c>&lt;f&gt;</c>。
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
    /// <c>"\r=1+1"</c> 读回来是 <c>"\n=1+1"</c>。这一格依旧不落公式、不加撇号前缀。
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
    /// <see cref="DateTime"/> 在流式路径同样按钟表时刻落格：不读 <see cref="DateTime.Kind"/>、不做时区换算
    /// </summary>
    /// <remarks>
    /// <c>Utc</c>／<c>Local</c> 都照显示的年月日时分秒落格，读回来一律是
    /// <see cref="DateTimeKind.Unspecified"/>。
    /// </remarks>
    /// <param name="kind">写出的 <see cref="DateTime"/> 带的 Kind</param>
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task DateTime在流式路径按钟表时刻落格且不读Kind(DateTimeKind kind)
    {
        var value = new DateTime(2024, 5, 6, 7, 8, 9, kind);

        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var back = await ImportValueAsync(stream);

        Assert.IsType<DateTime>(back);

        var read = (DateTime)back!;

        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), read);
        Assert.Equal(7, read.Hour);
        Assert.Equal(8, read.Minute);
        Assert.Equal(9, read.Second);
        Assert.Equal(DateTimeKind.Unspecified, read.Kind);
    }

    /// <summary>
    /// 不可用的表名抛出的是本组件的框架异常，不是渲染库那句英文异常，也不是被转义后的另一个名字
    /// </summary>
    /// <remarks>
    /// 断言表名判据交出的中文消息与 <c>ParamName</c>。
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
    /// 零行时连表头行都不写。
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
    /// 两列共用一个列键时被拒
    /// </summary>
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
    /// 无标题行时，表头行加数据行正好占满上限的那一份照常写出
    /// </summary>
    /// <remarks>
    /// 上限注入成 3：写出库落下的表头占第 1 行，两行数据占第 2、3 行，正好触线。
    /// 用本组件的导入器回读。
    /// </remarks>
    [Fact]
    public async Task 流式路径行数恰等上限时照常写出()
    {
        var exporter = new MiniExcelStreamExporter(3);
        var stream = new MemoryStream();

        var result = await exporter.ExportAsync(
            stream, BuildRowLimitSpec(Rows(2), title: null), TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
        Assert.Equal(["AWB0", "AWB1"], await ImportAwbNosAsync(stream));
    }

    /// <summary>
    /// 超过行数上限时抛出框架异常、不回报成功结果，且判定没有把行集合物化
    /// </summary>
    /// <remarks>
    /// 上限注入成 3，行集合给 10 行：表头占第 1 行，第 3 行数据要落到第 4 行即触线。
    /// 断言行集合只取到第 3 行、消息点出上限值与两条出路；不断言输出流零字节。
    /// </remarks>
    [Fact]
    public async Task 流式路径超过行数上限时抛且不回报成功结果()
    {
        var exporter = new MiniExcelStreamExporter(3);
        var rows = new CountingRows(10);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildRowLimitSpec(rows, title: null), TestContext.Current.CancellationToken));

        Assert.Equal(3, rows.Count);
        Assert.Contains("3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("分成多张表", failure.Message, StringComparison.Ordinal);
        Assert.Contains("文字档", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 本路径不写标题行，因此标题不占行数：同样的上限 3，带标题的两行数据照样放行
    /// </summary>
    [Fact]
    public async Task 流式路径不写标题行因此标题不占行数()
    {
        var exporter = new MiniExcelStreamExporter(3);
        var stream = new MemoryStream();

        await exporter.ExportAsync(
            stream, BuildRowLimitSpec(Rows(2), title: "运单明细"), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("提单号", sheet.Cell(1, 1).GetString());
        Assert.Equal(["AWB0", "AWB1"], await ImportAwbNosAsync(stream));
    }

    /// <summary>
    /// 表头长过单元格上限时，本路径也在调用写出库之前把它拒掉：零字节、行集合一次都没被枚举
    /// </summary>
    /// <remarks>
    /// 消息不嵌超长表头本身。
    /// </remarks>
    [Fact]
    public async Task 流式路径表头超过单元格上限时在写出前被拒()
    {
        var exporter = new MiniExcelStreamExporter();
        var rows = new CountingRows(1);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream,
            BuildTextLimitSpec(new string('A', 40_000), title: null, rows: rows),
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Header), failure.ParamName);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, rows.Count);
        Assert.Contains(ExcelConstants.MaxCellTextLength.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains("40000", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow.AwbNo), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表头刚过上限一个字符（32,768）同样被拒：边界是「超过即拒」，两条路径同一把尺
    /// </summary>
    [Fact]
    public async Task 流式路径表头刚过单元格上限一个字符时同样在写出前被拒()
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelStreamExporter().ExportAsync(
            stream,
            BuildTextLimitSpec(new string('A', ExcelConstants.MaxCellTextLength + 1), title: null, rows: Rows(1)),
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Header), failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 标题长过单元格上限时本路径同样拒，即使本路径根本不写标题行
    /// </summary>
    [Fact]
    public async Task 流式路径标题超过单元格上限时在写出前被拒()
    {
        var exporter = new MiniExcelStreamExporter();
        var rows = new CountingRows(1);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream,
            BuildTextLimitSpec("提单号", title: new string('B', 40_000), rows: rows),
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.Title), failure.ParamName);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, rows.Count);
        Assert.Contains(ExcelConstants.MaxCellTextLength.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains("40000", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BBBB", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 标题刚过上限一个字符（32,768）同样被拒
    /// </summary>
    [Fact]
    public async Task 流式路径标题刚过单元格上限一个字符时同样在写出前被拒()
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelStreamExporter().ExportAsync(
            stream,
            BuildTextLimitSpec("提单号", title: new string('B', ExcelConstants.MaxCellTextLength + 1), rows: Rows(1)),
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.Title), failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 表头恰等单元格上限（32,767）时照常写出，读回来的表头逐字不变
    /// </summary>
    /// <remarks>
    /// 用本组件的导入器回读：按 <c>HasHeader</c> 把第一行当列名交回，键名就是档里落下的表头。
    /// </remarks>
    [Fact]
    public async Task 流式路径表头恰等单元格上限时照常写出并可读回()
    {
        var header = new string('A', ExcelConstants.MaxCellTextLength);
        var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(
            stream, BuildTextLimitSpec(header, title: null, rows: Rows(1)), TestContext.Current.CancellationToken);

        var imported = await ImportRowsAsync(stream, hasHeader: true);

        var row = Assert.Single(imported);

        Assert.Equal(header, row.Values.Keys.Single());
        Assert.Equal("AWB0", row.Values[header]);
    }

    /// <summary>
    /// 标题恰等上限时本路径照常写出，标题按降级处理、不落档
    /// </summary>
    /// <remarks>
    /// 降级理由点着标题行；按 <c>HasHeader = true</c> 读回时列名是「提单号」而不是那串标题。
    /// </remarks>
    [Fact]
    public async Task 流式路径标题恰等单元格上限时照常写出且标题仍不落档()
    {
        var stream = new MemoryStream();

        var result = await new MiniExcelStreamExporter().ExportAsync(
            stream,
            BuildTextLimitSpec("提单号", title: new string('B', ExcelConstants.MaxCellTextLength), rows: Rows(1)),
            TestContext.Current.CancellationToken);

        Assert.False(result.StylingApplied);
        Assert.Contains("标题行", result.StylingSkipReason, StringComparison.Ordinal);

        var imported = await ImportRowsAsync(stream, hasHeader: true);

        var row = Assert.Single(imported);

        Assert.Equal("提单号", row.Values.Keys.Single());
        Assert.Equal("AWB0", row.Values["提单号"]);
    }

    /// <summary>
    /// 同一份超长声明在两条 xlsx 路径上得到同一个答案：同型别、同 <c>ParamName</c>、同一条消息
    /// </summary>
    [Fact]
    public async Task 两条xlsx路径对同一份超长表头与标题给同一个答案()
    {
        var oversizedHeader = new string('A', 40_000);
        var oversizedTitle = new string('B', 40_000);

        var fullHeaderStream = new MemoryStream();
        var streamHeaderStream = new MemoryStream();

        var fullHeader = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(fullHeaderStream, BuildTextLimitSpec(oversizedHeader, title: null, rows: Rows(1)), TestContext.Current.CancellationToken));
        var streamHeader = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(streamHeaderStream, BuildTextLimitSpec(oversizedHeader, title: null, rows: Rows(1)), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Header), streamHeader.ParamName);
        Assert.Equal(fullHeader.ParamName, streamHeader.ParamName);
        Assert.Equal(fullHeader.Message, streamHeader.Message);
        Assert.Equal(0, fullHeaderStream.Length);
        Assert.Equal(0, streamHeaderStream.Length);

        var fullTitleStream = new MemoryStream();
        var streamTitleStream = new MemoryStream();

        var fullTitle = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(fullTitleStream, BuildTextLimitSpec("提单号", title: oversizedTitle, rows: Rows(1)), TestContext.Current.CancellationToken));
        var streamTitle = await Assert.ThrowsAsync<ArgumentException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(streamTitleStream, BuildTextLimitSpec("提单号", title: oversizedTitle, rows: Rows(1)), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.Title), streamTitle.ParamName);
        Assert.Equal(fullTitle.ParamName, streamTitle.ParamName);
        Assert.Equal(fullTitle.Message, streamTitle.Message);
        Assert.Equal(0, fullTitleStream.Length);
        Assert.Equal(0, streamTitleStream.Length);
    }

    /// <summary>
    /// 构造只有一列、可指定表头与标题的表规格，专走表头与标题的长度预检
    /// </summary>
    private static ExcelSheetSpec BuildTextLimitSpec(string header, string? title, System.Collections.IEnumerable rows) => new()
    {
        SheetName = "运单",
        Title = title,
        RowType = typeof(SampleRow),
        Columns =
        [
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = header,
                Value = row => row.AwbNo
            }
        ],
        Rows = rows
    };

    /// <summary>
    /// 用本框架的导入器把导出的档整份读回
    /// </summary>
    /// <param name="stream">导出后的流，本方法把它回到起点，不关闭也不释放</param>
    /// <param name="hasHeader">是否把第一行当列名，见 <see cref="ExcelImportOptions.HasHeader"/></param>
    private static async Task<List<ExcelImportRow>> ImportRowsAsync(MemoryStream stream, bool hasHeader)
    {
        stream.Position = 0;

        var rows = new List<ExcelImportRow>();

        await foreach (var row in new ExcelDataReaderImporter()
            .ReadAsync(stream, new ExcelImportOptions { Format = ExcelImportFormat.Xlsx, HasHeader = hasHeader }, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// 构造只有一列、可指定标题行的表规格，专走行数上限判定
    /// </summary>
    private static ExcelSheetSpec BuildRowLimitSpec(System.Collections.IEnumerable rows, string? title) => new()
    {
        SheetName = "运单",
        Title = title,
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
        Rows = rows
    };

    /// <summary>
    /// 用本框架的导入器把导出的档读回，交出「提单号」这一列的逐行取值
    /// </summary>
    /// <param name="stream">导出后的流，本方法把它回到起点，不关闭也不释放</param>
    private static async Task<string[]> ImportAwbNosAsync(MemoryStream stream)
    {
        stream.Position = 0;

        var values = new List<string>();

        await foreach (var row in new ExcelDataReaderImporter()
            .ReadAsync(stream, new ExcelImportOptions { Format = ExcelImportFormat.Xlsx }, TestContext.Current.CancellationToken))
        {
            Assert.True(row.Values.ContainsKey("提单号"));

            values.Add((string)row.Values["提单号"]!);
        }

        return [.. values];
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
    /// 流式工作簿支持非法 C0 字符的 OOXML 转义，且不把字面转义序列误还原为控制字符
    /// </summary>
    /// <param name="value">原始文本</param>
    /// <param name="expected">读取器应交回的文本</param>
    [Theory]
    [InlineData("a\u0000b", "a\u0000b")]
    [InlineData("a\u0001b", "a\u0001b")]
    [InlineData("a\u0008b", "a\u0008b")]
    [InlineData("a\u000Bb", "a\u000Bb")]
    [InlineData("a\u000Cb", "a\u000Cb")]
    [InlineData("a\u000Eb", "a\u000Eb")]
    [InlineData("a\u001Fb", "a\u001Fb")]
    [InlineData("a_x0001_b", "a_x0001_b")]
    [InlineData("a_x0009_b", "a_x0009_b")]
    [InlineData("a_x000D_b", "a_x000D_b")]
    [InlineData("a\tb", "a\tb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    public async Task 控制字符与字面转义序列在流式路径按原有语义读回(string value, string expected)
    {
        using var stream = new MemoryStream();

        await new MiniExcelStreamExporter().ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.IsType<string>(await ImportValueAsync(stream)));
    }

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
    /// 匹配式要求 <c>f</c> 之后紧跟空白、<c>/</c> 或 <c>&gt;</c>，<c>&lt;framePr&gt;</c>、
    /// <c>&lt;fextLdr&gt;</c> 这类同名前缀的元素不算公式。
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
    /// 导入器对数值格交回 <see cref="double"/>、对文字格交回 <see cref="string"/>。
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
        /// 只在建了 <c>writtenBytes</c> 采样器时才有值。
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
