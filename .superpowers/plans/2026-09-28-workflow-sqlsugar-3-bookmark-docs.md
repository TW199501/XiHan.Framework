# Workflow.SqlSugar 第 3 份：书签存储、引擎端到端测试与文档站收尾 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以 SqlSugar 实现替换 `IWorkflowBookmarkStore`（10 个方法），加上书签消费守卫（`DeleteAsync` 删到 0 行抛 `WorkflowException`）与进程内锁的启动警告，用真实 `WorkflowEngine` 跑通六个端到端流程与一个双节点竞争用例，并完成新包的全部登记与文档。

**Architecture:** 一张表 `sys_workflow_bookmark`，四个索引覆盖全部查询（实例、节点实例、到期时间、种类+键+创建时间）。按种类与键匹配的两个查询在数据库条件之后再按序数比较过滤一遍，使匹配区分大小写且不受数据库排序规则影响。所有读写经第 1 份的 `WorkflowSqlSugarExecutor`。端到端测试用 `AddXiHanWorkflow` + `AddXiHanWorkflowSqlSugar` 组装真实引擎，存储落在临时 SQLite。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform、VitePress（文档站）

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-workflow-sqlsugar-3-bookmark-docs-design.md`

**Linear 议题:** https://linear.app/elf-express/issue/EDDIE-14

**前置:** 第 1 份（`.superpowers/plans/2026-09-28-workflow-sqlsugar-1-skeleton-definition.md`）与第 2 份（`.superpowers/plans/2026-09-28-workflow-sqlsugar-2-instance.md`）必须已完成。本计划依赖 `WorkflowSqlSugarExecutor`、`WorkflowJsonColumn`、注册扩展、`WorkflowTestDatabase`、`TestEntityTypes`、`ServiceRegistrationTests.CreateServices`、`SqlSugarWorkflowDefinitionStore`、`SqlSugarWorkflowInstanceStore`。

> spec 自成一体。**先读 spec 第 2、5 节再动手。**

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：沿用 worktree `E:/source/XiHan/XiHan.Framework-workflow`，分支 `feat/workflow-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。若 worktree 不存在：

```bash
git -C E:/source/XiHan/XiHan.Framework worktree add ../XiHan.Framework-workflow -b feat/workflow-sqlsugar dev
```

以下路径相对该 worktree。

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

**SqlSugar 签名只信源码**：权威源码 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（不是 `Src/Asp.Net/`）；更新提供者目录 `Abstract/UpdateProvider/`。文档 `E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar —— Interface/ISqlSugarClient.cs
ISugarQueryable<T> Queryable<T>();
IInsertable<T> Insertable<T>(T insertObj) where T : class, new();
IUpdateable<T> Updateable<T>(T UpdateObj) where T : class, new();
IDeleteable<T> Deleteable<T>() where T : class, new();

// SqlSugar —— Interface/IQueryable.cs
ISugarQueryable<T> Where(Expression<Func<T, bool>> expression);
ISugarQueryable<T> WhereIF(bool isWhere, Expression<Func<T, bool>> expression);
ISugarQueryable<T> OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc);   // 多次调用依次追加
ISugarQueryable<T> Take(int num);
Task<List<T>> ToListAsync(CancellationToken token);
Task<List<T>> ToListAsync();
int Count();

// SqlSugar —— Interface/Insertable.cs、IUpdateable.cs、IDeleteable.cs
Task<int> ExecuteCommandAsync(CancellationToken token);
Task<int> ExecuteCommandAsync();
IDeleteable<T> Where(Expression<Func<T, bool>> expression);

// SqlSugar —— Entities/Mapping/SugarMappingAttribute.cs（AllowMultiple = true，字段按属性名）
SugarIndexAttribute(string indexName, string fieldName, OrderByType sortType, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3, bool isUnique = false)

// 第 1 份产出
WorkflowSqlSugarExecutor.ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation, CancellationToken cancellationToken)
WorkflowJsonColumn.Serialize<T>(T value) / WorkflowJsonColumn.Deserialize<T>(string? json) where T : class, new()   // internal

// 框架 —— 引擎测试用
XiHan.Framework.DistributedIds.IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload(ushort workerId = 1)   // 返回 IDistributedIdGenerator<long>
XiHan.Framework.Workflow.Builders.WorkflowDefinitionBuilder.Create(string code, string name)
  .AddStart(string id = "start") / .AddEnd(string id = "end") / .AddDelay(string id, double durationSeconds, string? name = null)
  .AddParallel(string id, string? name = null) / .AddJoin(string id, bool waitAny = false, string? name = null)
  .AddNode(string id, string activityType, string? name = null, Action<WorkflowNodeBuilder>? configure = null)
  .AddUserTask(string id, string name, Action<WorkflowNodeBuilder> configure)
  .AddTransition(string sourceNodeId, string targetNodeId, string? condition = null, int priority = 0, string? name = null)
  .Build()
WorkflowNodeBuilder.WithProperty(string name, object? value)
IWorkflowEngine.StartAsync(WorkflowStartRequest request, CancellationToken cancellationToken = default)
IWorkflowEngine.ResumeBookmarkAsync(string bookmarkId, Dictionary<string, object?>? inputs = null, bool throwIfNotResumable = true, string? expectedBookmarkKey = null, CancellationToken cancellationToken = default)
IWorkflowEngine.PublishSignalAsync(string signalName, Dictionary<string, object?>? payload = null, string? correlationId = null, CancellationToken cancellationToken = default)
IWorkflowEngine.SuspendAsync(string instanceId, string? reason = null, CancellationToken cancellationToken = default)
IWorkflowEngine.CancelAsync(string instanceId, string? reason = null, CancellationToken cancellationToken = default)
IWorkflowUserTaskService.GetPendingAsync(string assigneeId, CancellationToken cancellationToken = default)
IWorkflowUserTaskService.CompleteAsync(string taskId, string actorId, string outcome, string? comment = null, Dictionary<string, object?>? variables = null, CancellationToken cancellationToken = default)
XiHanWorkflowOptions.NotResumableTimerBackoffSeconds   // 默认 300
XiHan.Framework.Workflow.Abstractions.Exceptions.WorkflowException(string message)
XiHan.Framework.Core.Application.ApplicationInitializationContext.ServiceProvider   // IServiceProvider
XiHanModule.OnApplicationInitialization(ApplicationInitializationContext context)  // public virtual void
XiHan.Framework.Caching.Distributed.DefaultDistributedLock                          // public sealed，无参构造，只在进程内互斥
```

**编码约定**：每个 `.cs` 以两行版权声明开头（`XHFH001`）；注释与 XML 文档注释一律简体中文、只写代码做什么；file-scoped namespace；表达式体方法与构造函数关闭（属性与访问器可以）；`public` 成员必须有 `<summary>`。

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.
```

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform**：**没有可用的筛选参数**（`--filter`、`--list-tests` 退出码 3），要跑单个测试类就整个项目跑；**不要带 `--logger trx` / `--results-directory`**（退出码 5）。

**测试项目**：沿用第 1 份的 csproj，不加 `PackageReference`。

**SQLite 临时库**：连接串带 `Pooling=False`（夹具已处理）。

**真实数据库测试**：`Assert.SkipWhen(...)` + `XIHAN_TEST_MYSQL`，CI 自动跳过。

**构建环境坑**：`MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，`taskkill //F //IM "<name>.exe"` 后重建。

**已知的无关抖动**：`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败，与本系列无关。

**建表**：`XiHan:Data:SqlSugarCore` 的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**，不开启不会自动建表，首次写入即报表不存在。本份写进 README 与文档站。

**测试代码里调 `Options.Create` 写全限定名** `Microsoft.Extensions.Options.Options.Create(...)`。

**提交信息**：中文 Conventional Commits，作用域 `workflow-sqlsugar`（文档提交用 `docs(workflow-sqlsugar): ...`）。**不加任何 AI 署名。**

---

## 本计划特有的硬约束

**① `GetByKindAndKeyAsync` 与 `GetBySignalAsync` 在数据库查询之后必须按 `StringComparison.Ordinal` 再过滤一遍全部字符串条件。**

MySQL 默认排序规则不区分大小写，SQLite 区分——单元测试永远发现不了。Task 5 的 MySQL 用例钉住这一条。

**② 相关性只在 `correlationId` 不为 `null` 时参与过滤；`""` 不是广播。**

不写 `string.IsNullOrEmpty(correlationId)`。

**③ `GetDueAsync` 必须同时有 `DueTime != null`、`DueTime <= now`、`Take(max)`，且 `max <= 0` 直接返回空。**

**④ 所有读写经执行器；`UpdateAsync` 纯更新；`DeleteAsync` 删到 0 行抛 `WorkflowException`；`DeleteByInstanceAsync` 删到 0 行不抛。**

`DeleteAsync` 的 0 行抛出是书签消费守卫（spec §4.7）：没有 Redis 锁时，它让同一书签只被一个节点推进。它有意比内存默认实现更严格。单元测试只证明存储会抛出；只有双节点竞争用例能证明引擎路径被拦住：去掉守卫时 Task 3 的单元测试会变红，但端到端用例照样全绿，因此 Task 7 必须做反向验证。

**⑤ 端到端测试主机必须 `CreateScope()` 后从作用域解析引擎与存储。**

**⑥ 不改 `docs/packages/workflow.md`、`XiHan.Framework.Workflow` 与 `Workflow.Abstractions` 的任何文件；根目录 `README.md` / `README_cn.md` 只改模块计数，不在「常用包」表格加行。**

**⑦ 模块计数不写死。** 实现时读出当前值再加一，见 Task 10 Step 5。

---

## File Structure

```
framework/src/XiHan.Framework.Workflow.SqlSugar/
  Entities/SysWorkflowBookmark.cs                                            新建
  Mapping/WorkflowBookmarkMapper.cs                                          新建
  Stores/SqlSugarWorkflowBookmarkStore.cs                                    新建
  Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs   修改：追加一行 Replace
  Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceProviderExtensions.cs   新建：进程内锁警告
  XiHanWorkflowSqlSugarModule.cs                                             修改：OnApplicationInitialization 调警告
  README.md                                                                  新建

framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/
  TestEntityTypes.cs                                                         修改：加书签类型
  WorkflowBookmarkEntityTests.cs                                             新建
  WorkflowBookmarkMapperTests.cs                                             新建
  SqlSugarWorkflowBookmarkStoreTests.cs                                      新建
  ServiceRegistrationTests.cs                                                修改：追加用例
  WorkflowBookmarkMatchingMySqlTests.cs                                      新建
  EngineTestDoubles.cs                                                       新建
  WorkflowEngineTestHost.cs                                                  新建
  WorkflowEngineEndToEndTests.cs                                             新建
  RendezvousBookmarkStore.cs                                                 新建：让两个节点的书签删除同时发生
  WorkflowBookmarkRaceMySqlTests.cs                                          新建：双节点竞争同一到期书签
  DistributedLockWarningTests.cs                                             新建

docs/packages/workflow-sqlsugar.md                                           新建
docs/packages/index.md                                                       修改：加一行
docs/.vitepress/config.ts                                                    修改：侧边栏加一项
framework/README.md                                                          修改：模块清单加一行
framework/README_cn.md                                                       修改：模块清单加一行、计数加一
README.md / README_cn.md                                                     修改：只改模块计数（含徽章）
docs/index.md、docs/introduction.md、docs/why.md 等 docs/**/*.md             修改：模块计数加一
```

---

### Task 1: 书签实体

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowBookmark.cs`
- Modify: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestEntityTypes.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkEntityTests.cs`

**Interfaces:**
- Consumes: `SugarEntity<string>`、`TableInitializationAttribute`
- Produces: `SysWorkflowBookmark : SugarEntity<string>`，属性：`InstanceId`、`NodeId`、`NodeInstanceId`、`Kind`、`BookmarkKey`、`PayloadJson`、`DueTime`、`CorrelationId`、`CreationTime`、`OwnerTenantId`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowNodeInstance.cs`（第 2 份）
- `framework/src/XiHan.Framework.Workflow.Abstractions/Runtime/WorkflowBookmark.cs`
- spec §4.1

**本任务禁止事项：** 属性不叫 `Key`、不叫 `TenantId`；索引名带 `{table}`；`SugarIndex` 字段用 `nameof`。

- [ ] **Step 1: 写失败的测试**

把 `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestEntityTypes.cs` 中的

```csharp
        typeof(SysWorkflowInstance),
        typeof(SysWorkflowNodeInstance)
    ];
```

改为

```csharp
        typeof(SysWorkflowInstance),
        typeof(SysWorkflowNodeInstance),
        typeof(SysWorkflowBookmark)
    ];
```

