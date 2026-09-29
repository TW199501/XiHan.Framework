// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Workflow.Extensions.DependencyInjection;
using XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Workflow.SqlSugar.Options;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 服务注册测试
/// </summary>
public class ServiceRegistrationTests
{
    /// <summary>
    /// 选项从配置节绑定
    /// </summary>
    [Fact]
    public void 选项从配置节绑定()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Workflow:SqlSugar:ConfigId"] = "Workflow"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddXiHanWorkflowSqlSugar(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<XiHanWorkflowSqlSugarOptions>>().Value;
        Assert.Equal("Workflow", options.ConfigId);
        Assert.Equal("XiHan:Workflow:SqlSugar", XiHanWorkflowSqlSugarOptions.SectionName);
    }

    /// <summary>
    /// 执行器注册为作用域服务
    /// </summary>
    [Fact]
    public void 执行器注册为作用域服务()
    {
        var services = CreateServices(registerSqlSugarFirst: false);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(WorkflowSqlSugarExecutor));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static IServiceCollection CreateServices(bool registerSqlSugarFirst)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        if (registerSqlSugarFirst)
        {
            services.AddXiHanWorkflowSqlSugar(configuration);
            services.AddXiHanWorkflow(configuration);
        }
        else
        {
            services.AddXiHanWorkflow(configuration);
            services.AddXiHanWorkflowSqlSugar(configuration);
        }

        return services;
    }
}
