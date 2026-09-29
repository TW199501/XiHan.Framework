// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net;

namespace XiHan.Framework.Docs.Mcp.Web.Tests;

/// <summary>
/// 端点鉴权：两种放行写法与四种拒绝情形
/// </summary>
/// <remarks>
/// 被测的 <c>McpApiKeyEndpointFilter</c> 源文件经 csproj 的 <c>&lt;Compile Link&gt;</c>
/// 链接自 <c>framework/src/XiHan.Framework.Web.Mcp/Filters/McpApiKeyEndpointFilter.cs</c>。
/// </remarks>
public class ApiKeyAuthTests : IAsyncLifetime
{
    private const string ApiKey = "correct-horse-battery-staple";

    private DocsMcpWebTestHost _host = null!;

    /// <summary>
    /// 起一个已就绪暴露的宿主，全组用例共用
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

    [Fact]
    public async Task 请求头携带正确密钥可放行()
    {
        using var response = await _host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Bearer携带正确密钥可放行()
    {
        using var response = await _host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 密钥不对返回401()
    {
        using var response = await _host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", "wrong-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 密钥为空串返回401()
    {
        using var response = await _host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", string.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 完全不带鉴权头返回401()
    {
        using var response = await _host.SendInitializeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bearer之外的授权方案返回401()
    {
        using var response = await _host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("Authorization", $"Basic {ApiKey}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 手写报文的对照组：单个正确密钥必须放行
    /// </summary>
    /// <returns>异步任务</returns>
    [Fact]
    public async Task 手写报文单个正确密钥可放行()
    {
        var status = await _host.SendRawInitializeAsync($"X-Api-Key: {ApiKey}");

        Assert.Equal(200, status);
    }

    /// <summary>
    /// 同一请求上真的送两行 X-Api-Key，其中一行是对的
    /// </summary>
    /// <remarks>
    /// 两行同名请求头的 <c>StringValues</c> 经 <c>ToString()</c> 以逗号拼接后与密钥不等，返回 401。
    /// 用手写报文发送两行请求头。
    /// </remarks>
    /// <returns>异步任务</returns>
    [Fact]
    public async Task 多值请求头返回401()
    {
        var status = await _host.SendRawInitializeAsync(
            $"X-Api-Key: {ApiKey}",
            "X-Api-Key: second-value");

        Assert.Equal(401, status);
    }

    /// <summary>
    /// 正确值排在第二行时同样必须 401
    /// </summary>
    /// <returns>异步任务</returns>
    [Fact]
    public async Task 多值请求头顺序颠倒同样返回401()
    {
        var status = await _host.SendRawInitializeAsync(
            "X-Api-Key: first-value",
            $"X-Api-Key: {ApiKey}");

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task 请求头名可配置()
    {
        await using var host = await DocsMcpWebTestHost.StartAsync(
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", ApiKey),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:HeaderName", "X-Docs-Key"));

        using var 旧头 = await host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey));
        using var 新头 = await host.SendInitializeAsync(
            request => request.Headers.TryAddWithoutValidation("X-Docs-Key", ApiKey));

        Assert.Equal(HttpStatusCode.Unauthorized, 旧头.StatusCode);
        Assert.Equal(HttpStatusCode.OK, 新头.StatusCode);
    }
}
