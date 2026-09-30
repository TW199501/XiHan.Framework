// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas.Abstractions;

/// <summary>
/// 配额用量存储
/// </summary>
/// <remarks>
/// <para>
/// 预留、提交、释放构成一次用量的完整生命周期：预留先占额度，业务成功提交转为已用，业务失败释放归还额度。
/// 实现必须让同一周期内的并发预留彼此可见，不得先放行再回滚成超额。
/// 超额、冲突与终态不可回退都以类型化结果返回，不抛业务异常。
/// </para>
/// <para>
/// 预留标识是「租户 + 配额项 + 操作标识」，不含计量周期：结算一律落回预留当时所属的周期桶，
/// 周期滚动后的迟到结算不得改动新周期。实现必须为该标识保留一段去重窗口——
/// 窗口内同标识重试不重复扣额，超出窗口后同标识重用视为新预留；窗口不对外承诺永久幂等。
/// </para>
/// <para>
/// 实现不得将预留与结算写入参与调用方的工作单元事务：预留必须独立于业务事务先落账，
/// 否则业务回滚会把已放行的预留一并回滚，先占额度的语义失效。
/// 多实例部署必须共享同一存储实现，否则各实例各记各的账。
/// </para>
/// <para>
/// 业务已提交但结算未完成的窗口由调用方负责：调用方必须持久化预留标识并至少一次重投结算。
/// 返回 <see cref="QuotaSettlementStatus.Expired"/>、<see cref="QuotaSettlementStatus.NotFound"/>
/// 或 <see cref="QuotaSettlementStatus.TerminalConflict"/> 都表示这一笔用量未计入，需调用方补记或告警；
/// 存储不提供绕过预留的直扣入口。
/// </para>
/// </remarks>
public interface IQuotaStore
{
    /// <summary>
    /// 原子预留用量
    /// </summary>
    /// <param name="request">预留请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>预留结果；放行时 <see cref="QuotaReserveResult.Allowed"/> 为 true</returns>
    /// <exception cref="ArgumentNullException">请求为 null</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaReserveResult> ReserveAsync(QuotaReserveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交预留，把预留量转为已用
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果；预留已过期时拒绝且不重新扣额，去重记录已被回收时返回未找到且不回报用量</returns>
    /// <exception cref="ArgumentNullException">预留标识为 null</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaSettlementResult> CommitAsync(QuotaReservationKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放预留，把预留量归还配额
    /// </summary>
    /// <param name="key">预留标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    /// <exception cref="ArgumentNullException">预留标识为 null</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaSettlementResult> ReleaseAsync(QuotaReservationKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询某租户在指定政策当前周期内的用量
    /// </summary>
    /// <remarks>
    /// 查询同样会触发到期判定与去重记录回收：实现不保证后台清理，回收发生在被访问的桶上。
    /// </remarks>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="policy">配额政策，决定统计哪个周期</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用量快照；该周期尚无记录时返回零用量</returns>
    /// <exception cref="ArgumentNullException">政策为 null</exception>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">配额项标识为空白</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaUsage> FindUsageAsync(
        long tenantId,
        string quotaKey,
        QuotaPolicy policy,
        CancellationToken cancellationToken = default);
}
