# Upgrade.SqlSugar 升级版本存储（1）设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-upgrade-sqlsugar-1-upgrade-version.md`
- **前置**：无（`XiHan.Framework.Upgrade`、`XiHan.Framework.Data` 均已发布）
- **所属 PR**：独立一个 PR（`Upgrade.SqlSugar`）
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-13
- **系列**：SqlSugar 持久化系列剩余模块之一，拆分方案见 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 3、5 节

> 本文档**自成一体**。实现本包所需的全部约束都写在这里。系列共用的约定在本文档里各自重复一份——若发现与拆分方案不一致，以本文档为准并在 PR 里提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 6 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。
>
> **本包最容易静默出错的地方**：`DefaultUpgradeVersionStore` 的 `SetUpgradingAsync`/`SetUpgradeCompletedAsync`/`SetUpgradeFailedAsync`/`UpdateDbVersionAsync` 四个方法，会把新状态**原地写回调用方传入的 `UpgradeVersionState` 参数对象**（`DefaultUpgradeVersionStore.cs` 里的 `CopyState(state, version)`，`state` 是存储内部状态、`version` 是调用方传入的引用）。`UpgradeVersionState` 是**可变类**（不是 `record`），调用方（`UpgradeEngine.ExecuteForTenantAsync`）在调用这些方法之后仍然持有并可能继续读取同一个 `version` 引用。SqlSugar 实现如果只把改动写进数据库、不把同样的字段同步写回这个引用对象，编译能过、大多数只测"数据库里的值对不对"的用例也会全绿，只有当调用方复用同一个 `version` 实例继续读取字段时才会读到旧值——这类问题在生产环境里会表现为"日志说已经在升级中，但内存里的状态对象显示没有"。第 6 节 ① 是这条的详细展开。

---

## 1. 背景与目标

### 1.1 现状

`IUpgradeVersionStore`（`framework/src/XiHan.Framework.Upgrade/Abstractions/IUpgradeVersionStore.cs`）是四个小包里契约最大的一个，9 个方法：

```csharp
public interface IUpgradeVersionStore
{
    Task EnsureTablesAsync(CancellationToken cancellationToken = default);
    Task<UpgradeVersionState> GetOrCreateAsync(string currentAppVersion, string minSupportVersion, CancellationToken cancellationToken = default);
    Task<UpgradeMigrationHistory?> GetLatestHistoryAsync(CancellationToken cancellationToken = default);
    Task SetUpgradingAsync(UpgradeVersionState version, string nodeName, DateTimeOffset startTime, CancellationToken cancellationToken = default);
    Task SetUpgradeCompletedAsync(UpgradeVersionState version, string appVersion, string dbVersion, CancellationToken cancellationToken = default);
    Task SetUpgradeFailedAsync(UpgradeVersionState version, CancellationToken cancellationToken = default);
    Task UpdateDbVersionAsync(UpgradeVersionState version, string dbVersion, CancellationToken cancellationToken = default);
    Task AddMigrationHistoryAsync(UpgradeMigrationHistory history, CancellationToken cancellationToken = default);
    Task<bool> HasMigrationHistoryAsync(string version, string scriptName, CancellationToken cancellationToken = default);
}
```

两个模型：

- `UpgradeVersionState`（`Models/UpgradeVersionState.cs`）：**可变类**，每个租户（或宿主，`TenantId == null`）一条，字段 `Id`/`TenantId`/`AppVersion`/`DbVersion`/`MinSupportVersion`/`IsUpgrading`/`UpgradeNode`/`UpgradeStartTime`
- `UpgradeMigrationHistory`（`Models/UpgradeMigrationHistory.cs`）：不可变字段的追加型记录，字段 `TenantId`/`Version`/`ScriptName`/`ExecutedTime`/`Success`/`NodeName`/`ErrorMessage`，**本身没有主键字段**——落库时需要一个独立于模型的行标识

`DefaultUpgradeVersionStore`（`Services/DefaultUpgradeVersionStore.cs`）是进程内存实现，用两个 `Dictionary` 分别存版本状态与迁移历史，键都是"租户键"——`BuildTenantKey(long? tenantId) => tenantId.HasValue ? $"tenant:{tenantId}" : "host"`（第 294-297 行）。

