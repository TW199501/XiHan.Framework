# P3：EventBus.SqlSugar 包骨架与发件箱实体映射 设计

- **日期**：2026-09-21
- **状态**：已评审通过，待实现
- **对应计划**：`.superpowers/plans/2026-09-21-eventbus-sqlsugar-p3-outbox-entity.md`
- **系列**：SqlSugar 持久化层 P3 / 规划 7 份，**现存 P1–P5**（P6–P7 待写）。P1–P2 `Auditing.SqlSugar`，P5 `Data`，P3–P4、P6–P7 `EventBus.SqlSugar`

> 本文档**自成一体**。实现 P3 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套，且**单元测试仍可能通过**，问题要到运行期才暴露。
>
> 第 2 节规定三件事：代码去哪找、文档去哪找、哪些不能做。
> 第 5 节列出六条陷阱。其中**第一条与 P1/P2 的约定不同**——本包主键是 `Guid` 不是雪花 `long`，照搬 P1/P2 会做错。

---

## 1. 背景与目标

`XiHan.Framework.EventBus` 实现了完整的发件箱模式骨架：`DistributedEventBusBase.AddToOutboxAsync` 在工作单元内把事件入箱，`EventBoxOutboxSenderHostedService` 轮询取出、投递、删除。

但 `IEventOutbox` 的唯一实现 `DefaultEventOutbox` 是 `ConcurrentDictionary`——**发件箱模式因此失去意义**：事件与业务数据不在同一事务，进程退出即丢，跨实例也不共享。

**P3 只做三件事**：建立 `XiHan.Framework.EventBus.SqlSugar` 包骨架、定义发件箱实体、实现 `OutgoingEventInfo` 与实体的双向映射。

**P3 结束时的状态**：表能被建出来，映射有往返测试，**`IEventOutbox` 尚未实现**——`DefaultEventOutbox` 仍是容器里的实现。入箱与领取是 P4 的事。

**成功标准**：

1. `sys_event_outbox` 表能在 SQLite 下建出来，列齐全
2. `OutgoingEventInfo` → 实体 → `OutgoingEventInfo` 往返后，`Id`、`EventName`、`EventData`、`CreatedTime`（归一到 UTC）与 `GetCorrelationId()` 全部保真
3. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 框架既有实现 —— 最高优先，本包必须与其一致**

```
framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/
  IEventOutbox.cs             契约（P3 不实现，但实体要能支撑它）
  IOutgoingEventInfo.cs       Id 是 Guid，继承 IHasExtraProperties
  OutgoingEventInfo.cs        只读属性 + protected 无参构造，不能当实体
framework/src/XiHan.Framework.EventBus/Distributed/
  DistributedEventBusBase.cs:250-270   入箱调用点，事件 Id 由 GuidGenerator 生成
  DistributedEventBusBase.cs:61        GuidGenerator 是 IDistributedIdGenerator<Guid>
  DefaultEventOutbox.cs                被替换的内存实现
framework/src/XiHan.Framework.Data/SqlSugar/
  Entities/SugarEntity.cs              列映射范本（Basic_Id / Row_Version）
  Entities/SugarFullAuditedEntity.cs   完整列映射示例
  Clients/ISqlSugarClientResolver.cs   客户端解析（P4 才用，P3 先读懂）
framework/src/XiHan.Framework.EventBus.Kafka/
  XiHan.Framework.EventBus.Kafka.csproj   兄弟子包的 csproj 范本
  XiHanKafkaEventBusModule.cs             兄弟子包的模块类范本
framework/src/XiHan.Framework.Data/README.md   README 七段结构范本
framework/src/XiHan.Framework.ObjectMapping/Extensions/Data/
  ExtraPropertyDictionary.cs   就是 Dictionary<string, object?>
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/
  Abstract/CodeFirstProvider/CodeFirstProvider.cs:733   byte[] ↔ blob 的映射
  Infrastructure/StaticConfig.cs:16                     CodeFirst_BigString 常量
  Abstract/InsertableProvider/                          插入
```

