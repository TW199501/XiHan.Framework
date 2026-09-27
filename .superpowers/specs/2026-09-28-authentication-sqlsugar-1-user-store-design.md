# Authentication.SqlSugar ①：包骨架与用户存储 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-authentication-sqlsugar-1-user-store.md`
- **前置**：无（`XiHan.Framework.Data` 的 P5 能力本份不需要）
- **所属 PR**：`Authentication.SqlSugar` 的唯一一个 PR，与第 ② 份（`.superpowers/specs/2026-09-28-authentication-sqlsugar-2-refresh-token-external-login-design.md`）同一个
- **Linear 议题**：`https://linear.app/elf-express/issue/EDDIE-9`
- **系列**：`Authentication.SqlSugar` 共 2 份。① 包骨架 + `IUserStore`；② `IRefreshTokenStore` + `IExternalLoginStore` + 文档站收尾

> 本文档**自成一体**。实现第 ① 份所需的全部约束都写在这里，不引用其他设计文档。与第 ② 份共用的约定在两份里各写一遍——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的地方是 `UpdateUserAsync`。** 框架自己的 `DefaultAuthenticationService` 有三条流程是「先读出用户对象 → 中途调别的存储方法改库 → 最后把**一开始读出的那个对象**整份交给 `UpdateUserAsync`」。内存版 `DefaultUserStore` 返回的是字典里的同一个引用，所以中途的修改会反映在那个对象上，流程恰好正确；换成数据库实现后，每次读取都是新对象，最后那次 `UpdateUserAsync` 会把**旧快照**写回去——改过的密码被还原、刚生成的恢复码被清空、登录成功后失败计数复原。**逐个方法测都是绿的**，只有按调用方的真实顺序串起来才会暴露。第 4.4 节的「作用域内身份映射」就是为此设计的，第 5 节第 ① 条详述。

---

## 1. 背景与目标

### 1.1 现状

`XiHan.Framework.Authentication` 的用户存储只有内存实现：

- 契约 `IUserStore`（`framework/src/XiHan.Framework.Authentication/Users/IUserStore.cs:12-82`）共 9 个方法：按用户名取、按标识取、更新用户、更新密码、失败计数的读 / 加 / 清、锁定结束时间的写 / 读
- 默认实现 `DefaultUserStore`（`Users/DefaultUserStore.cs`）是两本 `ConcurrentDictionary`，另有契约外的 `AddUserAsync` / `DeleteUserAsync` / `ClearAsync` / `GetAllUsersAsync` / `AddUsersAsync`
- 注册方式 `services.TryAddScoped<IUserStore, DefaultUserStore>()`（`Extensions/DependencyInjection/XiHanAuthenticationServiceCollectionExtensions.cs:43`）。**作用域注册的内存实现意味着每个请求都是一本空字典**——主包在生产环境实际上不可用

契约的数据载体是 `UserInfo`（`Users/UserInfo.cs:9-85`），15 个属性，用户标识是 `string UserId`。

### 1.2 调用方（读过才知道 `UserInfo` 是怎么被用的）

框架内唯一调用方是 `DefaultAuthenticationService`（`Users/DefaultAuthenticationService.cs`，命名空间 `XiHan.Framework.Authentication`）：

