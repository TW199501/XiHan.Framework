// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

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
    /// 只认 <see cref="AnnotatedRow"/> 的一列，用于「列清单与声明的行型别不符」的反例
    /// </summary>
    /// <remarks>
    /// 列键取 <c>anno-name</c> 而不是属性名，为的是让「消息点名列键」这条断言不受别的措辞干扰。
    /// </remarks>
    private static readonly ExcelColumn[] MismatchedColumns =
    [
        new ExcelColumn<AnnotatedRow>
        {
            Key = "anno-name",
            Header = "名称",
            Order = 0,
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

        // SampleRow.Eta 未赋值时是 0001-01-01，远早于本组件接受的最早日期 1900-01-01
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([new SampleRow { AwbNo = "AWB1", Weight = 1.5m }]), TestContext.Current.CancellationToken));

        Assert.Contains("预计到达", failure.Message, StringComparison.Ordinal);
        Assert.Contains("键", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 非有限数值与超长字串按框架异常拒写，理由同下一组用例
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取值域与列宽同一条判据：工作簿的数值格只有有限十进制数，字串格另有 32767 字符的硬上限，越界的值在工作簿里
    /// 根本没有对应形态。判据由两条 xlsx 写出路径共用的一份守卫交出，因此下面几条断的都是框架自己的
    /// <see cref="InvalidOperationException"/> 并点名行列，而不是工作簿自己那句英文异常。
    /// </para>
    /// <para>
    /// 非有限数一律抛出，不做「写成空格里装个字符串」这类改写：把 <c>NaN</c> 变成 <c>"NaN"</c> 会让读回的数值列
    /// 多出字串，把 <c>∞</c> 夹成最大有限数会凭空造出一个数据里不存在的数。字串超长同样抛而不截断——
    /// 截断会丢弃数据，与本组件对超宽输入的一贯取向一致。
    /// </para>
    /// </remarks>
    /// <param name="value">要写进一格的双精度值</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task 非有限数值按框架异常拒写(double value)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        Assert.Contains("取值", failure.Message, StringComparison.Ordinal);
        Assert.Contains("键 Value", failure.Message, StringComparison.Ordinal);
        Assert.Contains("第 1 行", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 单精度非有限数同样被拒
    /// </summary>
    [Fact]
    public async Task 单精度非有限数值同样拒写()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(new MemoryStream(), BuildValueSpec(float.NaN), TestContext.Current.CancellationToken));

        Assert.Contains("取值", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 超过单元格字数上限的字串被拒，并点名上限数值而不是截断
    /// </summary>
    [Fact]
    public async Task 超过单元格字数上限的字串被拒()
    {
        var text = new string('X', ExcelConstants.MaxCellTextLength + 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(new MemoryStream(), BuildValueSpec(text), TestContext.Current.CancellationToken));

        Assert.Contains(ExcelConstants.MaxCellTextLength.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains("截断", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 恰好等于单元格字数上限的字串照写，边界不提前拒绝
    /// </summary>
    [Fact]
    public async Task 恰好等于单元格字数上限的字串照写()
    {
        var stream = await ExportAsync(BuildValueSpec(new string('X', ExcelConstants.MaxCellTextLength)));

        using var workbook = Open(stream);
        Assert.Equal(ExcelConstants.MaxCellTextLength, workbook.Worksheet(1).Cell(2, 1).GetString().Length);
    }

    /// <summary>
    /// 有效数字多于 <see cref="ExcelConstants.MaxExactNumericSignificantDigits"/> 位的数值在全量路径拒写，
    /// 不交出一份被舍短的另一份数
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条界不是「双精度装不装得下」。16 位整数 <c>1234567890123456</c> 在 2^53 以内、双精度装得下，
    /// 但落进数值格时写进档里的是 <c>1.23456789012346E+15</c>，用本组件的导入器读回得到
    /// <c>1234567890123460</c>——数据被改了，导出却回报成功。同一份取值走流式路径读回又是原样，
    /// 「同一份规格走哪条路径拿到哪个数」就成了走哪条的副产品。这一格现在先拒。
    /// </para>
    /// <para>
    /// <c>float</c> 不在条文最初列的四个型别里，是按同一判据扩用进来的：落进数值格的是它展开成
    /// <c>double</c> 后的那份形态，<c>0.1f</c> 展开后已是 17 位，与上面是同一类静默改写。
    /// </para>
    /// <para>
    /// 拒写而不改短是政策：本组件不替呼叫端决定该舍到第几位。消息里的出路只有一条——需要完整精度的值
    /// 由呼叫端转成字符串栏位；不写「让该列取成文本」，因为导出侧不会自动替取值换格位。
    /// </para>
    /// </remarks>
    /// <param name="value">要落进一格的数值取值</param>
    /// <param name="reason">消息里该出现的成因片段（点明实际位数）</param>
    [Theory]
    [MemberData(nameof(OverPreciseNumericCases))]
    public async Task 有效数字多于十五位的数值在全量路径拒写(object value, string reason)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(stream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        Assert.Contains("取值", failure.Message, StringComparison.Ordinal);
        Assert.Contains("键 Value", failure.Message, StringComparison.Ordinal);
        Assert.Contains("第 1 行", failure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelConstants.MaxExactNumericSignificantDigits.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains("转成字符串栏位", failure.Message, StringComparison.Ordinal);

        // 全量路径在存盘之前就被拦下，输出流里不留半成品
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 上限内的数值照写，并由本组件的导入器读回同一份数，判尺不多拒
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判尺只拦「交不回呼叫端给的那个数」的取值：一位小数、恰为 15 位的整数、末尾带零的 15 位整数都照写。
    /// 带负号与带小数点那两条专门挡「按文本长度数位数」的改法——<c>-12345678901234.5</c> 的文本长 17 个字符，
    /// 有效数字却只有 15 位，按长度数会把它误拒。
    /// </para>
    /// <para>
    /// 往返判定用的是本框架的 <see cref="ExcelDataReaderImporter"/>，不用 ClosedXML 读自己写的档：
    /// 工作簿会按自己的形式反算，读回来的数看着和写进去的一样，恰好掩盖档里被改写过这件事。
    /// 导入器对数值格交回 <see cref="double"/>，因此按不变文化文本比读回的数。
    /// </para>
    /// </remarks>
    /// <param name="value">要落进一格的数值取值</param>
    /// <param name="expected">读回来该是的那份数的不变文化文本</param>
    [Theory]
    [MemberData(nameof(WithinPrecisionNumericCases))]
    public async Task 上限内的数值照写并由导入器读回原值(object value, string expected)
    {
        var stream = await ExportAsync(BuildValueSpec(value));

        var back = await ImportValueAsync(stream);

        Assert.IsType<double>(back);
        Assert.Equal(expected, ((double)back!).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 同一份越界取值在两条 xlsx 路径一起被拒，且成因句逐字相同——判尺只有一份的直接验证
    /// </summary>
    /// <remarks>
    /// 两条路径的消息前缀各自点名行位置、表头与列键，这里比的是前缀之后那句成因：它由
    /// <c>ExcelWorkbookWriteGuard</c> 里同一个函数交出。若位数的判定被抄成两份，两条措辞迟早分叉。
    /// </remarks>
    /// <param name="value">要落进一格的数值取值</param>
    /// <param name="reason">消息里该出现的成因片段</param>
    [Theory]
    [MemberData(nameof(OverPreciseNumericCases))]
    public async Task 越界数值在两条xlsx路径一起拒写(object value, string reason)
    {
        var fullStream = new MemoryStream();
        var fullFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(fullStream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        var streamStream = new MemoryStream();
        var streamFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        Assert.Contains(reason, fullFailure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, streamFailure.Message, StringComparison.Ordinal);
        Assert.Equal(CauseOf(fullFailure.Message), CauseOf(streamFailure.Message));
    }

    /// <summary>
    /// 同一份上限内取值在两条 xlsx 路径都照写，且导入器读回同一个数
    /// </summary>
    /// <param name="value">要落进一格的数值取值</param>
    /// <param name="expected">读回来该是的那份数的不变文化文本</param>
    [Theory]
    [MemberData(nameof(WithinPrecisionNumericCases))]
    public async Task 上限内数值在两条xlsx路径给出同一个数(object value, string expected)
    {
        var fullStream = await ExportAsync(BuildValueSpec(value));

        var streamStream = new MemoryStream();
        await new MiniExcelStreamExporter().ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var fullBack = await ImportValueAsync(fullStream);
        var streamBack = await ImportValueAsync(streamStream);

        Assert.Equal(expected, ((double)fullBack!).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(expected, ((double)streamBack!).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 越界数值案例：要落一格的取值，与消息里该出现的成因片段
    /// </summary>
    /// <remarks>
    /// 位数按取值的不变文化文本数（指数、小数点与正负号不参与）。取值一律用字面量写死，
    /// 免得用例自己的算式变成第二处判尺。
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
    /// 指数形态的取值按尾数数位数，指数部分不参与——两条 xlsx 路径都照写并读回同一个数
    /// </summary>
    /// <remarks>
    /// <para>
    /// 双精度的最短往返文本在量级够大或够小时会写成指数形态（<c>9.87654321012345E+18</c>、
    /// <c>1.23456789012345E-10</c>），此时判据只数 <c>E</c> 之前的尾数：指数移动的是小数点，不增加有效数字。
    /// 两档的尾数都恰好是承诺上限 15 位，因此照写；把剥离指数那一段去掉，指数里的数字会被一起数进去
    /// （17 位与 16 位），两档立刻被判成越界——这条用例就是钉住那一段真在起作用，不是死码。
    /// </para>
    /// <para>
    /// 这两档是 <c>double</c>，因此不受整数大小那道约束：呼叫端交出的本来就是双精度取值，
    /// 落进数值格的是同一个双精度，交回的也是它（量级超过 2^53 的那一道只管
    /// <c>long</c>／<c>ulong</c>／<c>decimal</c>，见 <see cref="超过整数上限的数值在两条xlsx路径一起拒写"/>）。
    /// </para>
    /// </remarks>
    /// <param name="value">要落进一格的双精度取值，它的不变文化文本是指数形态</param>
    /// <param name="expected">导入器读回来该是的那份数的不变文化文本</param>
    [Theory]
    [InlineData(9876543210123450000d, "9.87654321012345E+18")]
    [InlineData(1.23456789012345E-10d, "1.23456789012345E-10")]
    public async Task 指数形态的数值按尾数数位数并在两条xlsx路径照写(double value, string expected)
    {
        // 前提自证：这一档的最短往返文本确实是指数形态，否则本用例就测不到剥离指数那一段
        Assert.Contains("E", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var fullStream = await ExportAsync(BuildValueSpec(value));

        var streamStream = new MemoryStream();
        await new MiniExcelStreamExporter().ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var fullBack = await ImportValueAsync(fullStream);
        var streamBack = await ImportValueAsync(streamStream);

        Assert.Equal(expected, ((double)fullBack!).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(expected, ((double)streamBack!).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 绝对值超过数值格能逐个表示的整数上限的取值在两条 xlsx 路径一起被拒，且成因句逐字相同
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一道补的是位数那道的漏。位数按「第一个非零数字到最后一个非零数字」数，整数末尾的一串零不计入，
    /// 于是 <c>9876543210123450000</c> 这个 19 位整数只算 15 位有效数字、在位数那道恰好过关；而它的量级已经
    /// 越过双精度能逐个表示整数的界（2^53 = 9007199254740992，这一段相邻可表示值的间距是 2048），
    /// 落进数值格再读回来是 9876543210123450368——数据被挪了，导出却回报成功。两条路径都是这个结果：
    /// 全量路径把 <c>9.87654321012345E+18</c> 写进档，流式路径把整串数字原样写进档，
    /// 而数值格在档里就是一个双精度数，读回时两边都只能落到最近的那个可表示值上。
    /// <c>decimal</c> 同样在列——它有 28 到 29 位十进制精度，量级越界之后一样交不回原值。
    /// </para>
    /// <para>
    /// 判的是「绝对值超过那道界」而不是「这个取值恰好落在两个可表示值之间」：<c>18000000000000000000</c>
    /// 只有 2 位有效数字、而且恰好是双精度可精确表示的，实测原样往返，但它在界外所以一并拒——
    /// 与位数那道同一取向（<c>1000000000000001</c> 在 2^53 以内、可精确表示，却因 16 位有效数字被拒），
    /// 按一把简单可预测的尺判，宁可多拒不静默改值。
    /// </para>
    /// </remarks>
    /// <param name="value">要落进一格的取值</param>
    [Theory]
    [MemberData(nameof(OverMagnitudeIntegerCases))]
    public async Task 超过整数上限的数值在两条xlsx路径一起拒写(object value)
    {
        var fullStream = new MemoryStream();
        var fullFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(fullStream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        var streamStream = new MemoryStream();
        var streamFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        // 上限值写死在断言里：它是判据的一部分，改动它就是要改动对外承诺，不该由判据自己报出来算数
        Assert.Contains("绝对值超过 xlsx 数值格能逐个表示的整数上限 9007199254740992", fullFailure.Message, StringComparison.Ordinal);
        Assert.Contains("绝对值超过 xlsx 数值格能逐个表示的整数上限 9007199254740992", streamFailure.Message, StringComparison.Ordinal);
        Assert.Contains("键 Value", fullFailure.Message, StringComparison.Ordinal);
        Assert.Equal(CauseOf(fullFailure.Message), CauseOf(streamFailure.Message));
    }

    /// <summary>
    /// 整数上限案例：有效数字都在 15 位以内（位数那道过关），量级却已越过 2^53
    /// </summary>
    /// <remarks>
    /// 取值一律用字面量写死。<c>9876543210123450000</c> 写不成 <c>long</c>（超出 <see cref="long.MaxValue"/>），
    /// 因此正的这一档只能用 <c>ulong</c>，负的对照另取 <c>long</c> 范围内的量级。
    /// </remarks>
    public static IEnumerable<object?[]> OverMagnitudeIntegerCases()
    {
        yield return new object?[] { 9876543210123450000UL };
        yield return new object?[] { 18000000000000000000UL };
        yield return new object?[] { 1234567890123000000L };
        yield return new object?[] { -1234567890123000000L };
        yield return new object?[] { 9876543210123450000m };
        yield return new object?[] { 1234567890123450000m };
        yield return new object?[] { -1234567890123450000m };
    }

    /// <summary>
    /// 量级在上限以内的整数照写，并由导入器读回同一个数——大小那道界不提前拒
    /// </summary>
    /// <remarks>
    /// 取的几档都只有很少的有效数字（位数那道本来就放行），量级分别落在 2^53 之内、紧贴它、以及为负：
    /// 用来钉住新增的那一道没有把界内的取值一起挡掉。<c>9000000000000000</c> 小于 9007199254740992，
    /// 是界内能量级最大的一档；再往上一档（<c>18000000000000000000</c>）已在界外，由上一组用例断拒写。
    /// 恰好等于 2^53 的那一档（<c>9007199254740992</c>）有 16 位有效数字，先在位数那道被拒，
    /// 因此不在这里当正例——两道界各判各的，谁先命中由 <c>DescribeUnwritable</c> 的臂序决定。
    /// </remarks>
    /// <param name="value">要落进一格的取值</param>
    /// <param name="expected">读回来该是的那份数的不变文化文本</param>
    [Theory]
    [MemberData(nameof(WithinIntegerMagnitudeCases))]
    public async Task 整数上限内的取值两条xlsx路径都照写并读回原值(object value, string expected)
    {
        var fullStream = await ExportAsync(BuildValueSpec(value));

        var streamStream = new MemoryStream();
        await new MiniExcelStreamExporter().ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var fullBack = await ImportValueAsync(fullStream);
        var streamBack = await ImportValueAsync(streamStream);

        Assert.Equal(expected, ((double)fullBack!).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(expected, ((double)streamBack!).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 整数上限内案例：要落一格的取值，与导入器读回来该是的那份数的不变文化文本
    /// </summary>
    public static IEnumerable<object?[]> WithinIntegerMagnitudeCases()
    {
        yield return new object?[] { 1234567890123450L, "1234567890123450" };
        yield return new object?[] { 9000000000000000L, "9000000000000000" };
        yield return new object?[] { 8000000000000000L, "8000000000000000" };
        yield return new object?[] { -9000000000000000L, "-9000000000000000" };
        yield return new object?[] { 9000000000000000m, "9000000000000000" };
        yield return new object?[] { -1234567890123450L, "-1234567890123450" };
    }

    /// <summary>
    /// 公式起首的值在全量路径落文字格、逐字读回，且档里不会多出撇号前缀
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是 §6.7-① 在 xlsx 侧的真实表现，不是待补的缺口：本类把字符串交给 <c>XLCellValue</c> 的文本形态落格，
    /// 工作簿按共享字符串存它，档里没有 <c>&lt;f&gt;</c>、<c>HasFormula</c> 为 <c>false</c>，读回的就是同一串文字。
    /// 只有模板路径的 <c>$=</c> 占位会被写出库当成公式解析（另一批处理），xlsx 的两条路径都不解析。
    /// </para>
    /// <para>
    /// 因此这里钉两件事：值逐字不变（不加 <c>'</c> 前缀——加了就是改写业务资料，逐字断言先红）、
    /// 落格形态是文字而不是公式（改成会触发公式的写出方式时，无 <c>&lt;f&gt;</c> 那条红）。
    /// 前导制表符与换行都属于值本身，不因起首像公式就被改写。
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
    public async Task 公式起首的值在全量路径落文字格且逐字读回(string value)
    {
        var stream = await ExportAsync(BuildValueSpec(value));

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
    /// 钉这个差异不是找补：XML 1.0 不允许文本内容里出现裸 <c>U+000D</c>，写出库把它归一成 <c>U+000A</c>，
    /// 因此 <c>"\r=1+1"</c> 读回来是 <c>"\n=1+1"</c>。这一格依旧不落公式、不加撇号前缀，
    /// 改的只是行尾字符本身，与「公式起首值不被改写成公式」是两件事。
    /// 上面那条用例把 <c>\r</c> 排除在外正是为此——逐字往返在这类取值上不成立，硬断会变成假绿。
    /// </remarks>
    [Fact]
    public async Task 值里的回车在档里归一化成换行()
    {
        var stream = await ExportAsync(BuildValueSpec("\r=1+1"));

        Assert.False(HasFormulaElement(stream));

        var back = await ImportValueAsync(stream);

        Assert.Equal("\n=1+1", back);
    }

    /// <summary>
    /// <see cref="DateTime"/> 按钟表时刻落格：不读 <see cref="DateTime.Kind"/>、不做时区换算
    /// </summary>
    /// <remarks>
    /// <para>
    /// xlsx 的日期格只是一个带日期格式的数，没有容纳时区的地方，所以 <c>Utc</c> 与 <c>Local</c> 的实例
    /// 都照它显示的年月日时分秒落格，读回来一律是 <see cref="DateTimeKind.Unspecified"/>。
    /// 两条 xlsx 路径这同一副样子（流式侧见 <c>MiniExcelStreamExporterTests</c>），因此没有数据差异，
    /// 缺的只是把「Kind 会被丢掉」写进文档——<see cref="DateTimeOffset"/> 那边早就披露了偏移量不落格，
    /// <c>DateTime.Kind</c> 此前没人说。
    /// </para>
    /// <para>
    /// 若日后有人在这里加时区换算（把 UTC 折算成本地钟点再落格），这一条按 07:08:09 断的期望会红；
    /// 那类换算该由呼叫端在交值之前自己做。
    /// </para>
    /// </remarks>
    /// <param name="kind">写出的 <see cref="DateTime"/> 带的 Kind</param>
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task DateTime按钟表时刻落格且不读Kind(DateTimeKind kind)
    {
        var value = new DateTime(2024, 5, 6, 7, 8, 9, kind);

        var stream = await ExportAsync(BuildValueSpec(value));

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
    /// 早于日期下限的三种日期型别在两条 xlsx 路径一起被拒，且成因句逐字相同
    /// </summary>
    /// <remarks>
    /// <para>
    /// 日期下限只有一把尺，判据落在两条 xlsx 写出路径共用的那一个函数里，因此「哪一天之前不能写」不由
    /// 落进哪种格子决定。<c>DateOnly</c> 与 <c>DateTimeOffset</c> 在本路径落文本格（值原样留在文字里）、
    /// 在流式路径落日期格，早先因此判得不对称——本路径照能导出、流式路径整段拒；分派器按行数替调用方选路径，
    /// 于是「同一份带早年日期的规格能不能导」成了走哪条的副产品。现在两边一起拒。
    /// </para>
    /// <para>
    /// 1899-12-31 与 1899-12-30 这两档是本次收口的核心：它们在改动前是本路径的正例（落文本格、原样读回），
    /// 而流式路径把它们写成日期序号 0 与 0.2708…，本框架的导入器读回是 1899-12-31 与 1899-12-31 06:30，
    /// 表格软件按 1900 日期系统显示的却是「1900-01-00」这一类不在日历上的日子——同一份档读回来是哪一天，
    /// 取决于谁来读。政策因此收成「不早于 1900-01-01」，不再赌写出库与表格软件各自的宽容度。
    /// 这段取证只写在这里与提交信息里，不写进对外消息。
    /// </para>
    /// </remarks>
    /// <param name="kind">0 用 <c>DateTime</c>，1 用 <c>DateOnly</c>，2 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(0, 1899, 12, 31)]
    [InlineData(0, 1899, 12, 30)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 1899, 12, 31)]
    [InlineData(1, 1899, 12, 30)]
    [InlineData(1, 1899, 12, 29)]
    [InlineData(1, 1500, 1, 1)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 1899, 12, 31)]
    [InlineData(2, 1899, 12, 30)]
    [InlineData(2, 1899, 12, 29)]
    [InlineData(2, 1, 1, 1)]
    public async Task 早于日期下限的三种日期型别在两条xlsx路径一起拒写(int kind, int year, int month, int day)
    {
        var value = DateValue(kind, year, month, day);

        var fullStream = new MemoryStream();
        var fullFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(fullStream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        var streamStream = new MemoryStream();
        var streamFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new MiniExcelStreamExporter()
            .ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken));

        Assert.Contains("日期", fullFailure.Message, StringComparison.Ordinal);
        Assert.Contains("1900-01-01", fullFailure.Message, StringComparison.Ordinal);
        Assert.Contains("键 Value", fullFailure.Message, StringComparison.Ordinal);
        Assert.Equal(0, fullStream.Length);
        Assert.Equal(CauseOf(fullFailure.Message), CauseOf(streamFailure.Message));

        // 「要保住这类日期就走全量路径」这条出路已随两路同拒作废，消息里不许再指点换路径
        Assert.DoesNotContain("全量", fullFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("流式", fullFailure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 下限当日及其后的三种日期型别在两条 xlsx 路径都照写，并由本框架的导入器读回同一天
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>1900-01-01</c> 是恰等正例：两条路径都把它写成日期序号 <c>1</c>，本框架的
    /// <see cref="ExcelDataReaderImporter"/> 读回来还是 <c>1900-01-01</c>，端到端往返闭合——
    /// 这道下限收得再紧也没有把边界当日挡在外面。判定器一律走导入器（<see cref="ImportValueAsync"/>），
    /// 不用工作簿读自己写的档：工作簿会按自己的位移规则反算，读回来的日期看着与写进去的一致，
    /// 恰好掩盖档里被挪过这件事——C3 那条发现正是这样被盖住的。
    /// </para>
    /// <para>
    /// 格位差异照旧存在，只是不再影响能不能导：<c>DateTime</c> 两条路径都落日期格，导入器交回
    /// <see cref="DateTime"/>，逐值可比；<c>DateOnly</c> 与 <c>DateTimeOffset</c> 在本路径落文本格、
    /// 导入器交回 <see cref="string"/>，文本的排法是库按不变文化格式排的（形如 <c>01/01/1900</c>），
    /// 改排法不算改契约，因此只断它是文本、带该年份；同两类型在流式路径落日期格，交回
    /// <see cref="DateTime"/>，逐值可比。
    /// </para>
    /// </remarks>
    /// <param name="kind">0 用 <c>DateTime</c>，1 用 <c>DateOnly</c>，2 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(0, 1900, 1, 1)]
    [InlineData(0, 2026, 1, 2)]
    [InlineData(1, 1900, 1, 1)]
    [InlineData(1, 2026, 1, 2)]
    [InlineData(2, 1900, 1, 1)]
    [InlineData(2, 2026, 1, 2)]
    public async Task 下限当日及其后的三种日期型别在两条xlsx路径都照写(int kind, int year, int month, int day)
    {
        var value = DateValue(kind, year, month, day);

        var fullStream = await ExportAsync(BuildValueSpec(value));

        var streamStream = new MemoryStream();
        await new MiniExcelStreamExporter().ExportAsync(streamStream, BuildValueSpec(value), TestContext.Current.CancellationToken);

        var fullBack = await ImportValueAsync(fullStream);
        var streamBack = await ImportValueAsync(streamStream);

        // 流式路径三种型别都落日期格，交回的是钟表时刻
        var expectedInStream = new DateTime(year, month, day, kind == 2 ? 6 : 0, kind == 2 ? 30 : 0, 0);

        Assert.IsType<DateTime>(streamBack);
        Assert.Equal(expectedInStream, (DateTime)streamBack!);

        if (kind == 0)
        {
            Assert.IsType<DateTime>(fullBack);
            Assert.Equal(new DateTime(year, month, day), (DateTime)fullBack!);
        }
        else
        {
            Assert.IsType<string>(fullBack);
            Assert.Contains(year.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0'), (string)fullBack!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 下限当日及其后的 DateOnly 与 DateTimeOffset 在本路径落文本格
    /// </summary>
    /// <remarks>
    /// 钉的是落格形态：工作簿没有这两个型别的格位，本类把它们交给 <c>XLCellValue</c> 的文本形态。
    /// 这道形态差异在日期下限收口之后仍然存在，只是不再决定「能不能导」——早于下限的取值两条路径一起拒，
    /// 见 <see cref="早于日期下限的三种日期型别在两条xlsx路径一起拒写"/>；读回的值由
    /// <see cref="下限当日及其后的三种日期型别在两条xlsx路径都照写"/> 用导入器断。
    /// 因此这里只断格位型别，不比对整串文本（那是库按不变文化格式排出来的，改排法不算改契约）。
    /// </remarks>
    /// <param name="kind">1 用 <c>DateOnly</c>，2 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    [Theory]
    [InlineData(1, 1900, 1, 1)]
    [InlineData(1, 2026, 1, 2)]
    [InlineData(2, 1900, 1, 1)]
    [InlineData(2, 2026, 1, 2)]
    public async Task 下限当日及其后的DateOnly与DateTimeOffset在本路径落文本格(int kind, int year, int month, int day)
    {
        var stream = await ExportAsync(BuildValueSpec(DateValue(kind, year, month, day)));

        using var workbook = Open(stream);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.Equal(XLDataType.Text, cell.DataType);
    }

    /// <summary>
    /// 按型别造一格日期取值：<c>DateTime</c> 取当日零点，<c>DateOnly</c> 不带时刻，
    /// <c>DateTimeOffset</c> 带 <c>06:30</c> 与 <c>+08:00</c>
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeOffset"/> 的构造本身不接受「公元 1 年再加 +08:00」这种组合（换算到 UTC 会掉出可表示范围），
    /// 所以早到那个量级的取值只能按零偏移量造——要验的是早于下限的日期一律被拒，与偏移量取值无关。
    /// </remarks>
    /// <param name="kind">0 用 <c>DateTime</c>，1 用 <c>DateOnly</c>，2 用带偏移量的 <c>DateTimeOffset</c></param>
    /// <param name="year">年份</param>
    /// <param name="month">月份</param>
    /// <param name="day">日</param>
    private static object DateValue(int kind, int year, int month, int day) => kind switch
    {
        0 => new DateTime(year, month, day),
        1 => new DateOnly(year, month, day),
        _ => new DateTimeOffset(year, month, day, 6, 30, 0, year == 1 ? TimeSpan.Zero : TimeSpan.FromHours(8))
    };

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
    /// 列约定的行型别与 <see cref="ExcelSheetSpec.RowType"/> 不符时，声明级预检在取出第一行之前就把整份规格拒掉
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两种坏声明都要拦：列清单来自另一个行类型（复制粘贴留下的 <c>ExcelColumn&lt;AnnotatedRow&gt;</c> 配
    /// <c>RowType = typeof(SampleRow)</c>），以及把 <c>RowType</c> 写成 <see cref="object"/> 来「放宽」。后者尤其
    /// 危险——<c>object</c> 看着像什么都收，实际让每一格取值都落到「行类型不符返回 null」，交回的档表头齐全、
    /// 数据全空、结果还写着 <c>StylingApplied=true</c>。
    /// </para>
    /// <para>
    /// 三条断言各挡一种改法：<c>stream.Length == 0</c> 挡「照样写」，<c>rows.Count == 0</c> 挡「判据挪进逐行路径」
    /// （那里要先枚举才看得见），消息里点名列键与两个型别全名挡「抛了但不说坏在哪一列」。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 列行型别与声明不符时在取出第一行之前就被拒()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var mismatchedRows = new CountingRows(3);
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
        Assert.Equal(0, mismatchedRows.Count);

        var widenedRows = new CountingRows(3);
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
        Assert.Contains(typeof(SampleRow).FullName!, widened.Message, StringComparison.Ordinal);
        Assert.Contains("System.Object", widened.Message, StringComparison.Ordinal);
        Assert.Equal(0, widenedStream.Length);
        Assert.Equal(0, widenedRows.Count);
    }

    /// <summary>
    /// 零行的坏声明同样在预检被拒：判据若放进逐行路径，这里会静默交出一份只有表头的档
    /// </summary>
    [Fact]
    public async Task 零行规格的列行型别不符同样在预检被拒()
    {
        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream,
            new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = typeof(SampleRow),
                Columns = MismatchedColumns,
                Rows = Array.Empty<SampleRow>()
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// <c>RowType</c> 是列约定行型别的派生型别时照常导出，预检不得多拒
    /// </summary>
    /// <remarks>
    /// 派生行类型交出的是列认识的那些属性，逐笔判定本来就放行；预检若写成「两个型别必须相等」，这条会先红。
    /// </remarks>
    [Fact]
    public async Task RowType是列行型别的派生型别时照常导出()
    {
        var stream = await ExportAsync(new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(DerivedSampleRow),
            Columns = Columns,
            Rows = new[] { new DerivedSampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) } }
        });

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("提单号", sheet.Cell(1, 1).GetString());
        Assert.Equal("AWB1", sheet.Cell(2, 1).GetString());
        Assert.Equal(1.5m, sheet.Cell(2, 2).GetValue<decimal>());
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
    /// 无标题行时，表头行加数据行正好占满上限的那一份照常写出
    /// </summary>
    /// <remarks>
    /// 上限注入成 3：表头占第 1 行，两行数据占第 2、3 行，正好触线。这条与
    /// <see cref="全量路径超过行数上限时抛且不留下任何字节"/> 是一对——把判定里的 <c>&gt;</c> 写成 <c>&gt;=</c>，
    /// 这条会先红（提前一行拒），那条仍然绿，因此两条都得留着。
    /// 真实上限是一百多万行，逐行写到触线要产出十几 MB 的档并整份建在内存里，因此边界断言改用注入的小上限，
    /// 读点与正式入口是同一个字段。
    /// </remarks>
    [Fact]
    public async Task 全量路径行数恰等上限时照常写出()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions(), 3);
        var stream = new MemoryStream();

        var result = await exporter.ExportAsync(
            stream, BuildRowLimitSpec(Rows(2), title: null), TestContext.Current.CancellationToken);

        Assert.True(result.StylingApplied);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("提单号", sheet.Cell(1, 1).GetString());
        Assert.Equal("AWB0", sheet.Cell(2, 1).GetString());
        Assert.Equal("AWB1", sheet.Cell(3, 1).GetString());
        Assert.True(sheet.Cell(4, 1).IsEmpty());
    }

    /// <summary>
    /// 有标题行时，标题行也计入行数：标题行加表头行加数据行正好占满上限的那一份照常写出
    /// </summary>
    /// <remarks>
    /// 上限注入成 4：标题占第 1 行、表头占第 2 行、两行数据占第 3、4 行，正好触线。
    /// </remarks>
    [Fact]
    public async Task 全量路径含标题行时行数恰等上限照常写出()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions(), 4);
        var stream = new MemoryStream();

        await exporter.ExportAsync(
            stream, BuildRowLimitSpec(Rows(2), title: "运单明细"), TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("运单明细", sheet.Cell(1, 1).GetString());
        Assert.Equal("提单号", sheet.Cell(2, 1).GetString());
        Assert.Equal("AWB1", sheet.Cell(4, 1).GetString());
        Assert.True(sheet.Cell(5, 1).IsEmpty());
    }

    /// <summary>
    /// 超过行数上限时抛出框架异常，输出流零字节，且判定没有把行集合物化
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上限注入成 3，行集合给 10 行：表头占第 1 行，第 3 行数据要落到第 4 行即触线。四条断言各挡一种改法——
    /// 异常型别挡「把库的 <see cref="ArgumentOutOfRangeException"/> 原样交出去」；<c>stream.Length == 0</c> 挡
    /// 「照样存盘」；<c>rows.Count == 3</c> 挡「为计数先 <c>ToList()</c> 或回头再枚举一遍」（物化会数到 10）；
    /// 消息里点出上限值与两条出路挡「抛了但不说上限是多少、也不说该怎么办」。
    /// </para>
    /// <para>
    /// 全量路径整份档先在内存里建好、<c>SaveAs</c> 只落盘一次，因此这里能主张零字节；流式路径边写边吐，
    /// 主张的是「不回报成功结果」，两边的失败语义不同，见流式导出测试。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 全量路径超过行数上限时抛且不留下任何字节()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions(), 3);
        var rows = new CountingRows(10);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildRowLimitSpec(rows, title: null), TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
        Assert.Equal(3, rows.Count);
        Assert.Contains("3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("分成多张表", failure.Message, StringComparison.Ordinal);
        Assert.Contains("文字档", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    /// <summary>
    /// 标题行计入行数：上限 3 时「标题 + 表头 + 2 行数据」共 4 行，已经触线
    /// </summary>
    /// <remarks>
    /// 这条专门挡「计数漏掉标题行」的改法：把 <c>headerRowNumber + rowIndex</c> 写成 <c>1 + rowIndex</c>，
    /// 这一份只有 3 行会被数到，判定放行，本条即红——而写出来的档里第 4 行确实有内容，超了声明的上限。
    /// </remarks>
    [Fact]
    public async Task 全量路径标题行计入行数上限()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions(), 3);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildRowLimitSpec(Rows(2), title: "运单明细"), TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
        Assert.Contains("4", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 多表路径按每张工作表各自计数，两张各占上限六成的表能同时写进一个工作簿
    /// </summary>
    /// <remarks>
    /// 上限注入成 10，两张表各 4 行数据（各占 5 行）：合计 10 行，若按整簿累计就会正好触线、再多一行即抛。
    /// 这里两份都放行，证明累计的是每张表自己的行号而不是整簿的行数。
    /// </remarks>
    [Fact]
    public async Task 多表路径每张工作表各自计数不做整簿累计()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions(), 10);
        var stream = new MemoryStream();

        var result = await exporter.ExportAllAsync(
            stream,
            [
                BuildRowLimitSpec(Rows(4), title: null, sheetName: "第一张"),
                BuildRowLimitSpec(Rows(4), title: null, sheetName: "第二张")
            ],
            TestContext.Current.CancellationToken);

        Assert.True(result.StylingApplied);

        using var workbook = Open(stream);

        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Equal("AWB3", workbook.Worksheet("第一张").Cell(5, 1).GetString());
        Assert.Equal("AWB3", workbook.Worksheet("第二张").Cell(5, 1).GetString());
    }

    /// <summary>
    /// 多表路径里任何一张表触线，整份请求即抛且输出流零字节
    /// </summary>
    /// <remarks>
    /// 第一张表 4 行数据放行，第二张 10 行数据要落到第 11 行、超过注入的上限 10。整份档先在内存里建好再落盘，
    /// 所以前面那张表已经写进工作簿的部分随异常一起被丢弃，输出流不会留下半个字节。
    /// </remarks>
    [Fact]
    public async Task 多表路径某张表触线时整份抛且零字节()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions(), 10);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAllAsync(
            stream,
            [
                BuildRowLimitSpec(Rows(4), title: null, sheetName: "第一张"),
                BuildRowLimitSpec(Rows(10), title: null, sheetName: "第二张")
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
        Assert.Contains("第二张", failure.Message, StringComparison.Ordinal);
        Assert.Contains("10", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表头长过单元格上限时，在写出第一格之前就被拒：零字节、行集合一次都没被枚举
    /// </summary>
    /// <remarks>
    /// <para>
    /// 表头落的也是单元格，与数据格共用 <see cref="ExcelConstants.MaxCellTextLength"/> 那道界。此前这一条没有预检，
    /// 超长表头一路走到赋值那一步，交回的是工作簿自己那句英文异常
    /// （<see cref="ArgumentOutOfRangeException"/>，<c>ParamName</c> 为 <c>text</c>），与本文件教调用方
    /// 「按异常类型与 <see cref="ArgumentException.ParamName"/> 分流」的口径冲突。
    /// </para>
    /// <para>
    /// 三条断言各挡一种改法：异常型别与 <c>ParamName</c> 挡「照旧把库的异常交出去」；<c>stream.Length == 0</c>
    /// 挡「先写一半再抛」；<c>rows.Count == 0</c> 挡「把判定挪进逐行路径」——那里要先取到一行才看得见，
    /// 与 §4 的行型预检同层，声明级的问题一个行元素都不该取。消息里不嵌入表头原文，
    /// 否则四万个字符会整段进异常消息。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 表头超过单元格上限时在写出前被拒()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());
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
    /// 表头刚过上限一个字符（32,768）同样被拒：边界是「超过即拒」，不是「超过一个档位才拒」
    /// </summary>
    [Fact]
    public async Task 表头刚过单元格上限一个字符时同样在写出前被拒()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream,
            BuildTextLimitSpec(new string('A', ExcelConstants.MaxCellTextLength + 1), title: null, rows: Rows(1)),
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Header), failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 标题长过单元格上限时，在写出第一格之前就被拒：零字节、行集合一次都没被枚举
    /// </summary>
    /// <remarks>
    /// 标题行落的也是一格（跨列合并后只有左上角有值，那一格照样受单元格上限约束）。
    /// <c>ParamName</c> 是 <c>Title</c>，与表头那条区分开——调用方按它就知道该改哪一项。
    /// </remarks>
    [Fact]
    public async Task 标题超过单元格上限时在写出前被拒()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());
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
    public async Task 标题刚过单元格上限一个字符时同样在写出前被拒()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
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
    /// 正例挡的是「把边界写成 <c>&gt;=</c> 提前一个字符拒」。读回判定走本组件的导入器
    /// （<see cref="ExcelDataReaderImporter"/>），不用工作簿读自己写的档：导入器按 <c>HasHeader</c> 把第一行
    /// 当列名交回，因此键名本身就是档里真实落下的那串表头文字，长度与内容都能直接比。
    /// </remarks>
    [Fact]
    public async Task 表头恰等单元格上限时照常写出并可读回()
    {
        var header = new string('A', ExcelConstants.MaxCellTextLength);
        var stream = new MemoryStream();

        await new ClosedXmlExporter(new XiHanExcelOptions()).ExportAsync(
            stream, BuildTextLimitSpec(header, title: null, rows: Rows(1)), TestContext.Current.CancellationToken);

        var imported = await ImportRowsAsync(stream, hasHeader: true);

        var row = Assert.Single(imported);

        Assert.True(row.Values.ContainsKey(header));
        Assert.Equal(header.Length, row.Values.Keys.Single().Length);
        Assert.Equal("AWB0", row.Values[header]);
    }

    /// <summary>
    /// 标题恰等单元格上限（32,767）时照常写出，读回来的标题逐字不变
    /// </summary>
    /// <remarks>
    /// 按 <c>HasHeader = false</c> 读回，三行依次是标题行、表头行与那一行数据，因此能同时钉住
    /// 「标题落在第 1 行」与「标题文字没有被截断」。
    /// </remarks>
    [Fact]
    public async Task 标题恰等单元格上限时照常写出并可读回()
    {
        var title = new string('B', ExcelConstants.MaxCellTextLength);
        var stream = new MemoryStream();

        await new ClosedXmlExporter(new XiHanExcelOptions()).ExportAsync(
            stream, BuildTextLimitSpec("提单号", title: title, rows: Rows(1)), TestContext.Current.CancellationToken);

        var imported = await ImportRowsAsync(stream, hasHeader: false);

        Assert.Equal(3, imported.Count);
        Assert.Equal(title, imported[0].Values.Values.Single());
        Assert.Equal("提单号", imported[1].Values.Values.Single());
        Assert.Equal("AWB0", imported[2].Values.Values.Single());
    }

    /// <summary>
    /// 多表路径里某张表的表头超长时，整份请求在写出前被拒且输出流零字节
    /// </summary>
    /// <remarks>
    /// 表头预检落在单表与多表共用的那个逐表方法里，因此清单里排在后面的表也一样先判后写；
    /// 整份档建好才落盘一次，前面那些表已经写进内存工作簿的部分随异常一起被丢弃。
    /// </remarks>
    [Fact]
    public async Task 多表路径某张表表头超长时整份在写出前被拒()
    {
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());
        var rows = new CountingRows(1);
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAllAsync(
            stream,
            [
                BuildTextLimitSpec("提单号", title: null, rows: Rows(1), sheetName: "第一张"),
                BuildTextLimitSpec(new string('A', 40_000), title: null, rows: rows, sheetName: "第二张")
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Header), failure.ParamName);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, rows.Count);
    }

    /// <summary>
    /// 构造只有一列、可指定表头与标题的表规格，专走表头与标题的长度预检
    /// </summary>
    /// <remarks>
    /// 列宽给定值，避免自适应列宽去量那串几万字符的表头——本组用例要判的是长度预检，不是列宽。
    /// </remarks>
    private static ExcelSheetSpec BuildTextLimitSpec(
        string header,
        string? title,
        System.Collections.IEnumerable rows,
        string sheetName = "运单")
        => new()
        {
            SheetName = sheetName,
            Title = title,
            RowType = typeof(SampleRow),
            Columns =
            [
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.AwbNo),
                    Header = header,
                    Width = 20,
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
    /// <remarks>
    /// 往返断言的判定器一律走这里，不用 <see cref="XLWorkbook"/> 读自己写的档：工作簿会按自己的形式反算，
    /// 读回来的东西看着与写进去的一致，恰好掩盖档里被截断或改写过这件事。
    /// </remarks>
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
    /// 造 <paramref name="count"/> 行只有提单号有值的测试行
    /// </summary>
    private static SampleRow[] Rows(int count)
    {
        var rows = new SampleRow[count];

        for (var index = 0; index < count; index++)
        {
            rows[index] = new SampleRow { AwbNo = $"AWB{index}", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) };
        }

        return rows;
    }

    /// <summary>
    /// 构造只有一列、可指定标题行与表名的表规格，专走行数上限判定
    /// </summary>
    /// <remarks>
    /// 只留一列是为了让「第几行有内容」这件事在回读时不掺别的列；列宽给定值，避免自适应宽度去动别的行。
    /// </remarks>
    private static ExcelSheetSpec BuildRowLimitSpec(System.Collections.IEnumerable rows, string? title, string sheetName = "运单")
        => new()
        {
            SheetName = sheetName,
            Title = title,
            RowType = typeof(SampleRow),
            Columns =
            [
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.AwbNo),
                    Header = "提单号",
                    Width = 20,
                    Value = row => row.AwbNo
                }
            ],
            Rows = rows
        };

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
    /// 取出取值域失败消息里「成因」那一段（前缀点名行位置、表头与列键，两条路径各自的措辞）
    /// </summary>
    /// <param name="message">框架异常的完整消息</param>
    /// <remarks>
    /// 分界取第一处「）」加句号之后的文字：那一段由 <c>ExcelWorkbookWriteGuard</c> 的判定函数交出，
    /// 两条 xlsx 路径共用同一份，因此这一段是「判尺只有一份」的直接可观察物。
    /// </remarks>
    private static string CauseOf(string message)
    {
        const string boundaryMarker = "）。";

        var boundary = message.IndexOf(boundaryMarker, StringComparison.Ordinal);

        if (boundary < 0)
        {
            throw new InvalidOperationException($"消息里没有成因与前缀的分界：{message}");
        }

        return message[(boundary + boundaryMarker.Length)..];
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
    /// 构造只有「取值」一列、把指定值原样交出的表规格，专走单元格取值域判定
    /// </summary>
    private static ExcelSheetSpec BuildValueSpec(object value)
    {
        return new ExcelSheetSpec
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