当前引用版本：`SqlSugarCore 5.1.4.221`（见 `framework/src/XiHan.Framework.Data/XiHan.Framework.Data.csproj`）。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 建表与实体元数据 | `實體管理EntityMaintenance.md`、`庫表管理DbMaintenance.md` |
| 列类型配置 | `修改配置.md` |
| 插入数据 | `插入數據.md` |
| Json 类型列 | `Json類型.md` |

`sqlsugar-mcp` 的 notes 语料与该目录是同一批文件，直接读目录即可，无需接入 MCP。

仓库自身文档：`docs/packages/data.md`（表名约定）、`docs/packages/eventbus.md`（发件箱现有行为）。

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

### 2.4 P3 特有的禁止事项

- **不实现 `IEventOutbox`**。P3 的模块类是空装配，`DefaultEventOutbox` 保持为容器里的实现。入箱与领取是 P4。
- **不让实体分表**。发件箱是短命队列，发完即删，分表只会让 P4 的抢占复杂化。
- **不引入映射框架**。字段少且一一对应，引入 AutoMapper 会让上游审查质疑必要性。
- **不给 `OutgoingEventInfo` 加 SqlSugar 特性**，也不改它。它在 `EventBus.Abstractions` 主包，主包不依赖 SqlSugar。
- **不新增 `PackageReference`**。SqlSugar 经 `XiHan.Framework.Data` 传递引入。
- **不改动 `EventBoxOutboxSenderHostedService` 或任何既有投递逻辑**。

## 3. 非目标

- **不实现入箱与领取**（P4）。
- **不做多库遍历发送**（P6）。
- **不做收件箱**（P6）。
- **不改 `IEventOutbox` / `IEventInbox` 契约**。原子领取在 P4 于实现内解决，不动 `EventBus.Abstractions` 的公开契约。
- **不做另外 12 个 Store**。
- **不连接真实数据库**。P3 无并发语义，SQLite 足够。真库并发测试在 P5。

## 4. 设计

### 4.1 包结构

沿用仓库既有的兄弟子包模式（`EventBus.Kafka`、`EventBus.RabbitMQ`、`EventBus.Redis`）：主包不依赖实现包，实现包依赖主包与 `XiHan.Framework.Data`。

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  XiHan.Framework.EventBus.SqlSugar.csproj
  XiHanSqlSugarEventBusModule.cs      P3 阶段为空装配
  README.md                           固定七段结构
  Entities/SysEventOutbox.cs
  Mapping/EventOutboxMapper.cs

framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  EventOutboxEntityTests.cs     实体元数据断言
  EventOutboxMapperTests.cs     映射往返，不碰数据库
  TableInitializationTests.cs   SQLite 建表
