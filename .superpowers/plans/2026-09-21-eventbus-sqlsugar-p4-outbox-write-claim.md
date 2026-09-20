# EventBus.SqlSugar 发件箱写入与原子领取（P4）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 `IEventOutbox` 的 SqlSugar 版本并接管发件箱，交付两个保证：入箱与业务数据同事务；多实例不重复投递。

**Architecture:** 实现注册为 Scoped，经 `ISqlSugarClientResolver` 取工作单元作用域的客户端，使事件行与业务数据落在同一事务。领取用「先选候选、再条件抢占、最后按令牌取回」三步，全部方言无关。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-21-eventbus-sqlsugar-p4-outbox-write-claim-design.md`

> 该 spec 自成一体，实现 P4 所需的全部约束都在其中。**不要**去读 `2026-09-21-sqlsugar-persistence-design.md`——那是拆分前的总纲，已停用。

**前置:** P3（`.superpowers/plans/2026-09-21-eventbus-sqlsugar-p3-outbox-entity.md`）必须已完成——本计划依赖它产出的 `SysEventOutbox` 与 `EventOutboxMapper`。

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
| `Migrations` / `Add-Migration` / `EnsureCreated()` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

**已核对的 SqlSugar 签名**（不要凭印象改写）：

```csharp
IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
IUpdateable<T>.ExecuteCommandAsync(CancellationToken token)
IDeleteable<T>.In<PkType>(List<PkType> primaryKeyValues)
IDeleteable<T>.ExecuteCommandAsync(CancellationToken token)
ISugarQueryable<T>.OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)
ISugarQueryable<T>.Take(int num)
ISugarQueryable<T>.Select<TResult>(Expression<Func<T, TResult>> expression)
ISugarQueryable<T>.ToListAsync(CancellationToken token)
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

**测试平台限制**：Microsoft.Testing.Platform，不是 VSTest。**没有可用的筛选参数**，`--filter`/`--list-tests` 返回退出码 3；**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。

**提交信息**：中文 Conventional Commits，作用域 `eventbus-sqlsugar`。**不加任何 AI 署名。**

---

## 本计划特有的四条硬约束

**前两条错了不会报错、单元测试照样全绿**，故障只在生产的多实例环境出现。

**① `EnqueueAsync` 必须用工作单元作用域的客户端。**

`DistributedEventBusBase.AddToOutboxAsync` 经 `unitOfWork.ServiceProvider.GetRequiredService(...)` 解析本实现，因此本类注册为 **Scoped**，构造函数注入 `ISqlSugarClientResolver`，`EnqueueAsync` 内调 `GetClientForEntity<SysEventOutbox>()`——该解析器会把连接钉入当前工作单元的事务。

**绝不要**在 `EnqueueAsync` 内自建 `SqlSugarClient`、或用 `IServiceScopeFactory` 另开作用域。那样事件行落在另一条连接、另一个事务上：业务回滚时事件已独立提交，发出一个根本没发生的事件。**没有任何异常，单元测试也照样通过。**

**② 抢占的 `Where` 必须重复可领取条件。**

抢占步骤若只按主键更新令牌，两个并发领取者会先后覆盖同一行的令牌，双方的取回步骤都可能拿到它，**同一事件被投递两次**。

条件必须是「主键在候选集内 **且**（待发送 **或** 领取已超时）」。

**③ 注册的覆盖方向相反：options 后写覆盖，`TryAdd` 先写胜出。**

- `ImplementationType`：`XiHanEventBusModule` 只在 `== default` 时设为 `DefaultEventOutbox`。`IConfigureOptions` 按注册顺序执行，本模块依赖它、后注册、**后执行覆盖**，直接赋值即可
- DI：`XiHanEventBusModule` 已 `TryAddSingleton<IEventOutbox>(...)`，本模块再 `TryAdd` 是**空操作**，必须用 `Replace`

照着其中一个的直觉去做另一个必错。

**④ `GetWaitingEventsAsync` 实为「领取并返回」。**

调用后记录已被标记为已领取。该落差必须写入 XML 文档注释，否则后续维护者会以为它是纯查询而在别处复用。

---

