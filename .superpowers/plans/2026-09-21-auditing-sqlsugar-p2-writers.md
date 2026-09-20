# Auditing.SqlSugar 日志写入器（P2）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 5 个审计日志写入器，把 `Auditing` 采集管线的记录落到 P1 建立的分表中，并用 `services.Replace` 顶替 5 个 `Null*Writer`；完成后 PR1 可提交。

**Architecture:** 记录模型到实体的转换抽成纯静态映射方法，可脱离数据库单测。写入器经 `ISqlSugarClientResolver` 取客户端、经 `IDistributedIdGenerator<long>` 取雪花主键，用 `Insertable(...).SplitTable().ExecuteCommandAsync()` 落库。写入器不参与业务事务——日志由后台 Worker 在业务请求之外消费。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-21-auditing-sqlsugar-p2-writers-design.md`

> 该 spec 自成一体，实现 P2 所需的全部约束都在其中。**不要**去读 `2026-09-21-sqlsugar-persistence-design.md`——那是拆分前的总纲，已停用。

**前置:** P1（`.superpowers/plans/2026-09-21-auditing-sqlsugar-p1-entities.md`）必须已完成——本计划依赖它产出的 5 个实体与测试项目。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-sqlsugar`）。worktree 内看不到这些文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录**：`E:/source/XiHan/XiHan.Framework-sqlsugar`（分支 `feat/sqlsugar`）。

**技术栈是 SqlSugar，不是 Entity Framework Core。** 下列 EF Core 惯用法一律禁止：

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | SqlSugar 无 change tracking，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()` |
| `Migrations` / `Add-Migration` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台限制**：Microsoft.Testing.Platform，不是 VSTest。**没有可用的筛选参数**，`--filter`/`--list-tests` 返回退出码 3；**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。要跑单个测试类就整个项目跑。

**提交信息**：中文 Conventional Commits，作用域 `auditing-sqlsugar`。**不加任何 AI 署名。**

---

## 本计划特有的四条硬约束

这四条是实现阶段最容易出错的地方，每个任务都适用。

**① 注册必须用 `services.Replace`，不能用 `TryAdd`。**

`XiHanAuditingModule` 在它自己的 `ConfigureServices` 里以 `TryAddScoped` 注册了 5 个 `Null*Writer`（见 `framework/src/XiHan.Framework.Auditing/Extensions/DependencyInjection/XiHanAuditingServiceCollectionExtensions.cs:48-52`）。本模块 `[DependsOn(typeof(XiHanAuditingModule))]`，依赖模块的 `ConfigureServices` 先执行，因此此刻 `TryAdd` 是空操作、`Null*Writer` 会留在容器里，日志照样丢失且无任何报错。必须用 `services.Replace(ServiceDescriptor.Scoped<IXxxLogWriter, XxxLogWriter>())`。

**② 主键经构造函数传入，不能用对象初始化器。**

`EntityBase<TKey>.BasicId` 是 `{ get; protected set; }`。P1 的实体各有两个公开构造函数，写入器用 `new SysOperationLog(idGenerator.NextId())`。

**③ 创建时间用 `DateTimeOffset.UtcNow`，不要引入 `IClock`。**

`XiHan.Framework.Timing` 在本包的依赖链上不可传递获得（`Data` 与 `Auditing` 都未引用它），为一个时间戳新增 `ProjectReference` 不划算。直接用 `DateTimeOffset.UtcNow`。

**④ 不要重做脱敏。**

`LogSanitizer` 已在采集端执行——见 `framework/src/XiHan.Framework.Web.Api/Filters/XiHanActionLoggingFilter.cs:96-97`、`Middlewares/XiHanApiLoggingMiddleware.cs:161`、`Logging/ExceptionLogReporter.cs:65-67`。记录到达写入器时已经脱敏，重复调用会二次遮蔽已遮蔽的内容。

---

## File Structure

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  XiHanAuditingSqlSugarModule.cs                    修改：注册写入器
  Extensions/DependencyInjection/
    XiHanAuditingSqlSugarServiceCollectionExtensions.cs   新增：Replace 注册
  Mapping/AuditingLogMapper.cs                      新增：5 个纯静态映射方法
  Writers/
    SqlSugarAccessLogWriter.cs      SqlSugarApiLogWriter.cs
    SqlSugarExceptionLogWriter.cs   SqlSugarLoginLogWriter.cs
    SqlSugarOperationLogWriter.cs

framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  AuditingLogMapperTests.cs     新增：映射纯函数测试，不碰数据库
  LogWriterTests.cs             新增：SQLite 落库测试
```

