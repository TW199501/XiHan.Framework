// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.Data.SqlSugar.Routing;
using XiHan.Framework.Domain.Entities.Abstracts;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Quotas.Options;
using XiHan.Framework.Quotas.SqlSugar.Entities;
using XiHan.Framework.Quotas.SqlSugar.Options;
using XiHan.Framework.Quotas.SqlSugar.Stores;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Abstracts;

namespace XiHan.Framework.Quotas.SqlSugar.Tests;

/// <summary>
/// 配额存储测试夹具：一个临时 SQLite 库、真实客户端解析器与真实工作单元管理器
/// </summary>
/// <remarks>
/// 用真实工作单元而不是替身，是因为被测存储依赖两件事：额度不足时靠事务回滚抹掉已插入的预留行，
/// 以及预留写入独立于调用方事务。替身工作单元会让这两点看起来通过而实际没被验证。
/// 夹具另按数据层的同一种形式装上全局租户过滤器，并用一张实现 <c>IMultiTenantEntity</c> 的探针表
/// 反证过滤器确实在工作——没有这个对照，「配额表不被环境租户过滤器吞掉」可能只是在验证过滤器没装上。
/// </remarks>
internal sealed class QuotaTestDatabase : IDisposable
{
    /// <summary>
    /// 连接配置标识
    /// </summary>
    public const string ConfigId = "Default";

    private readonly ServiceProvider _services;
    private readonly string _sqliteFile;

    private QuotaTestDatabase()
    {
        _sqliteFile = Path.Combine(Path.GetTempPath(), $"xihan_quotas_{Guid.NewGuid():N}.db");

        CurrentTenant = new MutableCurrentTenant();
        var accessor = new TenantAccessor(CurrentTenant);

        Scope = new SqlSugarScope([
            new ConnectionConfig
            {
                ConfigId = ConfigId,
                ConnectionString = ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            }
        ]);

        var provider = Scope.GetConnectionScope(ConfigId);
        provider.CodeFirst.InitTables([
            typeof(SysQuotaBucket),
            typeof(SysQuotaReservation),
            typeof(SysTenantProbe)
        ]);

        // 与 XiHanDataServiceCollectionExtensions.ApplySugarGlobalFilters 同形式：表达式内只出现 long 标量
        provider.QueryFilter.AddTableFilter<IMultiTenantEntity>(
            entity => entity.TenantId == 0 || entity.TenantId == ResolveTenantScopeId(accessor));

        _services = BuildServices();
        UnitOfWorkManager = _services.GetRequiredService<IUnitOfWorkManager>();

        Resolver = new SqlSugarClientResolver(
            Scope,
            new FixedTenantConnectionResolver(ConfigId),
            new EntityModuleDataSourceResolver(),
            new StubModuleDataSourceConnectionResolver(),
            UnitOfWorkManager,
            CurrentTenant,
            new PassThroughConnectionConfigurator(),
            []);

        Clock = new TestClock();

        Store = new SqlSugarQuotaStore(
            Resolver,
            UnitOfWorkManager,
            IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload(),
            Clock,
            Microsoft.Extensions.Options.Options.Create(new XiHanQuotasOptions()),
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarCoreOptions()),
            Microsoft.Extensions.Options.Options.Create(new XiHanQuotasSqlSugarOptions()));
    }

    /// <summary>
    /// 被测存储
    /// </summary>
    public SqlSugarQuotaStore Store { get; }

    /// <summary>
    /// 测试时钟
    /// </summary>
    public TestClock Clock { get; }

    /// <summary>
    /// 当前租户替身
    /// </summary>
    public MutableCurrentTenant CurrentTenant { get; }

    /// <summary>
    /// 多连接容器
    /// </summary>
    public SqlSugarScope Scope { get; }

    /// <summary>
    /// 工作单元管理器
    /// </summary>
    public IUnitOfWorkManager UnitOfWorkManager { get; }

    /// <summary>
    /// 客户端解析器
    /// </summary>
    public ISqlSugarClientResolver Resolver { get; }

    /// <summary>
    /// 数据库连接串
    /// </summary>
    public string ConnectionString => $"DataSource={_sqliteFile};Pooling=False";

    /// <summary>
    /// 创建夹具
    /// </summary>
    /// <returns>测试夹具</returns>
    public static QuotaTestDatabase Create()
    {
        return new QuotaTestDatabase();
    }

    /// <summary>
    /// 用不受过滤器影响的独立连接读取原始行数
    /// </summary>
    /// <typeparam name="TEntity">实体类型</typeparam>
    /// <returns>行数</returns>
    public async Task<int> CountRawAsync<TEntity>()
        where TEntity : class, new()
    {
        using var probe = CreateProbeClient();

        return await probe.Queryable<TEntity>().CountAsync();
    }

    /// <summary>
    /// 创建一条独立连接，只能看到已提交且未被过滤的数据
    /// </summary>
    /// <returns>探针客户端</returns>
    public SqlSugarClient CreateProbeClient()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConfigId = "Probe",
            ConnectionString = ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
    }

    /// <summary>
    /// 释放连接并删除临时库
    /// </summary>
    public void Dispose()
    {
        _services.Dispose();
        Scope.Dispose();

        if (File.Exists(_sqliteFile))
        {
            File.Delete(_sqliteFile);
        }
    }

    private static long ResolveTenantScopeId(ICurrentTenantAccessor accessor)
    {
        return accessor.Current?.TenantId ?? 0;
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkEventPublisher, NullUnitOfWorkEventPublisher>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 由当前租户替身驱动的租户上下文访问器
    /// </summary>
    /// <param name="tenant">当前租户替身</param>
    private sealed class TenantAccessor(MutableCurrentTenant tenant) : ICurrentTenantAccessor
    {
        /// <summary>
        /// 当前租户快照
        /// </summary>
        public BasicTenantInfo? Current
        {
            get => tenant.Id is { } id ? new BasicTenantInfo(id, tenant.Name) : null;
            set
            {
                _ = value;
            }
        }
    }
}
