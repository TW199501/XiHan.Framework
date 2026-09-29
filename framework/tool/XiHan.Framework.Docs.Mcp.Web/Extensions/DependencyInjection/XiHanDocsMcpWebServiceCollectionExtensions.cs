// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Docs.Mcp.Indexing;
using XiHan.Framework.Docs.Mcp.Options;
using XiHan.Framework.Docs.Mcp.Search;
using XiHan.Framework.Docs.Mcp.Sources;
using XiHan.Framework.Docs.Mcp.Tools;
using XiHan.Framework.Docs.Mcp.Web.Options;

namespace XiHan.Framework.Docs.Mcp.Web.Extensions.DependencyInjection;

/// <summary>
/// 曦寒框架文档 MCP Server（HTTP 传输）服务集合扩展
/// </summary>
public static class XiHanDocsMcpWebServiceCollectionExtensions
{
    /// <summary>
    /// 请求体的字节数上限
    /// </summary>
    public const long MaxRequestBodySize = 64 * 1024;

    /// <summary>
    /// 添加文档 MCP Server 服务（绑定并校验配置、注册检索三层；仅在启用且配了密钥时注册 HTTP 传输与三个工具）
    /// </summary>
    /// <remarks>
    /// fail-closed：<see cref="XiHanDocsMcpWebOptions.IsExposable"/> 为 false 时不注册任何 MCP 服务，
    /// 端点映射据同一判定跳过（见 <c>MapXiHanDocsMcp</c>）；就绪暴露时由 <see cref="XiHanDocsMcpWebOptionsValidator"/> 在启动时校验配置。
    /// 索引与检索相关单例无条件注册，与 stdio 宿主（<c>framework/tool/XiHan.Framework.Docs.Mcp/Program.cs</c>）一致。
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">应用配置</param>
    /// <param name="repositoryRoot">仓库根的绝对路径</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanDocsMcpWeb(
        this IServiceCollection services,
        IConfiguration configuration,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var section = configuration.GetSection(XiHanDocsMcpWebOptions.SectionName);

        // 启动时校验配置
        services
            .AddOptions<XiHanDocsMcpWebOptions>()
            .Bind(section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<XiHanDocsMcpWebOptions>, XiHanDocsMcpWebOptionsValidator>();

        // 限制 Kestrel 单个请求体的字节数
        services.Configure<KestrelServerOptions>(kestrel => kestrel.Limits.MaxRequestBodySize = MaxRequestBodySize);

        services.AddSingleton(new DocsMcpOptions());
        services.AddSingleton(new DocSourceLocator(repositoryRoot));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DocIndex>();
        services.AddSingleton<SectionScorer>();
        services.AddSingleton<RelevanceGate>();
        services.AddSingleton(provider => SynonymExpander.Load(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "synonyms.json"),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<SynonymExpander>()));
        services.AddSingleton<DocsMcpTools>();

        var options = section.Get<XiHanDocsMcpWebOptions>() ?? new XiHanDocsMcpWebOptions();
        if (options.IsExposable)
        {
            // 显式登记 DocsMcpTools 中的工具
            services
                .AddMcpServer()
                .WithHttpTransport(transport => transport.Stateless = options.Stateless)
                .WithTools<DocsMcpTools>();
        }

        return services;
    }
}
