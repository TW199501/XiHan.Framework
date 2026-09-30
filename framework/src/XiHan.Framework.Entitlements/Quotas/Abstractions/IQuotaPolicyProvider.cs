// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Quotas.Abstractions;

/// <summary>
/// 配额政策提供器
/// </summary>
/// <remarks>
/// 政策来源由应用提供（数据库、配置或计费系统皆可），框架不预设来源。
/// 返回 null 表示该租户没有该配额项的政策，调用方必须拒绝，不得当作无限额。
/// </remarks>
public interface IQuotaPolicyProvider
{
    /// <summary>
    /// 查找某租户对某配额项的政策
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>配额政策；无政策时为 null</returns>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">配额项标识为空白</exception>
    Task<QuotaPolicy?> FindPolicyAsync(
        long tenantId,
        string quotaKey,
        CancellationToken cancellationToken = default);
}
