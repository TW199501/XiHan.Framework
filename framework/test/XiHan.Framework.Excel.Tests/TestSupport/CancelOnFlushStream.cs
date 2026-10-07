// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 第一次被 flush 时才把取消令牌取消的内存流，用来造出「写出已经收尾、回传还没发生」那个窗口
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。取消发生在第一次 flush，此时库的写出已经完成。
/// </remarks>
internal sealed class CancelOnFlushStream(CancellationTokenSource source) : MemoryStream
{
    public override void Flush()
    {
        source.Cancel();
        base.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        source.Cancel();
        return base.FlushAsync(cancellationToken);
    }
}
