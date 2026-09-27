# Workflow.SqlSugar 第 3 份：书签存储、引擎端到端测试与文档站收尾 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-workflow-sqlsugar-3-bookmark-docs.md`
- **前置**：第 1 份（`.superpowers/specs/2026-09-28-workflow-sqlsugar-1-skeleton-definition-design.md`）与第 2 份（`.superpowers/specs/2026-09-28-workflow-sqlsugar-2-instance-design.md`）必须已完成
- **所属 PR**：**`Workflow.SqlSugar` 一个 PR**，本份完成后方可提交
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-14
- **系列**：`Workflow.SqlSugar` 共 3 份，**本份为最后一份**，负责收尾

> 本文档**自成一体**。第 1、2 份里与本份相关的共用约定在此重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。
>
> **本份最容易静默出错的地方是书签匹配的字符串比较**。MySQL 默认排序规则 `utf8mb4_0900_ai_ci` 不区分大小写，`WHERE Bookmark_Key = 'alice'` 会命中受理人 `Alice` 的待办——`GetPendingAsync("alice")` 把别人的审批任务列给当前用户，信号 `order-paid` 恢复了等待 `Order-Paid` 的实例。SQLite 的 `=` 区分大小写，**单元测试全绿**。本份在数据库查询之后再按序数比较过滤一遍，并用 MySQL 用例钉住。

---

## 1. 背景与目标

### 1.1 现状

第 2 份结束时，定义与实例两个存储已是 SqlSugar 实现；`IWorkflowBookmarkStore` 仍是 `DefaultWorkflowBookmarkStore`（`framework/src/XiHan.Framework.Workflow/Stores/DefaultWorkflowBookmarkStore.cs`），上限 50 万条的 `ConcurrentDictionary`。

契约 `framework/src/XiHan.Framework.Workflow.Abstractions/Stores/IWorkflowBookmarkStore.cs` 共 **10 个方法**。其类注释有两条决定本份设计的约定：

- 「定时器 Worker 已通过分布式锁保证集群单活，实现无需在查询层做原子领取」
- 「查询不做租户过滤，租户隔离由引擎与任务服务在查询结果上按环境租户执行」

### 1.2 「匹配」到底按什么条件——逐个调用方核对

书签种类与 `Key` 的语义（`Workflow.Abstractions/WorkflowBookmarkKinds.cs`）：

| 种类 | `Key` | `DueTime` | `CorrelationId` | 谁来恢复 |
| --- | --- | --- | --- | --- |
| `UserTask` | 受理人标识 | 无 | 实例的相关性 | 人工办理 |
| `Timer` | 无 | 到期时间 | | 定时器 Worker |
| `Signal` | 信号名称 | 无 | 等待的业务单号 | `PublishSignalAsync` |
| `SubWorkflow` | 父节点实例标识 | 无 | | 子流程终态回调 |
| `Retry` | 无 | 下次重试时间 | | 定时器 Worker |
| `NodeTimeout` | 无 | 超时时间 | | 定时器 Worker |

每个查询方法的调用方与实际匹配条件：

| 方法 | 调用方 | 匹配条件 | 频率 |
| --- | --- | --- | --- |
| `GetDueAsync(now, max)` | `WorkflowTimerWorker.PollOnceAsync:120`，每个轮询周期一次 | `DueTime IS NOT NULL AND DueTime <= now`，按 `DueTime` 升序，取 `max` 条 | **最高**：无论有无到期书签都在轮询 |
| `GetBySignalAsync(name, corr)` | `WorkflowEngine.PublishSignalAsync:204` | `Kind = Signal AND Key = name`；`corr` 非空时追加 `CorrelationId IS NULL OR CorrelationId = corr` | 每次业务发信号 |
| `GetByKindAndKeyAsync(kind, key)` | `WorkflowUserTaskService.GetPendingAsync:93`（待办列表）；`WorkflowEngine.NotifyParentOnceAsync:444`（子流程回调） | `Kind = kind AND Key = key`，按创建时间升序 | 待办列表是用户高频操作 |
| `GetByInstanceAsync(id)` | `FinalizeBurstAsync:1030`（**每个执行批次收尾**）；`GetPendingByInstanceAsync` | `InstanceId = id` | 每个批次一次 |
| `GetByNodeInstanceAsync(id)` | `DeleteNodeBookmarksAsync:1386`（节点完成、续行、重试） | `NodeInstanceId = id` | 每个离开挂起态的节点一次 |
| `FindAsync(id)` | 恢复入口与锁内二次校验、人工任务的每个操作 | 主键 | |

