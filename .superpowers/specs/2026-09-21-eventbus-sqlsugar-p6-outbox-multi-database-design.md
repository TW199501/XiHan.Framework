# P6：EventBus.SqlSugar 发件箱多库 设计

- **日期**：2026-09-21
- **状态**：已评审通过，待实现
- **对应计划**：`.superpowers/plans/2026-09-21-eventbus-sqlsugar-p6-outbox-multi-database.md`
- **前置**：P4（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p4-outbox-write-claim-design.md`）与 P5（`.superpowers/specs/2026-09-21-data-all-databases-capability-p5-design.md`）必须已完成
- **所属 PR**：**第二个 PR**（`EventBus.SqlSugar`），与 P3、P4、P7 同一个
- **系列**：SqlSugar 持久化层 P6 / 规划 7 份，**现存 P1–P6**（P7 待写）。P1–P2 `Auditing.SqlSugar`，P5 `Data`，P3–P4、P6–P7 `EventBus.SqlSugar`

> 本文档**自成一体**。实现 P6 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份改的是 P4 已经全绿的四个方法。** 第 5 节的第一条错了**不会报错、P4 的既有测试照样全绿**——因为那些测试的桩解析器只有一个库。故障只在真正配置了模块库的应用上出现。

---

## 1. 背景与目标

P4 交付的 `SqlSugarEventOutbox` 是**单库**实现：四个方法（`EnqueueAsync`、`GetWaitingEventsAsync`、`DeleteAsync`、`DeleteManyAsync`）都取 `GetClientForEntity<SysEventOutbox>()`。

`SysEventOutbox` 自身没有标注 `[ModuleDataSource]`，因此该调用**永远解析到当前布局的主库**。业务实体经 `[ModuleDataSource]` 落在模块库时：

- 业务数据写进模块库，事件行写进主库
- 多个 SqlSugar 连接在同一个工作单元里**各自独立提交**，框架没有两阶段提交
- 于是「入箱与业务数据同事务」这条 P4 的核心保证**在模块库场景下不成立**

P5 已补齐两个所需能力，但**没有任何调用方**：

- `TableInitializationAttribute.IncludeModuleConnections`：让实体在模块库也建表
- `ISqlSugarClientResolver.GetEnlistedConfigIds()`：查询当前工作单元已登记的连接标识

**P6 让发件箱用上它们**，交付两件事：

1. **入箱落在业务所在的库**：事件行与触发它的业务写入进同一个库、同一个事务
2. **发送端遍历当前布局的全部库**：领取与删除不再只看主库

**P6 结束时的状态**：业务实体分布在主库与模块库时，发件箱仍然成立。租户独立库与收件箱不在本份范围。

**成功标准**：

1. 业务写在模块库时，事件行也写在该模块库；该模块库的事务回滚后事件消失
2. 当前工作单元登记了多个连接时抛出明确异常，而不是猜一个库
3. 发送端能从主库与模块库同时领取，配额按库平均分配
4. 某个库不可达时只跳过该库，其余库照常投递
5. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 要修改的文件本身与 P4/P5 的产出 —— 最高优先**

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  Outbox/SqlSugarEventOutbox.cs        要改的三个方法
  Entities/SysEventOutbox.cs           要加建表标注
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs        GetEnlistedConfigIds / GetCurrentLayoutConfigIds / GetClient
  Clients/SqlSugarClientResolver.cs         三者的实现，注意 GetClient 内部调 EnlistCurrentUnitOfWork
  Initializers/TableInitializationAttribute.cs   IncludeModuleConnections
  Initializers/DbEntityTypeProvider.cs           该属性如何参与放行判定
framework/src/XiHan.Framework.EventBus/Distributed/
  EventBoxOutboxSenderHostedService.cs   发送循环：每轮只调一次 GetWaitingEventsAsync
  DistributedEventBusBase.cs:236-272     入箱调用点
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/
  Abstract/UpdateableProvider/    条件更新、SetColumns
  Abstract/QueryableProvider/     Take / OrderBy / In
  Abstract/DeleteProvider/        条件删除
```

