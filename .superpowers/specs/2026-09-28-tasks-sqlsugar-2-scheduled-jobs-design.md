# Tasks.SqlSugar ②：定时任务存储与文档站收尾 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-tasks-sqlsugar-2-scheduled-jobs.md`
- **前置**：第 ① 份（`.superpowers/specs/2026-09-28-tasks-sqlsugar-1-background-jobs-design.md`）必须已完成。本份复用它产出的包骨架、`XiHanTasksSqlSugarOptions`、`TasksHostClientAccessor`、`AddXiHanTasksSqlSugar` 与测试夹具
- **所属 PR**：`Tasks.SqlSugar` 单独一个 PR，由第 ① 份与本份共同组成；本份完成后提交
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-8
- **系列**：`Tasks.SqlSugar` 共 2 份。① 包骨架 + `IBackgroundJobStore`；② `IJobStore` + 文档站收尾（本份）

> 本文档**自成一体**。实现本份所需的全部约束都写在这里。第 ① 份的产出在 §1.3 列出了确切签名，不需要回头读第 ① 份的 spec。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的地方是「运行中实例」**（第 5 节第 ①）。内存存储在进程重启时自动清空，落库之后不会——一个在执行途中崩溃的实例会**永远**停在「运行中」，而调度器对不允许并发的任务会据此**永远跳过触发**，只留一行警告日志。SQLite 测试不会自然暴露它，必须专门写用例。
>
> 第二容易出错的是 **`DateTimeOffset` 读回偏移**（第 5 节第 ②）：直接用 `DateTimeOffset` 列，在 SQLite 上读回的瞬时会被平移，UTC 时区的 CI 机器上看不出来。

---

## 1. 背景与目标

### 1.1 现状

`XiHan.Framework.Tasks` 的定时任务由调度器、执行器、存储三方协作：

| 角色 | 位置 | 与存储的交互 |
| --- | --- | --- |
| 调度器 | `ScheduledJobs/Scheduler/CompositeJobScheduler.cs:253-300` | 任务不允许并发（`AllowConcurrent == false`）时先调 `GetRunningInstancesAsync`（`:262`），有任何运行中实例就跳过本次触发 |
| 执行器 | `ScheduledJobs/Executor/JobExecutor.cs:41-138` | 执行前 `SaveJobInstanceAsync`（`:53`，状态 `Running`）→ 执行 → `UpdateJobStatusAsync`（`:105`，异常路径 `:126`）→ `SaveJobHistoryAsync`（`:168`） |
| 存储 | `ScheduledJobs/Abstractions/IJobStore.cs` | 7 个方法 |

`GetJobInstanceAsync`、`GetJobHistoryAsync`、`CleanupHistoryAsync` 在框架内**没有调用方**，留给应用侧（管理后台、定期清理）使用。`XiHanJobOptions.HistoryRetentionDays`（默认 30）也没有被任何代码读取。

默认存储 `DefaultJobStore` 是进程内字典，带三个上限（实例 20000、已结束实例 10000 之后按先进先出淘汰、历史 100000）。

**注册方式**：`XiHanTasksModule.ConfigureServices`（`XiHanTasksModule.cs:49`）调 `AddXiHanTasks`，其中 `services.TryAddSingleton<IJobStore, DefaultJobStore>()`（`ScheduledJobs/Extensions/DependencyInjection/XiHanTasksServiceCollectionExtensions.cs:60`）。

**消费方的生命周期**：`JobExecutor` 与 `CompositeJobScheduler` 都注册为 `Singleton`，都在**构造函数**里注入 `IJobStore`。因此 `IJobStore` **必须**是单例，不能改成 `Scoped`。

### 1.2 契约语义（从 `DefaultJobStore` 反推）

| 方法 | 语义 |
| --- | --- |
| `SaveJobInstanceAsync(JobInstance)` | 插入或更新（按 `InstanceId`）；`null` 抛 `ArgumentNullException`；状态为 `Succeeded` / `Failed` / `Canceled` 时，若 `CompletedAt` 为空则**就地**补为当前 UTC 时间（`DefaultJobStore.cs:44-48`） |
| `UpdateJobStatusAsync(string, JobStatus)` | 实例存在则改状态；终止状态时 `CompletedAt` 覆盖为当前 UTC 时间；实例不存在时什么都不做 |
| `SaveJobHistoryAsync(JobHistory)` | `HistoryId` 为空白时就地补一个新 `Guid` 的 `N` 格式；插入或更新 |
| `GetJobInstanceAsync(string)` | 按标识查，不存在返回 `null` |
| `GetJobHistoryAsync(string, int, int)` | `jobName` 空白抛 `ArgumentException`；`pageIndex < 1` 或 `pageSize < 1` 抛 `ArgumentOutOfRangeException`；按 `StartedAt` 降序分页 |
| `GetRunningInstancesAsync(string)` | `jobName` 空白抛 `ArgumentException`；返回该任务状态为 `Running` 的实例 |
| `CleanupHistoryAsync(int)` | `retentionDays < 0` 抛 `ArgumentOutOfRangeException`；删除 `StartedAt` 早于「当前 UTC 时间 − 保留天数」的历史 |

契约的时间全部是 `DateTimeOffset`，由 `DateTimeOffset.UtcNow` 产生。

