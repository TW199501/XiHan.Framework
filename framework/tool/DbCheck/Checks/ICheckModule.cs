// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace DbCheck.Checks;

/// <summary>
/// 检查模块合约
/// </summary>
public interface ICheckModule
{
    /// <summary>
    /// 模块名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 对指定目标执行检查
    /// </summary>
    Task<CheckResult> CheckAsync(CheckTargetContext target, CancellationToken cancellationToken);
}

/// <summary>
/// 检查目标上下文
/// </summary>
/// <param name="Name">目标逻辑名称</param>
/// <param name="ConnectionString">解析后的连接字符串</param>
/// <param name="Clock">时间提供程序</param>
public sealed record CheckTargetContext(string Name, string? ConnectionString, TimeProvider Clock);
