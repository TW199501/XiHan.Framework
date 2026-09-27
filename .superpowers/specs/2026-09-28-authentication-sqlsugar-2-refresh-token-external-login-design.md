# Authentication.SqlSugar ②：刷新令牌、第三方登录与文档收尾 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-authentication-sqlsugar-2-refresh-token-external-login.md`
- **前置**：第 ① 份（`.superpowers/specs/2026-09-28-authentication-sqlsugar-1-user-store-design.md`）必须已完成——本份沿用它的包骨架、`StorageTime`、测试夹具与替身
- **所属 PR**：`Authentication.SqlSugar` 的唯一一个 PR，与第 ① 份同一个；本份完成后方可提交
- **Linear 议题**：`https://linear.app/elf-express/issue/EDDIE-9`
- **系列**：`Authentication.SqlSugar` 共 2 份。① 包骨架 + `IUserStore`；② `IRefreshTokenStore` + `IExternalLoginStore` + 文档站收尾

> 本文档**自成一体**。实现第 ② 份所需的全部约束都写在这里，不引用其他设计文档。与第 ① 份共用的约定在两份里各写一遍——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的地方是重用检测的级联撤销。** 下游应用的刷新接口在 `RefreshAccessToken` 返回 `null` 时抛业务异常，工作单元随之**回滚**——若级联撤销写在调用方的事务里，它会跟着被回滚：检测到了令牌被盗，却什么都没撤销，且没有任何报错。SQLite 单测默认没有事务，**照样全绿**。第 4.4 节规定级联撤销走独立连接，第 5 节第 ① 条详述，第 6 节有一条专门的用例。
>
> 第二个陷阱是**生命周期**：`IRefreshTokenStore` 被单例 `JwtTokenService` 捕获，只能注册为 Singleton，而数据库客户端解析器是 Scoped——不能构造函数注入，必须每次调用自建作用域。

---

## 1. 背景与目标

### 1.1 现状

**刷新令牌**：

- 契约 `IRefreshTokenStore`（`framework/src/XiHan.Framework.Authentication/Jwt/IRefreshTokenStore.cs:9-32`）只有三个**同步**方法：`void Save(string refreshToken, string? subject, DateTime expiresAt)`、`bool Validate(string refreshToken, string? subject = null)`、`void Remove(string refreshToken)`。没有家族标识、没有原子「校验并消费」、没有按主体撤销、没有租户
- 默认实现 `DefaultRefreshTokenStore`（`Jwt/DefaultRefreshTokenStore.cs`）是进程内 `ConcurrentDictionary`，**以令牌明文为键**；每 256 次保存清理一次过期项，满 10 万条抛异常；`Remove` 直接删项；`Save` 对同一令牌覆盖；过期时间已过的 `Save` 等同删除
- 注册 `services.TryAddSingleton<IRefreshTokenStore, DefaultRefreshTokenStore>()`（`Extensions/DependencyInjection/XiHanAuthenticationServiceCollectionExtensions.cs:35`）

**第三方登录**：

- 契约 `IExternalLoginStore`（`OAuth/IExternalLoginStore.cs:9-37`）：`Task<long?> FindUserIdAsync(string provider, string providerKey, long? tenantId = null, CancellationToken)`、`Task CreateAsync(long userId, ExternalLoginInfo info, long? tenantId = null, CancellationToken)`、`Task RemoveAsync(long userId, string provider, CancellationToken)`
- 默认实现 `DefaultExternalLoginStore`（`OAuth/DefaultExternalLoginStore.cs`）：键为 `$"{provider}:{providerKey}:{tenantId ?? 0}"`，**`tenantId` 为空时落到 0**；`CreateAsync` 对同键**静默覆盖**（可把一个第三方账号从 A 用户改绑到 B 用户）；`RemoveAsync` 按 `provider:` 前缀、忽略大小写删除该用户的全部绑定，**不看租户**
- 注册 `services.TryAddScoped<IExternalLoginStore, DefaultExternalLoginStore>()`，**仅在 `XiHan:Authentication:OAuth:Enabled` 为真且配置了提供商时**（`OAuth/XiHanOAuthServiceCollectionExtensions.cs:33-38`）

### 1.2 调用方

**刷新令牌**的唯一调用方是 `JwtTokenService`（`Jwt/JwtTokenService.cs`，注册为 Singleton，`XiHanAuthenticationServiceCollectionExtensions.cs:37`，构造函数注入 `IRefreshTokenStore`，28-33 行）：

| 流程 | 行号 | 对存储的调用 |
| --- | --- | --- |
| 签发 `GenerateAccessToken` | 40-77 | 生成 64 字节随机数的 Base64 刷新令牌（83-88）→ `Save(refreshToken, subject, UtcNow + RefreshTokenExpirationDays)`（65） |
| 刷新 `RefreshAccessToken` | 193-225 | 用放过有效期的参数校验旧访问令牌，取出主体 → `Validate(refreshToken, subject)`（210）→ 失败返回 `null` → 成功则 `GenerateAccessToken`（内部 `Save` 新令牌）→ `Remove(旧令牌)`（217）。**整个方法 `catch` 一切异常返回 `null`**（221-224） |

主体取自声明 `sub` / `NameIdentifier` / `XiHanClaimTypes.UserId`（253-259），可能为 `null`。

从中读出的事实：

1. **现行语义是一次性使用 + 轮换**：每次刷新都签发新令牌、作废旧令牌；新令牌的有效期从刷新时刻重新计 7 天（默认），所以只要持续刷新，会话没有绝对上限
2. **`Validate` 与 `Remove` 之间不是原子的**：两个请求同时拿同一个令牌刷新，都可能在对方 `Remove` 之前 `Validate` 成功。`Remove` 是整个流程的最后一步且在 `try` 之内，它抛出会让该请求得到 `null`——存储层可以借此收窄竞态（见 R8）
3. **新旧令牌之间没有任何关联**：`Save` 不接收「由哪个令牌轮换而来」，存储层无从建立令牌家族
4. **调用都是同步的**，走在请求线程上
5. **`Validate` 的失败对调用方只是一个 `false`**，存储层抛异常也会被 `RefreshAccessToken` 吞成 `null`

