# Security.SqlSugar 密码历史存储（1）设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-security-sqlsugar-1-password-history.md`
- **前置**：无（`XiHan.Framework.Security`、`XiHan.Framework.Data` 均已发布）
- **所属 PR**：独立一个 PR（`Security.SqlSugar`）
- **Linear 议题**：https://linear.app/elf-express/issue/EDDIE-11
- **系列**：SqlSugar 持久化系列剩余模块之一，拆分方案见 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 3、5 节

> 本文档**自成一体**。实现本包所需的全部约束都写在这里，不依赖读者已读过其他 spec。系列共用的约定在本文档里各自重复一份——若发现与拆分方案不一致，以本文档为准并在 PR 里提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。
>
> **本包最容易静默出错的地方**：`IPasswordHistoryStore` 契约**只有一个读方法**，没有任何写方法——主包的 `DefaultPasswordHistoryStore.RecordPassword` 是一个**静态方法**，不在接口上，全仓库也没有任何调用方调用它（已用 `grep` 核实）。这意味着"记录新密码"这件事，在当前框架里从未被真正接入任何调用链。如果照抄接口只实现一个 `GetRecentPasswordHashesAsync`，交付出来的包在生产环境里就是一个**只能读、永远读不到任何数据**的空壳，因为没有任何写入路径。第 4.3 节给出了本包的应对方式；第 5 节 ① 是这条的详细展开。

---

## 1. 背景与目标

### 1.1 现状

`IPasswordHistoryStore`（`framework/src/XiHan.Framework.Security/Services/IPasswordHistoryStore.cs`）只声明了一个方法：

```csharp
public interface IPasswordHistoryStore
{
    Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken ct = default);
}
```

它回答的问题是："给定一个用户，最近使用过的 N 个密码哈希是什么"——用于 `PasswordPolicyService.IsPasswordReusedAsync`（`framework/src/XiHan.Framework.Security/Services/PasswordPolicyService.cs:173-191`）在用户改密码时逐条 `VerifyPassword(历史哈希, 新密码明文)` 比对，防止重复使用旧密码。

`DefaultPasswordHistoryStore`（同目录）是进程内存实现：

- `GetRecentPasswordHashesAsync` 从 `ConcurrentDictionary<long, ConcurrentQueue<string>>` 里 `TakeLast(count)`
- 另有一个**静态方法** `RecordPassword(long userId, string passwordHash, int maxHistoryCount = 10)`，把新哈希入队并把队列裁剪到 `maxHistoryCount` 条以内

`RecordPassword` 不在接口上，`grep -rn "RecordPassword"` 全仓库只命中它自己的定义——**没有任何调用方**。也就是说主包里"记录密码历史"这一步本身就是悬空的，本包不是在替换一个已经工作的写入路径，而是补一个从未存在过的写入路径。

存的是**密码哈希**（`PasswordHasher.HashPassword` 产出的 `version:iterations:hashAlgorithm:salt:hash` 格式字符串，见 `framework/src/XiHan.Framework.Security/Password/PasswordHasher.cs:52`），不是明文。**本包不做任何哈希计算**，只原样存取调用方传入的哈希字符串。

### 1.2 交付目标

1. 新增 `XiHan.Framework.Security.SqlSugar` 包，提供 `IPasswordHistoryStore` 的 SqlSugar 落库实现 `SqlSugarPasswordHistoryStore`
2. 补一个**契约之外**的公开写入方法 `RecordPasswordAsync`，形状照抄 `DefaultPasswordHistoryStore.RecordPassword`（同步→异步、静态→实例），供应用层在密码修改成功后显式调用
3. 用 `services.Replace` 顶替 `DefaultPasswordHistoryStore`

### 1.3 成功标准

1. 调用 `RecordPasswordAsync` 写入的哈希，能被 `GetRecentPasswordHashesAsync` 按"最近 N 条"读回
2. 单个用户的历史记录数超过 `maxHistoryCount` 时，`RecordPasswordAsync` 会把超出的最旧记录裁掉
3. `GetRecentPasswordHashesAsync` 参与调用方所在的当前工作单元事务（若有）
4. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 契约与主包实现——最高优先**

```
framework/src/XiHan.Framework.Security/
  Services/IPasswordHistoryStore.cs                 唯一的契约方法
  Services/DefaultPasswordHistoryStore.cs           内存实现，RecordPassword 的形状范本
  Services/PasswordPolicyService.cs:173-191          唯一的调用方，确认调用语义
  Extensions/DependencyInjection/XiHanSecurityServiceCollectionExtensions.cs:39   确认注册用的是 TryAddScoped
  Password/PasswordHasher.cs:52                      哈希字符串的格式，确认列宽
```