| 流程 | 行号 | 对存储的调用顺序 |
| --- | --- | --- |
| 用户名密码登录 `AuthenticateAsync` | 57-118 | `GetLockoutEndAsync` → `GetUserByUsernameAsync`（得到对象 **U**）→ 失败时 `RecordFailedLoginAttemptAsync` → 成功时可能 `UpdatePasswordAsync`（重新哈希，96-97）→ `ResetFailedLoginAttemptsAsync`（107）→ 改 `U.LastLoginTime` → **`UpdateUserAsync(U)`**（110-111） |
| 修改密码 `ChangePasswordAsync` | 232-270 | `GetUserByIdAsync`（**U**）→ `UpdatePasswordAsync`（263）→ 改 `U.PasswordChangedTime` → **`UpdateUserAsync(U)`**（266-267） |
| 重置密码 `ResetPasswordAsync` | 275-311 | 同上（304-308） |
| 启用双因素 `EnableTwoFactorAuthenticationAsync` | 316-351 | `GetUserByIdAsync`（**U**）→ `GenerateRecoveryCodesAsync`（337，内部另取一次 **U'**、写恢复码哈希、`UpdateUserAsync(U')`，411-422）→ 改 `U.TwoFactorSecret` / `U.TwoFactorEnabled` → **`UpdateUserAsync(U)`**（342） |
| 核销恢复码 `VerifyRecoveryCodeAsync` | 430-457 | `GetUserByIdAsync` → 移除用过的那个哈希 → `UpdateUserAsync`（450-451） |
| 记录失败 `RecordFailedLoginAttemptAsync` | 462-481 | `IncrementFailedLoginAttemptsAsync` → `GetFailedLoginAttemptsAsync` → 达到阈值时 `SetLockoutEndAsync(UtcNow + 锁定时长)` |
| 解除锁定 `ResetFailedLoginAttemptsAsync` | 486-495 | `ResetFailedLoginAttemptsAsync` → `SetLockoutEndAsync(null)` |
| 是否锁定 `IsAccountLockedAsync` | 500-522 | `GetLockoutEndAsync`，与 `DateTime.UtcNow` 比较 |

从中读出的事实：

1. **加粗的四处 `UpdateUserAsync` 都在写回一个「中途被别的存储调用改过库」的旧对象**（见实现前必读）。内存版之所以正确，是因为 `UpdatePasswordAsync` 内部 `GetUserByIdAsync` 拿到的就是调用方手里那个引用（`DefaultUserStore.cs:106-113`）
2. **存储层拿到的永远是已哈希的值**：密码由 `IPasswordHasher.HashPassword` 在服务层算好（96、260、301 行），恢复码同样在服务层哈希后才赋给 `RecoveryCodes`（421 行）。`TwoFactorSecret` 则是 TOTP 明文密钥（331、340 行），无法哈希
3. **失败计数是「加一后再读」**（469-470 行），并据读到的值决定是否锁定。计数若不是数据库侧原子累加，并发的失败登录会丢失累加，锁定阈值可被绕过
4. **时间一律是 `DateTime.UtcNow`**，锁定判定是 `lockoutEnd.Value <= DateTime.UtcNow`（514 行），比较不看 `Kind`
5. **用户名按调用方传入的原样查找**，服务层不做大小写处理

下游 `XiHan.BasicApp` 的 `SaasUserStore`（本仓库外，`E:/source/XiHan/XiHan.BasicApp/backend/src/modules/XiHan.BasicApp.Saas/Infrastructure/Auth/SaasUserStore.cs`）是一个现成的数据库实现，可作旁证：它以 `long.TryParse` 解析 `UserId`、按当前租户过滤、`UpdateUserAsync` 刻意不写密码列。**但它有第 1 条所述的缺陷**——它的 `UpdateUserAsync` 会把旧快照里的失败计数与锁定字段写回去。

**对主要消费方的意义**：BasicApp 已经在 `Extensions/ServiceCollectionExtensions.cs` 的 `AddSaasAuthStores` 里以 `Replace` 换掉了本包要顶替的全部三个存储——`SaasUserStore`（落在它自己的 `Sys_User` / `Sys_User_Security` 表）、`SaasRefreshTokenStore`（分布式缓存、SHA-256 作键）、`SaasExternalLoginStore`（它自己的 `Sys_External_Login` 表）。因此本包**不是** BasicApp 的直接依赖，它的用户数据也不会迁到 `sys_auth_user`。本包服务的是没有自建用户体系、直接使用框架 `DefaultAuthenticationService` 的应用；对 BasicApp 的价值主要在于把上面那个旧快照问题与刷新令牌的重用检测以参照实现的形式摆出来。模块装配顺序上 BasicApp 的 `Replace` 在后，同时引用本包也不会改变它现有的行为。

### 1.3 要交付什么

新包 `XiHan.Framework.Authentication.SqlSugar`，本份交付：

1. 包骨架：csproj、模块类、注册扩展、包 README
2. 实体 `SysAuthUser`（表 `sys_auth_user`）
3. 映射 `AuthUserMapper` 与时间换算 `StorageTime`
4. `SqlSugarUserStore : IUserStore`，外加契约外的 `AddUserAsync`
5. 以 `Replace` 顶替主包的 `DefaultUserStore`，生命周期保持 Scoped

刷新令牌、第三方登录、文档站与模块清单属第 ② 份。

### 1.4 成功标准

1. `DefaultAuthenticationService` 在本存储上跑完「修改密码」「启用双因素」「核销恢复码」「失败后成功登录」「连续失败锁定」「登录时重新哈希」六条流程，每一步之后用**新的存储实例**读库，结果与内存版语义一致
2. 两个存储实例交替对同一用户累加失败计数，库里的值等于累加总次数
3. 用户名查找大小写不敏感；同一租户内仅大小写不同的用户名被拒绝
4. 租户上下文里看不到其他租户与平台（`TenantId = 0`）的用户，按用户名与按标识都一样
5. 存储层对 `PasswordHash`、`RecoveryCodes`、`TwoFactorSecret` 原样存取，不做任何哈希或变换
6. `IUserStore` 的注册被顶替为 `SqlSugarUserStore`，生命周期为 Scoped
7. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约、默认实现与调用方 —— 最高优先**

```
framework/src/XiHan.Framework.Authentication/
  Users/IUserStore.cs                    9 个方法的签名与注释
  Users/UserInfo.cs                      15 个属性
  Users/DefaultUserStore.cs              内存语义，尤其 99-114 行的引用共享
  Users/DefaultAuthenticationService.cs  全部调用方，第 1.2 节的表格按它整理
  Extensions/DependencyInjection/XiHanAuthenticationServiceCollectionExtensions.cs:43   TryAddScoped
  XiHanAuthenticationModule.cs           模块依赖
framework/src/XiHan.Framework.Security/Password/PasswordHasher.cs:35-53   密码哈希的格式（version:iterations:algorithm:salt:hash）
```

**② 同系列已完成的包 —— 形状范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/     简单「实体 + 存储」、雪花 long 主键、Replace 顶替、取消令牌只在写入前检查
framework/src/XiHan.Framework.EventBus.SqlSugar/     SugarEntity 派生实体、SetColumns 条件更新
framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs   单客户端桩解析器的写法
framework/test/XiHan.Framework.Auditing.Tests/Fakes/FakeCurrentTenant.cs   当前租户替身的写法
```

