# Authorization.SqlSugar ③：策略存储与文档收尾 设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-authorization-sqlsugar-3-policy-store-docs.md`
- **前置**：①（`.superpowers/specs/2026-09-28-authorization-sqlsugar-1-permission-store-design.md`）与 ②（`.superpowers/specs/2026-09-28-authorization-sqlsugar-2-role-store-checker-design.md`）必须已完成
- **所属 PR**：`Authorization.SqlSugar` 单独一个 PR，由 ①②③ 共同组成；**本份完成后方可提 PR**
- **Linear 议题**：<https://linear.app/elf-express/issue/EDDIE-10>
- **系列**：`Authorization.SqlSugar` 共 3 份。① 包骨架 + 权限存储；② 角色存储 + 权限检查器；**③ 策略存储 + 文档收尾**

> 本文档**自成一体**。实现 ③ 所需的全部约束都写在这里，不引用其他设计文档。三份共用的约定（表名、主键、租户、客户端）在每份里各重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份最容易静默出错的一处**：`PolicyDefinition.CustomRequirements` 是 `List<IAuthorizationRequirement>`——带行为的对象，**存不进数据库**。若存储悄悄丢掉它，策略落库后再读出来就少了一条要求，`DefaultPolicyEvaluator` 照样判定通过——**等于绕过授权，而且不报错**。本份对含自定义要求的策略一律拒绝写入。见第 5 节 ①。

---

## 1. 背景与目标

### 1.1 现状

`IPolicyStore`（`framework/src/XiHan.Framework.Authorization/Policies/IPolicyStore.cs`）5 个方法：

```
GetAllPoliciesAsync()
GetPolicyByNameAsync(policyName)
CreatePolicyAsync(policy)
UpdatePolicyAsync(policy)
DeletePolicyAsync(policyName)
```

默认实现 `DefaultPolicyStore` 是内存字典，在 `XiHanAuthorizationServiceCollectionExtensions.cs:36` 以 **`TryAddScoped`** 注册——作用域生命周期 + 实例字典，每个请求都是空的。

`PolicyDefinition`（`Policies/PolicyDefinition.cs`）的字段：

| 字段 | 类型 | 可持久化 |
| --- | --- | --- |
| `Name` | `string`（唯一标识） | 是 |
| `DisplayName` / `Description` | `string` / `string?` | 是 |
| `RequiredRoles` | `List<string>`（任一即可） | 是，JSON |
| `RequiredPermissions` | `List<string>`（全部都要） | 是，JSON |
| `RequiredClaims` | `Dictionary<string, string>` | 是，JSON |
| `CustomRequirements` | `List<IAuthorizationRequirement>` | **否**——接口对象，带 `EvaluateAsync` 行为 |
| `IsEnabled` | `bool` | 是 |
| `Properties` | `Dictionary<string, object>?` | 是，JSON（读回为 `JsonElement`） |

唯一调用方是 `DefaultPolicyEvaluator.EvaluateAsync`（`Policies/DefaultPolicyEvaluator.cs:252-257`）：每次评估一条 `GetPolicyByNameAsync`。它自己判断 `IsEnabled`（`:259-262`），存储不过滤。

① ② 已交付五张表（`sys_authz_permission`、`sys_authz_user_permission`、`sys_authz_role_permission`、`sys_authz_role`、`sys_authz_user_role`）、三个存储中的两个、权限检查器、注册扩展与模块类。

### 1.2 默认实现的语义（落库实现必须保持）

读 `DefaultPolicyStore.cs` 得出：

