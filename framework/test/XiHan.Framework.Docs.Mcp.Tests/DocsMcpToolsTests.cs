// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Docs.Mcp.Indexing;
using XiHan.Framework.Docs.Mcp.Options;
using XiHan.Framework.Docs.Mcp.Search;
using XiHan.Framework.Docs.Mcp.Sources;
using XiHan.Framework.Docs.Mcp.Tools;

namespace XiHan.Framework.Docs.Mcp.Tests;

/// <summary>
/// MCP 工具层测试
/// </summary>
public class DocsMcpToolsTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// 构造一个最小仓库结构
    /// </summary>
    public DocsMcpToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xihan-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guide"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "packages"));
        Directory.CreateDirectory(Path.Combine(_root, "framework", "src"));

        File.WriteAllText(
            Path.Combine(_root, "docs", "guide", "event-bus.md"),
            "# 事件总线\n\n发布方不认识订阅方。\n\n## 本地事件还是分布式事件\n\n分布式事件在事务提交之后发布。\n");
        File.WriteAllText(
            Path.Combine(_root, "docs", "packages", "caching.md"),
            "# 缓存包\n\n## 配置项\n\n缓存过期时间的配置说明。\n");

        // 仓库根内、但不属于任何一类文档来源的文件，用来验证 read_doc 的白名单
        File.WriteAllText(
            Path.Combine(_root, "framework", "src", "appsettings.Production.json"),
            "{ \"ConnectionString\": \"绝密连接串\" }");
    }

    /// <summary>
    /// 清理临时目录
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 检索结果带出处：相对路径、标题路径与行号
    /// </summary>
    [Fact]
    public void 检索结果带出处()
    {
        var result = CreateTools().SearchDocs("分布式事件什么时候发布", source: null, limit: 5);

        Assert.Contains("docs/guide/event-bus.md", result);
        Assert.Contains("本地事件还是分布式事件", result);
        Assert.Contains("事务提交之后", result);
    }

    /// <summary>
    /// 零命中时明确告知文档中没有相关内容
    /// </summary>
    [Fact]
    public void 零命中时明确告知()
    {
        var result = CreateTools().SearchDocs("量子纠缠的宏观表现", source: null, limit: 5);

        Assert.Contains("未找到", result);

        // 同时要求不要基于猜测作答
        Assert.Contains("不要基于猜测", result);
    }

    /// <summary>
    /// 超过长度上限的查询被拒绝，不执行检索
    /// </summary>
    [Fact]
    public void 超长查询被拒绝()
    {
        var query = "分布式事件什么时候发布" + new string(' ', DocsMcpOptions.MaxQueryLength);

        var result = CreateTools().SearchDocs(query, source: null, limit: 5);

        Assert.Contains("过长", result);
        Assert.Contains(DocsMcpOptions.MaxQueryLength.ToString(), result);
        Assert.DoesNotContain("docs/guide/event-bus.md", result);
    }

    /// <summary>
    /// 长度恰好等于上限的查询照常检索
    /// </summary>
    [Fact]
    public void 长度等于上限的查询照常检索()
    {
        var prefix = "分布式事件什么时候发布";
        var query = prefix + new string(' ', DocsMcpOptions.MaxQueryLength - prefix.Length);

        var result = CreateTools().SearchDocs(query, source: null, limit: 5);

        Assert.Contains("事务提交之后", result);
    }

    /// <summary>
    /// 来源过滤生效：限定指南时不返回包文档
    /// </summary>
    [Fact]
    public void 来源过滤生效()
    {
        var tools = CreateTools();

        var unfiltered = tools.SearchDocs("缓存过期配置", source: null, limit: 5);
        var filtered = tools.SearchDocs("缓存过期配置", source: "guide", limit: 5);

        Assert.Contains("docs/packages/caching.md", unfiltered);
        Assert.Contains("未找到", filtered);
    }

    /// <summary>
    /// 读取整篇文档返回原文
    /// </summary>
    [Fact]
    public void 读取整篇文档()
    {
        var result = CreateTools().ReadDoc("docs/guide/event-bus.md", section: null);

        Assert.Contains("发布方不认识订阅方", result);
        Assert.Contains("分布式事件在事务提交之后发布", result);
    }

    /// <summary>
    /// 指定章节时只返回该节
    /// </summary>
    [Fact]
    public void 读取指定章节()
    {
        var result = CreateTools().ReadDoc("docs/guide/event-bus.md", "本地事件还是分布式事件");

        Assert.Contains("事务提交之后", result);
        Assert.DoesNotContain("发布方不认识订阅方", result);
    }

    /// <summary>
    /// 路径不存在时给出候选建议而不是裸错误
    /// </summary>
    [Fact]
    public void 路径不存在时给出建议()
    {
        var result = CreateTools().ReadDoc("docs/guide/eventbus.md", section: null);

        Assert.Contains("未找到", result);
        Assert.Contains("event-bus.md", result);
    }

    /// <summary>
    /// 逃逸仓库根的路径被拒绝
    /// </summary>
    [Fact]
    public void 拒绝逃逸路径()
    {
        var result = CreateTools().ReadDoc("../../secrets.txt", section: null);

        Assert.Contains("拒绝", result);
    }

    /// <summary>
    /// 仓库根内但未被索引的文件不返回内容
    /// </summary>
    [Fact]
    public void 拒绝读取未被索引的仓库内文件()
    {
        var secret = Path.Combine(_root, "framework", "src", "appsettings.Production.json");
        Assert.True(File.Exists(secret), $"夹具文件 {secret} 不存在，这条测试会在空转的情况下变绿。");

        var tools = CreateTools();
        var result = tools.ReadDoc("framework/src/appsettings.Production.json", section: null);

        Assert.DoesNotContain("绝密连接串", result);
        Assert.Contains("未找到", result);

        // 同一实例读取已索引的文档仍返回正文
        Assert.Contains("发布方不认识订阅方", tools.ReadDoc("docs/guide/event-bus.md", section: null));
    }

    /// <summary>
    /// 同一篇文档的等价写法都能读到，不因写法差异退化成「未找到」
    /// </summary>
    /// <param name="path">等价写法</param>
    [Theory]
    [InlineData("docs/guide/event-bus.md")]
    [InlineData("./docs/guide/event-bus.md")]
    [InlineData("docs//guide/event-bus.md")]
    [InlineData("docs/./guide/event-bus.md")]
    [InlineData("docs/packages/../guide/event-bus.md")]
    public void 路径的等价写法都能读到同一篇文档(string path)
    {
        var result = CreateTools().ReadDoc(path, section: null);

        Assert.Contains("发布方不认识订阅方", result);
    }

    /// <summary>
    /// 白名单对大小写的态度与包含性校验保持一致
    /// </summary>
    /// <remarks>
    /// Windows 上视为同一文件，其余平台上视为不存在的路径。
    /// </remarks>
    [Fact]
    public void 大小写写法按平台判定()
    {
        var result = CreateTools().ReadDoc("DOCS/GUIDE/EVENT-BUS.MD", section: null);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("发布方不认识订阅方", result);
        }
        else
        {
            Assert.Contains("未找到", result);
            Assert.DoesNotContain("发布方不认识订阅方", result);
        }
    }

    /// <summary>
    /// 换个写法也绕不过白名单
    /// </summary>
    /// <remarks>
    /// 用 <c>./</c> 与 <c>..</c> 改写路径后，未被索引的仓库内文件仍读不出来。
    /// </remarks>
    [Fact]
    public void 等价写法绕不过白名单()
    {
        var result = CreateTools().ReadDoc("./framework/src/../src/appsettings.Production.json", section: null);

        Assert.DoesNotContain("绝密连接串", result);
        Assert.Contains("未找到", result);
    }

    /// <summary>
    /// 默认列表不展开章节标题
    /// </summary>
    [Fact]
    public void 默认列表不展开章节()
    {
        var result = CreateTools().ListDocs(source: null, includeSections: false);

        Assert.Contains("docs/guide/event-bus.md", result);
        Assert.DoesNotContain("本地事件还是分布式事件", result);
    }

    /// <summary>
    /// 显式要求时展开章节标题
    /// </summary>
    [Fact]
    public void 显式要求时展开章节()
    {
        var result = CreateTools().ListDocs(source: null, includeSections: true);

        Assert.Contains("本地事件还是分布式事件", result);
    }

    /// <summary>
    /// 构造被测工具层
    /// </summary>
    private DocsMcpTools CreateTools()
    {
        var locator = new DocSourceLocator(_root);
        var options = new DocsMcpOptions();
        var index = new DocIndex(locator, options, TimeProvider.System, NullLogger<DocIndex>.Instance);

        return new DocsMcpTools(
            index,
            locator,
            SynonymExpander.Load(jsonPath: null, NullLogger.Instance),
            new SectionScorer(options),
            new RelevanceGate(options),
            options,
            NullLogger<DocsMcpTools>.Instance);
    }
}
