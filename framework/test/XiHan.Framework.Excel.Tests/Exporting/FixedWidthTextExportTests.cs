// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Columns;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Text;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Exporting;

/// <summary>
/// 固定宽度布局的文字档导出测试
/// </summary>
/// <remarks>
/// <para>
/// 断言一律落在字节上：固定宽度档由读档方按字节位置切列，所以每条用例检查的是「整行字节数等于各列宽之和」
/// 与「每格字节数等于该列列宽」，不是字符数。多数夹具取 <c>EncodingName = "utf-8"</c>（不写 BOM），让流的字节数
/// 正好等于表头行 + 数据行 + 行尾，不必先剥 BOM 再算。
/// </para>
/// <para>
/// 列宽、补位方向与补位字符是列级设置，超宽策略与编码是档级设置，因此夹具逐列给出
/// <see cref="ExcelColumn.FixedWidth"/>。
/// </para>
/// </remarks>
public class FixedWidthTextExportTests
{
    private const int AwbWidth = 12;

    private const int WeightWidth = 10;

    private const int EtaWidth = 12;

    private const int RowWidth = AwbWidth + WeightWidth + EtaWidth;

    /// <summary>
    /// 提单号与重量靠右补空格、预计到达靠左补零的三列夹具
    /// </summary>
    private static readonly ExcelColumn[] FixedColumns =
    [
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.AwbNo),
            Header = "提单号",
            Order = 0,
            FixedWidth = AwbWidth,
            Value = row => row.AwbNo
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Weight),
            Header = "重量",
            Order = 1,
            FixedWidth = WeightWidth,
            TextFormat = "0.00",
            Value = row => row.Weight
        },
        new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.Eta),
            Header = "预计到达",
            Order = 2,
            FixedWidth = EtaWidth,
            Padding = ExcelTextPadding.Left,
            PadChar = '0',
            TextFormat = "yyyy-MM-dd",
            Value = row => row.Eta
        }
    ];

    /// <summary>
    /// 表头与数据行都按列宽补位，列级补位方向与补位字符各自生效
    /// </summary>
    [Fact]
    public async Task 表头与数据行按列宽补位()
    {
        var spec = BuildSpec(FixedColumns, new SampleRow { AwbNo = "AWB1", Weight = 1.5m, Eta = new DateTime(2026, 1, 2) });

        var (stream, result) = await ExportAsync(FixedOptions(), spec, ExcelFormat.Txt);

        Assert.Equal(".txt", result.FileExtension);
        Assert.Equal("text/plain", result.ContentType);

        // 表头：提单号(9 字节)+3、重量(6)+4、预计到达(12)+0；数据行：AWB1(4)+8、1.50(4)+6、2026-01-02(10) 靠左补两个 0
        Assert.Equal(
            "提单号" + "   " + "重量" + "    " + "预计到达" + "\r\n" +
            "AWB1" + "        " + "1.50" + "      " + "002026-01-02" + "\r\n",
            BodyOf(stream));

        // utf-8 不写 BOM：两行各 34 字节内容 + 2 字节行尾
        Assert.Equal(2 * (RowWidth + 2), stream.ToArray().Length);
    }

    /// <summary>
    /// 内容字节数不足列宽时补位差额按字节算，整行字节数等于该列列宽
    /// </summary>
    [Fact]
    public async Task 内容不足列宽时按字节补满()
    {
        var encoding = Encoding.UTF8;

        // 「已到港」在 UTF-8 下 9 字节、3 个字符；列宽 12 字节，差额 3 按字节补。按字符补只会补到 6 字节
        var spec = BuildSpec([Column(AwbWidth)], new SampleRow { AwbNo = "已到港" });

        var stream = (await ExportAsync(FixedOptions(), spec)).Stream;
        var lines = BodyOf(stream).Split(["\r\n"], StringSplitOptions.None);

        Assert.Equal(3, lines.Length);                                        // 表头行 + 数据行 + 行尾后的空段
        Assert.Equal(AwbWidth, encoding.GetByteCount(lines[0]));
        Assert.Equal(AwbWidth, encoding.GetByteCount(lines[1]));
        Assert.Equal("已到港" + "   ", lines[1]);
    }

    /// <summary>
    /// 行元素为空时三列取值都是空字串，整格由各自的补位字符填满，不缺格
    /// </summary>
    [Fact]
    public async Task 空行补满整行宽度()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = FixedColumns,
            Rows = new SampleRow?[] { null }
        };

        var stream = (await ExportAsync(FixedOptions(), spec)).Stream;
        var lines = BodyOf(stream).Split(["\r\n"], StringSplitOptions.None);

        Assert.Equal(RowWidth, Encoding.UTF8.GetByteCount(lines[1]));
        Assert.Equal(new string(' ', AwbWidth + WeightWidth) + new string('0', EtaWidth), lines[1]);
    }

    /// <summary>
    /// 固定宽度布局与分隔符布局走同一份行型守卫：第二笔异型同样抛出并点名行号
    /// </summary>
    [Fact]
    public async Task 固定宽度第二笔异型时抛出并点名行号()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = FixedColumns,
            Rows = new object?[] { new SampleRow { AwbNo = "AWB1" }, "不是行类型" }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ExportAsync(
            FixedOptions(), spec, ExcelFormat.Txt));

        Assert.Contains("第 2 行", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow), exception.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 整行字节数超过所有列宽之和时抛出，信息带该列列宽与实际字节数
    /// </summary>
    [Fact]
    public async Task 整行超宽时抛出且信息带列宽与实际字节数()
    {
        // 「中文中文中文」18 字节 > 列宽 12 字节，整行因此超过列宽之和
        var spec = BuildSpec([Column(AwbWidth)], new SampleRow { AwbNo = "中文中文中文" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ExportAsync(FixedOptions(), spec));

        Assert.Contains("实际 18 字节", exception.Message, StringComparison.Ordinal);
        Assert.Contains("列宽 12 字节", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow.AwbNo), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 全形字跨越列边界时按列宽截断，不切断多字节字符也不出现替换字符
    /// </summary>
    [Fact]
    public async Task 整行超宽时按列宽截断不切断全形字()
    {
        // 「中文中文中」15 字节，列宽 11 字节：第 4 个汉字跨过边界，取舍停在 9 字节，再补 2 格
        var spec = BuildSpec([Column(11)], new SampleRow { AwbNo = "中文中文中" });

        var stream = (await ExportAsync(FixedOptions(overflow: ExcelTextOverflow.Truncate), spec)).Stream;
        var body = BodyOf(stream);
        var lines = body.Split(["\r\n"], StringSplitOptions.None);

        Assert.Equal(11, Encoding.UTF8.GetByteCount(lines[1]));
        Assert.Equal("中文中" + "  ", lines[1]);
        Assert.DoesNotContain('\uFFFD', body);
    }

    /// <summary>
    /// 截断丢弃了数据，整份文件只记一条聚合 Warning，报出数量与首个触发的行列
    /// </summary>
    [Fact]
    public async Task 截断丢弃内容时记一条聚合Warning()
    {
        var sink = new FakeLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new SinkLoggerProvider(sink)));

        var spec = BuildSpec(
            FixedColumns,
            new SampleRow { AwbNo = "中文中文中文", Eta = new DateTime(2026, 1, 2) },
            new SampleRow { AwbNo = "英文英文英文", Eta = new DateTime(2026, 1, 3) });

        await ExportAsync(
            FixedOptions(overflow: ExcelTextOverflow.Truncate),
            spec,
            logger: factory.CreateLogger<DelimitedTextExporter>());

        var warnings = sink.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("有 2 个字段", warnings[0].Message, StringComparison.Ordinal);   // 两行的提单号列都被截断
        Assert.Contains(nameof(SampleRow.AwbNo), warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("第 1 行", warnings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 缺 FixedWidth 的列在写出任何字节之前就失败，信息列出所有缺宽度的列键
    /// </summary>
    [Fact]
    public async Task 缺FixedWidth的列在写出前就失败()
    {
        // 提单号有列宽，重量与预计到达没有：两个缺宽度的列键都要出现在信息里
        var columns = new List<ExcelColumn>
        {
            Column(AwbWidth),
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.Weight),
                Header = "重量",
                Order = 1,
                TextFormat = "0.00",
                Value = row => row.Weight
            },
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.Eta),
                Header = "预计到达",
                Order = 2,
                Value = row => row.Eta
            }
        };

        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(columns, new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Txt,
            FixedOptions(), TestContext.Current.CancellationToken));

        Assert.Contains(nameof(SampleRow.Weight), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow.Eta), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ExcelColumn.FixedWidth), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 特性驱动的列不带字节宽度，固定宽度布局下按「缺宽度」抛出而不是猜一个宽度
    /// </summary>
    /// <remarks>
    /// 列特性没有字节宽度成员，构建器也从不写 <see cref="ExcelColumn.FixedWidth"/>（特性上的 <c>Width</c>
    /// 是工作簿显示宽度，与这里的字节宽度不是同一件事）。因此 <c>Layout = FixedWidth</c> 时列必须由调用方自己
    /// 构造并给 <c>FixedWidth</c>，本条把该现实钉住。
    /// </remarks>
    [Fact]
    public async Task 特性构建的列在固定宽度布局下缺字节宽度()
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var spec = new ExcelSheetSpec
        {
            SheetName = "标注",
            RowType = typeof(AnnotatedRow),
            Columns = ExcelColumnBuilder.CreateColumns<AnnotatedRow>(),
            Rows = new[] { new AnnotatedRow { Name = "甲", Quantity = 3 } }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, spec, ExcelFormat.Txt, FixedOptions(), TestContext.Current.CancellationToken));

        Assert.Contains(nameof(AnnotatedRow.Name), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AnnotatedRow.Quantity), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 列宽必须是正整数：零宽与负宽都在写出前失败，不静默当成「未指定」
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task 非正列宽在写出前就失败(int width)
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([Column(width)], new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Txt,
            FixedOptions(), TestContext.Current.CancellationToken));

        Assert.Contains($"{nameof(SampleRow.AwbNo)}={width}", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 补位字符在目标编码下不是单字节时，在写出任何字节之前就失败
    /// </summary>
    [Fact]
    public async Task 多字节补位字符在写出前就失败()
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var columns = new List<ExcelColumn>
        {
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Order = 0,
                FixedWidth = AwbWidth,
                PadChar = '中',
                Value = row => row.AwbNo
            }
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(columns, new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Txt,
            FixedOptions(), TestContext.Current.CancellationToken));

        Assert.Contains("单字节", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow.AwbNo), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 值内含换行在固定宽度布局下不可写出：取到该行即抛，信息点行位置与列键
    /// </summary>
    /// <remarks>
    /// 本布局没有可以包住换行的引号，写出后读档方会把一档当成错行的两档，因此不清洗、不替换、不静默截断，直接抛。
    /// 抛出时机与 <c>Overflow = Throw</c> 的超宽抛出同一层：都在渲染那一格时，前面已有的行不受影响。
    /// </remarks>
    [Theory]
    [InlineData("A\nB")]      // 换行符
    [InlineData("A\rB")]      // 回车
    [InlineData("A\r\nB")]    // CRLF
    public async Task 值含换行时抛出且点名行列位置(string rawValue)
    {
        var spec = BuildSpec([Column(AwbWidth)], new SampleRow { AwbNo = rawValue });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ExportAsync(FixedOptions(), spec));

        Assert.Contains("第 1 行", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow.AwbNo), exception.Message, StringComparison.Ordinal);
        Assert.Contains("换行", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 换行抛出时前面的行已经落盘：该档是半成品，调用方不能把它当完整档交出
    /// </summary>
    /// <remarks>
    /// 行集合是惰性游标，值含不含换行要取到那一行才知道，因此这条不可能「写出任何字节之前」失败；用例刻意让
    /// 前 200 行的字节量超过 <see cref="StreamWriter"/> 的缓冲，把「已经落盘」变成可断言的现实。
    /// </remarks>
    [Fact]
    public async Task 值含换行时前面的行已经落盘()
    {
        var rows = new List<SampleRow>();

        for (var index = 1; index <= 200; index++)
        {
            rows.Add(new SampleRow { AwbNo = $"R{index:000}" });
        }

        rows.Add(new SampleRow { AwbNo = "BAD\nTAIL" });
        rows.Add(new SampleRow { AwbNo = "R202" });

        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            stream, BuildSpec([Column(AwbWidth)], [.. rows]), ExcelFormat.Txt,
            FixedOptions(), TestContext.Current.CancellationToken));

        Assert.Contains("第 201 行", exception.Message, StringComparison.Ordinal);

        // 前若干行已进流：这里不保证零字节残留，只保证出问题的那一行没有落进档里
        Assert.True(stream.Length > 0, $"第 201 行抛出前应该已有行落盘，实际流长 {stream.Length}");

        var written = BodyOf(stream);
        Assert.Contains("R001", written, StringComparison.Ordinal);
        Assert.DoesNotContain("BAD", written);
    }

    /// <summary>
    /// 补位字符是换行时等于凭空造行，与内容无关，因此在写出任何字节之前就失败
    /// </summary>
    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    public async Task 补位字符是换行时在写出前就失败(char padChar)
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var columns = new List<ExcelColumn>
        {
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Order = 0,
                FixedWidth = AwbWidth,
                PadChar = padChar,
                Value = row => row.AwbNo
            }
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(columns, new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Txt,
            FixedOptions(), TestContext.Current.CancellationToken));

        Assert.Contains("换行", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow.AwbNo), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 固定宽度布局不解释分隔符、引号策略与公式前缀，三者一律无效
    /// </summary>
    /// <remarks>
    /// 档按字节位置解析：引号与分隔符没有对应的解析位，公式前缀会吃掉一格字节宽度、破坏列宽契约，
    /// 因此三者不参与写出，写出的是原值补位到列宽。
    /// </remarks>
    [Fact]
    public async Task 固定宽度下分隔符引号与公式前缀无效()
    {
        var spec = BuildSpec(
            [Column(AwbWidth)],
            new SampleRow { AwbNo = "=1+1" },
            new SampleRow { AwbNo = "A;B," });

        var options = new ExcelTextOptions
        {
            Layout = ExcelTextLayout.FixedWidth,
            EncodingName = "utf-8",
            Delimiter = ';',
            Quote = ExcelTextQuote.All,
            EscapeFormulaPrefix = true
        };

        var stream = (await ExportAsync(options, spec)).Stream;
        var body = BodyOf(stream);

        Assert.DoesNotContain("\"", body);
        Assert.DoesNotContain("'", body);
        Assert.Equal(
            "提单号" + "   " + "\r\n" +
            "=1+1" + "        " + "\r\n" +
            "A;B," + "        " + "\r\n",
            body);
    }

    /// <summary>
    /// 分隔符的三条拒写在定宽布局不适用：本布局根本不读 Delimiter，换行分隔符既不报错也不生效
    /// </summary>
    /// <remarks>
    /// 定宽档按字节位置切列，分隔符没有对应的解析位，因此 <c>\r</c>／<c>\n</c>／<c>"</c> 这些在分隔符布局里
    /// 无法成立的取值，对本布局不构成坏输入。真正会被拒的是列上的补位字符取换行（与内容无关，排在预写校验里）。
    /// </remarks>
    [Fact]
    public async Task 定宽布局不拒换行分隔符()
    {
        var spec = BuildSpec([Column(AwbWidth)], new SampleRow { AwbNo = "AWB1" });

        var options = new ExcelTextOptions
        {
            Layout = ExcelTextLayout.FixedWidth,
            EncodingName = "utf-8",
            Delimiter = '\n'
        };

        var stream = (await ExportAsync(options, spec)).Stream;

        Assert.Equal("提单号" + "   " + "\r\n" + "AWB1" + "        " + "\r\n", BodyOf(stream));
    }

    /// <summary>
    /// 不写表头时固定宽度只出行，行宽仍等于各列宽之和
    /// </summary>
    [Fact]
    public async Task 不含表头时只出行()
    {
        var spec = BuildSpec(FixedColumns, new SampleRow { AwbNo = "AWB1", Weight = 2m, Eta = new DateTime(2026, 1, 2) });

        var stream = (await ExportAsync(FixedOptions(includeHeader: false), spec)).Stream;

        Assert.DoesNotContain("提单号", BodyOf(stream));
        Assert.Equal(RowWidth + 2, stream.ToArray().Length);
    }

    /// <summary>
    /// 行尾序列同样作用于固定宽度档，且不占列宽
    /// </summary>
    [Fact]
    public async Task 行尾可配且不占列宽()
    {
        var spec = BuildSpec(FixedColumns, new SampleRow { AwbNo = "AWB1", Weight = 2m, Eta = new DateTime(2026, 1, 2) });

        var options = new ExcelTextOptions
        {
            Layout = ExcelTextLayout.FixedWidth,
            EncodingName = "utf-8",
            NewLine = "\n"
        };

        var stream = (await ExportAsync(options, spec)).Stream;
        var body = BodyOf(stream);

        Assert.DoesNotContain("\r", body);
        Assert.Equal(2 * RowWidth + 2, Encoding.UTF8.GetByteCount(body));
    }

    /// <summary>
    /// 目标编码收不下的字符在固定宽度路径同样抛出，不产出问号字节的坏档
    /// </summary>
    /// <remarks>
    /// 与分隔符路径同一标准：简体表头不在 Big5（代码页 950）字符集内，严格编码器在算字节数时就拒绝。
    /// 本条不断言残留字节数为零——缓冲区决定何时落盘，这里只保证不产出「看起来成功」的坏档。
    /// </remarks>
    [Fact]
    public async Task 不可映射字符在固定宽度下抛编码回退异常()
    {
        var spec = BuildSpec([Column(AwbWidth)], new SampleRow { AwbNo = "AWB1" });

        await Assert.ThrowsAsync<EncoderFallbackException>(async () => await ExportAsync(FixedOptions(encodingName: "big5"), spec));
    }

    /// <summary>
    /// 用 Big5 写繁体固定宽度档：一个汉字两字节，列宽在该编码下按字节成立
    /// </summary>
    [Fact]
    public async Task 大五码下按列宽写出繁体档()
    {
        var encoding = TextWriterHelper.ResolveEncoding("big5");

        var columns = new List<ExcelColumn>
        {
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提單號",
                Order = 0,
                FixedWidth = 8,
                Value = row => row.AwbNo
            },
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.Eta),
                Header = "預計到達",
                Order = 1,
                FixedWidth = 10,
                TextFormat = "yyyy-MM-dd",
                Value = row => row.Eta
            }
        };

        var spec = BuildSpec(columns, new SampleRow { AwbNo = "AWB1", Eta = new DateTime(2026, 1, 2) });

        var stream = (await ExportAsync(FixedOptions(encodingName: "big5"), spec)).Stream;
        var lines = encoding.GetString(stream.ToArray()).Split(["\r\n"], StringSplitOptions.None);

        // 两行都是 8 + 10 = 18 字节：提單號(6)+2、預計到達(8)+2；AWB1(4)+4、2026-01-02(10)+0
        Assert.Equal(18, encoding.GetByteCount(lines[0]));
        Assert.Equal(18, encoding.GetByteCount(lines[1]));
        Assert.Equal("提單號" + "  " + "預計到達" + "  ", lines[0]);
        Assert.Equal("AWB1" + "    " + "2026-01-02", lines[1]);
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
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(FixedColumns, new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Txt,
            FixedOptions(), source.Token));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 写出过程中取消时不再继续后续行
    /// </summary>
    [Fact]
    public async Task 写出中途取消时停止后续行()
    {
        using var source = new CancellationTokenSource();

        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = FixedColumns,
            Rows = RowsThatCancelAfterFirst(source)
        };

        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await exporter.ExportAsync(
            new MemoryStream(), spec, ExcelFormat.Txt, FixedOptions(), source.Token));
    }

    /// <summary>
    /// 固定宽度布局同样按目标格式给出扩展名与内容类型
    /// </summary>
    [Theory]
    [InlineData(ExcelFormat.Csv, ".csv", "text/csv")]
    [InlineData(ExcelFormat.Txt, ".txt", "text/plain")]
    public async Task 固定宽度输出格式契约(ExcelFormat format, string expectedExtension, string expectedContentType)
    {
        var spec = BuildSpec(FixedColumns, new SampleRow { AwbNo = "AWB1" });

        var (_, result) = await ExportAsync(FixedOptions(), spec, format);

        Assert.Equal(expectedExtension, result.FileExtension);
        Assert.Equal(expectedContentType, result.ContentType);
        Assert.Equal(format, result.Format);
        Assert.True(result.StylingApplied);
    }

    /// <summary>
    /// 写出固定宽度档，返回结果流与导出结果
    /// </summary>
    private static async Task<(MemoryStream Stream, ExcelExportResult Result)> ExportAsync(
        ExcelTextOptions options,
        ExcelSheetSpec spec,
        ExcelFormat format = ExcelFormat.Txt,
        ILogger<DelimitedTextExporter>? logger = null)
    {
        var stream = new MemoryStream();

        var result = await new DelimitedTextExporter(logger ?? NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(stream, spec, format, options, TestContext.Current.CancellationToken);

        return (stream, result);
    }

    /// <summary>
    /// 取文字档正文，剥掉前导 BOM
    /// </summary>
    private static string BodyOf(MemoryStream stream)
        => Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');

    /// <summary>
    /// 构造固定宽度选项，夹具默认用不带 BOM 的 UTF-8 以便逐字节核对
    /// </summary>
    private static ExcelTextOptions FixedOptions(
        ExcelTextOverflow overflow = ExcelTextOverflow.Throw,
        bool includeHeader = true,
        string encodingName = "utf-8")
    {
        return new ExcelTextOptions
        {
            Layout = ExcelTextLayout.FixedWidth,
            EncodingName = encodingName,
            Overflow = overflow,
            IncludeHeader = includeHeader
        };
    }

    /// <summary>
    /// 取提单号一列，指定列宽
    /// </summary>
    private static ExcelColumn Column(int width)
    {
        return new ExcelColumn<SampleRow>
        {
            Key = nameof(SampleRow.AwbNo),
            Header = "提单号",
            Order = 0,
            FixedWidth = width,
            Value = row => row.AwbNo
        };
    }

    /// <summary>
    /// 构造指定列清单的表规格
    /// </summary>
    private static ExcelSheetSpec BuildSpec(IReadOnlyList<ExcelColumn> columns, params SampleRow[] rows)
    {
        return new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = columns,
            Rows = rows
        };
    }

    /// <summary>
    /// 第一行交出后取消，用于验证每行写出前都检查取消令牌
    /// </summary>
    private static IEnumerable<SampleRow> RowsThatCancelAfterFirst(CancellationTokenSource source)
    {
        yield return new SampleRow { AwbNo = "A1", Weight = 1m, Eta = new DateTime(2026, 1, 1) };

        source.Cancel();

        yield return new SampleRow { AwbNo = "A2", Weight = 2m, Eta = new DateTime(2026, 1, 2) };
    }
}