```

- csproj 按序 Import `netcore` / `common` / `version` / `nuget` 四个 props，使用 `Microsoft.NET.Sdk`
- 模块类命名依兄弟包惯例为 `XiHanSqlSugarEventBusModule`（对照 `XiHanKafkaEventBusModule`），置于项目根目录
- `[DependsOn(typeof(XiHanEventBusModule), typeof(XiHanDataModule))]`
- 注册进 `framework/XiHan.Framework.slnx` 的 `/1.src/6.Infrastructure/` 文件夹，紧随 `XiHan.Framework.EventBus.Redis` 之后
- 测试项目注册进 slnx 的测试文件夹，Import `props/test.props`

### 4.2 发件箱表

表名 `sys_event_outbox`，`sys_` 前缀依据 `docs/packages/data.md`。**不分表**。

列名 Pascal_Snake_Case，每列带 `ColumnDescription` 简体中文说明：

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `Guid`，主键，非自增 | 事件唯一标识，直接用契约的 `OutgoingEventInfo.Id` |
| `Row_Version` | `long` | 基类带的并发标识 |
| `Event_Name` | `string`，长度 256，非空 | 事件名，`OutgoingEventInfo.MaxEventNameLength` 即 256 |
| `Event_Data` | `byte[]`，非空 | 序列化后的事件数据，SqlSugar 原生映射为 blob |
| `Created_Time` | `DateTimeOffset`，非空 | 事件创建时间，见 §5 第三条 |
| `Extra_Properties` | 大文本，可空 | `ExtraProperties` 的 JSON，见 §5 第四条 |
| `Status` | `int`，非空 | 0=待发送，1=已领取。**P3 只定义，P4 使用** |
| `Claim_Token` | `string`，长度 64，可空 | 领取令牌。**P3 只定义，P4 使用** |
| `Claim_Time` | `DateTimeOffset`，可空 | 领取时刻，用于超时释放。**P3 只定义，P4 使用** |

P3 把整张表定义完整，P4 只加行为不改表结构。

**基类**：`SugarEntity<Guid>`。它提供 `Basic_Id` 与 `Row_Version`，不带创建/修改/软删除审计列——发件箱是队列，不需要那些。`Created_Time` 由本实体自己声明，它是事件的创建时间，不是行的审计时间。

**大文本列**：`Extra_Properties` 用 `ColumnDataType = StaticConfig.CodeFirst_BigString`。该常量定义在 `StaticConfig.cs:16`，值为 `"varcharmax,longtext,text,clob"`——各方言的大文本类型清单，`CodeFirstProvider` 按当前 `DbType` 挑选。这是可移植写法，**不要**改成写死的 `"text"`。

**二进制列**：`Event_Data` 声明为 `byte[]` 即可，不指定 `ColumnDataType`。`CodeFirstProvider.cs:733` 处理 `byte[]` ↔ `blob`，各方言的 `DbMaintenance` 各自映射。

### 4.3 映射

`EventOutboxMapper` 是静态类，两个方向：

```
ToEntity(OutgoingEventInfo info) → SysEventOutbox
ToEventInfo(SysEventOutbox entity) → OutgoingEventInfo
```

`OutgoingEventInfo` 的属性只读、无参构造是 `protected`，**不能当实体**。但它有公开构造函数：

```csharp
OutgoingEventInfo(Guid id, string eventName, byte[] eventData, DateTime createdTime)
```

反向映射经这个构造函数重建，再把 JSON 反序列化出的键值填回 `ExtraProperties`——该属性 getter 公开且是可变字典（`ExtraPropertyDictionary : Dictionary<string, object?>`），可以直接写入。

`Status` / `Claim_Token` / `Claim_Time` 不参与映射：它们是存储侧的状态，不属于事件本身。`ToEntity` 把 `Status` 置 0、另两列留空。

### 4.4 数据库可移植性

只使用 SqlSugar 的通用表达式 API 与方言无关的常量，不写针对特定数据库的原生 SQL 或写死的列类型。

## 5. 六条陷阱

**① 主键是 `Guid`，不是雪花 `long`——与 P1/P2 的约定不同。**

`IOutgoingEventInfo.Id` 是 `Guid`，`IEventOutbox.DeleteAsync(Guid id)` 与 `DeleteManyAsync(IEnumerable<Guid> ids)` 都按它定位。把它设成主键，删除就是主键查找；另立一个雪花 `long` 主键则每次删除都要走二级索引，且同一条记录有两个身份。

索引碎片不是问题：事件 `Id` 由 `DistributedEventBusBase.GuidGenerator`（`IDistributedIdGenerator<Guid>`）生成，`XiHanDistributedIdsModule` 注册的是**顺序 Guid** 生成器。

照搬 P1/P2 的「雪花 long 主键」会做错。

**② `OutgoingEventInfo` 不能当实体。**

属性全是 get-only，无参构造是 `protected`，SqlSugar 物化不了。必须另立实体类加双向映射。反向走公开的四参构造函数。

**③ `DateTime` ↔ `DateTimeOffset` 的往返会归一到 UTC，必须显式处理。**

契约的 `CreatedTime` 是 `DateTime`，实体列是 `DateTimeOffset`（框架列约定）。

入库方向按 `Kind` 分支：`Utc` 用零偏移；`Local` 用本地偏移；`Unspecified` **当作 UTC**处理。`Clock.Now` 依 `Options.Kind` 返回 `DateTime.UtcNow` 或 `DateTime.Now`，Kind 恒有值，`Unspecified` 只在手工构造 `OutgoingEventInfo` 时出现，把它当 UTC 是有意选择。

出库方向取 `DateTimeOffset.UtcDateTime`，得到 `Kind == Utc` 的 `DateTime`。

因此往返**不是**逐位相等：原本 `Local` 的时间回来会变成等价的 UTC。这是有意的归一，要有测试断言，不要试图「修」成保留原 Kind。

**④ `ExtraProperties` 的 JSON 往返不保留原始类型。**

`ExtraPropertyDictionary` 是 `Dictionary<string, object?>`。用 `System.Text.Json` 往返后，值会变成 `JsonElement` 而不是原来的 `string` / `int`。

因此**能保证的契约是 `GetCorrelationId()` 往返保真**（它内部做 `?.ToString()`，`JsonElement` 持有字符串时 `ToString()` 返回原值），**不是**字典的深度相等。测试按前者断言，不要按后者。

**⑤ 表不分表。**

P1/P2 的日志表按月分表，本表**不分**。发件箱是短命队列，发完即删，分表只会让 P4 的抢占跨表、复杂度暴涨。不要因为「前两份都分表了」就顺手加 `[SplitTable]`。

**⑥ 主键只能经构造函数传入。**

`EntityBase<TKey>.BasicId` 是 `{ get; protected set; }`，`SugarEntity<TKey>` 只是覆写它并加上 `[SugarColumn]`，可见性不变。**不能用对象初始化器赋值**。

因此实体需要两个公开构造函数：无参的供 SqlSugar 物化，`(Guid basicId)` 的供映射使用。漏掉后者的后果是映射无法写入事件标识，所有行的主键都是 `Guid.Empty`，第二条插入即主键冲突。

## 6. 测试策略

CI 在 ubuntu 运行且不启动任何外部服务。P3 的测试全部可在 CI 裸跑：

**实体元数据层（反射断言）** —— 表名为 `sys_event_outbox`；**没有** `SplitTableAttribute`；主键列为 `Basic_Id` 且 `IsIdentity == false`；`Event_Name` 长度 256；列名为 Pascal_Snake_Case。

**映射往返层（纯函数）** —— `OutgoingEventInfo` → 实体 → `OutgoingEventInfo` 后，`Id`、`EventName`、`EventData`（逐字节相等）保真；`CreatedTime` 归一到 UTC 且时刻相同；`GetCorrelationId()` 保真；无关联标识时 `ToEventInfo` 不抛异常。另断言 `ToEntity` 把 `Status` 置 0、`Claim_Token` 与 `Claim_Time` 为空。

**SQLite 集成层** —— `CodeFirst.InitTables()` 建出 `sys_event_outbox`；插入一条带二进制 `Event_Data` 的记录后查回，字节数组逐字节相等。

**不需要真实数据库**。P3 无并发语义，真库并发测试在 P5。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| `ExtraProperties` 类型保真 | JSON 往返后值为 `JsonElement`。框架目前只用它存关联标识，够用；若日后要存复杂对象需带类型信息 |
| 时间归一 | `Local` 时间往返后变 UTC，时刻等价但 `Kind` 不同 |
| 状态列未使用 | `Status` / `Claim_Token` / `Claim_Time` 在 P3 只有定义，P4 才赋予行为 |
| 发件箱尚未生效 | P3 结束时 `DefaultEventOutbox` 仍是容器里的实现，事件仍在内存 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- `DefaultEventOutbox` **仍是容器里的实现**
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `eventbus-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本 PR 无关的文档

## 9. 下一份

P4（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p4-outbox-write-claim-design.md`，已写）：`IEventOutbox` 实现——入箱的事务参与（必须用工作单元 scope 的客户端）与原子领取（条件 `UPDATE` 抢占 + 超时释放）。
