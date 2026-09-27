# EventBus.SqlSugar 收件箱（P7）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在已存在的 `XiHan.Framework.EventBus.SqlSugar` 包里实现 `IEventInbox` 的 SqlSugar 版本，替换进程内的 `DefaultEventInbox`：按消息标识去重、多实例处理互斥、已处理记录按保留期清理；并把收件箱补进该包现有的文档站条目与模块清单。

**Architecture:** 新增实体 `SysEventInbox`（表 `sys_event_inbox`，去重键唯一索引）与映射 `EventInboxMapper`。`SqlSugarEventInbox` 的每次读写都在 `ICurrentTenant.Change(null)` 内经 `GetCurrentClient()` 取宿主布局主库；入箱「先插、失败后按去重键回查」；领取沿用发件箱的三步抢占，另加 `NextRetryTime` 条件；标记已处理 / 已丢弃只改状态不删行，清理按 `HandledTime` 与保留期删除。注册追加在现有的 `AddXiHanSqlSugarEventBus` 里。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-eventbus-sqlsugar-p7-inbox-design.md`

> 该 spec 自成一体，实现 P7 所需的全部约束都在其中。**动手前先读 spec 的第 5 节（九条静默陷阱）与「待确认的决策」**。**不要**去读 `2026-09-21-sqlsugar-persistence-design.md`——那是拆分前的总纲，已停用。

**Linear 议题:** `https://linear.app/elf-express/issue/EDDIE-6`

**前置:** P3、P4、P6 已完成并已合入 `dev`——`framework/src/XiHan.Framework.EventBus.SqlSugar/` 下已有 `Entities/SysEventOutbox.cs`、`Mapping/EventOutboxMapper.cs`、`Outbox/SqlSugarEventOutbox.cs`、`Options/XiHanSqlSugarEventBoxOptions.cs`、`Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs`，测试项目 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/` 已有 `OutboxTestContext.cs`（内含 `StubClientResolver`）。开工前确认这些文件存在。

> 设计文档与计划提交在 `dev` 分支。worktree 内若看不到这些文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：从 `dev` 开 worktree，分支 `feat/eventbus-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。

```bash
git worktree add ../XiHan.Framework-eventbus -b feat/eventbus-sqlsugar dev
```

之后所有命令在 `E:/source/XiHan/XiHan.Framework-eventbus` 下执行。

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

**SqlSugar 签名只信源码**：本包引用 `SqlSugarCore 5.1.4.221`，权威源码是 `/e/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`，那是 .NET Framework 变体）。更新提供者的目录名是 `Abstract/UpdateProvider/`，**不是** `UpdateableProvider`。文档：`/e/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar —— Interface/ISqlSugarClient.cs
ISugarQueryable<T> Queryable<T>()
IInsertable<T> Insertable<T>(T insertObj) where T : class, new()
IUpdateable<T> Updateable<T>() where T : class, new()
IDeleteable<T> Deleteable<T>() where T : class, new()

// Interface/Insertable.cs
Task<int> IInsertable<T>.ExecuteCommandAsync()

// Interface/IQueryable.cs
ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
ISugarQueryable<T>.OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)
ISugarQueryable<T>.Take(int num)
ISugarQueryable<TResult> ISugarQueryable<T>.Select<TResult>(Expression<Func<T, TResult>> expression)
Task<List<T>> ISugarQueryable<T>.ToListAsync(CancellationToken token)
Task<bool> ISugarQueryable<T>.AnyAsync(Expression<Func<T, bool>> expression)
Task<int> ISugarQueryable<T>.CountAsync()
Task<int> ISugarQueryable<T>.CountAsync(Expression<Func<T, bool>> expression)
Task<T> ISugarQueryable<T>.FirstAsync()

// Interface/IUpdateable.cs
IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IUpdateable<T>.ExecuteCommandAsync()
Task<int> IUpdateable<T>.ExecuteCommandAsync(CancellationToken token)

// Interface/IDeleteable.cs
IDeleteable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IDeleteable<T>.ExecuteCommandAsync()

// Interface/IAdo.cs
int IAdo.ExecuteCommand(string sql, params SugarParameter[] parameters)

// Entities/Mapping/SugarMappingAttribute.cs:346,354 —— 字段参数按「属性名」匹配列（CodeFirstProvider.cs:384）
SugarIndexAttribute(string indexName, string fieldName, OrderByType sortType, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)

// 框架 —— XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver
ISqlSugarClient GetCurrentClient()                 // 按当前租户解析布局主库，内部调 GetClient → EnlistCurrentUnitOfWork

// 框架 —— XiHan.Framework.MultiTenancy.Abstractions.ICurrentTenant
long? Id { get; }
IDisposable Change(long? id, string? name = null)  // 传 null 切到无租户上下文，Dispose 时恢复

// 框架 —— XiHan.Framework.Data.SqlSugar.Initializers
TableInitializationAttribute { bool Enabled; DbInitializationTarget Target; bool IncludeModuleConnections; ... }
DbInitializationTarget.Platform
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事、设计理由、反事实推理（「否则会……」）写进提交信息
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以）
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀
- `public` 成员必须有 `<summary>`（`GenerateDocumentationFile` 全局开启，缺了会告警）
- 只写实际用到的 `using`

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`、全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目**：沿用现有的 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/`，**不新建测试项目**，不改它的 csproj。`Microsoft.Data.Sqlite` 与 MySQL 驱动经 `SqlSugarCore` 传递引入。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**SQLite 的时间**：`DateTimeOffset` 不保存偏移，在 UTC+8 机器上读回会平移 8 小时。**不要写「读回的时间等于写入的时间」的断言**；构造「未到 / 已到」「过期 / 未过期」一律用 **±1 天以上**的间隔。

**真实数据库测试**：`Assert.SkipWhen(...)` + 环境变量 `XIHAN_TEST_MYSQL`，CI 自动跳过。范式：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`、`framework/test/XiHan.Framework.EventBus.Redis.Tests/RedisPendingBehaviorTests.cs`。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本系列无关，**不要去追它**。

**提交信息**：中文 Conventional Commits，作用域 `eventbus-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。这是用户的硬规则，高于任何默认行为。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**（`framework/src/XiHan.Framework.Data/SqlSugar/Options/XiHanSqlSugarCoreOptions.cs`）。不开启就不会自动建表，首次入箱即报表不存在。README 与文档站必须写明。

---

## 本计划特有的五条硬约束

**① 去重键是 `MessageId`，不是事件 `Id`。**

`DistributedEventBusBase.AddToInboxAsync` 每次收到消息都 `GuidGenerator.NextId()` 生成新 `Id`。按 `Id` 去重永不命中。**去重用例必须构造两个 `Id` 不同、`MessageId` 相同的实例**——用同一个实例入箱两次，主键冲突会让用例假装去重成功。

**② 无消息标识时，去重键每条各不相同。**

写成常量或空串，第二条无标识消息会撞唯一索引、又被回查判为重复，**静默丢弃**。

**③ 标记已处理 / 已丢弃只改状态，不删行。**

去重靠这些记录。删行由 `DeleteOldEventsAsync` 按保留期做，且只删「已处理或已丢弃、`HandledTime` 早于保留期」的记录——待处理与已领取的记录无论多旧都不删。

**④ 抢占的 `Where` 必须同时含候选主键与完整的可领取条件。**

可领取 = （待处理 **且**（`NextRetryTime` 为空 **或** 已到））**或**（已领取 **且** 领取已超时）。只按主键更新令牌会让并发领取者互相覆盖，同一事件被处理两次。SQLite 用例构造不出这个竞态，只有真库用例能发现。

**⑤ 每次读写都在 `_currentTenant.Change(null)` 内取客户端，且只用 `GetCurrentClient()`。**

不切租户，库隔离租户上下文中的入箱会写进租户库，处理循环永远读不到。不调 `GetClientForEntity`、`GetEnlistedConfigIds`、`GetCurrentLayoutConfigIds`——那是发件箱的做法。

---

## File Structure

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  Entities/SysEventInbox.cs                     新建：收件箱实体
  Mapping/EventInboxMapper.cs                   新建：契约与实体的双向映射
  Inbox/SqlSugarEventInbox.cs                   新建：IEventInbox 的 SqlSugar 实现
  Options/XiHanSqlSugarEventBoxOptions.cs       修改：新增 InboxRetentionPeriod
  Extensions/DependencyInjection/
    XiHanSqlSugarEventBusServiceCollectionExtensions.cs   修改：追加收件箱注册
  XiHanSqlSugarEventBusModule.cs                修改：<remarks> 一句
  README.md                                     修改：补收件箱

framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxTestContext.cs                          修改：StubClientResolver 新增 CurrentConfigIdSelector
  EventInboxEntityTests.cs                      新建
  EventInboxMapperTests.cs                      新建
  TableInitializationTests.cs                   修改：追加两个收件箱用例
  InboxTestContext.cs                           新建：夹具 + FakeCurrentTenant
  InboxEnqueueTests.cs                          新建
  InboxClaimTests.cs                            新建
  InboxStateTests.cs                            新建
  InboxRegistrationTests.cs                     新建
  InboxConcurrencyTests.cs                      新建（真库，CI 跳过）

docs/packages/eventbus-sqlsugar.md              修改：整页重写，补收件箱与 P6 多库
framework/README.md                             修改：第 92 行
framework/README_cn.md                          修改：第 92 行
```

`docs/.vitepress/config.ts` 第 168 行已有 `pkg("EventBus.SqlSugar", "eventbus-sqlsugar")`，**不改**；Task 5 只核对。根 `README.md` / `README_cn.md` **不改**（spec「待确认的决策」D14）。

---

### Task 1: 收件箱实体与映射

**Files:**
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventInbox.cs`
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventInboxMapper.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventInboxEntityTests.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventInboxMapperTests.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/TableInitializationTests.cs`

**Interfaces:**
- Consumes: `XiHan.Framework.Data.SqlSugar.Entities.SugarEntity<Guid>`、`TableInitializationAttribute`、`DbInitializationTarget`、`IncomingEventInfo`
- Produces:
  - `public class SysEventInbox : SugarEntity<Guid>`，常量 `StatusPending = 0`、`StatusClaimed = 1`、`StatusProcessed = 2`、`StatusDiscarded = 3`；属性 `string? MessageId`、`string DedupKey`、`string EventName`、`byte[] EventData`、`DateTimeOffset CreatedTime`、`string? ExtraProperties`、`int Status`、`int RetryCount`、`DateTimeOffset? NextRetryTime`、`string? ClaimToken`、`DateTimeOffset? ClaimTime`、`DateTimeOffset? HandledTime`
  - `public static class EventInboxMapper`：`const string NoMessageIdKeyPrefix`、`SysEventInbox ToEntity(IncomingEventInfo info)`、`IncomingEventInfo ToEventInfo(SysEventInbox entity)`、`internal static DateTimeOffset ToOffset(DateTime value)`

**参考来源（动手前先读）：**
- 实体范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`
- 映射范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventOutboxMapper.cs`
- 契约：`framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/IncomingEventInfo.cs`
- 建表判定：`framework/src/XiHan.Framework.Data/SqlSugar/Initializers/DbEntityTypeProvider.cs:66-143`
- 索引：`/e/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Abstract/CodeFirstProvider/CodeFirstProvider.cs:331-395`
- spec §4.1、§4.2

**本任务禁止事项：** 硬约束 ①②。不加 `[SplitTable]`。不设 `IncludeModuleConnections`。`SugarIndex` 的字段参数写**属性名**（`nameof(SysEventInbox.DedupKey)`），不写列名。完结时刻的属性**不要**叫 `ModifiedTime`（会被 `Data` 包的审计 AOP 改写）。

- [ ] **Step 1: 写失败的实体测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventInboxEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱实体映射测试
/// </summary>
public class EventInboxEntityTests
{
    /// <summary>
    /// 表名带 sys 前缀且不分表
    /// </summary>
    [Fact]
    public void 表名带前缀且不分表()
    {
        var table = typeof(SysEventInbox).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_event_inbox", table.TableName);
        Assert.Null(typeof(SysEventInbox).GetCustomAttribute<SplitTableAttribute>());
    }

