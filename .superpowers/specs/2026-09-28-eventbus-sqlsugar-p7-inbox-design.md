# P7：EventBus.SqlSugar 收件箱 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-eventbus-sqlsugar-p7-inbox.md`
- **前置**：P3、P4、P6（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p{3,4,6}-*-design.md`）必须已完成——本份在同一个包里追加收件箱，复用它们的包结构、选项类与测试夹具
- **所属 PR**：**第二个 PR**（`EventBus.SqlSugar`），与 P3、P4、P6 同一个。**本份是该 PR 的最后一份**，完成后 PR 方可提交
- **Linear 议题**：`https://linear.app/elf-express/issue/EDDIE-6`
- **系列**：SqlSugar 持久化层 P7 / 规划 7 份。P1–P2 `Auditing.SqlSugar`，P5 `Data`，P3–P4、P6–P7 `EventBus.SqlSugar`

> 本文档**自成一体**。实现 P7 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节、第 5 节与「待确认的决策」。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **收件箱与发件箱不对称，不能照抄。** 发件箱的核心是「与业务同事务」「按业务所在库落点」「投递后删除」；收件箱三条全都相反：入箱时**没有**业务事务、**没有**业务所在的库、处理完**不能**删除（去重靠这些记录）。照着 P4/P6 的直觉写，会写出能编译、测试全绿、但去重永不生效的收件箱。
>
> **本份最容易静默出错的是第 5 节的前三条**：去重键选错、无消息标识时去重键写成常量、处理完就删行。三者都不会报错，第一条与第三条连用例都照样全绿。

---

## 1. 背景与目标

### 1.1 现状

`XiHan.Framework.EventBus.SqlSugar` 已交付发件箱（P3 实体与映射、P4 入箱与原子领取、P6 多库），但**收件箱一行都没有**。容器里的 `IEventInbox` 仍是 `DefaultEventInbox`：

- `framework/src/XiHan.Framework.EventBus/Distributed/DefaultEventInbox.cs:18`：`ConcurrentDictionary<Guid, InboxEntry>`，进程内、上限 100000 条
- 去重在内存里：`ExistsByMessageIdAsync`（`:137-147`）遍历字典比对 `MessageId`——实例之间互不可见
- 待处理事件在内存里：进程退出即丢
- 领取没有互斥：`GetWaitingEventsAsync`（`:52-76`）是纯查询，多个实例各自处理同一批

**事件发得出去、收不进来。多实例部署时同一事件会被每个实例各处理一次。发件箱做完不等于 outbox 模式做完。**

### 1.2 契约与消费方（已核对）

契约 `framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/IEventInbox.cs` 共 7 个方法：

| 方法 | 调用方 | 语义 |
| --- | --- | --- |
| `EnqueueAsync(IncomingEventInfo)` | `DistributedEventBusBase.AddToInboxAsync`（`:344`） | 收下一条入站事件 |
| `ExistsByMessageIdAsync(string)` | 同上（`:322`），入箱前先查 | 按**消息标识**判重 |
| `GetWaitingEventsAsync(int, filter, ct)` | `EventBoxInboxProcessorHostedService`（`:103`） | 取一批待处理事件 |
| `MarkAsProcessedAsync(Guid)` | 同上（`:115`） | 处理成功 |
| `RetryLaterAsync(Guid, int, DateTime?)` | 同上（`:147`） | 处理失败、稍后重试 |
| `MarkAsDiscardAsync(Guid)` | 同上（`:135`） | 重试次数用尽、丢弃 |
| `DeleteOldEventsAsync()` | 同上（`:106`、`:124`），**每轮轮询都调** | 清理过期记录 |

三处与本设计直接相关的事实：

1. **入箱时的事件 `Id` 每次都是新生成的**：`AddToInboxAsync` 用 `GuidGenerator.NextId()` 构造 `IncomingEventInfo`（`DistributedEventBusBase.cs:331-337`）。同一条消息被 broker 投递两次，得到两个不同的 `Id`。**`Id` 因此不能做去重键**，去重只能靠 `MessageId`。
2. **入箱发生在新开的服务作用域里，没有业务工作单元**：`AddToInboxAsync` 自己 `ServiceScopeFactory.CreateScope()`（`:311`）。调用它的三条路径——broker 消费回调 `BrokerDistributedEventBusBase.ProcessIncomingMessageAsync`（`:308`）、`LocalDistributedEventBus.PublishFromOutboxAsync`（`:230`）与 `PublishToEventBusAsync`（`:286`）——都不在事务型工作单元内。工作单元把分布式事件留到**提交之后**才发布（`XiHan.Framework.Uow/UnitOfWork.cs:279-298`），此时该工作单元已 `IsCompleted`，`AmbientUnitOfWork.GetCurrentByChecking`（`:44-49`）会跳过它。**入箱时没有「业务所在的库」可推导。**
3. **重试计数在宿主服务的内存里，不在收件箱里**：`EventBoxInboxProcessorHostedService._retryCounter`（`:23`）是进程内 `ConcurrentDictionary<Guid, int>`，达到 `EventBoxProcessingOptions.MaxInboxRetryCount`（默认 5）时调 `MarkAsDiscardAsync`，否则把算好的次数传给 `RetryLaterAsync`（`:128-153`）。**丢弃的判定权在宿主，收件箱只负责记录。**

`MessageId` 可能为空：`AddToInboxAsync` 的参数是 `string? messageId`，原样以 `messageId!` 传入构造函数（`:333`）；RabbitMQ 取自 `BasicProperties.MessageId`（`RabbitMQDistributedEventBus.cs:227`），外部生产者可不设。

### 1.3 要交付什么

在**已存在的** `XiHan.Framework.EventBus.SqlSugar` 包里追加：

1. 实体 `SysEventInbox`（表 `sys_event_inbox`）与映射 `EventInboxMapper`
2. `SqlSugarEventInbox : IEventInbox`，7 个方法全部落库
3. 注册：在现有的 `AddXiHanSqlSugarEventBus` 里一并顶替 `IEventInbox`
4. 选项：`XiHanSqlSugarEventBoxOptions` 新增 `InboxRetentionPeriod`
5. 文档：包 README、`docs/packages/eventbus-sqlsugar.md`、`framework/README.md` 与 `framework/README_cn.md` 的模块清单——**这些条目已存在，只描述了发件箱，本份把收件箱补进去**

