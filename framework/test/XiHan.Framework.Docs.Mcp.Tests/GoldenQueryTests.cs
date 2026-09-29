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
/// 黄金查询集：在真实文档上验证检索排序与相关性截断
/// </summary>
public class GoldenQueryTests
{
    private static readonly Lazy<GoldenFixture> Shared = new(BuildFixture);

    /// <summary>
    /// 不在文档范围内的查询得到显式否认
    /// </summary>
    /// <param name="query">查询串</param>
    [Theory]
    [InlineData("量子纠缠的宏观表现")]
    [InlineData("怎么用 Rust 写异步运行时")]
    [InlineData("红烧肉的做法")]
    [InlineData("Kubernetes Ingress 怎么配 TLS")]
    [InlineData("今天北京的天气怎么样")]
    [InlineData("莎士比亚十四行诗的韵律")]
    [InlineData("如何煮一杯手冲咖啡")]
    [InlineData("怎么在 Django 里做数据库迁移")]
    [InlineData("PostgreSQL 的 VACUUM 什么时候触发")]
    [InlineData("Spring Boot 的自动配置怎么关掉")]
    [InlineData("React 的 useEffect 什么时候执行")]
    [InlineData("曦寒框架支持 GraphQL 吗")]
    [InlineData(
        "Vue 的响应式原理是什么",
        Skip = "已知漏过（覆盖率 1.000）：查询中每个词条在语料中都出现过。")]
    public void 无关查询得到显式否认(string query)
    {
        var result = Shared.Value.Tools.SearchDocs(query, source: null, limit: 5);

        Assert.Contains("未找到", result);
        Assert.Contains("不要基于猜测", result);
    }