当前引用版本：`SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 多库与切换 | `多租戶.md`、`切換數據庫.md` |
| 事务参与 | `事务用法.md`、`UnitOfWork工作單元.md` |
| 并发与线程安全 | `偶發性錯誤與執行緒安全.md` |
| 条件更新 | `更新數據.md` |

仓库自身文档：`docs/packages/data.md`（P5 已补写多库声明方式）、`docs/packages/eventbus.md`、`docs/guide/uow.md`。

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

### 2.4 P6 特有的禁止事项

- **不再在发件箱里调 `GetClientForEntity<SysEventOutbox>()`**。它永远解析到主库，是本份要修掉的那个行为。
- **不改 P4 三步抢占的内部逻辑**。抢占步骤的 `Where` 必须继续同时包含主键集合与可领取条件，只是现在逐库执行。
- **不改动 `EventBoxOutboxSenderHostedService`**。它的「取 → 投递 → 删」循环保持原样，多库在实现内解决。
- **不改 `IEventOutbox` 契约**。
- **不做跨库两阶段提交**。框架没有分布式事务，也不引入。
- **不做租户独立库的遍历**。见 §7。
- **不做收件箱**（P7）。

## 3. 非目标

- **不支持跨库的单个事务**。已登记多个连接时本份选择 fail-closed 抛异常，见 §4.2。
- **不做租户独立库遍历**。发送循环运行在无租户上下文的后台作用域，只遍历默认布局。
- **不支持 `filter` 参数**。沿用 P4：非 `null` 抛 `NotSupportedException`。
- **不追求恰好一次投递**。至少一次是发件箱模式的固有语义。
- **不改 `EventBoxOutboxSenderHostedService` 的轮询节奏或批量大小**。

## 4. 设计

### 4.1 建表：实体进入所有库

`SysEventOutbox` 加上 P5 的标注：

```csharp
[SugarTable("sys_event_outbox")]
[TableInitialization(IncludeModuleConnections = true)]
public class SysEventOutbox : SugarEntity<Guid>
```

`DbEntityTypeProvider.IsModuleDataSourceAllowed` 因此对该实体放行，`sys_event_outbox` 在主库与每个模块库都由 `DbInitializer` 在启动时建出来。

该标注默认 `false`，对其他实体零影响。

### 4.2 入箱：从已登记连接推导落点

`EnqueueAsync` 的落点判定：

| `GetEnlistedConfigIds()` | 落点 |
| --- | --- |
| 0 个 | `GetCurrentClient()` |
| 1 个 | `GetClient(该标识)` |
| 多于 1 个 | 抛 `InvalidOperationException`，消息列出已登记的标识 |

`GetClient(configId)` 内部调用 `EnlistCurrentUnitOfWork`，事件行因此与业务数据落在同一个事务上。

**0 个的含义**：无工作单元、工作单元非事务型、或当前事务尚未写过任何库。此时退回当前布局的主库——与 P4 的行为完全一致，单库应用与非事务场景不受本份影响。

**多于 1 个为什么不猜**：`OutgoingEventInfo` 不携带任何业务实体信息，框架无从判断事件属于哪一次业务写入。`GetEnlistedConfigIds()` 的文档明写「返回顺序不作承诺」，取第一个就是取一个任意值。猜错的结果是事件行落在另一个库上、同事务保证静默失效。抛异常让它在第一次运行时就暴露，与 P4 对 `filter` 的取舍一致。

异常消息需列出已登记的标识并指向「拆小工作单元」这一出路。

### 4.3 领取：遍历、配额、逐库隔离

`GetWaitingEventsAsync` 的结构：

```
configIds = GetCurrentLayoutConfigIds()
若为空则直接返回空集合

quota      = max(1, maxCount / configIds.Count)
claimToken = Guid.NewGuid().ToString("N")