    /// <summary>
    /// 主键是事件自身的标识且非自增
    /// </summary>
    [Fact]
    public void 主键是事件标识且非自增()
    {
        var property = typeof(SysEventInbox).GetProperty(nameof(SysEventInbox.BasicId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.Equal(typeof(Guid), property!.PropertyType);
        Assert.NotNull(column);
        Assert.True(column.IsPrimaryKey);
        Assert.False(column.IsIdentity);
    }

    /// <summary>
    /// 列名使用帕斯卡下划线
    /// </summary>
    /// <param name="propertyName">属性名</param>
    /// <param name="columnName">预期列名</param>
    [Theory]
    [InlineData(nameof(SysEventInbox.MessageId), "Message_Id")]
    [InlineData(nameof(SysEventInbox.DedupKey), "Dedup_Key")]
    [InlineData(nameof(SysEventInbox.EventName), "Event_Name")]
    [InlineData(nameof(SysEventInbox.EventData), "Event_Data")]
    [InlineData(nameof(SysEventInbox.CreatedTime), "Created_Time")]
    [InlineData(nameof(SysEventInbox.ExtraProperties), "Extra_Properties")]
    [InlineData(nameof(SysEventInbox.Status), "Status")]
    [InlineData(nameof(SysEventInbox.RetryCount), "Retry_Count")]
    [InlineData(nameof(SysEventInbox.NextRetryTime), "Next_Retry_Time")]
    [InlineData(nameof(SysEventInbox.ClaimToken), "Claim_Token")]
    [InlineData(nameof(SysEventInbox.ClaimTime), "Claim_Time")]
    [InlineData(nameof(SysEventInbox.HandledTime), "Handled_Time")]
    public void 列名使用帕斯卡下划线(string propertyName, string columnName)
    {
        var column = typeof(SysEventInbox).GetProperty(propertyName)!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal(columnName, column.ColumnName);
    }

    /// <summary>
    /// 消息标识可空而去重键非空，两者长度一致
    /// </summary>
    [Fact]
    public void 消息标识可空而去重键非空()
    {
        var messageId = typeof(SysEventInbox).GetProperty(nameof(SysEventInbox.MessageId))!.GetCustomAttribute<SugarColumn>();
        var dedupKey = typeof(SysEventInbox).GetProperty(nameof(SysEventInbox.DedupKey))!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(messageId);
        Assert.NotNull(dedupKey);
        Assert.True(messageId.IsNullable);
        Assert.False(dedupKey.IsNullable);
        Assert.Equal(256, messageId.Length);
        Assert.Equal(256, dedupKey.Length);
    }

    /// <summary>
    /// 事件名长度上限与契约一致
    /// </summary>
    [Fact]
    public void 事件名长度上限与契约一致()
    {
        var column = typeof(SysEventInbox).GetProperty(nameof(SysEventInbox.EventName))!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal(256, column.Length);
    }

    /// <summary>
    /// 收件箱只在平台库建表
    /// </summary>
    [Fact]
    public void 收件箱只在平台库建表()
    {
        var attribute = typeof(SysEventInbox).GetCustomAttribute<TableInitializationAttribute>(inherit: true);

        Assert.NotNull(attribute);
        Assert.True(attribute.Enabled);
        Assert.False(attribute.IncludeModuleConnections);
        Assert.Equal(DbInitializationTarget.Platform, attribute.Target);
    }

    /// <summary>
    /// 去重键带唯一索引
    /// </summary>
    [Fact]
    public void 去重键带唯一索引()
    {
        var index = typeof(SysEventInbox).GetCustomAttributes<SugarIndexAttribute>()
            .SingleOrDefault(item => item.IsUnique);

        Assert.NotNull(index);
        Assert.Equal("ux_sys_event_inbox_dedup_key", index.IndexName);
        Assert.Single(index.IndexFields);
        Assert.True(index.IndexFields.ContainsKey(nameof(SysEventInbox.DedupKey)));
    }

    /// <summary>
    /// 状态索引覆盖状态与创建时间
    /// </summary>
    [Fact]
    public void 状态索引覆盖状态与创建时间()
    {
        var index = typeof(SysEventInbox).GetCustomAttributes<SugarIndexAttribute>()
            .SingleOrDefault(item => !item.IsUnique);

        Assert.NotNull(index);
        Assert.Equal("ix_sys_event_inbox_status", index.IndexName);
        Assert.Equal(2, index.IndexFields.Count);
        Assert.True(index.IndexFields.ContainsKey(nameof(SysEventInbox.Status)));
        Assert.True(index.IndexFields.ContainsKey(nameof(SysEventInbox.CreatedTime)));
    }

    /// <summary>
    /// 主键经构造函数传入
    /// </summary>
    [Fact]
    public void 主键经构造函数传入()
    {
        var id = Guid.NewGuid();

        var entity = new SysEventInbox(id);

        Assert.Equal(id, entity.BasicId);
    }
}
```

- [ ] **Step 2: 写失败的映射测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventInboxMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Mapping;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱映射测试
/// </summary>
public class EventInboxMapperTests
{
    /// <summary>
    /// 事件标识、消息标识、名称与数据往返保真
    /// </summary>
    [Fact]
    public void 事件标识消息标识名称与数据往返保真()
    {
        var id = Guid.NewGuid();
        byte[] data = [1, 2, 3, 250];
        var info = new IncomingEventInfo(id, "msg-001", "Order.Paid", data, DateTime.UtcNow);

        var restored = EventInboxMapper.ToEventInfo(EventInboxMapper.ToEntity(info));

        Assert.Equal(id, restored.Id);
        Assert.Equal("msg-001", restored.MessageId);
        Assert.Equal("Order.Paid", restored.EventName);
        Assert.Equal(data, restored.EventData);
    }

    /// <summary>
    /// 有消息标识时去重键等于消息标识
    /// </summary>
    [Fact]
    public void 有消息标识时去重键等于消息标识()
    {
        var info = new IncomingEventInfo(Guid.NewGuid(), "msg-002", "Order.Paid", [1], DateTime.UtcNow);

        var entity = EventInboxMapper.ToEntity(info);

        Assert.Equal("msg-002", entity.MessageId);
        Assert.Equal("msg-002", entity.DedupKey);
    }

    /// <summary>
    /// 无消息标识时去重键由事件标识生成
    /// </summary>
    /// <param name="messageId">空白的消息标识</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 无消息标识时去重键由事件标识生成(string? messageId)
    {
        var id = Guid.NewGuid();
        var info = new IncomingEventInfo(id, messageId!, "Order.Paid", [1], DateTime.UtcNow);

        var entity = EventInboxMapper.ToEntity(info);

        Assert.Null(entity.MessageId);
        Assert.Equal($"{EventInboxMapper.NoMessageIdKeyPrefix}{id:N}", entity.DedupKey);
    }

    /// <summary>
    /// 两条无消息标识的事件去重键不同
    /// </summary>
    [Fact]
    public void 两条无消息标识的事件去重键不同()
    {
        var first = EventInboxMapper.ToEntity(
            new IncomingEventInfo(Guid.NewGuid(), null!, "Order.Paid", [1], DateTime.UtcNow));
        var second = EventInboxMapper.ToEntity(
            new IncomingEventInfo(Guid.NewGuid(), null!, "Order.Paid", [1], DateTime.UtcNow));

        Assert.NotEqual(first.DedupKey, second.DedupKey);
    }

    /// <summary>
    /// 无消息标识的记录还原为空字符串
    /// </summary>
    [Fact]
    public void 无消息标识的记录还原为空字符串()
    {
        var info = new IncomingEventInfo(Guid.NewGuid(), null!, "Order.Paid", [1], DateTime.UtcNow);

        var restored = EventInboxMapper.ToEventInfo(EventInboxMapper.ToEntity(info));

        Assert.Equal(string.Empty, restored.MessageId);
    }

    /// <summary>
    /// 协调世界时往返后时刻不变且类型为协调世界时
    /// </summary>
    [Fact]
    public void 协调世界时往返后时刻不变()
    {
        var created = new DateTime(2026, 9, 28, 10, 30, 0, DateTimeKind.Utc);
        var info = new IncomingEventInfo(Guid.NewGuid(), "msg-003", "Order.Paid", [1], created);

        var restored = EventInboxMapper.ToEventInfo(EventInboxMapper.ToEntity(info));

        Assert.Equal(DateTimeKind.Utc, restored.CreatedTime.Kind);
        Assert.Equal(created, restored.CreatedTime);
    }

    /// <summary>
    /// 未指定类型的时间按协调世界时处理
    /// </summary>
    [Fact]
    public void 未指定类型的时间按协调世界时处理()
    {
        var created = new DateTime(2026, 9, 28, 10, 30, 0, DateTimeKind.Unspecified);
        var info = new IncomingEventInfo(Guid.NewGuid(), "msg-004", "Order.Paid", [1], created);

        var entity = EventInboxMapper.ToEntity(info);

        Assert.Equal(TimeSpan.Zero, entity.CreatedTime.Offset);
        Assert.Equal(created.Ticks, entity.CreatedTime.UtcDateTime.Ticks);
    }

    /// <summary>
    /// 关联标识往返保真
    /// </summary>
    [Fact]
    public void 关联标识往返保真()
    {
        var info = new IncomingEventInfo(Guid.NewGuid(), "msg-005", "Order.Paid", [1], DateTime.UtcNow);
        info.SetCorrelationId("corr-inbox");

        var restored = EventInboxMapper.ToEventInfo(EventInboxMapper.ToEntity(info));

        Assert.Equal("corr-inbox", restored.GetCorrelationId());
    }

    /// <summary>
    /// 入库时状态为待处理且无领取与完结信息
    /// </summary>
    [Fact]
    public void 入库时状态为待处理且无领取与完结信息()
    {
        var info = new IncomingEventInfo(Guid.NewGuid(), "msg-006", "Order.Paid", [1], DateTime.UtcNow);

        var entity = EventInboxMapper.ToEntity(info);

        Assert.Equal(SysEventInbox.StatusPending, entity.Status);
        Assert.Equal(0, entity.RetryCount);
        Assert.Null(entity.NextRetryTime);
        Assert.Null(entity.ClaimToken);
        Assert.Null(entity.ClaimTime);
        Assert.Null(entity.HandledTime);
    }
}
```

- [ ] **Step 3: 追加建表测试**

修改 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/TableInitializationTests.cs`。

在 `private static SqlSugarClient CreateClient(string databaseFile)` **之前**插入两个用例：

```csharp
    /// <summary>
    /// 收件箱表能建出来
    /// </summary>
    [Fact]
    public void 收件箱表能建出来()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_inbox_{Guid.NewGuid():N}.db");

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.InitTables(typeof(SysEventInbox));

            var tableNames = db.DbMaintenance.GetTableInfoList(false)
                .Select(table => table.Name)
                .ToList();

            Assert.Contains(tableNames, name => string.Equals(name, "sys_event_inbox", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteQuietly(databaseFile);
        }
    }

    /// <summary>
    /// 重复的去重键被唯一索引拦下
    /// </summary>
    [Fact]
    public void 重复的去重键被唯一索引拦下()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_inbox_{Guid.NewGuid():N}.db");

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.InitTables(typeof(SysEventInbox));

            db.Insertable(NewInboxEntity("dup-key")).ExecuteCommand();

            Assert.ThrowsAny<Exception>(() => db.Insertable(NewInboxEntity("dup-key")).ExecuteCommand());
            Assert.Equal(1, db.Queryable<SysEventInbox>().Count());
        }
        finally
        {
            DeleteQuietly(databaseFile);
        }
    }
```

在 `private static void DeleteQuietly(string databaseFile)` **之后**、类的右花括号之前插入：

```csharp

    private static SysEventInbox NewInboxEntity(string dedupKey)
    {
        return new SysEventInbox(Guid.NewGuid())
        {
            MessageId = dedupKey,
            DedupKey = dedupKey,
            EventName = "Order.Paid",
            EventData = [1],
            CreatedTime = DateTimeOffset.UtcNow,
            Status = SysEventInbox.StatusPending
        };
    }
```

该文件已有 `using SqlSugar;` 与 `using XiHan.Framework.EventBus.SqlSugar.Entities;`，不新增 using。

- [ ] **Step 4: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：测试项目编译失败，`CS0246` 找不到 `SysEventInbox` 与 `EventInboxMapper`。

- [ ] **Step 5: 写实体**

新建 `framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventInbox.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.EventBus.SqlSugar.Entities;

/// <summary>
/// 收件箱实体
/// </summary>
[SugarTable("sys_event_inbox")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("ux_sys_event_inbox_dedup_key", nameof(SysEventInbox.DedupKey), OrderByType.Asc, true)]
[SugarIndex("ix_sys_event_inbox_status", nameof(SysEventInbox.Status), OrderByType.Asc, nameof(SysEventInbox.CreatedTime), OrderByType.Asc)]
public class SysEventInbox : SugarEntity<Guid>
{
    /// <summary>
    /// 待处理状态
    /// </summary>
    public const int StatusPending = 0;

    /// <summary>
    /// 已领取状态
    /// </summary>
    public const int StatusClaimed = 1;

    /// <summary>
    /// 已处理状态
    /// </summary>
    public const int StatusProcessed = 2;