映射与写入分离：映射是纯函数、可脱库单测；写入器只负责取客户端、取主键、执行插入。

---

### Task 1: 记录到实体的映射

**Files:**
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping/AuditingLogMapper.cs`
- Create: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/AuditingLogMapperTests.cs`

**Interfaces:**
- Consumes: P1 产出的 5 个实体及其 `(long basicId)` 构造函数
- Produces: `public static class AuditingLogMapper`，5 个方法，签名统一为
  `public static TEntity ToEntity(TRecord record, long basicId, DateTimeOffset createdTime)`
  具体为 `ToEntity(AccessLogRecord, long, DateTimeOffset) → SysAccessLog`、`ToEntity(ApiLogRecord, ...) → SysApiLog`、`ToEntity(ExceptionLogRecord, ...) → SysExceptionLog`、`ToEntity(LoginLogRecord, ...) → SysLoginLog`、`ToEntity(OperationLogRecord, ...) → SysOperationLog`（按参数类型重载）

**参考来源（动手前先读）：**
- 记录模型字段：`framework/src/XiHan.Framework.Auditing/AccessLogRecord.cs`、`ApiLogRecord.cs`、`ExceptionLogRecord.cs`、`LoginLogRecord.cs`、`OperationLogRecord.cs`
- 实体字段：P1 产出的 `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/*.cs`

**本任务禁止事项：** 不要引入 AutoMapper 或 `XiHan.Framework.ObjectMapping`——字段是一一对应的平铺赋值，加映射框架会让上游审查质疑必要性。不要在映射里调用 `LogSanitizer`（硬约束 ④）。不要在映射里生成主键或取当前时间——两者都由调用方传入，这样映射才是纯函数。

- [x] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/AuditingLogMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Auditing.SqlSugar.Mapping;

namespace XiHan.Framework.Auditing.SqlSugar.Tests;

