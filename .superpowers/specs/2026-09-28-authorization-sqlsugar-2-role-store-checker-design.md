# Authorization.SqlSugar ②：角色存储与权限检查器 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-authorization-sqlsugar-2-role-store-checker.md`
- **前置**：①（`.superpowers/specs/2026-09-28-authorization-sqlsugar-1-permission-store-design.md`）必须已完成
- **所属 PR**：`Authorization.SqlSugar` 单独一个 PR，由 ①②③ 共同组成
- **Linear 议题**：<https://linear.app/elf-express/issue/EDDIE-10>
- **系列**：`Authorization.SqlSugar` 共 3 份。① 包骨架 + 权限存储；**② 角色存储 + 权限检查器（热路径）**；③ 策略存储 + 文档收尾

> 本文档**自成一体**。实现 ② 所需的全部约束都写在这里，不引用其他设计文档。三份共用的约定（表名、主键、租户、客户端）在每份里各重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份的难点在查询形状，不在写入。** 只把 `IRoleStore` 落库、不动检查器，实现照样能跑、测试照样全绿，但每次鉴权打 `2 + 角色数` 次库，一次判定多个权限时再乘以权限数。第 4.1 节先把调用方数清楚，再看设计。
>
> **本份最容易静默出错的一处**：`SqlSugarAdo.UseTranAsync` **吞异常**，并且在已有外层事务时会**提交外层事务**。删除角色的级联写法若无条件套它，失败时调用方以为删成功了，成功时业务工作单元被提前提交。见第 5 节 ③。

---

## 1. 背景与目标

### 1.1 现状

`IRoleStore`（`framework/src/XiHan.Framework.Authorization/Roles/IRoleStore.cs`）11 个方法：

```
GetUserRolesAsync(userId)              用户的角色定义列表
IsInRoleAsync(userId, roleName)
AddUserToRoleAsync(userId, roleName)
RemoveUserFromRoleAsync(userId, roleName)
GetAllRolesAsync()
GetRoleByNameAsync(roleName)
GetRoleByIdAsync(roleId)
CreateRoleAsync(role)
UpdateRoleAsync(role)
DeleteRoleAsync(roleId)
GetUsersInRoleAsync(roleName)
```

默认实现 `DefaultRoleStore` 是内存字典，在 `XiHanAuthorizationServiceCollectionExtensions.cs:30` 以 **`TryAddScoped`** 注册——作用域生命周期 + 实例字典，每个请求都是空的。

`IPermissionChecker` 的默认实现 `DefaultPermissionChecker`（`Permissions/DefaultPermissionChecker.cs`）同样以 `TryAddScoped` 注册（`:34`），它**只**通过 `IPermissionStore` 与 `IRoleStore` 取数。

① 已交付 `SqlSugarPermissionStore` 与三张表：`sys_authz_permission`（全局）、`sys_authz_user_permission`、`sys_authz_role_permission`（严格按租户隔离），均在 `sys_authz_` 前缀下，主键雪花 `long`。

### 1.2 默认实现的语义（落库实现必须保持，除 1.3 列出的一处）

| 行为 | 默认实现 | 位置 |
| --- | --- | --- |
| `GetUserRolesAsync` 返回**全部**角色，含已禁用的 | 无 `IsEnabled` 过滤 | `DefaultRoleStore.cs:43-63` |
| `IsInRoleAsync` **不看**角色是否启用 | `roleNames.Contains(roleName)` | `:72-85` |
| `AddUserToRoleAsync` 角色不存在时抛 `InvalidOperationException("角色 'x' 不存在")` | | `:93-113` |
| `AddUserToRoleAsync` 重复加入幂等 | `HashSet` | 同上 |
| `CreateRoleAsync`：`null` 抛 `ArgumentNullException`；`Id` 或 `Name` 为空抛 `ArgumentException`；`Id` 或 `Name` 已存在抛 `InvalidOperationException` | | `:193-227` |
| `UpdateRoleAsync`：`null` 抛 `ArgumentNullException`；`Id` 为空抛 `ArgumentException`；不存在抛 `InvalidOperationException`；改名撞到别的角色抛 `InvalidOperationException`；把 `LastModifiedTime` 写成 `DateTime.UtcNow`（**也写回传入的对象**） | | `:234-270` |
| `DeleteRoleAsync` **不检查** `IsStatic`；删除后从所有用户里移除该角色 | | `:277-299` |
| 空参数：读返回空集合 / `null` / `false`，写直接返回 | `string.IsNullOrEmpty` 守卫 | 各方法开头 |

`DefaultPermissionChecker` 的判定语义：

| 行为 | 位置 |
| --- | --- |
| 用户或权限名为空 → `false` | `:34-39` |
| 先看直接授予（且权限 `IsEnabled`），命中即返回 `true` | `:41-46` |
| 再看**启用的**角色所授予的、**启用的**权限 | `:48-57` |
| `IsAnyGrantedAsync` / `IsAllGrantedAsync`：列表为空 → `false`（`IsAll` 对空列表也是 `false`） | `:69-112` |
| `GetGrantedPermissionsAsync`：直接授予 ∪ 启用角色授予，只含启用的权限，去重 | `:120-143` |
| `PermissionExistsAsync`：有定义即 `true`，**不看**启用状态 | `:151-155` |

