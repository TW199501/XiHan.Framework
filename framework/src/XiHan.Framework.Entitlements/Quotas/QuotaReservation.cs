// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Quotas;

/// <summary>
/// 一次配额预留
/// </summary>
/// <remarks>
/// 不可变；状态流转由存储负责，读出的实例代表该时刻的状态快照。
/// </remarks>
public sealed class QuotaReservation
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="periodStart">所属计量周期起始时刻</param>
    /// <param name="operationId">操作标识</param>
    /// <param name="amount">预留量</param>
    /// <param name="policyVersion">政策版本</param>
    /// <param name="expiresAt">失效时刻</param>
    /// <param name="state">预留状态</param>
    public QuotaReservation(
        long tenantId,
        string quotaKey,
        DateTimeOffset periodStart,
        string operationId,
        long amount,
        string policyVersion,
        DateTimeOffset expiresAt,
        QuotaReservationState state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "预留状态不是已定义的值。");
        }

        TenantId = tenantId;
        QuotaKey = quotaKey;
        PeriodStart = periodStart;
        OperationId = operationId;
        Amount = amount;
        PolicyVersion = policyVersion;
        ExpiresAt = expiresAt;
        State = state;
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
    /// 所属计量周期的起始 UTC 时刻，不周期政策为 <see cref="DateTimeOffset.MinValue"/>
    /// </summary>
    public DateTimeOffset PeriodStart { get; }

    /// <summary>
    /// 操作标识，与租户、配额项、周期共同构成预留标识
    /// </summary>
    public string OperationId { get; }

    /// <summary>
    /// 预留量，恒为正数
    /// </summary>
    public long Amount { get; }

    /// <summary>
    /// 预留时的政策版本
    /// </summary>
    public string PolicyVersion { get; }

    /// <summary>
    /// 预留失效的 UTC 时刻
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// 预留状态
    /// </summary>
    public QuotaReservationState State { get; }

    /// <summary>
    /// 以新状态复制本预留
    /// </summary>
    /// <param name="state">新状态</param>
    /// <returns>新状态的预留副本</returns>
    internal QuotaReservation WithState(QuotaReservationState state)
    {
        return new QuotaReservation(
            TenantId, QuotaKey, PeriodStart, OperationId, Amount, PolicyVersion, ExpiresAt, state);
    }
}
