# Tasks.SqlSugar ①：包骨架与后台作业存储 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-tasks-sqlsugar-1-background-jobs.md`
- **前置**：无代码前置。`XiHan.Framework.Data` 的 P5 能力（`ISqlSugarClientResolver.GetCurrentLayoutConfigIds()` 等）已在 `dev` 上
- **所属 PR**：`Tasks.SqlSugar` 单独一个 PR，由本份与第 ② 份（`.superpowers/specs/2026-09-28-tasks-sqlsugar-2-scheduled-jobs-design.md`）共同组成；第 ② 份完成后方可提交
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-8
- **系列**：`Tasks.SqlSugar` 共 2 份。① 包骨架 + `IBackgroundJobStore`；② `IJobStore` + 文档站收尾

> 本文档**自成一体**。实现本份所需的全部约束都写在这里，不需要去读发件箱的 spec。凡是从发件箱沿用的结论，都在本文重复了一遍并注明出处，便于核对。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的地方是领取（`GetWaitingJobsAsync`）。** 第 5 节第 ① 条错了**不会报错、SQLite 测试照样全绿**——SQLite 的写锁是库级串行化，两个领取者天然不会交错。唯一能证明领取正确的是第 6 节的真实 MySQL 并发测试，并且要做一次「拿掉可领取条件后测试变红」的反向验证。
>
> 第二容易出错的是**租户上下文**（第 5 节第 ⑤ 条）：错了在 SQLite 测试里同样全绿，因为测试桩不挂框架的数据审计 AOP，也不存在租户独立库。

---

## 1. 背景与目标

### 1.1 现状

`XiHan.Framework.Tasks` 的后台作业由三方协作：

| 角色 | 位置 | 与存储的交互 |
| --- | --- | --- |
| 入队 | `BackgroundJobs/BackgroundJobManager.cs:52-78` | `InsertAsync`，`TenantId` 取入队时的当前租户（`:64`） |
| 轮询执行 | `BackgroundJobs/BackgroundJobWorker.cs:96-154` | 抢分布式锁（`:98`）→ `GetWaitingJobsAsync`（`:113`）→ 逐个执行 → 成功 `DeleteAsync`（`:215`）/ 失败或放弃 `UpdateAsync`（`:266`） |
| 存储 | `BackgroundJobs/Abstractions/IBackgroundJobStore.cs` | 5 个方法 |

默认存储 `DefaultBackgroundJobStore` 是进程内字典：重启即丢、不跨实例。主包另带一个 `RedisBackgroundJobStore`，需要 Redis。

**注册方式**：`XiHanTasksModule.PreConfigureServices`（`XiHanTasksModule.cs:36`）调 `AddXiHanBackgroundJobs`，其中 `services.TryAddSingleton<IBackgroundJobStore, DefaultBackgroundJobStore>()`（`BackgroundJobs/Extensions/DependencyInjection/XiHanBackgroundJobsServiceCollectionExtensions.cs:32`）。Redis 版用的是 `services.Replace(...)`（同文件 `:62`）。

**消费方的生命周期**：`BackgroundJobManager` 注册为 `Transient` 且构造函数注入存储；`BackgroundJobWorker` 从每轮新建的作用域解析存储（`BackgroundJobWorker.cs:109-111`）。

### 1.2 契约语义（从三个现有实现与 Worker 反推）

| 方法 | 语义 | 依据 |
| --- | --- | --- |
| `FindAsync(Guid)` | 按标识查，不存在返回 `null`；已放弃的作业也能查到 | Redis 版在放弃后仍保留作业体 |
| `InsertAsync(BackgroundJobInfo)` | 插入，`null` 抛 `ArgumentNullException` | 三个实现一致 |
| `GetWaitingJobsAsync(string?, int)` | 过滤「应用名序数相等 且 未放弃 且 `NextTryTime <= 当前时间`」，排序「`Priority` 降序、`TryCount` 升序、`NextTryTime` 升序」，取前 `maxResultCount` 条 | 接口 XML 注释 `IBackgroundJobStore.cs:34-37`；`DefaultBackgroundJobStore.cs:62-74` |
| `DeleteAsync(Guid)` | 执行成功后删除，幂等 | Worker `:215` |
| `UpdateAsync(BackgroundJobInfo)` | 失败退避或放弃后回写 `TryCount`、`LastTryTime`、`NextTryTime`、`IsAbandoned` | Worker `:192-240` |

**应用名的实际语义**：接口参数注释写「为空表示不区分」（`IBackgroundJobStore.cs:38`），但 `Default` 与 `Redis` 两个实现都用 `string.Equals(x.ApplicationName, applicationName, StringComparison.Ordinal)`——`null` 只匹配 `null`，不是「不区分」。Worker 与 Manager 用的是同一个 `BackgroundJobWorkerOptions.ApplicationName`，两端一致，所以按实现行为走。本份**以实现行为为准**。

**「当前时间」的来源**：Manager 用 `IClock.Now` 写 `NextTryTime`（`BackgroundJobManager.cs:63,73`），Default 用 `IClock.Now` 比较（`DefaultBackgroundJobStore.cs:64`）。`IClock.Now` 在默认配置下是**本地时间**：`XiHanClockOptions.Kind` 默认 `Unspecified`，`Clock.Now` 仅在 `Kind == Utc` 时返回 `DateTime.UtcNow`，否则返回 `DateTime.Now`（`XiHan.Framework.Timing/Clock.cs:33`）。

### 1.3 领取的并发问题

接口注释要求「在 `GetWaitingJobsAsync` 中做原子领取以避免多实例重复执行」（`IBackgroundJobStore.cs:12-13`）。Redis 版的注释则说「因框架 `BackgroundJobWorker` 已用分布式锁保证单活，本存储的领取无需再做原子租约」（`RedisBackgroundJobStore.cs:19`）。