### 1.3 与默认实现唯一的不同：用户角色关联存角色标识

默认实现的用户角色关联存的是**角色名称**（`_userRoles: 用户 → 角色名集合`，`:30`），而角色权限关联存的是**角色标识**（`DefaultPermissionStore._rolePermissions: 角色ID → 权限名集合`）。`UpdateRoleAsync` 改名时只更新了名称映射，没更新 `_userRoles`，于是**改名后用户静默失去该角色**。

落库实现的用户角色关联表存**角色标识**，`AddUserToRoleAsync` / `RemoveUserFromRoleAsync` 按名称查到角色后写标识。改名后成员关系保持不变。这是对默认实现的一处**修正**，契约签名不变。

### 1.4 要交付什么

1. 两个实体：`SysAuthzRole`（`sys_authz_role`）、`SysAuthzUserRole`（`sys_authz_user_role`）
2. `RoleMapper`：角色定义 ↔ 实体
3. `SqlSugarRoleStore`：`IRoleStore` 的 11 个方法，删除角色在同一事务内级联
4. `SqlSugarPermissionChecker`：`IPermissionChecker` 的 5 个方法，每次判定至多 2 次查询
5. 在 ① 的注册扩展里追加两条 `Replace`

### 1.5 成功标准

1. `SqlSugarRoleStore` 通过 1.2 表的每一条行为测试
2. 角色改名后，用户仍在该角色中、仍拥有该角色授予的权限
3. 删除角色后，其用户关联与角色权限一并删除；以同一标识重建角色**不继承**旧授权
4. 级联删除中途失败时整体回滚并把异常抛给调用方；外层已有事务时不提前提交外层事务
5. `SqlSugarPermissionChecker` 在 7 类用例（直接 / 经角色 / 禁用角色 / 禁用权限 / 未授予 / 空参数 / 多权限）上与 `DefaultPermissionChecker`（喂同样的 SqlSugar 存储）**判定完全一致**
6. `IsGrantedAsync` 直接授予命中时执行 **1** 条 SQL，否则 **2** 条；`IsAllGrantedAsync` 与 `GetGrantedPermissionsAsync` 至多 **2** 条——与角色数、权限数无关
7. 关联行与角色行 `TenantId` 不一致时不配对
8. 驱动表（`user_role`、`user_permission`）与角色表的每条查询都显式带 `Tenant_Id = 当前租户`：未注册任何租户过滤器时，租户 A 的用户读不到租户 B 同名用户的角色与授予；平台态只见租户 0 的数据
9. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 要实现的契约、默认实现与调用方 —— 最高优先**

```
framework/src/XiHan.Framework.Authorization/
  Roles/IRoleStore.cs                          要实现的 11 个方法
  Roles/DefaultRoleStore.cs                    语义基准，见 1.2
  Roles/RoleDefinition.cs                      字段来源；CreatedTime 是 DateTime（UTC）
  Permissions/IPermissionChecker.cs            要实现的 5 个方法
  Permissions/DefaultPermissionChecker.cs      判定语义基准，见 1.2
  Policies/DefaultPolicyEvaluator.cs:63-157   调用方：IsInRoleAsync / IsGrantedAsync / GetUserRolesAsync / GetGrantedPermissionsAsync
  DefaultAuthorizationService.cs               调用方：AuthorizeRoleAsync / GrantPermissionAsync / AddUserToRoleAsync
  AspNetCore/HybridPermissionAuthorizationHandler.cs:49-57   调用方：每个带权限码的请求一次 IsGrantedAsync
  Extensions/DependencyInjection/XiHanAuthorizationServiceCollectionExtensions.cs:30-34   TryAddScoped
```

**② ① 的产出**

```
framework/src/XiHan.Framework.Authorization.SqlSugar/
  Entities/SysAuthzPermission.cs / SysAuthzUserPermission.cs / SysAuthzRolePermission.cs
  Mapping/JsonColumn.cs                        internal，SerializeOrNull / DeserializeOrNull
  Permissions/SqlSugarPermissionStore.cs       客户端取法、查重再插入的写法
  Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs   要追加两行
framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/
  AuthorizationTestContext.cs                  夹具（按反射建全部表）与 StubClientResolver
```

**③ 数据层**

```
framework/src/XiHan.Framework.Data/
  Extensions/DependencyInjection/XiHanDataServiceCollectionExtensions.cs:229-256   两个租户过滤器
  SqlSugar/Clients/SqlSugarClientResolver.cs:256-290    EnlistCurrentUnitOfWork：登记即 Ado.BeginTran
  SqlSugar/Clients/SqlSugarTransactionApi.cs            工作单元提交走 Ado.CommitTranAsync
  SqlSugar/Extensions/EntityAuditExtensions.cs          ToCreated 按属性名 CreatedTime 填值（仅在默认值时）
framework/src/XiHan.Framework.Domain/Entities/Abstracts/IStrictMultiTenantEntity.cs
```