**P7 结束时的状态**：应用声明 `[DependsOn(typeof(XiHanSqlSugarEventBusModule))]` 后，收发件箱都落库；多实例部署时同一消息只被处理一次（在保留期内），同一条待处理记录只被一个实例领走。

### 1.4 成功标准

1. 同一 `MessageId`、不同 `Id` 的两次入箱，库里只留一条，第二次不抛异常
2. 没有 `MessageId` 的两条事件，库里留两条
3. 处于租户上下文时入箱，记录仍写进宿主布局的主库
4. 并发调用 `GetWaitingEventsAsync`，同一条记录只被一个调用方领到（真实数据库验证）
5. `NextRetryTime` 未到的记录不被领取；已到的被领取
6. `MarkAsProcessedAsync` / `MarkAsDiscardAsync` 之后记录仍在库，`ExistsByMessageIdAsync` 仍返回 `true`
7. `DeleteOldEventsAsync` 只删除「已处理或已丢弃、且完结时刻早于保留期」的记录，待处理与已领取的记录无论多旧都不删
8. `IEventInbox` 与 `XiHanDistributedEventBusOptions.Inboxes["Default"].ImplementationType` 都解析到 `SqlSugarEventInbox`，生命周期 `Scoped`
9. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 本包已实现的发件箱——同一个包，结构与写法必须一致**

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  Entities/SysEventOutbox.cs          实体写法：两个构造函数、列名 Pascal_Snake_Case、DateTimeOffset
  Mapping/EventOutboxMapper.cs        映射写法：ToOffset 的 Kind 分支、ExtraProperties 的 JSON 往返
  Outbox/SqlSugarEventOutbox.cs       ClaimFromDatabaseAsync：三步抢占 + 最多三轮重试，收件箱照此写
  Options/XiHanSqlSugarEventBoxOptions.cs    本份在此追加 InboxRetentionPeriod
  Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs   本份在此追加注册
framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  OutboxTestContext.cs                StubClientResolver 在此定义，本份给它加一个可选属性后复用
  OutboxClaimTests.cs / OutboxConcurrencyTests.cs    用例写法与真库跳过的范式
```

**② 契约、被替换的实现与消费方**

```
framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/
  IEventInbox.cs                    要实现的 7 个方法
  IncomingEventInfo.cs              五参公开构造函数；MaxEventNameLength = 256
  IIncomingEventInfo.cs             filter 的元素类型
framework/src/XiHan.Framework.EventBus/Distributed/
  DefaultEventInbox.cs              被替换的内存实现：状态机、保留期 7 天、按 MessageId 判重
  EventBoxInboxProcessorHostedService.cs    处理循环：取 → 逐条处理 → 标记 → 每轮清理
  DistributedEventBusBase.cs:297-351        入箱调用点 AddToInboxAsync
framework/src/XiHan.Framework.EventBus/Extensions/DependencyInjection/
  XiHanEventBusServiceCollectionExtensions.cs:93-117   ImplementationType 默认值与 TryAddSingleton 注册
framework/src/XiHan.Framework.Data/SqlSugar/Clients/
  ISqlSugarClientResolver.cs / SqlSugarClientResolver.cs   GetCurrentClient 如何按租户解析布局（:79-117）
framework/src/XiHan.Framework.Data/SqlSugar/Initializers/
  TableInitializationAttribute.cs / DbInitializationTarget.cs / DbEntityTypeProvider.cs:66-143
framework/src/XiHan.Framework.MultiTenancy.Abstractions/ICurrentTenant.cs    Change(long? id, string? name = null)
```

**③ SqlSugar 源码——API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Entities/Mapping/SugarMappingAttribute.cs:340-352       SugarIndexAttribute 构造函数
  Abstract/CodeFirstProvider/CodeFirstProvider.cs:331-395 CreateIndex：IndexFields 的键按「属性名」匹配列
  ExpressionsToSql/ResolveItems/MemberInitExpressionResolve.cs:164-181   SetColumns 里常量 null 赋值走参数化
  Interface/IQueryable.cs / IUpdateable.cs / IDeleteable.cs / Insertable.cs
```

> 树与目录名：本包引用 SqlSugarCore 5.1.4.221（依赖 `Microsoft.Data.Sqlite`），权威源码是 `Src/Asp.NetCore2/`。同级的 `Src/Asp.Net/` 是 .NET Framework 变体。更新提供者的目录名是 `UpdateProvider`，不是 `UpdateableProvider`。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 条件更新、`SetColumns` 多列 | `更新數據.md` |
| 条件删除 | `刪除數據.md` |
| 索引与建表 | `庫表管理DbMaintenance.md`、`實體管理EntityMaintenance.md` |
| 多租户与切库 | `多租戶基礎.md`、`SAAS分庫.md` |
| 并发与线程安全 | `偶發性錯誤與執行緒安全.md` |

仓库自身文档：`docs/packages/eventbus.md`（收件箱后台循环一节）、`docs/packages/eventbus-sqlsugar.md`（本份要改的页面）。

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

### 2.4 P7 特有的禁止事项