下游 `XiHan.BasicApp`（本仓库外）的 `AuthTokenIssueService.RefreshAccessToken` 直接转调 `JwtTokenService.RefreshAccessToken`，结果为 `null` 时抛业务异常；应用服务运行在事务型工作单元内，**异常导致事务回滚**。它自己的 `SaasRefreshTokenStore` 用分布式缓存、以 SHA-256 哈希作键——只存哈希的做法在下游已有先例。BasicApp 已以 `Replace` 换掉本份涉及的两个存储（`SaasRefreshTokenStore`、`SaasExternalLoginStore`，见其 `Extensions/ServiceCollectionExtensions.cs` 的 `AddSaasAuthStores`），因此本包不是它的直接依赖；它的刷新令牌存储没有撤销标记，不做重用检测。

**第三方登录**在框架内**没有调用方**（全仓库检索 `IExternalLoginStore` 只有契约、默认实现与注册三处）。语义只能从契约注释、默认实现与下游 `SaasExternalLoginStore` 推断：下游在 `tenantId` 为空时回退到**当前租户**（而非 0），软删除，`Provider + ProviderKey + TenantId` 唯一。

### 1.3 要交付什么

本份交付：

1. 配置 `XiHanAuthenticationSqlSugarOptions`（配置节 `XiHan:Authentication:SqlSugar`）
2. 实体 `SysAuthRefreshToken`（表 `sys_auth_refresh_token`）、`RefreshTokenHasher`、`SqlSugarRefreshTokenStore : IRefreshTokenStore`
3. 实体 `SysAuthExternalLogin`（表 `sys_auth_external_login`）、`SqlSugarExternalLoginStore : IExternalLoginStore`
4. 注册扩展补齐：配置绑定 + 两个 `Replace`
5. 包 README 整份重写
6. 文档站条目 `docs/packages/authentication-sqlsugar.md`、侧边栏、`docs/packages/index.md`、`framework/README.md` 与 `framework/README_cn.md` 的模块清单，以及八个文件里的模块计数

### 1.4 成功标准

1. 数据库里不存在任何刷新令牌明文，只有其 SHA-256 哈希
2. 经 `JwtTokenService` 刷新一次后，旧令牌不能再次刷新
3. 已被轮换掉的令牌再次出现时，同一租户下同一主体的全部未撤销令牌被撤销——**即使调用方的事务随后回滚**
4. 宽限期内的重复使用只拒绝、不级联；关闭重用检测时只拒绝、不级联
5. 已撤销但未过期的令牌行在清理后仍然保留；已过期的行被清理
6. 第三方登录：`tenantId` 为空时按当前租户查找与写入；同一第三方账号不能被改绑到另一个用户；`ProviderKey` 区分大小写
7. `IRefreshTokenStore` 注册为 Singleton、`IExternalLoginStore` 注册为 Scoped，在 `ValidateScopes = true` 的容器里能解析
8. 文档站与模块清单七处齐全，模块计数加一
9. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约、默认实现与调用方 —— 最高优先**

```
framework/src/XiHan.Framework.Authentication/
  Jwt/IRefreshTokenStore.cs              三个同步方法
  Jwt/DefaultRefreshTokenStore.cs        内存语义、256 次清理一次
  Jwt/JwtTokenService.cs                 唯一调用方，第 1.2 节的表格按它整理
  OAuth/IExternalLoginStore.cs
  OAuth/DefaultExternalLoginStore.cs
  OAuth/ExternalLoginInfo.cs             Provider / ProviderKey / DisplayName / Email / AvatarUrl
  OAuth/XiHanOAuthServiceCollectionExtensions.cs:33-38   条件注册
  Extensions/DependencyInjection/XiHanAuthenticationServiceCollectionExtensions.cs:35-37
```

**② 第 ① 份的产出**

```
framework/src/XiHan.Framework.Authentication.SqlSugar/
  Mapping/StorageTime.cs                 UTC 换算
  Users/SqlSugarUserStore.cs             取客户端、租户、雪花主键的写法
  Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs   要补齐的注册
framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/
  AuthenticationTestContext.cs           要扩展的夹具
  Fakes/                                 三个替身
```

**③ 数据层与工作单元**

```
framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs
framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs:256-300   EnlistCurrentUnitOfWork：事务型工作单元内返回钉住连接；requiresNew 时用 CopyNew 另开连接
framework/src/XiHan.Framework.Data/Extensions/DependencyInjection/XiHanDataServiceCollectionExtensions.cs:61   解析器注册为 Scoped
framework/src/XiHan.Framework.Uow/AmbientUnitOfWork.cs   当前工作单元存于 AsyncLocal，新建的依赖注入作用域同样看得见
```

**④ SqlSugar 源码 —— API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/ISqlSugarClient.cs:54                 SqlSugarClient CopyNew()
  Abstract/SugarProvider/SqlSugarProvider.cs:1868 CopyNew：按当前连接配置新建客户端（新连接）
  Abstract/SugarProvider/SqlSugarScopeProvider.cs:829
  Abstract/UpdateProvider/UpdateableProvider.cs   SetColumns、Where、ExecuteCommand
  Interface/IDeleteable.cs                        Where、ExecuteCommand
  Interface/IQueryable.cs                         First、ToListAsync
```

> 树与目录名：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`，权威源码是 `Src/Asp.NetCore2/`，不是 `Src/Asp.Net/`。更新提供者的目录名是 `UpdateProvider`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：

| 任务 | 必读 |
| --- | --- |
| 条件更新 | `更新數據.md` |
| 条件删除 | `刪除數據.md` |
| 事务与独立连接 | `事务用法.md`、`UnitOfWork工作單元.md`、`偶發性錯誤與執行緒安全.md` |