**③ 数据层 —— 客户端解析与租户**

```
framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs    GetClientForEntity<T>()
framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs               Basic_Id、Row_Version
framework/src/XiHan.Framework.Data/Extensions/DependencyInjection/XiHanDataServiceCollectionExtensions.cs:246-249   全局租户过滤器放行 TenantId = 0
framework/src/XiHan.Framework.MultiTenancy.Abstractions/ICurrentTenant.cs        long? Id
```

**④ SqlSugar 源码 —— API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Abstract/UpdateProvider/UpdateableProvider.cs    SetColumns 两种重载（851 行 T 形式、936 行 bool 形式）
  Abstract/CodeFirstProvider/CodeFirstProvider.cs  350-392 行：SugarIndex 建索引、{table} 占位符替换
  Entities/Mapping/SugarMappingAttribute.cs        SugarIndexAttribute 构造函数（346、354 行）
  Interface/IQueryable.cs                          FirstAsync() / AnyAsync()
```

> 树与目录名：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`（依赖 `Microsoft.Data.Sqlite`），权威源码是 `Src/Asp.NetCore2/`。同级的 `Src/Asp.Net/` 是 .NET Framework 变体。更新提供者的目录名是 `UpdateProvider`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 条件更新、字段自增 | `更新數據.md`（第 25 行与 2.2 节：`.SetColumns(it => it.Num == it.Num + 1)`） |
| 建表与索引 | `庫表管理DbMaintenance.md`、`实体配置`相关的 `SugarIndex` 用法 |
| 查询 | `簡單的查詢.md`、`異步查詢.md` |
| 多租户 | `多租戶基礎.md` |

仓库自身文档：`docs/packages/data.md`、`docs/packages/authentication.md`、`docs/packages/auditing-sqlsugar.md`（同系列包文档的写法）。

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

- **不在存储层做任何哈希、加密、修剪或大小写变换**于 `PasswordHash`、`RecoveryCodes`、`TwoFactorSecret`。它们原样进、原样出。用户名的规范化只写进**单独的** `Normalized_User_Name` 列，`User_Name` 列保存原样
- **不改 `IUserStore` 契约、`UserInfo`、`DefaultAuthenticationService`、`DefaultUserStore`**。主包一行都不动
- **不依赖全局租户过滤器**做隔离，也不让实体实现 `IMultiTenantEntity` / `IStrictMultiTenantEntity`。租户条件在每条 SQL 里显式写出
- **不用读-改-写实现失败计数累加**。必须是数据库侧 `SET x = x + 1`
- **不以受影响行数单独判定「用户不存在」**
- **不用 `DateTimeOffset` 做时间列**，也不在比较前把读回的时间当作本地时间
- **不在 `UpdateUserAsync` 里写 `Password_Hash`、`Failed_Login_Attempts`、`Is_Locked`、`Lockout_End` 四列**
- **不把 `IUserStore` 注册为 Singleton**
- **不做刷新令牌与第三方登录**（第 ② 份），**不动 `docs/` 与模块清单**（第 ② 份）

## 3. 非目标

- **不提供契约外的删除、列举、批量导入**。`DefaultUserStore` 的 `DeleteUserAsync` / `ClearAsync` / `GetAllUsersAsync` / `AddUsersAsync` 不移植，只移植让表可用所必需的 `AddUserAsync`
- **不做按邮箱或手机号登录**。契约没有这两个查找入口，邮箱与手机号不建唯一约束
- **不做字段级加密**。`TwoFactorSecret` 明文落库，见「待确认的决策」S1
- **不做乐观并发**。`Row_Version` 列来自基类，本包不启用版本校验
- **不修复 `DefaultAuthenticationService` 自身的问题**（例如 `ResetPasswordAsync` 不校验重置令牌，见其 282-284 行注释）——那是主包的事

## 五个共同问题

| 问题 | 本份的答案 | 依据 |
| --- | --- | --- |
| **分表与否** | **不分表** | 用户是长期主数据，不是按时间增长的日志 |
| **主键类型** | **雪花 `long`**（经 `IDistributedIdGenerator<long>`），对外以十进制字符串作为 `UserInfo.UserId` | 契约 `IUserStore` 用 `string UserId`，但同包的 `IExternalLoginStore` 用 `long userId`（`OAuth/IExternalLoginStore.cs:19-36`）。两个契约要能指向同一个用户，只能取两者的交集——能解析为正整数的字符串。非数字的 `UserId` 在本存储里视为不存在 |
| **是否参与工作单元事务** | **参与**：注册为 Scoped，经 `ISqlSugarClientResolver.GetClientForEntity<SysAuthUser>()` 取客户端，事务型工作单元内自动登记 | 登录、改密码是业务操作的一部分，失败时应随业务回滚 |
| **是否需要多库** | **单库**：跟随当前租户布局的主库。不加 `IncludeModuleConnections` | 用户表不属于任何业务模块库 |
| **顶替方式** | **`services.Replace(ServiceDescriptor.Scoped<IUserStore, SqlSugarUserStore>())`** | 主包用 `TryAddScoped`（`XiHanAuthenticationServiceCollectionExtensions.cs:43`），`TryAdd` 在已注册时是空操作。生命周期必须保持 Scoped，理由见 4.4 |

## 待确认的决策

**安全相关的排在前面。** 每一条都是本设计替用户做的选择。

