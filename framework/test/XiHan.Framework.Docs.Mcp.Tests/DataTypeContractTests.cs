// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Docs.Mcp.Indexing;
using XiHan.Framework.Docs.Mcp.Sources;

namespace XiHan.Framework.Docs.Mcp.Tests;

/// <summary>
/// 数据类型的契约测试：位置参数顺序与来源分类的成员集合
/// </summary>
/// <remarks>
/// 按位置构造、按名字断言，固定记录类型的位置参数顺序。
/// </remarks>
public class DataTypeContractTests
{
    /// <summary>
    /// 文档文件的位置参数顺序：绝对路径在前，相对路径在后
    /// </summary>
    [Fact]
    public void 文档文件的位置参数顺序()
    {
        var lastWriteUtc = new DateTime(2026, 8, 16, 12, 34, 56, DateTimeKind.Utc);

        var file = new DocFile(
            "/repo/docs/guide/event-bus.md",
            "docs/guide/event-bus.md",
            DocSourceKind.Guide,
            lastWriteUtc);

        Assert.Equal("/repo/docs/guide/event-bus.md", file.AbsolutePath);
        Assert.Equal("docs/guide/event-bus.md", file.RelativePath);
        Assert.Equal(DocSourceKind.Guide, file.Source);
        Assert.Equal(lastWriteUtc, file.LastWriteUtc);
    }

    /// <summary>
    /// 章节的位置参数顺序：四个连续字符串与两个连续行号各就各位
    /// </summary>
    /// <remarks>
    /// 四个字符串参数取值互不相同且互不为子串，起止行号取不同的值。
    /// </remarks>
    [Fact]
    public void 章节的位置参数顺序()
    {
        var section = new DocSection(
            "docs/guide/event-bus.md",
            DocSourceKind.Guide,
            "事件总线",
            "本地事件还是分布式事件",
            "事件总线 > 本地事件还是分布式事件",
            "分布式事件在事务提交之后发布。",
            7,
            26);

        Assert.Equal("docs/guide/event-bus.md", section.RelativePath);
        Assert.Equal(DocSourceKind.Guide, section.Source);
        Assert.Equal("事件总线", section.DocumentTitle);
        Assert.Equal("本地事件还是分布式事件", section.Heading);
        Assert.Equal("事件总线 > 本地事件还是分布式事件", section.TitlePath);
        Assert.Equal("分布式事件在事务提交之后发布。", section.Content);
        Assert.Equal(7, section.StartLine);
        Assert.Equal(26, section.EndLine);
    }

    /// <summary>
    /// 来源分类恰好是四个成员
    /// </summary>
    /// <remarks>
    /// 成员变化时须同步 <c>DocsMcpTools</c> 中 <c>ParseSource</c> 与 <c>DescribeSource</c> 的 switch。
    /// </remarks>
    [Fact]
    public void 来源分类恰好四个成员()
    {
        string[] expected = ["Guide", "Package", "PackageReadme", "Root"];

        Assert.Equal(expected, Enum.GetNames<DocSourceKind>().Order(StringComparer.Ordinal));
    }
}