**唯一调用方**是 `UpgradeEngine`（`Services/UpgradeEngine.cs`）与 `UpgradeStatusService`（`Services/UpgradeStatusService.cs`）：

- `UpgradeEngine.ExecuteForTenantAsync`（第 147-258 行）：先 `EnsureTablesAsync` → `GetOrCreateAsync` 拿到 `version` → 判断是否需要升级 → 经 `IUpgradeLockProvider.TryAcquireLockAsync` 获取锁 → `SetUpgradingAsync(version, ...)` → 执行迁移脚本（内部逐条 `HasMigrationHistoryAsync` 判重、`AddMigrationHistoryAsync` 记录、`UpdateDbVersionAsync` 推进版本）→ `SetUpgradeCompletedAsync(version, ...)` 或异常路径的 `SetUpgradeFailedAsync(version, ...)`
- 整个流程里 `version` 从头到尾是**同一个对象引用**，`Set*`/`Update*` 系列方法接收它、修改它，但从不重新赋值局部变量——这正是"实现前必读"里提到的原地回写契约

**互斥已经由别的契约解决**：`IUpgradeLockProvider.TryAcquireLockAsync`（`Abstractions/IUpgradeLockProvider.cs`）在真正执行迁移脚本之前就已获取分布式锁（`UpgradeEngine.cs:177-188`），本包的 `IUpgradeVersionStore` **不需要**自己实现类似 `SqlSugarEventOutbox.ClaimFromDatabaseAsync` 的抢占协议——多实例并发执行同一次升级这件事，已经被锁挡在了更上层。本包真正需要处理的并发点只有一处：`GetOrCreateAsync` 在**获取锁之前**就会被调用（`ExecuteForTenantAsync` 第 151 行），多个实例同时启动、同时探测"这个租户/宿主有没有版本记录"时，可能会同时判定为"不存在"并各自尝试插入第一行——这是插入去重问题，不是执行互斥问题，见 4.3 节。

### 1.2 交付目标

1. 新增 `XiHan.Framework.Upgrade.SqlSugar` 包，提供 `IUpgradeVersionStore` 的 SqlSugar 落库实现 `SqlSugarUpgradeVersionStore`
2. 两个实体：`SysUpgradeVersion`（每租户一行的版本状态）、`SysUpgradeMigrationHistory`（追加型迁移历史）
3. 严格复现 `DefaultUpgradeVersionStore` 的"原地回写调用方对象"这一可观察行为
4. 用 `services.Replace` 顶替 `DefaultUpgradeVersionStore`

### 1.3 成功标准

1. `GetOrCreateAsync` 首次调用为给定租户键创建一行，二次调用返回同一行（`Id` 不变）
2. `SetUpgradingAsync`/`SetUpgradeCompletedAsync`/`SetUpgradeFailedAsync`/`UpdateDbVersionAsync` 调用后，数据库里的值与**调用方传入的 `version` 对象**的字段值一致
3. `HasMigrationHistoryAsync` 只把 `Success == true` 的记录视为"已执行"
4. `GetOrCreateAsync` 在两个并发调用同时探测到"不存在"时不产生两行数据
5. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约与主包实现——最高优先**

```
framework/src/XiHan.Framework.Upgrade/
  Abstractions/IUpgradeVersionStore.cs                9 个方法的契约
  Abstractions/IUpgradeLockProvider.cs                确认互斥已由锁提供者解决，本包不重复实现
  Models/UpgradeVersionState.cs                       可变类，Set*/Update* 会原地回写它
  Models/UpgradeMigrationHistory.cs                   无主键字段的追加型记录
  Services/DefaultUpgradeVersionStore.cs              内存实现，尤其 CopyState/CloneState/BuildTenantKey
  Services/UpgradeEngine.cs:147-258                    唯一的完整调用序列，含加锁时机
  Services/UpgradeStatusService.cs                     只读调用方，确认 GetOrCreateAsync/GetLatestHistoryAsync 的读语义
  Extensions/XiHanUpgradeServiceCollectionExtensions.cs:29   确认注册用的是 TryAddScoped
```

