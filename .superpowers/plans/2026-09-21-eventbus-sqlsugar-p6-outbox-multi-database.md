# EventBus.SqlSugar 发件箱多库（P6）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让发件箱用上 P5 补齐的两个能力：事件行落在业务所在的库，发送端遍历当前布局的全部库。

**Architecture:** 建表靠 `[TableInitialization(IncludeModuleConnections = true)]` 让 `sys_event_outbox` 进入每个模块库。入箱从 `GetEnlistedConfigIds()` 推导落点。领取把现有单库逻辑整体下沉为一个私有方法，外层按库遍历、平均分配配额、逐库隔离故障。删除向每个库各发一次按主键删除。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-21-eventbus-sqlsugar-p6-outbox-multi-database-design.md`

> 该 spec 自成一体，实现 P6 所需的全部约束都在其中。**不要**去读 `2026-09-21-sqlsugar-persistence-design.md`——那是拆分前的总纲，已停用。

**前置:** P4（`.superpowers/plans/2026-09-21-eventbus-sqlsugar-p4-outbox-write-claim.md`）与 P5（`.superpowers/plans/2026-09-21-data-all-databases-capability-p5.md`）必须已完成。本计划依赖 P4 的 `SqlSugarEventOutbox` 与 P5 的 `TableInitializationAttribute.IncludeModuleConnections`、`ISqlSugarClientResolver.GetEnlistedConfigIds()`。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-sqlsugar`）。worktree 内看不到这些文件，请按上面的绝对路径读取。

> **动手前先看 `GetWaitingEventsAsync` 的当前实现。** 本计划 Task 3 的做法是把**整个单库领取逻辑原样下沉**为私有方法 `ClaimFromDatabaseAsync`，不改动它内部的任何一行。若该方法此刻已包含「候选被抢光则另选一批重试」的循环，连同循环一起下沉即可——外层遍历与配额分配不受影响。

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
| `Migrations` / `Add-Migration` / `EnsureCreated()` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar
IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
IUpdateable<T>.ExecuteCommandAsync(CancellationToken token)
IDeleteable<T>.In<PkType>(List<PkType> primaryKeyValues)
IDeleteable<T>.ExecuteCommandAsync(CancellationToken token)
ISugarQueryable<T>.OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)
ISugarQueryable<T>.Take(int num)
ISugarQueryable<T>.Select<TResult>(Expression<Func<T, TResult>> expression)
ISugarQueryable<T>.ToListAsync(CancellationToken token)

// 框架（P5 产出，位于 XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver）
ISqlSugarClient GetCurrentClient()
ISqlSugarClient GetClient(string configId)          // 内部会调 EnlistCurrentUnitOfWork
IReadOnlyList<string> GetEnlistedConfigIds()        // 默认实现返回 []
IReadOnlyList<string> GetCurrentLayoutConfigIds()   // 主库在前，其下已建连的模块库在后
IReadOnlyCollection<string> GetAllConfigIds()
IEnumerable<ISqlSugarClient> GetAllClients()
ITenant AsTenant()
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台限制**：Microsoft.Testing.Platform，不是 VSTest。**没有可用的筛选参数**，`--filter`/`--list-tests` 返回退出码 3；**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。要跑单个测试类就整个项目跑。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**提交信息**：中文 Conventional Commits，作用域 `eventbus-sqlsugar`。**不加任何 AI 署名。**

---

## 本计划特有的三条硬约束

**① 建表标注与落点推导必须一起改，缺一都不成立。**

- 只改落点、没加 `IncludeModuleConnections`：模块库里没有 `sys_event_outbox`，入箱直接报表不存在
- 只加标注、没改落点：表建出来了但永远是空的，事件仍全部落在主库

**P4 的既有测试两种情况都发现不了**——它们的桩解析器只有一个库。Task 1 与 Task 2 因此必须都做完才算一个完整状态。

**② 抢占的 `Where` 在下沉时不许简化。**

条件必须始终是「主键在候选集内 **且**（待发送 **或** 领取已超时）」。只按主键更新令牌会让两个并发领取者互相覆盖，**同一事件被投递两次**。Task 3 的做法是整段原样搬进私有方法，**一行都不改**。

**③ 遍历时必须用 `GetClient(configId)`。**

用 `GetCurrentClient()` 或 `GetClientForEntity<SysEventOutbox>()` 会让每一轮都作用在同一个主库上，循环空转 N 次，表面上一切正常、测试也可能碰巧通过。

---