**④ SqlSugar 源码 —— API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs:338-700                        二、三、四表的 InnerJoin / Where / Select
  Abstract/QueryableProvider/QueryableExecuteSqlAsync.cs:132-140   AnyAsync = Clone().Take(1).Select("1")，联表保留
  Abstract/AdoProvider/AdoProvider.cs:261-264            IsNoTran() => Transaction == null
  Abstract/AdoProvider/AdoProvider.cs:271-276            BeginTranAsync 在已有事务时什么都不做
  Abstract/AdoProvider/AdoProvider.cs:368-391            UseTranAsync：catch 后吞掉异常、返回 DbResult；成功时 CommitTranAsync
  Entities/DbResult.cs                                   IsSuccess / ErrorException
  Abstract/AopProvider/AopProvider.cs:20                 OnLogExecuting，测试里用来数 SQL
  Abstract/FilterProvider/FilterProvider.cs:111          AddTableFilter<T>(expr, FilterJoinPosition.On)
```

> 树与目录名：本包引用 SqlSugarCore 5.1.4.221，权威源码是 `Src/Asp.NetCore2/`，不是 `Src/Asp.Net/`。更新提供者的目录名是 `UpdateProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：

| 任务 | 必读 |
| --- | --- |
| 联表 | `聯表查詢.md`、`Select用法.md`（4.5 节） |
| 事务 | `事务用法.md`（4「语法糖」、9「嵌套事务」） |
| 过滤器 | `查詢過濾器.md`（第五节联表过滤器位置） |

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

- **不改 `XiHan.Framework.Authorization` 主包的任何文件**，包括 `DefaultPermissionChecker` 与 `DefaultPolicyEvaluator`
- **不调 `GetClientForEntity<T>()` / `GetClient()`**，统一 `GetCurrentClient()`
- **不依赖全局租户过滤器做隔离**：角色表与两张驱动表的每条读写显式带 `TenantId == ICurrentTenant.Id ?? 0`，插入时显式赋同一个值（见 4.4）
- **不无条件调用 `Ado.UseTranAsync`**，只在 `Ado.IsNoTran()` 为真时用，且必须检查 `IsSuccess` 并重新抛出
- **联表条件里必须带 `TenantId` 相等**，不能只靠租户过滤器
- **检查器不做任何缓存或请求内记忆化**
- **不写 `<inheritdoc/>`**

## 3. 非目标

- 不实现 `IPolicyStore`（③）
- 不替换 `IPolicyEvaluator` 或 `IAuthorizationService`。`DefaultPolicyEvaluator` 对 `RequiredPermissions` 仍是逐个调 `IsGrantedAsync`，每个权限 1–2 次查询；这一点在已知边界里写明
- 不拦截静态角色的删除（与默认实现一致，见「待确认的决策」）
- 不做通配权限 `*`、会话有效性校验——下游 `SaasPermissionChecker` 那类需求由应用自己替换检查器
- 不做用户存在性校验：用户属于 `Authentication` 的存储，本包只存用户标识字符串

## 4. 设计

### 4.1 热路径：谁调了什么、调几次

逐个读调用方数出来（N = 用户的角色数，k = 一次判定涉及的权限数，r = 策略要求的角色数，p = 策略要求的权限数）。「现状」一列是**只落库存储、保留 `DefaultPermissionChecker`** 时每次调用打的 SQL 条数：

| 调用方 | 调用 | 现状 | 本份之后 |
| --- | --- | --- | --- |
| `HybridPermissionAuthorizationHandler.HandleRequirementAsync`（每个 `[PermissionAuthorize]` 请求） | `IsGrantedAsync` × 1 | 2 + N | 1 或 2 |
| `DefaultAuthorizationService.AuthorizeAsync` | `IsGrantedAsync` | 2 + N | 1 或 2 |
| `DefaultAuthorizationService.AuthorizeAnyAsync` / `AuthorizeAllAsync` | `IsAnyGrantedAsync` / `IsAllGrantedAsync` | k × (2 + N) | 1 或 2 |
| `DefaultAuthorizationService.GetUserPermissionsAsync` | `GetGrantedPermissionsAsync` | 2 + N | 2 |
| `DefaultAuthorizationService.AuthorizeRoleAsync` | `IRoleStore.IsInRoleAsync` | 1 | 1 |
| `DefaultPolicyEvaluator.EvaluateAsync` | `GetPolicyByNameAsync` + ≤ r × `IsInRoleAsync` + p × `IsGrantedAsync` + （有自定义要求时）`GetUserRolesAsync` + `GetGrantedPermissionsAsync` | 1 + r + p(2+N) + 1 + (2+N) | 1 + r + p×2 + 1 + 2 |

「每次鉴权打三次库」的说法低估了：单权限是 `2 + N`，多权限是 `k(2 + N)`。

**只改存储解决不了**：`DefaultPermissionChecker.IsGrantedAsync` 在循环里对每个启用角色调一次 `GetRolePermissionsAsync(role.Id)`（`:50-57`），契约就是按单个角色取权限。存储层能做的最多是请求内记忆化，但这要求两个独立的作用域实例（权限存储、角色存储）互相通知作废，得不偿失。

