# SqlSugar 持久化层设计（总纲，已拆分）

- **日期**：2026-09-21
- **状态**：**已拆分为 6 份单元 spec，实现时不要读本文档**
- **范围**：两个独立可交付的包，分 6 个交付单元

---

> ## 本文档已停用
>
> 为配合「一份 spec 对一份计划」的执行方式，本文档已按交付单元拆分。**实现时请读对应单元的 spec，不要读本文档**——它是拆分前的总纲，与单元 spec 若有出入，以单元 spec 为准。
>
> | 单元 | spec | 计划 |
> | --- | --- | --- |
> | P1 | `2026-09-21-auditing-sqlsugar-p1-entities-design.md` | `2026-09-21-auditing-sqlsugar-p1-entities.md` |
> | P2 | `2026-09-21-auditing-sqlsugar-p2-writers-design.md` | `2026-09-21-auditing-sqlsugar-p2-writers.md` |
> | P3 | 待写 | 待写 |
> | P4 | 待写 | 待写 |
> | P5 | 待写 | 待写 |
> | P6 | 待写 | 待写 |
>
> 本文档保留的价值只剩两处：第 1 节的缺口清点（13 个内存 Store 的全貌）与第 8–9 节的 `EventBus.SqlSugar` 设计——后者会在写 P3–P6 的 spec 时被吸收，吸收完本文档即可归档。

---

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 3 节。**
>
> 本设计的技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套，且**单元测试仍可能通过**，问题要到运行期才暴露。
>
> 第 3 节规定三件事，缺一不可：
>
> 1. **代码去哪找** —— 框架既有实现、SqlSugar 源码、Admin.NET 生产参考的确切路径
> 2. **文档去哪找** —— 按任务对应到具体篇目的映射表
> 3. **哪些不能做** —— EF Core 惯用法与 SqlSugar 写法的逐条对照
>
> 实现计划中的**每一个任务**都必须重复标注它对应的参考来源，不得只在总纲写一次。

---

## 1. 背景与目标

`XiHan.Framework` 自带完整的 SqlSugar 数据访问层（`XiHan.Framework.Data`，8991 行：多数据源路由、读写分离、租户连接推导、差异日志 AOP、CodeFirst 初始化、乐观锁转译），但**框架自身的状态没有任何一处落到数据库**。

清点结果，以下实现全部是进程内内存或空实现：

| 能力 | 当前实现 | 后果 |
| --- | --- | --- |
| 事件发件箱 / 收件箱 | `DefaultEventOutbox` / `DefaultEventInbox`（`ConcurrentDictionary`） | **outbox 模式失去意义**：事件与业务数据不在同一事务，进程退出即丢 |
| 审计日志 | 5 个 `Null*Writer` | 采集管线、队列、Worker 全部就绪，最后一步写进黑洞 |
| 权限 / 角色 / 用户 | `DefaultPermissionStore` 等 | 下游必须逐个重写 |
| 设置 / 租户 / 灰度 / 工作流 / 升级 | `NullSettingStore` 等 | 同上 |

本设计交付前两项的 SqlSugar 落地实现，并**建立后续 11 个 Store 可照抄的范式**。

**成功标准**：

1. 业务事务回滚时，同一事务内产生的分布式事件随之消失；提交后事件必被投递至少一次。
2. 多实例部署时，同一条 outbox 记录不会被重复投递。
3. 审计日志按月分表落库，框架启动时自动建表。

## 2. 非目标

明确不做，避免范围蔓延：

- **不做另外 11 个 Store**。本设计只交付审计与事件两项，其余照本设计建立的范式后续补齐。
- **不修改 `XiHan.Framework.EventBus.Abstractions` 的任何公开契约**。`IEventOutbox` / `IEventInbox` 保持原样；原子领取在实现内解决。接口若要调整，是独立 PR。
- **不做插件清单、签名、商店、运行期安装**。本设计只需保证不把未来的插件化堵死（见第 9 节）。
- **不修复 `docs/packages/data.md` 第 170 行的列名表述笔误**（文档写 snake_case，代码实为 Pascal_Snake_Case）。单独 PR 处理。
- **不引入跨库分布式事务**。框架明确不提供，本设计顺应该边界而非绕过它。

## 3. 参考来源与禁止事项（强制）

> **本节是实现阶段的硬性约束。** .NET 数据访问的默认肌肉记忆是 Entity Framework Core，若不锁定参考来源，实现会不自觉写成 EF 风格并全盘失效。动手前必须先读本节列出的来源。

