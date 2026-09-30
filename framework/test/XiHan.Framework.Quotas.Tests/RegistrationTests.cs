// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.Extensions.DependencyInjection;
using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas.Providers;
using XiHan.Framework.Quotas.Stores;
using XiHan.Framework.Timing;
using XiHan.Framework.Timing.Extensions.DependencyInjection;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 配额政策与配额存储的注册测试
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

        services.AddXiHanQuotas();

        var provider = services.BuildServiceProvider();
        Assert.IsType<DefaultQuotaPolicyProvider>(provider.GetRequiredService<IQuotaPolicyProvider>());
        Assert.IsType<DefaultQuotaStore>(provider.GetRequiredService<IQuotaStore>());

        // 配额服务按请求解析，才能承接持久化实现的按请求连接；它需要当前租户，故只断言登记不解析
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaService)).Lifetime);
    }

    /// <summary>
    /// 应用注册的政策来源不被默认实现覆盖
    /// </summary>
    [Fact]
    public void 应用注册的政策来源不被默认实现覆盖()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQuotaPolicyProvider, StubQuotaPolicyProvider>();

        services.AddXiHanQuotas();

        Assert.Equal(
            typeof(StubQuotaPolicyProvider),
            services.BuildServiceProvider().GetRequiredService<IQuotaPolicyProvider>().GetType());
    }

    /// <summary>
    /// 重复注册不产生重复的注册项
    /// </summary>
    [Fact]
    public void 重复注册不产生重复的注册项()
    {
        var services = new ServiceCollection();

        services.AddXiHanQuotas();
        services.AddXiHanQuotas();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaPolicyProvider));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaStore));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaService));
    }

    /// <summary>
    /// 不传配置时选项基础设施由本扩展自己登记
    /// </summary>
    /// <remarks>
    /// 存储需要 IOptions，不能依赖别的模块先 AddOptions；
    /// 解析 IQuotaStore 还需要时间模块注册 IClock，那是模块声明的依赖，不属本扩展职责。
    /// </remarks>
    [Fact]
    public void 不传配置也登记选项基础设施()
    {
        var services = new ServiceCollection();

        services.AddXiHanQuotas();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanQuotasOptions>>();

        Assert.Equal(TimeSpan.FromMinutes(5), options.Value.DefaultReservationTtl);
    }

    /// <summary>
    /// 非法选项在启动校验期失败
    /// </summary>
    [Fact]
    public void 非法选项在启动校验期失败()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Quotas:MaxTrackedReservations"] = "0"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddXiHanQuotas(configuration);

        var options = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<XiHanQuotasOptions>>();

        Assert.Throws<OptionsValidationException>(() => _ = options.CurrentValue);
    }

    /// <summary>
    /// 模块只声明租户抽象与时间模块为依赖
    /// </summary>
    [Fact]
    public void 模块只声明租户抽象与时间模块为依赖()
    {
        var dependsOn = (DependsOnAttribute[])typeof(XiHanQuotasModule)
            .GetCustomAttributes(typeof(DependsOnAttribute), inherit: true);

        var declared = dependsOn.SelectMany(static attribute => attribute.DependedTypes).ToArray();

        Assert.Equal([typeof(XiHanMultiTenancyAbstractionsModule), typeof(XiHanTimingModule)], declared);
    }

    /// <summary>
    /// 配额模块不引用授权、Web、ORM 与多租户实现程序集
    /// </summary>
    /// <remarks>
    /// 只能挡住「新增引用且被使用」的提交：未使用的程序集引用不会写进引用表。
    /// 多租户只允许依赖抽象包，功能开关与租户存储在实现包里，配额不该牵连进来。
    /// </remarks>
    [Fact]
    public void 配额模块不引用授权与Web与ORM与多租户实现程序集()
    {
        var referenced = Array.ConvertAll(
            typeof(XiHanQuotasModule).Assembly.GetReferencedAssemblies(),
            static assembly => assembly.Name ?? string.Empty);

        Assert.DoesNotContain("XiHan.Framework.Authorization", referenced);
        Assert.DoesNotContain("XiHan.Framework.Authorization.SqlSugar", referenced);
        Assert.DoesNotContain("XiHan.Framework.Web.Core", referenced);
        Assert.DoesNotContain("XiHan.Framework.Data", referenced);
        Assert.DoesNotContain("SqlSugar", referenced);
        Assert.DoesNotContain("XiHan.Framework.MultiTenancy", referenced);
        Assert.DoesNotContain("XiHan.Framework.Settings", referenced);
    }
}
