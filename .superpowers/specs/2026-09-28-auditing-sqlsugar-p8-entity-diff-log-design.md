# P8：Auditing.SqlSugar 实体差异日志写入器 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-auditing-sqlsugar-p8-entity-diff-log.md`
- **前置**：P1（`.superpowers/specs/2026-09-21-auditing-sqlsugar-p1-entities-design.md`）与 P2（`.superpowers/specs/2026-09-21-auditing-sqlsugar-p2-writers-design.md`）必须已完成——本份复用它们建立的实体基类、映射器、注册扩展与测试项目
- **所属 PR**：**第一个 PR**（`Auditing.SqlSugar`），与 P1、P2 同一个。本份落地后 PR1 才算完整
- **Linear 议题**：`https://linear.app/elf-express/issue/EDDIE-5`
- **系列**：SqlSugar 持久化层，P1–P2 已完成、P3–P7 见拆分方案，本份补记 P1/P2 扫描时漏掉的第 6 个写入器

> 本文档**自成一体**。实现 P8 所需的全部约束都写在这里，不引用其他设计文档（P1/P2 spec 除外——它们建立的形状是本份复用的基础，冲突时以本文档为准）。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的地方**：`IEntityDiffLogWriter` 契约与 `NullEntityDiffLogWriter` 空实现位于 `XiHan.Framework.Auditing` **根命名空间**，不在 `XiHan.Framework.Auditing.Writers` 下——P1/P2 当初扫描 `Writers/` 目录时漏掉了它，这正是本份存在的原因。第二个陷阱：本包 5 个既有写入器全部经 `GetClientForEntity<T>()` 取客户端且**不**登记进当前工作单元的事务钉子测试断言（`GetCurrentClientCalls == 0`），但差异日志写入器**必须**反过来——经 `GetCurrentClient()` 取当前工作单元的连接，这是 `SqlSugarDiffLogAop` 与 `docs/guide/auditing.md`「写入器的事务契约」一节明确写死的契约，抄错会在功能上正常跑（表也会建、日志也会写),只有业务回滚时才会发现审计行没有一起回滚。

---

## 1. 背景与目标

`framework/src/XiHan.Framework.Auditing/NullEntityDiffLogWriter.cs` 是 `IEntityDiffLogWriter` 的默认实现，`WriteAsync` 直接 `return Task.CompletedTask`——采集到的实体差异全部丢弃。

调用链已经齐全，只缺落库这一环：

- `framework/src/XiHan.Framework.Data/SqlSugar/Auditing/SqlSugarDiffLogAop.cs`：基于 SqlSugar 原生 `Aop.OnDiffLogEvent`，把 Diff 事件按行组装成 `EntityDiffLogRecord` 并调用 `IEntityDiffLogWriter.WriteAsync`（第 130 行）
- `framework/src/XiHan.Framework.Auditing/DefaultEntityAuditContextProvider.cs`：从 `ICurrentUser`/`ICurrentTenant` 填充 `UserId`/`UserName`/`TenantId`，并在 `ShouldAudit` 里把 `XiHan.Framework.Auditing` 命名空间与类型全名含 `AuditLog`/`DiffLog` 的实体排除在审计之外
- `framework/src/XiHan.Framework.Auditing/Extensions/DependencyInjection/XiHanAuditingServiceCollectionExtensions.cs:56`：`services.TryAddScoped<IEntityDiffLogWriter, NullEntityDiffLogWriter>()`——子包必须用 `Replace`，`TryAdd` 是空操作

**交付**：`Auditing.SqlSugar` 包新增第 6 个实体（差异日志）与第 6 个写入器，`services.Replace` 顶替空实现。

**成功标准**：

1. `NullEntityDiffLogWriter` 被 `services.Replace` 顶替为 SqlSugar 实现，有测试断言
2. 差异日志按月分表落库，字段与 `EntityDiffLogRecord` 的 14 个属性逐一对应
3. 写入器经 `ISqlSugarClientResolver.GetCurrentClient()`（不是 `GetClientForEntity`）取客户端，有测试断言调用的是哪一个方法
4. `AuditingLogMapper` 新增第 6 个 `ToEntity` 重载，是纯函数，可脱库单测
5. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
6. 包 README「核心能力」与 `docs/packages/auditing-sqlsugar.md` 同步更新（这两处已经提到「实体变更日志仍为空实现」，需要改写）

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约与调用方 —— 最高优先，决定本份的全部行为**

