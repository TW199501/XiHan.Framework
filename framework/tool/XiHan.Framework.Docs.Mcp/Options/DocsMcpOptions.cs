// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Docs.Mcp.Sources;

namespace XiHan.Framework.Docs.Mcp.Options;

/// <summary>
/// 文档 MCP 服务端的可调参数
/// </summary>
public sealed class DocsMcpOptions
{
    /// <summary>
    /// 检索查询串的字符数上限，超出时拒绝检索
    /// </summary>
    public const int MaxQueryLength = 512;

    /// <summary>
    /// 标题命中的加权倍数
    /// </summary>
    public double TitleBoost { get; init; } = 3.0;

    /// <summary>
    /// 同一文件最多返回的章节数
    /// </summary>
    public int MaxSectionsPerFile { get; init; } = 2;

    /// <summary>
    /// 检索结果的默认条数
    /// </summary>
    public int DefaultLimit { get; init; } = 5;

    /// <summary>
    /// 检索结果的条数上限，超出时截断而非报错
    /// </summary>
    public int MaxLimit { get; init; } = 15;

    /// <summary>
    /// 热更新检查的节流间隔，两次查询间隔小于此值时跳过 mtime 扫描
    /// </summary>
    public TimeSpan RefreshThrottle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 单篇文档整体返回的字符数上限，超出时改为返回章节目录
    /// </summary>
    public int MaxWholeDocumentLength { get; init; } = 30 * 1024;

    /// <summary>
    /// 判定「这个问题在问我们的文档吗」的阈值：查询里语料认识的词条，其 IDF 之和至少要占全部词条 IDF 之和的这个比例
    /// </summary>
    public double MinKnownTermCoverage { get; init; } = 0.90;

    /// <summary>
    /// 拉丁词条要出现在至少这么多个章节里，才算「语料认识它」
    /// </summary>
    public int MinLatinTermDocumentFrequency { get; init; } = 2;

    /// <summary>
    /// 语料至少要有这么多章节，相关性截断才生效
    /// </summary>
    public int MinSectionsForRelevanceCutoff { get; init; } = 200;

    /// <summary>
    /// 各来源的排序权重
    /// </summary>
    public IReadOnlyDictionary<DocSourceKind, double> SourceWeights { get; init; } =
        new Dictionary<DocSourceKind, double>
        {
            [DocSourceKind.Guide] = 1.2,
            [DocSourceKind.Package] = 1.0,
            [DocSourceKind.Root] = 0.9,
            [DocSourceKind.PackageReadme] = 0.8
        };
}