/// <summary>
/// 日志映射测试
/// </summary>
public class AuditingLogMapperTests
{
    private static readonly DateTimeOffset CreatedTime = new(2026, 9, 21, 10, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// 操作日志映射保留全部字段
    /// </summary>
    [Fact]
    public void 操作日志映射保留全部字段()
    {
        var record = new OperationLogRecord
        {
            TraceId = "trace-1",
            SessionId = "session-1",
            UserId = 42,
            UserName = "tester",
            ControllerName = "Order",
            ActionName = "Create",
            Method = "POST",
            Path = "/Order",
            RequestParams = "{}",
            ResponseResult = "{\"ok\":true}",
            StatusCode = 200,
            ElapsedMilliseconds = 12,
            RemoteIp = "127.0.0.1",
            UserAgent = "xunit",
            ErrorMessage = null
        };

        var entity = AuditingLogMapper.ToEntity(record, 1001L, CreatedTime);

        Assert.Equal(1001L, entity.BasicId);
        Assert.Equal(CreatedTime, entity.CreatedTime);
        Assert.Equal("trace-1", entity.TraceId);
        Assert.Equal("session-1", entity.SessionId);
        Assert.Equal(42, entity.UserId);
        Assert.Equal("tester", entity.UserName);
        Assert.Equal("Order", entity.ControllerName);
        Assert.Equal("Create", entity.ActionName);
        Assert.Equal("POST", entity.Method);
        Assert.Equal("/Order", entity.Path);
        Assert.Equal("{}", entity.RequestParams);
        Assert.Equal("{\"ok\":true}", entity.ResponseResult);
        Assert.Equal(200, entity.StatusCode);
        Assert.Equal(12, entity.ElapsedMilliseconds);
        Assert.Equal("127.0.0.1", entity.RemoteIp);
        Assert.Equal("xunit", entity.UserAgent);
        Assert.Null(entity.ErrorMessage);
    }

    /// <summary>
    /// 登录日志的登录时间与创建时间各自独立
    /// </summary>
    [Fact]
    public void 登录日志的登录时间与创建时间各自独立()
    {
        var loginTime = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        var record = new LoginLogRecord
        {
            TraceId = "trace-2",
            UserId = 7,
            UserName = "tester",
            SessionId = "session-2",
            LoginResult = 1,
            Message = "成功",
            LoginIp = "10.0.0.1",
            UserAgent = "xunit",
            DeviceId = "device-1",
            LoginTime = loginTime
        };

        var entity = AuditingLogMapper.ToEntity(record, 1002L, CreatedTime);

        Assert.Equal(CreatedTime, entity.CreatedTime);
        Assert.Equal(loginTime, entity.LoginTime);
        Assert.Equal(1, entity.LoginResult);
        Assert.Equal("device-1", entity.DeviceId);
    }

    /// <summary>
    /// 异常日志映射保留异常三要素
    /// </summary>
    [Fact]
    public void 异常日志映射保留异常三要素()
    {
        var record = new ExceptionLogRecord
        {
            TraceId = "trace-3",
            StatusCode = 500,
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "boom",
            ExceptionStackTrace = "at X.Y()"
        };

        var entity = AuditingLogMapper.ToEntity(record, 1003L, CreatedTime);

        Assert.Equal("System.InvalidOperationException", entity.ExceptionType);
        Assert.Equal("boom", entity.ExceptionMessage);
        Assert.Equal("at X.Y()", entity.ExceptionStackTrace);
        Assert.Equal(500, entity.StatusCode);
    }

    /// <summary>
    /// 接口日志映射保留签名校验结果
    /// </summary>
    [Fact]
    public void 接口日志映射保留签名校验结果()
    {
        var record = new ApiLogRecord
        {
            TraceId = "trace-4",
            ClientId = "client-1",
            AppId = "app-1",
            IsSignatureValid = false,
            SignatureAlgorithm = "HMACSHA256",
            Method = "GET",
            Path = "/Api",
            StatusCode = 401,
            IsSuccess = false
        };

        var entity = AuditingLogMapper.ToEntity(record, 1004L, CreatedTime);

        Assert.False(entity.IsSignatureValid);
        Assert.Equal("HMACSHA256", entity.SignatureAlgorithm);
        Assert.False(entity.IsSuccess);
        Assert.Equal("client-1", entity.ClientId);
    }

    /// <summary>
    /// 访问日志映射保留响应大小与耗时
    /// </summary>
    [Fact]
    public void 访问日志映射保留响应大小与耗时()
    {
        var record = new AccessLogRecord
        {
            TraceId = "trace-5",
            ResourceName = "Home",
            Method = "GET",
            Path = "/",
            QueryString = "?a=1",
            StatusCode = 200,
            ElapsedMilliseconds = 8,
            ResponseSize = 2048
        };

        var entity = AuditingLogMapper.ToEntity(record, 1005L, CreatedTime);

        Assert.Equal("Home", entity.ResourceName);
        Assert.Equal("?a=1", entity.QueryString);
        Assert.Equal(8, entity.ElapsedMilliseconds);
        Assert.Equal(2048, entity.ResponseSize);
    }
}
```

- [x] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`AuditingLogMapper` 不存在。

- [x] **Step 3: 实现映射器**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping/AuditingLogMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Auditing.SqlSugar.Entities;

namespace XiHan.Framework.Auditing.SqlSugar.Mapping;

/// <summary>
/// 审计日志记录到实体的映射
/// </summary>
public static class AuditingLogMapper
{
    /// <summary>
    /// 把访问日志记录转换为实体
    /// </summary>
    /// <param name="record">访问日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>访问日志实体</returns>
    public static SysAccessLog ToEntity(AccessLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysAccessLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            SessionId = record.SessionId,
            ResourceName = record.ResourceName,
            Method = record.Method,
            Path = record.Path,
            QueryString = record.QueryString,
            RequestBody = record.RequestBody,
            StatusCode = record.StatusCode,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            Referer = record.Referer,
            ElapsedMilliseconds = record.ElapsedMilliseconds,
            ResponseSize = record.ResponseSize,
            ErrorMessage = record.ErrorMessage
        };
    }

    /// <summary>
    /// 把接口日志记录转换为实体
    /// </summary>
    /// <param name="record">接口日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>接口日志实体</returns>
    public static SysApiLog ToEntity(ApiLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysApiLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            ClientId = record.ClientId,
            AppId = record.AppId,
            IsSignatureValid = record.IsSignatureValid,
            SignatureAlgorithm = record.SignatureAlgorithm,
            Method = record.Method,
            Path = record.Path,
            ApiName = record.ApiName,
            ControllerName = record.ControllerName,
            ActionName = record.ActionName,
            RequestParams = record.RequestParams,
            RequestBody = record.RequestBody,
            ResponseBody = record.ResponseBody,
            StatusCode = record.StatusCode,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            Referer = record.Referer,
            ElapsedMilliseconds = record.ElapsedMilliseconds,
            RequestSize = record.RequestSize,
            ResponseSize = record.ResponseSize,
            IsSuccess = record.IsSuccess,
            ErrorMessage = record.ErrorMessage
        };
    }

    /// <summary>
    /// 把异常日志记录转换为实体
    /// </summary>
    /// <param name="record">异常日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>异常日志实体</returns>
    public static SysExceptionLog ToEntity(ExceptionLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysExceptionLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            Path = record.Path,
            Method = record.Method,
            ControllerName = record.ControllerName,
            ActionName = record.ActionName,
            StatusCode = record.StatusCode,
            ExceptionType = record.ExceptionType,
            ExceptionMessage = record.ExceptionMessage,
            ExceptionStackTrace = record.ExceptionStackTrace,
            RequestHeaders = record.RequestHeaders,
            RequestParams = record.RequestParams,
            RequestBody = record.RequestBody,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent
        };
    }

    /// <summary>
    /// 把登录日志记录转换为实体
    /// </summary>
    /// <param name="record">登录日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>登录日志实体</returns>
    public static SysLoginLog ToEntity(LoginLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysLoginLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            UserId = record.UserId,
            UserName = record.UserName,
            SessionId = record.SessionId,
            LoginResult = record.LoginResult,
            Message = record.Message,
            LoginIp = record.LoginIp,
            UserAgent = record.UserAgent,
            DeviceId = record.DeviceId,
            LoginTime = record.LoginTime
        };
    }

    /// <summary>
    /// 把操作日志记录转换为实体
    /// </summary>
    /// <param name="record">操作日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>操作日志实体</returns>
    public static SysOperationLog ToEntity(OperationLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysOperationLog(basicId)
        {
            CreatedTime = createdTime,
            TraceId = record.TraceId,
            SessionId = record.SessionId,
            UserId = record.UserId,
            UserName = record.UserName,
            ControllerName = record.ControllerName,
            ActionName = record.ActionName,
            Method = record.Method,
            Path = record.Path,
            RequestParams = record.RequestParams,
            ResponseResult = record.ResponseResult,
            StatusCode = record.StatusCode,
            ElapsedMilliseconds = record.ElapsedMilliseconds,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            ErrorMessage = record.ErrorMessage
        };
    }
}
```

- [x] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [x] **Step 5: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/AuditingLogMapperTests.cs
git commit -m "feat(auditing-sqlsugar): 新增日志记录到实体的映射"
```

---

### Task 2: 操作日志写入器与注册

**Files:**
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarOperationLogWriter.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Auditing.SqlSugar/XiHanAuditingSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `AuditingLogMapper.ToEntity`；P1 的实体；框架的 `ISqlSugarClientResolver`、`IDistributedIdGenerator<long>`
- Produces: `SqlSugarOperationLogWriter : IOperationLogWriter`；扩展方法 `IServiceCollection AddXiHanAuditingSqlSugar(this IServiceCollection services)`；后续 4 个写入器照此形状

**参考来源（动手前先读）：**
- 客户端解析：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`
- 写入器契约与 Null 实现：`framework/src/XiHan.Framework.Auditing/Writers/IOperationLogWriter.cs`、`NullOperationLogWriter.cs`
- 现有注册（确认 `TryAddScoped` 的位置）：`framework/src/XiHan.Framework.Auditing/Extensions/DependencyInjection/XiHanAuditingServiceCollectionExtensions.cs:47-52`
- Worker 如何调用写入器：`framework/src/XiHan.Framework.Auditing/Workers/OperationLogQueueWorker.cs`（`FlushAsync` 建 scope、解析一次写入器、逐条调用 `WriteAsync`）
- ID 生成器注册：`framework/src/XiHan.Framework.DistributedIds/Extensions/DependencyInjection/`（`AddSingleton` 出 `IDistributedIdGenerator<long>`）
- 插入 API：`/e/source/platfrom-admin/docs/SqlSugar-docs/插入數據.md`、`自動分表.md`

**本任务禁止事项：** 见本计划「四条硬约束」全部四条。另外不要在写入器里开事务——审计写入不参与业务事务（spec §7.3）。不要在写入器里做批量缓冲——`IOperationLogWriter.WriteAsync` 是逐条契约，缓冲会让进程退出时丢数据。

**依赖可得性说明：** `IDistributedIdGenerator<long>` 由 `XiHanDistributedIdsModule` 注册，而 `XiHanDataModule` 已 `[DependsOn(typeof(XiHanDistributedIdsModule))]`，本模块经 `XiHanDataModule` 间接获得，**不需要**新增 `ProjectReference` 或 `DependsOn`。

- [x] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Auditing.SqlSugar.Writers;
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
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_writer_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databaseFile}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

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
            if (File.Exists(databaseFile))
            {
                File.Delete(databaseFile);
            }
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
```

- [x] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarOperationLogWriter` 不存在。