### 3.1 代码参考（按优先级）

**① 框架既有实现 —— 最高优先，新包必须与其一致**

```
framework/src/XiHan.Framework.Data/SqlSugar/
  Repository/SqlSugarRepositoryBase.cs    CRUD 的既定写法
  Clients/SqlSugarTransactionApi.cs       事务如何接入工作单元（含连接钉住的告警）
  Clients/SqlSugarClientResolver.cs       如何取得正确的客户端
  Entities/SugarFullAuditedEntity.cs      列映射范本（Pascal_Snake_Case + ColumnDescription）
  Auditing/SqlSugarDiffLogAop.cs          AOP 写法
  Routing/ModuleDataSourceAttribute.cs    模块分库语义与跨库边界
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Abstract/
  AdoProvider/          事务、原生 SQL
  InsertableProvider/   UpdateableProvider/   QueryableProvider/
  FastestProvider/      大批量写入
  FilterProvider/       全局过滤器
```

当前引用版本：`SqlSugarCore 5.1.4.221`（见 `XiHan.Framework.Data.csproj`）。

**③ Admin.NET 生产参考**

```
/e/source/platfrom-admin/Admin.NET.Core/
  Logging/DatabaseLoggingWriter.cs   日志落库，与 PR1 目标一致
  SqlSugar/SqlSugarFilter.cs         过滤器与数据权限
  SqlSugar/SqlSugarUnitOfWork.cs     工作单元对照
```

### 3.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）。按任务对应：

| 任务 | 必读 |
| --- | --- |
| 事务参与 | `事务用法.md`、`事務鎖.md`、`UnitOfWork工作單元.md` |
| 并发抢占 | `偶發性錯誤與執行緒安全.md`、`並發控制樂觀鎖.md` |
| 日志按月分表 | `自動分表.md` |
| 雪花主键 | `雪花ID.md` |
| 抢占用的无实体 UPDATE | `無實體更新.md`、`更新數據.md` |
| 多库 / 跨库 | `SAAS分庫.md`、`跨庫查詢.md`、`讀寫分離.md` |
| 建表与实体元数据 | `實體管理EntityMaintenance.md`、`庫表管理DbMaintenance.md` |

`sqlsugar-mcp` 的 notes 语料与该目录是同一批文件，直接读目录即可，无需接入 MCP。

仓库自身文档：`docs/packages/data.md`（表名约定、模块分库、建表选取规则）、`docs/guide/data.md`。

### 3.3 禁止事项：EF Core 惯用法

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | **SqlSugar 无 change tracking**，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()` |
| `Migrations` / `Add-Migration` | `CodeFirst.InitTables()`，本仓库经 `DbInitializer` 驱动 |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

## 4. 交付范围

| | 包 | 内容 |
| --- | --- | --- |
| **PR1** | `XiHan.Framework.Auditing.SqlSugar` | 5 个日志写入器 + 5 张表。建立包骨架范式 |
| **PR2** | `XiHan.Framework.EventBus.SqlSugar` | 发件箱 + 收件箱 |

两个 PR 相互独立，各自从 `upstream/main` 开 worktree。PR1 先行，其确立的项目结构、命名、props 引入顺序、README 七段结构由 PR2 及后续 Store 沿用。

## 5. 包结构与命名

沿用仓库既有的兄弟子包模式（`EventBus.Kafka`、`Bot.Telegram`、`SearchEngines.Elasticsearch`）：主包不依赖实现包，实现包依赖主包与 `XiHan.Framework.Data`。

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  XiHanAuditingSqlSugarModule.cs
  Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs
  Entities/        Writers/        README.md
```

- 模块类命名 `XiHan{ModuleName}Module`，置于项目根目录，只做装配不写逻辑
- csproj 按序 Import `netcore` / `common` / `version` / `nuget` 四个 props，使用 `Microsoft.NET.Sdk`
- 注册进 `framework/XiHan.Framework.slnx` 对应分层文件夹
- 补 `docs/packages/*.md` 并更新 `docs/.vitepress/config.ts` 侧边栏
- 更新根 `README.md` 模块清单

## 6. 公共约定

### 6.1 表名

框架自有表一律 `sys_` 前缀、全小写下划线，依据 `docs/packages/data.md`（`sys_user` / `sys_tenant` / `sys_archive` / `sys_partitioned_log`）。

