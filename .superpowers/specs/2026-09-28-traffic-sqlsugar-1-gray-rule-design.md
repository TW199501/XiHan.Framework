# Traffic.SqlSugar 灰度规则仓储（1）设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-traffic-sqlsugar-1-gray-rule.md`
- **前置**：无（`XiHan.Framework.Traffic`、`XiHan.Framework.Data` 均已发布）
- **所属 PR**：独立一个 PR（`Traffic.SqlSugar`）
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-12
- **系列**：SqlSugar 持久化系列剩余模块之一，拆分方案见 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 3、5 节

> 本文档**自成一体**。实现本包所需的全部约束都写在这里。系列共用的约定在本文档里各自重复一份——若发现与拆分方案不一致，以本文档为准并在 PR 里提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。
>
> **本包最容易静默出错的地方有两处**：① `IGrayRuleRepository` 被注册为 **Singleton**（`framework/src/XiHan.Framework.Traffic/Extensions/DependencyInjection/XiHanTrafficServiceCollectionExtensions.cs:28`），而 SqlSugar 的 `ISqlSugarClientResolver` 是 **Scoped**——直接构造函数注入会在容器校验阶段（`ValidateOnBuild`）或运行时抛出生命周期错误，必须经 `IServiceScopeFactory` 手动开 scope。② `DefaultGrayRuleEngine.DecideAsync`（`framework/src/XiHan.Framework.Traffic/GrayRouting/Implementations/DefaultGrayRuleEngine.cs:76-79`）用 `(rule as GrayRule)?.TargetVersion` **按具体类型做模式匹配**取目标版本——若本包返回的规则对象不是 `GrayRule`（或其子类）的实例，这一步会静默得到 `null`，灰度决策永远退化成字面量 `"gray"`，没有任何异常、任何测试红灯。第 5 节 ② 是这条的详细展开。

---

## 1. 背景与目标

### 1.1 现状

`IGrayRuleRepository`（`framework/src/XiHan.Framework.Traffic/GrayRouting/Abstractions/IGrayRuleRepository.cs`）声明 3 个方法：

```csharp
public interface IGrayRuleRepository
{
    Task<List<IGrayRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default);
    Task<IGrayRule?> GetRuleByIdAsync(string ruleId, CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
```

接口自带的 XML 注释写明了它的定位："负责灰度规则的读取,不负责写入和管理"——"Gateway 只读取,应用层管理"。这不是疏漏，是有意的职责边界：写入/管理规则是应用层（例如后台管理系统）的事，本仓储**只读**。

`DefaultGrayRuleRepository` 是进程内存实现（`ConcurrentDictionary<string, IGrayRule>`），另有 `AddRule`/`RemoveRule`/`Clear` 三个"仅用于测试"的方法，不在接口上。

**热路径调用方**：`DefaultGrayRuleEngine.DecideAsync`（`framework/src/XiHan.Framework.Traffic/GrayRouting/Implementations/DefaultGrayRuleEngine.cs:38-90`）在**每一次**灰度决策时都调用 `GetEnabledRulesAsync`（第 43 行），逐条匹配。灰度决策是路由热路径，这意味着 `GetEnabledRulesAsync` 会被高频调用——不能每次都查库。

**注册方式**：`XiHanTrafficServiceCollectionExtensions.AddGrayRouting()` 用 `TryAddSingleton` 注册默认实现（第 28 行），并且**已经提供了**一个替换用的泛型扩展方法：

```csharp
public static IServiceCollection ReplaceGrayRuleRepository<TRepository>(this IServiceCollection services)
    where TRepository : class, IGrayRuleRepository
{
    services.Replace(ServiceDescriptor.Singleton<IGrayRuleRepository, TRepository>());
    return services;
}
```

（`framework/src/XiHan.Framework.Traffic/Extensions/DependencyInjection/XiHanTrafficServiceCollectionExtensions.cs:59-64`）

本包直接调用这个既有扩展方法完成注册，不重新发明。

**`(rule as GrayRule)` 的模式匹配**：`DefaultGrayRuleEngine.DecideAsync` 命中规则后取目标版本用的是：

```csharp
return GrayDecision.Gray(
    targetVersion: (rule as GrayRule)?.TargetVersion ?? "gray",
    ...);
```

