// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Quotas.Abstractions;
using XiHan.Framework.Quotas.Services;

namespace XiHan.Framework.Quotas.Tests;

/// <summary>
/// 配额服务编排测试
/// </summary>
public class QuotaServiceTests
{
    /// <summary>
    /// 缺少政策时显式拒绝，且不落到存储
    /// </summary>
    [Fact]
    public async Task 缺少政策时显式拒绝且不落到存储()
    {
        var store = new RecordingQuotaStore();
        var service = CreateService(1001, store, hasPolicy: false);

        var result = await service.ReserveAsync("export.rows", 1, "op-1");

        Assert.Equal(QuotaReserveStatus.UnknownPolicy, result.Status);
        Assert.False(result.Allowed);
        Assert.Null(result.Usage);
        Assert.Null(result.Reservation);
        Assert.Equal(0, store.ReserveCalls);
    }

    /// <summary>
    /// 政策存在时按当前租户转交存储
    /// </summary>
    [Fact]
    public async Task 政策存在时按当前租户转交存储()
    {
        var store = new RecordingQuotaStore();
        var service = CreateService(1001, store);

        var result = await service.ReserveAsync("export.rows", 3, "op-1");

        Assert.True(result.Allowed);
        Assert.Equal(1001, store.LastReserve!.TenantId);
        Assert.Equal("export.rows", store.LastReserve.QuotaKey);
        Assert.Equal("op-1", store.LastReserve.OperationId);
        Assert.Equal(3, store.LastReserve.Amount);
        Assert.Equal("v1", store.LastReserve.Policy.Version);
    }

    /// <summary>
    /// 无租户上下文时按平台零号租户预留
    /// </summary>
    [Fact]
    public async Task 无租户上下文时按平台零号租户预留()
    {
        var store = new RecordingQuotaStore();
        var service = CreateService(null, store);

        await service.ReserveAsync("export.rows", 1, "op-1");

        Assert.Equal(0, store.LastReserve!.TenantId);
    }

    /// <summary>
    /// 平台零号租户与无租户上下文同口径
    /// </summary>
    [Fact]
    public async Task 平台零号租户与无租户上下文同口径()
    {
        var store = new RecordingQuotaStore();

        await CreateService(null, store).ReserveAsync("export.rows", 1, "op-1");
        var implicitPlatform = store.LastReserve!.TenantId;
        await CreateService(0, store).ReserveAsync("export.rows", 1, "op-2");
        var explicitPlatform = store.LastReserve!.TenantId;

        Assert.Equal(implicitPlatform, explicitPlatform);
    }

    /// <summary>
    /// 提交与释放按当前租户组装预留标识
    /// </summary>
    [Fact]
    public async Task 提交与释放按当前租户组装预留标识()
    {
        var store = new RecordingQuotaStore();
        var service = CreateService(2002, store);

        await service.CommitAsync("export.rows", "op-9");
        await service.ReleaseAsync("export.rows", "op-9");

        Assert.Equal((2002, "export.rows", "op-9"), store.LastCommitKey);
        Assert.Equal((2002, "export.rows", "op-9"), store.LastReleaseKey);
    }

    /// <summary>
    /// 缺政策时用量查询不回报快照而不是零用量
    /// </summary>
    [Fact]
    public async Task 缺政策时用量查询返回空()
    {
        var store = new RecordingQuotaStore();

        var usage = await CreateService(1001, store, hasPolicy: false).FindUsageAsync("export.rows");

        Assert.Null(usage);
        Assert.Equal(0, store.UsageCalls);
    }

    /// <summary>
    /// 政策存在时用量查询转交存储
    /// </summary>
    [Fact]
    public async Task 政策存在时用量查询转交存储()
    {
        var store = new RecordingQuotaStore();

        var usage = await CreateService(1001, store).FindUsageAsync("export.rows");

        Assert.Equal(7, usage?.Committed);
        Assert.Equal(1001, store.LastUsageTenantId);
    }

    /// <summary>
    /// 配额项与操作标识空白快速失败且不查政策
    /// </summary>
    /// <remarks>
    /// 入参非法必须先于政策查询失败，否则应用侧能用空白键打到政策来源。
    /// </remarks>
    /// <param name="quotaKey">配额项标识</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 配额项标识空白快速失败且不查政策(string quotaKey)
    {
        var store = new RecordingQuotaStore();
        var provider = new CountingQuotaPolicyProvider(QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1"));
        var service = new QuotaService(new StubCurrentTenant(1001), provider, store);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReserveAsync(quotaKey, 1, "op-1"));

        Assert.Equal(0, provider.Calls);
    }

    /// <summary>
    /// 预留量非正数快速失败且不查政策
    /// </summary>
    /// <param name="amount">预留量</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 预留量非正数快速失败且不查政策(long amount)
    {
        var store = new RecordingQuotaStore();
        var provider = new CountingQuotaPolicyProvider(QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1"));
        var service = new QuotaService(new StubCurrentTenant(1001), provider, store);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ReserveAsync("export.rows", amount, "op-1"));

        Assert.Equal(0, provider.Calls);
    }

    /// <summary>
    /// 创建被测配额服务
    /// </summary>
    /// <param name="tenantId">当前租户标识，null 表示平台</param>
    /// <param name="store">记录型存储</param>
    /// <param name="hasPolicy">该租户是否有政策</param>
    /// <returns>配额服务</returns>
    private static QuotaService CreateService(
        long? tenantId, RecordingQuotaStore store, bool hasPolicy = true)
    {
        var policy = hasPolicy ? QuotaPolicy.Limited(10, QuotaPeriod.Day, "v1") : null;

        return new QuotaService(new StubCurrentTenant(tenantId), new StubPolicy(policy), store);
    }