    /// <summary>
    /// 已丢弃状态
    /// </summary>
    public const int StatusDiscarded = 3;

    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysEventInbox() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">事件唯一标识</param>
    public SysEventInbox(Guid basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 消息标识
    /// </summary>
    [SugarColumn(ColumnName = "Message_Id", Length = 256, IsNullable = true, ColumnDescription = "消息标识")]
    public string? MessageId { get; set; }

    /// <summary>
    /// 去重键，有消息标识时等于消息标识
    /// </summary>
    [SugarColumn(ColumnName = "Dedup_Key", Length = 256, IsNullable = false, ColumnDescription = "去重键，有消息标识时等于消息标识")]
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>
    /// 事件名称
    /// </summary>
    [SugarColumn(ColumnName = "Event_Name", Length = 256, IsNullable = false, ColumnDescription = "事件名称")]
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// 序列化后的事件数据
    /// </summary>
    [SugarColumn(ColumnName = "Event_Data", IsNullable = false, ColumnDescription = "序列化后的事件数据")]
    public byte[] EventData { get; set; } = [];

    /// <summary>
    /// 入箱时刻
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "入箱时刻")]
    public DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 扩展属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Extra_Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "扩展属性的 JSON")]
    public string? ExtraProperties { get; set; }

    /// <summary>
    /// 处理状态，0 待处理，1 已领取，2 已处理，3 已丢弃
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "处理状态，0 待处理，1 已领取，2 已处理，3 已丢弃")]
    public int Status { get; set; }

    /// <summary>
    /// 重试次数
    /// </summary>
    [SugarColumn(ColumnName = "Retry_Count", IsNullable = false, ColumnDescription = "重试次数")]
    public int RetryCount { get; set; }

    /// <summary>
    /// 下次重试时刻，早于该时刻不领取
    /// </summary>
    [SugarColumn(ColumnName = "Next_Retry_Time", IsNullable = true, ColumnDescription = "下次重试时刻")]
    public DateTimeOffset? NextRetryTime { get; set; }

    /// <summary>
    /// 领取令牌
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Token", Length = 64, IsNullable = true, ColumnDescription = "领取令牌")]
    public string? ClaimToken { get; set; }

    /// <summary>
    /// 领取时刻
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Time", IsNullable = true, ColumnDescription = "领取时刻")]
    public DateTimeOffset? ClaimTime { get; set; }

    /// <summary>
    /// 完结时刻，进入已处理或已丢弃状态时写入
    /// </summary>
    [SugarColumn(ColumnName = "Handled_Time", IsNullable = true, ColumnDescription = "完结时刻")]
    public DateTimeOffset? HandledTime { get; set; }
}
```

- [ ] **Step 6: 写映射**

新建 `framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventInboxMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Mapping;

/// <summary>
/// 收件箱契约与实体的双向映射
/// </summary>
public static class EventInboxMapper
{
    /// <summary>
    /// 无消息标识时去重键的前缀
    /// </summary>
    public const string NoMessageIdKeyPrefix = "no-message-id:";

    /// <summary>
    /// 把入站事件信息转换为实体
    /// </summary>
    /// <remarks>
    /// 有消息标识时去重键等于消息标识；消息标识为空白时存为空值，去重键由前缀与事件标识组成。
    /// </remarks>
    /// <param name="info">入站事件信息</param>
    /// <returns>收件箱实体</returns>
    public static SysEventInbox ToEntity(IncomingEventInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var messageId = string.IsNullOrWhiteSpace(info.MessageId) ? null : info.MessageId;

        return new SysEventInbox(info.Id)
        {
            MessageId = messageId,
            DedupKey = messageId ?? $"{NoMessageIdKeyPrefix}{info.Id:N}",
            EventName = info.EventName,
            EventData = info.EventData,
            CreatedTime = ToOffset(info.CreatedTime),
            ExtraProperties = info.ExtraProperties.Count == 0
                ? null
                : JsonSerializer.Serialize(info.ExtraProperties),
            Status = SysEventInbox.StatusPending,
            RetryCount = 0,
            NextRetryTime = null,
            ClaimToken = null,
            ClaimTime = null,
            HandledTime = null
        };
    }

    /// <summary>
    /// 把实体转换为入站事件信息
    /// </summary>
    /// <remarks>
    /// 消息标识为空值时还原为空字符串。
    /// </remarks>
    /// <param name="entity">收件箱实体</param>
    /// <returns>入站事件信息</returns>
    public static IncomingEventInfo ToEventInfo(SysEventInbox entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var info = new IncomingEventInfo(
            entity.BasicId,
            entity.MessageId ?? string.Empty,
            entity.EventName,
            entity.EventData,
            entity.CreatedTime.UtcDateTime);

        if (string.IsNullOrWhiteSpace(entity.ExtraProperties))
        {
            return info;
        }

        var properties = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.ExtraProperties);
        if (properties is null)
        {
            return info;
        }

        foreach (var pair in properties)
        {
            info.ExtraProperties[pair.Key] = pair.Value;
        }

        return info;
    }

    /// <summary>
    /// 按时间类型转换为带偏移的时间，未指定类型的按协调世界时处理
    /// </summary>
    /// <param name="value">时间</param>
    /// <returns>带偏移的时间</returns>
    internal static DateTimeOffset ToOffset(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero)
        };
    }
}
```

- [ ] **Step 7: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（`OutboxConcurrencyTests` 在未设 `XIHAN_TEST_MYSQL` 时显示为 skipped）。

若 `重复的去重键被唯一索引拦下` 失败于 `ThrowsAny`（第二次插入成功了），说明唯一索引没建出来：核对 `SugarIndex` 的字段参数写的是**属性名** `nameof(SysEventInbox.DedupKey)` 而不是列名 `"Dedup_Key"`，且第四个参数是 `true`。

- [ ] **Step 8: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 新增收件箱实体与映射" -m "去重键另立一列并建唯一索引：入箱时事件 Id 每次新生成，只能按消息标识去重；无消息标识时去重键由事件 Id 生成，避免被唯一索引误拦。完结时刻取名 HandledTime，避开 Data 审计 AOP 按名写入的 ModifiedTime。"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: 测试夹具、入箱、判重与领取

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Options/XiHanSqlSugarEventBoxOptions.cs`
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Inbox/SqlSugarEventInbox.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`（仅 `StubClientResolver`）
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxTestContext.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxEnqueueTests.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxClaimTests.cs`

> 入箱与领取放在同一个任务里：`SqlSugarEventInbox` 的构造函数从一开始就接收选项，而选项里的 `ClaimTimeout` 只有领取会读。拆成两个任务会留下一个「字段只写不读」的中间提交。

**Interfaces:**
- Consumes: Task 1 的 `SysEventInbox`、`EventInboxMapper`
- Produces:
  - `XiHanSqlSugarEventBoxOptions.InboxRetentionPeriod`（`TimeSpan`，默认 7 天）
  - `public class SqlSugarEventInbox`（本任务**暂不**声明实现 `IEventInbox`，Task 3 补上），构造函数 `(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IOptions<XiHanSqlSugarEventBoxOptions> options, ILogger<SqlSugarEventInbox> logger)`；方法 `Task EnqueueAsync(IncomingEventInfo incomingEvent)`、`Task<bool> ExistsByMessageIdAsync(string messageId)`、`Task<List<IncomingEventInfo>> GetWaitingEventsAsync(int maxCount, Expression<Func<IIncomingEventInfo, bool>>? filter = null, CancellationToken cancellationToken = default)`；私有 `Task<List<SysEventInbox>> ClaimAsync(ISqlSugarClient client, int maxCount, CancellationToken cancellationToken)`
  - `StubClientResolver.CurrentConfigIdSelector`（`Func<string>?`）
  - `InboxTestContext(TimeSpan? claimTimeout = null, TimeSpan? retentionPeriod = null)`；常量 `MainConfigId = "Default"`、`TenantConfigId = "Tenant_1001"`、`TenantId = 1001`；属性 `Clients`、`Resolver`、`CurrentTenant`、`Inbox`、`Client`、`TenantClient`
  - `FakeCurrentTenant : ICurrentTenant`

**参考来源（动手前先读）：**
- 落点、去重与领取：spec §4.3、§4.4、§4.5
- 入箱调用点：`framework/src/XiHan.Framework.EventBus/Distributed/DistributedEventBusBase.cs:297-351`
- 处理循环：`framework/src/XiHan.Framework.EventBus/Distributed/EventBoxInboxProcessorHostedService.cs:77-126`
- 租户解析：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs:79-117`
- 发件箱的 `ClaimFromDatabaseAsync`：`framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs:145-208`——收件箱的 `ClaimAsync` 照它写，只多一项 `NextRetryTime` 条件
- 既有夹具：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`

**本任务禁止事项：** 硬约束 ①②④⑤。插入失败时**不许**一律吞掉——必须回查去重键，查不到就 `throw;` 原样重抛。不识别任何数据库错误码。不开事务。**不要**使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP` 或任何方言相关 SQL。**不要**省掉领取的三轮重试循环——丢掉它不会有任何第一层用例变红（spec §6）。**不要**吞 `OperationCanceledException`。`StubClientResolver` 只加属性、改 `GetCurrentClient` 一行，不改构造函数与其他成员——发件箱的用例依赖它们。

- [ ] **Step 1: 给选项加保留期**

修改 `framework/src/XiHan.Framework.EventBus.SqlSugar/Options/XiHanSqlSugarEventBoxOptions.cs`，把：

```csharp
    /// <summary>
    /// 领取超时，超过该时长仍未删除的已领取记录可被重新领取
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);
```

改为：

```csharp
    /// <summary>
    /// 领取超时，超过该时长仍未完结的已领取记录可被重新领取，收发件箱共用
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 收件箱已处理与已丢弃记录的保留期，超过该时长的记录被清理
    /// </summary>
    public TimeSpan InboxRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
```

- [ ] **Step 2: 给桩解析器加可选的当前库选择器**

修改 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs` 中的 `StubClientResolver`。

在 `FaultyConfigIds` 属性之后插入：

```csharp

    /// <summary>
    /// 当前库的选择器，为空时使用构造函数传入的当前库
    /// </summary>
    public Func<string>? CurrentConfigIdSelector { get; set; }
```

把 `GetCurrentClient` 的方法体由：

```csharp
        return GetClient(_currentConfigId);
```

改为：

```csharp
        return GetClient(CurrentConfigIdSelector?.Invoke() ?? _currentConfigId);
```

**只改 `GetCurrentClient`**，`GetClientForEntity` 保持 `return GetClient(_currentConfigId);` 不动。

- [ ] **Step 3: 写收件箱夹具**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxTestContext.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Inbox;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱测试夹具，提供宿主主库与一个租户库两个临时 SQLite 库
/// </summary>
internal sealed class InboxTestContext : IDisposable
{
    /// <summary>
    /// 宿主主库的连接配置标识
    /// </summary>
    public const string MainConfigId = "Default";

    /// <summary>
    /// 租户库的连接配置标识
    /// </summary>
    public const string TenantConfigId = "Tenant_1001";

    /// <summary>
    /// 租户标识
    /// </summary>
    public const long TenantId = 1001;