| # | 决策 | 默认值 | 理由 | 若想改会影响什么 |
| --- | --- | --- | --- | --- |
| S1 | `TwoFactorSecret` 如何落库 | **原样明文**存入 `Two_Factor_Secret`（长 256） | TOTP 校验需要密钥明文，无法哈希；契约与框架都没有字段级加密的抽象。可用的 ASP.NET Data Protection 在密钥环未持久化时，进程重启即令全部已存密钥无法解密——全员双因素失效，比明文更糟。**这是一个缺口，不是安全的选择**：数据库泄漏即泄漏全部 TOTP 密钥 | 改为加密需新增一个加解密抽象（例如 `ITwoFactorSecretProtector`）并规定密钥环持久化，属于独立议题；本包届时只需在映射层调用它 |
| S2 | 存储层是否对 `PasswordHash` / `RecoveryCodes` 做任何计算 | **不做**，原样存取 | 服务层已哈希（`DefaultAuthenticationService.cs:96、260、301、421`）。存储层再算一次就是双重哈希，所有登录永远失败 | 无。这一条不应改 |
| S3 | `UpdateUserAsync` 是否写 `Password_Hash` 与失败计数、锁定三列 | **都不写**：密码只经 `UpdatePasswordAsync`；`Failed_Login_Attempts` / `Is_Locked` / `Lockout_End` 只经计数与锁定的专用方法 | 与 S4 共同防旧快照回写；也防止调用方构造一个 `PasswordHash` 为空的新 `UserInfo` 去更新资料时把密码清空。框架自身所有改密码路径都走 `UpdatePasswordAsync`。锁定三列：身份映射只保证单个请求内一致；跨请求时，请求 A（改密码、启停双因素、核销恢复码）读出用户，并发的请求 B 累加失败次数或加锁，A 最后整份写回就会**解除锁定**——与 S5 要防的丢失更新是同一个问题。`DefaultAuthenticationService` 只经专用方法（469-494 行）改这三列 | 与 `DefaultUserStore` 不同：直接改 `user.PasswordHash` / `user.IsLocked` 等再 `UpdateUserAsync` 的下游代码，改动会被**静默忽略**（管理后台「解锁用户」须调 `SetLockoutEndAsync(username, null)` 与 `ResetFailedLoginAttemptsAsync`）。已写入 README |
| S4 | 作用域内身份映射 | **开启**：同一个存储实例内，同一用户的各次读取返回同一个 `UserInfo` 实例；`UpdatePasswordAsync`、失败计数、锁定四个写入方法同时更新该实例 | 这是让 `DefaultAuthenticationService` 四条「写回旧快照」流程在数据库上仍正确的唯一办法（见 4.4、5 ①）。存储是 Scoped，映射随请求作用域结束而丢弃 | 关掉则：修改密码后旧密码仍然有效、启用双因素后恢复码为空、登录成功后失败计数复原 |
| S5 | 失败计数如何累加 | **数据库侧原子累加** `SET Failed_Login_Attempts = Failed_Login_Attempts + 1` | 读-改-写在并发失败登录下丢失累加，攻击者并行猜密码可绕过锁定阈值 | 无。这一条不应改 |
| S6 | 用户名的大小写与唯一性 | **大小写不敏感**：另存 `Normalized_User_Name`（`ToUpperInvariant()`），查找与唯一索引 `(Tenant_Id, Normalized_User_Name)` 都用它 | 大小写敏感的唯一约束允许 `Admin` 与 `admin` 并存，而 MySQL / SQL Server 的默认排序规则下 `=` 又不区分大小写——查找会命中任意一个，身份混淆。规范化列让行为与数据库排序规则无关 | 与 `DefaultUserStore`（`ConcurrentDictionary` 默认序数比较，大小写敏感）不同。从大小写敏感的旧数据迁入时，仅大小写不同的重名会让唯一索引建不起来 |
| S7 | 邮箱是否唯一 | **不唯一、不建索引** | 契约没有按邮箱查找的方法，唯一性属业务规则 | 需要时由业务加约束；本包不读邮箱 |
| S8 | 租户隔离方式 | **显式列 `Tenant_Id` + 每条 SQL 等值条件**（`ICurrentTenant.Id ?? 0`），实体不实现任何框架租户接口 | 框架全局租户过滤器对 `IMultiTenantEntity` 是「本租户 **或** `TenantId = 0`」（`XiHanDataServiceCollectionExtensions.cs:246-249`）——租户上下文里能查到平台用户，即可用平台管理员的用户名在租户入口登录。过滤器还能被 `EnableTenantFilter = false` 整体关掉 | 改用全局过滤器会引入上面两个问题。若下游希望用户全局唯一（不分租户），把所有请求都放在无租户上下文即可 |
| O1 | 主键 | 雪花 `long`，`UserId` 为其十进制字符串 | 见「五个共同问题」 | 改为字符串主键则与 `IExternalLoginStore` 的 `long userId` 无法对应 |
| O2 | 契约外的 `AddUserAsync` | **提供**，签名 `Task<string> AddUserAsync(UserInfo user, CancellationToken cancellationToken = default)` | 契约没有创建方法，没有它表就无法被本包写入；`DefaultUserStore` 同样提供 | 下游也可以直接插 `SysAuthUser`，但需自行填 `Normalized_User_Name` |
| O3 | `AddUserAsync` 的标识 | `UserId` 为空时生成雪花标识并回写到 `user.UserId`；为正整数字符串时沿用；其余抛 `ArgumentException` | 允许从旧系统迁入时保留标识 | — |
| O4 | 表名 | `sys_auth_user` | 下游 BasicApp 已有 `Sys_User`（MySQL 在 Linux 上表名区分大小写，Windows 上不区分），加 `auth_` 段避免同库冲突 | 改名需同步 README 与文档站 |
| O5 | 时间列类型 | `DateTime`，写入前换算为 UTC（`Local` 转换、`Unspecified` 视为 UTC），读回后标记为 `Utc` | `DateTimeOffset` 在 SQLite 上读回时按本机时区平移（同系列 P6 实测）；契约本身就是 `DateTime` | — |
| O6 | `RecoveryCodes` / `AdditionalData` 的存法 | `System.Text.Json` 序列化为文本列（`StaticConfig.CodeFirst_BigString`） | 不引入关联表；不用 SqlSugar 的 `IsJson`，序列化行为由本包掌握 | `AdditionalData` 的值往返后是 `JsonElement`，不再是原始类型 |
| O7 | 取消令牌 | 只在访问数据库前 `ThrowIfCancellationRequested()`，**不传给 SqlSugar** | 与 `Auditing.SqlSugar` 一致。SqlSugar 的 `*Async(CancellationToken)` 把令牌写进 `Context.Ado.CancellationToken` 且不清除（`QueryableExecuteSqlAsync.cs:79`、`UpdateableProvider.cs:182`），会残留给同一上下文里后续不带令牌的异步调用 | 改为传入则需评估残留影响 |
| O8 | 时间来源 | 注入 `TimeProvider`，注册时 `TryAddSingleton<TimeProvider>(TimeProvider.System)` | 让锁定判定可测 | — |
| O9 | 输入边界 | 沿用 `DefaultUserStore`：空白用户名的查找返回 `null`、计数操作静默忽略；`UpdateUserAsync` 用户不存在抛 `InvalidOperationException`；`UpdatePasswordAsync` 空白参数抛 `ArgumentException` | 调用方按这些语义写过代码 | — |