`IGrayRule` 接口本身**不暴露** `TargetVersion`（接口只有 `RuleId`/`RuleName`/`RuleType`/`IsEnabled`/`Priority` 五个只读属性，见 `IGrayRule.cs`），`TargetVersion` 只存在于具体类 `GrayRule`（`framework/src/XiHan.Framework.Traffic/GrayRouting/Models/GrayRule.cs`）上。这意味着本包返回的规则对象的**运行时类型必须是 `GrayRule`**（或其子类），哪怕自己实现一个"更规范"的、单纯实现 `IGrayRule` 接口的类，也会让上面这行 `as` 转换失败，目标版本永远读不到。

### 1.2 交付目标

1. 新增 `XiHan.Framework.Traffic.SqlSugar` 包，提供 `IGrayRuleRepository` 的 SqlSugar 落库实现 `SqlSugarGrayRuleRepository`
2. 落库实体 `SysGrayRule`，供应用层直接用 SqlSugar 增删改（本包只读，见 1.1 节接口定位）
3. 内存缓存 + 显式 `RefreshAsync` + 到期自动刷新，避免每次决策都查库
4. 用既有的 `ReplaceGrayRuleRepository<TRepository>()` 完成注册

### 1.3 成功标准

1. `GetEnabledRulesAsync` 返回的规则运行时类型是 `GrayRule`，`(rule as GrayRule)?.TargetVersion` 能取到值
2. 应用层直接向 `sys_gray_rule` 表插入一行后，调用 `RefreshAsync()` 能让 `GetEnabledRulesAsync` 立即看到新规则
3. 缓存过期后自动从数据库重新加载，不需要显式调用 `RefreshAsync`
4. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约与主包实现——最高优先**

```
framework/src/XiHan.Framework.Traffic/GrayRouting/
  Abstractions/IGrayRuleRepository.cs                 契约的 3 个方法
  Abstractions/IGrayRule.cs                           接口只有 5 个只读属性
  Models/GrayRule.cs                                  具体类，含 TargetVersion 等契约外字段
  Implementations/DefaultGrayRuleRepository.cs        内存实现范本
  Implementations/DefaultGrayRuleEngine.cs:38-90       唯一的热路径调用方，含 (rule as GrayRule) 陷阱
  Enums/GrayRuleType.cs                               规则类型枚举
framework/src/XiHan.Framework.Traffic/Extensions/DependencyInjection/
  XiHanTrafficServiceCollectionExtensions.cs:22-64     确认 TryAddSingleton 与既有的 ReplaceGrayRuleRepository
```

**② 兄弟子包范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/       简单实体形状范本
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs                   GetClientForEntity<T>() 的签名，确认其为 Scoped
```

**③ SqlSugar 源码——API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Abstract/QueryableProvider/QueryableProvider.cs      Where / OrderBy / ToListAsync
```