```
framework/src/XiHan.Framework.Auditing/
  IEntityDiffLogWriter.cs              契约，1 个方法：WriteAsync(EntityDiffLogRecord, CancellationToken)
  EntityDiffLogRecord.cs               14 个属性，全部要映射到实体
  NullEntityDiffLogWriter.cs           要顶替的空实现
  DefaultEntityAuditContextProvider.cs ShouldAudit 的排除名单（第二道保险，见 §5①）
  Extensions/DependencyInjection/XiHanAuditingServiceCollectionExtensions.cs:56   TryAddScoped 的位置
framework/src/XiHan.Framework.Data/SqlSugar/Auditing/SqlSugarDiffLogAop.cs
  第 16-28 行类型 <remarks>：写入器必须与业务同事务、必须走裸 Insertable
  第 130 行：writer.WriteAsync(record, CancellationToken.None).GetAwaiter().GetResult() 调用点
framework/src/XiHan.Framework.Data/SqlSugar/Clients/
  ISqlSugarClientResolver.cs            GetCurrentClient() 与 GetClientForEntity(Type) 的语义差异
  SqlSugarClientResolver.cs             GetCurrentClient/GetClientForEntity 内部都过 EnlistCurrentUnitOfWork
framework/src/XiHan.Framework.Uow/
  AmbientUnitOfWork.cs                  IUnitOfWork? 由 AsyncLocal<IUnitOfWork?> 承载，与 DI Scope 无关（见 §5②）
  UnitOfWorkManager.cs                  Current => _ambientUnitOfWork.GetCurrentByChecking()
docs/guide/auditing.md
  128-213 行「实体变更审计」「写入器的事务契约」——本份的行为规格说明书，示例代码里写死用 GetCurrentClient()
```

**② 照抄的兄弟——形状范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  Entities/SysOperationLog.cs                      实体形状：SplitTable + SugarTable + 基类 + 两个构造函数
  Writers/SqlSugarOperationLogWriter.cs            写入器形状（本份要改成 GetCurrentClient）
  Writers/SqlSugarExceptionLogWriter.cs            大文本列（BigString）在写入器里怎么处理——不处理，映射层原样传
  Mapping/AuditingLogMapper.cs                     纯静态映射器，本份追加第 6 个重载
  Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs   Replace 注册的位置
framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  EntityMappingTests.cs      实体反射断言的形状
  AuditingLogMapperTests.cs  映射纯函数测试的形状
  LogWriterTests.cs          SQLite 落库测试 + StubClientResolver（本份要在桩里新增计数字段）
  TableInitializationTests.cs   建表测试的形状
```

**③ SqlSugar 源码 —— API 真实签名的唯一权威**

本份不引入任何 P1/P2 未用过的 SqlSugar API——`Insertable(entity).SplitTable().ExecuteCommandAsync()` 与 5 个兄弟逐字相同，其签名已由 P1/P2 在 CI 验证过，不再重新核对源码。唯一的差异是客户端来源方法从 `GetClientForEntity<T>()` 换成 `GetCurrentClient()`，两者都定义在同一个接口（`ISqlSugarClientResolver.cs` 第 22 行与第 33 行），本文档 §2.1 已引用。

当前引用版本：`SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

- `docs/guide/auditing.md` 128-283 行：实体变更审计的完整行为规格，包括事务契约、脱敏时机、多租户配合
- `docs/packages/auditing-sqlsugar.md`：本包现有文档，**94-101 行与 171 行已经预告了本份要做的事**（「实体变更日志仍为空实现」「这与 `IEntityDiffLogWriter`『必须与业务同事务』的契约方向相反」），本份落地后这几处需要改写而不是继续留空
- `/e/source/platfrom-admin/docs/SqlSugar-docs/插入數據.md`、`自動分表.md`（P1/P2 已读过，行为未变）

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

