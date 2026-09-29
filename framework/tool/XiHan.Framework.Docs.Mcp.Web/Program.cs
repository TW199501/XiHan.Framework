// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using XiHan.Framework.Docs.Mcp.Indexing;
using XiHan.Framework.Docs.Mcp.Sources;
using XiHan.Framework.Docs.Mcp.Web.Extensions;
using XiHan.Framework.Docs.Mcp.Web.Extensions.DependencyInjection;
using XiHan.Framework.Docs.Mcp.Web.Options;

string repositoryRoot;
try
{
    repositoryRoot = DocSourceLocator.ResolveRepositoryRoot(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("XIHAN_DOCS_ROOT"));
}
catch (DocsRootNotFoundException ex)
{
    // 错误信息写入 stderr，并以退出码 1 结束
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddXiHanDocsMcpWeb(builder.Configuration, repositoryRoot);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("XiHan.Framework.Docs.Mcp.Web");
var options = app.Services.GetRequiredService<IOptions<XiHanDocsMcpWebOptions>>().Value;

// 启动时同步建立索引，建完才开始接受请求
app.Services.GetRequiredService<DocIndex>().EnsureFresh();

if (options.IsExposable)
{
    _ = app.MapXiHanDocsMcp(options);
    logger.LogInformation(
        "文档 MCP 端点已映射到 {Path}，仓库根 {Root}；请求须携带 {HeaderName} 或 Authorization: Bearer。",
        options.Path,
        repositoryRoot,
        options.HeaderName);
}
else
{
    // 未暴露端点：记录缺少的配置项
    var missing = new List<string>();
    if (!options.Enabled)
    {
        missing.Add($"{XiHanDocsMcpWebOptions.SectionName}:Enabled 为 false");
    }

    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        missing.Add($"{XiHanDocsMcpWebOptions.SectionName}:ApiKey 未配置");
    }

    logger.LogWarning(
        "文档 MCP 端点未暴露：{Missing}。进程已启动但不提供任何端点，补齐后重启即可（密钥请用 dotnet user-secrets 或环境变量 XiHan__Docs__Mcp__ApiKey 注入，切勿写进仓库）。",
        string.Join("；", missing));
}

await app.RunAsync();
return 0;
