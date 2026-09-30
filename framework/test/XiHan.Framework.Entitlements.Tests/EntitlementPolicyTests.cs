// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Entitlements.Features;
using XiHan.Framework.Entitlements.Features.Abstractions;
using XiHan.Framework.Entitlements.Providers;
using XiHan.Framework.Entitlements.Quotas;
using XiHan.Framework.Entitlements.Quotas.Abstractions;

namespace XiHan.Framework.Entitlements.Tests;

/// <summary>
/// 功能授权与配额政策契约测试
/// </summary>
public class EntitlementPolicyTests
{
    /// <summary>
    /// 默认功能授权提供器把任何功能都按未知功能拒绝
    /// </summary>
    [Fact]
    public async Task 默认功能授权提供器一律拒绝未知功能()
    {
        var provider = new DefaultFeatureEntitlementProvider();

        var decision = await provider.EvaluateAsync(1001, "saas.export");

        Assert.False(decision.IsEnabled);
        Assert.Equal(FeatureDenyReason.UnknownFeature, decision.DenyReason);
        Assert.Equal(1001, decision.TenantId);
        Assert.Equal("saas.export", decision.FeatureKey);
    }

    /// <summary>
    /// 平台零号租户同样被默认功能授权提供器拒绝
    /// </summary>
    [Fact]
    public async Task 平台零号租户同样按未知功能拒绝()
    {
        var provider = new DefaultFeatureEntitlementProvider();

        var decision = await provider.EvaluateAsync(0, "saas.export");

        Assert.False(decision.IsEnabled);
        Assert.Equal(FeatureDenyReason.UnknownFeature, decision.DenyReason);
    }

    /// <summary>
    /// 功能授权契约对负数租户标识快速失败
    /// </summary>
    [Fact]
    public async Task 负数租户标识快速失败()
    {
        var provider = new DefaultFeatureEntitlementProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => provider.EvaluateAsync(-1, "saas.export"));
    }

    /// <summary>
    /// 功能授权契约对空白功能标识快速失败
    /// </summary>
    [Fact]
    public async Task 空白功能标识快速失败()
    {
        var provider = new DefaultFeatureEntitlementProvider();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.EvaluateAsync(1001, "   "));
    }

    /// <summary>
    /// 放行决策不得携带拒绝原因
    /// </summary>
    [Fact]
    public void 放行决策不携带拒绝原因()
    {
        var decision = FeatureDecision.Enabled(1001, "saas.export", "v1");

        Assert.True(decision.IsEnabled);
        Assert.Null(decision.DenyReason);
        Assert.Equal("v1", decision.PolicyVersion);
    }

    /// <summary>
    /// 拒绝决策缺少拒绝原因时快速失败
    /// </summary>
    [Fact]
    public void 拒绝决策缺少拒绝原因快速失败()
    {
        Assert.Throws<ArgumentException>(() => FeatureDecision.Denied(1001, "saas.export", null));
    }

    /// <summary>
    /// 政策按租户区分，租户甲的授权不影响租户乙
    /// </summary>
    [Fact]
    public async Task 租户甲的授权不影响租户乙()
    {
        var provider = new StubFeatureEntitlementProvider();
        provider.Grants.Add((1001, "saas.export"));

        var granted = await provider.EvaluateAsync(1001, "saas.export");
        var notGranted = await provider.EvaluateAsync(1002, "saas.export");

        Assert.True(granted.IsEnabled);
        Assert.False(notGranted.IsEnabled);
        Assert.Equal(FeatureDenyReason.NotEntitled, notGranted.DenyReason);
    }

    /// <summary>
    /// 默认配额政策提供器一律返回无政策
    /// </summary>
    [Fact]
    public async Task 默认配额政策提供器返回无政策()
    {
        var provider = new DefaultQuotaPolicyProvider();

        var policy = await provider.FindPolicyAsync(1001, "export.rows");

        Assert.Null(policy);
    }

    /// <summary>
    /// 有限额政策的配额上限必须为正数
    /// </summary>
    [Fact]
    public void 有限额政策的配额上限必须为正数()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuotaPolicy.Limited(0, QuotaPeriod.Day, "v1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuotaPolicy.Limited(-1, QuotaPeriod.Day, "v1"));
    }

    /// <summary>
    /// 无限额是显式政策，上限为 null 且周期为不周期
    /// </summary>
    [Fact]
    public void 无限额政策上限为空且周期为不周期()
    {
        var policy = QuotaPolicy.Unlimited("v1");

        Assert.True(policy.IsUnlimited);
        Assert.Null(policy.Limit);
        Assert.Equal(QuotaPeriod.None, policy.Period);
    }

    /// <summary>
    /// 政策版本空白的政策快速失败
    /// </summary>
    [Fact]
    public void 政策版本空白快速失败()
    {
        Assert.Throws<ArgumentException>(() => QuotaPolicy.Limited(10, QuotaPeriod.Day, " "));
        Assert.Throws<ArgumentException>(() => QuotaPolicy.Unlimited(""));
    }

    /// <summary>
    /// 按 UTC 自然日对齐计量周期起点
    /// </summary>
    [Fact]
    public void 日周期按UTC自然日对齐()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");
        var now = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), policy.ResolvePeriodStart(now));
    }

    /// <summary>
    /// 按 UTC 自然月对齐计量周期起点
    /// </summary>
    [Fact]
    public void 月周期按UTC自然月对齐()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Month, "v1");
        var now = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), policy.ResolvePeriodStart(now));
    }

    /// <summary>
    /// 不周期政策不切分周期，起点固定为最小值
    /// </summary>
    [Fact]
    public void 不周期政策不切分周期()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.None, "v1");

        Assert.Equal(DateTimeOffset.MinValue, policy.ResolvePeriodStart(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 带偏移的时刻先转 UTC 再对齐周期，UTC 日界不因本地偏移而误判
    /// </summary>
    [Fact]
    public void 带偏移的时刻先转UTC再对齐周期()
    {
        var policy = QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1");
        var withOffset = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(8));

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), policy.ResolvePeriodStart(withOffset));
    }

    /// <summary>
    /// 授权功能不自动授予用户权限，也不引用授权层程序集
    /// </summary>
    [Fact]
    public void 功能授权模块不引用授权与Web与ORM程序集()
    {
        var referenced = Array.ConvertAll(
            typeof(XiHanEntitlementsModule).Assembly.GetReferencedAssemblies(),
            static assembly => assembly.Name ?? string.Empty);

        Assert.DoesNotContain("XiHan.Framework.Authorization", referenced);
        Assert.DoesNotContain("XiHan.Framework.Authorization.SqlSugar", referenced);
        Assert.DoesNotContain("XiHan.Framework.Web.Core", referenced);
        Assert.DoesNotContain("XiHan.Framework.Data", referenced);
        Assert.DoesNotContain("SqlSugar", referenced);
    }
}
