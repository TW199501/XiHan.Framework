// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Features;

/// <summary>
/// 租户功能授权决策
/// </summary>
/// <remarks>
/// 只描述「该租户是否被允许使用该功能」，不含任何使用者身份，也不替代使用者权限校验。
/// 实例只能经 <see cref="Enabled"/> 与 <see cref="Denied"/> 创建，保证放行时无拒绝原因、拒绝时必有拒绝原因。
/// </remarks>
public sealed class FeatureDecision
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="featureKey">功能标识</param>
    /// <param name="isEnabled">是否放行</param>
    /// <param name="denyReason">拒绝原因</param>
    /// <param name="policyVersion">政策版本</param>
    private FeatureDecision(
        long tenantId,
        string featureKey,
        bool isEnabled,
        FeatureDenyReason? denyReason,
        string? policyVersion)
    {
        TenantId = tenantId;
        FeatureKey = featureKey;
        IsEnabled = isEnabled;
        DenyReason = denyReason;
        PolicyVersion = policyVersion;
    }

    /// <summary>
    /// 被决策的租户标识，平台为 0
    /// </summary>
    public long TenantId { get; }

    /// <summary>
    /// 被决策的功能标识
    /// </summary>
    public string FeatureKey { get; }

    /// <summary>
    /// 是否放行
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// 拒绝原因，放行时为 null
    /// </summary>
    public FeatureDenyReason? DenyReason { get; }

    /// <summary>
    /// 产出本决策的政策版本，来源未提供版本时为 null
    /// </summary>
    public string? PolicyVersion { get; }

    /// <summary>
    /// 创建放行决策
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="featureKey">功能标识，不可为空白</param>
    /// <param name="policyVersion">政策版本，可空</param>
    /// <returns>放行决策</returns>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">功能标识为空白</exception>
    public static FeatureDecision Enabled(long tenantId, string featureKey, string? policyVersion = null)
    {
        return new FeatureDecision(
            RequireTenantId(tenantId), RequireFeatureKey(featureKey), true, null, policyVersion);
    }

    /// <summary>
    /// 创建拒绝决策
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="featureKey">功能标识，不可为空白</param>
    /// <param name="denyReason">拒绝原因，不可为 null</param>
    /// <param name="policyVersion">政策版本，可空</param>
    /// <returns>拒绝决策</returns>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">功能标识为空白，或拒绝原因为 null</exception>
    public static FeatureDecision Denied(
        long tenantId, string featureKey, FeatureDenyReason? denyReason, string? policyVersion = null)
    {
        if (denyReason is null)
        {
            throw new ArgumentException("拒绝决策必须给出拒绝原因。", nameof(denyReason));
        }

        return new FeatureDecision(
            RequireTenantId(tenantId), RequireFeatureKey(featureKey), false, denyReason, policyVersion);
    }

    /// <summary>
    /// 校验租户标识并返回原值
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <returns>已校验的租户标识</returns>
    internal static long RequireTenantId(long tenantId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tenantId);

        return tenantId;
    }

    /// <summary>
    /// 校验功能标识并返回原值
    /// </summary>
    /// <param name="featureKey">功能标识</param>
    /// <returns>已校验的功能标识</returns>
    internal static string RequireFeatureKey(string featureKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureKey);

        return featureKey;
    }
}
