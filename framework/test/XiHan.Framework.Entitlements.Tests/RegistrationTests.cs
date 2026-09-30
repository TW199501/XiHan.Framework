// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Entitlements.Extensions.DependencyInjection;
using XiHan.Framework.Entitlements.Features.Abstractions;
using XiHan.Framework.Entitlements.Providers;
using XiHan.Framework.Entitlements.Quotas.Abstractions;
using XiHan.Framework.Entitlements.Quotas.Stores;
using XiHan.Framework.Timing;
using XiHan.Framework.Timing.Extensions.DependencyInjection;

namespace XiHan.Framework.Entitlements.Tests;

/// <summary>
/// 功能授权、配额政策与配额存储的注册测试
/// </summary>
public class RegistrationTests
{
    /// <summary>
    /// 未接入政策来源时注册一律拒绝的安全默认实现
    /// </summary>
    [Fact]
    public void 默认注册安全拒绝实现()
    {
        var services = new ServiceCollection();
        services.AddXiHanTiming();

        services.AddXiHanEntitlements();

        var provider = services.BuildServiceProvider();
        Assert.IsType<DefaultFeatureEntitlementProvider>(provider.GetRequiredService<IFeatureEntitlementProvider>());
        Assert.IsType<DefaultQuotaPolicyProvider>(provider.GetRequiredService<IQuotaPolicyProvider>());
        Assert.IsType<DefaultQuotaStore>(provider.GetRequiredService<IQuotaStore>());
    }

    /// <summary>
    /// 应用注册的政策来源不被默认实现覆盖
    /// </summary>
    [Fact]
    public void 应用注册的政策来源不被默认实现覆盖()
    {
        var services = new ServiceCollection();
        var custom = new StubFeatureEntitlementProvider();
        services.AddSingleton<IFeatureEntitlementProvider>(custom);

        services.AddXiHanEntitlements();

        Assert.Same(custom, services.BuildServiceProvider().GetRequiredService<IFeatureEntitlementProvider>());
    }

    /// <summary>
    /// 重复注册不产生重复的注册项
    /// </summary>
    [Fact]
    public void 重复注册不产生重复的注册项()
    {
        var services = new ServiceCollection();

        services.AddXiHanEntitlements();
        services.AddXiHanEntitlements();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IFeatureEntitlementProvider));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaPolicyProvider));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaStore));
    }

    /// <summary>
    /// 模块只声明时间模块为依赖
    /// </summary>
    [Fact]
    public void 模块只声明时间模块为依赖()
    {
        var dependsOn = (DependsOnAttribute[])typeof(XiHanEntitlementsModule)
            .GetCustomAttributes(typeof(DependsOnAttribute), inherit: true);

        var declared = dependsOn.SelectMany(static attribute => attribute.DependedTypes).ToArray();

        Assert.Equal([typeof(XiHanTimingModule)], declared);
    }
}