| 行为 | 位置 |
| --- | --- |
| `CreatePolicyAsync`：`policy` 为空**或**名称为空都抛 `ArgumentException("策略或策略名称不能为空")`（注意：`null` 也是 `ArgumentException`，不是 `ArgumentNullException`） | `:59-64` |
| `CreatePolicyAsync`：同名已存在抛 `InvalidOperationException` | `:66-71` |
| `UpdatePolicyAsync`：同样的参数校验；不存在抛 `InvalidOperationException`；整对象替换 | `:84-99` |
| `DeletePolicyAsync`：空名称直接返回；不存在不抛 | `:109-118` |
| `GetPolicyByNameAsync`：空名称返回 `null` | `:43-52` |
| 读取不过滤 `IsEnabled` | 全部 |

### 1.3 要交付什么

1. 实体 `SysAuthzPolicy`（`sys_authz_policy`）与 `PolicyMapper`
2. `SqlSugarPolicyStore`：`IPolicyStore` 的 5 个方法，含自定义要求的策略拒绝写入
3. 在注册扩展里追加 `IPolicyStore` 的 `Replace`
4. **新包收尾的文档六处中剩余的四处**：包 `README.md`；`docs/packages/authorization-sqlsugar.md` + 侧边栏 + 包索引页；`framework/README.md` / `framework/README_cn.md` 的模块清单，以及四份 README 与文档站的模块数。另两处（csproj 与模块类 / 注册扩展、解决方案注册）已在 ① 完成

### 1.4 成功标准

1. `SqlSugarPolicyStore` 通过 1.2 表的每一条行为测试
2. `RequiredRoles`、`RequiredPermissions`、`RequiredClaims` 往返后逐项相等；列为空时读回空集合而不是 `null`
3. `CustomRequirements` 非空的策略，创建与更新都抛 `NotSupportedException`，库里的数据不变
4. `IPolicyStore` 只有一个描述符，实现为 `SqlSugarPolicyStore`、`Scoped`
5. 六处文档齐全；新文档页能在侧边栏「安全 · 认证 · 授权」分组里点到
6. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误；全量测试全绿

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 要实现的契约、默认实现与调用方 —— 最高优先**

```
framework/src/XiHan.Framework.Authorization/
  Policies/IPolicyStore.cs                     要实现的 5 个方法
  Policies/DefaultPolicyStore.cs               语义基准，见 1.2
  Policies/PolicyDefinition.cs                 字段来源
  Policies/IAuthorizationRequirement.cs        自定义要求的接口（带行为）
  Policies/DefaultPolicyEvaluator.cs:252-346   唯一调用方；自定义要求在 :317-346 评估
```

**② ① ② 的产出**

```
framework/src/XiHan.Framework.Authorization.SqlSugar/
  Entities/SysAuthzPermission.cs               全局实体（不分租户）的写法
  Mapping/JsonColumn.cs                        SerializeOrNull / DeserializeOrNull
  Mapping/PermissionMapper.cs                  映射器写法
  Permissions/SqlSugarPermissionStore.cs       Client 属性、SetColumns 条件更新
  Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs   要追加一行
framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/
  AuthorizationTestContext.cs / EntityConventionTests.cs / RegistrationTests.cs
```

**③ 文档范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/README.md     包 README 七段结构
framework/src/XiHan.Framework.EventBus.SqlSugar/README.md     「配置与约定」段写已知边界的方式
docs/packages/auditing-sqlsugar.md                            文档站条目的头部元数据与章节
docs/packages/eventbus-sqlsugar.md                            同上
docs/packages/index.md:58-64                                  「安全 · 认证 · 授权」分组的表
docs/.vitepress/config.ts:139-147                             同名侧边栏分组
README.md / README_cn.md                                      只改模块数（「常用包」表不加行）
framework/README.md:83 / framework/README_cn.md:83            模块清单中 Authorization 一行
```

**④ SqlSugar 源码**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Interface/IQueryable.cs          AnyAsync / FirstAsync / ToListAsync
  Interface/IUpdateable.cs         SetColumns / Where
  Interface/IDeleteable.cs         Where
```

