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
    /// <summary>
    /// 操作日志写入后能查回且主键非零
    /// </summary>
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

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"xihan_writer_{Guid.NewGuid():N}.db");

    private static SqlSugarClient CreateClient(string databaseFile) => new(new ConnectionConfig
    {
        // 关闭连接池，避免用例结束后驱动仍持有临时库文件句柄。
        ConnectionString = $"DataSource={databaseFile};Pooling=False",
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = true
    });

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
