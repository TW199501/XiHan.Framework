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
    /// 表头取繁体（提單號／預計到達）：Big5（代码页 950）不收只在简体里出现的字符，而解析出的编码带严格回退，
    /// 遇到收不下的字符直接抛 <see cref="EncoderFallbackException"/>（见 <c>简体表头在大五码下抛且不写任何字节</c>），
    /// 不会像 .NET 默认的替换回退那样把坏字符安静写成 <c>?</c> 再交回一个成功结果。
    /// 要在 Big5 档里保留简体字，得改用 <c>gb18030</c> 或 UTF-8。
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
    /// 目标编码收不下的字符不留坏档：简体表头写 Big5 时抛出，且一个字节都没进流
    /// </summary>
    [Fact]
    public async Task 简体表头在大五码下抛且不写任何字节()
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        // 默认夹具的表头是简体「提单号／预计到达」，其中的简体字不在 Big5（代码页 950）的字符集里
        await Assert.ThrowsAsync<EncoderFallbackException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(new SampleRow { AwbNo = "AWB1" }), ExcelFormat.Csv,
            new ExcelTextOptions { EncodingName = "big5" }, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 导出 txt 用制表符作默认分隔符，并给出 .txt 的扩展名与内容类型
    /// </summary>
    [Fact]
    public async Task 导出txt_预设制表符()
    {
        var spec = BuildSpec(new SampleRow { AwbNo = "AWB1" });
        var (stream, result) = await ExportWithResult(new ExcelTextOptions(), ExcelFormat.Txt, spec);

        Assert.Equal(".txt", result.FileExtension);
        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal(ExcelFormat.Txt, result.Format);
        Assert.True(result.StylingApplied);
        Assert.Equal("提单号\t重量\t预计到达" + "\r\n" + "AWB1\t0.00\t0001-01-01" + "\r\n", BodyOf(stream));
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
    /// 空格分隔符与不加引号策略是结构性非法组合，写出任何字节之前就拒绝
    /// </summary>
    /// <remarks>
    /// 该组合下值内的空格与分隔符无法区分，替换成空格又是 no-op：既不报错也不留日志就产出一份列数错位的坏档，
    /// 比抛异常伤人得多，因此按「无法确定的输入直接抛」处理，与其他非法输入同一标准。
    /// </remarks>
    [Fact]
    public async Task 空格分隔符与不加引号策略抛异常()
    {
        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () => await exporter.ExportAsync(
            stream, BuildSpec(new SampleRow { AwbNo = "A B" }), ExcelFormat.Csv,
            new ExcelTextOptions { Delimiter = ' ', Quote = ExcelTextQuote.None }, TestContext.Current.CancellationToken));

        Assert.Contains("空格", exception.Message, StringComparison.Ordinal);
        Assert.Contains("不加引号", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// 空格作分隔符时其余引号策略照常工作，值内空格被引号包住
    /// </summary>
    [Fact]
    public async Task 空格分隔符在最小引号下正常工作()
    {
        var spec = BuildSpec(new SampleRow { AwbNo = "A B" });
        var stream = await Export(new ExcelTextOptions { Delimiter = ' ' }, spec: spec);

        Assert.Equal("提单号 重量 预计到达" + "\r\n" + "\"A B\" 0.00 0001-01-01" + "\r\n", BodyOf(stream));
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
    /// 负数经文本格式后仍触发公式前缀，改动以一条聚合 Warning 留痕
    /// </summary>
    /// <remarks>
    /// 默认开启的 <c>EscapeFormulaPrefix</c> 把 <c>-5.00</c> 写成 <c>'-5.00</c>，读回是文本而非数字，
    /// 是防公式注入的既定代价。本条把该代价显性化：字节序列含前缀，且整份文件只记一条 Warning，内容含改写数量与首个触发的列键，
    /// 不逐格刷日志。
    /// </remarks>
    [Fact]
    public async Task 负数列的公式前缀记一条聚合Warning()
    {
        var sink = new FakeLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new SinkLoggerProvider(sink)));
        var exporter = new DelimitedTextExporter(factory.CreateLogger<DelimitedTextExporter>());
        var stream = new MemoryStream();

        var spec = BuildSpec(
            new SampleRow { AwbNo = "AWB1", Weight = -5m, Eta = new DateTime(2026, 1, 2) },
            new SampleRow { AwbNo = "AWB2", Weight = -12m, Eta = new DateTime(2026, 1, 3) });

        await exporter.ExportAsync(stream, spec, ExcelFormat.Csv, new ExcelTextOptions(), TestContext.Current.CancellationToken);

        var text = BodyOf(stream);
        Assert.Equal(
            "提单号,重量,预计到达" + "\r\n" +
            "AWB1,'-5.00,2026-01-02" + "\r\n" +
            "AWB2,'-12.00,2026-01-03" + "\r\n", text);

        var warnings = sink.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("有 2 个字段", warnings[0].Message, StringComparison.Ordinal);   // 改写数量：两行的重量列
        Assert.Contains("Weight", warnings[0].Message, StringComparison.Ordinal);         // 首个触发的列键
        Assert.Contains("第 1 行", warnings[0].Message, StringComparison.Ordinal);        // 首个触发的行位置
    }

    /// <summary>
    /// 文字档与 xlsx 共用同一份行型守卫：首笔正确、次笔异型即抛，并点名行号与两个类型
    /// </summary>
    /// <remarks>
    /// 判据只有一份，措辞前缀按路径给出。断言落在异常类型、行号与两个类型名上，不断言流的字节数：
    /// 文字档是 <c>StreamWriter</c> 边写边缓冲，第一行可能已经落盘，抛出时留下的半份档正是这条守卫要拦的结果，
    /// 不是它要消除的现象。
    /// </remarks>
    [Fact]
    public async Task 文字导出第二笔异型时抛出并点名行号()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = Columns,
            Rows = new object?[] { new SampleRow { AwbNo = "AWB1" }, "不是行类型" }
        };

        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await exporter.ExportAsync(
            new MemoryStream(), spec, ExcelFormat.Csv, new ExcelTextOptions(), TestContext.Current.CancellationToken));

        Assert.Contains("第 2 行", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SampleRow), exception.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 文字导出沿用同一条声明级预检：<c>RowType</c> 为 null 时在写出任何字节之前抛，行集合一次都不被枚举
    /// </summary>
    [Fact]
    public async Task 文字导出RowType为null时预检抛且零字节()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = null!,
            Columns = Columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1" } }
        };

        var stream = new MemoryStream();
        var exporter = new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(async () => await exporter.ExportAsync(
            stream, spec, ExcelFormat.Csv, new ExcelTextOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ExcelSheetSpec.RowType), exception.ParamName);
        Assert.Equal(0, stream.Length);
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
    /// 文字档最后一笔取值期间取消时抛出，不交出成功结果
    /// </summary>
    /// <remarks>
    /// 与 xlsx 路径不同：这里的 <c>StreamWriter</c> 边写边缓冲，抛出时已落进流的字节数不保证为零，
    /// 所以本条只断「不回报成功」，不断零字节——两套现实不合并成一句承诺。取消的落点可能是写出侧的显式检查，
    /// 也可能是带令牌的异步写作本身，契约只要求 <see cref="OperationCanceledException" /> 家族，故用 ThrowsAny。
    /// </remarks>
    [Fact]
    public async Task 文字导出最后一笔取值期间取消时不回传成功结果()
    {
        using var source = new CancellationTokenSource();

        var columns = new ExcelColumn[]
        {
            new ExcelColumn<SampleRow>
            {
                Key = nameof(SampleRow.AwbNo),
                Header = "提单号",
                Value = row =>
                {
                    // 取消落在最后一笔的取值期间：该轮之后循环没有下一行可查
                    if (row.AwbNo == "LAST")
                    {
                        source.Cancel();
                    }

                    return row.AwbNo;
                }
            }
        };

        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = columns,
            Rows = new[] { new SampleRow { AwbNo = "AWB1" }, new SampleRow { AwbNo = "LAST" } }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(new MemoryStream(), spec, ExcelFormat.Csv, new ExcelTextOptions(), source.Token));
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
    /// 用默认单行规格写成 csv，返回结果流
    /// </summary>
    private static async Task<MemoryStream> Export(ExcelTextOptions? options, ExcelSheetSpec? spec = null)
        => (await ExportWithResult(options, ExcelFormat.Csv, spec)).Stream;

    /// <summary>
    /// 按指定格式写出，同时返回结果流与导出结果，供扩展名与内容类型断言取用
    /// </summary>
    private static async Task<(MemoryStream Stream, ExcelExportResult Result)> ExportWithResult(
        ExcelTextOptions? options, ExcelFormat format, ExcelSheetSpec? spec = null)
    {
        var stream = new MemoryStream();

        var result = await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance)
            .ExportAsync(stream, spec ?? BuildSpec(new SampleRow { AwbNo = "AWB1" }), format, options, TestContext.Current.CancellationToken);

        return (stream, result);
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
