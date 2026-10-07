// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 第一次被写入时就把取消令牌取消的内存流，用来模拟「取消落在存盘期间」
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。令牌在第一次写入时取消，落盘完成时令牌已是取消状态。
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
