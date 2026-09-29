// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using XiHan.Framework.Docs.Mcp.Indexing;
using XiHan.Framework.Docs.Mcp.Options;
using XiHan.Framework.Docs.Mcp.Search;
using XiHan.Framework.Docs.Mcp.Sources;

namespace XiHan.Framework.Docs.Mcp.Tools;

/// <summary>
/// 曦寒框架文档的三个 MCP 工具
/// </summary>
/// <param name="index">索引门面</param>
/// <param name="locator">文档来源定位器</param>
/// <param name="expander">同义词扩展器</param>
/// <param name="scorer">排序器</param>
/// <param name="gate">相关性截断</param>
/// <param name="options">可调参数</param>
/// <param name="logger">日志记录器</param>
/// <remarks>
/// 三个工具均返回 Markdown 文本，失败时返回说明文字而不向客户端抛出异常。
/// 每次调用记一条结构化日志（工具名、耗时、结果、结果条数），只经 <see cref="ILogger"/> 输出，不记录密钥与请求头。
/// </remarks>
[McpServerToolType]
public sealed class DocsMcpTools(
    DocIndex index,
    DocSourceLocator locator,
    SynonymExpander expander,
    SectionScorer scorer,
    RelevanceGate gate,
    DocsMcpOptions options,
    ILogger<DocsMcpTools> logger)
{
    /// <summary>
    /// 检索曦寒框架文档，返回最相关的章节原文
    /// </summary>
    /// <param name="query">自然语言问题或关键词</param>
    /// <param name="source">来源过滤</param>
    /// <param name="limit">返回条数</param>
    /// <returns>带出处的章节原文</returns>
    [McpServerTool(Name = "search_docs")]
    [Description("检索曦寒框架（XiHan.Framework）的文档，返回最相关的章节原文与出处。用它回答框架的用法、配置、API 与设计原理问题，不要凭记忆作答。")]
    public string SearchDocs(
        [Description("自然语言问题或关键词，例如「分布式事件什么时候发出去」")] string query,
        [Description("来源过滤：guide 使用指南、packages 包文档、readme 包自述、root 全局文档、all 全部。默认 all")] string? source = null,
        [Description("返回的章节数，默认 5，最大 15")] int limit = 5)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                logger.LogInformation(
                    "{Tool} 拒绝空查询，耗时 {ElapsedMs} 毫秒。",
                    "search_docs",
                    stopwatch.ElapsedMilliseconds);

                return "查询串为空，请给出要检索的问题或关键词。";
            }

            if (query.Length > DocsMcpOptions.MaxQueryLength)
            {
                logger.LogInformation(
                    "{Tool} 拒绝超长查询：长度 {QueryLength} 超过上限 {MaxQueryLength}，耗时 {ElapsedMs} 毫秒。",
                    "search_docs",
                    query.Length,
                    DocsMcpOptions.MaxQueryLength,
                    stopwatch.ElapsedMilliseconds);

                return $"查询串过长（{query.Length} 个字符），上限为 {DocsMcpOptions.MaxQueryLength} 个字符，请精简后重试。";
            }

            var snapshot = index.EnsureFresh();

            var (filter, notice) = ParseSource(source);
            var effectiveLimit = Math.Clamp(limit <= 0 ? options.DefaultLimit : limit, 1, options.MaxLimit);
            var terms = expander.Expand(query);
            var hits = scorer.Rank(terms, snapshot.Sections, snapshot.Index, filter, effectiveLimit);

            // 判定查询是否落在已索引文档范围内，并取得覆盖率用于日志
            var relevant = gate.IsAboutIndexedDocs(query, snapshot.Index, snapshot.Sections.Count, out var coverage);

            if (hits.Count == 0 || !relevant)
            {
                if (hits.Count > 0)
                {
                    logger.LogInformation(
                        "{Tool} 被相关性截断拒绝：查询「{Query}」命中 {HitCount} 段，覆盖率 {Coverage:F3} 低于阈值 {Threshold:F2}，来源 {Source}，耗时 {ElapsedMs} 毫秒。",
                        "search_docs",
                        query,
                        hits.Count,
                        coverage,
                        options.MinKnownTermCoverage,
                        source ?? "all",
                        stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    logger.LogInformation(
                        "{Tool} 零命中：查询「{Query}」，来源 {Source}，语料 {SectionCount} 个章节，耗时 {ElapsedMs} 毫秒。文档确实没涵盖这个主题时，这是正确行为。",
                        "search_docs",
                        query,
                        source ?? "all",
                        snapshot.Sections.Count,
                        stopwatch.ElapsedMilliseconds);
                }

                return BuildEmptyResult(snapshot, query, notice);
            }

            logger.LogInformation(
                "{Tool} 完成：查询「{Query}」，来源 {Source}，返回 {HitCount} 段，最高分 {TopScore:F2}，耗时 {ElapsedMs} 毫秒。",
                "search_docs",
                query,
                source ?? "all",
                hits.Count,
                hits[0].Score,
                stopwatch.ElapsedMilliseconds);

            var builder = new StringBuilder();
            if (notice.Length > 0)
            {
                builder.AppendLine(notice).AppendLine();
            }

            builder.AppendLine($"检索「{query}」共命中 {hits.Count} 个章节：").AppendLine();

            foreach (var hit in hits)
            {
                builder
                    .AppendLine($"## {hit.Section.TitlePath}")
                    .AppendLine($"- 出处：`{hit.Section.RelativePath}` 第 {hit.Section.StartLine}-{hit.Section.EndLine} 行")
                    .AppendLine($"- 来源：{DescribeSource(hit.Section.Source)}；得分：{hit.Score:F2}")
                    .AppendLine()
                    .AppendLine(hit.Section.Content)
                    .AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "{Tool} 失败：{ExceptionType} — {ExceptionMessage}；查询「{Query}」，来源 {Source}，耗时 {ElapsedMs} 毫秒。",
                "search_docs",
                ex.GetType().FullName,
                ex.Message,
                query,
                source ?? "all",
                stopwatch.ElapsedMilliseconds);

            return "检索时发生错误，请稍后重试。";
        }
    }

    /// <summary>
    /// 读取一篇文档的全文或指定章节
    /// </summary>
    /// <param name="path">相对仓库根的文档路径</param>
    /// <param name="section">章节标题，为空则返回全文</param>
    /// <returns>文档原文</returns>
    [McpServerTool(Name = "read_doc")]
    [Description("读取曦寒框架的一篇文档。先用 search_docs 找到路径，需要更多上下文时再用本工具。")]
    public string ReadDoc(
        [Description("相对仓库根的路径，例如 docs/guide/event-bus.md")] string path,
        [Description("章节标题，只返回该节。留空返回全文；全文过长时会改为返回章节目录")] string? section = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var snapshot = index.EnsureFresh();

            if (!locator.TryResolveDocumentPath(path, out var absolutePath))
            {
                // 越界路径按警告级别单独记录
                logger.LogWarning(
                    "{Tool} 拒绝越界路径：请求路径「{Path}」不在仓库根内，耗时 {ElapsedMs} 毫秒。",
                    "read_doc",
                    path,
                    stopwatch.ElapsedMilliseconds);

                return $"拒绝访问 `{path}`：路径必须是仓库根内的相对路径。";
            }

            // 只放行索引内的文档：按解析后的绝对路径比对，大小写规则与包含性校验共用 PathComparison
            var indexed = snapshot.Files.FirstOrDefault(
                f => f.AbsolutePath.Equals(absolutePath, DocSourceLocator.PathComparison));

            if (indexed is null)
            {
                LogReadDocOutcome("未在索引内", path, section, stopwatch);
                return BuildPathSuggestion(snapshot, path);
            }

            if (!File.Exists(absolutePath))
            {
                // 文件在索引内但已不存在
                logger.LogWarning(
                    "{Tool} 命中索引却读不到文件：「{Path}」可能已被删除或改名，索引尚未刷新，耗时 {ElapsedMs} 毫秒。",
                    "read_doc",
                    indexed.RelativePath,
                    stopwatch.ElapsedMilliseconds);

                return BuildPathSuggestion(snapshot, path);
            }

            // 以下使用索引中的规范相对路径
            var sections = snapshot.Sections.Where(s => s.RelativePath == indexed.RelativePath).ToList();

            if (!string.IsNullOrWhiteSpace(section))
            {
                var matched = sections.Where(s => s.Heading.Contains(section, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matched.Count == 0)
                {
                    LogReadDocOutcome("章节未找到", indexed.RelativePath, section, stopwatch);

                    var available = string.Join("、", sections.Select(s => s.Heading).Distinct());
                    return $"文档 `{path}` 中未找到章节「{section}」。可用章节：{available}";
                }

                LogReadDocOutcome("返回章节", indexed.RelativePath, section, stopwatch);

                var builder = new StringBuilder();
                foreach (var item in matched)
                {
                    builder
                        .AppendLine($"## {item.TitlePath}")
                        .AppendLine($"- 出处：`{item.RelativePath}` 第 {item.StartLine}-{item.EndLine} 行")
                        .AppendLine()
                        .AppendLine(item.Content)
                        .AppendLine();
                }

                return builder.ToString().TrimEnd();
            }

            var content = File.ReadAllText(absolutePath);
            if (content.Length <= options.MaxWholeDocumentLength)
            {
                LogReadDocOutcome("返回全文", indexed.RelativePath, section, stopwatch);
                return $"# `{path}`\n\n{content}";
            }

            // 全文超长时改为返回章节目录
            logger.LogInformation(
                "{Tool} 全文超长改返目录：「{Path}」共 {ContentLength} 个字符，超过上限 {MaxLength}，耗时 {ElapsedMs} 毫秒。",
                "read_doc",
                indexed.RelativePath,
                content.Length,
                options.MaxWholeDocumentLength,
                stopwatch.ElapsedMilliseconds);

            var headings = string.Join("\n", sections.Select(s => $"- {s.Heading}"));
            return $"""
                文档 `{path}` 共 {content.Length} 个字符，超过单次返回上限。
                请用 section 参数指定要读的章节。可用章节：

                {headings}
                """;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "{Tool} 失败：{ExceptionType} — {ExceptionMessage}；请求路径「{Path}」，章节「{Section}」，耗时 {ElapsedMs} 毫秒。",
                "read_doc",
                ex.GetType().FullName,
                ex.Message,
                path,
                section ?? string.Empty,
                stopwatch.ElapsedMilliseconds);

            return "读取文档时发生错误，请稍后重试。";
        }
    }

    /// <summary>
    /// 列出全部被索引的文档
    /// </summary>
    /// <param name="source">来源过滤</param>
    /// <param name="includeSections">是否展开章节标题</param>
    /// <returns>文档清单</returns>
    [McpServerTool(Name = "list_docs")]
    [Description("列出曦寒框架全部文档，用于建立整体地图。默认只列标题与摘要；includeSections 为 true 时展开章节标题，输出会大幅变长。")]
    public string ListDocs(
        [Description("来源过滤：guide、packages、readme、root、all。默认 all")] string? source = null,
        [Description("是否展开每篇的章节标题，默认 false")] bool includeSections = false)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var snapshot = index.EnsureFresh();

            var (filter, notice) = ParseSource(source);
            var files = filter is null ? snapshot.Files : snapshot.Files.Where(f => f.Source == filter).ToList();

            // 按相对路径对章节分组
            var sectionsByPath = snapshot.Sections.ToLookup(s => s.RelativePath, StringComparer.Ordinal);

            var builder = new StringBuilder();
            if (notice.Length > 0)
            {
                builder.AppendLine(notice).AppendLine();
            }

            builder.AppendLine($"共 {files.Count} 篇文档：").AppendLine();

            foreach (var file in files)
            {
                var sections = sectionsByPath[file.RelativePath];
                var title = sections.FirstOrDefault()?.DocumentTitle ?? file.RelativePath;

                builder.AppendLine($"- `{file.RelativePath}` — {title}");

                var summary = BuildSummary(sections);
                if (summary.Length > 0)
                {
                    builder.AppendLine($"  {summary}");
                }

                if (!includeSections)
                {
                    continue;
                }

                foreach (var heading in sections.Select(s => s.Heading).Distinct())
                {
                    builder.AppendLine($"  - {heading}");
                }
            }

            logger.LogInformation(
                "{Tool} 完成：来源 {Source}，列出 {FileCount} 篇文档（语料共 {TotalFileCount} 篇），展开章节 {IncludeSections}，耗时 {ElapsedMs} 毫秒。",
                "list_docs",
                source ?? "all",
                files.Count,
                snapshot.Files.Count,
                includeSections,
                stopwatch.ElapsedMilliseconds);

            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "{Tool} 失败：{ExceptionType} — {ExceptionMessage}；来源 {Source}，耗时 {ElapsedMs} 毫秒。",
                "list_docs",
                ex.GetType().FullName,
                ex.Message,
                source ?? "all",
                stopwatch.ElapsedMilliseconds);

            return "列出文档时发生错误，请稍后重试。";
        }
    }

    /// <summary>
    /// 记一条 read_doc 的结果日志
    /// </summary>
    /// <param name="outcome">结果分类，与返回给客户端的那段文字一一对应</param>
    /// <param name="path">文档路径</param>
    /// <param name="section">请求的章节，未指定时记空串</param>
    /// <param name="stopwatch">本次调用的计时器</param>
    /// <remarks>
    /// 路径越界、索引内文件缺失、全文超长改返目录三种情况单独记录，其余结果经本方法记录，以 <c>Outcome</c> 字段区分。
    /// </remarks>
    private void LogReadDocOutcome(string outcome, string path, string? section, Stopwatch stopwatch)
    {
        logger.LogInformation(
            "{Tool} 完成：结果 {Outcome}，文档「{Path}」，章节「{Section}」，耗时 {ElapsedMs} 毫秒。",
            "read_doc",
            outcome,
            path,
            section ?? string.Empty,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// 解析来源参数，无法识别时按全部处理并附提示
    /// </summary>
    private static (DocSourceKind? Filter, string Notice) ParseSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return (null, string.Empty);
        }

        return source.ToLowerInvariant() switch
        {
            "guide" => (DocSourceKind.Guide, string.Empty),
            "packages" or "package" => (DocSourceKind.Package, string.Empty),
            "readme" => (DocSourceKind.PackageReadme, string.Empty),
            "root" => (DocSourceKind.Root, string.Empty),
            _ => (null, $"提示：无法识别的 source 取值「{source}」，已按 all 处理。可用取值为 guide、packages、readme、root、all。")
        };
    }

    /// <summary>
    /// 描述来源分类
    /// </summary>
    private static string DescribeSource(DocSourceKind source)
    {
        return source switch
        {
            DocSourceKind.Guide => "使用指南",
            DocSourceKind.Package => "包文档",
            DocSourceKind.PackageReadme => "包自述",
            DocSourceKind.Root => "全局文档",
            _ => "未知"
        };
    }

    /// <summary>
    /// 从概述章节取一句话摘要
    /// </summary>
    private static string BuildSummary(IEnumerable<DocSection> sections)
    {
        var preamble = sections.FirstOrDefault(s => s.Heading == "概述");
        if (preamble is null)
        {
            return string.Empty;
        }

        foreach (var line in preamble.Content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('>') || trimmed.StartsWith('-') || trimmed.StartsWith('|') || trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var cleaned = trimmed.Replace("**", string.Empty).Replace("`", string.Empty);
            return cleaned.Length <= 80 ? cleaned : cleaned[..80];
        }

        return string.Empty;
    }

    /// <summary>
    /// 构造零命中时的回复，明确告知文档中没有相关内容
    /// </summary>
    private static string BuildEmptyResult(IndexSnapshot snapshot, string query, string notice)
    {
        var candidates = string.Join("\n", snapshot.Files.Take(10).Select(f => $"- `{f.RelativePath}`"));

        return $"""
            {notice}
            未找到与「{query}」相关的文档内容。曦寒框架的文档中没有涵盖这个主题，请不要基于猜测作答。

            可以换个关键词再试，或用 list_docs 查看全部文档。部分文档：
            {candidates}
            """.Trim();
    }

    /// <summary>
    /// 构造路径不在索引内时的候选建议
    /// </summary>
    private static string BuildPathSuggestion(IndexSnapshot snapshot, string path)
    {
        var target = Path.GetFileNameWithoutExtension(path);
        var suggestions = snapshot.Files
            .OrderByDescending(f => CountCommonCharacters(Path.GetFileNameWithoutExtension(f.RelativePath), target))
            .Take(3)
            .Select(f => $"- `{f.RelativePath}`");

        return $"""
            未找到文档 `{path}`。你可能是指：
            {string.Join("\n", suggestions)}
            """;
    }

    /// <summary>
    /// 统计两个文件名的共同字符数，用于粗略推荐相近路径
    /// </summary>
    private static int CountCommonCharacters(string left, string right)
    {
        var pool = right.ToLowerInvariant().ToList();
        var count = 0;

        foreach (var ch in left.ToLowerInvariant())
        {
            if (pool.Remove(ch))
            {
                count++;
            }
        }

        return count;
    }
}