### 1.3 第 ① 份已产出、本份直接使用的

```csharp
// XiHan.Framework.Tasks.SqlSugar.Options
public class XiHanTasksSqlSugarOptions
{
    public const string SectionName = "XiHan:Tasks:SqlSugar";
    public TimeSpan BackgroundJobLeaseTimeout { get; set; }   // 本份追加 RunningInstanceGracePeriod
}

// XiHan.Framework.Tasks.SqlSugar.Clients —— 每次操作新建作用域、切到宿主上下文、取默认布局主库客户端
public sealed class TasksHostClientAccessor
{
    public Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation);
    public Task ExecuteAsync(Func<ISqlSugarClient, Task> operation);
}

// XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection
public static IServiceCollection AddXiHanTasksSqlSugar(this IServiceCollection services, IConfiguration configuration);
// 现有内容：Configure 选项、TryAddSingleton<TasksHostClientAccessor>、Replace IBackgroundJobStore

// 测试项目：TasksTestContext（SQLite 夹具）、StubClientResolver、FakeClock
```

### 1.4 要交付什么

1. 实体 `SysJobInstance`（表 `sys_job_instance`）、`SysJobHistory`（表 `sys_job_history`）与双向映射 `JobStoreMapper`
2. `SqlSugarJobStore`：`IJobStore` 的 7 个方法
3. 运行中实例的**截止时刻**，使崩溃遗留的「运行中」记录在超时后不再阻塞调度
4. 以 `Replace` 顶替主包的 `IJobStore`
5. 新包的文档站收尾：包 README 补全、`docs/packages/tasks-sqlsugar.md`、`docs/.vitepress/config.ts` 侧边栏、`docs/packages/index.md` 模块清单、`framework/README.md` 与 `framework/README_cn.md` 模块清单，以及四个 README 与文档站里的模块计数、`framework/README*.md` 里的测试工程计数

### 1.5 成功标准

1. 容器里的 `IJobStore` 是 `SqlSugarJobStore`，生命周期 `Singleton`；`ValidateScopes = true` 时可从根容器解析
2. 7 个方法的行为与 §1.2 的表一致
3. 超过截止时刻的「运行中」实例不再出现在 `GetRunningInstancesAsync` 的结果里
4. 以 `+08:00` 偏移写入的时间，读回后瞬时不变（相差小于 1 秒）
5. 在租户上下文里保存时，写库期间处于宿主上下文，且行上的 `Tenant_Id` 保留实例自带的租户
6. `CleanupHistoryAsync` 同时删除过期历史与过期的已结束实例，不删除运行中或等待中的实例
7. 文档站条目、侧边栏、两个模块清单与包 README 全部就位
8. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 被替换的契约与它的消费方 —— 最高优先**

```
framework/src/XiHan.Framework.Tasks/ScheduledJobs/
  Abstractions/IJobStore.cs                  要实现的 7 个方法
  Store/DefaultJobStore.cs                   被替换的实现；校验、排序、补时间的语义以它为准
  Models/JobInstance.cs、JobHistory.cs、JobInfo.cs、JobStatus.cs、JobTriggerType.cs
  Executor/JobExecutor.cs:41-175             调用顺序：保存实例 → 执行 → 更新状态 → 保存历史
  Scheduler/CompositeJobScheduler.cs:253-300 不允许并发时查运行中实例
  Pipeline/TimeoutMiddleware.cs:30           超时取 JobInfo.TimeoutMilliseconds
  Extensions/DependencyInjection/XiHanTasksServiceCollectionExtensions.cs:60,152-183
framework/test/XiHan.Framework.Tasks.Tests/ScheduledJobs/DefaultJobStoreTests.cs   现有契约用例
```

**② 第 ① 份的产出**

```
framework/src/XiHan.Framework.Tasks.SqlSugar/
  Clients/TasksHostClientAccessor.cs
  BackgroundJobs/SqlSugarBackgroundJobStore.cs     同一个包里「经访问器写库」的写法
  Entities/SysBackgroundJob.cs、Mapping/BackgroundJobMapper.cs
  Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs
framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/
  TasksTestContext.cs、StubClientResolver.cs、FakeClock.cs、TasksSqlSugarRegistrationTests.cs
```

**③ 时间与审计 AOP**

```
framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventOutboxMapper.cs:77-85   DateTime → DateTimeOffset 的归一写法
framework/src/XiHan.Framework.Data/SqlSugar/Extensions/EntityAuditExtensions.cs:51-68,172-193   按属性名改写 TenantId
```

**④ SqlSugar 源码 —— API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs          AnyAsync / ToPageListAsync / OrderBy / FirstAsync
  Interface/IUpdateable.cs         SetColumns / Where
  Interface/IDeleteable.cs         Where
  Entities/Mapping/SugarMappingAttribute.cs:346-362   SugarIndexAttribute
```

> 树与目录名：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`，权威源码是 `Src/Asp.NetCore2/`，**不是** `Src/Asp.Net/`。更新提供者的目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：

| 任务 | 必读 |
| --- | --- |
| 插入或更新 | `插入或更新Storageable.md`（本份不用 `Storageable`，读它是为了理解为什么选「先查后写」） |
| 分页 | `分頁查詢，同步分頁和非同步分頁.md` |
| 条件更新 | `更新數據.md` |
| 建表与索引 | `库表管理、数据库表操作方法、 Show tables 获取表结构、索引.md` |

