// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱注册测试
/// </summary>
public class OutboxRegistrationTests
{
    /// <summary>
    /// 发件箱实现类型被顶替
    /// </summary>
    [Fact]
    public void 发件箱实现类型被顶替()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<XiHanDistributedEventBusOptions>>().Value;

        Assert.Equal(typeof(SqlSugarEventOutbox), options.Outboxes["Default"].ImplementationType);
    }

    /// <summary>
    /// 发件箱接口注册被顶替
    /// </summary>
    [Fact]
    public void 发件箱接口注册被顶替()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IEventOutbox));

        Assert.Equal(typeof(SqlSugarEventOutbox), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 模拟事件总线模块已注册的默认值
    /// </summary>
    private static ServiceCollection BuildServicesWithDefaults()
    {
        var services = new ServiceCollection();

        services.Configure<XiHanDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config =>
            {
                if (config.ImplementationType == default)
                {
                    config.ImplementationType = typeof(DefaultEventOutbox);
                }
            });
        });

        services.TryAddSingleton<DefaultEventOutbox>();
        services.TryAddSingleton<IEventOutbox>(sp => sp.GetRequiredService<DefaultEventOutbox>());

        return services;
    }
}