仓库自身文档：`docs/packages/authentication.md`、`docs/packages/auditing-sqlsugar.md`（包文档写法）、`docs/packages/eventbus-sqlsugar.md`、`docs/.vitepress/config.ts`。

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

- **不存刷新令牌明文**，任何列、任何日志都不出现令牌原文
- **不改 `IRefreshTokenStore`、`IExternalLoginStore`、`JwtTokenService`**。契约缺的能力写进已知边界，不偷偷扩展
- **`Remove` 不删行**，只标记撤销
- **级联撤销不走调用方的事务**
- **不在构造函数注入 `ISqlSugarClientResolver` / `ICurrentTenant`** 到刷新令牌存储
- **不把 `ProviderKey` 规范化**，也不截断它；也不截断 `Subject`
- **`CreateAsync` 不覆盖已有绑定**
- **不做 `IUserStore` 的任何改动**（第 ① 份已完成）

## 3. 非目标

- **不做真正的令牌家族**：契约不提供新旧令牌的关联，本份以「同一租户、同一主体」近似家族（见 R3）
- **不改 `Validate` 为消费型**：契约只有非消费的 `Validate`；并发刷新改由 `Remove` 的条件更新收窄（见 R8）
- **不做会话绝对有效期**：存储层不知道一个会话最初何时登录
- **不提供「撤销某用户全部令牌」的公开 API**：契约没有，本份不扩展
- **不做后台定时清理**：清理在保存时顺带进行
- **第三方登录不做软删除、不记录最近登录时间**
- **不改 `docs/packages/authentication.md` 与 `docs/changelog.md`**

## 五个共同问题

| 问题 | 刷新令牌 | 第三方登录 |
| --- | --- | --- |
| **分表与否** | **不分表**。行数由「活跃会话数 × 有效期」决定，过期行被清理，规模有上界 | **不分表**。绑定是长期主数据 |
| **主键类型** | **雪花 `long`**。契约没有令牌标识，令牌本身（的哈希）是业务键，另建唯一索引 `Token_Hash` | **雪花 `long`**。契约用 `long userId` 指向用户，与第 ① 份的用户主键一致 |
| **是否参与工作单元事务** | **保存、校验、移除参与**（经解析器取客户端，事务型工作单元内自动登记）；**级联撤销与过期清理不参与**，走 `CopyNew()` 的独立连接 | **参与**：注册为 Scoped，经 `GetClientForEntity<SysAuthExternalLogin>()` 取客户端 |
| **是否需要多库** | **单库**：跟随当前租户布局的主库 | **单库**：同左 |
| **顶替方式** | **`services.Replace(ServiceDescriptor.Singleton<IRefreshTokenStore, SqlSugarRefreshTokenStore>())`**。主包 `TryAddSingleton`；生命周期必须是 Singleton，因为 `JwtTokenService` 是单例并在构造函数里捕获它 | **`services.Replace(ServiceDescriptor.Scoped<IExternalLoginStore, SqlSugarExternalLoginStore>())`**。主包只在启用 OAuth 时 `TryAddScoped`；`Replace` 在未注册时等同新增，因此本包无论 OAuth 是否启用都会注册 |

## 待确认的决策

**安全相关的排在前面。** R 开头是刷新令牌，E 开头是第三方登录，O 开头是其余。

