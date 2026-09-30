// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Quotas.SqlSugar.Entities;

/// <summary>
/// 配额桶实体
/// </summary>
/// <remarks>
/// 一行代表一个「租户 + 配额项 + 计量周期」的账本。
/// 刻意不实现 <c>IMultiTenantEntity</c>：配额按显式租户标识读写，平台还要替任意租户查用量，
/// 一旦实现该接口就会被按环境租户的全局过滤器静默改写查询条件，读不到就表现为额度永远为零。
/// 表只建在平台库。
/// </remarks>
[SugarTable("sys_quota_bucket")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("uk_sys_quota_bucket_scope",
    nameof(TenantId), OrderByType.Asc,
    nameof(QuotaKey), OrderByType.Asc,
    nameof(Period), OrderByType.Asc,
    nameof(PeriodStart), OrderByType.Asc,
    isUnique: true)]
public class SysQuotaBucket : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysQuotaBucket() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysQuotaBucket(long basicId) : base(basicId)
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
    /// 计量周期，与配额政策的周期一致；不周期为 0
    /// </summary>
    [SugarColumn(ColumnName = "Period", IsNullable = false, ColumnDescription = "计量周期")]
    public int Period { get; set; }

    /// <summary>
    /// 计量周期起始，UTC；不周期为 DateTimeOffset.MinValue 的 UTC 刻度
    /// </summary>
    [SugarColumn(ColumnName = "Period_Start", IsNullable = false, ColumnDescription = "计量周期起始，UTC")]
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// 建桶时依据的上限，仅供回报用量；判定一律用调用方传入的政策
    /// </summary>
    [SugarColumn(ColumnName = "Recorded_Limit", IsNullable = true, ColumnDescription = "建桶时依据的上限")]
    public long? RecordedLimit { get; set; }

    /// <summary>
    /// 本周期已提交用量
    /// </summary>
    [SugarColumn(ColumnName = "Committed", IsNullable = false, ColumnDescription = "本周期已提交用量")]
    public long Committed { get; set; }

    /// <summary>
    /// 本周期已预留未定稿用量
    /// </summary>
    [SugarColumn(ColumnName = "Reserved", IsNullable = false, ColumnDescription = "本周期已预留未定稿用量")]
    public long Reserved { get; set; }
}
