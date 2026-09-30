// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas.SqlSugar.Options;

/// <summary>
/// 曦寒配额 SqlSugar 提供程序选项
/// </summary>
public class XiHanQuotasSqlSugarOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Quotas:SqlSugar";

    /// <summary>
    /// 配额数据表所在连接的配置标识
    /// </summary>
    /// <remarks>
    /// 留空时用数据访问层的默认连接。配额表只建在平台库，因此这里固定取一个连接、
    /// 不随当前租户切换，否则跨租户读写会被环境租户静默改写。
    /// </remarks>
    public string? ConfigId { get; set; }
}
