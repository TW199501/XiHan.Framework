// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Importing;

/// <summary>
/// ExcelDataReader 导入测试：签章判别、表头处理、文字档编码回退与行数上限
/// </summary>
/// <remarks>
/// <para>
/// 断言只看对外可观察的东西：键名与键序、取值与型别、行号、抛出的异常类型与消息。夹具一律当场造
/// （<see cref="ImportFixtures" />），仓库里不留二进制档。
/// </para>
/// <para>
/// 编码相关的期望值来自实测而不是推测：无 BOM 的 Big5 档若不交回正确编码，读取器按 <c>windows-1252</c>
/// 解出来的是「´£³æ¸¹」这种看着也像字的乱码（取证 <c>t8-probe-exceldreader-behavior.txt</c> 的 B2/B4），
/// 因此下面既断「回退后读到正字」，也断「编码指错时报错而不是吐乱码」。
/// </para>
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
    /// 被引号包住换行的记录之后，行号按记录数递增而不是按物理行补号——这条把实测到的口径钉住
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
    /// TrimValues 默认不剥：值里的首尾空白是业务数据，剥了就不能逐字往返
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
    /// with 派生副本不改动原选项对象——取 record 而不是 class 的存在证明
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
    /// 枚举结束后调用方的流仍然可用：读取器不关流（库默认会把流一起关掉，实测得来）
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

        // xlsx 走同一条复位：容器解析器实测本来也会回到起点
        using var xlsx = ImportFixtures.OneRowXlsx();
        xlsx.Position = 200;

        var sheetRows = await ReadAll(xlsx);
        Assert.Equal("AWB1", sheetRows[0].Values["提单号"]);
    }

    /// <summary>
    /// 不可定位的流在建立读取器之前就被拒，不把库的 NotSupportedException 当契约
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
    /// <remarks>
    /// 抽象契约在抽象包里，它不引用 ExcelDataReader，因此库自己的异常型别不能成为对外承诺（取证
    /// <c>t8-probe-exception-types.txt</c> 列出的三个库异常型别全部只继承 <see cref="Exception"/>）。
    /// </remarks>
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
    /// 档头真是 zip 签名但容器截断时同样转译，不把库的容器异常当契约
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
    /// 容器失败之外，编码解码失败不被转译：<c>DecoderFallbackException</c> 是 <see cref="ArgumentException"/>
    /// 的后代，收口时绝不能连它一起吞进容器异常
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
    /// <remarks>
    /// 这一条守的是 <c>TrimWhiteSpace = false</c> 那行设置：库在 3.9.0 上默认开着它，删掉那行设置后
    /// CSV 的 <c>TrimValues=false</c> 会当场变成谎话（xlsx 路径不受影响，所以只有文字档断言能抓它）。
    /// 变异检查的输出见 <c>t8-fix-1-mutation-*.log</c>。
    /// </remarks>
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
    /// 只有表头一行的文字档交出空序列（Review Focus 第 2 项的文字档半边）
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
    /// <remarks>
    /// <para>
    /// 本任务公开了 <see cref="ExcelImportFormat.Xls"/>，但夹具造不出旧版二进制工作簿——ClosedXML 只写 xlsx，
    /// 拿 OLE 档头喂它只会撞到 <c>ArgumentException</c>（取证 <c>t8-probe-exceldreader-behavior.txt</c> 的 H7 与
    /// <c>t8-probe2-rowcount-and-decode.txt</c> 的 O4）。于是「读 .xls」这条能力在 CI 上只有签章判别的证据、
    /// 没有从容器里读出数据的证据。
    /// </para>
    /// <para>
    /// <b>跳过不算通过。</b>这条要由持有真实 <c>.xls</c> 的人把 <c>XIHAN_TEST_XLS_FILE</c> 指过去才会执行；
    /// 不设该变量时它是未验证项，不是绿灯。
    /// </para>
    /// </remarks>
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

        // 表头与至少一个非空取值：整份档读出一堆 null 也算「成功」的话，这条用例就没有意义
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
    /// 行号不随跳过空行重排，这是错误报表能定位到源档那一行的前提
    /// </summary>
    [Fact]
    public async Task 跳过空行的档行号仍指向源档位置()
    {
        var rows = await ReadCsv("单号\r\n\r\n\r\nA\r\n"u8.ToArray());

        Assert.Single(rows);
        Assert.Equal(4, rows[0].RowNumber);
    }

    private static async Task<List<ExcelImportRow>> ReadAll(Stream input, ExcelImportOptions? options = null)
    {
        var rows = new List<ExcelImportRow>();

        await foreach (var row in new ExcelDataReaderImporter()
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
}