- [x] **Step 3: 实现写入器**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarOperationLogWriter.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Auditing.SqlSugar.Mapping;
using XiHan.Framework.Auditing.Writers;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Auditing.SqlSugar.Writers;

/// <summary>
/// 操作日志 SqlSugar 写入器
/// </summary>
public class SqlSugarOperationLogWriter : IOperationLogWriter
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarOperationLogWriter(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 写入操作日志
    /// </summary>
    /// <param name="record">操作日志记录</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WriteAsync(OperationLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = AuditingLogMapper.ToEntity(record, _idGenerator.NextId(), DateTimeOffset.UtcNow);
        var client = _clientResolver.GetClientForEntity<SysOperationLog>();

        await client.Insertable(entity).SplitTable().ExecuteCommandAsync();
    }
}
```

**关于取消令牌**：`SplitInsertable.ExecuteCommandAsync()` **没有**接受 `CancellationToken` 的重载（已核对 `/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Abstract/InsertableProvider/SplitInsertable.cs:42`，该类只有 `ExecuteCommand()`、`ExecuteCommandAsync()`、`ExecuteReturnSnowflakeId*` 几个执行方法）。因此令牌只能在执行前检查一次，**不要**试图传进去，也不要为此改用非分表的 `Insertable` 重载。

- [x] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [x] **Step 5: 实现注册扩展**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Auditing.SqlSugar.Writers;
using XiHan.Framework.Auditing.Writers;

namespace XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 审计日志 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanAuditingSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 写入器替换审计日志的空写入器
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanAuditingSqlSugar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IOperationLogWriter, SqlSugarOperationLogWriter>());

        return services;
    }
}
```

