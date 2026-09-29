// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Docs.Mcp.Indexing;
using XiHan.Framework.Docs.Mcp.Options;
using XiHan.Framework.Docs.Mcp.Search;
using XiHan.Framework.Docs.Mcp.Sources;
using XiHan.Framework.Docs.Mcp.Tools;

namespace XiHan.Framework.Docs.Mcp.Tests;

/// <summary>
/// 工具层的结构化日志
/// </summary>
public class DocsMcpToolsLoggingTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// 构造一个最小仓库结构
    /// </summary>
    public DocsMcpToolsLoggingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xihan-tools-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guide"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "packages"));

        File.WriteAllText(
            Path.Combine(_root, "docs", "guide", "event-bus.md"),
            "# 事件总线\n\n发布方不认识订阅方。\n\n## 本地事件还是分布式事件\n\n分布式事件在事务提交之后发布。\n");
        File.WriteAllText(
            Path.Combine(_root, "docs", "packages", "caching.md"),
            "# 缓存包\n\n## 配置项\n\n缓存过期时间的配置说明。\n");
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

    [Fact]
    public void 检索命中时记下工具名命中数与耗时()
    {
        var (tools, logger) = CreateTools();

        tools.SearchDocs("分布式事件什么时候发布", source: null, limit: 5);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("search_docs", entry.Value("Tool"));
        Assert.Equal(2, entry.Value("HitCount"));
        Assert.NotNull(entry.Value("ElapsedMs"));

        // 日志带上查询串
        Assert.Equal("分布式事件什么时候发布", entry.Value("Query"));
    }

    /// <summary>
    /// 零命中单独记录一条日志
    /// </summary>
    [Fact]
    public void 零命中单独记一条并带上查询串()
    {
        var (tools, logger) = CreateTools();

        tools.SearchDocs("量子纠缠的宏观表现", source: null, limit: 5);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("search_docs", entry.Value("Tool"));
        Assert.Equal("量子纠缠的宏观表现", entry.Value("Query"));
        Assert.Contains("零命中", entry.Message, StringComparison.Ordinal);

        // 零命中日志不带 HitCount 字段
        Assert.Null(entry.Value("HitCount"));
    }

    /// <summary>
    /// 相关性截断拒绝时记录覆盖率与阈值
    /// </summary>
    /// <remarks>
    /// 将 <c>MinSectionsForRelevanceCutoff</c> 设为 1，使判据在小语料上生效。
    /// </remarks>
    [Fact]
    public void 相关性截断拒绝时记下覆盖率()
    {
        var options = new DocsMcpOptions { MinSectionsForRelevanceCutoff = 1 };
        var (tools, logger) = CreateTools(options);

        var result = tools.SearchDocs("缓存的量子纠缠", source: null, limit: 5);

        // 确认走的是命中后被截断拒绝的分支
        Assert.Contains("不要基于猜测", result, StringComparison.Ordinal);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal("search_docs", entry.Value("Tool"));
        Assert.Contains("相关性截断", entry.Message, StringComparison.Ordinal);

        var hitCount = Assert.IsType<int>(entry.Value("HitCount"));
        Assert.True(hitCount > 0, $"本用例要的是「命中了但被拒绝」，实际 HitCount = {hitCount}，说明走的是零命中分支。");

        var coverage = Assert.IsType<double>(entry.Value("Coverage"));
        Assert.True(
            coverage < options.MinKnownTermCoverage,
            $"被拒绝的查询覆盖率应低于阈值 {options.MinKnownTermCoverage}，实际 {coverage}。");

        Assert.Equal(options.MinKnownTermCoverage, entry.Value("Threshold"));
    }

    [Fact]
    public void 列出文档时记下条数()
    {
        var (tools, logger) = CreateTools();

        tools.ListDocs(source: null, includeSections: false);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal("list_docs", entry.Value("Tool"));
        Assert.Equal(2, entry.Value("FileCount"));
        Assert.NotNull(entry.Value("ElapsedMs"));
    }

    [Fact]
    public void 读取文档时记下结果分类()
    {
        var (tools, logger) = CreateTools();

        tools.ReadDoc("docs/guide/event-bus.md", section: null);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal("read_doc", entry.Value("Tool"));
        Assert.Equal("返回全文", entry.Value("Outcome"));
        Assert.Equal("docs/guide/event-bus.md", entry.Value("Path"));
    }

    /// <summary>
    /// 越界路径按警告级别记录
    /// </summary>
    [Fact]
    public void 越界路径按警告记录()
    {
        var (tools, logger) = CreateTools();

        tools.ReadDoc("../../etc/passwd", section: null);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("read_doc", entry.Value("Tool"));
        Assert.Equal("../../etc/passwd", entry.Value("Path"));
    }

    /// <summary>
    /// 未在索引内的路径按信息级别记录
    /// </summary>
    [Fact]
    public void 未在索引内的路径记为普通结果()
    {
        var (tools, logger) = CreateTools();

        tools.ReadDoc("docs/guide/不存在的文档.md", section: null);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("未在索引内", entry.Value("Outcome"));
    }

    /// <summary>
    /// search_docs 抛异常时按 Error 记录异常类型与消息，返回内容不含异常消息
    /// </summary>
    /// <remarks>
    /// 将 <c>MaxLimit</c> 设为 0，使 <c>Math.Clamp(x, 1, 0)</c> 抛出 <see cref="ArgumentException"/>。
    /// </remarks>
    [Fact]
    public void 检索抛异常时按错误级别记录()
    {
        var (tools, logger) = CreateTools(new DocsMcpOptions { MaxLimit = 0 });

        var result = tools.SearchDocs("分布式事件", source: null, limit: 5);

        Assert.Contains("发生错误", result, StringComparison.Ordinal);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("search_docs", entry.Value("Tool"));
        Assert.Equal(typeof(ArgumentException).FullName, entry.Value("ExceptionType"));

        var exceptionMessage = Assert.IsType<string>(entry.Value("ExceptionMessage"));
        Assert.DoesNotContain(exceptionMessage, result, StringComparison.Ordinal);

        // 日志附带异常对象
        Assert.NotNull(entry.Exception);
    }

    /// <summary>
    /// read_doc 抛异常时按 Error 记录，返回内容不含异常消息
    /// </summary>
    /// <remarks>
    /// 路径中的空字符使 <c>Path.GetFullPath</c> 抛出 <see cref="ArgumentException"/>。
    /// </remarks>
    [Fact]
    public void 读取抛异常时按错误级别记录()
    {
        var (tools, logger) = CreateTools();

        var result = tools.ReadDoc("docs/guide/\0event-bus.md", section: null);

        Assert.Contains("发生错误", result, StringComparison.Ordinal);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("read_doc", entry.Value("Tool"));
        Assert.Equal(typeof(ArgumentException).FullName, entry.Value("ExceptionType"));
        Assert.DoesNotContain(Assert.IsType<string>(entry.Value("ExceptionMessage")), result, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    /// <summary>
    /// 构造被测工具层与捕获日志
    /// </summary>
    private (DocsMcpTools Tools, CapturingLogger<DocsMcpTools> Logger) CreateTools(DocsMcpOptions? options = null)
    {
        var effective = options ?? new DocsMcpOptions();
        var locator = new DocSourceLocator(_root);
        var index = new DocIndex(locator, effective, TimeProvider.System, NullLogger<DocIndex>.Instance);
        var logger = new CapturingLogger<DocsMcpTools>();

        var tools = new DocsMcpTools(
            index,
            locator,
            SynonymExpander.Load(jsonPath: null, NullLogger.Instance),
            new SectionScorer(effective),
            new RelevanceGate(effective),
            effective,
            logger);

        return (tools, logger);
    }
}
