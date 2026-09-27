# Authorization.SqlSugar ①：包骨架与权限存储 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-authorization-sqlsugar-1-permission-store.md`
- **前置**：无（`XiHan.Framework.Data` 的 P5 能力已在 `dev`，本份不依赖它）
- **所属 PR**：`Authorization.SqlSugar` 单独一个 PR，由本份与 ②（角色存储与权限检查器）、③（策略存储与收尾）共同组成
- **Linear 议题**：<https://linear.app/elf-express/issue/EDDIE-10>
- **系列**：`Authorization.SqlSugar` 共 3 份。① 包骨架 + 权限存储；② 角色存储 + 权限检查器（热路径）；③ 策略存储 + 文档收尾

> 本文档**自成一体**。实现 ① 所需的全部约束都写在这里，不引用其他设计文档。三份共用的约定（表名、主键、租户、客户端）在每份里各重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的两处**：
>
> 1. **租户接口选错**。授予行必须实现 `IStrictMultiTenantEntity`，不是只实现 `IMultiTenantEntity`。选错**不会报错、SQLite 测试照样全绿**——测试客户端根本没注册租户过滤器。故障只在真正多租户的应用里出现：租户能看见平台态授予的行。
> 2. **表名与下游撞车**。下游 `XiHan.BasicApp` 已有 `Sys_Role`、`Sys_Permission` 两张结构完全不同的表。本包若用 `sys_role` / `sys_permission`，MySQL 与 SQL Server 默认大小写不敏感，CodeFirst 会去**改那张已有的表**——加列、改列，不报错。

---

## 1. 背景与目标

### 1.1 现状

`XiHan.Framework.Authorization` 定义了三个存储契约，默认实现全部是内存字典：

| 契约 | 方法数 | 默认实现 |
| --- | --- | --- |
| `IPermissionStore`（`framework/src/XiHan.Framework.Authorization/Permissions/IPermissionStore.cs:12-76`） | 8 | `DefaultPermissionStore` |
| `IRoleStore`（`Roles/IRoleStore.cs`） | 11 | `DefaultRoleStore` |
| `IPolicyStore`（`Policies/IPolicyStore.cs`） | 5 | `DefaultPolicyStore` |

三者在 `XiHanAuthorizationServiceCollectionExtensions.cs:30-36` 以 **`TryAddScoped`** 注册。**作用域生命周期 + 实例字段里的字典**意味着：每个请求拿到一个全新的空存储，前一个请求写进去的数据下一个请求就看不见。默认实现在真实应用里等于不可用——下游 `XiHan.BasicApp` 的 `SaasPermissionChecker` 注释里写明了「框架默认的 `DefaultPermissionChecker` 走内存版 `IPermissionStore/IRoleStore`，对真实用户恒返回 false」。

`IPermissionStore` 的 8 个方法：

```
GetUserPermissionsAsync(userId)                  用户直接拥有的权限定义
GetRolePermissionsAsync(roleId)                  角色拥有的权限定义
GrantPermissionToUserAsync(userId, name)
RevokePermissionFromUserAsync(userId, name)
GrantPermissionToRoleAsync(roleId, name)
RevokePermissionFromRoleAsync(roleId, name)
GetAllPermissionsAsync()
GetPermissionByNameAsync(name)
```

**契约里没有「新增权限定义」的方法。** `DefaultPermissionStore` 把 `AddOrUpdatePermissionAsync`、`AddPermissionsAsync`、`RemovePermissionAsync` 放在了实现类上（`DefaultPermissionStore.cs:214-269`），不在接口里。落库实现若不提供等价入口，权限定义就只能由应用手写 SQL 灌进去。

### 1.2 默认实现的语义（落库实现必须保持）

读 `DefaultPermissionStore.cs` 得出，逐条保持：

| 行为 | 默认实现 | 位置 |
| --- | --- | --- |
| 用户权限只含**直接授予**，不含经角色得到的 | `_userPermissions[userId]` | `:43-61` |
| 角色权限按**角色标识**（不是名称）存取 | `_rolePermissions[roleId]` | `:69-87` |
| 授予时**不校验**权限定义是否存在 | 直接 `HashSet.Add` | `:95-109` |
| 读取时**只返回有定义的**授予（定义缺失的静默跳过） | `Select(...).Where(p != null)` | `:55-58` |
| 读取**不过滤** `IsEnabled`（过滤由检查器做） | 无过滤 | 同上 |
| 重复授予是幂等的 | `HashSet` | 同上 |
| 空参数：读返回空集合 / `null`，写直接返回，不抛异常 | `string.IsNullOrEmpty` 守卫 | 各方法开头 |
| 删除权限定义**不级联**删除授予 | `_permissions.TryRemove` 只动定义字典 | `:230-239` |