| # | 决策 | 默认值 | 理由 | 若想改会影响什么 |
| --- | --- | --- | --- | --- |
| R1 | 刷新令牌怎么存 | **只存 SHA-256 哈希**：`Convert.ToHexString(SHA256.HashData(UTF8(token)))`，64 位大写十六进制，列 `Token_Hash` 唯一索引；不加盐、不加 pepper | 契约的三个方法都传入令牌原文，存储层能先哈希再按哈希查——**契约完全允许，没有缺口**。数据库泄漏时拿到的哈希不能直接用于刷新。`JwtTokenService` 生成的令牌是 512 位随机数，对它做 SHA-256 不存在字典攻击或彩虹表的可能，加盐会让「按哈希查找」无法走索引，pepper 只在令牌低熵时才有意义 | 若下游用 `Save` 存入自己生成的低熵令牌（契约允许任意字符串），哈希对它保护不足；改为 HMAC 需要引入密钥管理 |
| R2 | 轮换语义 | **一次性使用**：沿用 `JwtTokenService` 现行的「校验 → 签发新令牌 → 移除旧令牌」。存储层不做滑动过期，也不改变新令牌的有效期 | 这是调用方已有的行为（第 1.2 节）；存储层只负责让「移除」真正生效并可追溯 | 会话没有绝对上限——只要在有效期内持续刷新就永不过期。要设上限需要契约提供「会话起始时间」或家族标识 |
| R3 | 重用检测 | **开启**。已撤销的令牌再次被 `Validate` 时：返回 `false`，并把**同一 `Tenant_Id`、同一 `Subject`** 下全部未撤销的令牌标记撤销。配置 `RefreshTokenReuseDetection` 可关 | 令牌被盗后，攻击者与合法用户迟早有一方会拿着已被对方轮换掉的令牌来刷新——这是唯一能发现盗用的信号。**契约没有家族标识（缺口）**，以主体近似家族：撤销范围比真正的家族宽，用户**所有设备**都会下线 | 近似带来一个拒绝服务面：持有该用户任一已撤销令牌的人，在该令牌自然过期前可以反复让该用户的全部会话下线。消除它需要契约扩展（例如 `Save` 多一个「前序令牌」参数，或新增家族存储接口）。关掉重用检测则回到「盗用者先刷新就能一直用下去」 |
| R4 | 宽限期 | **`RefreshTokenReuseGracePeriod = 0`**：任何重复使用都级联 | 安全的默认值。宽限期内的重复使用**依然被拒绝**，宽限期只决定是否级联 | 单页应用多标签页同时刷新会触发级联、全部下线；前端应对刷新请求加互斥。下游可设为数秒以换取体验 |
| R5 | 撤销方式 | **标记**：`Remove` 把 `Revoked_Time` 设为当前时间（只更新尚未撤销的行），不删行 | 重用检测需要记得「这个令牌曾经有效、已被撤销」；删行后它与「从未存在的令牌」无法区分 | 删行就无法做重用检测 |
| R6 | 过期记录谁来清 | **保存时顺带清理**：每保存 `RefreshTokenCleanupFrequency`（默认 256，与 `DefaultRefreshTokenStore` 相同）次，删除 `Expires_At < 当前时间` 的行，含已撤销的。已撤销但未过期的行保留。配置 ≤ 0 时不清理 | 不引入后台服务与 `Tasks` 依赖。过期的令牌无论是否被撤销都会因过期被拒，重用检测对它已无意义 | 多实例各自计数，清理时刻不同步，属预期；低流量应用清理间隔会很长，可调小频率 |
| R7 | 级联撤销与清理的事务归属 | **走独立连接**（`client.CopyNew()`，用后释放），自动提交；清理放在插入新令牌**之前** | 下游刷新失败时业务异常会让工作单元回滚，级联撤销若在同一事务里就会被一并回滚（第 5 节 ①）。清理放在插入之前，避免独立连接的范围删除去等待本事务刚插入的新行上的锁 | 若调用方事务在此之前已经写过同一主体的令牌行（框架自身的流程不会），级联撤销会等待这些行锁直至超时，失败只记日志 |
| R8 | 校验是否消费令牌 | **不消费**，`Validate` 是只读（级联撤销除外） | 契约叫「校验」；若让它消费，任何只想检查令牌是否有效的调用都会把令牌烧掉，下一次正常刷新即被判为重用、全部下线 | — |
| R8b | 并发刷新（第 1.2 节第 2 条）如何处理 | **收窄：`Remove` 条件更新影响 0 行、且该令牌存在并已撤销时抛 `InvalidOperationException`**。另一选项是保持静默（影响 0 行也不报错），两个并发请求各得一个新令牌 | `Remove` 本来就是 `WHERE Token_Hash = @h AND Revoked_Time IS NULL` 的条件更新，并发的两次里只有一次命中。落败方抛出后被 `JwtTokenService.RefreshAccessToken` 的 `catch` 吞成 `null`；在事务型工作单元里，它刚插入的新令牌随回滚消失。**核查了 `Remove` 的全部调用方**：框架内只有 `JwtTokenService.RefreshAccessToken`（217 行，位于 `try` 内）；下游 BasicApp 整个替换了刷新令牌存储，其代码里没有直接调用 `IRefreshTokenStore.Remove`；框架没有登出接口调用它。因此抛出只会以「刷新失败」的形式出现。受影响行数的口径不影响判断：命中时 `Revoked_Time` 由空变为非空，MySQL 两种口径下都是 1 | **代价**：对同一令牌第二次调用 `Remove`（例如下游自己写的登出被调用两次）会抛出，调用方须自行处理。不在事务里时，落败方已插入的新令牌成为无人持有的孤行，至过期被清理。选静默则回到「并发刷新各得一个新令牌」，此时只能靠契约扩展一个原子的「校验并消费」来消除 |
| R9 | 主体绑定 | **沿用契约**：传入的 `subject` 为空白时不做绑定校验；非空时与存储的主体序数比较 | 契约把 `subject` 定义为可选参数 | 更严格的做法（令牌带主体时要求必须传入并匹配）会改变契约语义；`JwtTokenService` 在访问令牌缺主体声明时传 `null` |
| R10 | 重复保存同一令牌 | **抛出**（唯一索引冲突），**不覆盖** | 覆盖会把一个已撤销的令牌「复活」。`JwtTokenService` 每次生成新随机数，不会重复保存 | 与 `DefaultRefreshTokenStore` 不同。自行调用 `Save` 的下游代码若依赖覆盖会失败 |
| R11 | 过期时间已过的保存 | **等同撤销**（走 `Revoke`，影响 0 行不抛），不插入 | 与 `DefaultRefreshTokenStore` 一致 | — |
| R12 | 租户 | 保存时记录当前租户到 `Tenant_Id`（无租户为 0），**只用于限定级联撤销的范围**；`Validate` 按哈希查找，不加租户条件 | 哈希全局唯一；刷新请求不一定能解析出与登录时相同的租户上下文。限定级联范围可避免不同租户里同名主体互相牵连 | 表本身随当前租户布局落库：独立库的租户，刷新请求必须解析到同一租户，否则在另一个库里找不到令牌（拒绝，不会误放行）。共享库时，在租户 A 签发、在租户 B 的上下文里刷新的令牌，新令牌记在 B 名下，级联范围随之漂移 |
| R13 | 生命周期与客户端获取 | **Singleton**，构造函数注入 `IServiceScopeFactory`，每次调用 `CreateScope()` 解析 `ISqlSugarClientResolver` 与 `ICurrentTenant` | `JwtTokenService` 是单例；解析器是 Scoped，直接注入会形成被捕获的作用域依赖（开发环境 `ValidateScopes` 下启动即抛）。当前工作单元存于 `AsyncLocal`，新作用域里的解析器仍能登记到调用方的事务 | — |
| R14 | 同步数据库访问 | **同步**（`First()` / `ExecuteCommand()`） | 契约是同步的 | 刷新与登录在请求线程上同步等待数据库；高并发下占用线程池线程 |
| R15 | 重用事件的日志 | 级联撤销时记 `Warning`，含租户与主体、撤销条数；**不记令牌或哈希** | 安全事件需可追溯 | — |
| E1 | `tenantId` 为空时 | **回退到当前租户**（`ICurrentTenant.Id ?? 0`），显式传入时以传入值为准 | `DefaultExternalLoginStore` 回退到 0：在租户上下文里调用而忘了传 `tenantId`，会查到**平台**的绑定，返回平台用户的标识——下游据此登录即是越权。下游 BasicApp 也是回退到当前租户 | 与 `DefaultExternalLoginStore` 不同 |
| E2 | 重复绑定 | 同一 `(租户, 提供商, 提供商用户标识)` 已绑定**同一用户**时幂等返回；已绑定**其他用户**时抛 `InvalidOperationException` | 默认实现的静默覆盖会把第三方账号从 A 改绑到 B，A 失去这个登录方式且不留痕迹 | 与 `DefaultExternalLoginStore` 不同。需要改绑的业务应先 `RemoveAsync` 再 `CreateAsync` |
| E3 | `ProviderKey` 的大小写 | **区分大小写**，原样存储；查询后在内存里再做一次序数比较 | 提供商用户标识是外部系统的不透明标识（如微信 openid），大小写有意义。MySQL / SQL Server 默认排序规则下 `=` 不区分大小写，仅靠 SQL 会让两个只差大小写的标识命中同一绑定 | 在不区分大小写的排序规则下，两个只差大小写的标识无法同时绑定（唯一索引冲突，失败而非误绑） |
| E4 | `Provider` 的大小写 | **规范化为小写**（`ToLowerInvariant()`）后存储与查询 | 默认实现的 `RemoveAsync` 已按忽略大小写处理；提供商名来自配置，大小写不应产生两份绑定 | — |
| E5 | `RemoveAsync` 的租户 | **不加租户条件**，删除该用户在该提供商下的全部绑定 | 契约没有租户参数，沿用默认实现；用户标识是全局唯一的雪花值 | 下游若用非全局唯一的用户标识，会删到其他租户同号用户的绑定 |
| E6 | 删除方式 | **物理删除** | 软删除会与唯一索引冲突，解绑后无法再绑 | 不留解绑记录 |
| E7 | 注册时机 | **无论 OAuth 是否启用都注册** | 模块类只做装配；存储本身不对外暴露任何端点，不属于需要 fail-closed 门控的能力 | 未启用 OAuth 的应用也能解析到 `IExternalLoginStore` |
| E8 | 展示性字段超长 | `DisplayName`、`Email` 截断到 256，`AvatarUrl` 截断到 2048 | 这些字段来自第三方、长度不受控；超长导致插入失败会让用户无法完成第三方登录 | 截断的是展示信息，不影响身份判定 |
| E9 | `userId` 非正数 | `CreateAsync` 抛 `ArgumentOutOfRangeException` | 0 与负数不是有效的雪花标识 | — |
| O1 | 配置 | `XiHanAuthenticationSqlSugarOptions`，配置节 `XiHan:Authentication:SqlSugar`：`RefreshTokenReuseDetection`（`true`）、`RefreshTokenReuseGracePeriod`（`00:00:00`）、`RefreshTokenCleanupFrequency`（`256`） | — | — |
| O2 | 表名 | `sys_auth_refresh_token`、`sys_auth_external_login` | 与第 ① 份的 `sys_auth_user` 同前缀；下游 BasicApp 已有 `Sys_External_Login` | — |
| O3 | 取消令牌 | 第三方登录只在访问数据库前检查，不传给 SqlSugar；刷新令牌契约没有取消令牌 | 与第 ① 份一致 | — |

