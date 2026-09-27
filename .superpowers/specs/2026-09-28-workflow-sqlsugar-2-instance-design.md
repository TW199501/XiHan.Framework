# Workflow.SqlSugar 第 2 份：流程实例与节点实例存储 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-workflow-sqlsugar-2-instance.md`
- **前置**：第 1 份（`.superpowers/specs/2026-09-28-workflow-sqlsugar-1-skeleton-definition-design.md`）必须已完成。本份依赖其 `WorkflowSqlSugarExecutor`、`WorkflowJsonColumn`、测试夹具 `WorkflowTestDatabase` 与 `TestEntityTypes`
- **所属 PR**：**`Workflow.SqlSugar` 一个 PR**，与第 1、3 份同一个
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-14
- **系列**：`Workflow.SqlSugar` 共 3 份。第 1 份骨架 + 定义，**第 2 份实例与节点实例**，第 3 份书签 + 端到端 + 文档站收尾

> 本文档**自成一体**。第 1 份里与本份相关的共用约定在此重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。
>
> **本份最容易静默出错的地方有两处**：
>
> 1. **实例存储是引擎锁协议的主角**。所有读写必须经第 1 份的执行器（独立事务、返回前提交）。绕过执行器直接取客户端，单元测试全绿，多实例下同一实例被两个节点先后推进。本份新增的 MySQL 用例是这一条唯一的真实数据库证据。
> 2. **`GetNodeInstancesAsync` 的顺序决定补偿顺序**。引擎取消实例时按这个顺序逆序补偿（`WorkflowEngine.CompensateAsync`）。MySQL 的 `datetime` 默认不保存小数秒，同一秒开始的节点只能靠 `Sequence` 列排序；少了它，补偿顺序在 MySQL 上是随机的，SQLite 上却完全正确。

---

## 1. 背景与目标

### 1.1 现状

第 1 份结束时，`IWorkflowDefinitionStore` 已是 SqlSugar 实现；`IWorkflowInstanceStore` 仍是 `DefaultWorkflowInstanceStore`（`framework/src/XiHan.Framework.Workflow/Stores/DefaultWorkflowInstanceStore.cs`），两个 `ConcurrentDictionary`，上限 10 万实例、50 万节点实例。

契约 `framework/src/XiHan.Framework.Workflow.Abstractions/Stores/IWorkflowInstanceStore.cs` 共 **10 个方法**：实例 6 个（`FindAsync`、`GetListAsync`、`GetChildrenAsync`、`InsertAsync`、`UpdateAsync`、`DeleteAsync`），节点实例 4 个（`FindNodeInstanceAsync`、`GetNodeInstancesAsync`、`InsertNodeInstanceAsync`、`UpdateNodeInstanceAsync`）。

### 1.2 实例生命周期与存储调用（逐行核对 `WorkflowEngine.cs`）

| 阶段 | 引擎位置 | 实例存储调用 | 书签存储调用 |
| --- | --- | --- | --- |
| 创建 | `StartAsync:145` | `InsertAsync(instance)`（**锁外**，随后才取锁） | |
| 执行节点 | `ExecuteNodeAsync:584/605` | 新节点 `InsertNodeInstanceAsync`；恢复/重试 `UpdateNodeInstanceAsync` | 重试时 `GetByNodeInstanceAsync` + `DeleteAsync` |
| 节点完成 | `HandleCompletedAsync:713` | `UpdateNodeInstanceAsync` | 清兄弟书签 |
| 挂起 | `HandleSuspendedAsync:736` | `UpdateNodeInstanceAsync` | 每个请求 `InsertAsync`，外加可选超时书签 |
| 节点故障 | `HandleFaultedAsync:797/823/834` | `UpdateNodeInstanceAsync` | 重试书签 `InsertAsync` |
| 批次收尾 | `FinalizeBurstAsync:1030-1066` | `UpdateAsync(instance)`：变量、汇聚状态、状态、结束时间 | `GetByInstanceAsync`（为空才判完成） |
| 被触发恢复 | `ResumeBookmarkCoreAsync:381-398` | 锁内 `FindAsync(instanceId)` → `FindNodeInstanceAsync` | 锁内 `FindAsync(bookmarkId)`，消费即 `DeleteAsync` |
| 挂起/恢复/重试 | `SuspendAsync`/`ResumeAsync`/`RetryAsync` | `FindAsync` + `UpdateAsync`；重试 `FindNodeInstanceAsync` | |
| 取消/终止 | `FinishForciblyAsync` + `CleanupNonFinalWorkAsync:1239-1248` | `GetNodeInstancesAsync` + 逐个 `UpdateNodeInstanceAsync` + `UpdateAsync` | **`DeleteByInstanceAsync`** |
| 补偿 | `CompensateAsync:1254-1291` | `GetNodeInstancesAsync`（**依赖顺序**）+ `UpdateNodeInstanceAsync` | |
| 子流程级联 | `ScheduleChildrenCascade:1215` | `GetChildrenAsync(parentId)` | |
| 人工任务 | `WorkflowUserTaskService` | `FindAsync`、`FindNodeInstanceAsync`、`UpdateNodeInstanceAsync` | |

