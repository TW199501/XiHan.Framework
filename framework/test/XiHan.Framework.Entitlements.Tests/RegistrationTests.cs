// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Entitlements.Extensions.DependencyInjection;
using XiHan.Framework.Entitlements.Features.Abstractions;
using XiHan.Framework.Entitlements.Providers;
using XiHan.Framework.Entitlements.Quotas.Abstractions;

namespace XiHan.Framework.Entitlements.Tests;

/// <summary>
/// 功能授权与配额政策注册测试
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

        services.AddXiHanEntitlements();

        var provider = services.BuildServiceProvider().GetRequiredService<IFeatureEntitlementProvider>();
        var policyProvider = services.BuildServiceProvider().GetRequiredService<IQuotaPolicyProvider>();

        Assert.IsType<DefaultFeatureEntitlementProvider>(provider);
        Assert.IsType<DefaultQuotaPolicyProvider>(policyProvider);
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
    /// 重复注册不产生重复的政策来源
    /// </summary>
    [Fact]
    public void 重复注册不产生重复的政策来源()
    {
        var services = new ServiceCollection();

        services.AddXiHanEntitlements();
        services.AddXiHanEntitlements();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IFeatureEntitlementProvider));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaPolicyProvider));
    }

    /// <summary>
    /// 模块声明只依赖核心模块
    /// </summary>
    [Fact]
    public void 模块声明只依赖核心模块()
    {
        var dependsOn = typeof(XiHanEntitlementsModule)
            .GetCustomAttributes(typeof(XiHan.Framework.Core.Modularity.DependsOnAttribute), inherit: true);

        Assert.Empty(dependsOn);
    }
}