**没有按负载哈希匹配的查询**。`Payload` 只在取出后读取（任务标题、表单数据、抄送人）。

### 1.3 本份交付

1. `sys_workflow_bookmark` 实体与映射、`SqlSugarWorkflowBookmarkStore` 的 10 个方法，以 `Replace` 顶替默认书签存储
2. **引擎端到端测试**：三个存储齐备后，用真实 `WorkflowEngine` 跑延时、信号、会签、并行汇聚、取消、挂起回退六个流程
3. MySQL 上的书签匹配大小写用例
4. 收尾：包 README（七段）、`docs/packages/workflow-sqlsugar.md`、文档站侧边栏、`docs/packages/index.md`、`framework/README.md` 与 `framework/README_cn.md` 的模块清单，以及 4 个 README 与文档站各页的**模块计数加一**（D11）

### 1.4 成功标准

1. `IWorkflowBookmarkStore` 注册为 `SqlSugarWorkflowBookmarkStore`、Scoped、单条；至此三个存储全部替换
2. 10 个方法的过滤与排序与 `DefaultWorkflowBookmarkStore` 一致（§4.3）
3. 信号匹配：`corr` 为 `null` 时广播；为 `"A"` 时命中 `null` 与 `"A"`、不命中 `"B"`；为 `""` 时命中 `null` 与 `""`、不命中 `"A"`
4. 书签匹配区分大小写，在 MySQL 默认排序规则下也成立
5. 引擎端到端六个流程在 SQLite 上全部通过
6. 文档站 `pnpm build` 不因新页面失败（若本机装有 pnpm）；六处登记齐全
7. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约、默认实现与全部调用方**

```
framework/src/XiHan.Framework.Workflow.Abstractions/
  Stores/IWorkflowBookmarkStore.cs            契约与两条类注释
  Runtime/WorkflowBookmark.cs                 11 个字段
  WorkflowBookmarkKinds.cs                    六种书签与 Key 的语义
framework/src/XiHan.Framework.Workflow/
  Stores/DefaultWorkflowBookmarkStore.cs      语义基准；GetBySignalAsync 的三段条件
  Workers/WorkflowTimerWorker.cs              GetDueAsync 的唯一调用方；分布式锁单活
  Engine/WorkflowEngine.cs                    §1.2 表格里的行号
  UserTasks/WorkflowUserTaskService.cs        待办列表、转办（UpdateAsync 改 Key）、加签（InsertAsync）
```

**② 第 1、2 份的产出**

```
framework/src/XiHan.Framework.Workflow.SqlSugar/
  Stores/WorkflowSqlSugarExecutor.cs、Mapping/WorkflowJsonColumn.cs
  Stores/SqlSugarWorkflowInstanceStore.cs     存储写法范本
framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/
  WorkflowTestDatabase.cs、TestEntityTypes.cs、ServiceRegistrationTests.cs、WorkflowIsolationMySqlTests.cs
```

**③ 引擎测试的现成搭法**

```
framework/test/XiHan.Framework.Workflow.Tests/
  WorkflowTestHost.cs                         TestClock / TestCurrentTenant / InProcessTestLock 照抄
  DelaySignalTests.cs、UserTaskTests.cs、ParallelJoinTests.cs   流程定义写法
```

**④ 文档与登记的现有条目**

```
framework/src/XiHan.Framework.EventBus.SqlSugar/README.md     七段结构范本
docs/packages/eventbus-sqlsugar.md                            文档站页面范本
docs/.vitepress/config.ts:201-202                            工作流两条侧边栏
docs/packages/index.md:116-117                               模块索引
framework/README.md:93-94、framework/README_cn.md:93-94     模块清单
```

