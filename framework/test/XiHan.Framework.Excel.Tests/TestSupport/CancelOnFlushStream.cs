// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 第一次被 flush 时才把取消令牌取消的内存流，用来造出「写出已经收尾、回传还没发生」那个窗口
/// </summary>
/// <remarks>
/// 只作为测试夹具，不进正式 API。<see cref="CancelOnWriteStream" /> 取消在写出的中段，库自己就有机会先观察到；
/// 本夹具把取消压到最后一次 flush 之后，此时库的写出已经完成、剩下的只是本组件要不要正常回传。
/// 用它才能证明「回传之前补查一次取消」这一道检查真在守什么。
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