- **不要用 `GetClientForEntity<SysDiffLog>()`**。这是 5 个兄弟的取法，本份必须用 `GetCurrentClient()`——见实现前必读与 §4.3。
- **不要给写入器的 `Insertable(...)` 挂 `.EnableDiffLogEvent(...)`**。挂了会让写审计的这条 INSERT 再次触发 `OnDiffLogEvent`，陷入递归。5 个兄弟本来就没挂，本份延续不挂即可，不需要新增任何防递归代码。
- **不要修改 `SqlSugarDiffLogAop.cs`、`DefaultEntityAuditContextProvider.cs`、`IEntityDiffLogWriter.cs`、`EntityDiffLogRecord.cs`**。四者都已就绪，本份只新增实现，不改契约。
- **不要新建 csproj，不要改 `XiHan.Framework.slnx`**。这不是新包，`Auditing.SqlSugar` 已经在解决方案里。
- **不要因为“同事务”就给写入器加 `BeginTran`/`CommitTran` 之类的显式事务代码**。同事务是靠 `GetCurrentClient()` 内部的 `EnlistCurrentUnitOfWork` 自动完成的，写入器本身不知道、也不需要知道自己是否在事务里。

## 3. 非目标

- **不实现批量写入**。`IEntityDiffLogWriter.WriteAsync` 是单条契约，`SqlSugarDiffLogAop` 按行逐条调用，本份不改动调用方也不给写入器加批量重载。
- **不做租户独立库遍历或多库分发**。差异日志固定落当前布局的主库（`GetCurrentClient()` 语义），不支持 `[ModuleDataSource]`。
- **不做收件箱、发件箱相关改动**。那是 P3/P4/P6/P7 的范围。
- **不改 `EnableDiffLog` 开关或 AOP 挂载时机**。两者都是 `XiHan.Framework.Data` 既有行为。
- **不新增配置节**。本包沿用主包已有的 `XiHan:Data:SqlSugarCore:EnableDiffLog`，自己没有配置。

## 4. 设计

### 4.1 实体：`SysDiffLog`

```csharp
[SplitTable(SplitType.Month)]
[SugarTable("sys_diff_log_{year}{month}{day}")]
public class SysDiffLog : SugarCreationEntity<long>, ISplitTableEntity
```

字段与 `EntityDiffLogRecord` 的 14 个属性逐一对应（列名 Pascal_Snake_Case，风格与 5 个兄弟一致）：

| 属性 | 列名 | 类型 | 长度/类型 | 可空 |
| --- | --- | --- | --- | --- |
| `CreatedTime`（基类 `override`，分表字段） | `Created_Time` | `DateTimeOffset` | - | 否 |
| `AuditType` | `Audit_Type` | `string` | `Length = 32` | 否，默认 `"EntityChange"` |
| `OperationType` | `Operation_Type` | `string` | `Length = 16` | 否 |
| `EntityType` | `Entity_Type` | `string` | `Length = 256` | 否 |
| `EntityId` | `Entity_Id` | `string?` | `Length = 256` | 是 |
| `BeforeData` | `Before_Data` | `string?` | `ColumnDataType = StaticConfig.CodeFirst_BigString` | 是 |
| `AfterData` | `After_Data` | `string?` | `ColumnDataType = StaticConfig.CodeFirst_BigString` | 是 |
| `ChangedFields` | `Changed_Fields` | `string?` | `ColumnDataType = StaticConfig.CodeFirst_BigString` | 是 |
| `RequestPath` | `Request_Path` | `string?` | `Length = 512` | 是 |
| `RequestMethod` | `Request_Method` | `string?` | `Length = 16` | 是 |
| `OperationIp` | `Operation_Ip` | `string?` | `Length = 64` | 是 |
| `RequestId` | `Request_Id` | `string?` | `Length = 64` | 是 |
| `UserId` | `User_Id` | `long?` | - | 是 |
| `UserName` | `User_Name` | `string?` | `Length = 128` | 是 |
| `TenantId` | `Tenant_Id` | `long?` | - | 是 |

两个公开构造函数与 5 个兄弟一致：无参（供 SqlSugar 物化）与 `(long basicId)`（供写入器构造）。

**实体命名与表名**：`docs/guide/auditing.md` 148-178 行「写入器的事务契约」一节的示例代码已经用 `SysDiffLog` 作为实体类型名——即便那只是应用侧的示例代码、不是框架强制约定，本份仍采用同名以保持与已发布文档一致，避免读过该文档的使用者产生「示例和实现对不上」的困惑。表名 `sys_diff_log_{year}{month}{day}`，与 5 个兄弟的 `sys_xxx_log_{year}{month}{day}` 命名风格一致。