**因此本份顶替 `IPermissionChecker`**：`SqlSugarPermissionChecker` 直接查 ① ② 的五张表，一次判定两条 SQL。

### 4.2 实体

**`SysAuthzRole`**（`sys_authz_role`）：`SugarMultiTenantEntity<long>` + `IStrictMultiTenantEntity`

| 属性 | 列 | 类型 | 长度 | 可空 |
| --- | --- | --- | --- | --- |
| `RoleId` | `Role_Id` | `string` | 128 | 否 |
| `RoleName` | `Role_Name` | `string` | 128 | 否 |
| `DisplayName` | `Display_Name` | `string` | 256 | 否 |
| `Description` | `Description` | `string?` | 1024 | 是 |
| `IsEnabled` | `Is_Enabled` | `bool` | — | 否 |
| `IsDefault` | `Is_Default` | `bool` | — | 否 |
| `IsStatic` | `Is_Static` | `bool` | — | 否 |
| `SortOrder` | `Sort_Order` | `int` | — | 否 |
| `CreatedTime` | `Created_Time` | `DateTime` | — | 否 |
| `LastModifiedTime` | `Last_Modified_Time` | `DateTime?` | — | 是 |
| `Properties` | `Properties` | `string?`（`CodeFirst_BigString`） | — | 是 |

`CreatedTime` 与数据层 AOP 按属性名填写的审计字段同名（`EntityAuditExtensions.ToCreated`），AOP **只在当前值为默认值时**写入；`RoleDefinition` 的两个构造函数都把它设成 `DateTime.UtcNow`，AOP 因此不改它。

**`SysAuthzUserRole`**（`sys_authz_user_role`）：同上基类，`UserId`（`User_Id`，128）、`RoleId`（`Role_Id`，128）。

两个实体与 ① 的实体一样标注 `[TableInitialization(Group = "Authorization")]`。

### 4.3 索引

| 索引名 | 表 | 字段 | 唯一 | 服务于 |
| --- | --- | --- | --- | --- |
| `ux_authz_role_tenant_id` | role | `TenantId, RoleId` | 是 | 按标识读角色；热路径联表 user_role → role、role → role_permission |
| `ux_authz_role_tenant_name` | role | `TenantId, RoleName` | 是 | 按名称读角色；`IsInRoleAsync`；`GetUsersInRoleAsync` |
| `ux_authz_ur_tenant_user_role` | user_role | `TenantId, UserId, RoleId` | 是 | **热路径驱动表**：按用户找角色；幂等加入 |
| `ix_authz_ur_tenant_role` | user_role | `TenantId, RoleId` | 否 | 按角色找用户；删角色时级联 |

① 已有 `ux_authz_rp_tenant_role_perm`（`TenantId, RoleId, PermissionName`）与 `ux_authz_perm_name`（`PermissionName`），热路径的后两跳用它们。

### 4.4 `SqlSugarRoleStore`

构造函数：`(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator)`。客户端一律 `GetCurrentClient()`。

**当前租户**：私有属性 `CurrentTenantId => _currentTenant.Id ?? 0`。`ICurrentTenant`（`XiHan.Framework.MultiTenancy.Abstractions`）的实现 `CurrentTenant` 读 `ICurrentTenantAccessor.Current?.TenantId`，无租户上下文时为 `null`，**平台态（宿主）因此取 0**——与数据层 AOP 在平台态插入时保留的 0、严格过滤器平台态的口径（`XiHanDataServiceCollectionExtensions.cs:481-484`）一致，平台管理员读写的是租户 0 的角色，宿主路径不受影响。角色表与 `user_role` 的每条查询、更新、删除都带 `Tenant_Id = @当前租户`，插入时显式赋值（AOP 见到与上下文一致的预置值放行）。与同一轮的 `Authentication.SqlSugar` 做法一致。

| 方法 | SQL 形状 |
| --- | --- |
| `GetUserRolesAsync(userId)` | `user_role INNER JOIN role ON Tenant_Id = Tenant_Id AND Role_Id = Role_Id WHERE user_role.Tenant_Id = @当前租户 AND User_Id = ?`，选 role 整行；内存按 `SortOrder`、`RoleName` 排序 |
| `IsInRoleAsync(userId, roleName)` | 同样联表，`WHERE user_role.Tenant_Id = @当前租户 AND User_Id = ? AND Role_Name = ?`，`AnyAsync` |
| `AddUserToRoleAsync(userId, roleName)` | ① `FirstAsync(Tenant_Id = @当前租户 AND Role_Name = ?)`，无则抛；② `AnyAsync(Tenant_Id, User_Id, Role_Id)` 查重；③ `Insertable`，`TenantId` 显式赋值 |
| `RemoveUserFromRoleAsync(userId, roleName)` | ① 按当前租户与名称取角色，无则返回；② `Deleteable WHERE Tenant_Id AND User_Id AND Role_Id` |
| `GetAllRolesAsync()` | `WHERE Tenant_Id = @当前租户`；内存按 `SortOrder`、`RoleName`（序数）排序 |
| `GetRoleByNameAsync` / `GetRoleByIdAsync` | `FirstAsync(Tenant_Id = @当前租户 AND …)` |
| `CreateRoleAsync(role)` | 参数校验 → `AnyAsync(Tenant_Id, Role_Id)` → `AnyAsync(Tenant_Id, Role_Name)` → `Insertable`，`TenantId` 显式赋值 |
| `UpdateRoleAsync(role)` | 参数校验 → 按 `Tenant_Id, Role_Id` 取 → 改名时 `AnyAsync(Tenant_Id AND Role_Name = 新名 AND Basic_Id <> 本行)` → `role.LastModifiedTime = UtcNow` → `SetColumns` 改写除 `RoleId`、`CreatedTime` 外的全部字段 |
| `DeleteRoleAsync(roleId)` | 见 4.5 |
| `GetUsersInRoleAsync(roleName)` | `role INNER JOIN user_role ON Tenant_Id AND Role_Id WHERE role.Tenant_Id = @当前租户 AND Role_Name = ?`，选 `User_Id`，内存去重 |