```
sys_operation_log   sys_login_log   sys_api_log
sys_access_log      sys_exception_log
sys_event_outbox    sys_event_inbox
```

### 6.2 列名

Pascal_Snake_Case，与 `SugarFullAuditedEntity` 一致（`Basic_Id`、`Row_Version`、`Created_Time`、`Is_Deleted`）。新增列同例：`Event_Name`、`Trace_Id`、`Status_Code`。

每列必须带 `ColumnDescription` 简体中文说明。

### 6.3 主键与时间

- 主键列 `Basic_Id`，类型 `long`，`IsIdentity = false`，由 `XiHan.Framework.DistributedIds` 的雪花生成器产生
- 时间列一律 `DateTimeOffset`。`OutgoingEventInfo.CreatedTime` / `IIncomingEventInfo.CreatedTime` 是 `DateTime`，映射时**显式转换并指明时区处理**，不得隐式转换

### 6.4 数据库可移植性

只使用 SqlSugar 的通用表达式 API，不写针对特定数据库的原生 SQL。`FOR UPDATE SKIP LOCKED` 等方言特性一律不用；抢占改用条件 `UPDATE` 实现（见 8.3）。

## 7. PR1：审计日志落库

### 7.1 现状

`XiHan.Framework.Auditing` 已具备完整采集链路：5 条 Pipeline → `ChannelLogQueue` → 5 个 QueueWorker → 5 个 Writer 接口。当前 5 个 Writer 全为 `Null*Writer`。

本 PR 只提供 Writer 的 SqlSugar 实现与对应实体，**不改动采集链路任何一行**。

### 7.2 实体与分表

五张表分别对应 `AccessLogRecord` / `ApiLogRecord` / `ExceptionLogRecord` / `LoginLogRecord` / `OperationLogRecord`。

记录模型位于 `XiHan.Framework.Auditing`，是不带持久化关注点的 POCO；实体定义在本包内，**不让记录模型继承 SugarEntity**，避免主包被迫依赖 SqlSugar。

日志表按月分表：实体实现 `ISplitTableEntity`，`DbInitializer` 已支持 `CodeFirst.SplitTables().InitTables()`。参考 `自動分表.md`。

### 7.3 写入

Writer 经 `ISqlSugarClientResolver.GetClientForEntity<T>()` 取得客户端后 `Insertable` 执行。

审计写入**不参与业务事务**：日志由后台 Worker 从队列消费，本就在业务请求之外，不应因业务回滚而丢失。

敏感字段沿用既有的 `LogSanitizer` 脱敏，Writer 不重复实现。

## 8. PR2：事件发件箱与收件箱

### 8.1 契约现状

`IEventOutbox`：`EnqueueAsync` / `GetWaitingEventsAsync(maxCount, filter, ct)` / `DeleteAsync` / `DeleteManyAsync`。

`IEventInbox`：`EnqueueAsync` / `GetWaitingEventsAsync` / `MarkAsProcessedAsync` / `RetryLaterAsync` / `MarkAsDiscardAsync` / `DeleteOldEventsAsync`。

宿主循环 `EventBoxOutboxSenderHostedService`：取待发 → `PublishManyFromOutboxAsync` → `DeleteManyAsync`。**无分布式锁、无领取语义**，去重责任完全落在实现的 `GetWaitingEventsAsync` 内。

`EventBoxInboxProcessorHostedService` 已实现重试与丢弃状态机（`MaxInboxRetryCount` 默认 5，`InboxRetryDelaySeconds` 默认 10）。

`OutgoingEventInfo` 属性只读、构造函数 protected，**不能直接作为实体**；本包另立实体类并实现双向映射。`ExtraProperties` 序列化为 JSON 列。

### 8.2 事务参与

`DistributedEventBusBase.AddToOutboxAsync` 经 `unitOfWork.ServiceProvider.GetRequiredService(...)` 解析 `IEventOutbox`，即实现位于当前工作单元的 scope 内。

因此 `EnqueueAsync` 必须经 `ISqlSugarClientResolver` 取得**当前工作单元已钉住的那条连接**，使事件行与业务数据落入同一个 `SqlSugarTransactionApi`。

> 禁止在 `EnqueueAsync` 内自行创建连接或使用新 scope。这样做会退化为两个独立事务，同事务保证静默失效，而单元测试仍会通过。

### 8.3 发件箱表的库位

**每个库各建一张 `sys_event_outbox`，事件行写入业务数据所在的那个库。**