## 4. 设计

### 4.1 包结构（本份新增与修改）

```
framework/src/XiHan.Framework.Authentication.SqlSugar/
  Options/XiHanAuthenticationSqlSugarOptions.cs                新增
  Entities/SysAuthRefreshToken.cs                              新增
  Entities/SysAuthExternalLogin.cs                             新增
  RefreshTokens/RefreshTokenHasher.cs                          新增
  RefreshTokens/SqlSugarRefreshTokenStore.cs                   新增
  ExternalLogins/SqlSugarExternalLoginStore.cs                 新增
  Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs   修改
  XiHanAuthenticationSqlSugarModule.cs                         修改（XML 注释）
  README.md                                                    重写
```

目录名用 `RefreshTokens/`、`ExternalLogins/`，不用 `Jwt/`、`OAuth/`，避免与主包命名空间混淆。

### 4.2 实体

**`SysAuthRefreshToken`**（表 `sys_auth_refresh_token`）：

```csharp
[SugarTable("sys_auth_refresh_token")]
[SugarIndex("ux_{table}_token_hash", nameof(TokenHash), OrderByType.Asc, true)]
[SugarIndex("ix_{table}_tenant_subject", nameof(TenantId), OrderByType.Asc, nameof(Subject), OrderByType.Asc)]
[SugarIndex("ix_{table}_expires_at", nameof(ExpiresAt), OrderByType.Asc)]
public class SysAuthRefreshToken : SugarEntity<long>
```

| 属性 | 列名 | 类型 / 长度 | 可空 | 说明 |
| --- | --- | --- | --- | --- |
| `TenantId` | `Tenant_Id` | `long` | 否 | 签发时的租户，0 为平台 |
| `TokenHash` | `Token_Hash` | 64 | 否 | SHA-256 大写十六进制 |
| `Subject` | `Subject` | 256 | 是 | 主体，通常为用户标识 |
| `ExpiresAt` | `Expires_At` | `DateTime` | 否 | UTC |
| `CreatedTime` | `Created_Time` | `DateTime` | 否 | UTC |
| `RevokedTime` | `Revoked_Time` | `DateTime` | 是 | UTC，为空表示未撤销 |

**`SysAuthExternalLogin`**（表 `sys_auth_external_login`）：

```csharp
[SugarTable("sys_auth_external_login")]
[SugarIndex("ux_{table}_tenant_provider_key", nameof(TenantId), OrderByType.Asc, nameof(Provider), OrderByType.Asc, nameof(ProviderKey), OrderByType.Asc, true)]
[SugarIndex("ix_{table}_user_provider", nameof(UserId), OrderByType.Asc, nameof(Provider), OrderByType.Asc)]
public class SysAuthExternalLogin : SugarEntity<long>
```