后者对 Redis 版成立，对本包不成立：

- Worker 的锁是 `IDistributedLock`，默认注册 `DefaultDistributedLock`——**仅在当前进程内互斥，不跨实例**（`XiHan.Framework.Caching/Distributed/DefaultDistributedLock.cs:10-13`）。只有配置了 Redis 连接串，Caching 才把它 `Replace` 成 `RedisDistributedLock`（`XiHanCachingServiceCollectionExtensions.cs:87-94`）
- 选数据库作业存储的应用，恰恰很可能**没有** Redis。此时多实例部署下 Worker 的锁形同虚设，N 个实例同时领到同一批作业

因此本份在存储内做原子领取，**不依赖**分布式锁。锁仍然有用（减少无谓的竞争），但正确性不押在它上面。

这与发件箱是同一个并发问题：多实例轮询、租约、超时释放。发件箱的三步抢占协议已被真实 MySQL 并发测试验证过（200 条 × 8 个并发领取者零重复；拿掉可领取条件后重复成 850 次），本份直接沿用。

### 1.4 要交付什么

1. 新包 `XiHan.Framework.Tasks.SqlSugar`（骨架、模块、注册扩展、选项、README、slnx）
2. 实体 `SysBackgroundJob`（表 `sys_background_job`）与双向映射
3. `TasksHostClientAccessor`：在宿主上下文取默认布局主库客户端的访问器，第 ② 份复用
4. `SqlSugarBackgroundJobStore`：`IBackgroundJobStore` 的 5 个方法，其中领取用三步抢占 + 租约
5. 以 `Replace` 顶替主包的默认存储
6. SQLite 单元测试 + 真实 MySQL 并发测试

### 1.5 成功标准

1. `DependsOn(typeof(XiHanTasksSqlSugarModule))` 后，容器里的 `IBackgroundJobStore` 是 `SqlSugarBackgroundJobStore`，生命周期仍为 `Singleton`
2. 以 `ValidateScopes = true` 构建容器、从根作用域解析 `IBackgroundJobStore` 不抛异常（无被捕获的作用域依赖）
3. `GetWaitingJobsAsync` 的过滤、排序、限量与 1.2 节的表一致
4. 已领取且租约未过期的作业不会被再次领取；租约过期后可再次领取
5. `UpdateAsync` 释放租约：作业在新的 `NextTryTime` 到期后即可再次领取，不必等租约过期
6. 在租户上下文里入队时，写库期间处于宿主上下文，且作业行的 `Tenant_Id` 保留入队时的租户
7. 本机设置 `XIHAN_TEST_MYSQL` 后，200 个作业 × 8 个并发领取者，领到的标识无重复、总数恰为 200；把抢占 `WHERE` 里的可领取条件拿掉后该测试变红
8. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 被替换的契约与它的消费方 —— 最高优先**

```
framework/src/XiHan.Framework.Tasks/BackgroundJobs/
  Abstractions/IBackgroundJobStore.cs        要实现的 5 个方法
  Models/BackgroundJobInfo.cs                存储边界的传输模型（11 个字段）
  Models/BackgroundJobPriority.cs            byte 枚举，值越大越优先
  DefaultBackgroundJobStore.cs               被替换的实现；过滤与排序以它为准
  RedisBackgroundJobStore.cs                 另一个持久化实现；放弃后保留作业体的做法以它为准
  BackgroundJobWorker.cs:96-154,181-272      调用顺序：抢锁 → 领取 → 执行 → 删除 / 更新
  BackgroundJobManager.cs:52-78              入队：TenantId 取当前租户，时间取 IClock.Now
  Extensions/DependencyInjection/XiHanBackgroundJobsServiceCollectionExtensions.cs:32,62
framework/src/XiHan.Framework.Tasks/XiHanTasksModule.cs:36   PreConfigureServices 里注册后台作业
```

**② 发件箱 —— 三步抢占协议的已验证实现**

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  Outbox/SqlSugarEventOutbox.cs:155-208      ClaimFromDatabaseAsync：选候选 → 条件 UPDATE → 按令牌取回，含三轮重试
  Entities/SysEventOutbox.cs                 实体写法：SugarEntity<Guid>、列名 Pascal_Snake、两个构造函数
  Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs   Replace 的写法
framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxConcurrencyTests.cs                  真实数据库并发测试的写法
  OutboxTestContext.cs                       SQLite 临时库夹具与桩解析器
```

**③ 数据层 —— 客户端解析与审计 AOP**

```
framework/src/XiHan.Framework.Data/
  SqlSugar/Clients/ISqlSugarClientResolver.cs        GetCurrentClient 等
  SqlSugar/Clients/SqlSugarClientResolver.cs:79-117  GetCurrentClient 按当前租户解析布局；:256-330 登记进环境工作单元
  SqlSugar/Auditing/SqlSugarDataExecutingHandler.cs  DataExecuting AOP：插入时按属性名填审计字段与 TenantId
  SqlSugar/Extensions/EntityAuditExtensions.cs:51-68,172-193   按属性名匹配；SetTenantIdValue 的三种分支
  Extensions/DependencyInjection/XiHanDataServiceCollectionExtensions.cs:61   ISqlSugarClientResolver 注册为 Scoped
framework/src/XiHan.Framework.Uow/UnitOfWorkManager.cs、AmbientUnitOfWork.cs   单例 + AsyncLocal 环境工作单元
framework/src/XiHan.Framework.MultiTenancy/CurrentTenant.cs、AsyncLocalCurrentTenantAccessor.cs
```

**④ SqlSugar 源码 —— API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs                          Where / OrderBy / Take / Select / ToListAsync / FirstAsync
  Abstract/QueryableProvider/QueryableProvider.cs:1355-1370   多次 OrderBy 按调用顺序追加
  Abstract/QueryableProvider/QueryableExecuteSqlAsync.cs:82-104   FirstAsync 无记录返回 default
  Interface/IUpdateable.cs                         SetColumns / Where / ExecuteCommandAsync
  Abstract/UpdateProvider/UpdateableProvider.cs    条件更新；IsEnableUpdateVersionValidation 仅显式开启才校验版本
  Interface/IDeleteable.cs、Interface/Insertable.cs
  Entities/Mapping/SugarMappingAttribute.cs:346-362   SugarIndexAttribute 构造函数
  Abstract/CodeFirstProvider/CodeFirstProvider.cs:377-392   建索引时按属性名找列
```