foreach configId in configIds:
    try:
        在 GetClient(configId) 上执行 P4 的三步抢占，候选取 quota 条
        累加取回的记录
    catch (OperationCanceledException):
        向上抛出，不吞
    catch (Exception ex):
        记录错误日志，continue

按 CreatedTime 升序合并后返回
```

**取消不算故障**：`cancellationToken` 触发时抛出的 `OperationCanceledException` 必须向上传播，不能被逐库隔离吞掉——否则宿主停机时循环会把每个库都试一遍。

**三步抢占逐库执行，内部逻辑与 P4 完全相同**：选候选 → 条件抢占 → 按令牌取回。抢占的 `Where` 继续是「主键在候选集内 **且**（待发送 **或** 领取已超时）」。

**令牌按库生成**：每个库在自己的抢占步骤里生成一个新令牌，取回步骤在该库内查 `ClaimToken == 本库令牌`。

> **修订（实现阶段）**：本节原先规定「所有库共用一个令牌」，理由是取回逐库进行、不会互相污染。该理由预设了各 `ConfigId` 指向不同的物理库。`GetCurrentLayoutConfigIds()` 去重的是标识名而非连接串，因此两个 `ConfigId` 可以指向同一个物理库；此时第二轮的取回会把第一轮已领的行连同新行一起按同一个令牌查回来，**同一事件在一批里出现两次**。这与 §1 成功标准「多实例不重复投递」直接冲突，故改为按库生成。

**配额平均分配**：每库最多领 `max(1, maxCount / 库数)` 条，任何一个库都不会被前面的库饿死。库数为 1 时 `quota == maxCount`，行为与 P4 一致。

> **修订（实现阶段）**：本节原先称「单批总量因此不超过 `maxCount`」。`max(1, ...)` 的下限使该表述在**库数大于 `maxCount`** 时不成立——整数商为 0 被抬到 1，每库各领 1 条，单批总量等于库数。下限是有意保留的（否则库会被饿死），因此修正的是表述而非行为。

**逐库隔离**：单个库不可达不应拖垮其余库的投递。该库的记录会在下一轮轮询重试；若某轮已经抢占成功但取回失败，记录由 P4 的领取超时机制释放。

**为什么遍历的是 `GetCurrentLayoutConfigIds()`**：它返回「主库 + 已建连的模块库」，正是发送端该覆盖的范围。`GetAllConfigIds()` 返回静态配置里的全部标识（含其他租户布局），语义不符。

### 4.4 删除：遍历同一组库

`DeleteAsync(Guid)` 与 `DeleteManyAsync(IEnumerable<Guid>)` 对 `GetCurrentLayoutConfigIds()` 的每个标识各发一次 `Deleteable<SysEventOutbox>().In(...)`。

主键是全局唯一的 `Guid`，在没有该行的库上执行只是删除 0 行。实现因此**无状态**，不需要记录记录与库的对应关系，`DeleteAsync` 也同样适用。

`DeleteManyAsync` 对空集合直接返回，不发任何 SQL。

### 4.5 日志

逐库隔离需要记录被跳过的库与原因，`SqlSugarEventOutbox` 因此新增构造函数参数 `ILogger<SqlSugarEventOutbox>`。

签名变更影响测试夹具 `OutboxTestContext` 与 `OutboxConcurrencyTests`，两处需同步更新。

## 5. 三条会静默失效的陷阱

**① 改了落点却没改建表标注，或反过来。**

两者缺一，模块库场景都不成立：

- 只改落点、没加 `IncludeModuleConnections`：模块库里根本没有 `sys_event_outbox` 表，入箱直接报表不存在
- 只加标注、没改落点：表建出来了但永远是空的，事件仍然全部落在主库

**P4 的既有测试两种情况都发现不了**——它们的桩解析器只有一个库。

**② 抢占的 `Where` 在逐库改写时丢掉可领取条件。**

P4 硬约束 ②：抢占步骤若只按主键更新令牌，两个并发领取者会先后覆盖同一行的令牌，双方的取回步骤都可能拿到它，**同一事件被投递两次**。

把单库循环改写成多库循环时容易顺手简化掉这段条件。条件必须始终是「主键在候选集内 **且**（待发送 **或** 领取已超时）」。

**③ 用 `GetCurrentClient()` 或 `GetClientForEntity` 代替 `GetClient(configId)`。**

遍历时必须按标识取对应的客户端。用前两者会让每一轮都作用在同一个主库上，循环空转 N 次，表面上一切正常。

## 6. 测试策略

分两层，沿用 P4 的划分。

**第一层 —— SQLite 多库，CI 强门禁执行**

桩解析器改为持有一组「标识 → 客户端」的映射，两个临时 `.db` 文件模拟主库与一个模块库。必测：

- **落点推导**：已登记恰好一个模块库时，事件行落在该模块库、不在主库
- **落点退回**：已登记为空时落在当前库，与 P4 行为一致
- **多库歧义**：已登记两个标识时抛 `InvalidOperationException`
- **遍历领取**：两个库各有待发记录，一次调用两边都领到
- **配额分配**：`maxCount` 为 4、两个库各有 10 条时，单批不超过 4 条
- **跨库删除**：删除一批跨两个库的标识，两边都清空
- **单库故障隔离**：一个库的客户端抛异常时，另一个库的记录仍被领到
- **取消传播**：已取消的令牌传入时抛 `OperationCanceledException`，不被逐库隔离吞掉
- **单库回归**：P4 的既有用例在只有一个库的桩上继续全绿

**第二层 —— 真实数据库，本机执行，CI 自动跳过**

沿用 P4 的 `XIHAN_TEST_MYSQL` 与 `Assert.SkipWhen(...)`，范式见 `framework/test/XiHan.Framework.EventBus.Redis.Tests/RedisPendingBehaviorTests.cs`。P4 的并发用例保持可用（签名变更后同步更新构造调用）。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

SQLite 临时库的连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会失败。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| ~~冷启动缺口~~ | **本条经实现阶段核对为不存在，已撤销。** 原文称模块库要等首次流量后才进入 `GetCurrentLayoutConfigIds()`。实际上 `XiHanDataServiceCollectionExtensions.CreateScope` 把主库与全部**静态配置**的模块库 `ConnectionConfig` 一次性交给 `new SqlSugarScope(...)`，其构造回调对每个 `ConfigId` 都调了 `GetConnectionScope`，因此 `IsAnyConnection` 在单例构造时即对全部模块库为真，没有逐库懒注册的时间差。懒注册路径只存在于运行期动态租户模块库，而发送循环不遍历那些库 |
| 跨库事务歧义 | 当前工作单元登记多于一个连接时抛异常。合法的跨库事务会被挡下，出路是拆小工作单元 |
| 发布早于写入 | 业务若先 `PublishAsync` 再写仓储，入箱那一刻已登记连接为空，事件落主库而业务落模块库——两个独立事务，且不报错。文档需提示「先写业务、后发事件」 |
| 单库故障吞异常 | 不可达的库只记录错误日志并跳过，不中断其余库 |
| 租户独立库 | 发送循环无租户上下文，只遍历默认布局。租户独立库的发件箱不在本份范围 |
| 单批吞吐 | 每库配额为 `maxCount / 库数`，单库单轮吞吐降为 1/N；轮询是连续的，累计吞吐不受影响 |
| N 条删除 | 每批删除向每个库各发一次 `DELETE`，其中至多一条有效，其余是空操作 |

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
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `eventbus-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本 PR 无关的文档

## 9. 下一份

P7（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p7-inbox-design.md`，待写）：收件箱——`IEventInbox` 的 SqlSugar 实现、`sys_event_inbox` 实体与去重，以及 `EventBus.SqlSugar` 的文档站条目（`docs/packages/eventbus-sqlsugar.md`、侧边栏、根 README 与 `framework/README.md` 的模块清单），补齐后第二个 PR 方可提交。