依据：`ModuleDataSourceAttribute` 的文档明确「同一工作单元跨多个库写入时，每个连接各开一个本地事务，框架不提供跨库分布式事务」。若发件箱固定在主库，而业务实体经 `[ModuleDataSource]` 落在模块库，两者即分属不同事务——业务回滚后事件仍会发出，且无任何报错。

该选择同时是插件兼容的前提：插件若使用自有模块库，自动获得自己的发件箱，无需改动框架。

代价：发送端需遍历 `ISqlSugarClientResolver.GetCurrentLayoutConfigIds()` 列出的全部库。

### 8.4 原子领取

`GetWaitingEventsAsync` 实现为「先抢占、再取回」：

1. 以条件 `UPDATE` 抢占一批待发行（置抢占令牌与抢占时间，条件含未被抢占或抢占已超时）
2. 按自己的抢占令牌取回该批记录

抢占超时后自动释放，避免实例异常退出导致记录永久滞留。

方法名为 `GetWaitingEvents` 而实际语义为「领取」，该落差必须写入 XML 文档注释。这是不修改 Abstractions 契约的代价，属有意选择。

收件箱的 `GetWaitingEventsAsync` 采用相同处理。收件箱去重键为 `MessageId`，建唯一索引。

### 8.5 投递语义

至少一次。发送成功与 `DeleteManyAsync` 之间若进程退出，记录会被重新投递——这是 outbox 模式的固有语义，消费端需幂等。该语义必须写入包 README。

## 9. 插件兼容性约束

框架的 `FolderPlugInSource` 将插件程序集载入 `AssemblyLoadContext.Default`，而 `DbEntityTypeProvider.ScanEntityTypes()` 经 `ReflectionHelper.GetAllAssemblies()` 枚举的正是同一个上下文。**插件携带的 `[SugarTable]` 实体当前即可被自动建表。**

本设计不得破坏该性质，并遵守三条约束：

1. **不引入任何硬编码的实体清单**，实体发现继续依赖既有扫描
2. **发件箱每库一张**（8.3），使自带模块库的插件可直接使用
3. **收件箱遇到无法解析类型的事件必须停放**（走既有的 `MarkAsDiscardAsync`），不得崩溃或无限重试。否则卸载一个插件将导致整个收件箱阻塞

**已知边界，本次不处理**：`ScanEntityTypes` 经 `Lazy` 缓存，启动后才载入的程序集不会被扫描到。当前 `FolderPlugInSource` 为启动期加载，不受影响；未来若支持运行期安装，此处需要失效机制。仅在此登记。

## 10. 测试策略

CI 在 ubuntu 运行且不启动任何外部服务，因此分两层：

**第一层 —— SQLite 内存库，CI 强门禁执行**

覆盖映射正确性、状态流转、过滤条件翻译、分表命名、重试与丢弃分支。`XiHan.Framework.Data.Tests` 已有 SQLite 用法可循。

**第二层 —— 真实 MySQL，本机执行，CI 自动跳过**

采用 `Assert.SkipWhen(...)` + 环境变量取地址，范式见 `XiHan.Framework.EventBus.Redis.Tests/RedisPendingBehaviorTests.cs`（读 `XIHAN_TEST_REDIS`）。本设计使用 `XIHAN_TEST_MYSQL`。

必测项：**多线程并发调用 `GetWaitingEventsAsync`，断言同一条记录只被一个调用方领取**。此项无法由 SQLite 证明，是第二层存在的唯一理由。

测试项目 Import `props/test.props`，使用 xunit.v3 + Microsoft.Testing.Platform。

## 11. 风险与已知边界

| 项 | 说明 |
| --- | --- |
| 方法名与语义落差 | `GetWaitingEventsAsync` 实为领取，靠文档注释说明；若上游日后愿意调整契约，应改为显式的领取方法 |
| 发送端遍历多库 | 库数量多时轮询成本上升。当前按布局内库数遍历，规模化后需要按库分片调度 |
| 至少一次投递 | 消费端必须幂等，框架不提供恰好一次 |
| 分表查询 | 日志实体分表后，跨月查询需显式 `SplitTable()`，仓储层当前未暴露该能力（属框架既有缺口，不在本次范围） |
| 审计写入不入事务 | 有意设计，见 7.3 |

## 12. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 每个 `.cs` 文件带两行版权声明（`XHFH001`）
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**；权衡论证与踩坑叙事写入提交信息，不写进代码
- 两个包各带 README，沿用固定七段结构