| 属性 | 列名 | 类型 / 长度 | 可空 | 说明 |
| --- | --- | --- | --- | --- |
| `TenantId` | `Tenant_Id` | `long` | 否 | |
| `UserId` | `User_Id` | `long` | 否 | 内部用户标识 |
| `Provider` | `Provider` | 64 | 否 | 小写 |
| `ProviderKey` | `Provider_Key` | 256 | 否 | 原样 |
| `DisplayName` | `Display_Name` | 256 | 是 | 截断 |
| `Email` | `Email` | 256 | 是 | 截断 |
| `AvatarUrl` | `Avatar_Url` | 2048 | 是 | 截断 |
| `CreatedTime` | `Created_Time` | `DateTime` | 否 | UTC |

索引长度：MySQL `utf8mb4` 下 `(Tenant_Id, Provider, Provider_Key)` 为 8 + (64 + 256) × 4 = 1288 字节，低于 InnoDB 3072 字节上限。

### 4.3 `RefreshTokenHasher`

公开静态类，`string Hash(string refreshToken)` 返回 `Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)))`。`"abc"` 的结果是 `BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD`（标准测试向量）。

### 4.4 `SqlSugarRefreshTokenStore`

构造函数：`(IServiceScopeFactory scopeFactory, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider, IOptions<XiHanAuthenticationSqlSugarOptions> options, ILogger<SqlSugarRefreshTokenStore> logger)`。

每个方法 `using var scope = _scopeFactory.CreateScope()`，从中解析 `ISqlSugarClientResolver` 取 `GetClientForEntity<SysAuthRefreshToken>()`，解析 `ICurrentTenant`（`GetService`，可缺省，缺省视为 0）。

**`Save(token, subject, expiresAt)`**：

```
token 空白 → 返回
now = 当前 UTC；expiresAtUtc = StorageTime.ToUtc(expiresAt)
expiresAtUtc <= now → Remove(token)，返回
CleanupIfDue(client, now)                         // 独立连接，插入之前
INSERT (雪花标识, 租户, Hash(token), subject, expiresAtUtc, now, Revoked_Time = NULL)
```

**`Validate(token, subject)`**：

```
token 空白 → false
entry = SELECT WHERE Token_Hash = Hash(token)
entry 为空 → false
entry.ExpiresAt <= now → false
entry.RevokedTime 非空 → HandleReuse(client, entry, now)，返回 false
subject 非空白 且 entry.Subject 与之序数不等 → false
→ true
```

判定顺序有意义：先看过期（过期令牌的重复使用不级联），再看撤销，最后看主体（被撤销的令牌无论配谁的访问令牌出现都算重用）。

**`Remove(token)`**：`UPDATE SET Revoked_Time = now WHERE Token_Hash = @h AND Revoked_Time IS NULL`（私有方法 `Revoke`）。影响 1 行即返回；影响 0 行时再查该哈希是否存在且已撤销——是则抛 `InvalidOperationException`，否则（令牌不存在）静默返回。`Save` 在过期时间已过时直接调 `Revoke`，不抛。

**`HandleReuse(client, entry, now)`**：

```
未开启重用检测 → 返回
宽限期 > 0 且 (now - entry.RevokedTime) < 宽限期 → 返回
entry.Subject 空白 → 记 Warning，返回
在 client.CopyNew() 上执行：
  UPDATE SET Revoked_Time = now
  WHERE Tenant_Id = entry.TenantId AND Subject = entry.Subject AND Revoked_Time IS NULL
记 Warning（租户、主体、条数）
异常：记 Error，不抛
```

宽限期写成「> 0 且 小于」，使宽限期为 0 时无论各实例时钟如何偏差都会级联。

**`CleanupIfDue(client, now)`**：频率 ≤ 0 时返回；`Interlocked.Increment(ref _saveCount) % 频率 != 0` 时返回；否则在 `client.CopyNew()` 上 `DELETE WHERE Expires_At < now`，异常记 Error 不抛。

### 4.5 `SqlSugarExternalLoginStore`

构造函数：`(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider)`。

| 方法 | 行为 |
| --- | --- |
| `FindUserIdAsync` | 提供商或提供商用户标识空白 → `null`；租户 = `tenantId ?? 当前租户 ?? 0`；`SELECT WHERE Tenant_Id AND Provider = 小写 AND Provider_Key = @k` 取列表，内存里按序数比较 `ProviderKey` 取第一条 |
| `CreateAsync` | `info` 为空 → `ArgumentNullException`；`userId <= 0` → `ArgumentOutOfRangeException`；提供商或提供商用户标识空白 → `ArgumentException`；按 `FindUserIdAsync` 的规则查已有绑定：同一用户 → 返回，其他用户 → `InvalidOperationException`；否则插入 |
| `RemoveAsync` | 提供商空白 → `ArgumentException`；`DELETE WHERE User_Id = @u AND Provider = 小写` |

### 4.6 注册

```csharp
services.Configure<XiHanAuthenticationSqlSugarOptions>(configuration.GetSection(XiHanAuthenticationSqlSugarOptions.SectionName));
services.TryAddSingleton<TimeProvider>(TimeProvider.System);
services.Replace(ServiceDescriptor.Scoped<IUserStore, SqlSugarUserStore>());
services.Replace(ServiceDescriptor.Singleton<IRefreshTokenStore, SqlSugarRefreshTokenStore>());
services.Replace(ServiceDescriptor.Scoped<IExternalLoginStore, SqlSugarExternalLoginStore>());
```

### 4.7 文档与模块清单（新包必须覆盖的七处）