**② 兄弟子包范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/       简单实体形状范本
framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs   参考「本包不需要」的抢占协议长什么样，用于确认互斥已在别处解决、本包无需照搬
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs                   GetClientForEntity<T>() 签名
  Entities/SugarEntity.cs                               无审计字段的实体基类
```

**③ SqlSugar 源码——API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Abstract/InsertableProvider/InsertableProvider.cs    Insertable(...).ExecuteCommandAsync()
  Abstract/UpdateProvider/UpdateableProvider.cs         SetColumns / Where / ExecuteCommandAsync
  Abstract/QueryableProvider/QueryableProvider.cs       Where / OrderBy / Take / ToListAsync
```

> 权威源码是 `Src/Asp.NetCore2/`（**不是** `Src/Asp.Net/`）。更新提供者目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。当前引用版本 `SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：`插入數據.md`、`更新數據.md`、`查詢數據.md`。

仓库自身文档：`docs/packages/upgrade.md`、`docs/packages/auditing-sqlsugar.md`（同类子包文档范本）。

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

- **不要在本包里重新实现分布式锁或抢占协议**。互斥由 `IUpgradeLockProvider` 负责，本包只做数据存取。
- **不要让 `Set*`/`Update*` 方法只更新数据库、不回写调用方的 `version` 参数对象**。见"实现前必读"。
- **不要给 `EnsureTablesAsync` 依赖全局的 `EnableTableInitialization` 开关**。调用方总是显式调用它来确保表存在（`UpgradeEngine.cs:149`），必须在方法内部直接 `CodeFirst.InitTables(...)`，不能假设全局自动建表已经跑过。
- **不要对 `TenantId` 为 `null` 的行使用数据库层面的可空唯一索引去重**。不同数据库对"多行 NULL 是否算重复"的处理不一致（SQLite/PostgreSQL 允许多个 NULL，SQL Server 同样允许，MySQL 视版本而定），本包用一个非空的 `Tenant_Key` 字符串列统一表达"host"或"tenant:{id}"，避免这个可移植性坑，见 4.1 节。

## 3. 非目标

- **不修改 `IUpgradeVersionStore` 契约**。
- **不实现分布式锁**。`IUpgradeLockProvider` 是另一个契约，不在本包范围。
- **不做多库路由**。升级版本记录是全局基础设施数据，不涉及 `[ModuleDataSource]`。
- **不做分表**。`sys_upgrade_version` 每租户一行，条数有界；`sys_upgrade_migration_history` 按脚本数量增长，量级是"脚本数 × 租户数"，不是无界日志。
- **不给迁移历史表建唯一索引**。`sys_upgrade_migration_history` 是追加型日志，同一 `(Tenant_Key, Version, Script_Name)` 允许出现失败重试的多条记录（`Success` 字段区分），不能唯一约束。（`sys_upgrade_version` 的 `Tenant_Key` 唯一索引见 4.1 节，两张表的约束策略不同。）

## 4. 设计

### 4.1 实体：`SysUpgradeVersion`

```csharp
[SugarIndex("uq_sys_upgrade_version_tenant_key", nameof(TenantKey), OrderByType.Asc, isUnique: true)]
[SugarTable("sys_upgrade_version")]
public class SysUpgradeVersion : SugarEntity<long>
```

| 属性 | 列名 | 类型 | 说明 |
| --- | --- | --- | --- |
| `BasicId`（继承） | `Basic_Id` | `long` | 主键，雪花 ID |
| `TenantId` | `Tenant_Id` | `long?` | 租户标识，`null` 表示宿主 |
| `TenantKey` | `Tenant_Key` | `string`，`NOT NULL`，**唯一索引** | `BuildUpgradeTenantKey(TenantId)` 的结果，`"host"` 或 `"tenant:{id}"`，用于替代对可空 `TenantId` 做唯一性判断 |
| `AppVersion` | `App_Version` | `string` | 应用版本 |
| `DbVersion` | `Db_Version` | `string` | 数据库版本 |
| `MinSupportVersion` | `Min_Support_Version` | `string?` | 最小支持版本 |
| `IsUpgrading` | `Is_Upgrading` | `bool` | 是否升级中 |
| `UpgradeNode` | `Upgrade_Node` | `string?` | 升级节点 |
| `UpgradeStartTime` | `Upgrade_Start_Time` | `DateTimeOffset?` | 升级开始时间 |

**为什么加一个 `TenantKey` 列而不是直接对 `TenantId` 做唯一索引**：`TenantId` 是可空的，"host" 场景下为 `null`；不同数据库对唯一索引里多个 `NULL` 是否算重复的处理不一致，把这个判断丢给数据库层面的约束会带来跨方言的不确定性。用一个非空字符串列表达"host"或"tenant:{id}"，把"哪一行代表宿主/哪个租户"这件事从"NULL 语义"降级为普通字符串相等比较，查询与去重都更简单可控——**在这个非空字符串列上建唯一索引，就完全不用再考虑 NULL 语义的跨方言差异**。

**`[SugarIndex]` 的字段参数是 C# 属性名，不是数据库列名**：已核对 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Abstract/CodeFirstProvider/CodeFirstProvider.cs:377`——`CreateIndex` 内部按 `entityInfo.Columns.FirstOrDefault(z => z.PropertyName == it.Key)` 解析 `IndexFields` 的键，传 `"Tenant_Key"`（列名）会解析不到列、直接抛 `Check.ExceptionEasy`。构造函数签名已核对 `Entities/Mapping/SugarMappingAttribute.cs:346`：`SugarIndexAttribute(string indexName, string fieldName, OrderByType sortType, bool isUnique = false)`。索引是否真的在 `CodeFirst.InitTables()` 时于 SQLite 上生效，由 Task 3 的"竞态测试变红"反向验证，见计划。