### 1.3 要交付什么

**新增包 `XiHan.Framework.Authorization.SqlSugar`**。① 交付：

1. 包骨架：csproj、模块类、注册扩展、测试项目、解决方案注册
2. 三个实体：权限定义、用户权限授予、角色权限授予
3. `SqlSugarPermissionStore`：`IPermissionStore` 的 8 个方法 + 与默认实现同名的 3 个定义维护方法
4. 以 `Replace` 顶替 `IPermissionStore`

角色、用户角色关联、权限检查器在 ②；策略、README、文档站在 ③。

### 1.4 成功标准

1. `SqlSugarPermissionStore` 通过 1.2 表的每一条行为测试（SQLite）
2. 同一租户内重复授予只留一行；不同租户可各有一行
3. 三个实体的表名均以 `sys_authz_` 开头；授予实体实现 `IStrictMultiTenantEntity`；权限定义实体**不**实现 `IMultiTenantEntity`
4. 注册后 `IPermissionStore` 只有一个描述符，实现类型为 `SqlSugarPermissionStore`、生命周期 `Scoped`
5. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 要实现的契约与默认实现 —— 最高优先**

```
framework/src/XiHan.Framework.Authorization/
  Permissions/IPermissionStore.cs            要实现的 8 个方法
  Permissions/DefaultPermissionStore.cs      语义基准，见 1.2
  Permissions/PermissionDefinition.cs        字段来源
  Extensions/DependencyInjection/XiHanAuthorizationServiceCollectionExtensions.cs:30-36   TryAddScoped
```

**② 同系列已完成的包 —— 形状范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  Writers/SqlSugarLoginLogWriter.cs          ISqlSugarClientResolver + IDistributedIdGenerator<long> 的构造形状
  Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs   Replace 写法
  XiHanAuditingSqlSugarModule.cs             模块类只做装配
framework/src/XiHan.Framework.EventBus.SqlSugar/
  Entities/SysEventOutbox.cs                 实体、SugarColumn、TableInitialization 写法
framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  XiHan.Framework.Auditing.SqlSugar.Tests.csproj   测试项目只 Import 三个 props
  LogWriterTests.cs:342-394                  StubClientResolver 写法
```

**③ 数据层 —— 租户、建表、审计 AOP**

```
framework/src/XiHan.Framework.Data/
  SqlSugar/Entities/SugarEntity.cs                   Basic_Id / Row_Version
  SqlSugar/Entities/SugarMultiTenantEntity.cs        Tenant_Id（long，0 = 平台）
  Extensions/DependencyInjection/XiHanDataServiceCollectionExtensions.cs:229-266   两个租户过滤器
  SqlSugar/Auditing/SqlSugarDataExecutingHandler.cs  插入时自动填 TenantId
  SqlSugar/Extensions/EntityAuditExtensions.cs:161-189   SetTenantIdValue 的跨租户防护
  SqlSugar/Initializers/TableInitializationAttribute.cs   Group / Enabled
  SqlSugar/Initializers/DbEntityTypeProvider.cs:65-110    OptIn 模式下不标注即不建表
  SqlSugar/Clients/ISqlSugarClientResolver.cs        GetCurrentClient
framework/src/XiHan.Framework.Domain/Entities/Abstracts/IStrictMultiTenantEntity.cs   严格隔离的语义
```

**④ SqlSugar 源码 —— API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs                          InnerJoin / Where / Select / AnyAsync / FirstAsync / ToListAsync
  Abstract/QueryableProvider/QueryableExecuteSqlAsync.cs:132-140   AnyAsync = Take(1).Select("1")
  Interface/IUpdateable.cs                         SetColumns / Where
  Interface/IDeleteable.cs                         Where
  Entities/Mapping/SugarMappingAttribute.cs:341-390   SugarIndexAttribute 的构造函数
  Abstract/CodeFirstProvider/CodeFirstProvider.cs:350-395   索引字段按「属性名」匹配、索引名已存在即跳过
```

