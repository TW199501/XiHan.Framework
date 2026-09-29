// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Authentication.SqlSugar.Users;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 认证存储 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanAuthenticationSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换认证模块的内存存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanAuthenticationSqlSugar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.Replace(ServiceDescriptor.Scoped<IUserStore, SqlSugarUserStore>());

        return services;
    }
}