**② 兄弟子包范本**

```
framework/src/XiHan.Framework.Auditing.SqlSugar/     简单实体 + 写入器的形状范本
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs                  GetClientForEntity<T>() 的签名
  Clients/SqlSugarClientResolver.cs:129-143            确认 GetClientForEntity 内部会调 EnlistCurrentUnitOfWork
  Entities/SugarCreationEntity.cs                      基类字段与 SugarColumn 写法
```

**③ SqlSugar 源码——API 真实签名的唯一权威**

```
E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  Abstract/InsertableProvider/InsertableProvider.cs   Insertable(...).ExecuteCommandAsync()
  Abstract/DeleteProvider/DeleteableProvider.cs        条件删除、In(...)
  Abstract/QueryableProvider/QueryableProvider.cs:1453,1458   Skip(int) / Take(int)
```

> 树与目录名：本包引用 SqlSugarCore 5.1.4.221，权威源码是 `Src/Asp.NetCore2/`（**不是** `Src/Asp.Net/`，那是 .NET Framework 变体）。更新提供者的目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。

### 2.2 文档参考

`E:/source/platfrom-admin/docs/SqlSugar-docs/`：`插入數據.md`、`刪除數據.md`、`查詢數據-進階查詢.md`（`Skip`/`Take` 分页写法）。

仓库自身文档：`docs/packages/security.md`、`docs/packages/auditing-sqlsugar.md`（同类子包文档范本）。

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

- **不要只实现接口上的那一个方法就收工**。见"实现前必读"——契约没有写方法不代表本包不需要写入能力，`RecordPasswordAsync` 是本包的核心交付物之一，不是可选项。
- **不要在存储层做任何哈希计算或密码校验**。`VerifyPassword` 是 `PasswordPolicyService` 的职责，本包只原样存取字符串。
- **不要把 `RecordPasswordAsync` 做成契约方法**（不要修改 `IPasswordHistoryStore`）。契约属于主包 `XiHan.Framework.Security`，改契约不在本 PR 范围，且会影响所有已实现该接口的地方。
- **不要用 `DateTime`，用 `DateTimeOffset`**。仓库既有实体全部如此。

## 3. 非目标

- **不修改 `IPasswordHistoryStore` 契约**，不给它加写方法。
- **不接入密码修改流程**。把 `RecordPasswordAsync` 接到"用户改密码成功后"这个业务时机，是应用层或 `Authentication` 模块的事，不在本包范围。
- **不做多库路由**。密码历史是全局数据，不涉及 `[ModuleDataSource]`。
- **不做分表**。数据按用户裁剪，单用户历史条数有界（`maxHistoryCount`），不是随时间无限增长的日志类数据。
- **不做租户隔离**。契约方法签名里没有租户参数，`userId` 是全局用户标识。

## 4. 设计

### 4.1 实体：`SysPasswordHistory`

```csharp
[SugarTable("sys_password_history")]
public class SysPasswordHistory : SugarCreationEntity<long>
```

字段：

| 属性 | 列名 | 类型 | 说明 |
| --- | --- | --- | --- |
| `BasicId`（继承） | `Basic_Id` | `long` | 主键，雪花 ID，经 `IDistributedIdGenerator<long>` 生成 |
| `CreatedTime`（继承） | `Created_Time` | `DateTimeOffset` | 记录时间，同时是"最近"的排序依据 |
| `UserId` | `User_Id` | `long` | 用户标识，`NOT NULL` |
| `PasswordHash` | `Password_Hash` | `string`，`Length = 512` | 密码哈希，`NOT NULL` |

不分表、不参与多库路由、无租户列。

### 4.2 读取：`GetRecentPasswordHashesAsync`

```
按 UserId 过滤，按 CreatedTime 降序取前 count 条，取回后在内存里 Reverse()
```

`Reverse()` 的原因：`DefaultPasswordHistoryStore` 用 `queue.TakeLast(count)`，队列是按写入顺序入队的，`TakeLast` 保留的是**原始顺序**（最旧的在前）。为了不悄悄改变契约的既有行为（虽然当前唯一调用方 `IsPasswordReusedAsync` 只是逐条 `foreach` 比对、不依赖顺序），SqlSugar 实现按降序查出最近 N 条后在内存翻转，得到同样"旧→新"的顺序。