## File Structure

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  XiHanSqlSugarEventBusModule.cs                  修改：调用注册扩展
  Options/XiHanSqlSugarEventBoxOptions.cs         新增：领取超时
  Outbox/SqlSugarEventOutbox.cs                   新增：IEventOutbox 实现
  Extensions/DependencyInjection/
    XiHanSqlSugarEventBusServiceCollectionExtensions.cs   新增

framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxTestContext.cs         测试夹具：SQLite 客户端 + 桩解析器
  OutboxEnqueueTests.cs        入箱与事务参与
  OutboxClaimTests.cs          领取、超时释放、删除、filter 守卫
  OutboxRegistrationTests.cs   注册断言
  OutboxConcurrencyTests.cs    真实数据库并发领取
```

---

### Task 1: 配置项、入箱与删除

**Files:**
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Options/XiHanSqlSugarEventBoxOptions.cs`
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxEnqueueTests.cs`

**Interfaces:**
- Consumes: P3 的 `SysEventOutbox`、`EventOutboxMapper`；框架的 `ISqlSugarClientResolver`
- Produces: `XiHanSqlSugarEventBoxOptions`（`SectionName = "XiHan:EventBus:SqlSugar"`，属性 `ClaimTimeout`，默认 5 分钟）；`SqlSugarEventOutbox : IEventOutbox`，构造函数 `(ISqlSugarClientResolver clientResolver, IOptions<XiHanSqlSugarEventBoxOptions> options)`；本任务实现 `EnqueueAsync` / `DeleteAsync` / `DeleteManyAsync`，`GetWaitingEventsAsync` 暂时抛出，Task 2 补齐

**参考来源（动手前先读）：**
- 契约：`framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/IEventOutbox.cs`
- 入箱调用点：`framework/src/XiHan.Framework.EventBus/Distributed/DistributedEventBusBase.cs:236-272`
- 客户端解析与事务钉入：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`、`SqlSugarClientResolver.cs:230-290`
- Options 范本：`framework/src/XiHan.Framework.EventBus/Distributed/EventBoxProcessingOptions.cs`
- 插入与删除：`/e/source/platfrom-admin/docs/SqlSugar-docs/插入數據.md`、`刪除數據.md`

**本任务禁止事项：** 硬约束 ①。另外不要在 `EnqueueAsync` 里开事务。不要给 `DeleteManyAsync` 的空集合发 SQL。

- [ ] **Step 1: 创建配置项**

`framework/src/XiHan.Framework.EventBus.SqlSugar/Options/XiHanSqlSugarEventBoxOptions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.EventBus.SqlSugar.Options;

/// <summary>
/// 事件收发件箱 SqlSugar 存储配置
/// </summary>
public class XiHanSqlSugarEventBoxOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:EventBus:SqlSugar";

    /// <summary>
    /// 领取超时，超过该时长仍未删除的已领取记录可被重新领取
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
```

- [ ] **Step 2: 创建测试夹具**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱测试夹具，提供临时 SQLite 库与发件箱实例
/// </summary>
internal sealed class OutboxTestContext : IDisposable
{
    private readonly string _databaseFile;

