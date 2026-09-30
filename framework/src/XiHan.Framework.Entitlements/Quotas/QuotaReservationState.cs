// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Quotas;

/// <summary>
/// 配额预留状态
/// </summary>
public enum QuotaReservationState
{
    /// <summary>
    /// 已预留，占用配额但未定稿
    /// </summary>
    Reserved = 0,

    /// <summary>
    /// 已提交，用量永久计入所属周期，终态
    /// </summary>
    Committed = 1,

    /// <summary>
    /// 已释放，预留占用归还配额，终态
    /// </summary>
    Released = 2,

    /// <summary>
    /// 已过期，预留占用归还配额，终态
    /// </summary>
    Expired = 3
}
