// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 事件总线 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanSqlSugarEventBusServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 发件箱替换默认的进程内发件箱
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanSqlSugarEventBus(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanSqlSugarEventBoxOptions>(
            configuration.GetSection(XiHanSqlSugarEventBoxOptions.SectionName));

        services.Configure<XiHanDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config => config.ImplementationType = typeof(SqlSugarEventOutbox));
        });

        services.TryAddScoped<SqlSugarEventOutbox>();
        services.Replace(ServiceDescriptor.Scoped<IEventOutbox, SqlSugarEventOutbox>());

        return services;
    }
}