- **不用事件 `Id` 去重，也不用内容哈希去重。** 见「待确认的决策」D1 与 §5 ①。
- **`MarkAsProcessedAsync` / `MarkAsDiscardAsync` 不删行。** 删行由 `DeleteOldEventsAsync` 按保留期统一做。
- **不在收件箱里调 `GetEnlistedConfigIds()` 推导落点，不遍历 `GetCurrentLayoutConfigIds()`。** 那是发件箱的做法，收件箱入箱时没有业务工作单元。
- **不调 `GetClientForEntity<SysEventInbox>()`**。统一在 `_currentTenant.Change(null)` 作用域内调 `GetCurrentClient()`，见 §4.3。
- **不在收件箱里开事务，也不主动脱离环境工作单元。**
- **不改动 `EventBoxInboxProcessorHostedService`、`DistributedEventBusBase`、`IEventInbox` 契约。** 重试计数与丢弃判定留在宿主服务。
- **不使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP`、`INSERT ... ON CONFLICT`、`INSERT IGNORE`、`MERGE` 或任何方言相关 SQL。** 去重冲突的判定用「插入失败后回查」，见 §4.4。
- **不加 `[SplitTable]`。** 唯一索引在分表下只在单张子表内生效，跨月的重复消息会漏过。
- **不动发件箱的任何代码与测试**，唯一例外是 `StubClientResolver` 新增一个可选属性（§6）。

## 3. 非目标

- **不支持 `filter` 参数**。与发件箱一致：非 `null` 抛 `NotSupportedException`。
- **不做持久化的重试上限**。重试次数写进库只作记录；丢弃的判定仍由宿主服务的进程内计数决定，见 D9。
- **不做租户独立库的收件箱**。所有读写落在宿主布局主库，见 D4。
- **不做清理节流**。宿主每轮都调 `DeleteOldEventsAsync`，本份每次都发一条条件 `DELETE`，见 D7。
- **不追求恰好一次处理**。去重窗口等于保留期；领取超时后会重新领取。处理器仍须幂等。
- **不改 `EventBoxProcessingOptions` 的任何默认值**（批量 100、轮询 2 秒、最大重试 5、重试延迟 10 秒）。

## 4. 设计

### 4.1 实体 `SysEventInbox`

```csharp
[SugarTable("sys_event_inbox")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("ux_sys_event_inbox_dedup_key", nameof(SysEventInbox.DedupKey), OrderByType.Asc, true)]
[SugarIndex("ix_sys_event_inbox_status", nameof(SysEventInbox.Status), OrderByType.Asc, nameof(SysEventInbox.CreatedTime), OrderByType.Asc)]
public class SysEventInbox : SugarEntity<Guid>
```

| 属性 | 列名 | 类型 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `BasicId`（基类） | `Basic_Id` | `Guid` | 主键，非自增 | 取 `IncomingEventInfo.Id` |
| `RowVersion`（基类） | `Row_Version` | `long` | | 基类自带 |
| `MessageId` | `Message_Id` | `string?` | 长度 256，可空 | 原始消息标识；空白时存 `null` |
| `DedupKey` | `Dedup_Key` | `string` | 长度 256，非空，**唯一索引** | 去重键，见 §4.2 |
| `EventName` | `Event_Name` | `string` | 长度 256，非空 | 与 `IncomingEventInfo.MaxEventNameLength` 一致 |
| `EventData` | `Event_Data` | `byte[]` | 非空 | |
| `CreatedTime` | `Created_Time` | `DateTimeOffset` | 非空 | 入箱时刻（`IncomingEventInfo.CreatedTime`） |
| `ExtraProperties` | `Extra_Properties` | `string?` | `StaticConfig.CodeFirst_BigString`，可空 | JSON |
| `Status` | `Status` | `int` | 非空 | 0 待处理、1 已领取、2 已处理、3 已丢弃 |
| `RetryCount` | `Retry_Count` | `int` | 非空 | 宿主传入的重试次数，仅作记录 |
| `NextRetryTime` | `Next_Retry_Time` | `DateTimeOffset?` | 可空 | 早于该时刻不领取 |
| `ClaimToken` | `Claim_Token` | `string?` | 长度 64，可空 | 领取令牌 |
| `ClaimTime` | `Claim_Time` | `DateTimeOffset?` | 可空 | 领取时刻，用于超时释放 |
| `HandledTime` | `Handled_Time` | `DateTimeOffset?` | 可空 | 进入「已处理 / 已丢弃」的时刻，保留期由此起算 |

状态常量：`StatusPending = 0`、`StatusClaimed = 1`、`StatusProcessed = 2`、`StatusDiscarded = 3`。

与发件箱一致的写法：两个公开构造函数（无参供物化、`(Guid basicId)` 供映射——`BasicId` 是 `protected set`，不能用对象初始化器赋值）；时间列一律 `DateTimeOffset`。

**不用 `ModifiedTime` 作列名**：`XiHan.Framework.Data` 的 `DataExecuting` AOP 按属性名填审计字段（`SqlSugar/Extensions/EntityAuditExtensions.cs:72-83`，`ToModified` 覆盖写入 `ModifiedTime`）。收件箱的完结时刻是业务语义，不能被审计 AOP 改写，故取名 `HandledTime`。

**建表范围**：`Target = DbInitializationTarget.Platform`、不设 `IncludeModuleConnections`。按 `DbEntityTypeProvider.IsModuleDataSourceAllowed`（`:119-143`），未声明模块数据源、未标注 `IncludeModuleConnections` 的实体只在非模块库建表；`Target = Platform` 再排除租户独立库。结果：`sys_event_inbox` 只在平台主库建出来。标注本身也让它在 `TableInitialization.Mode = OptIn` 下照样参与建表。

**两个索引**：

- `ux_sys_event_inbox_dedup_key`（唯一）：去重的最终保证，也是 `ExistsByMessageIdAsync` 的查找路径
- `ix_sys_event_inbox_status`（`Status, Created_Time`）：领取的候选查询与清理都按 `Status` 过滤；已处理记录要保留一周，表会比发件箱大得多，没有它每 2 秒一次的候选查询就是全表扫描

`SugarIndex` 的字段参数按**属性名**匹配列（`CodeFirstProvider.cs:384`），因此用 `nameof(SysEventInbox.DedupKey)`，不写列名 `"Dedup_Key"`。索引名写死、不用 `{table}` 占位符。

### 4.2 映射 `EventInboxMapper`

`public static class EventInboxMapper`，与 `EventOutboxMapper` 同形：

**`ToEntity(IncomingEventInfo info)`**：

- `MessageId`：`string.IsNullOrWhiteSpace(info.MessageId) ? null : info.MessageId`
- `DedupKey`：有消息标识时等于 `MessageId`；没有时为 `"no-message-id:" + info.Id.ToString("N")`——**每条记录各不相同**，即无消息标识的事件不参与去重
- `CreatedTime`：`ToOffset(info.CreatedTime)`，`Kind` 分支与发件箱相同（`Utc` 零偏移、`Local` 本地偏移、`Unspecified` 当 UTC）
- `ExtraProperties`：空字典存 `null`，否则 `JsonSerializer.Serialize`
- `Status = StatusPending`，`RetryCount = 0`，其余可空列为 `null`

**`ToEventInfo(SysEventInbox entity)`**：五参构造函数，`MessageId` 为 `null` 时还原为 `string.Empty`（契约类型是非空 `string`，还原后的消息标识没有任何消费方读取，见 D12）；`CreatedTime` 取 `UtcDateTime`；`ExtraProperties` 逐项写回（`JsonElement` 值，`GetCorrelationId()` 往返保真）。

**`internal static DateTimeOffset ToOffset(DateTime value)`**：`SqlSugarEventInbox.RetryLaterAsync` 也要用它转换 `nextRetryTime`，因此是 `internal` 而非 `private`。

前缀常量 `public const string NoMessageIdKeyPrefix = "no-message-id:";` 放在映射类上。

### 4.3 落点：固定为宿主布局主库

收件箱的每一次读写都这样取客户端：

```csharp
using (_currentTenant.Change(null))
{
    var client = _clientResolver.GetCurrentClient();
    // 本次读写全部在此块内完成
}
```

**为什么固定在宿主布局主库**：写入方与读取方必须落在同一个库。读取方是 `EventBoxInboxProcessorHostedService`，它在自建的服务作用域里运行、没有租户上下文，`GetCurrentClient()` 解析到宿主布局主库。写入方的三条路径里，`LocalDistributedEventBus.PublishToEventBusAsync` 可能处于租户上下文：若不切换，库隔离租户的事件会写进租户库，处理循环永远读不到它——**事件静默滞留，不报错**（§5 ⑥）。在写入与读取两侧都显式 `Change(null)`，两侧就共用同一条解析路径 `SqlSugarClientResolver.ResolveCurrentLayout()` 的「无租户」分支，落点一致由构造保证，而不是由调用环境碰巧保证。

**为什么不照发件箱推导落点**：发件箱靠 `GetEnlistedConfigIds()` 找到业务所在的库，前提是入箱发生在业务的事务型工作单元里。收件箱入箱时没有这样的工作单元（§1.2 第 2 条），推导不出任何东西。

**事务**：`GetCurrentClient()` 内部走 `GetClient` → `EnlistCurrentUnitOfWork`，只在存在「未完成的事务型工作单元」时才登记。正常路径下入箱没有这样的工作单元，写入自动提交。本份不主动开事务，也不绕开解析器去取不登记的连接（见 D5 与 §7「在事务型工作单元内入箱」）。

### 4.4 入箱与去重

```
entity   = EventInboxMapper.ToEntity(incomingEvent)
dedupKey = entity.DedupKey