> 树与目录名：本包引用 SqlSugarCore 5.1.4.221（依赖 `Microsoft.Data.Sqlite`），权威源码是 `Src/Asp.NetCore2/`。同级的 `Src/Asp.Net/` 是 .NET Framework 变体。更新提供者的目录名是 `UpdateProvider`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 联表 | `聯表查詢.md`、`Select用法.md`（4.5 节「只查一張表的欄位」） |
| 过滤器 | `查詢過濾器.md`（第五节联表过滤器位置） |
| 条件更新与删除 | `更新數據.md`、`刪除數據.md` |
| 索引 | `庫表管理DbMaintenance.md` |

仓库自身文档：`docs/packages/data.md`（租户过滤、建表选项）、`docs/packages/authorization.md`。

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

- **不改 `XiHan.Framework.Authorization` 主包的任何文件**。契约、默认实现、注册代码一律不动。
- **不调 `GetClientForEntity<T>()`**。本包所有表必须落在同一个库里才能联表，统一用 `GetCurrentClient()`。
- **不依赖全局租户过滤器做隔离**。授予表的每条读写都显式带 `TenantId == ICurrentTenant.Id ?? 0`，插入时显式赋同一个值，见 §4.4。
- **不截断权限名称、用户标识、角色标识**。`Auditing.SqlSugar` 的映射器会把超长文本截到列宽，本包**不能照抄**：截断后的名称是另一个权限。
- **不做软删除**。授予行撤销即物理删除。
- **不加缓存**。见 §4.7 与「待确认的决策」。
- **不写 `<inheritdoc/>`**。仓库已在 `9eb340ce` 把全部 `inheritdoc` 换成了完整的 XML 文档注释。

## 3. 非目标

- 不实现 `IRoleStore`、`IPermissionChecker`（②）与 `IPolicyStore`（③）
- 不写包 README 与文档站条目（③ 收尾）
- 不做权限定义的层级校验（`ParentName` 只存不校验，与默认实现一致）
- 不做通配权限（`*`）。默认实现与默认检查器都没有这个语义
- 不支持真实数据库并发测试层：本份没有并发协议，唯一的并发冲突（重复授予）由唯一索引兜底

## 4. 设计

### 4.1 包的整体数据模型（三份共用，本份只建前三张表）

| 实体 | 表 | 基类 | 租户 | 所属份 |
| --- | --- | --- | --- | --- |
| `SysAuthzPermission` | `sys_authz_permission` | `SugarEntity<long>` | 全局，不分租户 | ① |
| `SysAuthzUserPermission` | `sys_authz_user_permission` | `SugarMultiTenantEntity<long>` + `IStrictMultiTenantEntity` | 严格隔离 | ① |
| `SysAuthzRolePermission` | `sys_authz_role_permission` | 同上 | 严格隔离 | ① |
| `SysAuthzRole` | `sys_authz_role` | 同上 | 严格隔离 | ② |
| `SysAuthzUserRole` | `sys_authz_user_role` | 同上 | 严格隔离 | ② |
| `SysAuthzPolicy` | `sys_authz_policy` | `SugarEntity<long>` | 全局，不分租户 | ③ |

议题描述估 3–5 个实体，实际是 6 个：契约有 `GrantPermissionToUserAsync`，用户直接授予需要自己的一张表。

**关联表存的是契约里的字符串标识，不是其他表的 `Basic_Id`。** 契约的用户、角色都以 `string` 传入（角色标识由调用方在 `RoleDefinition.Id` 上给出），关联表直接存这个字符串，热路径联表时不必先把字符串换成雪花主键。

### 4.2 实体字段

所有列名 Pascal_Snake_Case，每列带简体中文 `ColumnDescription`。基类带 `Basic_Id`（雪花 `long`，非自增）与 `Row_Version`；多租户基类另带 `Tenant_Id`（`long`，`IsOnlyIgnoreUpdate`）。

**`SysAuthzPermission`**