> 树与目录名：SqlSugarCore 5.1.4.221 的权威源码是 `Src/Asp.NetCore2/`，不是 `Src/Asp.Net/`。更新提供者的目录名是 `UpdateProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：`更新數據.md`、`刪除數據.md`、`Json類型.md`（本份不用 SqlSugar 的 `IsJson`，见 4.1）。

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

- **不静默丢弃 `CustomRequirements`**。非空即抛 `NotSupportedException`
- **不用 SqlSugar 的 `[SugarColumn(IsJson = true)]`**。它在不同数据库上的列类型与序列化器由 SqlSugar 决定（Newtonsoft），与 ① ② 的 `Properties` 列用 System.Text.Json 的做法不一致；本份与 ① ② 一样存 `CodeFirst_BigString` 文本、由 `JsonColumn` 序列化
- **不改主包任何文件**，包括 `DefaultPolicyEvaluator`
- **不调 `GetClientForEntity<T>()`**，统一 `GetCurrentClient()`
- **文档只写本包**：不顺手改 `docs/packages/authorization.md`、`docs/guide/` 或其他包的页面——一个 PR 只做一件事
- **不写 `<inheritdoc/>`**

## 3. 非目标

- 不持久化自定义要求。需要自定义要求的策略由应用自行替换 `IPolicyStore` 提供（见「待确认的决策」与已知边界）
- 不做策略的租户隔离（见 4.2）
- 不替换 `IPolicyEvaluator`
- 不写 `docs/guide/` 下的指南页：本包没有独立的使用流程，包页面足够

## 4. 设计

### 4.1 实体 `SysAuthzPolicy`

`sys_authz_policy`，基类 `SugarEntity<long>`（**不分租户**），标注 `[TableInitialization(Group = "Authorization")]`。

| 属性 | 列 | 类型 | 长度 | 可空 |
| --- | --- | --- | --- | --- |
| `PolicyName` | `Policy_Name` | `string` | 256 | 否 |
| `DisplayName` | `Display_Name` | `string` | 256 | 否 |
| `Description` | `Description` | `string?` | 1024 | 是 |
| `RequiredRoles` | `Required_Roles` | `string`（`CodeFirst_BigString`，JSON 数组） | — | 否 |
| `RequiredPermissions` | `Required_Permissions` | `string`（同上） | — | 否 |
| `RequiredClaims` | `Required_Claims` | `string`（`CodeFirst_BigString`，JSON 对象） | — | 否 |
| `IsEnabled` | `Is_Enabled` | `bool` | — | 否 |
| `Properties` | `Properties` | `string?`（`CodeFirst_BigString`） | — | 是 |

索引：`ux_authz_policy_name`（`PolicyName`，唯一）。

三个 JSON 列默认值为 `"[]"` / `"[]"` / `"{}"`，列不可空。

### 4.2 租户：全局

策略名由代码里的 `[Authorize(Policy = "...")]` / `AuthorizePolicyAsync(userId, "...")` 引用，是全应用一份的目录，与权限定义同性质。策略的内容（要求哪些角色、哪些权限）在各租户里指向的是**各自租户的**角色与授予（经 ② 的严格隔离），因此一份全局策略天然在每个租户里按本租户数据评估。

### 4.3 `PolicyMapper`

```
ToEntity(PolicyDefinition definition, long basicId) → SysAuthzPolicy
  RequiredRoles       = JsonColumn.SerializeOrNull(definition.RequiredRoles)       ?? "[]"
  RequiredPermissions = JsonColumn.SerializeOrNull(definition.RequiredPermissions) ?? "[]"
  RequiredClaims      = JsonColumn.SerializeOrNull(definition.RequiredClaims)      ?? "{}"
  Properties          = JsonColumn.SerializeOrNull(definition.Properties)
  （不读 CustomRequirements）

ToDefinition(SysAuthzPolicy entity) → PolicyDefinition
  RequiredRoles       = DeserializeOrNull<List<string>>(...)               ?? []
  RequiredPermissions = DeserializeOrNull<List<string>>(...)               ?? []
  RequiredClaims      = DeserializeOrNull<Dictionary<string, string>>(...) ?? []
  CustomRequirements  = []（构造函数默认值）