## 4. 设计

### 4.1 包结构

```
framework/src/XiHan.Framework.Authentication.SqlSugar/
  XiHan.Framework.Authentication.SqlSugar.csproj    引用 Authentication 与 Data
  XiHanAuthenticationSqlSugarModule.cs              [DependsOn(Authentication, Data)]，只调注册扩展
  README.md                                         七段结构
  Entities/SysAuthUser.cs
  Mapping/StorageTime.cs                            UTC 换算
  Mapping/AuthUserMapper.cs                         UserInfo ↔ SysAuthUser
  Users/SqlSugarUserStore.cs
  Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs
```

目录名刻意**不叫** `Jwt/` / `OAuth/`，避免与主包命名空间 `XiHan.Framework.Authentication.Jwt` 等在名称查找时混淆；第 ② 份用 `RefreshTokens/`、`ExternalLogins/`。

### 4.2 实体 `SysAuthUser`

```csharp
[SugarTable("sys_auth_user")]
[SugarIndex("ux_{table}_tenant_normalized_user_name", nameof(TenantId), OrderByType.Asc, nameof(NormalizedUserName), OrderByType.Asc, true)]
public class SysAuthUser : SugarEntity<long>
```

| 属性 | 列名 | 类型 / 长度 | 可空 | 说明 |
| --- | --- | --- | --- | --- |
| `BasicId`（基类） | `Basic_Id` | `long` | 否 | 雪花标识，非自增 |
| `RowVersion`（基类） | `Row_Version` | `long` | 否 | 本包不使用 |
| `TenantId` | `Tenant_Id` | `long` | 否 | 0 为平台 |
| `UserName` | `User_Name` | 128 | 否 | 原样 |
| `NormalizedUserName` | `Normalized_User_Name` | 128 | 否 | `ToUpperInvariant()` |
| `PasswordHash` | `Password_Hash` | 512 | 否 | 原样；框架默认格式约 110 字符，512 给 bcrypt / Argon2 等留余量；无密码的账户存空串 |
| `Email` | `Email` | 256 | 是 | |
| `PhoneNumber` | `Phone_Number` | 32 | 是 | |
| `TwoFactorEnabled` | `Two_Factor_Enabled` | `bool` | 否 | |
| `TwoFactorSecret` | `Two_Factor_Secret` | 256 | 是 | 明文，见 S1 |
| `RecoveryCodes` | `Recovery_Codes` | 大文本 | 是 | 恢复码哈希的 JSON 数组 |
| `IsLocked` | `Is_Locked` | `bool` | 否 | |
| `LockoutEnd` | `Lockout_End` | `DateTime` | 是 | UTC |
| `FailedLoginAttempts` | `Failed_Login_Attempts` | `int` | 否 | |
| `LastLoginTime` | `Last_Login_Time` | `DateTime` | 是 | UTC |
| `PasswordChangedTime` | `Password_Changed_Time` | `DateTime` | 是 | UTC |
| `IsActive` | `Is_Active` | `bool` | 否 | |
| `AdditionalData` | `Additional_Data` | 大文本 | 是 | JSON 对象 |

