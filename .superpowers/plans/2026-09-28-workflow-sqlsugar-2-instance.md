# Workflow.SqlSugar 第 2 份：流程实例与节点实例存储 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以 SqlSugar 实现替换 `IWorkflowInstanceStore`（10 个方法），并用真实 MySQL 用例证明「外层事务未提交时，存储写入已对其他连接可见」。

**Architecture:** 两张表 `sys_workflow_instance`、`sys_workflow_node_instance`。可变状态（变量、汇聚波次、节点输入输出与私有状态）各存一个 JSON 列，序列化沿用第 1 份的 `WorkflowJsonColumn`。节点实例新增 `Sequence` 列（标识解析出的 `long`），执行历史按 `Start_Time, Sequence, Basic_Id` 排序。所有读写经第 1 份的 `WorkflowSqlSugarExecutor`；`DeleteAsync` 的两条删除在执行器的同一个工作单元里。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-workflow-sqlsugar-2-instance-design.md`

**Linear 议题:** https://linear.app/elf-express/issue/EDDIE-14

**前置:** 第 1 份（`.superpowers/plans/2026-09-28-workflow-sqlsugar-1-skeleton-definition.md`）必须已完成。本计划依赖其 `WorkflowSqlSugarExecutor`、`WorkflowJsonColumn`、`XiHanWorkflowSqlSugarServiceCollectionExtensions`、测试夹具 `WorkflowTestDatabase`、`TestEntityTypes`、`ServiceRegistrationTests.CreateServices`。

> spec 自成一体。**先读 spec 第 2、5 节再动手。**

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：沿用第 1 份的 worktree `E:/source/XiHan/XiHan.Framework-workflow`，分支 `feat/workflow-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。若 worktree 不存在：

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
ISugarQueryable<T> OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc);   // 多次调用依次追加排序键
ISugarQueryable<T> Take(int num);
Task<List<T>> ToListAsync(CancellationToken token);
Task<List<T>> ToListAsync();
int Count();

// SqlSugar —— Interface/Insertable.cs、IUpdateable.cs、IDeleteable.cs
Task<int> ExecuteCommandAsync(CancellationToken token);
Task<int> ExecuteCommandAsync();
IDeleteable<T> Where(Expression<Func<T, bool>> expression);

// SqlSugar —— Entities/Mapping/SugarMappingAttribute.cs（AllowMultiple = true）
SugarIndexAttribute(string indexName, string fieldName, OrderByType sortType, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3, bool isUnique = false)

// 第 1 份产出
WorkflowSqlSugarExecutor.ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation, CancellationToken cancellationToken)
WorkflowJsonColumn.Serialize<T>(T value) / WorkflowJsonColumn.Deserialize<T>(string? json) where T : class, new()   // internal

// 框架
XiHan.Framework.Workflow.Abstractions.Runtime.WorkflowVariables(Dictionary<string, object?> variables)
WorkflowVariables.Get<T>(string name) / WorkflowVariables.Get(string name)
XiHan.Framework.Uow.Options.XiHanUnitOfWorkOptions(bool isTransactional = false, ...)
```

**编码约定**：每个 `.cs` 以两行版权声明开头（`XHFH001`）；注释与 XML 文档注释一律简体中文、只写代码做什么；file-scoped namespace；表达式体方法与构造函数关闭；`public` 成员必须有 `<summary>`。

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.
```

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform**：**没有可用的筛选参数**（`--filter`、`--list-tests` 退出码 3），要跑单个测试类就整个项目跑；**不要带 `--logger trx` / `--results-directory`**（退出码 5）。命令 `dotnet test --project <csproj> -c Release`，全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`。

**测试项目**：沿用第 1 份的 csproj，不加 `PackageReference`。MySQL 驱动经 `SqlSugarCore` 传递引入。

**SQLite 临时库**：连接串带 `Pooling=False`（第 1 份夹具已处理）。

**真实数据库测试**：`Assert.SkipWhen(...)` + `XIHAN_TEST_MYSQL`，CI 自动跳过。范式 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`。

**构建环境坑**：`MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，`taskkill //F //IM "<name>.exe"` 后重建。

**已知的无关抖动**：`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败，与本系列无关。

**测试代码里调 `Options.Create` 写全限定名** `Microsoft.Extensions.Options.Options.Create(...)`（命名空间 `XiHan.Framework.Workflow.SqlSugar.Options` 会遮蔽它）。

**提交信息**：中文 Conventional Commits，作用域 `workflow-sqlsugar`。**不加任何 AI 署名。**

---

## 本计划特有的硬约束

**① 所有读写经 `WorkflowSqlSugarExecutor`，读也不例外。**

锁内的 `FindAsync(instanceId)` 读到外层事务的旧快照或从库的滞后数据，整批推进都会基于旧状态。SQLite 构造不出来，只有 Task 5 的 MySQL 用例能发现。

**② 执行历史排序必须是 `Start_Time, Sequence, Basic_Id`，`Sequence` 由标识解析为 `long`。**

只按时间排在 SQLite 上全绿、在 MySQL 上同秒随机；按字符串标识排 `"10" < "9"`。补偿按这个顺序逆序执行。

**③ 更新是纯更新：全部列、按主键、0 行不补插、不忽略空值列。**

`RetryAsync` 依赖把 `EndTime`、`FaultMessage` 等写回 `null`。

**④ 每次查询都新建契约对象，不缓存、不返回共享引用。**

**⑤ `DeleteAsync` 只删实例与节点实例，不碰书签表。**

---

## File Structure

```
framework/src/XiHan.Framework.Workflow.SqlSugar/
  Entities/SysWorkflowInstance.cs                                            新建
  Entities/SysWorkflowNodeInstance.cs                                        新建
  Mapping/WorkflowInstanceMapper.cs                                          新建
  Mapping/WorkflowNodeInstanceMapper.cs                                      新建
  Stores/SqlSugarWorkflowInstanceStore.cs                                    新建
  Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs   修改：追加一行 Replace

framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/
  TestEntityTypes.cs                                                         修改：加两个类型
  WorkflowInstanceEntityTests.cs                                             新建
  WorkflowInstanceMapperTests.cs                                             新建
  SqlSugarWorkflowInstanceStoreTests.cs                                      新建
  WorkflowIsolationMySqlTests.cs                                             新建
  ServiceRegistrationTests.cs                                                修改：追加用例
```

---

### Task 1: 实例与节点实例实体

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowInstance.cs`
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowNodeInstance.cs`
- Modify: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestEntityTypes.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowInstanceEntityTests.cs`

