// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;

namespace XiHan.Framework.Docs.Mcp.Web.Options;

/// <summary>
/// 文档 MCP Server HTTP 传输配置的启动期校验
/// </summary>
/// <remarks>
/// 仅在 <see cref="XiHanDocsMcpWebOptions.IsExposable"/> 为 true 时校验请求头名、端点路径与密钥长度；
/// 配合 <c>ValidateOnStart()</c> 使用，校验失败时进程在开始服务之前退出。
/// </remarks>
public sealed class XiHanDocsMcpWebOptionsValidator : IValidateOptions<XiHanDocsMcpWebOptions>
{
    /// <summary>
    /// 密钥的最短长度
    /// </summary>
    private const int MinimumApiKeyLength = 16;

    /// <summary>
    /// RFC 9110 token 允许的非字母数字字符
    /// </summary>
    private const string TokenSpecialCharacters = "!#$%&'*+-.^_`|~";

    /// <summary>
    /// 校验配置
    /// </summary>
    /// <param name="name">具名选项的名字，本类型只有默认实例</param>
    /// <param name="options">待校验的配置</param>
    /// <returns>校验结果，失败时逐条列出原因</returns>
    public ValidateOptionsResult Validate(string? name, XiHanDocsMcpWebOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 不暴露时跳过校验
        if (!options.IsExposable)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        ValidateHeaderName(options.HeaderName, failures);
        ValidatePath(options.Path, failures);
        ValidateApiKey(options.ApiKey!, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// 校验请求头名：非空白，且只由 RFC 9110 的 token 字符组成
    /// </summary>
    private static void ValidateHeaderName(string headerName, List<string> failures)
    {
        var setting = $"{XiHanDocsMcpWebOptions.SectionName}:HeaderName";

        if (string.IsNullOrWhiteSpace(headerName))
        {
            failures.Add($"{setting} 为空白。它是携带密钥的请求头名，必须是合法的 HTTP 字段名，例如 X-Api-Key。");
            return;
        }

        var invalid = headerName.Where(c => !IsTokenCharacter(c)).Distinct().ToList();
        if (invalid.Count > 0)
        {
            failures.Add(
                $"{setting} = 「{headerName}」含有 HTTP 字段名不允许的字符：{string.Join("、", invalid.Select(DescribeCharacter))}。" +
                $"合法字符为字母、数字与 {TokenSpecialCharacters}（RFC 9110 token），例如 X-Api-Key。");
        }
    }

    /// <summary>
    /// 校验端点路径：非空白、以斜杠开头、不含空白字符
    /// </summary>
    private static void ValidatePath(string path, List<string> failures)
    {
        var setting = $"{XiHanDocsMcpWebOptions.SectionName}:Path";

        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{setting} 为空白。它是端点路径，必须是以斜杠开头的绝对路径，例如 /mcp。");
            return;
        }

        if (!path.StartsWith('/'))
        {
            failures.Add($"{setting} = 「{path}」没有以斜杠开头。端点路径必须是绝对路径，例如 /mcp。");
        }

        if (path.Any(char.IsWhiteSpace))
        {
            failures.Add($"{setting} = 「{path}」含有空白字符。端点路径不允许出现空白，例如 /mcp 或 /docs-mcp。");
        }
    }

    /// <summary>
    /// 校验密钥长度
    /// </summary>
    private static void ValidateApiKey(string apiKey, List<string> failures)
    {
        var setting = $"{XiHanDocsMcpWebOptions.SectionName}:ApiKey";

        if (apiKey.Length < MinimumApiKeyLength)
        {
            failures.Add(
                $"{setting} 只有 {apiKey.Length} 个字符，至少需要 {MinimumApiKeyLength} 个——本服务没有限流，" +
                "短密钥可被在线爆破。可用 `openssl rand -base64 32` 或 " +
                "`[Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))` 生成一把，" +
                "再经 dotnet user-secrets 或环境变量 XiHan__Docs__Mcp__ApiKey 注入，切勿写进仓库。");
        }
    }

    /// <summary>
    /// 判断字符是否属于 RFC 9110 的 token 字符集
    /// </summary>
    private static bool IsTokenCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || TokenSpecialCharacters.Contains(value, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把非法字符描述成可读形式
    /// </summary>
    private static string DescribeCharacter(char value)
    {
        return char.IsWhiteSpace(value) || char.IsControl(value)
            ? $"U+{(int)value:X4}"
            : $"「{value}」";
    }
}
