// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Core.Exceptions;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas.Stores;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 配额模块装配与配置绑定测试
/// </summary>
public class XiHanQuotasModuleTests
{
    /// <summary>
    /// 模块继承自框架模块基类
    /// </summary>
    [Fact]
    public void 模块继承框架模块基类()
    {
        Assert.True(typeof(XiHanQuotasModule).IsSubclassOf(typeof(XiHanModule)));
    }

    /// <summary>
    /// 配置阶段登记配额政策与配额存储
    /// </summary>
    [Fact]
    public void 配置阶段登记配额服务()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        new XiHanQuotasModule().ConfigureServices(new ServiceConfigurationContext(services));

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaPolicyProvider));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IQuotaStore));
    }

    /// <summary>
    /// 配置阶段把配置节里的配额选项绑定进来
    /// </summary>
    [Fact]
    public void 配置阶段绑定配额选项()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Quotas:DefaultReservationTtl"] = "00:00:42",
                ["XiHan:Quotas:MaxTrackedReservations"] = "123",
                ["XiHan:Quotas:MaxTrackedBuckets"] = "45",
                ["XiHan:Quotas:NonPeriodicTombstoneRetention"] = "01:30:00"
            })
            .Build();

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        new XiHanQuotasModule().ConfigureServices(new ServiceConfigurationContext(services));

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanQuotasOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(42), options.DefaultReservationTtl);
        Assert.Equal(123, options.MaxTrackedReservations);
        Assert.Equal(45, options.MaxTrackedBuckets);
        Assert.Equal(TimeSpan.FromMinutes(90), options.NonPeriodicTombstoneRetention);
    }

    /// <summary>
    /// 缺少配置对象时模块显式失败
    /// </summary>
    [Fact]
    public void 缺少配置对象时模块显式失败()
    {
        IServiceCollection services = new ServiceCollection();

        Assert.Throws<XiHanException>(
            () => new XiHanQuotasModule().ConfigureServices(new ServiceConfigurationContext(services)));
    }
}
