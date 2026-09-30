// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Quotas.Abstractions;

/// <summary>
/// 面向当前租户的配额服务
/// </summary>
/// <remarks>
/// <para>
/// 把「按当前租户取政策、缺政策即拒绝、再交给 <see cref="IQuotaStore"/> 记账」这条顺序固化在一处，
/// 避免每个调用点各自决定 null 政策怎么解释。租户取自
/// <c>XiHan.Framework.MultiTenancy.Abstractions</c> 的 <c>ICurrentTenant</c>，无租户上下文即平台（0 号租户）。
/// </para>
/// <para>
/// 本服务不判定功能开关，也不判定使用者权限：租户能否使用某功能由
/// <c>XiHan.Framework.MultiTenancy</c> 的 <c>ITenantFeatureChecker</c> 回答，
/// 使用者能否执行某操作由 <c>XiHan.Framework.Authorization</c> 回答，三者必须各自检查。
/// </para>
/// <para>
/// 注册为 Scoped：默认存储虽是单例，但持久化实现需要按请求解析数据库连接。
/// </para>
/// </remarks>
public interface IQuotaService
{
    /// <summary>
    /// 为当前租户预留用量
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="amount">预留量，必须大于 0，语义由应用解释</param>
    /// <param name="operationId">操作标识，不可为空白；同一操作重试必须复用</param>
    /// <param name="reservationTtl">预留存活时长，为 null 时用存储默认值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>预留结果；当前租户没有该配额项的政策时返回 <see cref="QuotaReserveStatus.UnknownPolicy"/></returns>
    /// <exception cref="ArgumentException">配额项或操作标识为空白</exception>
    /// <exception cref="ArgumentOutOfRangeException">预留量不大于 0</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaReserveResult> ReserveAsync(
        string quotaKey,
        long amount,
        string operationId,
        TimeSpan? reservationTtl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交当前租户的一次预留
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="operationId">操作标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    /// <exception cref="ArgumentException">配额项或操作标识为空白</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaSettlementResult> CommitAsync(
        string quotaKey,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放当前租户的一次预留
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="operationId">操作标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    /// <exception cref="ArgumentException">配额项或操作标识为空白</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaSettlementResult> ReleaseAsync(
        string quotaKey,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询当前租户在指定配额项当前周期内的用量
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用量快照；当前租户没有该配额项的政策时为 null</returns>
    /// <exception cref="ArgumentException">配额项标识为空白</exception>
    /// <exception cref="OperationCanceledException">取消令牌已触发</exception>
    Task<QuotaUsage?> FindUsageAsync(
        string quotaKey,
        CancellationToken cancellationToken = default);
}
