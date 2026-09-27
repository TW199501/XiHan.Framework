// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Auditing.SqlSugar.Writers;
using XiHan.Framework.Auditing.Writers;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Auditing.SqlSugar.Tests;

/// <summary>
/// 日志写入器测试
/// </summary>
public class LogWriterTests
{
    [Fact]
    public async Task 访问日志写入器按实体类型路由并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysAccessLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarAccessLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new AccessLogRecord
            {
                TraceId = "trace-access",
                Method = "GET",
                Path = "/Home",
                QueryString = "?page=1"
            });
            var after = DateTimeOffset.UtcNow;

            AssertRouted(resolver, typeof(SysAccessLog));

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysAccessLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.TraceId == "trace-access")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }

    [Fact]
    public async Task 接口日志写入器按实体类型路由并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysApiLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarApiLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new ApiLogRecord
            {
                TraceId = "trace-api",
                Method = "POST",
                Path = "/Api/Order",
                AppId = "app-1",
                StatusCode = 200
            });
            var after = DateTimeOffset.UtcNow;

            AssertRouted(resolver, typeof(SysApiLog));

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysApiLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.TraceId == "trace-api")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            Assert.Equal("app-1", row.AppId);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }

    [Fact]
    public async Task 异常日志写入器按实体类型路由并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysExceptionLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarExceptionLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new ExceptionLogRecord
            {
                TraceId = "trace-exception",
                Method = "GET",
                Path = "/Boom",
                StatusCode = 500,
                ExceptionType = typeof(InvalidOperationException).FullName!,
                ExceptionMessage = "boom"
            });
            var after = DateTimeOffset.UtcNow;

            AssertRouted(resolver, typeof(SysExceptionLog));

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysExceptionLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.TraceId == "trace-exception")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            Assert.Equal(500, row.StatusCode);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }

    [Fact]
    public async Task 登录日志写入器按实体类型路由并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysLoginLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarLoginLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var loginTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new LoginLogRecord
            {
                TraceId = "trace-login",
                UserName = "tester",
                LoginResult = 1,
                LoginIp = "10.0.0.1",
                LoginTime = loginTime
            });
            var after = DateTimeOffset.UtcNow;

            AssertRouted(resolver, typeof(SysLoginLog));

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysLoginLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.TraceId == "trace-login")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            Assert.Equal("tester", row.UserName);
            Assert.Equal(1, row.LoginResult);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }

    [Fact]
    public async Task 操作日志写入器按实体类型路由并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysOperationLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarOperationLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new OperationLogRecord
            {
                TraceId = "trace-operation",
                Method = "POST",
                Path = "/Order",
                StatusCode = 201,
                ElapsedMilliseconds = 5
            });
            var after = DateTimeOffset.UtcNow;

            AssertRouted(resolver, typeof(SysOperationLog));

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysOperationLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.TraceId == "trace-operation")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            Assert.Equal(201, row.StatusCode);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }

    [Fact]
    public async Task 实体差异日志写入器经当前工作单元客户端写入并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysDiffLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarEntityDiffLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new EntityDiffLogRecord
            {
                OperationType = "Update",
                EntityType = "Order",
                EntityId = "1001",
                ChangedFields = "[{\"Field\":\"Status\"}]"
            });
            var after = DateTimeOffset.UtcNow;

            AssertUsedCurrentClient(resolver);

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysDiffLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.EntityId == "1001")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            Assert.Equal("Update", row.OperationType);
            Assert.Equal("EntityChange", row.AuditType);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }

    /// <summary>
    /// 注册扩展以 SqlSugar 写入器顶替空写入器
    /// </summary>
    [Fact]
    public void 注册扩展顶替空写入器()
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IOperationLogWriter, NullOperationLogWriter>();

        services.AddXiHanAuditingSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IOperationLogWriter));
        Assert.Equal(typeof(SqlSugarOperationLogWriter), descriptor.ImplementationType);
    }

    /// <summary>
    /// 六个写入器全部被注册扩展顶替
    /// </summary>
    [Theory]
    [InlineData(typeof(IAccessLogWriter), typeof(SqlSugarAccessLogWriter))]
    [InlineData(typeof(IApiLogWriter), typeof(SqlSugarApiLogWriter))]
    [InlineData(typeof(IEntityDiffLogWriter), typeof(SqlSugarEntityDiffLogWriter))]
    [InlineData(typeof(IExceptionLogWriter), typeof(SqlSugarExceptionLogWriter))]
    [InlineData(typeof(ILoginLogWriter), typeof(SqlSugarLoginLogWriter))]
    [InlineData(typeof(IOperationLogWriter), typeof(SqlSugarOperationLogWriter))]
    public void 六个写入器全部被顶替(Type serviceType, Type expectedImplementationType)
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IAccessLogWriter, NullAccessLogWriter>();
        services.TryAddScoped<IApiLogWriter, NullApiLogWriter>();
        services.TryAddScoped<IEntityDiffLogWriter, NullEntityDiffLogWriter>();
        services.TryAddScoped<IExceptionLogWriter, NullExceptionLogWriter>();
        services.TryAddScoped<ILoginLogWriter, NullLoginLogWriter>();
        services.TryAddScoped<IOperationLogWriter, NullOperationLogWriter>();

        services.AddXiHanAuditingSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == serviceType);
        Assert.Equal(expectedImplementationType, descriptor.ImplementationType);
    }

    private static DateTime[] CurrentUtcMonthRange()
    {
        var now = DateTimeOffset.UtcNow;
        var begin = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return [begin, begin.AddMonths(1).AddTicks(-1)];
    }

    private static void AssertRouted(StubClientResolver resolver, Type expectedEntityType)
    {
        Assert.Equal(expectedEntityType, Assert.Single(resolver.RequestedEntityTypes));
        Assert.Equal(0, resolver.GetCurrentClientCalls);
        Assert.Equal(0, resolver.GetClientCalls);
    }

    private static void AssertUsedCurrentClient(StubClientResolver resolver)
    {
        Assert.Empty(resolver.RequestedEntityTypes);
        Assert.Equal(1, resolver.GetCurrentClientCalls);
        Assert.Equal(0, resolver.GetClientCalls);
    }

    private static void AssertCreatedTimeNearNow(DateTimeOffset actual, DateTimeOffset before, DateTimeOffset after)
    {
        // SQLite 的 DateTimeOffset 列不保存偏移，回读按本地时区解析，因此比对写入的时钟读数而非瞬时
        Assert.InRange(
            actual.DateTime,
            before.UtcDateTime.AddSeconds(-5),
            after.UtcDateTime.AddSeconds(5));
    }

    private static string NewDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"xihan_writer_{Guid.NewGuid():N}.db");
    }

    private static SqlSugarClient CreateClient(string databaseFile)
    {
        // 关闭连接池，避免用例结束后驱动仍持有临时库文件句柄。
        return new(new ConnectionConfig
        {
            ConnectionString = $"DataSource={databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
    }

    private static void DeleteDatabase(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// 测试用客户端解析器：固定返回同一个客户端，并记录被请求的是哪个实体类型
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
{
    private readonly ISqlSugarClient _client;

    public StubClientResolver(ISqlSugarClient client)
    {
        _client = client;
    }

    public List<Type> RequestedEntityTypes { get; } = [];

    public int GetCurrentClientCalls { get; private set; }

    public int GetClientCalls { get; private set; }

    public ISqlSugarClient GetCurrentClient()
    {
        GetCurrentClientCalls++;
        return _client;
    }

    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        RequestedEntityTypes.Add(entityType);
        return _client;
    }

    public ISqlSugarClient GetClient(string configId)
    {
        GetClientCalls++;
        return _client;
    }

    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return ["Default"];
    }

    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return ["Default"];
    }

    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [_client];
    }

    public ITenant AsTenant()
    {
        throw new NotSupportedException("测试桩不支持多租户切换。");
    }
}