### 4.2 实体：`SysUpgradeMigrationHistory`

```csharp
[SugarTable("sys_upgrade_migration_history")]
public class SysUpgradeMigrationHistory : SugarEntity<long>
```

| 属性 | 列名 | 类型 | 说明 |
| --- | --- | --- | --- |
| `BasicId`（继承） | `Basic_Id` | `long` | 主键，雪花 ID；`UpgradeMigrationHistory` 模型本身没有这个字段，纯粹是落库需要 |
| `TenantId` | `Tenant_Id` | `long?` | 租户标识 |
| `TenantKey` | `Tenant_Key` | `string`，`NOT NULL` | 同 4.1 节，与版本状态表统一口径 |
| `Version` | `Version` | `string` | 版本 |
| `ScriptName` | `Script_Name` | `string` | 脚本名称 |
| `ExecutedTime` | `Executed_Time` | `DateTimeOffset` | 执行时间 |
| `Success` | `Success` | `bool` | 是否成功 |
| `NodeName` | `Node_Name` | `string?` | 节点名称 |
| `ErrorMessage` | `Error_Message` | `string?`，`ColumnDataType = StaticConfig.CodeFirst_BigString` | 错误信息 |

不分表——迁移历史随脚本数量增长，不是无界日志。

### 4.3 `GetOrCreateAsync`：查询优先、唯一索引兜底、插入失败退回重查

```
tenantId = _currentTenant?.Id
tenantKey = BuildUpgradeTenantKey(tenantId)   // "host" 或 "tenant:{id}"
client = _clientResolver.GetClientForEntity<SysUpgradeVersion>()

existing = 按 TenantKey == tenantKey 查询单行
若存在：
    若 AppVersion 或 MinSupportVersion 为空白，回填并 Updateable 更新（与 Default 的 else 分支一致）
    返回映射后的 UpgradeVersionState

若不存在：
    entity = 新建 SysUpgradeVersion(idGenerator.NextId()) { TenantId, TenantKey, AppVersion=Normalize(currentAppVersion), DbVersion="0.0.0", MinSupportVersion=Normalize(minSupportVersion), IsUpgrading=false }
    try:
        Insertable(entity).ExecuteCommandAsync()
    catch (Exception):
        // 并发窗口：另一个实例/线程在探测之后、插入之前抢先插入了同一租户键的行
        retryExisting = 按 TenantKey == tenantKey 重新查询单行
        若 retryExisting 存在：返回映射后的它
        否则：rethrow 原始异常
    返回映射后的 entity
```

