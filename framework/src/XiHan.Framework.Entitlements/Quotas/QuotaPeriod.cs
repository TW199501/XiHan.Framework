// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Quotas;

/// <summary>
/// 配额计量周期
/// </summary>
/// <remarks>
/// 除 <see cref="None"/> 外，周期边界一律按 UTC 对齐：日取当日 00:00、周取 ISO 周星期一 00:00、月取当月 1 日 00:00。
/// </remarks>
public enum QuotaPeriod
{
    /// <summary>
    /// 不周期，用量累计不清零
    /// </summary>
    None = 0,

    /// <summary>
    /// 按 UTC 自然日
    /// </summary>
    Day = 1,

    /// <summary>
    /// 按 UTC ISO 自然周，起始为星期一
    /// </summary>
    Week = 2,

    /// <summary>
    /// 按 UTC 自然月
    /// </summary>
    Month = 3
}