索引名里的 `{table}` 由 SqlSugar 建索引时替换为表名（`CodeFirstProvider.cs:361-364`）。SQLite 与 PostgreSQL 的索引名在库内全局唯一，必须带表名。

不加 `[TableInitialization]`，与 `Auditing.SqlSugar` 一致。

### 4.3 映射

`StorageTime`（公开静态类）：

| 方法 | 行为 |
| --- | --- |
| `DateTime ToUtc(DateTime value)` / `DateTime? ToUtc(DateTime? value)` | `Local` → `ToUniversalTime()`；`Unspecified` → `SpecifyKind(Utc)`；`Utc` 原样 |
| `DateTime FromStorage(DateTime value)` / `DateTime? FromStorage(DateTime? value)` | `SpecifyKind(Utc)` |

`AuthUserMapper`（公开静态类）：

| 方法 | 行为 |
| --- | --- |
| `string NormalizeUserName(string userName)` | `ToUpperInvariant()` |
| `SysAuthUser ToEntity(UserInfo user, long basicId, long tenantId)` | 逐字段复制；时间经 `StorageTime.ToUtc`；忽略 `user.UserId` |
| `UserInfo ToUserInfo(SysAuthUser entity)` | 逐字段复制；`UserId = BasicId.ToString(CultureInfo.InvariantCulture)`；时间经 `StorageTime.FromStorage` |
| `string SerializeRecoveryCodes(List<string>? codes)` | `null` 视为空列表，结果至少是 `[]` |
| `List<string> DeserializeRecoveryCodes(string? json)` | 空白 → 空列表 |
| `string? SerializeAdditionalData(Dictionary<string, object>? data)` | `null` → `null` |
| `Dictionary<string, object>? DeserializeAdditionalData(string? json)` | 空白 → `null`；值为 `JsonElement` |

### 4.4 `SqlSugarUserStore`

构造函数：`(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider)`。

**作用域内身份映射**：字段 `Dictionary<long, UserInfo> _loadedUsers`。

- 读取方法**总是先查库**（带租户条件），查到后若映射里已有该标识的实例就返回那个实例，否则映射出新实例放进映射。先查库再取映射，保证租户切换后不会从映射里拿到别的租户的用户
- `UpdatePasswordAsync` 成功后，若映射里有该用户，改其 `PasswordHash`
- 失败计数的加 / 读 / 清与锁定的写 / 读，执行后重新读取该行，把 `FailedLoginAttempts`、`IsLocked`、`LockoutEnd` 同步到映射里的实例
- `UpdateUserAsync(user)` 成功后把 `user` 放进映射（替换原有实例）
- `AddUserAsync(user)` 成功后把 `user` 放进映射

有了映射，第 1.2 节四条流程里「一开始读出的对象」就是后续写入方法改过的那个对象，`UpdateUserAsync` 写回的不再是旧快照。

**租户**：`private long GetTenantId()` 返回 `_currentTenant.Id ?? 0`。所有查询与更新的 `Where` 同时包含 `TenantId == tenantId`。

**九个契约方法**：

| 方法 | SQL 形状 | 边界 |
| --- | --- | --- |
| `GetUserByUsernameAsync` | `WHERE Tenant_Id = @t AND Normalized_User_Name = @n` | 空白 → `null` |
| `GetUserByIdAsync` | `WHERE Basic_Id = @id AND Tenant_Id = @t` | 非正整数 → `null` |
| `UpdateUserAsync` | `UPDATE SET` 11 列：`User_Name`、`Normalized_User_Name`、`Email`、`Phone_Number`、`Two_Factor_Enabled`、`Two_Factor_Secret`、`Recovery_Codes`、`Last_Login_Time`、`Password_Changed_Time`、`Is_Active`、`Additional_Data`；**不写** `Password_Hash`、`Failed_Login_Attempts`、`Is_Locked`、`Lockout_End`；`WHERE Basic_Id AND Tenant_Id` | `user` 为 `null`、`UserId` 非正整数或 `Username` 空白 → `ArgumentException`；不存在 → `InvalidOperationException` |
| `UpdatePasswordAsync` | `UPDATE SET Password_Hash = @h WHERE Basic_Id AND Tenant_Id` | 空白参数 → `ArgumentException`；非正整数或不存在 → `InvalidOperationException` |
| `GetFailedLoginAttemptsAsync` | 读行 | 不存在 → 0 |
| `IncrementFailedLoginAttemptsAsync` | `UPDATE SET Failed_Login_Attempts = Failed_Login_Attempts + 1 WHERE Tenant_Id AND Normalized_User_Name` | 不存在静默忽略 |
| `ResetFailedLoginAttemptsAsync` | `UPDATE SET Failed_Login_Attempts = 0 WHERE …` | 同上 |
| `SetLockoutEndAsync` | `UPDATE SET Lockout_End = @e, Is_Locked = @l WHERE …`，`@l = e 非空 且 e > 当前 UTC` | 同上；`Is_Locked` 的计算沿用 `DefaultUserStore.cs:160` |
| `GetLockoutEndAsync` | 读行 | 不存在 → `null`；返回值 `Kind = Utc` |

**「不存在」的判定**：`UPDATE` 受影响行数为 0 时**再查一次**存在性（`AnyAsync`），不存在才抛。原因见 5 ⑤。