## File Structure

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  Entities/SysEventOutbox.cs        修改：加建表标注
  Outbox/SqlSugarEventOutbox.cs     修改：三个方法多库化，新增 ILogger 与两个私有方法
  README.md                         修改：核心能力与已知边界

framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxTestContext.cs              重写：多库夹具 + 可编程桩解析器
  EventOutboxEntityTests.cs         追加：建表标注断言
  OutboxEnqueueTests.cs             追加：落点推导三个用例
  OutboxClaimTests.cs               追加：遍历、配额、故障隔离、取消四个用例
  OutboxConcurrencyTests.cs         修改：构造函数签名同步
```

---

### Task 1: 实体进入所有库

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventOutboxEntityTests.cs`

**Interfaces:**
- Consumes: P5 的 `XiHan.Framework.Data.SqlSugar.Initializers.TableInitializationAttribute`（属性 `bool IncludeModuleConnections { get; set; }`，默认 `false`）
- Produces: `SysEventOutbox` 带 `[TableInitialization(IncludeModuleConnections = true)]`，后续任务据此假定每个模块库都有 `sys_event_outbox` 表

**参考来源（动手前先读）：**
- 属性定义：`framework/src/XiHan.Framework.Data/SqlSugar/Initializers/TableInitializationAttribute.cs`
- 放行判定：`framework/src/XiHan.Framework.Data/SqlSugar/Initializers/DbEntityTypeProvider.cs` 的 `IsModuleDataSourceAllowed`
- 文档：`docs/packages/data.md`（P5 已补写实体进入所有库的声明方式）

**本任务禁止事项：** 不要加 `[SplitTable]`——发件箱不分表。不要动 `[SugarTable]` 的表名。不要给其他实体加这个标注。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventOutboxEntityTests.cs` 的 `主键经构造函数传入` 用例**之前**插入：

```csharp
    /// <summary>
    /// 发件箱在模块库也建表
    /// </summary>
    [Fact]
    public void 发件箱在模块库也建表()
    {
        var attribute = typeof(SysEventOutbox).GetCustomAttribute<TableInitializationAttribute>(inherit: true);

        Assert.NotNull(attribute);
        Assert.True(attribute.IncludeModuleConnections);
    }
```

并在该文件的 using 区追加：

```csharp
using XiHan.Framework.Data.SqlSugar.Initializers;
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`发件箱在模块库也建表` 失败于 `Assert.NotNull`（实体还没有该标注）。

- [ ] **Step 3: 给实体加标注**

修改 `framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`。

using 区追加：

```csharp
using XiHan.Framework.Data.SqlSugar.Initializers;
```

类声明由：

```csharp
[SugarTable("sys_event_outbox")]
public class SysEventOutbox : SugarEntity<Guid>
```

改为：

```csharp
[SugarTable("sys_event_outbox")]
[TableInitialization(IncludeModuleConnections = true)]
public class SysEventOutbox : SugarEntity<Guid>
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 发件箱表在模块库也创建"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: 多库测试夹具与入箱落点

**Files:**
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`（整份重写）
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxEnqueueTests.cs`
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysEventOutbox`
- Produces:
  - `OutboxTestContext(TimeSpan? claimTimeout = null, bool withModuleDatabase = false)`；常量 `MainConfigId = "Default"`、`ModuleConfigId = "Default_Shop"`；属性 `Client`（主库）、`ModuleClient`（模块库）、`Resolver`、`Outbox`
  - `StubClientResolver`，属性 `List<string> EnlistedConfigIds`（用例可写，驱动落点推导）、`Dictionary<string, Exception> FaultyConfigIds`（用例可写，模拟库不可达）
  - `SqlSugarEventOutbox` 新增私有方法 `ISqlSugarClient ResolveEnqueueClient()`

**参考来源（动手前先读）：**
- 待实现的接口：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`
- P4 的既有夹具：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`
- 落点语义：spec 第 4.2 节

**本任务禁止事项：** 硬约束 ①③。另外**不要**在 `EnqueueAsync` 里开事务。**不要**保留对 `GetClientForEntity<SysEventOutbox>()` 的调用——它永远解析到主库，是本份要修掉的行为。`GetWaitingEventsAsync` 与两个删除方法本任务**不动**，留给 Task 3、Task 4。

- [ ] **Step 1: 重写测试夹具**

把 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱测试夹具，提供一个或两个临时 SQLite 库与发件箱实例
/// </summary>
internal sealed class OutboxTestContext : IDisposable
{
    /// <summary>
    /// 主库的连接配置标识
    /// </summary>
    public const string MainConfigId = "Default";

