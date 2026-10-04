// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 第一次被写入时就把取消令牌取消的内存流，用来模拟「取消落在存盘期间」
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。导出器把取消检查排在落盘之前与回传结果之前两处，本夹具专门造出后者才观察得到的
/// 那个窗口：写出侧完成落盘后令牌已经取消，此时已经没有「零字节」可以主张，能主张的只有「不回传成功结果」。
/// </remarks>
internal sealed class CancelOnWriteStream(CancellationTokenSource source) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        source.Cancel();
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        source.Cancel();
        base.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        source.Cancel();
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        source.Cancel();
        return base.WriteAsync(buffer, cancellationToken);
    }
}
