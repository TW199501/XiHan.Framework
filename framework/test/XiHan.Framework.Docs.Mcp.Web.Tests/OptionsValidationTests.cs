// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;

namespace XiHan.Framework.Docs.Mcp.Web.Tests;

/// <summary>
/// 启动期配置校验：配错的部署必须在开始服务之前就失败
/// </summary>
/// <remarks>
/// 全部用例经 <see cref="DocsMcpWebTestHost.StartAsync"/> 走真实的宿主启动路径。
/// </remarks>
public class OptionsValidationTests
{
    /// <summary>
    /// 够长的密钥，用在所有「密钥本身没问题」的用例里
    /// </summary>
    private const string ValidApiKey = "correct-horse-battery-staple";

    /// <summary>
    /// 各种非法的请求头名与端点路径，每条都必须拒绝启动
    /// </summary>
    public static TheoryData<string, string, string, string> 非法的配置 => new()
    {
        { "请求头名为空串", "XiHan:Docs:Mcp:HeaderName", string.Empty, "HeaderName" },
        { "请求头名全是空白", "XiHan:Docs:Mcp:HeaderName", "   ", "HeaderName" },
        { "请求头名中间有空格", "XiHan:Docs:Mcp:HeaderName", "X Api Key", "HeaderName" },
        { "请求头名含冒号", "XiHan:Docs:Mcp:HeaderName", "X-Api-Key:", "HeaderName" },
        { "请求头名含中文", "XiHan:Docs:Mcp:HeaderName", "X-密钥", "HeaderName" },
        { "路径为空串", "XiHan:Docs:Mcp:Path", string.Empty, "Path" },
        { "路径全是空白", "XiHan:Docs:Mcp:Path", "   ", "Path" },
        { "路径不以斜杠开头", "XiHan:Docs:Mcp:Path", "mcp", "Path" },
        { "路径含空格", "XiHan:Docs:Mcp:Path", "/docs mcp", "Path" }
    };

    [Theory]
    [MemberData(nameof(非法的配置))]
    public async Task 配置非法时拒绝启动(string 场景, string 键, string 值, string 应出现在消息里的设置名)
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await DocsMcpWebTestHost.StartAsync(
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", ValidApiKey),
                new KeyValuePair<string, string?>(键, 值)));

        // 异常消息点名配错的设置项
        var expected = $"XiHan:Docs:Mcp:{应出现在消息里的设置名}";

        Assert.True(
            exception.Message.Contains(expected, StringComparison.Ordinal),
            $"{场景}：校验消息本应点名 {expected}，实际是「{exception.Message}」。");
    }

    /// <summary>
    /// 短密钥必须被拒绝，且消息要说清最短要多少、怎么生成
    /// </summary>
    [Theory]
    [InlineData("123456789012345")]
    [InlineData("short")]
    [InlineData("a")]
    public async Task 密钥短于十六字符时拒绝启动(string apiKey)
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await DocsMcpWebTestHost.StartAsync(
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", apiKey)));

        Assert.Contains("XiHan:Docs:Mcp:ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("16", exception.Message, StringComparison.Ordinal);

        // 消息附带密钥生成命令
        Assert.Contains("openssl rand -base64 32", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 密钥刚好十六字符时可以启动()
    {
        await using var host = await DocsMcpWebTestHost.StartAsync(
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", "0123456789abcdef"));

        Assert.NotNull(host.BaseAddress);
    }

    /// <summary>
    /// 未启用的部署即便其余配置全是非法值，也必须干干净净地起来
    /// </summary>
    [Fact]
    public async Task 未启用时即便配置非法也照常启动()
    {
        await using var host = await DocsMcpWebTestHost.StartAsync(
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "false"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", "x"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:HeaderName", "X Api Key"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Path", "mcp 端点"));

        Assert.NotNull(host.BaseAddress);
    }

    /// <summary>
    /// 什么都不配（默认值）时同样要能起来
    /// </summary>
    [Fact]
    public async Task 完全不配时照常启动()
    {
        await using var host = await DocsMcpWebTestHost.StartAsync();

        Assert.NotNull(host.BaseAddress);
    }

    /// <summary>
    /// 一次配错多项时，消息要把每一项都列出来，而不是只报第一条
    /// </summary>
    [Fact]
    public async Task 多项配错时逐条列出()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await DocsMcpWebTestHost.StartAsync(
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", "tooshort"),
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:HeaderName", "X Api Key"),
                new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Path", "mcp")));

        Assert.Contains("XiHan:Docs:Mcp:ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("XiHan:Docs:Mcp:HeaderName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("XiHan:Docs:Mcp:Path", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// token 允许的特殊字符不该被误判成非法
    /// </summary>
    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("X_Api_Key")]
    [InlineData("X.Api.Key")]
    [InlineData("Api~Key!")]
    public async Task 合法的请求头名照常启动(string headerName)
    {
        await using var host = await DocsMcpWebTestHost.StartAsync(
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:Enabled", "true"),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:ApiKey", ValidApiKey),
            new KeyValuePair<string, string?>("XiHan:Docs:Mcp:HeaderName", headerName));

        Assert.NotNull(host.BaseAddress);
    }
}
