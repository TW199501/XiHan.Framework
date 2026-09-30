// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas;

/// <summary>
/// 配额用量快照
/// </summary>
/// <remarks>
/// 只描述所属计量周期内的口径，跨周期数据不在其中。
/// </remarks>
public sealed class QuotaUsage
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="limit">上限</param>
    /// <param name="committed">已提交用量</param>
    /// <param name="reserved">已预留未定稿用量</param>
    public QuotaUsage(long? limit, long committed, long reserved)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(committed);
        ArgumentOutOfRangeException.ThrowIfNegative(reserved);

        Limit = limit;
        Committed = committed;
        Reserved = reserved;
    }

    /// <summary>
    /// 本周期上限，显式无限额时为 null
    /// </summary>
    public long? Limit { get; }

    /// <summary>
    /// 本周期已提交用量
    /// </summary>
    public long Committed { get; }

    /// <summary>
    /// 本周期已预留未定稿用量
    /// </summary>
    public long Reserved { get; }

    /// <summary>
    /// 本周期剩余可用量，显式无限额时为 null
    /// </summary>
    public long? Remaining => Limit is null ? null : Math.Max(0, Limit.Value - Committed - Reserved);
}

/// <summary>
/// 配额预留标识
/// </summary>
/// <remarks>
/// 由租户、配额项与操作标识组成，不含周期：周期在预留时确定并随预留一并保存，
/// 因此提交或释放能命中原来那一周期，不会被周期滚动后的新桶误导。
/// </remarks>
public sealed class QuotaReservationKey
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="operationId">操作标识</param>
    public QuotaReservationKey(long tenantId, string quotaKey, string operationId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        TenantId = tenantId;
        QuotaKey = quotaKey;
        OperationId = operationId;
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
    /// 操作标识
    /// </summary>
    public string OperationId { get; }
}