    public OutboxTestContext(TimeSpan? claimTimeout = null)
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_outbox_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_databaseFile}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        Client.CodeFirst.InitTables(typeof(SysEventOutbox));

        Outbox = new SqlSugarEventOutbox(
            new StubClientResolver(Client),
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5)
            }));
    }

    public SqlSugarClient Client { get; }

    public SqlSugarEventOutbox Outbox { get; }

    public void Dispose()
    {
        Client.Dispose();

        if (File.Exists(_databaseFile))
        {
            File.Delete(_databaseFile);
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

- [ ] **Step 3: 写失败的测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxEnqueueTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱入箱测试
/// </summary>
public class OutboxEnqueueTests
{
    /// <summary>
    /// 入箱后记录落库且状态为待发送
    /// </summary>
    [Fact]
    public async Task 入箱后记录落库且状态为待发送()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();

        await context.Outbox.EnqueueAsync(info);

        var stored = context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).First();

        Assert.NotNull(stored);
        Assert.Equal("Order.Created", stored.EventName);
        Assert.Equal(SysEventOutbox.StatusPending, stored.Status);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.ClaimTime);
    }

    /// <summary>
    /// 事务回滚后记录不落库
    /// </summary>
    [Fact]
    public async Task 事务回滚后记录不落库()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();

        context.Client.Ado.BeginTran();
        await context.Outbox.EnqueueAsync(info);
        context.Client.Ado.RollbackTran();

        var count = await context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).CountAsync();

        Assert.Equal(0, count);
    }

    /// <summary>
    /// 事务提交后记录落库
    /// </summary>
    [Fact]
    public async Task 事务提交后记录落库()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();

        context.Client.Ado.BeginTran();
        await context.Outbox.EnqueueAsync(info);
        context.Client.Ado.CommitTran();

        var count = await context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).CountAsync();

        Assert.Equal(1, count);
    }

    /// <summary>
    /// 按标识删除生效
    /// </summary>
    [Fact]
    public async Task 按标识删除生效()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();
        await context.Outbox.EnqueueAsync(info);

        await context.Outbox.DeleteAsync(info.Id);

        var count = await context.Client.Queryable<SysEventOutbox>().CountAsync();

        Assert.Equal(0, count);
    }

    /// <summary>
    /// 批量删除空集合不抛异常
    /// </summary>
    [Fact]
    public async Task 批量删除空集合不抛异常()
    {
        using var context = new OutboxTestContext();

        await context.Outbox.DeleteManyAsync([]);
    }

    /// <summary>
    /// 批量删除生效
    /// </summary>
    [Fact]
    public async Task 批量删除生效()
    {
        using var context = new OutboxTestContext();
        var first = NewEvent();
        var second = NewEvent();
        await context.Outbox.EnqueueAsync(first);
        await context.Outbox.EnqueueAsync(second);

        await context.Outbox.DeleteManyAsync([first.Id, second.Id]);

        var count = await context.Client.Queryable<SysEventOutbox>().CountAsync();

        Assert.Equal(0, count);
    }

    private static OutgoingEventInfo NewEvent()
    {
        return new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1, 2, 3], DateTime.UtcNow);
    }
}
```

- [ ] **Step 4: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarEventOutbox` 不存在。

- [ ] **Step 5: 实现发件箱（领取留到 Task 2）**

`framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Mapping;
using XiHan.Framework.EventBus.SqlSugar.Options;

namespace XiHan.Framework.EventBus.SqlSugar.Outbox;

/// <summary>
/// 发件箱的 SqlSugar 实现
/// </summary>
public class SqlSugarEventOutbox : IEventOutbox
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly XiHanSqlSugarEventBoxOptions _options;

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

    /// <summary>
    /// 将事件信息添加到发件箱
    /// </summary>
    /// <param name="outgoingEvent">出站事件信息</param>
    public async Task EnqueueAsync(OutgoingEventInfo outgoingEvent)
    {
        ArgumentNullException.ThrowIfNull(outgoingEvent);

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Insertable(EventOutboxMapper.ToEntity(outgoingEvent)).ExecuteCommandAsync();
    }

    /// <summary>
    /// 领取一批待发送的事件信息
    /// </summary>
    /// <remarks>
    /// 本方法在返回前会把记录标记为已领取，不是纯查询。
    /// 领取超时后记录可被重新领取，超时时长由 <see cref="XiHanSqlSugarEventBoxOptions.ClaimTimeout"/> 配置。
    /// </remarks>
    /// <param name="maxCount">最大数量</param>
    /// <param name="filter">过滤条件，本实现不支持，传入非空值将抛出异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本次领取到的事件信息</returns>
    /// <exception cref="NotSupportedException"><paramref name="filter"/> 不为空</exception>
    public Task<List<OutgoingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IOutgoingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 删除指定的事件信息
    /// </summary>
    /// <param name="id">事件唯一标识符</param>
    public async Task DeleteAsync(Guid id)
    {
        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Deleteable<SysEventOutbox>().In(id).ExecuteCommandAsync();
    }

    /// <summary>
    /// 批量删除事件信息
    /// </summary>
    /// <param name="ids">事件唯一标识符集合</param>
    public async Task DeleteManyAsync(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();

        await client.Deleteable<SysEventOutbox>().In(idList).ExecuteCommandAsync();
    }
}
```