新建 `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using System.Reflection;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程书签实体测试
/// </summary>
public class WorkflowBookmarkEntityTests
{
    /// <summary>
    /// 表名固定
    /// </summary>
    [Fact]
    public void 表名固定()
    {
        Assert.Equal("sys_workflow_bookmark", typeof(SysWorkflowBookmark).GetCustomAttribute<SugarTable>()?.TableName);
    }

    /// <summary>
    /// 四个索引覆盖全部查询
    /// </summary>
    [Fact]
    public void 四个索引覆盖全部查询()
    {
        var indexes = typeof(SysWorkflowBookmark)
            .GetCustomAttributes<SugarIndexAttribute>()
            .ToDictionary(item => item.IndexName, item => item.IndexFields.Keys.ToArray());

        Assert.Equal(4, indexes.Count);
        Assert.Equal([nameof(SysWorkflowBookmark.InstanceId), nameof(SysWorkflowBookmark.CreationTime)], indexes["idx_{table}_instance"]);
        Assert.Equal([nameof(SysWorkflowBookmark.NodeInstanceId)], indexes["idx_{table}_node_instance"]);
        Assert.Equal([nameof(SysWorkflowBookmark.DueTime)], indexes["idx_{table}_due"]);
        Assert.Equal(
            [nameof(SysWorkflowBookmark.Kind), nameof(SysWorkflowBookmark.BookmarkKey), nameof(SysWorkflowBookmark.CreationTime)],
            indexes["idx_{table}_kind_key"]);
    }

    /// <summary>
    /// 索引键列不使用保留字
    /// </summary>
    [Fact]
    public void 索引键列不使用保留字()
    {
        var column = typeof(SysWorkflowBookmark)
            .GetProperty(nameof(SysWorkflowBookmark.BookmarkKey))?
            .GetCustomAttribute<SugarColumn>();

        Assert.Equal("Bookmark_Key", column?.ColumnName);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysWorkflowBookmark` 不存在。

- [ ] **Step 3: 创建实体**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowBookmark.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Workflow.SqlSugar.Entities;

/// <summary>
/// 流程书签实体
/// </summary>
[SugarTable("sys_workflow_bookmark")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("idx_{table}_instance", nameof(InstanceId), OrderByType.Asc, nameof(CreationTime), OrderByType.Asc)]
[SugarIndex("idx_{table}_node_instance", nameof(NodeInstanceId), OrderByType.Asc)]
[SugarIndex("idx_{table}_due", nameof(DueTime), OrderByType.Asc)]
[SugarIndex("idx_{table}_kind_key", nameof(Kind), OrderByType.Asc, nameof(BookmarkKey), OrderByType.Asc, nameof(CreationTime), OrderByType.Asc)]
public class SysWorkflowBookmark : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysWorkflowBookmark() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">书签标识</param>
    public SysWorkflowBookmark(string basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 所属流程实例标识
    /// </summary>
    [SugarColumn(ColumnName = "Instance_Id", Length = 255, IsNullable = false, ColumnDescription = "所属流程实例标识")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 节点标识
    /// </summary>
    [SugarColumn(ColumnName = "Node_Id", Length = 255, IsNullable = false, ColumnDescription = "节点标识")]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// 节点实例标识
    /// </summary>
    [SugarColumn(ColumnName = "Node_Instance_Id", Length = 255, IsNullable = false, ColumnDescription = "节点实例标识")]
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 书签种类
    /// </summary>
    [SugarColumn(ColumnName = "Kind", Length = 64, IsNullable = false, ColumnDescription = "书签种类")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// 索引键，语义随种类而定：受理人标识、信号名称或父节点实例标识
    /// </summary>
    [SugarColumn(ColumnName = "Bookmark_Key", Length = 256, IsNullable = true, ColumnDescription = "索引键，语义随种类而定：受理人标识、信号名称或父节点实例标识")]
    public string? BookmarkKey { get; set; }

    /// <summary>
    /// 附加数据的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Payload_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "附加数据的 JSON")]
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// 到期时间
    /// </summary>
    [SugarColumn(ColumnName = "Due_Time", IsNullable = true, ColumnDescription = "到期时间")]
    public DateTime? DueTime { get; set; }

    /// <summary>
    /// 业务相关性标识
    /// </summary>
    [SugarColumn(ColumnName = "Correlation_Id", Length = 255, IsNullable = true, ColumnDescription = "业务相关性标识")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Creation_Time", IsNullable = false, ColumnDescription = "创建时间")]
    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 租户标识
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识")]
    public long? OwnerTenantId { get; set; }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（含第 1 份的约定测试对书签实体的检查）。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增流程书签实体"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 2: 书签映射

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowBookmarkMapper.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkMapperTests.cs`

**Interfaces:**
- Consumes: Task 1 的实体；`WorkflowJsonColumn`
- Produces: `public static class WorkflowBookmarkMapper`：`SysWorkflowBookmark ToEntity(WorkflowBookmark bookmark)`、`WorkflowBookmark ToBookmark(SysWorkflowBookmark entity)`

**参考来源（动手前先读）：** `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowNodeInstanceMapper.cs`（第 2 份）；spec §4.2。

**本任务禁止事项：** `""` 与 `null` 不互转。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Mapping;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程书签映射测试
/// </summary>
public class WorkflowBookmarkMapperTests
{
    /// <summary>
    /// 书签往返一致
    /// </summary>
    [Fact]
    public void 书签往返一致()
    {
        var bookmark = new WorkflowBookmark
        {
            Id = "4001",
            InstanceId = "2001",
            NodeId = "approve",
            NodeInstanceId = "3001",
            Kind = WorkflowBookmarkKinds.UserTask,
            Key = "u1",
            Payload = new Dictionary<string, object?> { ["title"] = "单据 B001 审批" },
            DueTime = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc),
            CorrelationId = "ORDER-1",
            CreationTime = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc),
            TenantId = 9
        };

        var entity = WorkflowBookmarkMapper.ToEntity(bookmark);
        var restored = WorkflowBookmarkMapper.ToBookmark(entity);

        Assert.Equal("u1", entity.BookmarkKey);
        Assert.Equal(9, entity.OwnerTenantId);
        Assert.Equal(bookmark.Id, restored.Id);
        Assert.Equal(bookmark.InstanceId, restored.InstanceId);
        Assert.Equal(bookmark.NodeId, restored.NodeId);
        Assert.Equal(bookmark.NodeInstanceId, restored.NodeInstanceId);
        Assert.Equal(bookmark.Kind, restored.Kind);
        Assert.Equal(bookmark.Key, restored.Key);
        Assert.Equal(bookmark.DueTime, restored.DueTime);
        Assert.Equal(bookmark.CorrelationId, restored.CorrelationId);
        Assert.Equal(bookmark.CreationTime, restored.CreationTime);
        Assert.Equal(bookmark.TenantId, restored.TenantId);
        Assert.Equal("单据 B001 审批", WorkflowValueConverter.ConvertTo<string>(restored.Payload["title"]));
    }

    /// <summary>
    /// 相关性的空串与空值各自保留
    /// </summary>
    [Fact]
    public void 相关性的空串与空值各自保留()
    {
        var empty = new WorkflowBookmark { Id = "1", Kind = WorkflowBookmarkKinds.Signal, CorrelationId = string.Empty };
        var none = new WorkflowBookmark { Id = "2", Kind = WorkflowBookmarkKinds.Signal, CorrelationId = null };

        Assert.Equal(string.Empty, WorkflowBookmarkMapper.ToBookmark(WorkflowBookmarkMapper.ToEntity(empty)).CorrelationId);
        Assert.Null(WorkflowBookmarkMapper.ToBookmark(WorkflowBookmarkMapper.ToEntity(none)).CorrelationId);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`WorkflowBookmarkMapper` 不存在。

- [ ] **Step 3: 创建映射**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowBookmarkMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Mapping;

/// <summary>
/// 流程书签契约与实体的双向映射
/// </summary>
public static class WorkflowBookmarkMapper
{
    /// <summary>
    /// 把书签转换为实体
    /// </summary>
    /// <param name="bookmark">书签</param>
    /// <returns>书签实体</returns>
    public static SysWorkflowBookmark ToEntity(WorkflowBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        return new SysWorkflowBookmark(bookmark.Id)
        {
            InstanceId = bookmark.InstanceId,
            NodeId = bookmark.NodeId,
            NodeInstanceId = bookmark.NodeInstanceId,
            Kind = bookmark.Kind,
            BookmarkKey = bookmark.Key,
            PayloadJson = WorkflowJsonColumn.Serialize(bookmark.Payload),
            DueTime = bookmark.DueTime,
            CorrelationId = bookmark.CorrelationId,
            CreationTime = bookmark.CreationTime,
            OwnerTenantId = bookmark.TenantId
        };
    }

    /// <summary>
    /// 把实体转换为书签
    /// </summary>
    /// <param name="entity">书签实体</param>
    /// <returns>书签</returns>
    public static WorkflowBookmark ToBookmark(SysWorkflowBookmark entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new WorkflowBookmark
        {
            Id = entity.BasicId,
            InstanceId = entity.InstanceId,
            NodeId = entity.NodeId,
            NodeInstanceId = entity.NodeInstanceId,
            Kind = entity.Kind,
            Key = entity.BookmarkKey,
            Payload = WorkflowJsonColumn.Deserialize<Dictionary<string, object?>>(entity.PayloadJson),
            DueTime = entity.DueTime,
            CorrelationId = entity.CorrelationId,
            CreationTime = entity.CreationTime,
            TenantId = entity.OwnerTenantId
        };
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增流程书签映射"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 3: 书签存储

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowBookmarkStore.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/SqlSugarWorkflowBookmarkStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的映射；`WorkflowSqlSugarExecutor`；`WorkflowBookmarkKinds`
- Produces: `public class SqlSugarWorkflowBookmarkStore : IWorkflowBookmarkStore`，构造 `(WorkflowSqlSugarExecutor executor)`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.Abstractions/Stores/IWorkflowBookmarkStore.cs`
- `framework/src/XiHan.Framework.Workflow/Stores/DefaultWorkflowBookmarkStore.cs`（语义基准，尤其 `GetBySignalAsync`）
- spec §4.3

**本任务禁止事项：** 硬约束 ①～④。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/SqlSugarWorkflowBookmarkStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Exceptions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程书签存储测试
/// </summary>
public class SqlSugarWorkflowBookmarkStoreTests : IDisposable
{
    private static readonly DateTime BaseTime = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    private readonly WorkflowTestDatabase _database = WorkflowTestDatabase.CreateSqlite();
    private readonly SqlSugarWorkflowBookmarkStore _store;

    /// <summary>
    /// 构造函数
    /// </summary>
    public SqlSugarWorkflowBookmarkStoreTests()
    {
        _store = new SqlSugarWorkflowBookmarkStore(_database.Executor);
    }

    /// <summary>
    /// 插入后按标识可查到
    /// </summary>
    [Fact]
    public async Task 插入后按标识可查到()
    {
        var bookmark = NewBookmark("b1", WorkflowBookmarkKinds.UserTask, "u1");
        bookmark.Payload["title"] = "审批";

        await _store.InsertAsync(bookmark);
        var found = await _store.FindAsync("b1");

        Assert.NotNull(found);
        Assert.Equal("u1", found.Key);
        Assert.Equal("审批", WorkflowValueConverter.ConvertTo<string>(found.Payload["title"]));
        Assert.Null(await _store.FindAsync("missing"));
    }

    /// <summary>
    /// 按实例与节点实例查询按创建时间升序
    /// </summary>
    [Fact]
    public async Task 按实例与节点实例查询按创建时间升序()
    {
        var later = NewBookmark("b2", WorkflowBookmarkKinds.Timer, null, creationTime: BaseTime.AddSeconds(1));
        var earlier = NewBookmark("b1", WorkflowBookmarkKinds.Timer, null);
        var otherNode = NewBookmark("b3", WorkflowBookmarkKinds.Timer, null, nodeInstanceId: "n2");
        var otherInstance = NewBookmark("b4", WorkflowBookmarkKinds.Timer, null, instanceId: "i2", nodeInstanceId: "n9");
        await _store.InsertAsync(later);
        await _store.InsertAsync(earlier);
        await _store.InsertAsync(otherNode);
        await _store.InsertAsync(otherInstance);

        var byInstance = await _store.GetByInstanceAsync("i1");
        var byNodeInstance = await _store.GetByNodeInstanceAsync("n1");

        Assert.Equal(["b1", "b3", "b2"], byInstance.Select(item => item.Id));
        Assert.Equal(["b1", "b2"], byNodeInstance.Select(item => item.Id));
    }

    /// <summary>
    /// 到期查询只含已到期书签并按到期时间升序截取
    /// </summary>
    [Fact]
    public async Task 到期查询只含已到期书签并按到期时间升序截取()
    {
        await _store.InsertAsync(NewBookmark("due-late", WorkflowBookmarkKinds.Timer, null, dueTime: BaseTime.AddSeconds(-10)));
        await _store.InsertAsync(NewBookmark("due-early", WorkflowBookmarkKinds.Retry, null, dueTime: BaseTime.AddSeconds(-20)));
        await _store.InsertAsync(NewBookmark("due-now", WorkflowBookmarkKinds.NodeTimeout, null, dueTime: BaseTime));
        await _store.InsertAsync(NewBookmark("future", WorkflowBookmarkKinds.Timer, null, dueTime: BaseTime.AddSeconds(1)));
        await _store.InsertAsync(NewBookmark("no-due", WorkflowBookmarkKinds.UserTask, "u1"));

        var all = await _store.GetDueAsync(BaseTime, 10);
        var limited = await _store.GetDueAsync(BaseTime, 2);

        Assert.Equal(["due-early", "due-late", "due-now"], all.Select(item => item.Id));
        Assert.Equal(["due-early", "due-late"], limited.Select(item => item.Id));
        Assert.Empty(await _store.GetDueAsync(BaseTime, 0));
    }

    /// <summary>
    /// 按种类和索引键查询
    /// </summary>
    [Fact]
    public async Task 按种类和索引键查询()
    {
        await _store.InsertAsync(NewBookmark("b2", WorkflowBookmarkKinds.UserTask, "u1", creationTime: BaseTime.AddSeconds(1)));
        await _store.InsertAsync(NewBookmark("b1", WorkflowBookmarkKinds.UserTask, "u1"));
        await _store.InsertAsync(NewBookmark("b3", WorkflowBookmarkKinds.UserTask, "u2"));
        await _store.InsertAsync(NewBookmark("b4", WorkflowBookmarkKinds.Signal, "u1"));

        var tasks = await _store.GetByKindAndKeyAsync(WorkflowBookmarkKinds.UserTask, "u1");

        Assert.Equal(["b1", "b2"], tasks.Select(item => item.Id));
    }

    /// <summary>
    /// 信号定向匹配不限相关性与相关性相同的书签
    /// </summary>
    [Fact]
    public async Task 信号定向匹配不限相关性与相关性相同的书签()
    {
        await SeedSignalBookmarksAsync();

        var matched = await _store.GetBySignalAsync("paid", "A");

        Assert.Equal(["any", "a"], matched.Select(item => item.Id));
    }

    /// <summary>
    /// 信号相关性为空值时广播
    /// </summary>
    [Fact]
    public async Task 信号相关性为空值时广播()
    {
        await SeedSignalBookmarksAsync();

        var matched = await _store.GetBySignalAsync("paid", null);

        Assert.Equal(["any", "a", "b", "empty"], matched.Select(item => item.Id));
    }

    /// <summary>
    /// 信号相关性为空串时不是广播
    /// </summary>
    [Fact]
    public async Task 信号相关性为空串时不是广播()
    {
        await SeedSignalBookmarksAsync();

        var matched = await _store.GetBySignalAsync("paid", string.Empty);

        Assert.Equal(["any", "empty"], matched.Select(item => item.Id));
    }

    /// <summary>
    /// 更新改写索引键与到期时间
    /// </summary>
    [Fact]
    public async Task 更新改写索引键与到期时间()
    {
        var bookmark = NewBookmark("b1", WorkflowBookmarkKinds.UserTask, "u1", dueTime: BaseTime);
        await _store.InsertAsync(bookmark);

        bookmark.Key = "u2";
        bookmark.DueTime = null;
        await _store.UpdateAsync(bookmark);

        var found = await _store.FindAsync("b1");
        Assert.NotNull(found);
        Assert.Equal("u2", found.Key);
        Assert.Null(found.DueTime);
        Assert.Empty(await _store.GetByKindAndKeyAsync(WorkflowBookmarkKinds.UserTask, "u1"));
    }

    /// <summary>
    /// 更新不存在的书签不新建行
    /// </summary>
    [Fact]
    public async Task 更新不存在的书签不新建行()
    {
        await _store.UpdateAsync(NewBookmark("ghost", WorkflowBookmarkKinds.Timer, null, dueTime: BaseTime));

        using var probe = _database.CreateProbeClient();
        Assert.Equal(0, probe.Queryable<SysWorkflowBookmark>().Count());
    }

    /// <summary>
    /// 删除单个书签与删除实例的全部书签
    /// </summary>
    [Fact]
    public async Task 删除单个书签与删除实例的全部书签()
    {
        await _store.InsertAsync(NewBookmark("b1", WorkflowBookmarkKinds.Timer, null));
        await _store.InsertAsync(NewBookmark("b2", WorkflowBookmarkKinds.Timer, null));
        await _store.InsertAsync(NewBookmark("b3", WorkflowBookmarkKinds.Timer, null, instanceId: "i2"));

        await _store.DeleteAsync("b1");
        Assert.Null(await _store.FindAsync("b1"));

        await _store.DeleteByInstanceAsync("i1");
        Assert.Empty(await _store.GetByInstanceAsync("i1"));
        Assert.Equal("b3", Assert.Single(await _store.GetByInstanceAsync("i2")).Id);
    }

    /// <summary>
    /// 删除不存在或已被删除的书签抛出工作流异常
    /// </summary>
    [Fact]
    public async Task 删除不存在或已被删除的书签抛出工作流异常()
    {
        await _store.InsertAsync(NewBookmark("b1", WorkflowBookmarkKinds.Timer, null));
        await _store.DeleteAsync("b1");

        await Assert.ThrowsAsync<WorkflowException>(() => _store.DeleteAsync("b1"));
        await Assert.ThrowsAsync<WorkflowException>(() => _store.DeleteAsync("missing"));
    }

    /// <summary>
    /// 删除没有书签的实例不抛异常
    /// </summary>
    [Fact]
    public async Task 删除没有书签的实例不抛异常()
    {
        await _store.DeleteByInstanceAsync("no-bookmarks");

        Assert.Empty(await _store.GetByInstanceAsync("no-bookmarks"));
    }

    /// <summary>
    /// 释放测试夹具
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private async Task SeedSignalBookmarksAsync()
    {
        await _store.InsertAsync(NewBookmark("any", WorkflowBookmarkKinds.Signal, "paid", correlationId: null));
        await _store.InsertAsync(NewBookmark("a", WorkflowBookmarkKinds.Signal, "paid", correlationId: "A", creationTime: BaseTime.AddSeconds(1)));
        await _store.InsertAsync(NewBookmark("b", WorkflowBookmarkKinds.Signal, "paid", correlationId: "B", creationTime: BaseTime.AddSeconds(2)));
        await _store.InsertAsync(NewBookmark("empty", WorkflowBookmarkKinds.Signal, "paid", correlationId: string.Empty, creationTime: BaseTime.AddSeconds(3)));
        await _store.InsertAsync(NewBookmark("other-signal", WorkflowBookmarkKinds.Signal, "shipped", correlationId: null));
        await _store.InsertAsync(NewBookmark("not-signal", WorkflowBookmarkKinds.UserTask, "paid", correlationId: null));
    }

    private static WorkflowBookmark NewBookmark(
        string id,
        string kind,
        string? key,
        string instanceId = "i1",
        string nodeInstanceId = "n1",
        DateTime? dueTime = null,
        string? correlationId = null,
        DateTime? creationTime = null)
    {
        return new WorkflowBookmark
        {
            Id = id,
            InstanceId = instanceId,
            NodeId = "node",
            NodeInstanceId = nodeInstanceId,
            Kind = kind,
            Key = key,
            DueTime = dueTime,
            CorrelationId = correlationId,
            CreationTime = creationTime ?? BaseTime
        };
    }
}
```

注：`按实例与节点实例查询按创建时间升序` 中 `b1` 与 `b3` 创建时间相同，按二级键 `Basic_Id` 排为 `b1`、`b3`。

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarWorkflowBookmarkStore` 不存在。

- [ ] **Step 3: 创建书签存储**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowBookmarkStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Exceptions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.Abstractions.Stores;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Mapping;

namespace XiHan.Framework.Workflow.SqlSugar.Stores;

/// <summary>
/// SqlSugar 流程书签存储
/// </summary>
/// <remarks>
/// 按种类与索引键匹配的查询在数据库条件之后再按序数比较过滤，匹配区分大小写。
/// 按标识删除时未删到任何行即抛出 <see cref="WorkflowException"/>。
/// </remarks>
public class SqlSugarWorkflowBookmarkStore : IWorkflowBookmarkStore
{
    private readonly WorkflowSqlSugarExecutor _executor;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="executor">工作流存储的数据库执行器</param>
    public SqlSugarWorkflowBookmarkStore(WorkflowSqlSugarExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// 按标识查找书签
    /// </summary>
    /// <param name="id">书签标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签（不存在返回 null）</returns>
    public async Task<WorkflowBookmark?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowBookmark>()
                .Where(item => item.BasicId == id)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return entities.Count == 0 ? null : WorkflowBookmarkMapper.ToBookmark(entities[0]);
    }

    /// <summary>
    /// 获取实例的全部书签
    /// </summary>
    /// <param name="instanceId">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表（按创建时间升序）</returns>
    public async Task<List<WorkflowBookmark>> GetByInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowBookmark>()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.CreationTime)
                .OrderBy(item => item.BasicId)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowBookmarkMapper.ToBookmark)];
    }

    /// <summary>
    /// 获取节点实例的全部书签
    /// </summary>
    /// <param name="nodeInstanceId">节点实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表（按创建时间升序）</returns>
    public async Task<List<WorkflowBookmark>> GetByNodeInstanceAsync(string nodeInstanceId, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowBookmark>()
                .Where(item => item.NodeInstanceId == nodeInstanceId)
                .OrderBy(item => item.CreationTime)
                .OrderBy(item => item.BasicId)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowBookmarkMapper.ToBookmark)];
    }

    /// <summary>
    /// 获取到期的定时类书签
    /// </summary>
    /// <param name="now">当前时间</param>
    /// <param name="maxResultCount">最大返回条数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>到期书签列表（按到期时间升序）</returns>
    public async Task<List<WorkflowBookmark>> GetDueAsync(DateTime now, int maxResultCount, CancellationToken cancellationToken = default)
    {
        if (maxResultCount <= 0)
        {
            return [];
        }

        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowBookmark>()
                .Where(item => item.DueTime != null && item.DueTime <= now)
                .OrderBy(item => item.DueTime)
                .OrderBy(item => item.BasicId)
                .Take(maxResultCount)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowBookmarkMapper.ToBookmark)];
    }

    /// <summary>
    /// 按种类和索引键查询书签
    /// </summary>
    /// <param name="kind">书签种类</param>
    /// <param name="key">索引键</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表（按创建时间升序）</returns>
    public async Task<List<WorkflowBookmark>> GetByKindAndKeyAsync(string kind, string key, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowBookmark>()
                .Where(item => item.Kind == kind && item.BookmarkKey == key)
                .OrderBy(item => item.CreationTime)
                .OrderBy(item => item.BasicId)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities
            .Where(item => string.Equals(item.Kind, kind, StringComparison.Ordinal)
                && string.Equals(item.BookmarkKey, key, StringComparison.Ordinal))
            .Select(WorkflowBookmarkMapper.ToBookmark)];
    }

    /// <summary>
    /// 查询匹配信号的书签（相关性为 null 表示广播）
    /// </summary>
    /// <param name="signalName">信号名称</param>
    /// <param name="correlationId">业务相关性标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表（按创建时间升序）</returns>
    public async Task<List<WorkflowBookmark>> GetBySignalAsync(string signalName, string? correlationId, CancellationToken cancellationToken = default)
    {
        const string signalKind = WorkflowBookmarkKinds.Signal;

        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowBookmark>()
                .Where(item => item.Kind == signalKind && item.BookmarkKey == signalName)
                .WhereIF(correlationId is not null, item => item.CorrelationId == null || item.CorrelationId == correlationId)
                .OrderBy(item => item.CreationTime)
                .OrderBy(item => item.BasicId)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities
            .Where(item => string.Equals(item.Kind, signalKind, StringComparison.Ordinal)
                && string.Equals(item.BookmarkKey, signalName, StringComparison.Ordinal)
                && (correlationId is null
                    || item.CorrelationId is null
                    || string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal)))
            .Select(WorkflowBookmarkMapper.ToBookmark)];
    }

    /// <summary>
    /// 插入书签
    /// </summary>
    /// <param name="bookmark">书签</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task InsertAsync(WorkflowBookmark bookmark, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowBookmarkMapper.ToEntity(bookmark);

        await _executor.ExecuteAsync(
            client => client.Insertable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 更新书签，标识不存在时不做任何事
    /// </summary>
    /// <param name="bookmark">书签</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task UpdateAsync(WorkflowBookmark bookmark, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowBookmarkMapper.ToEntity(bookmark);

        await _executor.ExecuteAsync(
            client => client.Updateable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 删除书签，未删到任何行时抛出异常
    /// </summary>
    /// <param name="id">书签标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    /// <exception cref="WorkflowException">书签不存在或已被删除</exception>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var affected = await _executor.ExecuteAsync(
            client => client.Deleteable<SysWorkflowBookmark>()
                .Where(item => item.BasicId == id)
                .ExecuteCommandAsync(cancellationToken),
            cancellationToken);

        if (affected == 0)
        {
            throw new WorkflowException($"书签 {id} 不存在或已被处理");
        }
    }

    /// <summary>
    /// 删除实例的全部书签
    /// </summary>
    /// <param name="instanceId">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task DeleteByInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await _executor.ExecuteAsync(
            client => client.Deleteable<SysWorkflowBookmark>()
                .Where(item => item.InstanceId == instanceId)
                .ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