> 树与目录名：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`，权威源码是 `Src/Asp.NetCore2/`，**不是** `Src/Asp.Net/`（那是 .NET Framework 变体）。更新提供者的目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 条件更新、`SetColumns` | `更新數據.md` |
| 建表与索引 | `库表管理、数据库表操作方法、 Show tables 获取表结构、索引.md`、`實體管理EntityMaintenance.md` |
| 并发与线程安全 | `偶發性錯誤與執行緒安全.md` |
| 事务参与 | `事务用法.md`、`UnitOfWork工作單元.md` |

仓库自身文档：`docs/packages/tasks.md`、`docs/packages/data.md`、`docs/packages/eventbus-sqlsugar.md`（同一套落库范式）。

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

- **不改 `XiHan.Framework.Tasks` 的任何文件**。不改 `IBackgroundJobStore`、`BackgroundJobInfo`、`BackgroundJobWorker`、`BackgroundJobManager`
- **不用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP`** 或任何方言相关 SQL。本框架是通用类库
- **抢占步骤不许只按主键更新**。见第 5 节第 ①
- **不自建 `SqlSugarClient`，不把 `ISqlSugarClientResolver` 注入单例字段**。见第 5 节第 ⑥
- **不用 `DateTime.UtcNow` / `DateTime.Now` 作为领取的「当前时间」**，一律 `IClock.Now`。见第 5 节第 ③
- **不给实体加 `[SplitTable]`**，不加 `[TableInitialization(IncludeModuleConnections = true)]`
- **不把实体属性命名为 `CreatedTime`、`ModifiedTime`、`IsDeleted` 等审计保留名**。见第 5 节第 ⑦
- **不做 `IJobStore`**（第 ② 份），**不改 `docs/`、根 README、`framework/README*.md`**（第 ② 份收尾）

## 3. 非目标

- **不遍历模块库与租户独立库**。作业只落默认布局的主库，见 §4.7 与「待确认的决策」
- **不做放弃作业的自动清理**。放弃的作业保留在表里供排查，清理由应用自行处理
- **不保证恰好一次执行**。至少一次是租约模式的固有语义，作业处理器需幂等
- **不改 Worker 的轮询节奏、批量大小或锁逻辑**
- **不引入 `CancellationToken`**。契约的 5 个方法都没有该参数

## 4. 设计

### 4.1 包结构

```
framework/src/XiHan.Framework.Tasks.SqlSugar/
  XiHan.Framework.Tasks.SqlSugar.csproj
  XiHanTasksSqlSugarModule.cs
  README.md
  Options/XiHanTasksSqlSugarOptions.cs
  Clients/TasksHostClientAccessor.cs
  Entities/SysBackgroundJob.cs
  Mapping/BackgroundJobMapper.cs
  BackgroundJobs/SqlSugarBackgroundJobStore.cs
  Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs
```

csproj 按序 Import `netcore` / `common` / `version` / `nuget` 四个 props，`ProjectReference` 指向 `XiHan.Framework.Tasks` 与 `XiHan.Framework.Data`，**不加任何 `PackageReference`**（SqlSugar 经 `Data` 传递引入）。

模块类 `XiHanTasksSqlSugarModule` 依赖 `XiHanTasksModule` 与 `XiHanDataModule`，`ConfigureServices` 里只调一行 `services.AddXiHanTasksSqlSugar(services.GetConfiguration())`。

### 4.2 选项

```csharp
public class XiHanTasksSqlSugarOptions
{
    public const string SectionName = "XiHan:Tasks:SqlSugar";

    public TimeSpan BackgroundJobLeaseTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
```

第 ② 份会在同一个类里追加一个属性。

### 4.3 实体 `SysBackgroundJob`

表 `sys_background_job`，继承 `SugarEntity<Guid>`（主键列 `Basic_Id`，取 `BackgroundJobInfo.Id`；基类另带 `Row_Version`）。

| 属性 | 列 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- | --- |
| `ApplicationName` | `Application_Name` | `string(128)` | 否 | `null` 存为空字符串，见 §4.4 |
| `TenantId` | `Tenant_Id` | `long?` | 是 | 入队时的租户 |
| `JobName` | `Job_Name` | `string(256)` | 否 | |
| `JobArgs` | `Job_Args` | 大文本（`StaticConfig.CodeFirst_BigString`） | 否 | 序列化后的参数 JSON |
| `TryCount` | `Try_Count` | `short` | 否 | |
| `CreationTime` | `Creation_Time` | `DateTime` | 否 | 放弃判定的基准 |
| `NextTryTime` | `Next_Try_Time` | `DateTime` | 否 | |
| `LastTryTime` | `Last_Try_Time` | `DateTime?` | 是 | |
| `IsAbandoned` | `Is_Abandoned` | `bool` | 否 | |
| `Priority` | `Priority` | `int` | 否 | `(int)BackgroundJobPriority` |
| `ClaimToken` | `Claim_Token` | `string(64)` | 是 | 领取令牌 |
| `ClaimTime` | `Claim_Time` | `DateTime?` | 是 | 领取时刻，`IClock.Now` 口径 |

两条索引（`SugarIndex` 的字段名是**属性名**，建索引时由 `CodeFirstProvider.cs:383` 按 `PropertyName` 找列）：