**不改 `CreatedTime`**：`UpdateRoleAsync` 传入的对象可能是新 `new RoleDefinition(...)` 出来的，它的 `CreatedTime` 是「刚才」，写回去会抹掉真实创建时间。

**`DateTime` 读回**：SQLite 把时间存成文本，`Kind` 丢失。`RoleMapper.ToDefinition` 对 `CreatedTime` 与 `LastModifiedTime` 统一 `DateTime.SpecifyKind(value, DateTimeKind.Utc)`——写入的本来就是 UTC 刻度。

### 4.5 删除角色：级联与事务

```
role = FirstAsync(Tenant_Id == 当前租户 && Role_Id == roleId)
若 role 为空：返回

在事务内依次：
  DELETE user_role       WHERE Tenant_Id = role.TenantId AND Role_Id = role.RoleId
  DELETE role_permission WHERE Tenant_Id = role.TenantId AND Role_Id = role.RoleId
  DELETE role            WHERE Basic_Id = role.BasicId
```

**为什么要级联 role_permission**：默认实现里角色权限在另一个存储，删角色不动它；两者各自是内存、每请求清空，这个缺口从未暴露。落库后若不级联，以同一标识重建的角色会**原样继承**被删角色的全部权限——调用方给的标识往往是可预测的（`"admin"`、`"editor"`）。

**为什么用删除前读到的 `TenantId` 写条件**：角色行已按当前租户显式读出，级联删除沿用行上的 `TenantId`，与过滤器开不开无关。

**先关联、后主表**：即使事务不成立（见下），中途失败也只会留下「角色还在、部分关联没了」，重试即可；不会出现「角色没了、授权还在」。

**事务写法**：

```csharp
private static async Task ExecuteInTransactionAsync(ISqlSugarClient client, Func<Task> action)
{
    if (!client.Ado.IsNoTran())
    {
        await action();
        return;
    }

    var result = await client.Ado.UseTranAsync(action);
    if (!result.IsSuccess)
    {
        ExceptionDispatchInfo.Capture(result.ErrorException).Throw();
    }
}
```

- **已在事务里**（事务型工作单元已经 `GetCurrentClient()` 登记过，`SqlSugarClientResolver` 登记时即 `Ado.BeginTran`）：直接执行，由工作单元统一提交或回滚
- **不在事务里**：自己开。`UseTranAsync` 失败时**不抛**，返回 `IsSuccess = false`，必须手动重新抛出

### 4.6 `SqlSugarPermissionChecker`

构造函数：`(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant)`。当前租户取法同 4.4（`ICurrentTenant.Id ?? 0`，平台态为 0）。

核心是一个私有方法：

```
GetGrantedNamesAsync(userId, candidates?)   // candidates 为空表示不限
  ① 直接授予：
     user_permission up
       INNER JOIN permission p ON p.Permission_Name = up.Permission_Name
     WHERE up.Tenant_Id = @当前租户 AND up.User_Id = ? AND p.Is_Enabled = 1 [AND p.Permission_Name IN (candidates)]
     SELECT p.Permission_Name
  若 candidates 非空且已全部命中：返回
  ② 经角色：
     user_role ur
       INNER JOIN role r             ON r.Tenant_Id = ur.Tenant_Id AND r.Role_Id = ur.Role_Id
       INNER JOIN role_permission rp ON rp.Tenant_Id = r.Tenant_Id AND rp.Role_Id = r.Role_Id
       INNER JOIN permission p       ON p.Permission_Name = rp.Permission_Name
     WHERE ur.Tenant_Id = @当前租户 AND ur.User_Id = ? AND r.Is_Enabled = 1 AND p.Is_Enabled = 1 [AND p.Permission_Name IN (candidates)]
     SELECT p.Permission_Name
  返回 ① ∪ ②（序数比较的 HashSet）
```

