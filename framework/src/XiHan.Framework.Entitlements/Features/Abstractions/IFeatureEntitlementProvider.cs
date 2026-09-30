// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Features.Abstractions;

/// <summary>
/// 租户功能授权决策提供器
/// </summary>
/// <remarks>
/// 只回答「该租户能否使用该功能」，不感知使用者身份，也不替代使用者权限校验：
/// 功能授权通过不代表使用者有权限，两者必须由调用方各自检查。
/// 实现必须对没有任何授权来源认识的功能返回 <see cref="FeatureDenyReason.UnknownFeature"/>，不得默认放行。
/// </remarks>
public interface IFeatureEntitlementProvider
{
    /// <summary>
    /// 评估某租户对某功能的授权
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="featureKey">功能标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>功能授权决策</returns>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">功能标识为空白</exception>
    Task<FeatureDecision> EvaluateAsync(
        long tenantId,
        string featureKey,
        CancellationToken cancellationToken = default);
}
