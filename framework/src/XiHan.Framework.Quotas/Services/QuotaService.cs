// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Quotas.Abstractions;

namespace XiHan.Framework.Quotas.Services;

/// <summary>
/// 面向当前租户的配额服务默认实现
/// </summary>
/// <remarks>
/// 只做「取当前租户、取政策、缺政策即拒绝、转交存储」的编排，不自己记账也不缓存政策；
/// 政策的来源与存储的实现都由应用替换。
/// </remarks>
public class QuotaService : IQuotaService
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IQuotaPolicyProvider _policyProvider;
    private readonly IQuotaStore _store;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="policyProvider">配额政策提供器</param>
    /// <param name="store">配额存储</param>
    public QuotaService(
        ICurrentTenant currentTenant,
        IQuotaPolicyProvider policyProvider,
        IQuotaStore store)
    {
        ArgumentNullException.ThrowIfNull(currentTenant);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(store);

        _currentTenant = currentTenant;
        _policyProvider = policyProvider;
        _store = store;
    }

    /// <summary>
    /// 为当前租户预留用量
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="amount">预留量，必须大于 0</param>
    /// <param name="operationId">操作标识，不可为空白</param>
    /// <param name="reservationTtl">预留存活时长，为 null 时用存储默认值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>预留结果</returns>
    public async Task<QuotaReserveResult> ReserveAsync(
        string quotaKey,
        long amount,
        string operationId,
        TimeSpan? reservationTtl = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        var policy = await _policyProvider.FindPolicyAsync(tenantId, quotaKey, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return new QuotaReserveResult(QuotaReserveStatus.UnknownPolicy, null, null);
        }

        return await _store.ReserveAsync(
            new QuotaReserveRequest(tenantId, quotaKey, operationId, amount, policy, reservationTtl),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 提交当前租户的一次预留
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="operationId">操作标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    public Task<QuotaSettlementResult> CommitAsync(
        string quotaKey,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return SettleAsync(quotaKey, operationId, static (store, key, token) =>
            store.CommitAsync(key, token), cancellationToken);
    }

    /// <summary>
    /// 释放当前租户的一次预留
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="operationId">操作标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    public Task<QuotaSettlementResult> ReleaseAsync(
        string quotaKey,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return SettleAsync(quotaKey, operationId, static (store, key, token) =>
            store.ReleaseAsync(key, token), cancellationToken);
    }

    /// <summary>
    /// 查询当前租户在指定配额项当前周期内的用量
    /// </summary>
    /// <param name="quotaKey">配额项标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用量快照；当前租户没有该配额项的政策时为 null</returns>
    public async Task<QuotaUsage?> FindUsageAsync(
        string quotaKey,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        ArgumentException.ThrowIfNullOrWhiteSpace(quotaKey);

        var policy = await _policyProvider.FindPolicyAsync(tenantId, quotaKey, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        return await _store.FindUsageAsync(tenantId, quotaKey, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 提交与释放的公共编排
    /// </summary>
    /// <param name="quotaKey">配额项标识</param>
    /// <param name="operationId">操作标识</param>
    /// <param name="settle">转交给存储的结算动作</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>结算结果</returns>
    private Task<QuotaSettlementResult> SettleAsync(
        string quotaKey,
        string operationId,
        Func<IQuotaStore, QuotaReservationKey, CancellationToken, Task<QuotaSettlementResult>> settle,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var key = new QuotaReservationKey(tenantId, quotaKey, operationId);

        return settle(_store, key, cancellationToken);
    }

    /// <summary>
    /// 取当前租户标识，无租户上下文按平台处理
    /// </summary>
    /// <returns>租户标识，平台为 0</returns>
    private long RequireTenant()
    {
        return _currentTenant.Id ?? 0;
    }
}