仓库文档：`docs/packages/tasks.md`、`docs/packages/eventbus-sqlsugar.md`（文档站条目的范本）、`docs/packages/index.md`、`docs/.vitepress/config.ts`。

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

- **不改 `XiHan.Framework.Tasks` 的任何文件**。不改 `IJobStore`、模型、`JobExecutor`、`CompositeJobScheduler`
- **实体时间列不用 `DateTimeOffset`**，一律 UTC 的 `DateTime`，由映射层归一。见第 5 节第 ②
- **存储里不许出现 `new SqlSugarClient`、不许注入 `ISqlSugarClientResolver`**，一律经 `TasksHostClientAccessor`
- **不改第 ① 份的领取逻辑**（`SqlSugarBackgroundJobStore.ClaimAsync`）
- **不加 `[SplitTable]`**
- **根 `README.md` / `README_cn.md` 只改模块计数，不往「常用包」表加行**：那是精选的常用包清单，`Auditing.SqlSugar`、`EventBus.SqlSugar` 都不在里面，见「待确认的决策」第 9 条
- **不改 `docs/packages/tasks.md`、`docs/guide/` 下的任何文件**：一个 PR 只做一件事

## 3. 非目标

- **不注册自动清理任务**。`CleanupHistoryAsync` 由应用自行定期调用；`XiHanJobOptions.HistoryRetentionDays` 维持现状（未被读取）
- **不完整还原 `JobInfo`**。实例只保存 `JobInfo` 的最小快照，见 §4.3
- **不保存 `JobInfo.Priority`、`Description`、`CronExpression` 等调度定义**。任务定义来自代码注册，不是存储
- **不做多库与租户库遍历**。全部写默认布局主库，与第 ① 份一致
- **不参与业务工作单元**。调用方（执行器、调度器）运行在无工作单元的后台线程

## 4. 设计

### 4.1 选项追加

`XiHanTasksSqlSugarOptions` 追加：

```csharp
public TimeSpan RunningInstanceGracePeriod { get; set; } = TimeSpan.FromMinutes(1);
```

### 4.2 实体

两张表都继承 `SugarEntity<string>`（主键列 `Basic_Id`，取契约的 `InstanceId` / `HistoryId`），**时间列一律是 UTC 口径的 `DateTime`**。

**`SysJobInstance`**，表 `sys_job_instance`，索引 `idx_sys_job_instance_name_status`（`JobName` 升序、`Status` 升序）：

| 属性 | 列 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- | --- |
| `JobName` | `Job_Name` | `string(256)` | 否 | |
| `JobTypeName` | `Job_Type_Name` | `string(512)` | 是 | `JobInfo.JobType.AssemblyQualifiedName` |
| `Status` | `Status` | `int` | 否 | `(int)JobStatus` |
| `TriggerType` | `Trigger_Type` | `int` | 否 | `(int)JobTriggerType` |
| `TenantId` | `Tenant_Id` | `long?` | 是 | |
| `ScheduledAt` | `Scheduled_At` | `DateTime` | 否 | UTC |
| `StartedAt` | `Started_At` | `DateTime?` | 是 | UTC |
| `CompletedAt` | `Completed_At` | `DateTime?` | 是 | UTC |
| `DurationMilliseconds` | `Duration_Milliseconds` | `long?` | 是 | |
| `RunningDeadline` | `Running_Deadline` | `DateTime?` | 是 | UTC，仅 `Running` 状态有值，见 §4.5 |
| `RetryCount` | `Retry_Count` | `int` | 否 | |
| `ExecutionNode` | `Execution_Node` | `string(256)` | 是 | |
| `TraceId` | `Trace_Id` | `string(64)` | 是 | |
| `ParametersJson` | `Parameters_Json` | 大文本 | 是 | `Parameters` 的 JSON |
| `ErrorMessage` | `Error_Message` | 大文本 | 是 | |
| `StackTrace` | `Stack_Trace` | 大文本 | 是 | |

**`SysJobHistory`**，表 `sys_job_history`，索引 `idx_sys_job_history_name_started`（`JobName` 升序、`StartedAt` 降序）：

| 属性 | 列 | 类型 | 可空 |
| --- | --- | --- | --- |
| `InstanceId` | `Instance_Id` | `string(128)` | 否 |
| `JobName` | `Job_Name` | `string(256)` | 否 |
| `Status` | `Status` | `int` | 否 |
| `StartedAt` | `Started_At` | `DateTime`（UTC） | 否 |
| `CompletedAt` | `Completed_At` | `DateTime?`（UTC） | 是 |
| `DurationMilliseconds` | `Duration_Milliseconds` | `long?` | 是 |
| `TenantId` | `Tenant_Id` | `long?` | 是 |
| `TriggerType` | `Trigger_Type` | `int` | 否 |
| `IsSuccess` | `Is_Success` | `bool` | 否 |
| `ErrorMessage` | `Error_Message` | 大文本 | 是 |
| `StackTrace` | `Stack_Trace` | 大文本 | 是 |
| `RetryCount` | `Retry_Count` | `int` | 否 |
| `ExecutionNode` | `Execution_Node` | `string(256)` | 是 |
| `TraceId` | `Trace_Id` | `string(64)` | 是 |
| `ParametersJson` | `Parameters_Json` | 大文本 | 是 |
| `Remarks` | `Remarks` | 大文本 | 是 |