| 属性 | 列 | 类型 | 长度 | 可空 |
| --- | --- | --- | --- | --- |
| `PermissionName` | `Permission_Name` | `string` | 256 | 否 |
| `DisplayName` | `Display_Name` | `string` | 256 | 否 |
| `Description` | `Description` | `string?` | 1024 | 是 |
| `ParentName` | `Parent_Name` | `string?` | 256 | 是 |
| `Tag` | `Tag` | `string?` | 128 | 是 |
| `IsEnabled` | `Is_Enabled` | `bool` | — | 否 |
| `SortOrder` | `Sort_Order` | `int` | — | 否 |
| `Properties` | `Properties` | `string?`（`CodeFirst_BigString`） | — | 是 |

`PermissionDefinition.Order` 映射为 `SortOrder` / `Sort_Order`：`Order` 是 SQL 关键字。

**`SysAuthzUserPermission`**：`UserId`（`User_Id`，128）、`PermissionName`（`Permission_Name`，256）。

**`SysAuthzRolePermission`**：`RoleId`（`Role_Id`，128）、`PermissionName`（`Permission_Name`，256）。

### 4.3 索引

`[SugarIndex]` 的字段参数是**属性名**（`CodeFirstProvider.cs:384` 按 `PropertyName` 找列），用 `nameof(类名.属性)` 写，不写列名。索引名在 PostgreSQL 里是 schema 级唯一、SQLite 里是库级唯一，`CodeFirstProvider.cs:377` 见同名索引即跳过——因此索引名必须带表的缩写，且长度控制在 30 字符以内。

| 索引名 | 表 | 字段 | 唯一 | 服务于 |
| --- | --- | --- | --- | --- |
| `ux_authz_perm_name` | permission | `PermissionName` | 是 | 按名读定义；热路径联表的被驱动表 |
| `ux_authz_up_tenant_user_perm` | user_permission | `TenantId, UserId, PermissionName` | 是 | 按用户读直接授予；幂等授予 |
| `ux_authz_rp_tenant_role_perm` | role_permission | `TenantId, RoleId, PermissionName` | 是 | 按角色读授予；热路径联表；删角色时级联 |

复合索引首列是 `TenantId`：每条查询都显式带 `Tenant_Id = ?`，首列等值才能用上索引。

### 4.4 租户

读 `XiHanDataServiceCollectionExtensions.cs:239-256` 得出两个过滤器：

- `IMultiTenantEntity`：**读共享**——租户态看「本租户 + `TenantId = 0` 的平台行」
- `IStrictMultiTenantEntity`（继承前者）：再加一条 `TenantId == 当前租户 ?? 0`，AND 之后变成**严格相等**

授予行选严格隔离：

- 读共享会让平台态授予的行在每个租户里都可见。平台态用户与租户态用户的标识若恰好相同（字符串标识，不保证全局唯一），租户用户就继承了平台的权限
- `IStrictMultiTenantEntity` 的注释（`IStrictMultiTenantEntity.cs:9-22`）明写它就是给「平台与租户各自拥有独立数据、不存在共用」的表用的

权限定义选全局：权限名由代码里的 `[PermissionAuthorize("...")]` 引用，是全应用一份的目录，不随租户变化。

**显式的当前租户条件（不依赖过滤器）**：实体实现 `IStrictMultiTenantEntity` 只是第一道防线。全局过滤器可以被 `EnableTenantFilter = false` 整体关掉，那时只按 `User_Id` / `Role_Id` 查询，租户 A 的用户 `"1001"` 会读到租户 B 同名用户的授予——跨租户授权。因此授予表的**每一条**查询、更新、删除都显式带 `TenantId == tenantId`，插入时显式把 `TenantId` 赋成同一个值：

- `tenantId = ICurrentTenant.Id ?? 0`。`ICurrentTenant`（`XiHan.Framework.MultiTenancy.Abstractions`）的实现 `CurrentTenant` 读 `ICurrentTenantAccessor.Current?.TenantId`，无租户上下文时为 `null`
- **平台态（宿主）取 0**：与数据层 AOP 在平台态插入时保留的 0（`EntityAuditExtensions.SetTenantIdValue`：上下文为空时不改预置值）一致，也与严格过滤器平台态的口径 `Current?.TenantId ?? 0`（`XiHanDataServiceCollectionExtensions.cs:481-484` 的 `ResolveStrictTenantScopeId`）一致。平台态读写的是平台自己的授予，看不到任何业务租户的授予——这是合法的宿主路径，不会被显式条件打断
- 插入时显式赋值不与 AOP 冲突：租户态预置值等于上下文时 AOP 放行；平台态 AOP 不改预置值
- 与同一轮的 `Authentication.SqlSugar` 做法一致（每条 SQL 显式 `Tenant_Id` 等值）