    /// <summary>
    /// 返回固定政策的政策提供器替身
    /// </summary>
    /// <param name="policy">政策，null 表示无政策</param>
    private sealed class StubPolicy(QuotaPolicy? policy) : IQuotaPolicyProvider
    {
        /// <summary>
        /// 查找政策
        /// </summary>
        /// <param name="tenantId">租户标识</param>
        /// <param name="quotaKey">配额项标识</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>政策</returns>
        public Task<QuotaPolicy?> FindPolicyAsync(
            long tenantId, string quotaKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(policy);
        }
    }

    /// <summary>
    /// 统计调用次数的政策提供器替身
    /// </summary>
    /// <param name="policy">政策</param>
    private sealed class CountingQuotaPolicyProvider(QuotaPolicy? policy) : IQuotaPolicyProvider
    {
        /// <summary>
        /// 被调用次数
        /// </summary>
        public int Calls { get; private set; }

        /// <summary>
        /// 查找政策
        /// </summary>
        /// <param name="tenantId">租户标识</param>
        /// <param name="quotaKey">配额项标识</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>政策</returns>
        public Task<QuotaPolicy?> FindPolicyAsync(
            long tenantId, string quotaKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(policy);
        }
    }

    /// <summary>
    /// 只记录入参的配额存储替身
    /// </summary>
    private sealed class RecordingQuotaStore : IQuotaStore
    {
        /// <summary>
        /// 预留调用次数
        /// </summary>
        public int ReserveCalls { get; private set; }

        /// <summary>
        /// 用量查询调用次数
        /// </summary>
        public int UsageCalls { get; private set; }

        /// <summary>
        /// 最后一次预留请求
        /// </summary>
        public QuotaReserveRequest? LastReserve { get; private set; }

        /// <summary>
        /// 最后一次提交用的预留标识
        /// </summary>
        public (long TenantId, string QuotaKey, string OperationId)? LastCommitKey { get; private set; }

        /// <summary>
        /// 最后一次释放用的预留标识
        /// </summary>
        public (long TenantId, string QuotaKey, string OperationId)? LastReleaseKey { get; private set; }

        /// <summary>
        /// 最后一次用量查询的租户标识
        /// </summary>
        public long LastUsageTenantId { get; private set; }

        /// <summary>
        /// 预留用量
        /// </summary>
        /// <param name="request">预留请求</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>预留结果</returns>
        public Task<QuotaReserveResult> ReserveAsync(
            QuotaReserveRequest request, CancellationToken cancellationToken = default)
        {
            ReserveCalls++;
            LastReserve = request;

            return Task.FromResult(new QuotaReserveResult(
                QuotaReserveStatus.Reserved,
                new QuotaReservation(
                    request.TenantId, request.QuotaKey, DateTimeOffset.MinValue, request.OperationId,
                    request.Amount, request.Policy.Version, DateTimeOffset.MaxValue,
                    QuotaReservationState.Reserved),
                new QuotaUsage(request.Policy.Limit, 0, request.Amount)));
        }

        /// <summary>
        /// 提交预留
        /// </summary>
        /// <param name="key">预留标识</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>结算结果</returns>
        public Task<QuotaSettlementResult> CommitAsync(
            QuotaReservationKey key, CancellationToken cancellationToken = default)
        {
            LastCommitKey = (key.TenantId, key.QuotaKey, key.OperationId);

            return Task.FromResult(new QuotaSettlementResult(
                QuotaSettlementStatus.Committed, null, new QuotaUsage(10, 1, 0)));
        }

        /// <summary>
        /// 释放预留
        /// </summary>
        /// <param name="key">预留标识</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>结算结果</returns>
        public Task<QuotaSettlementResult> ReleaseAsync(
            QuotaReservationKey key, CancellationToken cancellationToken = default)
        {
            LastReleaseKey = (key.TenantId, key.QuotaKey, key.OperationId);

            return Task.FromResult(new QuotaSettlementResult(
                QuotaSettlementStatus.Released, null, new QuotaUsage(10, 0, 0)));
        }

        /// <summary>
        /// 查询用量
        /// </summary>
        /// <param name="tenantId">租户标识</param>
        /// <param name="quotaKey">配额项标识</param>
        /// <param name="policy">政策</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>用量快照</returns>
        public Task<QuotaUsage> FindUsageAsync(
            long tenantId, string quotaKey, QuotaPolicy policy, CancellationToken cancellationToken = default)
        {
            UsageCalls++;
            LastUsageTenantId = tenantId;

            return Task.FromResult(new QuotaUsage(policy.Limit, 7, 0));
        }
    }

    /// <summary>
    /// 固定租户上下文的当前租户替身
    /// </summary>
    /// <param name="tenantId">租户标识，null 表示平台</param>
    private sealed class StubCurrentTenant(long? tenantId) : ICurrentTenant
    {
        /// <summary>
        /// 当前租户标识
        /// </summary>
        public long? Id { get; private set; } = tenantId;

        /// <summary>
        /// 当前租户名称
        /// </summary>
        public string? Name { get; private set; }

        /// <summary>
        /// 当前租户是否可用
        /// </summary>
        public bool IsAvailable => Id.HasValue;

        /// <summary>
        /// 临时切换租户
        /// </summary>
        /// <param name="id">租户标识</param>
        /// <param name="name">租户名称</param>
        /// <returns>还原用的释放器</returns>
        public IDisposable Change(long? id, string? name = null)
        {
            var previousId = Id;
            var previousName = Name;
            Id = id;
            Name = name;

            return new Restore(this, previousId, previousName);
        }

        private sealed class Restore(StubCurrentTenant owner, long? previousId, string? previousName) : IDisposable
        {
            public void Dispose()
            {
                owner.Id = previousId;
                owner.Name = previousName;
            }
        }
    }
}
