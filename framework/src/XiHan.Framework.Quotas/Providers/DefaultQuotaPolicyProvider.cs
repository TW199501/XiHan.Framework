// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Quotas;
using XiHan.Framework.Quotas.Abstractions;

namespace XiHan.Framework.Quotas.Providers;

/// <summary>
/// 配额政策提供器的框架默认实现
/// </summary>
/// <remarks>
/// 不接任何外部基础设施，一律返回 null 表示没有任何配额政策，调用方必须据此拒绝。
/// 无限额是显式政策，因此「查不到政策」永远不等于无限额。
/// </remarks>
public class DefaultQuotaPolicyProvider : IQuotaPolicyProvider
{
    /// <summary>
    /// 查找某租户对某配额项的政策，一律返回 null
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>始终为 null</returns>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">配额项标识为空白</exception>
    public Task<QuotaPolicy?> FindPolicyAsync(
        long tenantId,
        string quotaKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);

        return Task.FromResult<QuotaPolicy?>(null);
    }
}