权限定义表全局，不带租户条件。

**更新与删除**：`EnableAutoUpdateQueryFilter` / `EnableAutoDeleteQueryFilter` 默认 `true`，过滤器开着时与显式条件叠加，结果相同；关掉时显式条件仍然生效。

### 4.5 客户端与事务

所有读写统一：

```csharp
private ISqlSugarClient Client => _clientResolver.GetCurrentClient();
```

- **不用 `GetClientForEntity<T>()`**：它按实体的 `[ModuleDataSource]` 路由。本包的查询要跨 3–5 张表联表，若有人给其中一张加了模块数据源，联表就跨库了。统一取当前租户的主库，六张表永远同库
- **参与工作单元**：`GetCurrentClient()` 在存在事务型工作单元时会自动登记（`ISqlSugarClientResolver.cs:13`），授予与业务写入同事务提交或回滚。存储因此注册为 `Scoped`，与主包默认一致

### 4.6 `SqlSugarPermissionStore`

构造函数：`(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator)`。主键在插入前由 `idGenerator.NextId()` 取，写法与 `SqlSugarLoginLogWriter` 一致。私有属性 `CurrentTenantId => _currentTenant.Id ?? 0`，各方法先取到局部变量再写进表达式。

| 方法 | SQL 形状 | 走的索引 |
| --- | --- | --- |
| `GetUserPermissionsAsync(userId)` | `user_permission INNER JOIN permission ON Permission_Name`，`WHERE Tenant_Id = @当前租户 AND User_Id = ?`，选 permission 整行 | `ux_authz_up_*` → `ux_authz_perm_name` |
| `GetRolePermissionsAsync(roleId)` | `role_permission INNER JOIN permission ON Permission_Name`，`WHERE Tenant_Id = @当前租户 AND Role_Id = ?` | `ux_authz_rp_*` → `ux_authz_perm_name` |
| `GrantPermissionToUserAsync` | `AnyAsync(Tenant_Id, User_Id, Permission_Name)` 查重 → 无则 `Insertable`，`TenantId` 显式赋当前租户 | `ux_authz_up_*` |
| `RevokePermissionFromUserAsync` | `Deleteable ... WHERE Tenant_Id AND User_Id AND Permission_Name` | 同上 |
| `GrantPermissionToRoleAsync` / `RevokePermissionFromRoleAsync` | 同上，换 role_permission | `ux_authz_rp_*` |
| `GetAllPermissionsAsync` | 全表，内存按 `SortOrder`、`PermissionName`（序数）排序 | — |
| `GetPermissionByNameAsync` | `FirstAsync(PermissionName == ?)` | `ux_authz_perm_name` |

**INNER JOIN 保持了「定义缺失的授予静默跳过」**：没有定义的授予行联不上，自然不返回；定义补回来后授予立即重新生效——与默认实现完全一致。

三个定义维护方法（与 `DefaultPermissionStore` 同名，签名多一个可选的 `CancellationToken`）：

| 方法 | 行为 |
| --- | --- |
| `AddOrUpdatePermissionAsync(PermissionDefinition, CancellationToken)` → `bool` | 空定义或空名称返回 `false`；按名称查，无则插入、有则 `SetColumns` 改写除名称外的全部字段；返回 `true` |
| `AddPermissionsAsync(List<PermissionDefinition>, CancellationToken)` | 跳过空项与空名称，逐条调上一个方法 |
| `RemovePermissionAsync(string, CancellationToken)` → `bool` | 按名称删除，返回是否删到了行；**不级联**删授予 |

### 4.7 缓存：不加

`XiHan.Framework.Authorization` 的 csproj 只引用 `Authentication` 与 `Core`，没有 `Caching`；全仓库 `Authorization` 目录下没有任何缓存代码。上层没有缓存。

存储层也不加：存储是作用域生命周期、被 `DefaultAuthorizationService` 的写路径直接调用，缓存加在这里就必须同时处理「谁改了什么就作废谁」，而撤销一个角色权限影响的是该角色下所有用户——作废范围在存储层算不出来。热路径的次数问题在 ② 用替换检查器解决（每次判定 1–2 次查询），不靠缓存。若应用仍需缓存，应在 `IPermissionChecker` 外套装饰器，由应用在授权写路径上作废。