`GetWaitingEventsAsync` 的 `NotImplementedException` 是本任务的**临时状态**，Task 2 补齐。本任务的测试不触碰它。

- [ ] **Step 6: 运行测试确认通过，验证构建，提交**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 新增发件箱入箱与删除"
```

---

### Task 2: 原子领取与超时释放

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxClaimTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SqlSugarEventOutbox` 与 `OutboxTestContext`
- Produces: `GetWaitingEventsAsync` 的完整实现

**参考来源（动手前先读）：**
- 宿主发送循环（确认它没有锁）：`framework/src/XiHan.Framework.EventBus/Distributed/EventBoxOutboxSenderHostedService.cs:85-110`
- 条件更新的既定写法：`framework/src/XiHan.Framework.Data/SqlSugar/Repository/SqlSugarRepositoryBase.cs:130-140`
- 并发与线程安全：`/e/source/platfrom-admin/docs/SqlSugar-docs/偶發性錯誤與執行緒安全.md`
- 条件更新：`/e/source/platfrom-admin/docs/SqlSugar-docs/更新數據.md`

**本任务禁止事项：** 硬约束 ②④。另外**不要**使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP` 或任何方言相关 SQL——本框架是通用类库。不要静默忽略 `filter`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxClaimTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱领取测试
/// </summary>
public class OutboxClaimTests
{
    /// <summary>
    /// 领取后记录被标记为已领取并带令牌
    /// </summary>
    [Fact]
    public async Task 领取后记录被标记并带令牌()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();
        await context.Outbox.EnqueueAsync(info);

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);

        var stored = context.Client.Queryable<SysEventOutbox>().Where(item => item.BasicId == info.Id).First();

        Assert.Equal(SysEventOutbox.StatusClaimed, stored.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.ClaimToken));
        Assert.NotNull(stored.ClaimTime);
    }

    /// <summary>
    /// 已领取的记录不会被再次领取
    /// </summary>
    [Fact]
    public async Task 已领取的记录不会被再次领取()
    {
        using var context = new OutboxTestContext();
        await context.Outbox.EnqueueAsync(NewEvent());

        var first = await context.Outbox.GetWaitingEventsAsync(10);
        var second = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(first);
        Assert.Empty(second);
    }

    /// <summary>
    /// 领取超时后记录可被重新领取
    /// </summary>
    [Fact]
    public async Task 领取超时后可被重新领取()
    {
        using var context = new OutboxTestContext(TimeSpan.FromMinutes(5));
        var info = NewEvent();
        await context.Outbox.EnqueueAsync(info);

        await context.Outbox.GetWaitingEventsAsync(10);

        context.Client.Updateable<SysEventOutbox>()
            .SetColumns(item => new SysEventOutbox { ClaimTime = DateTimeOffset.UtcNow.AddHours(-1) })
            .Where(item => item.BasicId == info.Id)
            .ExecuteCommand();

        var reclaimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(reclaimed);
        Assert.Equal(info.Id, reclaimed[0].Id);
    }

    /// <summary>
    /// 领取数量不超过上限且按创建时间升序
    /// </summary>
    [Fact]
    public async Task 领取数量不超过上限且按创建时间升序()
    {
        using var context = new OutboxTestContext();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        for (var index = 0; index < 5; index++)
        {
            await context.Outbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [(byte)index], baseTime.AddMinutes(index)));
        }

        var claimed = await context.Outbox.GetWaitingEventsAsync(3);

        Assert.Equal(3, claimed.Count);
        Assert.Equal([0, 1, 2], claimed.Select(item => item.EventData[0]).ToArray());
    }

    /// <summary>
    /// 传入过滤条件时明确抛出不支持
    /// </summary>
    [Fact]
    public async Task 传入过滤条件时抛出不支持()
    {
        using var context = new OutboxTestContext();
        Expression<Func<IOutgoingEventInfo, bool>> filter = item => item.EventName == "Order.Created";

        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Outbox.GetWaitingEventsAsync(10, filter));
    }

    /// <summary>
    /// 领取回的事件信息保真
    /// </summary>
    [Fact]
    public async Task 领取回的事件信息保真()
    {
        using var context = new OutboxTestContext();
        var info = NewEvent();
        info.SetCorrelationId("corr-claim");
        await context.Outbox.EnqueueAsync(info);

        var claimed = await context.Outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);
        Assert.Equal("Order.Created", claimed[0].EventName);
        Assert.Equal(info.EventData, claimed[0].EventData);
        Assert.Equal("corr-claim", claimed[0].GetCorrelationId());
    }

    private static OutgoingEventInfo NewEvent()
    {
        return new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1, 2, 3], DateTime.UtcNow);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`NotImplementedException`。

- [ ] **Step 3: 实现原子领取**

把 `SqlSugarEventOutbox.GetWaitingEventsAsync` 的方法体替换为（XML 文档注释保持 Task 1 写好的那份）：

```csharp
    public async Task<List<OutgoingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IOutgoingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
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

        var client = _clientResolver.GetClientForEntity<SysEventOutbox>();
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - _options.ClaimTimeout;
        var claimToken = Guid.NewGuid().ToString("N");

        var candidateIds = await client.Queryable<SysEventOutbox>()
            .Where(item => item.Status == SysEventOutbox.StatusPending
                || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore))
            .OrderBy(item => item.CreatedTime)
            .Take(maxCount)
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

        var claimed = await client.Queryable<SysEventOutbox>()
            .Where(item => item.ClaimToken == claimToken)
            .OrderBy(item => item.CreatedTime)
            .ToListAsync(cancellationToken);

        return [.. claimed.Select(EventOutboxMapper.ToEventInfo)];
    }
