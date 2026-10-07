// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Excel.Abstractions.Enums;
using XiHan.Framework.Excel.Abstractions.Importing;
using XiHan.Framework.Excel.Importing;
using XiHan.Framework.Excel.Tests.TestSupport;

namespace XiHan.Framework.Excel.Tests.Importing;

/// <summary>
/// 指名行数上限后的停止读取行为。
/// </summary>
public class ExplicitRowLimitTests
{
    /// <summary>
    /// CSV 预扫完成后，交够请求行数不再读取空白尾部。
    /// </summary>
    [Fact]
    public async Task Csv交够后不再读取尾部()
    {
        using var input = new ReadCountingStream(new MemoryStream(Encoding.UTF8.GetBytes("A\nvalue\n" + new string('\n', 100_000) + "tail\n")));
        await using var rows = new ExcelDataReaderImporter().ReadAsync(input,
            new ExcelImportOptions { Format = ExcelImportFormat.Csv, MaxRowCount = 1 },
            TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());
        Assert.Equal("value", rows.Current.Values["A"]);
        var bytes = input.TotalBytesRead;
        Assert.False(await rows.MoveNextAsync());
        Assert.Equal(bytes, input.TotalBytesRead);
    }

    /// <summary>
    /// CSV 预扫后的尾部 I/O 故障不影响指名的前缀请求。
    /// </summary>
    [Fact]
    public async Task Csv截断不触发尾部读取故障()
    {
        await ReadPrefix(1);
        await Assert.ThrowsAsync<IOException>(() => ReadPrefix(2));

        static async Task ReadPrefix(int maxRows)
        {
            using var input = new TailFailureStream(Encoding.UTF8.GetBytes("A\nvalue\n" + new string('\n', 100_000) + "tail\n"));
            await using var rows = new ExcelDataReaderImporter().ReadAsync(input,
                new ExcelImportOptions { Format = ExcelImportFormat.Csv, MaxRowCount = maxRows },
                TestContext.Current.CancellationToken).GetAsyncEnumerator();
            Assert.True(await rows.MoveNextAsync());
            Assert.Equal("value", rows.Current.Values["A"]);
            input.FailReads = true;
            Assert.False(await rows.MoveNextAsync());
        }
    }

    /// <summary>
    /// 定宽交够请求行数不再读取空白尾部。
    /// </summary>
    [Fact]
    public async Task 定宽交够后不再读取尾部()
    {
        using var input = new ReadCountingStream(new MemoryStream(Encoding.UTF8.GetBytes("A\n" + new string('\n', 100_000) + "B\n")));
        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        await using var rows = importer.ReadAsync(input, FixedOptions(), TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());
        Assert.Equal("A", rows.Current.Values["A"]);
        var bytes = input.TotalBytesRead;
        Assert.False(await rows.MoveNextAsync());
        Assert.Equal(bytes, input.TotalBytesRead);
    }

    /// <summary>
    /// 定宽上限后的非法 UTF-8 不影响已请求的前缀。
    /// </summary>
    [Fact]
    public async Task 定宽截断不解码尾部坏行()
    {
        using var input = new MemoryStream([0x41, 0x0A, 0xFF, 0x0A]);
        var importer = new FixedWidthTextImporter(NullLogger<FixedWidthTextImporter>.Instance);
        var rows = await AsyncCollector.CollectAsync(importer.ReadAsync(input, FixedOptions(), TestContext.Current.CancellationToken));
        Assert.Single(rows);
        Assert.Equal("A", rows[0].Values["A"]);
        input.Position = 0;
        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            await AsyncCollector.CollectAsync(importer.ReadAsync(input, FixedOptions() with { MaxRowCount = 2 }, TestContext.Current.CancellationToken)));
    }

    private static ExcelImportOptions FixedOptions() => new()
    {
        Format = ExcelImportFormat.Txt,
        HasHeader = false,
        FixedColumns = [new("A", 1)],
        TextEncodingName = "utf-8",
        MaxRowCount = 1
    };
    private sealed class TailFailureStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool FailReads { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (FailReads)
            {
                throw new IOException("尾部读取故障");
            }
            return base.Read(buffer, offset, count);
        }
    }

}
