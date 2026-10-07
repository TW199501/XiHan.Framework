// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Excel.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Exporting;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Exporting;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Importing;

/// <summary>
/// 固定宽度文字导入测试：按字节切列、字节层切行、编码解码、列定义校验、留痕与导入门面路由
/// </summary>
/// <remarks>
/// <para>
/// 断言只看对外可观察的东西：键名与键序、逐列取值、行号、留痕日志、抛出的异常类型与消息。
/// 夹具一律当场造字节（<see cref="ImportFixtures" />），仓库里不留文字档样本。
/// </para>
/// <para>
/// <c>Big5</c> 下一个汉字占 2 字节（<c>中</c> = <c>A4 A4</c>），<c>U+2028</c> 在 UTF-8 下占 3 字节而 <c>U+0085</c> 占 2 字节。
/// </para>
/// <para>
/// 定宽路径把 <see cref="ExcelImportOptions.HasHeader"/> 按「无表头」处理：键名恒取列定义的键、首行就是数据、
/// 行号从 <c>1</c> 起。每条用例都<u>显式写出</u>它所依赖的 <c>HasHeader</c> 取值。
/// </para>
/// </remarks>
public class FixedWidthTextImporterTests
{
    /// <summary>
    /// 替换字符，用来判断读回来的是不是解码失败的产物
    /// </summary>
    private const string ReplacementChar = "\uFFFD";