- `idx_sys_background_job_waiting`：`ApplicationName` 升序、`IsAbandoned` 升序、`NextTryTime` 升序——领取的候选查询
- `idx_sys_background_job_claim_token`：`ClaimToken` 升序——按令牌取回

**时间不做 UTC 归一**。`BackgroundJobInfo` 的时间全部来自 `IClock.Now`，写入与比较用同一个时钟，按原值存取即可。读回的 `DateTime.Kind` 可能变成 `Unspecified`，Worker 只对这些时间做减法与比较，不受影响。

**`Priority` 存 `int` 而不是枚举**：避开 SqlSugar 对 `byte` 枚举的映射细节，排序语义不变。

### 4.4 映射 `BackgroundJobMapper`

静态类，三个公开方法：

- `SysBackgroundJob ToEntity(BackgroundJobInfo info)`：逐字段复制；`ApplicationName` 经 `ToApplicationKey` 转换；`ClaimToken`、`ClaimTime` **一律置 `null`**
- `BackgroundJobInfo ToJobInfo(SysBackgroundJob entity)`：逐字段复制；空字符串的 `ApplicationName` 还原为 `null`
- `string ToApplicationKey(string? applicationName)`：`applicationName ?? string.Empty`

**为什么把 `null` 存成空字符串**：领取的过滤条件要在两个地方写两遍（候选查询与抢占 `UPDATE`），若应用名可为 `null`，每处都要分「`IS NULL`」与「`= @p`」两支。统一成非空字符串后，两处都是一个 `item.ApplicationName == applicationKey`，不依赖 SqlSugar 对「与 `null` 变量比较」的翻译行为。代价见 §7「空字符串应用名」。

`ToEntity` 清空租约，这是 §4.6 `UpdateAsync` 释放租约的实现手段。

### 4.5 访问器 `TasksHostClientAccessor`

```csharp
public sealed class TasksHostClientAccessor
{
    public TasksHostClientAccessor(IServiceScopeFactory scopeFactory, ICurrentTenant currentTenant);

    public Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation);
    public Task ExecuteAsync(Func<ISqlSugarClient, Task> operation);
}
```

每次调用：

1. `_scopeFactory.CreateScope()` 新建作用域
2. `using (_currentTenant.Change(null))` 切到宿主上下文，**覆盖整个操作**，不只是解析客户端那一行
3. 从作用域解析 `ISqlSugarClientResolver`，调 `GetCurrentClient()`
4. 执行 `operation(client)` 并 `await`
5. 退出 `using` 恢复原租户，释放作用域

**为什么是单例 + 每次操作一个作用域**，而不是像发件箱那样把存储注册成 `Scoped`：

- `IBackgroundJobStore` 原本是 `Singleton`，下游可能有单例构造函数注入 `IBackgroundJobManager`（`Transient`，内含存储）。改成 `Scoped` 会让这些单例在开发环境 `ValidateScopes` 下启动即抛——这是一个破坏性变更，没有必要
- 第 ② 份的 `IJobStore` **必须**是单例（消费方 `JobExecutor`、`CompositeJobScheduler` 都是单例且构造函数注入它），两份共用同一个访问器更一致
- 工作单元参与不受影响：`UnitOfWorkManager` 与 `AmbientUnitOfWork` 都是单例、环境工作单元存在 `AsyncLocal` 里（`AmbientUnitOfWork.cs:15-22`），新作用域里解析出的 `SqlSugarClientResolver` 看到的是调用方所在异步流的同一个环境工作单元，`GetCurrentClient()` 照样把连接登记进去（`SqlSugarClientResolver.cs:256-330`）

**为什么强制宿主上下文**：见第 5 节第 ⑤。

### 4.6 存储 `SqlSugarBackgroundJobStore`

构造函数：`(TasksHostClientAccessor clientAccessor, IClock clock, IOptions<XiHanTasksSqlSugarOptions> options)`。每个方法都经 `_clientAccessor.ExecuteAsync(...)` 取客户端。

| 方法 | 实现 |
| --- | --- |
| `FindAsync(Guid jobId)` | `Queryable<SysBackgroundJob>().Where(BasicId == jobId).FirstAsync()`，`null` 则返回 `null`，否则映射 |
| `InsertAsync(BackgroundJobInfo)` | `ThrowIfNull`；`Insertable(ToEntity(info)).ExecuteCommandAsync()`。主键重复时数据库抛异常，不覆盖 |
| `GetWaitingJobsAsync(string?, int)` | `maxResultCount <= 0` 直接返回空；否则三步抢占，见 §4.7 |
| `DeleteAsync(Guid)` | `Deleteable<SysBackgroundJob>().Where(BasicId == jobId).ExecuteCommandAsync()`，删除 0 行不报错 |
| `UpdateAsync(BackgroundJobInfo)` | `ThrowIfNull`；`Updateable(ToEntity(info)).ExecuteCommandAsync()`。按主键更新全部列，`Claim_Token`、`Claim_Time` 随之置空；**不存在的作业不插入** |

放弃（`IsAbandoned == true`）的作业走同一条 `UpdateAsync`：行保留、标记放弃，领取条件据此排除它。与 `Redis` 版一致（保留作业体供排查），与 `Default` 版不同（`Default` 直接移除）。

`Updateable(entity)` 不会触发版本校验：`SugarEntity<TKey>.RowVersion` 标了 `IsEnableUpdateVersionValidation = true`，但 SqlSugar 只在显式调用 `IsEnableUpdateVersionValidation()` 或 `ExecuteCommandWithOptLock(true)` 时才校验（`UpdateableProvider.cs:78-90,399-402`）。

### 4.7 领取：三步抢占 + 租约

「可领取」的定义（候选查询与抢占 `UPDATE` 的 `WHERE` 里**完全相同地写两遍**）：

