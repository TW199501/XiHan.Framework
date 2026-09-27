# Workflow.SqlSugar 第 1 份：包骨架、执行器与流程定义存储 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-workflow-sqlsugar-1-skeleton-definition.md`
- **前置**：无（`XiHan.Framework.Data` 的 `ISqlSugarClientResolver`、`IUnitOfWorkManager.Begin(requiresNew: true)` 的隔离连接、`TableInitializationAttribute` 均已在 `dev` 上）
- **所属 PR**：**`Workflow.SqlSugar` 一个 PR**，与第 2、3 份同一个；第 3 份完成前不提交 PR
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-14
- **系列**：`Workflow.SqlSugar` 共 3 份。第 1 份骨架 + 定义，第 2 份实例与节点实例，第 3 份书签 + 引擎端到端测试 + 文档站收尾

> 本文档**自成一体**。实现第 1 份所需的全部约束都写在这里。第 2、3 份会重复其中与它们相关的共用约定——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的地方是执行器（§4.2）**：工作流存储的每一次读写都必须在**独立的**事务型工作单元里执行并立即提交，**不能**加入调用方的工作单元。这一条与发件箱（`EventBus.SqlSugar`）正好相反——发件箱必须与业务同事务。照抄发件箱的 `GetClientForEntity<T>()` 写法，单元测试全绿，但多实例部署下同一个流程实例会被两个节点先后推进。§5 ① 详述。

---

## 1. 背景与目标

### 1.1 现状

`XiHan.Framework.Workflow` 的三个存储端口只有进程内实现，注册在 `framework/src/XiHan.Framework.Workflow/Extensions/DependencyInjection/XiHanWorkflowServiceCollectionExtensions.cs:51-53`：

```csharp
services.TryAddSingleton<IWorkflowDefinitionStore, DefaultWorkflowDefinitionStore>();
services.TryAddSingleton<IWorkflowInstanceStore, DefaultWorkflowInstanceStore>();
services.TryAddSingleton<IWorkflowBookmarkStore, DefaultWorkflowBookmarkStore>();
```

三个默认实现（`framework/src/XiHan.Framework.Workflow/Stores/Default*.cs`）都是有界的 `ConcurrentDictionary`：进程重启即全部丢失，不跨实例。

契约的实际方法数（逐个数过）：

| 契约 | 文件 | 方法数 |
| --- | --- | --- |
| `IWorkflowDefinitionStore` | `Workflow.Abstractions/Stores/IWorkflowDefinitionStore.cs` | **8** |
| `IWorkflowInstanceStore` | `Workflow.Abstractions/Stores/IWorkflowInstanceStore.cs` | **10**（实例 6 + 节点实例 4） |
| `IWorkflowBookmarkStore` | `Workflow.Abstractions/Stores/IWorkflowBookmarkStore.cs` | 10 |

合计 **28** 个方法、**4** 张表（定义、实例、节点实例、书签）。

### 1.2 本份交付

1. 新包 `framework/src/XiHan.Framework.Workflow.SqlSugar/` 的骨架：csproj、模块类、选项、注册扩展、解决方案登记
2. **执行器** `WorkflowSqlSugarExecutor`：三个存储共用的「独立事务 + 固定连接」执行入口
3. `sys_workflow_definition` 实体、映射、`SqlSugarWorkflowDefinitionStore`（8 个方法），并以 `Replace` 顶替默认定义存储
4. 测试项目骨架与测试夹具（后两份沿用）

实例存储、书签存储在第 2、3 份；本份结束时这两个端口仍是进程内默认实现。

### 1.3 成功标准

1. `IWorkflowDefinitionStore` 的注册被替换为 `SqlSugarWorkflowDefinitionStore`，生命周期 Scoped，容器中只有这一条描述符；`AddXiHanWorkflow` 与 `AddXiHanWorkflowSqlSugar` 的调用先后顺序不影响结果
2. 8 个方法的过滤与排序语义与 `DefaultWorkflowDefinitionStore` 一致（§4.5 逐条列出）
3. 存储在外层事务型工作单元内执行写入后，外层回滚**不**撤销该写入（SQLite 用例，CI 强门禁）
4. `(Code, Version)` 唯一：同编码同版本第二次插入抛异常
5. `UpdateAsync` 对不存在的标识**不**新建行
6. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 工作流契约与引擎——决定存储语义的唯一依据**