    private readonly List<string> _databaseFiles = [];

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="claimTimeout">领取超时</param>
    /// <param name="retentionPeriod">已完结记录的保留期</param>
    public InboxTestContext(TimeSpan? claimTimeout = null, TimeSpan? retentionPeriod = null)
    {
        string[] configIds = [MainConfigId, TenantConfigId];

        foreach (var configId in configIds)
        {
            var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_inbox_{Guid.NewGuid():N}.db");
            _databaseFiles.Add(databaseFile);

            var client = new SqlSugarClient(new ConnectionConfig
            {
                // 禁用连接池
                ConnectionString = $"DataSource={databaseFile};Pooling=False",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

            client.CodeFirst.InitTables(typeof(SysEventInbox));
            Clients[configId] = client;
        }

        CurrentTenant = new FakeCurrentTenant();

        Resolver = new StubClientResolver(Clients, [MainConfigId], MainConfigId)
        {
            CurrentConfigIdSelector = () => CurrentTenant.Id is null ? MainConfigId : TenantConfigId
        };

        Inbox = new SqlSugarEventInbox(
            Resolver,
            CurrentTenant,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5),
                InboxRetentionPeriod = retentionPeriod ?? TimeSpan.FromDays(7)
            }),
            NullLogger<SqlSugarEventInbox>.Instance);
    }

    /// <summary>
    /// 各库的客户端
    /// </summary>
    public Dictionary<string, SqlSugarClient> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 可编程的客户端解析器，当前库随当前租户切换
    /// </summary>
    public StubClientResolver Resolver { get; }

    /// <summary>
    /// 当前租户
    /// </summary>
    public FakeCurrentTenant CurrentTenant { get; }

    /// <summary>
    /// 被测收件箱
    /// </summary>
    public SqlSugarEventInbox Inbox { get; }

    /// <summary>
    /// 宿主主库客户端
    /// </summary>
    public SqlSugarClient Client
    {
        get { return Clients[MainConfigId]; }
    }

    /// <summary>
    /// 租户库客户端
    /// </summary>
    public SqlSugarClient TenantClient
    {
        get { return Clients[TenantConfigId]; }
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
/// 测试用当前租户，切换后在释放时恢复原值
/// </summary>
internal sealed class FakeCurrentTenant : ICurrentTenant
{
    /// <summary>
    /// 当前租户是否可用
    /// </summary>
    public bool IsAvailable => Id.HasValue;

    /// <summary>
    /// 当前租户标识
    /// </summary>
    public long? Id { get; private set; }

    /// <summary>
    /// 当前租户名称
    /// </summary>
    public string? Name { get; private set; }

    /// <summary>
    /// 临时切换当前租户
    /// </summary>
    /// <param name="id">租户标识，为空表示无租户</param>
    /// <param name="name">租户名称</param>
    /// <returns>释放时恢复原租户的对象</returns>
    public IDisposable Change(long? id, string? name = null)
    {
        var restorer = new TenantRestorer(this, Id, Name);

        Id = id;
        Name = name;

        return restorer;
    }

    /// <summary>
    /// 释放时把租户恢复为切换前的值
    /// </summary>
    private sealed class TenantRestorer : IDisposable
    {
        private readonly FakeCurrentTenant _owner;
        private readonly long? _id;
        private readonly string? _name;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="owner">所属的当前租户</param>
        /// <param name="id">切换前的租户标识</param>
        /// <param name="name">切换前的租户名称</param>
        public TenantRestorer(FakeCurrentTenant owner, long? id, string? name)
        {
            _owner = owner;
            _id = id;
            _name = name;
        }

        /// <summary>
        /// 恢复切换前的租户
        /// </summary>
        public void Dispose()
        {
            _owner.Id = _id;
            _owner.Name = _name;
        }
    }
}
```

- [ ] **Step 4: 写失败的入箱测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxEnqueueTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱入箱与判重测试
/// </summary>
public class InboxEnqueueTests
{
    /// <summary>
    /// 入箱后记录为待处理且无领取信息
    /// </summary>
    [Fact]
    public async Task 入箱后记录为待处理且无领取信息()
    {
        using var context = new InboxTestContext();
        var info = NewEvent("msg-enqueue");

        await context.Inbox.EnqueueAsync(info);

        var stored = await context.Client.Queryable<SysEventInbox>()
            .Where(item => item.BasicId == info.Id)
            .FirstAsync();

        Assert.NotNull(stored);
        Assert.Equal(SysEventInbox.StatusPending, stored.Status);
        Assert.Equal("msg-enqueue", stored.MessageId);
        Assert.Equal("msg-enqueue", stored.DedupKey);
        Assert.Equal(0, stored.RetryCount);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.HandledTime);
    }

    /// <summary>
    /// 同一消息标识再次入箱只保留一条且不抛异常
    /// </summary>
    [Fact]
    public async Task 同一消息标识再次入箱只保留一条且不抛异常()
    {
        using var context = new InboxTestContext();
        var first = NewEvent("msg-duplicate");
        var second = NewEvent("msg-duplicate");

        Assert.NotEqual(first.Id, second.Id);

        await context.Inbox.EnqueueAsync(first);
        await context.Inbox.EnqueueAsync(second);

        var stored = await context.Client.Queryable<SysEventInbox>().ToListAsync();

        Assert.Single(stored);
        Assert.Equal(first.Id, stored[0].BasicId);
    }

    /// <summary>
    /// 无消息标识的事件各自入箱
    /// </summary>
    [Fact]
    public async Task 无消息标识的事件各自入箱()
    {
        using var context = new InboxTestContext();

        await context.Inbox.EnqueueAsync(NewEvent(null!));
        await context.Inbox.EnqueueAsync(NewEvent(null!));

        Assert.Equal(2, await context.Client.Queryable<SysEventInbox>().CountAsync());
    }

    /// <summary>
    /// 非重复原因的写入失败原样抛出
    /// </summary>
    [Fact]
    public async Task 非重复原因的写入失败原样抛出()
    {
        using var context = new InboxTestContext();
        context.Client.Ado.ExecuteCommand(
            "CREATE TRIGGER trg_reject_inbox BEFORE INSERT ON sys_event_inbox BEGIN SELECT RAISE(ABORT, 'inbox-rejected'); END;");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => context.Inbox.EnqueueAsync(NewEvent("msg-rejected")));

        Assert.Contains("inbox-rejected", error.ToString());
    }

    /// <summary>
    /// 租户上下文中入箱仍写宿主主库
    /// </summary>
    [Fact]
    public async Task 租户上下文中入箱仍写宿主主库()
    {
        using var context = new InboxTestContext();

        using (context.CurrentTenant.Change(InboxTestContext.TenantId))
        {
            await context.Inbox.EnqueueAsync(NewEvent("msg-tenant"));
        }

        Assert.Equal(1, await context.Client.Queryable<SysEventInbox>().CountAsync());
        Assert.Equal(0, await context.TenantClient.Queryable<SysEventInbox>().CountAsync());
    }

    /// <summary>
    /// 入箱后恢复调用方的租户上下文
    /// </summary>
    [Fact]
    public async Task 入箱后恢复调用方的租户上下文()
    {
        using var context = new InboxTestContext();

        using (context.CurrentTenant.Change(InboxTestContext.TenantId))
        {
            await context.Inbox.EnqueueAsync(NewEvent("msg-restore"));

            Assert.Equal(InboxTestContext.TenantId, context.CurrentTenant.Id);
        }
    }

    /// <summary>
    /// 已收录的消息标识判定为存在
    /// </summary>
    [Fact]
    public async Task 已收录的消息标识判定为存在()
    {
        using var context = new InboxTestContext();
        await context.Inbox.EnqueueAsync(NewEvent("msg-exists"));

        Assert.True(await context.Inbox.ExistsByMessageIdAsync("msg-exists"));
        Assert.False(await context.Inbox.ExistsByMessageIdAsync("msg-unknown"));
    }

    /// <summary>
    /// 空白消息标识判定为不存在
    /// </summary>
    /// <param name="messageId">空白的消息标识</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空白消息标识判定为不存在(string messageId)
    {
        using var context = new InboxTestContext();

        Assert.False(await context.Inbox.ExistsByMessageIdAsync(messageId));
    }

    /// <summary>
    /// 租户上下文中判重仍查宿主主库
    /// </summary>
    [Fact]
    public async Task 租户上下文中判重仍查宿主主库()
    {
        using var context = new InboxTestContext();
        await context.Inbox.EnqueueAsync(NewEvent("msg-host"));

        using (context.CurrentTenant.Change(InboxTestContext.TenantId))
        {
            Assert.True(await context.Inbox.ExistsByMessageIdAsync("msg-host"));
        }
    }

    private static IncomingEventInfo NewEvent(string messageId)
    {
        return new IncomingEventInfo(Guid.NewGuid(), messageId, "Order.Paid", [1, 2, 3], DateTime.UtcNow);
    }
}
```

- [ ] **Step 5: 写失败的领取测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxClaimTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱领取测试
/// </summary>
public class InboxClaimTests
{
    /// <summary>
    /// 领取后记录被标记为已领取并带令牌
    /// </summary>
    [Fact]
    public async Task 领取后记录被标记并带令牌()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);

        var claimed = await context.Inbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);
        Assert.Equal(info.MessageId, claimed[0].MessageId);

        var stored = await context.Client.Queryable<SysEventInbox>()
            .Where(item => item.BasicId == info.Id)
            .FirstAsync();

        Assert.Equal(SysEventInbox.StatusClaimed, stored.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.ClaimToken));
        Assert.NotNull(stored.ClaimTime);
    }

    /// <summary>
    /// 已领取的记录不会被再次领取
    /// </summary>
    [Fact]
    public async Task 已领取的记录不会被再次领取()
    {
        using var context = new InboxTestContext();
        await context.Inbox.EnqueueAsync(NewEvent());

        var first = await context.Inbox.GetWaitingEventsAsync(10);
        var second = await context.Inbox.GetWaitingEventsAsync(10);

        Assert.Single(first);
        Assert.Empty(second);
    }

    /// <summary>
    /// 领取超时后记录可被重新领取
    /// </summary>
    [Fact]
    public async Task 领取超时后可被重新领取()
    {
        using var context = new InboxTestContext(claimTimeout: TimeSpan.FromMinutes(5));
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);
        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));

        var stale = DateTimeOffset.UtcNow.AddDays(-1);
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { ClaimTime = stale })
            .Where(item => item.BasicId == info.Id)
            .ExecuteCommandAsync();

        var reclaimed = await context.Inbox.GetWaitingEventsAsync(10);

        Assert.Single(reclaimed);
        Assert.Equal(info.Id, reclaimed[0].Id);
    }

    /// <summary>
    /// 未到重试时刻的记录不被领取
    /// </summary>
    [Fact]
    public async Task 未到重试时刻的记录不被领取()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);

        var future = DateTimeOffset.UtcNow.AddDays(1);
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { NextRetryTime = future })
            .Where(item => item.BasicId == info.Id)
            .ExecuteCommandAsync();

        Assert.Empty(await context.Inbox.GetWaitingEventsAsync(10));
    }

    /// <summary>
    /// 已到重试时刻的记录被领取
    /// </summary>
    [Fact]
    public async Task 已到重试时刻的记录被领取()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);

        var past = DateTimeOffset.UtcNow.AddDays(-1);
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { NextRetryTime = past })
            .Where(item => item.BasicId == info.Id)
            .ExecuteCommandAsync();

        var claimed = await context.Inbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);
    }

    /// <summary>
    /// 已处理与已丢弃的记录不被领取
    /// </summary>
    [Fact]
    public async Task 已处理与已丢弃的记录不被领取()
    {
        using var context = new InboxTestContext();
        var processed = NewEvent();
        var discarded = NewEvent();
        await context.Inbox.EnqueueAsync(processed);
        await context.Inbox.EnqueueAsync(discarded);

        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { Status = SysEventInbox.StatusProcessed })
            .Where(item => item.BasicId == processed.Id)
            .ExecuteCommandAsync();
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { Status = SysEventInbox.StatusDiscarded })
            .Where(item => item.BasicId == discarded.Id)
            .ExecuteCommandAsync();

        Assert.Empty(await context.Inbox.GetWaitingEventsAsync(10));
    }

    /// <summary>
    /// 按创建时间升序领取且不超过上限
    /// </summary>
    [Fact]
    public async Task 按创建时间升序领取且不超过上限()
    {
        using var context = new InboxTestContext();
        var baseTime = DateTime.UtcNow.AddDays(-2);

        for (var index = 4; index >= 0; index--)
        {
            await context.Inbox.EnqueueAsync(new IncomingEventInfo(
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                "Order.Paid",
                [(byte)index],
                baseTime.AddMinutes(index)));
        }

        var claimed = await context.Inbox.GetWaitingEventsAsync(3);

        Assert.Equal(3, claimed.Count);
        Assert.Equal(new[] { 0, 1, 2 }, claimed.Select(item => (int)item.EventData[0]).ToArray());
    }

    /// <summary>
    /// 过滤条件不为空时抛出不支持
    /// </summary>
    [Fact]
    public async Task 过滤条件不为空时抛出不支持()
    {
        using var context = new InboxTestContext();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Inbox.GetWaitingEventsAsync(10, item => item.EventName == "Order.Paid"));
    }

    /// <summary>
    /// 上限非正时返回空且不领取
    /// </summary>
    [Fact]
    public async Task 上限非正时返回空且不领取()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);

        Assert.Empty(await context.Inbox.GetWaitingEventsAsync(0));

        var stored = await context.Client.Queryable<SysEventInbox>()
            .Where(item => item.BasicId == info.Id)
            .FirstAsync();

        Assert.Equal(SysEventInbox.StatusPending, stored.Status);
    }

    /// <summary>
    /// 已取消的令牌抛出取消异常
    /// </summary>
    [Fact]
    public async Task 已取消的令牌抛出取消异常()
    {
        using var context = new InboxTestContext();
        await context.Inbox.EnqueueAsync(NewEvent());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Inbox.GetWaitingEventsAsync(10, cancellationToken: cancellation.Token));
    }

    /// <summary>
    /// 租户上下文中领取仍读宿主主库
    /// </summary>
    [Fact]
    public async Task 租户上下文中领取仍读宿主主库()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);

        List<IncomingEventInfo> claimed;
        using (context.CurrentTenant.Change(InboxTestContext.TenantId))
        {
            claimed = await context.Inbox.GetWaitingEventsAsync(10);
        }

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);
    }

    private static IncomingEventInfo NewEvent()
    {
        return new IncomingEventInfo(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "Order.Paid", [1], DateTime.UtcNow);
    }
}
```

- [ ] **Step 6: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：测试项目编译失败，`CS0246` 找不到 `SqlSugarEventInbox`（命名空间 `XiHan.Framework.EventBus.SqlSugar.Inbox` 不存在）。

- [ ] **Step 7: 写收件箱的入箱、判重与领取**

新建 `framework/src/XiHan.Framework.EventBus.SqlSugar/Inbox/SqlSugarEventInbox.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Mapping;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.EventBus.SqlSugar.Inbox;

