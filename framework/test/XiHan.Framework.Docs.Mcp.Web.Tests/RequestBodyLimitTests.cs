// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using XiHan.Framework.Docs.Mcp.Web.Extensions.DependencyInjection;

namespace XiHan.Framework.Docs.Mcp.Web.Tests;

/// <summary>
/// 请求体大小上限
/// </summary>
public class RequestBodyLimitTests : IAsyncLifetime
{
    private const string ApiKey = "correct-horse-battery-staple";

    private DocsMcpWebTestHost _host = null!;

    /// <summary>
    /// 起一个已就绪暴露的宿主
    /// </summary>
    /// <returns>异步任务</returns>
    public async ValueTask InitializeAsync()
    {
        _host = await DocsMcpWebTestHost.StartAsync(
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", ApiKey));
    }

    /// <summary>
    /// 停掉宿主
    /// </summary>
    /// <returns>异步任务</returns>
    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    /// <summary>
    /// 请求体超过上限时返回 413
    /// </summary>
    /// <returns>异步任务</returns>
    [Fact]
    public async Task 请求体超过上限返回413()
    {
        var padding = new string(' ', (int)XiHanDocsMcpWebServiceCollectionExtensions.MaxRequestBodySize);
        const string initialize =
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"docs-mcp-web-tests","version":"1.0.0"}}}
            """;
        var payload = initialize + padding;

        using var request = DocsMcpWebTestHost.CreateInitializeRequest();
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);

        using var response = await _host.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
