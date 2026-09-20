# P4：EventBus.SqlSugar 发件箱写入与原子领取 设计

- **日期**：2026-09-21
- **状态**：已评审通过，待实现
- **对应计划**：`.superpowers/plans/2026-09-21-eventbus-sqlsugar-p4-outbox-write-claim.md`
- **前置**：P3（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p3-outbox-entity-design.md`）必须已完成
- **系列**：SqlSugar 持久化层 P4 / 共 6 份（P1–P2 为 `Auditing.SqlSugar`，P3–P6 为 `EventBus.SqlSugar`）

> 本文档**自成一体**。实现 P4 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份是整个系列技术上最难、也最容易静默出错的一份。** 第 5 节的前两条错了**不会报错、单元测试照样全绿**，故障只在生产的多实例环境下出现，表现为事件丢失或重复投递。动手前必须读懂它们。

---

## 1. 背景与目标

P3 已建立 `XiHan.Framework.EventBus.SqlSugar` 包与 `sys_event_outbox` 表，但 `IEventOutbox` 尚未实现——容器里仍是 `DefaultEventOutbox`（`ConcurrentDictionary`），事件仍在内存。

**P4 实现 `IEventOutbox` 并接管发件箱**，交付两个核心保证：

1. **入箱与业务数据同事务**：业务回滚，事件随之消失；业务提交，事件必然在库
2. **多实例不重复投递**：`N` 个实例同时轮询，同一条记录只会被一个实例领走

**P4 结束时的状态**：应用声明 `[DependsOn(typeof(XiHanSqlSugarEventBusModule))]` 后，发件箱真正落库并可靠投递（单库）。多库遍历在 P5，收件箱在 P6。

**成功标准**：

1. 业务事务回滚后，发件箱里没有该事件；提交后有
2. 多线程并发调用 `GetWaitingEventsAsync`，同一条记录只被一个调用方领到（真实数据库验证）
3. 领取超时后记录可被重新领取
4. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 框架既有实现 —— 最高优先，本包必须与其一致**

```
framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/
  IEventOutbox.cs             要实现的契约（4 个方法）
  IOutgoingEventInfo.cs       filter 参数的元素类型
  OutgoingEventInfo.cs        公开四参构造函数
framework/src/XiHan.Framework.EventBus/Distributed/
  DistributedEventBusBase.cs:236-272   入箱调用点：经 unitOfWork.ServiceProvider 解析
  EventBoxOutboxSenderHostedService.cs 发送循环：取 → 投递 → 删，无锁无领取
  DefaultEventOutbox.cs                被替换的内存实现
framework/src/XiHan.Framework.EventBus/Extensions/DependencyInjection/
  XiHanEventBusServiceCollectionExtensions.cs:76-120
                              ImplementationType 的默认值与 IEventOutbox 的注册方式
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs        客户端解析
  Clients/SqlSugarClientResolver.cs:230-290 如何把连接钉入工作单元事务
  Clients/SqlSugarTransactionApi.cs         事务适配器，含连接钉住的告警
  Repository/SqlSugarRepositoryBase.cs:130-140  条件更新的既定写法
framework/src/XiHan.Framework.EventBus.SqlSugar/   P3 产出的实体与映射
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/
  Abstract/UpdateableProvider/    条件更新、SetColumns
  Abstract/QueryableProvider/     Take / OrderBy / In
  Abstract/DeleteProvider/        条件删除
  Abstract/AdoProvider/           事务
```

当前引用版本：`SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 事务参与 | `事务用法.md`、`事務鎖.md`、`UnitOfWork工作單元.md` |
| 并发与线程安全 | `偶發性錯誤與執行緒安全.md`、`並發控制樂觀鎖.md` |
| 条件更新 | `更新數據.md`、`無實體更新.md` |
| 删除 | `刪除數據.md` |
| 查询与分页 | `執行查詢.md`、`SQL分頁查詢.md` |

`sqlsugar-mcp` 的 notes 语料与该目录是同一批文件，直接读目录即可，无需接入 MCP。

仓库自身文档：`docs/packages/eventbus.md`、`docs/guide/uow.md`、`docs/packages/uow.md`。

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

### 2.4 P4 特有的禁止事项