```

抢占语句的 `Where` **必须**同时包含主键集合与可领取条件——只按主键更新会让并发领取者互相覆盖令牌，双方都以为领到了（硬约束 ②）。

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `candidateIds.Contains(...)` 无法被 SqlSugar 翻译，改用 `.In(item => item.BasicId, candidateIds)`——以 `/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Interface/IUpdateable.cs` 的实际签名为准，不要改成逐条更新（那会失去原子性）。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 发件箱领取改为条件抢占并支持超时释放"
```

---

### Task 3: 注册与接管

**Files:**
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 1、Task 2 的 `SqlSugarEventOutbox`
- Produces: 扩展方法 `IServiceCollection AddXiHanSqlSugarEventBus(this IServiceCollection services, IConfiguration configuration)`

**参考来源（动手前先读）：**
- 默认注册与覆盖点：`framework/src/XiHan.Framework.EventBus/Extensions/DependencyInjection/XiHanEventBusServiceCollectionExtensions.cs:76-120`
- 发件箱配置字典：`framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/OutboxConfigDictionary.cs`（不带名称的 `Configure` 作用于 `"Default"`）
- 模块装配范本：`framework/src/XiHan.Framework.EventBus.Kafka/XiHanKafkaEventBusModule.cs`

**本任务禁止事项：** 硬约束 ③。**不要**用 `TryAdd` 去顶替 `IEventOutbox`，那是空操作。**不要**试图用「替换」写法去设置 `ImplementationType`，直接赋值即可。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxRegistrationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱注册测试
/// </summary>
public class OutboxRegistrationTests
{
    /// <summary>
    /// 发件箱实现类型被顶替
    /// </summary>
    [Fact]
    public void 发件箱实现类型被顶替()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<XiHanDistributedEventBusOptions>>().Value;

        Assert.Equal(typeof(SqlSugarEventOutbox), options.Outboxes["Default"].ImplementationType);
    }

    /// <summary>
    /// 发件箱接口注册被顶替
    /// </summary>
    [Fact]
    public void 发件箱接口注册被顶替()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IEventOutbox));

        Assert.Equal(typeof(SqlSugarEventOutbox), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 模拟事件总线模块已注册的默认值
    /// </summary>
    private static ServiceCollection BuildServicesWithDefaults()
    {
        var services = new ServiceCollection();

        services.Configure<XiHanDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config =>
            {
                if (config.ImplementationType == default)
                {
                    config.ImplementationType = typeof(DefaultEventOutbox);
                }
            });
        });

        services.TryAddSingleton<DefaultEventOutbox>();
        services.TryAddSingleton<IEventOutbox>(sp => sp.GetRequiredService<DefaultEventOutbox>());

        return services;
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`AddXiHanSqlSugarEventBus` 不存在。

