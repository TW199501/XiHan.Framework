// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Docs.Mcp.Sources;

namespace XiHan.Framework.Docs.Mcp.Indexing;

/// <summary>
/// 一次重建产出的索引快照，三者互相自洽且整体不可变
/// </summary>
/// <param name="Sections">全部章节，下标与倒排索引中的 SectionId 对应</param>
/// <param name="Index">倒排索引</param>
/// <param name="Files">被索引的文件列表</param>
/// <remarks>
/// 章节列表与倒排索引须从同一快照成对取用：倒排索引中的 SectionId 是本快照章节列表的下标。
/// </remarks>
public sealed record IndexSnapshot(
    IReadOnlyList<DocSection> Sections,
    BigramIndex Index,
    IReadOnlyList<DocFile> Files);