### 4.8 建表标注

每个实体标注 `[TableInitialization(Group = "Authorization")]`：

- `TableInitialization.Mode = OptIn` 时，不标注的实体一律不建表（`DbEntityTypeProvider.cs:77-80`），且后续的 `IncludedTables` 也救不回来
- `Mode = All`（默认）时标注不改变行为；应用想跳过本包的表，可以 `ExcludedGroups: ["Authorization"]`

不设 `IncludeModuleConnections`：本包的表只在主库建。

### 4.9 注册与模块

```csharp
public static IServiceCollection AddXiHanAuthorizationSqlSugar(this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);

    services.Replace(ServiceDescriptor.Scoped<IPermissionStore, SqlSugarPermissionStore>());
    services.TryAddScoped<SqlSugarPermissionStore>();

    return services;
}
```

具体类型另注册一份，供应用在种子数据里解析 `SqlSugarPermissionStore` 调用三个定义维护方法。

模块类 `XiHanAuthorizationSqlSugarModule`：`[DependsOn(typeof(XiHanAuthorizationModule), typeof(XiHanDataModule))]`，`ConfigureServices` 只调 `context.Services.AddXiHanAuthorizationSqlSugar()`。本包没有配置节，不需要 `IConfiguration`。

## 5. 会静默失效的陷阱

**① 授予实体只实现了 `IMultiTenantEntity`。**

读共享口径下，平台态授予的行在每个租户里都可见。SQLite 测试客户端没有注册任何过滤器，**全部测试照样绿**。用反射测试钉死：两个授予实体必须可赋值给 `IStrictMultiTenantEntity`，权限定义实体必须**不**可赋值给 `IMultiTenantEntity`。

**①′ 只靠过滤器做隔离，查询里不带当前租户条件。**

过滤器开着时一切正常；`EnableTenantFilter = false` 时跨租户读到同名用户的授予。SQLite 测试客户端本就没有过滤器，所以测试**能**抓到它：夹具带一个可写的 `StubCurrentTenant`，用例在租户 1 授予、切到租户 2 与平台态读，断言读不到。

**② 用 `sys_role` / `sys_permission` 这类通用表名。**

下游 `XiHan.BasicApp` 已有 `Sys_Role`、`Sys_Permission`、`Sys_Role_Data_Scope` 等表。MySQL、SQL Server 的默认排序规则对表名大小写不敏感，CodeFirst 看到「表已存在」就走改表分支，往那张业务表里加本包的列。**不报错**，损坏在下游下一次读那张表时才暴露。用反射测试钉死所有实体表名以 `sys_authz_` 开头。

**③ `TryAdd` 顶替。**

主包用 `TryAddScoped` 注册了 `IPermissionStore`，子包再 `TryAdd` 是空操作：注册「成功」、无报错、应用照样跑在每请求一个空字典的内存实现上。必须 `Replace`，用注册测试断言只剩一个描述符。

**④ 把 `[SugarIndex]` 的字段写成列名。**

`CodeFirstProvider.cs:384` 按**属性名**找列，写成 `"Permission_Name"` 会在建表时抛「索引特性没找到列」——这条会报错，不算静默；但若写成一个恰好也是别的属性名的字符串，索引就建在了错的列上。统一用 `nameof(类名.属性)`。

**⑤ 截断超长名称。**

照抄 `AuditingLogMapper` 的截断会把 `Order.Export.Detail.Excel` 截成 `Order.Export.Detail.Exc`，授予了一个谁也不检查的权限，或者更糟——截断后恰好等于另一个真实权限名。超长就让数据库报错。

**⑥ 读取时加了 `IsEnabled` 过滤。**

默认实现的存储读取**不**过滤启用状态，过滤由检查器做。存储层若自作主张过滤，后台管理界面就看不到被禁用的权限、无法重新启用。测试断言「已禁用的定义照样返回」。

## 6. 测试策略

**只有 SQLite 一层，CI 强门禁执行。** 本份没有并发协议，不需要真实数据库层；租户过滤器属于 `Data` 的行为，本份用反射测试钉住实体接口，② 会补一个在 SQLite 客户端上手工挂过滤器的隔离用例。