- [ ] **Step 3: 实现注册扩展**

`framework/src/XiHan.Framework.EventBus.SqlSugar/Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 事件总线 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanSqlSugarEventBusServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 发件箱替换默认的进程内发件箱
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanSqlSugarEventBus(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanSqlSugarEventBoxOptions>(
            configuration.GetSection(XiHanSqlSugarEventBoxOptions.SectionName));

        services.Configure<XiHanDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config => config.ImplementationType = typeof(SqlSugarEventOutbox));
        });

        services.TryAddScoped<SqlSugarEventOutbox>();
        services.Replace(ServiceDescriptor.Scoped<IEventOutbox, SqlSugarEventOutbox>());

        return services;
    }
}
```

- [ ] **Step 4: 在模块里调用扩展**

把 `XiHanSqlSugarEventBusModule.cs` 的类体改为：

```csharp
public class XiHanSqlSugarEventBusModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddXiHanSqlSugarEventBus(services.GetConfiguration());
    }
}
```

文件顶部补 `using XiHan.Framework.Core.Extensions.DependencyInjection;`（`GetConfiguration` 的所在）与 `using XiHan.Framework.EventBus.SqlSugar.Extensions.DependencyInjection;`。

- [ ] **Step 5: 运行测试，验证构建并提交**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 注册 SqlSugar 发件箱并顶替进程内实现"
```

---

### Task 4: 真实数据库并发领取

**Files:**
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`

**Interfaces:**
- Consumes: 前三个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 跳过范式：`framework/test/XiHan.Framework.EventBus.Redis.Tests/RedisPendingBehaviorTests.cs`（读 `XIHAN_TEST_REDIS`，用 `Assert.SkipWhen`）
- 并发与线程安全：`/e/source/platfrom-admin/docs/SqlSugar-docs/偶發性錯誤與執行緒安全.md`

**本任务禁止事项：** 不要把并发测试写成 SQLite——SQLite 的写锁是库级串行化，**天然不会出现竞争**，跑通了也证明不了任何事。不要在 CI 上要求外部数据库。

**这一条是 P4 的核心证明。** 不跑真库，「多实例不重复投递」就是未经验证的声明。

- [ ] **Step 1: 写并发测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱并发领取测试，需要真实数据库
/// </summary>
public class OutboxConcurrencyTests
{
    private const string SkipReason = "未设置 XIHAN_TEST_MYSQL，跳过真实数据库并发测试。";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("XIHAN_TEST_MYSQL");

    /// <summary>
    /// 并发领取时同一条记录只会被一个调用方领到
    /// </summary>
    [Fact]
    public async Task 并发领取时记录不重复()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        const int eventCount = 200;
        const int workerCount = 8;

        using var setupClient = CreateClient();
        setupClient.CodeFirst.InitTables(typeof(SysEventOutbox));
        await setupClient.Deleteable<SysEventOutbox>().ExecuteCommandAsync();