在宿主主库上：
    try:
        Insertable(entity).ExecuteCommandAsync()
    catch (Exception ex) when ex is not OperationCanceledException:
        若 Queryable<SysEventInbox>().AnyAsync(DedupKey == dedupKey) 为 false：
            throw;               原样重抛，不吞
        记录 Debug 日志，视为重复消息，正常返回
```

**两层去重**：

1. **先查**：`AddToInboxAsync` 在入箱前调 `ExistsByMessageIdAsync`，命中即视为已处理、不再入箱（`DistributedEventBusBase.cs:320-328`）。这一层挡住绝大多数重复投递
2. **后拦**：两个实例几乎同时收到同一消息，都查不到、都去插——唯一索引只放行一个，另一个的插入抛异常

**插入失败后回查**：方言相关的「唯一冲突」错误码各不相同（MySQL 1062、PostgreSQL 23505、SQL Server 2627/2601、SQLite 2067），SqlSugar 也不统一包装。本份不识别错误码，而是在失败后按去重键回查：查得到，说明失败原因就是它已在库，按重复处理；查不到，说明是别的故障，原样重抛。这是方言无关的判定。

**重抛的必要性**：若把插入失败一律吞掉，建表缺失、连接中断之类的故障会让 `EnqueueAsync` 正常返回，`AddToInboxAsync` 返回 `true`，broker 随即 ack——**事件丢失且无任何异常**（§5 ⑧）。

**`ExistsByMessageIdAsync(string messageId)`**：空白返回 `false`（与 `DefaultEventInbox` 一致）；否则在宿主主库上 `AnyAsync(item => item.DedupKey == messageId)`。**不限状态**：已处理、已丢弃的记录同样算存在，这正是去重要拦下的情况。

### 4.5 领取：三步抢占

宿主处理循环是「取 → 逐条处理 → 标记」，**没有分布式锁**。`N` 个实例会取到同一批记录、各处理一次。互斥责任落在 `GetWaitingEventsAsync` 的实现内，语义是**「领取并返回」**，必须写进 XML 文档注释。

结构与发件箱的 `ClaimFromDatabaseAsync` 相同，单库、最多三轮：

```
若 filter 不为 null：抛 NotSupportedException
若 maxCount <= 0：返回空集合
cancellationToken.ThrowIfCancellationRequested()

在宿主主库上，最多三轮：
    now         = DateTimeOffset.UtcNow
    staleBefore = now - ClaimTimeout
    claimToken  = Guid.NewGuid().ToString("N")          每轮新建

    可领取 := (Status == 待处理 且 (NextRetryTime 为空 或 NextRetryTime <= now))
           或 (Status == 已领取 且 ClaimTime 不为空 且 ClaimTime < staleBefore)

    1. 选候选：Where(可领取).OrderBy(CreatedTime).Take(maxCount).Select(BasicId)
       候选为空 → 返回空集合
    2. 抢占：Updateable.SetColumns(Status=已领取, ClaimToken=claimToken, ClaimTime=now)
             .Where(主键在候选内 且 可领取)            ← 可领取条件必须原样重复
       受影响行数为 0 → 候选全被抢走，进入下一轮
    3. 取回：Where(ClaimToken == claimToken).OrderBy(CreatedTime)

三轮都没抢到 → 返回空集合
映射为 IncomingEventInfo 返回
```

**可领取条件比发件箱多一项 `NextRetryTime`**：`RetryLaterAsync` 把记录放回待处理并设下次重试时刻，未到时刻不应被领取。

**令牌**：单库，每轮一个新令牌，与发件箱每库每轮新建的做法一致。

**领取超时**：复用 `XiHanSqlSugarEventBoxOptions.ClaimTimeout`（默认 5 分钟）。实例在处理途中退出时，记录在超时后重新变为可领取。

**不做逐库隔离、不遍历**：只有一个库。该库不可达时异常直接抛给宿主循环，由其 `catch` 记录日志后进入下一轮（`EventBoxInboxProcessorHostedService.cs:61-64`）。

### 4.6 状态流转

所有更新都在宿主主库上按主键执行 `Updateable<SysEventInbox>().SetColumns(...).Where(item => item.BasicId == id)`：

| 方法 | `Status` | `RetryCount` | `NextRetryTime` | `ClaimToken` / `ClaimTime` | `HandledTime` |
| --- | --- | --- | --- | --- | --- |
| `MarkAsProcessedAsync` | 已处理 | 不动 | `null` | `null` | `now` |
| `MarkAsDiscardAsync` | 已丢弃 | 不动 | `null` | `null` | `now` |
| `RetryLaterAsync` | **待处理** | 传入值 | 传入值，`null` 时取 `now` | `null` | 不动 |

`RetryLaterAsync` 的 `nextRetryTime` 是 `DateTime?`，经 `EventInboxMapper.ToOffset` 转换；为 `null` 时取 `DateTimeOffset.UtcNow`，与 `DefaultEventInbox`（`:108`）的 `nextRetryTime ?? DateTime.UtcNow` 一致。

**`RetryLaterAsync` 必须把 `Status` 改回待处理并清空领取字段**：只改 `NextRetryTime` 而把状态留在「已领取」，记录要等 `ClaimTimeout`（5 分钟）超时才会重新可领取，重试延迟从 10 秒静默变成 5 分钟（§5 ⑤）。

`SetColumns` 的成员初始化里写 `ClaimToken = null` 会生成参数化的 `= NULL`（`MemberInitExpressionResolve.cs:164-181`，常量分支；可空值类型按其基础类型定参数的 `DbType`）。

对不存在的主键执行只更新 0 行，不抛异常——与 `DefaultEventInbox` 的 `TryGetValue` 未命中即静默一致。

### 4.7 清理

```
cutoff = DateTimeOffset.UtcNow - InboxRetentionPeriod
Deleteable<SysEventInbox>()
    .Where((Status == 已处理 或 Status == 已丢弃) 且 HandledTime 不为空 且 HandledTime <= cutoff)
