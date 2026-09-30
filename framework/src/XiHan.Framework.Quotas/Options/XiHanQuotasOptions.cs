// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas.Options;

/// <summary>
/// 曦寒配额选项
/// </summary>
public class XiHanQuotasOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Quotas";

    /// <summary>
    /// 预留默认存活时长
    /// </summary>
    /// <remarks>
    /// 预留超过此时长仍未提交即视为过期，占用的额度归还。必须大于零。
    /// 业务时长可能超过该值的调用方应显式传入更长的预留存活时长。
    /// </remarks>
    public TimeSpan DefaultReservationTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 进程内默认存储可跟踪的预留条目上限
    /// </summary>
    /// <remarks>
    /// 达到上限时先回收已过期的预留与超出保留期的去重记录；仍放不下则按容量不足拒绝新的预留，
    /// 不驱逐仍在占用额度的活动预留。必须大于零。
    /// </remarks>
    public int MaxTrackedReservations { get; set; } = 100000;

    /// <summary>
    /// 进程内默认存储可跟踪的配额桶上限
    /// </summary>
    /// <remarks>
    /// 一个配额桶对应一个「租户 + 配额项 + 计量周期」。达到上限时先驱逐周期已结束且记录全部过保留期的桶，
    /// 以及既无记录也无用量的空桶；无可驱逐桶时按容量不足拒绝新周期的预留。仍在计费的桶不被驱逐。必须大于零。
    /// </remarks>
    public int MaxTrackedBuckets { get; set; } = 20000;

    /// <summary>
    /// 不周期政策的去重记录保留时长
    /// </summary>
    /// <remarks>
    /// 带周期的政策按「一个完整周期」保留去重记录；不周期没有周期可依据，用本时长兜底。必须大于零。
    /// </remarks>
    public TimeSpan NonPeriodicTombstoneRetention { get; set; } = TimeSpan.FromHours(24);
}
