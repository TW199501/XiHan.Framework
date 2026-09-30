// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.MultiTenancy.Features;
using XiHan.Framework.MultiTenancy.Tests.Fakes;

namespace XiHan.Framework.MultiTenancy.Tests;

/// <summary>
/// 租户功能检查器的测试
/// </summary>
/// <remarks>
/// 只覆盖 E-23 用量配额所依赖的两条行为：真值判定，以及「功能未知」与「显式关闭」的区分。
/// 配额侧的「未知功能默认拒绝」正是靠这两条成立，故此前它们没有任何测试这一点必须补上。
/// 平台态取值与跨租户互不影响属检查器自身的完整边界，另单跟踪，不在本文件断言。
/// </remarks>
public class TenantFeatureCheckerTests
{
    /// <summary>
    /// 功能开关的设定键前缀不漂移
    /// </summary>
    /// <remarks>
    /// 前缀已随既有设定落库，改动等于历史功能开关全部失配。
    /// </remarks>
    [Fact]
    public void FeatureKeyPrefix_IsStable()
    {
        Assert.Equal("Feature:", TenantFeatureChecker.FeatureKeyPrefix);
    }

    /// <summary>
    /// 认可四种真值写法且忽略大小写
    /// </summary>
    /// <param name="value">设定值</param>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("TRUE")]
    [InlineData("On")]
    public async Task IsEnabledAsync_WithTruthValue_ReturnsTrue(string value)
    {
        var checker = CreateChecker(value);

        Assert.True(await checker.IsEnabledAsync("saas.export"));
    }

    /// <summary>
    /// 显式关闭的值一律判为未启用
    /// </summary>
    /// <remarks>
    /// 含两侧留空白的真值写法：检查器只对功能名做 Trim，不修剪设定值，
    /// 因此 "  true  " 落进「非真值」而按未启用处理。方向是保守拒绝，不改成放行。
    /// </remarks>
    /// <param name="value">设定值</param>
    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("  true  ")]
    public async Task IsEnabledAsync_WithNonTruthValue_ReturnsFalse(string value)
    {
        var checker = CreateChecker(value);

        Assert.False(await checker.IsEnabledAsync("saas.export"));
    }

    /// <summary>
    /// 取值接口能区分「功能未知」与「显式关闭」
    /// </summary>
    /// <remarks>
    /// 这是配额侧拒绝原因的唯一来源：null 表示没有任何授权来源认识该功能，
    /// 非 null 但非真值表示功能已知而未授权。两者不能混为一谈。
    /// </remarks>
    [Fact]
    public async Task GetValueOrNullAsync_DistinguishesUnknownFromExplicitOff()
    {
        var unknown = CreateChecker(null);
        var explicitOff = CreateChecker("false");

        Assert.Null(await unknown.GetValueOrNullAsync("saas.export"));
        Assert.Equal("false", await explicitOff.GetValueOrNullAsync("saas.export"));
    }

    /// <summary>
    /// 功能未知时按传入的默认值决定，默认默认值为拒绝
    /// </summary>
    /// <remarks>
    /// 默认形参必须是 false：配额侧「未知功能默认拒绝」的验收条件依赖调用方不显式改传 true。
    /// </remarks>
    [Fact]
    public async Task IsEnabledAsync_WhenFeatureUnknown_DefaultsToDeny()
    {
        var checker = CreateChecker(null);

        Assert.False(await checker.IsEnabledAsync("saas.export"));
        Assert.True(await checker.IsEnabledAsync("saas.export", defaultValue: true));
    }

    /// <summary>
    /// 功能名空白时按未知处理而不抛异常
    /// </summary>
    /// <param name="featureName">功能名</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsEnabledAsync_WithBlankFeatureName_TreatedAsUnknown(string featureName)
    {
        var checker = CreateChecker("true");

        Assert.Null(await checker.GetValueOrNullAsync(featureName));
        Assert.False(await checker.IsEnabledAsync(featureName));
    }

    /// <summary>
    /// 以功能名前缀、租户提供者与租户标识去查设定存储
    /// </summary>
    [Fact]
    public async Task GetValueOrNullAsync_UsesFeaturePrefixAndTenantProviderKey()
    {
        var store = new FakeSettingStore();
        var currentTenant = new FakeCurrentTenant { Id = 1001, Name = "acme" };
        store.Seed("Feature:saas.export", "T", "1001", "true");

        var enabled = await new TenantFeatureChecker(currentTenant, store).IsEnabledAsync("saas.export");

        Assert.True(enabled);
        var call = Assert.Single(store.GetOrNullCalls);
        Assert.Equal("Feature:saas.export", call.Name);
        Assert.Equal("T", call.ProviderName);
        Assert.Equal("1001", call.ProviderKey);
    }

    /// <summary>
    /// 创建带指定设定值的功能检查器
    /// </summary>
    /// <param name="value">预置的功能设定值，null 表示没有任何来源认识该功能</param>
    /// <returns>功能检查器</returns>
    private static TenantFeatureChecker CreateChecker(string? value)
    {
        var store = new FakeSettingStore();
        var currentTenant = new FakeCurrentTenant { Id = 1001, Name = "acme" };
        if (value is not null)
        {
            store.Seed("Feature:saas.export", "T", "1001", value);
        }

        return new TenantFeatureChecker(currentTenant, store);
    }
}