**长度选择**：`EntityType`/`EntityId` 比照 5 个兄弟里同量级字段（`ControllerName`/`ActionName`，均 256）；`RequestPath` 比照 `Path`（512）；`RequestMethod` 比照 `Method`（16）；`OperationIp` 比照 `RemoteIp`（64）；`RequestId` 比照 `TraceId`（64）；`UserName` 比照其余 5 个实体的 `UserName`（128）。`AuditType`/`OperationType` 长度按现有取值（`"EntityChange"`、`"Create"`/`"Update"`/`"Delete"`/`"Restore"`）留出余量取 32/16。

### 4.2 映射：`AuditingLogMapper` 第 6 个重载

```csharp
public static SysDiffLog ToEntity(EntityDiffLogRecord record, long basicId, DateTimeOffset createdTime)
```

字段搬运方式与 5 个兄弟一致：定长列过 `Clamp(value, maxLength)`，`BeforeData`/`AfterData`/`ChangedFields` 三个 `BigString` 列原样传（不截断，`SqlSugarDiffLogAop` 已经在采集端做过 8000 字符的合法 JSON 截断，参见 `SqlSugarDiffLogAop.cs:31-32`，二次截断可能截断出非法 JSON 的中段）。`AuditType` 固定映射 `record.AuditType`（默认值 `"EntityChange"` 已在记录模型里定好，映射器不重复赋默认值）。

### 4.3 写入器：`SqlSugarEntityDiffLogWriter`

```csharp
namespace XiHan.Framework.Auditing.SqlSugar.Writers;

public class SqlSugarEntityDiffLogWriter : IEntityDiffLogWriter
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    public SqlSugarEntityDiffLogWriter(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    public async Task WriteAsync(EntityDiffLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = AuditingLogMapper.ToEntity(record, _idGenerator.NextId(), DateTimeOffset.UtcNow);
        var client = _clientResolver.GetCurrentClient();

        await client.Insertable(entity).SplitTable().ExecuteCommandAsync();
    }
}
```

与 5 个兄弟唯一的结构性差异是 `_clientResolver.GetCurrentClient()` 代替 `_clientResolver.GetClientForEntity<TEntity>()`——理由见 §4.4。

**命名空间提示（实现前必读已点过，这里再明确一次)**：`IEntityDiffLogWriter` 与 `EntityDiffLogRecord` 都在 `XiHan.Framework.Auditing` 根命名空间；本文件所在的 `XiHan.Framework.Auditing.SqlSugar.Writers` 是它的子命名空间之一（`XiHan.Framework.Auditing` → `XiHan.Framework.Auditing.SqlSugar` → `XiHan.Framework.Auditing.SqlSugar.Writers`），C# 的名称解析会沿着这条外层命名空间链查找，因此**不需要**额外 `using XiHan.Framework.Auditing;`——5 个兄弟能直接用 `OperationLogRecord` 而不加这行 using，就是同样的原因。`IOperationLogWriter` 需要显式 `using XiHan.Framework.Auditing.Writers;` 是因为那是**平级**命名空间，不在外层链上。

### 4.4 五个共同问题的答案

1. **分表与否**：按月分表，与 5 个兄弟一致。差异日志和其余审计日志一样是持续增长、按时间检索的日志类数据，具备保留期语义（超过保留期整月删表即可），不是短命队列。
2. **主键类型**：雪花 `long`，经 `IDistributedIdGenerator<long>`——与 5 个兄弟相同，因为 `EntityDiffLogRecord` 本身不携带任何标识字段（它的 `EntityId` 是**被审计实体**的主键值,不是这条审计记录自己的主键），主键类型由本包的既有约定决定，不是契约决定。
3. **是否参与工作单元事务**：**参与，且是本份与 5 个兄弟唯一的行为差异**。`SqlSugarDiffLogAop` 类型注释（`SqlSugarDiffLogAop.cs:24-25`）与 `docs/guide/auditing.md` 148-182 行都明确要求「审计写入与业务同事务」，写入器必须用 `GetCurrentClient()` 而不是 `GetClientForEntity()`。**这与 Linear 议题描述里「不参与工作单元事务」的表述不一致**，本文档以代码与已发布文档为准，见 §8「与议题描述不一致之处」。
4. **是否需要多库**：不需要。`GetCurrentClient()` 只解析当前布局的主库，不支持 `[ModuleDataSource]` 路由——这也是「参与业务事务」的必然代价：业务写在模块库时，若差异日志坚持落主库，两者其实不在同一个物理连接上，`EnlistCurrentUnitOfWork` 也无法把跨库的两个连接钉成一个事务。这个已知边界写入 §7，不在本份修。
5. **顶替方式**：`Replace`。主包第 56 行用 `TryAddScoped<IEntityDiffLogWriter, NullEntityDiffLogWriter>()`，`TryAdd` 对已注册的服务类型是空操作。