    /// <summary>
    /// 模块库的连接配置标识
    /// </summary>
    public const string ModuleConfigId = "Default_Shop";

    private readonly List<string> _databaseFiles = [];

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="claimTimeout">领取超时</param>
    /// <param name="withModuleDatabase">是否额外创建一个模块库</param>
    public OutboxTestContext(TimeSpan? claimTimeout = null, bool withModuleDatabase = false)
    {
        List<string> configIds = withModuleDatabase
            ? [MainConfigId, ModuleConfigId]
            : [MainConfigId];

        foreach (var configId in configIds)
        {
            var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_outbox_{Guid.NewGuid():N}.db");
            _databaseFiles.Add(databaseFile);

            var client = new SqlSugarClient(new ConnectionConfig
            {
                // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
                ConnectionString = $"DataSource={databaseFile};Pooling=False",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

            client.CodeFirst.InitTables(typeof(SysEventOutbox));
            Clients[configId] = client;
        }

        Resolver = new StubClientResolver(Clients, configIds, MainConfigId);

        Outbox = new SqlSugarEventOutbox(
            Resolver,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5)
            }));
    }

    /// <summary>
    /// 各库的客户端
    /// </summary>
    public Dictionary<string, SqlSugarClient> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 可编程的客户端解析器
    /// </summary>
    public StubClientResolver Resolver { get; }

    /// <summary>
    /// 被测发件箱
    /// </summary>
    public SqlSugarEventOutbox Outbox { get; }

    /// <summary>
    /// 主库客户端
    /// </summary>
    public SqlSugarClient Client
    {
        get { return Clients[MainConfigId]; }
    }

    /// <summary>
    /// 模块库客户端
    /// </summary>
    public SqlSugarClient ModuleClient
    {
        get { return Clients[ModuleConfigId]; }
    }

    /// <summary>
    /// 释放客户端并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        foreach (var client in Clients.Values)
        {
            client.Dispose();
        }

        foreach (var databaseFile in _databaseFiles)
        {
            if (File.Exists(databaseFile))
            {
                File.Delete(databaseFile);
            }
        }
    }
}

/// <summary>
/// 测试用客户端解析器，按连接配置标识返回对应客户端
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
{
    private readonly IReadOnlyDictionary<string, SqlSugarClient> _clients;
    private readonly IReadOnlyList<string> _configIds;
    private readonly string _currentConfigId;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clients">各库的客户端</param>
    /// <param name="configIds">连接配置标识，主库在前</param>
    /// <param name="currentConfigId">当前库的连接配置标识</param>
    public StubClientResolver(
        IReadOnlyDictionary<string, SqlSugarClient> clients,
        IReadOnlyList<string> configIds,
        string currentConfigId)
    {
        _clients = clients;
        _configIds = configIds;
        _currentConfigId = currentConfigId;
    }

    /// <summary>
    /// 当前工作单元已登记的连接配置标识，用例可直接增删
    /// </summary>
    public List<string> EnlistedConfigIds { get; } = [];

    /// <summary>
    /// 取客户端时抛出的异常，用例据此模拟库不可达
    /// </summary>
    public Dictionary<string, Exception> FaultyConfigIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取当前客户端
    /// </summary>
    /// <returns>当前库的客户端</returns>
    public ISqlSugarClient GetCurrentClient()
    {
        return GetClient(_currentConfigId);
    }

    /// <summary>
    /// 获取实体对应的客户端
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>当前库的客户端</returns>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        return GetClient(_currentConfigId);
    }

    /// <summary>
    /// 按连接配置标识获取客户端
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    /// <returns>该库的客户端</returns>
    public ISqlSugarClient GetClient(string configId)
    {
        if (FaultyConfigIds.TryGetValue(configId, out var error))
        {
            throw error;
        }

        return _clients[configId];
    }

    /// <summary>
    /// 获取当前工作单元已登记的连接配置标识
    /// </summary>
    /// <returns>已登记的连接配置标识</returns>
    public IReadOnlyList<string> GetEnlistedConfigIds()
    {
        return EnlistedConfigIds;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识集合</returns>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return _configIds;
    }

    /// <summary>
    /// 获取当前布局的全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识集合</returns>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return _configIds;
    }

    /// <summary>
    /// 获取所有库的客户端
    /// </summary>
    /// <returns>客户端集合</returns>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return _clients.Values;
    }

    /// <summary>
    /// 获取底层多租户接口
    /// </summary>
    /// <returns>不返回，始终抛出</returns>
    /// <exception cref="NotSupportedException">始终抛出</exception>
    public ITenant AsTenant()
    {
        throw new NotSupportedException("测试桩不支持多租户切换。");
    }
}
```

- [ ] **Step 2: 写失败的测试**

在 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxEnqueueTests.cs` 的 `private static OutgoingEventInfo NewEvent()` **之前**插入三个用例：