**`AddUserAsync`**：先 `AnyAsync` 检查同租户规范化用户名，存在则抛 `InvalidOperationException`；唯一索引兜住并发插入。`PasswordHash` 原样写入。

**取消令牌**：每个方法开头 `cancellationToken.ThrowIfCancellationRequested()`，不传给 SqlSugar（O7）。

### 4.5 注册

```csharp
public static IServiceCollection AddXiHanAuthenticationSqlSugar(this IServiceCollection services, IConfiguration configuration)
{
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.TryAddSingleton<TimeProvider>(TimeProvider.System);
    services.Replace(ServiceDescriptor.Scoped<IUserStore, SqlSugarUserStore>());

    return services;
}
```

`configuration` 本份暂不读取，第 ② 份用它绑定配置节。模块类 `ConfigureServices` 只调这一个方法。

模块依赖 `XiHanAuthenticationModule` 与 `XiHanDataModule`，因此本模块的 `ConfigureServices` 在主包之后执行，`Replace` 必然覆盖主包的 `TryAdd`。

## 5. 会静默失效的陷阱

**① 让 `UpdateUserAsync` 写回旧快照。**

第 1.2 节加粗的四处调用，在没有身份映射、且 `UpdateUserAsync` 写全部列的实现上：

- `ChangePasswordAsync`：`UpdatePasswordAsync` 写入新哈希后，`UpdateUserAsync(U)` 用 `U.PasswordHash`（旧哈希）覆盖回去——**用户改了密码，旧密码继续有效**。在账户被盗后改密码的场景里，这是安全事故
- `EnableTwoFactorAuthenticationAsync`：`GenerateRecoveryCodesAsync` 写入 10 个恢复码哈希后，`UpdateUserAsync(U)` 用 `U.RecoveryCodes`（空列表）覆盖——**用户以为有恢复码，实际一个都没有**
- `AuthenticateAsync`：`ResetFailedLoginAttemptsAsync` 清零后，`UpdateUserAsync(U)` 用 `U.FailedLoginAttempts`（登录前的值）覆盖——失败计数在成功登录后复原，下次几次失败就锁定

**逐方法的单元测试全部是绿的**，对 `DefaultUserStore` 跑同样的流程也是绿的（它靠引用共享碰巧正确）。只有用真实的 `DefaultAuthenticationService`、在每步之后用**新的存储实例**读库，才会暴露。本份的流程测试就是这么写的，**不要为了省事把流程测试改成共用一个存储实例读结果**——那样读到的是映射里的对象，库里写错了也看不出来。

S3（不写密码列）只挡住第一条；第二、三条只能靠身份映射挡。跨请求的同类问题——另一个请求刚写入的失败计数或锁定被整份写回抹掉——身份映射挡不住，靠 S3 把锁定三列也排除在 `UpdateUserAsync` 之外；用例「更新用户信息不改写其他请求写入的失败次数与锁定」覆盖。

**② 失败计数写成读-改-写。**

`var u = Get(); u.Failed++; Update(u)`：串行测试完全正确。并发下 N 个失败请求可能只累加 1 次，锁定阈值形同虚设。SQLite 单测不起并发，发现不了——本份「两个存储实例交替累加」的用例只抓得住一种读-改-写：从身份映射里的旧对象取值再写回。「每次重新查库、加一、写回」的读-改-写串行时结果正确，这条用例照样放过。原子性以 `SET x = x + 1` 这条 SQL 为准，**不要以用例变绿为准**。

**③ 靠全局租户过滤器做隔离。**

全局过滤器放行 `TenantId = 0` 的平台行，租户入口能用平台用户名登录；`EnableTenantFilter = false` 时过滤器整体失效。**SQLite 测试桩不装任何过滤器**，依赖过滤器的实现在测试里与显式条件的实现表现完全一样。每条 SQL 必须显式带 `Tenant_Id` 条件，用例「平台上下文看不到租户用户」「按标识查找不跨租户」专门覆盖。

**④ 在存储层哈希或变换密码哈希。**

`AddUserAsync` / `UpdatePasswordAsync` 里多一次 `HashPassword`、一次 `Trim()`、一次大小写变换，存储自己的读写测试照样绿（读回的就是自己写的）；所有登录永远失败。流程测试能发现，但前提是流程测试用真实的 `PasswordHasher`。

**⑤ 用受影响行数判断「用户不存在」。**

MySQL 在连接串 `UseAffectedRows=true` 时，`UPDATE` 返回**实际变更**的行数——值没变（例如把 `LastLoginTime` 更新成同一个值、或重复写同一个密码哈希）就返回 0，于是对存在的用户抛「用户不存在」。SQLite 返回匹配行数，**测试永远发现不了**。本设计在受影响行数为 0 时再查一次存在性。

**⑥ 时间的 `Kind`。**

- 用 `DateTimeOffset` 列：SQLite 读回时按本机时区平移，UTC+8 的机器上锁定结束时间整体晚 8 小时
- 读回的 `DateTime` 是 `Unspecified`，若有人在比较前调 `ToUniversalTime()`，它会被当成本地时间再换算一次，同样平移时区小时数

`DefaultAuthenticationService` 的比较不看 `Kind`（`DateTime` 比较只看刻度），只要写入时是 UTC、读回时不再换算就正确。`StorageTime` 统一处理两个方向。**不要写「读回的时间等于写入的时间」这类带毫秒以下精度的断言**，用整秒时间。