### 4.5 注册

`XiHanAuditingSqlSugarServiceCollectionExtensions.AddXiHanAuditingSqlSugar` 追加一行：

```csharp
services.Replace(ServiceDescriptor.Scoped<IEntityDiffLogWriter, SqlSugarEntityDiffLogWriter>());
```

`IEntityDiffLogWriter` **不需要**额外 `using`。该扩展方法类所在命名空间是 `XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection`，把它从右向左逐段去掉（`DependencyInjection`、`Extensions`、`SqlSugar`）就得到 `XiHan.Framework.Auditing`——这条外层链本身就包含 `IEntityDiffLogWriter`所在的命名空间，C# 名称解析会沿链找到它，不需要显式 `using`。当前文件已有的 `using XiHan.Framework.Auditing.Writers;` 是为另外 5 个 `IXxxLogWriter` 准备的（`.Writers` 是平级子命名空间，不在外层链上，必须显式 `using`），`IEntityDiffLogWriter` 不需要蹭这一行。

不新增 `ProjectReference`：`ISqlSugarClientResolver` 与 `IDistributedIdGenerator<long>` 均已由现有依赖（`XiHan.Framework.Data`）提供,与 5 个兄弟共享同一条依赖链。

## 5. 会静默失效的陷阱

**① 忘记 `ShouldAudit` 的排除名单只挡业务实体,不会阻止你手滑给写入器的 `Insertable` 挂 `EnableDiffLogEvent`。**

`ShouldAudit` 只在 `SqlSugarDiffLogAop.HandleDiffLog` 里被调用一次,用来判断**触发** Diff 事件的那次写操作要不要审计。它挡不住任何显式调用——如果实现时手滑给 `SqlSugarEntityDiffLogWriter.WriteAsync` 里的 `Insertable(entity)` 加了 `.EnableDiffLogEvent(typeof(SysDiffLog))`,这次写操作本身会再触发一次 `OnDiffLogEvent`,新事件的 `BusinessData` 是 `typeof(SysDiffLog)`,传给 `ShouldAudit` 判断,`SysDiffLog` 的完整名 `XiHan.Framework.Auditing.SqlSugar.Entities.SysDiffLog` 确实以 `XiHan.Framework.Auditing` 开头,会被排除——**这道保险生效,不会死循环**。但如果排除名单的判断逻辑将来被改掉(比如改成按 Attribute 而不是命名空间前缀),这个二次触发就会在没人注意的时候复活。**结论:写入器压根不要调用 `.EnableDiffLogEvent(...)`**,不要指望排除名单兜底,那是第二道保险不是第一道。

**② `GetCurrentClient()` 与 `GetClientForEntity()` 在没有活动工作单元时行为相同,容易在测试里发现不了「取错了方法」这类回归。**

`SqlSugarClientResolver.GetCurrentClient()` 与 `GetClientForEntity(Type)`(未声明 `[ModuleDataSource]` 时)都会先解析出当前布局主库的客户端,再各自调用 `EnlistCurrentUnitOfWork`。**没有活动事务型工作单元时**,`EnlistCurrentUnitOfWork` 直接原样返回传入的客户端——两条路径此时的返回值完全一致。也就是说,如果实现时手滑把 `GetCurrentClient()` 写成了 `GetClientForEntity<SysDiffLog>()`,只要测试没有显式开一个事务型工作单元,**日志一样能写进去,断言"数据落库"的用例照样全绿**。只有当测试真正开启一个事务型工作单元、往回滚前查询这条差异日志时,两者的差异才会暴露。本份的第一层测试必须包含"调用的是哪一个方法"的直接断言(桩记录调用次数),不能只测"数据有没有落库"。

**③ `EntityId` 是被审计实体的主键值,不是 `SysDiffLog` 自己的主键。**