- **不在 `EnqueueAsync` 里自建连接或新建服务作用域**。见 §5 第一条——这是本份最致命的错误。
- **不在 `EnqueueAsync` 里开事务**。事务由工作单元管，入箱只是参与。
- **不写方言相关的 SQL**。`FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP` 一律不用，见 §4.4。
- **不改 `IEventOutbox` 契约**。原子领取在实现内解决。
- **不改动 `EventBoxOutboxSenderHostedService`**。它的「取 → 投递 → 删」循环保持原样。
- **不做多库遍历**（P5）。本份只处理 `GetClientForEntity<SysEventOutbox>()` 解析出的那一个库。
- **不做收件箱**（P6）。
- **不静默忽略 `filter` 参数**。见 §4.5。

## 3. 非目标

- **不做多库遍历发送**（P5）。
- **不做收件箱**（P6）。
- **不支持 `filter` 参数的表达式翻译**。见 §4.5，本份的选择是 fail-closed 抛异常而非静默忽略。
- **不追求恰好一次投递**。至少一次是发件箱模式的固有语义，消费端需幂等。
- **不做保留期清理**。发件箱记录在投递成功后由宿主循环删除。

## 4. 设计

### 4.1 新增结构

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  XiHanSqlSugarEventBusModule.cs                    修改：调用注册扩展
  Extensions/DependencyInjection/
    XiHanSqlSugarEventBusServiceCollectionExtensions.cs   新增
  Options/XiHanSqlSugarEventBoxOptions.cs           新增：领取超时
  Outbox/SqlSugarEventOutbox.cs                     新增：IEventOutbox 实现

framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxEnqueueTests.cs        SQLite：入箱与事务参与
  OutboxClaimTests.cs          SQLite：领取、超时释放、删除
  OutboxConcurrencyTests.cs    真实数据库：并发领取互斥
  OutboxRegistrationTests.cs   注册断言