`NormalizeVersion` 与 `BuildUpgradeTenantKey` 是私有静态方法，逻辑与 `DefaultUpgradeVersionStore` 对应方法字面一致（`NormalizeVersion`：空白则 `"0.0.0"`，否则 `Trim()`；`BuildUpgradeTenantKey`：`tenantId.HasValue ? $"tenant:{tenantId.Value}" : "host"`）。

**为什么用"任何插入失败都退回重查"而不是识别具体的唯一约束冲突异常**：SqlSugar 对不同数据库提供方抛出的具体异常类型不统一（SQLite 是 `Microsoft.Data.Sqlite.SqliteException`，MySQL/PostgreSQL 各有各的驱动异常类型），逐一识别"哪个是唯一约束冲突"的成本高于收益。策略是"插入失败就再查一次，查到就用查到的、查不到就把原始异常抛出去"——不需要识别失败原因，只要失败就假设"可能是并发建行"；这个假设在极端情况下（真的是其他原因导致插入失败，比如连接断开）会多做一次无谓的查询，但不会掩盖真正的错误（重查也查不到时原始异常仍会抛出）。**这套策略本身与是否存在唯一索引无关**——唯一索引的作用是把"两个实例同时判定为'不存在'"这个竞态**从"两次插入都可能成功"变成"至少一次插入必然失败"**，从而保证重查分支一定会被触发；没有索引时重查分支只是"锦上添花"（大多数时候用不上，极端时序下起不了作用），有索引后重查分支是竞态发生时的**唯一**保障。二者组合起来，"任一原因的插入失败都退回重查"这条策略才是对索引冲突本身生效的完整闭环，不需要额外识别异常类型。

测试如何在不依赖真实并发的前提下验证这条路径：`SqlSugarUpgradeVersionStore` 留了一个 `protected virtual` 钩子 `OnBeforeInsertAsync(string tenantKey, CancellationToken)`，在"确认不存在"与"执行插入"之间调用，生产环境是空操作；测试用子类重写它，在这个精确的时间点抢先插入一行同租户键的记录，从而确定性地触发唯一索引冲突，不依赖真实的多线程时序（见计划 Task 3）。

### 4.4 `Set*`/`Update*` 四个方法：更新数据库后原地回写调用方对象

四个方法形状一致，以 `SetUpgradingAsync` 为例：

```csharp
public async Task SetUpgradingAsync(UpgradeVersionState version, string nodeName, DateTimeOffset startTime, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(version);
    if (version.Id <= 0)
    {
        throw new ArgumentException("version.Id 必须来自 GetOrCreateAsync 的返回值。", nameof(version));
    }

    var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();

    await client.Updateable<SysUpgradeVersion>()
        .SetColumns(x => new SysUpgradeVersion { IsUpgrading = true, UpgradeNode = nodeName, UpgradeStartTime = startTime })
        .Where(x => x.BasicId == version.Id)
        .ExecuteCommandAsync(cancellationToken);

    version.IsUpgrading = true;
    version.UpgradeNode = nodeName;
    version.UpgradeStartTime = startTime;
}
```

`SetUpgradeCompletedAsync`：更新 `IsUpgrading=false, AppVersion=Normalize(appVersion), DbVersion=Normalize(dbVersion)`，同步回写 `version.IsUpgrading`/`version.AppVersion`/`version.DbVersion`。

`SetUpgradeFailedAsync`：更新 `IsUpgrading=false`，回写 `version.IsUpgrading`。

`UpdateDbVersionAsync`：更新 `DbVersion=Normalize(dbVersion)`，回写 `version.DbVersion`。

