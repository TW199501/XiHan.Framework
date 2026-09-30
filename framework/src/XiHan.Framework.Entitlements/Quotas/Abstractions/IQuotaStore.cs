// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Entitlements.Quotas.Abstractions;

/// <summary>
/// 配额用量存储
/// </summary>
/// <remarks>
/// 预留、提交、释放构成一次用量的完整生命周期：预留先占额度，业务成功提交转为已用，业务失败释放归还额度。
/// 实现必须让同一周期内的并发预留彼此可见，不得先放行再回滚成超额。
/// 超额、冲突与终态不可回退都以类型化结果返回，不抛业务异常。
/// </remarks>
public interface IQuotaStore
{
    /// <summary>
    /// 原子预留用量
    /// </summary>
    /// <param name="request">预留请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>预留结果；放行时 <see cref="QuotaReserveResult.Allowed"/> 为 true</returns>
    Task<QuotaReserveResult> ReserveAsync(QuotaReserveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交预留，把预留量转为已用
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果；预留已过期时拒绝且不重新扣额</returns>
    Task<QuotaSettlementResult> CommitAsync(QuotaReservationKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放预留，把预留量归还配额
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    Task<QuotaSettlementResult> ReleaseAsync(QuotaReservationKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询某租户在指定政策当前周期内的用量
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="policy">配额政策，决定统计哪个周期</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用量快照；该周期尚无记录时返回零用量</returns>
    Task<QuotaUsage> FindUsageAsync(
        long tenantId,
        string quotaKey,
        QuotaPolicy policy,
        CancellationToken cancellationToken = default);
}