属性名避开审计 AOP 的保留名（`CreatedTime`、`ModifiedTime`、`IsDeleted` 等）；`TenantId` 保留原名，由访问器的宿主上下文保护（第 5 节第 ④）。

### 4.3 映射 `JobStoreMapper`

静态类：

| 方法 | 说明 |
| --- | --- |
| `SysJobInstance ToEntity(JobInstance instance, TimeSpan runningGracePeriod)` | 时间经 `ToUtc` 归一；`JobTypeName` 取 `JobInfo?.JobType?.AssemblyQualifiedName`；`RunningDeadline` 见 §4.5；`Parameters` 序列化抛 `NotSupportedException` / `JsonException` 时存 `null` |
| `JobInstance ToJobInstance(SysJobInstance entity)` | 时间经 `FromUtc` 还原为偏移 0 的 `DateTimeOffset`；`JobInfo` 只还原 `JobName`、`JobType`（能解析时）、`TriggerType`、`TenantId`；`Parameters` 反序列化为 `Dictionary<string, object?>` |
| `SysJobHistory ToEntity(JobHistory history)` | 逐字段复制，时间归一 |
| `JobHistory ToJobHistory(SysJobHistory entity)` | 逐字段复制，时间还原 |

四个私有辅助：`DateTime ToUtc(DateTimeOffset)`、`DateTime? ToUtc(DateTimeOffset?)`、`DateTimeOffset FromUtc(DateTime)`（`new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))`）、`DateTimeOffset? FromUtc(DateTime?)`。

**`JobInfo` 的最小快照**：`JobInfo.JobType` 是 `Type`，无法原样落库，只存程序集限定名，读回时 `Type.GetType(name, throwOnError: false)`；解析不到就不赋值（保持 `JobInfo` 的默认值）。框架内唯一读取实例的调用方是调度器，它只看 `GetRunningInstancesAsync` 的结果「有没有」，不读 `JobInfo`。

**参数的序列化**：用 `System.Text.Json`。`Parameters` 的值是 `object?`，可能含无法序列化的类型（如 `System.Type` 会抛 `NotSupportedException`，循环引用抛 `JsonException`）。这两类异常被捕获并存 `null`——否则 `JobExecutor` 在执行前的 `SaveJobInstanceAsync` 就抛异常，任务根本不会执行，而 `DefaultJobStore` 只是持有引用、从不失败。**只保证这两类**：参数对象的属性 getter 抛出的其他异常仍会向上传播，保存实例失败、任务不执行（§7）。读回的值是 `JsonElement`，不是原类型。

### 4.4 存储 `SqlSugarJobStore`

构造函数 `(TasksHostClientAccessor clientAccessor, IOptions<XiHanTasksSqlSugarOptions> options)`。每个方法都经访问器取客户端；时间一律 `DateTimeOffset.UtcNow` / `DateTime.UtcNow`（与 `JobExecutor` 同源）。

| 方法 | 实现 |
| --- | --- |
| `SaveJobInstanceAsync` | `ThrowIfNull`；终止状态时 `jobInstance.CompletedAt ??= DateTimeOffset.UtcNow`；映射；在同一次访问器操作里 `AnyAsync` 判存在 → `Updateable(entity)` 或 `Insertable(entity)` |
| `UpdateJobStatusAsync` | `ThrowIfNull(instanceId)`；终止状态 `SetColumns(Status, CompletedAt = DateTime.UtcNow)`，否则 `SetColumns(Status)`；`Where(BasicId == instanceId)`；0 行不报错 |
| `SaveJobHistoryAsync` | `ThrowIfNull`；`HistoryId` 空白时就地补新值；映射；先查后写，同上 |
| `GetJobInstanceAsync` | `ThrowIfNull(instanceId)`；`Where(BasicId == instanceId).FirstAsync()` |
| `GetJobHistoryAsync` | 参数校验同 `DefaultJobStore`；`Where(JobName == jobName).OrderBy(StartedAt, Desc).ToPageListAsync(pageIndex, pageSize)` |
| `GetRunningInstancesAsync` | `ThrowIfNullOrWhiteSpace`；`Where(JobName == jobName && Status == Running && RunningDeadline != null && RunningDeadline > 当前 UTC)` |
| `CleanupHistoryAsync` | 参数校验；在同一次访问器操作里删「`StartedAt` 早于截止」的历史，再删「终止状态且 `CompletedAt` 早于截止」的实例 |

**「先查后写」而不是 `Storageable`**：插入或更新只发生在同一个实例标识上，而实例标识在一次执行里只由一个执行器使用，不存在并发写同一标识的场景；`AnyAsync` + `Insertable` / `Updateable` 两个 API 都已在第 ① 份核对过，不引入新 API。

### 4.5 运行中实例的截止时刻

`SaveJobInstanceAsync` 以 `Running` 状态保存时计算：

