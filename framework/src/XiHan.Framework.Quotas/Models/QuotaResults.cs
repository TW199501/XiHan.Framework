// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas;

/// <summary>
/// 配额预留结果状态
/// </summary>
public enum QuotaReserveStatus
{
    /// <summary>
    /// 新预留成功，占用配额
    /// </summary>
    Reserved = 0,

    /// <summary>
    /// 同一预留标识与同一预留量的重试，命中已有预留，不重复扣额
    /// </summary>
    Replayed = 1,

    /// <summary>
    /// 超出本周期上限，未占用配额
    /// </summary>
    Exceeded = 2,

    /// <summary>
    /// 同一预留标识已被占用且不可重开：预留量或政策版本与本请求不同，或该标识已有终态与墓碑记录。
    /// 未占用配额，既有记录回传在 <see cref="QuotaReserveResult.Reservation"/> 供调用方区分原因
    /// </summary>
    Conflict = 3,

    /// <summary>
    /// 存储已达容量上限，未占用配额
    /// </summary>
    CapacityExhausted = 4,

    /// <summary>
    /// 显式无限额政策，不记账直接放行
    /// </summary>
    Unlimited = 5
}

/// <summary>
/// 配额预留结果
/// </summary>
public sealed class QuotaReserveResult
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="status">结果状态</param>
    /// <param name="reservation">预留记录</param>
    /// <param name="usage">用量快照</param>
    public QuotaReserveResult(QuotaReserveStatus status, QuotaReservation? reservation, QuotaUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        Status = status;
        Reservation = reservation;
        Usage = usage;
    }

    /// <summary>
    /// 结果状态
    /// </summary>
    public QuotaReserveStatus Status { get; }

    /// <summary>
    /// 预留记录：放行时为本次或既有的记录，重放与冲突回传既有记录以便区分原因，超额与容量不足时为 null
    /// </summary>
    public QuotaReservation? Reservation { get; }

    /// <summary>
    /// 本次判定后的所属周期用量快照
    /// </summary>
    public QuotaUsage Usage { get; }

    /// <summary>
    /// 是否放行
    /// </summary>
    public bool Allowed => Status is QuotaReserveStatus.Reserved
        or QuotaReserveStatus.Replayed
        or QuotaReserveStatus.Unlimited;
}

/// <summary>
/// 配额结算状态，即提交与释放的结果
/// </summary>
public enum QuotaSettlementStatus
{
    /// <summary>
    /// 本次提交成功，预留量转为已用
    /// </summary>
    Committed = 0,

    /// <summary>
    /// 本次释放成功，预留量归还配额
    /// </summary>
    Released = 1,

    /// <summary>
    /// 该预留已是已提交终态，重复提交不再改动用量
    /// </summary>
    AlreadyCommitted = 2,

    /// <summary>
    /// 该预留已是已释放终态，重复释放不再改动用量
    /// </summary>
    AlreadyReleased = 3,

    /// <summary>
    /// 找不到该预留标识，含去重记录已过保留期被回收
    /// </summary>
    NotFound = 4,

    /// <summary>
    /// 预留已过期，占用已归还；提交被拒绝且不会重新扣额
    /// </summary>
    Expired = 5,

    /// <summary>
    /// 该预留已处于相反的终态，终态不可回退
    /// </summary>
    TerminalConflict = 6
}

/// <summary>
/// 配额结算结果
/// </summary>
public sealed class QuotaSettlementResult
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="status">结算状态</param>
    /// <param name="reservation">预留记录</param>
    /// <param name="usage">用量快照</param>
    public QuotaSettlementResult(QuotaSettlementStatus status, QuotaReservation? reservation, QuotaUsage? usage)
    {
        Status = status;
        Reservation = reservation;
        Usage = usage;
    }

    /// <summary>
    /// 结算状态
    /// </summary>
    public QuotaSettlementStatus Status { get; }

    /// <summary>
    /// 预留记录，未找到该预留时为 null
    /// </summary>
    public QuotaReservation? Reservation { get; }

    /// <summary>
    /// 本次结算后的所属周期用量快照；未找到该预留时为 null——此时无从确定它属于哪个周期，
    /// 合成零用量会与「显式无限额」同形而被误读
    /// </summary>
    public QuotaUsage? Usage { get; }

    /// <summary>
    /// 是否已定稿（已提交或已释放，含重复调用的幂等命中）
    /// </summary>
    public bool Settled => Status is QuotaSettlementStatus.Committed
        or QuotaSettlementStatus.Released
        or QuotaSettlementStatus.AlreadyCommitted
        or QuotaSettlementStatus.AlreadyReleased;
}