**⑦ 用户名比较交给数据库排序规则。**

MySQL / SQL Server 默认不区分大小写，PostgreSQL 与 SQLite 区分——同一份代码在不同库上行为不同。规范化列让比较与排序规则无关。**SQLite 区分大小写**，所以「按用户名查找不区分大小写」这条用例能证明规范化列生效；反过来，一个忘了规范化、直接比 `User_Name` 的实现在 MySQL 上也会「碰巧」大小写不敏感，但唯一约束仍按原样生效。

**⑧ 注册写成 `TryAdd`，或生命周期写成 Singleton。**

- `TryAddScoped`：主包已注册，空操作。表现是「注册成功、无报错、用户依然在内存字典里」
- `Singleton`：身份映射跨请求共享——A 请求读出的用户对象会被 B 请求拿到，并发下相互污染，且整个进程生命周期内不再从库里刷新。注册测试断言生命周期为 Scoped

## 6. 测试策略

**只有一层：SQLite，CI 强门禁执行。** 本份没有跨实例并发协议，不设真实数据库层。

测试项目 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/`。夹具 `AuthenticationTestContext`：一个临时 `.db` 文件、单客户端桩解析器、`FakeCurrentTenant`、`MutableTimeProvider`、一个雪花生成器；`CreateUserStore()` 每次返回**新的**存储实例，代表一个新的请求作用域。

| 文件 | 覆盖 |
| --- | --- |
| `AuthUserEntityTests` | 表名、唯一索引字段、密码列长度与非空、同租户重复规范化用户名被索引拒绝、跨租户同名允许 |
| `AuthUserMapperTests` | 规范化、全字段往返、`Local` / `Unspecified` 的换算、读回标记 `Utc`、恢复码空列表、附加数据往返为 `JsonElement` |
| `UserStoreReadTests` | 大小写不敏感查找、按标识查找、非数字标识、空白用户名、跨租户与平台隔离、同作用域同实例、`AddUserAsync` 的标识与重复检查、密码哈希原样保存 |
| `UserStoreWriteTests` | `UpdateUserAsync` 不改写密码、不改写其他请求写入的失败计数与锁定、可写入空值、其余字段写入、不存在与非法参数、`UpdatePasswordAsync` 原样写入、数据库侧累加、写入同步到已加载实例、锁定标志、计数不跨租户、不存在用户的计数静默忽略 |
| `AuthenticationFlowTests` | 第 1.4 节成功标准第 1 条的六条流程，使用真实的 `DefaultAuthenticationService`、`PasswordHasher`（迭代次数调低到 1000）、`OtpService`、`JwtTokenService` + `DefaultRefreshTokenStore` |
| `RegistrationTests` | `Replace` 后唯一、实现类型、Scoped、`TimeProvider` 注册且不覆盖已有 |

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

SQLite 临时库的连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

写用例的两条约束：

- 测试里一律写 `Microsoft.Extensions.Options.Options.Create(...)` 全名。第 ② 份会新增命名空间 `XiHan.Framework.Authentication.SqlSugar.Options`，测试命名空间 `XiHan.Framework.Authentication.SqlSugar.Tests` 向上查找简单名 `Options` 时会先命中它，届时写简名的用例全部编译失败
- 时间断言用整秒值（见 5 ⑥）

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 双因素密钥明文落库 | 见 S1。数据库泄漏即泄漏全部 TOTP 密钥 |
| `UpdateUserAsync` 不写密码、失败计数与锁定 | 与 `DefaultUserStore` 不同。这四列只能经各自的专用方法修改 |
| 用户名大小写不敏感 | 与 `DefaultUserStore` 不同。旧数据若有仅大小写不同的重名，唯一索引建不起来 |
| 身份映射不刷新 | 同一请求作用域内，除失败计数与锁定三个字段外，已读出的用户对象不会反映其他请求在此期间对库的修改 |
| 非数字 `UserId` | 视为不存在。下游若以非数字字符串作用户标识，不能用本包 |
| 自动建表默认关闭 | `XiHan:Data:SqlSugarCore` 下 `EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`，不开启则首次读写即报表不存在 |
| `OptIn` 建表模式 | 本包实体不带 `[TableInitialization]`，`TableInitialization.Mode = OptIn` 时不会自动建表，需自行建表 |
| 附加数据类型 | `AdditionalData` 的值往返后为 `JsonElement` |
| 与下游自有实现并存 | 下游若也 `Replace` 了 `IUserStore`（如 BasicApp 的 `SaasUserStore`），以模块装配顺序靠后者为准 |
| 取消令牌 | 只在访问数据库前检查，已发出的 SQL 不会被取消 |
| 契约外 API | 仅提供 `AddUserAsync`，没有删除与列举 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release` 全绿
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败与本包无关）
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `authentication-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：本份不碰 `docs/` 与模块清单

## 9. 下一份

第 ② 份（`.superpowers/specs/2026-09-28-authentication-sqlsugar-2-refresh-token-external-login-design.md`）：`IRefreshTokenStore` 的 SqlSugar 实现（只存哈希、撤销标记、重用检测、过期清理）、`IExternalLoginStore` 的 SqlSugar 实现、配置节 `XiHan:Authentication:SqlSugar`，以及文档站条目、侧边栏、四份 README 的模块清单与各处模块计数。两份完成后方可提交 PR。