```
TimeoutMilliseconds <= 0（不限时）: RunningDeadline = UnboundedRunningDeadline（9999-12-31 00:00:00 UTC）
否则:                               RunningDeadline = (StartedAt ?? ScheduledAt) + TimeoutMilliseconds 毫秒 + RunningInstanceGracePeriod
```

其他状态存 `null`。

**不限时的任务**：`TimeoutMiddleware` 把 `TimeoutMilliseconds <= 0` 当作「不限时」直接放行（`Pipeline/TimeoutMiddleware.cs:32-35`）。这类实例没有可推导的运行上限，截止时刻取常量 `JobStoreMapper.UnboundedRunningDeadline`，在被显式结束（`UpdateJobStatusAsync` 写入终止状态）之前一直算运行中。取 `9999-12-31 00:00:00` 而不是 `DateTime.MaxValue`：后者带 7 位小数秒，MySQL `datetime` 列按精度四舍五入后会越过 9999-12-31。代价与清除方法见 §7，取舍见「待确认的决策」第 10 条。`GetRunningInstancesAsync` 只返回截止时刻**晚于当前时间**的运行中实例。

依据：`TimeoutMiddleware` 以 `JobInfo.TimeoutMilliseconds` 为整次执行（含重试）的上限（`Pipeline/TimeoutMiddleware.cs:30`；中间件注册顺序 `Logging → Timeout → Lock → Retry → Metrics`，超时在重试之外）。超过「开始 + 超时 + 宽限」仍标为运行中的实例，只可能是执行途中进程退出、或 `UpdateJobStatusAsync` 本身失败（`JobExecutor.cs:124-131` 只记日志）留下的遗留记录。

截止时刻只影响「算不算运行中」，**不改写实例的状态**：遗留记录的 `Status` 仍是 `Running`，`GetJobInstanceAsync` 照样如实返回。

### 4.6 注册

`AddXiHanTasksSqlSugar` 在第 ① 份的基础上追加一行：

```csharp
services.Replace(ServiceDescriptor.Singleton<IJobStore, SqlSugarJobStore>());
```

主包 `TryAddSingleton<IJobStore, DefaultJobStore>()`（`XiHanTasksServiceCollectionExtensions.cs:60`）在 `XiHanTasksModule.ConfigureServices` 里执行；本模块依赖 `XiHanTasksModule`，其 `ConfigureServices` 在后，`Replace` 能找到那条注册。

应用若之后再调 `XiHanJobBuilder.UseStore<T>()`（内部是 `AddSingleton`，`XiHanJobBuilder.cs:30-34`），会多出一条注册、按「最后一条生效」覆盖本包——这是预期行为，写进 README。

### 4.7 文档站收尾

| 位置 | 改动 |
| --- | --- |
| `framework/src/XiHan.Framework.Tasks.SqlSugar/README.md` | 整份替换为覆盖两个存储的七段版本 |
| `docs/packages/tasks-sqlsugar.md` | 新建，结构照 `docs/packages/eventbus-sqlsugar.md`：页首引言与四行元数据、概述、何时使用、安装与启用、表结构、工作原理、配置、主要 API / 类型、注意事项与最佳实践、扩展点 / 自定义、依赖模块、相关模块 |
| `docs/.vitepress/config.ts` | 「存储 · 模板 · 任务 · 治理」分组里，`pkg("Tasks 定时任务", "tasks"),` 之后插入 `pkg("Tasks.SqlSugar", "tasks-sqlsugar"),` |
| `docs/packages/index.md` | `[Tasks](./tasks)` 那一行之后插入 `Tasks.SqlSugar` 一行（`EventBus.SqlSugar`、`Auditing.SqlSugar` 都在这张清单里） |
| `framework/README.md` | 模块清单 `` `Tasks` `` 那一行之后插入英文一行 |
| `framework/README_cn.md` | 同上，中文 |
| 模块计数 | 实现时先读当前值再加一，不写死数字。写本份时共 20 处：`README.md`、`README_cn.md` 各 3 处（含 shields.io 徽章 `Modules-NN-1f6feb`）；`framework/README.md`、`framework/README_cn.md` 各 2 处模块计数 + 1 处「单测工程」计数；文档站 `docs/index.md` 2 处、`docs/introduction.md` 1 处、`docs/why.md` 4 处、`docs/packages/index.md` 1 处。查找范围覆盖四个 README 与 `docs/**/*.md`（排除 `node_modules`、`.vitepress`、`docs/changelog.md` 的历史记录） |

## 5. 会静默失效的陷阱

以下每一条错了都**不会报错**，且除特别注明外 **SQLite 单元测试照样全绿**。

**① 崩溃遗留的「运行中」实例会让不允许并发的任务永远不再触发。**

`DefaultJobStore` 在进程重启时清空，遗留的运行中实例随之消失；落库之后它们永久存在。调度器对 `AllowConcurrent == false` 的任务，只要 `GetRunningInstancesAsync` 返回任何一条就跳过触发（`CompositeJobScheduler.cs:260-267`），日志只有一行 `Warning`：「任务 X 不允许并发执行，跳过本次触发」。**任务从此再也不执行**，没有异常，没有告警。

触发条件很普通：进程在任务执行途中被部署重启、被 OOM 杀掉，或 `UpdateJobStatusAsync` 遇上一次数据库抖动。