```csharp
    /// <summary>
    /// 已登记单个模块库时事件写进该库
    /// </summary>
    [Fact]
    public async Task 已登记单个模块库时事件写进该库()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        var info = NewEvent();

        await context.Outbox.EnqueueAsync(info);

        Assert.Equal(1, await context.ModuleClient.Queryable<SysEventOutbox>().CountAsync());
        Assert.Equal(0, await context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 未登记任何连接时事件写进当前库
    /// </summary>
    [Fact]
    public async Task 未登记任何连接时事件写进当前库()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var info = NewEvent();

        await context.Outbox.EnqueueAsync(info);

        Assert.Equal(1, await context.Client.Queryable<SysEventOutbox>().CountAsync());
        Assert.Equal(0, await context.ModuleClient.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 登记了多个连接时抛出无法确定落点
    /// </summary>
    [Fact]
    public async Task 登记了多个连接时抛出无法确定落点()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.MainConfigId);
        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Outbox.EnqueueAsync(NewEvent()));
    }
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`已登记单个模块库时事件写进该库` 与 `登记了多个连接时抛出无法确定落点` 失败——当前实现走 `GetClientForEntity`，永远写主库、也不会抛。

- [ ] **Step 4: 改写入箱落点**

修改 `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`。

`EnqueueAsync` 的方法体由：

```csharp
        ArgumentNullException.ThrowIfNull(outgoingEvent);

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Insertable(EventOutboxMapper.ToEntity(outgoingEvent)).ExecuteCommandAsync();
```

改为：

```csharp
        ArgumentNullException.ThrowIfNull(outgoingEvent);

        var client = ResolveEnqueueClient();

        await client.Insertable(EventOutboxMapper.ToEntity(outgoingEvent)).ExecuteCommandAsync();
```

并在 `EnqueueAsync` 之后、`GetWaitingEventsAsync` 之前插入私有方法：

```csharp
    /// <summary>
    /// 解析入箱写入的客户端
    /// </summary>
    /// <remarks>
    /// 当前工作单元已登记恰好一个连接时写该库，未登记任何连接时写当前库。
    /// </remarks>
    /// <returns>事件行写入的客户端</returns>
    /// <exception cref="InvalidOperationException">当前工作单元登记了多个连接</exception>
    private ISqlSugarClient ResolveEnqueueClient()
    {
        var enlistedConfigIds = _clientResolver.GetEnlistedConfigIds();

        if (enlistedConfigIds.Count == 0)
        {
            return _clientResolver.GetCurrentClient();
        }

        if (enlistedConfigIds.Count == 1)
        {
            return _clientResolver.GetClient(enlistedConfigIds[0]);
        }

        throw new InvalidOperationException(
            $"当前工作单元登记了多个数据库连接（{string.Join("、", enlistedConfigIds)}），无法确定事件应写入哪个库。" +
            "请拆分工作单元，使每个事务只写入一个库。");
    }
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。P4 的既有用例在只有主库的夹具上应保持全绿——`EnlistedConfigIds` 默认为空，落点退回当前库，行为与 P4 一致。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 入箱按工作单元已登记的连接决定落库"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 领取遍历全部库

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxClaimTests.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `OutboxTestContext`、`StubClientResolver`
- Produces:
  - `SqlSugarEventOutbox` 构造函数变为 `(ISqlSugarClientResolver clientResolver, IOptions<XiHanSqlSugarEventBoxOptions> options, ILogger<SqlSugarEventOutbox> logger)`
  - 新增私有方法 `Task<List<SysEventOutbox>> ClaimFromDatabaseAsync(ISqlSugarClient client, int quota, string claimToken, CancellationToken cancellationToken)`

**参考来源（动手前先读）：**
- `GetWaitingEventsAsync` 的当前实现（要原样下沉的那一段）
- 遍历与配额语义：spec 第 4.3 节
- 宿主发送循环：`framework/src/XiHan.Framework.EventBus/Distributed/EventBoxOutboxSenderHostedService.cs` 的 `SendWaitingEventsAsync`