```

**只删完结的记录**：待处理与已领取的记录无论多旧都保留。积压了一周的待处理事件被当成「过期」删掉，就是无声丢事件（§5 ⑦）。

**保留期**：`XiHanSqlSugarEventBoxOptions.InboxRetentionPeriod`，默认 `TimeSpan.FromDays(7)`，与 `DefaultEventInbox.RetentionPeriod`（`:17`）相同。保留期就是去重窗口。

### 4.8 选项

`XiHanSqlSugarEventBoxOptions`（配置节 `XiHan:EventBus:SqlSugar`，已存在）追加：

```csharp
/// <summary>
/// 收件箱已处理与已丢弃记录的保留期，超过该时长的记录被清理
/// </summary>
public TimeSpan InboxRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
```

`ClaimTimeout` 的 XML 注释由「超过该时长仍未删除的已领取记录可被重新领取」改为「超过该时长仍未完结的已领取记录可被重新领取，收发件箱共用」——收件箱的已领取记录处理完不是删除，而是改为已处理。

### 4.9 注册

在现有的 `AddXiHanSqlSugarEventBus` 里追加，不新增扩展方法：

```csharp
services.Configure<XiHanDistributedEventBusOptions>(options =>
{
    options.Outboxes.Configure(config => config.ImplementationType = typeof(SqlSugarEventOutbox));
    options.Inboxes.Configure(config => config.ImplementationType = typeof(SqlSugarEventInbox));
});

services.TryAddScoped<SqlSugarEventOutbox>();
services.Replace(ServiceDescriptor.Scoped<IEventOutbox, SqlSugarEventOutbox>());

services.TryAddScoped<SqlSugarEventInbox>();
services.Replace(ServiceDescriptor.Scoped<IEventInbox, SqlSugarEventInbox>());
```

两个方向与发件箱相同：**options 后写覆盖**（本模块依赖 `XiHanEventBusModule`，动作后执行，直接赋值即覆盖默认的 `DefaultEventInbox`）；**`TryAdd` 先写胜出**（主包已 `TryAddSingleton<IEventInbox>`，本包必须 `Replace`，用 `TryAdd` 是空操作）。

**生命周期由 Singleton 改为 Scoped**：`ISqlSugarClientResolver` 是 Scoped。经核对框架内**没有任何构造函数注入 `IEventInbox`**——两处消费点（`DistributedEventBusBase.cs:318`、`EventBoxInboxProcessorHostedService.cs:97`）都从新建的作用域按 `ImplementationType` 解析，不会产生被捕获依赖。写入 README 与 PR 描述。

模块类 `XiHanSqlSugarEventBusModule` 的 `<remarks>` 中「收件箱在后续版本提供」一句改为「本模块以 SqlSugar 收发件箱替换默认的进程内收发件箱」。

### 4.10 构造函数

```csharp
public SqlSugarEventInbox(
    ISqlSugarClientResolver clientResolver,
    ICurrentTenant currentTenant,
    IOptions<XiHanSqlSugarEventBoxOptions> options,
    ILogger<SqlSugarEventInbox> logger)