对策是 §4.5 的截止时刻。用例「超过截止时刻的运行中实例不再算运行中」专门兜住它——**不要**把这个用例的时间改成贴着截止时刻的边界值。

另一面：截止时刻计算若漏掉了宽限期、或用了 `ScheduledAt` 而不是 `StartedAt`，一个**真正在跑**的长任务可能在超时前就被判定为「不在运行」，调度器随即再触发一次，**同一任务并发执行两份**。用例「运行中实例的截止时刻为开始时间加超时再加宽限」精确断言这个值。

第三种写法同样静默失效：把不限时（`TimeoutMilliseconds <= 0`）按 `max(0, timeout)` 当成 0 毫秒。一个不限时、不允许并发的任务跑过 1 分钟宽限期后就不再算运行中，调度器随即再触发一份——**同一任务并发两份**，不报错。用例「超时关闭时截止时刻为无限远」断言映射结果，用例「超时关闭的非并发任务在宽限期后仍算运行中且调度器不再触发第二份」用真实的 `CompositeJobScheduler.TriggerJobAsync` 断言调度器返回空字符串、执行器一次都没被调用。

**② 时间列用 `DateTimeOffset` 会在读回时平移瞬时。**

SQLite 侧的 `DateTimeOffset` 不保存偏移：表达式参数经 `UtilMethods.ConvertFromDateTimeOffset` 折叠成 `DateTime`，列建成 `datetime` 文本，读回时偏移由 `DateTime.Kind` 反推。写入 `+08:00` 的时间在 UTC 机器上读回会被当成 UTC 的同一个墙钟时刻，**瞬时平移 8 小时**；反过来在 UTC+8 机器上写 `+00:00` 也会平移（该现象在发件箱开发时实测，见 `.superpowers/specs/2026-09-21-eventbus-sqlsugar-p6-outbox-multi-database-design.md` §6）。

`GetJobHistoryAsync` 的排序与 `CleanupHistoryAsync` 的截止比较都在 SQL 侧、两侧同向折叠，**排序与清理照样正确**，只有读回给调用方的值是错的——管理后台显示的执行时间差 8 小时，没有任何测试会失败，除非专门断言读回的瞬时。

对策：实体一律存 UTC 的 `DateTime`，映射层 `value.UtcDateTime` 写入、`SpecifyKind(Utc)` 读回。用例「保存后按标识查回任务实例」用 `+08:00` 的输入断言读回的瞬时——在 UTC 时区的 CI 机器上同样能抓到错误。

**③ 参数序列化抛异常会让任务根本不执行。**

`JobExecutor` 在执行任务**之前**调 `SaveJobInstanceAsync`（`JobExecutor.cs:53`），它抛异常就直接进入 `catch`：任务被记为失败、业务代码一行都没跑。`Parameters` 是 `IDictionary<string, object?>`，调用方塞进一个 `Type` 或带循环引用的对象都会让 `JsonSerializer` 抛异常。`DefaultJobStore` 只持有引用，从不失败——换成本包后，同样的调用会让任务静默地不再执行（只留一条错误日志）。

对策：捕获 `NotSupportedException` 与 `JsonException`，存 `null`。用例「参数无法序列化时存为空」兜住它。**本包保证的只是这两类异常不阻止任务执行**：参数对象的属性 getter 抛出的其他异常（如 `InvalidOperationException`）仍会让保存实例失败、任务不执行。不捕获 `Exception` 是为了不吞掉数据库写入以外的编程错误；这一残余风险写进 §7。

**④ 写库必须处于宿主上下文。**

与第 ① 份相同的两个失效方式，在本份的表现：

- 实例行写进租户独立库，调度器（无租户上下文）查不到它，不允许并发的任务在多实例下失去互斥；管理后台在宿主上下文里也查不到这些历史
- 数据审计 AOP 按属性名 `TenantId` 在插入时改写或校验：租户上下文下预置了不同租户的值会**抛异常**，预置 `null` 会被改写成当前租户

`JobExecutor` 在调用 `SaveJobInstanceAsync` 时不在租户上下文里（租户切换只包住管道执行，`JobExecutor.cs:79-89`），所以当前代码路径碰不到它；但存储不能依赖调用方的这个细节。所有写库都经 `TasksHostClientAccessor`，用例「在租户上下文中保存时以宿主上下文写库」兜住它。

**⑤ 顶替用 `Replace`，不是 `TryAdd`。**

主包 `TryAddSingleton<IJobStore, DefaultJobStore>()`，本包再 `TryAdd` 是空操作：实例与历史继续写进内存，应用以为已经落库。注册用例断言只有一条 `IJobStore` 注册、实现类型是本包的类型。

**⑥ 分页页码从 1 开始。**

`ToPageListAsync(pageNumber, pageSize)` 的 `pageNumber` 从 1 开始，与契约的 `pageIndex` 一致，直接传入。若有人「顺手」写成 `Skip(pageIndex * pageSize)`，第 1 页会跳过前 `pageSize` 条——单条历史的用例看不出来。用例「按开始时间倒序分页返回执行历史」断言前三页各自的内容。

## 6. 测试策略

全部在 SQLite 层，CI 强门禁。本份没有并发语义，不需要真实数据库测试。