> 权威源码是 `Src/Asp.NetCore2/`（**不是** `Src/Asp.Net/`）。当前引用版本 `SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：`查詢數據.md`。

仓库自身文档：`docs/packages/traffic.md`、`docs/packages/auditing-sqlsugar.md`（同类子包文档范本）。

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

- **不要给 `SqlSugarGrayRuleRepository` 的构造函数直接注入 `ISqlSugarClientResolver`**。它是 Scoped，本仓储是 Singleton，直接注入会在依赖图校验或运行时出错。必须注入 `IServiceScopeFactory`，每次刷新时 `CreateScope()` 取一个临时的 Scoped 解析器，用完随 `using` 释放。
- **不要新建一个只实现 `IGrayRule` 接口的类替代 `GrayRule`**。见"实现前必读"——`DefaultGrayRuleEngine` 依赖具体类型 `GrayRule` 做模式匹配，返回值的运行时类型必须是 `GrayRule`。
- **不要在本仓储上实现写方法**（不要给 `IGrayRuleRepository` 加 `AddRuleAsync` 之类）。接口的定位是"只读"，写入由应用层直接对 `SysGrayRule` 表操作。
- **不要让 `GetEnabledRulesAsync` 每次都查库**。它是路由热路径的直接调用方，必须走缓存。

## 3. 非目标

- **不修改 `IGrayRuleRepository` 契约**，不加写方法。
- **不做多库路由**。灰度规则是全局配置，不涉及 `[ModuleDataSource]`。
- **不做分表**。规则由运营人员手工维护，条数以百为量级，不是随时间增长的数据。
- **不做后台管理 API**。应用层如何写入 `sys_gray_rule` 表（管理界面、导入脚本）不在本包范围，本包只发布实体与只读仓储。
- **不实现分布式缓存失效通知**（如经消息队列广播刷新）。缓存刷新只有"到期自动刷新"与"显式 `RefreshAsync()`"两种触发方式，多实例部署下每个实例各自到期后独立刷新，见 §7 已知边界。

## 4. 设计

### 4.1 实体：`SysGrayRule`

```csharp
[SugarTable("sys_gray_rule")]
public class SysGrayRule : SugarEntity<string>
```

主键类型是 `string`（`RuleId`），由应用层赋值，不是雪花 ID——见 §5 共同问题 2。选 `SugarEntity<string>`（无审计字段的基类）而非 `SugarFullAuditedEntity`：`GrayRule` 模型只有 `CreatedTime`/`UpdatedTime` 两个时间戳，没有创建人/修改人/软删除字段，引入全审计基类会带出一堆用不到的列。

字段：

| 属性 | 列名 | 类型 | 说明 |
| --- | --- | --- | --- |
| `BasicId`（继承） | `Basic_Id` | `string` | 主键，即 `RuleId`，`IsIdentity = false` |
| `RuleName` | `Rule_Name` | `string` | 规则名称 |
| `RuleType` | `Rule_Type` | `int`（枚举） | 对应 `GrayRuleType` |
| `IsEnabled` | `Is_Enabled` | `bool` | 是否启用 |
| `Priority` | `Priority` | `int` | 优先级 |
| `TargetVersion` | `Target_Version` | `string?` | 目标版本 |
| `TargetServiceId` | `Target_Service_Id` | `string?` | 目标服务标识 |
| `Configuration` | `Configuration` | `string?`，`ColumnDataType = StaticConfig.CodeFirst_BigString` | JSON 配置，结构随 `RuleType` 变化，用大文本列而非固定长度 |
| `EffectiveTime` | `Effective_Time` | `DateTimeOffset?` | 生效时间 |
| `ExpiryTime` | `Expiry_Time` | `DateTimeOffset?` | 失效时间 |
| `CreatedTime` | `Created_Time` | `DateTimeOffset` | 创建时间 |
| `UpdatedTime` | `Updated_Time` | `DateTimeOffset?` | 更新时间 |
| `Remark` | `Remark` | `string?` | 备注 |

不分表、不做多库路由、无租户列（`GrayRule` 模型本身也没有租户字段，`TenantId` 规则类型是靠 `Configuration` JSON 里的 `tenantIds` 数组表达，不是独立列）。

### 4.2 映射：`GrayRuleMapper`

纯静态方法，`SysGrayRule ↔ GrayRule` 双向转换：

```csharp
public static class GrayRuleMapper
{
    public static GrayRule ToModel(SysGrayRule entity);
    public static SysGrayRule ToEntity(GrayRule model);
}
```

`ToModel` 返回的是**具体类 `GrayRule`**（`XiHan.Framework.Traffic.GrayRouting.Models.GrayRule`），不是自定义类型——这正是解决"实现前必读"② 那个陷阱的地方。仓储对外一律通过 `ToModel` 产出规则对象，调用方拿到的 `IGrayRule` 在运行时永远是 `GrayRule` 的实例。

### 4.3 缓存与刷新

`SqlSugarGrayRuleRepository` 内部维护：

```csharp
private readonly IServiceScopeFactory _scopeFactory;
private readonly IOptions<XiHanTrafficSqlSugarOptions> _options;
private readonly Lock _refreshLock = new();
private volatile Dictionary<string, GrayRule> _cache = new(StringComparer.Ordinal);
private DateTimeOffset _lastRefreshTime = DateTimeOffset.MinValue;
```

- `GetEnabledRulesAsync` / `GetRuleByIdAsync`：先判断 `DateTimeOffset.UtcNow - _lastRefreshTime >= _options.Value.RefreshInterval`，超过则先 `await RefreshAsync()` 再读缓存；未超过直接读缓存
- `RefreshAsync`：`IServiceScopeFactory.CreateScope()` 开一个临时 scope，取该 scope 内的 `ISqlSugarClientResolver`，查询 `sys_gray_rule` 全表（不只查启用的——`GetRuleByIdAsync` 也可能查到禁用规则），整表加载进新的 `Dictionary<string, GrayRule>`，用 `Interlocked.Exchange` 或直接字段赋值原子替换 `_cache`（引用赋值本身是原子的，读者拿到的要么是旧字典要么是新字典，不会读到"半更新"状态），最后更新 `_lastRefreshTime`
- 刷新用 `Lock`（.NET 9+ 的 `System.Threading.Lock`）避免并发刷新时重复查库；`GetEnabledRulesAsync`/`GetRuleByIdAsync` 判断"是否需要刷新"时不加锁（允许多个并发请求都判断为"需要刷新"，但 `RefreshAsync` 内部用锁把实际查库串行化，锁外的请求在锁释放后读到的已经是刷新后的缓存）

`XiHanTrafficSqlSugarOptions`：

```csharp
public class XiHanTrafficSqlSugarOptions
{
    public const string SectionName = "XiHan:Traffic:SqlSugar";
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
```

配置节 `XiHan:Traffic:SqlSugar`。

### 4.4 注册

`XiHanTrafficServiceCollectionExtensions.AddGrayRouting()` 用 `TryAddSingleton` 注册默认实现（`XiHanTrafficServiceCollectionExtensions.cs:28`），且主包**已经提供**替换用的扩展方法 `ReplaceGrayRuleRepository<TRepository>()`（同文件 59-64 行），本包直接复用：

```csharp
services.ReplaceGrayRuleRepository<SqlSugarGrayRuleRepository>();
```

不重新写一遍 `services.Replace(ServiceDescriptor.Singleton<...>())`。

## 5. 五个共同问题

1. **分表与否**：不分表。规则由运营人员手工维护，条数以百为量级，不是随时间增长的数据。
2. **主键类型**：`string`（`RuleId`），由应用层赋值。契约的两个读方法都以 `string ruleId` 作为查找键（`GetRuleByIdAsync(string ruleId, ...)`），主键必须与之一致，不能引入一个额外的自增/雪花主键再拿 `RuleId` 做唯一索引——那样反而多一层间接。
3. **是否参与工作单元事务**：**不参与**。本仓储是 Singleton、只读、走独立于业务请求的缓存刷新周期，不应该也无法钉在某一次业务请求的工作单元里。
4. **是否需要多库**：不需要。灰度规则是全局配置。
5. **顶替方式**：主包用 `TryAddSingleton` 注册 `DefaultGrayRuleRepository`，且**已经**提供了 `ReplaceGrayRuleRepository<TRepository>()` 这个基于 `services.Replace(ServiceDescriptor.Singleton<...>())` 的扩展方法，本包直接调用它，不重复造轮子。

## 6. 会静默失效的陷阱

**① 返回的规则对象不是 `GrayRule` 的实例。**

见"实现前必读"。`DefaultGrayRuleEngine.DecideAsync` 用 `(rule as GrayRule)?.TargetVersion ?? "gray"` 取目标版本——如果本包图"接口隔离更规范"而自定义一个只实现 `IGrayRule` 的 DTO 类型，这一步的 `as` 转换会静默返回 `null`，`TargetVersion` 永远读到兜底值 `"gray"`。**这个错误不会抛异常、不会让任何单元测试失败**（除非专门测试 `DecideAsync` 与本仓储的集成），因为 `IGrayRule` 接口本身没有 `TargetVersion` 这个成员，类型系统层面完全合法。必须在测试里断言 `GetEnabledRulesAsync()` 返回列表里的元素能 `as GrayRule` 转换成功且 `TargetVersion` 保留正确的值。

**② Singleton 仓储直接注入 Scoped 客户端解析器。**

`ISqlSugarClientResolver` 是 Scoped（`XiHanDataServiceCollectionExtensions.cs:61`），若构造函数直接声明 `ISqlSugarClientResolver clientResolver` 参数，在 `services.BuildServiceProvider(validateScopes: true)`（多数测试与生产环境的默认行为）下会在容器构建期直接抛出 `InvalidOperationException`（"Cannot consume scoped service from singleton"）。必须注入 `IServiceScopeFactory`，每次刷新时 `CreateScope()`。这个错误在**不开启** `validateScopes` 的宽松环境下甚至不会立刻爆出来，而是在运行时某个不确定的时刻表现为诡异的跨请求数据串用——务必在 Task 里对着构造函数签名核对一遍。

**③ 缓存到期判断与实际刷新之间的竞态导致击穿。**

如果"判断是否需要刷新"与"执行刷新"共用同一把锁、且锁的粒度覆盖了整个读路径，会让所有并发的路由决策请求在缓存到期的瞬间排队等一次数据库查询，路由热路径出现毛刺。本设计里"判断"不加锁、"刷新执行"内部加锁做去重，是刻意的取舍（见 4.3 节），实现时不要把锁的范围扩大到覆盖 `GetEnabledRulesAsync`/`GetRuleByIdAsync` 的整个方法体。

## 7. 测试策略

**第一层——SQLite，CI 强门禁执行**

- 建表：`SysGrayRule` 能在 SQLite 上建出 `sys_gray_rule` 表
- 映射往返：`GrayRuleMapper.ToEntity(GrayRuleMapper.ToModel(entity))` 各字段与原始 `entity` 一致（不含时间戳的毫秒级精度断言，避免 SQLite `DateTimeOffset` 折叠问题）
- **具体类型断言（对应陷阱①）**：向表中插入一条启用规则，`RefreshAsync()` 后 `GetEnabledRulesAsync()` 返回的元素 `is GrayRule`，且 `((GrayRule)item).TargetVersion` 等于插入时的值
- 只返回启用规则：插入一条禁用规则与一条启用规则，`GetEnabledRulesAsync()` 只包含启用的那条
- `GetRuleByIdAsync` 能查到禁用规则（契约未限定只查启用规则，`DefaultGrayRuleRepository.GetRuleByIdAsync` 同样不过滤 `IsEnabled`）
- `GetRuleByIdAsync` 查不存在的标识返回 `null`
- 显式刷新：先插入一条规则后调用 `RefreshAsync()`（不等到期），能立即读到
- 到期自动刷新：把 `RefreshInterval` 设为极短值（如 1 毫秒），插入规则后等待超过该间隔，不显式调用 `RefreshAsync()` 也能读到（用 `Task.Delay` 等待，避免测试对时钟精度过分敏感）
- 未到期不查库：把 `RefreshInterval` 设为很长的值，`RefreshAsync()` 加载一次后，插入新规则但不调用 `RefreshAsync()`，`GetEnabledRulesAsync()` 仍返回旧结果（验证走的是缓存而非每次查库）
- 注册断言：`services.AddXiHanTrafficSqlSugar()` 后 `IGrayRuleRepository` 的实现类型是 `SqlSugarGrayRuleRepository`

SQLite 临时库连接串必须带 `Pooling=False`。

**第二层**：本包不涉及并发写入协议或多实例竞态，不需要真实数据库层的专门测试。

## 8. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 多实例缓存不同步 | 每个实例各自维护本地缓存、各自按 `RefreshInterval` 独立刷新。应用层在一个实例上调用 `RefreshAsync()`（若能拿到该实例的仓储引用）不会让其他实例立即感知，规则变更在最坏情况下需要等到 `RefreshInterval` 才会在所有实例生效 |
| 规则写入完全由应用层负责 | 本包不提供任何写方法，`sys_gray_rule` 表由应用层直接用 SqlSugar（或其他方式）维护，本包只负责只读查询与缓存 |
| `Configuration` 列不做 JSON 校验 | 存取时按字符串原样处理，格式是否合法由调用方（`IGrayMatcher` 实现）在使用时自行解析校验 |

## 9. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `GetEnabledRulesAsync` 返回的元素运行时类型是 `GrayRule`，有测试断言 `as GrayRule` 转换成功
- 注册经既有的 `ReplaceGrayRuleRepository<TRepository>()` 完成，有测试断言顶替生效
- 每个 `.cs` 文件带两行版权声明
- 注释与 XML 文档注释为简体中文，且只说明代码做什么
- 包 README 沿用固定七段结构
- 提交信息中文 Conventional Commits，作用域 `traffic-sqlsugar`，不加 AI 署名

## 10. 下一份

本包只有一份计划。系列内下一个包见 `.superpowers/plans/2026-09-28-upgrade-sqlsugar-1-upgrade-version.md`（独立计划，互不依赖）。