```

命名空间 `XiHan.Framework.EventBus.SqlSugar.Inbox`，文件 `Inbox/SqlSugarEventInbox.cs`，与 `Outbox/` 平行。`ICurrentTenant` 来自 `XiHan.Framework.MultiTenancy.Abstractions`，经 `XiHan.Framework.Data` 传递引用，无需新增项目引用。

## 五个共同问题

| 问题 | 本包收件箱的答案 | 依据 |
| --- | --- | --- |
| **分表与否** | **不分表** | 数据按保留期滚动清理，总量有界；去重靠唯一索引，分表后唯一性只在单张子表内成立，跨月的重复消息会漏过；领取也要跨表 |
| **主键类型** | 契约自带的 **`Guid`**（`IncomingEventInfo.Id`） | 契约的 `MarkAsProcessedAsync` / `RetryLaterAsync` / `MarkAsDiscardAsync` 都按 `Guid` 定位，主键查找；由 `IDistributedIdGenerator<Guid>` 的顺序 Guid 生成，无索引碎片。**主键不是去重键**，去重键另立一列 |
| **是否参与工作单元事务** | **不参与**，也不主动脱离 | 入箱发生在无业务工作单元的新作用域里（§1.2）。仍注册为 Scoped，因为 `ISqlSugarClientResolver` 是 Scoped |
| **是否需要多库** | **单库**：宿主布局主库，`Target = Platform` | 入箱时没有业务所在的库；读写两侧显式切到无租户上下文，落点一致由构造保证（§4.3） |
| **顶替方式** | options 直接赋值 + DI **`Replace`** | 主包 `XiHanEventBusServiceCollectionExtensions.cs:115,117` 用的是 `TryAddSingleton`，`TryAdd` 顶替是空操作 |

## 待确认的决策

以下每一条都是本设计替用户做的选择。**请逐条审阅。**

| # | 决策 | 默认值 | 理由 | 若改动会影响什么 |
| --- | --- | --- | --- | --- |
| D1 | **去重键** | `MessageId`（不用事件 `Id`，不用内容哈希） | `Id` 在每次收到时新生成（`DistributedEventBusBase.cs:332`），同一消息两次收到的 `Id` 不同，按 `Id` 去重永不命中。内容哈希会把两条内容恰好相同的**不同**业务事件（例如两次「库存 −1」）误判为重复而丢掉其一。`MessageId` 是 broker 给的消息身份，框架自己的发布路径都会设置它 | 改成内容哈希：需新增哈希列与计算，且有误合并风险。改成 `Id`：去重失效 |
| D2 | **去重的强度** | 唯一索引 + 插入失败后回查 | 仅「先查后插」（`DefaultEventInbox` 的做法）在多实例同时收到同一消息时有竞态窗口，两边都插入、都处理。唯一索引是唯一方言无关的原子保证；回查让我们不必识别各库的错误码 | 退回「仅先查后插」：去掉唯一索引与回查，实现更简单，但多实例下偶发重复处理 |
| D3 | **无消息标识时的去重键** | `"no-message-id:" + Id.ToString("N")`，即不去重 | 契约允许空消息标识（`AddToInboxAsync` 的 `string? messageId`）。没有标识就无从判断重复；若给一个常量或空串，第二条无标识消息会被唯一索引拦下、又被回查判为重复，**静默丢弃** | 若想「无标识就拒收」：`EnqueueAsync` 抛异常，外部生产者不设 `MessageId` 的消息将无法消费 |
| D4 | **入箱落在哪个库** | 宿主布局主库：读写两侧都在 `ICurrentTenant.Change(null)` 内调 `GetCurrentClient()` | 入箱时没有业务工作单元，推导不出业务所在的库；读取方（处理循环）无租户上下文。两侧显式对齐，库隔离租户的事件才不会写进处理循环读不到的库 | 若要按租户分库存放：处理循环需遍历所有租户库，而它没有租户列表——需改宿主服务，超出本份范围 |
| D5 | **事务** | 不开事务、不主动脱离环境工作单元（走解析器的常规登记逻辑） | 正常路径下入箱时没有未完成的事务型工作单元，写入自动提交。绕开解析器（`AsTenant().GetConnectionScope`）虽能强制脱离，但与本包发件箱的取客户端方式不一致，且测试桩不支持 | 若要强制脱离：改用 `AsTenant().GetConnectionScope(configId)`，桩解析器需同步支持 |
| D6 | **处理的并发语义** | 三步抢占（候选 → 条件 UPDATE 盖令牌 → 按令牌取回），`WHERE` 同时含候选主键与可领取条件，每轮新令牌，最多三轮；复用 `ClaimTimeout` | 多实例的处理循环会取到同一批记录。与发件箱已被真实数据库验证的协议同构，只多一项 `NextRetryTime` 条件 | 若为收件箱单设超时：新增 `InboxClaimTimeout` 选项。若不做互斥：多实例重复处理，收件箱失去意义 |
| D7 | **已处理记录的保留** | 标记后保留；`InboxRetentionPeriod` 默认 7 天；由宿主每轮调用的 `DeleteOldEventsAsync` 清理，**不节流** | 去重依赖这些记录，处理完就删等于没有去重。7 天与 `DefaultEventInbox` 相同。清理是一条命中 `(Status, …)` 索引的条件 `DELETE`，每轮只删掉刚越过界限的少量行 | 若嫌每 2 秒一次 `DELETE` 太频繁：需要一个单例节流器（Scoped 实例上的字段跨作用域不共享），增加一个注册 |
| D8 | **已丢弃记录的保留** | 与已处理同一保留期 | 与 `DefaultEventInbox` 一致；丢弃的记录也承担去重（同一坏消息再来不再重试 5 次） | 若要为排障保留更久：新增独立选项 `InboxDiscardedRetentionPeriod` |
| D9 | **失败重试** | 记次数（`Retry_Count` 列，写入宿主传来的值）；阈值判定与丢弃留在宿主服务 | 契约把次数作为参数传入，判定权在 `EventBoxInboxProcessorHostedService`；本份禁止改动它 | 若要持久化的重试上限：`RetryLaterAsync` 注入 `IOptions<EventBoxProcessingOptions>`，按 `max(库中次数 + 1, 传入值)` 计数、达到上限直接置丢弃——这改变了 `RetryLater` 的契约语义，需用户明确同意 |
| D10 | **多库** | 不遍历 `GetCurrentLayoutConfigIds()`；`[TableInitialization(Target = Platform)]`，不设 `IncludeModuleConnections` | 所有记录都在一个库，遍历只会空转；表只需建在平台主库 | 若将来按租户分库存放，需同时改建表标注与宿主循环 |
| D11 | **`filter` 参数** | 非 `null` 抛 `NotSupportedException` | 与发件箱一致的 fail-closed；框架自身从不传 | 若要支持：需要把 `IIncomingEventInfo` 上的表达式翻译到实体列 |
| D12 | **还原时的空消息标识** | `null` 还原为 `string.Empty` | 契约的 `MessageId` 是非空 `string`；出箱后没有任何代码读取它（`ProcessFromInboxAsync` 只用 `EventName`、`EventData`、`GetCorrelationId()`） | 改为 `entity.MessageId!` 可逐位还原 `null`，但要对编译器撒谎 |
| D13 | **索引** | 唯一索引 `ux_sys_event_inbox_dedup_key`；普通索引 `ix_sys_event_inbox_status (Status, Created_Time)` | 前者是去重保证；后者服务于领取与清理——保留一周的已处理记录会让无索引的候选查询变成每 2 秒一次全表扫描。本仓库此前没有用过 `SugarIndex`，这是第一处 | 去掉普通索引：功能不变，大表下领取变慢 |
| D14 | **根 `README.md` / `README_cn.md`** | **不改** | 两者的表格是「常用包」而非全量清单，`Auditing.SqlSugar` 与 `EventBus.SqlSugar` 都不在其中；全量清单在 `framework/README*.md`（`:92` 已有本包条目） | 若要列入：两份各加一行，但需说明为何把一个持久化子包列为「常用」 |
| D15 | **实现分支** | 从 `dev` 开 `feat/eventbus-sqlsugar` | 共用简报的约定 | PR2 的发件箱提交目前在 `feat/sqlsugar`；PR 如何组装由派工者决定 |
| D16 | **依赖框架现有的「提交后发布」顺序** | 接受该依赖，并在 §7 登记 | §1.2 第 2 条「入箱时没有未完成的事务型工作单元」成立，是因为 `UnitOfWork.CompleteAsync` 先提交、置 `IsCompleted`，再发布缓冲的分布式事件（`UnitOfWork.cs:279-298`），且 `AmbientUnitOfWork.GetCurrentByChecking` 跳过已完成的工作单元（`:44-49`）。同一顺序也使默认发布路径绕过发件箱（`AddToOutboxAsync` 拿到 `null` 返回 `false`）；是否修改该顺序由用户另行决定 | 若框架改为提交前发布或让发件箱在提交前入箱：`LocalDistributedEventBus` 路径上的收件箱入箱可能落进业务事务（见 §7「在事务型工作单元内入箱」），须重新核对 D4、D5 与 §5 ⑥ |

## 5. 会静默失效的陷阱

以下每一条错了**都不会报错**，多数连测试都照样全绿。

**① 用事件 `Id` 当去重键。**

入箱时的 `Id` 每次都新生成。按 `Id` 查重永远查不到，按 `Id` 建唯一索引永远不冲突——**去重完全失效，但没有任何异常**。

更隐蔽的是测试：若用例拿**同一个** `IncomingEventInfo` 实例入箱两次，两次的 `Id` 相同，主键冲突会让用例「看起来」去重成功了。**去重用例必须构造两个 `Id` 不同、`MessageId` 相同的实例。**

**② 无消息标识时去重键写成空串、`null` 或常量。**

写成常量：第二条无标识消息撞上唯一索引，回查又按该常量查到了第一条，于是被判为重复、**静默丢弃**。写成 `null`：SQL Server 的唯一索引只允许一个 `NULL`，同样撞上。去重键在无标识时必须每条各不相同。

**③ `MarkAsProcessedAsync` 删行。**

发件箱「投递后删除」的直觉搬到这里就错了。行被删掉后，broker 重投同一消息时 `ExistsByMessageIdAsync` 查不到它，消息被再处理一遍。**所有测试照样全绿**——除非有专门断言「标记已处理后仍判定为存在」的用例。

**④ 抢占的 `WHERE` 丢掉可领取条件。**

只按主键更新令牌，两个并发领取者会先后覆盖同一行的令牌，各自的取回步骤都可能拿到它，**同一事件被处理两次**。条件必须是「主键在候选集内 **且**（待处理且重试时刻已到 **或** 领取已超时）」。SQLite 单连接的用例构造不出这个竞态，只有真实数据库并发用例能发现。

**⑤ `RetryLaterAsync` 不把状态改回待处理。**

只写 `NextRetryTime`、状态留在「已领取」：记录要等 `ClaimTimeout` 超时才会被重新领取。重试延迟从 10 秒静默变成 5 分钟，功能「看起来」正常。反过来，改了状态却不清令牌不影响正确性，但会留下误导排障的脏数据——两者都要做。

**⑥ 读写两侧没有切到无租户上下文。**

库隔离租户上下文中入箱，记录写进租户库；处理循环在无租户上下文中读宿主主库，**永远读不到它**。事件静默滞留，不报错，不重试。只在库隔离租户 + 本地分布式总线直发的组合下出现，单租户测试发现不了——必须有专门的租户用例。

**⑦ 清理删掉了未完结的旧记录。**

条件只写「`CreatedTime` 早于保留期」而不限状态：积压超过一周的待处理事件、处理超慢的已领取事件都会被删掉，**无声丢事件**。条件必须限定「已处理或已丢弃」且按 `HandledTime` 判断。

**⑧ 插入失败一律吞掉。**

「反正唯一冲突就是重复」的简化会把建表缺失、连接中断、列超长等故障也吞掉。`EnqueueAsync` 正常返回 → `AddToInboxAsync` 返回 `true` → broker ack → **事件丢失**。必须回查确认是重复才吞，否则原样重抛。

**⑨ 注册的覆盖方向。**

options 后写覆盖，`TryAdd` 先写胜出。用 `TryAddScoped<IEventInbox, SqlSugarEventInbox>()` 顶替是空操作——`DefaultEventInbox` 留在容器里，options 里的 `ImplementationType` 却指向 `SqlSugarEventInbox`，两处不一致，而宿主与 `AddToInboxAsync` 都按 `ImplementationType` 解析，**表面上一切正常**。注册测试要同时断言两处。

## 6. 测试策略

分两层，沿用 P4/P6 的划分。测试项目沿用现有的 `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/`，不新建项目。

**夹具**：新增 `InboxTestContext`，两个临时 SQLite 库分别模拟宿主主库（`"Default"`）与一个租户库（`"Tenant_1001"`），两库都建 `sys_event_inbox`。复用 `OutboxTestContext.cs` 里的 `StubClientResolver`，为它新增一个可选属性：

```csharp
public Func<string>? CurrentConfigIdSelector { get; set; }
```

`GetCurrentClient()` 改为 `GetClient(CurrentConfigIdSelector?.Invoke() ?? _currentConfigId)`。属性默认 `null`，发件箱的既有用例行为不变。收件箱夹具把它设为「当前租户为空 → 主库，否则 → 租户库」，并配一个测试用 `FakeCurrentTenant : ICurrentTenant`。

**第一层 —— SQLite，CI 强门禁执行**

- 实体：表名、不分表、主键、列名、建表标注（`Target = Platform`、`IncludeModuleConnections == false`）、唯一索引标注
- 映射：字段往返、去重键规则（有标识 / 无标识且各不相同）、空标识还原为空串、时间归一到 UTC、关联标识往返
- 建表：`sys_event_inbox` 能建出来；两行相同 `Dedup_Key` 时第二行插入失败（证明唯一索引真的建出来了）
- 入箱：记录为待处理；**两个 `Id` 不同、`MessageId` 相同的实例入箱，只留一条且不抛异常**；两个无标识事件留两条；租户上下文中入箱写主库；插入被触发器以非唯一冲突的原因拒绝时入箱原样抛出（回查查不到，不得吞掉）
- 判重：入箱后为 `true`；未知标识与空白为 `false`；租户上下文中仍查主库
- 领取：标记并带令牌；不重复领取；超时后可重新领取；重试时刻未到不领取、已到则领取；已处理与已丢弃不领取；按创建时间升序、不超过上限；`filter` 非空抛异常；`maxCount` 非正返回空；已取消的令牌抛取消异常；租户上下文中仍领取主库
- 状态流转：标记已处理后不再领取、仍判定为存在、`HandledTime` 有值、令牌清空；延后重试把状态改回待处理、清令牌、记次数，时刻已到立即可领取、未到暂不领取，`null` 立即可领取；标记丢弃同上
- 清理：四行（过期已处理、过期已丢弃、未过期已处理、很旧的待处理）清理后只剩后两行
- 注册：`ImplementationType`、`IEventInbox` 描述符为 `Scoped` 的 `SqlSugarEventInbox`、具体类型注册为 `Scoped`

写用例时有三条来自实测的约束：

- **不要写「读回的时间等于写入的时间」这类断言**。SQLite 侧的 `DateTimeOffset` 不保存偏移，读回时偏移由 `DateTime.Kind` 反推，在 UTC+8 机器上写入 `+00:00` 会读回 `+08:00`、瞬时被平移 8 小时。时间比较都放在 SQL 侧、两侧同向归一；**构造「未到 / 已到」「过期 / 未过期」时一律用 ±1 天以上的间隔**，大于任何时区偏移。
- **「候选被抢光则另选一批重试」只能由第二层覆盖**。第一层构造不出跨实例竞态，丢掉三轮循环 CI 不会有任何反应。实现时以本 spec 与计划给出的代码为准，不以用例是否变绿为准。
- **去重用例必须用两个 `Id` 不同的实例**（§5 ①）。

**第二层 —— 真实数据库，本机执行，CI 自动跳过**

沿用 `XIHAN_TEST_MYSQL` 与 `Assert.SkipWhen(...)`（范式：`OutboxConcurrencyTests.cs`、`framework/test/XiHan.Framework.EventBus.Redis.Tests/RedisPendingBehaviorTests.cs`）：

- **并发领取不重复**：200 条待处理，8 个工作者循环领取，领到的标识无重复、总数 200
- **并发入箱同一消息只留一条**：8 个工作者同时以同一 `MessageId`、各自不同的 `Id` 入箱，全部不抛异常，库里恰好 1 条——这是「唯一索引 + 回查」在真实并发下的证明

测试平台是 Microsoft.Testing.Platform：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。SQLite 临时库连接串必须带 `Pooling=False`。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 去重窗口等于保留期 | 已处理记录在 `InboxRetentionPeriod`（默认 7 天）后被清理，此后同一消息再来会被当作新消息处理。处理器仍须幂等 |
| 无消息标识不去重 | 没有 `MessageId` 的消息每次都入箱、都处理 |
| 重试计数在进程内 | 宿主服务的计数器按实例、按进程生命周期累计。多实例轮流领到同一条失败事件、或进程重启后，计数从 1 重来；一条始终失败的事件实际重试次数可能超过 `MaxInboxRetryCount`。库中 `Retry_Count` 只记录宿主传来的值 |
| 处理超时会重复处理 | 宿主逐条顺序处理一批（默认 100 条）。整批处理耗时超过 `ClaimTimeout`（默认 5 分钟）时，尾部记录会被其他实例重新领取并处理。`ClaimTimeout` 须大于单批最长处理时间 |
| 至少一次处理 | 处理成功与标记已处理之间进程退出，记录在领取超时后被重新处理 |
| 在事务型工作单元内入箱 | 若调用方在未完成的事务型工作单元内触发入箱（例如在事务内以 `onUnitOfWorkComplete: false, useOutbox: false` 经本地分布式总线发布），入箱写入会登记进该事务：事务回滚则入箱记录一同消失；且主库被登记后，同一工作单元内的发件箱入箱可能因「登记了多个连接」而抛异常。正常路径（broker 消费、工作单元提交后发布）不触发 |
| 唯一冲突在事务内的回查 | 若入箱处于事务中（上一条），PostgreSQL 在唯一冲突后整个事务进入中止状态，回查查询本身会失败，抛出的是回查的异常而非「重复」判定 |
| 消息标识长度 | `Message_Id` 与 `Dedup_Key` 长度 256。更长的消息标识在严格模式的数据库上入箱失败并抛异常（SQLite 不校验长度） |
| 租户独立库 | 收件箱只在宿主布局主库。库隔离租户收到的事件也存在这里，处理器按事件自身携带的信息切换租户 |
| 依赖「提交后发布」的框架顺序 | 「入箱时没有业务事务」这一前提来自 `UnitOfWork.CompleteAsync` 先提交后发布分布式事件、且已完成的工作单元不再是当前工作单元。**修改该顺序的人必须重新核对收件箱的落点与事务行为**（决策 D16）。同一顺序使默认的 `onUnitOfWorkComplete: true` 发布绕过发件箱，本包发件箱只在未完成的工作单元内以 `onUnitOfWorkComplete: false` 发布、且配置了 `Outboxes` 时才被写入（事务型工作单元时与业务同事务） |
| 清理不节流 | 宿主每轮（默认 2 秒）每个实例都执行一次条件 `DELETE` |
| `IEventInbox` 生命周期 | 由 Singleton 改为 Scoped。框架内无构造函数注入该接口的地方；应用若自行构造函数注入 `IEventInbox` 到单例中，会得到被捕获依赖 |
| 升级 | 新增表 `sys_event_inbox`。未开启 `EnableDbInitialization` 与 `EnableTableInitialization`（二者默认都为 `false`）又未手工建表时，首次入箱抛「表不存在」——该异常会让 broker 消费失败并重投，而不是静默丢失 |
| 抢占的受影响行数口径 | 与发件箱相同：每次抢占都写入新的 `Claim_Token` 与 `Claim_Time`，「匹配行数」与「实际变更行数」两种口径下都大于 0 |
| `filter` 未支持 | 非 `null` 抛 `NotSupportedException` |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真库用例在无环境变量时跳过；`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 是已知的无关抖动）
- 真实数据库的两个并发用例在本机通过（`XIHAN_TEST_MYSQL` 指向可用实例）
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 文档站条目（`docs/packages/eventbus-sqlsugar.md`）与 `framework/README*.md` 的模块清单反映收件箱
- 提交信息为中文 Conventional Commits，作用域 `eventbus-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本包无关的文档

## 9. 下一份

P7 是 `EventBus.SqlSugar` 的最后一份，完成后第二个 PR（`EventBus.SqlSugar`：P3、P4、P6、P7）方可提交。

系列的后续按 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 5 节的顺序进行，下一个是 `Settings.SqlSugar`。其中 `Tasks.SqlSugar` 的作业领取可直接复用本份与 P4 的三步抢占协议。