```
Application_Name = applicationKey
AND Is_Abandoned = false
AND Next_Try_Time <= now
AND (Claim_Time IS NULL OR Claim_Time < now - BackgroundJobLeaseTimeout)
```

结构（与 `SqlSugarEventOutbox.ClaimFromDatabaseAsync` 同构）：

```
if maxResultCount <= 0: return []
applicationKey = ToApplicationKey(applicationName)

在 _clientAccessor.ExecuteAsync 内：
  for attempt in 0..2:
      now               = _clock.Now
      leaseExpiredBefore = now - _options.BackgroundJobLeaseTimeout
      claimToken        = Guid.NewGuid().ToString("N")          ← 每轮新建

      ① 选候选：Queryable.Where(可领取).OrderBy(Priority 降, TryCount 升, NextTryTime 升)
                .Take(maxResultCount).Select(BasicId)
         候选为空 → return []

      ② 抢占：Updateable<SysBackgroundJob>()
                .SetColumns(ClaimToken = claimToken, ClaimTime = now)
                .Where(candidateIds.Contains(BasicId) AND 可领取)     ← 两者缺一不可
         affected == 0 → continue（候选全被别人抢走，另选一批）

      ③ 取回：Queryable.Where(ClaimToken == claimToken)
                .OrderBy(Priority 降, TryCount 升, NextTryTime 升)
         return 映射结果

  return []
```

**正确性来自第 ② 步**：每行的 `UPDATE` 是原子的，两个并发领取者中只有一个能让某行在「可领取」状态下被改写；输给对手的行不带本方令牌，第 ③ 步自然取不到。

**租约**：领取不删除、不改 `NextTryTime`，只盖令牌与时刻。之后三条路径释放它：

| 路径 | 结果 |
| --- | --- |
| 执行成功 → `DeleteAsync` | 行被删除 |
| 执行失败或放弃 → `UpdateAsync` | `ToEntity` 把租约置空，作业在新的 `NextTryTime` 到期后可再次领取；放弃的作业因 `Is_Abandoned` 被排除 |
| 进程在执行中退出 | 租约在 `BackgroundJobLeaseTimeout` 后过期，作业重新可领取 |

**为什么不借 `NextTryTime` 做租约**（把领取时的 `NextTryTime` 推后 `LeaseTimeout`，省掉两列）：那会改写契约模型里一个有业务含义的字段，`FindAsync` 读到的「下次执行时间」会变成租约到期时刻；而且领取后返回给 Worker 的对象携带的是被改写过的值。独立两列与发件箱一致，语义不互相污染。

**令牌长度**：`Guid.NewGuid().ToString("N")` 为 32 字符，与 `Claim_Token` 列长 64 相容。

### 4.8 注册

```csharp
public static IServiceCollection AddXiHanTasksSqlSugar(this IServiceCollection services, IConfiguration configuration)
{
    services.Configure<XiHanTasksSqlSugarOptions>(configuration.GetSection(XiHanTasksSqlSugarOptions.SectionName));
    services.TryAddSingleton<TasksHostClientAccessor>();
    services.Replace(ServiceDescriptor.Singleton<IBackgroundJobStore, SqlSugarBackgroundJobStore>());
    return services;
}
```

主包用 `TryAddSingleton` 注册默认存储（`XiHanBackgroundJobsServiceCollectionExtensions.cs:32`），因此本包**必须**用 `Replace`——`TryAdd` 是空操作。

执行顺序：主包在 `PreConfigureServices` 注册（`XiHanTasksModule.cs:36`），本模块依赖 `XiHanTasksModule`，其 `ConfigureServices` 在所有模块的 `PreConfigureServices` 之后执行，`Replace` 能找到并替换那条注册。

应用若之后再调 `UseRedisBackgroundJobStore()`，Redis 版会反过来顶替本包——后调用者赢，这是预期行为，写进 README。

## 5. 会静默失效的陷阱

以下每一条错了都**不会报错**，且除特别注明外 **SQLite 单元测试照样全绿**。

**① 抢占的 `WHERE` 必须同时含候选主键集合与可领取条件。**

只按主键更新（`.Where(item => candidateIds.Contains(item.BasicId))`）时，两个并发领取者 A、B 选到同一批候选：A 盖令牌、A 取回；随后 B 的 `UPDATE` 不受条件约束，把 A 的令牌覆盖成 B 的，B 再取回——**同一个作业被执行两次**。不报错，单线程测试全绿，因为单线程下不存在「选完候选之后、盖令牌之前」被别人插队的窗口。

SQLite 也证明不了：它的写锁是库级串行化，两个连接的 `UPDATE` 天然不交错。**唯一的证明是第 6 节的真实 MySQL 并发测试，外加一次「拿掉可领取条件后测试变红」的反向验证**——不做反向验证，就无法区分「协议正确」与「测试在空转」。发件箱做这个验证时，拿掉条件后 200 条事件被领取了 850 次。

**② 令牌每轮新建、按库生成，不跨库、不跨调用共用。**

令牌必须在重试循环**体内**、每轮 `Guid.NewGuid()` 一次。两种错误写法都会让第 ③ 步「按令牌取回」捞到不属于本轮的行：