/// <summary>
/// 收件箱的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 所有读写都切换到无租户上下文，落在宿主布局的主库。
/// </remarks>
public class SqlSugarEventInbox
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<SqlSugarEventInbox> _logger;
    private readonly XiHanSqlSugarEventBoxOptions _options;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="options">收发件箱存储配置</param>
    /// <param name="logger">日志器</param>
    public SqlSugarEventInbox(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IOptions<XiHanSqlSugarEventBoxOptions> options,
        ILogger<SqlSugarEventInbox> logger)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 将事件信息添加到收件箱
    /// </summary>
    /// <remarks>
    /// 去重键已在库时不写入、不抛异常；写入因其他原因失败时原样抛出。
    /// </remarks>
    /// <param name="incomingEvent">入站事件信息</param>
    public async Task EnqueueAsync(IncomingEventInfo incomingEvent)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        var entity = EventInboxMapper.ToEntity(incomingEvent);
        var dedupKey = entity.DedupKey;

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            try
            {
                await client.Insertable(entity).ExecuteCommandAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var duplicated = await client.Queryable<SysEventInbox>()
                    .AnyAsync(item => item.DedupKey == dedupKey);

                if (!duplicated)
                {
                    throw;
                }

                _logger.LogDebug(ex, "收件箱已存在去重键为 {DedupKey} 的记录，本次入箱已忽略。", dedupKey);
            }
        }
    }

    /// <summary>
    /// 检查消息标识符是否存在
    /// </summary>
    /// <remarks>
    /// 不区分记录状态，已处理与已丢弃的记录同样视为存在。
    /// </remarks>
    /// <param name="messageId">消息标识符</param>
    /// <returns>存在返回 true</returns>
    public async Task<bool> ExistsByMessageIdAsync(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            return await client.Queryable<SysEventInbox>()
                .AnyAsync(item => item.DedupKey == messageId);
        }
    }

    /// <summary>
    /// 领取一批待处理的事件信息
    /// </summary>
    /// <remarks>
    /// 本方法在返回前会把记录标记为已领取，不是纯查询。
    /// 可领取的记录是：待处理且未设下次重试时刻或该时刻已到的记录，以及领取已超时的记录；
    /// 超时时长由 <see cref="XiHanSqlSugarEventBoxOptions.ClaimTimeout"/> 配置。
    /// </remarks>
    /// <param name="maxCount">最大数量</param>
    /// <param name="filter">过滤条件，本实现不支持，传入非空值将抛出异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本次领取到的事件信息</returns>
    /// <exception cref="NotSupportedException"><paramref name="filter"/> 不为空</exception>
    public async Task<List<IncomingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IIncomingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (filter is not null)
        {
            throw new NotSupportedException(
                "SqlSugar 收件箱暂不支持 filter 参数，请改为在事件处理器内筛选。");
        }

        if (maxCount <= 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            var claimed = await ClaimAsync(client, maxCount, cancellationToken);

            return [.. claimed.Select(EventInboxMapper.ToEventInfo)];
        }
    }

    /// <summary>
    /// 在指定库上领取一批待处理的记录
    /// </summary>
    /// <remarks>
    /// 选中的候选被其他实例抢先领走时另选一批重试，最多三轮；返回空集合表示确实没有可领取的记录。
    /// </remarks>
    /// <param name="client">客户端</param>
    /// <param name="maxCount">最多领取的条数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>领取到的记录</returns>
    private async Task<List<SysEventInbox>> ClaimAsync(
        ISqlSugarClient client,
        int maxCount,
        CancellationToken cancellationToken)
    {
        const int maxClaimAttempts = 3;

        for (var attempt = 0; attempt < maxClaimAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;
            var staleBefore = now - _options.ClaimTimeout;
            var claimToken = Guid.NewGuid().ToString("N");

            var candidateIds = await client.Queryable<SysEventInbox>()
                .Where(item => (item.Status == SysEventInbox.StatusPending && (item.NextRetryTime == null || item.NextRetryTime <= now))
                    || (item.Status == SysEventInbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore))
                .OrderBy(item => item.CreatedTime)
                .Take(maxCount)
                .Select(item => item.BasicId)
                .ToListAsync(cancellationToken);

            if (candidateIds.Count == 0)
            {
                return [];
            }

            var affected = await client.Updateable<SysEventInbox>()
                .SetColumns(item => new SysEventInbox
                {
                    Status = SysEventInbox.StatusClaimed,
                    ClaimToken = claimToken,
                    ClaimTime = now
                })
                .Where(item => candidateIds.Contains(item.BasicId)
                    && ((item.Status == SysEventInbox.StatusPending && (item.NextRetryTime == null || item.NextRetryTime <= now))
                        || (item.Status == SysEventInbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore)))
                .ExecuteCommandAsync(cancellationToken);

            // 候选全被其他实例抢走，另选一批重试
            if (affected == 0)
            {
                continue;
            }

            return await client.Queryable<SysEventInbox>()
                .Where(item => item.ClaimToken == claimToken)
                .OrderBy(item => item.CreatedTime)
                .ToListAsync(cancellationToken);
        }

        return [];
    }
}
```

抢占步骤的 `Where` 与候选步骤的可领取条件**逐字相同**，外层再与 `candidateIds.Contains(item.BasicId)` 做「且」。写完后把两段条件并排对照一遍。

- [ ] **Step 8: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。发件箱的既有用例保持全绿——`CurrentConfigIdSelector` 默认为 `null`，`GetCurrentClient` 行为不变。

若 `同一消息标识再次入箱只保留一条且不抛异常` 抛出了异常：检查 `catch` 里是否按 `dedupKey` 回查。若 `非重复原因的写入失败原样抛出` 没有抛异常：说明 `catch` 吞掉了非重复的故障（硬约束，spec §5 ⑧）。若 `租户上下文中入箱仍写宿主主库` 失败：检查 `GetCurrentClient()` 是否在 `_currentTenant.Change(null)` 的 `using` 块**之内**调用。

若 `按创建时间升序领取且不超过上限` 的顺序不对：检查 `OrderBy(item => item.CreatedTime)` 是否同时出现在候选与取回两步。若 `未到重试时刻的记录不被领取` 失败：检查 `NextRetryTime <= now` 的条件是否漏在了某一步。

- [ ] **Step 9: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 收件箱入箱去重、固定落在宿主主库并以条件抢占领取" -m "入箱先插入，失败后按去重键回查：查到即视为重复消息，查不到原样重抛，不依赖各数据库的唯一冲突错误码；吞掉所有插入失败会让表缺失等故障被当成已收下，broker 随即 ack 而丢事件。读写都切到无租户上下文，与无租户的处理循环落在同一个库。宿主处理循环没有分布式锁，领取沿用发件箱的三步抢占，另加下次重试时刻的过滤。"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 状态流转与清理

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Inbox/SqlSugarEventInbox.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxStateTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SqlSugarEventInbox` 与 `XiHanSqlSugarEventBoxOptions.InboxRetentionPeriod`、Task 1 的 `EventInboxMapper.ToOffset`
- Produces:
  - `SqlSugarEventInbox : IEventInbox`
  - `Task MarkAsProcessedAsync(Guid id)`、`Task MarkAsDiscardAsync(Guid id)`、`Task RetryLaterAsync(Guid id, int retryCount, DateTime? nextRetryTime)`、`Task DeleteOldEventsAsync()`
  - 私有 `Task MarkAsHandledAsync(Guid id, int status)`

**参考来源（动手前先读）：**
- 被替换实现的状态语义：`framework/src/XiHan.Framework.EventBus/Distributed/DefaultEventInbox.cs:83-174`
- 失败处理：`framework/src/XiHan.Framework.EventBus/Distributed/EventBoxInboxProcessorHostedService.cs:128-153`
- spec §4.6、§4.7

**本任务禁止事项：** 硬约束 ③⑤。`MarkAsProcessedAsync` / `MarkAsDiscardAsync` **不许**删行。`RetryLaterAsync` 必须把状态改回待处理**并**清空令牌与领取时刻。清理条件必须限定「已处理或已丢弃」且按 `HandledTime` 判断，**不许**按 `CreatedTime`。不要在 `RetryLaterAsync` 里自行判断是否达到重试上限（spec 决策 D9）。

- [ ] **Step 1: 写失败的测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxStateTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱状态流转与清理测试
/// </summary>
public class InboxStateTests
{
    /// <summary>
    /// 实现收件箱契约
    /// </summary>
    [Fact]
    public void 实现收件箱契约()
    {
        using var context = new InboxTestContext();

        Assert.IsAssignableFrom<IEventInbox>(context.Inbox);
    }