夹具沿用第 ① 份的 `TasksTestContext`，建表时追加两个实体、追加 `JobStore` 属性。定时任务的时间来自系统时钟（与 `JobExecutor` 同源），用例以 `DateTimeOffset.UtcNow` 为基准构造数据，**时间差一律留出小时级以上的余量**。

| 分组 | 用例 |
| --- | --- |
| 选项 | 宽限期默认一分钟；从配置节绑定 |
| 实体与映射 | 两张表与两条索引能建出；实例往返字段一致（含 `JobType` 还原、参数为 `JsonElement`、偏移归零）；运行中实例的截止时刻 = 开始 + 超时 + 宽限；超时为 0 或负数时截止时刻为 `UnboundedRunningDeadline`；非运行状态截止时刻为空；参数无法序列化时存为空；无法解析的任务类型不赋值；历史往返字段一致 |
| 实例 | 保存后查回（`+08:00` 输入，瞬时不变）；查不存在返回空；空参数抛异常；重复保存覆盖；终止状态保存时补完成时间；更新为终止状态写完成时间且不再算运行中；更新不存在的实例不抛且不插入；只返回该任务运行中的实例；超过截止时刻不再算运行中；超时关闭的非并发任务在宽限期后仍算运行中且调度器（真实的 `CompositeJobScheduler`）不再触发第二份；任务名空白抛异常；租户上下文下以宿主上下文写库 |
| 历史 | 按开始时间倒序分页（三页）；历史标识为空时自动生成；页码或页大小非法抛异常；清理早于保留期的历史与已结束实例、保留运行中实例；保留天数为负抛异常 |
| 注册 | 定时任务存储被顶替为单例；`ValidateScopes` 下从根容器可解析；第 ① 份的注册用例保持全绿 |