**本任务禁止事项：** 硬约束 ②③。另外**不要**吞掉 `OperationCanceledException`。**不要**改 `EventBoxOutboxSenderHostedService`。**不要**使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP` 或任何方言相关 SQL。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxClaimTests.cs` 的 `private static OutgoingEventInfo NewEvent()` **之前**插入四个用例：

```csharp
    /// <summary>
    /// 两个库的待发记录都会被领到
    /// </summary>
    [Fact]
    public async Task 两个库的待发记录都会被领到()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var mainEvent = NewEvent();
        var moduleEvent = NewEvent();

        await context.Outbox.EnqueueAsync(mainEvent);

        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        await context.Outbox.EnqueueAsync(moduleEvent);
        context.Resolver.EnlistedConfigIds.Clear();

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Equal(2, claimed.Count);
        Assert.Contains(claimed, item => item.Id == mainEvent.Id);
        Assert.Contains(claimed, item => item.Id == moduleEvent.Id);
    }

    /// <summary>
    /// 单批总量不超过上限且配额按库平均分配
    /// </summary>
    [Fact]
    public async Task 单批总量不超过上限且配额按库平均分配()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        for (var index = 0; index < 10; index++)
        {
            await context.Outbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], baseTime.AddSeconds(index)));
        }

        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        for (var index = 0; index < 10; index++)
        {
            await context.Outbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [2], baseTime.AddSeconds(index)));
        }
        context.Resolver.EnlistedConfigIds.Clear();

        var claimed = await context.Outbox.GetWaitingEventsAsync(4);

        Assert.Equal(4, claimed.Count);
        Assert.Equal(2, claimed.Count(item => item.EventData[0] == 1));
        Assert.Equal(2, claimed.Count(item => item.EventData[0] == 2));
    }

    /// <summary>
    /// 单个库不可达时其余库照常领取
    /// </summary>
    [Fact]
    public async Task 单个库不可达时其余库照常领取()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var mainEvent = NewEvent();
        await context.Outbox.EnqueueAsync(mainEvent);

        context.Resolver.FaultyConfigIds[OutboxTestContext.ModuleConfigId] =
            new InvalidOperationException("模拟模块库不可达。");

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(mainEvent.Id, claimed[0].Id);
    }

    /// <summary>
    /// 已取消的令牌抛出取消异常
    /// </summary>
    [Fact]
    public async Task 已取消的令牌抛出取消异常()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        await context.Outbox.EnqueueAsync(NewEvent());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Outbox.GetWaitingEventsAsync(10, cancellationToken: cancellation.Token));
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`两个库的待发记录都会被领到`、`单批总量不超过上限且配额按库平均分配`、`单个库不可达时其余库照常领取` 三个失败——当前实现只看主库。

- [ ] **Step 3: 注入日志器**

修改 `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`。

using 区追加：

```csharp
using Microsoft.Extensions.Logging;
```

字段区由：

```csharp
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly XiHanSqlSugarEventBoxOptions _options;
```

改为：

```csharp
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ILogger<SqlSugarEventOutbox> _logger;
    private readonly XiHanSqlSugarEventBoxOptions _options;
```

构造函数由：

```csharp
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="options">收发件箱存储配置</param>
    public SqlSugarEventOutbox(
        ISqlSugarClientResolver clientResolver,
        IOptions<XiHanSqlSugarEventBoxOptions> options)
    {
        _clientResolver = clientResolver;
        _options = options.Value;
    }
```

改为：

```csharp
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="options">收发件箱存储配置</param>
    /// <param name="logger">日志器</param>
    public SqlSugarEventOutbox(
        ISqlSugarClientResolver clientResolver,
        IOptions<XiHanSqlSugarEventBoxOptions> options,
        ILogger<SqlSugarEventOutbox> logger)
    {
        _clientResolver = clientResolver;
        _options = options.Value;
        _logger = logger;
    }
