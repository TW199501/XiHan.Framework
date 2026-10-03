// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace DbCheck.Checks;

/// <summary>
/// 单项检查结果
/// </summary>
/// <param name="Target">检查目标逻辑名称</param>
/// <param name="Module">检查模块名称</param>
/// <param name="Status">检查状态</param>
/// <param name="Evidence">判定依据描述</param>
/// <param name="Observed">观测值</param>
/// <param name="Threshold">判定阈值</param>
/// <param name="CheckedAt">检查时间</param>
public sealed record CheckResult(
    string Target,
    string Module,
    CheckStatus Status,
    string Evidence,
    double? Observed = null,
    double? Threshold = null,
    DateTimeOffset CheckedAt = default);