```

模块类**只做装配不写逻辑**——`ConfigureServices` 里只调一个 `services.AddXiHanSqlSugarEventBus()`。

### 4.2 入箱与事务参与

`DistributedEventBusBase.AddToOutboxAsync` 经 `unitOfWork.ServiceProvider.GetRequiredService(outboxConfig.ImplementationType)` 解析实现——也就是**从当前工作单元的作用域**取。

因此 `SqlSugarEventOutbox` 必须注册为 **Scoped**，构造函数注入 `ISqlSugarClientResolver`。`EnqueueAsync` 经 `GetClientForEntity<SysEventOutbox>()` 取客户端，该解析器会把连接钉入当前工作单元的事务（`SqlSugarClientResolver.EnlistCurrentUnitOfWork`），事件行因此与业务数据落在同一个 `SqlSugarTransactionApi` 上。

`EnqueueAsync` 只做映射与 `Insertable(...).ExecuteCommandAsync()`，不开事务、不提交。

### 4.3 注册：与 P2 相反的覆盖方向

两处注册，方向相反，**这是本份最容易搞混的地方**：

**① `ImplementationType` —— 后配置者赢。**

`XiHanEventBusModule` 注册了一个 options 动作，仅在 `ImplementationType == default` 时设为 `typeof(DefaultEventOutbox)`。`IConfigureOptions` 动作按**注册顺序**执行，本模块依赖 `XiHanEventBusModule`、其 `ConfigureServices` 先跑，因此本模块后注册的动作**后执行、覆盖前者**：

```csharp
services.Configure<XiHanDistributedEventBusOptions>(options =>
{
    options.Outboxes.Configure(config => config.ImplementationType = typeof(SqlSugarEventOutbox));
});
```

`Outboxes.Configure(action)` 不带名称时作用于名为 `"Default"` 的发件箱。

**② DI 注册 —— 用 `Replace`，因为 `TryAdd` 先到先赢。**

`XiHanEventBusModule` 已 `TryAddSingleton<DefaultEventOutbox>()` 与 `TryAddSingleton<IEventOutbox>(sp => sp.GetRequiredService<DefaultEventOutbox>())`。本模块须：

- `services.TryAddScoped<SqlSugarEventOutbox>()` —— 按具体类型注册，供 `ImplementationType` 解析
- `services.Replace(ServiceDescriptor.Scoped<IEventOutbox, SqlSugarEventOutbox>())` —— 顶替接口注册

> **对照记忆**：options 是**后写覆盖**，`TryAdd` 是**先写胜出**。两者方向相反，照着其中一个的直觉去做另一个必错。

**生命周期变更说明**：`IEventOutbox` 原注册为 Singleton，本包改为 Scoped——这是必须的，Singleton 拿不到工作单元作用域的客户端。经核对，框架内**没有任何构造函数注入 `IEventOutbox`**（两处消费点都是从作用域按 `ImplementationType` 解析），因此不会产生被捕获依赖。该变更写入 README 与 PR 描述。

### 4.4 原子领取：可移植的三步抢占

宿主的发送循环是「取待发 → 投递 → 删除」，**没有分布式锁、没有领取语义**。`N` 个实例会取到同一批记录并重复投递。去重责任完全落在 `GetWaitingEventsAsync` 的实现内。

契约没有留领取方法的位置，因此 `GetWaitingEventsAsync` 的语义变为**「领取并返回」**。该落差必须写入 XML 文档注释。

实现分三步，全部使用方言无关的表达式 API：

1. **选候选**：查 `Status == Pending`，或 `Status == Claimed 且 Claim_Time < 超时界限` 的记录，按 `Created_Time` 升序取 `maxCount` 条的主键
2. **抢占**：`Updateable<SysEventOutbox>()` 把这批主键的 `Status` 置 `Claimed`、`Claim_Token` 置本次令牌、`Claim_Time` 置当前时刻，**`Where` 里重复第 1 步的可领取条件**
3. **取回**：查 `Claim_Token == 本次令牌` 的记录并映射回 `OutgoingEventInfo`

正确性来自第 2 步：每行的 `UPDATE` 是原子的，两个并发领取者中只有一个能把某行从可领取改成已领取；输给对手的行不会带上本方令牌，第 3 步自然取不到。**第 2 步的 `Where` 绝不能省略可领取条件**——只按主键更新会让两个领取者互相覆盖令牌，双方都以为领到了。

**不使用** `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP`：三者分别只在部分方言可用，而本框架是通用类库。分两步「先选后抢」是各方言都成立的写法，代价是多一次往返。

**超时释放**：`Claim_Time` 早于「当前时刻 − 领取超时」的已领取记录重新变为可领取，避免实例异常退出导致记录永久滞留。超时值由 `XiHanSqlSugarEventBoxOptions.ClaimTimeout` 配置，默认 5 分钟，配置节 `XiHan:EventBus:SqlSugar`。

**令牌**：每次调用生成一个新的 `Guid.NewGuid().ToString("N")`，32 字符，与 `Claim_Token` 列长 64 相容。

### 4.5 `filter` 参数：fail-closed

`IEventOutbox.GetWaitingEventsAsync` 接受 `Expression<Func<IOutgoingEventInfo, bool>>?`。`IOutgoingEventInfo` 与实体的成员并不对应（`Id` ↔ `BasicId`、`DateTime` ↔ `DateTimeOffset`、`ExtraProperties` 是字典 ↔ JSON 字符串），要支持需要一个表达式翻译器。

框架自身从不传该参数（`EventBoxOutboxSenderHostedService` 调用时只给 `maxCount` 与取消令牌）。

**本份的选择**：`filter` 非 `null` 时抛 `NotSupportedException`，消息说明未支持且指向后续版本。

理由是 fail-closed：静默忽略过滤条件会让调用方以为筛选生效、实际领走全部记录，是会在生产造成错误投递的静默故障；抛异常则在第一次调用时就暴露。与框架在「未启用或未配置时既不注册服务也不映射端点」的一贯取舍一致。

### 4.6 删除

`DeleteAsync(Guid id)` 与 `DeleteManyAsync(IEnumerable<Guid> ids)` 按主键删除。`DeleteManyAsync` 对空集合直接返回，不发 SQL。

### 4.7 投递语义

**至少一次**。宿主循环在投递成功后才删除，若进程在投递与删除之间退出，记录会被重新领取并再次投递。这是发件箱模式的固有语义，消费端需幂等。该语义写入包 README。

## 5. 四条会静默失效的陷阱

前两条错了**不会报错、单元测试照样全绿**，故障只在生产的多实例环境出现。

**① `EnqueueAsync` 必须用工作单元作用域的客户端。**

若在 `EnqueueAsync` 内自建 `SqlSugarClient`、或用 `IServiceScopeFactory` 另开作用域取客户端，事件行就落在**另一条连接、另一个事务**上。业务回滚时事件已经独立提交，于是发出一个根本没发生的事件；业务提交而事件写入失败时，事件永远丢失。

**同事务保证因此失效，但不会有任何异常，单元测试也照样通过**——因为单测里通常没有真正的工作单元事务。

正确做法：构造函数注入 `ISqlSugarClientResolver`（本类注册为 Scoped），`EnqueueAsync` 内调 `GetClientForEntity<SysEventOutbox>()`。

**② 抢占的 `Where` 必须重复可领取条件。**

第 2 步若写成「按主键更新令牌」而不带 `Status` / `Claim_Time` 条件，两个并发领取者会先后覆盖同一行的令牌，各自的第 3 步都可能取到它，**同一事件被投递两次**。

条件必须是「主键在候选集内 **且** （待发送 **或** 领取已超时）」。

**③ 注册的覆盖方向：options 后写覆盖，`TryAdd` 先写胜出。**

见 §4.3。用 `TryAdd` 去顶替 `IEventOutbox` 是空操作（`DefaultEventOutbox` 留在容器里）；反过来，以为 options 也要用某种「替换」写法则是多余的——直接赋值即可，因为本模块的动作后执行。

**④ `GetWaitingEventsAsync` 的名字与语义有落差。**

它实际是「领取并返回」，调用后记录已被标记为已领取。该落差必须写入 XML 文档注释，否则后续维护者会以为它是纯查询而在别处复用它。

## 6. 测试策略

分两层。**第二层是本份存在的核心证明，不能省。**

**第一层 —— SQLite，CI 强门禁执行**

- 入箱：`EnqueueAsync` 后记录在库，`Status` 为待发送、令牌为空
- 事务参与：在一个会回滚的 SqlSugar 事务内入箱，回滚后记录不在库；提交后在库
- 领取：`GetWaitingEventsAsync` 返回的记录 `Status` 变为已领取且带令牌；二次调用取不到同一条
- 超时释放：把 `Claim_Time` 改到超时界限之前，记录可被重新领取
- 删除：`DeleteAsync` / `DeleteManyAsync` 生效，空集合不报错
- `filter` 非 `null` 时抛 `NotSupportedException`
- 注册：`ImplementationType` 为 `SqlSugarEventOutbox`，`IEventOutbox` 解析到 `SqlSugarEventOutbox`

**第二层 —— 真实数据库，本机执行，CI 自动跳过**

采用 `Assert.SkipWhen(...)` + 环境变量取地址，范式见 `framework/test/XiHan.Framework.EventBus.Redis.Tests/RedisPendingBehaviorTests.cs`（读 `XIHAN_TEST_REDIS`）。本份使用 `XIHAN_TEST_MYSQL`。

**必测**：预置 `M` 条待发记录，`N` 个线程并发调用 `GetWaitingEventsAsync`，断言所有线程领到的记录**两两不相交**、并集不超过 `M`。

这一条**无法由 SQLite 证明**（SQLite 的写锁是库级串行化，天然不会出现竞争），而它正是 P4 的核心承诺。不跑真库等于 P4 未经验证。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| `filter` 未支持 | 非 `null` 抛 `NotSupportedException`。需要时应加表达式翻译器，是独立改动 |
| 方法名与语义落差 | `GetWaitingEventsAsync` 实为领取。若上游愿意调整契约，应改为显式的领取方法 |
| 至少一次投递 | 消费端必须幂等，框架不提供恰好一次 |
| 单库 | 本份只处理当前解析出的那一个库；业务实体经 `[ModuleDataSource]` 落在模块库时的多库遍历在 P5 |
| `IEventOutbox` 生命周期变更 | 由 Singleton 改为 Scoped，见 §4.3 |
| 两次往返 | 「先选后抢」比 `SKIP LOCKED` 多一次往返，是可移植性的代价 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 真实数据库并发测试在本机通过（`XIHAN_TEST_MYSQL` 指向可用实例）
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- Options 类型命名 `XiHanSqlSugarEventBoxOptions`，自带 `const string SectionName`，配置节 `XiHan:EventBus:SqlSugar`
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `eventbus-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本 PR 无关的文档

## 9. 下一份

P5（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p5-multi-database-design.md`，待写）：多库遍历——业务实体经 `[ModuleDataSource]` 落在模块库时，发件箱须写在同一个库，发送端须遍历 `GetCurrentLayoutConfigIds()` 列出的全部库。