```

- [ ] **Step 4: 把单库领取逻辑下沉为私有方法**

把 `GetWaitingEventsAsync` 方法体中**从取客户端之后、到返回映射结果之前**的整段逻辑，原样搬进新的私有方法。该方法放在 `GetWaitingEventsAsync` 之后、`DeleteAsync` 之前：

```csharp
    /// <summary>
    /// 在指定库上领取一批待发送的记录
    /// </summary>
    /// <param name="client">该库的客户端</param>
    /// <param name="quota">本库最多领取的条数</param>
    /// <param name="claimToken">本次领取的令牌</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本库领取到的记录</returns>
    private async Task<List<SysEventOutbox>> ClaimFromDatabaseAsync(
        ISqlSugarClient client,
        int quota,
        string claimToken,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - _options.ClaimTimeout;

        var candidateIds = await client.Queryable<SysEventOutbox>()
            .Where(item => item.Status == SysEventOutbox.StatusPending
                || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore))
            .OrderBy(item => item.CreatedTime)
            .Take(quota)
            .Select(item => item.BasicId)
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return [];
        }

        await client.Updateable<SysEventOutbox>()
            .SetColumns(item => new SysEventOutbox
            {
                Status = SysEventOutbox.StatusClaimed,
                ClaimToken = claimToken,
                ClaimTime = now
            })
            .Where(item => candidateIds.Contains(item.BasicId)
                && (item.Status == SysEventOutbox.StatusPending
                    || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore)))
            .ExecuteCommandAsync(cancellationToken);

        return await client.Queryable<SysEventOutbox>()
            .Where(item => item.ClaimToken == claimToken)
            .OrderBy(item => item.CreatedTime)
            .ToListAsync(cancellationToken);
    }
```

> **若当前实现已含「候选被抢光则另选一批重试」的循环**，把那个循环连同它内部的三步一起搬进本方法，只把 `Take(maxCount)` 改成 `Take(quota)`、把返回值由 `List<OutgoingEventInfo>` 改成 `List<SysEventOutbox>`（映射交给外层）。抢占的 `Where` 条件一行都不要动。

- [ ] **Step 5: 改写 GetWaitingEventsAsync 为遍历**

把 `GetWaitingEventsAsync` 的方法体替换为（XML 文档注释保持原样，只在 `<remarks>` 末尾追加一句）：

```csharp
        if (filter is not null)
        {
            throw new NotSupportedException(
                "SqlSugar 发件箱暂不支持 filter 参数，请改为在消费端筛选。");
        }

        if (maxCount <= 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        var configIds = _clientResolver.GetCurrentLayoutConfigIds();
        if (configIds.Count == 0)
        {
            return [];
        }

        var quota = Math.Max(1, maxCount / configIds.Count);
        var claimToken = Guid.NewGuid().ToString("N");
        var claimed = new List<SysEventOutbox>();

        foreach (var configId in configIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var client = _clientResolver.GetClient(configId);

                claimed.AddRange(await ClaimFromDatabaseAsync(client, quota, claimToken, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从数据库 {ConfigId} 领取待发送事件失败，已跳过该库。", configId);
            }
        }

        return [.. claimed.OrderBy(item => item.CreatedTime).Select(EventOutboxMapper.ToEventInfo)];
```

`<remarks>` 末尾追加：

```csharp
    /// 领取会遍历当前布局的全部库，每库最多领取 <c>maxCount</c> 除以库数的条数；某个库不可达时记录日志并跳过。
```

- [ ] **Step 6: 同步两处构造调用**

在 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs` 的 using 区追加：

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

构造 `Outbox` 的那一段由：

```csharp
        Outbox = new SqlSugarEventOutbox(
            Resolver,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5)
            }));
```

改为：

```csharp
        Outbox = new SqlSugarEventOutbox(
            Resolver,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5)
            }),
            NullLogger<SqlSugarEventOutbox>.Instance);
```

在 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs` 的 using 区追加：

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

并把 `CreateOutbox` 整个方法替换为：

```csharp
    private static SqlSugarEventOutbox CreateOutbox(SqlSugarClient client)
    {
        Dictionary<string, SqlSugarClient> clients = new(StringComparer.Ordinal)
        {
            [OutboxTestContext.MainConfigId] = client
        };

        var resolver = new StubClientResolver(
            clients,
            [OutboxTestContext.MainConfigId],
            OutboxTestContext.MainConfigId);

        return new SqlSugarEventOutbox(
            resolver,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = TimeSpan.FromMinutes(5)
            }),
            NullLogger<SqlSugarEventOutbox>.Instance);
    }
