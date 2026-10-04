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
/// ClosedXML 多工作表导出测试
/// </summary>
/// <remarks>
/// <para>
/// 断言一律走 <c>new XLWorkbook(stream)</c> 回读，与单表测试同一口径：多表路径的全部价值就在于每张表都真的落进档里，
/// 这只有回读能证明。
/// </para>
/// <para>
/// 表名守卫的判据不凭空加严，逐条对齐 ClosedXML 0.105.1 的现实（取证见
/// <c>.superpowers/sdd/2026-10-04-excel/t7-probe-sheetname-charset.txt</c> 与
/// <c>t7-probe-sheetname-parity.txt</c>）：非法字符集、31 的长度量纲（按 UTF-16 代码单元）、首尾撇号、
/// 判重的大小写口径（<see cref="StringComparer.OrdinalIgnoreCase"/>）都与之同一套，
/// 因此这里既断「库拒的我们拒」，也断「库肯收的我们不误杀」。
/// </para>
/// <para>
/// 每条测试各自新建 <see cref="MemoryStream" />，不共用类字段：写出侧会把字节留在流里，共用会让前后用例互相污染。
/// </para>
/// </remarks>
public class ClosedXmlMultiSheetTests
{
    /// <summary>
    /// 31 个字符的表名，正好卡在长度上限上
    /// </summary>
    private const string MaximumLengthName = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>
    /// 运单表的两列
    /// </summary>
    private static readonly ExcelColumn[] WaybillColumns =
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
        }
    ];

    /// <summary>
    /// 汇总表的两列：与运单表列数相同但键、表头、格式都不同，用来证明列清单按表各自生效
    /// </summary>
    private static readonly ExcelColumn[] SummaryColumns =
    [
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Eta),
            Header = "预计到达",
            Order = 0,
            NumberFormat = "yyyy-mm-dd",
            Value = row => row.Eta
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.AwbNo),
            Header = "单号",
            Order = 1,
            Width = 18,
            Value = row => row.AwbNo
        }
    ];

    /// <summary>
    /// 两张表可以各有自己的列清单，且都能按各自表头与数据回读
    /// </summary>
    [Fact]
    public async Task 两张表列可各自不同且都能回读到()
    {
        var stream = new MemoryStream();
        ExcelSheetSpec[] specs =
        [
            Spec("运单", WaybillColumns, [Row("AWB1", 1.5m), Row("AWB2", 2.5m)]),
            Spec("汇总", SummaryColumns, [Row("AWB1", 1.5m)])
        ];

        var result = await new ClosedXmlExporter(new XiHanExcelOptions()).ExportAllAsync(
            stream, specs, TestContext.Current.CancellationToken);

        Assert.Equal(ExcelFormat.Xlsx, result.Format);
        Assert.Equal(".xlsx", result.FileExtension);
        Assert.Equal(ExcelConstants.XlsxContentType, result.ContentType);
        Assert.True(result.StylingApplied);
        Assert.True(stream.CanWrite);

        using var workbook = Open(stream);
        Assert.Equal(2, workbook.Worksheets.Count);

        var waybill = workbook.Worksheet(1);
        Assert.Equal("运单", waybill.Name);
        Assert.Equal("提单号", waybill.Cell(1, 1).GetString());
        Assert.Equal("重量", waybill.Cell(1, 2).GetString());
        Assert.Equal("AWB2", waybill.Cell(3, 1).GetString());
        Assert.Equal("0.00", waybill.Cell(2, 2).Style.NumberFormat.Format);

        var summary = workbook.Worksheet(2);
        Assert.Equal("汇总", summary.Name);
        Assert.Equal("预计到达", summary.Cell(1, 1).GetString());
        Assert.Equal("单号", summary.Cell(1, 2).GetString());
        Assert.Equal(new DateTime(2026, 1, 2), summary.Cell(2, 1).GetDateTime());
        Assert.Equal("AWB1", summary.Cell(2, 2).GetString());
        Assert.Equal(18d, summary.Column(2).Width);

        // 列的格式串按各自表的列清单落地：汇总表的日期列走 yyyy-mm-dd，运单表的 0.00 不跨表渗过来
        Assert.Equal("yyyy-mm-dd", summary.Cell(2, 1).Style.DateFormat.Format);
        Assert.Equal("0.00", waybill.Cell(2, 2).Style.NumberFormat.Format);
    }

    /// <summary>
    /// 表的写出顺序就是清单顺序，不受名字先后影响
    /// </summary>
    [Fact]
    public async Task 三张表按清单顺序落位()
    {
        var stream = new MemoryStream();
        ExcelSheetSpec[] specs = [Spec("丙表", WaybillColumns, []), Spec("甲表", WaybillColumns, []), Spec("乙表", WaybillColumns, [])];

        await new ClosedXmlExporter(new XiHanExcelOptions()).ExportAllAsync(stream, specs, TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.Equal("丙表", workbook.Worksheet(1).Name);
        Assert.Equal("甲表", workbook.Worksheet(2).Name);
        Assert.Equal("乙表", workbook.Worksheet(3).Name);
    }

    /// <summary>
    /// 每张表按自己声明的行类型取值，两种行类型并存互不干扰
    /// </summary>
    [Fact]
    public async Task 每张表按各自的RowType取值()
    {
        var stream = new MemoryStream();
        var specs = new ExcelSheetSpec[]
        {
            Spec("运单", WaybillColumns, [Row("AWB1", 1.5m)]),
            new()
            {
                SheetName = "清点",
                RowType = typeof(AnnotatedRow),
                Columns =
                [
                    new ExcelColumn<AnnotatedRow>
                    {
                        Key = nameof(AnnotatedRow.Name),
                        Header = "名称",
                        Value = row => row.Name
                    },
                    new ExcelColumn<AnnotatedRow>
                    {
                        Key = nameof(AnnotatedRow.Quantity),
                        Header = "数量",
                        Value = row => row.Quantity
                    }
                ],
                Rows = new[] { new AnnotatedRow { Name = "甲件", Quantity = 7 } }
            }
        };

        await new ClosedXmlExporter(new XiHanExcelOptions()).ExportAllAsync(stream, specs, TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        Assert.Equal("AWB1", workbook.Worksheet(1).Cell(2, 1).GetString());
        Assert.Equal("甲件", workbook.Worksheet(2).Cell(2, 1).GetString());
        Assert.Equal("7", workbook.Worksheet(2).Cell(2, 2).GetString());
    }

    /// <summary>
    /// 某一张表的行型不符时沿用单表的行型守卫抛出，消息点名行号与期望／实际两个类型
    /// </summary>
    /// <remarks>
    /// 多表路径不另写一份行型守卫（消息因此只到「第几行」，不到「第几张表」），这里断言的是它没有被绕过：
    /// 坏行在第二张表里，异常照旧来自同一份守卫，且整份档零字节。
    /// </remarks>
    [Fact]
    public async Task 其中一张表的行型不符时抛出且不写出任何字节()
    {
        var stream = new MemoryStream();
        var specs = new[]
        {
            Spec("运单", WaybillColumns, [Row("AWB1", 1.5m)]),
            new ExcelSheetSpec
            {
                SheetName = "坏表",
                RowType = typeof(SampleRow),
                Columns = WaybillColumns,
                Rows = new[] { "不是行类型" }
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, specs, TestContext.Current.CancellationToken));

        Assert.Contains("第 1 行", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow), failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 单表入口与多表入口对同一份规格交出完全一致的工作表，证明两条路径共用同一套写出实现
    /// </summary>
    /// <remarks>
    /// 这是「把单表并进多表同一 <c>WriteSheet</c> 循环」的差异探测：不是列一批两边同结论的样本，
    /// 而是把同一条规格分别经两个入口写出，逐格、逐列宽、逐样式比对。规格挑边界形状：带标题、零行、
    /// 固定列宽、条件样式、关掉边框/冻结/筛选。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 单表与多表对同一规格交出一致的工作表(int rowCount)
    {
        var spec = BuildEquivalentSpec(rowCount);

        var singleStream = new MemoryStream();
        var singleResult = await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(singleStream, spec, TestContext.Current.CancellationToken);

        var multiStream = new MemoryStream();
        var multiResult = await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(multiStream, [spec], TestContext.Current.CancellationToken);

        Assert.Equal(singleResult, multiResult);

        using var singleWorkbook = Open(singleStream);
        using var multiWorkbook = Open(multiStream);
        Assert.Equal(1, multiWorkbook.Worksheets.Count);

        var single = singleWorkbook.Worksheet(1);
        var multi = multiWorkbook.Worksheet(1);

        Assert.Equal(single.Name, multi.Name);

        var lastRow = Math.Max(single.RangeUsed()?.LastRow().RowNumber() ?? 1, multi.RangeUsed()?.LastRow().RowNumber() ?? 1);
        var lastColumn = Math.Max(single.RangeUsed()?.LastColumn().ColumnNumber() ?? 1, multi.RangeUsed()?.LastColumn().ColumnNumber() ?? 1);

        for (var row = 1; row <= lastRow; row++)
        {
            for (var column = 1; column <= lastColumn; column++)
            {
                var left = single.Cell(row, column);
                var right = multi.Cell(row, column);

                Assert.Equal(left.GetString(), right.GetString());
                Assert.Equal(left.DataType, right.DataType);
                Assert.Equal(left.Style.Font.Bold, right.Style.Font.Bold);
                Assert.Equal(HexOf(left.Style.Fill.BackgroundColor), HexOf(right.Style.Fill.BackgroundColor));
                Assert.Equal(left.Style.Border.TopBorder, right.Style.Border.TopBorder);
                Assert.Equal(left.IsMerged(), right.IsMerged());
            }
        }

        for (var column = 1; column <= lastColumn; column++)
        {
            Assert.Equal(single.Column(column).Width, multi.Column(column).Width);
        }

        Assert.Equal(single.SheetView.SplitRow, multi.SheetView.SplitRow);
        Assert.Equal(single.AutoFilter.IsEnabled, multi.AutoFilter.IsEnabled);
    }

    /// <summary>
    /// 空表清单抛出明确的 ArgumentException，不把库的 InvalidOperationException 交出去
    /// </summary>
    /// <remarks>
    /// 实测零工作表的工作簿存盘会得到 <c>InvalidOperationException("Workbooks need at least one worksheet.")</c>
    /// （取证见 <c>t7-probe-zerosheet.txt</c>）。清单为空不是「写出一份空档」，也不该让库的英文异常成为契约，
    /// 因此在预检层就抛。
    /// </remarks>
    [Fact]
    public async Task 空表清单抛出明确异常而不是库的异常()
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [], TestContext.Current.CancellationToken));

        Assert.Equal("sheets", failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 清单本身为 null、清单里有 null 项、输出流为 null 都在入口抛 ArgumentNullException
    /// </summary>
    /// <remarks>
    /// null 项不会被当成「跳过这张表」：它既没有表名也没有列，写出侧给不出任何一档；
    /// 没有这道判，取 null 项的表名就直接是 <c>NullReferenceException</c>，那种形态不能被当成契约。
    /// </remarks>
    [Fact]
    public async Task 清单为null或含null项时抛ArgumentNullException()
    {
        var stream = new MemoryStream();
        var exporter = new ClosedXmlExporter(new XiHanExcelOptions());

        var nullList = await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter
            .ExportAllAsync(stream, null!, TestContext.Current.CancellationToken));
        Assert.Equal("sheets", nullList.ParamName);

        var nullItem = await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter
            .ExportAllAsync(stream, [Spec("运单", WaybillColumns, []), null!], TestContext.Current.CancellationToken));
        Assert.Equal("sheets", nullItem.ParamName);
        Assert.Contains("第 2 项", nullItem.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter
            .ExportAllAsync(null!, [Spec("运单", WaybillColumns, [])], TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 表名守卫在写出第一格之前完成：第二张表名非法时整份档不落盘
    /// </summary>
    [Fact]
    public async Task 第二张表名非法时在写出任何字节前抛()
    {
        var stream = new MemoryStream();
        var specs = new[]
        {
            Spec("好表", WaybillColumns, [Row("AWB1", 1.5m)]),
            Spec("坏:表", WaybillColumns, [Row("AWB2", 2.5m)])
        };

        await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, specs, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 拒绝重名工作表，消息点名是第几张表
    /// </summary>
    [Fact]
    public async Task 拒绝重名工作表()
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec("A", WaybillColumns, []), Spec("A", SummaryColumns, [])], TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.SheetName), failure.ParamName);
        Assert.Contains("第 2 张表", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 判重不区分大小写，与 Excel 和库的口径一致
    /// </summary>
    [Fact]
    public async Task 重名判定不区分大小写()
    {
        var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec("Sheet A", WaybillColumns, []), Spec("sheet a", WaybillColumns, [])], TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 尾随空格算两个不同的名字，证明判重没有比库更严
    /// </summary>
    [Fact]
    public async Task 与库口径一致_尾随空格不算重名()
    {
        var stream = new MemoryStream();

        await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec("Sheet1", WaybillColumns, []), Spec("Sheet1 ", WaybillColumns, [])], TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Equal("Sheet1", workbook.Worksheet(1).Name);
        Assert.Equal("Sheet1 ", workbook.Worksheet(2).Name);
    }

    /// <summary>
    /// 拒绝库不接受的表名字符，由我们的预检抛出而不是库的异常
    /// </summary>
    [Theory]
    [InlineData("坏:表")]
    [InlineData("坏*表")]
    [InlineData("坏?表")]
    [InlineData("坏/表")]
    [InlineData("坏\\表")]
    [InlineData("坏[表")]
    [InlineData("坏]表")]
    [InlineData("坏\u0000表")]
    [InlineData("坏\u0003表")]
    [InlineData("'开头")]
    [InlineData("结尾'")]
    public async Task 拒绝非法字符的表名(string sheetName)
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec(sheetName, WaybillColumns, [])], TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.SheetName), failure.ParamName);
        Assert.Contains("第 1 张表", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 表名长度上限是 31 个字符，按 UTF-16 代码单元计，与库同一量纲
    /// </summary>
    [Fact]
    public async Task 拒绝三十二个字符的表名()
    {
        var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec(new string('x', 32), WaybillColumns, [])], TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.SheetName), failure.ParamName);
        Assert.Contains("31", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 库肯收的表名一律不误杀：上限长度、竖线、尖括号、全形、前后空白、中间撇号、数字开头都能写出
    /// </summary>
    [Theory]
    [InlineData(MaximumLengthName)]
    [InlineData("N|N")]
    [InlineData("N<N")]
    [InlineData("N>N")]
    [InlineData("N\u007FN")]
    [InlineData("含全角ＡＢＣ")]
    [InlineData(" 前后空白 ")]
    [InlineData("A'B")]
    [InlineData("123")]
    [InlineData("😀票")]
    public async Task 库肯收的表名不被预检拒掉(string sheetName)
    {
        var stream = new MemoryStream();

        await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec(sheetName, WaybillColumns, [Row("AWB1", 1.5m)])], TestContext.Current.CancellationToken);

        using var workbook = Open(stream);
        Assert.Equal(1, workbook.Worksheets.Count);
        Assert.Equal(sheetName, workbook.Worksheet(1).Name);
        Assert.Equal("AWB1", workbook.Worksheet(1).Cell(2, 1).GetString());
    }

    /// <summary>
    /// 空白表名由 <see cref="ExcelSheetSpec.SheetName"/> 的构造守卫兜住，多表入口不再立第二份守卫
    /// </summary>
    [Fact]
    public void 拒绝空白表名由规格构造承担()
    {
        var failure = Assert.Throws<ArgumentException>(() => Spec("   ", WaybillColumns, []));

        Assert.Equal(nameof(ExcelSheetSpec.SheetName), failure.ParamName);
        Assert.Contains("不能为空", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 零行且 <c>RowType</c> 为 null 的非法声明在预检就抛，两个入口都拦且零字节
    /// </summary>
    [Fact]
    public async Task 零行且RowType为null时预检抛且零字节()
    {
        var singleStream = new MemoryStream();
        var singleFailure = await Assert.ThrowsAsync<ArgumentNullException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(singleStream, SpecWithoutRowType([]), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), singleFailure.ParamName);
        Assert.Equal(0, singleStream.Length);

        var multiStream = new MemoryStream();
        var multiFailure = await Assert.ThrowsAsync<ArgumentNullException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(multiStream, [SpecWithoutRowType([])], TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), multiFailure.ParamName);
        Assert.Equal(0, multiStream.Length);
    }

    /// <summary>
    /// <c>RowType</c> 为 null 的判定上移到预检后，行集合一次都不被枚举
    /// </summary>
    [Fact]
    public async Task RowType为null时不枚举行集合()
    {
        var rows = new CountingRows(3);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAsync(new MemoryStream(), new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = null!,
                Columns = WaybillColumns,
                Rows = rows
            }, TestContext.Current.CancellationToken));

        Assert.Equal(0, rows.Count);

        var multiRows = new CountingRows(3);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(new MemoryStream(), [new ExcelSheetSpec
            {
                SheetName = "运单",
                RowType = null!,
                Columns = WaybillColumns,
                Rows = multiRows
            }], TestContext.Current.CancellationToken));

        Assert.Equal(0, multiRows.Count);
    }

    /// <summary>
    /// 取消令牌已取消时多表路径不写出任何字节
    /// </summary>
    [Fact]
    public async Task 多表取消令牌已取消时不写出任何字节()
    {
        using var source = new CancellationTokenSource();

        source.Cancel();

        var stream = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec("运单", WaybillColumns, [Row("AWB1", 1.5m)])], source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 后一张表的行集合在中途被取消时同样不落盘，且前面的表不留下半个字节
    /// </summary>
    [Fact]
    public async Task 逐表写出途中取消时不写出任何字节()
    {
        using var source = new CancellationTokenSource();
        var specs = new[]
        {
            Spec("运单", WaybillColumns, [Row("AWB1", 1.5m)]),
            new ExcelSheetSpec
            {
                SheetName = "汇总",
                RowType = typeof(SampleRow),
                Columns = WaybillColumns,
                Rows = RowsThatCancel(source)
            }
        };

        var stream = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, specs, source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 多表路径同样检查取样上限，非法选项在写出前抛
    /// </summary>
    [Fact]
    public async Task 多表路径的负取样上限在写出前抛()
    {
        var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions { AutoWidthSampleRows = -1 })
            .ExportAllAsync(stream, [Spec("运单", WaybillColumns, [Row("AWB1", 1.5m)])], TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 表级列宽越界在多表路径同样抛，不复写第二份守卫
    /// </summary>
    [Fact]
    public async Task 多表路径的列宽越界仍由列守卫拦下()
    {
        var stream = new MemoryStream();
        var columns = new ExcelColumn[]
        {
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Width = 0,
                Value = row => row.AwbNo
            }
        };

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec("运单", columns, [Row("AWB1", 1.5m)]), Spec("汇总", WaybillColumns, [])], TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelColumn.Width), failure.ParamName);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 表级底色非法在多表路径同样抛成 ArgumentException，颜色守卫只有 <c>WriteSheet</c> 里那一份
    /// </summary>
    [Fact]
    public async Task 多表路径的非法底色仍由同一份颜色守卫拦下()
    {
        var stream = new MemoryStream();
        var spec = new ExcelSheetSpec
        {
            SheetName = "汇总",
            RowType = typeof(SampleRow),
            Columns = WaybillColumns,
            Rows = new SampleRow[] { },
            HeaderFill = "不是颜色"
        };

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await new ClosedXmlExporter(new XiHanExcelOptions())
            .ExportAllAsync(stream, [Spec("运单", WaybillColumns, []), spec], TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.HeaderFill), failure.ParamName);
        Assert.Equal(0, stream.Length);
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
    /// 构造一行测试数据
    /// </summary>
    private static SampleRow Row(string awbNo, decimal weight)
        => new()
        {
            AwbNo = awbNo,
            Weight = weight,
            Eta = new DateTime(2026, 1, 2)
        };

    /// <summary>
    /// 构造表规格
    /// </summary>
    private static ExcelSheetSpec Spec(string sheetName, ExcelColumn[] columns, SampleRow[] rows)
        => new()
        {
            SheetName = sheetName,
            RowType = typeof(SampleRow),
            Columns = columns,
            Rows = rows
        };

    /// <summary>
    /// 构造 <see cref="ExcelSheetSpec.RowType"/> 为 null 的非法规格，行集合按参数给定
    /// </summary>
    private static ExcelSheetSpec SpecWithoutRowType(SampleRow[] rows)
        => new()
        {
            SheetName = "运单",
            RowType = null!,
            Columns = WaybillColumns,
            Rows = rows
        };

    /// <summary>
    /// 构造用于单表/多表一致性比对的规格：标题、样式、排版开关与行数都取边界组合
    /// </summary>
    private static ExcelSheetSpec BuildEquivalentSpec(int rowCount)
    {
        ExcelColumn[] columns =
        [
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Width = 12,
                Alignment = ExcelAlignment.Center,
                Value = row => row.AwbNo
            },
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                NumberFormat = "0.00",
                CellStyle = row => row is SampleRow typed && typed.Weight > 2m
                    ? new ExcelTextStyle { Bold = true, FontColor = "#C00000" }
                    : null,
                Value = row => row.Weight
            }
        ];

        var rows = new SampleRow[rowCount];

        for (var index = 0; index < rowCount; index++)
        {
            rows[index] = Row($"AWB{index}", index == 1 ? 3.5m : 1.5m);
        }

        return new ExcelSheetSpec
        {
            SheetName = "运单",
            Title = "空运提单汇总",
            RowType = typeof(SampleRow),
            Columns = columns,
            Rows = rows,
            HeaderFill = "#FFF2CC",
            FreezeHeader = false,
            AutoFilter = false
        };
    }

    /// <summary>
    /// 交出两行的惰性序列，取到第二行之前先把取消令牌取消
    /// </summary>
    private static IEnumerable<SampleRow> RowsThatCancel(CancellationTokenSource source)
    {
        yield return new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) };

        source.Cancel();

        yield return new SampleRow { AwbNo = "AWB2", Weight = 2.5m, Eta = new DateTime(2026, 1, 3) };
    }

    /// <summary>
    /// 把 ClosedXML 的颜色按 RGB 分量写成 <c>#RRGGBB</c>，避免依赖 <see cref="XLColor"/> 的相等性
    /// </summary>
    private static string HexOf(XLColor color)
        => $"#{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}";
}