测试夹具 `AuthorizationTestContext`：一个临时 `.db` 文件，`CodeFirst.InitTables` 建出**本程序集里所有标注了 `[SugarTable]` 的实体**（用反射枚举，② ③ 新增实体时夹具不用改），桩解析器 `StubClientResolver` 的 `GetCurrentClient()` 返回该客户端，`GetClientForEntity` 与 `GetClient` 抛 `NotSupportedException`——顺带钉住 §4.5 的「只用当前客户端」。

必测：

- **实体约定**：表名前缀、建表分组、租户接口（§5 ①②）
- **唯一索引生效**：同名权限定义插第二次抛异常；同一租户重复授予抛异常，不同租户可重复（直接 `Insertable` 带不同 `TenantId`，SQLite 下无 AOP）
- **映射**：逐字段对应；`Properties` 以 JSON 往返，值读回为 `JsonElement`
- **1.2 表的每一条语义**：直接授予、按角色标识、定义缺失跳过、定义补回后生效、不过滤启用、幂等、空参数
- **当前租户**：夹具的 `StubCurrentTenant`（`Id` 可写，默认 `null` 即平台态）；租户 1 授予的行，租户 2 与平台态都读不到、撤销不掉；落库行的 `TenantId` 为 1
- **定义维护**：新增、同名更新而不新增、空名称不写、批量写入排序、删除返回值
- **注册**：`Replace` 后只剩一个描述符；具体类型以作用域注册；模块依赖授权模块与数据模块

写用例时的两条约束：

- **不要断言读回的 `DateTime` 与写入值相等**。SQLite 把时间存成文本，`DateTime.Kind` 不保留。本份的实体没有时间列，② 的角色有，届时由映射器统一 `SpecifyKind(Utc)`
- 测试项目 csproj 只 Import `netcore.props`、`common.props`、`test.props` 三个，**不 Import `version.props`、不设 `AssemblyName`**；SQLite 连接串必须带 `Pooling=False`，否则清理临时文件抛 `IOException`

测试平台是 Microsoft.Testing.Platform：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。

## 7. 已知边界

