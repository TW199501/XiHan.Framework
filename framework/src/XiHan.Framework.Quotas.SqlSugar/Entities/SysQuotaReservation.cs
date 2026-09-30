// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Quotas.SqlSugar.Entities;

/// <summary>
/// 配额预留实体
/// </summary>
/// <remarks>
/// 一行代表一次预留，唯一索引 (Tenant_Id, Quota_Key, Operation_Id) 就是去重闸门：
/// 同一操作标识的并发重试只有一行能插进去，因此不会重复扣额。
/// 所属周期随记录保存，提交与释放只改动它原来那一桶。
/// 与桶一样刻意不实现 <c>IMultiTenantEntity</c>，表只建在平台库。
/// </remarks>
[SugarTable("sys_quota_reservation")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("uk_sys_quota_reservation_key",
    nameof(TenantId), OrderByType.Asc,
    nameof(QuotaKey), OrderByType.Asc,
    nameof(OperationId), OrderByType.Asc,
    isUnique: true)]
[SugarIndex("idx_sys_quota_reservation_bucket_state",
    nameof(BucketId), OrderByType.Asc,
    nameof(State), OrderByType.Asc)]
public class SysQuotaReservation : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysQuotaReservation() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysQuotaReservation(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 租户标识，平台为 0
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = false, ColumnDescription = "租户标识，平台为 0")]
    public long TenantId { get; set; }

    /// <summary>
    /// 配额项标识
    /// </summary>
    [SugarColumn(ColumnName = "Quota_Key", Length = 255, IsNullable = false, ColumnDescription = "配额项标识")]
    public string QuotaKey { get; set; } = string.Empty;

    /// <summary>
    /// 操作标识，与租户、配额项共同构成预留标识
    /// </summary>
    [SugarColumn(ColumnName = "Operation_Id", Length = 128, IsNullable = false, ColumnDescription = "操作标识")]
    public string OperationId { get; set; } = string.Empty;

    /// <summary>
    /// 所属配额桶标识，无限额记录不记账时为 0
    /// </summary>
    [SugarColumn(ColumnName = "Bucket_Id", IsNullable = false, ColumnDescription = "所属配额桶标识，不记账为 0")]
    public long BucketId { get; set; }

    /// <summary>
    /// 所属计量周期起始，UTC
    /// </summary>
    [SugarColumn(ColumnName = "Period_Start", IsNullable = false, ColumnDescription = "所属计量周期起始，UTC")]
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// 预留量
    /// </summary>
    [SugarColumn(ColumnName = "Amount", IsNullable = false, ColumnDescription = "预留量")]
    public long Amount { get; set; }

    /// <summary>
    /// 预留时的政策版本
    /// </summary>
    [SugarColumn(ColumnName = "Policy_Version", Length = 64, IsNullable = false, ColumnDescription = "政策版本")]
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>
    /// 预留失效时刻，UTC
    /// </summary>
    [SugarColumn(ColumnName = "Expires_At", IsNullable = false, ColumnDescription = "预留失效时刻，UTC")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 预留状态，与 QuotaReservationState 的整数值一致
    /// </summary>
    [SugarColumn(ColumnName = "State", IsNullable = false, ColumnDescription = "预留状态")]
    public int State { get; set; }

    /// <summary>
    /// 进入终态的时刻，UTC；仍在预留中时为 null
    /// </summary>
    [SugarColumn(ColumnName = "Settled_At", IsNullable = true, ColumnDescription = "进入终态的时刻，UTC")]
    public DateTime? SettledAt { get; set; }
}