    /// <summary>
    /// 前 4 字节是两个 Big5 汉字、后 2 字节是 AB：按字节切列不会把一个全形字当一格
    /// </summary>
    [Fact]
    public async Task 按字节切列_Big5全形字不错位()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "中中AB\r\n");

        var rows = await Read(bytes, [new("A", 4), new("B", 2)], hasHeader: false);

        // "中中AB" 在 Big5 下是 6 字节（A4 A4 A4 A4 41 42），加行尾共 8 字节；按字符切只有 4 格
        Assert.Equal(8, bytes.Length);
        Assert.Equal("中中", rows[0].Values["A"]);
        Assert.Equal("AB", rows[0].Values["B"]);
    }

    /// <summary>
    /// 全形字之后的列不跟着错位：A 列宽 3 收下一个汉字加一个 ASCII，B 列仍拿到下一个 ASCII
    /// </summary>
    [Fact]
    public async Task 全形字之后的列不跟着错位()
    {
        // Big5: "中"=2 字节，"A"=1 字节 → A 列宽 3 应含 "中A"，B 列宽 1 得 "B"
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "中AB\r\n");

        var rows = await Read(bytes, [new("A", 3), new("B", 1)], hasHeader: false);

        Assert.Equal("中A", rows[0].Values["A"]);
        Assert.Equal("B", rows[0].Values["B"]);
    }

    /// <summary>
    /// 行字节数不足列宽总和时，缺的列交出空字串而不是 null 或抛
    /// </summary>
    [Fact]
    public async Task 行长度不足时缺的列视为空值()
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("A", 2), new("B", 2)], hasHeader: false);

        Assert.Equal("AB", rows[0].Values["A"]);
        Assert.Equal("", rows[0].Values["B"]);
    }

    /// <summary>
    /// 行字节数超出列宽总和时，多出的部分被忽略，后面的列不跟着位移
    /// </summary>
    [Fact]
    public async Task 行超长时多出部分被忽略()
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("ABCDE\r\n"), [new("A", 2), new("B", 2)], hasHeader: false);

        Assert.Equal(new object?[] { "AB", "CD" }, rows[0].Values.Values);
    }

    /// <summary>
    /// 没给列定义时抛，不把整行当成一列交回
    /// </summary>
    [Fact]
    public async Task 未给FixedColumns时抛而不是整行当一列()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), fields: null, hasHeader: false));

        Assert.Contains(nameof(ExcelImportOptions.FixedColumns), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行号是 1 起始的记录序号，与分隔符路径同口径：不随跳行重排
    /// </summary>
    [Fact]
    public async Task 行号沿用记录序号与分隔符模式一致()
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("x\r\ny\r\n"), [new("A", 1)], hasHeader: false);

        Assert.Equal([1, 2], rows.Select(row => row.RowNumber));
    }

    /// <summary>
    /// 取值集合的键序就是列定义给出的顺序，不是按键排序
    /// </summary>
    [Fact]
    public async Task 键序就是列定义的顺序()
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("乙", 1), new("甲", 1)], hasHeader: false);

        Assert.Equal(["乙", "甲"], rows[0].Values.Keys);
    }

    /// <summary>
    /// 三种行尾都在字节层切分，档里不带 \r\n 也能正常分行
    /// </summary>
    /// <param name="newLine">本份档使用的行尾序列</param>
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public async Task 三种行尾都在字节层切分(string newLine)
    {
        var rows = await Read(
            ImportFixtures.Utf8NoBom.GetBytes($"A{newLine}B"),
            [new("A", 1)],
            hasHeader: false);

        Assert.Equal(["A", "B"], rows.Select(row => (string?)row.Values["A"]));
        Assert.Equal([1, 2], rows.Select(row => row.RowNumber));
    }

    /// <summary>
    /// 行尾落在一次读取的边界上时，跨段的 \n 属于同一个行尾而不是凭空多出的空行
    /// </summary>
    /// <remarks>
    /// 第一行的长度在读取缓冲边界前后各取一档，保证总有一档把 <c>\r</c> 留在段尾、<c>\n</c> 推到下一段开头。
    /// </remarks>
    /// <param name="fillerLength">第一行的字节长度，行尾从这一格之后开始</param>
    [Theory]
    [InlineData(4094)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    public async Task 行尾落在读取边界上时不拆成两行(int fillerLength)
    {
        var text = new string('x', fillerLength) + "\r\n" + "y\r\n";

        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes(text), [new("A", 1)], hasHeader: false);

        Assert.Equal(["x", "y"], rows.Select(row => (string?)row.Values["A"]));
        Assert.Equal([1, 2], rows.Select(row => row.RowNumber));
    }

    /// <summary>
    /// U+2028／U+2029／U+0085 在定宽档里不算行尾，导入侧只按 \r 与 \n 分行
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本组件的行尾定义只有 <c>\r\n</c>／<c>\n</c>／<c>\r</c>，与 <see cref="ExcelTextOptions.NewLine"/> 写出的序列一致。
    /// </para>
    /// <para>
    /// 同一条用例记下 .NET 10 上 <c>StringReader.ReadLine()</c> 的实际行为：它在这三个字符处<u>不</u>分行。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 全形行分隔符在定宽档里不算行尾()
    {
        var text = "甲\u2028乙\u2029丙\u0085丁";

        // UTF-8 下这段是 3+3+3+3+3+2+3 = 20 字节，正好一列
        Assert.Equal(20, ImportFixtures.Utf8NoBom.GetByteCount(text));
        Assert.Equal(1, CountReadLines(text));

        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes(text + "\r\n"), [new("A", 20)], hasHeader: false);

        Assert.Single(rows);
        Assert.Equal(text, rows[0].Values["A"]);
    }

    /// <summary>
    /// 列边界落在多字节字符中间时按解码失败停下，不交出半个字
    /// </summary>
    /// <remarks>
    /// 断言抛 <see cref="DecoderFallbackException"/>，而不是交出替换字符或私有区字符。
    /// </remarks>
    [Fact]
    public async Task 列边界切在多字节字符中间时解码失败不交回半个字()
    {
        // Big5 "中中" = A4 A4 A4 A4：A 列取 3 字节会留下一个落单的高位字节
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "中中\r\n");

        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            await Read(bytes, [new("A", 3), new("B", 1)], hasHeader: false,
                mutate: o => o with { TextEncodingName = "big5" }));
    }

    /// <summary>
    /// 档带 UTF-8 BOM 时前导字节不属于第一列的数据，三种编码请求下都读不回 U+FEFF
    /// </summary>
    /// <remarks>
    /// 指名为不带 BOM 的 <c>utf-8</c> 时同样剥掉 BOM。
    /// </remarks>
    /// <param name="encodingName">指名的编码，<c>null</c> 走自动判别</param>
    [Theory]
    [InlineData(null)]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    public async Task 带UTF8BOM的定宽档第一列不夹前导字符(string? encodingName)
    {
        using var input = ImportFixtures.TextWithUtf8Bom("AWB1x\r\n");

        var rows = await Read(input, [new("A", 5)], hasHeader: false,
            mutate: o => o with { TextEncodingName = encodingName });

        var value = (string)rows[0].Values["A"]!;
        Assert.Equal("AWB1x", value);
        Assert.DoesNotContain("\uFEFF", value, StringComparison.Ordinal);
    }

    /// <summary>
    /// 无 BOM 的非 UTF-8 Big5 定宽档回退后读到正字，不是看着也像字的乱码
    /// </summary>
    [Fact]
    public async Task 无BOM且非UTF8的Big5定宽档回退后不乱码()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號AB\r\n");

        var rows = await Read(bytes, [new("A", 6), new("B", 2)], hasHeader: false);

        Assert.Equal("提單號", rows[0].Values["A"]);
        Assert.Equal("AB", rows[0].Values["B"]);

        var key = (string)rows[0].Values["A"]!;
        Assert.DoesNotContain(ReplacementChar, key, StringComparison.Ordinal);
        Assert.DoesNotContain("\u00B4", key, StringComparison.Ordinal);
    }

    /// <summary>
    /// 定宽路径的编码判别不另起一套：指错编码照样撞到解码错误，而不是吐出另一种语言的字
    /// </summary>
    [Fact]
    public async Task 定宽档编码指错时解码失败而不交回乱码()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號\r\n");

        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            await Read(bytes, [new("A", 6)], hasHeader: false, mutate: o => o with { TextEncodingName = "utf-8" }));
    }

    /// <summary>
    /// 未知编码名沿用解析器给出的 ArgumentException 与 ParamName，定宽路径不换成别的型别
    /// </summary>
    [Fact]
    public async Task 定宽路径的未知编码名沿用解析器的ArgumentException()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("A", 2)], hasHeader: false,
                mutate: o => o with { TextEncodingName = "utf-99" }));

        Assert.Equal(nameof(ExcelImportOptions.TextEncodingName), failure.ParamName);
        Assert.Contains("utf-99", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// UTF-16／UTF-32 在定宽路径直接拒读
    /// </summary>
    /// <param name="encodingName">要指的宽字节编码名</param>
    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public async Task 宽字节编码在定宽路径被拒(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("AB\r\n")];

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(bytes, [new("A", 2)], hasHeader: false, mutate: o => o with { TextEncodingName = encodingName }));

        Assert.Contains(encodingName, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("字节", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 自动判别撞到 UTF-16 BOM 时同样拒读，不靠「调用方没指名」放宽
    /// </summary>
    [Fact]
    public async Task 自动判别出的UTF16定宽档同样被拒()
    {
        byte[] bytes = [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("AB\r\n")];

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(bytes, [new("A", 2)], hasHeader: false));

        Assert.Contains("utf-16", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 行尾编不成单字节（EBCDIC）与有状态编码（ISO-2022）都在定宽导入被拒，且一条行都不交出
    /// </summary>
    /// <remarks>
    /// 判据与导出侧共用 <c>TextWriterHelper.ValidateFixedWidthEncoding</c>（两条检查：行尾字节唯一可寻址、
    /// 分段编码等于整体编码）。用手工枚举断言一行都不交出，消息点名不过的是哪一条检查。
    /// </remarks>
    /// <param name="encodingName">要指的坏编码</param>
    /// <param name="expectedCheck">消息应点名的检查项</param>
    [Theory]
    [InlineData("IBM037", "检查一")]
    [InlineData("iso-2022-jp", "检查二")]
    public async Task 不能按字节切列的编码在定宽导入被拒且零行交出(string encodingName, string expectedCheck)
    {
        var encoding = Encoding.GetEncoding(encodingName);

        // 两行、每行两栏，声明列宽 2 + 2
        var bytes = encoding.GetBytes("AB\r\nCD\r\n");

        var (rows, failure) = await ReadWithOutcomeAsync(bytes, encodingName);

        Assert.NotNull(failure);
        Assert.Empty(rows);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains(expectedCheck, failure!.Message, StringComparison.Ordinal);
        Assert.Contains(encodingName, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("字节", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 守卫不多拒：合法的单字节与多字节编码照常逐行读出定宽档
    /// </summary>
    /// <remarks>
    /// Big5、Shift-JIS、GB18030 等多字节编码满足「行尾字节唯一可寻址」与「分段编码等于整体编码」两条性质，
    /// 正例按编码族逐个列出。
    /// </remarks>
    /// <param name="encodingName">合法编码名</param>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("big5")]
    [InlineData("shift_jis")]
    [InlineData("gb18030")]
    [InlineData("windows-1252")]
    public async Task 合法编码在定宽导入照常逐行读出(string encodingName)
    {
        // 内容取 ASCII：五种编码下 "AB"／"CD" 都是 2 字节，声明列宽 2 + 2 在每个编码里都成立
        var bytes = Encoding.ASCII.GetBytes("AB\r\nCD\r\n");

        var rows = await Read(bytes, [new("A", 2), new("B", 2)], hasHeader: false,
            mutate: o => o with { TextEncodingName = encodingName });

        Assert.Equal(2, rows.Count);
        Assert.Equal("AB", rows[0].Values["A"]);
        Assert.Equal("CD", rows[1].Values["A"]);
    }

    /// <summary>
    /// 列清单是空集合时抛，不交出一列都没有的行
    /// </summary>
    [Fact]
    public async Task 定宽列集合为空时抛而不是交出空行()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [], hasHeader: false));

        Assert.Contains(nameof(ExcelImportOptions.FixedColumns), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列宽 0 与负数不是「未指定」的另一种写法，抛出的消息点名该列
    /// </summary>
    /// <param name="widthBytes">非法列宽</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 定宽列宽非正数时抛并点名该列(int widthBytes)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("提單號", widthBytes)], hasHeader: false));

        Assert.Contains("提單號", failure.Message, StringComparison.Ordinal);
        Assert.Contains("正整数", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列键重复时抛，不让后一列盖掉前一列
    /// </summary>
    [Fact]
    public async Task 定宽列键重复时抛而不是后列盖掉前列()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("A", 1), new("A", 1)], hasHeader: false));

        Assert.Contains("重复", failure.Message, StringComparison.Ordinal);
        Assert.Contains("A", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列键是空字串或仅含空白时抛，并点名是第几列
    /// </summary>
    /// <param name="key">非法列键</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 定宽列键为空时抛并点名第几列(string key)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new(key, 2)], hasHeader: false));

        Assert.Contains("第 1 列", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列宽总和超过硬上限时抛
    /// </summary>
    [Fact]
    public async Task 定宽列宽总和超过单行缓冲上限时抛()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(
                ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"),
                [new("A", ExcelConstants.MaxFixedRowWidthBytes), new("B", 1)],
                hasHeader: false));

        Assert.Contains("缓冲上限", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ExcelConstants.MaxFixedRowWidthBytes), failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列宽总和正好等于硬上限时不抛
    /// </summary>
    /// <remarks>
    /// <c>B</c> 列给 1 字节，整行列宽总和恰好是 <see cref="ExcelConstants.MaxFixedRowWidthBytes"/>。
    /// </remarks>
    [Fact]
    public async Task 定宽列宽总和正好等于硬上限时不抛()
    {
        var rows = await Read(
            ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"),
            [new("A", ExcelConstants.MaxFixedRowWidthBytes - 1), new("B", 1)],
            hasHeader: false);

        Assert.Single(rows);
        Assert.Equal("AB", rows[0].Values["A"]);
        Assert.Equal("", rows[0].Values["B"]);
    }

    /// <summary>
    /// 列清单里有空项时抛并点名第几项
    /// </summary>
    [Fact]
    public async Task 定宽列清单里有空项时抛并点名第几项()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("A", 1), null!], hasHeader: false));

        Assert.Contains("第 2 项", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 定宽路径把 HasHeader 按无表头处理：两种取值都不吃行，键名恒取列定义的键，首行行号是 1
    /// </summary>
    /// <param name="hasHeader">用例显式给出的表头声明</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 定宽路径按无表头处理且键名恒取列定义(bool hasHeader)
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("AWB12\r\nXY\r\n"), [new("A", 5)], hasHeader: hasHeader);

        Assert.Equal(2, rows.Count);
        Assert.Equal([1, 2], rows.Select(row => row.RowNumber));
        Assert.Equal("AWB12", rows[0].Values["A"]);
        Assert.DoesNotContain("AWB12", rows[0].Values.Keys);
    }

    /// <summary>
    /// 前导行按 HeaderRowIndex 丢掉，丢掉的行仍占行号
    /// </summary>
    /// <remarks>
    /// 丢掉的前导行不切列，也不为它们留痕。
    /// </remarks>
    [Fact]
    public async Task 定宽路径按HeaderRowIndex丢掉前导行且行号含它()
    {
        var rows = await Read(
            ImportFixtures.Utf8NoBom.GetBytes("HEADER1\r\nBANNER2\r\nAWB12\r\n"),
            [new("A", 7)],
            hasHeader: false,
            mutate: o => o with { HeaderRowIndex = 2 });

        Assert.Single(rows);
        Assert.Equal(3, rows[0].RowNumber);
        Assert.Equal("AWB12", rows[0].Values["A"]);
    }

    /// <summary>
    /// 空行默认跳过且行号不重排
    /// </summary>
    [Fact]
    public async Task 定宽空行默认跳过且行号不重排()
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("A\r\n\r\nB\r\n"), [new("A", 1)], hasHeader: false);

        Assert.Equal(["A", "B"], rows.Select(row => (string?)row.Values["A"]));
        Assert.Equal([1, 3], rows.Select(row => row.RowNumber));
    }

    /// <summary>
    /// 不跳空行时空行以全空值集合交出，行数与源档一致
    /// </summary>
    [Fact]
    public async Task 不跳空行时交出全空值行()
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes("A\r\n\r\nB\r\n"), [new("A", 1)], hasHeader: false,
            mutate: o => o with { SkipEmptyRows = false });

        Assert.Equal(3, rows.Count);
        Assert.Equal([1, 2, 3], rows.Select(row => row.RowNumber));
        Assert.Equal("", rows[1].Values["A"]);
    }

    /// <summary>
    /// TrimValues 在定宽路径逐项生效：默认不剥补位空格，读回的值与源档逐字相同
    /// </summary>
    /// <param name="trimValues">是否剥取值首尾空白</param>
    /// <param name="expected">期望取值</param>
    [Theory]
    [InlineData(false, " AWB1 ")]
    [InlineData(true, "AWB1")]
    public async Task TrimValues在定宽路径逐项生效(bool trimValues, string expected)
    {
        var rows = await Read(ImportFixtures.Utf8NoBom.GetBytes(" AWB1 \r\n"), [new("A", 7)], hasHeader: false,
            mutate: o => o with { TrimValues = trimValues });

        Assert.Equal(expected, rows[0].Values["A"]);
    }

    /// <summary>
    /// MaxRowCount 在定宽路径同样收紧行数，行号仍是源档行号
    /// </summary>
    [Fact]
    public async Task MaxRowCount截定宽档且不影响行号连续性()
    {
        var rows = await Read(
            ImportFixtures.Utf8NoBom.GetBytes("A\r\nB\r\nC\r\nD\r\nE\r\n"),
            [new("A", 1)],
            hasHeader: false,
            mutate: o => o with { MaxRowCount = 2 });

        Assert.Equal(["A", "B"], rows.Select(row => (string?)row.Values["A"]));
        Assert.Equal([1, 2], rows.Select(row => row.RowNumber));
    }

    /// <summary>
    /// 越界上限先于列校验被拒
    /// </summary>
    /// <remarks>
    /// 与分隔符路径同顺序：同一条缺列定义的档上，越界的 MaxRowCount 报「上限越界」而不是「没给列定义」。
    /// </remarks>
    [Fact]
    public async Task 越界上限在定宽路径先于列校验被拒()
    {
        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Read(ImportFixtures.Utf8NoBom.GetBytes("A\r\n"), fields: null, hasHeader: false,
                mutate: o => o with { MaxRowCount = ExcelConstants.DefaultMaxImportRows + 1 }));

        Assert.Equal(nameof(ExcelImportOptions.MaxRowCount), failure.ParamName);
    }

    /// <summary>
    /// 配置的行数上限在定宽路径同样收紧，两条导入路径认同一个上限
    /// </summary>
    [Fact]
    public async Task 配置的行数上限在定宽路径同样收紧()
    {
        var importer = new FixedWidthTextImporter(new XiHanExcelOptions { MaxImportRows = 2 }, NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = ImportFixtures.Text("A\r\nB\r\nC\r\nD\r\n");

        var rows = await AsyncCollector.CollectAsync(importer.ReadAsync(
            stream, ColumnsOnly([new("A", 1)]), TestContext.Current.CancellationToken));

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// 配置的行数上限高于框架硬上限时构造当场抛，不夹回到上限里
    /// </summary>
    [Fact]
    public void 配置的行数上限高于框架硬上限时定宽路径构造即抛()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FixedWidthTextImporter(new XiHanExcelOptions { MaxImportRows = ExcelConstants.DefaultMaxImportRows + 1 }, NullLogger<FixedWidthTextImporter>.Instance));

        Assert.Equal(nameof(XiHanExcelOptions.MaxImportRows), failure.ParamName);
    }

    /// <summary>
    /// 档大小超过配置上限时定宽路径整份拒收，判据与容器路径共用一份
    /// </summary>
    [Fact]
    public async Task 超过配置档大小上限的定宽档被拒()
    {
        var importer = new FixedWidthTextImporter(
            new XiHanExcelOptions { MaxImportBytes = 4 },
            NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = ImportFixtures.Text("AB\r\nCD\r\n");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AsyncCollector.CollectAsync(importer.ReadAsync(
                stream, ColumnsOnly([new("A", 1), new("B", 1)]), TestContext.Current.CancellationToken)));

        Assert.Contains("导入的档有 8 字节", failure.Message, StringComparison.Ordinal);
        Assert.Contains("上限 4 字节", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    /// <summary>
    /// 定宽档大小恰等上限时照读：上限是「超过才拒」，与容器路径同一个口径
    /// </summary>
    [Fact]
    public async Task 定宽档大小恰等上限时照读()
    {
        var importer = new FixedWidthTextImporter(
            new XiHanExcelOptions { MaxImportBytes = 8 },
            NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = ImportFixtures.Text("AB\r\nCD\r\n");

        var rows = await AsyncCollector.CollectAsync(importer.ReadAsync(
            stream, ColumnsOnly([new("A", 1), new("B", 1)]), TestContext.Current.CancellationToken));

        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0].Values["A"]);
        Assert.Equal("C", rows[1].Values["A"]);
    }

    /// <summary>
    /// 没指名上限而数据行超过框架硬上限时，定宽路径同样抛出而不是静默截断
    /// </summary>
    /// <remarks>
    /// 判据与容器路径共用 <c>ImportSharedRules</c>；抛之前已交出的行一行不少。
    /// </remarks>
    [Fact]
    public async Task 未指名上限而数据行超过框架硬上限时定宽路径抛出()
    {
        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = new MemoryStream(SingleColumnRows(ExcelConstants.DefaultMaxImportRows + 1));
        var emitted = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in importer.ReadAsync(
                stream, ColumnsOnly([new("A", 1)]), TestContext.Current.CancellationToken))
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
    /// 定宽档的数据行恰好等于框架硬上限时不抛，与容器路径同一个口径
    /// </summary>
    [Fact]
    public async Task 定宽档数据行恰等框架硬上限时不抛()
    {
        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = new MemoryStream(SingleColumnRows(ExcelConstants.DefaultMaxImportRows));
        var emitted = 0;

        await foreach (var row in importer.ReadAsync(
            stream, ColumnsOnly([new("A", 1)]), TestContext.Current.CancellationToken))
        {
            emitted++;
        }

        Assert.Equal(ExcelConstants.DefaultMaxImportRows, emitted);
    }

    /// <summary>
    /// 只给日志器的构造照旧可用，且上限仍是框架默认硬上限
    /// </summary>
    [Fact]
    public async Task 只给日志器的构造仍按框架默认上限工作()
    {
        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = ImportFixtures.Text("A\r\nB\r\n");

        var rows = await AsyncCollector.CollectAsync(importer.ReadAsync(
            stream,
            ColumnsOnly([new("A", 1)]) with { MaxRowCount = ExcelConstants.DefaultMaxImportRows },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// 读取从流起点开始且不关闭调用方的流，枚举完还能重读
    /// </summary>
    [Fact]
    public async Task 定宽读取从流起点开始且不关闭调用方的流()
    {
        using var stream = ImportFixtures.Text("AB\r\n");
        stream.Position = 1;

        var first = await Read(stream, [new("A", 1), new("B", 1)], hasHeader: false);
        var second = await Read(stream, [new("A", 1), new("B", 1)], hasHeader: false);

        Assert.Equal("A", first[0].Values["A"]);
        Assert.Equal("B", first[0].Values["B"]);
        Assert.True(stream.CanRead);
        Assert.Equal("A", second[0].Values["A"]);
    }

    /// <summary>
    /// 不可定位的输入流在就读之前被拒
    /// </summary>
    [Fact]
    public async Task 不可定位的定宽输入流被拒()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n")));

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Read(input, [new("A", 1), new("B", 1)], hasHeader: false));

        Assert.Equal("input", failure.ParamName);
        Assert.Contains("可定位", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 空档与只有一个行尾的档交出空序列，不抛也不造出一条空行
    /// </summary>
    /// <remarks>
    /// 「只有行尾」那一条是在 <see cref="ExcelImportOptions.SkipEmptyRows"/> 默认开启下交出空序列；
    /// 空档则是根本没有行。
    /// </remarks>
    /// <param name="text">档内容</param>
    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    public async Task 空档与只有行尾的档交出空序列(string text)
        => Assert.Empty(await Read(ImportFixtures.Utf8NoBom.GetBytes(text), [new("A", 1)], hasHeader: false));

    /// <summary>
    /// 令牌在取数之前就已取消时不交出任何行
    /// </summary>
    [Fact]
    public async Task 令牌已取消时定宽路径不交出任何行()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = ImportFixtures.Text("A\r\nB\r\n");

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await AsyncCollector.CollectAsync(importer.ReadAsync(
                stream, ColumnsOnly([new("A", 1)]), source.Token)));
    }

    /// <summary>
    /// 取消检查排在每一行之前：交出第一行之后取消，下一次推进就停
    /// </summary>
    [Fact]
    public async Task 定宽逐行取数时每行都检查取消()
    {
        using var source = new CancellationTokenSource();
        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        using var stream = ImportFixtures.Text("A\r\nB\r\nC\r\n");

        var enumerator = importer.ReadAsync(stream, ColumnsOnly([new("A", 1)]), source.Token).GetAsyncEnumerator();

        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("A", enumerator.Current.Values["A"]);

            source.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// 行不足补空字串时按规格留一条 Debug 日志
    /// </summary>
    [Fact]
    public async Task 行不足时补空字串并记一条Debug留痕()
    {
        var sink = new FakeLogSink();
        using var factory = NewDebugFactory(sink);

        var rows = await Read(
            ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"),
            [new("A", 2), new("B", 2), new("C", 2)],
            hasHeader: false,
            logger: factory.CreateLogger<FixedWidthTextImporter>());

        Assert.Equal("", rows[0].Values["B"]);
        Assert.Equal(1, sink.Entries.Count(entry => entry.Level == LogLevel.Debug));

        var entry = sink.Entries.Single(item => item.Level == LogLevel.Debug);
        Assert.Contains(nameof(FixedWidthTextImporter), entry.Category, StringComparison.Ordinal);
        Assert.Contains("不足", entry.Message, StringComparison.Ordinal);
        Assert.Contains("B", entry.Message, StringComparison.Ordinal);
        Assert.Contains("C", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行超长丢弃多出部分时按规格留一条 Debug 日志
    /// </summary>
    [Fact]
    public async Task 行超长时丢弃多出部分并记一条Debug留痕()
    {
        var sink = new FakeLogSink();
        using var factory = NewDebugFactory(sink);

        var rows = await Read(
            ImportFixtures.Utf8NoBom.GetBytes("ABCDE\r\n"),
            [new("A", 2), new("B", 2)],
            hasHeader: false,
            logger: factory.CreateLogger<FixedWidthTextImporter>());

        Assert.Equal("CD", rows[0].Values["B"]);
        Assert.Equal(1, sink.Entries.Count(entry => entry.Level == LogLevel.Debug));

        var entry = sink.Entries.Single(item => item.Level == LogLevel.Debug);
        Assert.Contains("超出", entry.Message, StringComparison.Ordinal);
        Assert.Contains("丢弃", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行字节数正好等于列宽总和时不留痕
    /// </summary>
    [Fact]
    public async Task 行刚好等宽时不记留痕日志()
    {
        var sink = new FakeLogSink();
        using var factory = NewDebugFactory(sink);

        await Read(ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), [new("A", 2)], hasHeader: false,
            logger: factory.CreateLogger<FixedWidthTextImporter>());

        Assert.Empty(sink.Entries);
    }

    /// <summary>
    /// 定宽往返读回等于原值：补位空白靠 TrimValues 剥掉，负数不遭公式前缀
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一趟<u>显式开着</u> <see cref="ExcelTextOptions.EscapeFormulaPrefix"/>（默认即 <c>true</c>）：
    /// 固定宽度布局不套该前缀，<c>-5.00</c> 写出去就是 <c>-5.00</c>，读回也还是 <c>-5.00</c>。
    /// </para>
    /// <para>
    /// 相等靠 <see cref="ExcelImportOptions.TrimValues"/> 剥掉补位空格。「不剥时读到什么」由
    /// <see cref="TrimValues在定宽路径逐项生效"/> 钉住。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 定宽往返读回等于原值且补位空白靠TrimValues剥掉()
    {
        var rows = new[]
        {
            new SampleRow { AwbNo = "AWB1", Weight = -5.00m, Eta = new DateTime(2026, 1, 2) },
            new SampleRow { AwbNo = "已到港", Weight = 1250.5m, Eta = new DateTime(2026, 10, 31) }
        };

        var bytes = await ExportFixedWidthAsync(rows, includeHeader: false, encodingName: "utf-8");

        var read = await ReadFacadeAsync(bytes, [
            new(nameof(SampleRow.AwbNo), 12),
            new(nameof(SampleRow.Weight), 10),
            new(nameof(SampleRow.Eta), 12)
        ], hasHeader: false, trimValues: true);

        Assert.Equal(2, read.Count);
        Assert.Equal("AWB1", read[0].Values[nameof(SampleRow.AwbNo)]);
        Assert.Equal("-5.00", read[0].Values[nameof(SampleRow.Weight)]);
        Assert.Equal("2026-01-02", read[0].Values[nameof(SampleRow.Eta)]);
        Assert.Equal("已到港", read[1].Values[nameof(SampleRow.AwbNo)]);
        Assert.Equal("1250.50", read[1].Values[nameof(SampleRow.Weight)]);
        Assert.Equal("2026-10-31", read[1].Values[nameof(SampleRow.Eta)]);
    }

    /// <summary>
    /// 定宽往返在 Big5 下也逐字段相等：写出与读回按同一套字节宽度
    /// </summary>
    [Fact]
    public async Task 定宽Big5往返读回等于原值()
    {
        var rows = new[] { new SampleRow { AwbNo = "提單號", Weight = 1.5m, Eta = new DateTime(2026, 2, 28) } };

        // 「提單號」在 Big5 下 6 字节，列宽 8 由导出侧补两个空格；读回同样按 8 字节切这一列
        var bytes = await ExportFixedWidthAsync(rows, includeHeader: false, encodingName: "big5", columnWidths: [8, 10, 12]);

        var read = await ReadFacadeAsync(bytes, [
            new(nameof(SampleRow.AwbNo), 8),
            new(nameof(SampleRow.Weight), 10),
            new(nameof(SampleRow.Eta), 12)
        ], hasHeader: false, trimValues: true, encodingName: "big5");

        Assert.Equal("提單號", read[0].Values[nameof(SampleRow.AwbNo)]);
        Assert.Equal("1.50", read[0].Values[nameof(SampleRow.Weight)]);
        Assert.Equal("2026-02-28", read[0].Values[nameof(SampleRow.Eta)]);
    }

    /// <summary>
    /// 定宽导出带表头行时用 HeaderRowIndex 跳过它，第一条数据行的行号是 2
    /// </summary>
    [Fact]
    public async Task 定宽导出带表头时用HeaderRowIndex跳过表头行()
    {
        var rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = 1m, Eta = new DateTime(2026, 3, 1) } };

        var bytes = await ExportFixedWidthAsync(rows, includeHeader: true, encodingName: "utf-8");

        var read = await ReadFacadeAsync(bytes, [
            new(nameof(SampleRow.AwbNo), 12),
            new(nameof(SampleRow.Weight), 10),
            new(nameof(SampleRow.Eta), 12)
        ], hasHeader: false, trimValues: true, headerRowIndex: 1);

        Assert.Single(read);
        Assert.Equal(2, read[0].RowNumber);
        Assert.Equal("AWB1", read[0].Values[nameof(SampleRow.AwbNo)]);
    }

    /// <summary>
    /// 分隔符往返关掉公式前缀后逐字段等于原值，含逗号、引号与换行的值也原样回来
    /// </summary>
    /// <remarks>
    /// 这一条<u>显式关掉</u> <see cref="ExcelTextOptions.EscapeFormulaPrefix"/>：<c>-5.00</c> 写出去没有前缀，读回也没有前缀。
    /// 值里的逗号与引号由 <see cref="ExcelTextQuote.Minimal"/> 按 RFC 4180 包住并转义，读档侧还原；值里的换行被引号包住后
    /// 属于同一条记录，后续记录的行号按记录递增（与 <see cref="ExcelImportRow.RowNumber"/> 的文字档口径一致）。
    /// </remarks>
    [Fact]
    public async Task 分隔符往返关掉公式前缀后逐字段等于原值()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = [
                new ExcelColumn<SampleRow> { Key = "AwbNo", Header = "提单号", Order = 0, Value = row => row.AwbNo },
                new ExcelColumn<SampleRow> { Key = "Weight", Header = "重量", Order = 1, TextFormat = "0.00", Value = row => row.Weight },
                new ExcelColumn<SampleRow> { Key = "Remark", Header = "备注", Order = 2, Value = row => row.AwbNo }
            ],
            Rows = new[]
            {
                new SampleRow { AwbNo = "AWB,1\"x\"", Weight = -5.00m },
                new SampleRow { AwbNo = "第一行\n第二行", Weight = 1.5m }
            }
        };

        var stream = new MemoryStream();

        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance).ExportAsync(
            stream,
            spec,
            ExcelFormat.Csv,
            new ExcelTextOptions
            {
                Layout = ExcelTextLayout.Delimited,
                Delimiter = ',',
                Quote = ExcelTextQuote.Minimal,
                EncodingName = "utf-8",
                EscapeFormulaPrefix = false
            },
            TestContext.Current.CancellationToken);

        var read = await ReadFacadeAsync(stream.ToArray(), fields: null, hasHeader: true, trimValues: false,
            delimiter: ',', asCsv: true);

        Assert.Equal(2, read.Count);
        Assert.Equal("AWB,1\"x\"", read[0].Values["提单号"]);
        Assert.Equal("-5.00", read[0].Values["重量"]);
        Assert.Equal("第一行\n第二行", read[1].Values["备注"]);
        Assert.Equal([2, 3], read.Select(row => row.RowNumber));
    }

    /// <summary>
    /// 分隔符往返开着公式前缀时负数读回带单引号
    /// </summary>
    /// <remarks>
    /// 有一个表头以 <c>-</c> 起始：键名带着前缀落进集合，按源表头文本「-重量」取值取不到。
    /// </remarks>
    [Fact]
    public async Task 分隔符往返开着公式前缀时负数读回带前缀()
    {
        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = [
                new ExcelColumn<SampleRow> { Key = "AwbNo", Header = "提单号", Order = 0, Value = row => row.AwbNo },
                new ExcelColumn<SampleRow> { Key = "Weight", Header = "-重量", Order = 1, TextFormat = "0.00", Value = row => row.Weight }
            ],
            Rows = new[] { new SampleRow { AwbNo = "AWB1", Weight = -5.00m } }
        };

        var stream = new MemoryStream();

        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance).ExportAsync(
            stream, spec, ExcelFormat.Csv,
            new ExcelTextOptions { EncodingName = "utf-8", EscapeFormulaPrefix = true },
            TestContext.Current.CancellationToken);

        var read = await ReadFacadeAsync(stream.ToArray(), fields: null, hasHeader: true, trimValues: false,
            delimiter: ',', asCsv: true);

        Assert.Equal("'-重量", read[0].Values.Keys.Last());
        Assert.Equal("'-5.00", read[0].Values["'-重量"]);
        Assert.False(read[0].Values.ContainsKey("-重量"));

        // 不以那四个字符起始的表头与取值不受影响
        Assert.Equal("AWB1", read[0].Values["提单号"]);
    }

    /// <summary>
    /// 门面按 <see cref="ExcelImportOptions.FixedColumns"/> 把定宽请求交给 <see cref="FixedWidthTextImporter" />
    /// </summary>
    [Fact]
    public async Task 门面把固定宽度请求路由到FixedWidthTextImporter()
    {
        var bytes = ImportFixtures.Encode(ImportFixtures.StrictBig5, "中AB\r\n");

        var options = new ExcelImportOptions
        {
            Format = ExcelImportFormat.Txt,
            TextEncodingName = "big5",
            FixedColumns = [new("A", 3), new("B", 1)],
            HasHeader = false
        };

        var rows = await AsyncCollector.CollectAsync(NewFacade()
            .ReadAsync(new MemoryStream(bytes), options, TestContext.Current.CancellationToken));

        Assert.Equal("中A", rows[0].Values["A"]);
    }

    /// <summary>
    /// 门面在没有列定义时把 xlsx 请求交给 <see cref="ExcelDataReaderImporter" />
    /// </summary>
    [Fact]
    public async Task 门面把xlsx请求路由到ExcelDataReaderImporter()
    {
        var options = new ExcelImportOptions { Format = ExcelImportFormat.Xlsx, HasHeader = true };

        var rows = await AsyncCollector.CollectAsync(NewFacade()
            .ReadAsync(ImportFixtures.OneRowXlsx(), options, TestContext.Current.CancellationToken));

        Assert.Single(rows);
        Assert.Equal("AWB1", rows[0].Values["提单号"]);
    }

    /// <summary>
    /// 格式未指名但给了列定义时走固定宽度：路由规则只有列定义这一条，门面不做签章嗅探
    /// </summary>
    [Fact]
    public async Task 门面在格式未指名但给了FixedColumns时走固定宽度()
    {
        var options = new ExcelImportOptions
        {
            FixedColumns = [new("A", 1)],
            HasHeader = false,
            TextEncodingName = "utf-8"
        };

        var rows = await AsyncCollector.CollectAsync(NewFacade()
            .ReadAsync(ImportFixtures.Text("A\r\n"), options, TestContext.Current.CancellationToken));

        Assert.Equal("A", rows[0].Values["A"]);
    }

    /// <summary>
    /// 门面把 csv 请求交给 <see cref="ExcelDataReaderImporter" />
    /// </summary>
    [Fact]
    public async Task 门面把csv请求路由到ExcelDataReaderImporter()
    {
        var options = new ExcelImportOptions
        {
            Format = ExcelImportFormat.Csv,
            Delimiter = ',',
            TextEncodingName = "utf-8",
            HasHeader = true
        };

        var rows = await AsyncCollector.CollectAsync(NewFacade()
            .ReadAsync(ImportFixtures.Text("提单号\r\nAWB1\r\n"), options, TestContext.Current.CancellationToken));

        Assert.Equal("AWB1", rows[0].Values["提单号"]);
        Assert.Equal(2, rows[0].RowNumber);
    }

    /// <summary>
    /// 列定义是空集合时门面仍走固定宽度，由那条路径抛，不悄悄改投容器读取器
    /// </summary>
    [Fact]
    public async Task 门面在FixedColumns为空集合时仍走固定宽度并由它抛()
    {
        var options = new ExcelImportOptions { FixedColumns = [], HasHeader = false };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AsyncCollector.CollectAsync(NewFacade()
                .ReadAsync(ImportFixtures.OneRowXlsx(), options, TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// 定宽导入器实现抽象契约，注册侧可按接口取用
    /// </summary>
    [Fact]
    public void 定宽导入器实现导入契约()
        => Assert.IsAssignableFrom<IExcelImporter>(new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance));

    /// <summary>
    /// 门面实现抽象契约并同时持有两个读实现
    /// </summary>
    [Fact]
    public void 门面实现导入契约()
        => Assert.IsAssignableFrom<IExcelImporter>(NewFacade());

    /// <summary>
    /// 门面构造时拒掉缺失的读实现
    /// </summary>
    [Fact]
    public void 门面构造时要求两个读实现()
    {
        var fixedWidth = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);

        Assert.Throws<ArgumentNullException>(() => new ExcelImporter(null!, fixedWidth));
        Assert.Throws<ArgumentNullException>(() => new ExcelImporter(new ExcelDataReaderImporter(), null!));
    }

    /// <summary>
    /// 定宽导入器构造时要求日志器，不接受 null
    /// </summary>
    [Fact]
    public void 定宽导入器构造时要求日志器()
        => Assert.Throws<ArgumentNullException>(() => new FixedWidthTextImporter(null!));

    /// <summary>
    /// 两条导入路径共用同一份编码判别：同一份字节在定宽与分隔符上给出的解码结果不分叉
    /// </summary>
    /// <remarks>
    /// <para>
    /// 把同一份字节分别交给两条路径，比的是<u>逐条对跑的结果</u>（取到的值，或抛出的异常型别与 ParamName）。
    /// 五个形态覆盖 BOM、无 BOM 的非 UTF-8、编码指错、未知编码名与空档。
    /// </para>
    /// <para>
    /// <b>不含无 BOM 的 UTF-16</b>：分隔符路径按严格 UTF-8 解出夹着 <c>0x00</c> 的字符，
    /// 定宽路径直接拒收（<see cref="宽字节编码在定宽路径被拒"/>）。
    /// </para>
    /// </remarks>
    /// <param name="shape">待对跑的档形态</param>
    [Theory]
    [InlineData("utf8裸字节")]
    [InlineData("utf8带BOM")]
    [InlineData("Big5无BOM")]
    [InlineData("Big5档指错成utf-8")]
    [InlineData("未知编码名")]
    [InlineData("空档")]
    public async Task 两条导入路径对同一份字节的编码判定不分叉(string shape)
    {
        var (bytes, fieldWidthBytes, encodingName) = shape switch
        {
            "utf8裸字节" => (ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), 2, null),
            "utf8带BOM" => ([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. ImportFixtures.Utf8NoBom.GetBytes("AB\r\n")], 2, null),
            "Big5无BOM" => (ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號\r\n"), 6, null),
            "Big5档指错成utf-8" => (ImportFixtures.Encode(ImportFixtures.StrictBig5, "提單號\r\n"), 6, "utf-8"),
            "未知编码名" => (ImportFixtures.Utf8NoBom.GetBytes("AB\r\n"), 2, "utf-99"),
            _ => ([], 1, null)
        };

        var fixedOutcome = await OutcomeOf(async () =>
        {
            var rows = await Read(bytes, [new("A", fieldWidthBytes)], hasHeader: false,
                mutate: o => o with { TextEncodingName = encodingName });

            return Summarize(rows.Select(row => (string?)row.Values["A"]));
        });

        var delimitedOutcome = await OutcomeOf(async () =>
        {
            var rows = await ReadDelimited(bytes, encodingName);

            return Summarize(rows.Select(row => (string?)row.Values["Col1"]));
        });

        Assert.Equal(delimitedOutcome, fixedOutcome);
    }

    /// <summary>
    /// 把一次读档的结果压成可比较的文本：成功记取值，抛出记型别与 ParamName
    /// </summary>
    /// <param name="read">读档动作</param>
    private static async Task<string> OutcomeOf(Func<Task<string>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex)
        {
            return $"throw:{ex.GetType().Name}:{(ex as ArgumentException)?.ParamName ?? "-"}";
        }
    }

    /// <summary>
    /// 交出一行的取值清单，用同一个分隔符拼起来好让两条路径对得上
    /// </summary>
    /// <param name="values">逐行第一列的取值</param>
    private static string Summarize(IEnumerable<string?> values)
        => "rows:" + string.Join("|", values.Select(value => value ?? "(null)"));

    /// <summary>
    /// 走分隔符那条路径读同一份字节，用来和定宽路径逐条对跑
    /// </summary>
    /// <param name="bytes">档字节</param>
    /// <param name="encodingName">指名的编码，<c>null</c> 走自动判别</param>
    private static async Task<List<ExcelImportRow>> ReadDelimited(byte[] bytes, string? encodingName)
    {
        var options = new ExcelImportOptions
        {
            Format = ExcelImportFormat.Csv,
            Delimiter = ',',
            HasHeader = false,
            TextEncodingName = encodingName
        };

        return await AsyncCollector.CollectAsync(new ExcelDataReaderImporter()
            .ReadAsync(new MemoryStream(bytes), options, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 造一台放行 Debug 的日志工厂
    /// </summary>
    /// <remarks>
    /// <c>LoggerFactory.Create</c> 的默认最低等级是 <c>Information</c>，这里放到 <c>Debug</c>。
    /// </remarks>
    /// <param name="sink">日志收集器</param>
    private static ILoggerFactory NewDebugFactory(FakeLogSink sink)
        => LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new SinkLoggerProvider(sink)));

    /// <summary>
    /// 逐行按 <c>StringReader.ReadLine()</c> 数行，用来记录该 API 在全形行分隔符上的实际行为
    /// </summary>
    /// <param name="text">待数行的文本</param>
    /// <returns>ReadLine 交出的行数</returns>
    private static int CountReadLines(string text)
    {
        var count = 0;

        using var reader = new StringReader(text);
        while (reader.ReadLine() is not null)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 用固定宽度布局导出一张表，交出写出的字节
    /// </summary>
    /// <param name="rows">数据行</param>
    /// <param name="includeHeader">是否写出表头行</param>
    /// <param name="encodingName">目标编码名</param>
    /// <param name="columnWidths">三列的字节宽度，传 <c>null</c> 用 12/10/12</param>
    private static async Task<byte[]> ExportFixedWidthAsync(
        IReadOnlyList<SampleRow> rows,
        bool includeHeader,
        string encodingName,
        int[]? columnWidths = null)
    {
        var widths = columnWidths ?? [12, 10, 12];

        var spec = new ExcelSheetSpec
        {
            SheetName = "运单",
            RowType = typeof(SampleRow),
            Columns = [
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.AwbNo),
                    Header = "提单号",
                    Order = 0,
                    FixedWidth = widths[0],
                    Value = row => row.AwbNo
                },
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.Weight),
                    Header = "重量",
                    Order = 1,
                    FixedWidth = widths[1],
                    TextFormat = "0.00",
                    Value = row => row.Weight
                },
                new ExcelColumn<SampleRow>
                {
                    Key = nameof(SampleRow.Eta),
                    Header = "预计到达",
                    Order = 2,
                    FixedWidth = widths[2],
                    TextFormat = "yyyy-MM-dd",
                    Value = row => row.Eta
                }
            ],
            Rows = rows
        };

        var stream = new MemoryStream();

        // 公式前缀保持开启
        await new DelimitedTextExporter(NullLogger<DelimitedTextExporter>.Instance).ExportAsync(
            stream,
            spec,
            ExcelFormat.Txt,
            new ExcelTextOptions
            {
                Layout = ExcelTextLayout.FixedWidth,
                EncodingName = encodingName,
                IncludeHeader = includeHeader,
                Overflow = ExcelTextOverflow.Throw,
                EscapeFormulaPrefix = true
            },
            TestContext.Current.CancellationToken);

        return stream.ToArray();
    }

    /// <summary>
    /// 经门面读回一份档：给了列定义就走定宽，没给就走容器／分隔符那条
    /// </summary>
    /// <param name="bytes">档字节</param>
    /// <param name="fields">列定义，传 <c>null</c> 表示不交给定宽路径</param>
    /// <param name="hasHeader">用例显式给出的表头声明</param>
    /// <param name="trimValues">是否剥取值首尾空白</param>
    /// <param name="encodingName">指名的编码，<c>null</c> 走自动判别</param>
    /// <param name="headerRowIndex">要丢掉的前导行数</param>
    /// <param name="delimiter">分隔符路径的分隔符，传 <c>null</c> 表示不设置</param>
    /// <param name="asCsv">是否指名 CSV 格式（只有分隔符往返用例需要）</param>
    private static Task<List<ExcelImportRow>> ReadFacadeAsync(
        byte[] bytes,
        IReadOnlyList<ExcelFixedWidthField>? fields,
        bool hasHeader,
        bool trimValues,
        string? encodingName = null,
        int headerRowIndex = 0,
        char? delimiter = null,
        bool asCsv = false)
    {
        var options = new ExcelImportOptions
        {
            Format = asCsv ? ExcelImportFormat.Csv : null,
            FixedColumns = fields,
            HasHeader = hasHeader,
            TrimValues = trimValues,
            TextEncodingName = encodingName,
            HeaderRowIndex = headerRowIndex,
            Delimiter = delimiter
        };

        return AsyncCollector.CollectAsync(NewFacade()
            .ReadAsync(new MemoryStream(bytes), options, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 只填列定义与无表头声明的选项对象，供直接喂给导入实现的用例使用
    /// </summary>
    /// <param name="fields">列定义</param>
    private static ExcelImportOptions ColumnsOnly(IReadOnlyList<ExcelFixedWidthField> fields)
        => new() { FixedColumns = fields, HasHeader = false };

    /// <summary>
    /// 造一份单列定宽档的字节：<paramref name="rows"/> 行、每行一个 <c>1</c> 加 <c>\r\n</c>
    /// </summary>
    /// <param name="rows">行数</param>
    /// <returns>档字节（UTF-8 无 BOM，每行 3 字节）</returns>
    /// <remarks>
    /// 每行都是同一段字节，按千行一块拼。
    /// </remarks>
    private static byte[] SingleColumnRows(int rows)
    {
        var chunk = string.Concat(Enumerable.Repeat("1\r\n", 1000));
        var builder = new StringBuilder(rows * 3);

        for (var written = 0; written < rows / 1000; written++)
        {
            builder.Append(chunk);
        }

        builder.Append(chunk, 0, (rows % 1000) * "1\r\n".Length);

        return ImportFixtures.Utf8NoBom.GetBytes(builder.ToString());
    }

    /// <summary>
    /// 门面实例：两个读实现按注册时的形状当场构造
    /// </summary>
    private static ExcelImporter NewFacade()
        => new(new ExcelDataReaderImporter(), new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance));

    /// <summary>
    /// 按字节造一份定宽档并读回
    /// </summary>
    /// <param name="bytes">档字节</param>
    /// <param name="fields">列定义，传 <c>null</c> 就是要验「没给列定义」的那条</param>
    /// <param name="hasHeader">用例显式给出的表头声明；定宽路径下两种取值行为相同</param>
    /// <param name="mutate">在默认选项上改一项</param>
    /// <param name="logger">日志器，默认用不记录的 NullLogger</param>
    private static Task<List<ExcelImportRow>> Read(
        byte[] bytes,
        IReadOnlyList<ExcelFixedWidthField>? fields,
        bool hasHeader,
        Func<ExcelImportOptions, ExcelImportOptions>? mutate = null,
        ILogger<FixedWidthTextImporter>? logger = null)
        => Read(new MemoryStream(bytes), fields, hasHeader, mutate, logger);

    /// <summary>
    /// 读一份定宽档，流所有权留在用例手上
    /// </summary>
    /// <param name="input">档流</param>
    /// <param name="fields">列定义</param>
    /// <param name="hasHeader">用例显式给出的表头声明</param>
    /// <param name="mutate">在默认选项上改一项</param>
    /// <param name="logger">日志器</param>
    private static async Task<List<ExcelImportRow>> Read(
        Stream input,
        IReadOnlyList<ExcelFixedWidthField>? fields,
        bool hasHeader,
        Func<ExcelImportOptions, ExcelImportOptions>? mutate = null,
        ILogger<FixedWidthTextImporter>? logger = null)
    {
        var options = new ExcelImportOptions
        {
            Format = ExcelImportFormat.Txt,
            FixedColumns = fields,
            HasHeader = hasHeader
        };

        var importer = new FixedWidthTextImporter(logger ?? NullLogger<FixedWidthTextImporter>.Instance);

        return await AsyncCollector.CollectAsync(importer.ReadAsync(
            input, mutate is null ? options : mutate(options), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 手工枚举一份定宽档，交出<u>已经交出</u>的行与第一个异常
    /// </summary>
    /// <remarks>
    /// 用 <c>GetAsyncEnumerator</c> 逐行推进，抛出时保留已交出的行。
    /// </remarks>
    /// <param name="bytes">档字节</param>
    /// <param name="encodingName">指名的编码</param>
    private static async Task<(List<ExcelImportRow> Rows, Exception? Failure)> ReadWithOutcomeAsync(
        byte[] bytes,
        string encodingName)
    {
        var options = new ExcelImportOptions
        {
            Format = ExcelImportFormat.Txt,
            FixedColumns = [new ExcelFixedWidthField("A", 2), new ExcelFixedWidthField("B", 2)],
            HasHeader = false,
            TextEncodingName = encodingName
        };

        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);

        await using var enumerator = importer
            .ReadAsync(new MemoryStream(bytes), options, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator();

        var rows = new List<ExcelImportRow>();

        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    return (rows, null);
                }
            }
            catch (Exception ex)
            {
                return (rows, ex);
            }

            rows.Add(enumerator.Current);
        }
    }
}