```

参数类型由 `ISqlSugarClient` 收窄为 `SqlSugarClient`——两个调用点传的都是 `CreateClient()` 的返回值，本来就是该类型，收窄后不需要强制转换。该文件的 `using SqlSugar;` 已存在，`using XiHan.Framework.Data.SqlSugar.Clients;` 这一条因此不再需要，不要加。

- [ ] **Step 7: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（真库并发用例在未设 `XIHAN_TEST_MYSQL` 时显示为 skipped）。

若 `单批总量不超过上限且配额按库平均分配` 得到的两边条数不是各 2 条，检查 `quota` 是否写成了 `maxCount` 而非 `maxCount / configIds.Count`。

- [ ] **Step 8: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 领取遍历当前布局的全部库并按库分配配额"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 跨库删除

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxEnqueueTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `OutboxTestContext`、Task 3 的 `_logger`
- Produces: `DeleteAsync` 与 `DeleteManyAsync` 的多库实现；`DeleteAsync` 改为委托给 `DeleteManyAsync`

**参考来源（动手前先读）：**
- 删除语义：spec 第 4.4 节
- 删除调用点：`framework/src/XiHan.Framework.EventBus/Distributed/EventBoxOutboxSenderHostedService.cs` 的 `SendWaitingEventsAsync` 末尾

**本任务禁止事项：** **不要**为了少发几条 SQL 而在实例上缓存「记录属于哪个库」的映射——发件箱是 Scoped，跨作用域调用时映射是空的。**不要**给空集合发 SQL。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxEnqueueTests.cs` 的 `private static OutgoingEventInfo NewEvent()` **之前**插入：

```csharp
    /// <summary>
    /// 跨库批量删除两个库都清空
    /// </summary>
    [Fact]
    public async Task 跨库批量删除两个库都清空()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var mainEvent = NewEvent();
        var moduleEvent = NewEvent();

        await context.Outbox.EnqueueAsync(mainEvent);

        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        await context.Outbox.EnqueueAsync(moduleEvent);
        context.Resolver.EnlistedConfigIds.Clear();

        await context.Outbox.DeleteManyAsync([mainEvent.Id, moduleEvent.Id]);

        Assert.Equal(0, await context.Client.Queryable<SysEventOutbox>().CountAsync());
        Assert.Equal(0, await context.ModuleClient.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 按标识删除模块库中的记录
    /// </summary>
    [Fact]
    public async Task 按标识删除模块库中的记录()
    {
        using var context = new OutboxTestContext(withModuleDatabase: true);
        var moduleEvent = NewEvent();

        context.Resolver.EnlistedConfigIds.Add(OutboxTestContext.ModuleConfigId);
        await context.Outbox.EnqueueAsync(moduleEvent);
        context.Resolver.EnlistedConfigIds.Clear();

        await context.Outbox.DeleteAsync(moduleEvent.Id);

        Assert.Equal(0, await context.ModuleClient.Queryable<SysEventOutbox>().CountAsync());
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：两个新用例失败——当前删除只作用在主库，模块库的记录还在。

- [ ] **Step 3: 改写两个删除方法**

把 `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs` 中 `DeleteAsync` 与 `DeleteManyAsync` 两个方法整体替换为：

```csharp
    /// <summary>
    /// 删除指定的事件信息
    /// </summary>
    /// <param name="id">事件唯一标识符</param>
    public async Task DeleteAsync(Guid id)
    {
        await DeleteManyAsync([id]);
    }

    /// <summary>
    /// 批量删除事件信息
    /// </summary>
    /// <remarks>
    /// 删除会遍历当前布局的全部库；主键全局唯一，没有该记录的库上执行只删除 0 行。
    /// </remarks>
    /// <param name="ids">事件唯一标识符集合</param>
    public async Task DeleteManyAsync(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        foreach (var configId in _clientResolver.GetCurrentLayoutConfigIds())
        {
            try
            {
                var client = _clientResolver.GetClient(configId);

                await client.Deleteable<SysEventOutbox>().In(idList).ExecuteCommandAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从数据库 {ConfigId} 删除已投递事件失败，已跳过该库。", configId);
            }
        }
    }
```

逐库隔离与 Task 3 的领取一致：一个库不可达不应阻止其余库清理已投递的记录。未删掉的记录会在领取超时后被重新领取并再次投递，符合至少一次语义。

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，含 P4 的 `批量删除空集合不抛异常` 与 `批量删除生效`。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 删除遍历当前布局的全部库"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 5: 文档与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`