**若 `删除不存在或已被删除的书签抛出工作流异常` 失败**，检查 `DeleteAsync` 是否判断了受影响行数。**若 `信号相关性为空串时不是广播` 读到全部四个**，说明相关性条件被写成了 `IsNullOrEmpty`（硬约束 ②）。**若 `到期查询...` 包含 `no-due`**，检查 `DueTime != null`。改实现，不改断言。**若 SqlSugar 对 `item.DueTime <= now` 报表达式不支持**，改写为 `item.DueTime!.Value <= now`，其余不动。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增 SqlSugar 流程书签存储"
```

提交信息正文写明守卫的取舍（注释里不写）：

```
书签按标识删除未删到任何行时抛 WorkflowException。
引擎消费书签的删除在执行批次开始之前，两个节点竞争同一书签时后删者在此放弃，
批次不开始、不留半写状态；定时器 Worker 与信号投递已把 WorkflowException 当作并发处理跳过。
比内存默认实现更严格：后者删除不存在的键静默成功。
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 4: 注册替换

**Files:**
- Modify: `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/ServiceRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的存储；`ServiceRegistrationTests.CreateServices(bool)`
- Produces: `services.Replace(ServiceDescriptor.Scoped<IWorkflowBookmarkStore, SqlSugarWorkflowBookmarkStore>())`

**参考来源（动手前先读）：** `framework/src/XiHan.Framework.Workflow/Extensions/DependencyInjection/XiHanWorkflowServiceCollectionExtensions.cs:51-53`。

**本任务禁止事项：** 不用 `TryAdd`；生命周期保持 Scoped。

- [ ] **Step 1: 写失败的测试**

在 `ServiceRegistrationTests` 的 `CreateServices` 方法之前追加：

```csharp
    /// <summary>
    /// 书签存储被替换为作用域实现
    /// </summary>
    /// <param name="registerSqlSugarFirst">是否先注册本包</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 书签存储被替换为作用域实现(bool registerSqlSugarFirst)
    {
        var services = CreateServices(registerSqlSugarFirst);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IWorkflowBookmarkStore));
        Assert.Equal(typeof(SqlSugarWorkflowBookmarkStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 三个默认存储全部被替换
    /// </summary>
    [Fact]
    public void 三个默认存储全部被替换()
    {
        var services = CreateServices(registerSqlSugarFirst: false);

        var storeTypes = services
            .Where(item => item.ServiceType == typeof(IWorkflowDefinitionStore)
                || item.ServiceType == typeof(IWorkflowInstanceStore)
                || item.ServiceType == typeof(IWorkflowBookmarkStore))
            .Select(item => item.ImplementationType)
            .ToList();

        Assert.Equal(3, storeTypes.Count);
        Assert.All(storeTypes, type => Assert.Equal("XiHan.Framework.Workflow.SqlSugar.Stores", type?.Namespace));
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：`书签存储被替换为作用域实现` 两个数据行与 `三个默认存储全部被替换` FAIL，书签实现仍是 `DefaultWorkflowBookmarkStore`。

- [ ] **Step 3: 追加注册**

在 `AddXiHanWorkflowSqlSugar` 中，把

```csharp
        services.Replace(ServiceDescriptor.Scoped<IWorkflowInstanceStore, SqlSugarWorkflowInstanceStore>());
```

改为

```csharp
        services.Replace(ServiceDescriptor.Scoped<IWorkflowInstanceStore, SqlSugarWorkflowInstanceStore>());
        services.Replace(ServiceDescriptor.Scoped<IWorkflowBookmarkStore, SqlSugarWorkflowBookmarkStore>());
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 以 SqlSugar 实现替换流程书签存储"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 5: MySQL 书签匹配大小写用例

**Files:**
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkMatchingMySqlTests.cs`

**Interfaces:**
- Consumes: `WorkflowTestDatabase.CreateMySql(string)`；Task 3 的存储
- Produces: 无（测试）

**参考来源（动手前先读）：** 第 2 份的 `WorkflowIsolationMySqlTests.cs`；spec §5 ①。

**本任务禁止事项：** 不清空整张表，只删本用例写入的行；不放宽断言。

- [ ] **Step 1: 写测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkMatchingMySqlTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 书签匹配在真实数据库排序规则下的测试
/// </summary>
/// <remarks>
/// 地址取环境变量 <c>XIHAN_TEST_MYSQL</c>，未设置时跳过。
/// </remarks>
public class WorkflowBookmarkMatchingMySqlTests
{
    private const string SkipReason = "未设置 XIHAN_TEST_MYSQL，跳过真实数据库测试。";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("XIHAN_TEST_MYSQL");

    /// <summary>
    /// 书签匹配区分大小写
    /// </summary>
    [Fact]
    public async Task 书签匹配区分大小写()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        using var database = WorkflowTestDatabase.CreateMySql(ConnectionString!);
        var store = new SqlSugarWorkflowBookmarkStore(database.Executor);
        var instanceId = Guid.NewGuid().ToString("N");
        var suffix = Guid.NewGuid().ToString("N");

        await store.InsertAsync(NewBookmark(instanceId, WorkflowBookmarkKinds.UserTask, "Alice-" + suffix));
        await store.InsertAsync(NewBookmark(instanceId, WorkflowBookmarkKinds.Signal, "Order-Paid-" + suffix));

        try
        {
            Assert.Empty(await store.GetByKindAndKeyAsync(WorkflowBookmarkKinds.UserTask, "alice-" + suffix));
            Assert.Empty(await store.GetBySignalAsync("order-paid-" + suffix, null));

            Assert.Single(await store.GetByKindAndKeyAsync(WorkflowBookmarkKinds.UserTask, "Alice-" + suffix));
            Assert.Single(await store.GetBySignalAsync("Order-Paid-" + suffix, null));
        }
        finally
        {
            await store.DeleteByInstanceAsync(instanceId);
        }
    }

    private static WorkflowBookmark NewBookmark(string instanceId, string kind, string key)
    {
        return new WorkflowBookmark
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceId = instanceId,
            NodeId = "node",
            NodeInstanceId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Key = key,
            CreationTime = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc)
        };
    }
}
```

- [ ] **Step 2: 无环境变量时确认跳过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：该用例 skipped，其余 PASS。

- [ ] **Step 3: 本机真库运行**

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：本用例与第 2 份的 `外层事务未提交时实例更新已对其他连接可见` 都 PASS。夹具的 `InitTables` 此时建出四张表，顺带验证全部索引在 MySQL 上的键长未超限。

- [ ] **Step 4: 拿掉关键条件，确认测试变红**

先确认测试库的排序规则不区分大小写：

```sql
SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'xihan_test';
```

结果以 `_ci` 结尾才能做本步验证；若是 `_bin` / `_cs`，在 PR 描述里注明「测试库区分大小写，未做变红验证」并跳过本步。

临时把 `SqlSugarWorkflowBookmarkStore.GetByKindAndKeyAsync` 的返回改为 `return [.. entities.Select(WorkflowBookmarkMapper.ToBookmark)];`（去掉序数后过滤），重跑 Step 3。

预期：本用例 **FAIL** 于第一个 `Assert.Empty`（MySQL 按不区分大小写命中了 `Alice-...`）。

确认后恢复原实现，重跑确认全绿，`git diff` 确认存储无残留改动。

- [ ] **Step 5: 提交**

```bash
git add framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "test(workflow-sqlsugar): 新增书签匹配区分大小写的真实数据库用例"
```

---

### Task 6: 引擎端到端测试

**Files:**
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/EngineTestDoubles.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowEngineTestHost.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowEngineEndToEndTests.cs`