    /// <summary>
    /// 标记已处理后不再被领取且仍判定为存在
    /// </summary>
    [Fact]
    public async Task 标记已处理后不再被领取且仍判定为存在()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);
        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));

        await context.Inbox.MarkAsProcessedAsync(info.Id);

        var stored = await FindAsync(context, info.Id);

        Assert.Equal(SysEventInbox.StatusProcessed, stored.Status);
        Assert.NotNull(stored.HandledTime);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.ClaimTime);
        Assert.Empty(await context.Inbox.GetWaitingEventsAsync(10));
        Assert.True(await context.Inbox.ExistsByMessageIdAsync(info.MessageId));
    }

    /// <summary>
    /// 标记丢弃后不再被领取且仍判定为存在
    /// </summary>
    [Fact]
    public async Task 标记丢弃后不再被领取且仍判定为存在()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);
        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));

        await context.Inbox.MarkAsDiscardAsync(info.Id);

        var stored = await FindAsync(context, info.Id);

        Assert.Equal(SysEventInbox.StatusDiscarded, stored.Status);
        Assert.NotNull(stored.HandledTime);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.ClaimTime);
        Assert.Empty(await context.Inbox.GetWaitingEventsAsync(10));
        Assert.True(await context.Inbox.ExistsByMessageIdAsync(info.MessageId));
    }

    /// <summary>
    /// 延后重试把记录放回待处理并记录次数
    /// </summary>
    [Fact]
    public async Task 延后重试把记录放回待处理并记录次数()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);
        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));

        await context.Inbox.RetryLaterAsync(info.Id, 2, DateTime.UtcNow.AddDays(-1));

        var stored = await FindAsync(context, info.Id);

        Assert.Equal(SysEventInbox.StatusPending, stored.Status);
        Assert.Equal(2, stored.RetryCount);
        Assert.NotNull(stored.NextRetryTime);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.ClaimTime);
        Assert.Null(stored.HandledTime);

        var retried = await context.Inbox.GetWaitingEventsAsync(10);

        Assert.Single(retried);
        Assert.Equal(info.Id, retried[0].Id);
    }

    /// <summary>
    /// 延后重试的时刻未到时暂不领取
    /// </summary>
    [Fact]
    public async Task 延后重试的时刻未到时暂不领取()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);
        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));

        await context.Inbox.RetryLaterAsync(info.Id, 1, DateTime.UtcNow.AddDays(1));

        Assert.Empty(await context.Inbox.GetWaitingEventsAsync(10));
    }

    /// <summary>
    /// 延后重试未给时刻时立即可领取
    /// </summary>
    [Fact]
    public async Task 延后重试未给时刻时立即可领取()
    {
        using var context = new InboxTestContext();
        var info = NewEvent();
        await context.Inbox.EnqueueAsync(info);
        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));

        await context.Inbox.RetryLaterAsync(info.Id, 1, null);

        Assert.Single(await context.Inbox.GetWaitingEventsAsync(10));
    }

    /// <summary>
    /// 对不存在的标识更新状态不抛异常
    /// </summary>
    [Fact]
    public async Task 对不存在的标识更新状态不抛异常()
    {
        using var context = new InboxTestContext();
        var missing = Guid.NewGuid();

        await context.Inbox.MarkAsProcessedAsync(missing);
        await context.Inbox.MarkAsDiscardAsync(missing);
        await context.Inbox.RetryLaterAsync(missing, 1, null);

        Assert.Equal(0, await context.Client.Queryable<SysEventInbox>().CountAsync());
    }

    /// <summary>
    /// 清理只删除超过保留期的已完结记录
    /// </summary>
    [Fact]
    public async Task 清理只删除超过保留期的已完结记录()
    {
        using var context = new InboxTestContext(retentionPeriod: TimeSpan.FromDays(7));
        var processedOld = NewEvent();
        var discardedOld = NewEvent();
        var processedRecent = NewEvent();
        var pendingOld = NewEvent();

        await context.Inbox.EnqueueAsync(processedOld);
        await context.Inbox.EnqueueAsync(discardedOld);
        await context.Inbox.EnqueueAsync(processedRecent);
        await context.Inbox.EnqueueAsync(pendingOld);

        await context.Inbox.MarkAsProcessedAsync(processedOld.Id);
        await context.Inbox.MarkAsDiscardAsync(discardedOld.Id);
        await context.Inbox.MarkAsProcessedAsync(processedRecent.Id);

        var old = DateTimeOffset.UtcNow.AddDays(-30);
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { HandledTime = old })
            .Where(item => item.BasicId == processedOld.Id || item.BasicId == discardedOld.Id)
            .ExecuteCommandAsync();
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { CreatedTime = old })
            .Where(item => item.BasicId == pendingOld.Id)
            .ExecuteCommandAsync();

        await context.Inbox.DeleteOldEventsAsync();

        var remaining = await context.Client.Queryable<SysEventInbox>()
            .Select(item => item.BasicId)
            .ToListAsync();

        Assert.Equal(2, remaining.Count);
        Assert.Contains(processedRecent.Id, remaining);
        Assert.Contains(pendingOld.Id, remaining);
    }

    /// <summary>
    /// 清理后同一消息可再次入箱
    /// </summary>
    [Fact]
    public async Task 清理后同一消息可再次入箱()
    {
        using var context = new InboxTestContext(retentionPeriod: TimeSpan.FromDays(7));
        var first = NewEvent("msg-expired");
        await context.Inbox.EnqueueAsync(first);
        await context.Inbox.MarkAsProcessedAsync(first.Id);

        var old = DateTimeOffset.UtcNow.AddDays(-30);
        await context.Client.Updateable<SysEventInbox>()
            .SetColumns(item => new SysEventInbox { HandledTime = old })
            .Where(item => item.BasicId == first.Id)
            .ExecuteCommandAsync();

        await context.Inbox.DeleteOldEventsAsync();

        Assert.False(await context.Inbox.ExistsByMessageIdAsync("msg-expired"));

        var second = NewEvent("msg-expired");
        await context.Inbox.EnqueueAsync(second);

        Assert.Equal(second.Id, (await FindAsync(context, second.Id)).BasicId);
    }

    private static async Task<SysEventInbox> FindAsync(InboxTestContext context, Guid id)
    {
        var stored = await context.Client.Queryable<SysEventInbox>()
            .Where(item => item.BasicId == id)
            .FirstAsync();

        Assert.NotNull(stored);

        return stored;
    }

    private static IncomingEventInfo NewEvent()
    {
        return NewEvent(Guid.NewGuid().ToString("N"));
    }

    private static IncomingEventInfo NewEvent(string messageId)
    {
        return new IncomingEventInfo(Guid.NewGuid(), messageId, "Order.Paid", [1], DateTime.UtcNow);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：测试项目编译失败，`CS1061` `SqlSugarEventInbox` 不包含 `MarkAsProcessedAsync` 等方法的定义。

- [ ] **Step 3: 声明实现契约**

修改 `framework/src/XiHan.Framework.EventBus.SqlSugar/Inbox/SqlSugarEventInbox.cs`，类声明由：

```csharp
public class SqlSugarEventInbox
```

改为：

```csharp
public class SqlSugarEventInbox : IEventInbox
```

`IEventInbox` 与 `IncomingEventInfo` 同在 `XiHan.Framework.EventBus.Abstractions.Distributed`，不新增 using。

- [ ] **Step 4: 实现状态流转与清理**

在 `GetWaitingEventsAsync` 之后、`ClaimAsync` 之前插入：

```csharp

    /// <summary>
    /// 标记事件为已处理
    /// </summary>
    /// <remarks>
    /// 记录保留在库中，保留期满后由 <see cref="DeleteOldEventsAsync"/> 清理。
    /// </remarks>
    /// <param name="id">事件唯一标识符</param>
    public async Task MarkAsProcessedAsync(Guid id)
    {
        await MarkAsHandledAsync(id, SysEventInbox.StatusProcessed);
    }

    /// <summary>
    /// 延迟处理事件
    /// </summary>
    /// <remarks>
    /// 记录放回待处理并清空领取信息，下次重试时刻为空时立即可领取。
    /// </remarks>
    /// <param name="id">事件唯一标识符</param>
    /// <param name="retryCount">重试次数</param>
    /// <param name="nextRetryTime">下次重试时间</param>
    public async Task RetryLaterAsync(Guid id, int retryCount, DateTime? nextRetryTime)
    {
        var nextRetry = nextRetryTime.HasValue
            ? EventInboxMapper.ToOffset(nextRetryTime.Value)
            : DateTimeOffset.UtcNow;

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            await client.Updateable<SysEventInbox>()
                .SetColumns(item => new SysEventInbox
                {
                    Status = SysEventInbox.StatusPending,
                    RetryCount = retryCount,
                    NextRetryTime = nextRetry,
                    ClaimToken = null,
                    ClaimTime = null
                })
                .Where(item => item.BasicId == id)
                .ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 标记事件为已丢弃
    /// </summary>
    /// <remarks>
    /// 记录保留在库中，保留期满后由 <see cref="DeleteOldEventsAsync"/> 清理。
    /// </remarks>
    /// <param name="id">事件唯一标识</param>
    public async Task MarkAsDiscardAsync(Guid id)
    {
        await MarkAsHandledAsync(id, SysEventInbox.StatusDiscarded);
    }

    /// <summary>
    /// 删除过期事件
    /// </summary>
    /// <remarks>
    /// 只删除已处理或已丢弃、且完结时刻早于保留期的记录，保留期由
    /// <see cref="XiHanSqlSugarEventBoxOptions.InboxRetentionPeriod"/> 配置。
    /// </remarks>
    public async Task DeleteOldEventsAsync()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.InboxRetentionPeriod;

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            await client.Deleteable<SysEventInbox>()
                .Where(item => (item.Status == SysEventInbox.StatusProcessed || item.Status == SysEventInbox.StatusDiscarded)
                    && item.HandledTime != null
                    && item.HandledTime <= cutoff)
                .ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 把记录置为完结状态并清空领取与重试信息
    /// </summary>
    /// <param name="id">事件唯一标识符</param>
    /// <param name="status">完结状态</param>
    private async Task MarkAsHandledAsync(Guid id, int status)
    {
        var now = DateTimeOffset.UtcNow;

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            await client.Updateable<SysEventInbox>()
                .SetColumns(item => new SysEventInbox
                {
                    Status = status,
                    NextRetryTime = null,
                    ClaimToken = null,
                    ClaimTime = null,
                    HandledTime = now
                })
                .Where(item => item.BasicId == id)
                .ExecuteCommandAsync();
        }
    }
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `标记已处理后不再被领取且仍判定为存在` 的 `ClaimToken` 断言失败（仍有值）：说明 `SetColumns` 里的 `ClaimToken = null` 没有生效，核对是否写在成员初始化里而非遗漏。若 `清理只删除超过保留期的已完结记录` 剩下的不是恰好两条：检查清理条件是否限定了状态、是否按 `HandledTime` 而非 `CreatedTime` 判断。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 收件箱状态流转与过期清理" -m "标记已处理、已丢弃只改状态不删行：去重靠这些记录，处理完就删会让 broker 重投的消息再被处理一遍。清理只删已完结且完结时刻早于保留期的记录，积压的待处理记录无论多旧都保留。重试次数照宿主传入值记录，丢弃判定留在宿主服务。"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 注册与真实数据库并发验证

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxRegistrationTests.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxConcurrencyTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarEventInbox : IEventInbox`
- Produces: `AddXiHanSqlSugarEventBus` 追加两处收件箱注册；签名不变

**参考来源（动手前先读）：**
- 主包注册：`framework/src/XiHan.Framework.EventBus/Extensions/DependencyInjection/XiHanEventBusServiceCollectionExtensions.cs:76-120`
- 发件箱的注册测试：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxRegistrationTests.cs`
- 真库范式：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`
- spec §4.9

**本任务禁止事项：** 硬约束（spec §5 ⑨）：`IEventInbox` 必须 `Replace`，**不能** `TryAdd`。不新增扩展方法，不改扩展方法签名。不改动发件箱的两行注册。模块类只改 `<remarks>` 一句，不加任何逻辑。

- [ ] **Step 1: 写失败的注册测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxRegistrationTests.cs`：

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
using XiHan.Framework.EventBus.SqlSugar.Inbox;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱注册测试
/// </summary>
public class InboxRegistrationTests
{
    /// <summary>
    /// 收件箱实现类型被顶替
    /// </summary>
    [Fact]
    public void 收件箱实现类型被顶替()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<XiHanDistributedEventBusOptions>>().Value;

        Assert.Equal(typeof(SqlSugarEventInbox), options.Inboxes["Default"].ImplementationType);
    }

    /// <summary>
    /// 收件箱接口注册被顶替为作用域
    /// </summary>
    [Fact]
    public void 收件箱接口注册被顶替为作用域()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IEventInbox));

        Assert.Equal(typeof(SqlSugarEventInbox), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 收件箱具体类型注册为作用域
    /// </summary>
    [Fact]
    public void 收件箱具体类型注册为作用域()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(SqlSugarEventInbox));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 发件箱注册不受收件箱注册影响
    /// </summary>
    [Fact]
    public void 发件箱注册不受收件箱注册影响()
    {
        var services = BuildServicesWithDefaults();

        services.AddXiHanSqlSugarEventBus(new ConfigurationBuilder().Build());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<XiHanDistributedEventBusOptions>>().Value;
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IEventOutbox));

        Assert.Equal(typeof(SqlSugarEventOutbox), options.Outboxes["Default"].ImplementationType);
        Assert.Equal(typeof(SqlSugarEventOutbox), descriptor.ImplementationType);
    }

    /// <summary>
    /// 模拟事件总线模块已注册的收发件箱默认值
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

            options.Inboxes.Configure(config =>
            {
                if (config.ImplementationType == default)
                {
                    config.ImplementationType = typeof(DefaultEventInbox);
                }
            });
        });

        services.TryAddSingleton<DefaultEventOutbox>();
        services.TryAddSingleton<DefaultEventInbox>();
        services.TryAddSingleton<IEventOutbox>(sp => sp.GetRequiredService<DefaultEventOutbox>());
        services.TryAddSingleton<IEventInbox>(sp => sp.GetRequiredService<DefaultEventInbox>());

        return services;
    }
}
```

- [ ] **Step 2: 写真实数据库并发测试**

新建 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/InboxConcurrencyTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Inbox;
using XiHan.Framework.EventBus.SqlSugar.Options;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 收件箱并发测试，需要真实数据库
/// </summary>
/// <remarks>
/// 地址取环境变量 <c>XIHAN_TEST_MYSQL</c>，未设置时整类跳过。
/// </remarks>
public class InboxConcurrencyTests
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
        setupClient.CodeFirst.InitTables(typeof(SysEventInbox));
        await setupClient.Deleteable<SysEventInbox>().ExecuteCommandAsync();

        var setupInbox = CreateInbox(setupClient);
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        for (var index = 0; index < eventCount; index++)
        {
            await setupInbox.EnqueueAsync(new IncomingEventInfo(
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                "Order.Paid",
                [1],
                baseTime.AddSeconds(index)));
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            using var client = CreateClient();
            var inbox = CreateInbox(client);
            var claimedIds = new List<Guid>();

            while (true)
            {
                var batch = await inbox.GetWaitingEventsAsync(10);
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

        await setupClient.Deleteable<SysEventInbox>().ExecuteCommandAsync();
    }

    /// <summary>
    /// 并发以同一消息标识入箱时只保留一条且都不抛异常
    /// </summary>
    [Fact]
    public async Task 并发入箱同一消息只保留一条()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        const int workerCount = 8;
        var messageId = Guid.NewGuid().ToString("N");

        using var setupClient = CreateClient();
        setupClient.CodeFirst.InitTables(typeof(SysEventInbox));
        await setupClient.Deleteable<SysEventInbox>().ExecuteCommandAsync();

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            using var client = CreateClient();
            var inbox = CreateInbox(client);

            await inbox.EnqueueAsync(new IncomingEventInfo(
                Guid.NewGuid(),
                messageId,
                "Order.Paid",
                [1],
                DateTime.UtcNow));
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(1, await setupClient.Queryable<SysEventInbox>().CountAsync(item => item.DedupKey == messageId));

        await setupClient.Deleteable<SysEventInbox>().ExecuteCommandAsync();
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

    private static SqlSugarEventInbox CreateInbox(SqlSugarClient client)
    {
        var clients = new Dictionary<string, SqlSugarClient>(StringComparer.Ordinal)
        {
            [InboxTestContext.MainConfigId] = client
        };
        var resolver = new StubClientResolver(clients, [InboxTestContext.MainConfigId], InboxTestContext.MainConfigId);

        return new SqlSugarEventInbox(
            resolver,
            new FakeCurrentTenant(),
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarEventBoxOptions
            {
                ClaimTimeout = TimeSpan.FromMinutes(5)
            }),
            NullLogger<SqlSugarEventInbox>.Instance);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`收件箱实现类型被顶替`（仍是 `DefaultEventInbox`）、`收件箱接口注册被顶替为作用域`（描述符是工厂注册的单例，`ImplementationType` 为 `null`）、`收件箱具体类型注册为作用域`（`Assert.Single` 找不到）三个失败；`发件箱注册不受收件箱注册影响` 通过；两个真库用例显示为 skipped。

- [ ] **Step 4: 追加注册**

把 `framework/src/XiHan.Framework.EventBus.SqlSugar/Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Inbox;
using XiHan.Framework.EventBus.SqlSugar.Options;
using XiHan.Framework.EventBus.SqlSugar.Outbox;

namespace XiHan.Framework.EventBus.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 事件总线 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanSqlSugarEventBusServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 收发件箱替换默认的进程内收发件箱
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
            options.Inboxes.Configure(config => config.ImplementationType = typeof(SqlSugarEventInbox));
        });

        services.TryAddScoped<SqlSugarEventOutbox>();
        services.Replace(ServiceDescriptor.Scoped<IEventOutbox, SqlSugarEventOutbox>());

        services.TryAddScoped<SqlSugarEventInbox>();
        services.Replace(ServiceDescriptor.Scoped<IEventInbox, SqlSugarEventInbox>());

        return services;
    }
}
```

- [ ] **Step 5: 改模块类的说明**

修改 `framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`，把 `<remarks>` 中这一行：

```csharp
/// 本模块以 SqlSugar 发件箱替换默认的进程内发件箱；收件箱在后续版本提供。
```

改为：

```csharp
/// 本模块以 SqlSugar 收发件箱替换默认的进程内收发件箱。
```

其余一行都不动。

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，三个真库用例（发件箱 1 个、收件箱 2 个）在未设 `XIHAN_TEST_MYSQL` 时显示为 skipped。`OutboxRegistrationTests` 保持全绿。

若 `收件箱接口注册被顶替为作用域` 找到两个描述符：说明用了 `Add` 而非 `Replace`。若 `ImplementationType` 仍是 `DefaultEventInbox`：检查 `Inboxes.Configure` 是否写在了 `Configure<XiHanDistributedEventBusOptions>` 的动作里。

- [ ] **Step 7: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 以 SqlSugar 收件箱替换默认的进程内收件箱" -m "主包以 TryAddSingleton 注册 IEventInbox，TryAdd 顶替是空操作，因此用 Replace；options 的 ImplementationType 后写覆盖，直接赋值。生命周期由单例改为作用域：框架内两处消费点都从新建作用域按 ImplementationType 解析，没有构造函数注入该接口。"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 5: 文档站条目、模块清单与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`（整份替换）
- Modify: `docs/packages/eventbus-sqlsugar.md`（整份替换）
- Modify: `framework/README.md`（第 92 行）
- Modify: `framework/README_cn.md`（第 92 行）
- 核对不改：`docs/.vitepress/config.ts`、根 `README.md`、根 `README_cn.md`

**Interfaces:**
- Consumes: 前四个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 现有页面：`docs/packages/eventbus-sqlsugar.md`——它只写了 P3/P4 的发件箱，第 19 行称收件箱不在范围内，第 82 行与第 124 行仍是 P6 之前的单库描述
- 已知边界：spec §7
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`
- 同类页面范式：`docs/packages/auditing-sqlsugar.md`

**本任务禁止事项：** **不要**新建文档站页面或侧边栏条目——都已存在。**不要**改 `docs/packages/eventbus.md` 或其他包的文档——一个 PR 只做一件事。**不要**改根 `README.md` / `README_cn.md`（spec 决策 D14）。**不要**把权衡论证写进代码注释。

- [ ] **Step 1: 替换包 README**

把 `framework/src/XiHan.Framework.EventBus.SqlSugar/README.md` 整份替换为：

````markdown
# XiHan.Framework.EventBus.SqlSugar

## 概述

`XiHan.Framework.EventBus` 的收发件箱 SqlSugar 持久化提供程序。默认的 `DefaultEventOutbox` / `DefaultEventInbox` 是进程内实现：发件箱的事件与业务数据不在同一事务、进程退出即丢；收件箱的去重与待处理事件都在内存里，多实例下同一事件会被每个实例各处理一次。本包把两者落到数据库。

## 核心能力

- 发件箱实体 `sys_event_outbox` 与 `OutgoingEventInfo` 的双向映射
- 入箱与业务数据落在同一事务、同一个库：业务写在哪个库，事件行就写在哪个库
- 发送端遍历当前布局的全部库，单个库不可达时只跳过该库
- 收件箱实体 `sys_event_inbox` 与 `IncomingEventInfo` 的双向映射
- 收件箱按消息标识去重：入箱前先查，唯一索引兜住多实例同时入箱的竞态
- 收发件箱的领取都是多实例互斥：条件抢占 + 领取超时释放，不依赖任何数据库方言特性
- 已处理与已丢弃的收件箱记录保留一段时间后清理，保留期即去重窗口
- 表结构由 `DbInitializer` 在应用启动时创建，**必须开启** `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization`（均默认 `false`）

## 依赖关系

依赖 `XiHan.Framework.EventBus`（收发件箱契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为事件自身的 `Guid` 标识，非自增。

两张表都不是分表，没有 SqlSugar 插入时自动建表的兜底，因此未开启建表初始化时首次入箱即抛「表不存在」：发件箱会使所在业务事务一同失败，收件箱会使该条消息消费失败并由 broker 重投。自行维护表结构时按本包实体的列定义建表，收件箱还需建出 `Dedup_Key` 列上的唯一索引 `ux_sys_event_inbox_dedup_key`。

配置节 `XiHan:EventBus:SqlSugar`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `ClaimTimeout` | `00:05:00` | 领取超时，超过该时长仍未完结的已领取记录可被重新领取，收发件箱共用 |
| `InboxRetentionPeriod` | `7.00:00:00` | 收件箱已处理与已丢弃记录的保留期，超过该时长的记录被清理 |

本包把 `IEventOutbox` 与 `IEventInbox` 的注册生命周期都由单例改为作用域——两者都经作用域内的 `ISqlSugarClientResolver` 取连接。框架内没有构造函数注入这两个接口的地方。

### 发件箱

投递语义为**至少一次**：宿主在投递成功后才删除记录，若进程在投递与删除之间退出，记录会被重新领取并再次投递，消费端需幂等。

删除按库执行，某个库删除失败时只记录日志并跳过，不会抛给调用方；该库上的记录留在原地，之后每次轮询都会被重新领取、重新投递，直到该库恢复为止。

事件行的落库由当前工作单元已登记的连接决定：恰好一个时写该库，一个都没有时写当前库，多于一个时抛 `InvalidOperationException`。因此业务代码应**先写业务数据、后发布事件**——反过来会让事件落在主库而业务落在模块库，两者不在同一个事务里，且不会报错。

领取配额在当前布局的各库间平均分配：每库最多领取 `maxCount` 除以库数的整数商，且每库至少领取 1 条；库数超过 `maxCount` 时，单次领取的总量等于库数。

发件箱表由 `[TableInitialization(IncludeModuleConnections = true)]` 声明进入所有库，主库与每个模块库都会建出 `sys_event_outbox`。

从旧版本升级的既有部署，本版本会开始把事件行按业务落库位置路由进各模块库，因此升级前须确保 `sys_event_outbox` 在每个模块库中已存在——开启上述两个自动建表选项，或手工在各模块库中建出该表。二者都没做时，第一次向该模块库写入的事件会在建表失败（缺表）时报错，且这次失败发生在业务事务内部。

主库与静态配置的模块库在 `SqlSugarScope` 构建时一并建连，进程重启后不存在「主库先建连、模块库延后建连」的时间差，待发事件不会因此暂时无法被领取。

发送循环运行在无租户上下文的后台作用域，只遍历默认布局，租户独立库的发件箱不在本包范围。

### 收件箱

收件箱的全部读写都切换到无租户上下文，落在宿主布局的主库；`sys_event_inbox` 只在平台主库建表，不进模块库与租户独立库。库隔离租户收到的事件也存在这里。

去重键是消息标识，不是事件标识——框架在每次收到消息时都会生成新的事件标识。没有消息标识的消息不参与去重，每次都入箱、都处理。

去重窗口等于保留期：已处理记录清理之后，同一消息再来会被当作新消息处理。处理器仍须幂等。

标记已处理、已丢弃只改状态、不删记录；清理只删除已处理或已丢弃、且完结时刻早于保留期的记录，待处理与已领取的记录无论多旧都保留。宿主每轮轮询都会调用一次清理。

重试次数由宿主服务在进程内累计并传入，本包只把它记在 `Retry_Count` 列上；是否丢弃由宿主按 `XiHan:EventBus:EventBoxes:MaxInboxRetryCount` 判定。多实例轮流领到同一条失败事件、或进程重启后，计数从 1 重来。

宿主逐条顺序处理一批事件。整批处理耗时超过 `ClaimTimeout` 时，尾部记录会被其他实例重新领取并处理，因此 `ClaimTimeout` 须大于单批最长处理时间。

消息标识最长 256 个字符，更长的消息标识在严格模式的数据库上入箱失败。

## 使用方式

在应用启动模块上声明依赖 `XiHanSqlSugarEventBusModule`。

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.EventBus.Abstractions.Distributed` 下的 `IEventOutbox` / `IEventInbox` 并在 DI 中 `Replace`，同时把 `XiHanDistributedEventBusOptions.Outboxes` / `Inboxes` 的 `ImplementationType` 指向自己的类型。

## 目录结构

```
Entities/                        收发件箱实体
Mapping/                         契约与实体的双向映射
Options/                         存储配置
Outbox/                          发件箱实现
Inbox/                           收件箱实现
Extensions/DependencyInjection/  服务注册扩展
```
````

- [ ] **Step 2: 替换文档站页面**

把 `docs/packages/eventbus-sqlsugar.md` 整份替换为：

````markdown
# XiHan.Framework.EventBus.SqlSugar

> 事件收发件箱的 SqlSugar 持久化提供程序：发件箱入箱与业务数据落在同一事务、同一个库；收件箱按消息标识去重；两者的领取都是多实例互斥。替换 [EventBus](./eventbus) 的进程内收发件箱后，outbox / inbox 模式才真正成立。

- **NuGet**：`XiHan.Framework.EventBus.SqlSugar`
- **模块类**：`XiHanSqlSugarEventBusModule`
- **所在层**：基础设施层
- **关键依赖**：[EventBus](./eventbus)（收发件箱契约）、[Data](./data)（SqlSugar 客户端、工作单元连接登记、建表）

## 概述

[EventBus](./eventbus) 实现了完整的收发件箱骨架：发件箱在工作单元内入箱，后台服务轮询取出、投递、删除；收件箱在收到消息时入箱，后台服务轮询取出、调用处理器、标记结果。但它的 `IEventOutbox` / `IEventInbox` 默认实现都是进程内字典——事件与业务数据不在同一事务，进程退出即丢，去重与待处理事件跨实例也不共享。

本包把两者落到数据表，补上这些保证：

- **发件箱入箱与业务同事务**：业务回滚，事件随之消失；业务提交，事件必然在库。业务写在模块库时，事件也写在那个模块库
- **收件箱去重**：同一消息被 broker 重复投递，只处理一次（在保留期内）
- **多实例领取互斥**：N 个实例同时轮询，同一条记录只会被一个实例领走

## 何时使用

- 用了分布式事件总线，且要求「业务成功才发事件、业务失败绝不发事件」
- 消费端要求同一消息不被重复处理，且应用以多实例部署
- 已在用 [Data](./data)，希望收发件箱与业务数据走同一套连接

不需要本包的场景：单实例、事件可丢、或事件与业务数据本就不要求一致。

## 安装与启用

```bash
dotnet add package XiHan.Framework.EventBus.SqlSugar
```

在启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanSqlSugarEventBusModule))]
public class YourAppModule : XiHanModule
{
}
```

建表需要开启 [Data](./data) 的库初始化与建表初始化，**两者默认都是关闭的**：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableDbInitialization": true,
        "EnableTableInitialization": true
      }
    }
  }
}
```

