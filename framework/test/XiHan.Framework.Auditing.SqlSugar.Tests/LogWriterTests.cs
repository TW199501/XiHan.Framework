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
    public async Task 操作日志写入后能查回且主键非零()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysOperationLog));

            var writer = new SqlSugarOperationLogWriter(
                new StubClientResolver(db),
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            await writer.WriteAsync(new OperationLogRecord
            {
                TraceId = "trace-writer",
                Method = "POST",
                Path = "/Order",
                StatusCode = 201,
                ElapsedMilliseconds = 5
            });

            var now = DateTime.UtcNow;
            var found = db.Queryable<SysOperationLog>()
                .SplitTable(now.AddDays(-1), now.AddDays(1))
                .Where(item => item.TraceId == "trace-writer")
                .ToList();

            Assert.Single(found);
            Assert.NotEqual(0L, found[0].BasicId);
            Assert.Equal(201, found[0].StatusCode);
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
    /// 五个写入器全部被注册扩展顶替
    /// </summary>
    [Theory]
    [InlineData(typeof(IAccessLogWriter), typeof(SqlSugarAccessLogWriter))]
    [InlineData(typeof(IApiLogWriter), typeof(SqlSugarApiLogWriter))]
    [InlineData(typeof(IExceptionLogWriter), typeof(SqlSugarExceptionLogWriter))]
    [InlineData(typeof(ILoginLogWriter), typeof(SqlSugarLoginLogWriter))]
    [InlineData(typeof(IOperationLogWriter), typeof(SqlSugarOperationLogWriter))]
    public void 五个写入器全部被顶替(Type serviceType, Type expectedImplementationType)
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IAccessLogWriter, NullAccessLogWriter>();
        services.TryAddScoped<IApiLogWriter, NullApiLogWriter>();
        services.TryAddScoped<IExceptionLogWriter, NullExceptionLogWriter>();
        services.TryAddScoped<ILoginLogWriter, NullLoginLogWriter>();
        services.TryAddScoped<IOperationLogWriter, NullOperationLogWriter>();

        services.AddXiHanAuditingSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == serviceType);
        Assert.Equal(expectedImplementationType, descriptor.ImplementationType);
    }

    [Fact]
    public async Task 登录日志写入后能查回()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysLoginLog));

            var writer = new SqlSugarLoginLogWriter(
                new StubClientResolver(db),
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            await writer.WriteAsync(new LoginLogRecord
            {
                TraceId = "trace-login",
                UserName = "tester",
                LoginResult = 1,
                LoginTime = DateTimeOffset.UtcNow
            });

            var now = DateTime.UtcNow;
            var found = db.Queryable<SysLoginLog>()
                .SplitTable(now.AddDays(-1), now.AddDays(1))
                .Where(item => item.TraceId == "trace-login")
                .ToList();

            Assert.Single(found);
            Assert.Equal("tester", found[0].UserName);
            Assert.Equal(1, found[0].LoginResult);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
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
/// 测试用客户端解析器，固定返回同一个客户端
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
{
    private readonly ISqlSugarClient _client;

    public StubClientResolver(ISqlSugarClient client)
    {
        _client = client;
    }

    public ISqlSugarClient GetCurrentClient()
    {
        return _client;
    }

    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        return _client;
    }

    public ISqlSugarClient GetClient(string configId)
    {
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