- **跨调用共用**（比如把令牌存在存储实例的字段里——本存储是单例，所有调用共享同一个实例）：上一次领取、尚未删除或更新的作业仍带着同一个令牌，下一次取回会把它们连同新行一起返回，**同一作业在两批里各出现一次**
- **跨库共用**：本份只写一个库，但若日后有人把领取扩展成遍历 `GetCurrentLayoutConfigIds()`，切记该方法去重的是**标识名而不是连接串**——两个 `ConfigId` 可以指向同一个物理库。发件箱第一版就是「所有库共用一个令牌」，第二轮取回把第一轮已领的行按同一令牌又查了回来，同一事件在一批里出现两次（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p6-outbox-multi-database-design.md` §4.3「修订（实现阶段）」）

**③ 领取的「当前时间」必须是 `IClock.Now`，不是 `DateTime.UtcNow`。**

入队时 `NextTryTime` 由 `IClock.Now` 写入，默认配置下它是**本地时间**（§1.2）。领取若用 `DateTime.UtcNow` 比较，在 UTC+8 的机器上「当前时间」比作业的时间早 8 小时，**所有作业都要晚 8 小时才被执行**。不报错，而且在 UTC 时区的 CI 机器上完全看不出来。

测试的防线：夹具的 `FakeClock` 固定在 **2030 年**，作业时间也围绕 2030 年构造。一个误用 `DateTime.UtcNow` 的实现在这组数据上什么都领不到，测试立即变红。

租约时刻 `Claim_Time` 同理，必须与 `NextTryTime` 同一个时钟口径。

**④ `UpdateAsync` 必须释放租约。**

若 `UpdateAsync` 只更新业务字段而保留 `Claim_Token` / `Claim_Time`，失败的作业要等**租约过期**（默认 5 分钟）才能重试，而不是等**退避时间**（首次失败默认 60 秒，`BackgroundJobWorkerOptions.DefaultFirstWaitDurationSeconds`）。退避策略静默失效，不报错。

本设计靠 `ToEntity` 把租约列置空、`UpdateAsync` 用 `Updateable(entity)` 整行更新来实现。若有人把 `UpdateAsync` 改成 `SetColumns` 只列业务字段，就会踩中。测试「更新后释放租约」用「租约 5 分钟、退避 2 分钟」的组合兜住它。

**⑤ 写库必须处于宿主上下文，并且覆盖整个操作。**

两个独立的失效方式：

- **租户独立库**：`GetCurrentClient()` 按**当前租户**解析布局（`SqlSugarClientResolver.cs:102-117`）。Manager 在业务请求里入队，处于租户上下文；若该租户配置了独立库，作业行就写进**租户库**。而 Worker 的轮询作用域没有租户上下文，只看**宿主主库**——那条作业**永远不会被执行**
- **数据审计 AOP 按属性名改写 `TenantId`**：框架的 `DataExecuting` 处理器在插入时按**属性名** `TenantId` 匹配（`EntityAuditExtensions.cs:51-58`，不看实体是否实现 `IMultiTenantEntity`）。租户上下文下：实体值为默认值时被改写成当前租户；实体预置了与当前租户不同的值时**抛异常**（`EntityAuditExtensions.cs:172-193`）。平台态（`TenantId` 为 `null`）则保留实体预置值。该处理器在 SQL 执行时读取当前租户，所以 `Change(null)` 必须包住 `ExecuteCommandAsync`，不能只包住 `GetCurrentClient()`

SQLite 测试桩既没有租户独立库，也没挂框架的 AOP，两种失效都不会在单元测试里出现。夹具因此自己挂一个 `Aop.DataExecuting` 钩子，**记录 SQL 执行那一刻的当前租户**，用例断言它是 `null`。

**⑥ 不能自建 `SqlSugarClient`，不能把 `ISqlSugarClientResolver` 注入单例字段。**

- 自建客户端或另开连接：入队不在业务事务里——业务回滚后作业仍在，执行一个根本没发生的业务的后续动作；业务提交而入队失败时作业丢失。单元测试里没有真正的事务，照样全绿
- 把 `Scoped` 的解析器注入单例存储：开发环境 `ValidateScopes` 下启动即抛；生产环境则静默地从根作用域取一个解析器永久持有。访问器每次操作都从新作用域解析，正是为了避开它。成功标准 2 的测试（`ValidateScopes = true` 从根作用域解析存储）兜住这一条

**⑦ 实体属性不能取审计 AOP 的保留名。**

`DataExecuting` 按属性名填值，不看实体实现了什么接口：

- `CreatedTime`：插入时若为默认值，被填成 `DateTime.UtcNow`（UTC 口径）
- `ModifiedTime` / `ModifiedId` / `ModifiedBy`：按对象更新时**无条件覆盖**（`EntityAuditExtensions.cs:76-84`，`overrideHandleValue: true`）
- `IsDeleted`：插入时被置为 `false`
- `TenantId`：见第 ⑤ 条

本实体的时间是 `IClock.Now` 口径（默认本地时间）。若把 `CreationTime`「顺手统一命名」为 `CreatedTime`，调用方漏填的创建时间不再是明显错误的 `0001-01-01`，而是被悄悄填成 UTC 时间，与同一行里本地口径的 `NextTryTime` 相差 8 小时，放弃判定随之偏移。`UpdateAsync` 走按对象更新，任何名为 `ModifiedTime` 的属性每次都会被覆盖。**保持 `CreationTime` 这个名字**，不要新增上述保留名的属性。

**⑧ 顶替用 `Replace`，不是 `TryAdd`。**

主包已 `TryAddSingleton<IBackgroundJobStore, DefaultBackgroundJobStore>()`，本包再 `TryAdd` 是空操作：默认内存存储留在容器里，作业写进内存，**进程一重启全部丢失**，而应用以为自己已经落库。注册测试断言容器里只有一条 `IBackgroundJobStore` 注册、实现类型是本包的类型。

## 6. 测试策略

测试项目 `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/`，Import `netcore.props`、`common.props`、`test.props` 三个，不 Import `version.props`、不设 `AssemblyName`。xunit.v3 + Microsoft.Testing.Platform：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。

### 6.1 第一层 —— SQLite，CI 强门禁

夹具：临时 `.db` 文件（连接串带 `Pooling=False`）、`FakeClock`（固定 2030-01-01 UTC）、`CurrentTenant`（基于 `AsyncLocalCurrentTenantAccessor.Instance`）、记录租户的桩解析器、真实的 `ServiceCollection` 提供作用域工厂、`Aop.DataExecuting` 钩子记录执行时租户。

必测：

| 分组 | 用例 |
| --- | --- |
| 选项 | 默认租约 5 分钟；从 `XiHan:Tasks:SqlSugar` 绑定 |
| 实体与映射 | 表与两条索引能建出；契约 ↔ 实体往返字段一致；`null` 应用名存为空字符串并还原；`ToEntity` 清空租约 |
| 访问器 | 在租户上下文中调用时，操作期间当前租户为 `null`；有返回值的操作把结果交回调用方；每次操作新建作用域解析客户端解析器 |
| 增删改查 | 插入后查回字段一致；查不存在返回 `null`；`null` 参数抛 `ArgumentNullException`；删除后查不到且重复删除不抛；更新覆盖字段；放弃的作业保留并标记；更新不存在的作业不会插入；在租户上下文入队时 SQL 执行期间处于宿主上下文且保留作业租户 |
| 领取 | 只领到期、未放弃、应用名匹配的；`null` 应用名只匹配 `null`；排序；限量；`maxResultCount` 为 0 返回空；已领取未过期的不再被领；租约过期后可再领；更新后释放租约；以注入时钟为准；删除后不再被领 |
| 注册 | 顶替后只有一条注册且为单例；访问器为单例；`ValidateScopes` 下从根作用域可解析 |

写用例的两条约束（来自发件箱的实测）：

- **不写「读回的时间等于写入的时间」这类精确断言**。SQLite 的 `datetime` 列按文本存取，读回的 `Kind` 与小数位可能与写入时不同。需要比较时间时用「相差小于 1 秒」
- **不写贴着边界的断言**（例如 `NextTryTime` 恰好等于当前时间）。批量写入的字面量与参数化写入的小数位格式不同，边界上的文本比较结果不可靠。用例一律留出分钟级的余量

**「候选被抢光则另选一批重试」只能由第二层覆盖**。第一层构造不出跨实例竞态，没有任何第一层用例能断言重试行为——重构领取时若丢掉那个三轮循环，CI 不会有任何反应。搬动该循环时以代码为准，不要以测试是否变绿为准。

### 6.2 第二层 —— 真实 MySQL，本机执行，CI 自动跳过

`BackgroundJobConcurrencyTests`：环境变量 `XIHAN_TEST_MYSQL` 取连接串，未设置时 `Assert.SkipWhen(...)` 跳过。范式照抄 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`。