| # | 位置 | 本份的动作 |
| --- | --- | --- |
| 1 | `framework/src/XiHan.Framework.Authentication.SqlSugar/` + csproj | 第 ① 份已建 |
| 2 | 模块类 + `Extensions/DependencyInjection/` | 第 ① 份已建，本份补齐注册 |
| 3 | 包 `README.md` 七段结构 | 本份整份重写 |
| 4 | `framework/XiHan.Framework.slnx` 的 `/1.src/6.Infrastructure/` 与 `/2.tests/1.UnitTests/` | 第 ① 份已注册 |
| 5 | `docs/packages/authentication-sqlsugar.md` + `docs/.vitepress/config.ts` 侧边栏（「安全 · 认证 · 授权」组，`Authentication 认证` 之后） | 本份新增 |
| 6 | `README.md`、`README_cn.md`、`framework/README.md`、`framework/README_cn.md` | 本份：两份 `framework/README*` 的模块清单表加一行；四份 README 与 `docs/**/*.md` 里的模块计数加一（写作时 8 个文件 20 处，含两个 shields.io 徽章 URL `Modules-N-1f6feb`；两份 `framework/README*` 里的单测工程数因本包新增测试项目同样加一）。根 `README.md` / `README_cn.md` 的「常用包」表**不加行** |
| 7 | `docs/packages/index.md` 包总览表 | 本份：「安全 · 认证 · 授权」表加一行 |

模块计数在实现时取当前值加一，**不要照抄本文写作时的数字或行号**——同系列其他包可能先合入。做法：从 `README.md` 徽章读出 N，`grep -rn "N"` 遍历四份 README 与 `docs/**/*.md`，逐条审阅后只改表示模块 / 包 / 包文档页数的命中。

## 5. 会静默失效的陷阱

**① 级联撤销写在调用方的事务里。**

下游刷新失败时抛业务异常，事务型工作单元回滚。级联撤销若经解析器拿到的客户端执行，它就在那个事务里——被一并回滚。结果是：重用被检测到、日志里可能还有一条「已撤销 N 条」，库里一条都没撤。**没有异常、没有失败的测试**：SQLite 单测默认不开事务，级联在任何实现下都「生效」。

本设计用 `client.CopyNew()` 另开连接执行。第 6 节有一条专门的用例：在 WAL 模式的 SQLite 上，让桩解析器返回一个手动 `BEGIN DEFERRED` 的连接，校验后 `ROLLBACK`，再用另一个连接确认级联结果仍在。**不要删这条用例，也不要把它改成不开事务**。

**② 构造函数注入 Scoped 依赖。**

`SqlSugarRefreshTokenStore` 注册为 Singleton；若构造函数注入 `ISqlSugarClientResolver`（Scoped）：生产环境 `ValidateScopes` 默认关闭，于是根容器解析出一个解析器实例被单例永久持有——所有请求共用一个解析器，工作单元与租户上下文错乱，而开发环境（`ValidateScopes` 打开）才会在启动时报错。直接的单元测试（`new` 出来的存储）永远发现不了。注册测试在 `ValidateScopes = true` 的容器里解析一次。

**③ 以为「移除」就是删行。**

删行之后，已撤销的令牌与从未存在的令牌无法区分，重用检测形同虚设——而所有「移除后校验失败」的用例照样通过。用例「移除是标记而非删除」直接查行。

**④ 清理把已撤销的行也删了。**

若清理条件写成 `Revoked_Time IS NOT NULL OR Expires_At < now`，重用检测只在两次清理之间有效，时灵时不灵。清理条件只能是 `Expires_At < now`。用例「清理保留已撤销但未过期的记录」覆盖。

**⑤ 宽限期比较写成小于等于。**

用冻结时钟的测试里，撤销与重用发生在同一刻，耗时为 0。`0 <= 0` 为真，宽限期为 0 时反而不级联——**默认配置下重用检测完全失效**。本设计写成「宽限期 > 0 且 耗时 < 宽限期」。

**⑥ `ProviderKey` 的比较交给数据库排序规则。**

MySQL 默认排序规则下 `'AbC' = 'abc'` 为真，两个只差大小写的第三方标识会命中同一个绑定——另一个人的第三方账号登录进来，拿到的是这个绑定的用户。**SQLite 区分大小写，测试永远发现不了。** 查询后在内存里再按序数比较一次。

**⑦ `tenantId` 为空时落到 0。**

照抄 `DefaultExternalLoginStore` 就是这个行为：租户上下文里忘了传 `tenantId`，查到平台绑定。单租户测试（当前租户本来就是空）发现不了。用例「未指定租户时使用当前租户」在租户上下文里建绑定，再在平台上下文里查不到。

**⑧ 注册写成 `TryAdd`。**

两个契约主包都已注册（`IExternalLoginStore` 在启用 OAuth 时），`TryAdd` 是空操作，表现为「注册成功、无报错、令牌依然在内存里」。

**⑨ 丢弃 `Remove` 条件更新的返回值。**

照「移除即标记」写完、不看受影响行数，所有单线程用例都绿，并发刷新却各得一个新令牌（R8b）。竞态只能用一个在 `Validate` 与 `Remove` 之间插入完整刷新的装饰器存储来确定性复现；计划里的用例就是这样写的，并要求反向核对（去掉 `throw` 后用例必须变红）。

**⑩ 时间。**

`JwtTokenService` 传入的过期时间是 `DateTime.UtcNow` 派生的 `Utc`，但契约允许任意 `Kind`。写入前一律经 `StorageTime.ToUtc`；读回的值不做换算直接与当前 UTC 比较（`DateTime` 比较只看刻度）。**不要用 `DateTimeOffset` 列**，SQLite 读回会按本机时区平移。

## 6. 测试策略

**只有一层：SQLite，CI 强门禁执行。** 事务隔离的用例也在 SQLite 上完成（WAL 模式）。

夹具沿用第 ① 份的 `AuthenticationTestContext`，本份新增：

- `CreateRefreshTokenStore(XiHanAuthenticationSqlSugarOptions? options = null, ISqlSugarClientResolver? resolver = null)`：用一个内部 `ServiceCollection`（注册桩解析器与 `FakeCurrentTenant`）构建作用域工厂，日志用 `NullLogger`
- `CreateExternalLoginStore()`
- `Dispose` 同时释放内部构建的服务容器

