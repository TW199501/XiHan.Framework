// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 打点流：转发到内层流，同时数一共从它读走了多少字节
/// </summary>
/// <remarks>
/// 与 <see cref="ForwardOnlyStream"/> 的分工：那一份用于「入口拒收不可定位的流」，本份记录某个时点之前
/// 已经读过多少字节。累计数跨复位重读继续往上加。
/// </remarks>
/// <param name="inner">被转发的流，本夹具随它一起释放</param>
internal sealed class ReadCountingStream(Stream inner) : Stream
{
    /// <summary>
    /// 至今从内层流读走的字节总数，跨复位重读累计
    /// </summary>
    public long TotalBytesRead { get; private set; }

    /// <summary>
    /// 至今调用 <see cref="Read(byte[],int,int)"/> 的次数
    /// </summary>
    public int ReadCalls { get; private set; }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);

        ReadCalls++;
        TotalBytesRead += read;

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