**Interfaces:**
- Consumes: 三个 SqlSugar 存储与注册扩展；`WorkflowTestDatabase`；主包 `AddXiHanWorkflow`
- Produces: 测试主机 `WorkflowEngineTestHost`，构造 `(WorkflowTestDatabase? database = null, Action<IServiceCollection>? configureServices = null, ushort workerId = 1)`（`database` 为空时新建临时 SQLite 库，主机负责释放）；属性 `Clock`、`Engine`、`DefinitionManager`、`UserTaskService`、`InstanceStore`、`BookmarkStore`、`DefinitionStore`；方法 `Task<WorkflowDefinition> PublishAsync(WorkflowDefinition)`（返回已发布的定义）、`ReloadAsync(string)`

**参考来源（动手前先读）：**
- `framework/test/XiHan.Framework.Workflow.Tests/WorkflowTestHost.cs`（测试替身照抄）
- `framework/test/XiHan.Framework.Workflow.Tests/DelaySignalTests.cs`、`UserTaskTests.cs`、`ParallelJoinTests.cs`
- `framework/src/XiHan.Framework.Workflow/Activities/BuiltIn/WaitSignalActivity.cs:37-47`（等待信号要求实例有相关性，否则需 `AcceptAnyCorrelation`）
- spec §4.6

**本任务禁止事项：** 硬约束 ⑤。不引用 `XiHan.Framework.Workflow.Tests` 项目。不改引擎或主包来让用例通过——端到端用例失败首先怀疑本包的映射与存储。

- [ ] **Step 1: 创建引擎测试替身**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/EngineTestDoubles.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using XiHan.Framework.Caching.Distributed.Abstracts;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Timing;
using XiHan.Framework.Workflow.Events;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 可拨动测试时钟
/// </summary>
internal sealed class TestClock : IClock
{
    private DateTime _now = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 当前时间
    /// </summary>
    public DateTime Now => _now;

    /// <summary>
    /// 时间类型，固定为 UTC
    /// </summary>
    public DateTimeKind Kind => DateTimeKind.Utc;

    /// <summary>
    /// 是否支持多时区，固定为不支持
    /// </summary>
    public bool SupportsMultipleTimezone => false;

    /// <summary>
    /// 拨动时钟
    /// </summary>
    /// <param name="duration">前进时长</param>
    public void Advance(TimeSpan duration)
    {
        _now = _now.Add(duration);
    }

    /// <summary>
    /// 规范化时间，原样返回
    /// </summary>
    /// <param name="dateTime">时间</param>
    /// <returns>原时间</returns>
    public DateTime Normalize(DateTime dateTime)
    {
        return dateTime;
    }

    /// <summary>
    /// 转换为用户时间，原样返回
    /// </summary>
    /// <param name="utcDateTime">UTC 时间</param>
    /// <returns>原时间</returns>
    public DateTime ConvertToUserTime(DateTime utcDateTime)
    {
        return utcDateTime;
    }

    /// <summary>
    /// 转换为用户时间，原样返回
    /// </summary>
    /// <param name="dateTimeOffset">时间偏移</param>
    /// <returns>原时间</returns>
    public DateTimeOffset ConvertToUserTime(DateTimeOffset dateTimeOffset)
    {
        return dateTimeOffset;
    }

    /// <summary>
    /// 转换为 UTC 时间，原样返回
    /// </summary>
    /// <param name="dateTime">时间</param>
    /// <returns>原时间</returns>
    public DateTime ConvertToUtc(DateTime dateTime)
    {
        return dateTime;
    }
}

/// <summary>
/// 可切换的测试租户
/// </summary>
internal sealed class TestCurrentTenant : ICurrentTenant
{
    private readonly AsyncLocal<long?> _id = new();

    /// <summary>
    /// 当前租户是否可用
    /// </summary>
    public bool IsAvailable => Id.HasValue;

    /// <summary>
    /// 当前租户标识
    /// </summary>
    public long? Id => _id.Value;

    /// <summary>
    /// 当前租户名称，固定为 null
    /// </summary>
    public string? Name => null;

    /// <summary>
    /// 临时切换当前租户，释放时恢复
    /// </summary>
    /// <param name="id">租户标识</param>
    /// <param name="name">租户名称，忽略</param>
    /// <returns>恢复上下文的释放器</returns>
    public IDisposable Change(long? id, string? name = null)
    {
        var previous = _id.Value;
        _id.Value = id;
        return new RestoreScope(() => _id.Value = previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly Action _restore;

        public RestoreScope(Action restore)
        {
            _restore = restore;
        }

        public void Dispose()
        {
            _restore();
        }
    }
}

/// <summary>
/// 进程内测试分布式锁
/// </summary>
internal sealed class InProcessTestLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    /// <summary>
    /// 尝试获取锁，单次不阻塞不重试
    /// </summary>
    /// <param name="resourceKey">资源键</param>
    /// <param name="expiry">锁过期时间，忽略</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>锁句柄，获取失败返回 null</returns>
    public async Task<IDistributedLockHandle?> TryAcquireAsync(string resourceKey, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        var semaphore = _semaphores.GetOrAdd(resourceKey, _ => new SemaphoreSlim(1, 1));
        var acquired = await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken);
        return acquired ? new Handle(resourceKey, semaphore) : null;
    }

    private sealed class Handle : IDistributedLockHandle
    {
        private readonly SemaphoreSlim _semaphore;
        private int _released;

        public Handle(string resourceKey, SemaphoreSlim semaphore)
        {
            ResourceKey = resourceKey;
            _semaphore = semaphore;
        }

        public string ResourceKey { get; }

        public string LockId { get; } = Guid.NewGuid().ToString("N");

        public bool IsReleased => _released == 1;

        public Task ReleaseAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _semaphore.Release();
            }

            return Task.CompletedTask;
        }

        public Task<bool> ExtendAsync(TimeSpan expiry, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!IsReleased);
        }

        public void Dispose()
        {
            ReleaseAsync().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(ReleaseAsync());
        }
    }
}