写入 PR 描述与包 README（③ 收尾时汇总），**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 名称大小写 | 名称比较交给数据库排序规则。MySQL / SQL Server 默认大小写不敏感，`User.Create` 与 `user.create` 被视为同一个权限且唯一索引冲突；PostgreSQL 与 SQLite 默认敏感。默认实现是序数比较（大小写敏感） |
| 并发重复授予 | 查重与插入之间没有锁，两个请求同时授予同一权限时，后到的一个撞唯一索引抛异常。结果状态正确（恰好一行），但调用方会收到一次异常 |
| 删除定义不级联 | 与默认实现一致。授予行保留，定义补回后立即重新生效。需要彻底清除时应用自行删除授予行 |
| 权限定义全局可写 | 权限定义表不分租户，租户态调用 `AddOrUpdatePermissionAsync` / `RemovePermissionAsync` 会改到所有租户共用的定义。应用层应只在平台态暴露这些操作 |
| `Properties` 的值类型 | 以 System.Text.Json 存取，读回的字典值是 `JsonElement`，不是写入时的原始类型 |
| 超长名称 | 权限名超过 256、用户或角色标识超过 128 时，严格模式的数据库直接报错，MySQL 非严格模式会静默截断——后者是数据库配置问题，本包不兜底 |
| 租户隔离的边界 | 授予表按 `ICurrentTenant.Id ?? 0` 显式过滤，与过滤器开关无关；平台态读写租户 0 的授予，看不到业务租户的授予。权限定义表全局共享，不受此约束 |
| 取消令牌的残留 | SqlSugar 的 `*Async(CancellationToken)` 把令牌写进 `Context.Ado.CancellationToken` 且执行后不清除。同一作用域内后续不带令牌的调用会继承它；授权调用与业务调用通常共用请求的令牌，行为不变 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动：`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败，与本包无关）
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭（属性与访问器可以）
- `public` 成员都有 `<summary>`，不用 `<inheritdoc/>`
- 提交信息为中文 Conventional Commits，作用域 `authorization-sqlsugar`，**不加任何 AI 署名**

## 五个共同问题

| 问题 | 本包的回答 | 依据 |
| --- | --- | --- |
| 分表与否 | **不分表** | 权限、角色、授予都是长期有效的配置型数据，没有保留期，量级随用户数线性增长而非随时间增长 |
| 主键类型 | **雪花 `long`**，经 `IDistributedIdGenerator<long>` 取 | 契约的标识是**调用方给的字符串**（角色 `Id`、权限 `Name`、用户 `userId`），既非 `Guid` 也不保证全局唯一——它们在租户内唯一，所以做成「`TenantId` + 业务键」的唯一索引，主键另取雪花 |
| 是否参与工作单元事务 | **参与**，经 `GetCurrentClient()` 自动登记，存储注册为 `Scoped` | 「创建用户 + 分配角色」这类写入应与业务同事务；读取登记进事务无副作用 |
| 是否需要多库 | **单库**，统一当前租户的主库 | 热路径要联表，表必须同库；授权数据不随业务模块分库 |
| 顶替方式 | **`Replace`** | 主包 `XiHanAuthorizationServiceCollectionExtensions.cs:30-36` 全部用 `TryAddScoped` |

## 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改会影响什么 |
| --- | --- | --- | --- | --- |
| 1 | 表名前缀 | `sys_authz_` | 避开下游 `Sys_Role` / `Sys_Permission`；缩写让索引名控制在 30 字符内 | 改前缀要同步改 6 个实体、8 个索引名与约定测试 |
| 2 | 实体类名前缀 | `SysAuthz`（如 `SysAuthzRole`） | 与表名一一对应 | 纯命名，改了只影响可读性 |
| 3 | 主键 | 雪花 `long` + 业务键唯一索引 | 契约标识是租户内唯一的字符串 | 改成以业务键作主键则无法在不同租户复用同一角色标识 |
| 4 | 权限定义不分租户 | 全局 | 权限目录由代码定义，全应用一份 | 若要租户自定义权限，定义表要改成读共享（`IMultiTenantEntity`），唯一索引加 `TenantId` |
| 5 | 授予行严格隔离 | `IStrictMultiTenantEntity` + 每条 SQL 显式 `TenantId == ICurrentTenant.Id ?? 0` | 平台态授予不应泄漏到租户；过滤器可被关掉，显式条件不依赖它；与 `Authentication.SqlSugar` 一致 | 去掉显式条件则关闭过滤器时可跨租户读到同名用户的授予 |
| 6 | 客户端 | 一律 `GetCurrentClient()` | 联表要求同库 | 改成按实体路由后，给任一实体加 `[ModuleDataSource]` 就会让联表跨库 |
| 7 | 定义维护方法 | 提供 `AddOrUpdatePermissionAsync` 等 3 个，并注册具体类型 | 契约没有新增定义的入口，与默认实现同名便于迁移 | 去掉则应用只能手写 `Insertable` 灌定义 |
| 8 | 删除定义不级联 | 不级联 | 与默认实现一致 | 改成级联则「临时删除再补回」会丢掉全部授予 |
| 9 | 缓存 | 不加 | 上层无缓存；存储层算不出作废范围；② 已把判定降到 1–2 次查询 | 要加应在检查器外套装饰器，由应用作废 |
| 10 | 建表分组 | `[TableInitialization(Group = "Authorization")]` | 让 `OptIn` 模式也能建表，`All` 模式可按组排除 | 不标注则 `OptIn` 模式的应用无法经框架建出这些表 |
| 11 | 列宽 | 用户 / 角色标识 128，权限 / 策略名 256，显示名 256，描述 1024 | MySQL utf8mb4 下三列复合唯一索引约 1.5 KB，在 InnoDB 3 KB 上限内；SQL Server 非聚集索引键约 0.8 KB，在 1.7 KB 上限内 | 加宽会逼近索引键长上限 |
| 12 | 硬删除 | 撤销即删除 | 授予行没有审计价值，审计由 `Auditing` 负责 | 改软删除要给每条查询加 `IsDeleted` 条件，且唯一索引要带上它 |

## 9. 下一份

②（`.superpowers/specs/2026-09-28-authorization-sqlsugar-2-role-store-checker-design.md`）：`SysAuthzRole`、`SysAuthzUserRole` 两个实体，`IRoleStore` 的 11 个方法（含删除角色的级联与事务），以及热路径的解法——以 `SqlSugarPermissionChecker` 顶替 `IPermissionChecker`，把一次判定从 `2 + 角色数` 次查询降到至多 2 次。