两张表都不是分表，没有 SqlSugar 插入时自动建表的兜底。未开启建表初始化又没有手工建表时，首次入箱即抛「表不存在」：发件箱会使所在业务事务一同失败，收件箱会使该条消息消费失败并由 broker 重投。

## 表结构

两张表都**不分表**。

### `sys_event_outbox`

发件箱是短命队列，投递成功即删除。主库与每个模块库都会建出这张表（`[TableInitialization(IncludeModuleConnections = true)]`）。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `Guid`，主键，非自增 | 事件唯一标识，直接取 `OutgoingEventInfo.Id` |
| `Row_Version` | `long` | 并发标识 |
| `Event_Name` | `string(256)`，非空 | 事件名 |
| `Event_Data` | `byte[]`，非空 | 序列化后的事件数据 |
| `Created_Time` | `DateTimeOffset`，非空 | 事件创建时间 |
| `Extra_Properties` | 大文本，可空 | 扩展属性的 JSON |
| `Status` | `int`，非空 | 0 待发送，1 已领取 |
| `Claim_Token` | `string(64)`，可空 | 领取令牌 |
| `Claim_Time` | `DateTimeOffset`，可空 | 领取时刻，用于超时释放 |

### `sys_event_inbox`

收件箱处理完不删除，保留一段时间承担去重。只在平台主库建表（`[TableInitialization(Target = DbInitializationTarget.Platform)]`）。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `Guid`，主键，非自增 | 事件唯一标识，直接取 `IncomingEventInfo.Id` |
| `Row_Version` | `long` | 并发标识 |
| `Message_Id` | `string(256)`，可空 | 原始消息标识 |
| `Dedup_Key` | `string(256)`，非空，**唯一索引** | 去重键：有消息标识时等于它，没有时由事件标识生成 |
| `Event_Name` | `string(256)`，非空 | 事件名 |
| `Event_Data` | `byte[]`，非空 | 序列化后的事件数据 |
| `Created_Time` | `DateTimeOffset`，非空 | 入箱时刻 |
| `Extra_Properties` | 大文本，可空 | 扩展属性的 JSON |
| `Status` | `int`，非空 | 0 待处理，1 已领取，2 已处理，3 已丢弃 |
| `Retry_Count` | `int`，非空 | 重试次数 |
| `Next_Retry_Time` | `DateTimeOffset`，可空 | 早于该时刻不领取 |
| `Claim_Token` | `string(64)`，可空 | 领取令牌 |
| `Claim_Time` | `DateTimeOffset`，可空 | 领取时刻，用于超时释放 |
| `Handled_Time` | `DateTimeOffset`，可空 | 进入已处理或已丢弃的时刻，保留期由此起算 |

索引：`ux_sys_event_inbox_dedup_key`（`Dedup_Key`，唯一）、`ix_sys_event_inbox_status`（`Status, Created_Time`）。

两张表的主键都用事件自身的 `Guid` 而非雪花 `long`：契约按 `Guid` 定位记录。该 `Guid` 由框架的顺序 Guid 生成器产出，不会造成索引碎片。

## 工作原理

### 发件箱入箱

`DistributedEventBusBase` 在工作单元的作用域内解析发件箱实现，因此 `SqlSugarEventOutbox` 注册为 `Scoped`。事件行的落库由当前工作单元已登记的连接决定：

| 已登记的连接 | 落点 |
| --- | --- |
| 0 个 | 当前库 |
| 1 个 | 该库，与业务数据同一个事务 |
| 多于 1 个 | 抛 `InvalidOperationException` |

### 收件箱入箱与去重

收件箱在收到消息时入箱，此时没有业务工作单元，也没有「业务所在的库」。`SqlSugarEventInbox` 的全部读写都切换到无租户上下文，落在宿主布局的主库——与同样无租户上下文的处理循环读写同一个库。

去重分两层：

1. 入箱前 `ExistsByMessageIdAsync` 按消息标识查重，命中即视为已处理
2. 两个实例同时收到同一消息、都查不到时，`Dedup_Key` 的唯一索引只放行一个；另一个的插入失败后，本包按去重键回查，确认已在库就按重复处理，否则原样抛出