**为什么要求 `version.Id > 0` 而不是像 `DefaultUpgradeVersionStore` 那样在找不到行时静默按租户键新建一行**：内存实现的 `GetOrCreateStateForWrite` 之所以能"找不到就新建"，是因为它的存储介质是进程内字典，新建一行的成本和语义都很轻。落库场景下，调用方（`UpgradeEngine`）**始终**先调用 `GetOrCreateAsync` 拿到带有效 `Id` 的 `version` 才会传给这四个方法（见 `UpgradeEngine.cs:151,191,208,241,319`），本包按契约的实际使用方式设计：要求 `Id` 有效，行为不存在时抛出明确异常而不是静默按租户键另建一行——避免"因为调用顺序错误而在数据库里插出一行孤儿记录"。

### 4.5 只读与追加方法

- `GetLatestHistoryAsync`：按当前租户键过滤，`OrderBy(x => x.ExecutedTime, OrderByType.Desc)`，`Take(1)`，无记录返回 `null`
- `AddMigrationHistoryAsync`：`tenantId = history.TenantId ?? _currentTenant?.Id`；构造 `SysUpgradeMigrationHistory(idGenerator.NextId())` 插入，`Version` 同样过 `NormalizeVersion`
- `HasMigrationHistoryAsync`：按当前租户键 + `Success == true` + `Version == version` + `ScriptName == scriptName` 过滤，`Take(1)` 后判断结果是否非空。**比较是大小写敏感的**（SqlSugar 生成的 SQL 用 `=`，具体方言的默认排序规则决定大小写敏感性；本包不显式指定排序规则），与 `DefaultUpgradeVersionStore` 用 `StringComparison.OrdinalIgnoreCase` 比较不完全一致，见 §7 已知边界
- `EnsureTablesAsync`：`client.CodeFirst.InitTables(typeof(SysUpgradeVersion), typeof(SysUpgradeMigrationHistory))`，不依赖全局的 `EnableTableInitialization` 开关——调用方每次执行升级流程都会显式调用它

### 4.6 注册

`XiHanUpgradeServiceCollectionExtensions.AddXiHanUpgrade` 用：

```csharp
services.TryAddScoped<IUpgradeVersionStore, DefaultUpgradeVersionStore>();
```

（`framework/src/XiHan.Framework.Upgrade/Extensions/XiHanUpgradeServiceCollectionExtensions.cs:29`）

本包 `XiHanUpgradeSqlSugarModule` 必须 `[DependsOn(typeof(XiHanUpgradeModule), typeof(XiHanDataModule))]`，`ConfigureServices` 里：

```csharp
services.Replace(ServiceDescriptor.Scoped<IUpgradeVersionStore, SqlSugarUpgradeVersionStore>());
```

## 5. 五个共同问题

1. **分表与否**：不分表。`sys_upgrade_version` 每租户一行，条数有界；`sys_upgrade_migration_history` 随脚本数量增长（脚本数 × 租户数量级），不是随时间无界增长的日志数据。
2. **主键类型**：`long`，雪花 ID。契约里 `UpgradeVersionState.Id` 本身就是 `long`（`DefaultUpgradeVersionStore` 用 `Interlocked.Increment` 生成，本包换成经 `IDistributedIdGenerator<long>` 的雪花 ID）；`UpgradeMigrationHistory` 模型没有 ID 字段，落库新增一个独立的雪花 `long` 主键，不从模型字段派生。
3. **是否参与工作单元事务**：不特别处理。`GetClientForEntity<T>()` 在单库场景下天然会参与调用方所在的当前工作单元（若有），但升级流程通常运行在应用启动阶段或独立的维护性调用里，不太可能与业务事务重叠，不需要为此做额外设计。
4. **是否需要多库**：不需要。升级版本记录是全局基础设施表，不涉及 `[ModuleDataSource]`；多租户通过 `Tenant_Key` 列在同一张表内区分，不是物理分库。
5. **顶替方式**：主包用 `TryAddScoped` 注册 `DefaultUpgradeVersionStore`，本包必须用 `services.Replace(ServiceDescriptor.Scoped<IUpgradeVersionStore, SqlSugarUpgradeVersionStore>())`。

## 6. 会静默失效的陷阱

**① `Set*`/`Update*` 只更新数据库、不回写调用方的 `version` 对象。**