        var setupOutbox = CreateOutbox(setupClient);
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        for (var index = 0; index < eventCount; index++)
        {
            await setupOutbox.EnqueueAsync(
                new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], baseTime.AddSeconds(index)));
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            using var client = CreateClient();
            var outbox = CreateOutbox(client);
            var claimedIds = new List<Guid>();

            while (true)
            {
                var batch = await outbox.GetWaitingEventsAsync(10);
                if (batch.Count == 0)
                {
                    break;
                }

                claimedIds.AddRange(batch.Select(item => item.Id));
            }

            return claimedIds;
        })).ToArray();

        var results = await Task.WhenAll(workers);

        var allClaimed = results.SelectMany(ids => ids).ToList();

        Assert.Equal(allClaimed.Count, allClaimed.Distinct().Count());
        Assert.Equal(eventCount, allClaimed.Count);

        await setupClient.Deleteable<SysEventOutbox>().ExecuteCommandAsync();
    }

    private static SqlSugarClient CreateClient()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = ConnectionString,
            DbType = DbType.MySql,
            IsAutoCloseConnection = true
        });
    }

    private static SqlSugarEventOutbox CreateOutbox(ISqlSugarClient client)
    {
        return new SqlSugarEventOutbox(
            new StubClientResolver(client),
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = TimeSpan.FromMinutes(5)
            }));
    }
}
```

- [ ] **Step 2: 在本机跑真库测试**

准备一个可用的 MySQL 实例并设置环境变量，然后运行：

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Database=xihan_test;Uid=root;Pwd=your_password;"
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`并发领取时记录不重复` PASS，且 `allClaimed` 无重复、总数等于 200。

**若该测试失败并出现重复标识**，说明抢占的 `Where` 丢了可领取条件（硬约束 ②）——回到 Task 2 Step 3 核对，不要通过放宽断言来让它变绿。

- [ ] **Step 3: 确认 CI 环境下自动跳过**

```bash
unset XIHAN_TEST_MYSQL
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：该测试显示为 skipped，其余全部 PASS，退出码为 0。

- [ ] **Step 4: 更新包 README**

把「核心能力」一节改为：

```markdown
## 核心能力

- 发件箱实体 `sys_event_outbox` 与 `OutgoingEventInfo` 的双向映射
- 入箱与业务数据落在同一事务：业务回滚，事件随之消失
- 多实例领取互斥：条件抢占 + 领取超时释放，不依赖任何数据库方言特性
- 表结构由 `DbInitializer` 在应用启动时创建
```

并在「配置与约定」一节追加：

```markdown
配置节 `XiHan:EventBus:SqlSugar`，`ClaimTimeout` 控制领取超时，默认 5 分钟。

投递语义为**至少一次**：宿主在投递成功后才删除记录，若进程在投递与删除之间退出，记录会被重新领取并再次投递，消费端需幂等。

本包把 `IEventOutbox` 的注册生命周期由单例改为作用域——发件箱必须取得工作单元作用域的数据库连接才能与业务数据同事务。
```

- [ ] **Step 5: 全量验收与注释复查**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 Warning(s) 0 Error(s)；全部测试通过（真库测试跳过）。

通读本计划新增的 `.cs` 文件注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [ ] **Step 6: 提交**

```bash
git add framework/test/XiHan.Framework.EventBus.SqlSugar.Tests framework/src/XiHan.Framework.EventBus.SqlSugar/README.md
git commit -m "test(eventbus-sqlsugar): 新增真实数据库并发领取测试"
```

---

## 完成标准

P4 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真库测试在无环境变量时跳过）
- 本机配置 `XIHAN_TEST_MYSQL` 后并发测试通过，领取的标识无重复
- 事务回滚后发件箱无该事件，提交后有
- 领取超时后记录可被重新领取
- `filter` 非 `null` 抛 `NotSupportedException`
- `ImplementationType` 为 `SqlSugarEventOutbox`，`IEventOutbox` 解析到它且生命周期为 Scoped

## 已知边界（写入 PR 描述，不写进代码注释）

- **`filter` 未支持**：非 `null` 抛 `NotSupportedException`。需要时应加表达式翻译器，是独立改动。
- **方法名与语义落差**：`GetWaitingEventsAsync` 实为领取。
- **至少一次投递**：消费端必须幂等。
- **单库**：本份只处理当前解析出的那一个库，多库遍历在 P6。
- **`IEventOutbox` 生命周期变更**：由 Singleton 改为 Scoped。框架内无构造函数注入该接口，不会产生被捕获依赖。
- **两次往返**：「先选后抢」比 `SKIP LOCKED` 多一次往返，是可移植性的代价。

## 下一份计划（P6，本计划完成后再写）

多库遍历：业务实体经 `[ModuleDataSource]` 落在模块库时，发件箱须写在同一个库；发送端须遍历 `ISqlSugarClientResolver.GetCurrentLayoutConfigIds()` 列出的全部库。