| 方法 | 实现 |
| --- | --- |
| `IsGrantedAsync(userId, name)` | 空参数 → `false`；`GetGrantedNamesAsync(userId, [name])` 含 `name` |
| `IsAnyGrantedAsync(userId, names)` | 列表为空或用户为空 → `false`；非空名称去重作候选；任一命中 |
| `IsAllGrantedAsync(userId, names)` | 同上；**每一项**都非空且命中（列表里有空串 → `false`，与默认实现逐项调 `IsGrantedAsync("")` 的结果一致） |
| `GetGrantedPermissionsAsync(userId)` | 用户为空 → 空列表；`GetGrantedNamesAsync(userId, null)` |
| `PermissionExistsAsync(name)` | 空 → `false`；`permission` 表 `AnyAsync(Permission_Name = ?)`，不看启用 |

**为什么两条 SQL 而不是一条 `UNION`**：直接授予命中时（管理员常见）第二条可以省掉；两条都是等值索引查找，第二条的四表联表每一跳都有首列等值的索引（4.3 与 ① 的索引）。`UNION ALL` 能压成一次往返，但 SqlSugar 的 `UnionAll` 对带联表与 `Select` 投影的子查询要额外核对生成的 SQL，收益是省一次往返，不值得在本份引入。

**两层租户条件各管什么**：

- **驱动表上的当前租户条件**（`ur.Tenant_Id = @当前租户`、`up.Tenant_Id = @当前租户`）决定查的是**哪个**租户。没有它，结果是不是当前租户的完全取决于全局过滤器：`EnableTenantFilter = false` 时，租户 A 的用户 `"1001"` 会拿到租户 B 同名用户 `"1001"` 的角色与授予——跨租户授权
- **联表上的 `Tenant_Id` 相等**保证一条联表链不跨租户：关联行只和同租户的角色、同租户的角色授予配对

两者合起来才保证「结果全部属于当前租户」；单有联表相等只保证「属于某一个租户」。

**关于过滤器在平台态的口径**：本包的四张表实现 `IStrictMultiTenantEntity`，平台态下严格过滤器收紧为 `TenantId == 0`（`XiHanDataServiceCollectionExtensions.cs:254-255`、`:481-484`），**不是**放行全部；读共享过滤器的平台态哨兵放行（`:246-249`）与严格过滤器 AND 之后同样只剩 0。显式条件在过滤器开着时与它结果相同，关掉时仍然成立。

### 4.7 缓存：不加

上层没有缓存（`XiHan.Framework.Authorization` 不引用 `Caching`，目录下无缓存代码）。检查器把判定降到 1–2 条等值索引查询后，缓存带来的收益是把这 1–2 条变成 0 条，代价是在所有授权写路径上作废——而写路径分散在三个存储、以及应用可能绕开存储直接写表的地方。本份不加；应用需要时在 `IPermissionChecker` 外套装饰器，由应用在自己的写路径上作废（下游 `SaasPermissionChecker` 就是这么做的）。

### 4.8 注册

在 ① 的 `AddXiHanAuthorizationSqlSugar` 里追加：

```csharp
services.Replace(ServiceDescriptor.Scoped<IRoleStore, SqlSugarRoleStore>());
services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, SqlSugarPermissionChecker>());
```

应用若自己替换了检查器（例如 `XiHan.BasicApp` 的 `SaasPermissionChecker` 在自己模块里 `Replace`），应用模块依赖本模块、`ConfigureServices` 后执行，应用的 `Replace` 仍然生效。

## 5. 会静默失效的陷阱

**① 只落库存储、不顶替检查器。**

一切功能正确、测试全绿，但每次鉴权 `2 + N` 条 SQL。本份用 `Aop.OnLogExecuting` 数 SQL 的测试把次数钉死：直接授予命中 1 条，其余 2 条，多权限判定 2 条。**不许为了让别的测试通过而放宽这几个断言。**

**② 检查器的 SQL 丢了 `IsEnabled` 条件。**

默认检查器在内存里 `Where(r => r.IsEnabled)`、`p.IsEnabled`，改写成 SQL 时容易只保留一个。丢了角色的：禁用角色照样授权；丢了权限的：禁用权限照样通过。测试逐条覆盖，并有一个「与 `DefaultPermissionChecker` 判定一致」的对照用例。

**③ `UseTranAsync` 的两个坑。**

- **吞异常**（`AdoProvider.cs:379-389`）：失败时返回 `DbResult.IsSuccess = false`，不抛。不检查返回值，调用方会以为删成功了
- **提交外层事务**：已有事务时 `BeginTranAsync` 什么都不做（`:271-276`），但随后的 `CommitTranAsync` 会把**外层工作单元的事务**提交掉。业务后续若回滚，角色删除已经落地

两条都有测试：删除中途失败（测试里先 `DropTable` 掉 `sys_authz_role_permission`）必须抛出且用户关联被回滚；外层 `BeginTran` → 删除 → `RollbackTran` 后角色必须还在。

**④ 只靠过滤器决定租户，或联表只按 `Role_Id`。**