`EntityDiffLogRecord.EntityId` 来自 `SqlSugarDiffLogAop.PairRowsByPrimaryKey` 按**被审计实体**的主键列拼出来的字符串(多主键以 `|` 连接),与 `SysDiffLog.BasicId`(这条审计记录自己的雪花 ID)是两个完全不同的概念。映射器与实体设计如果把两者混为一谈(比如打算复用 `EntityId` 做 `SysDiffLog` 的主键),会导致同一被审计实体的多次变更互相覆盖——`EntityId` 不是唯一的,同一行实体改 10 次就有 10 条 `EntityId` 相同的差异日志。

## 6. 测试策略

### 第一层——SQLite,CI 强门禁执行

**实体层(比照 `EntityMappingTests.cs`)**:

- `SysDiffLog` 表名为 `sys_diff_log_{year}{month}{day}`
- `SysDiffLog` 标注 `[SplitTable(SplitType.Month)]`
- `SysDiffLog` 的 `CreatedTime` 标注 `[SplitFieldAttribute]`
- `SysDiffLog` 实现 `ISplitTableEntity`
- 建表测试(比照 `TableInitializationTests.cs`):`SysDiffLog` 能建出当月分表,写入后能按时间区间查回

**映射层(比照 `AuditingLogMapperTests.cs`,不碰数据库)**:

- 全字段映射保留(14 个属性逐一断言,包括 `AuditType` 默认值 `"EntityChange"`)
- 超长定长字段按列宽截断(复用 `Clamp` 既有断言模式,选 2-3 个代表性字段而非全部 11 个定长字段)
- `BeforeData`/`AfterData`/`ChangedFields` 原样保留,不截断(用超过所有定长列宽度、但明显小于 8000 字符的 JSON 字符串断言长度不变)

**写入器层(比照 `LogWriterTests.cs`,SQLite 落库,**必须**新增"调用了哪个客户端解析方法"的断言)**:

- 写入后能按时间区间查回、主键非零
- **`StubClientResolver` 新增 `GetCurrentClientCalls` 计数已存在(既有字段),断言 `SqlSugarEntityDiffLogWriter.WriteAsync` 执行后 `GetCurrentClientCalls == 1` 且 `RequestedEntityTypes` 为空集合**——这是 §5②陷阱的直接断言,是本份测试策略里最重要的一条
- 注册断言:`AddXiHanAuditingSqlSugar` 后 `IEntityDiffLogWriter` 被 `Replace` 为 `SqlSugarEntityDiffLogWriter`(追加进既有的「六个写入器全部被顶替」`[Theory]`,原「五个」改「六个」)

### 第二层——真实数据库

本份不需要。差异日志的"与业务同事务"语义由 `ISqlSugarClientResolver.GetCurrentClient()` + `EnlistCurrentUnitOfWork` 保证,这条链路已经在 `XiHan.Framework.Data.Tests` 里被 P5 及更早的既有测试覆盖(`RequiresNewIsolationTests`、`EnlistedConfigIdsTests` 等),本份不重复验证工作单元机制本身,只验证"写入器调用了正确的方法"。

## 7. 已知边界

写入 PR 描述与包 README,不写进代码注释:

| 项 | 说明 |
| --- | --- |
| 差异日志固定落主库 | `GetCurrentClient()` 不支持 `[ModuleDataSource]` 路由;业务实体声明了模块数据源时,该实体的差异日志仍然落在当前布局的主库,不跟随业务数据分库。这是「与业务同事务」的直接代价——差异日志与业务实体分处两个物理连接时,不可能同时钉进同一个事务 |
| 差异日志的事务方向与其余 5 类日志相反 | 访问/接口/异常/登录/操作 5 类日志"尽量不与业务同生共死"(队列或请求后写入,业务回滚不影响它们);差异日志"必须同生共死"(业务回滚,差异日志随之回滚)。两者对同一套写入器基础设施的期望方向相反,应用侧自定义写入器时需要留意选哪一种 |
| 高频变更下的写放大 | 同一实体短时间内多次变更,每次都产生一条独立的 `SysDiffLog` 行(不去重、不合并),高频批量更新场景下差异日志的行数可能远超预期 |
| 队列模式不适用 | 与其余 5 类日志不同,差异日志没有队列开关,`SqlSugarDiffLogAop` 里是同步调用 `.GetAwaiter().GetResult()`(见 `SqlSugarDiffLogAop.cs:130`),写入耗时直接计入业务请求耗时 |

