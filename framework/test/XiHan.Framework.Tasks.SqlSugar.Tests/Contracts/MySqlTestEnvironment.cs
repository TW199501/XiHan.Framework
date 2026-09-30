// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Tasks.SqlSugar.Tests.Contracts;

/// <summary>
/// 真实 MySQL 测试环境
/// </summary>
internal static class MySqlTestEnvironment
{
    /// <summary>
    /// 未配置时的跳过原因
    /// </summary>
    public const string SkipReason = "未设置 XIHAN_TEST_MYSQL，跳过真实数据库契约测试。";

    /// <summary>
    /// 连接字符串，取环境变量 XIHAN_TEST_MYSQL
    /// </summary>
    public static string? ConnectionString { get; } = Environment.GetEnvironmentVariable("XIHAN_TEST_MYSQL");

    /// <summary>
    /// 是否已配置
    /// </summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