| 文件 | 覆盖 |
| --- | --- |
| `RefreshTokenEntityTests` | 表名、三个索引、哈希列长度 64、同一哈希被唯一索引拒绝、哈希标准向量、配置默认值与配置节名 |
| `RefreshTokenStoreTests` | 重复移除抛出、过期时间已过的保存遇到已撤销令牌不抛、保存后校验通过、只存哈希、按实体类型解析客户端、记录租户、主体不符、主体为空跳过绑定、未知令牌、空白令牌、过期、过期时间已过的保存不写入、移除是标记而非删除、移除未知令牌、重复保存抛出、本地时间换算为 UTC |
| `RefreshTokenReuseTests` | 校验未知令牌不撤销任何令牌、级联撤销同一主体、不波及其他主体、不波及其他租户、宽限期内只拒绝、超过宽限期级联、关闭检测只拒绝、主体为空只拒绝、**级联撤销不随调用方事务回滚**、清理删除已过期、清理保留已撤销未过期、频率为 0 不清理 |
| `RefreshTokenRotationTests` | 经真实 `JwtTokenService`：并发刷新同一令牌只有一方成功（竞态装饰器 `Fakes/RacingRefreshTokenStore`）、刷新后旧令牌失效、重复使用旧令牌使新令牌失效、宽限期内重复使用不影响新令牌、库里没有令牌明文、主体不符的访问令牌不能刷新 |
| `ExternalLoginStoreTests` | 绑定后可查、未绑定与空白参数返回空、提供商不区分大小写、提供商用户标识区分大小写、未指定租户用当前租户、显式租户优先、同一用户幂等、改绑被拒、移除范围、非法参数、展示字段截断、按实体类型解析客户端 |
| `RegistrationTests`（追加） | 刷新令牌存储顶替为 Singleton、第三方登录存储顶替为 Scoped、未启用 OAuth 也注册、配置节绑定、`ValidateScopes = true` 的容器能解析刷新令牌存储 |

**事务用例的构造**（第 5 节 ①）：

1. 用夹具的主客户端执行 `PRAGMA journal_mode=WAL;`
2. 另建一个 `IsAutoCloseConnection = false` 的客户端 `transactional`，手动执行 `BEGIN DEFERRED;`
3. 用返回 `transactional` 的桩解析器建一个刷新令牌存储，对已撤销的令牌 `Validate`
4. `transactional` 执行 `ROLLBACK;`
5. 用主客户端的存储校验同一主体的另一个令牌，应为 `false`

WAL 模式下，只读事务不阻塞另一连接的写入；延迟事务在只读期间不持有写锁。若用 `Ado.BeginTran()`，Microsoft.Data.Sqlite 默认开 `BEGIN IMMEDIATE`，持有写锁，独立连接的级联会等满超时后失败——所以必须手动 `BEGIN DEFERRED`。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

SQLite 临时库的连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。夹具已同时删除 `-wal` 与 `-shm` 文件。

写用例的约束：

- 测试里一律写 `Microsoft.Extensions.Options.Options.Create(...)` 全名——本份新增的命名空间 `XiHan.Framework.Authentication.SqlSugar.Options` 会遮蔽简名 `Options`
- 不写「读回的时间等于写入的时间」这类带毫秒以下精度的断言，用整秒时间
- 经 `JwtTokenService` 的用例只断言「这一次刷新成功 / 失败」与「某个令牌经存储校验是否有效」，不连续刷新两级——第二级刷新依赖 `JwtTokenService` 把旧访问令牌的全部声明复制进新令牌的行为，与本包无关

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 无令牌家族（契约缺口） | 重用检测以「同一租户、同一主体」近似家族：用户所有设备一并下线；持有任一已撤销令牌者可在其过期前反复触发 |
| 并发刷新以 `Remove` 收窄 | 落败方得到 `null`；对同一令牌第二次 `Remove` 会抛出；不在事务里时落败方的新令牌成为孤行，至过期被清理 |
| 刷新后租户归属漂移 | 在租户 A 签发、在租户 B 上下文里刷新的令牌，新令牌记在 B 名下，级联范围随之改变 |
| 会话无绝对上限 | 持续刷新即永不过期 |
| 多标签页 | 宽限期为 0 时，多标签页同时刷新会触发级联、全部下线；前端需对刷新加互斥，或调大宽限期 |
| 同步数据库访问 | 刷新令牌三个方法同步访问数据库 |
| 级联与清理走独立连接 | 自动提交，不随业务回滚；调用方事务若已写过同一主体的令牌行，级联会等锁至超时 |
| 清理节奏 | 按保存次数触发，多实例各自计数；低流量时过期行留存较久 |
| 独立库租户 | 刷新请求必须解析到与登录时相同的租户，否则找不到令牌 |
| 重复保存 | 抛唯一索引冲突，不覆盖 |
| 第三方登录租户回退 | `tenantId` 为空时用当前租户，与默认实现不同 |
| 第三方登录不可静默改绑 | 与默认实现不同 |
| `RemoveAsync` 不看租户 | 沿用默认实现 |
| 注册不受 OAuth 开关影响 | 未启用 OAuth 也能解析 `IExternalLoginStore` |
| 自动建表默认关闭 | `XiHan:Data:SqlSugarCore` 下 `EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`；`OptIn` 模式下本包的表不会自动创建 |
| 与下游自有实现并存 | 下游若也 `Replace` 了这些契约（如 BasicApp 的 `SaasRefreshTokenStore` / `SaasExternalLoginStore`），以模块装配顺序靠后者为准 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release` 全绿
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败与本包无关）
- 文档站本地构建通过：`cd docs && pnpm install && pnpm build`
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `authentication-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：只动本包、它的测试项目、第 4.7 节列出的文档与清单

## 9. 下一份

本包到此完成，两份合为一个 PR 提交。按拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 5 节），下一个包是 `Authorization.SqlSugar`。

若要消除第 7 节的两个契约缺口（令牌家族、原子消费），需要先在主包 `XiHan.Framework.Authentication` 扩展 `IRefreshTokenStore` 与 `JwtTokenService`——那是主包的独立 PR，不属本系列。