## 8. 与议题描述不一致之处

Linear 议题 EDDIE-5 附带的调研笔记(`issue-01.md`)在「五个共同问题的答案」一条里写「不参与工作单元事务」。经本份核对源码(`SqlSugarDiffLogAop.cs` 类型注释、`docs/guide/auditing.md` 148-182 行「写入器的事务契约」),正确答案是**参与**——写入器必须经 `GetCurrentClient()` 使用当前工作单元的连接,业务回滚时差异日志随之回滚。这一点在 §4.4 第 3 条已给出结论并解释原因,已按代码与已发布文档的实际约定设计,不按议题笔记的表述实现。

## 9. 待确认的决策

| 决策 | 本份选择 | 理由 | 若用户想改的影响 |
| --- | --- | --- | --- |
| 实体类名 | `SysDiffLog` | 与 `docs/guide/auditing.md` 148-178 行已发布的示例代码同名,避免读过该文档的使用者困惑;若严格按记录类型名 `EntityDiffLogRecord` 直译应为 `SysEntityDiffLog`,两者均不与仓库任何既有类型冲突 | 改名只影响本包内部(实体类名、表名前缀、`AuditingLogMapper` 与 `SqlSugarEntityDiffLogWriter` 里的类型引用),不影响契约,是纯重命名 |
| 写入器类名 | `SqlSugarEntityDiffLogWriter` | 按「`SqlSugar` + 契约名去掉 `I` 前缀」的既有命名规则(`IOperationLogWriter` → `SqlSugarOperationLogWriter`),契约是 `IEntityDiffLogWriter` | 同上,纯重命名 |
| `EntityId`/`EntityType` 列宽 | 均 256 | 比照 5 个兄弟里同量级的 `ControllerName`/`ActionName`;`EntityId` 在多主键场景下用 `\|` 连接、理论上可能超出 256,但目前仓库内所有实体主键都是单列雪花 `long` 或 `Guid`,转字符串后远小于 256 | 若未来出现真正的复合主键实体且启用了差异审计,可能需要放宽到 512 或改用 `CodeFirst_BigString`,是加列宽,不是破坏性变更 |
| `AuditType`/`OperationType` 列宽 | 32 / 16 | 按当前已知取值(`"EntityChange"`;`"Create"`/`"Update"`/`"Delete"`/`"Restore"`)留有余量,与其余定长列同规格(`Length` 属性,超长走 `Clamp`) | 加列宽是非破坏性变更 |
| 是否新增 `[ModuleDataSource]` 支持 | 不支持,固定 `GetCurrentClient()` | 与「必须同事务」这条契约互斥(见 §7),本份不引入折中方案 | 若后续要支持,需要重新设计"如何在保持同事务的前提下按实体路由",不是简单加标注,已在 §7 标为已知边界 |

## 10. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿(已知的 `Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 随机失败与本份无关,不算阻塞)
- `IEntityDiffLogWriter` 经 `services.Replace` 顶替为 `SqlSugarEntityDiffLogWriter`,有测试断言
- 写入器调用 `GetCurrentClient()` 而非 `GetClientForEntity()`,有测试直接断言调用方法(不只是断言数据落库)
- `SysDiffLog` 按月分表落库,14 个属性全部映射,`BeforeData`/`AfterData`/`ChangedFields` 原样保留不截断
- 每个 `.cs` 文件带两行版权声明(分析器 `XHFH001`)
- 注释与 XML 文档注释为简体中文,且只说明代码做什么;权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息
- file-scoped namespace;表达式体方法/构造函数在本仓库明确关闭
- 包 README、`docs/packages/auditing-sqlsugar.md` 已同步更新(两处都要去掉「实体变更日志仍为空实现」的旧说明)
- 提交信息为中文 Conventional Commits,作用域 `auditing-sqlsugar`,**不加任何 AI 署名**
- 一个 PR 只做一件事:不顺手改与本份无关的文档

## 11. 下一份

本份完成后 **PR1(`Auditing.SqlSugar`)整体完成**,可以提交给上游。下一份是 PR2(`EventBus.SqlSugar`)范围内的 P3/P4(已有 spec/计划,若尚未实现)或 P6(已有 spec/计划),具体顺序按拆分方案 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 执行。