见"实现前必读"。`UpgradeVersionState` 是可变类，`UpgradeEngine.ExecuteForTenantAsync` 里同一个 `version` 引用贯穿整个升级流程。如果 `SetUpgradingAsync` 之后不把 `version.IsUpgrading = true` 等字段同步写回，`version` 对象本身在调用方看来仍然是"未升级"状态——这不会影响本包自己的单元测试（如果测试只查数据库），但会让"读取同一个 `version` 引用的下游代码"看到过期数据。测试必须在调用 `Set*`/`Update*` 之后，直接断言**传入的那个 `version` 对象**的字段值，而不是重新 `GetOrCreateAsync` 查一份新对象来断言。

**② `GetOrCreateAsync` 的插入竞态被简化成"先查后插"而漏掉插入失败后的重查。**

`Tenant_Key` 上有唯一索引（见 4.1 节），所以两个实例在极短时间内同时首次调用 `GetOrCreateAsync`（例如集群同时启动）都判定为"不存在"、都尝试插入时，数据库**保证**其中一个插入会因唯一约束冲突而失败——这一步不会静默产生两行数据。但"数据库会挡住第二次插入"和"代码正确处理了这次失败"是两件事：如果实现只写"查不到就插入"、不写"插入失败后再查一次"，败者的插入异常会不经处理直接向上抛出，业务侧看到的是一次不该出现的启动期异常，而不是拿到赢家已经创建的那一行版本记录。这个疏漏在**单实例**场景下完全不会被触发（永远不会撞上竞态），只有两个调用方同时首次探测同一个空表时才会现形——第一层的桩测试用"插入前抢先写入一行"模拟这个时序（见计划 Task 3 的 `OnBeforeInsertAsync` 钩子），不写这段重查逻辑，该测试会直接看到未处理的异常冒出来。

**③ `Set*`/`Update*` 对不存在的 `Id` 静默无操作。**

`Updateable<T>().Where(x => x.BasicId == version.Id).ExecuteCommandAsync()` 在 `version.Id` 对应的行不存在时，`ExecuteCommandAsync` 返回 `0`（受影响行数为零），**不会抛异常**。如果调用方传入了一个 `Id` 未曾被 `GetOrCreateAsync` 创建过的 `version`（例如手工 `new UpgradeVersionState { Id = 999 }`），四个 `Set*`/`Update*` 方法会"成功返回"但数据库里什么都没发生，且 4.4 节设计的 `Id <= 0` 校验拦不住这种情况（`999 > 0`）。这是本包接受的边界（见 §7 已知边界的"孤儿 `Id`"一项），测试里需要显式断言"合法 `Id`（来自 `GetOrCreateAsync`）能成功更新"，不需要也无法覆盖"任意 `Id` 都能被安全拒绝"。

## 7. 测试策略

**第一层——SQLite，CI 强门禁执行**

- 建表：`EnsureTablesAsync` 能建出 `sys_upgrade_version` 与 `sys_upgrade_migration_history` 两张表
- 首次创建：`GetOrCreateAsync` 在空表上创建一行，返回的 `Id > 0`
- 二次调用返回同一行：连续两次 `GetOrCreateAsync` 传入相同参数，两次返回的 `Id` 相同
- 回填空白字段：先插入一行 `AppVersion` 为空字符串的记录（模拟历史脏数据），`GetOrCreateAsync` 应回填并持久化
- **原地回写（对应陷阱①）**：调用 `SetUpgradingAsync(version, "node-1", now)` 后，直接断言传入的 `version.IsUpgrading == true`、`version.UpgradeNode == "node-1"`，不重新查询
- 同理为 `SetUpgradeCompletedAsync`、`SetUpgradeFailedAsync`、`UpdateDbVersionAsync` 各写一个"回写断言"用例
- 插入竞态：借 `OnBeforeInsertAsync` 钩子在"确认不存在"与"执行插入"之间确定性地插入一行相同 `Tenant_Key` 的记录，验证 `GetOrCreateAsync` 自身的插入因唯一索引冲突失败后能捕获异常并重查、返回竞争者已插入的那一行，而不是把异常抛给调用方或产生第二行；同一测试还要求"临时删掉重查分支后该用例必须变红"，证明它不是摆设（见计划 Task 3 的反向验证步骤）
- `HasMigrationHistoryAsync`：只有 `Success == true` 的记录视为已执行；`Success == false` 的同名记录不算
- `GetLatestHistoryAsync`：多条记录时返回 `ExecutedTime` 最新的一条；空表返回 `null`
- `AddMigrationHistoryAsync`：写入后能被 `GetLatestHistoryAsync`/`HasMigrationHistoryAsync` 读到
- 宿主与租户隔离：`TenantId = null`（宿主）与 `TenantId = 1`（租户）各自的版本状态互不影响
- 注册断言：`services.AddXiHanUpgradeSqlSugar()` 后 `IUpgradeVersionStore` 的实现类型是 `SqlSugarUpgradeVersionStore`