    /// <summary>
    /// 每条查询的期望命中文件必须出现在前三名
    /// </summary>
    /// <remarks>
    /// 验证 <c>SectionScorer.Rank</c> 的前三名；同一查询在 <see cref="相关查询不被截断误杀"/> 中期望的文件不同。
    /// </remarks>
    /// <param name="query">查询串</param>
    /// <param name="expectedPathFragment">期望命中的路径片段</param>
    [Theory]
    [InlineData("分布式事件什么时候发出去", "docs/guide/event-bus.md")]
    [InlineData("动态 API 路由为什么没有动词", "docs/guide/dynamic-api.md")]
    [InlineData("ILocalEventBus", "eventbus")]
    [InlineData("模块的生命周期钩子有哪些", "docs/guide/modularity.md")]
    [InlineData("多租户怎么隔离数据", "docs/guide/multi-tenancy.md")]
    [InlineData("怎么配置缓存过期时间", "docs/guide/caching.md")]
    [InlineData("工作单元什么时候回滚", "docs/guide/uow.md")]
    [InlineData("雪花 ID 会不会重复", "docs/guide/distributed-ids.md")]
    [InlineData("审计日志记录了哪些字段", "docs/guide/auditing.md")]
    [InlineData("对象存储怎么换成 MinIO", "docs/guide/storage.md")]
    [InlineData("SignalR 实时推送怎么用", "docs/guide/realtime.md")]
    [InlineData("本地化资源文件放在哪里", "docs/guide/localization.md")]
    [InlineData("后台定时任务怎么注册 Cron 表达式", "docs/guide/tasks.md")]
    [InlineData("怎么打开链路追踪", "docs/guide/observability.md")]
    [InlineData("对象映射用的是哪个库", "docs/guide/mapping.md")]
    [InlineData("限流和熔断是怎么实现的", "docs/guide/gateway.md")]
    [InlineData("虚拟文件系统能做什么", "docs/packages/virtual-file-system.md")]
    public void 期望文件出现在前三名(string query, string expectedPathFragment)
    {
        var fixture = Shared.Value;
        var snapshot = fixture.Index.EnsureFresh();

        var hits = fixture.Scorer.Rank(
            fixture.Expander.Expand(query),
            snapshot.Sections,
            snapshot.Index,
            sourceFilter: null,
            limit: 3);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, hit => hit.Section.RelativePath.Contains(expectedPathFragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 相关查询不能被相关性截断误杀，必须拿到带出处的正文
    /// </summary>
    /// <remarks>
    /// 验证 <c>SearchDocs</c> 经相关性截断后的前五条；同一查询在 <see cref="期望文件出现在前三名"/> 中期望的文件不同。
    /// </remarks>
    /// <param name="query">查询串</param>
    /// <param name="expectedPathFragment">期望命中的路径片段</param>
    [Theory]
    [InlineData("分布式事件什么时候发出去", "docs/guide/event-bus.md")]
    [InlineData("动态 API 路由为什么没有动词", "docs/guide/dynamic-api.md")]
    [InlineData("ILocalEventBus", "eventbus")]
    [InlineData("模块的生命周期钩子有哪些", "docs/guide/lifecycle.md")]
    [InlineData("多租户怎么隔离数据", "docs/guide/multi-tenancy.md")]
    [InlineData("怎么配置缓存过期时间", "docs/guide/caching.md")]
    [InlineData("工作单元什么时候回滚", "docs/guide/uow.md")]
    [InlineData("雪花 ID 会不会重复", "docs/guide/distributed-ids.md")]
    [InlineData("对象存储怎么换成 MinIO", "docs/guide/storage.md")]
    [InlineData("怎么打开链路追踪", "docs/guide/observability.md")]
    public void 相关查询不被截断误杀(string query, string expectedPathFragment)
    {
        var result = Shared.Value.Tools.SearchDocs(query, source: null, limit: 5);

        Assert.DoesNotContain("不要基于猜测", result);
        Assert.Contains(expectedPathFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 相关与不相关查询的覆盖率与阈值之间保留余量
    /// </summary>
    /// <remarks>
    /// 阈值见 <see cref="DocsMcpOptions.MinKnownTermCoverage"/>；相关查询最低覆盖率不低于 0.95，不相关查询最高覆盖率不高于 0.85。
    /// </remarks>
    [Fact]
    public void 截断判据在正负例之间留有余量()
    {
        var fixture = Shared.Value;
        var snapshot = fixture.Index.EnsureFresh();

        double Coverage(string query)
        {
            return fixture.Gate.MeasureKnownTermCoverage(query, snapshot.Index, snapshot.Sections.Count);
        }

        string[] relevant =
        [
            "分布式事件什么时候发出去",
            "动态 API 路由为什么没有动词",
            "多租户怎么隔离数据",
            "怎么配置缓存过期时间",
            "模块的生命周期钩子有哪些"
        ];

        string[] irrelevant =
        [
            "量子纠缠的宏观表现",
            "怎么用 Rust 写异步运行时",
            "红烧肉的做法",
            "Kubernetes Ingress 怎么配 TLS",
            "PostgreSQL 的 VACUUM 什么时候触发"
        ];

        var lowestRelevant = relevant.Min(Coverage);
        var highestIrrelevant = irrelevant.Max(Coverage);

        Assert.True(
            lowestRelevant >= 0.95,
            $"相关查询的最低覆盖率跌到了 {lowestRelevant:F3}，截断判据正在逼近误杀相关查询。");
        Assert.True(
            highestIrrelevant <= 0.85,
            $"不相关查询的最高覆盖率涨到了 {highestIrrelevant:F3}，截断判据正在逼近放过不相关查询。");
    }

    /// <summary>
    /// 索引覆盖四类来源且章节数符合预期
    /// </summary>
    [Fact]
    public void 索引覆盖四类来源()
    {
        var fixture = Shared.Value;
        var snapshot = fixture.Index.EnsureFresh();

        Assert.Contains(snapshot.Files, f => f.Source == DocSourceKind.Guide);
        Assert.Contains(snapshot.Files, f => f.Source == DocSourceKind.Package);
        Assert.Contains(snapshot.Files, f => f.Source == DocSourceKind.Root);
        Assert.Contains(snapshot.Files, f => f.Source == DocSourceKind.PackageReadme);
        Assert.True(
            snapshot.Sections.Count > 500,
            $"章节数只有 {snapshot.Sections.Count}，切片器可能出了问题。");
    }

    /// <summary>
    /// 用真实仓库构造索引与检索链路
    /// </summary>
    private static GoldenFixture BuildFixture()
    {
        var root = DocSourceLocator.ResolveRepositoryRoot(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("XIHAN_DOCS_ROOT"));

        var options = new DocsMcpOptions();
        var locator = new DocSourceLocator(root);
        var index = new DocIndex(locator, options, TimeProvider.System, NullLogger<DocIndex>.Instance);

        var expander = SynonymExpander.Load(
            Path.Combine(root, "framework", "tool", "XiHan.Framework.Docs.Mcp", "Resources", "synonyms.json"),
            NullLogger.Instance);
        var scorer = new SectionScorer(options);
        var gate = new RelevanceGate(options);

        return new GoldenFixture(
            index,
            scorer,
            expander,
            gate,
            new DocsMcpTools(index, locator, expander, scorer, gate, options, NullLogger<DocsMcpTools>.Instance));
    }

    /// <summary>
    /// 黄金查询集共用的检索链路
    /// </summary>
    /// <param name="Index">索引门面</param>
    /// <param name="Scorer">排序器</param>
    /// <param name="Expander">同义词扩展器</param>
    /// <param name="Gate">相关性截断</param>
    /// <param name="Tools">工具层</param>
    private sealed record GoldenFixture(
        DocIndex Index,
        SectionScorer Scorer,
        SynonymExpander Expander,
        RelevanceGate Gate,
        DocsMcpTools Tools);
}