**没有任何框架代码调用 `IWorkflowInstanceStore.DeleteAsync` 与 `GetListAsync`**——它们是给应用的管理接口。

### 1.3 实例状态的序列化形状

引擎没有「执行栈」或「当前活动」字段。实例的运行位置由两样东西共同表达：状态为 `Running`/`Suspended` 的节点实例，以及挂在它们上面的书签。实例本身要持久化的可变状态只有：

| 对象 | 字段 | 类型 | 大小特征 |
| --- | --- | --- | --- |
| `WorkflowInstance` | `Variables` | `Dictionary<string, object?>` | 业务决定，无上限；每次节点完成把输出合并进来 |
| `WorkflowInstance` | `JoinStates` | `Dictionary<string, WorkflowJoinState>`（`HashSet<string>` + `bool`） | 只在并行分支未汇合时非空，通常很小 |
| `WorkflowNodeInstance` | `Inputs` / `Outputs` / `State` | 各一个 `Dictionary<string, object?>` | 会签进度、遍历游标、子实例标识列表、审批轨迹 |

每次节点执行产生一条节点实例；循环、遍历、重试都会让节点实例数随执行次数增长（重试复用同一条，`TryCount` 递增）。

`Workflow.Abstractions/Runtime/WorkflowValueConverter.cs` 的类注释写明「实例变量经 JSON 持久化往返后会变成 `JsonElement`」，引擎与内置活动全程经它归一化——**框架为 JSON 持久化做过准备**，本份按 JSON 列存储即可。

### 1.4 本份交付

1. `sys_workflow_instance`、`sys_workflow_node_instance` 两个实体与映射
2. `SqlSugarWorkflowInstanceStore` 的 10 个方法，以 `Replace` 顶替默认实例存储
3. MySQL 上的隔离可见性用例（CI 自动跳过）

### 1.5 成功标准

1. `IWorkflowInstanceStore` 注册为 `SqlSugarWorkflowInstanceStore`、Scoped、只有一条描述符
2. 10 个方法的过滤、排序与 `DefaultWorkflowInstanceStore` 一致（§4.4）
3. 同一时刻开始的节点实例按创建先后返回：标识 `"9"` 排在 `"10"` 之前
4. `UpdateAsync` 能把 `EndTime`、`FaultMessage` 等可空字段写回 `null`
5. `DeleteAsync` 在一个事务里删除实例及其全部节点实例，不影响其他实例
6. 本机 MySQL：外层事务型工作单元**未提交**时，另一条连接已能读到实例更新；外层回滚后更新仍在
7. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约、默认实现与引擎**

