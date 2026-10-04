// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using ClosedXML.Excel;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// ClosedXML 单表导出与排版测试
/// </summary>
/// <remarks>
/// <para>
/// 断言一律走 <c>new XLWorkbook(stream)</c> 回读工作簿属性，不检查导出器内部对象：样式、格式串、合并范围、
/// 冻结与筛选只有落进 xlsx 才算兑现，回读是唯一能证明「文件里真是这样」的口径。
/// </para>
/// <para>
/// 颜色按 <see cref="XLColor.Color"/> 的 RGB 分量比成 <c>#RRGGBB</c> 文本，不比 <see cref="XLColor"/> 的相等性——
/// 存盘后 ClosedXML 可能以索引色或主题色交回同一个颜色，相等性会随写入路径漂移。
/// </para>
/// </remarks>
public class ClosedXmlExporterTests
{
    private const string LongAwbNo = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>
    /// 提单号、重量、预计到达三列：数值列格式串取 <c>0.00</c>，日期列取 <c>yyyy-mm-dd</c>
    /// </summary>
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
            NumberFormat = "0.00",
            Value = row => row.Weight
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Eta),
            Header = "预计到达",
            Order = 2,
            NumberFormat = "yyyy-mm-dd",
            Value = row => row.Eta
        }
    ];

    /// <summary>
    /// 写出表头与数据，并证明交回的流仍可从头读回
    /// </summary>
    [Fact]
    public async Task 写出表头与数据并可回读()
    {
        var (stream, result) = await ExportWithResultAsync(
            BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }]));

        Assert.Equal(ExcelFormat.Xlsx, result.Format);
        Assert.Equal(".xlsx", result.FileExtension);
        Assert.Equal(ExcelConstants.XlsxContentType, result.ContentType);
        Assert.True(result.StylingApplied);
        Assert.Null(result.StylingSkipReason);
        Assert.True(stream.CanWrite);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("运单", sheet.Name);
        Assert.Equal("提单号", sheet.Cell(1, 1).GetString());
        Assert.Equal("AWB1", sheet.Cell(2, 1).GetString());
    }

    /// <summary>
    /// 数值格式与日期格式各按自己的属性回读，且值本身仍是数值与日期
    /// </summary>
    /// <remarks>
    /// 简报要求两者分开断言。ClosedXML 0.105.1 没有 <c>Style.DateFormatString</c>，
    /// <see cref="IXLStyle.NumberFormat"/> 与 <see cref="IXLStyle.DateFormat"/> 指向同一份数字格式，
    /// 因此这里按写出路径分别取证：数值列走 <see cref="IXLStyle.NumberFormat"/>，日期列走
    /// <see cref="IXLStyle.DateFormat"/>，并检查两列的格式串没有互串。
    /// </remarks>
    [Fact]
    public async Task 数字格式与日期格式落到单元格()
    {
        var (stream, result) = await ExportWithResultAsync(
            BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }]));
        Assert.True(result.StylingApplied);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);
        var weight = sheet.Cell(2, 2);
        var eta = sheet.Cell(2, 3);

        Assert.Equal("0.00", weight.Style.NumberFormat.Format);
        Assert.True(weight.Value.IsNumber);
        Assert.Equal("yyyy-mm-dd", eta.Style.DateFormat.Format);
        Assert.True(eta.Value.IsDateTime);
        Assert.Equal(new DateTime(2026, 1, 2), eta.Value.GetDateTime());
    }

    /// <summary>
    /// 逐格条件样式只对命中行生效
    /// </summary>
    [Fact]
    public async Task 条件样式对超重行标红加粗()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Value = row => row.AwbNo
            },
            new()
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                NumberFormat = "0.00",
                Value = row => row.Weight,
                CellStyle = row => row is SampleRow typed && typed.Weight > 30
                    ? new ExcelTextStyle("#C00000", null, true)
                    : null
            }
        };

        var stream = await ExportAsync(BuildSpec(
            [
                new SampleRow { AwbNo = "轻货", Weight = 5m, Eta = new DateTime(2026, 1, 2) },
                new SampleRow { AwbNo = "重货", Weight = 40m, Eta = new DateTime(2026, 1, 2) }
            ],
            columns));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("#C00000", HexOf(sheet.Cell(3, 2).Style.Font.FontColor));
        Assert.True(sheet.Cell(3, 2).Style.Font.Bold);
        Assert.Equal("#000000", HexOf(sheet.Cell(2, 2).Style.Font.FontColor));
        Assert.False(sheet.Cell(2, 2).Style.Font.Bold);
    }

    /// <summary>
    /// 表头行冻结与自动筛选范围生效
    /// </summary>
    /// <remarks>
    /// 简报把这条用例命名为「冻结首列」，但它的断言正文与 <see cref="ExcelSheetSpec.FreezeHeader"/> 都是冻结表头行，
    /// 规格里也没有冻结列的开关，故按冻结表头行取证，并额外断言冻结列数保持 0，证明写出侧没有多冻结一列。
    /// ClosedXML 0.105.1 的 <see cref="IXLSheetView"/> 没有 <c>FrozenRows</c> 属性，冻结行数的回读属性是
    /// <see cref="IXLSheetView.SplitRow"/>。
    /// </remarks>
    [Fact]
    public async Task 冻结首列与自动筛选生效()
    {
        var stream = await ExportAsync(BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }]));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal(0, sheet.SheetView.SplitColumn);
        Assert.True(sheet.AutoFilter.IsEnabled);

        var address = sheet.AutoFilter.Range.RangeAddress;
        Assert.Equal(1, address.FirstAddress.RowNumber);
        Assert.Equal(1, address.FirstAddress.ColumnNumber);
        Assert.Equal(2, address.LastAddress.RowNumber);
        Assert.Equal(3, address.LastAddress.ColumnNumber);
    }

    /// <summary>
    /// 自适应列宽的取样不超过约定行数，行集合也不被重复枚举
    /// </summary>
    [Fact]
    public async Task 自动列宽只取样前约定制列数()
    {
        var counter = new CountingRows(1200);
        var spec = BuildSingleColumnSpec(counter, width: null);

        await new ClosedXmlExporter(new XiHanExcelOptions { AutoWidthSampleRows = 500 })
            .ExportAsync(new MemoryStream(), spec, TestContext.Current.CancellationToken);

        // 写数据枚举 1200 次 + 取样至多 500 次 = 1700；若实现忘了限量会达到 2400（1200 + 1200）
        Assert.True(counter.Count <= 1700, $"自动列宽取样枚举了 {counter.Count} 行，超过 1200 + 500 的上限");
    }

    /// <summary>
    /// 排在取样上限之后的长值不参与列宽计算，但仍然完整写出
    /// </summary>
    [Fact]
    public async Task 超出取样上限的行不参与列宽计算()
    {
        var rows = BuildRows(600, LongAwbNo);

        var cappedWidth = await ColumnWidthOfAsync(rows, sampleRows: 500);
        var fullWidth = await ColumnWidthOfAsync(rows, sampleRows: 600);

        Assert.True(cappedWidth < fullWidth, $"取样上限 500 行的列宽 {cappedWidth} 没有窄于全量取样的 {fullWidth}");

        using var workbook = Open(await ExportAsync(
            BuildSingleColumnSpec(rows, width: null), new XiHanExcelOptions { AutoWidthSampleRows = 500 }));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(601, sheet.RowsUsed().Count());
        Assert.Equal(LongAwbNo, sheet.Cell(601, 1).GetString());
    }

    /// <summary>
    /// 带表标题时在最上方写一行并合并前 N 列，表头与排版整体下移一行
    /// </summary>
    [Fact]
    public async Task 有Title时在最上方写合并标题列()
    {
        var stream = await ExportAsync(BuildSpec(
            [new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }],
            title: "运单总表"));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(1, sheet.MergedRanges.Count);
        var merged = sheet.MergedRanges.ToArray()[0].RangeAddress;
        Assert.Equal(1, merged.FirstAddress.RowNumber);
        Assert.Equal(1, merged.FirstAddress.ColumnNumber);
        Assert.Equal(1, merged.LastAddress.RowNumber);
        Assert.Equal(3, merged.LastAddress.ColumnNumber);
        Assert.Equal("运单总表", sheet.Cell(1, 1).GetString());
        Assert.True(sheet.Cell(1, 2).IsMerged());
        Assert.Equal(string.Empty, sheet.Cell(1, 2).GetString());

        // 表头、冻结与筛选都跟着标题行下移一行
        Assert.Equal("提单号", sheet.Cell(2, 1).GetString());
        Assert.Equal("AWB1", sheet.Cell(3, 1).GetString());
        Assert.Equal(2, sheet.SheetView.SplitRow);
        Assert.Equal(2, sheet.AutoFilter.Range.RangeAddress.FirstAddress.RowNumber);
    }

    /// <summary>
    /// 零数据行时仍写表头并交出成功结果，不抛异常
    /// </summary>
    [Fact]
    public async Task 零数据行时仍写表头不抛异常()
    {
        var (stream, result) = await ExportWithResultAsync(BuildSpec([]));

        Assert.True(result.StylingApplied);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("提单号", sheet.Cell(1, 1).GetString());
        Assert.Equal("预计到达", sheet.Cell(1, 3).GetString());
        Assert.True(sheet.Cell(2, 1).IsEmpty());
        Assert.Single(sheet.RowsUsed());
    }

    /// <summary>
    /// 表头默认加粗、上底色并画边框
    /// </summary>
    [Fact]
    public async Task 表头默认加粗上底色并画边框()
    {
        var stream = await ExportAsync(BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }]));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal("#D9E1F2", HexOf(sheet.Cell(1, 1).Style.Fill.BackgroundColor));
        Assert.Equal(XLFillPatternValues.Solid, sheet.Cell(1, 1).Style.Fill.PatternType);
        Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(1, 1).Style.Border.TopBorder);
        Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(1, 2).Style.Border.LeftBorder);
        Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(2, 1).Style.Border.BottomBorder);
    }

    /// <summary>
    /// 关掉排版开关后不加粗、不上底色、不画边框、不冻结、不筛选
    /// </summary>
    [Fact]
    public async Task 关闭表头加粗底色与边框时不套用()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } },
            HeaderBold = false,
            HeaderFill = null,
            Borders = false,
            FreezeHeader = false,
            AutoFilter = false
        };

        var stream = await ExportAsync(spec);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.False(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(XLFillPatternValues.None, sheet.Cell(1, 1).Style.Fill.PatternType);
        Assert.Equal(XLBorderStyleValues.None, sheet.Cell(1, 1).Style.Border.TopBorder);
        Assert.Equal(0, sheet.SheetView.SplitRow);
        Assert.False(sheet.AutoFilter.IsEnabled);
    }

    /// <summary>
    /// 列对齐与换行落到数据格，<see cref="ExcelAlignment.Auto"/> 不显式设置
    /// </summary>
    [Fact]
    public async Task 对齐与换行落到数据格()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Value = row => row.AwbNo
            },
            new()
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                Alignment = ExcelAlignment.Right,
                Wrap = true,
                Value = row => row.Weight
            },
            new()
            {
                Key = nameof(SampleRow.Eta),
                Header = "预计到达",
                Alignment = ExcelAlignment.Center,
                Value = row => row.Eta
            }
        };

        var stream = await ExportAsync(BuildSpec(
            [new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }], columns));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(XLAlignmentHorizontalValues.General, sheet.Cell(2, 1).Style.Alignment.Horizontal);
        Assert.False(sheet.Cell(2, 1).Style.Alignment.WrapText);
        Assert.Equal(XLAlignmentHorizontalValues.Right, sheet.Cell(2, 2).Style.Alignment.Horizontal);
        Assert.True(sheet.Cell(2, 2).Style.Alignment.WrapText);
        Assert.Equal(XLAlignmentHorizontalValues.Center, sheet.Cell(2, 3).Style.Alignment.Horizontal);
    }

    /// <summary>
    /// 给了 <see cref="ExcelColumn.Width"/> 的列走固定列宽，未给的列走自适应
    /// </summary>
    [Fact]
    public async Task 给定列宽走固定宽度()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Width = 24.5,
                Value = row => row.AwbNo
            },
            new()
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                Value = row => row.Weight
            }
        };

        var stream = await ExportAsync(BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m }], columns));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal(24.5, sheet.Column(1).Width);
        Assert.NotEqual(24.5, sheet.Column(2).Width);
    }

    /// <summary>
    /// 列顺序按 <see cref="ExcelSheetSpec.Columns"/> 给出的顺序写出，不按 Order 再排一次
    /// </summary>
    [Fact]
    public async Task 列顺序按给出顺序写出不按Order重排()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.Eta),
                Header = "预计到达",
                Order = 7,
                Value = row => row.Eta
            },
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Order = 1,
                Value = row => row.AwbNo
            },
            new()
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                Order = 3,
                NumberFormat = "0.00",
                Value = row => row.Weight
            }
        };

        var stream = await ExportAsync(BuildSpec(
            [new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }], columns));

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("预计到达", sheet.Cell(1, 1).GetString());
        Assert.Equal("提单号", sheet.Cell(1, 2).GetString());
        Assert.Equal("重量", sheet.Cell(1, 3).GetString());
        Assert.Equal("AWB1", sheet.Cell(2, 2).GetString());
        Assert.Equal(1.5d, sheet.Cell(2, 3).Value.GetNumber());
    }

    /// <summary>
    /// xlsx 表示不了的日期直接抛并点名行列，不交出被改写后的值
    /// </summary>
    [Fact]
    public async Task 早于工作簿日期下限的行直接抛()
    {
        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        // SampleRow.Eta 未赋值时是 0001-01-01，早于 xlsx 的 1900 日期系统能表示的最早时刻 1899-12-30
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m }]), TestContext.Current.CancellationToken));

        Assert.Contains("预计到达", failure.Message, StringComparison.Ordinal);
        Assert.Contains("键", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 非法列宽在写出任何字节之前抛，信息同时点出下界与 xlsx 的 255 上限
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(10000.0)]
    public async Task 非法列宽在写出前抛且不写任何字节(double width)
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Width = width,
                Value = row => row.AwbNo
            }
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([new SampleRow { AwbNo = "AWB1" }], columns), TestContext.Current.CancellationToken));

        // 超过 xlsx 上限 255 的宽度会被工作簿静默夹到 254.29，与负数、无穷同一口径：一律抛，并写明上限
        Assert.Contains("255", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 行集合元素与 <see cref="ExcelSheetSpec.RowType"/> 不符时抛出，信息点名两个类型
    /// </summary>
    [Fact]
    public async Task 异型行抛出并点名期望类型与实际类型()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { "不是行类型" }
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        // 异型行经 ExcelColumn<TRow>.GetValue 只会取到 null，放行就是交出一份「表头齐全、数据全空、还报成功」的档
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, spec, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(SampleRow), failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", failure.Message, StringComparison.Ordinal);
        Assert.Contains("第 1 行", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 行型一致性逐笔判定，null 行没有类型可判、本身按列契约写成空格
    /// </summary>
    [Fact]
    public async Task 类型一致性逐笔判定且null行不判()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var stream = await ExportAsync(new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new SampleRow?[] { null, new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } }
        });

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);
        Assert.True(sheet.Cell(2, 1).IsEmpty());
        Assert.Equal("AWB1", sheet.Cell(3, 1).GetString());

        var mismatched = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new object?[] { null, "不是行类型" }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            new MemoryStream(), mismatched, TestContext.Current.CancellationToken));

        // null 行没有类型可判，判定落在第二个元素上，行号也跟着它
        Assert.Contains("第 2 行", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行型一致性逐笔判定：首笔正确、次笔异型时抛出并点名第二行的行号与两个类型
    /// </summary>
    /// <remarks>
    /// 「只判第一笔」正是本条要拦的形态：第一笔过了就再不判，第二笔的异型行经列的取值方法只会得到 null，
    /// 于是档落成「表头齐全、第二行全空」，结果仍写着 <c>StylingApplied=true</c>。
    /// </remarks>
    [Fact]
    public async Task 第二笔异型时逐笔判定抛出并点名行号()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new object?[]
            {
                new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) },
                "不是行类型"
            }
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, spec, TestContext.Current.CancellationToken));

        Assert.Contains("第 2 行", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow), failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", failure.Message, StringComparison.Ordinal);

        // 逐笔判定仍在存盘之前，抛出那一刻工作簿没有落进流里
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 表头底色不是合法十六进制颜色时在写出前抛
    /// </summary>
    [Fact]
    public async Task 非法表头底色在写出前抛()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } },
            HeaderFill = "#GGGGGG"
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream, spec, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 逐格样式里的非法颜色抛出并点名行列
    /// </summary>
    [Fact]
    public async Task 非法逐格样式颜色抛出并点名行列()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                Value = row => row.Weight,
                CellStyle = row => new ExcelTextStyle("#GGGGGG", null, false)
            }
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([new SampleRow { Weight = 1.5m }], columns), TestContext.Current.CancellationToken));

        Assert.Contains("重量", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 形状像十六进制但用非 ASCII 数字的表头底色，抛框架自己的 ArgumentException 而不是库内异常
    /// </summary>
    /// <remarks>
    /// 全形数字与阿拉伯-印度数字都过得了 <c>ValidateHelper.IsHexColor</c>（其字符判定是 Unicode 感知的
    /// <c>char.IsDigit</c>），但 <c>XLColor.FromHtml</c> 只认 ASCII 位，会抛 <see cref="FormatException"/>。
    /// 这条用例钉的是：这类串对外仍只暴露文档化过的 <see cref="ArgumentException"/>，且原始异常作为内部异常保留。
    /// </remarks>
    [Fact]
    public async Task 非ASCII数字的表头底色抛框架异常并点名参数名()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } },
            HeaderFill = "#１２３"
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream, spec, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.HeaderFill), failure.ParamName);
        Assert.Contains("#１２３", failure.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(failure.InnerException);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 形状像十六进制但用非 ASCII 数字的逐格样式色，抛框架自己的 InvalidOperationException 并点名行列
    /// </summary>
    [Fact]
    public async Task 非ASCII数字的逐格样式色抛框架异常并点名行列()
    {
        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                Value = row => row.Weight,
                CellStyle = row => new ExcelTextStyle("#٣٣٣", null, false)
            }
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([new SampleRow { Weight = 1.5m }], columns), TestContext.Current.CancellationToken));

        Assert.Contains("重量", failure.Message, StringComparison.Ordinal);
        Assert.Contains("#٣٣٣", failure.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(failure.InnerException);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 三位缩写颜色串照常落档，不受解析失败转译的影响
    /// </summary>
    [Fact]
    public async Task 三位缩写颜色串照常落档()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } },
            HeaderFill = "#FFF"
        };

        var stream = await ExportAsync(spec);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("#FFFFFF", HexOf(sheet.Cell(1, 1).Style.Fill.BackgroundColor));
        Assert.Equal(XLFillPatternValues.Solid, sheet.Cell(1, 1).Style.Fill.PatternType);
    }

    /// <summary>
    /// RowType 为 null 的非法声明直接抛，不静默跳过行型判定
    /// </summary>
    [Fact]
    public async Task RowType为null时抛而不是跳过判定()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = null!,
            Columns = Columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } }
        };

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var failure = await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter.ExportAsync(
            stream, spec, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 取消令牌已取消时在写出任何字节之前停止
    /// </summary>
    [Fact]
    public async Task 取消令牌已取消时不写出任何字节()
    {
        using var source = new CancellationTokenSource();

        source.Cancel();

        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }]), source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 写出过程中取消时抛出，不交出带样式已生效的成功结果
    /// </summary>
    [Fact]
    public async Task 写出中途取消时不返回成功结果()
    {
        using var source = new CancellationTokenSource();

        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = RowsThatCancelAfterFirst(source)
        };

        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());
        var stream = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await exporter.ExportAsync(stream, spec, source.Token));

        // 抛出那一刻整张表尚未存盘，流里不会留下半成品
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 最后一笔取值期间才取消时抛出且零字节：逐行检查没有下一轮可拦，靠的是存盘之前那一次
    /// </summary>
    /// <remarks>
    /// xlsx 的落盘只发生在 <c>SaveAs</c>，取消检查排在它之前，所以这一处的取消仍然可以主张输出流零字节；
    /// 这与文字档路径（<c>StreamWriter</c> 边写边缓冲，不保证零字节残留）是两套现实，不合并成一句承诺。
    /// </remarks>
    [Fact]
    public async Task 最后一笔取值期间取消时抛且零字节()
    {
        using var source = new CancellationTokenSource();

        var columns = new ExcelColumn<SampleRow>[]
        {
            new()
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Value = row =>
                {
                    // 取消发生在最后一笔的取值委托里：循环已经取到这一行，不会再有下一轮的逐行检查
                    if (row.AwbNo == "LAST")
                    {
                        source.Cancel();
                    }

                    return row.AwbNo;
                }
            }
        };

        var spec = BuildSpec([new SampleRow { AwbNo = "AWB1" }, new SampleRow { AwbNo = "LAST" }], columns);
        var stream = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(stream, spec, source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 取消只在存盘之后被观察到时，档已经落盘但不回传成功结果
    /// </summary>
    /// <remarks>
    /// 这一条不主张零字节——落盘已经发生，能主张的只有「不交出成功结果」。写出侧不承诺失败原子性：
    /// 要么尚未存盘（零字节），要么整份档完整落盘而没有成功结果被交出，两者按取消被观察到的时机区分。
    /// </remarks>
    [Fact]
    public async Task 存盘之后才观察到的取消不回传成功结果()
    {
        using var source = new CancellationTokenSource();

        var spec = BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) }]);
        var stream = new CancelOnWriteStream(source);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(stream, spec, source.Token));

        Assert.True(stream.Length > 0);   // 存盘已经完成，这一处的取消收不回字节
    }

    /// <summary>
    /// 构造函数拒绝空选项
    /// </summary>
    [Fact]
    public void 构造函数拒绝空选项()
        => Assert.Throws<ArgumentNullException>(() => new ClosedXmlExporter(null!));

    /// <summary>
    /// 自适应列宽的取样上限为负数时在写出前抛
    /// </summary>
    [Fact]
    public async Task 负取样上限在写出前抛()
    {
        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions { AutoWidthSampleRows = -1 });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await exporter.ExportAsync(
            stream, BuildSingleColumnSpec([new SampleRow { AwbNo = "AWB1" }], width: null), TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 写出一张表，返回可供回读的流
    /// </summary>
    private static async Task<MemoryStream> ExportAsync(ExcelSheetSpec spec, XiHanExcelOptions? options = null)
        => (await ExportWithResultAsync(spec, options)).Stream;

    /// <summary>
    /// 写出一张表，同时返回流与导出结果
    /// </summary>
    private static async Task<(MemoryStream Stream, ExcelExportResult Result)> ExportWithResultAsync(ExcelSheetSpec spec, XiHanExcelOptions? options = null)
    {
        var stream = new MemoryStream();

        var result = await new ClosedXmlExporter(options ?? new XiHanExcelOptions())
            .ExportAsync(stream, spec, TestContext.Current.CancellationToken);

        return (stream, result);
    }

    /// <summary>
    /// 把导出后的流回到起点并交回可读的工作簿，同时证明流没有被导出器关闭
    /// </summary>
    private static XLWorkbook Open(MemoryStream stream)
    {
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    /// <summary>
    /// 按指定取样上限写出单列表并回读第一列宽度
    /// </summary>
    private static async Task<double> ColumnWidthOfAsync(IEnumerable<SampleRow> rows, int sampleRows)
    {
        var stream = await ExportAsync(BuildSingleColumnSpec(rows, width: null), new XiHanExcelOptions { AutoWidthSampleRows = sampleRows });

        using var workbook = Open(stream);
        return workbook.Worksheet(1).Column(1).Width;
    }

    /// <summary>
    /// 构造表规格：列缺省时用三列夹具
    /// </summary>
    private static ExcelSheetSpec BuildSpec(SampleRow[] rows, ExcelColumn[]? columns = null, string? title = null)
    {
        return new ExcelSheetSpec
        {
            SheetName = "运单",
            Title = title,
            RowType = typeof(SampleRow),
            Columns = columns ?? Columns,
            Rows = rows
        };
    }

    /// <summary>
    /// 构造只有一列、可指定列宽的表规格，专走固定列宽或自适应宽度路径
    /// </summary>
    private static ExcelSheetSpec BuildSingleColumnSpec(IEnumerable<SampleRow> rows, double? width)
    {
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
                    Width = width,
                    Value = row => row.AwbNo
                }
            ],
            Rows = rows
        };
    }

    /// <summary>
    /// 造若干测试行，最后一行的提单号取 <paramref name="lastAwbNo"/>
    /// </summary>
    private static SampleRow[] BuildRows(int count, string lastAwbNo)
    {
        var rows = new SampleRow[count];

        for (var index = 0; index < count; index++)
        {
            rows[index] = new SampleRow
            {
                AwbNo = index == count - 1 ? lastAwbNo : "AWB",
                Weight = 1.5m,
                Eta = new DateTime(2026, 1, 2)
            };
        }

        return rows;
    }

    /// <summary>
    /// 交出两行的惰性序列，取到第二行之前先把取消令牌取消
    /// </summary>
    private static IEnumerable<SampleRow> RowsThatCancelAfterFirst(CancellationTokenSource source)
    {
        yield return new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) };

        source.Cancel();

        yield return new SampleRow { AwbNo = "AWB2", Weight = 2.5m, Eta = new DateTime(2026, 1, 3) };
    }

    /// <summary>
    /// 把 ClosedXML 的颜色按 RGB 分量写成 <c>#RRGGBB</c>，供与契约里的颜色串比对
    /// </summary>
    private static string HexOf(XLColor color)
        => $"#{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}";
}