`count <= 0` 时直接返回空集合，不发 SQL。

`client = _clientResolver.GetClientForEntity<SysPasswordHistory>()`——本包不涉及 `[ModuleDataSource]`，该调用在单库场景下会解析到当前布局的主库，并且（见 `SqlSugarClientResolver.GetClientForEntity` 源码第 129-143 行）内部继续调用 `GetClient` 从而登记进当前事务型工作单元。

### 4.3 写入：`RecordPasswordAsync`（契约之外的公开方法）

```csharp
public async Task RecordPasswordAsync(long userId, string passwordHash, int maxHistoryCount = 10, CancellationToken ct = default)
```

行为与 `DefaultPasswordHistoryStore.RecordPassword` 对齐：

1. 插入一条新记录：`BasicId = _idGenerator.NextId()`、`UserId = userId`、`PasswordHash = passwordHash`、`CreatedTime = DateTimeOffset.UtcNow`
2. 裁剪：查询该用户按 `CreatedTime` 降序、`Skip(maxHistoryCount)` 之后的记录的 `BasicId` 集合，若非空则 `Deleteable<SysPasswordHistory>().In(...)` 删除

两步在同一个 `client` 上顺序执行（`client = _clientResolver.GetClientForEntity<SysPasswordHistory>()`，取一次复用），不额外开事务——若调用方处于事务型工作单元中，两次操作经 `EnlistCurrentUnitOfWork` 落在同一个钉住的连接上，天然具有事务一致性；若不在工作单元中，两次操作各自独立提交，裁剪失败不影响插入已生效（可接受：极端情况下多出一条历史记录，不影响读取的正确性上限只是暂时多存了一条）。

### 4.4 注册

`XiHanSecurityServiceCollectionExtensions.AddXiHanSecurityServices` 用的是：

```csharp
services.TryAddScoped<IPasswordHistoryStore, DefaultPasswordHistoryStore>();
```

（`framework/src/XiHan.Framework.Security/Extensions/DependencyInjection/XiHanSecurityServiceCollectionExtensions.cs:39`）

`XiHanSecurityModule` 没有 `[DependsOn]`（本模块的 `ConfigureServices` 先于任何依赖它的模块执行），因此本包的 `XiHanSecuritySqlSugarModule` 必须 `[DependsOn(typeof(XiHanSecurityModule), typeof(XiHanDataModule))]`，确保 `TryAddScoped` 先注册、`Replace` 后顶替：

```csharp
services.Replace(ServiceDescriptor.Scoped<IPasswordHistoryStore, SqlSugarPasswordHistoryStore>());
```

## 5. 五个共同问题

1. **分表与否**：不分表。单用户历史条数有界（由 `maxHistoryCount` 裁剪，默认 10），不是随时间无限增长的日志数据，没有分表的必要。
2. **主键类型**：`long`，雪花 ID（经 `IDistributedIdGenerator<long>`）。契约的标识参数 `userId` 是业务外键而非本表主键——一个用户对应多行历史记录，主键必须是独立于 `userId` 的行标识，不能照搬 `userId` 做主键。
3. **是否参与工作单元事务**：**参与**。`GetClientForEntity<T>()` 在单库场景下会调用 `GetClient` 从而 `EnlistCurrentUnitOfWork`（`SqlSugarClientResolver.cs:138,150-155`），密码历史的写入与用户改密码这一业务操作理应同一个事务：若改密码的事务回滚，历史记录也应回滚，否则会出现"密码没改成功但历史记录里多了一条"的不一致。因此注册为 **Scoped**（与主包一致）。
4. **是否需要多库**：不需要。密码历史是全局用户数据，契约方法没有租户或模块维度参数，单库即可。
5. **顶替方式**：主包用 `TryAddScoped` 注册 `DefaultPasswordHistoryStore`，本包必须用 `services.Replace(ServiceDescriptor.Scoped<IPasswordHistoryStore, SqlSugarPasswordHistoryStore>())`。`TryAdd` 在此处是空操作。

## 6. 会静默失效的陷阱

**① 只实现接口方法，不提供写入能力。**