```
framework/src/XiHan.Framework.Workflow.Abstractions/
  Stores/IWorkflowInstanceStore.cs            契约；注释「引擎对同一实例的读写已由实例级分布式锁串行化」
  Runtime/WorkflowInstance.cs                 20 个字段
  Runtime/WorkflowNodeInstance.cs             14 个字段
  Runtime/WorkflowJoinState.cs
  Runtime/WorkflowInstanceStatus.cs           Running=1 … Terminated=6（从 1 开始）
  Runtime/WorkflowNodeInstanceStatus.cs       Running=1 … Compensated=6
  Runtime/WorkflowValueConverter.cs
framework/src/XiHan.Framework.Workflow/
  Stores/DefaultWorkflowInstanceStore.cs      语义基准；GetNodeInstancesAsync 的 long.TryParse 二级排序
  Engine/WorkflowEngine.cs                    §1.2 表格里的每一行
  Engine/WorkflowInstanceLocker.cs
  UserTasks/WorkflowUserTaskService.cs
```

**② 第 1 份的产出**

```
framework/src/XiHan.Framework.Workflow.SqlSugar/
  Stores/WorkflowSqlSugarExecutor.cs          唯一的数据库入口
  Mapping/WorkflowJsonColumn.cs               JsonSerializerOptions.Web
  Entities/SysWorkflowDefinition.cs           实体写法范本
  Stores/SqlSugarWorkflowDefinitionStore.cs   存储写法范本
framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/
  WorkflowTestDatabase.cs                     CreateSqlite / CreateMySql / CreateProbeClient
  TestEntityTypes.cs                          本份加两个类型
```

**③ SqlSugar 源码**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs                     Where / WhereIF / OrderBy（可链式追加）/ Take
  Interface/IDeleteable.cs                    Where + ExecuteCommandAsync
  Entities/Mapping/SugarMappingAttribute.cs:340   SugarIndex AllowMultiple = true
  Realization/MySql/DbBind/MySqlDbBind.cs     DateTime → datetime（无小数秒）