/// <summary>
/// 丢弃全部事件的工作流事件发布器
/// </summary>
internal sealed class NullWorkflowEventPublisher : IWorkflowEventPublisher
{
    /// <summary>
    /// 发布事件，不做任何事
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="eventData">事件数据</param>
    /// <returns>已完成的任务</returns>
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : class
    {
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 创建测试主机**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowEngineTestHost.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Caching.Distributed.Abstracts;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Timing;
using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.Abstractions.Engine;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.Abstractions.Stores;
using XiHan.Framework.Workflow.Abstractions.UserTasks;
using XiHan.Framework.Workflow.Events;
using XiHan.Framework.Workflow.Extensions.DependencyInjection;
using XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 以 SqlSugar 存储组装真实工作流引擎的测试主机
/// </summary>
internal sealed class WorkflowEngineTestHost : IDisposable
{
    private readonly WorkflowTestDatabase _database;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="database">测试库，为空时新建临时 SQLite 库；主机负责释放</param>
    /// <param name="configureServices">在本包注册之后追加的服务注册</param>
    /// <param name="workerId">雪花标识的工作节点号，多个主机共用一个库时各取不同值</param>
    public WorkflowEngineTestHost(
        WorkflowTestDatabase? database = null,
        Action<IServiceCollection>? configureServices = null,
        ushort workerId = 1)
    {
        _database = database ?? WorkflowTestDatabase.CreateSqlite();

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions();
        services.AddLogging();

        Clock = new TestClock();
        services.AddSingleton<IClock>(Clock);
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant());
        services.AddSingleton<IDistributedLock>(new InProcessTestLock());
        services.AddSingleton(IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload(workerId));

        services.AddXiHanWorkflow(configuration);
        services.Replace(ServiceDescriptor.Singleton<IWorkflowEventPublisher, NullWorkflowEventPublisher>());

        services.AddSingleton<ISqlSugarClientResolver>(_database.Resolver);
        services.AddSingleton(_database.UnitOfWorkManager);
        services.AddXiHanWorkflowSqlSugar(configuration);
        configureServices?.Invoke(services);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    /// <summary>
    /// 可拨动测试时钟
    /// </summary>
    public TestClock Clock { get; }

    /// <summary>
    /// 工作流引擎
    /// </summary>
    public IWorkflowEngine Engine => _scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

    /// <summary>
    /// 定义管理器
    /// </summary>
    public IWorkflowDefinitionManager DefinitionManager => _scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionManager>();

    /// <summary>
    /// 人工任务服务
    /// </summary>
    public IWorkflowUserTaskService UserTaskService => _scope.ServiceProvider.GetRequiredService<IWorkflowUserTaskService>();

    /// <summary>
    /// 实例存储
    /// </summary>
    public IWorkflowInstanceStore InstanceStore => _scope.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>();

    /// <summary>
    /// 书签存储
    /// </summary>
    public IWorkflowBookmarkStore BookmarkStore => _scope.ServiceProvider.GetRequiredService<IWorkflowBookmarkStore>();

    /// <summary>
    /// 定义存储
    /// </summary>
    public IWorkflowDefinitionStore DefinitionStore => _scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();

    /// <summary>
    /// 创建并发布定义
    /// </summary>
    /// <param name="definition">定义内容</param>
    /// <returns>已发布的定义</returns>
    public async Task<WorkflowDefinition> PublishAsync(WorkflowDefinition definition)
    {
        var created = await DefinitionManager.CreateAsync(definition);
        return await DefinitionManager.PublishAsync(created.Id);
    }

    /// <summary>
    /// 从存储重新加载实例
    /// </summary>
    /// <param name="instanceId">实例标识</param>
    /// <returns>实例</returns>
    public async Task<WorkflowInstance> ReloadAsync(string instanceId)
    {
        return await InstanceStore.FindAsync(instanceId)
            ?? throw new InvalidOperationException($"实例 {instanceId} 不存在");
    }

    /// <summary>
    /// 释放作用域、容器与测试库
    /// </summary>
    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _database.Dispose();
    }
}
```

- [ ] **Step 3: 写端到端测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowEngineEndToEndTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.Builders;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 以 SqlSugar 存储运行真实引擎的端到端测试
/// </summary>
public class WorkflowEngineEndToEndTests : IDisposable
{
    private readonly WorkflowEngineTestHost _host = new();

    /// <summary>
    /// 延时书签到期后恢复并完成
    /// </summary>
    [Fact]
    public async Task 延时书签到期后恢复并完成()
    {
        await _host.PublishAsync(BuildDelayDefinition());

        var instance = await _host.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = "delay" });

        var timer = Assert.Single(await _host.BookmarkStore.GetByInstanceAsync(instance.Id));
        Assert.Equal(WorkflowBookmarkKinds.Timer, timer.Kind);
        Assert.Equal(_host.Clock.Now.AddSeconds(300), timer.DueTime);
        Assert.Empty(await _host.BookmarkStore.GetDueAsync(_host.Clock.Now, 10));

        _host.Clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Single(await _host.BookmarkStore.GetDueAsync(_host.Clock.Now, 10));

        await _host.Engine.ResumeBookmarkAsync(timer.Id);

        var reloaded = await _host.ReloadAsync(instance.Id);
        Assert.Equal(WorkflowInstanceStatus.Completed, reloaded.Status);
        Assert.Empty(await _host.BookmarkStore.GetByInstanceAsync(instance.Id));

        var history = await _host.InstanceStore.GetNodeInstancesAsync(instance.Id);
        Assert.Equal(["start", "wait", "end"], history.Select(item => item.NodeId));
    }

    /// <summary>
    /// 信号按相关性定向恢复后再广播
    /// </summary>
    [Fact]
    public async Task 信号按相关性定向恢复后再广播()
    {
        var definition = WorkflowDefinitionBuilder.Create("signal", "信号流程")
            .AddStart()
            .AddNode("wait", WorkflowActivityTypes.WaitSignal, "等待支付", node => node
                .WithProperty("SignalName", "order-paid"))
            .AddEnd()
            .AddTransition("start", "wait")
            .AddTransition("wait", "end")
            .Build();
        await _host.PublishAsync(definition);

        var first = await _host.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = "signal", CorrelationId = "ORDER-1" });
        var second = await _host.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = "signal", CorrelationId = "ORDER-2" });

        var targeted = await _host.Engine.PublishSignalAsync(
            "order-paid", new Dictionary<string, object?> { ["paidAmount"] = 88 }, "ORDER-1");

        Assert.Equal(1, targeted);
        var firstReloaded = await _host.ReloadAsync(first.Id);
        Assert.Equal(WorkflowInstanceStatus.Completed, firstReloaded.Status);
        Assert.Equal(88m, new WorkflowVariables(firstReloaded.Variables).Get<decimal>("paidAmount"));
        Assert.Equal(WorkflowInstanceStatus.Running, (await _host.ReloadAsync(second.Id)).Status);

        Assert.Equal(1, await _host.Engine.PublishSignalAsync("order-paid"));
        Assert.Equal(WorkflowInstanceStatus.Completed, (await _host.ReloadAsync(second.Id)).Status);
    }

    /// <summary>
    /// 会签节点状态跨两次办理保持
    /// </summary>
    [Fact]
    public async Task 会签节点状态跨两次办理保持()
    {
        var definition = WorkflowDefinitionBuilder.Create("all-approve", "会签流程")
            .AddStart()
            .AddUserTask("approve", "审批", node => node
                .WithProperty("Assignees", new List<string> { "u1", "u2" })
                .WithProperty("CompletionPolicy", "All")
                .WithProperty("Title", "单据 {{billNo}} 审批"))
            .AddNode("accepted", WorkflowActivityTypes.SetVariable, "通过", node => node
                .WithProperty("Values", new Dictionary<string, object?> { ["result"] = "accepted" }))
            .AddNode("denied", WorkflowActivityTypes.SetVariable, "拒绝", node => node
                .WithProperty("Values", new Dictionary<string, object?> { ["result"] = "denied" }))
            .AddEnd()
            .AddTransition("start", "approve")
            .AddTransition("approve", "accepted", "outcome == 'approved'")
            .AddTransition("approve", "denied", "outcome == 'rejected'")
            .AddTransition("accepted", "end")
            .AddTransition("denied", "end")
            .Build();
        await _host.PublishAsync(definition);

        var instance = await _host.Engine.StartAsync(new WorkflowStartRequest
        {
            DefinitionCode = "all-approve",
            Variables = new Dictionary<string, object?> { ["billNo"] = "B001" }
        });

        var firstTask = Assert.Single(await _host.UserTaskService.GetPendingAsync("u1"));
        Assert.Equal("单据 B001 审批", firstTask.Title);
        var afterFirst = await _host.UserTaskService.CompleteAsync(firstTask.TaskId, "u1", WorkflowUserTaskOutcomes.Approved);
        Assert.Equal(WorkflowInstanceStatus.Running, afterFirst.Status);

        var secondTask = Assert.Single(await _host.UserTaskService.GetPendingAsync("u2"));
        var afterSecond = await _host.UserTaskService.CompleteAsync(secondTask.TaskId, "u2", WorkflowUserTaskOutcomes.Approved);
        Assert.Equal(WorkflowInstanceStatus.Completed, afterSecond.Status);

        var reloaded = await _host.ReloadAsync(instance.Id);
        Assert.Equal("accepted", new WorkflowVariables(reloaded.Variables).Get<string>("result"));
    }

    /// <summary>
    /// 并行分支一支挂起后仍能汇合
    /// </summary>
    [Fact]
    public async Task 并行分支一支挂起后仍能汇合()
    {
        var definition = WorkflowDefinitionBuilder.Create("fork-wait", "并行等待")
            .AddStart()
            .AddParallel("fork")
            .AddNode("a", WorkflowActivityTypes.SetVariable, "分支A", node => node
                .WithProperty("Values", new Dictionary<string, object?> { ["a"] = 1 }))
            .AddNode("wait", WorkflowActivityTypes.WaitSignal, "等待", node => node
                .WithProperty("SignalName", "go"))
            .AddJoin("join")
            .AddEnd()
            .AddTransition("start", "fork")
            .AddTransition("fork", "a")
            .AddTransition("fork", "wait")
            .AddTransition("a", "join")
            .AddTransition("wait", "join")
            .AddTransition("join", "end")
            .Build();
        await _host.PublishAsync(definition);

        var instance = await _host.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = "fork-wait", CorrelationId = "FORK-1" });

        var suspended = await _host.ReloadAsync(instance.Id);
        Assert.Equal(WorkflowInstanceStatus.Running, suspended.Status);
        Assert.Single(suspended.JoinStates["join"].ArrivedTransitionIds);

        Assert.Equal(1, await _host.Engine.PublishSignalAsync("go"));

        var completed = await _host.ReloadAsync(instance.Id);
        Assert.Equal(WorkflowInstanceStatus.Completed, completed.Status);
        Assert.Empty(completed.JoinStates);
        Assert.Equal(1m, new WorkflowVariables(completed.Variables).Get<decimal>("a"));
    }

    /// <summary>
    /// 取消实例清空书签并取消挂起节点
    /// </summary>
    [Fact]
    public async Task 取消实例清空书签并取消挂起节点()
    {
        await _host.PublishAsync(BuildDelayDefinition());
        var instance = await _host.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = "delay" });

        await _host.Engine.CancelAsync(instance.Id, "不需要了");

        var reloaded = await _host.ReloadAsync(instance.Id);
        Assert.Equal(WorkflowInstanceStatus.Canceled, reloaded.Status);
        Assert.Equal("不需要了", reloaded.CancellationReason);
        Assert.Empty(await _host.BookmarkStore.GetByInstanceAsync(instance.Id));

        var history = await _host.InstanceStore.GetNodeInstancesAsync(instance.Id);
        Assert.Equal(WorkflowNodeInstanceStatus.Canceled, Assert.Single(history, item => item.NodeId == "wait").Status);
    }

    /// <summary>
    /// 挂起实例的到期书签回退到期时间
    /// </summary>
    [Fact]
    public async Task 挂起实例的到期书签回退到期时间()
    {
        await _host.PublishAsync(BuildDelayDefinition());
        var instance = await _host.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = "delay" });
        var timer = Assert.Single(await _host.BookmarkStore.GetByInstanceAsync(instance.Id));

        await _host.Engine.SuspendAsync(instance.Id);
        _host.Clock.Advance(TimeSpan.FromSeconds(301));
        await _host.Engine.ResumeBookmarkAsync(timer.Id, inputs: null, throwIfNotResumable: false);

        var kept = await _host.BookmarkStore.FindAsync(timer.Id);
        Assert.NotNull(kept);
        Assert.Equal(_host.Clock.Now.AddSeconds(300), kept.DueTime);
        Assert.Equal(WorkflowInstanceStatus.Suspended, (await _host.ReloadAsync(instance.Id)).Status);
    }

    /// <summary>
    /// 释放测试主机
    /// </summary>
    public void Dispose()
    {
        _host.Dispose();
    }

    private static WorkflowDefinition BuildDelayDefinition()
    {
        return WorkflowDefinitionBuilder.Create("delay", "延时流程")
            .AddStart()
            .AddDelay("wait", 300)
            .AddEnd()
            .AddTransition("start", "wait")
            .AddTransition("wait", "end")
            .Build();
    }
}
```

- [ ] **Step 4: 运行测试**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：六个端到端用例全部 PASS。

排查指引（**改本包，不改断言、不改引擎**）：

- `并行分支一支挂起后仍能汇合` 实例变成 `Faulted` 且故障信息含「汇聚网关 join 等待的分支已死亡」：`JoinStates` 没有写回或没有读回，查 `WorkflowInstanceMapper` 的 `JoinStatesJson`
- `会签节点状态跨两次办理保持` 第二次办理后仍是 `Running` 或第二个待办不存在：节点实例 `State` 没有写回，查 `UpdateNodeInstanceAsync`
- `延时书签到期后恢复并完成` 的执行历史顺序为 `wait, start, end`：`Sequence` 排序失效
- `挂起实例的到期书签回退到期时间` 的 `DueTime` 未变：书签 `UpdateAsync` 未写回
- 任何用例报「书签 ... 不存在或已被处理」而本不应如此：检查存储是否被写成了读外层连接、或返回共享引用

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "test(workflow-sqlsugar): 新增以 SqlSugar 存储运行真实引擎的端到端测试"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 7: 双节点竞争同一到期书签（MySQL）

**Files:**
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/RendezvousBookmarkStore.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkRaceMySqlTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarWorkflowBookmarkStore`（`DeleteAsync` 的 0 行抛出）；Task 6 的 `WorkflowEngineTestHost(WorkflowTestDatabase?, Action<IServiceCollection>?, ushort)`、`PublishAsync` 返回定义、`DefinitionStore`；第 1 份的 `WorkflowTestDatabase.CreateMySql`
- Produces: 测试辅助 `DeleteRendezvous`（构造 `(int parties)`，属性 `string? BookmarkId`，方法 `Task ArriveAsync(string id)`）、`RendezvousBookmarkStore : IWorkflowBookmarkStore`（构造 `(IWorkflowBookmarkStore inner, DeleteRendezvous rendezvous)`）

**参考来源（动手前先读）：**
- spec §4.7（调用点与守卫机制）、§6 第二层第二个用例
- `framework/src/XiHan.Framework.Workflow/Engine/WorkflowEngine.cs:370-400`（`ResumeBookmarkCoreAsync` 的锁内二次查找）与 `:490-520`（消费删除）

**本任务禁止事项：** 两个主机不得共用同一个 `WorkflowTestDatabase` 或同一把锁——各自一套才是「两个进程、没有 Redis」。雪花工作节点号必须不同。不为让用例变绿而放宽断言。

- [ ] **Step 1: 创建汇合装饰器**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/RendezvousBookmarkStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.Abstractions.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 让多个调用方对同一书签的删除在同一时刻放行的汇合点
/// </summary>
internal sealed class DeleteRendezvous
{
    private readonly int _parties;
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="parties">需要汇合的调用方数量</param>
    public DeleteRendezvous(int parties)
    {
        _parties = parties;
    }

    /// <summary>
    /// 需要汇合的书签标识，为空时不拦截任何删除
    /// </summary>
    public string? BookmarkId { get; set; }

    /// <summary>
    /// 到达汇合点，目标书签的删除要等全部调用方到达后才放行
    /// </summary>
    /// <param name="id">正在删除的书签标识</param>
    /// <returns>任务</returns>
    public async Task ArriveAsync(string id)
    {
        if (!string.Equals(id, BookmarkId, StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.Increment(ref _arrived) >= _parties)
        {
            _allArrived.TrySetResult();
        }

        await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

/// <summary>
/// 在删除前经过汇合点的书签存储装饰器
/// </summary>
internal sealed class RendezvousBookmarkStore : IWorkflowBookmarkStore
{
    private readonly IWorkflowBookmarkStore _inner;
    private readonly DeleteRendezvous _rendezvous;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="inner">被装饰的书签存储</param>
    /// <param name="rendezvous">删除汇合点</param>
    public RendezvousBookmarkStore(IWorkflowBookmarkStore inner, DeleteRendezvous rendezvous)
    {
        _inner = inner;
        _rendezvous = rendezvous;
    }

    /// <summary>
    /// 按标识查找书签
    /// </summary>
    /// <param name="id">书签标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签</returns>
    public Task<WorkflowBookmark?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        return _inner.FindAsync(id, cancellationToken);
    }

    /// <summary>
    /// 获取实例的全部书签
    /// </summary>
    /// <param name="instanceId">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表</returns>
    public Task<List<WorkflowBookmark>> GetByInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        return _inner.GetByInstanceAsync(instanceId, cancellationToken);
    }

    /// <summary>
    /// 获取节点实例的全部书签
    /// </summary>
    /// <param name="nodeInstanceId">节点实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表</returns>
    public Task<List<WorkflowBookmark>> GetByNodeInstanceAsync(string nodeInstanceId, CancellationToken cancellationToken = default)
    {
        return _inner.GetByNodeInstanceAsync(nodeInstanceId, cancellationToken);
    }

    /// <summary>
    /// 获取到期的定时类书签
    /// </summary>
    /// <param name="now">当前时间</param>
    /// <param name="maxResultCount">最大返回条数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>到期书签列表</returns>
    public Task<List<WorkflowBookmark>> GetDueAsync(DateTime now, int maxResultCount, CancellationToken cancellationToken = default)
    {
        return _inner.GetDueAsync(now, maxResultCount, cancellationToken);
    }

    /// <summary>
    /// 按种类和索引键查询书签
    /// </summary>
    /// <param name="kind">书签种类</param>
    /// <param name="key">索引键</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表</returns>
    public Task<List<WorkflowBookmark>> GetByKindAndKeyAsync(string kind, string key, CancellationToken cancellationToken = default)
    {
        return _inner.GetByKindAndKeyAsync(kind, key, cancellationToken);
    }

    /// <summary>
    /// 查询匹配信号的书签
    /// </summary>
    /// <param name="signalName">信号名称</param>
    /// <param name="correlationId">业务相关性标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>书签列表</returns>
    public Task<List<WorkflowBookmark>> GetBySignalAsync(string signalName, string? correlationId, CancellationToken cancellationToken = default)
    {
        return _inner.GetBySignalAsync(signalName, correlationId, cancellationToken);
    }

    /// <summary>
    /// 插入书签
    /// </summary>
    /// <param name="bookmark">书签</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public Task InsertAsync(WorkflowBookmark bookmark, CancellationToken cancellationToken = default)
    {
        return _inner.InsertAsync(bookmark, cancellationToken);
    }

    /// <summary>
    /// 更新书签
    /// </summary>
    /// <param name="bookmark">书签</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public Task UpdateAsync(WorkflowBookmark bookmark, CancellationToken cancellationToken = default)
    {
        return _inner.UpdateAsync(bookmark, cancellationToken);
    }

    /// <summary>
    /// 经过汇合点后删除书签
    /// </summary>
    /// <param name="id">书签标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _rendezvous.ArriveAsync(id);
        await _inner.DeleteAsync(id, cancellationToken);
    }

    /// <summary>
    /// 删除实例的全部书签
    /// </summary>
    /// <param name="instanceId">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public Task DeleteByInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        return _inner.DeleteByInstanceAsync(instanceId, cancellationToken);
    }
}
```

- [ ] **Step 2: 写双节点竞争用例**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowBookmarkRaceMySqlTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.Abstractions.Exceptions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.Abstractions.Stores;
using XiHan.Framework.Workflow.Builders;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 两个节点在没有跨进程锁时竞争同一书签的真实数据库测试
/// </summary>
/// <remarks>
/// 地址取环境变量 <c>XIHAN_TEST_MYSQL</c>，未设置时跳过。
/// 两个主机各自一套连接与进程内锁，共用同一个库。
/// </remarks>
public class WorkflowBookmarkRaceMySqlTests
{
    private const string SkipReason = "未设置 XIHAN_TEST_MYSQL，跳过真实数据库测试。";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("XIHAN_TEST_MYSQL");