SQLite 临时库连接串必须带 `Pooling=False`。

**第二层**：本包的互斥已经由 `IUpgradeLockProvider`（另一个契约）解决，不需要真实数据库层面的并发专项测试；`GetOrCreateAsync` 的插入竞态用第一层"手工模拟"的方式已经覆盖了核心逻辑分支。

## 8. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 唯一索引冲突时机取决于数据库 | `Tenant_Key` 唯一索引由 `CodeFirst.InitTables()` 在建表时创建（`CodeFirstProvider.CreateIndex`），前提是目标数据库的 `DbMaintenance` 实现支持该操作；本包只在 SQLite（第一层测试）上验证过，MySQL/PostgreSQL/SQL Server 等生产数据库首次接入时应确认索引确实被创建（`SHOW INDEX` / `\d`），而不是假设与 SQLite 行为一致 |
| 孤儿 `Id` 静默无操作 | `Set*`/`Update*` 传入未经 `GetOrCreateAsync` 创建的 `Id` 时不抛异常也不生效，见陷阱③ |
| `HasMigrationHistoryAsync` 的大小写敏感性 | 与 `DefaultUpgradeVersionStore` 的 `OrdinalIgnoreCase` 比较不完全一致，具体大小写敏感性由目标数据库的排序规则决定，见 4.5 节 |
| 互斥依赖另一个契约 | `IUpgradeLockProvider` 目前仍是进程内实现（`DefaultUpgradeLockProvider`），跨进程/跨机器部署时锁本身不是分布式的；这不是本包的范围，但会影响"多实例并发升级安全"这一整体属性 |
| PostgreSQL 上事务内的唯一索引冲突会中止整个事务 | 若 `GetOrCreateAsync` 在一个已开启的事务里执行，插入因 `Tenant_Key` 唯一索引冲突失败后，PostgreSQL 会把当前事务整体标记为出错状态，同一连接、同一事务内紧接着的重查也会失败，4.3 节的重查分支形同虚设，异常会一路抛给调用方。SQLite、MySQL（InnoDB）不存在这个限制，单条语句失败不影响同一事务里后续语句执行。`GetOrCreateAsync` 应在事务外调用；若调用方确实需要在事务内调用，需接受"该数据库上的竞态会以异常形式暴露，而不是被重查吸收"这一后果 |

## 9. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `Set*`/`Update*` 四个方法均有"回写调用方 `version` 对象"的测试断言
- `GetOrCreateAsync` 的插入竞态有测试覆盖，且该测试在临时删除重查分支后能变红（证明测试确实在验证这条分支，不是摆设）
- 注册用 `services.Replace`，有测试断言顶替生效
- 每个 `.cs` 文件带两行版权声明
- 注释与 XML 文档注释为简体中文，且只说明代码做什么
- 包 README 沿用固定七段结构
- 提交信息中文 Conventional Commits，作用域 `upgrade-sqlsugar`，不加 AI 署名

## 10. 下一份

本包只有一份计划。系列内三个小包（`Security.SqlSugar`、`Traffic.SqlSugar`、`Upgrade.SqlSugar`）均已各自成文，互不依赖，可并行实现。
