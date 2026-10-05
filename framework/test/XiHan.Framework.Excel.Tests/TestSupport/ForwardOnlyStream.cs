// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;

namespace XiHan.Framework.Excel.Tests.TestSupport;

/// <summary>
/// 只能顺序读的流，用来证明入口对可定位性的要求
/// </summary>
/// <remarks>
/// 与 <c>MiniExcelTemplateRendererTests</c> 里的同名私有夹具分开一份：那边是嵌套在测试类内部的私有类型，
/// 导入侧的测试改不到它，而本类型要给签章判别与导入两条路径共用。两者的判断口径一致：可读、不可定位、不可写。
/// </remarks>
internal sealed class ForwardOnlyStream(Stream inner) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

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