    /// <summary>
    /// 两个节点竞争同一到期书签时只有一个执行批次运行
    /// </summary>
    [Fact]
    public async Task 两个节点竞争同一到期书签时只有一个执行批次运行()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        var rendezvous = new DeleteRendezvous(parties: 2);
        using var nodeA = new WorkflowEngineTestHost(
            WorkflowTestDatabase.CreateMySql(ConnectionString!), services => UseRendezvous(services, rendezvous), workerId: 11);
        using var nodeB = new WorkflowEngineTestHost(
            WorkflowTestDatabase.CreateMySql(ConnectionString!), services => UseRendezvous(services, rendezvous), workerId: 12);

        var code = "race-" + Guid.NewGuid().ToString("N");
        WorkflowDefinition? definition = null;
        WorkflowInstance? instance = null;

        try
        {
            definition = await nodeA.PublishAsync(BuildDelayDefinition(code));
            instance = await nodeA.Engine.StartAsync(new WorkflowStartRequest { DefinitionCode = code });

            var timer = Assert.Single(await nodeA.BookmarkStore.GetByInstanceAsync(instance.Id));
            rendezvous.BookmarkId = timer.Id;
            nodeA.Clock.Advance(TimeSpan.FromSeconds(301));
            nodeB.Clock.Advance(TimeSpan.FromSeconds(301));

            var outcomes = await Task.WhenAll(
                TryResumeAsync(nodeA, timer.Id),
                TryResumeAsync(nodeB, timer.Id));

            Assert.Equal(1, outcomes.Count(resumed => resumed));
            Assert.Equal(WorkflowInstanceStatus.Completed, (await nodeA.ReloadAsync(instance.Id)).Status);

            var history = await nodeA.InstanceStore.GetNodeInstancesAsync(instance.Id);
            Assert.Single(history, item => item.NodeId == "end");
        }
        finally
        {
            rendezvous.BookmarkId = null;

            if (instance is not null)
            {
                await nodeA.BookmarkStore.DeleteByInstanceAsync(instance.Id);
                await nodeA.InstanceStore.DeleteAsync(instance.Id);
            }

            if (definition is not null)
            {
                await nodeA.DefinitionStore.DeleteAsync(definition.Id);
            }
            else
            {
                var drafts = await nodeA.DefinitionStore.GetListAsync(code: code);
                foreach (var draft in drafts)
                {
                    await nodeA.DefinitionStore.DeleteAsync(draft.Id);
                }
            }
        }
    }

    private static void UseRendezvous(IServiceCollection services, DeleteRendezvous rendezvous)
    {
        services.Replace(ServiceDescriptor.Scoped<IWorkflowBookmarkStore>(provider => new RendezvousBookmarkStore(
            new SqlSugarWorkflowBookmarkStore(provider.GetRequiredService<WorkflowSqlSugarExecutor>()),
            rendezvous)));
    }

    private static async Task<bool> TryResumeAsync(WorkflowEngineTestHost node, string bookmarkId)
    {
        try
        {
            await node.Engine.ResumeBookmarkAsync(bookmarkId);
            return true;
        }
        catch (WorkflowException)
        {
            return false;
        }
    }

    private static WorkflowDefinition BuildDelayDefinition(string code)
    {
        return WorkflowDefinitionBuilder.Create(code, "竞争流程")
            .AddStart()
            .AddDelay("wait", 300)
            .AddEnd()
            .AddTransition("start", "wait")
            .AddTransition("wait", "end")
            .Build();
    }
}
```

汇合点只拦截目标书签的删除：两个节点都已在各自的实例锁内 `FindAsync` 到该书签、都走到 `WorkflowEngine.cs:518` 才会放行，竞争必然发生。没有汇合点，两个调用的先后取决于调度，用例可能碰巧只跑一个批次而掩盖守卫缺失。

- [ ] **Step 3: 无环境变量时确认跳过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：该用例 skipped，其余 PASS。

- [ ] **Step 4: 本机真库运行**

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：`两个节点竞争同一到期书签时只有一个执行批次运行` PASS。

**若抛 `TimeoutException`**：只有一个节点走到了删除——另一个节点在锁内 `FindAsync` 时已找不到书签。检查 `rendezvous.BookmarkId` 是否在两次 `TryResumeAsync` 之前设置，以及装饰器是否替换进了容器（`configureServices` 必须在 `AddXiHanWorkflowSqlSugar` 之后执行）。

- [ ] **Step 5: 反向验证：拿掉守卫后必须变红**

临时把 `SqlSugarWorkflowBookmarkStore.DeleteAsync` 里的

```csharp
        if (affected == 0)
        {
            throw new WorkflowException($"书签 {id} 不存在或已被处理");
        }
```

整段删掉，重跑 Step 4。

预期：本用例 **FAIL** 于 `Assert.Equal(1, outcomes.Count(...))`（两个调用都成功；若继续执行，`end` 节点出现两条）。同时 Task 3 的 `删除不存在或已被删除的书签抛出工作流异常` 也 FAIL。

确认后恢复，重跑确认全绿，`git diff` 确认存储无残留改动。

- [ ] **Step 6: 提交**

```bash
git add framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "test(workflow-sqlsugar): 新增两个节点竞争同一书签只推进一次的真实数据库用例"
```

---

### Task 8: 进程内锁的启动警告

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceProviderExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Workflow.SqlSugar/XiHanWorkflowSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/DistributedLockWarningTests.cs`