**Interfaces:**
- Consumes: 第 1 份的实体写法；`SugarEntity<string>`、`TableInitializationAttribute`
- Produces:
  - `SysWorkflowInstance : SugarEntity<string>`，属性：`DefinitionId`、`DefinitionCode`、`DefinitionVersion`、`Name`、`Status`（`int`）、`VariablesJson`、`JoinStatesJson`、`CorrelationId`、`StarterId`、`ParentInstanceId`、`ParentNodeInstanceId`、`Depth`、`OwnerTenantId`、`CreationTime`、`StartTime`、`EndTime`、`FaultMessage`、`FaultNodeId`、`FaultNodeInstanceId`、`CancellationReason`
  - `SysWorkflowNodeInstance : SugarEntity<string>`，属性：`InstanceId`、`NodeId`、`Name`、`ActivityType`、`Status`（`int`）、`TryCount`、`StartTime`、`EndTime`、`InputsJson`、`OutputsJson`、`StateJson`、`FaultMessage`、`CompensatedTime`、`OwnerTenantId`、`Sequence`（`long`）

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowDefinition.cs`（第 1 份）
- `framework/src/XiHan.Framework.Workflow.Abstractions/Runtime/WorkflowInstance.cs`、`WorkflowNodeInstance.cs`
- spec §4.1、§4.2

**本任务禁止事项：** 不出现名为 `TenantId` 的属性；`SugarIndex` 字段参数用 `nameof(属性)`；索引名带 `{table}`。第 1 份的 `WorkflowEntityConventionTests` 会对新实体自动生效。

- [ ] **Step 1: 写失败的测试**

把 `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestEntityTypes.cs` 中的

```csharp
    public static Type[] All { get; } = [typeof(SysWorkflowDefinition)];
```

改为

```csharp
    public static Type[] All { get; } =
    [
        typeof(SysWorkflowDefinition),
        typeof(SysWorkflowInstance),
        typeof(SysWorkflowNodeInstance)
    ];
```

新建 `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowInstanceEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using System.Reflection;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程实例与节点实例实体测试
/// </summary>
public class WorkflowInstanceEntityTests
{
    /// <summary>
    /// 表名固定
    /// </summary>
    [Fact]
    public void 表名固定()
    {
        Assert.Equal("sys_workflow_instance", typeof(SysWorkflowInstance).GetCustomAttribute<SugarTable>()?.TableName);
        Assert.Equal("sys_workflow_node_instance", typeof(SysWorkflowNodeInstance).GetCustomAttribute<SugarTable>()?.TableName);
    }

    /// <summary>
    /// 实例有四个查询索引
    /// </summary>
    [Fact]
    public void 实例有四个查询索引()
    {
        var names = typeof(SysWorkflowInstance)
            .GetCustomAttributes<SugarIndexAttribute>()
            .Select(item => item.IndexName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["idx_{table}_correlation", "idx_{table}_definition_code", "idx_{table}_parent", "idx_{table}_status"],
            names);
    }

    /// <summary>
    /// 节点实例索引覆盖执行历史排序
    /// </summary>
    [Fact]
    public void 节点实例索引覆盖执行历史排序()
    {
        var index = Assert.Single(typeof(SysWorkflowNodeInstance).GetCustomAttributes<SugarIndexAttribute>());

        Assert.Equal(
            [nameof(SysWorkflowNodeInstance.InstanceId), nameof(SysWorkflowNodeInstance.StartTime), nameof(SysWorkflowNodeInstance.Sequence)],
            index.IndexFields.Keys);
        Assert.False(index.IsUnique);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysWorkflowInstance`、`SysWorkflowNodeInstance` 不存在。

- [ ] **Step 3: 创建实例实体**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowInstance.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Workflow.SqlSugar.Entities;

/// <summary>
/// 流程实例实体
/// </summary>
[SugarTable("sys_workflow_instance")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("idx_{table}_status", nameof(Status), OrderByType.Asc, nameof(CreationTime), OrderByType.Desc)]
[SugarIndex("idx_{table}_definition_code", nameof(DefinitionCode), OrderByType.Asc, nameof(CreationTime), OrderByType.Desc)]
[SugarIndex("idx_{table}_correlation", nameof(CorrelationId), OrderByType.Asc)]
[SugarIndex("idx_{table}_parent", nameof(ParentInstanceId), OrderByType.Asc, nameof(CreationTime), OrderByType.Asc)]
public class SysWorkflowInstance : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysWorkflowInstance() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">实例标识</param>
    public SysWorkflowInstance(string basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 定义标识
    /// </summary>
    [SugarColumn(ColumnName = "Definition_Id", Length = 255, IsNullable = false, ColumnDescription = "定义标识")]
    public string DefinitionId { get; set; } = string.Empty;

    /// <summary>
    /// 定义编码
    /// </summary>
    [SugarColumn(ColumnName = "Definition_Code", Length = 128, IsNullable = false, ColumnDescription = "定义编码")]
    public string DefinitionCode { get; set; } = string.Empty;

    /// <summary>
    /// 定义版本
    /// </summary>
    [SugarColumn(ColumnName = "Definition_Version", IsNullable = false, ColumnDescription = "定义版本")]
    public int DefinitionVersion { get; set; }

    /// <summary>
    /// 实例名称
    /// </summary>
    [SugarColumn(ColumnName = "Name", Length = 256, IsNullable = false, ColumnDescription = "实例名称")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 状态，1 运行中，2 已挂起，3 已完成，4 已取消，5 已故障，6 已终止
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "状态，1 运行中，2 已挂起，3 已完成，4 已取消，5 已故障，6 已终止")]
    public int Status { get; set; }

    /// <summary>
    /// 实例变量的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Variables_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "实例变量的 JSON")]
    public string VariablesJson { get; set; } = "{}";

    /// <summary>
    /// 汇聚网关波次状态的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Join_States_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "汇聚网关波次状态的 JSON")]
    public string JoinStatesJson { get; set; } = "{}";

    /// <summary>
    /// 业务相关性标识
    /// </summary>
    [SugarColumn(ColumnName = "Correlation_Id", Length = 255, IsNullable = true, ColumnDescription = "业务相关性标识")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// 发起人标识
    /// </summary>
    [SugarColumn(ColumnName = "Starter_Id", Length = 255, IsNullable = true, ColumnDescription = "发起人标识")]
    public string? StarterId { get; set; }

    /// <summary>
    /// 父实例标识
    /// </summary>
    [SugarColumn(ColumnName = "Parent_Instance_Id", Length = 255, IsNullable = true, ColumnDescription = "父实例标识")]
    public string? ParentInstanceId { get; set; }

    /// <summary>
    /// 父节点实例标识
    /// </summary>
    [SugarColumn(ColumnName = "Parent_Node_Instance_Id", Length = 255, IsNullable = true, ColumnDescription = "父节点实例标识")]
    public string? ParentNodeInstanceId { get; set; }

    /// <summary>
    /// 实例深度
    /// </summary>
    [SugarColumn(ColumnName = "Depth", IsNullable = false, ColumnDescription = "实例深度")]
    public int Depth { get; set; }

    /// <summary>
    /// 租户标识
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识")]
    public long? OwnerTenantId { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Creation_Time", IsNullable = false, ColumnDescription = "创建时间")]
    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 开始时间
    /// </summary>
    [SugarColumn(ColumnName = "Start_Time", IsNullable = true, ColumnDescription = "开始时间")]
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// 结束时间
    /// </summary>
    [SugarColumn(ColumnName = "End_Time", IsNullable = true, ColumnDescription = "结束时间")]
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 故障信息
    /// </summary>
    [SugarColumn(ColumnName = "Fault_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "故障信息")]
    public string? FaultMessage { get; set; }

    /// <summary>
    /// 故障节点标识
    /// </summary>
    [SugarColumn(ColumnName = "Fault_Node_Id", Length = 255, IsNullable = true, ColumnDescription = "故障节点标识")]
    public string? FaultNodeId { get; set; }

    /// <summary>
    /// 故障节点实例标识
    /// </summary>
    [SugarColumn(ColumnName = "Fault_Node_Instance_Id", Length = 255, IsNullable = true, ColumnDescription = "故障节点实例标识")]
    public string? FaultNodeInstanceId { get; set; }

    /// <summary>
    /// 取消或终止原因
    /// </summary>
    [SugarColumn(ColumnName = "Cancellation_Reason", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "取消或终止原因")]
    public string? CancellationReason { get; set; }
}
```

- [ ] **Step 4: 创建节点实例实体**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowNodeInstance.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Workflow.SqlSugar.Entities;

/// <summary>
/// 流程节点实例实体
/// </summary>
[SugarTable("sys_workflow_node_instance")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("idx_{table}_instance", nameof(InstanceId), OrderByType.Asc, nameof(StartTime), OrderByType.Asc, nameof(Sequence), OrderByType.Asc)]
public class SysWorkflowNodeInstance : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysWorkflowNodeInstance() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">节点实例标识</param>
    public SysWorkflowNodeInstance(string basicId) : base(basicId)
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
    /// 节点名称快照
    /// </summary>
    [SugarColumn(ColumnName = "Name", Length = 256, IsNullable = false, ColumnDescription = "节点名称快照")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 活动类型编码
    /// </summary>
    [SugarColumn(ColumnName = "Activity_Type", Length = 128, IsNullable = false, ColumnDescription = "活动类型编码")]
    public string ActivityType { get; set; } = string.Empty;

    /// <summary>
    /// 状态，1 执行中，2 已挂起，3 已完成，4 已取消，5 已故障，6 已补偿
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "状态，1 执行中，2 已挂起，3 已完成，4 已取消，5 已故障，6 已补偿")]
    public int Status { get; set; }