**Interfaces:**
- Consumes: 前四个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 已知边界清单：spec 第 7 节
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`

**本任务禁止事项：** **不要**顺手改 `docs/` 下的文件站条目或根 `README.md` 的模块清单——那是 P7 的收尾内容，一个 PR 只做一件事。**不要**把权衡论证写进代码注释。

- [ ] **Step 1: 更新包 README 的核心能力**

把「核心能力」一节中这一行：

```markdown
- 入箱与业务数据落在同一事务：业务回滚，事件随之消失
```

改为两行：

```markdown
- 入箱与业务数据落在同一事务、同一个库：业务写在哪个库，事件行就写在哪个库
- 发送端遍历当前布局的全部库，单个库不可达时只跳过该库
```

- [ ] **Step 2: 在配置与约定一节追加**

在「配置与约定」一节末尾追加：

```markdown
事件行的落库由当前工作单元已登记的连接决定：恰好一个时写该库，一个都没有时写当前库，多于一个时抛 `InvalidOperationException`。因此业务代码应**先写业务数据、后发布事件**——反过来会让事件落在主库而业务落在模块库，两者不在同一个事务里，且不会报错。

一次领取的总量不超过 `maxCount`，配额在当前布局的各库间平均分配。

发件箱表由 `[TableInitialization(IncludeModuleConnections = true)]` 声明进入所有库，主库与每个模块库都会建出 `sys_event_outbox`。

进程重启后、首次流量之前，模块库尚未建连，该库中的待发事件暂时不会被领取；首次访问该模块库后即恢复。

发送循环运行在无租户上下文的后台作用域，只遍历默认布局，租户独立库的发件箱不在本包范围。
```

- [ ] **Step 3: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过（真库测试在无环境变量时跳过）。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`），先结束残留的测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.Utils.Tests.exe"
```

- [ ] **Step 4: 本机真库验证**

准备一个可用的 MySQL 实例并设置环境变量，确认 P4 的并发用例在签名变更后仍然通过：

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`并发领取时记录不重复` PASS，领取的标识无重复、总数等于 200。

**若该测试失败并出现重复标识**，说明下沉时丢了抢占的可领取条件（硬约束 ②）——回到 Task 3 Step 4 核对，不要通过放宽断言来让它变绿。

- [ ] **Step 5: 注释复查**

通读本计划改动过的 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.EventBus.SqlSugar/README.md
git commit -m "docs(eventbus-sqlsugar): 补写多库落点与遍历的使用约定"
```

---

## 完成标准

P6 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真库测试在无环境变量时跳过）
- `SysEventOutbox` 带 `[TableInitialization(IncludeModuleConnections = true)]`
- 已登记恰好一个模块库时，事件行落在该模块库、不在主库
- 未登记任何连接时，事件行落在当前库，与 P4 行为一致
- 已登记多于一个连接时抛 `InvalidOperationException`
- 两个库各有待发记录时，一次领取两边都拿到
- `maxCount` 为 4、两个库各有 10 条时，单批恰好 4 条且两库各 2 条
- 一个库不可达时，另一个库的记录仍被领到
- 已取消的令牌传入时抛 `OperationCanceledException`
- 跨库批量删除后两个库都清空
- 本机配置 `XIHAN_TEST_MYSQL` 后并发测试通过，领取的标识无重复

## 已知边界（写入 PR 描述，不写进代码注释）

- **冷启动缺口**：`GetCurrentLayoutConfigIds()` 只列出已建连的模块库。`SqlSugarScope` 是单例、判定是进程级，因此模块库被任何请求碰过一次后即长期在列；但进程重启后、首次流量之前，模块库里的待发事件暂时领不到。
- **跨库事务歧义**：当前工作单元登记多于一个连接时抛异常，合法的跨库事务会被挡下，出路是拆小工作单元。
- **发布早于写入**：业务若先 `PublishAsync` 再写仓储，入箱那一刻已登记连接为空，事件落主库而业务落模块库——两个独立事务，且不报错。
- **单库故障吞异常**：不可达的库只记录错误日志并跳过，领取与删除都是如此。
- **租户独立库**：发送循环无租户上下文，只遍历默认布局。
- **单批吞吐**：每库配额为 `maxCount / 库数`，单库单轮吞吐降为 1/N；轮询是连续的，累计吞吐不受影响。
- **N 条删除**：每批删除向每个库各发一次 `DELETE`，其中至多一条有效，其余是空操作。
- **`filter` 未支持**：沿用 P4，非 `null` 抛 `NotSupportedException`。
- **至少一次投递**：消费端必须幂等。

## 下一份计划（P7，本计划完成后再写）

收件箱：`IEventInbox` 的 SqlSugar 实现、`sys_event_inbox` 实体与去重，以及 `EventBus.SqlSugar` 的文件站条目（`docs/packages/eventbus-sqlsugar.md`、`docs/.vitepress/config.ts` 侧边栏、根 `README.md` 与 `framework/README.md` 的模块清单），补齐后第二个 PR 方可提交。