**Interfaces:**
- Consumes: `XiHan.Framework.Caching.Distributed.Abstracts.IDistributedLock`、`XiHan.Framework.Caching.Distributed.DefaultDistributedLock`、`XiHan.Framework.Core.Application.ApplicationInitializationContext`
- Produces: `public static bool WarnIfWorkflowLockIsProcessLocal(this IServiceProvider serviceProvider)`（记录了警告返回 `true`）；模块重写 `OnApplicationInitialization`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Caching/Distributed/DefaultDistributedLock.cs`（类注释：只在当前进程内互斥）
- `framework/src/XiHan.Framework.Caching/Extensions/DependencyInjection/XiHanCachingServiceCollectionExtensions.cs:43`、`:94`
- `framework/src/XiHan.Framework.Core/Modularity/XiHanModule.cs:137`
- spec §4.8

**本任务禁止事项：** 不新增托管服务；不抛异常、不阻止启动；模块类里只有一行调用，判断与日志写在扩展方法里。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/DistributedLockWarningTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using XiHan.Framework.Caching.Distributed;
using XiHan.Framework.Caching.Distributed.Abstracts;
using XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 进程内分布式锁的启动警告测试
/// </summary>
public class DistributedLockWarningTests
{
    /// <summary>
    /// 默认进程内锁记录一条警告
    /// </summary>
    [Fact]
    public void 默认进程内锁记录一条警告()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs, services => services.AddSingleton<IDistributedLock>(new DefaultDistributedLock()));

        Assert.True(provider.WarnIfWorkflowLockIsProcessLocal());

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(DefaultDistributedLock), entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 其他锁实现不记录警告
    /// </summary>
    [Fact]
    public void 其他锁实现不记录警告()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs, services => services.AddSingleton<IDistributedLock>(new InProcessTestLock()));

        Assert.False(provider.WarnIfWorkflowLockIsProcessLocal());
        Assert.Empty(logs.Entries);
    }

    /// <summary>
    /// 未注册锁时不记录警告
    /// </summary>
    [Fact]
    public void 未注册锁时不记录警告()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs, _ => { });

        Assert.False(provider.WarnIfWorkflowLockIsProcessLocal());
        Assert.Empty(logs.Entries);
    }

    private static ServiceProvider BuildProvider(CapturingLoggerProvider logs, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        configure(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 记录全部日志条目的日志提供程序
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        /// <summary>
        /// 已记录的日志条目
        /// </summary>
        public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(_entries);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries;

        public CapturingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries)
        {
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`WarnIfWorkflowLockIsProcessLocal` 不存在。

实现后做一次反向验证：临时删掉扩展方法里的 `LogWarning` 调用，`默认进程内锁记录一条警告` 必须失败于 `Assert.Single(logs.Entries)`；确认后恢复。

- [ ] **Step 3: 创建扩展方法**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceProviderExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XiHan.Framework.Caching.Distributed;
using XiHan.Framework.Caching.Distributed.Abstracts;

namespace XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 工作流 SqlSugar 存储的服务提供者扩展
/// </summary>
public static class XiHanWorkflowSqlSugarServiceProviderExtensions
{
    /// <summary>
    /// 解析出的分布式锁只在进程内互斥时记录一条警告
    /// </summary>
    /// <param name="serviceProvider">服务提供者</param>
    /// <returns>记录了警告返回 true</returns>
    public static bool WarnIfWorkflowLockIsProcessLocal(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (serviceProvider.GetService<IDistributedLock>() is not DefaultDistributedLock)
        {
            return false;
        }

        serviceProvider.GetService<ILoggerFactory>()?
            .CreateLogger(typeof(XiHanWorkflowSqlSugarModule))
            .LogWarning(
                "当前分布式锁为 {LockType}，只在进程内互斥。工作流以多实例部署时请配置 Redis 分布式锁：" +
                "同一书签的重复恢复已由书签存储拦截，但同一实例的不同书签被多个节点同时恢复时仍会相互覆盖。",
                nameof(DefaultDistributedLock));

        return true;
    }
}
```

- [ ] **Step 4: 模块调用**

修改 `framework/src/XiHan.Framework.Workflow.SqlSugar/XiHanWorkflowSqlSugarModule.cs`。using 区追加：

```csharp
using XiHan.Framework.Core.Application;
```

在 `ConfigureServices` 方法之后追加：

```csharp
    /// <summary>
    /// 应用初始化
    /// </summary>
    /// <param name="context">应用初始化上下文</param>
    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        context.ServiceProvider.WarnIfWorkflowLockIsProcessLocal();
    }
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 分布式锁只在进程内互斥时在启动时记录警告"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 9: 包 README

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/README.md`

**Interfaces:** 无

**参考来源（动手前先读）：** `framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`（七段结构与语气）；三份 spec 的 §7 已知边界。

**本任务禁止事项：** 七段标题与顺序固定：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构。不声称仓库里有 `.codegraph/`。

- [ ] **Step 1: 写 README**

`framework/src/XiHan.Framework.Workflow.SqlSugar/README.md`：

````markdown
# XiHan.Framework.Workflow.SqlSugar

## 概述

`XiHan.Framework.Workflow` 的定义、实例与书签的 SqlSugar 持久化提供程序。主包的三个默认存储是有界的进程内实现，进程重启即全部丢失、不跨实例；本包把它们落到四张表。

## 核心能力

- 四张表：`sys_workflow_definition`、`sys_workflow_instance`、`sys_workflow_node_instance`、`sys_workflow_bookmark`
- 以 `Replace` 替换 `IWorkflowDefinitionStore`、`IWorkflowInstanceStore`、`IWorkflowBookmarkStore` 的默认实现，生命周期 Scoped
- 每次存储操作在独立的事务型工作单元内执行并在返回前提交，不加入调用方的工作单元；引擎释放实例锁时，写入对其他节点已可见
- 书签按实例、节点实例、到期时间、种类与索引键查询均有索引；按种类与索引键匹配区分大小写，不受数据库排序规则影响
- 执行历史按开始时间与创建顺序返回，取消时的补偿逆序与内存实现一致
- 表结构由 `DbInitializer` 在应用启动时创建，**必须开启** `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization`（二者默认均为 `false`）

## 依赖关系

依赖 `XiHan.Framework.Workflow`（存储契约与默认注册）与 `XiHan.Framework.Data`（SqlSugar 客户端解析、工作单元、建表初始化）。

## 配置与约定

配置节 `XiHan:Workflow:SqlSugar`，`ConfigId` 指定工作流表所在连接的配置标识，为空时使用 `XiHan:Data:SqlSugarCore:DefaultConfigId`。四张表固定落在这一个连接上，不随租户切库；表只在平台库（静态配置的连接）建出。

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为契约的字符串标识，非自增。可变状态（变量、汇聚波次、节点输入输出与私有状态、书签附加数据）以 JSON 存储，序列化选项为 `JsonSerializerOptions.Web`。

未开启建表初始化又没有手工建表时，首次写入即抛「表不存在」。自行维护表结构时按本包实体的列与索引定义建表。

**多实例部署应启用 Redis 分布式锁。** 引擎靠实例级分布式锁串行化同一实例的推进，定时器 Worker 靠分布式锁保证集群单活。默认的 `DefaultDistributedLock` 只在进程内互斥。本包的书签存储在按标识删除未删到任何行时抛 `WorkflowException`，因此即使没有 Redis 锁，同一个书签也只会被一个节点推进；但同一实例的**不同**书签（并行分支、会签的多个受理人、超时与办理）被多个节点同时恢复时，仍会并发推进、后写覆盖先写。配置 `XiHan:Caching` 的 Redis 连接后框架自动替换为 Redis 锁；使用默认锁时本包在应用初始化时记录一条警告。

**书签删除比内存实现严格。** `IWorkflowBookmarkStore.DeleteAsync` 删除不存在或已被删除的书签会抛 `WorkflowException`（内存默认实现静默成功）；`DeleteByInstanceAsync` 不受影响。

**工作流写入不随业务回滚。** 存储的每次操作独立提交，在业务事务里启动流程后业务回滚，流程实例仍在。需要「业务失败则不启动」时，在业务提交之后再启动流程。

**SQLite 与外层事务不能共用。** 外层工作单元已写过同一个 SQLite 库时，存储的独立连接会撞 `database is locked`。

**变量必须可 JSON 序列化。** 不可序列化的变量会让执行批次在写回时抛出，实例停在运行中且书签已被消费。读回后整数与小数都是 `decimal`、嵌套对象是 `JsonElement`，业务代码应经 `WorkflowVariables` / `WorkflowValueConverter` 取值。

**删除实例前先删书签。** `IWorkflowInstanceStore.DeleteAsync` 只级联删除节点实例；应先调 `IWorkflowBookmarkStore.DeleteByInstanceAsync`，否则人工任务书签成为孤儿。

**推进过程不是原子的。** 每次恢复书签先删除书签并提交，再逐个节点提交执行结果，最后提交实例状态；启动先插入实例再执行；取消、终止、重试同理。在这些提交之间进程崩溃或重新部署，会留下状态为运行中、却没有任何书签的实例，它不会再被推进，也不报错。内存实现崩溃时什么都不留下，这是持久化带来的新情况。建议定期查询 `sys_workflow_instance` 中 `Status = 1` 且在 `sys_workflow_bookmark` 中没有书签的实例，人工判断后取消或终止。

**已完成实例永久保留。** 本包不自动清理，保留策略由应用决定。

**编码、相关性等字符串比较随数据库排序规则。** 定义编码、实例列表的过滤条件交给数据库比较，MySQL 默认不区分大小写；书签匹配另做序数过滤，不受影响。

**MySQL 的时间没有小数秒。** `datetime` 写入时四舍五入到秒，读回的时间可能与写入相差不到一秒；执行历史顺序由 `Sequence` 列保证。

**插入与更新不是 upsert。** 重复主键插入抛出数据库异常；更新不存在的标识什么也不做。同一编码的定义并发创建会撞 `(Code, Version)` 唯一索引，调用方需重试。

**存储是 Scoped 服务。** 在服务作用域内解析 `IWorkflowEngine` 等服务，不要从根容器解析。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanWorkflowSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

开启建表：

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

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.Workflow.Abstractions.Stores` 下的契约并在 DI 中 `Replace`。`WorkflowDefinitionMapper`、`WorkflowInstanceMapper`、`WorkflowNodeInstanceMapper`、`WorkflowBookmarkMapper` 为公开的静态映射，可供运维工具直接读写表数据。

## 目录结构

```
Entities/                        四个工作流实体
Mapping/                         契约与实体的双向映射、JSON 列工具
Options/                         存储配置
Stores/                          执行器与三个存储实现
Extensions/DependencyInjection/  服务注册扩展
```
````

（外层四个反引号只是本计划的代码块围栏，README 文件内容从 `# XiHan.Framework.Workflow.SqlSugar` 开始，到最后一个 ```` ``` ```` 结束。）

- [ ] **Step 2: 提交**

```bash
git add framework/src/XiHan.Framework.Workflow.SqlSugar/README.md
git commit -m "docs(workflow-sqlsugar): 新增包 README"
```

---

### Task 10: 文档站页面、侧边栏与模块清单

**Files:**
- Create: `docs/packages/workflow-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`
- Modify: `framework/README.md`
- Modify: `framework/README_cn.md`
- Modify: `README.md`（只改模块计数，含徽章）
- Modify: `README_cn.md`（只改模块计数，含徽章）
- Modify: `docs/index.md`（模块计数）
- Modify: `docs/introduction.md`（模块计数）
- Modify: `docs/why.md`（模块计数，含「包参考 N 页」）
- 以及 Step 5 的 grep 在 `docs/**/*.md` 中找到的其他模块计数

**Interfaces:** 无

**参考来源（动手前先读）：**
- `docs/packages/eventbus-sqlsugar.md`（页面结构）
- `docs/.vitepress/config.ts:190-210`（「存储 · 模板 · 任务 · 治理」分组）
- `docs/packages/index.md:105-120`
- `framework/README.md:86-95`、`framework/README_cn.md:86-95`

**本任务禁止事项：** 不改 `docs/packages/workflow.md`；不在根 `README.md` / `README_cn.md` 的「常用包」表格加行（它是精选清单，`EventBus.SqlSugar`、`Auditing.SqlSugar` 也未列入），根 README 只改计数；不改 `framework/README*.md` 的架构图；不写死计数数字与行号。

- [ ] **Step 1: 写文档站页面**

`docs/packages/workflow-sqlsugar.md`：

````markdown
# XiHan.Framework.Workflow.SqlSugar

> 工作流定义、实例与书签的 SqlSugar 持久化提供程序：替换 [Workflow](./workflow) 的进程内默认存储后，流程实例跨进程重启、跨实例共享。