```

映射器是纯函数，不校验 `CustomRequirements`；校验在存储里做，映射器因此能单独测字段搬运。

### 4.4 `SqlSugarPolicyStore`

构造函数：`(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator)`。

| 方法 | 行为 |
| --- | --- |
| `GetAllPoliciesAsync` | 全表；内存按 `PolicyName` 序数排序 |
| `GetPolicyByNameAsync` | 空名称返回 `null`；`FirstAsync(Policy_Name = ?)` |
| `CreatePolicyAsync` | `policy` 为空或名称为空 → `ArgumentException`；含自定义要求 → `NotSupportedException`；同名存在 → `InvalidOperationException`；`Insertable` |
| `UpdatePolicyAsync` | 同样的参数与自定义要求校验；不存在 → `InvalidOperationException`；`SetColumns` 改写名称以外的全部字段 |
| `DeletePolicyAsync` | 空名称直接返回；`Deleteable ... WHERE Policy_Name = ?` |

**校验顺序**：参数 → 自定义要求 → 查库。自定义要求的检查不碰数据库，失败时库里什么都没发生。

`NotSupportedException` 的消息要写出策略名，并指出出路：含自定义要求的策略由应用自行实现的 `IPolicyStore` 提供（启用本包后主包的内存策略存储已被顶替，「留在代码里注册」这条路不存在）。

### 4.5 注册

在 `AddXiHanAuthorizationSqlSugar` 里追加：

```csharp
services.Replace(ServiceDescriptor.Scoped<IPolicyStore, SqlSugarPolicyStore>());
```

**策略存储被整体顶替**：主包的策略存储每个请求都是空的，应用若原本在别处往 `DefaultPolicyStore` 实例里 `AddPoliciesAsync`，那条路本来就不生效；启用本包后策略只从库里来。

### 4.6 文档六处中本份负责的四处

**① 包 `README.md`**（`framework/src/XiHan.Framework.Authorization.SqlSugar/README.md`），固定七段：

| 段 | 内容要点 |
| --- | --- |
| 概述 | 主包三个存储是作用域内存实现、每请求为空；本包落库并顶替检查器 |
| 核心能力 | 六张表；三个存储 + 检查器；每次判定至多 2 条 SQL；删除角色同事务级联；严格租户隔离 |
| 依赖关系 | `Authorization`、`Data` |
| 配置与约定 | 无配置节；**建表两个开关都默认 `false`**；表名前缀与原因；主键；租户；自定义要求不支持；已知边界汇总 |
| 使用方式 | `[DependsOn]`；用 `SqlSugarPermissionStore` 播种权限定义 |
| 扩展点 | 用 `Replace` 再替换；检查器外套缓存装饰器 |
| 目录结构 | 完整文件树 |

**② 文档站**：`docs/packages/authorization-sqlsugar.md`，头部元数据与章节照 `auditing-sqlsugar.md` / `eventbus-sqlsugar.md`：概述 / 何时使用 / 安装与启用 / 表结构 / 工作原理 / 主要 API / 使用示例 / 扩展点 / 注意事项与最佳实践 / 依赖模块 / 相关模块。

侧边栏：`docs/.vitepress/config.ts` 的「安全 · 认证 · 授权」分组，`pkg("Authorization 授权", "authorization")` 之后加 `pkg("Authorization.SqlSugar", "authorization-sqlsugar")`。

包索引：`docs/packages/index.md` 「## 4. 安全 · 认证 · 授权」表中 `[Authorization]` 一行之后加一行。

**③ 四份 README**：

- `framework/README.md` / `framework/README_cn.md` 的模块清单，`Authorization` 一行之后
- 根 `README.md` / `README_cn.md` 的「常用包」表**不加行**（精选列表，`Auditing.SqlSugar`、`EventBus.SqlSugar` 均不在其中）
- **模块数与单测工程数各加一**：四份 README 与 `docs/index.md`、`docs/introduction.md`、`docs/why.md`、`docs/packages/index.md` 里写着模块 / 包 / 单测工程数量的地方（编写时为 68，含 `README.md` / `README_cn.md` 第 20 行徽章 URL 里的 `Modules-68-1f6feb`）。系列里 8 个包先后合入，**不写死数字**：实现时先读徽章里的当前值 N，grep 全部命中后逐条改成 N+1

> 沿用既有惯例：两个已完成的 SqlSugar 子包都没进根 README 的「常用包」表，本包同样不进。

## 5. 会静默失效的陷阱

**① 静默丢弃 `CustomRequirements`。**

`DefaultPolicyEvaluator` 只在 `policy.CustomRequirements.Count > 0` 时评估自定义要求（`:317`）。存储若只存其余字段，读回的策略 `CustomRequirements` 为空，这一整段直接跳过——**原本会拒绝的请求被放行**，无报错、无日志。测试断言创建与更新都抛 `NotSupportedException`，且库里没有该策略 / 该策略未被改写。

**② JSON 列为空时读回 `null`。**

`DefaultPolicyEvaluator` 直接访问 `policy.RequiredRoles.Count`（`:267`）。反序列化得到 `null` 而没兜底，评估时抛 `NullReferenceException`——这条会报错，但只在策略被评估时才报，存储自己的测试发现不了。映射器对三个集合一律 `?? []`，测试覆盖「列是空串时读回空集合」。

**③ `TryAdd` 顶替。**

主包 `IPolicyStore` 是 `TryAddScoped`，`TryAdd` 是空操作：应用照样跑在每请求为空的内存存储上，所有策略评估返回「策略不存在」——这条会以授权失败表现出来，但原因难查。注册测试断言只剩一个描述符。

**④ 文档漏写「建表默认关闭」。**

两个开关默认 `false`，不开就不建表，首次访问任何一个存储即报表不存在。README 与文档页都要写，并给出配置片段。

## 6. 测试策略

**只有 SQLite 一层，CI 强门禁执行。** 沿用 ① 的 `AuthorizationTestContext`（按反射建全部表），新增 `CreatePolicyStore()`。

必测：

- **实体约定**：`SysAuthzPolicy` 进入 ① 的约定测试（前缀、分组），且**不**可赋值给 `IMultiTenantEntity`；策略名唯一
- **映射**：三个集合与 `Properties` 往返；列为空串时读回空集合；映射器不读 `CustomRequirements`
- **存储**：1.2 表的每一条；读取不过滤 `IsEnabled`；全部策略按名称排序
- **自定义要求**：创建抛 `NotSupportedException` 且库里没有该策略；更新抛且原策略不变
- **注册**：`IPolicyStore` 只剩一个描述符

测试需要一个 `IAuthorizationRequirement` 的桩：只实现 `Name` 与 `EvaluateAsync`，放在测试文件里。

测试平台是 Microsoft.Testing.Platform：**没有可用的筛选参数**，**不要带 `--logger trx` / `--results-directory`**。SQLite 连接串必须带 `Pooling=False`。

## 7. 已知边界

写入 PR 描述与包 README（本份汇总 ① ② ③ 的全部边界），**不写进代码注释**。本份新增的：

| 项 | 说明 |
| --- | --- |
| 自定义要求不能落库 | 含 `CustomRequirements` 的策略写入即抛 `NotSupportedException`。需要自定义要求的策略只能在代码里评估，或由应用自行替换 `IPolicyStore` 做「代码注册 + 库」的合并 |
| 策略全局可写 | 策略表不分租户，租户态调用写方法会改到所有租户共用的策略；应用层应只在平台态暴露 |
| 并发创建同名策略 | 后到者撞唯一索引抛数据库异常而非 `InvalidOperationException` |
| 声明值区分大小写 | `RequiredClaims` 以 JSON 存取，键值原样保留；比较规则由 `DefaultPolicyEvaluator` 决定（键不区分大小写、值区分） |
| `Properties` 的值类型 | 读回为 `JsonElement` |

① ② 的边界（名称大小写、并发重复授予、删除定义不级联、权限定义全局可写、策略评估逐权限查询、检查器被应用替换、用户角色关联存标识、静态角色可删、`IsInRoleAsync` 不看启用、需要租户过滤器、取消令牌残留）一并写进 README 的「配置与约定」与 PR 描述。

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动：`MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`）
- 每个 `.cs` 文件带两行版权声明（`XHFH001`）
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**；判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**关闭
- `public` 成员都有 `<summary>`，不用 `<inheritdoc/>`
- 包 README 七段齐全；文档站页面、侧边栏、包索引、四份 README 均已更新
- 提交信息为中文 Conventional Commits，作用域 `authorization-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：只改本包相关的文档

