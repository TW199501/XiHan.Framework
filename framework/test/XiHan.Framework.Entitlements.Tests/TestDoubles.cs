// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Entitlements.Quotas;
using XiHan.Framework.Entitlements.Quotas.Abstractions;

namespace XiHan.Framework.Entitlements.Tests;

/// <summary>
/// 固定返回同一条政策的配额政策提供器测试桩
/// </summary>
internal sealed class StubQuotaPolicyProvider : IQuotaPolicyProvider
{
    /// <summary>
    /// 查找政策，一律返回构造时给定的政策
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>政策</returns>
    public Task<QuotaPolicy?> FindPolicyAsync(
        long tenantId,
        string quotaKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<QuotaPolicy?>(QuotaPolicy.Limited(10, QuotaPeriod.Day, "stub"));
    }
}