测试平台限制：Microsoft.Testing.Platform，**没有可用的筛选参数**，**不要带 `--logger trx` / `--results-directory`**。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 表只增不减 | 每次执行新增一行实例与一行历史。框架不调用 `CleanupHistoryAsync`，`XiHanJobOptions.HistoryRetentionDays` 也未被读取——应用需自行定期调用 `CleanupHistoryAsync`，否则两张表无限增长 |
| 清理也删除已结束实例 | `CleanupHistoryAsync` 同时删除终止状态且完成时间早于截止的实例。契约只写了「清理历史」，本包扩大到实例，对应 `DefaultJobStore` 对已结束实例的数量淘汰 |
| 运行中实例对所有节点可见 | 多个节点共用一个库时，某节点的运行中实例会让其他节点跳过不允许并发的任务。这是跨节点互斥，与内存存储「只看本进程」不同 |
| 截止时刻依赖协作式超时 | 超时靠取消令牌，任务代码不响应取消时可能在截止时刻之后仍在运行，此时调度器会再触发一份 |
| 遗留记录不改状态 | 超过截止时刻的遗留实例仍标为 `Running`，只是不再阻塞调度 |
| 不限时任务的遗留实例会一直阻塞 | 任务超时小于等于 0 时截止时刻为 `9999-12-31`。不允许并发的这类任务在执行途中崩溃后，遗留实例会一直让该任务的后续触发被跳过（只留警告日志）。清除方法：调用 `IJobStore.UpdateJobStatusAsync(实例标识, JobStatus.Failed)`，或执行 `UPDATE sys_job_instance SET Status = 3 WHERE Basic_Id = '实例标识'`（`3` 为 `JobStatus.Failed`）；遗留实例可按 `Status = 1` 且 `Running_Deadline` 为 `9999-12-31` 查出 |
| 改成 `Running` 不写截止时刻 | `UpdateJobStatusAsync` 只写状态与完成时间，经它把实例改成 `Running` 时截止时刻仍为空，这样的实例不算运行中。框架内的执行器只经 `SaveJobInstanceAsync` 写入 `Running`，不触发 |
| 参数序列化只兜住两类异常 | `NotSupportedException` 与 `JsonException` 存为空；参数对象的属性 getter 抛出的其他异常仍会让保存实例失败、任务不执行 |
| 实例状态字段有限 | `UpdateJobStatusAsync` 只写状态与完成时间；执行器在内存里补的错误信息、耗时不会回写到实例行（它们在历史行里） |
| `JobInfo` 只还原最小快照 | 读回的 `JobInfo` 只有任务名、任务类型（能解析时）、触发类型、租户 |
| 参数读回为 `JsonElement` | 且无法序列化的参数存为 `null` |
| 只落宿主主库 | 与第 ① 份一致 |
| 自定义存储会覆盖本包 | 之后调用 `XiHanJobBuilder.UseStore<T>()` 会按「最后一条生效」覆盖本包 |
| 生命周期保持单例 | `IJobStore` 仍为 `Singleton`，不构成破坏性变更 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败与本包无关）
- 第 ① 份的并发测试在本机配置 `XIHAN_TEST_MYSQL` 后仍通过
- 每个 `.cs` 文件带两行版权声明（`XHFH001`）
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**；判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**关闭
- 包 README 七段结构；文档站条目、侧边栏、`docs/packages/index.md`、两个 `framework/README*.md` 全部更新
- 提交信息中文 Conventional Commits，作用域 `tasks-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手改 `docs/packages/tasks.md`、`docs/guide/`；根 README 只改模块计数
- 四个 README 与文档站（`docs/**/*.md`）里的模块计数、`framework/README*.md` 里的测试工程计数都已按实现时的当前值加一，含两个 shields.io 徽章

## 9. 五个共同问题

| 问题 | 本份的答案 | 依据 |
| --- | --- | --- |
| 分表与否 | **不分表** | `GetJobHistoryAsync` 按任务名跨时间分页，按月分表后需要跨表联合分页；保留期靠 `CleanupHistoryAsync` 按行删除。量大时的对策是索引（§4.2）而不是分表 |
| 主键类型 | **`string`**（`SugarEntity<string>`） | 契约的标识是 `string`（`JobInstance.InstanceId`、`JobHistory.HistoryId`，`GetJobInstanceAsync(string)`、`UpdateJobStatusAsync(string, ...)`） |
| 是否参与工作单元事务 | **不参与**（实际上） | 访问器会把连接登记进环境工作单元，但两个调用方都运行在调度器的定时器回调与 `Task.Run` 里，没有环境工作单元；执行记录也不应随业务回滚 |
| 是否需要多库 | **单库：默认布局的主库** | 调度器与执行器无租户上下文，只看宿主布局 |
| 顶替方式 | **`Replace`** | 主包 `TryAddSingleton`（`XiHanTasksServiceCollectionExtensions.cs:60`） |

## 10. 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改变会影响什么 |
| --- | --- | --- | --- | --- |
| 1 | 陈旧运行中实例的处理 | 截止时刻 = 开始 + 超时 + 宽限，超过即不算运行中（不限时任务除外，见第 10 条） | 不处理则崩溃一次就让不允许并发的任务永久停摆（§5 ①） | 不处理：行为与内存存储「重启即清」背离；改为启动时清理：多节点下会误清别的节点正在跑的实例 |
| 2 | 宽限期默认值 | 1 分钟 | 覆盖保存实例到真正开始执行之间的调度延迟 | 调大：崩溃后恢复调度更慢；调小：长任务在超时边界附近更容易被重复触发 |
| 3 | 时间存储 | UTC 的 `DateTime`，映射层归一 | 避开 `DateTimeOffset` 读回平移（§5 ②） | 用 `DateTimeOffset` 列：读回瞬时可能平移 |
| 4 | 参数序列化失败 | 存 `null`，不抛 | 保持「保存实例从不阻止任务执行」（§5 ③） | 改为抛：带不可序列化参数的任务不再执行 |
| 5 | `CleanupHistoryAsync` 的范围 | 同时删除过期的已结束实例 | 实例表每次执行增加一行，不清理就无限增长；契约里没有别的清理入口 | 只删历史：实例表需应用另写清理 |
| 6 | `JobInfo` 还原范围 | 任务名、任务类型、触发类型、租户 | 任务定义来自代码注册；框架内没有调用方读取实例的 `JobInfo` | 完整还原需要把整个 `JobInfo` 序列化落库，含 `RetryPolicy` 等对象 |
| 7 | 历史与实例的保存语义 | 插入或更新（先查后写） | 与 `DefaultJobStore` 一致 | 改为只插入：重复保存同一标识抛主键冲突 |
| 8 | 建两条索引 | 实例（任务名 + 状态）、历史（任务名 + 开始时间降序） | 两个查询方法各自的过滤与排序列 | 不建：表大后调度器每次触发前的查询变慢 |
| 9 | 根 `README.md` / `README_cn.md` | **只改模块计数，不往「常用包」表加行**（派工者对八个包统一的裁定） | 「常用包」是精选清单，另两个 SqlSugar 包都不在里面；逐包清单在 `framework/README*.md` | 若要加行，应同时补上另两个 SqlSugar 包，那是另一件事 |
| 10 | 不限时任务（超时 `<= 0`）的运行截止时刻 | `9999-12-31`：在被显式结束之前一直算运行中（派工者裁定） | 配置为不允许并发的任务绝不能并发执行，这优先于可用性；不限时任务没有可推导的运行上限 | 改为有界截止时刻（例如开始 + 宽限期，或另设一个「不限时任务的最长运行时间」选项）：崩溃后能自动恢复调度，但不限时的长任务一旦跑过该上限，调度器会再触发一份、同一任务并发两份，且不报错 |
| 11 | `docs/packages/index.md` | **要改** | 它是文档站的模块清单，另两个 SqlSugar 包都在里面；拆分方案与 Linear 议题列出的六处漏了它 | 不改则文档站总览里找不到本包 |
| 12 | 测试不需要真实数据库 | 是 | 本份没有并发语义；跨节点互斥靠的是查询结果，不靠原子更新；调度器不再重复触发由 SQLite 层的真实 `CompositeJobScheduler` 用例覆盖 | 无 |
| 13 | 模块计数的改法 | 实现时 `grep` 当前值、每处加一，不在文档里写死「68 → 69」 | 八个 SqlSugar 包先后落地，写死的数字只对第一个包成立 | 写死则后落地的包会把计数改错 |

## 11. 下一份

无。本份完成后 `Tasks.SqlSugar` 两份计划全部完成，PR 可提交。PR 描述需包含：两份 spec 的已知边界、第 ① 份反向验证的两个数字、「生命周期保持单例、无破坏性变更」的说明。