## 五个共同问题

| 问题 | 本份的回答 | 依据 |
| --- | --- | --- |
| 分表与否 | **不分表** | 策略是配置型数据，条数以十计 |
| 主键类型 | **雪花 `long`** | 契约以名称标识策略；名称做唯一索引，主键另取，与 ① ② 一致 |
| 是否参与工作单元事务 | **参与**，`Scoped` | 经 `GetCurrentClient()` 自动登记；写入都是单语句 |
| 是否需要多库 | **单库** | 与本包其余五张表同库 |
| 顶替方式 | **`Replace`** | 主包 `IPolicyStore` 为 `TryAddScoped`（`XiHanAuthorizationServiceCollectionExtensions.cs:36`） |

## 待确认的决策

| # | 决策 | 默认值 | 理由 | 若改会影响什么 |
| --- | --- | --- | --- | --- |
| 1 | 含自定义要求的策略 | 拒绝写入，抛 `NotSupportedException` | 丢弃等于绕过授权 | 若改为「代码注册的自定义要求与库里的策略合并」，需新增注册入口与合并规则，是另一份设计 |
| 2 | 策略不分租户 | 全局 | 策略名由代码引用；要求的角色与权限已按租户隔离 | 改为按租户则唯一索引加 `TenantId`，平台策略需读共享 |
| 3 | 集合的存法 | 三个 `CodeFirst_BigString` JSON 列，System.Text.Json | 与 `Properties` 一致；不引入子表 | 改成子表可按角色 / 权限反查策略，但每次读策略多两次查询 |
| 4 | 不用 SqlSugar 的 `IsJson` | 不用 | 序列化器与列类型由 SqlSugar 决定，与本包其余 JSON 列不一致 | 用它可省 `JsonColumn` 调用，但 `Dictionary<string, object>` 的读回类型会变成 Newtonsoft 的 `JToken` |
| 5 | 读取不过滤 `IsEnabled` | 不过滤 | 与默认实现一致，评估器自己判断 | 过滤则管理界面看不到禁用策略 |
| 6 | 文档站分组 | 「安全 · 认证 · 授权」，紧跟 `Authorization` | 与 `Auditing.SqlSugar` 紧跟 `Auditing`、`EventBus.SqlSugar` 在事件分组的做法一致 | 纯导航位置 |
| 7 | 根 README「常用包」表是否加行 | 不加，只改模块数 | 沿用 `Auditing.SqlSugar`、`EventBus.SqlSugar` 的惯例；该表是精选列表 | 若要加，在两份根 README 各加一行 |
| 8 | 不写 `docs/guide/` 指南 | 不写 | 包页面已覆盖使用方式 | 需要时另起文档 PR |

## 9. 下一份

本包三份到此完成，可以提 PR（从 `upstream/main` 开 worktree、按份 cherry-pick 或整体重放）。下一个包按拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 5 节）是 `Security` + `Traffic` + `Upgrade` 三个小包。
