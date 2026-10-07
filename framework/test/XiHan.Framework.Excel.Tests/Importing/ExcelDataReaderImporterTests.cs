// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Importing;

/// <summary>
/// ExcelDataReader 导入测试：签章判别、表头处理、文字档编码回退与行数上限
/// </summary>
/// <remarks>
/// 断言只看对外可观察的东西：键名与键序、取值与型别、行号、抛出的异常类型与消息。夹具一律当场造
/// （<see cref="ImportFixtures" />），仓库里不留二进制档。
/// </remarks>
public class ExcelDataReaderImporterTests
{
    /// <summary>
    /// 框架侧导入行数硬上限，与 <c>ExcelConstants.DefaultMaxImportRows</c> 同一个数
    /// </summary>
    private const int HardMaxRows = 1_000_000;

    /// <summary>
    /// 替换字符，用来判断读回来的是不是解码失败的产物
    /// </summary>
    private const string ReplacementChar = "\uFFFD";

    /// <summary>
    /// 格式未指名时按档头判别：内容是 xlsx 就读成 xlsx，与它从哪来、档名叫什么无关
    /// </summary>
    [Fact]
    public async Task 不信任副档名_按签章判别xlsx()
    {
        var rows = await ReadAll(ImportFixtures.OneRowXlsx());

        Assert.Single(rows);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);
    }

    /// <summary>
    /// 伪装成 Excel 的 HTML 表格判不出来，抛的异常要点名检测到的形态并要求指定格式
    /// </summary>
    [Fact]
    public async Task 伪装成xls的HTML表格抛明确异常并写出检测到的格式()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(ImportFixtures.HtmlTable()));

        Assert.Contains("HTML", failure.Message, StringComparison.Ordinal);
        Assert.Contains("<html", failure.Message, StringComparison.Ordinal);

        // <html><body> 的头八字节，既要能看出是标记语言，也要给出十六进制原样
        Assert.Contains("3C 68 74 6D 6C 3E 3C 62", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelImportFormat.Csv.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelImportFormat.Txt.ToString(), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 二进制垃圾同样判不出来，消息给出档头十六进制而不是空话，也不硬安一个伪装名
    /// </summary>
    [Fact]
    public async Task 无法判别的二进制档头交出十六进制可读形式()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(ImportFixtures.BinaryGarbage()));

        Assert.Contains("00 01 02 03 04 05 06 07", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 只有表头没有数据行交出空序列，不抛也不交出一个全空行
    /// </summary>
    [Fact]
    public async Task 零数据行返回空序列而不是抛()
        => Assert.Empty(await ReadAll(ImportFixtures.HeaderOnlyXlsx()));

    /// <summary>
    /// 表头重名加 _n 后缀，两份数据都留得住
    /// </summary>
    [Fact]
    public async Task 表头重名加后缀不互相覆盖()
    {
        var row = (await ReadAll(ImportFixtures.DuplicateHeaderXlsx("重量", "重量")))[0];

        Assert.Equal(["重量", "重量_2"], row.Values.Keys);
        Assert.Equal("值1", row.Values["重量"]);
        Assert.Equal("值2", row.Values["重量_2"]);
    }

    /// <summary>
    /// 源档里本来就有「重量_2」时，生成的后缀要让路到不重名为止，不能把已有键盖掉
    /// </summary>
    [Fact]
    public async Task 生成的后缀与已有键撞名时继续加号()
    {
        var row = (await ReadAll(ImportFixtures.DuplicateHeaderXlsx("重量", "重量", "重量_2")))[0];

        // 第二个「重量」先占走 重量_2，第三个表头文案正好与它同名，只能再加一号
        Assert.Equal(["重量", "重量_2", "重量_2_2"], row.Values.Keys);
        Assert.Equal("值1", row.Values["重量"]);
        Assert.Equal("值2", row.Values["重量_2"]);
        Assert.Equal("值3", row.Values["重量_2_2"]);
    }

    /// <summary>
    /// 无 BOM 的非 UTF-8 Big5 档回退后读到正字，不是看着像字的乱码
    /// </summary>
    [Fact]
    public async Task 无BOM且非UTF8的Big5档回退后不乱码()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號\r\nAWB1\r\n");
        var rows = await ReadCsv(bytes);

        var key = rows[0].Values.Keys.Single();

        Assert.Equal("提單號", key);
        Assert.Equal("AWB1", rows[0].Values[key]);

        // 既不能是解码失败的替换字符，也不能是按 windows-1252 解出来的那一串「也是字」的乱码
        Assert.DoesNotContain(ReplacementChar, key, StringComparison.Ordinal);
        Assert.DoesNotContain("\u00B4", key, StringComparison.Ordinal);
    }

    /// <summary>
    /// 指名 Big5 时按指名的编码读，取值可按数字解释
    /// </summary>
    [Fact]
    public async Task 指名Big5编码时按指名读()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "重量\r\n1.5\r\n");
        var rows = await ReadCsv(bytes, o => o with { TextEncodingName = "big5" });

        Assert.Equal(1.5m, Convert.ToDecimal(rows[0].Values["重量"], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Big5 档把编码指成 UTF-8 时解码失败就抛，不交回另一种语言的字当正常数据
    /// </summary>
    [Fact]
    public async Task 指错编码时解码失败而不交回乱码()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號\r\nAWB1\r\n");

        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            await ReadCsv(bytes, o => o with { TextEncodingName = "utf-8" }));
    }

    /// <summary>
    /// 未知编码名抛 ArgumentException 并点名 TextEncodingName，内层保留解析失败的原话
    /// </summary>
    [Fact]
    public async Task 未知编码名抛ArgumentException并保留内层异常()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ReadCsv("a\r\n1\r\n"u8.ToArray(), o => o with { TextEncodingName = "utf-99" }));

        Assert.Equal(nameof(ExcelImportOptions.TextEncodingName), failure.ParamName);
        Assert.Contains("utf-99", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.InnerException);
    }

    /// <summary>
    /// 前段是纯 ASCII 的 Big5 档照旧读出正字：编码判定不按档头窗口做，整档试解 UTF-8 不成才落 Big5
    /// </summary>
    /// <remarks>
    /// 档的前 <c>47KB</c> 全是 ASCII，中文在其后才出现。
    /// </remarks>
    [Fact]
    public async Task ASCII前缀超过试探窗口的Big5档读出正字()
    {
        var ascii = new string('A', 47 * 1024);
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, $"{ascii},PAD\r\n提單號,AWB1\r\n");

        var rows = await ReadCsv(bytes);

        var row = Assert.Single(rows);
        Assert.Equal("提單號", row.Values[ascii]);
        Assert.Equal("AWB1", row.Values["PAD"]);
        Assert.DoesNotContain(ReplacementChar, (string?)row.Values[ascii], StringComparison.Ordinal);
    }

    /// <summary>
    /// 指名编码时按指名的编码解码：UTF-8 合法字节指名 latin-1 就读回 latin-1 的字
    /// </summary>
    [Fact]
    public async Task 指名latin1时UTF8合法字节按latin1读()
    {
        // 0xC3 0xA9 在 UTF-8 下是一个 U+00E9，在 latin-1 下是 U+00C3 与 U+00A9 两个字符
        var bytes = new byte[] { (byte)'a', 0x0D, 0x0A, 0xC3, 0xA9, 0x0D, 0x0A };

        var rows = await ReadCsv(bytes, o => o with { TextEncodingName = "iso-8859-1" });

        Assert.Equal("\u00C3\u00A9", rows[0].Values["a"]);
    }

    /// <summary>
    /// 档带 BOM 时以 BOM 为准，指名的编码让位：读取器认 BOM 且优先于任何回退编码
    /// </summary>
    [Fact]
    public async Task 档带BOM时指名编码让位给BOM()
    {
        using var input = ImportFixtures.TextWithUtf8Bom("提單號\r\nAWB1\r\n");

        var rows = await ReadAll(input, new ExcelImportOptions
        {
            Format = ExcelImportFormat.Csv,
            TextEncodingName = "iso-8859-1"
        });

        Assert.Equal("AWB1", rows[0].Values["提單號"]);
    }

    /// <summary>
    /// 指名编码那一支转码用的是另建的流，调用方的流读完仍归调用方
    /// </summary>
    [Fact]
    public async Task 指名编码转码之后调用方的流仍可用()
    {
        using var input = ImportFixtures.Big5Text("提單號\r\nAWB1\r\n");

        var rows = await ReadAll(input, new ExcelImportOptions
        {
            Format = ExcelImportFormat.Csv,
            TextEncodingName = "big5"
        });

        Assert.Equal("AWB1", rows[0].Values["提單號"]);
        Assert.True(input.CanRead);

        input.Position = 0;
        Assert.Equal(0xB4, input.ReadByte());
    }

    /// <summary>
    /// 文字档在交出第一行之前已把整档读过一遍：解码编码与整档最大列数都在建立读取器时定下
    /// </summary>
    /// <remarks>
    /// 断言的是<u>累计读走的字节数</u>而不是流位置。档有五千行。
    /// </remarks>
    [Fact]
    public async Task 文字档交出第一行时整档已扫过一遍()
    {
        using var csv = ImportFixtures.Csv("提单号", 5000);
        using var counted = new ReadCountingStream(csv);
        var length = csv.Length;
        var importer = new ExcelDataReaderImporter();
        var options = new ExcelImportOptions { Format = ExcelImportFormat.Csv };
        var read = 0;
        var bytesReadAtFirstRow = 0L;

        await foreach (var _ in importer.ReadAsync(counted, options, TestContext.Current.CancellationToken))
        {
            read++;

            if (read == 1)
            {
                bytesReadAtFirstRow = counted.TotalBytesRead;
            }
        }

        Assert.Equal(5000, read);
        Assert.True(
            bytesReadAtFirstRow >= length,
            $"交出第一行时只从档里读走 {bytesReadAtFirstRow} 字节，档长 {length} 字节：建立读取器时没有扫完整档。");
    }

    /// <summary>
    /// 靠后才变宽的行照旧补出 <c>Col{n}</c>：整档最大列数按整档算，不按开头若干行算
    /// </summary>
    /// <remarks>
    /// 第 1200 个数据行才有四列。
    /// </remarks>
    [Fact]
    public async Task 靠后的宽行仍补出Col键而不丢列()
    {
        var builder = new StringBuilder("ID,NAME\r\n");

        for (var row = 1; row <= 1500; row++)
        {
            builder
                .Append(row.ToString(CultureInfo.InvariantCulture))
                .Append(",v")
                .Append(row.ToString(CultureInfo.InvariantCulture));

            if (row == 1200)
            {
                builder.Append(",EXTRA1,EXTRA2");
            }

            builder.Append("\r\n");
        }

        var rows = await ReadCsv(ImportFixtures.Utf8NoBom.GetBytes(builder.ToString()));

        Assert.Equal(1500, rows.Count);
        Assert.Equal(["ID", "NAME", "Col3", "Col4"], rows[1199].Values.Keys);
        Assert.Equal("EXTRA1", rows[1199].Values["Col3"]);
        Assert.Equal("EXTRA2", rows[1199].Values["Col4"]);
    }

    /// <summary>
    /// 带 UTF-8 前导字节的 XML 伪装档照样点名 XML，不因前导字节认不出形态
    /// </summary>
    [Fact]
    public async Task 带UTF8前导字节的XML伪装档点名XML()
    {
        using var input = new MemoryStream(
            [0xEF, 0xBB, 0xBF, .. "<?xml version=\"1.0\"?><Workbook>"u8.ToArray()]);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(input));

        Assert.Contains("<?xml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("EF BB BF 3C 3F 78 6D 6C", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("不是有签名可依的文字档", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// UTF-16 LE 前导字节的 HTML 伪装档同样点名 HTML
    /// </summary>
    [Fact]
    public async Task 带UTF16前导字节的HTML伪装档点名HTML()
    {
        using var input = new MemoryStream(
            [0xFF, 0xFE, .. new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("<html><body>")]);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(input));

        Assert.Contains("<html", failure.Message, StringComparison.Ordinal);
        Assert.Contains("FF FE 3C 00", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 分隔模式 <c>.txt</c> 里制表符起首的值被引号包住，读回还原成同一个值
    /// </summary>
    /// <remarks>
    /// 导出侧给制表符起首的值整段裹引号，写出的档形如 <c>"\tABC"</c>。
    /// 引号本身往返一致：值内的引号翻倍、值内的换行原样，都读得回来。
    /// </remarks>
    [Fact]
    public async Task 制表符起首的值按引号写出后原样读回()
    {
        var bytes = "COL\r\n\"\tABC\"\r\n"u8.ToArray();

        var rows = await ReadAll(new MemoryStream(bytes), new ExcelImportOptions { Format = ExcelImportFormat.Txt });

        Assert.Equal("\tABC", rows[0].Values["COL"]);
    }

    /// <summary>
    /// 公式注入前缀是单向改写：读回的值带着那个前导撇号，不还原成原值
    /// </summary>
    /// <remarks>
    /// 导出侧默认给制表符起首的值加 <c>'</c> 前缀，并按改写条数记一条警告。前缀不是引号策略的一部分，
    /// 导入侧也不认它、不剥它：读回的就是带前缀的那个值。
    /// </remarks>
    [Fact]
    public async Task 制表符起首的值带公式前缀时读回多出撇号()
    {
        var bytes = "COL\r\n\"'\tABC\"\r\n"u8.ToArray();

        var rows = await ReadAll(new MemoryStream(bytes), new ExcelImportOptions { Format = ExcelImportFormat.Txt });

        Assert.Equal("'\tABC", rows[0].Values["COL"]);
    }

    /// <summary>
    /// MaxRowCount 截断交出前 N 行，行号仍是源文件里的行号
    /// </summary>
    [Fact]
    public async Task MaxRowCount截断且不影响行号连续性()
    {
        using var csv = ImportFixtures.Csv("提单号", 100);

        var rows = await ReadAll(csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv, MaxRowCount = 3 });

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal(3, rows[1].RowNumber);
        Assert.Equal(4, rows[2].RowNumber);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);
        Assert.Equal("AWB3", rows[2].Values["提单号"]);
    }

    /// <summary>
    /// 高于框架硬上限的 MaxRowCount 被拒，调用方不能自己放大内存上限
    /// </summary>
    [Fact]
    public async Task 超过框架硬上限的MaxRowCount被拒()
    {
        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await ReadCsv("提单号\r\nAWB1\r\n"u8.ToArray(), o => o with { MaxRowCount = HardMaxRows + 1 }));

        Assert.Equal(nameof(ExcelImportOptions.MaxRowCount), failure.ParamName);
        Assert.Contains(HardMaxRows.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// MaxRowCount 为 0 或负数同样被拒，不当成「不限制」
    /// </summary>
    /// <param name="maxRows">请求的上限</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 非正整数的MaxRowCount被拒(int maxRows)
    {
        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await ReadCsv("提单号\r\nAWB1\r\n"u8.ToArray(), o => o with { MaxRowCount = maxRows }));

        Assert.Equal(nameof(ExcelImportOptions.MaxRowCount), failure.ParamName);
    }

    /// <summary>
    /// 合并单元格里非左上角的格位读回 null，Excel 的存储形态不做补偿
    /// </summary>
    [Fact]
    public async Task 合并单元格非左上角读到null()
    {
        var row = (await ReadAll(ImportFixtures.MergedCellXlsx()))[0];

        Assert.Equal("左上角", row.Values["第一列"]);
        Assert.Null(row.Values["第二列"]);
    }

    /// <summary>
    /// 值内换行在 Minimal 引号下逐字往返
    /// </summary>
    [Fact]
    public async Task 值内换行在Minimal引号下往返相等()
    {
        var rows = await ReadCsv("a\r\n\"x\r\ny\"\r\n"u8.ToArray());

        Assert.Equal("x\r\ny", rows[0].Values["a"]);
    }

    /// <summary>
    /// 值内的引号按 RFC 4180 写两遍，读回来一对一
    /// </summary>
    [Fact]
    public async Task 值内引号翻倍转义逐字往返()
        => Assert.Equal("x\"y", (await ReadCsv("a\r\n\"x\"\"y\"\r\n"u8.ToArray()))[0].Values["a"]);

    /// <summary>
    /// 被引号包住换行的记录之后，行号按记录数递增而不是按物理行补号
    /// </summary>
    [Fact]
    public async Task 值内换行之后的记录行号按记录数递增()
    {
        var rows = await ReadCsv("h\r\n\"x\r\ny\"\r\nz\r\n"u8.ToArray());

        Assert.Equal([2, 3], rows.Select(r => r.RowNumber).ToArray());
        Assert.Equal("z", rows[1].Values["h"]);
    }

    /// <summary>
    /// 文字档没有签名可依，格式未指名时必须抛并教调用方怎么指名
    /// </summary>
    [Fact]
    public async Task 文字档未指名格式时抛并要求指定Csv或Txt()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadCsv("a,b\r\n1,2\r\n"u8.ToArray(), o => o with { Format = null }));

        Assert.Contains(ExcelImportFormat.Csv.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelImportFormat.Txt.ToString(), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// HasHeader 为 false 时没有任何行被当作表头吃掉，两行都交出，键名取 Col1、Col2…
    /// </summary>
    [Fact]
    public async Task 无表头时用Col1Col2作键()
    {
        var rows = await ReadAll(
            ImportFixtures.Xlsx([["提单号", "重量"], ["AWB1", "1.5"]]),
            new ExcelImportOptions { HasHeader = false });

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Col1", "Col2"], rows[0].Values.Keys);
        Assert.Equal("提单号", rows[0].Values["Col1"]);
        Assert.Equal("AWB1", rows[1].Values["Col1"]);
        Assert.Equal(2, rows[1].RowNumber);
    }

    /// <summary>
    /// TrimHeaders 为 true 才剥表头空白，默认剥
    /// </summary>
    /// <param name="trimHeaders">是否剥表头空白</param>
    /// <param name="expectedKey">期望键名</param>
    [Theory]
    [InlineData(true, "单号")]
    [InlineData(false, "  单号  ")]
    public async Task TrimHeaders逐项生效(bool trimHeaders, string expectedKey)
    {
        var row = (await ReadAll(
            ImportFixtures.XlsxCells(("A1", "  单号  "), ("A2", "AWB1")),
            new ExcelImportOptions { TrimHeaders = trimHeaders }))[0];

        Assert.Equal(expectedKey, row.Values.Keys.Single());
        Assert.Equal("AWB1", row.Values[expectedKey]);
    }

    /// <summary>
    /// TrimValues 默认不剥值里的首尾空白
    /// </summary>
    /// <param name="trimValues">是否剥取值空白</param>
    /// <param name="expected">期望取值</param>
    [Theory]
    [InlineData(false, "  AWB1  ")]
    [InlineData(true, "AWB1")]
    public async Task TrimValues默认不剥但设了才剥(bool trimValues, string expected)
    {
        var row = (await ReadAll(
            ImportFixtures.XlsxCells(("A1", "单号"), ("A2", "  AWB1  ")),
            new ExcelImportOptions { TrimValues = trimValues }))[0];

        Assert.Equal(expected, row.Values["单号"]);
    }

    /// <summary>
    /// SkipEmptyRows 为 true 时整行皆空的记录不交出，为 false 时交出，且两种情形行号都连续
    /// </summary>
    /// <param name="skipEmptyRows">是否跳过空行</param>
    /// <param name="expectedRowNumbers">期望交出的行号</param>
    [Theory]
    [InlineData(true, new[] { 2, 4 })]
    [InlineData(false, new[] { 2, 3, 4 })]
    public async Task SkipEmptyRows逐项生效(bool skipEmptyRows, int[] expectedRowNumbers)
    {
        var rows = await ReadCsv("单号\r\nA\r\n\r\nB\r\n"u8.ToArray(), o => o with { SkipEmptyRows = skipEmptyRows });

        Assert.Equal(expectedRowNumbers, rows.Select(r => r.RowNumber).ToArray());
    }

    /// <summary>
    /// 「全空格行」算不算空行取决于 TrimValues：不剥时空格是实数据，剥完才成空行
    /// </summary>
    /// <param name="trimValues">是否剥取值空白</param>
    /// <param name="skipEmptyRows">是否跳过空行</param>
    /// <param name="expectedCount">期望交出的行数</param>
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    public async Task 空格行算不算空行取决于TrimValues(bool trimValues, bool skipEmptyRows, int expectedCount)
    {
        var rows = await ReadCsv("单号\r\n \r\n"u8.ToArray(),
            o => o with { TrimValues = trimValues, SkipEmptyRows = skipEmptyRows });

        Assert.Equal(expectedCount, rows.Count);
    }

    /// <summary>
    /// HeaderRowIndex 丢掉前导行，丢掉的行仍占行号
    /// </summary>
    [Fact]
    public async Task HeaderRowIndex丢前导行且行号照算()
    {
        var rows = await ReadCsv("报表标题\r\n制作日期\r\n单号\r\nA\r\n"u8.ToArray(),
            o => o with { HeaderRowIndex = 2 });

        Assert.Single(rows);
        Assert.Equal(4, rows[0].RowNumber);
        Assert.Equal("A", rows[0].Values["单号"]);
    }

    /// <summary>
    /// 前导行超出档里的行数时交出空序列而不是抛
    /// </summary>
    [Fact]
    public async Task 前导行超出档长时交出空序列()
        => Assert.Empty(await ReadCsv("单号\r\nA\r\n"u8.ToArray(), o => o with { HeaderRowIndex = 9 }));

    /// <summary>
    /// 负数的 HeaderRowIndex 在构造选项时就拒掉
    /// </summary>
    [Fact]
    public void 负的HeaderRowIndex在构造时抛出()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => new ExcelImportOptions { HeaderRowIndex = -1 });
        Assert.Equal(nameof(ExcelImportOptions.HeaderRowIndex), failure.ParamName);
    }

    /// <summary>
    /// 空字串的 TextEncodingName 不等于「自动判别」，构造时就拒
    /// </summary>
    /// <param name="encodingName">给出的编码名</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白编码名在构造时抛出(string encodingName)
    {
        var failure = Assert.Throws<ArgumentException>(() => new ExcelImportOptions { TextEncodingName = encodingName });
        Assert.Equal(nameof(ExcelImportOptions.TextEncodingName), failure.ParamName);
    }

    /// <summary>
    /// with 派生副本不改动原选项对象
    /// </summary>
    [Fact]
    public void 派生副本不改动原选项对象()
    {
        var original = new ExcelImportOptions();
        var derived = original with { MaxRowCount = 3, TrimValues = true };

        Assert.Null(original.MaxRowCount);
        Assert.False(original.TrimValues);
        Assert.Equal(3, derived.MaxRowCount);
        Assert.True(derived.TrimValues);
        Assert.True(original.HasHeader);
    }

    /// <summary>
    /// 枚举结束后调用方的流仍然可用：读取器不关流
    /// </summary>
    [Fact]
    public async Task 枚举结束后输入流仍归调用方可用()
    {
        using var csv = ImportFixtures.Csv("提单号", 3);
        var options = new ExcelImportOptions { Format = ExcelImportFormat.Csv };

        await ReadAll(csv, options);

        Assert.True(csv.CanRead);
        csv.Position = 0;

        // 复位后头三个字节还是「提」的 UTF-8 序列，说明整条流没被读走或关掉
        var head = new byte[3];
        Assert.Equal(3, csv.Read(head, 0, 3));
        Assert.Equal([0xE6, 0x8F, 0x90], head);

        var again = await ReadAll(csv, options);
        Assert.Equal(3, again.Count);
    }

    /// <summary>
    /// 调用方把流位置留在中间时，本类复位到起点再读：嗅探与解析看的是同一段字节
    /// </summary>
    [Fact]
    public async Task 流位置不在起点时也从起点读()
    {
        using var csv = ImportFixtures.Csv("提单号", 2);
        csv.Position = 11;

        var rows = await ReadAll(csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv });

        Assert.Equal(2, rows.Count);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);

        // xlsx 走同一条复位
        using var xlsx = ImportFixtures.OneRowXlsx();
        xlsx.Position = 200;

        var sheetRows = await ReadAll(xlsx);
        Assert.Equal("AWB1", sheetRows[0].Values["提单号"]);
    }

    /// <summary>
    /// 不可定位的流在建立读取器之前就被拒
    /// </summary>
    [Fact]
    public async Task 不可定位的流被拒并点名input()
    {
        using var backing = ImportFixtures.Csv("提单号", 1);
        using var forward = new ForwardOnlyStream(backing);

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () => await ReadAll(forward));

        Assert.Equal("input", failure.ParamName);
        Assert.Contains("可定位", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 输入流为 null 抛 ArgumentNullException
    /// </summary>
    [Fact]
    public async Task 输入流为null时抛出()
    {
        var failure = await Assert.ThrowsAsync<ArgumentNullException>(async () => await ReadAll(null!));
        Assert.Equal("input", failure.ParamName);
    }

    /// <summary>
    /// 选项传 null 等同默认值
    /// </summary>
    [Fact]
    public async Task 选项为null时用默认值读()
    {
        var rows = await ReadAll(ImportFixtures.OneRowXlsx(), null);

        Assert.Single(rows);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);
    }

    /// <summary>
    /// 指名表名读那一张，而不是永远读第一张
    /// </summary>
    [Fact]
    public async Task 指名工作表名读对应那一张()
    {
        var rows = await ReadAll(ImportFixtures.TwoSheetXlsx(), new ExcelImportOptions { SheetName = "第二表" });

        Assert.Single(rows);
        Assert.Equal("B-2", rows[0].Values["单号"]);
    }

    /// <summary>
    /// 表名比对不区分大小写，与导出侧的表名判重口径一致
    /// </summary>
    [Fact]
    public async Task 表名比对不区分大小写()
    {
        var rows = await ReadAll(ImportFixtures.TwoSheetXlsx(secondName: "SUMMARY"),
            new ExcelImportOptions { SheetName = "summary" });

        Assert.Equal("B-2", rows[0].Values["单号"]);
    }

    /// <summary>
    /// 只读指名的那一张，不把后面的表也接上
    /// </summary>
    [Fact]
    public async Task 一次导入只读一张表()
    {
        var rows = await ReadAll(ImportFixtures.TwoSheetXlsx());

        Assert.Single(rows);
        Assert.Equal("A-1", rows[0].Values["单号"]);
    }

    /// <summary>
    /// 表名不存在时抛并列出实际表名，不降级去读第一张
    /// </summary>
    [Fact]
    public async Task 表名不存在时抛出并列出实际表名()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadAll(ImportFixtures.TwoSheetXlsx(), new ExcelImportOptions { SheetName = "不存在" }));

        Assert.Contains("第一表", failure.Message, StringComparison.Ordinal);
        Assert.Contains("第二表", failure.Message, StringComparison.Ordinal);
        Assert.Contains("不存在", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// .txt 未指名分隔符时用制表符，与导出侧同一默认口径
    /// </summary>
    [Fact]
    public async Task txt默认按制表符切列()
    {
        var rows = await ReadAll(ImportFixtures.Text("单号\t重量\r\nAWB1\t1.5\r\n"),
            new ExcelImportOptions { Format = ExcelImportFormat.Txt });

        Assert.Equal(["单号", "重量"], rows[0].Values.Keys);
        Assert.Equal("1.5", rows[0].Values["重量"]);
    }

    /// <summary>
    /// 指名的分隔符是钉死的：候选表只有一个字符时不再做多行嗅探，文件里另有常见分隔符也不改判
    /// </summary>
    [Fact]
    public async Task 指名分隔符不被文件里的其他常见符号改判()
    {
        var bytes = "甲,乙\t丙\r\n1,2\t3\r\n"u8.ToArray();

        var tab = await ReadCsv(bytes, o => o with { Delimiter = '\t' });
        Assert.Equal(["甲,乙", "丙"], tab[0].Values.Keys);
        Assert.Equal("1,2", tab[0].Values["甲,乙"]);

        var comma = await ReadCsv(bytes, o => o with { Delimiter = ',' });
        Assert.Equal(["甲", "乙\t丙"], comma[0].Values.Keys);
    }

    /// <summary>
    /// 行比表头宽时多出的列以 Col{n} 保留，不丢数据
    /// </summary>
    [Fact]
    public async Task 行比表头宽时补Col键而不丢列()
    {
        var rows = await ReadCsv("a,b\r\n1,2,3\r\n"u8.ToArray());

        Assert.Equal(["a", "b", "Col3"], rows[0].Values.Keys);
        Assert.Equal("3", rows[0].Values["Col3"]);
    }

    /// <summary>
    /// 行比表头窄时缺的列取 null，与空格子的形态一致
    /// </summary>
    [Fact]
    public async Task 行比表头窄时缺的列为null()
    {
        var rows = await ReadCsv("a,b,c\r\n1\r\n"u8.ToArray());

        Assert.Equal("1", rows[0].Values["a"]);
        Assert.Null(rows[0].Values["b"]);
        Assert.Null(rows[0].Values["c"]);
    }

    /// <summary>
    /// 表头空位用列序补名，键不会是空字串
    /// </summary>
    [Fact]
    public async Task 空表头位用Col补名()
    {
        var rows = await ReadCsv("a,,c\r\n1,2,3\r\n"u8.ToArray());

        Assert.Equal(["a", "Col2", "c"], rows[0].Values.Keys);
        Assert.Equal("2", rows[0].Values["Col2"]);
    }

    /// <summary>
    /// 数值表头按不变文化转成键名，同一份档在不同机器上得到同一个键
    /// </summary>
    [Fact]
    public async Task 数值表头按不变文化转键名()
    {
        var row = (await ReadAll(ImportFixtures.XlsxCells(("A1", 2026m), ("A2", "值"))))[0];

        Assert.Equal("2026", row.Values.Keys.Single());
    }

    /// <summary>
    /// 工作簿的值保留 CLR 型别，文字档一律是字串——两种来源的差别不做统一化
    /// </summary>
    [Fact]
    public async Task 工作簿保留型别而文字档一律字串()
    {
        var sheet = (await ReadAll(ImportFixtures.XlsxCells(
            ("A1", "重量"), ("B1", "日期"), ("A2", 1.5), ("B2", new DateTime(2026, 1, 2)))))[0];
        var text = (await ReadCsv("重量,日期\r\n1.5,2026-01-02\r\n"u8.ToArray()))[0];

        Assert.IsType<double>(sheet.Values["重量"]);
        Assert.IsType<DateTime>(sheet.Values["日期"]);
        Assert.IsType<string>(text.Values["重量"]);
        Assert.IsType<string>(text.Values["日期"]);
    }

    /// <summary>
    /// 中途取消在行检查点停下，不把剩下的行继续交出来
    /// </summary>
    [Fact]
    public async Task 中途取消时停止取行()
    {
        using var source = new CancellationTokenSource();
        var importer = new ExcelDataReaderImporter();
        var options = new ExcelImportOptions { Format = ExcelImportFormat.Csv };
        var collected = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in importer.ReadAsync(ImportFixtures.Csv("提单号", 200), options, source.Token))
            {
                collected++;
                if (collected == 1)
                {
                    source.Cancel();
                }
            }
        });

        // 令牌每 64 行才检查一次，因此取消后最多多读到那个检查点
        Assert.InRange(collected, 1, 64);
    }

    /// <summary>
    /// 已经取消的令牌在取第一行之前就抛
    /// </summary>
    [Fact]
    public async Task 预取消的令牌不读任何行()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var importer = new ExcelDataReaderImporter();
        var read = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in importer.ReadAsync(ImportFixtures.Csv("提单号", 5),
                new ExcelImportOptions { Format = ExcelImportFormat.Csv }, source.Token))
            {
                read++;
            }
        });

        Assert.Equal(0, read);
    }

    /// <summary>
    /// 带 BOM 的 UTF-8 文字档照读，BOM 不会混进第一个键名
    /// </summary>
    [Fact]
    public async Task 带BOM的UTF8档不把BOM读进键名()
    {
        var rows = await ReadAll(ImportFixtures.TextWithUtf8Bom("提单号\r\nAWB1\r\n"),
            new ExcelImportOptions { Format = ExcelImportFormat.Csv });

        Assert.Equal("提单号", rows[0].Values.Keys.Single());
    }

    /// <summary>
    /// 显式指名 xlsx 而内容不是该容器时，换成框架已声明的类型，库原话留在内部异常
    /// </summary>
    [Fact]
    public async Task 指名xlsx而内容是HTML表格时转译为框架异常()
    {
        using var html = ImportFixtures.HtmlTable();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadAll(html, new ExcelImportOptions { Format = ExcelImportFormat.Xlsx }));

        Assert.NotNull(failure.InnerException);
        Assert.Contains(ExcelImportFormat.Xlsx.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains("容器", failure.Message, StringComparison.Ordinal);
        Assert.Contains("3C 68 74 6D 6C", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 档头真是 zip 签名但容器截断时同样转译
    /// </summary>
    [Fact]
    public async Task 截断的xlsx容器转译为框架异常()
    {
        using var truncated = new MemoryStream([0x50, 0x4B, 0x03, 0x04, .. "junkjunkjunk"u8.ToArray()]);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(truncated));

        Assert.NotNull(failure.InnerException);
        Assert.Contains(ExcelImportFormat.Xlsx.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains("50 4B 03 04", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 编码解码失败不被转译成容器异常，原样抛出 <c>DecoderFallbackException</c>
    /// </summary>
    [Fact]
    public async Task 解码失败不被转译成容器异常()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號\r\nAWB1\r\n");

        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            await ReadCsv(bytes, o => o with { TextEncodingName = "utf-8" }));
    }

    /// <summary>
    /// TrimValues 在文字档上逐项生效：默认不剥值里的首尾空白
    /// </summary>
    /// <param name="trimValues">是否剥取值空白</param>
    /// <param name="expected">期望取值</param>
    [Theory]
    [InlineData(false, " AWB1 ")]
    [InlineData(true, "AWB1")]
    public async Task TrimValues在文字档上逐项生效(bool trimValues, string expected)
    {
        var rows = await ReadCsv("單號\r\n AWB1 \r\n"u8.ToArray(), o => o with { TrimValues = trimValues });

        Assert.Equal(expected, rows[0].Values["單號"]);
    }

    /// <summary>
    /// 表头含空白时按键名取值，值不被顺手剥：两个旋钮在文字档上各管各的
    /// </summary>
    [Fact]
    public async Task 表头含空白时按键名取值而值不被顺手剥()
    {
        var rows = await ReadCsv(" 單號 ,甲\r\n 乙 ,丙\r\n"u8.ToArray());

        Assert.Equal(["單號", "甲"], rows[0].Values.Keys);
        Assert.Equal(" 乙 ", rows[0].Values["單號"]);
        Assert.Equal("丙", rows[0].Values["甲"]);
    }

    /// <summary>
    /// 只有表头一行的文字档交出空序列
    /// </summary>
    [Fact]
    public async Task 只有表头一行的文字档交出空序列()
        => Assert.Empty(await ReadCsv("單號\r\n"u8.ToArray()));

    /// <summary>
    /// 上限校验排在格式判别与建立读取器之前：同一条垃圾档上，越界上限报越界而不是报判不出格式
    /// </summary>
    [Fact]
    public async Task 越界上限先于格式判别被拒()
    {
        using var garbage = ImportFixtures.BinaryGarbage();

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await ReadAll(garbage, new ExcelImportOptions { MaxRowCount = HardMaxRows + 1 }));

        Assert.Equal(nameof(ExcelImportOptions.MaxRowCount), failure.ParamName);
    }

    /// <summary>
    /// 门控的 <c>.xls</c> 端到端读取：仓里不提交二进制档，没有 <c>XIHAN_TEST_XLS_FILE</c> 时明确跳过
    /// </summary>
    [Fact]
    public async Task 门控的真实xls档能读出数据行()
    {
        var path = Environment.GetEnvironmentVariable("XIHAN_TEST_XLS_FILE");

        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(path),
            "需要一份真实 .xls（旧版二进制工作簿）：本仓不提交二进制档，夹具也造不出来。" +
            "把环境变量 XIHAN_TEST_XLS_FILE 指向该档即可执行本用例；跳过不算通过。");

        Assert.SkipUnless(File.Exists(path), $"XIHAN_TEST_XLS_FILE 指向的档不存在：{path}");

        List<ExcelImportRow> auto;
        List<ExcelImportRow> declared;

        using (var stream = File.OpenRead(path!))
        {
            // 不给格式：靠 OLE 复合档头判出 Xls，再交给容器解析
            auto = await ReadAll(stream);
        }

        using (var stream = File.OpenRead(path!))
        {
            declared = await ReadAll(stream, new ExcelImportOptions { Format = ExcelImportFormat.Xls });
        }

        Assert.NotEmpty(auto);
        Assert.Equal(auto[0].Values.Keys, declared[0].Values.Keys);
        Assert.Equal(auto[0].RowNumber, declared[0].RowNumber);

        // 表头与至少一个非空取值
        Assert.NotEmpty(auto[0].Values);
        Assert.Contains(auto[0].Values.Values, value => value is not null and not "");
        Assert.True(auto[0].RowNumber >= 2, $"第一数据行的行号应当不小于 2，实际是 {auto[0].RowNumber}。");
    }

    /// <summary>
    /// 导入器实现抽象契约，供门面与注册按接口取用
    /// </summary>
    [Fact]
    public void 导入器实现导入契约()
        => Assert.IsAssignableFrom<IExcelImporter>(new ExcelDataReaderImporter());

    /// <summary>
    /// 行号不随跳过空行重排
    /// </summary>
    [Fact]
    public async Task 跳过空行的档行号仍指向源档位置()
    {
        var rows = await ReadCsv("单号\r\n\r\n\r\nA\r\n"u8.ToArray());

        Assert.Single(rows);
        Assert.Equal(4, rows[0].RowNumber);
    }

    /// <summary>
    /// 应用把 <see cref="XiHanExcelOptions.MaxImportRows" /> 配得更低时，读取器按配置值截断
    /// </summary>
    [Fact]
    public async Task 配置的行数上限低于默认时按配置截断()
    {
        var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportRows = 2 });
        var rows = await ReadAll(
            importer,
            ImportFixtures.Csv("提单号", 5),
            new ExcelImportOptions { Format = ExcelImportFormat.Csv });

        Assert.Equal(2, rows.Count);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);
    }

    /// <summary>
    /// 单次请求的行数上限高于配置上限时被拒，消息报出的是配置值而不是框架默认值
    /// </summary>
    [Fact]
    public async Task 请求上限高于配置上限时被拒并报出配置值()
    {
        var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportRows = 2 });

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await ReadAll(
            importer,
            ImportFixtures.Csv("提单号", 5),
            new ExcelImportOptions { Format = ExcelImportFormat.Csv, MaxRowCount = 3 }));

        Assert.Equal(nameof(ExcelImportOptions.MaxRowCount), failure.ParamName);
        Assert.Contains("2", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HardMaxRows.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 配置的行数上限不是正整数，或高到框架硬上限之外时，构造当场就抛
    /// </summary>
    /// <param name="maxImportRows">要配的进行数上限</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExcelConstants.DefaultMaxImportRows + 1)]
    public void 非法的配置行数上限在构造时抛(int maxImportRows)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportRows = maxImportRows }));

        Assert.Equal(nameof(XiHanExcelOptions.MaxImportRows), failure.ParamName);
    }

    /// <summary>
    /// 无参构造按框架默认硬上限工作
    /// </summary>
    [Fact]
    public async Task 无参构造仍按框架默认硬上限工作()
    {
        var importer = new ExcelDataReaderImporter();
        var rows = await ReadAll(importer, ImportFixtures.Csv("提单号", 3), new ExcelImportOptions { Format = ExcelImportFormat.Csv, MaxRowCount = HardMaxRows });

        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// 档大小超过本次生效的上限时整份档被拒，且判在格式判别之前
    /// </summary>
    /// <remarks>
    /// 喂的是判别不出格式的二进制垃圾。
    /// </remarks>
    [Fact]
    public async Task 超过配置档大小上限的档在格式判别之前被拒()
    {
        var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportBytes = 8 });
        using var garbage = ImportFixtures.BinaryGarbage();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(importer, garbage));

        Assert.Contains("导入的档有 9 字节", failure.Message, StringComparison.Ordinal);
        Assert.Contains("上限 8 字节", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("无法从档头判定导入格式", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    /// <summary>
    /// 档大小恰等上限时照读：上限是「超过才拒」，不是「达到就拒」
    /// </summary>
    [Fact]
    public async Task 档大小恰等上限时照读()
    {
        using var csv = ImportFixtures.Csv("提单号", 2);
        var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportBytes = csv.Length });

        var rows = await ReadAll(importer, csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv });

        Assert.Equal(2, rows.Count);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);
    }

    /// <summary>
    /// 解压比超标的 xlsx 在建立工作簿读取器之前被拒
    /// </summary>
    /// <remarks>
    /// 夹具是一份<u>结构完整、读得通</u>的 xlsx，只有共享字串部件里塞了一段高度重复的内容，
    /// <see cref="ExcelImportOptions.MaxRowCount"/> 设成 1。断言消息点名部件，内部异常为 <c>null</c>。
    /// </remarks>
    [Fact]
    public async Task 解压比超标的xlsx在建立读取器之前被拒()
    {
        using var bomb = HighRatioXlsx(8 * 1024 * 1024);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadAll(bomb, new ExcelImportOptions { MaxRowCount = 1 }));

        Assert.Contains("xl/sharedStrings.xml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("解压比过高", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelConstants.MaxImportCompressionRatio.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    /// <summary>
    /// 解压规模扫档之后流位置归零：同一份 xlsx 连着读两次都读出同一行
    /// </summary>
    [Fact]
    public async Task 解压规模扫档之后流位置归零仍能读出数据()
    {
        using var xlsx = ImportFixtures.OneRowXlsx();

        var first = await ReadAll(xlsx);
        var second = await ReadAll(xlsx);

        Assert.Equal("AWB1", first[0].Values["提单号"]);
        Assert.Equal(first[0].Values.Keys, second[0].Values.Keys);
        Assert.Equal("AWB1", second[0].Values["提单号"]);
    }

    /// <summary>
    /// 解压规模守卫排在建立工作簿读取器<u>之前</u>，不是之后
    /// </summary>
    /// <remarks>
    /// 夹具是「zip 完好、共享字串解压比超标、但缺工作簿部件」的档：断言报的是解压比超标、内部异常为 <c>null</c>，
    /// 而不是「按 Xlsx 读不通」那条转译。
    /// </remarks>
    [Fact]
    public async Task 解压规模守卫排在建立读取器之前()
    {
        using var bomb = HighRatioXlsx(8 * 1024 * 1024, withWorkbookPart: false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(bomb));

        Assert.Contains("xl/sharedStrings.xml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("解压比过高", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("读不通", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    /// <summary>
    /// 解压规模守卫不吃调用方的流：扫过中央目录之后位置归零，流照旧可读
    /// </summary>
    [Fact]
    public async Task 解压规模守卫不吃调用方的流()
    {
        using var bomb = HighRatioXlsx(8 * 1024 * 1024);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(bomb));

        Assert.Equal(0, bomb.Position);
        Assert.True(bomb.CanRead);
        Assert.True(bomb.CanSeek);
    }

    /// <summary>
    /// 列数超过上限的档整份被拒，点名实际列数与上限，且一行都不交出
    /// </summary>
    /// <remarks>
    /// 列数取刚过界的 16,385 与 100,001 两档；档只有两行。
    /// </remarks>
    /// <param name="columns">档里每行的列数</param>
    [Theory]
    [InlineData(ExcelConstants.MaxImportColumns + 1)]
    [InlineData(100_001)]
    public async Task 列数超过上限的档被拒且一行都不交出(int columns)
    {
        using var csv = WideCsv(columns);
        var emitted = new List<ExcelImportRow>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in new ExcelDataReaderImporter()
                .ReadAsync(csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv }, TestContext.Current.CancellationToken))
            {
                emitted.Add(row);
            }
        });

        Assert.Empty(emitted);
        Assert.Contains(columns.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelConstants.MaxImportColumns.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ExcelConstants.MaxImportColumns), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列数恰等上限时照读：上限是「超过才拒」，16,384 列的表头整个建得起来
    /// </summary>
    [Fact]
    public async Task 列数恰等上限时照读()
    {
        using var csv = WideCsv(ExcelConstants.MaxImportColumns);

        var rows = await ReadAll(csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv });

        Assert.Single(rows);
        Assert.Equal(ExcelConstants.MaxImportColumns, rows[0].Values.Count);
        Assert.Equal("h0", rows[0].Values.Keys.First());
        Assert.Equal($"h{ExcelConstants.MaxImportColumns - 1}", rows[0].Values.Keys.Last());
    }

    /// <summary>
    /// 表头只有大小写不同时是两个键，不加重名后缀
    /// </summary>
    /// <remarks>
    /// 判重用的比较器是 <c>Ordinal</c>。
    /// </remarks>
    [Fact]
    public async Task 表头只有大小写不同时不加重名后缀()
    {
        var rows = await ReadCsv("A,a\r\n1,2\r\n"u8.ToArray());

        Assert.Equal(["A", "a"], rows[0].Values.Keys);
        Assert.Equal("1", rows[0].Values["A"]);
        Assert.Equal("2", rows[0].Values["a"]);
    }

    /// <summary>
    /// 没指名上限、撞上的是框架硬上限时要抛；指名过（调用端或配置端收紧过）就按截断处理
    /// </summary>
    /// <remarks>
    /// 直接按数字调用 <c>ImportSharedRules</c> 的判据，端到端的用例在下面两条。
    /// </remarks>
    [Fact]
    public void 撞上限时抛还是截断按有没有指名判()
    {
        // 没指名，生效的就是框架硬上限 → 抛
        Assert.True(ImportSharedRules.ThrowsWhenRowLimitHit(null, ExcelConstants.DefaultMaxImportRows));

        // 配置端把上限收紧过 → 生效上限低于框架硬上限 → 截断
        Assert.False(ImportSharedRules.ThrowsWhenRowLimitHit(null, 2));

        // 调用端指名，哪怕指名的正是框架硬上限 → 截断
        Assert.False(ImportSharedRules.ThrowsWhenRowLimitHit(3, 3));
        Assert.False(ImportSharedRules.ThrowsWhenRowLimitHit(ExcelConstants.DefaultMaxImportRows, ExcelConstants.DefaultMaxImportRows));
    }

    /// <summary>
    /// 没指名上限而档的数据行超过框架硬上限时抛出，已经交出的行照旧交完
    /// </summary>
    [Fact]
    public async Task 未指名上限而数据行超过框架硬上限时抛出()
    {
        using var csv = new MemoryStream(SingleColumnCsv(ExcelConstants.DefaultMaxImportRows + 1));
        var emitted = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in new ExcelDataReaderImporter()
                .ReadAsync(csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv }, TestContext.Current.CancellationToken))
            {
                emitted++;
            }
        });

        Assert.Equal(ExcelConstants.DefaultMaxImportRows, emitted);
        Assert.Contains(ExcelConstants.DefaultMaxImportRows.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Contains("行数上限", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ExcelImportOptions.MaxRowCount), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 档的数据行恰好等于框架硬上限时不抛：抛的条件是「上限之后仍有数据行」，不是「撞到了上限」
    /// </summary>
    [Fact]
    public async Task 数据行恰等框架硬上限时不抛()
    {
        using var csv = new MemoryStream(SingleColumnCsv(ExcelConstants.DefaultMaxImportRows));

        var emitted = 0;

        await foreach (var row in new ExcelDataReaderImporter()
            .ReadAsync(csv, new ExcelImportOptions { Format = ExcelImportFormat.Csv }, TestContext.Current.CancellationToken))
        {
            emitted++;
        }

        Assert.Equal(ExcelConstants.DefaultMaxImportRows, emitted);
    }

    /// <summary>
    /// 约 154:1 的合法高重复工作簿在默认解压比上限下照读，上限收到 100 时被拒
    /// </summary>
    /// <remarks>
    /// 夹具是一份结构合法的工作簿：四万行同值，工作表不写规格里可选的 <c>r</c> 属性，解压比约 154 倍。
    /// 用例先断言夹具比值落在 (100, 200) 之间，再断言默认上限下照读、上限设为 100 时拒收。
    /// </remarks>
    [Fact]
    public async Task 实测合法簇的高重复工作簿在默认解压比上限下照读()
    {
        var bytes = RepeatedRowsXlsx(40_000);
        var noHeader = new ExcelImportOptions { HasHeader = false };

        var (declared, compressed) = PartLengths(bytes, "xl/worksheets/sheet1.xml");
        var ratio = declared / compressed;

        // 夹具比值落在 (100, 200) 之间
        Assert.InRange(ratio, 101, 199);

        using (var accepted = new MemoryStream(bytes))
        {
            var rows = await ReadAll(accepted, noHeader);

            Assert.Equal(40_000, rows.Count);
            Assert.Equal("甲", rows[0].Values["Col1"]);
        }

        using (var rejected = new MemoryStream(bytes))
        {
            var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportCompressionRatio = 100 });

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(importer, rejected, noHeader));

            Assert.Contains("解压比过高", failure.Message, StringComparison.Ordinal);
            Assert.Contains("xl/worksheets/sheet1.xml", failure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(XiHanExcelOptions.MaxImportCompressionRatio), failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 解压比 368:1 的炸弹档在默认上限下被拒，一个部件都不解压
    /// </summary>
    /// <remarks>
    /// 夹具把 <c>xl/sharedStrings.xml</c> 在中央目录里声明的解压后长度改写成「压缩后长度 × 368」。
    /// 断言内部异常为 <c>null</c>，并断言夹具比值高于默认上限。
    /// </remarks>
    [Fact]
    public async Task 实测炸弹量级的解压比在默认上限下仍被拒()
    {
        const int bombRatio = 368;

        using var bomb = DeclaredRatioXlsx(bombRatio);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadAll(bomb, new ExcelImportOptions { MaxRowCount = 1 }));

        Assert.Contains("xl/sharedStrings.xml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("解压比过高", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"是 {bombRatio} 倍", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExcelConstants.MaxImportCompressionRatio.ToString(CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
        Assert.True(bombRatio > ExcelConstants.MaxImportCompressionRatio,
            "夹具的比值必须高过默认上限，否则这条用例验不到「炸弹仍被拒」。");
    }

    /// <summary>
    /// 解压比设成 <c>0</c> 时不判比值，高重复的合法档照读
    /// </summary>
    /// <remarks>
    /// <c>0</c> 与负数表示关掉这条启发式。
    /// </remarks>
    [Fact]
    public async Task 解压比设成零时不判比值()
    {
        using var stream = new MemoryStream(RepeatedRowsXlsx(40_000));
        var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportCompressionRatio = 0 });

        var rows = await ReadAll(importer, stream, new ExcelImportOptions { HasHeader = false });

        Assert.Equal(40_000, rows.Count);
        Assert.Equal("甲", rows[0].Values["Col1"]);
    }

    /// <summary>
    /// 关掉解压比<u>不等于</u>关掉解压侧防护：单部件解压后长度那道绝对上限照常生效
    /// </summary>
    /// <remarks>
    /// 夹具把 zip 中央目录里 <c>xl/worksheets/sheet1.xml</c> 声明的解压后长度改写成超过
    /// <see cref="ExcelConstants.MaxImportEntryDecompressedBytes"/> 的值。断言消息是「超过单个部件的上限」而不是「解压比过高」。
    /// </remarks>
    [Fact]
    public async Task 关掉解压比之后单部件解压后长度上限仍然生效()
    {
        using var source = new MemoryStream(RepeatedRowsXlsx(40_000));
        using var declared = WithDeclaredEntryLength(
            source, "xl/worksheets/sheet1.xml", ExcelConstants.MaxImportEntryDecompressedBytes + 1);
        var importer = new ExcelDataReaderImporter(new XiHanExcelOptions { MaxImportCompressionRatio = 0 });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAll(importer, declared));

        Assert.Contains("超过单个部件的上限", failure.Message, StringComparison.Ordinal);
        Assert.Contains("xl/worksheets/sheet1.xml", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("解压比过高", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    private static async Task<List<ExcelImportRow>> ReadAll(Stream input, ExcelImportOptions? options = null)
    {
        return await ReadAll(new ExcelDataReaderImporter(), input, options);
    }

    /// <summary>
    /// 用指定的读取器取完一份档
    /// </summary>
    private static async Task<List<ExcelImportRow>> ReadAll(ExcelDataReaderImporter importer, Stream input, ExcelImportOptions? options = null)
    {
        var rows = new List<ExcelImportRow>();

        await foreach (var row in importer
            .ReadAsync(input, options, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static Task<List<ExcelImportRow>> ReadCsv(byte[] bytes, Func<ExcelImportOptions, ExcelImportOptions>? mutate = null)
    {
        var options = new ExcelImportOptions { Format = ExcelImportFormat.Csv };

        return ReadAll(new MemoryStream(bytes), mutate is null ? options : mutate(options));
    }

    /// <summary>
    /// 造一份单列 CSV 的字节：表头 <c>A</c>，其后 <paramref name="dataRows"/> 行数据、每行一个 <c>1</c>
    /// </summary>
    /// <param name="dataRows">数据行数</param>
    /// <returns>档字节（UTF-8 无 BOM）</returns>
    /// <remarks>
    /// 每行都是同一个 <c>1\r\n</c>，按千行一块拼。
    /// </remarks>
    private static byte[] SingleColumnCsv(int dataRows)
    {
        var chunk = string.Concat(Enumerable.Repeat("1\r\n", 1000));
        var builder = new StringBuilder(2 + (dataRows * 3)).Append("A\r\n");

        for (var written = 0; written < dataRows / 1000; written++)
        {
            builder.Append(chunk);
        }

        builder.Append(chunk, 0, (dataRows % 1000) * "1\r\n".Length);

        return ImportFixtures.Utf8NoBom.GetBytes(builder.ToString());
    }

    /// <summary>
    /// 造一份每行 <paramref name="columns"/> 列的 CSV：表头是 <c>h0</c>…<c>h{n-1}</c>，数据行每列一个 <c>v</c>
    /// </summary>
    /// <param name="columns">每行的列数</param>
    /// <returns>位置在起点的流</returns>
    /// <remarks>
    /// 表头文案互不相同，建键时不加重名后缀。
    /// </remarks>
    private static MemoryStream WideCsv(int columns)
    {
        var builder = new StringBuilder(columns * 8);

        for (var index = 0; index < columns; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('h').Append(index.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append("\r\n");

        for (var index = 0; index < columns; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('v');
        }

        builder.Append("\r\n");

        return new MemoryStream(ImportFixtures.Utf8NoBom.GetBytes(builder.ToString()));
    }

    /// <summary>
    /// 造一份解压比超标的 xlsx：共享字串部件里塞满高度重复的内容
    /// </summary>
    /// <param name="payloadBytes">共享字串里那段重复内容的字节数</param>
    /// <param name="withWorkbookPart">是否写出工作簿部件；<c>false</c> 时这份 zip 建不起工作簿读取器</param>
    /// <returns>位置在起点的 xlsx 流</returns>
    /// <remarks>
    /// 手工按 zip 部件写。<paramref name="withWorkbookPart"/> 为 <c>true</c> 时工作表只引用共享字串的第 0 项，档读得通。
    /// 重复内容分块写，共享字串用 <see cref="CompressionLevel.Optimal"/> 压。
    /// </remarks>
    private static MemoryStream HighRatioXlsx(int payloadBytes, bool withWorkbookPart = true)
    {
        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>
                """);

            WriteEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);

            if (withWorkbookPart)
            {
                WriteEntry(zip, "xl/workbook.xml",
                    """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="导入" sheetId="1" r:id="rId1"/></sheets></workbook>
                    """);
            }

            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>
                """);

            WriteEntry(zip, "xl/worksheets/sheet1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row><row r="2"><c r="A2" t="s"><v>0</v></c></row></sheetData></worksheet>
                """);

            var entry = zip.CreateEntry("xl/sharedStrings.xml", CompressionLevel.Optimal);

            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="1" uniqueCount="1"><si><t>
                """);

            var chunk = new string('x', 4096);
            for (var written = 0; written < payloadBytes; written += chunk.Length)
            {
                writer.Write(chunk);
            }

            writer.Write("</t></si></sst>");
        }

        stream.Position = 0;

        return stream;
    }

    /// <summary>
    /// 往 zip 里写一个文字部件
    /// </summary>
    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// 造一份<u>合法</u>但解压比很高的 xlsx：<paramref name="rows"/> 行同值，工作表不写规格里可选的 <c>r</c> 属性
    /// </summary>
    /// <param name="rows">行数（含被当作表头的第一行）</param>
    /// <returns>档字节</returns>
    /// <remarks>
    /// 与 <see cref="HighRatioXlsx"/> 不同，这一份是<u>正常工作簿</u>：每行一列、值都是共享字串第 0 项。
    /// <c>row</c> 与 <c>c</c> 不写 <c>r</c> 属性，整段 <c>sheetData</c> 逐字节重复，四万行时解压比约 154 倍。
    /// 工作表用 <see cref="CompressionLevel.Optimal"/> 压，四万行时该部件解压后约 1.2 MiB，
    /// 超过「比值只判不小于 1 MiB 的部件」那道门槛。
    /// </remarks>
    private static byte[] RepeatedRowsXlsx(int rows)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>
                """);

            WriteEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);

            WriteEntry(zip, "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="导入" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);

            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>
                """);

            WriteEntry(zip, "xl/sharedStrings.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="1" uniqueCount="1"><si><t>甲</t></si></sst>
                """);

            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);

            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false), 1 << 16);
            writer.Write(
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                """);

            for (var row = 0; row < rows; row++)
            {
                writer.Write("<row><c t=\"s\"><v>0</v></c></row>");
            }

            writer.Write("</sheetData></worksheet>");
        }

        return stream.ToArray();
    }

    /// <summary>
    /// 把 zip 中央目录里某个部件声明的「解压后长度」改写成给定值
    /// </summary>
    /// <param name="source">原档</param>
    /// <param name="entryName">要改写的部件名</param>
    /// <param name="declaredLength">要声明的解压后长度</param>
    /// <returns>改写后的档流，位置在起点</returns>
    /// <remarks>
    /// 中央目录记录的固定部分长 46 字节：签名 <c>50 4B 01 02</c> 之后 <c>+20</c> 是压缩后长度、
    /// <c>+24</c> 是解压后长度、<c>+28</c> 是档名长度，档名紧跟在 <c>+46</c>。改写的是 <c>+24</c> 这个声明值，
    /// 记录按档名比对而不是取第一条。
    /// </remarks>
    private static MemoryStream WithDeclaredEntryLength(MemoryStream source, string entryName, long declaredLength)
    {
        var bytes = source.ToArray();
        var name = Encoding.ASCII.GetBytes(entryName);

        for (var offset = 0; offset + 46 <= bytes.Length; offset++)
        {
            if (bytes[offset] != 0x50 || bytes[offset + 1] != 0x4B ||
                bytes[offset + 2] != 0x01 || bytes[offset + 3] != 0x02)
            {
                continue;
            }

            var nameLength = BitConverter.ToUInt16(bytes, offset + 28);

            if (nameLength != name.Length || offset + 46 + nameLength > bytes.Length)
            {
                continue;
            }

            if (!bytes.AsSpan(offset + 46, nameLength).SequenceEqual(name))
            {
                continue;
            }

            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 24, 4), (uint)declaredLength);

            return new MemoryStream(bytes);
        }

        throw new InvalidOperationException($"夹具在中央目录里找不到部件 {entryName}。");
    }

    /// <summary>
    /// 读出 zip 里某个部件在中央目录里声明的解压后长度与压缩后长度
    /// </summary>
    /// <param name="bytes">档字节</param>
    /// <param name="entryName">部件名</param>
    /// <returns>声明的解压后长度与压缩后长度</returns>
    private static (long Declared, long Compressed) PartLengths(byte[] bytes, string entryName)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var entry = zip.Entries.FirstOrDefault(item => item.FullName == entryName)
            ?? throw new InvalidOperationException($"夹具在中央目录里找不到部件 {entryName}。");

        return (entry.Length, entry.CompressedLength);
    }

    /// <summary>
    /// 造一份解压比恰为 <paramref name="ratio"/> 的 xlsx：炸弹形态的档，声明的解压后长度按比值改写
    /// </summary>
    /// <param name="ratio">要让守卫算出来的解压比（解压后长度 ÷ 压缩后长度）</param>
    /// <returns>位置在起点的 xlsx 流</returns>
    /// <remarks>
    /// 先按 <see cref="HighRatioXlsx"/> 造档，量出共享字串部件压缩后的实际长度，
    /// 再把中央目录里声明的解压后长度改写成「压缩后长度 × 比值」，本地档头不改。
    /// </remarks>
    private static MemoryStream DeclaredRatioXlsx(int ratio)
    {
        using var source = HighRatioXlsx(8 * 1024 * 1024);

        var (_, compressed) = PartLengths(source.ToArray(), "xl/sharedStrings.xml");

        return WithDeclaredEntryLength(source, "xl/sharedStrings.xml", compressed * ratio);
    }
}