    /// <summary>
    /// 尝试次数
    /// </summary>
    [SugarColumn(ColumnName = "Try_Count", IsNullable = false, ColumnDescription = "尝试次数")]
    public int TryCount { get; set; }

    /// <summary>
    /// 开始时间
    /// </summary>
    [SugarColumn(ColumnName = "Start_Time", IsNullable = false, ColumnDescription = "开始时间")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 结束时间
    /// </summary>
    [SugarColumn(ColumnName = "End_Time", IsNullable = true, ColumnDescription = "结束时间")]
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 输入快照的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Inputs_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "输入快照的 JSON")]
    public string InputsJson { get; set; } = "{}";

    /// <summary>
    /// 输出快照的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Outputs_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "输出快照的 JSON")]
    public string OutputsJson { get; set; } = "{}";

    /// <summary>
    /// 活动私有状态的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "State_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "活动私有状态的 JSON")]
    public string StateJson { get; set; } = "{}";

    /// <summary>
    /// 故障信息
    /// </summary>
    [SugarColumn(ColumnName = "Fault_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "故障信息")]
    public string? FaultMessage { get; set; }

    /// <summary>
    /// 补偿时间
    /// </summary>
    [SugarColumn(ColumnName = "Compensated_Time", IsNullable = true, ColumnDescription = "补偿时间")]
    public DateTime? CompensatedTime { get; set; }

    /// <summary>
    /// 租户标识
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识")]
    public long? OwnerTenantId { get; set; }

    /// <summary>
    /// 创建顺序，由节点实例标识解析得到，无法解析时为 0
    /// </summary>
    [SugarColumn(ColumnName = "Sequence", IsNullable = false, ColumnDescription = "创建顺序，由节点实例标识解析得到，无法解析时为 0")]
    public long Sequence { get; set; }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，包括第 1 份的 `WorkflowEntityConventionTests` 对两个新实体的检查。夹具的 `InitTables(TestEntityTypes.All)` 此时会建出三张表。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增流程实例与节点实例实体"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 2: 实例与节点实例映射

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowInstanceMapper.cs`
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowNodeInstanceMapper.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowInstanceMapperTests.cs`

**Interfaces:**
- Consumes: Task 1 的两个实体；第 1 份的 `WorkflowJsonColumn`
- Produces:
  - `public static class WorkflowInstanceMapper`：`SysWorkflowInstance ToEntity(WorkflowInstance instance)`、`WorkflowInstance ToInstance(SysWorkflowInstance entity)`
  - `public static class WorkflowNodeInstanceMapper`：`SysWorkflowNodeInstance ToEntity(WorkflowNodeInstance nodeInstance)`、`WorkflowNodeInstance ToNodeInstance(SysWorkflowNodeInstance entity)`、`long ParseSequence(string id)`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowDefinitionMapper.cs`（第 1 份）
- `framework/src/XiHan.Framework.Workflow/Stores/DefaultWorkflowInstanceStore.cs` 的 `GetNodeInstancesAsync`（`long.TryParse` 的二级排序）
- `framework/src/XiHan.Framework.Workflow.Abstractions/Runtime/WorkflowVariables.cs`
- spec §4.3

**本任务禁止事项：** 字符串原样保留，`""` 与 `null` 不互转。不新建 JSON 选项，一律经 `WorkflowJsonColumn`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowInstanceMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Mapping;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程实例与节点实例映射测试
/// </summary>
public class WorkflowInstanceMapperTests
{
    private static readonly DateTime BaseTime = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 实例标量字段往返一致
    /// </summary>
    [Fact]
    public void 实例标量字段往返一致()
    {
        var instance = CreateInstance();

        var entity = WorkflowInstanceMapper.ToEntity(instance);
        var restored = WorkflowInstanceMapper.ToInstance(entity);

        Assert.Equal("2001", entity.BasicId);
        Assert.Equal((int)WorkflowInstanceStatus.Faulted, entity.Status);
        Assert.Equal(9, entity.OwnerTenantId);
        Assert.Equal(instance.Id, restored.Id);
        Assert.Equal(instance.DefinitionId, restored.DefinitionId);
        Assert.Equal(instance.DefinitionCode, restored.DefinitionCode);
        Assert.Equal(instance.DefinitionVersion, restored.DefinitionVersion);
        Assert.Equal(instance.Name, restored.Name);
        Assert.Equal(instance.Status, restored.Status);
        Assert.Equal(instance.StarterId, restored.StarterId);
        Assert.Equal(instance.ParentInstanceId, restored.ParentInstanceId);
        Assert.Equal(instance.ParentNodeInstanceId, restored.ParentNodeInstanceId);
        Assert.Equal(instance.Depth, restored.Depth);
        Assert.Equal(instance.TenantId, restored.TenantId);
        Assert.Equal(instance.CreationTime, restored.CreationTime);
        Assert.Equal(instance.StartTime, restored.StartTime);
        Assert.Equal(instance.EndTime, restored.EndTime);
        Assert.Equal(instance.FaultMessage, restored.FaultMessage);
        Assert.Equal(instance.FaultNodeId, restored.FaultNodeId);
        Assert.Equal(instance.FaultNodeInstanceId, restored.FaultNodeInstanceId);
        Assert.Equal(instance.CancellationReason, restored.CancellationReason);
    }

    /// <summary>
    /// 相关性标识的空串与空值各自保留
    /// </summary>
    [Fact]
    public void 相关性标识的空串与空值各自保留()
    {
        var emptyInstance = CreateInstance();
        emptyInstance.CorrelationId = string.Empty;
        var nullInstance = CreateInstance();
        nullInstance.CorrelationId = null;

        Assert.Equal(string.Empty, WorkflowInstanceMapper.ToInstance(WorkflowInstanceMapper.ToEntity(emptyInstance)).CorrelationId);
        Assert.Null(WorkflowInstanceMapper.ToInstance(WorkflowInstanceMapper.ToEntity(nullInstance)).CorrelationId);
    }

    /// <summary>
    /// 变量往返后可按原类型取值
    /// </summary>
    [Fact]
    public void 变量往返后可按原类型取值()
    {
        var instance = CreateInstance();
        instance.Variables = new Dictionary<string, object?>
        {
            ["text"] = "a",
            ["count"] = 3,
            ["amount"] = 12.5m,
            ["flag"] = true,
            ["when"] = BaseTime,
            ["nested"] = new Dictionary<string, object?> { ["k"] = "v" },
            ["list"] = new List<string> { "x", "y" },
            ["none"] = null
        };

        var restored = WorkflowInstanceMapper.ToInstance(WorkflowInstanceMapper.ToEntity(instance));
        var variables = new WorkflowVariables(restored.Variables);

        Assert.Equal("a", variables.Get<string>("text"));
        Assert.Equal(3, variables.Get<int>("count"));
        Assert.Equal(12.5m, variables.Get<decimal>("amount"));
        Assert.True(variables.Get<bool>("flag"));
        Assert.Equal(BaseTime, variables.Get<DateTime>("when"));
        Assert.Equal("v", variables.Get<Dictionary<string, string>>("nested")?["k"]);
        Assert.Equal(["x", "y"], variables.Get<List<string>>("list"));
        Assert.True(restored.Variables.ContainsKey("none"));
        Assert.Null(variables.Get("none"));
    }

    /// <summary>
    /// 汇聚波次状态往返一致
    /// </summary>
    [Fact]
    public void 汇聚波次状态往返一致()
    {
        var instance = CreateInstance();
        instance.JoinStates = new Dictionary<string, WorkflowJoinState>
        {
            ["join"] = new WorkflowJoinState { ArrivedTransitionIds = ["t1", "t2"], Fired = true }
        };

        var restored = WorkflowInstanceMapper.ToInstance(WorkflowInstanceMapper.ToEntity(instance));

        var state = Assert.Single(restored.JoinStates);
        Assert.Equal("join", state.Key);
        Assert.True(state.Value.Fired);
        Assert.True(state.Value.ArrivedTransitionIds.SetEquals(["t1", "t2"]));
    }

    /// <summary>
    /// 节点实例往返一致
    /// </summary>
    [Fact]
    public void 节点实例往返一致()
    {
        var nodeInstance = new WorkflowNodeInstance
        {
            Id = "3001",
            InstanceId = "2001",
            NodeId = "approve",
            Name = "审批",
            ActivityType = "UserTask",
            Status = WorkflowNodeInstanceStatus.Suspended,
            TryCount = 2,
            StartTime = BaseTime,
            EndTime = BaseTime.AddSeconds(5),
            Inputs = new Dictionary<string, object?> { ["actorId"] = "u1" },
            Outputs = new Dictionary<string, object?> { ["outcome"] = "approved" },
            State = new Dictionary<string, object?> { ["assignees"] = new List<string> { "u1", "u2" } },
            FaultMessage = "旧故障",
            CompensatedTime = BaseTime.AddSeconds(9),
            TenantId = 9
        };

        var entity = WorkflowNodeInstanceMapper.ToEntity(nodeInstance);
        var restored = WorkflowNodeInstanceMapper.ToNodeInstance(entity);

        Assert.Equal(3001, entity.Sequence);
        Assert.Equal((int)WorkflowNodeInstanceStatus.Suspended, entity.Status);
        Assert.Equal(nodeInstance.Id, restored.Id);
        Assert.Equal(nodeInstance.InstanceId, restored.InstanceId);
        Assert.Equal(nodeInstance.NodeId, restored.NodeId);
        Assert.Equal(nodeInstance.Name, restored.Name);
        Assert.Equal(nodeInstance.ActivityType, restored.ActivityType);
        Assert.Equal(nodeInstance.Status, restored.Status);
        Assert.Equal(nodeInstance.TryCount, restored.TryCount);
        Assert.Equal(nodeInstance.StartTime, restored.StartTime);
        Assert.Equal(nodeInstance.EndTime, restored.EndTime);
        Assert.Equal(nodeInstance.FaultMessage, restored.FaultMessage);
        Assert.Equal(nodeInstance.CompensatedTime, restored.CompensatedTime);
        Assert.Equal(nodeInstance.TenantId, restored.TenantId);
        Assert.Equal("u1", WorkflowValueConverter.ConvertTo<string>(restored.Inputs["actorId"]));
        Assert.Equal("approved", WorkflowValueConverter.ConvertTo<string>(restored.Outputs["outcome"]));
        Assert.Equal(["u1", "u2"], WorkflowValueConverter.ConvertTo<List<string>>(restored.State["assignees"]));
    }

    /// <summary>
    /// 创建顺序由标识解析
    /// </summary>
    [Fact]
    public void 创建顺序由标识解析()
    {
        Assert.Equal(123, WorkflowNodeInstanceMapper.ParseSequence("123"));
        Assert.Equal(0, WorkflowNodeInstanceMapper.ParseSequence("abc"));
        Assert.Equal(0, WorkflowNodeInstanceMapper.ParseSequence(string.Empty));
    }

    private static WorkflowInstance CreateInstance()
    {
        return new WorkflowInstance
        {
            Id = "2001",
            DefinitionId = "1001",
            DefinitionCode = "leave",
            DefinitionVersion = 3,
            Name = "请假",
            Status = WorkflowInstanceStatus.Faulted,
            CorrelationId = "ORDER-1",
            StarterId = "u0",
            ParentInstanceId = "2000",
            ParentNodeInstanceId = "3000",
            Depth = 1,
            TenantId = 9,
            CreationTime = BaseTime,
            StartTime = BaseTime,
            EndTime = BaseTime.AddMinutes(1),
            FaultMessage = "故障",
            FaultNodeId = "approve",
            FaultNodeInstanceId = "3001",
            CancellationReason = "原因"
        };
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，两个映射类不存在。

- [ ] **Step 3: 创建实例映射**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowInstanceMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Mapping;

/// <summary>
/// 流程实例契约与实体的双向映射
/// </summary>
public static class WorkflowInstanceMapper
{
    /// <summary>
    /// 把流程实例转换为实体
    /// </summary>
    /// <param name="instance">流程实例</param>
    /// <returns>实例实体</returns>
    public static SysWorkflowInstance ToEntity(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new SysWorkflowInstance(instance.Id)
        {
            DefinitionId = instance.DefinitionId,
            DefinitionCode = instance.DefinitionCode,
            DefinitionVersion = instance.DefinitionVersion,
            Name = instance.Name,
            Status = (int)instance.Status,
            VariablesJson = WorkflowJsonColumn.Serialize(instance.Variables),
            JoinStatesJson = WorkflowJsonColumn.Serialize(instance.JoinStates),
            CorrelationId = instance.CorrelationId,
            StarterId = instance.StarterId,
            ParentInstanceId = instance.ParentInstanceId,
            ParentNodeInstanceId = instance.ParentNodeInstanceId,
            Depth = instance.Depth,
            OwnerTenantId = instance.TenantId,
            CreationTime = instance.CreationTime,
            StartTime = instance.StartTime,
            EndTime = instance.EndTime,
            FaultMessage = instance.FaultMessage,
            FaultNodeId = instance.FaultNodeId,
            FaultNodeInstanceId = instance.FaultNodeInstanceId,
            CancellationReason = instance.CancellationReason
        };
    }

    /// <summary>
    /// 把实体转换为流程实例
    /// </summary>
    /// <param name="entity">实例实体</param>
    /// <returns>流程实例</returns>
    public static WorkflowInstance ToInstance(SysWorkflowInstance entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new WorkflowInstance
        {
            Id = entity.BasicId,
            DefinitionId = entity.DefinitionId,
            DefinitionCode = entity.DefinitionCode,
            DefinitionVersion = entity.DefinitionVersion,
            Name = entity.Name,
            Status = (WorkflowInstanceStatus)entity.Status,
            Variables = WorkflowJsonColumn.Deserialize<Dictionary<string, object?>>(entity.VariablesJson),
            JoinStates = WorkflowJsonColumn.Deserialize<Dictionary<string, WorkflowJoinState>>(entity.JoinStatesJson),
            CorrelationId = entity.CorrelationId,
            StarterId = entity.StarterId,
            ParentInstanceId = entity.ParentInstanceId,
            ParentNodeInstanceId = entity.ParentNodeInstanceId,
            Depth = entity.Depth,
            TenantId = entity.OwnerTenantId,
            CreationTime = entity.CreationTime,
            StartTime = entity.StartTime,
            EndTime = entity.EndTime,
            FaultMessage = entity.FaultMessage,
            FaultNodeId = entity.FaultNodeId,
            FaultNodeInstanceId = entity.FaultNodeInstanceId,
            CancellationReason = entity.CancellationReason
        };
    }
}
```

- [ ] **Step 4: 创建节点实例映射**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowNodeInstanceMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Mapping;

/// <summary>
/// 节点实例契约与实体的双向映射
/// </summary>
public static class WorkflowNodeInstanceMapper
{
    /// <summary>
    /// 把节点实例转换为实体
    /// </summary>
    /// <param name="nodeInstance">节点实例</param>
    /// <returns>节点实例实体</returns>
    public static SysWorkflowNodeInstance ToEntity(WorkflowNodeInstance nodeInstance)
    {
        ArgumentNullException.ThrowIfNull(nodeInstance);

        return new SysWorkflowNodeInstance(nodeInstance.Id)
        {
            InstanceId = nodeInstance.InstanceId,
            NodeId = nodeInstance.NodeId,
            Name = nodeInstance.Name,
            ActivityType = nodeInstance.ActivityType,
            Status = (int)nodeInstance.Status,
            TryCount = nodeInstance.TryCount,
            StartTime = nodeInstance.StartTime,
            EndTime = nodeInstance.EndTime,
            InputsJson = WorkflowJsonColumn.Serialize(nodeInstance.Inputs),
            OutputsJson = WorkflowJsonColumn.Serialize(nodeInstance.Outputs),
            StateJson = WorkflowJsonColumn.Serialize(nodeInstance.State),
            FaultMessage = nodeInstance.FaultMessage,
            CompensatedTime = nodeInstance.CompensatedTime,
            OwnerTenantId = nodeInstance.TenantId,
            Sequence = ParseSequence(nodeInstance.Id)
        };
    }

    /// <summary>
    /// 把实体转换为节点实例
    /// </summary>
    /// <param name="entity">节点实例实体</param>
    /// <returns>节点实例</returns>
    public static WorkflowNodeInstance ToNodeInstance(SysWorkflowNodeInstance entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new WorkflowNodeInstance
        {
            Id = entity.BasicId,
            InstanceId = entity.InstanceId,
            NodeId = entity.NodeId,
            Name = entity.Name,
            ActivityType = entity.ActivityType,
            Status = (WorkflowNodeInstanceStatus)entity.Status,
            TryCount = entity.TryCount,
            StartTime = entity.StartTime,
            EndTime = entity.EndTime,
            Inputs = WorkflowJsonColumn.Deserialize<Dictionary<string, object?>>(entity.InputsJson),
            Outputs = WorkflowJsonColumn.Deserialize<Dictionary<string, object?>>(entity.OutputsJson),
            State = WorkflowJsonColumn.Deserialize<Dictionary<string, object?>>(entity.StateJson),
            FaultMessage = entity.FaultMessage,
            CompensatedTime = entity.CompensatedTime,
            TenantId = entity.OwnerTenantId
        };
    }

    /// <summary>
    /// 把节点实例标识解析为创建顺序，无法解析时返回 0
    /// </summary>
    /// <param name="id">节点实例标识</param>
    /// <returns>创建顺序</returns>
    public static long ParseSequence(string id)
    {
        return long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ? sequence : 0;
    }
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
git commit -m "feat(workflow-sqlsugar): 新增流程实例与节点实例映射"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 3: 实例存储

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowInstanceStore.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/SqlSugarWorkflowInstanceStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的两个映射；第 1 份的 `WorkflowSqlSugarExecutor`
- Produces: `public class SqlSugarWorkflowInstanceStore : IWorkflowInstanceStore`，构造 `(WorkflowSqlSugarExecutor executor)`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.Abstractions/Stores/IWorkflowInstanceStore.cs`
- `framework/src/XiHan.Framework.Workflow/Stores/DefaultWorkflowInstanceStore.cs`（语义基准）
- `framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowDefinitionStore.cs`（第 1 份，写法范本）
- spec §4.4、§4.5

**本任务禁止事项：** 硬约束 ①～⑤。表达式里不出现 `status.Value`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/SqlSugarWorkflowInstanceStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程实例存储测试
/// </summary>
public class SqlSugarWorkflowInstanceStoreTests : IDisposable
{
    private static readonly DateTime BaseTime = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    private readonly WorkflowTestDatabase _database = WorkflowTestDatabase.CreateSqlite();
    private readonly SqlSugarWorkflowInstanceStore _store;

    /// <summary>
    /// 构造函数
    /// </summary>
    public SqlSugarWorkflowInstanceStoreTests()
    {
        _store = new SqlSugarWorkflowInstanceStore(_database.Executor);
    }

    /// <summary>
    /// 插入后按标识可查到
    /// </summary>
    [Fact]
    public async Task 插入后按标识可查到()
    {
        var instance = NewInstance("i1", BaseTime);
        instance.Variables["amount"] = 88;

        await _store.InsertAsync(instance);
        var found = await _store.FindAsync("i1");

        Assert.NotNull(found);
        Assert.Equal("leave", found.DefinitionCode);
        Assert.Equal(88, new WorkflowVariables(found.Variables).Get<int>("amount"));
        Assert.Null(await _store.FindAsync("missing"));
    }

    /// <summary>
    /// 每次查找返回新对象
    /// </summary>
    [Fact]
    public async Task 每次查找返回新对象()
    {
        await _store.InsertAsync(NewInstance("i1", BaseTime));

        var first = await _store.FindAsync("i1");
        var second = await _store.FindAsync("i1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// 列表按条件过滤并按创建时间降序截取
    /// </summary>
    [Fact]
    public async Task 列表按条件过滤并按创建时间降序截取()
    {
        var older = NewInstance("i1", BaseTime);
        var newer = NewInstance("i2", BaseTime.AddMinutes(1));
        var other = NewInstance("i3", BaseTime.AddMinutes(2));
        other.DefinitionCode = "expense";
        other.Status = WorkflowInstanceStatus.Completed;
        other.CorrelationId = "ORDER-9";
        await _store.InsertAsync(older);
        await _store.InsertAsync(newer);
        await _store.InsertAsync(other);

        var all = await _store.GetListAsync();
        Assert.Equal(["i3", "i2", "i1"], all.Select(item => item.Id));

        var running = await _store.GetListAsync(status: WorkflowInstanceStatus.Running);
        Assert.Equal(["i2", "i1"], running.Select(item => item.Id));

        var leave = await _store.GetListAsync(definitionCode: "leave", maxResultCount: 1);
        Assert.Equal("i2", Assert.Single(leave).Id);

        var correlated = await _store.GetListAsync(correlationId: "ORDER-9");
        Assert.Equal("i3", Assert.Single(correlated).Id);

        Assert.Empty(await _store.GetListAsync(maxResultCount: 0));
    }

    /// <summary>
    /// 子实例按创建时间升序
    /// </summary>
    [Fact]
    public async Task 子实例按创建时间升序()
    {
        var later = NewInstance("c2", BaseTime.AddSeconds(2));
        later.ParentInstanceId = "p1";
        var earlier = NewInstance("c1", BaseTime.AddSeconds(1));
        earlier.ParentInstanceId = "p1";
        var unrelated = NewInstance("c3", BaseTime);
        unrelated.ParentInstanceId = "p2";
        await _store.InsertAsync(later);
        await _store.InsertAsync(earlier);
        await _store.InsertAsync(unrelated);

        var children = await _store.GetChildrenAsync("p1");

        Assert.Equal(["c1", "c2"], children.Select(item => item.Id));
    }

    /// <summary>
    /// 更新能把可空字段写回空值
    /// </summary>
    [Fact]
    public async Task 更新能把可空字段写回空值()
    {
        var instance = NewInstance("i1", BaseTime);
        instance.Status = WorkflowInstanceStatus.Faulted;
        instance.EndTime = BaseTime.AddMinutes(1);
        instance.FaultMessage = "故障";
        instance.FaultNodeId = "n1";
        instance.FaultNodeInstanceId = "ni1";
        await _store.InsertAsync(instance);

        instance.Status = WorkflowInstanceStatus.Running;
        instance.EndTime = null;
        instance.FaultMessage = null;
        instance.FaultNodeId = null;
        instance.FaultNodeInstanceId = null;
        instance.JoinStates["join"] = new WorkflowJoinState { ArrivedTransitionIds = ["t1"] };
        await _store.UpdateAsync(instance);

        var found = await _store.FindAsync("i1");
        Assert.NotNull(found);
        Assert.Equal(WorkflowInstanceStatus.Running, found.Status);
        Assert.Null(found.EndTime);
        Assert.Null(found.FaultMessage);
        Assert.Null(found.FaultNodeId);
        Assert.Null(found.FaultNodeInstanceId);
        Assert.Contains("t1", found.JoinStates["join"].ArrivedTransitionIds);
    }

    /// <summary>
    /// 更新不存在的实例不新建行
    /// </summary>
    [Fact]
    public async Task 更新不存在的实例不新建行()
    {
        await _store.UpdateAsync(NewInstance("ghost", BaseTime));

        using var probe = _database.CreateProbeClient();
        Assert.Equal(0, probe.Queryable<SysWorkflowInstance>().Count());
    }

    /// <summary>
    /// 删除实例级联删除其节点实例
    /// </summary>
    [Fact]
    public async Task 删除实例级联删除其节点实例()
    {
        await _store.InsertAsync(NewInstance("i1", BaseTime));
        await _store.InsertAsync(NewInstance("i2", BaseTime));
        await _store.InsertNodeInstanceAsync(NewNodeInstance("101", "i1", BaseTime));
        await _store.InsertNodeInstanceAsync(NewNodeInstance("102", "i1", BaseTime));
        await _store.InsertNodeInstanceAsync(NewNodeInstance("201", "i2", BaseTime));

        await _store.DeleteAsync("i1");

        Assert.Null(await _store.FindAsync("i1"));
        Assert.Empty(await _store.GetNodeInstancesAsync("i1"));
        Assert.NotNull(await _store.FindAsync("i2"));
        Assert.Equal("201", Assert.Single(await _store.GetNodeInstancesAsync("i2")).Id);
    }

    /// <summary>
    /// 节点实例插入后可查到并可更新私有状态
    /// </summary>
    [Fact]
    public async Task 节点实例插入后可查到并可更新私有状态()
    {
        var nodeInstance = NewNodeInstance("101", "i1", BaseTime);
        await _store.InsertNodeInstanceAsync(nodeInstance);

        nodeInstance.Status = WorkflowNodeInstanceStatus.Suspended;
        nodeInstance.State["assignees"] = new List<string> { "u1", "u2" };
        await _store.UpdateNodeInstanceAsync(nodeInstance);

        var found = await _store.FindNodeInstanceAsync("101");
        Assert.NotNull(found);
        Assert.Equal(WorkflowNodeInstanceStatus.Suspended, found.Status);
        Assert.Equal(["u1", "u2"], WorkflowValueConverter.ConvertTo<List<string>>(found.State["assignees"]));
        Assert.Null(await _store.FindNodeInstanceAsync("missing"));
    }

    /// <summary>
    /// 执行历史同秒按创建顺序而非字符串标识排序
    /// </summary>
    [Fact]
    public async Task 执行历史同秒按创建顺序而非字符串标识排序()
    {
        await _store.InsertNodeInstanceAsync(NewNodeInstance("10", "i1", BaseTime));
        await _store.InsertNodeInstanceAsync(NewNodeInstance("9", "i1", BaseTime));
        await _store.InsertNodeInstanceAsync(NewNodeInstance("1", "i1", BaseTime.AddSeconds(1)));
        await _store.InsertNodeInstanceAsync(NewNodeInstance("5", "i2", BaseTime));

        var history = await _store.GetNodeInstancesAsync("i1");

        Assert.Equal(["9", "10", "1"], history.Select(item => item.Id));
    }

    /// <summary>
    /// 释放测试夹具
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private static WorkflowInstance NewInstance(string id, DateTime creationTime)
    {
        return new WorkflowInstance
        {
            Id = id,
            DefinitionId = "d1",
            DefinitionCode = "leave",
            DefinitionVersion = 1,
            Name = "请假",
            Status = WorkflowInstanceStatus.Running,
            CreationTime = creationTime,
            StartTime = creationTime
        };
    }

    private static WorkflowNodeInstance NewNodeInstance(string id, string instanceId, DateTime startTime)
    {
        return new WorkflowNodeInstance
        {
            Id = id,
            InstanceId = instanceId,
            NodeId = "n" + id,
            Name = "节点" + id,
            ActivityType = "SetVariable",
            Status = WorkflowNodeInstanceStatus.Completed,
            TryCount = 1,
            StartTime = startTime
        };
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarWorkflowInstanceStore` 不存在。

- [ ] **Step 3: 创建实例存储**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowInstanceStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.Abstractions.Stores;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Mapping;

namespace XiHan.Framework.Workflow.SqlSugar.Stores;

/// <summary>
/// SqlSugar 流程实例存储（实例与节点实例执行历史）
/// </summary>
public class SqlSugarWorkflowInstanceStore : IWorkflowInstanceStore
{
    private readonly WorkflowSqlSugarExecutor _executor;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="executor">工作流存储的数据库执行器</param>
    public SqlSugarWorkflowInstanceStore(WorkflowSqlSugarExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// 按标识查找实例
    /// </summary>
    /// <param name="id">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>实例（不存在返回 null）</returns>
    public async Task<WorkflowInstance?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowInstance>()
                .Where(item => item.BasicId == id)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return entities.Count == 0 ? null : WorkflowInstanceMapper.ToInstance(entities[0]);
    }

    /// <summary>
    /// 查询实例列表
    /// </summary>
    /// <param name="status">状态（为空表示不过滤）</param>
    /// <param name="definitionCode">定义编码（为空表示不过滤）</param>
    /// <param name="correlationId">业务相关性标识（为空表示不过滤）</param>
    /// <param name="maxResultCount">最大返回条数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>实例列表（按创建时间降序）</returns>
    public async Task<List<WorkflowInstance>> GetListAsync(
        WorkflowInstanceStatus? status = null,
        string? definitionCode = null,
        string? correlationId = null,
        int maxResultCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maxResultCount <= 0)
        {
            return [];
        }

        var statusValue = (int)(status ?? default);

        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowInstance>()
                .WhereIF(status is not null, item => item.Status == statusValue)
                .WhereIF(definitionCode is not null, item => item.DefinitionCode == definitionCode)
                .WhereIF(correlationId is not null, item => item.CorrelationId == correlationId)
                .OrderBy(item => item.CreationTime, OrderByType.Desc)
                .OrderBy(item => item.BasicId, OrderByType.Desc)
                .Take(maxResultCount)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowInstanceMapper.ToInstance)];
    }

    /// <summary>
    /// 获取实例的直接子实例列表
    /// </summary>
    /// <param name="parentInstanceId">父实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>子实例列表（按创建时间升序）</returns>
    public async Task<List<WorkflowInstance>> GetChildrenAsync(string parentInstanceId, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowInstance>()
                .Where(item => item.ParentInstanceId == parentInstanceId)
                .OrderBy(item => item.CreationTime)
                .OrderBy(item => item.BasicId)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowInstanceMapper.ToInstance)];
    }

    /// <summary>
    /// 插入实例
    /// </summary>
    /// <param name="instance">实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task InsertAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowInstanceMapper.ToEntity(instance);

        await _executor.ExecuteAsync(
            client => client.Insertable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 更新实例，标识不存在时不做任何事
    /// </summary>
    /// <param name="instance">实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task UpdateAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowInstanceMapper.ToEntity(instance);

        await _executor.ExecuteAsync(
            client => client.Updateable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 在同一事务内删除实例及其全部节点实例
    /// </summary>
    /// <param name="id">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _executor.ExecuteAsync(
            async client =>
            {
                await client.Deleteable<SysWorkflowNodeInstance>()
                    .Where(item => item.InstanceId == id)
                    .ExecuteCommandAsync(cancellationToken);

                return await client.Deleteable<SysWorkflowInstance>()
                    .Where(item => item.BasicId == id)
                    .ExecuteCommandAsync(cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// 按标识查找节点实例
    /// </summary>
    /// <param name="id">节点实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点实例（不存在返回 null）</returns>
    public async Task<WorkflowNodeInstance?> FindNodeInstanceAsync(string id, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowNodeInstance>()
                .Where(item => item.BasicId == id)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return entities.Count == 0 ? null : WorkflowNodeInstanceMapper.ToNodeInstance(entities[0]);
    }

    /// <summary>
    /// 获取实例的节点实例列表（执行历史）
    /// </summary>
    /// <param name="instanceId">实例标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点实例列表（按开始时间升序，同刻按创建顺序）</returns>
    public async Task<List<WorkflowNodeInstance>> GetNodeInstancesAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowNodeInstance>()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.StartTime)
                .OrderBy(item => item.Sequence)
                .OrderBy(item => item.BasicId)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowNodeInstanceMapper.ToNodeInstance)];
    }

    /// <summary>
    /// 插入节点实例
    /// </summary>
    /// <param name="nodeInstance">节点实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task InsertNodeInstanceAsync(WorkflowNodeInstance nodeInstance, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowNodeInstanceMapper.ToEntity(nodeInstance);

        await _executor.ExecuteAsync(
            client => client.Insertable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 更新节点实例，标识不存在时不做任何事
    /// </summary>
    /// <param name="nodeInstance">节点实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task UpdateNodeInstanceAsync(WorkflowNodeInstance nodeInstance, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowNodeInstanceMapper.ToEntity(nodeInstance);

        await _executor.ExecuteAsync(
            client => client.Updateable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

**若 `执行历史同秒按创建顺序而非字符串标识排序` 读到 `["10", "9", "1"]`**，说明二级排序用的是 `BasicId` 而不是 `Sequence`（硬约束 ②）。**若 `更新能把可空字段写回空值` 失败**，说明更新忽略了空值列（硬约束 ③）。改实现，不改断言。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增 SqlSugar 流程实例存储"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 4: 注册替换

**Files:**
- Modify: `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/ServiceRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarWorkflowInstanceStore`；第 1 份 `ServiceRegistrationTests.CreateServices(bool)`
- Produces: `services.Replace(ServiceDescriptor.Scoped<IWorkflowInstanceStore, SqlSugarWorkflowInstanceStore>())`

**参考来源（动手前先读）：** `framework/src/XiHan.Framework.Workflow/Extensions/DependencyInjection/XiHanWorkflowServiceCollectionExtensions.cs:48-50`。

**本任务禁止事项：** 不用 `TryAdd`；不改生命周期为 Singleton。

- [ ] **Step 1: 写失败的测试**

在 `ServiceRegistrationTests` 的 `CreateServices` 方法之前追加：

```csharp
    /// <summary>
    /// 实例存储被替换为作用域实现
    /// </summary>
    /// <param name="registerSqlSugarFirst">是否先注册本包</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 实例存储被替换为作用域实现(bool registerSqlSugarFirst)
    {
        var services = CreateServices(registerSqlSugarFirst);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IWorkflowInstanceStore));
        Assert.Equal(typeof(SqlSugarWorkflowInstanceStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：`实例存储被替换为作用域实现` 两个数据行都 FAIL，`ImplementationType` 为 `DefaultWorkflowInstanceStore`。

- [ ] **Step 3: 追加注册**

在 `XiHanWorkflowSqlSugarServiceCollectionExtensions.AddXiHanWorkflowSqlSugar` 中，把

```csharp
        services.Replace(ServiceDescriptor.Scoped<IWorkflowDefinitionStore, SqlSugarWorkflowDefinitionStore>());
```

改为

```csharp
        services.Replace(ServiceDescriptor.Scoped<IWorkflowDefinitionStore, SqlSugarWorkflowDefinitionStore>());
        services.Replace(ServiceDescriptor.Scoped<IWorkflowInstanceStore, SqlSugarWorkflowInstanceStore>());
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
git commit -m "feat(workflow-sqlsugar): 以 SqlSugar 实现替换流程实例存储"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 5: MySQL 隔离可见性用例

**Files:**
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowIsolationMySqlTests.cs`

**Interfaces:**
- Consumes: 第 1 份 `WorkflowTestDatabase.CreateMySql(string)`、`CreateProbeClient()`、`Resolver`、`UnitOfWorkManager`；Task 3 的存储
- Produces: 无（测试）

**参考来源（动手前先读）：**
- `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`（环境变量与跳过写法）
- `framework/test/XiHan.Framework.Data.Tests/RequiresNewIsolationTests.cs` 的 remarks（为什么这个形态只能在 MySQL/PostgreSQL 上验证）
- spec §6 第二层

**本任务禁止事项：** 不在断言里比较时间。不清空整张表——真实库可能有别的数据，只删本用例写入的行。不为了让用例变绿而放宽断言。

- [ ] **Step 1: 写测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowIsolationMySqlTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Uow.Options;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 存储写入与外层事务隔离的真实数据库测试
/// </summary>
/// <remarks>
/// 地址取环境变量 <c>XIHAN_TEST_MYSQL</c>，未设置时跳过。
/// </remarks>
public class WorkflowIsolationMySqlTests
{
    private const string SkipReason = "未设置 XIHAN_TEST_MYSQL，跳过真实数据库测试。";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("XIHAN_TEST_MYSQL");

    /// <summary>
    /// 外层事务未提交时实例更新已对其他连接可见，外层回滚后更新仍在
    /// </summary>
    [Fact]
    public async Task 外层事务未提交时实例更新已对其他连接可见()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        using var database = WorkflowTestDatabase.CreateMySql(ConnectionString!);
        var store = new SqlSugarWorkflowInstanceStore(database.Executor);
        var instanceId = Guid.NewGuid().ToString("N");
        var outerDefinitionId = Guid.NewGuid().ToString("N");
        var instance = new WorkflowInstance
        {
            Id = instanceId,
            DefinitionId = "d1",
            DefinitionCode = "isolation",
            DefinitionVersion = 1,
            Name = "隔离",
            Status = WorkflowInstanceStatus.Running,
            CreationTime = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc)
        };
        await store.InsertAsync(instance);

        try
        {
            using (database.UnitOfWorkManager.Begin(new XiHanUnitOfWorkOptions(isTransactional: true)))
            {
                // 外层事务在同一个库上真正开启并持有一条未提交的写入
                await database.Resolver.GetClient(WorkflowTestDatabase.ConfigId)
                    .Insertable(new SysWorkflowDefinition(outerDefinitionId)
                    {
                        Code = "isolation-" + outerDefinitionId,
                        Name = "外层",
                        Version = 1
                    })
                    .ExecuteCommandAsync();

                instance.Status = WorkflowInstanceStatus.Suspended;
                await store.UpdateAsync(instance);

                using var probe = database.CreateProbeClient();
                var seen = await probe.Queryable<SysWorkflowInstance>()
                    .Where(item => item.BasicId == instanceId)
                    .ToListAsync();
                Assert.Equal((int)WorkflowInstanceStatus.Suspended, Assert.Single(seen).Status);

                // 外层不 Complete，随 Dispose 回滚
            }

            using var after = database.CreateProbeClient();
            var outerRows = await after.Queryable<SysWorkflowDefinition>()
                .Where(item => item.BasicId == outerDefinitionId)
                .ToListAsync();
            Assert.Empty(outerRows);

            var persisted = await after.Queryable<SysWorkflowInstance>()
                .Where(item => item.BasicId == instanceId)
                .ToListAsync();
            Assert.Equal((int)WorkflowInstanceStatus.Suspended, Assert.Single(persisted).Status);
        }
        finally
        {
            await store.DeleteAsync(instanceId);
        }
    }
}
```

- [ ] **Step 2: 无环境变量时确认跳过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：该用例显示为 skipped，其余全部 PASS。

- [ ] **Step 3: 本机真库运行**

准备一个可用的 MySQL 实例（库需已存在），设置环境变量后运行：

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：`外层事务未提交时实例更新已对其他连接可见` PASS。夹具的 `InitTables` 同时验证三张表与全部索引能在 MySQL 上建出（索引键长度未超限）。

**若出现锁等待超时**（`Lock wait timeout exceeded`），说明存储的写入走进了外层那条连接又被探针以外的东西阻塞——检查执行器是否仍是 `requiresNew: true`，不要调大超时。

- [ ] **Step 4: 拿掉关键条件，确认测试变红**

临时把 `Stores/WorkflowSqlSugarExecutor.cs` 的 `requiresNew: true` 改成 `requiresNew: false`，重跑 Step 3 的命令。

预期：本用例 **FAIL**，失败点是外层 `using` 块内的第一个 `Assert.Equal`（探针读到 `Running`，即 `1`）；第 1 份的 `写入不随外层工作单元回滚` 同时 FAIL。

确认后改回 `requiresNew: true`，重跑确认全绿，`git diff` 确认执行器无残留改动。

- [ ] **Step 5: 提交**

```bash
git add framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "test(workflow-sqlsugar): 新增外层事务未提交时写入可见的真实数据库用例"
```

---

### Task 6: 本份验收

**Files:** 无新增

- [ ] **Step 1: 全量构建与测试**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：**0 Warning(s) 0 Error(s)**；全部测试通过（真库用例在无环境变量时跳过；已知抖动除外）。

- [ ] **Step 2: 注释复查**

通读本计划新建的每个 `.cs` 文件。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事。发现即移出到提交信息。

---

## 完成标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `IWorkflowInstanceStore` 只有一条描述符，实现为 `SqlSugarWorkflowInstanceStore`，Scoped；两种注册顺序结果相同
- 同秒节点实例按 `"9"`、`"10"` 的顺序返回
- `UpdateAsync` 能把 `EndTime`、`FaultMessage`、`FaultNodeId`、`FaultNodeInstanceId` 写回 `null`；不新建行
- `DeleteAsync` 级联删除节点实例，不影响其他实例
- 每次查找返回新对象
- 本机 MySQL 用例通过；改成 `requiresNew: false` 后变红

## 已知边界（写入 PR 描述，不写进代码注释）

- **取消/终止非原子**：删书签、取消节点实例、更新实例是三次独立提交
- **变量必须可 JSON 序列化**：否则批次中途抛出，实例停在运行中且书签已消费
- **变量类型会归一化**：整数与小数读回为 `decimal`，嵌套对象为 `JsonElement`
- **删除实例不删书签**：删除前应先调 `IWorkflowBookmarkStore.DeleteByInstanceAsync`
- **无自动清理**：已完成实例与执行历史永久保留
- **MySQL 时间精度**：`datetime` 无小数秒，读回时间可能与写入相差不到一秒；执行顺序由 `Sequence` 保证
- **执行历史不分页**
- **字符串比较随排序规则**：实例列表的编码与相关性过滤在 MySQL 默认排序规则下不区分大小写

## 下一份计划

第 3 份（`.superpowers/plans/2026-09-28-workflow-sqlsugar-3-bookmark-docs.md`）：书签实体与 `SqlSugarWorkflowBookmarkStore` 的 10 个方法、引擎端到端测试、包 README、文档站页面与侧边栏、模块清单。