```
framework/src/XiHan.Framework.Workflow.Abstractions/
  Stores/IWorkflowDefinitionStore.cs         本份实现的契约
  Definitions/WorkflowDefinition.cs          映射对象；注意 Id 是 string
  Definitions/WorkflowDefinitionStatus.cs    Draft=0 / Published=1 / Disabled=2 / Archived=3
  Definitions/WorkflowNode.cs 等             Nodes/Transitions/Variables 的形状
  Runtime/WorkflowValueConverter.cs          JsonElement 归一化，决定 JSON 列用什么选项
framework/src/XiHan.Framework.Workflow/
  Stores/DefaultWorkflowDefinitionStore.cs   语义基准：过滤、排序、返回值
  Definitions/WorkflowDefinitionManager.cs   定义存储的全部调用方：版本号、仅草稿可改/可删
  Engine/WorkflowEngine.cs:1326-1356         引擎按 Id / 编码+版本 / 最新已发布 取定义
  Engine/WorkflowInstanceLocker.cs           实例锁协议；第 95 行注释「后续冲突由存储层最后写入语义兜底」
  Extensions/DependencyInjection/XiHanWorkflowServiceCollectionExtensions.cs:51-53   TryAddSingleton
```

**② 已完成的兄弟子包——骨架形状照抄**

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  XiHan.Framework.EventBus.SqlSugar.csproj    四个 props 的 Import 顺序
  XiHanSqlSugarEventBusModule.cs              模块类只做装配
  Extensions/DependencyInjection/*.cs         Replace 顶替 + TryAddScoped
  Entities/SysEventOutbox.cs                  SugarColumn 写法、CodeFirst_BigString
  Mapping/EventOutboxMapper.cs                契约 ↔ 实体映射
framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxTestContext.cs                        SQLite 临时库夹具（注意它用全限定名调 Options.Create）
  OutboxConcurrencyTests.cs                   XIHAN_TEST_MYSQL + Assert.SkipWhen
```

**③ 数据访问层——执行器依赖的能力**

```
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs          GetClient(configId)
  Clients/SqlSugarClientResolver.cs           EnlistCurrentUnitOfWork：requiresNew 时 CopyNew 物化独立连接
  Clients/SqlSugarTransactionApi.cs           构造即 BeginTran
  Entities/SugarEntity.cs                     Basic_Id / Row_Version
  Initializers/TableInitializationAttribute.cs、DbEntityTypeProvider.cs、DbInitializationTarget.cs
  Auditing/SqlSugarDataExecutingHandler.cs    插入 AOP 按属性名写 TenantId（§5 ②）
  Extensions/EntityAuditExtensions.cs:155-190 SetTenantIdValue
  Options/XiHanSqlSugarCoreOptions.cs         DefaultConfigId、EnableDbInitialization、EnableTableInitialization
framework/src/XiHan.Framework.Uow/
  UnitOfWorkManager.cs:43-70                  Begin(requiresNew: true) 在有外层时置 RequiresIsolatedConnection
framework/test/XiHan.Framework.Data.Tests/RequiresNewIsolationTests.cs
                                              真实工作单元 + 真实解析器的测试搭法，本包夹具照抄
```

**④ SqlSugar 源码——API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs                     Where / WhereIF / OrderBy / Take / Select / ToListAsync
  Interface/Insertable.cs                     ExecuteCommandAsync(CancellationToken)
  Interface/IUpdateable.cs                    ExecuteCommandAsync(CancellationToken)
  Interface/IDeleteable.cs                    Where / ExecuteCommandAsync(CancellationToken)
  Abstract/UpdateProvider/UpdateableHelper.cs:665-710   ValidateVersion 只在显式开启时执行
  Abstract/CodeFirstProvider/CodeFirstProvider.cs:332-392 SugarIndex：字段名按属性名匹配、{table} 占位
  Entities/Mapping/SugarMappingAttribute.cs:341-400      SugarIndexAttribute 构造函数
  Abstract/AdoProvider/AdoProvider.cs:1692-1717          事务内的读走主库
```

> 树与目录名：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`，权威源码是 `Src/Asp.NetCore2/`。同级的 `Src/Asp.Net/` 是 .NET Framework 变体。更新提供者的目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 事务与工作单元 | `事务用法.md`、`UnitOfWork工作單元.md` |
| 建表与索引 | `CodeFirst.md`（`SugarIndex`） |
| 并发与线程安全 | `偶發性錯誤與執行緒安全.md` |
| 更新语义 | `更新數據.md` |

仓库自身文档：`docs/packages/workflow.md`（引擎的并发模型与「书签消费后必收尾」）、`docs/packages/data.md`、`docs/guide/uow.md`。

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

- **不用 `GetClientForEntity<T>()` / `GetCurrentClient()`**。前者会把操作登记进调用方的工作单元，后者随当前租户切库。一律经执行器 `GetClient(配置标识)`。
- **不在存储里自己 `new SqlSugarClient` 或 `CopyNew()`**。独立连接由 `IUnitOfWorkManager.Begin(requiresNew: true)` 经解析器物化，自建连接会丢掉构建期挂载的 AOP 与过滤器。
- **不做乐观并发**：不调 `IsEnableUpdateVersionValidation()`，不调 `ExecuteCommandWithOptLock*`。理由见 §4.2 与「待确认的决策」D1。
- **`UpdateAsync` 不做 upsert**：不用 `Storageable`、`InsertOrUpdate`，也不在更新 0 行时补一次插入。
- **`UpdateAsync` 不忽略空值列**：不写 `IgnoreColumns(ignoreAllNullColumns: true)`。
- **实体不继承 `SugarMultiTenant*` 基类，不实现 `IMultiTenantEntity`，不声明名为 `TenantId` 的属性**。§5 ②。
- **不改 `XiHan.Framework.Workflow` 与 `Workflow.Abstractions` 的任何文件**。契约、引擎、管理器保持原样。
- **不改 `docs/packages/workflow.md`**（一个 PR 只做一件事，见 D13）。

## 3. 非目标

- **不改引擎的并发模型**。实例互斥仍靠引擎的实例级分布式锁，存储不另加锁、不另加版本校验。
- **不提供数据保留与清理**。框架没有任何代码调用 `IWorkflowInstanceStore.DeleteAsync`；何时清理已完成实例由应用决定。
- **不分表**（见「五个共同问题」）。
- **不支持租户独立库分布**：工作流四张表固定落在一个连接上，不随租户切库。
- **不在存储层强制「已发布定义不可改」**：该规则在 `WorkflowDefinitionManager` 里，存储保持哑存储。

## 4. 设计

### 4.1 包结构

```
framework/src/XiHan.Framework.Workflow.SqlSugar/
  XiHan.Framework.Workflow.SqlSugar.csproj
  XiHanWorkflowSqlSugarModule.cs
  Options/XiHanWorkflowSqlSugarOptions.cs
  Stores/WorkflowSqlSugarExecutor.cs
  Entities/SysWorkflowDefinition.cs
  Mapping/WorkflowJsonColumn.cs                 internal
  Mapping/WorkflowDefinitionMapper.cs
  Stores/SqlSugarWorkflowDefinitionStore.cs
  Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs
```

csproj 引用 `XiHan.Framework.Workflow`（契约、`TryAdd` 的默认注册、`XiHanWorkflowModule`）与 `XiHan.Framework.Data`（SqlSugar、解析器、工作单元）。

### 4.2 执行器：独立事务 + 固定连接

```csharp
public sealed class WorkflowSqlSugarExecutor
{
    public string ConfigId { get; }
    public Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation, CancellationToken cancellationToken);
}
```

每次调用：

1. `cancellationToken.ThrowIfCancellationRequested()`
2. `using var unitOfWork = _unitOfWorkManager.Begin(new XiHanUnitOfWorkOptions(isTransactional: true), requiresNew: true);`
3. `var client = _clientResolver.GetClient(ConfigId);` —— 解析器把该连接登记进**刚开的这个**工作单元
4. 执行 `operation(client)`
5. `await unitOfWork.CompleteAsync(cancellationToken);` —— 提交
6. 异常时 `using` 释放工作单元即回滚

`ConfigId` = `XiHanWorkflowSqlSugarOptions.ConfigId`（非空白时）否则 `XiHanSqlSugarCoreOptions.DefaultConfigId`（默认 `"Default"`）。

**为什么必须独立事务——引擎的锁协议要求「释放锁之前写入已提交」。**

引擎对同一实例的一切推进都在实例级分布式锁内完成（`WorkflowInstanceLocker.AcquireAsync`），契约 `IWorkflowInstanceStore` 的注释写明「引擎对同一实例的读写已由实例级分布式锁串行化，存储实现无需再做乐观并发控制」，`WorkflowInstanceLocker.cs:95` 更写明锁丢失后「后续冲突由存储层最后写入语义兜底」。这套协议成立的前提是：**持锁者释放锁时，它的写入对下一个持锁者可见**。

若存储加入调用方的工作单元（动态 API 的写操作默认就开事务型工作单元）：

- 节点 A 在请求事务 T 内办理任务：删除书签、更新实例——都未提交；随后释放实例锁，继续跑请求的其余逻辑，最后才提交 T
- 节点 B 在 A 释放锁之后、T 提交之前拿到锁，读到的是**提交前的旧状态**（MVCC 一致性读不等待 A 的行锁），书签还在
- B 重放同一个书签；它的 `DELETE` 被 A 的行锁挡住，等 T 提交后删除 0 行、不报错，B 照常跑完批次，覆盖 A 的实例更新

两次推进、最后写入覆盖，而且全程无异常。独立事务让每次写入在返回前就已提交，锁一释放写入即可见。

**读也必须走独立事务**，原因有二：

- MySQL 默认 `REPEATABLE READ`：外层事务第一次读取时建立快照，此后在外层连接上的读都停在那个快照上，看不到其他节点刚提交的推进
- 配置了从库时，SqlSugar 只在**没有事务**时把读分流到从库（`AdoProvider.cs:1694`）。引擎「写完立刻读」（如 `FinalizeBurstAsync` 插完书签马上 `GetByInstanceAsync`）若读到滞后的从库，会把仍有书签的实例判成已完成。事务内的读固定走主库

**代价**：每次存储调用一个 `BEGIN`/`COMMIT`；有外层工作单元时每次调用另开一条物理连接（连接池内）。一个执行批次通常十几次到几十次存储调用。

**并发控制的分工：引擎锁为主，书签删除守卫兜底，实例不做乐观并发**：

- **书签消费做守卫（第 3 份实现）**：引擎消费书签的删除（`WorkflowEngine.cs:518`）在 `RunBurstAsync` 之前、以 `CancellationToken.None` 执行。书签存储的 `DeleteAsync` 删到 0 行时抛 `WorkflowException`，落败节点的批次根本不会开始、不留半写状态；定时器 Worker、信号投递已把 `WorkflowException` 当作「已被并发处理」跳过。执行器的独立提交保证先删者的删除在后删者执行前已提交，后删者确定性地看到 0 行。这让「同一书签被两个节点同时恢复」在没有 Redis 锁时也只推进一次。调用点逐一分析见第 3 份 §4.7
- **实例不做乐观并发**：实例的 POCO（`WorkflowInstance`）没有版本字段，存储拿不到「读时版本」；若在作用域里自建身份映射记录版本，版本冲突会在批次中途抛出——而引擎规定批次一旦开始必须收尾（`WorkflowEngine.cs` 的 `ExecutionSession` 注释），中途抛出正是它要避免的「状态半落盘」
- 引擎作者选择「锁 + 最后写入」，见上文两处注释；书签守卫是在这一选择之内、只作用于批次开始之前的补强

**守卫覆盖不到的场景，仍需要 Redis 锁**：两个节点**同时恢复同一实例的不同书签**（并行分支各自的等待点、会签的两个受理人同时办理、超时书签与人工办理同时到来）。两边各自删到 1 行、各自开跑批次，写回按最后写入覆盖。因此多实例部署仍应把 `IDistributedLock` 换成 Redis 实现（配置 `XiHan:Caching` 的 Redis 连接后框架自动 `Replace`，`XiHanCachingServiceCollectionExtensions.cs:94`）。默认的 `DefaultDistributedLock` 只在进程内互斥；本包在应用初始化时检测到它会记录一条警告（第 3 份 D12），警告是提示，不是唯一防线。

**为什么固定连接、不随租户切库**：

- 契约 `IWorkflowBookmarkStore` 注释：「查询不做租户过滤，租户隔离由引擎与任务服务在查询结果上按环境租户执行」——四张表是**全租户共表**的设计
- 平台级定义（`TenantId == null`）要能被任何租户的实例找到（`WorkflowEngine.StartAsync` 取 `_currentTenant.Id ?? definition.TenantId`）
- 定时器 Worker 在**无租户上下文**里调 `GetDueAsync`，再按书签的 `TenantId` 切租户恢复（`WorkflowTimerWorker.cs:120-133`）。若随租户切库，租户独立库里的定时书签永远不会被轮询到

### 4.3 选项

```csharp
public class XiHanWorkflowSqlSugarOptions
{
    public const string SectionName = "XiHan:Workflow:SqlSugar";
    public string? ConfigId { get; set; }
}
```

只有一项：工作流表所在连接的配置标识。为空时用 `XiHan:Data:SqlSugarCore:DefaultConfigId`。

### 4.4 定义实体 `sys_workflow_definition`

```csharp
[SugarTable("sys_workflow_definition")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("ux_{table}_code_version", nameof(Code), OrderByType.Asc, nameof(Version), OrderByType.Asc, true)]
public class SysWorkflowDefinition : SugarEntity<string>
```

| 列 | 属性 | 类型 | 说明 |
| --- | --- | --- | --- |
| `Basic_Id` | `BasicId` | `string`，主键，默认长度 255 | 契约 `WorkflowDefinition.Id` |
| `Row_Version` | `RowVersion` | `long` | 基类自带，本包**不使用**，恒为 0 |
| `Code` | `Code` | `string(128)`，非空 | 流程编码 |
| `Name` | `Name` | `string(256)`，非空 | |
| `Version` | `Version` | `int`，非空 | |
| `Description` | `Description` | 大文本，可空 | |
| `Category` | `Category` | `string(128)`，可空 | |
| `Status` | `Status` | `int`，非空 | 枚举按整数存 |
| `Enable_Compensation` | `EnableCompensation` | `bool`，非空 | |
| `Nodes_Json` | `NodesJson` | 大文本，非空 | `List<WorkflowNode>` |
| `Transitions_Json` | `TransitionsJson` | 大文本，非空 | `List<WorkflowTransition>` |
| `Variables_Json` | `VariablesJson` | 大文本，非空 | `List<WorkflowVariableDefinition>` |
| `Extra_Properties` | `ExtraProperties` | 大文本，非空 | `Dictionary<string, string>` |
| `Tenant_Id` | `OwnerTenantId` | `long?` | 为空表示平台级定义 |
| `Creation_Time` | `CreationTime` | `DateTime`，非空 | |
| `Publish_Time` | `PublishTime` | `DateTime?` | |

唯一索引 `(Code, Version)`。`[TableInitialization(Target = Platform)]` 让表只建在平台库（静态配置的连接），不建进租户独立库。

**图结构为什么拆成四个 JSON 列而不是一整份定义 JSON**：标量列要参与查询（编码、版本、状态），整份 JSON 会让它们存两遍、日后可能不一致；集合各自一列，映射一一对应，读写都不需要合并。

### 4.5 JSON 列约定

`Mapping/WorkflowJsonColumn`（`internal static`）：

- 序列化与反序列化一律用 **`JsonSerializerOptions.Web`**，不加任何转换器
- 反序列化空白文本返回 `new T()`

选项必须与 `WorkflowValueConverter.ConvertTo`（`Workflow.Abstractions/Runtime/WorkflowValueConverter.cs:87`）完全一致——它把 `JsonElement` 按 `JsonSerializerOptions.Web` 反序列化成目标类型。§5 ③。

`object?` 类型的值（节点属性、变量默认值）读回后是 `JsonElement`，引擎全程经 `WorkflowValueConverter` 归一化，这是框架为持久化预留的设计（见该类注释），不需要本包额外处理。

### 4.6 定义存储的查询形状

`SqlSugarWorkflowDefinitionStore`，构造注入 `WorkflowSqlSugarExecutor`。

| 方法 | SQL 形状 | 走的索引 | 与默认实现的对应 |
| --- | --- | --- | --- |
| `FindAsync(id)` | `WHERE Basic_Id = @id` | 主键 | `GetValueOrDefault` |
| `FindByVersionAsync(code, version)` | `WHERE Code = @code AND Version = @v` | `ux_code_version` | 同 |
| `FindLatestPublishedAsync(code)` | `WHERE Code = @code AND Status = 1 ORDER BY Version DESC LIMIT 1` | `ux_code_version` | 同 |
| `GetMaxVersionAsync(code)` | `SELECT Version WHERE Code = @code ORDER BY Version DESC LIMIT 1`，无行返回 0 | `ux_code_version` | 同 |
| `GetListAsync(code?, status?)` | 参数非 `null` 才加对应条件；`ORDER BY Code ASC, Version DESC` | 带编码时走 `ux_code_version` | 同 |
| `InsertAsync` | `INSERT` | | 默认实现是 upsert，本实现主键冲突抛异常 |
| `UpdateAsync` | `UPDATE ... WHERE Basic_Id = @id`，全部列 | 主键 | 默认实现是 upsert，本实现 0 行即 0 行 |
| `DeleteAsync(id)` | `DELETE WHERE Basic_Id = @id` | 主键 | 同 |

`GetMaxVersionAsync` 不用 `MaxAsync`：空集上 `MAX` 返回 `NULL`，各驱动转 `int` 的行为不一，取「降序第一行」在四种库上一致。

`GetListAsync` 的 `status` 在表达式外先算成 `int`，表达式里不出现 `status.Value`。

**定义的版本化**：`WorkflowDefinitionManager.CreateAsync` 与 `CreateNewVersionAsync` 都是「`GetMaxVersionAsync` + 1 再插入」，两个请求并发创建同一编码时会算出同一个版本号。默认实现静默产生两个同版本定义，之后 `FindByVersionAsync` 取到哪个是任意的；唯一索引让第二个插入抛异常，由调用方重试。版本号在契约里是**跨租户按编码全局递增**（`GetMaxVersionAsync(code)` 不带租户），唯一键因此是 `(Code, Version)` 而不含租户。

**已发布的定义能不能改**：存储不拦。管理器只允许改草稿（`UpdateDraftAsync`），但发布、停用、归档本身就是对已发布行的更新，存储层拦了会把这三个动作一起拦掉。

### 4.7 注册与模块

```csharp
public static IServiceCollection AddXiHanWorkflowSqlSugar(this IServiceCollection services, IConfiguration configuration)
{
    services.Configure<XiHanWorkflowSqlSugarOptions>(configuration.GetSection(XiHanWorkflowSqlSugarOptions.SectionName));
    services.TryAddScoped<WorkflowSqlSugarExecutor>();
    services.Replace(ServiceDescriptor.Scoped<IWorkflowDefinitionStore, SqlSugarWorkflowDefinitionStore>());
    return services;
}
```

第 2、3 份各追加一行 `Replace`。

- 主包用 `TryAddSingleton`，所以必须 `Replace`；`TryAdd` 是空操作
- 生命周期 **Scoped**：`ISqlSugarClientResolver` 是 Scoped（`XiHanDataServiceCollectionExtensions.cs:61`）。存储的全部消费者——`WorkflowEngine`、`WorkflowDefinitionManager`、`WorkflowUserTaskService`（Transient）与 `WorkflowTimerWorker`（每轮自建作用域）——都在作用域内解析，已逐个核对
- 模块 `XiHanWorkflowSqlSugarModule` `[DependsOn(typeof(XiHanWorkflowModule), typeof(XiHanDataModule))]`，`ConfigureServices` 只调 `services.AddXiHanWorkflowSqlSugar(services.GetConfiguration())`

## 五个共同问题

| 问题 | 本包的答案 | 依据 |
| --- | --- | --- |
| **1. 分表与否** | **不分表**，四张表都不分 | 定义是长期模板；实例生命周期可跨月（审批挂起数周很常见），按创建月分表后 `FindAsync(id)` 要扫所有分表；节点实例与书签按实例查询，必须与实例同表空间。保留期问题交给应用调 `DeleteAsync`（§3） |
| **2. 主键类型** | **`string`**（`SugarEntity<string>`） | 契约四个模型的 `Id` 全是 `string`。引擎用 `IDistributedIdGenerator<long>.NextIdString()` 生成，但 `WorkflowStartRequest.InstanceId` 允许调用方传任意字符串，不能假定可解析为 `long` |
| **3. 是否参与工作单元事务** | **不参与，且必须主动隔离**：每次操作独立事务、立即提交 | §4.2。与发件箱相反：发件箱要「业务回滚则事件消失」，工作流要「锁释放则写入可见」 |
| **4. 是否需要多库** | **单库**，固定在 `ConfigId`（默认 `DefaultConfigId`），不随租户切库，不遍历模块库 | §4.2 最后一段 |
| **5. 顶替方式** | **`Replace`** | 主包 `XiHanWorkflowServiceCollectionExtensions.cs:51-53` 是 `TryAddSingleton` |

## 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改会影响什么 |
| --- | --- | --- | --- | --- |
| D1 | 并发控制方式 | **引擎实例锁 + 书签删除守卫**：书签 `DeleteAsync` 删到 0 行抛 `WorkflowException`（第 3 份）；实例不做乐观并发、最后写入 | 消费删除发生在批次开始之前，抛出只让落败节点放弃、不留半写状态；实例 POCO 无版本字段，乐观并发在批次中途抛出违反「批次必收尾」 | 去掉守卫：没有 Redis 锁时同一书签可被两个节点各推进一次。守卫让书签 `DeleteAsync` 比内存默认实现更严格（后者删不存在的键静默成功），这是有意的差异 |
| D2 | 多实例未配 Redis 锁时的处理 | **启动时记录一条警告**：模块 `OnApplicationInitialization` 发现解析出的 `IDistributedLock` 是 `DefaultDistributedLock` 时记录 Warning（第 3 份实现，见第 3 份 D12 与 §4.8），不新增托管服务，不阻止启动 | 单实例用数据库持久化而不上 Redis 是合法部署，不能拒绝启动；有书签守卫兜底后，警告针对的是守卫覆盖不到的「不同书签并发恢复」 | 改为拒绝启动：单实例部署被迫引入 Redis |
| D3 | 事务参与 | **每次操作独立事务型工作单元**（`requiresNew: true`） | §4.2 | 改为加入调用方工作单元：启动流程可与业务同事务回滚，但多实例下出现重复推进；SQLite 下外层已写同库时独立连接会撞 `database is locked`（已知边界） |
| D4 | 表落在哪个连接 | **`DefaultConfigId`，可由 `XiHan:Workflow:SqlSugar:ConfigId` 覆盖** | §4.2 | 若要随租户切库：定时器 Worker 与平台级定义都会失效 |
| D5 | 主键 | `string`，SqlSugar 默认长度 255 | 契约标识是字符串 | 改 `long` 会让自定义 `InstanceId` 插入失败 |
| D6 | 租户列的属性名 | **`OwnerTenantId`**（列名仍是 `Tenant_Id`） | 插入 AOP 按属性名 `TenantId` 注入/校验租户（§5 ②），会改写平台级定义、在跨租户场景抛异常；存储必须原样保存契约给的值 | 改回 `TenantId`：平台级定义在租户上下文里创建时被写成该租户；租户 B 为租户 A 的任务加签时插入抛异常 |
| D7 | 定义唯一键 | `(Code, Version)` 唯一索引 | 防并发创建产生同版本；契约的版本号跨租户全局递增 | 去掉：同版本重复，`FindByVersionAsync` 结果任意 |
| D8 | `InsertAsync` / `UpdateAsync` 语义 | 纯插入（冲突抛异常）/ 纯更新（0 行不补插） | 默认实现两者都是 upsert；数据库上 upsert 会让已被消费（删除）的书签在锁失效窗口里被「更新」复活 | 改为 upsert：书签复活导致重复恢复 |
| D9 | JSON 选项 | `JsonSerializerOptions.Web`，无转换器 | 与 `WorkflowValueConverter` 一致 | 加 `JsonStringEnumConverter`：`object?` 里的枚举值存成字符串，`ConvertTo<枚举>` 读回时抛异常 |
| D10 | `Row_Version` | 不使用，恒为 0 | D1 | 无 |
| D11 | 存储生命周期 | Scoped | 解析器是 Scoped | 在根容器直接解析 `IWorkflowEngine` 的应用代码会拿到根作用域的存储（开启作用域校验时抛异常），README 写明 |
| D12 | 份数切分 | 1 骨架+定义 / 2 实例+节点实例 / 3 书签+端到端+文档 | 执行器是三份共用的地基，先与最简单的定义存储一起落地并用隔离测试钉住；实例存储是并发协议的主角，真实数据库测试放第 2 份；书签查询最密集，且端到端测试需要三个存储齐备 | 无 |
| D13 | `docs/packages/workflow.md` 的「示例 6：换成持久化存储」 | **不改** | 一个 PR 只做一件事；新包文档站页面已在第 3 份写全 | 若要加一行指向新包的链接，属于对 `workflow.md` 的改动，需在 PR 描述里单列 |

## 5. 会静默失效的陷阱

**① 执行器没有隔离，或隔离的写法被「简化」。**

以下任何一种写法，单元测试都照样全绿：

- 用 `GetClientForEntity<T>()` / `GetCurrentClient()` 代替执行器（照抄发件箱最容易这样）
- 执行器里 `Begin(..., requiresNew: false)`：没有外层时行为不变，有外层时返回 `ChildUnitOfWork`，写入加入外层事务
- 执行器里 `new XiHanUnitOfWorkOptions(isTransactional: false)`：`EnlistCurrentUnitOfWork` 对非事务型工作单元直接返回共享上下文，那条连接上可能正开着外层事务
- 只把写操作放进执行器、读操作直接取客户端：外层 `REPEATABLE READ` 快照与从库分流（§4.2）

后果都是「锁释放时写入未提交 / 读到旧状态」，只在多实例 + 请求事务里出现，且不报错。第 1 份用 SQLite 用例钉住「外层回滚不撤销写入」，第 2 份用 MySQL 用例钉住「外层未提交时其他连接已可见」；实现者须做一次「把 `requiresNew: true` 改成 `false` 后两条用例变红」的验证。

**② 实体上出现名为 `TenantId` 的属性，或继承了多租户基类。**

`SqlSugarDataExecutingHandler` 在插入时按**属性名** `TenantId` 调 `SetTenantIdValue`（`EntityAuditExtensions.cs:155`），与实体是否实现接口无关：

- 当前有租户上下文且实体值为 `null`：写成当前租户——平台级定义（`TenantId == null`）被改成租户私有
- 实体值与当前租户不同：抛 `InvalidOperationException`「禁止跨租户写入」

实现 `IMultiTenantEntity` 则还会被全局查询过滤器按当前租户过滤，定时器 Worker 与跨租户的平台定义查找随之失效。

测试夹具不挂这个 AOP（`PassThroughConnectionConfigurator`），**测试发现不了**。因此第 1 份写了反射约定测试：所有工作流实体都不实现 `IMultiTenantEntity`、都没有 `TenantId` 属性。

**③ JSON 选项与 `WorkflowValueConverter` 不一致。**

`WorkflowValueConverter.ConvertTo` 把 `JsonElement` 按 `JsonSerializerOptions.Web` 反序列化。若本包加了 `JsonStringEnumConverter`，存进 `object?` 的枚举读回是字符串 `JsonElement`，`ConvertTo<SomeEnum>` 抛 `JsonException`；若设了 `DictionaryKeyPolicy`，变量名被改成驼峰，按原名取值取不到。二者都只在特定变量上出现。

**④ `SugarIndex` 的字段写成列名。**

`CodeFirstProvider.CreateIndex` 按**属性名**匹配（`entityInfo.Columns.FirstOrDefault(z => z.PropertyName == it.Key)`），写成列名 `"Code"` 碰巧与属性名相同能过，写成 `"Enable_Compensation"` 这类就会在建表时抛「索引特性没找到列」——这一条会报错；静默的是**索引名**：不带 `{table}` 占位时，PostgreSQL 的索引名在模式内全局唯一，与其他包同名的索引会让 `IsAnyIndex` 返回真而**跳过创建**。本包索引名一律 `idx_{table}_…` / `ux_{table}_…`。

**⑤ `UpdateAsync` 忽略空值列。**

`IgnoreColumns(ignoreAllNullColumns: true)` 看起来是「只更新有值的列」的优化。定义上 `PublishTime`、`Description` 被清空时写不回去；第 2 份的实例上，重试会把 `EndTime`、`FaultMessage` 置空（`WorkflowEngine.RetryAsync`），忽略空值列后它们永远保留旧值，实例显示为「运行中但有结束时间和故障信息」。

## 6. 测试策略

**第一层 —— SQLite，CI 强门禁**

夹具 `WorkflowTestDatabase` 照 `RequiresNewIsolationTests` 搭：真实 `SqlSugarScope`、真实 `SqlSugarClientResolver`、真实 `UnitOfWorkManager`（四条注册），测试替身只替换租户、模块数据源与连接配置器。必测：

- **注册**：替换生效、Scoped、只有一条描述符；两种调用顺序结果相同
- **约定**：所有工作流实体不实现 `IMultiTenantEntity`、没有 `TenantId` 属性、`TableInitialization.Target == Platform`、主键为 `string`
- **定义实体**：表名、唯一索引字段与 `IsUnique`
- **映射往返**：标量字段逐一相等；节点属性读回后经 `WorkflowValueConverter` 取值与原值相等
- **存储语义**：§4.6 表格逐行；`UpdateAsync` 不新建行；重复主键与重复 `(Code, Version)` 插入抛异常
- **隔离**：外层事务型工作单元内插入定义，外层不提交就释放，新连接仍能读到该定义

写用例时的约束：

- 时间一律用整秒（`new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc)`），不要断言毫秒，也不要断言 `DateTime.Kind`——读回的 `Kind` 是 `Unspecified`，`DateTime.Equals` 只比较 `Ticks`
- 隔离用例里外层工作单元**不能**触碰同一个 SQLite 库：SQLite 是库级单写者，外层一旦写入，内层独立连接会撞 `database is locked`（`RequiresNewIsolationTests` 的 remarks 有同样说明）。同库形态由第 2 份的 MySQL 用例覆盖

**第二层 —— 真实数据库**：本份没有；第 2 份新增。

测试平台是 Microsoft.Testing.Platform：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。SQLite 连接串必须带 `Pooling=False`。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 多实例应上 Redis 锁 | 默认 `DefaultDistributedLock` 只在进程内互斥。书签删除守卫保证同一书签只被推进一次，但两个节点同时恢复同一实例的**不同**书签时仍会并发推进、最后写入覆盖。启动时有警告，不阻止启动 |
| 工作流写入不随业务回滚 | 业务事务里启动流程后业务回滚，流程实例仍在。需要「业务失败则不启动」时，应在业务提交之后再启动流程 |
| SQLite + 外层事务 | 外层工作单元已写过同一个 SQLite 库时，存储的独立连接会撞 `database is locked`。SQLite 只适合无外层事务的场景 |
| 并发创建同编码定义 | 唯一索引让后到者抛数据库异常，异常类型随驱动而定，调用方需重试 |
| 行为差异：插入与更新 | 默认内存实现两者都是 upsert；本包插入冲突抛异常、更新 0 行不补插 |
| 表只建在平台库 | `Target = Platform`；把 `ConfigId` 指向租户独立库的连接不受支持 |
| 每次操作一个事务 | 有外层工作单元时每次存储调用各占一条物理连接（连接池内），高并发下连接池需相应调大 |
| 字符串长度 | 编码 128、名称 256；超长在 MySQL 严格模式下报错，在 SQLite 下不截断 |
| 编码比较随排序规则 | 定义存储的编码比较交给数据库。MySQL 默认排序规则不区分大小写：`Leave` 与 `leave` 被视为同一编码，版本号互相递增，同版本插入撞唯一索引。内存实现区分大小写。编码应保持大小写一致，或把表改为区分大小写的排序规则。书签存储（第 3 份）另做序数后过滤，不受此影响 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动 `Script.Tests...MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 除外）
- 把执行器的 `requiresNew: true` 临时改成 `false`，隔离用例变红；改回后变绿
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 提交信息为中文 Conventional Commits，作用域 `workflow-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事

## 9. 下一份

第 2 份（`.superpowers/specs/2026-09-28-workflow-sqlsugar-2-instance-design.md`）：`sys_workflow_instance` 与 `sys_workflow_node_instance`、`SqlSugarWorkflowInstanceStore` 的 10 个方法、节点实例执行顺序的持久化，以及 MySQL 上的隔离可见性用例。
