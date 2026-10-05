// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 只允许异步 I/O 的内存流：任何同步写入或同步 Flush 都抛，异步路径照常工作
/// </summary>
/// <remarks>
/// <para>
/// 只作为测试夹具，不进正式 API。它复刻的是 Kestrel 在 <c>AllowSynchronousIO=false</c> 下的
/// <c>HttpRequestStream</c>：同步写抛 <see cref="InvalidOperationException"/>，异步写正常。
/// 用它跑导出，观察的是「写出器释放时走的是哪条 I/O」——骨架里的 <c>StreamWriter</c> 若以同步
/// <c>Dispose</c> 结束，为了排空缓冲必然撞进本夹具的同步写，抛出与本次导出无关的异常。
/// </para>
/// <para>
/// 内部自持一份 <see cref="MemoryStream" />，异步重载直接交给它落笔，不借用 <see cref="Stream" />
/// 那些「异步默认实现其实同步写」的基类行为——正因为那个默认实现会把异步写转成同步写，光派生
/// <see cref="MemoryStream" /> 再改写同步成员是不够的：不重写异步成员时它们照样会抛，夹具就废了。
/// </para>
/// </remarks>
internal sealed class AsyncOnlyStream : Stream
{
    /// <summary>
    /// 同步 I/O 被拒时抛出的消息，与 Kestrel 的措辞同一句
    /// </summary>
    internal const string SyncDisallowedMessage = "Synchronous operations are disallowed.";

    private readonly MemoryStream _inner = new();

    /// <summary>
    /// 已经落进流的全部字节
    /// </summary>
    internal byte[] ToArray() => _inner.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
        => throw new InvalidOperationException(SyncDisallowedMessage);

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => throw new InvalidOperationException(SyncDisallowedMessage);

    public override void Write(ReadOnlySpan<byte> buffer)
        => throw new InvalidOperationException(SyncDisallowedMessage);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => throw new InvalidOperationException(SyncDisallowedMessage);

    public override int Read(Span<byte> buffer)
        => throw new InvalidOperationException(SyncDisallowedMessage);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);
}