去重键是消息标识而不是事件标识——框架在每次收到消息时都会生成新的事件标识。

### 领取

宿主的发送循环与处理循环都没有分布式锁。去重由本包的领取实现完成，分三步，全部使用方言无关的表达式 API：

1. 查出可领取记录的主键，按创建时间升序取一批
2. 条件 `UPDATE` 抢占这批主键，`WHERE` 里重复可领取条件
3. 按本次令牌取回真正抢到的记录

第 2 步的每行 `UPDATE` 是原子的，两个并发领取者只有一个能把某行从可领取改成已领取。候选全被抢走时另选一批重试，最多三轮。

可领取条件：发件箱是「待发送，或已领取但超时」；收件箱是「待处理且下次重试时刻为空或已到，或已领取但超时」。

发件箱的领取遍历当前布局的全部库（主库与已建连的模块库），每库配额为 `maxCount` 除以库数、至少 1 条，单个库不可达时记录日志并跳过。收件箱只有一个库，不遍历。

不使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT` 等方言特性——本框架是通用类库，代价是多一次往返。

### 超时释放

`Claim_Time` 早于「当前时刻 − 领取超时」的已领取记录重新变为可领取，避免实例异常退出导致记录永久滞留。

### 收件箱的状态流转与清理

| 方法 | 结果 |
| --- | --- |
| `MarkAsProcessedAsync` | 已处理，写入 `Handled_Time`，**不删除** |
| `MarkAsDiscardAsync` | 已丢弃，写入 `Handled_Time`，**不删除** |
| `RetryLaterAsync` | 回到待处理，清空领取信息，记录重试次数与下次重试时刻 |
| `DeleteOldEventsAsync` | 删除已处理或已丢弃、且 `Handled_Time` 早于保留期的记录 |

重试次数由宿主服务在进程内累计，达到 `MaxInboxRetryCount` 时由宿主调用 `MarkAsDiscardAsync`。

## 配置

配置节 `XiHan:EventBus:SqlSugar`（`XiHanSqlSugarEventBoxOptions.SectionName`）。

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `ClaimTimeout` | `TimeSpan` | `00:05:00` | 领取超时，超过该时长仍未完结的已领取记录可被重新领取，收发件箱共用 |
| `InboxRetentionPeriod` | `TimeSpan` | `7.00:00:00` | 收件箱已处理与已丢弃记录的保留期，也是去重窗口 |

轮询间隔、批量大小、收件箱最大重试次数与重试延迟属于 [EventBus](./eventbus) 的 `XiHan:EventBus:EventBoxes`，本包不改动。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanSqlSugarEventBusModule` | 模块类，声明依赖即启用 |
| `SqlSugarEventOutbox` | `IEventOutbox` 的 SqlSugar 实现，注册为 `Scoped` |
| `SqlSugarEventInbox` | `IEventInbox` 的 SqlSugar 实现，注册为 `Scoped` |
| `SysEventOutbox` / `SysEventInbox` | 收发件箱实体 |
| `EventOutboxMapper` / `EventInboxMapper` | 契约与实体的双向映射 |
| `XiHanSqlSugarEventBoxOptions` | 领取超时与收件箱保留期配置 |

## 注意事项与最佳实践

- **投递与处理都是至少一次**。发件箱在投递成功后才删除记录；收件箱在处理成功后才标记已处理。进程在两步之间退出，记录会被重新领取。处理器必须幂等。
- **先写业务数据、后发布事件**。发件箱按工作单元已登记的连接决定落点；先发布再写业务，事件会落在主库而业务落在模块库，两者不在同一个事务里，且不会报错。
- **一个工作单元只写一个库**。登记了多个连接时发件箱入箱抛异常，出路是拆小工作单元。
- **收件箱的去重窗口等于保留期**。清理之后同一消息再来会被当作新消息处理；没有消息标识的消息不参与去重。
- **`ClaimTimeout` 要大于单批最长处理时间**。宿主逐条顺序处理一批事件，整批超时后尾部记录会被其他实例重新领取。
- **收件箱的重试计数在进程内**。多实例轮流领到同一条失败事件、或进程重启后，计数从 1 重来，实际重试次数可能超过 `MaxInboxRetryCount`。
- **`IEventOutbox` / `IEventInbox` 的生命周期由单例改为作用域**。框架内没有构造函数注入这两个接口的地方，不会产生被捕获依赖；应用若自行把它们注入单例，需要改为从作用域解析。
- **`GetWaitingEventsAsync` 是领取不是查询**。调用后记录已被标记为已领取，不要在别处当作只读查询复用。
- **`filter` 参数未支持**。传入非空值会抛 `NotSupportedException`，而不是静默忽略。
- **租户独立库不在范围内**。发件箱的发送循环只遍历默认布局；收件箱的全部记录都在宿主布局主库。

## 扩展点 / 自定义

需要完全自定义存储行为时，实现 `IEventOutbox` / `IEventInbox` 并在 DI 中 `Replace`，同时把 `XiHanDistributedEventBusOptions.Outboxes` / `Inboxes` 的 `ImplementationType` 指向自己的类型。

## 依赖模块

- [EventBus](./eventbus)：收发件箱契约与后台循环
- [Data](./data)：SqlSugar 客户端解析、工作单元连接登记、建表初始化

## 相关模块

- [EventBus.RabbitMQ](./eventbus-rabbitmq) / [EventBus.Kafka](./eventbus-kafka) / [EventBus.Redis](./eventbus-redis)：投递用的 Broker 提供程序，与本包正交——本包管「事件怎么存」，它们管「事件怎么发出去、怎么收进来」
- [Uow](./uow)：发件箱入箱所参与的工作单元
- [Auditing.SqlSugar](./auditing-sqlsugar)：同一套落库范式的审计日志实现
````

- [ ] **Step 3: 更新模块清单**

`framework/README.md` 第 92 行由：

```markdown
| `EventBus.SqlSugar` | SqlSugar persistence provider for the event outbox: enqueue joins the business transaction, claiming is mutually exclusive across instances |
```

改为：

```markdown
| `EventBus.SqlSugar` | SqlSugar persistence provider for the event outbox and inbox: outbox enqueue joins the business transaction, the inbox deduplicates by message id, claiming is mutually exclusive across instances |
```

`framework/README_cn.md` 第 92 行由：

```markdown
| `EventBus.SqlSugar` | 事件发件箱 SqlSugar 持久化提供程序：入箱与业务同事务，多实例领取互斥 |
```

改为：

```markdown
| `EventBus.SqlSugar` | 事件收发件箱 SqlSugar 持久化提供程序：发件箱入箱与业务同事务，收件箱按消息标识去重，多实例领取互斥 |
```

改之前先 `sed -n 92p` 确认行号仍对应该条目；若不对，按条目内容定位。

- [ ] **Step 4: 核对不改的三处**

```bash
grep -n 'eventbus-sqlsugar' docs/.vitepress/config.ts
grep -n 'Auditing.SqlSugar\|EventBus.SqlSugar' README.md README_cn.md
```

预期：第一条命中 `pkg("EventBus.SqlSugar", "eventbus-sqlsugar")`（侧边栏已存在，不改）；第二条**无输出**——根 README 的表格是「常用包」，两个 SqlSugar 子包都不在其中，维持现状（spec 决策 D14）。若第二条有输出，说明有人已把 SqlSugar 子包加进了根 README，此时照同样格式补上收件箱的描述，并在 PR 描述里说明。

- [ ] **Step 5: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过（真库测试在无环境变量时跳过）。`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败是已知的无关抖动，重跑即可，不要去追它。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`），先结束残留的测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.EventBus.SqlSugar.Tests.exe"
```

- [ ] **Step 6: 本机真库验证**

准备一个可用的 MySQL 实例并设置环境变量：

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：`InboxConcurrencyTests.并发领取时记录不重复` 与 `InboxConcurrencyTests.并发入箱同一消息只保留一条` PASS，发件箱的 `并发领取时记录不重复` 同样 PASS。

**若领取用例出现重复标识**，说明抢占的 `Where` 丢了可领取条件（硬约束 ④）——回到 Task 2 Step 7 核对，不要通过放宽断言来让它变绿。**若入箱用例抛出异常**，说明回查没有识别出重复——核对 `catch` 里的回查条件是否用的是 `DedupKey`。**若入箱用例库里多于一条**，说明唯一索引没有建出来——在 MySQL 上执行 `SHOW INDEX FROM sys_event_inbox` 确认 `ux_sys_event_inbox_dedup_key` 存在且 `Non_unique = 0`。

- [ ] **Step 7: 注释复查**

通读本计划新建与改动过的全部 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即移出到提交信息。

`ClaimAsync` 里那一行 `// 候选全被其他实例抢走，另选一批重试` 描述的是这段代码做什么，保留。

- [ ] **Step 8: 提交**

```bash
git add framework/src/XiHan.Framework.EventBus.SqlSugar/README.md docs/packages/eventbus-sqlsugar.md framework/README.md framework/README_cn.md
git commit -m "docs(eventbus-sqlsugar): 补写收件箱的文档站条目与模块清单" -m "文档站页面原先只写了单库发件箱：补上收件箱、P6 的多库落点与遍历，以及两个建表开关都需开启。根 README 的表格是常用包清单，SqlSugar 子包均不在其中，维持现状。"
```

---

## 完成标准

P7 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真库测试在无环境变量时跳过）
- `SysEventInbox` 带 `[TableInitialization(Target = DbInitializationTarget.Platform)]` 与 `Dedup_Key` 上的唯一索引，SQLite 上重复去重键的第二次插入失败
- 两个 `Id` 不同、`MessageId` 相同的入箱只留一条，不抛异常
- 两个无消息标识的入箱留两条
- 插入因非重复原因失败时原样抛出
- 租户上下文中的入箱、判重、领取都作用于宿主主库，且调用后恢复原租户
- 领取标记并带令牌、不重复领取、超时后可重新领取、尊重 `NextRetryTime`、跳过已处理与已丢弃、按创建时间升序且不超上限
- 标记已处理 / 已丢弃后记录仍在库、`ExistsByMessageIdAsync` 为 `true`、不再被领取
- `RetryLaterAsync` 把记录放回待处理、清空领取信息、记录次数
- 清理只删除超过保留期的已完结记录
- `IEventInbox` 与 `Inboxes["Default"].ImplementationType` 都是 `SqlSugarEventInbox`，生命周期 `Scoped`；发件箱注册不受影响
- 本机配置 `XIHAN_TEST_MYSQL` 后两个收件箱并发用例通过
- 包 README、`docs/packages/eventbus-sqlsugar.md`、`framework/README.md`、`framework/README_cn.md` 反映收件箱
- 所有提交信息为中文 Conventional Commits、作用域 `eventbus-sqlsugar`、**无任何 AI 署名**

## 已知边界（写入 PR 描述，不写进代码注释）

- **去重窗口等于保留期**：已处理记录在 `InboxRetentionPeriod`（默认 7 天）后被清理，此后同一消息再来会被当作新消息处理。
- **无消息标识不去重**：没有 `MessageId` 的消息每次都入箱、都处理。
- **重试计数在进程内**：宿主服务按实例、按进程生命周期累计。多实例轮流领到同一条失败事件、或进程重启后，计数从 1 重来，实际重试次数可能超过 `MaxInboxRetryCount`。
- **处理超时会重复处理**：整批处理耗时超过 `ClaimTimeout` 时，尾部记录会被其他实例重新领取。
- **至少一次处理**：处理成功与标记已处理之间进程退出，记录在领取超时后被重新处理。处理器须幂等。
- **在事务型工作单元内入箱**：若调用方在未完成的事务型工作单元内触发入箱，写入会登记进该事务；主库被登记后，同一工作单元内的发件箱入箱可能因「登记了多个连接」而抛异常。正常路径（broker 消费、工作单元提交后发布）不触发。
- **唯一冲突在事务内的回查**：上一条的情形下，PostgreSQL 在唯一冲突后整个事务进入中止状态，回查本身会失败，抛出的是回查的异常。
- **消息标识长度**：最长 256 个字符，更长的在严格模式的数据库上入箱失败。
- **租户独立库**：收件箱的全部记录在宿主布局主库。
- **清理不节流**：宿主每轮（默认 2 秒）每个实例都执行一次条件 `DELETE`。
- **破坏性变更——`IEventInbox` 生命周期由单例改为作用域**：框架内无构造函数注入；应用若自行注入单例会得到被捕获依赖。**没有配置层面的逃生口**，不想要该变更的应用只能不引用本模块。
- **破坏性变更——新增表 `sys_event_inbox`**：升级前开启 `EnableDbInitialization` 与 `EnableTableInitialization`，或手工建表（含唯一索引 `ux_sys_event_inbox_dedup_key`）。缺表时收件箱入箱抛异常，broker 重投，而不是静默丢失。
- **`filter` 未支持**：非 `null` 抛 `NotSupportedException`。

## 下一份计划

P7 是 `EventBus.SqlSugar` 的最后一份，完成后第二个 PR（`EventBus.SqlSugar`：P3、P4、P6、P7）方可提交。

系列的后续按 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 5 节的顺序，下一个是 `Settings.SqlSugar`（spec 与计划待写）。`Tasks.SqlSugar` 的作业领取可直接复用本份与 P4 的三步抢占协议。