`Replace` 位于 `Microsoft.Extensions.DependencyInjection.Extensions` 命名空间，需要额外 `using`。

- [x] **Step 6: 在模块里调用扩展**

把 `XiHanAuditingSqlSugarModule.cs` 改为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Auditing.SqlSugar;

/// <summary>
/// 曦寒框架审计日志 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuditingSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 写入器替换 <see cref="XiHanAuditingModule"/> 注册的空写入器。
/// </remarks>
[DependsOn(
    typeof(XiHanAuditingModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuditingSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanAuditingSqlSugar();
    }
}
```

- [x] **Step 7: 补一个注册断言测试**

在 `LogWriterTests.cs` 的 `LogWriterTests` 类里追加：

```csharp
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
```

文件顶部补 `using`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection;
```

- [x] **Step 8: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS；构建 0 Warning(s) 0 Error(s)。

- [x] **Step 9: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar framework/test/XiHan.Framework.Auditing.SqlSugar.Tests
git commit -m "feat(auditing-sqlsugar): 新增操作日志写入器并替换空实现"
```

---

### Task 3: 其余四个写入器

**Files:**
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarAccessLogWriter.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarApiLogWriter.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarExceptionLogWriter.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarLoginLogWriter.cs`
- Modify: `framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs`

**Interfaces:**
- Consumes: Task 2 确立的写入器形状与 `StubClientResolver`
- Produces: `SqlSugarAccessLogWriter` / `SqlSugarApiLogWriter` / `SqlSugarExceptionLogWriter` / `SqlSugarLoginLogWriter`，全部注册在 `AddXiHanAuditingSqlSugar` 内

**参考来源（动手前先读）：**
- 写入器契约：`framework/src/XiHan.Framework.Auditing/Writers/IAccessLogWriter.cs`、`IApiLogWriter.cs`、`IExceptionLogWriter.cs`、`ILoginLogWriter.cs`
- 写入器形状：本计划 Task 2 的 `SqlSugarOperationLogWriter`（逐字照抄结构）

**本任务禁止事项：** 同 Task 2 全部四条硬约束。

- [x] **Step 1: 追加失败的测试**

在 `LogWriterTests` 类里追加：

```csharp
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

    /// <summary>
    /// 登录日志写入后能查回
    /// </summary>
    [Fact]
    public async Task 登录日志写入后能查回()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_writer_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databaseFile}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

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
        }
        finally
        {
            if (File.Exists(databaseFile))
            {
                File.Delete(databaseFile);
            }
        }
    }
```

- [x] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，四个写入器类型不存在。

- [x] **Step 3: 实现四个写入器**

四个文件结构一致，仅记录类型、实体类型、接口不同。

`Writers/SqlSugarAccessLogWriter.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Auditing.SqlSugar.Mapping;
using XiHan.Framework.Auditing.Writers;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Auditing.SqlSugar.Writers;

/// <summary>
/// 访问日志 SqlSugar 写入器
/// </summary>
public class SqlSugarAccessLogWriter : IAccessLogWriter
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarAccessLogWriter(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 写入访问日志
    /// </summary>
    /// <param name="record">访问日志记录</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task WriteAsync(AccessLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = AuditingLogMapper.ToEntity(record, _idGenerator.NextId(), DateTimeOffset.UtcNow);
        var client = _clientResolver.GetClientForEntity<SysAccessLog>();

        await client.Insertable(entity).SplitTable().ExecuteCommandAsync();
    }
}
```

其余三个文件与上面这份**逐字相同**，只替换下表列出的 5 个标识符与 1 处注释文字。除此之外不得有任何差异——包括 `using` 顺序、成员顺序、空行位置。

| 文件 | 类名 | 实现接口 | 记录参数类型 | 实体泛型参数 | XML 注释中的日志名 |
| --- | --- | --- | --- | --- | --- |
| `SqlSugarApiLogWriter.cs` | `SqlSugarApiLogWriter` | `IApiLogWriter` | `ApiLogRecord` | `SysApiLog` | 接口日志 |
| `SqlSugarExceptionLogWriter.cs` | `SqlSugarExceptionLogWriter` | `IExceptionLogWriter` | `ExceptionLogRecord` | `SysExceptionLog` | 异常日志 |
| `SqlSugarLoginLogWriter.cs` | `SqlSugarLoginLogWriter` | `ILoginLogWriter` | `LoginLogRecord` | `SysLoginLog` | 登录日志 |

「XML 注释中的日志名」指类摘要（`/// 访问日志 SqlSugar 写入器`）、`WriteAsync` 摘要（`/// 写入访问日志`）与其 `record` 参数说明（`/// <param name="record">访问日志记录</param>`）三处的「访问日志」。

- [x] **Step 4: 补齐注册**

把 `AddXiHanAuditingSqlSugar` 的方法体改为：

```csharp
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IAccessLogWriter, SqlSugarAccessLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IApiLogWriter, SqlSugarApiLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IExceptionLogWriter, SqlSugarExceptionLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<ILoginLogWriter, SqlSugarLoginLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IOperationLogWriter, SqlSugarOperationLogWriter>());

        return services;
```

- [x] **Step 5: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS；构建 0 Warning(s) 0 Error(s)。

- [x] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar framework/test/XiHan.Framework.Auditing.SqlSugar.Tests
git commit -m "feat(auditing-sqlsugar): 新增访问、接口、异常、登录日志写入器"
```

---

### Task 4: 文档与 PR 收尾

**Files:**
- Modify: `framework/src/XiHan.Framework.Auditing.SqlSugar/README.md`
- Create: `docs/packages/auditing-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `README.md`（根目录模块清单）

**Interfaces:**
- Consumes: 前三个任务的全部产出
- Produces: 无（终端任务）

**参考来源（动手前先读）：**
- 包文档范本：`docs/packages/auditing.md`
- 侧边栏结构：`docs/.vitepress/config.ts`（找到 packages 分组中 `auditing` 的位置）
- 根 README 模块清单：`README.md` 与 `README_cn.md` 中列出 66 个模块的表格

**本任务禁止事项：** 不要顺手重写与本 PR 无关的文档。上游明确要求「一个 PR 只做一件事」——一个改代码的 PR 附带无关的文档重写，等于请他审一份他没要的文档 PR。不要在文档里声称仓库有 `.codegraph/` 目录。

- [x] **Step 1: 更新包 README**

把 P1 写的 README「核心能力」一节改为：

```markdown
## 核心能力

- 5 类审计日志（访问 / 接口 / 异常 / 登录 / 操作）的 SqlSugar 实体与写入器
- 按月自动分表，表名形如 `sys_operation_log_20260901`
- 表结构由 `DbInitializer` 在应用启动时创建
- 主键为雪花 ID，由 `XiHan.Framework.DistributedIds` 生成
```

并在「扩展点」一节追加：

```markdown
写入器以 `services.Replace` 顶替 `XiHan.Framework.Auditing` 注册的空实现。应用侧若要再次替换，同样使用 `Replace`——`TryAdd` 不会生效。
```

- [x] **Step 2: 新增包文档**

`docs/packages/auditing-sqlsugar.md`，按 `docs/packages/auditing.md` 的结构组织，至少包含：包定位、表清单与分表规则、主键与列名约定、启用方式（`[DependsOn]`）、与 `XiHan.Framework.Auditing` 的关系、审计写入不参与业务事务这一行为说明。

文档里要用到的 P1 既定约定（**本任务只是引用，不要改动实体**）：

**五张表**，按月分表，实际表名带月份后缀（如 `sys_operation_log_20260901`）：

```
sys_access_log   sys_api_log   sys_exception_log
sys_login_log    sys_operation_log
```

| 约定 | 值 |
| --- | --- |
| 表名 | `sys_` 前缀、全小写下划线 |
| 分表 | `[SplitTable(SplitType.Month)]`，`[SugarTable]` 模板含 `{year}{month}{day}` 三个变量 |
| 分表字段 | `Created_Time` |
| 列名 | Pascal_Snake_Case，如 `Basic_Id`、`Trace_Id`、`Status_Code` |
| 主键 | `Basic_Id`，`long`，非自增，雪花 ID |
| 实体基类 | `SugarCreationEntity<long>`，实现 `ISplitTableEntity` |

`SysLoginLog` 的 `Login_Time` 是业务时间，与分表字段 `Created_Time` 各占一列。

- [x] **Step 3: 挂上侧边栏**

在 `docs/.vitepress/config.ts` 的 packages 分组里、`auditing` 条目之后插入 `auditing-sqlsugar` 条目，`text` 用「审计日志 SqlSugar」。

- [x] **Step 4: 更新根 README 模块清单**

在 `README.md` 与 `README_cn.md` 的模块表格中，紧随 `XiHan.Framework.Auditing` 之后加入 `XiHan.Framework.Auditing.SqlSugar` 一行，描述用「审计日志 SqlSugar 持久化」。若表格顶部或徽章处写了模块总数（原为 66），一并 +1。

- [x] **Step 5: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 Warning(s) 0 Error(s)；全部测试通过。

- [x] **Step 6: 复查注释是否混入论证**

逐个通读本 PR 新增的 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [x] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar/README.md docs README.md README_cn.md
git commit -m "docs(auditing-sqlsugar): 补写包文档与模块清单"
```

---

## 完成标准

PR1 可提交时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 5 个写入器全部经 `services.Replace` 顶替 `Null*Writer`，有测试断言
- 日志经写入器落到当月分表，主键非零
- 包 README、`docs/packages/auditing-sqlsugar.md`、VitePress 侧边栏、根 README 均已更新
- 新增代码的注释只说明代码做什么

## 已知边界（写入 PR 描述，不写进代码注释）

- **逐条插入**：`IXxxLogWriter.WriteAsync` 是单条契约，队列 Worker 批量取出后仍逐条调用（见 `OperationLogQueueWorker.FlushAsync`）。高吞吐场景下这是 N 次往返。批量写入需要给写入器契约加批量重载，属于 `XiHan.Framework.Auditing` 主包的改动，不在本 PR 范围。
- **分表查询**：跨月查询需显式 `SplitTable(begin, end)`，仓储层未暴露该能力，属框架既有缺口。
- **审计写入不参与业务事务**：有意设计，日志由后台 Worker 在业务请求之外消费，不应因业务回滚而丢失。

## 本机验收记录（2026-09-21，Windows / SDK 10.0.112）

- **构建**：本包两个项目单独重建为 **0 警告 0 错误**。全解决方案构建 0 错误，**无任何 CS/NU/分析器警告**；警告全部是 MSB3026 复制重试，占用方指向 `XiHan.Framework.EventBus.SqlSugar.Tests` 与 `Workflow.Tests` 的 bin——即另一会话在同一 worktree 并发构建与跑测试所致。断线前一次无并发的全量构建为 0 警告 0 错误（137 个输出）。**开 PR 前请在另一会话停止后复跑一次，确认 0 警告。**
- **测试**：`XiHan.Framework.Auditing.SqlSugar.Tests` 31 通过 / 0 失败。全量 11380 例中 1 失败 + `Utils.Tests` 整项目异常退出，两处都在本 PR 零改动的包：
  - `Script.Tests` 内存用例两次红的不是同一个（`MemoryUsageTests.cs:55` 与 `:77`），属既有随机失败。仓库先例：`452dc323 test(script): 修掉 GC 计数用例的随机失败`、`c6fe01dd test: 兼容零托管堆内存读数`
  - `Utils.Tests` 退出码 -1，MTP 报「收到测试会话开始事件，但没有对应的会话结束」；单跑同样崩在 96 线程压力阶段（该用例族约写 2.6 GB）
  - 判定为既有环境问题，**按决定不修**，写进 PR 描述
- **与计划的两处偏差**（已核实为计划的错误假设，不是实现遗漏）：
  - Task 4 Step 4 要「在根 README 的模块表格加一行」：根 `README.md` / `README_cn.md` 没有逐包表格，只有指向 `framework/README.md#module-catalog` 的入口。实际改动是模块总数 66→67（含徽章与文档站文案），清单行加在 `framework/README.md` 与 `framework/README_cn.md`
  - Task 4 Step 3 要侧边栏 `text` 用「审计日志 SqlSugar」：与同组条目的 `<包名> <中文简述>` 格式、以及同类子包先例（`EventBus.RabbitMQ`、`Bot.Telegram` 用裸包名）都不符，最终用 `pkg("Auditing.SqlSugar", "auditing-sqlsugar")`
- **Task 4 Step 6 复查结论**：17 个 `.cs` 的版权头、file-scoped namespace、LF 无 BOM、五个 Writer 逐字同构全部通过。发现并修正 18 处：9 处 `<summary>` 与中文用例名逐字重复（既有惯例是中文用例名不附 summary，见 `Data.Tests/ModuleDataSourceRoutingTests.cs`）、4 处表达式体私有方法（撞 `framework/.editorconfig:106`）、5 个 Writer 的 `cancellationToken` 文档未写明「仅在写入前检查、不传递给数据库」这一边界。修正提交 `d01c5b64`、`278ce7c9`

## 下一份计划（P3，本计划完成后再写）

`XiHan.Framework.EventBus.SqlSugar` 的包骨架与发件箱实体、`OutgoingEventInfo` 双向映射（含 `DateTime` → `DateTimeOffset` 转换与 `ExtraProperties` 的 JSON 列）。
