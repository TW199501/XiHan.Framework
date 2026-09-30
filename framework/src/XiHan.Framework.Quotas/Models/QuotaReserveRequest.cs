// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas;

/// <summary>
/// 配额预留请求
/// </summary>
public sealed class QuotaReserveRequest
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="operationId">操作标识</param>
    /// <param name="amount">预留量</param>
    /// <param name="policy">配额政策</param>
    /// <param name="reservationTtl">预留存活时长，null 用存储默认值</param>
    public QuotaReserveRequest(
        long tenantId,
        string quotaKey,
        string operationId,
        long amount,
        QuotaPolicy policy,
        TimeSpan? reservationTtl = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentNullException.ThrowIfNull(policy);
        if (reservationTtl is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reservationTtl), reservationTtl, "预留存活时长必须大于零。");
        }

        TenantId = tenantId;
        QuotaKey = quotaKey;
        OperationId = operationId;
        Amount = amount;
        Policy = policy;
        ReservationTtl = reservationTtl;
    }

    /// <summary>
    /// 租户标识，平台为 0
    /// </summary>
    public long TenantId { get; }

    /// <summary>
    /// 配额项标识
    /// </summary>
    public string QuotaKey { get; }

    /// <summary>
    /// 操作标识，同一操作重试必须复用
    /// </summary>
    public string OperationId { get; }

    /// <summary>
    /// 预留量，必须大于 0，语义由应用解释
    /// </summary>
    public long Amount { get; }

    /// <summary>
    /// 本次预留依据的配额政策
    /// </summary>
    public QuotaPolicy Policy { get; }

    /// <summary>
    /// 预留存活时长，null 表示用存储默认值
    /// </summary>
    public TimeSpan? ReservationTtl { get; }
}