租户过滤器开着时两者都看不出区别。过滤器关掉时：驱动表没有当前租户条件，就会读到别的租户同名用户的角色与授予；联表只按 `Role_Id`，就会让关联行与别的租户同标识的角色配对。SQLite 测试客户端本就没有过滤器，两条都能直接测：夹具的 `StubCurrentTenant` 设为租户 5，库里同时放租户 5、6 与平台租户 0 的同名数据，断言只见租户 5；另插 `TenantId` 不一致的关联行，断言不配对。

**⑤ 用户角色关联存角色名称。**

照抄默认实现存名称，改名后成员关系静默丢失，而且 `DeleteRoleAsync(roleId)` 的级联要多一次按名称的反查。测试覆盖「改名后仍在角色中、仍有权限」。

**⑥ 删除角色不级联 role_permission。**

以同一标识重建的角色继承旧权限。测试覆盖「删后重建不继承」。

**⑦ `UpdateRoleAsync` 把传入对象的 `CreatedTime` 写回库。**

不会报错，只是创建时间被改成更新时刻。测试断言更新后 `CreatedTime` 不变。

## 6. 测试策略

**只有 SQLite 一层，CI 强门禁执行。** 本份没有并发协议。租户隔离靠显式条件，测试客户端不挂过滤器即可覆盖「过滤器关闭」的情形；另有一组用例手工挂上与框架同形的严格过滤器，确认两者叠加结果不变。

沿用 ① 的 `AuthorizationTestContext`（按反射建出程序集里全部实体的表，② 新增实体不用改夹具的建表逻辑），新增 `CreateRoleStore()`、`CreatePermissionChecker()` 两个工厂方法。

必测：

- **实体约定**：两个新实体进入 ① 的约定测试（表名前缀、分组、严格租户）；唯一索引生效
- **映射**：逐字段对应；`CreatedTime` / `LastModifiedTime` 读回 `Kind == Utc`
- **角色 CRUD**：1.2 表的每一条，包括参数校验的异常类型
- **用户角色关联**：加入不存在的角色抛异常、幂等、移出、按角色取用户、改名后成员关系保持、`IsInRole` 不看启用、跨租户不配对
- **删除角色**：级联两张关联表且不动其他角色的行、删后重建不继承、静态角色同样可删、中途失败整体回滚并抛出、外层事务回滚时删除一并回滚
- **检查器语义**：7 类判定 + 与 `DefaultPermissionChecker` 的对照
- **检查器查询次数**：`Client.Aop.OnLogExecuting` 计数
- **检查器跨租户**：关联行与角色行、角色行与角色权限行 `TenantId` 不一致时不授予
- **当前租户**：租户 5、6 与平台租户 0 各有同名用户 `u1` 的角色关联、角色授予与**直接授予**（`user_permission`）；当前租户为 5 时，存储与检查器只见租户 5 的数据——不挂过滤器、挂严格过滤器两种情形各跑一遍；当前租户为空（平台态）时只见租户 0
- **角色按租户隔离**：同一角色标识与名称可在不同租户各建一个，互相不可见
- **注册**：`IRoleStore`、`IPermissionChecker` 各只剩一个描述符

写用例时的约束：

- **不要断言读回的 `DateTime` 与写入值相等**。SQLite 存文本、精度与 `Kind` 都可能变化。只断言 `Kind` 与「不早于某个时刻」
- 数 SQL 的用例要在**造数之后**才挂 `OnLogExecuting`，否则把造数的 SQL 也数进去
- SQLite 连接串必须带 `Pooling=False`

测试平台是 Microsoft.Testing.Platform：**没有可用的筛选参数**，**不要带 `--logger trx` / `--results-directory`**。

## 7. 已知边界

写入 PR 描述与包 README（③ 收尾时汇总），**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 策略评估仍逐权限查询 | `DefaultPolicyEvaluator` 对 `RequiredPermissions` 逐个调 `IsGrantedAsync`，p 个权限约 2p 条 SQL；本包不替换评估器 |
| 检查器被应用替换 | 应用若 `Replace` 了自己的 `IPermissionChecker`，本包的检查器不生效，热路径次数由应用的实现决定 |
| 与默认实现的一处差异 | 用户角色关联存角色标识，改名后成员关系保持；默认实现改名后会丢失 |
| 静态角色可删 | `IsStatic` 只存不拦，与默认实现一致；拦截应在应用服务层做 |
| `IsInRoleAsync` 不看启用 | 与默认实现一致；禁用角色的成员在 `IsInRoleAsync` 与策略的 `RequiredRoles` 上仍然通过，但不会从该角色得到权限 |
| 租户隔离的保证范围 | 角色表与两张驱动表的每条 SQL 显式带 `Tenant_Id = ICurrentTenant.Id ?? 0`，联表另带 `Tenant_Id` 相等，因此结果只来自当前租户，**与 `EnableTenantFilter` 开关无关**。平台态读写租户 0 的数据，看不到业务租户的角色与授予；平台态要管理某个租户的授权，须先 `ICurrentTenant.Change(租户标识)`。权限定义表全局共享，不在此列 |
| 名称比较口径不一 | `IsInRoleAsync`、`GetRoleByNameAsync` 等按名称的查询在数据库里比较：MySQL / SQL Server 默认排序规则不区分大小写，且按 PAD SPACE 忽略尾随空格，`Admin` 与 `admin `都能命中 `admin`。检查器取回权限名后在内存里用序数比较再核对一次，大小写不同的名称判为未授予。唯一索引保证同一租户内不会同时存在两个只差大小写的名称，因此不构成越权，只是两条路径口径不一致 |
| 预先授予不存在的角色 | `GrantPermissionToRoleAsync` 不校验角色是否存在（与默认实现一致）。给一个尚未创建的角色标识授权会留下孤儿授予，之后以该标识创建的角色直接继承它 |
| 并发创建同名角色 | 查重与插入之间无锁，后到者撞唯一索引抛数据库异常而非 `InvalidOperationException` |
| 无外层事务时的级联 | 自开事务；数据库不支持事务时（不在本框架支持范围）退化为「先关联、后主表」的顺序删除 |
| 名称大小写 | 由数据库排序规则决定；MySQL / SQL Server 默认不敏感 |
| 取消令牌的残留 | SqlSugar 把令牌写进 `Context.Ado.CancellationToken` 且不清除，同作用域后续不带令牌的调用会继承 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动：`MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`）
- 每个 `.cs` 文件带两行版权声明（`XHFH001`）
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**；判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**关闭
- `public` 成员都有 `<summary>`，不用 `<inheritdoc/>`
- 提交信息为中文 Conventional Commits，作用域 `authorization-sqlsugar`，**不加任何 AI 署名**

