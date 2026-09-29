// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;
using XiHan.Framework.Authentication.SqlSugar.Users;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 服务注册测试
/// </summary>
public class RegistrationTests
{
    /// <summary>
    /// 用户存储被顶替为作用域的 SqlSugar 实现
    /// </summary>
    [Fact]
    public void 用户存储被顶替为作用域的SqlSugar实现()
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IUserStore, DefaultUserStore>();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IUserStore));
        Assert.Equal(typeof(SqlSugarUserStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 注册系统时间提供程序
    /// </summary>
    [Fact]
    public void 注册系统时间提供程序()
    {
        var services = new ServiceCollection();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(TimeProvider));
        Assert.Same(TimeProvider.System, descriptor.ImplementationInstance);
    }

    /// <summary>
    /// 已注册的时间提供程序不被覆盖
    /// </summary>
    [Fact]
    public void 已注册的时间提供程序不被覆盖()
    {
        var services = new ServiceCollection();
        var custom = new MutableTimeProvider(DateTimeOffset.UtcNow);
        services.AddSingleton<TimeProvider>(custom);

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(TimeProvider));
        Assert.Same(custom, descriptor.ImplementationInstance);
    }

    /// <summary>
    /// 空参数抛出
    /// </summary>
    [Fact]
    public void 空参数抛出()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddXiHanAuthenticationSqlSugar(configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddXiHanAuthenticationSqlSugar(null!));
    }
}