```

> 权威源码是 `Src/Asp.NetCore2/`，不是 `Src/Asp.Net/`；更新提供者目录是 `Abstract/UpdateProvider/`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：`事务用法.md`、`UnitOfWork工作單元.md`、`CodeFirst.md`、`更新數據.md`、`刪除數據.md`。仓库文档：`docs/packages/workflow.md` 的「并发模型」与「书签消费后必收尾」。

### 2.3 禁止事项：EF Core 惯用法

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

### 2.4 本份特有的禁止事项

- **所有读写经 `WorkflowSqlSugarExecutor`**。不用 `GetClientForEntity<T>()` / `GetCurrentClient()`。
- **不缓存实体、不返回共享引用**。每次 `Find*` / `Get*` 都新建契约对象。默认内存实现返回的是字典里的同一个对象，引擎某处「改了对象忘了调 `Update`」在内存实现下会被掩盖；数据库实现必须把它暴露出来（第 3 份的端到端测试依赖这一点）。
- **不做乐观并发**，`Row_Version` 不参与。
- **`UpdateAsync` / `UpdateNodeInstanceAsync` 是纯更新**：全部列、按主键、0 行不补插、不忽略空值列。
- **`DeleteAsync` 不删书签**。契约写的是「级联删除节点实例」，书签属于另一个存储。
- **实体不实现 `IMultiTenantEntity`、不声明名为 `TenantId` 的属性**（第 1 份 §5 ②）。
- **不改 `XiHan.Framework.Workflow` 与 `Workflow.Abstractions`**。

## 3. 非目标

- **不提供已完成实例的自动清理**。框架无调用方；清理策略（保留天数、归档）由应用决定，调 `DeleteAsync`。
- **不分表**。
- **不做执行历史分页**。`GetNodeInstancesAsync` 按契约返回实例的全部节点实例。
- **不让 `DeleteAsync` 与书签删除同事务**（见 §4.5）。

## 4. 设计

### 4.1 实例实体 `sys_workflow_instance`

```csharp
[SugarTable("sys_workflow_instance")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("idx_{table}_status", nameof(Status), OrderByType.Asc, nameof(CreationTime), OrderByType.Desc)]
[SugarIndex("idx_{table}_definition_code", nameof(DefinitionCode), OrderByType.Asc, nameof(CreationTime), OrderByType.Desc)]
[SugarIndex("idx_{table}_correlation", nameof(CorrelationId), OrderByType.Asc)]
[SugarIndex("idx_{table}_parent", nameof(ParentInstanceId), OrderByType.Asc, nameof(CreationTime), OrderByType.Asc)]
public class SysWorkflowInstance : SugarEntity<string>
```

| 列 | 属性 | 类型 |
| --- | --- | --- |
| `Basic_Id` | `BasicId` | `string` 主键 |
| `Definition_Id` | `DefinitionId` | `string(255)` 非空 |
| `Definition_Code` | `DefinitionCode` | `string(128)` 非空 |
| `Definition_Version` | `DefinitionVersion` | `int` |
| `Name` | `Name` | `string(256)` 非空 |
| `Status` | `Status` | `int`（枚举从 1 开始） |
| `Variables_Json` | `VariablesJson` | 大文本 非空 |
| `Join_States_Json` | `JoinStatesJson` | 大文本 非空 |
| `Correlation_Id` | `CorrelationId` | `string(255)` 可空 |
| `Starter_Id` | `StarterId` | `string(255)` 可空 |
| `Parent_Instance_Id` | `ParentInstanceId` | `string(255)` 可空 |
| `Parent_Node_Instance_Id` | `ParentNodeInstanceId` | `string(255)` 可空 |
| `Depth` | `Depth` | `int` |
| `Tenant_Id` | `OwnerTenantId` | `long?` |
| `Creation_Time` | `CreationTime` | `DateTime` |
| `Start_Time` | `StartTime` | `DateTime?` |
| `End_Time` | `EndTime` | `DateTime?` |
| `Fault_Message` | `FaultMessage` | 大文本 可空 |
| `Fault_Node_Id` | `FaultNodeId` | `string(255)` 可空 |
| `Fault_Node_Instance_Id` | `FaultNodeInstanceId` | `string(255)` 可空 |
| `Cancellation_Reason` | `CancellationReason` | 大文本 可空 |

所有标识引用列长度 255，与主键 `Basic_Id` 的 SqlSugar 默认长度一致——调用方可传任意 `InstanceId`，子实例的 `Parent_Instance_Id` 必须装得下父实例的标识。

### 4.2 节点实例实体 `sys_workflow_node_instance`

```csharp
[SugarTable("sys_workflow_node_instance")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("idx_{table}_instance", nameof(InstanceId), OrderByType.Asc, nameof(StartTime), OrderByType.Asc, nameof(Sequence), OrderByType.Asc)]
public class SysWorkflowNodeInstance : SugarEntity<string>
```

| 列 | 属性 | 类型 |
| --- | --- | --- |
| `Basic_Id` | `BasicId` | `string` 主键 |
| `Instance_Id` | `InstanceId` | `string(255)` 非空 |
| `Node_Id` | `NodeId` | `string(255)` 非空 |
| `Name` | `Name` | `string(256)` 非空 |
| `Activity_Type` | `ActivityType` | `string(128)` 非空 |
| `Status` | `Status` | `int` |
| `Try_Count` | `TryCount` | `int` |
| `Start_Time` | `StartTime` | `DateTime` |
| `End_Time` | `EndTime` | `DateTime?` |
| `Inputs_Json` / `Outputs_Json` / `State_Json` | … | 大文本 非空 |
| `Fault_Message` | `FaultMessage` | 大文本 可空 |
| `Compensated_Time` | `CompensatedTime` | `DateTime?` |
| `Tenant_Id` | `OwnerTenantId` | `long?` |
| `Sequence` | `Sequence` | `long` 非空 |

**`Sequence`**：映射时由 `long.TryParse(Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0` 计算，与 `DefaultWorkflowInstanceStore.GetNodeInstancesAsync` 的二级排序键完全相同。引擎用雪花标识，雪花值随时间单调递增，因此 `Sequence` 即创建先后。`Basic_Id` 是字符串，按字典序 `"10" < "9"`，不能替代它。

### 4.3 映射

`WorkflowInstanceMapper`（`ToEntity` / `ToInstance`）、`WorkflowNodeInstanceMapper`（`ToEntity` / `ToNodeInstance` / `ParseSequence`），均为 `public static`。

- 四个字典与 `JoinStates` 经第 1 份的 `WorkflowJsonColumn`（`JsonSerializerOptions.Web`）
- 枚举与 `int` 互转
- 字符串原样保留：`""` 不转 `null`，`null` 不转 `""`（`CorrelationId` 的空串与 `null` 在书签匹配里语义不同）

### 4.4 查询形状与索引

| 方法 | SQL 形状 | 索引 |
| --- | --- | --- |
| `FindAsync(id)` | `WHERE Basic_Id = @id` | 主键 |
| `GetListAsync(status?, code?, correlation?, max)` | 各参数非 `null` 才加 `=` 条件；`ORDER BY Creation_Time DESC, Basic_Id DESC LIMIT @max`；`max <= 0` 直接返回空 | 按过滤条件分别命中 `idx_status` / `idx_definition_code` / `idx_correlation` |
| `GetChildrenAsync(parentId)` | `WHERE Parent_Instance_Id = @p ORDER BY Creation_Time, Basic_Id` | `idx_parent` |
| `InsertAsync` | `INSERT` | |
| `UpdateAsync` | `UPDATE ... WHERE Basic_Id = @id`（全部列） | 主键 |
| `DeleteAsync(id)` | 同一事务：`DELETE sys_workflow_node_instance WHERE Instance_Id = @id`；`DELETE sys_workflow_instance WHERE Basic_Id = @id` | `idx_instance`、主键 |
| `FindNodeInstanceAsync(id)` | `WHERE Basic_Id = @id` | 主键 |
| `GetNodeInstancesAsync(instanceId)` | `WHERE Instance_Id = @i ORDER BY Start_Time, Sequence, Basic_Id` | `idx_instance`（覆盖排序） |
| `InsertNodeInstanceAsync` / `UpdateNodeInstanceAsync` | 同实例 | |

`GetListAsync` 的 `correlationId` 为 `""` 时按「等于空串」过滤，与默认实现一致（默认实现只在 `null` 时不过滤）。

`DeleteAsync` 的两条语句在执行器的同一个工作单元里，天然同事务。

### 4.5 书签与实例的一致性

逐一核对引擎的终态路径：

| 终态 | 书签的处理 | 是否需要存储配合 |
| --- | --- | --- |
| **完成** | 引擎只在 `GetByInstanceAsync` 返回空时才判完成（`FinalizeBurstAsync:1032`），完成时本就没有书签 | 否 |
| **故障** | 书签**有意保留**：重试书签等待到期，定时书签到期回退（`TryConsumeBookmarkAsync:473-478`） | 否 |
| **取消 / 终止** | `CleanupNonFinalWorkAsync` 先 `DeleteByInstanceAsync`，再逐个取消节点实例，最后 `UpdateAsync(instance)` | 这三步是三次独立的存储调用 |

取消/终止的三步各自提交，**不在一个事务里**。但这只是一个更普遍问题的特例，见 §4.6「崩溃窗口」。

`IWorkflowInstanceStore.DeleteAsync` 不删书签。被删实例遗留的书签：定时类会在到期时被 Worker 取到，`ResumeBookmarkCoreAsync:389-394` 发现实例不存在后删除该书签（孤儿自清理）；信号类在下次信号投递时同样自清理；**人工任务类不会自清理**——`WorkflowUserTaskService.BuildTasksAsync` 只是跳过它们。应用删除实例前应先调 `IWorkflowBookmarkStore.DeleteByInstanceAsync`（写入 README）。

### 4.6 崩溃窗口：推进过程不是原子的

引擎的每一次推进都是「一串各自提交的存储调用」，中间没有事务把它们包起来：

| 入口 | 提交顺序 | 在中途崩溃或重新部署的后果 |
| --- | --- | --- |
| 恢复书签（定时、信号、人工办理、子流程回调） | `TryConsumeBookmarkAsync:518` 先删书签并提交 → `RunBurstAsync` 逐节点插入/更新节点实例、插入新书签 → `FinalizeBurstAsync` 更新实例 | 书签已删，但新书签尚未插入、实例状态尚未写回：实例停在 `Running`，**没有任何书签**，再也不会被推进 |
| 启动 | `StartAsync:145` 先插入实例并提交（锁外）→ 取锁 → `RunBurstAsync` | 实例已在 `Running`，起始节点可能尚未执行、也没有书签：同样永远不会被推进 |
| 取消/终止 | `DeleteByInstanceAsync` → 逐个取消节点实例 → `UpdateAsync(instance)` | 书签已删、实例仍是 `Running` |
| 重试 | `UpdateAsync(instance)` 置回 `Running` → `RunBurstAsync` | 实例 `Running`、故障节点尚未重跑、没有重试书签 |

这是**持久化带来的新问题**：内存实现在进程崩溃时什么都不留下（实例连同书签一起消失）；数据库实现会留下这些「运行中但无人推进」的实例，且不报错。

本包不修复它：要让一次推进原子化，引擎必须把整个批次包进一个工作单元并让存储加入——与本包「每次操作独立提交」（第 1 份 D3，引擎锁协议所需）正面冲突，且是引擎改动。可行的运维对策写进 README：定期查询 `Status = Running` 且在 `sys_workflow_bookmark` 中没有任何书签的实例，人工判断后取消或终止（写入已知边界）。

### 4.7 数据保留

- 实例、节点实例永久保留，直到应用调 `DeleteAsync`
- 大字段：`Variables_Json` 与节点实例的三个 JSON 列都是 `CodeFirst_BigString`（MySQL 上为 `longtext`，SQL Server 上为 `nvarchar(max)`）。单行大小的实际上限是 MySQL 的 `max_allowed_packet`（8.0 默认 64MB）

### 4.8 注册

在第 1 份的注册扩展里追加：

```csharp
services.Replace(ServiceDescriptor.Scoped<IWorkflowInstanceStore, SqlSugarWorkflowInstanceStore>());
```

## 五个共同问题

| 问题 | 答案 | 依据 |
| --- | --- | --- |
| **1. 分表与否** | **不分表** | 实例可挂起数周跨月，`FindAsync(id)` 不能扫所有分表；节点实例按实例标识取，须与实例同表空间 |
| **2. 主键类型** | **`string`** | 契约 `Id` 是字符串，`WorkflowStartRequest.InstanceId` 可由调用方指定任意字符串 |
| **3. 是否参与工作单元事务** | **不参与，主动隔离**（第 1 份执行器） | 锁释放时写入必须已提交；读必须看到最新提交且走主库 |
| **4. 是否需要多库** | **单库**，与定义同一个连接 | 契约「查询不做租户过滤」；子实例、父实例可能分属不同租户上下文执行 |
| **5. 顶替方式** | **`Replace`** | 主包 `TryAddSingleton<IWorkflowInstanceStore, DefaultWorkflowInstanceStore>` |

## 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改会影响什么 |
| --- | --- | --- | --- | --- |
| D1 | 并发控制 | 实例存储**不做**乐观并发，依赖引擎实例锁 + 最后写入；同一书签的重复恢复由第 3 份的书签删除守卫拦在批次开始之前 | 契约注释明文；`WorkflowInstance` 无版本字段；乐观并发冲突会在批次中途抛出，违反「批次必收尾」（`WorkflowEngine` 的 `ExecutionSession` 注释） | 改为乐观并发需改契约与引擎 |
| D2 | 执行历史顺序 | 新增 `Sequence` 列，`ORDER BY Start_Time, Sequence, Basic_Id` | MySQL `datetime` 无小数秒，同秒节点必须有稳定的二级键；补偿依赖该顺序 | 去掉 `Sequence`：MySQL 上同秒节点补偿顺序任意 |
| D3 | `DeleteAsync` 是否连带删书签 | **否** | 契约只说级联节点实例；跨存储删除会让实例存储依赖书签表 | 改为连带删除：两张表同事务更一致，但偏离契约，且与书签存储的职责重叠 |
| D4 | 推进过程的原子性（恢复、启动、取消、重试，§4.6） | **不处理**，写入已知边界并给出运维查询 | 需要引擎改动，且与独立提交冲突 | 若要处理：引擎把 `CleanupNonFinalWorkAsync` 与收尾更新包进工作单元，存储改为加入该工作单元——与 D3（第 1 份）冲突，需整体重新设计 |
| D5 | 已完成实例的保留 | **永久保留**，无自动清理 | 框架无调用方；保留策略是业务决定 | 若要自动清理：新增后台作业与保留期选项，建议另开议题 |
| D6 | 实例列表的二级排序 | `Basic_Id DESC` | 默认实现同刻顺序不确定；数据库需要确定顺序才能分页稳定 | 无 |
| D7 | 标识引用列长度 | 255 | 与主键默认长度一致 | 缩短会让超长的自定义 `InstanceId` 在子实例上插入失败 |
| D8 | MySQL 真实库用例的形态 | 「外层未提交时其他连接可见」+「外层回滚后仍在」合在一个用例 | 这是锁协议需要的可见性，SQLite 单写者无法构造 | 无 |

## 5. 会静默失效的陷阱

**① 绕过执行器，或只让写走执行器。**

见第 1 份 §5 ①。本份的实例存储是锁协议的主角：`ResumeBookmarkCoreAsync` 在锁内 `FindAsync(instanceId)`，读到的若是外层事务的旧快照或滞后的从库，整批推进都基于旧状态。SQLite 上构造不出这种情形，**只有 Task 6 的 MySQL 用例能发现**，且要求做「改成 `requiresNew: false` 后变红」的验证。

**② 执行历史只按 `Start_Time` 排序，或按字符串 `Basic_Id` 做二级排序。**

SQLite 把 `DateTime` 存成带小数秒的文本，同一秒内的节点也能排对，单元测试全绿。MySQL 的 `datetime` 默认精度为 0，写入时**四舍五入**到秒，同秒节点的相对顺序由优化器决定。`CompensateAsync` 取已完成节点逆序补偿，顺序错了就是「先撤销付款，再撤销下单」这类业务错误，不报错。按字符串标识排序同样错：`"10" < "9"`。第 1 层用例用两个 `StartTime` 相同、标识为 `"10"` 与 `"9"` 的节点实例钉住这一条。

**③ `UpdateAsync` 忽略空值列。**

`RetryAsync` 把 `EndTime`、`FaultMessage`、`FaultNodeId`、`FaultNodeInstanceId` 置空后调 `UpdateAsync`（`WorkflowEngine.cs:348-353`）。忽略空值列时这些字段保留旧值：实例显示为「运行中，但有结束时间与故障信息」，重试入口字段还指向旧的故障节点。

**④ 实现者为「性能」加了实体缓存或返回共享引用。**

引擎在内存实现下能工作，部分是因为它改的就是字典里的那个对象。若存储缓存实例、让 `FindAsync` 返回上一次的同一对象，引擎里任何漏掉 `Update` 的路径都会重新被掩盖，第 3 份的端到端测试也会跟着失效。

**⑤ 变量值不可 JSON 序列化。**

内存实现从不序列化，任何对象都能放进 `Variables`。本包序列化失败会在批次中途的 `UpdateNodeInstanceAsync` 或 `UpdateAsync` 抛出；引擎捕获后走 `FaultInstanceAsync`，其中的 `UpdateAsync(instance)` 带着同一个不可序列化的变量再抛一次，异常逃出批次，**书签已消费但实例仍是运行中**。这不是本包能修的（变量内容由业务决定），写入已知边界与 README。

## 6. 测试策略

**第一层 —— SQLite，CI 强门禁**

沿用第 1 份的 `WorkflowTestDatabase.CreateSqlite()`。必测：

- **实体**：两个表名；节点实例索引字段顺序为 `InstanceId, StartTime, Sequence`；实例四个索引都存在
- **映射往返**：变量含字符串、整数、小数、布尔、日期、嵌套字典、列表，读回后经 `WorkflowValueConverter` 取值相等；`JoinStates` 的 `HashSet` 与 `Fired` 往返；`CorrelationId` 的 `""` 与 `null` 各自保留；`Sequence` 对 `"123"` 为 123、对 `"abc"` 为 0
- **实例存储**：`FindAsync`、`GetListAsync` 三个过滤与降序与上限（含 `max <= 0`）、`GetChildrenAsync` 升序、`UpdateAsync` 清空可空字段、`UpdateAsync` 不新建行、`DeleteAsync` 级联且不影响其他实例
- **节点实例**：`FindNodeInstanceAsync`、`UpdateNodeInstanceAsync` 写回 `State`、`GetNodeInstancesAsync` 的时间序与同秒 `"9"` 先于 `"10"`
- **注册**：替换生效、Scoped、单条

**第二层 —— 真实 MySQL，本机执行，CI 自动跳过**

读 `XIHAN_TEST_MYSQL`，`Assert.SkipWhen(...)`。一个用例：

1. 存储插入一个实例
2. 开外层事务型工作单元，外层在**同一个库**上插入一条定义（让外层真正持有事务）
3. 存储把实例状态更新为 `Suspended`
4. 在外层**尚未结束**时，用一条全新连接读该实例：必须是 `Suspended`
5. 外层不提交、释放（回滚）
6. 新连接再读：外层插入的定义不存在，实例仍是 `Suspended`
7. `finally` 里删除本用例写入的实例

实现者必须做一次验证：把执行器的 `requiresNew: true` 改成 `false`，本用例在第 4 步变红；改回后变绿。

写用例时的约束：

- 时间一律整秒，不断言 `DateTime.Kind`
- 真实库上的表会保留，用例只删自己写入的行，标识用 `Guid.NewGuid().ToString("N")`
- MySQL 的 `datetime` 会把小数秒四舍五入，**不要**在第二层写时间相等断言

测试平台是 Microsoft.Testing.Platform：没有筛选参数；不带 `--logger trx` / `--results-directory`。SQLite 连接串带 `Pooling=False`。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 推进过程非原子（持久化带来的新问题） | 每次恢复先删书签并提交，再逐步提交批次；启动先插入实例再跑批次；取消与重试同理（§4.6）。在这些提交之间崩溃或重新部署，会留下 `Running` 且没有任何书签的实例，它永远不会再被推进，也不报错。内存实现崩溃时什么都不留下，所以以前没有这个问题。对策：定期查询「运行中且无书签」的实例并人工处理 |
| 变量必须可 JSON 序列化 | 不可序列化的变量会让批次中途抛出、实例停在运行中且书签已消费 |
| 变量类型会归一化 | 读回后整数、小数都是 `decimal`，嵌套对象是 `JsonElement`；业务代码应经 `WorkflowVariables` / `WorkflowValueConverter` 取值 |
| 删除实例不删书签 | 删除前应先调 `IWorkflowBookmarkStore.DeleteByInstanceAsync`，否则人工任务书签成为孤儿 |
| 无自动清理 | 已完成实例与执行历史永久保留，需要应用自行清理 |
| MySQL 时间精度 | `datetime` 无小数秒，读回的时间可能与写入相差不到一秒（四舍五入）；执行顺序由 `Sequence` 保证 |
| 执行历史不分页 | `GetNodeInstancesAsync` 一次返回全部；循环次数极多的实例会返回很多行 |
| 字符串比较随排序规则 | `GetListAsync` 的编码与相关性过滤、`GetChildrenAsync` 的父标识交给数据库比较；MySQL 默认不区分大小写。自定义 `InstanceId` 仅大小写不同的两个实例在 MySQL 上撞主键 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知抖动 `MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 除外）
- 本机设置 `XIHAN_TEST_MYSQL` 后 MySQL 用例通过；改成 `requiresNew: false` 后变红
- 每个 `.cs` 文件带两行版权声明（`XHFH001`）
- 注释与 XML 文档注释为简体中文，且只说明代码做什么；论证与叙事进提交信息
- file-scoped namespace；表达式体方法/构造函数关闭
- 提交信息中文 Conventional Commits，作用域 `workflow-sqlsugar`，**不加任何 AI 署名**

## 9. 下一份

第 3 份（`.superpowers/specs/2026-09-28-workflow-sqlsugar-3-bookmark-docs-design.md`）：`sys_workflow_bookmark`、`SqlSugarWorkflowBookmarkStore` 的 10 个方法与书签匹配的查询形状、引擎端到端测试（三个存储齐备后首次跑真实引擎），以及包 README、文档站页面、侧边栏、模块清单等收尾。