## 五个共同问题

| 问题 | 本份的回答 | 依据 |
| --- | --- | --- |
| 分表与否 | **不分表** | 角色与关联是长期有效的配置型数据，没有保留期 |
| 主键类型 | **雪花 `long`** | 契约的角色标识是调用方给的字符串、只在租户内唯一，做成 `TenantId + RoleId` 唯一索引，主键另取 |
| 是否参与工作单元事务 | **参与**，`Scoped` | 经 `GetCurrentClient()` 自动登记；删除角色在工作单元里时由工作单元提交，不在时自开事务 |
| 是否需要多库 | **单库** | 检查器要四表联表，五张表必须同库 |
| 顶替方式 | **`Replace`** | 主包 `IRoleStore`、`IPermissionChecker` 均为 `TryAddScoped` |

## 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改会影响什么 |
| --- | --- | --- | --- | --- |
| 1 | 顶替 `IPermissionChecker` | 顶替 | 只改存储无法降低次数（默认检查器按角色循环取权限） | 不顶替则每次鉴权 `2 + N` 条 SQL，多权限判定再乘 k |
| 2 | 判定用几条 SQL | 2 条（直接命中时 1 条） | 省去 `UnionAll` 的 SQL 生成核对；两条都走索引 | 改一条 `UNION ALL` 省一次往返，但要先核对 SqlSugar 对联表子查询的生成结果 |
| 3 | 缓存 | 不加，也不做请求内记忆化 | 上层无缓存；作废点分散；判定已是 1–2 条索引查询 | 要加应做成 `IPermissionChecker` 装饰器，由应用作废 |
| 4 | 用户角色关联存什么 | 角色标识 | 改名不丢成员；与角色权限表同键 | 存名称则改名丢成员，级联删除要反查 |
| 5 | 删除角色级联 | 级联 user_role 与 role_permission，同事务 | 防止同标识重建继承旧权限 | 不级联则留下孤儿授权行 |
| 6 | 静态角色可删 | 不拦截 | 与默认实现一致，契约没说要拦 | 改为拦截要定异常类型，是行为变更 |
| 7 | `IsInRoleAsync` 看不看启用 | 不看 | 与默认实现一致 | 改为看启用会让策略的 `RequiredRoles` 对禁用角色失败，是行为变更 |
| 8 | `UpdateRoleAsync` 是否改 `CreatedTime` | 不改 | 创建时间不应随更新变化 | 改为写回则与默认实现一致，但会丢真实创建时间 |
| 9 | `GetUserRolesAsync` / `GetAllRolesAsync` 排序 | 按 `SortOrder`、`RoleName` 序数 | 默认实现无序；给出稳定顺序 | 纯展示顺序 |
| 10 | 联表带 `TenantId` 相等 | 带 | 不依赖过滤器开关 | 去掉则过滤器关闭时可跨租户配对 |
| 11 | 驱动表与角色表带当前租户条件 | 带，`ICurrentTenant.Id ?? 0`，平台态为 0 | 过滤器可被关掉；与 `Authentication.SqlSugar` 一致 | 去掉则过滤器关闭时跨租户读到同名用户的授权 |

## 9. 下一份

③（`.superpowers/specs/2026-09-28-authorization-sqlsugar-3-policy-store-docs-design.md`）：`SysAuthzPolicy` 实体与 `IPolicyStore` 的 5 个方法（`RequiredRoles` / `RequiredPermissions` / `RequiredClaims` 存 JSON，含自定义要求的策略拒绝写入），以及包 README、`docs/packages/authorization-sqlsugar.md`、侧边栏、包索引页、四份 README 的模块清单。