**⑤ SqlSugar 源码**：`E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（不是 `Src/Asp.Net/`）；`Interface/IQueryable.cs`、`Interface/IDeleteable.cs`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：`查詢函數.md`（`WhereIF`、空值比较）、`CodeFirst.md`。仓库文档：`docs/packages/workflow.md`（引擎并发模型、定时器 Worker 配置）。

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

- **所有读写经 `WorkflowSqlSugarExecutor`**。
- **`GetDueAsync` 不做原子领取**：不加状态列、不条件 `UPDATE`、不 `FOR UPDATE SKIP LOCKED`。契约明文说 Worker 已单活，领取也没有对应的「释放」契约方法。
- **书签的删除不做「删到 0 行则抛异常」**。`DeleteAsync` 被 `DeleteNodeBookmarksAsync` 在批次中途调用，抛出会让引擎把实例判为故障。
- **`UpdateAsync` 不做 upsert**：已被消费（删除）的书签在锁失效窗口里被「更新」时必须保持删除状态。
- **不改 `docs/packages/workflow.md`；根目录 `README.md` / `README_cn.md` 只改模块计数，不在「常用包」表格加行**（见 D8、D9）。
- **不改 `XiHan.Framework.Workflow` 与 `Workflow.Abstractions`**。

## 3. 非目标

- **不做负载内容的查询**（没有调用方）。
- **不做租户过滤**（契约交给引擎与任务服务）。
- **不做书签的原子领取与租约**（Worker 单活，见 §4.5）。
- **不做取消/终止三步的同事务**（见第 2 份 §4.5）。

## 4. 设计

### 4.1 书签实体 `sys_workflow_bookmark`

```csharp
[SugarTable("sys_workflow_bookmark")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("idx_{table}_instance", nameof(InstanceId), OrderByType.Asc, nameof(CreationTime), OrderByType.Asc)]
[SugarIndex("idx_{table}_node_instance", nameof(NodeInstanceId), OrderByType.Asc)]
[SugarIndex("idx_{table}_due", nameof(DueTime), OrderByType.Asc)]
[SugarIndex("idx_{table}_kind_key", nameof(Kind), OrderByType.Asc, nameof(BookmarkKey), OrderByType.Asc, nameof(CreationTime), OrderByType.Asc)]
public class SysWorkflowBookmark : SugarEntity<string>
```

| 列 | 属性 | 类型 |
| --- | --- | --- |
| `Basic_Id` | `BasicId` | `string` 主键 |
| `Instance_Id` | `InstanceId` | `string(255)` 非空 |
| `Node_Id` | `NodeId` | `string(255)` 非空 |
| `Node_Instance_Id` | `NodeInstanceId` | `string(255)` 非空 |
| `Kind` | `Kind` | `string(64)` 非空 |
| `Bookmark_Key` | `BookmarkKey` | `string(256)` 可空 |
| `Payload_Json` | `PayloadJson` | 大文本 非空 |
| `Due_Time` | `DueTime` | `DateTime?` |
| `Correlation_Id` | `CorrelationId` | `string(255)` 可空 |
| `Creation_Time` | `CreationTime` | `DateTime` |
| `Tenant_Id` | `OwnerTenantId` | `long?` |

列名用 `Bookmark_Key` 而不是 `Key`：`KEY` 是 MySQL 保留字。SqlSugar 会给标识符加引号，所以用 `Key` 也能跑，但手写 SQL 排查时容易踩。

MySQL `utf8mb4` 下 `idx_kind_key` 的键长：`64×4 + 256×4 + 5 = 1285` 字节，低于 InnoDB 的 3072 上限。第二层用例的建表会实际验证。

### 4.2 映射

`WorkflowBookmarkMapper`（`public static`）：`ToEntity(WorkflowBookmark)` / `ToBookmark(SysWorkflowBookmark)`。`Payload` 经 `WorkflowJsonColumn`；`Key` ↔ `BookmarkKey`；字符串原样保留，`""` 与 `null` 不互转。

### 4.3 查询形状与索引

| 方法 | SQL 形状 | 索引 | 序数后过滤 |
| --- | --- | --- | --- |
| `FindAsync(id)` | `WHERE Basic_Id = @id` | 主键 | |
| `GetByInstanceAsync(id)` | `WHERE Instance_Id = @id ORDER BY Creation_Time, Basic_Id` | `idx_instance` | |
| `GetByNodeInstanceAsync(id)` | `WHERE Node_Instance_Id = @id ORDER BY Creation_Time, Basic_Id` | `idx_node_instance` | |
| `GetDueAsync(now, max)` | `WHERE Due_Time IS NOT NULL AND Due_Time <= @now ORDER BY Due_Time, Basic_Id LIMIT @max`；`max <= 0` 直接返回空 | `idx_due`（范围扫描，天然跳过 `NULL`） | |
| `GetByKindAndKeyAsync(kind, key)` | `WHERE Kind = @kind AND Bookmark_Key = @key ORDER BY Creation_Time, Basic_Id` | `idx_kind_key`（等值前缀 + 排序） | `Kind`、`Key` |
| `GetBySignalAsync(name, corr)` | `WHERE Kind = 'Signal' AND Bookmark_Key = @name [AND (Correlation_Id IS NULL OR Correlation_Id = @corr)] ORDER BY Creation_Time, Basic_Id` | `idx_kind_key` | `Kind`、`Key`、`CorrelationId` |
| `InsertAsync` | `INSERT` | | |
| `UpdateAsync` | `UPDATE ... WHERE Basic_Id = @id`（全部列） | 主键 | |
| `DeleteAsync(id)` | `DELETE WHERE Basic_Id = @id` | 主键 | |
| `DeleteByInstanceAsync(id)` | `DELETE WHERE Instance_Id = @id` | `idx_instance` | |

`GetBySignalAsync` 的相关性条件只在 `corr` **不为 `null`** 时追加（`WhereIF(correlationId is not null, ...)`）。`""` 不是「广播」，是「定向匹配空串或未限定相关性的书签」——与默认实现 `correlationId is null || ...` 完全一致。表达式 `item.CorrelationId == null` 由 SqlSugar 翻译为 `IS NULL`。

**序数后过滤**：`GetByKindAndKeyAsync` 与 `GetBySignalAsync` 在数据库查询之后，对结果再按 `StringComparison.Ordinal` 比较一次全部字符串条件。数据库条件负责走索引缩小范围，序数过滤负责语义。这两个方法的结果集天然很小（一个受理人的待办、一个信号的等待者），后过滤没有性能代价。

`GetByInstanceAsync` 与 `GetByNodeInstanceAsync` 按标识查询，标识由引擎以雪花数生成，不存在大小写问题，不做后过滤。

二级排序键 `Basic_Id`：MySQL `datetime` 无小数秒，同一批次插入的书签 `Creation_Time` 相同，需要确定的次序。书签的顺序只影响待办列表的展示与 `NotifyParentOnceAsync` 的 `FirstOrDefault()`（同一父节点实例同时只有一个子流程书签），不像执行历史那样影响补偿，因此不引入 `Sequence` 列。

### 4.4 注册

在注册扩展里追加：

```csharp
services.Replace(ServiceDescriptor.Scoped<IWorkflowBookmarkStore, SqlSugarWorkflowBookmarkStore>());
```

至此三个存储全部替换。

### 4.5 并发：为什么不做原子领取

| 场景 | 互斥来源 | 存储要做什么 |
| --- | --- | --- |
| 两个节点的定时器 Worker 同时轮询 | `WorkflowTimerWorker.PollOnceAsync:103` 每轮先 `TryAcquireAsync(DistributedLockName)`，拿不到就跳过本轮 | 无 |
| 同一书签被信号、Worker、人工办理同时恢复 | `ResumeBookmarkCoreAsync` 在实例锁内二次 `FindAsync(bookmarkId)`，找不到即「已被处理」 | 锁内的读必须看到已提交的删除——第 1 份的执行器保证 |
| 转办与办理并发 | `CompleteAsync` 把 `expectedBookmarkKey` 带进锁内校验，`TransferAsync` 在锁内改 `Key` | `UpdateAsync` 必须真的写回 `Key` |

三处互斥都来自分布式锁。多实例部署使用 Redis 锁时，存储只需保证「提交即可见」；使用进程内默认锁时，两节点的 Worker 会同时取到同一批到期书签并各自恢复——这是部署约束，不是存储能修的（契约没有领取/释放的方法可供实现）。

### 4.6 引擎端到端测试

测试主机 `WorkflowEngineTestHost`：`AddXiHanWorkflow` + 本包的 `AddXiHanWorkflowSqlSugar`，存储指向第 1 份夹具的临时 SQLite；时钟、租户、锁、事件发布器用测试替身（照抄 `Workflow.Tests/WorkflowTestHost.cs`）。引擎从一个服务作用域中解析（存储是 Scoped）。

六个流程各自覆盖的存储能力：

| 流程 | 覆盖 |
| --- | --- |
| 延时书签到期后恢复 | `GetDueAsync` 的到期判断、`DueTime` 往返、`DeleteAsync`、`FinalizeBurstAsync` 的 `GetByInstanceAsync`；同一时刻开始的 `start` 与 `wait` 节点按 `Sequence` 排序 |
| 信号定向后广播 | `GetBySignalAsync` 的相关性语义、信号负载进入变量 |
| 会签跨两次办理 | `GetByKindAndKeyAsync`、节点实例 `State` 的受理人列表与已同意列表经两次 JSON 往返、书签 `Payload` 的任务标题 |
| 并行分支一支挂起后汇合 | 实例 `JoinStates` 经 JSON 往返后仍能判定汇合；往返丢失时实例会被判为「汇聚网关等待的分支已死亡」而故障 |
| 取消实例 | `DeleteByInstanceAsync`、`GetNodeInstancesAsync` + `UpdateNodeInstanceAsync`、`CancellationReason` |
| 挂起实例的到期书签回退 | 书签 `UpdateAsync` 写回新的 `DueTime`；实例保持挂起 |

这组测试的价值在于：默认内存实现返回共享引用，引擎里任何「改了对象但漏调 `Update`」的路径在内存实现下都被掩盖；数据库实现每次返回新对象，漏掉的写回会直接让这些流程失败。

## 五个共同问题

| 问题 | 答案 | 依据 |
| --- | --- | --- |
| **1. 分表与否** | **不分表** | 书签按实例、节点实例、种类+键、到期时间查询，都需要在一张表里走索引；书签在恢复时即删除，表规模等于「当前等待中的点」，不随时间增长 |
| **2. 主键类型** | **`string`** | 契约 `WorkflowBookmark.Id` 是字符串；任务标识即书签标识，对外暴露 |
| **3. 是否参与工作单元事务** | **不参与，主动隔离**（第 1 份执行器） | 锁内二次校验必须读到其他节点已提交的删除 |
| **4. 是否需要多库** | **单库**，与定义、实例同一个连接 | Worker 在无租户上下文里轮询全部到期书签 |
| **5. 顶替方式** | **`Replace`** | 主包 `TryAddSingleton<IWorkflowBookmarkStore, DefaultWorkflowBookmarkStore>` |

## 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改会影响什么 |
| --- | --- | --- | --- | --- |
| D1 | 到期书签的领取方式 | **普通查询，不原子领取** | 契约明文 Worker 单活；无领取/释放的契约方法 | 若要支持无 Redis 的多实例：需给契约加领取语义并改 Worker，上游改动 |
| D2 | 书签匹配的大小写 | **区分大小写**：数据库条件 + 序数后过滤 | 与内存实现一致；不区分会把他人的待办列给当前用户 | 改为依赖排序规则：MySQL 上受理人与信号名不区分大小写 |
| D3 | 书签列名 | `Bookmark_Key` | `KEY` 是 MySQL 保留字 | 无功能影响 |
| D4 | 书签二级排序 | `Basic_Id`，不加 `Sequence` | 书签顺序不影响补偿；同一父节点实例只有一个子流程书签 | 若日后有依赖书签顺序的逻辑，需补 `Sequence` |
| D5 | `DeleteAsync` 删到 0 行 | **不报错** | 批次中途调用，抛出会把实例判为故障 | 改为抛出：并发下正常的重复清理会故障实例 |
| D6 | 索引集合 | 四个：实例、节点实例、到期、种类+键+创建时间 | 覆盖 §1.2 全部查询；相关性不单独建索引（信号查询已被种类+键收窄） | 去掉 `idx_due`：Worker 每轮全表扫描 |
| D7 | 端到端测试的位置 | 本包测试项目内，复制 `Workflow.Tests` 的四个测试替身 | 测试项目不引用别的测试项目 | 无 |
| D8 | 根目录 `README.md` / `README_cn.md` | **只把模块计数加一，不在「常用包」表格加行** | 常用包是精选清单，`EventBus.SqlSugar`、`Auditing.SqlSugar` 也未列入；逐包表格在 `framework/README*.md` | 若要列入常用包：需在 PR 描述说明 |
| D11 | 模块计数的更新方式 | **实现时读取当前值 N 再全局加一**，不写死数字与行号 | 8 个 SqlSugar 包先后合入，计数与行号都会漂移；计数分布在 4 个 README 与 `docs/**/*.md` 共约 20 处，含 shields.io 徽章 `Modules-N-1f6feb` 与「包参考 N 页」；`framework/README*.md` 里「N 个单测工程」数的是测试项目，本包新增测试项目，同样加一 | 写死 `68 → 69` 只对第一个合入的包正确 |
| D9 | `docs/packages/workflow.md` 的「示例 6：换成持久化存储」 | **不改** | 一个 PR 只做一件事 | 若要改成指向本包：属于对另一页的改动 |
| D10 | 文档站页面放在侧边栏的哪一组 | 「存储 · 模板 · 任务 · 治理」，紧跟 `Workflow 工作流` | 与 `EventBus.SqlSugar` 紧跟 `EventBus` 的做法一致 | 无 |

## 5. 会静默失效的陷阱

**① 书签匹配依赖数据库排序规则。**

见「实现前必读」。SQLite 的 `=` 区分大小写，第一层用例永远发现不了。后果：

- `GetPendingAsync("alice")` 返回 `Alice` 的待办——当前用户能在列表里看到别人的审批标题与表单数据（`CompleteAsync` 会因锁内 `Key` 序数比较失败而拒绝办理，但列表已经泄露）
- 信号 `order-paid` 恢复等待 `Order-Paid` 的实例，信号负载写进错误的实例
- `CorrelationId` 为 `order-1` 的定向信号恢复 `ORDER-1` 的实例

第二层 MySQL 用例钉住这一条，要求做「去掉序数后过滤后变红」的验证（前提：测试库使用默认的不区分大小写排序规则）。

**② 相关性为 `""` 被当成广播。**

写成 `WhereIF(!string.IsNullOrEmpty(correlationId), ...)` 看起来更「稳妥」，却把 `""` 变成了广播，恢复所有等待该信号的实例。默认实现只在 `null` 时广播。第一层有专门用例。

**③ `GetDueAsync` 被「优化」成只取最近的到期书签，或忘了 `Take`。**

漏掉 `Take(max)`：积压的到期书签（例如实例被挂起、书签每轮回退）一次全部取出，Worker 的单轮耗时随积压增长。漏掉 `DueTime IS NOT NULL`：SQLite 与 MySQL 对 `NULL <= @now` 都返回未知、行被排除，结果碰巧正确；但换成 `Due_Time <= @now OR Due_Time IS NULL` 这类改写就会把人工任务书签当成到期书签恢复——`UserTaskActivity.ResumeAsync` 发现没有办理结果会重建待办，**不报错，待办被重复创建**。

**④ `UpdateAsync` 写成 upsert。**

实例锁因续期失败而丢失时（`WorkflowInstanceLocker.cs:95`），旧持锁者可能在书签已被新持锁者消费（删除）之后，再对它 `UpdateAsync`（例如挂起实例的到期回退）。upsert 会把书签复活，它随后被再次恢复。纯更新只是更新 0 行。

**⑤ 端到端测试的主机从根容器解析引擎。**

存储是 Scoped。从根容器解析 `IWorkflowEngine` 在未开启作用域校验时能跑通，存储随根作用域成为事实上的单例，用例全绿但没有测到真实的作用域用法。主机必须 `CreateScope()` 后从作用域解析。

## 6. 测试策略

**第一层 —— SQLite，CI 强门禁**

- **实体**：表名、四个索引、种类+键索引的字段顺序
- **映射**：`Payload` 往返；`Key` ↔ `BookmarkKey`；`CorrelationId` 的 `""` 与 `null` 各自保留
- **存储**：§4.3 表格逐行；`GetDueAsync` 排除 `null` 与未到期、包含恰好到期、按到期时间升序、`max` 截断与 `max <= 0`；信号三种相关性语义；非信号种类同键不命中；`UpdateAsync` 改 `Key` 与 `DueTime`、不新建行；`DeleteByInstanceAsync` 只删该实例
- **注册**：替换生效、Scoped、单条；三个存储全部替换
- **引擎端到端**：§4.6 六个流程

**第二层 —— 真实 MySQL，本机执行，CI 自动跳过**

读 `XIHAN_TEST_MYSQL`，`Assert.SkipWhen(...)`。一个用例：插入受理人 `Alice` 的人工任务书签与信号 `Order-Paid` 的信号书签；`GetByKindAndKeyAsync(UserTask, "alice")` 与 `GetBySignalAsync("order-paid", null)` 都必须返回空；`finally` 删除本用例写入的书签。实现者须验证：去掉序数后过滤，本用例在默认排序规则的 MySQL 上变红。

第 2 份的 MySQL 用例在本份结束时再跑一遍：夹具的 `InitTables` 此时包含书签表，顺带验证四张表的全部索引能在 MySQL 上建出。

写用例时的约束：时间一律整秒；不断言 `DateTime.Kind`；真实库只删自己写入的行。测试平台限制同前两份。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 多实例必须上 Redis 锁 | 默认进程内锁下，多个节点的定时器 Worker 会同时取到同一批到期书签并各自恢复 |
| 书签表规模 | 等于当前等待中的点；被挂起实例的到期书签每轮回退、长期保留 |
| 信号名与受理人长度 | `Bookmark_Key` 256 字符；超长在 MySQL 严格模式下报错 |
| 定时精度 | MySQL `datetime` 无小数秒，到期时间写入时四舍五入到秒，定时书签最多晚不到一秒被取到 |
| 其余 | 第 1、2 份已知边界全部适用（工作流写入不随业务回滚、SQLite 外层事务锁库、变量须可序列化、删除实例不删书签、取消非原子、编码比较随排序规则、无自动清理） |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知抖动 `MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 除外）
- 本机 MySQL：本份与第 2 份的真实库用例都通过；去掉序数后过滤时本份用例变红
- 新包七处登记齐全：csproj 与四个 props、模块类与注册扩展、README 七段、slnx 两个项目、`docs/packages/workflow-sqlsugar.md` + 侧边栏 + `docs/packages/index.md`、`framework/README.md` 与 `framework/README_cn.md` 模块清单；模块计数在全部出现处加一（根 README 按 D8 只改计数）
- README 写明 `EnableDbInitialization` 与 `EnableTableInitialization` 默认 `false`
- 每个 `.cs` 带两行版权声明；注释只写做什么；file-scoped namespace；表达式体方法/构造函数关闭
- 提交信息中文 Conventional Commits，作用域 `workflow-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不改 `docs/packages/workflow.md`；根 README 只改计数

## 9. 下一份

本份是 `Workflow.SqlSugar` 的最后一份。完成后提交 PR（从 `upstream/main` 开的 worktree 另行整理，见仓库的上游协作约定），PR 描述汇总三份 spec 的「已知边界」与「待确认的决策」。