`IPasswordHistoryStore` 只有一个读方法，编译器不会提醒你"还需要一个写方法"。如果只写 `GetRecentPasswordHashesAsync` 就收工，交付出来的包能通过编译、能通过所有只测读取路径的单元测试，但在真实环境里 `sys_password_history` 表永远是空表——因为没有任何代码往里面写。这个错误在代码审查里也不容易被发现，因为"契约只有一个方法"这件事本身是真的，容易让人误以为任务已经完成。**必须**同时交付 `RecordPasswordAsync`（详见 4.3 节）并在测试里覆盖"写入后能读回"的完整链路，而不是孤立地测试 `GetRecentPasswordHashesAsync` 对着手工插入的行做查询。

**② 用对象初始化器给 `BasicId` 赋值。**

`EntityBase<TKey>.BasicId` 是 `{ get; protected set; }`，只能经构造函数 `SysPasswordHistory(long basicId)` 传入。用 `new SysPasswordHistory { BasicId = ... }` 编译不过，但若为了"图省事"给实体加一个 `public` setter 破坏这个约束，会与仓库其余所有实体的约定不一致，构建时 `XHFH001` 之外的分析器不会拦，只有代码审查能发现。

**③ 裁剪逻辑的 `Skip` 顺序写反。**

裁剪要删除的是"超出 `maxHistoryCount` 的最旧记录"，即按 `CreatedTime` **降序**排列后、`Skip(maxHistoryCount)` **之后**的那些行。如果排序方向写反（升序 `Skip`），删掉的会是最新写入的记录而不是最旧的——`RecordPasswordAsync` 的单元测试如果只断言"总条数不超过上限"，这个方向错误发现不了，必须断言"保留的是哪几条"（例如断言最新写入的哈希仍在，最旧的那条被删除）。

## 7. 测试策略

**第一层——SQLite，CI 强门禁执行**

- 建表：`SysPasswordHistory` 能在 SQLite 上建出 `sys_password_history` 表
- 写入后能读回：`RecordPasswordAsync` 写入一条，`GetRecentPasswordHashesAsync` 能读到
- 顺序：连续写入 3 条不同哈希，`GetRecentPasswordHashesAsync(userId, 3)` 返回顺序与写入顺序一致（旧→新）
- 裁剪：`maxHistoryCount = 3` 时写入第 4 条，读回的 3 条不包含最早写入的那条，包含最新的 3 条
- `count` 大于实际条数：只返回实际存在的条数，不抛异常
- `count <= 0`：返回空集合，不发 SQL（可通过断言不抛异常、返回空集合覆盖，不需要断言"没发 SQL"）
- 跨用户隔离：用户 A 的写入不出现在用户 B 的查询结果里
- 注册断言：`services.AddXiHanSecuritySqlSugar()` 后 `IPasswordHistoryStore` 的实现类型是 `SqlSugarPasswordHistoryStore`

SQLite 临时库连接串必须带 `Pooling=False`。

**第二层**：本包不涉及并发协议或分布式语义，不需要真实数据库层的专门测试。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform，**没有可用的筛选参数**，**不要带 `--logger trx` / `--results-directory`**。

## 8. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| `RecordPasswordAsync` 暂无调用方 | 全仓库当前没有任何代码在密码修改成功后调用它（`DefaultPasswordHistoryStore.RecordPassword` 同样如此，这是主包既有的设计缺口，不是本 PR 引入的）。接入密码修改流程需要 `Authentication` 或应用层显式调用，不在本 PR 范围 |
| 非事务场景下写入不是原子的 | 不在工作单元中调用 `RecordPasswordAsync` 时，插入与裁剪是两次独立提交；裁剪失败只会让历史表多保留一条记录，不影响读取正确性的上限 |
| 无并发去重保护 | 若同一用户的密码修改在极短时间内被并发调用两次，可能写入两条历史记录；这是密码修改场景本身极少并发的操作，未做额外互斥 |

## 9. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `SqlSugarPasswordHistoryStore` 同时提供 `GetRecentPasswordHashesAsync`（契约）与 `RecordPasswordAsync`（补充能力），有测试覆盖"写入后能读回"的完整链路
- 注册用 `services.Replace`，有测试断言顶替生效
- 每个 `.cs` 文件带两行版权声明
- 注释与 XML 文档注释为简体中文，且只说明代码做什么
- 包 README 沿用固定七段结构
- 提交信息中文 Conventional Commits，作用域 `security-sqlsugar`，不加 AI 署名

## 10. 下一份

本包只有一份计划。系列内下一个包见拆分方案第 5 节建议顺序（`Traffic.SqlSugar`、`Upgrade.SqlSugar` 各自独立成文）。