- 插入 200 个到期作业，8 个并发领取者各自持有独立客户端与存储实例，循环领取（每批 10 条）直到领不到
- 断言领到的标识**无重复**、总数**恰为 200**

**反向验证（必须做，结果写进 PR 描述）**：临时把抢占 `UPDATE` 的 `Where` 改成只剩 `candidateIds.Contains(item.BasicId)`，设置 `XIHAN_TEST_MYSQL` 跑一次，**预期该测试变红**（出现重复或总数超过 200），记录重复次数；然后还原改动、确认重新变绿。同一改动下 SQLite 层全部仍为绿——这本身就是第 5 节第 ① 条「单线程测试照样全绿」的实证。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 至少一次执行 | 进程在执行成功与 `DeleteAsync` 之间退出，作业会在租约过期后再次执行。作业处理器需幂等 |
| 租约短于单轮耗时 | Worker 串行执行一轮领到的全部作业（`MaxJobFetchCount` 默认 1000）。若分布式锁未跨实例生效（未配置 Redis）、且一轮耗时超过 `BackgroundJobLeaseTimeout`，本轮尚未执行到的作业租约过期，可能被另一实例领走并重复执行。对策：调大租约，或调小 `MaxJobFetchCount` |
| 提前结束的一轮 | Worker 因停机或锁续期失败提前结束本轮时，已领取但未执行的作业要等租约过期才会被再次领取，最长延迟一个租约时长。`Default` 与 `Redis` 版没有租约，下一轮即可领到 |
| 更新不再是插入或更新 | `UpdateAsync` 对不存在的作业不插入（`Default` 与 `Redis` 版会插入）。避免租约过期后被另一实例执行并删除的作业，又被原实例的 `UpdateAsync` 复活 |
| 重复插入抛异常 | `InsertAsync` 遇主键重复时抛数据库异常（`Default` 版覆盖）。Manager 每次都用新 `Guid`，正常路径不触发 |
| 放弃的作业只增不减 | 放弃的作业保留在表里，没有自动清理，也没有 `Redis` 版那样的 TTL。应用需自行定期删除 `Is_Abandoned = 1` 的旧行 |
| 空字符串应用名 | `ApplicationName` 为 `""` 与为 `null` 被视为同一个应用。`Default` 版把二者区分开 |
| 只落宿主主库 | 作业行固定写默认布局的主库，不跟随业务所在的模块库或租户库。业务写在模块库或租户库时，入队与业务**不在同一个事务**（两条连接各自提交），不报错 |
| 在事务型工作单元内入队 | 入队经解析器登记进环境工作单元，与业务同事务（业务也在宿主主库时）。未提交前 Worker 看不到该作业 |
| 不要在业务工作单元里领取 | `GetWaitingJobsAsync` 若在事务型工作单元内调用，条件 `UPDATE` 持有的行锁要到工作单元提交才释放。Worker 的轮询作用域没有工作单元，正常路径不触发 |
| 生命周期保持单例 | `IBackgroundJobStore` 仍注册为 `Singleton`，与主包一致，不构成破坏性变更 |
| 抢占的受影响行数口径 | MySQL 返回「匹配行数」还是「实际变更行数」取决于连接串与驱动。每轮抢占都写入新的 `Claim_Token` 与 `Claim_Time`，两种口径下都大于 0；若日后把抢占改成「仅在必要时改列」，`affected == 0` 会被误判为「被抢光」而空转三轮 |
| 建表默认关闭 | `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`，不开启时首次入队即报表不存在 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真实数据库测试在无环境变量时跳过；`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败与本包无关）
- 本机配置 `XIHAN_TEST_MYSQL` 后并发测试通过，且反向验证确认测试会变红
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `tasks-sqlsugar`，**不加任何 AI 署名**

## 9. 五个共同问题

| 问题 | 本份的答案 | 依据 |
| --- | --- | --- |
| 分表与否 | **不分表** | 作业表是队列：成功即删除，只有放弃的作业滞留。按月分表会让领取查询跨表，且领取的条件 `UPDATE` 无法在分表上原子地跨表执行 |
| 主键类型 | **`Guid`**（`SugarEntity<Guid>`） | 契约的标识类型是 `Guid`（`BackgroundJobInfo.Id`，`FindAsync(Guid)`、`DeleteAsync(Guid)`），直接用作主键使按标识查找与删除都是主键操作 |
| 是否参与工作单元事务 | **参与**（入队） | 经 `ISqlSugarClientResolver.GetCurrentClient()` 取客户端，自动登记进环境工作单元；业务回滚则作业消失。领取、删除、更新都在 Worker 无工作单元的作用域里，自动提交 |
| 是否需要多库 | **单库：默认布局的主库** | Worker 的轮询作用域无租户上下文，只能看到宿主布局；让作业跟随模块库需要领取端遍历多库并按库分配配额，复杂度与收益不成比例。见「待确认的决策」第 4 条 |
| 顶替方式 | **`Replace`** | 主包 `TryAddSingleton`（`XiHanBackgroundJobsServiceCollectionExtensions.cs:32`），`TryAdd` 是空操作 |

## 10. 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改变会影响什么 |
| --- | --- | --- | --- | --- |
| 1 | 两份的拆法 | ① 骨架 + 后台作业；② 定时任务 + 文档收尾 | 后台作业有领取语义、需要真实数据库并发测试，单独一份；定时任务是纯 CRUD + 一个陈旧实例问题 | 合成一份则计划超长；按实体拆则骨架与注册被拆散 |
| 2 | 分布式锁存在时仍做原子领取 | 做 | 默认锁只在进程内互斥（§1.3），不能把正确性押在是否配置 Redis 上 | 不做则省掉两列与三步协议，但无 Redis 的多实例部署会重复执行 |
| 3 | 租约默认时长 | 5 分钟 | 与发件箱 `ClaimTimeout`、Worker 锁 TTL（300 秒）一致 | 调大：崩溃恢复与提前结束的一轮更慢；调小：长轮次下更容易被重复领取（§7） |
| 4 | 作业落库位置 | 默认布局主库，强制宿主上下文 | Worker 只轮询宿主布局；跟随租户库会让作业永不执行（§5 ⑤） | 若要跟随模块库，需按发件箱 P6 的方式改领取为遍历 `GetCurrentLayoutConfigIds()`、令牌按库生成、配额按库分配，并处理「已登记多个连接」的歧义 |
| 5 | 存储生命周期 | `Singleton` + 每次操作新建作用域 | 保持主包原生命周期，不构成破坏性变更；与必须是单例的 `IJobStore` 共用访问器 | 改 `Scoped` 会让注入 `IBackgroundJobManager` 的下游单例在开发环境启动即抛 |
| 6 | `null` 应用名存为空字符串 | 是 | 领取条件在两处各写一个等值比较，不依赖 SqlSugar 对 `null` 变量的翻译 | 保留 `null` 则两处都要分支；`""` 与 `null` 可区分 |
| 7 | 放弃的作业 | 保留行、标记放弃、不自动清理 | 与 `Redis` 版一致，便于排查 | 改为删除则与 `Default` 版一致，但失去排查依据 |
| 8 | `UpdateAsync` 遇到不存在的作业 | 不插入 | 防止被别的实例执行并删除的作业被复活 | 改为插入或更新则与 `Default`/`Redis` 一致，但有复活风险 |
| 9 | `InsertAsync` 主键重复 | 抛数据库异常 | 数据库主键约束的自然行为；正常路径不触发 | 改为覆盖需先查后写，多一次往返 |
| 10 | 建两条索引 | 候选查询索引 + 令牌索引 | 放弃的作业只增不减，表会增长；领取每轮都查 | 不建则表大后领取变慢；多建则写入变慢 |
| 11 | 时间按 `IClock` 口径原样存取 | 是，不做 UTC 归一 | 写入与比较同源，Worker 只做减法与比较 | 归一到 UTC 需要在映射层知道时钟的 `Kind`，并且所有比较参数同步转换 |
| 12 | 命名 | 包 `XiHan.Framework.Tasks.SqlSugar`，模块 `XiHanTasksSqlSugarModule`，选项 `XiHanTasksSqlSugarOptions`，配置节 `XiHan:Tasks:SqlSugar`，表 `sys_background_job` | 沿用 `Auditing.SqlSugar` 的「模块名 + SqlSugar」顺序与 `sys_` 表名前缀 | 改名影响第 ② 份与文档站条目 |
| 13 | 测试方法名 | 中文 | 与 `EventBus.SqlSugar.Tests`、`Auditing.SqlSugar.Tests` 一致（`Tasks.Tests` 是英文） | 无功能影响 |

## 11. 下一份

第 ② 份（`.superpowers/specs/2026-09-28-tasks-sqlsugar-2-scheduled-jobs-design.md`）：`IJobStore` 的 SqlSugar 实现——`sys_job_instance`、`sys_job_history` 两个实体、陈旧运行中实例的处理、历史清理；并完成新包的文档站收尾（`docs/packages/tasks-sqlsugar.md`、`docs/.vitepress/config.ts` 侧边栏、`framework/README.md` 与 `framework/README_cn.md` 的模块清单）。两份都完成后本包的 PR 方可提交。