- **NuGet**：`XiHan.Framework.Workflow.SqlSugar`
- **模块类**：`XiHanWorkflowSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Workflow](./workflow)（存储契约与引擎）、[Data](./data)（SqlSugar 客户端、工作单元、建表）

## 概述

[Workflow](./workflow) 的三个存储端口——定义、实例（含执行历史）、书签——默认是有界的进程内字典：进程重启即全部丢失，也不跨实例。本包把它们落到四张表，并保证引擎的并发模型在数据库上依然成立。

## 何时使用

- 流程需要跨进程重启存活（审批挂起数天、定时器等待数小时）
- 应用以多实例部署，任一实例都要能办理任务、接收信号
- 已在用 [Data](./data)，希望工作流数据与业务数据用同一套连接配置

不需要本包的场景：只在单进程内跑短流程，重启丢失可以接受。

## 安装与启用

```bash
dotnet add package XiHan.Framework.Workflow.SqlSugar
```

```csharp
[DependsOn(typeof(XiHanWorkflowSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

建表需要开启 [Data](./data) 的初始化，**两个开关默认都是关闭的**：

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

::: warning 多实例应启用 Redis 分布式锁
引擎靠实例级分布式锁串行化同一实例的推进，定时器 Worker 靠分布式锁保证集群单活。默认的进程内锁不跨实例。本包的书签删除守卫保证同一书签只被一个节点推进，但同一实例的不同书签被多个节点同时恢复时仍会相互覆盖。配置 [Caching](./caching) 的 Redis 连接后框架自动换成 Redis 锁；使用默认锁时本包在启动时记录警告。
:::

## 表结构

四张表，**都不分表**，只建在平台库。

| 表 | 内容 | 主要索引 |
| --- | --- | --- |
| `sys_workflow_definition` | 定义：编码、版本、状态；节点、连线、变量声明、扩展属性各一个 JSON 列 | `(Code, Version)` 唯一 |
| `sys_workflow_instance` | 实例：状态、相关性、父子关系、故障信息；变量与汇聚波次为 JSON 列 | 状态、定义编码、相关性、父实例 |
| `sys_workflow_node_instance` | 执行历史：每次节点执行一行；输入、输出、私有状态为 JSON 列；`Sequence` 为创建顺序 | `(Instance_Id, Start_Time, Sequence)` |
| `sys_workflow_bookmark` | 书签：种类、索引键、到期时间、相关性；附加数据为 JSON 列 | 实例、节点实例、到期时间、`(Kind, Bookmark_Key, Creation_Time)` |

主键为契约的字符串标识。租户列 `Tenant_Id` 原样保存契约给的值，不参与全局租户过滤——契约规定存储不做租户过滤，隔离由引擎与任务服务在结果上执行。

## 工作原理

### 每次操作独立提交

存储的每次读写都在一个新开的事务型工作单元里执行，并在返回前提交；调用方若已在一个工作单元里，本包另开一条连接，不加入它。

原因是引擎的锁协议：同一实例的推进在实例锁内完成，锁一释放，下一个持锁者必须能读到上一个持锁者的全部写入。若存储加入请求的工作单元，写入要等请求结束才提交，另一节点在锁释放之后、提交之前拿到锁，会读到旧状态并重复推进。读同样走独立事务，保证读到最新提交、且在配置了从库时走主库。

代价是：在业务事务里启动流程后业务回滚，流程实例仍在。

### 书签消费守卫

引擎消费书签时先删除书签、再开始执行批次。本包的 `DeleteAsync` 在未删到任何行时抛 `WorkflowException`：两个节点竞争同一书签时，后到者在批次开始之前放弃，定时器 Worker 与信号投递把它当作「已被并发处理」跳过，人工办理的调用方得到「书签不存在或已被处理」。

### 书签匹配

| 查询 | 调用方 | 条件 |
| --- | --- | --- |
| 到期书签 | 定时器 Worker 每轮 | `Due_Time <= now`，按到期时间升序，取配置的条数 |
| 信号 | `PublishSignalAsync` | 种类为信号、键为信号名；相关性非空时再要求书签相关性为空或相等 |
| 种类 + 键 | 待办列表、子流程回调 | 种类与键相等，按创建时间升序 |

按种类与键匹配的两个查询在数据库条件之后再做一次区分大小写的比较，因此在 MySQL 默认的不区分大小写排序规则下，`alice` 也不会看到 `Alice` 的待办。

### 执行历史顺序

取消实例时，引擎按执行历史逆序补偿。MySQL 的 `datetime` 没有小数秒，同一秒开始的节点只能靠 `Sequence`（由雪花标识解析）排序，本包按 `Start_Time, Sequence` 返回。

## 配置

配置节 `XiHan:Workflow:SqlSugar`（`XiHanWorkflowSqlSugarOptions.SectionName`）。

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `ConfigId` | `string?` | 空 | 工作流表所在连接的配置标识；为空时用 `XiHan:Data:SqlSugarCore:DefaultConfigId` |

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanWorkflowSqlSugarModule` | 模块类，声明依赖即启用 |
| `SqlSugarWorkflowDefinitionStore` | `IWorkflowDefinitionStore` 的 SqlSugar 实现 |
| `SqlSugarWorkflowInstanceStore` | `IWorkflowInstanceStore` 的 SqlSugar 实现 |
| `SqlSugarWorkflowBookmarkStore` | `IWorkflowBookmarkStore` 的 SqlSugar 实现 |
| `WorkflowSqlSugarExecutor` | 三个存储共用的独立事务执行器 |
| `SysWorkflowDefinition` / `SysWorkflowInstance` / `SysWorkflowNodeInstance` / `SysWorkflowBookmark` | 四个实体 |
| `XiHanWorkflowSqlSugarOptions` | 连接配置 |

## 注意事项与最佳实践

- **变量必须可 JSON 序列化**。读回后整数与小数都是 `decimal`、嵌套对象是 `JsonElement`，经 `WorkflowVariables` / `WorkflowValueConverter` 取值。
- **删除实例前先删书签**：先 `IWorkflowBookmarkStore.DeleteByInstanceAsync`，再 `IWorkflowInstanceStore.DeleteAsync`。
- **推进过程不是原子的**：恢复、启动、取消、重试都由多次独立提交组成，进程在中途崩溃或重新部署会留下运行中却没有书签的实例；定期查询这类实例并人工处理。
- **书签删除比内存实现严格**：`DeleteAsync` 删除不存在的书签抛 `WorkflowException`。
- **已完成实例永久保留**，清理策略由应用实现。
- **定义编码与实例过滤条件随数据库排序规则比较**，MySQL 默认不区分大小写；编码保持大小写一致。
- **SQLite 不适合与外层事务共用**：外层已写同一个库时，存储的独立连接会撞 `database is locked`。
- **在服务作用域内解析引擎**：存储是 Scoped。

## 扩展点 / 自定义

实现 `XiHan.Framework.Workflow.Abstractions.Stores` 下的契约并在 DI 中 `Replace`。四个 `*Mapper` 静态类公开契约与实体的映射，可供运维工具直接读写表数据。

## 依赖模块

- [Workflow](./workflow)：存储契约、引擎、定时器 Worker
- [Data](./data)：SqlSugar 客户端解析、工作单元、建表初始化

## 相关模块

- [Workflow.Abstractions](./workflow-abstractions)：存储端口与运行时模型
- [Caching](./caching)：Redis 分布式锁
- [EventBus.SqlSugar](./eventbus-sqlsugar) / [Auditing.SqlSugar](./auditing-sqlsugar)：同一套落库范式的其他实现
````

（外层四个反引号只是本计划的代码块围栏。）

- [ ] **Step 2: 侧边栏**

修改 `docs/.vitepress/config.ts`，把

```ts
          pkg("Workflow 工作流", "workflow"),
```

改为

```ts
          pkg("Workflow 工作流", "workflow"),
          pkg("Workflow.SqlSugar", "workflow-sqlsugar"),
```

- [ ] **Step 3: 模块索引**

修改 `docs/packages/index.md`，把

```markdown
| [Workflow](./workflow) | 工作流引擎：图执行、17 个内置活动、人工任务（审批）、表达式、定时器 |
```

改为

```markdown
| [Workflow](./workflow) | 工作流引擎：图执行、17 个内置活动、人工任务（审批）、表达式、定时器 |
| [Workflow.SqlSugar](./workflow-sqlsugar) | 工作流 SqlSugar 持久化提供程序：定义、实例、执行历史与书签落库，每次操作独立提交 |
```

- [ ] **Step 4: 框架模块清单**

修改 `framework/README.md`，把

```markdown
| `Workflow` | Workflow engine: graph execution engine, built-in activity set, human tasks (approvals), expression evaluation, timer scheduling, in-memory store by default |
```

改为

```markdown
| `Workflow` | Workflow engine: graph execution engine, built-in activity set, human tasks (approvals), expression evaluation, timer scheduling, in-memory store by default |
| `Workflow.SqlSugar` | SqlSugar persistence provider for workflows: definitions, instances, execution history and bookmarks; every store call commits in its own transaction |
```

修改 `framework/README_cn.md`，把

```markdown
| `Workflow` | 工作流引擎：图执行引擎、内置活动集、人工任务（审批）、表达式求值、定时器调度、内存存储默认实现 |
```

改为

```markdown
| `Workflow` | 工作流引擎：图执行引擎、内置活动集、人工任务（审批）、表达式求值、定时器调度、内存存储默认实现 |
| `Workflow.SqlSugar` | 工作流 SqlSugar 持久化提供程序：定义、实例、执行历史与书签落库，每次存储操作独立事务提交 |
```

- [ ] **Step 5: 模块计数加一**

计数分布在 4 个 README 与文档站多个页面（合计约 20 处），其他 SqlSugar 包可能先于本包合入，**不要写死数字或行号**，按下面的步骤现场确定。

1. 从根 `README.md` 的 shields.io 徽章读出当前模块数 N（形如 `https://img.shields.io/badge/Modules-N-1f6feb`）：

```bash
grep -n "Modules-[0-9]*-1f6feb" README.md
```

2. 列出所有出现 N 的位置（把 `N` 换成上一步读到的数字）：

```bash
grep -rn "N" README.md README_cn.md framework/README.md framework/README_cn.md docs --include=*.md
```

3. 逐条判断：表示模块数、包数、「包参考 N 页」、`src/` 模块数的，改为 N+1；**徽章 URL 里的 `Modules-N-1f6feb` 也要改**（数字夹在 URL 中间，最容易漏）。与模块数无关的巧合数字（版本号、日期、端口等）不改。
4. `framework/README.md` 与 `framework/README_cn.md` 里「N 个单测工程 / N unit-test projects」数的是**测试项目**：先单独读出它的当前值 T（可能与 N 不同），本包新增一个测试项目，改为 T+1。
5. 用第 2 步的 grep 再跑一次（换成 N 与 N+1 各一次），确认旧值只剩与模块数无关的命中，新值出现在每一处该改的位置。

截至 2026-09-28 的参考分布（仅供核对，以现场 grep 为准）：`README.md` 与 `README_cn.md` 各 3 处（第 8 行标语、第 20 行徽章、第 55 行文档站说明）；`framework/README.md` 与 `framework/README_cn.md` 各 3 处（第 55、186 行为模块数，第 198 行为测试项目数）；`docs/index.md` 2 处、`docs/introduction.md` 1 处、`docs/why.md` 4 处（含「包参考 N 页」）、`docs/packages/index.md` 1 处。

- [ ] **Step 6: 文档站构建（本机有 pnpm 时）**

```bash
cd docs && pnpm install && pnpm build
```

预期：构建成功，无死链告警指向 `workflow-sqlsugar`。本机没有 pnpm 时跳过，并在 PR 描述里注明「未本地构建文档站」。

- [ ] **Step 7: 提交**

```bash
git add docs README.md README_cn.md framework/README.md framework/README_cn.md
git commit -m "docs(workflow-sqlsugar): 新增文档站页面并登记侧边栏与模块清单"
```

---

### Task 11: 全量验收

**Files:** 无新增

- [ ] **Step 1: 全量构建与测试**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：**0 Warning(s) 0 Error(s)**；全部测试通过（真库用例在无环境变量时跳过；已知抖动 `MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 除外）。

- [ ] **Step 2: 本机真库全部跑一遍**

设置 `XIHAN_TEST_MYSQL` 后运行测试项目，确认三个 MySQL 用例都通过。

- [ ] **Step 3: 七处登记核对**

逐项确认：

1. `framework/src/XiHan.Framework.Workflow.SqlSugar/XiHan.Framework.Workflow.SqlSugar.csproj` 按序 Import `netcore` / `common` / `version` / `nuget`
2. `XiHanWorkflowSqlSugarModule.cs` 的 `ConfigureServices` 只调 `AddXiHanWorkflowSqlSugar`、`OnApplicationInitialization` 只调 `WarnIfWorkflowLockIsProcessLocal`；注册扩展含执行器与三行 `Replace`
3. `README.md` 七段齐全，写明两个建表开关默认 `false`
4. `framework/XiHan.Framework.slnx` 的 `/1.src/6.Infrastructure/` 与 `/2.tests/1.UnitTests/` 各有一个新项目
5. `docs/packages/workflow-sqlsugar.md`、`docs/.vitepress/config.ts` 侧边栏、`docs/packages/index.md`
6. `framework/README.md` 与 `framework/README_cn.md` 模块清单；模块计数在 4 个 README 与 `docs/**/*.md` 的全部出现处（含徽章）已加一，测试项目计数已加一；根 README 的「常用包」表格未加行

- [ ] **Step 4: 注释复查**

通读三份计划新建的全部 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [ ] **Step 5: 范围复查**

```bash
git diff --stat dev...HEAD
```

确认改动只涉及：新包目录、新测试目录、`framework/XiHan.Framework.slnx`、`docs/packages/workflow-sqlsugar.md`、`docs/packages/index.md`、`docs/.vitepress/config.ts`、`framework/README.md`、`framework/README_cn.md`，以及只改了计数的 `README.md`、`README_cn.md` 与 `docs/` 下各页。出现其他文件或计数以外的改动即回退。

---

## 完成标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 三个存储均只有一条描述符，实现在 `XiHan.Framework.Workflow.SqlSugar.Stores`，Scoped
- 信号匹配三种相关性语义正确；书签匹配区分大小写（MySQL 用例证明）
- `GetDueAsync` 排除无到期时间与未到期、按到期时间升序、遵守上限
- 六个引擎端到端流程在 SQLite 上通过
- 三个 MySQL 用例本机通过（第 2 份隔离可见性、本份大小写与双节点竞争），且各自做过「拿掉关键条件变红」的验证
- `DeleteAsync` 删不存在的书签抛 `WorkflowException`；`DeleteByInstanceAsync` 不抛
- 解析出 `DefaultDistributedLock` 时启动记录一条警告
- 七处登记齐全（含 `docs/packages/index.md`），模块计数全部加一

## 已知边界（写入 PR 描述，不写进代码注释）

- **多实例应上 Redis 锁**：书签删除守卫保证同一书签只推进一次；同一实例不同书签的并发恢复仍会相互覆盖，最好情况下实例进入 `Faulted`；启动时有警告
- **书签删除比内存实现严格**：`DeleteAsync` 删不存在的书签抛 `WorkflowException`，这是有意的差异
- **工作流写入不随业务回滚**
- **SQLite + 外层事务会锁库**
- **变量必须可 JSON 序列化**；读回类型归一化为 `decimal` / `JsonElement`
- **删除实例不删书签**；**推进过程非原子**：恢复、启动、取消、重试在提交之间崩溃会留下运行中且无书签的实例（持久化带来的新情况）
- **已完成实例永久保留**，无自动清理
- **定义编码与实例过滤条件随数据库排序规则**；书签匹配不受影响
- **MySQL 时间精度到秒**；定时书签最多晚不到一秒被取到
- **插入与更新不是 upsert**；并发创建同编码定义会撞唯一索引
- **根 README 只改了模块计数**：「常用包」表格是精选清单，未加行
- **`docs/packages/workflow.md` 的「示例 6：换成持久化存储」未改**：仍是手写存储的示例，未指向本包

## 下一份计划

无。本份是 `Workflow.SqlSugar` 的最后一份，完成后整理 PR。
