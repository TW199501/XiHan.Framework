// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Quotas.SqlSugar.Tests;

/// <summary>
/// 租户探针实体
/// </summary>
/// <remarks>
/// 实现 <c>IMultiTenantEntity</c>，用于证明测试夹具确实装上了全局租户过滤器：
/// 它会被按环境租户静默过滤，而配额两张表不会。没有这个对照，
/// 「配额表不被环境租户过滤器吞掉」的断言可能只是在验证过滤器根本没生效。
/// </remarks>
[SugarTable("sys_tenant_probe")]
internal sealed class SysTenantProbe : SugarMultiTenantEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysTenantProbe() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysTenantProbe(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 探针名称
    /// </summary>
    [SugarColumn(ColumnName = "Probe_Name", Length = 64, IsNullable = false, ColumnDescription = "探针名称")]
    public string ProbeName { get; set; } = string.Empty;
}
