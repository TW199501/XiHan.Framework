# Authentication.SqlSugar ②：刷新令牌、第三方登录与文档收尾 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在第 ① 份建好的 `XiHan.Framework.Authentication.SqlSugar` 包里，以 SqlSugar 落库实现顶替 `IRefreshTokenStore` 与 `IExternalLoginStore`，并补齐文档站条目与模块清单，使整个包可以作为一个 PR 提交。

**Architecture:** 刷新令牌只存 SHA-256 哈希（`Token_Hash` 唯一索引）；`Remove` 标记 `Revoked_Time` 而不删行；已撤销的令牌再次被校验时，在 `CopyNew()` 开出的独立连接上撤销同一租户、同一主体的全部未撤销令牌（不随调用方事务回滚）；每保存 N 次在独立连接上清理已过期的行。存储注册为 Singleton（被单例 `JwtTokenService` 捕获），每次调用经 `IServiceScopeFactory` 自建作用域解析 Scoped 的客户端解析器。第三方登录存储为 Scoped，`tenantId` 为空时回退当前租户，拒绝改绑，`ProviderKey` 在内存里再做一次序数比较。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform、VitePress（文档站）

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-authentication-sqlsugar-2-refresh-token-external-login-design.md`

> 该 spec 自成一体。**动手前先读它的「待确认的决策」与第 5 节「会静默失效的陷阱」。**

**Linear 议题:** `https://linear.app/elf-express/issue/EDDIE-9`

**前置:** 第 ① 份（`.superpowers/plans/2026-09-28-authentication-sqlsugar-1-user-store.md`）必须已完成。本计划依赖它的：包骨架与 csproj、`Mapping/StorageTime`、`Users/SqlSugarUserStore`、注册扩展 `AddXiHanAuthenticationSqlSugar`、测试项目与 `AuthenticationTestContext`、`Fakes/` 下的三个替身、`RegistrationTests`。

> 在第 ① 份的同一个 worktree（`E:/source/XiHan/XiHan.Framework-authentication`，分支 `feat/authentication-sqlsugar`）上继续。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：`E:/source/XiHan/XiHan.Framework-authentication`，分支 `feat/authentication-sqlsugar`（第 ① 份从 `dev` 开出）。上游是 `main`，**绝不在 `main` 上提交**。若 worktree 不存在：

```bash
git worktree add ../XiHan.Framework-authentication -b feat/authentication-sqlsugar dev
```

**技术栈是 SqlSugar，不是 Entity Framework Core。** 下列 EF Core 惯用法一律禁止：

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

**SqlSugar 签名只信源码**：权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）；更新提供者目录名是 `Abstract/UpdateProvider/`，**不是** `UpdateableProvider`。文档在 `E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar
ISugarQueryable<T> ISqlSugarClient.Queryable<T>()
ISugarQueryable<T> ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
T                  ISugarQueryable<T>.First()                  // 无匹配时返回 default(T)
List<T>            ISugarQueryable<T>.ToList()
Task<List<T>>      ISugarQueryable<T>.ToListAsync()            // Interface/IQueryable.cs:236
int                ISugarQueryable<T>.Count()
Task<int>          ISugarQueryable<T>.CountAsync()
IInsertable<T>     ISqlSugarClient.Insertable<T>(T insertObj)
int                IInsertable<T>.ExecuteCommand()
Task<int>          IInsertable<T>.ExecuteCommandAsync()
IUpdateable<T>     ISqlSugarClient.Updateable<T>() where T : class, new()
IUpdateable<T>     IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T>     IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
int                IUpdateable<T>.ExecuteCommand()
IDeleteable<T>     ISqlSugarClient.Deleteable<T>() where T : class, new()
IDeleteable<T>     IDeleteable<T>.Where(Expression<Func<T, bool>> expression)
int                IDeleteable<T>.ExecuteCommand()
Task<int>          IDeleteable<T>.ExecuteCommandAsync()
SqlSugarClient     ISqlSugarClient.CopyNew()                   // Interface/ISqlSugarClient.cs:54，按当前连接配置新建客户端与连接
int                IAdo.ExecuteCommand(string sql, params SugarParameter[] parameters)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3, bool isUnique = false)

// 框架与 BCL
ISqlSugarClient ISqlSugarClientResolver.GetClientForEntity<TEntity>()
long? ICurrentTenant.Id
TKey IDistributedIdGenerator<TKey>.NextId()
IServiceScope IServiceScopeFactory.CreateScope()
byte[] SHA256.HashData(byte[] source)
string Convert.ToHexString(byte[] inArray)                     // 大写
void ArgumentOutOfRangeException.ThrowIfNegativeOrZero<T>(T value, string? paramName = null)
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事、设计理由、反事实推理（「否则会……」）一律进提交信息
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以）
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀
- `public` 成员必须有 `<summary>`

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`；全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，不 Import `version.props`、不设 `AssemblyName`；`Microsoft.Data.Sqlite` 经 `SqlSugarCore` 传递引入，不需要额外 `PackageReference`（第 ① 份已建好，本份不改）。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本包无关，不要去追。

**提交信息**：中文 Conventional Commits，作用域 `authentication-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**。不开启就不会自动建表，首次读写即报表不存在。README 与文档站都要写明。

---

## 本计划特有的硬约束

**① 数据库与日志里不出现刷新令牌原文。** 只存 `RefreshTokenHasher.Hash(token)`。

**② 级联撤销与过期清理必须在 `client.CopyNew()` 上执行。** 在解析器给的客户端上执行，会被调用方事务一并回滚——SQLite 单测默认没有事务，**照样全绿**。Task 3 的用例「级联撤销不随调用方事务回滚」是唯一的防线，**不要删它、不要把它改成不开事务**。

**③ `SqlSugarRefreshTokenStore` 的构造函数只注入 `IServiceScopeFactory` 与单例依赖。** 注入 `ISqlSugarClientResolver` / `ICurrentTenant` 会形成被单例捕获的作用域依赖。

**④ `Remove` 只标记 `Revoked_Time`，清理只删 `Expires_At < now`。** 删行或清掉已撤销未过期的行都会让重用检测静默失效。

**⑤ 宽限期判断写成 `grace > TimeSpan.Zero && elapsed < grace`。** 写成 `elapsed <= grace` 会让默认的 0 宽限期在冻结时钟下不级联。

**⑥ `ProviderKey` 不规范化、不截断，查询后在内存里用 `StringComparison.Ordinal` 再比较一次。**

**⑦ 两个 `Replace`：刷新令牌 Singleton、第三方登录 Scoped。** `TryAdd` 是空操作。

**⑧ 测试里 `Options.Create` 一律写全名 `Microsoft.Extensions.Options.Options.Create`。** 本份新增的命名空间 `XiHan.Framework.Authentication.SqlSugar.Options` 会遮蔽简名。

---

## File Structure

```
framework/src/XiHan.Framework.Authentication.SqlSugar/
  Options/XiHanAuthenticationSqlSugarOptions.cs                         新建（Task 1）
  Entities/SysAuthRefreshToken.cs                                       新建（Task 1）
  Entities/SysAuthExternalLogin.cs                                      新建（Task 5）
  RefreshTokens/RefreshTokenHasher.cs                                   新建（Task 1）
  RefreshTokens/SqlSugarRefreshTokenStore.cs                            新建（Task 2），整份替换（Task 3）
  ExternalLogins/SqlSugarExternalLoginStore.cs                          新建（Task 5）
  Extensions/DependencyInjection/
    XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs           整份替换（Task 6）
  XiHanAuthenticationSqlSugarModule.cs                                  修改 XML 注释（Task 6）
  README.md                                                             整份重写（Task 7）

framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/
  AuthenticationTestContext.cs                                          修改（Task 2、Task 3、Task 5）
  RefreshTokenEntityTests.cs                                            新建（Task 1）
  RefreshTokenStoreTests.cs                                             新建（Task 2）
  RefreshTokenReuseTests.cs                                             新建（Task 3）
  RefreshTokenRotationTests.cs                                          新建（Task 4）
  ExternalLoginStoreTests.cs                                            新建（Task 5）
  RegistrationTests.cs                                                  修改（Task 6）

docs/packages/authentication-sqlsugar.md                                新建（Task 8）
docs/.vitepress/config.ts                                               修改（Task 8）
docs/packages/index.md                                                  修改（Task 8）
docs/index.md、docs/introduction.md、docs/why.md                        修改计数（Task 8）
framework/README.md、framework/README_cn.md                             修改（Task 8）
README.md、README_cn.md                                                 修改计数（Task 8）
```

---

### Task 1: 配置项、刷新令牌实体与哈希

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Options/XiHanAuthenticationSqlSugarOptions.cs`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Entities/SysAuthRefreshToken.cs`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/RefreshTokenHasher.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenEntityTests.cs`

**Interfaces:**
- Consumes: 第 ① 份的 `AuthenticationTestContext(params Type[] entityTypes)`
- Produces:
  - `XiHanAuthenticationSqlSugarOptions`：`const string SectionName = "XiHan:Authentication:SqlSugar"`；`bool RefreshTokenReuseDetection`（默认 `true`）、`TimeSpan RefreshTokenReuseGracePeriod`（默认 `TimeSpan.Zero`）、`int RefreshTokenCleanupFrequency`（默认 `256`）
  - `SysAuthRefreshToken : SugarEntity<long>`：`long TenantId`、`string TokenHash`、`string? Subject`、`DateTime ExpiresAt`、`DateTime CreatedTime`、`DateTime? RevokedTime`
  - `static string RefreshTokenHasher.Hash(string refreshToken)`

**参考来源（动手前先读）：**
- 第 ① 份的 `Entities/SysAuthUser.cs`（实体写法）
- Options 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/Options/XiHanSqlSugarEventBoxOptions.cs`
- 列定义：spec 第 4.2 节

**本任务禁止事项：** 硬约束 ①。不要加 `[SplitTable]`、`[TableInitialization]`。不要用 `DateTimeOffset`。不要给哈希加盐。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Options;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 刷新令牌实体、哈希与配置测试
/// </summary>
public class RefreshTokenEntityTests
{
    /// <summary>
    /// 表名为刷新令牌表
    /// </summary>
    [Fact]
    public void 表名为刷新令牌表()
    {
        var table = typeof(SysAuthRefreshToken).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_auth_refresh_token", table.TableName);
    }

    /// <summary>
    /// 令牌哈希唯一且另有主体与过期时间索引
    /// </summary>
    [Fact]
    public void 令牌哈希唯一且另有主体与过期时间索引()
    {
        var indexes = typeof(SysAuthRefreshToken).GetCustomAttributes<SugarIndexAttribute>().ToList();
        var unique = Assert.Single(indexes, item => item.IsUnique);
        string[] expected = [nameof(SysAuthRefreshToken.TokenHash)];

        Assert.Equal(3, indexes.Count);
        Assert.Equal(expected, unique.IndexFields.Keys.ToArray());
    }

    /// <summary>
    /// 令牌哈希列长度为 64 且非空
    /// </summary>
    [Fact]
    public void 令牌哈希列长度为64且非空()
    {
        var column = typeof(SysAuthRefreshToken)
            .GetProperty(nameof(SysAuthRefreshToken.TokenHash))!
            .GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Token_Hash", column.ColumnName);
        Assert.Equal(64, column.Length);
        Assert.False(column.IsNullable);
    }

    /// <summary>
    /// 同一令牌哈希被唯一索引拒绝
    /// </summary>
    [Fact]
    public void 同一令牌哈希被唯一索引拒绝()
    {
        using var context = new AuthenticationTestContext(typeof(SysAuthRefreshToken));
        var tokenHash = RefreshTokenHasher.Hash("token-1");

        context.Client.Insertable(NewEntity(1, tokenHash)).ExecuteCommand();

        Assert.ThrowsAny<Exception>(() => context.Client.Insertable(NewEntity(2, tokenHash)).ExecuteCommand());
    }

    /// <summary>
    /// 哈希为 SHA-256 大写十六进制
    /// </summary>
    [Fact]
    public void 哈希为SHA256大写十六进制()
    {
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            RefreshTokenHasher.Hash("abc"));
    }

    /// <summary>
    /// 不同令牌的哈希不同
    /// </summary>
    [Fact]
    public void 不同令牌的哈希不同()
    {
        Assert.NotEqual(RefreshTokenHasher.Hash("token-1"), RefreshTokenHasher.Hash("token-2"));
    }

    /// <summary>
    /// 配置默认值与配置节名
    /// </summary>
    [Fact]
    public void 配置默认值与配置节名()
    {
        var options = new XiHanAuthenticationSqlSugarOptions();

        Assert.Equal("XiHan:Authentication:SqlSugar", XiHanAuthenticationSqlSugarOptions.SectionName);
        Assert.True(options.RefreshTokenReuseDetection);
        Assert.Equal(TimeSpan.Zero, options.RefreshTokenReuseGracePeriod);
        Assert.Equal(256, options.RefreshTokenCleanupFrequency);
    }

    private static SysAuthRefreshToken NewEntity(long id, string tokenHash)
    {
        return new SysAuthRefreshToken(id)
        {
            TokenHash = tokenHash,
            Subject = "1001",
            ExpiresAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到 `SysAuthRefreshToken`、`XiHanAuthenticationSqlSugarOptions`、`RefreshTokenHasher`。

- [ ] **Step 3: 创建配置类**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Options/XiHanAuthenticationSqlSugarOptions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Authentication.SqlSugar.Options;

/// <summary>
/// 认证存储 SqlSugar 配置
/// </summary>
public class XiHanAuthenticationSqlSugarOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Authentication:SqlSugar";

    /// <summary>
    /// 是否启用刷新令牌重用检测
    /// </summary>
    /// <remarks>
    /// 启用时，已撤销的刷新令牌再次被校验会撤销同一租户下同一主体的全部未撤销刷新令牌。
    /// </remarks>
    public bool RefreshTokenReuseDetection { get; set; } = true;

    /// <summary>
    /// 刷新令牌重用检测的宽限期
    /// </summary>
    /// <remarks>
    /// 令牌撤销后在此时长内再次被校验，只拒绝、不级联撤销。为零时任何再次校验都级联撤销。
    /// </remarks>
    public TimeSpan RefreshTokenReuseGracePeriod { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// 每保存多少次刷新令牌清理一次已过期的记录，小于等于 0 时不清理
    /// </summary>
    public int RefreshTokenCleanupFrequency { get; set; } = 256;
}
```

- [ ] **Step 4: 创建刷新令牌实体**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Entities/SysAuthRefreshToken.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Authentication.SqlSugar.Entities;

/// <summary>
/// 刷新令牌实体
/// </summary>
[SugarTable("sys_auth_refresh_token")]
[SugarIndex("ux_{table}_token_hash", nameof(TokenHash), OrderByType.Asc, true)]
[SugarIndex("ix_{table}_tenant_subject", nameof(TenantId), OrderByType.Asc, nameof(Subject), OrderByType.Asc)]
[SugarIndex("ix_{table}_expires_at", nameof(ExpiresAt), OrderByType.Asc)]
public class SysAuthRefreshToken : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthRefreshToken() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthRefreshToken(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 签发时的租户标识，0 表示平台
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = false, ColumnDescription = "签发时的租户标识，0 表示平台")]
    public long TenantId { get; set; }

    /// <summary>
    /// 刷新令牌的 SHA-256 哈希
    /// </summary>
    [SugarColumn(ColumnName = "Token_Hash", Length = 64, IsNullable = false, ColumnDescription = "刷新令牌的 SHA-256 哈希")]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// 主体标识
    /// </summary>
    [SugarColumn(ColumnName = "Subject", Length = 256, IsNullable = true, ColumnDescription = "主体标识")]
    public string? Subject { get; set; }

    /// <summary>
    /// 过期时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Expires_At", IsNullable = false, ColumnDescription = "过期时间（UTC）")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 创建时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "创建时间（UTC）")]
    public DateTime CreatedTime { get; set; }

    /// <summary>
    /// 撤销时间（UTC），为空表示未撤销
    /// </summary>
    [SugarColumn(ColumnName = "Revoked_Time", IsNullable = true, ColumnDescription = "撤销时间（UTC），为空表示未撤销")]
    public DateTime? RevokedTime { get; set; }
}
```

- [ ] **Step 5: 创建令牌哈希**

`framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/RefreshTokenHasher.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

/// <summary>
/// 刷新令牌哈希
/// </summary>
public static class RefreshTokenHasher
{
    /// <summary>
    /// 计算刷新令牌的 SHA-256 哈希
    /// </summary>
    /// <param name="refreshToken">刷新令牌</param>
    /// <returns>64 位大写十六进制字符串</returns>
    public static string Hash(string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（含第 ① 份的全部用例）。

- [ ] **Step 7: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 新增刷新令牌实体、令牌哈希与配置项"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: 刷新令牌存储的保存、校验与移除

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/SqlSugarRefreshTokenStore.cs`
- Modify: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的实体与哈希；第 ① 份的 `StorageTime`
- Produces:
  - `SqlSugarRefreshTokenStore(IServiceScopeFactory scopeFactory, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider)`（Task 3 追加两个参数）
  - `void Save(string refreshToken, string? subject, DateTime expiresAt)`、`bool Validate(string refreshToken, string? subject = null)`、`void Remove(string refreshToken)`
  - 夹具新增 `SqlSugarRefreshTokenStore CreateRefreshTokenStore(ISqlSugarClientResolver? resolver = null)`（Task 3 改签名）与私有 `IServiceScopeFactory CreateScopeFactory(ISqlSugarClientResolver resolver)`

**参考来源（动手前先读）：**
- 契约与默认实现：`framework/src/XiHan.Framework.Authentication/Jwt/IRefreshTokenStore.cs`、`Jwt/DefaultRefreshTokenStore.cs`
- 调用方：`framework/src/XiHan.Framework.Authentication/Jwt/JwtTokenService.cs:40-77`、`193-225`
- 存储语义：spec 第 4.4 节

**本任务禁止事项：** 硬约束 ①③④。本任务的 `Validate` 遇到已撤销的行只返回 `false`，重用检测留给 Task 3。不要在 `Validate` 里消费令牌。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 刷新令牌存储的保存、校验与移除测试
/// </summary>
public class RefreshTokenStoreTests
{
    private const string Subject = "1001";

    /// <summary>
    /// 保存后校验通过
    /// </summary>
    [Fact]
    public void 保存后校验通过()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();

        store.Save("token-1", Subject, InOneDay(context));

        Assert.True(store.Validate("token-1", Subject));
    }

    /// <summary>
    /// 数据库只保存令牌哈希
    /// </summary>
    [Fact]
    public void 数据库只保存令牌哈希()
    {
        using var context = NewContext();

        context.CreateRefreshTokenStore().Save("token-1", Subject, InOneDay(context));

        var row = Assert.Single(context.Client.Queryable<SysAuthRefreshToken>().ToList());
        Assert.Equal(RefreshTokenHasher.Hash("token-1"), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.NotEqual("token-1", row.TokenHash);
        Assert.Equal(Subject, row.Subject);
        Assert.Null(row.RevokedTime);
    }

    /// <summary>
    /// 按实体类型解析客户端
    /// </summary>
    [Fact]
    public void 按实体类型解析客户端()
    {
        using var context = NewContext();

        context.CreateRefreshTokenStore().Save("token-1", Subject, InOneDay(context));

        Assert.Contains(typeof(SysAuthRefreshToken), context.Resolver.RequestedEntityTypes);
    }

    /// <summary>
    /// 记录签发时的租户
    /// </summary>
    [Fact]
    public void 记录签发时的租户()
    {
        using var context = NewContext();
        context.Tenant.Id = 5;

        context.CreateRefreshTokenStore().Save("token-1", Subject, InOneDay(context));

        var row = Assert.Single(context.Client.Queryable<SysAuthRefreshToken>().ToList());
        Assert.Equal(5L, row.TenantId);
    }

    /// <summary>
    /// 主体不符时校验失败
    /// </summary>
    [Fact]
    public void 主体不符时校验失败()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));

        Assert.False(store.Validate("token-1", "2002"));
        Assert.True(store.Validate("token-1", Subject));
    }

    /// <summary>
    /// 未传主体时跳过绑定校验
    /// </summary>
    [Fact]
    public void 未传主体时跳过绑定校验()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));

        Assert.True(store.Validate("token-1"));
        Assert.True(store.Validate("token-1", " "));
    }

    /// <summary>
    /// 未知令牌校验失败
    /// </summary>
    [Fact]
    public void 未知令牌校验失败()
    {
        using var context = NewContext();

        Assert.False(context.CreateRefreshTokenStore().Validate("unknown", Subject));
    }

    /// <summary>
    /// 空白令牌不保存也不通过校验
    /// </summary>
    [Fact]
    public void 空白令牌不保存也不通过校验()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();

        store.Save(" ", Subject, InOneDay(context));
        store.Remove(" ");

        Assert.False(store.Validate(" ", Subject));
        Assert.Equal(0, context.Client.Queryable<SysAuthRefreshToken>().Count());
    }

    /// <summary>
    /// 过期后校验失败
    /// </summary>
    [Fact]
    public void 过期后校验失败()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, context.Clock.GetUtcNow().UtcDateTime.AddHours(1));

        context.Clock.Advance(TimeSpan.FromHours(2));

        Assert.False(store.Validate("token-1", Subject));
    }

    /// <summary>
    /// 过期时间已过的保存不写入
    /// </summary>
    [Fact]
    public void 过期时间已过的保存不写入()
    {
        using var context = NewContext();

        context.CreateRefreshTokenStore().Save("token-1", Subject, context.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

        Assert.Equal(0, context.Client.Queryable<SysAuthRefreshToken>().Count());
    }

    /// <summary>
    /// 移除是标记而非删除
    /// </summary>
    [Fact]
    public void 移除是标记而非删除()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));

        store.Remove("token-1");

        Assert.False(store.Validate("token-1", Subject));
        var row = Assert.Single(context.Client.Queryable<SysAuthRefreshToken>().ToList());
        Assert.NotNull(row.RevokedTime);
    }

    /// <summary>
    /// 移除未知令牌不抛出
    /// </summary>
    [Fact]
    public void 移除未知令牌不抛出()
    {
        using var context = NewContext();

        context.CreateRefreshTokenStore().Remove("unknown");

        Assert.Equal(0, context.Client.Queryable<SysAuthRefreshToken>().Count());
    }

    /// <summary>
    /// 重复保存同一令牌抛出
    /// </summary>
    [Fact]
    public void 重复保存同一令牌抛出()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));

        Assert.ThrowsAny<Exception>(() => store.Save("token-1", Subject, InOneDay(context)));
    }

    /// <summary>
    /// 本地时间的过期时间换算为 UTC
    /// </summary>
    [Fact]
    public void 本地时间的过期时间换算为UTC()
    {
        using var context = NewContext();
        var local = new DateTime(2099, 1, 1, 8, 0, 0, DateTimeKind.Local);

        context.CreateRefreshTokenStore().Save("token-1", Subject, local);

        var row = Assert.Single(context.Client.Queryable<SysAuthRefreshToken>().ToList());
        Assert.Equal(local.ToUniversalTime(), row.ExpiresAt);
    }

    private static AuthenticationTestContext NewContext()
    {
        return new AuthenticationTestContext(typeof(SysAuthRefreshToken));
    }

    private static DateTime InOneDay(AuthenticationTestContext context)
    {
        return context.Clock.GetUtcNow().UtcDateTime.AddDays(1);
    }
}
```

- [ ] **Step 2: 给夹具加刷新令牌存储工厂**

修改 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`。

using 区追加：

```csharp
using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;
```

字段区（`private readonly string _databaseFile;` 之后）追加：

```csharp
    private readonly List<ServiceProvider> _serviceProviders = [];
```

在 `CreateUserStore` 方法之后、`Dispose` 之前插入：

```csharp
    /// <summary>
    /// 创建刷新令牌存储
    /// </summary>
    /// <param name="resolver">客户端解析器，为空时使用夹具的桩解析器</param>
    /// <returns>刷新令牌存储</returns>
    public SqlSugarRefreshTokenStore CreateRefreshTokenStore(ISqlSugarClientResolver? resolver = null)
    {
        return new SqlSugarRefreshTokenStore(CreateScopeFactory(resolver ?? Resolver), IdGenerator, Clock);
    }

    private IServiceScopeFactory CreateScopeFactory(ISqlSugarClientResolver resolver)
    {
        var provider = new ServiceCollection()
            .AddSingleton(resolver)
            .AddSingleton<ICurrentTenant>(Tenant)
            .BuildServiceProvider();
        _serviceProviders.Add(provider);

        return provider.GetRequiredService<IServiceScopeFactory>();
    }
```

`Dispose` 方法体的第一行（`Client.Dispose();` 之前）插入：

```csharp
        foreach (var serviceProvider in _serviceProviders)
        {
            serviceProvider.Dispose();
        }
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到 `SqlSugarRefreshTokenStore`。

- [ ] **Step 4: 创建刷新令牌存储**

`framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/SqlSugarRefreshTokenStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

/// <summary>
/// 刷新令牌 SqlSugar 存储
/// </summary>
/// <remarks>
/// 只保存令牌的 SHA-256 哈希；移除令牌时标记撤销而不删除行。每次调用在新的依赖注入作用域中解析数据库客户端。
/// </remarks>
public class SqlSugarRefreshTokenStore : IRefreshTokenStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="scopeFactory">作用域工厂</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="timeProvider">时间提供程序</param>
    public SqlSugarRefreshTokenStore(
        IServiceScopeFactory scopeFactory,
        IDistributedIdGenerator<long> idGenerator,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _idGenerator = idGenerator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 保存刷新令牌
    /// </summary>
    /// <remarks>
    /// 过期时间不晚于当前时间时等同于移除该令牌。
    /// </remarks>
    /// <param name="refreshToken">刷新令牌</param>
    /// <param name="subject">主体标识</param>
    /// <param name="expiresAt">过期时间</param>
    public void Save(string refreshToken, string? subject, DateTime expiresAt)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var now = GetUtcNow();
        var expiresAtUtc = StorageTime.ToUtc(expiresAt);
        if (expiresAtUtc <= now)
        {
            Remove(refreshToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();

        GetClient(scope).Insertable(new SysAuthRefreshToken(_idGenerator.NextId())
        {
            TenantId = GetTenantId(scope),
            TokenHash = RefreshTokenHasher.Hash(refreshToken),
            Subject = subject,
            ExpiresAt = expiresAtUtc,
            CreatedTime = now
        }).ExecuteCommand();
    }

    /// <summary>
    /// 校验刷新令牌
    /// </summary>
    /// <param name="refreshToken">刷新令牌</param>
    /// <param name="subject">主体标识，为空白时不做绑定校验</param>
    /// <returns>令牌存在、未过期、未撤销且主体相符时返回 true</returns>
    public bool Validate(string refreshToken, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var tokenHash = RefreshTokenHasher.Hash(refreshToken);

        using var scope = _scopeFactory.CreateScope();

        var entry = GetClient(scope).Queryable<SysAuthRefreshToken>()
            .Where(item => item.TokenHash == tokenHash)
            .First();

        if (entry is null || entry.ExpiresAt <= GetUtcNow() || entry.RevokedTime is not null)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(subject) ||
               string.Equals(entry.Subject, subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// 移除刷新令牌，标记为已撤销
    /// </summary>
    /// <param name="refreshToken">刷新令牌</param>
    public void Remove(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = RefreshTokenHasher.Hash(refreshToken);
        var now = GetUtcNow();

        using var scope = _scopeFactory.CreateScope();

        GetClient(scope).Updateable<SysAuthRefreshToken>()
            .SetColumns(item => new SysAuthRefreshToken { RevokedTime = now })
            .Where(item => item.TokenHash == tokenHash && item.RevokedTime == null)
            .ExecuteCommand();
    }

    private DateTime GetUtcNow()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
    }

    private static ISqlSugarClient GetClient(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<ISqlSugarClientResolver>().GetClientForEntity<SysAuthRefreshToken>();
    }

    private static long GetTenantId(IServiceScope scope)
    {
        return scope.ServiceProvider.GetService<ICurrentTenant>()?.Id ?? 0;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `移除是标记而非删除` 失败于 `Assert.Single` 找不到行，说明 `Remove` 写成了删除（硬约束 ④）。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 刷新令牌只存哈希并以标记方式撤销"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 重用检测、级联撤销与过期清理

**Files:**
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/SqlSugarRefreshTokenStore.cs`（整份替换）
- Modify: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenReuseTests.cs`

**Interfaces:**
- Consumes: Task 1 的配置类；Task 2 的存储与夹具
- Produces:
  - 构造函数变为 `SqlSugarRefreshTokenStore(IServiceScopeFactory scopeFactory, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider, IOptions<XiHanAuthenticationSqlSugarOptions> options, ILogger<SqlSugarRefreshTokenStore> logger)`
  - 私有方法 `HandleReuse(ISqlSugarClient client, SysAuthRefreshToken entry, DateTime now)`、`CleanupIfDue(ISqlSugarClient client, DateTime now)`
  - 夹具 `CreateRefreshTokenStore(XiHanAuthenticationSqlSugarOptions? options = null, ISqlSugarClientResolver? resolver = null)`、`SqlSugarClient CreateClient(bool autoClose)`

**参考来源（动手前先读）：**
- 语义：spec「待确认的决策」R3–R7、第 4.4 节、第 5 节 ①④⑤
- 独立连接：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs:256-300`（`requiresNew` 时同样用 `CopyNew()`）
- SqlSugar：`Abstract/SugarProvider/SqlSugarProvider.cs:1868`、`SqlSugarClient.cs:1251`

**本任务禁止事项：** 硬约束 ②④⑤。清理条件只能是 `item.ExpiresAt < now`。级联撤销与清理的异常只记日志、不向外抛；`Validate` 在任何情况下都不得对已撤销的令牌返回 `true`。日志里不写令牌或哈希。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenReuseTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Options;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;
using XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 刷新令牌重用检测与过期清理测试
/// </summary>
public class RefreshTokenReuseTests
{
    private const string Subject = "1001";

    /// <summary>
    /// 已撤销令牌再次出现时撤销同一主体的全部令牌
    /// </summary>
    [Fact]
    public void 已撤销令牌再次出现时撤销同一主体的全部令牌()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));
        store.Save("token-2", Subject, InOneDay(context));
        store.Remove("token-1");

        Assert.False(store.Validate("token-1", Subject));

        Assert.False(store.Validate("token-2", Subject));
    }

    /// <summary>
    /// 级联撤销不波及其他主体
    /// </summary>
    [Fact]
    public void 级联撤销不波及其他主体()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));
        store.Save("other", "2002", InOneDay(context));
        store.Remove("token-1");

        store.Validate("token-1", Subject);

        Assert.True(store.Validate("other", "2002"));
    }

    /// <summary>
    /// 级联撤销不波及其他租户的同一主体
    /// </summary>
    [Fact]
    public void 级联撤销不波及其他租户的同一主体()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        context.Tenant.Id = 7;
        store.Save("tenant-token", Subject, InOneDay(context));
        context.Tenant.Id = null;
        store.Save("token-1", Subject, InOneDay(context));
        store.Remove("token-1");

        store.Validate("token-1", Subject);

        Assert.True(store.Validate("tenant-token", Subject));
    }

    /// <summary>
    /// 宽限期内重复使用只拒绝不级联
    /// </summary>
    [Fact]
    public void 宽限期内重复使用只拒绝不级联()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenReuseGracePeriod = TimeSpan.FromMinutes(1)
        });
        store.Save("token-1", Subject, InOneDay(context));
        store.Save("token-2", Subject, InOneDay(context));
        store.Remove("token-1");
        context.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.False(store.Validate("token-1", Subject));

        Assert.True(store.Validate("token-2", Subject));
    }

    /// <summary>
    /// 超过宽限期后重复使用触发级联
    /// </summary>
    [Fact]
    public void 超过宽限期后重复使用触发级联()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenReuseGracePeriod = TimeSpan.FromMinutes(1)
        });
        store.Save("token-1", Subject, InOneDay(context));
        store.Save("token-2", Subject, InOneDay(context));
        store.Remove("token-1");
        context.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(store.Validate("token-1", Subject));

        Assert.False(store.Validate("token-2", Subject));
    }

    /// <summary>
    /// 关闭重用检测后只拒绝不级联
    /// </summary>
    [Fact]
    public void 关闭重用检测后只拒绝不级联()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenReuseDetection = false
        });
        store.Save("token-1", Subject, InOneDay(context));
        store.Save("token-2", Subject, InOneDay(context));
        store.Remove("token-1");

        Assert.False(store.Validate("token-1", Subject));

        Assert.True(store.Validate("token-2", Subject));
    }

    /// <summary>
    /// 主体为空的令牌重复使用只拒绝
    /// </summary>
    [Fact]
    public void 主体为空的令牌重复使用只拒绝()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        store.Save("anonymous", null, InOneDay(context));
        store.Save("token-2", Subject, InOneDay(context));
        store.Remove("anonymous");

        Assert.False(store.Validate("anonymous"));

        Assert.True(store.Validate("token-2", Subject));
    }

    /// <summary>
    /// 级联撤销不随调用方事务回滚
    /// </summary>
    [Fact]
    public void 级联撤销不随调用方事务回滚()
    {
        using var context = NewContext();
        context.Client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
        var store = context.CreateRefreshTokenStore();
        store.Save("token-1", Subject, InOneDay(context));
        store.Save("token-2", Subject, InOneDay(context));
        store.Remove("token-1");

        using var transactional = context.CreateClient(autoClose: false);
        var transactionalStore = context.CreateRefreshTokenStore(resolver: new StubClientResolver(transactional));

        transactional.Ado.ExecuteCommand("BEGIN DEFERRED;");
        var reused = transactionalStore.Validate("token-1", Subject);
        transactional.Ado.ExecuteCommand("ROLLBACK;");

        Assert.False(reused);
        Assert.False(store.Validate("token-2", Subject));
    }

    /// <summary>
    /// 保存达到清理频率时删除已过期记录
    /// </summary>
    [Fact]
    public void 保存达到清理频率时删除已过期记录()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenCleanupFrequency = 1
        });
        store.Save("expired", Subject, context.Clock.GetUtcNow().UtcDateTime.AddHours(1));
        context.Clock.Advance(TimeSpan.FromHours(2));

        store.Save("fresh", Subject, InOneDay(context));

        Assert.Equal(0, CountByToken(context, "expired"));
        Assert.Equal(1, CountByToken(context, "fresh"));
    }

    /// <summary>
    /// 清理保留已撤销但未过期的记录
    /// </summary>
    [Fact]
    public void 清理保留已撤销但未过期的记录()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenCleanupFrequency = 1
        });
        store.Save("revoked", Subject, InOneDay(context));
        store.Remove("revoked");

        store.Save("fresh", Subject, InOneDay(context));

        Assert.Equal(1, CountByToken(context, "revoked"));
    }

    /// <summary>
    /// 清理频率为零时不清理
    /// </summary>
    [Fact]
    public void 清理频率为零时不清理()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenCleanupFrequency = 0
        });
        store.Save("expired", Subject, context.Clock.GetUtcNow().UtcDateTime.AddHours(1));
        context.Clock.Advance(TimeSpan.FromHours(2));

        store.Save("fresh", Subject, InOneDay(context));

        Assert.Equal(1, CountByToken(context, "expired"));
    }

    private static AuthenticationTestContext NewContext()
    {
        return new AuthenticationTestContext(typeof(SysAuthRefreshToken));
    }

    private static DateTime InOneDay(AuthenticationTestContext context)
    {
        return context.Clock.GetUtcNow().UtcDateTime.AddDays(1);
    }

    private static int CountByToken(AuthenticationTestContext context, string token)
    {
        var tokenHash = RefreshTokenHasher.Hash(token);

        return context.Client.Queryable<SysAuthRefreshToken>()
            .Where(item => item.TokenHash == tokenHash)
            .Count();
    }
}
```

- [ ] **Step 2: 夹具改为带配置与客户端工厂**

修改 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`。

using 区追加：

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using XiHan.Framework.Authentication.SqlSugar.Options;
```

把 Task 2 加的 `CreateRefreshTokenStore` 方法整个替换为：

```csharp
    /// <summary>
    /// 创建刷新令牌存储
    /// </summary>
    /// <param name="options">存储配置，为空时使用默认配置</param>
    /// <param name="resolver">客户端解析器，为空时使用夹具的桩解析器</param>
    /// <returns>刷新令牌存储</returns>
    public SqlSugarRefreshTokenStore CreateRefreshTokenStore(
        XiHanAuthenticationSqlSugarOptions? options = null,
        ISqlSugarClientResolver? resolver = null)
    {
        return new SqlSugarRefreshTokenStore(
            CreateScopeFactory(resolver ?? Resolver),
            IdGenerator,
            Clock,
            Microsoft.Extensions.Options.Options.Create(options ?? new XiHanAuthenticationSqlSugarOptions()),
            NullLogger<SqlSugarRefreshTokenStore>.Instance);
    }

    /// <summary>
    /// 创建连接同一临时库的新客户端
    /// </summary>
    /// <param name="autoClose">是否每条命令后自动关闭连接</param>
    /// <returns>客户端</returns>
    public SqlSugarClient CreateClient(bool autoClose)
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = autoClose
        });
    }
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS1729`：`SqlSugarRefreshTokenStore` 不包含采用 5 个参数的构造函数。

- [ ] **Step 4: 整份替换刷新令牌存储**

把 `framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/SqlSugarRefreshTokenStore.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Authentication.SqlSugar.Options;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

/// <summary>
/// 刷新令牌 SqlSugar 存储
/// </summary>
/// <remarks>
/// 只保存令牌的 SHA-256 哈希；移除令牌时标记撤销而不删除行。已撤销的令牌再次被校验时，
/// 按配置在独立连接上撤销同一租户下同一主体的全部未撤销令牌。每次调用在新的依赖注入作用域中解析数据库客户端。
/// </remarks>
public class SqlSugarRefreshTokenStore : IRefreshTokenStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly XiHanAuthenticationSqlSugarOptions _options;
    private readonly ILogger<SqlSugarRefreshTokenStore> _logger;
    private int _saveCount;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="scopeFactory">作用域工厂</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="timeProvider">时间提供程序</param>
    /// <param name="options">认证存储配置</param>
    /// <param name="logger">日志器</param>
    public SqlSugarRefreshTokenStore(
        IServiceScopeFactory scopeFactory,
        IDistributedIdGenerator<long> idGenerator,
        TimeProvider timeProvider,
        IOptions<XiHanAuthenticationSqlSugarOptions> options,
        ILogger<SqlSugarRefreshTokenStore> logger)
    {
        _scopeFactory = scopeFactory;
        _idGenerator = idGenerator;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 保存刷新令牌
    /// </summary>
    /// <remarks>
    /// 过期时间不晚于当前时间时等同于移除该令牌。每保存若干次先在独立连接上删除已过期的记录。
    /// </remarks>
    /// <param name="refreshToken">刷新令牌</param>
    /// <param name="subject">主体标识</param>
    /// <param name="expiresAt">过期时间</param>
    public void Save(string refreshToken, string? subject, DateTime expiresAt)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var now = GetUtcNow();
        var expiresAtUtc = StorageTime.ToUtc(expiresAt);
        if (expiresAtUtc <= now)
        {
            Remove(refreshToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var client = GetClient(scope);

        CleanupIfDue(client, now);

        client.Insertable(new SysAuthRefreshToken(_idGenerator.NextId())
        {
            TenantId = GetTenantId(scope),
            TokenHash = RefreshTokenHasher.Hash(refreshToken),
            Subject = subject,
            ExpiresAt = expiresAtUtc,
            CreatedTime = now
        }).ExecuteCommand();
    }

    /// <summary>
    /// 校验刷新令牌
    /// </summary>
    /// <remarks>
    /// 已撤销的令牌再次被校验时，按配置撤销同一租户下同一主体的全部未撤销令牌。
    /// </remarks>
    /// <param name="refreshToken">刷新令牌</param>
    /// <param name="subject">主体标识，为空白时不做绑定校验</param>
    /// <returns>令牌存在、未过期、未撤销且主体相符时返回 true</returns>
    public bool Validate(string refreshToken, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var tokenHash = RefreshTokenHasher.Hash(refreshToken);
        var now = GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var client = GetClient(scope);

        var entry = client.Queryable<SysAuthRefreshToken>()
            .Where(item => item.TokenHash == tokenHash)
            .First();

        if (entry is null || entry.ExpiresAt <= now)
        {
            return false;
        }

        if (entry.RevokedTime is not null)
        {
            HandleReuse(client, entry, now);
            return false;
        }

        return string.IsNullOrWhiteSpace(subject) ||
               string.Equals(entry.Subject, subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// 移除刷新令牌，标记为已撤销
    /// </summary>
    /// <param name="refreshToken">刷新令牌</param>
    public void Remove(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = RefreshTokenHasher.Hash(refreshToken);
        var now = GetUtcNow();

        using var scope = _scopeFactory.CreateScope();

        GetClient(scope).Updateable<SysAuthRefreshToken>()
            .SetColumns(item => new SysAuthRefreshToken { RevokedTime = now })
            .Where(item => item.TokenHash == tokenHash && item.RevokedTime == null)
            .ExecuteCommand();
    }

    /// <summary>
    /// 处理已撤销令牌的再次使用
    /// </summary>
    /// <param name="client">当前客户端</param>
    /// <param name="entry">被再次使用的令牌记录</param>
    /// <param name="now">当前 UTC 时间</param>
    private void HandleReuse(ISqlSugarClient client, SysAuthRefreshToken entry, DateTime now)
    {
        if (!_options.RefreshTokenReuseDetection)
        {
            return;
        }

        var gracePeriod = _options.RefreshTokenReuseGracePeriod;
        if (gracePeriod > TimeSpan.Zero && now - entry.RevokedTime!.Value < gracePeriod)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Subject))
        {
            _logger.LogWarning("已撤销的刷新令牌再次被使用，该令牌未绑定主体，未执行级联撤销。");
            return;
        }

        var tenantId = entry.TenantId;
        var subject = entry.Subject;

        try
        {
            using var isolated = client.CopyNew();

            var revoked = isolated.Updateable<SysAuthRefreshToken>()
                .SetColumns(item => new SysAuthRefreshToken { RevokedTime = now })
                .Where(item => item.TenantId == tenantId && item.Subject == subject && item.RevokedTime == null)
                .ExecuteCommand();

            _logger.LogWarning(
                "已撤销的刷新令牌再次被使用，已撤销租户 {TenantId} 下主体 {Subject} 的全部刷新令牌，共 {Count} 条。",
                tenantId,
                subject,
                revoked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销租户 {TenantId} 下主体 {Subject} 的刷新令牌失败。", tenantId, subject);
        }
    }

    /// <summary>
    /// 达到清理频率时在独立连接上删除已过期的记录
    /// </summary>
    /// <param name="client">当前客户端</param>
    /// <param name="now">当前 UTC 时间</param>
    private void CleanupIfDue(ISqlSugarClient client, DateTime now)
    {
        var frequency = _options.RefreshTokenCleanupFrequency;
        if (frequency <= 0 || Interlocked.Increment(ref _saveCount) % frequency != 0)
        {
            return;
        }

        try
        {
            using var isolated = client.CopyNew();

            isolated.Deleteable<SysAuthRefreshToken>()
                .Where(item => item.ExpiresAt < now)
                .ExecuteCommand();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理已过期的刷新令牌失败。");
        }
    }

    private DateTime GetUtcNow()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
    }

    private static ISqlSugarClient GetClient(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<ISqlSugarClientResolver>().GetClientForEntity<SysAuthRefreshToken>();
    }

    private static long GetTenantId(IServiceScope scope)
    {
        return scope.ServiceProvider.GetService<ICurrentTenant>()?.Id ?? 0;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，含 Task 2 的全部用例。

逐项排查：

- `级联撤销不随调用方事务回滚` 失败于最后一个断言（`token-2` 仍有效）：级联在解析器给的客户端上执行了，被 `ROLLBACK` 撤回（硬约束 ②）
- `级联撤销不随调用方事务回滚` 卡住约 30 秒后失败：`transactional` 持有写锁——检查 `BEGIN` 是否写成了 `BEGIN DEFERRED;`，以及 `PRAGMA journal_mode=WAL;` 是否在其他连接打开之前执行；**不要**改用 `Ado.BeginTran()`（Microsoft.Data.Sqlite 默认开 `BEGIN IMMEDIATE`）
- `已撤销令牌再次出现时撤销同一主体的全部令牌` 失败：检查宽限期判断是否写成了 `<=`（硬约束 ⑤）
- `清理保留已撤销但未过期的记录` 失败：清理条件多了撤销条件（硬约束 ④）

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 刷新令牌重用检测、级联撤销与过期清理"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

提交信息正文写明：以主体近似令牌家族的原因与代价、级联撤销与清理走独立连接的原因（spec R3、R7、第 5 节 ①），代码注释里不写。

---

### Task 4: 与 JwtTokenService 的集成

**Files:**
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenRotationTests.cs`

**Interfaces:**
- Consumes: Task 3 的存储与夹具；主包 `XiHan.Framework.Authentication.Jwt.JwtTokenService`、`JwtOptions`、`IRefreshTokenStore`
- Produces: 无（验证任务）

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Authentication/Jwt/JwtTokenService.cs` 全文

**本任务禁止事项：** 不改 `JwtTokenService`。不连续刷新两级（spec 第 6 节最后一条）——第二级的有效性用 `store.Validate` 断言。

本任务只新增用例，Task 3 正确时**直接通过**；失败回 Task 3 修，不改用例。

- [ ] **Step 1: 写集成测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenRotationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Security.Claims;
using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Options;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 刷新令牌存储与 JwtTokenService 的轮换集成测试
/// </summary>
public class RefreshTokenRotationTests
{
    /// <summary>
    /// 刷新后旧令牌失效
    /// </summary>
    [Fact]
    public void 刷新后旧令牌失效()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        var jwtTokenService = CreateJwtTokenService(store);
        var issued = jwtTokenService.GenerateAccessToken(CreateClaims("1001"));

        var refreshed = jwtTokenService.RefreshAccessToken(issued.AccessToken, issued.RefreshToken);

        Assert.NotNull(refreshed);
        Assert.NotEqual(issued.RefreshToken, refreshed.RefreshToken);
        Assert.Null(jwtTokenService.RefreshAccessToken(issued.AccessToken, issued.RefreshToken));
    }

    /// <summary>
    /// 重复使用旧令牌使新令牌失效
    /// </summary>
    [Fact]
    public void 重复使用旧令牌使新令牌失效()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        var jwtTokenService = CreateJwtTokenService(store);
        var issued = jwtTokenService.GenerateAccessToken(CreateClaims("1001"));
        var refreshed = jwtTokenService.RefreshAccessToken(issued.AccessToken, issued.RefreshToken);
        Assert.NotNull(refreshed);

        var replay = jwtTokenService.RefreshAccessToken(issued.AccessToken, issued.RefreshToken);

        Assert.Null(replay);
        Assert.False(store.Validate(refreshed.RefreshToken, "1001"));
    }

    /// <summary>
    /// 宽限期内重复使用不影响新令牌
    /// </summary>
    [Fact]
    public void 宽限期内重复使用不影响新令牌()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore(new XiHanAuthenticationSqlSugarOptions
        {
            RefreshTokenReuseGracePeriod = TimeSpan.FromMinutes(1)
        });
        var jwtTokenService = CreateJwtTokenService(store);
        var issued = jwtTokenService.GenerateAccessToken(CreateClaims("1001"));
        var refreshed = jwtTokenService.RefreshAccessToken(issued.AccessToken, issued.RefreshToken);
        Assert.NotNull(refreshed);

        var replay = jwtTokenService.RefreshAccessToken(issued.AccessToken, issued.RefreshToken);

        Assert.Null(replay);
        Assert.True(store.Validate(refreshed.RefreshToken, "1001"));
    }

    /// <summary>
    /// 数据库中没有令牌明文
    /// </summary>
    [Fact]
    public void 数据库中没有令牌明文()
    {
        using var context = NewContext();
        var jwtTokenService = CreateJwtTokenService(context.CreateRefreshTokenStore());

        var issued = jwtTokenService.GenerateAccessToken(CreateClaims("1001"));

        var rows = context.Client.Queryable<SysAuthRefreshToken>().ToList();
        Assert.DoesNotContain(rows, row => row.TokenHash == issued.RefreshToken || row.Subject == issued.RefreshToken);
        Assert.Contains(rows, row => row.TokenHash == RefreshTokenHasher.Hash(issued.RefreshToken) && row.Subject == "1001");
    }

    /// <summary>
    /// 主体不符的访问令牌不能刷新
    /// </summary>
    [Fact]
    public void 主体不符的访问令牌不能刷新()
    {
        using var context = NewContext();
        var store = context.CreateRefreshTokenStore();
        var jwtTokenService = CreateJwtTokenService(store);
        var alice = jwtTokenService.GenerateAccessToken(CreateClaims("1001"));
        var bob = jwtTokenService.GenerateAccessToken(CreateClaims("2002"));

        var result = jwtTokenService.RefreshAccessToken(bob.AccessToken, alice.RefreshToken);

        Assert.Null(result);
        Assert.True(store.Validate(alice.RefreshToken, "1001"));
    }

    private static AuthenticationTestContext NewContext()
    {
        return new AuthenticationTestContext(typeof(SysAuthRefreshToken));
    }

    private static JwtTokenService CreateJwtTokenService(IRefreshTokenStore store)
    {
        return new JwtTokenService(
            Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                SecretKey = "xihan-authentication-sqlsugar-tests-secret-key-0123456789",
                Issuer = "xihan-tests",
                Audience = "xihan-tests"
            }),
            store);
    }

    private static List<Claim> CreateClaims(string userId)
    {
        return
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, $"user-{userId}")
        ];
    }
}
```

- [ ] **Step 2: 运行测试**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `刷新后旧令牌失效` 的第一次刷新就得到 `null`：`JwtTokenService.RefreshAccessToken` 吞掉了异常，在该方法 `catch` 处临时打断点或把用例改成直接调 `store.Validate(issued.RefreshToken, "1001")` 定位是签名校验还是存储的问题；定位后恢复用例原样。

- [ ] **Step 3: 提交**

```bash
git add framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RefreshTokenRotationTests.cs
git commit -m "test(authentication-sqlsugar): 刷新令牌存储与 JwtTokenService 的轮换集成"
```

---

### Task 5: 第三方登录实体与存储

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Entities/SysAuthExternalLogin.cs`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/ExternalLogins/SqlSugarExternalLoginStore.cs`
- Modify: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/ExternalLoginStoreTests.cs`

**Interfaces:**
- Consumes: 第 ① 份的夹具；主包 `XiHan.Framework.Authentication.OAuth.IExternalLoginStore`、`ExternalLoginInfo`
- Produces:
  - `SysAuthExternalLogin : SugarEntity<long>`：`long TenantId`、`long UserId`、`string Provider`、`string ProviderKey`、`string? DisplayName`、`string? Email`、`string? AvatarUrl`、`DateTime CreatedTime`
  - `SqlSugarExternalLoginStore(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider)`
  - 夹具新增 `SqlSugarExternalLoginStore CreateExternalLoginStore()`

**参考来源（动手前先读）：**
- 契约与默认实现：`framework/src/XiHan.Framework.Authentication/OAuth/IExternalLoginStore.cs`、`OAuth/DefaultExternalLoginStore.cs`、`OAuth/ExternalLoginInfo.cs`
- 语义：spec「待确认的决策」E1–E9、第 4.5 节、第 5 节 ⑥⑦

**本任务禁止事项：** 硬约束 ⑥。`tenantId` 为空时**不要**落到 0。`CreateAsync` 不覆盖已有绑定。不做软删除。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/ExternalLoginStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.OAuth;
using XiHan.Framework.Authentication.SqlSugar.Entities;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 第三方登录存储测试
/// </summary>
public class ExternalLoginStoreTests
{
    /// <summary>
    /// 绑定后可按提供商账号查到用户
    /// </summary>
    [Fact]
    public async Task 绑定后可按提供商账号查到用户()
    {
        using var context = NewContext();

        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("github", "gh-1"));

        Assert.Equal(1001L, await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1"));
    }

    /// <summary>
    /// 按实体类型解析客户端
    /// </summary>
    [Fact]
    public async Task 按实体类型解析客户端()
    {
        using var context = NewContext();

        await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1");

        Assert.Contains(typeof(SysAuthExternalLogin), context.Resolver.RequestedEntityTypes);
    }

    /// <summary>
    /// 未绑定或参数空白时返回空
    /// </summary>
    [Fact]
    public async Task 未绑定或参数空白时返回空()
    {
        using var context = NewContext();
        var store = context.CreateExternalLoginStore();

        Assert.Null(await store.FindUserIdAsync("github", "none"));
        Assert.Null(await store.FindUserIdAsync(" ", "gh-1"));
        Assert.Null(await store.FindUserIdAsync("github", " "));
    }

    /// <summary>
    /// 提供商名称不区分大小写
    /// </summary>
    [Fact]
    public async Task 提供商名称不区分大小写()
    {
        using var context = NewContext();

        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("GitHub", "gh-1"));

        Assert.Equal(1001L, await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1"));
        var row = Assert.Single(context.Client.Queryable<SysAuthExternalLogin>().ToList());
        Assert.Equal("github", row.Provider);
    }

    /// <summary>
    /// 提供商用户标识区分大小写
    /// </summary>
    [Fact]
    public async Task 提供商用户标识区分大小写()
    {
        using var context = NewContext();

        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("weixin", "OpenId-AbC"));

        Assert.Null(await context.CreateExternalLoginStore().FindUserIdAsync("weixin", "openid-abc"));
        Assert.Equal(1001L, await context.CreateExternalLoginStore().FindUserIdAsync("weixin", "OpenId-AbC"));
    }

    /// <summary>
    /// 未指定租户时使用当前租户
    /// </summary>
    [Fact]
    public async Task 未指定租户时使用当前租户()
    {
        using var context = NewContext();
        context.Tenant.Id = 5;
        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("github", "gh-1"));

        context.Tenant.Id = null;
        var inPlatform = await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1");
        var explicitTenant = await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1", 5);
        context.Tenant.Id = 5;
        var inTenant = await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1");

        Assert.Null(inPlatform);
        Assert.Equal(1001L, explicitTenant);
        Assert.Equal(1001L, inTenant);
        var row = Assert.Single(context.Client.Queryable<SysAuthExternalLogin>().ToList());
        Assert.Equal(5L, row.TenantId);
    }

    /// <summary>
    /// 显式租户优先于当前租户
    /// </summary>
    [Fact]
    public async Task 显式租户优先于当前租户()
    {
        using var context = NewContext();
        context.Tenant.Id = 5;

        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("github", "gh-1"), 7);

        Assert.Null(await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1"));
        Assert.Equal(1001L, await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1", 7));
    }

    /// <summary>
    /// 同一用户重复绑定是幂等的
    /// </summary>
    [Fact]
    public async Task 同一用户重复绑定是幂等的()
    {
        using var context = NewContext();

        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("github", "gh-1"));
        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("github", "gh-1"));

        Assert.Equal(1, await context.Client.Queryable<SysAuthExternalLogin>().CountAsync());
    }

    /// <summary>
    /// 已绑定到其他用户时拒绝改绑
    /// </summary>
    [Fact]
    public async Task 已绑定到其他用户时拒绝改绑()
    {
        using var context = NewContext();
        await context.CreateExternalLoginStore().CreateAsync(1001, NewInfo("github", "gh-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.CreateExternalLoginStore().CreateAsync(2002, NewInfo("github", "gh-1")));

        Assert.Equal(1001L, await context.CreateExternalLoginStore().FindUserIdAsync("github", "gh-1"));
    }

    /// <summary>
    /// 移除只删除该用户在该提供商下的绑定
    /// </summary>
    [Fact]
    public async Task 移除只删除该用户在该提供商下的绑定()
    {
        using var context = NewContext();
        var store = context.CreateExternalLoginStore();
        await store.CreateAsync(1001, NewInfo("github", "gh-1"));
        await store.CreateAsync(1001, NewInfo("gitee", "ge-1"));
        await store.CreateAsync(2002, NewInfo("github", "gh-2"));

        await context.CreateExternalLoginStore().RemoveAsync(1001, "GitHub");

        var reader = context.CreateExternalLoginStore();
        Assert.Null(await reader.FindUserIdAsync("github", "gh-1"));
        Assert.Equal(1001L, await reader.FindUserIdAsync("gitee", "ge-1"));
        Assert.Equal(2002L, await reader.FindUserIdAsync("github", "gh-2"));
    }

    /// <summary>
    /// 参数校验
    /// </summary>
    [Fact]
    public async Task 参数校验()
    {
        using var context = NewContext();
        var store = context.CreateExternalLoginStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CreateAsync(1001, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CreateAsync(0, NewInfo("github", "gh-1")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(1001, NewInfo(" ", "gh-1")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(1001, NewInfo("github", " ")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RemoveAsync(1001, " "));
    }

    /// <summary>
    /// 展示信息超长时截断
    /// </summary>
    [Fact]
    public async Task 展示信息超长时截断()
    {
        using var context = NewContext();
        var info = NewInfo("github", "gh-1");
        info.DisplayName = new string('名', 300);
        info.AvatarUrl = $"https://example.com/{new string('a', 3000)}";

        await context.CreateExternalLoginStore().CreateAsync(1001, info);

        var row = Assert.Single(context.Client.Queryable<SysAuthExternalLogin>().ToList());
        Assert.Equal(256, row.DisplayName?.Length);
        Assert.Equal(2048, row.AvatarUrl?.Length);
        Assert.Equal("gh-1", row.ProviderKey);
    }

    private static AuthenticationTestContext NewContext()
    {
        return new AuthenticationTestContext(typeof(SysAuthExternalLogin));
    }

    private static ExternalLoginInfo NewInfo(string provider, string providerKey)
    {
        return new ExternalLoginInfo
        {
            Provider = provider,
            ProviderKey = providerKey,
            DisplayName = "Alice",
            Email = "alice@example.com"
        };
    }
}
```

- [ ] **Step 2: 给夹具加第三方登录存储工厂**

修改 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`。

using 区追加：

```csharp
using XiHan.Framework.Authentication.SqlSugar.ExternalLogins;
```

在 `CreateClient` 方法之后插入：

```csharp
    /// <summary>
    /// 创建第三方登录存储，每次返回新实例
    /// </summary>
    /// <returns>第三方登录存储</returns>
    public SqlSugarExternalLoginStore CreateExternalLoginStore()
    {
        return new SqlSugarExternalLoginStore(Resolver, Tenant, IdGenerator, Clock);
    }
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到 `SysAuthExternalLogin`、`SqlSugarExternalLoginStore`。

- [ ] **Step 4: 创建第三方登录实体**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Entities/SysAuthExternalLogin.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Authentication.SqlSugar.Entities;

/// <summary>
/// 第三方登录绑定实体
/// </summary>
[SugarTable("sys_auth_external_login")]
[SugarIndex("ux_{table}_tenant_provider_key", nameof(TenantId), OrderByType.Asc, nameof(Provider), OrderByType.Asc, nameof(ProviderKey), OrderByType.Asc, true)]
[SugarIndex("ix_{table}_user_provider", nameof(UserId), OrderByType.Asc, nameof(Provider), OrderByType.Asc)]
public class SysAuthExternalLogin : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthExternalLogin() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthExternalLogin(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 租户标识，0 表示平台
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = false, ColumnDescription = "租户标识，0 表示平台")]
    public long TenantId { get; set; }

    /// <summary>
    /// 内部用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = false, ColumnDescription = "内部用户标识")]
    public long UserId { get; set; }

    /// <summary>
    /// 提供商名称（小写）
    /// </summary>
    [SugarColumn(ColumnName = "Provider", Length = 64, IsNullable = false, ColumnDescription = "提供商名称（小写）")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// 提供商用户标识
    /// </summary>
    [SugarColumn(ColumnName = "Provider_Key", Length = 256, IsNullable = false, ColumnDescription = "提供商用户标识")]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// 提供商返回的显示名称
    /// </summary>
    [SugarColumn(ColumnName = "Display_Name", Length = 256, IsNullable = true, ColumnDescription = "提供商返回的显示名称")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// 提供商返回的邮箱
    /// </summary>
    [SugarColumn(ColumnName = "Email", Length = 256, IsNullable = true, ColumnDescription = "提供商返回的邮箱")]
    public string? Email { get; set; }

    /// <summary>
    /// 提供商返回的头像地址
    /// </summary>
    [SugarColumn(ColumnName = "Avatar_Url", Length = 2048, IsNullable = true, ColumnDescription = "提供商返回的头像地址")]
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// 绑定时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "绑定时间（UTC）")]
    public DateTime CreatedTime { get; set; }
}
```

- [ ] **Step 5: 创建第三方登录存储**

`framework/src/XiHan.Framework.Authentication.SqlSugar/ExternalLogins/SqlSugarExternalLoginStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Authentication.OAuth;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.ExternalLogins;

/// <summary>
/// 第三方登录绑定 SqlSugar 存储
/// </summary>
/// <remarks>
/// 未指定租户时使用当前租户。同一第三方账号已绑定其他用户时拒绝再次绑定。
/// 提供商名称不区分大小写，提供商用户标识区分大小写。
/// </remarks>
public class SqlSugarExternalLoginStore : IExternalLoginStore
{
    private const int MaxDisplayNameLength = 256;
    private const int MaxEmailLength = 256;
    private const int MaxAvatarUrlLength = 2048;

    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="timeProvider">时间提供程序</param>
    public SqlSugarExternalLoginStore(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IDistributedIdGenerator<long> idGenerator,
        TimeProvider timeProvider)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _idGenerator = idGenerator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 根据提供商和提供商用户标识查找关联的内部用户标识
    /// </summary>
    /// <param name="provider">提供商名称</param>
    /// <param name="providerKey">提供商用户标识</param>
    /// <param name="tenantId">租户标识，为空时使用当前租户</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>内部用户标识，未绑定时返回空</returns>
    public async Task<long?> FindUserIdAsync(string provider, string providerKey, long? tenantId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        var entity = await FindAsync(GetClient(), ResolveTenantId(tenantId), provider, providerKey);

        return entity?.UserId;
    }

    /// <summary>
    /// 创建第三方登录绑定记录
    /// </summary>
    /// <remarks>
    /// 已绑定到同一用户时不做任何事。
    /// </remarks>
    /// <param name="userId">内部用户标识</param>
    /// <param name="info">第三方登录信息</param>
    /// <param name="tenantId">租户标识，为空时使用当前租户</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <exception cref="ArgumentOutOfRangeException">用户标识不是正数</exception>
    /// <exception cref="ArgumentException">提供商名称或提供商用户标识为空白</exception>
    /// <exception cref="InvalidOperationException">该第三方账号已绑定到其他用户</exception>
    public async Task CreateAsync(long userId, ExternalLoginInfo info, long? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        if (string.IsNullOrWhiteSpace(info.Provider) || string.IsNullOrWhiteSpace(info.ProviderKey))
        {
            throw new ArgumentException("提供商名称与提供商用户标识不能为空", nameof(info));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = GetClient();
        var effectiveTenantId = ResolveTenantId(tenantId);
        var existing = await FindAsync(client, effectiveTenantId, info.Provider, info.ProviderKey);

        if (existing is not null)
        {
            if (existing.UserId == userId)
            {
                return;
            }

            throw new InvalidOperationException($"{info.Provider} 账号已绑定到其他用户");
        }

        await client.Insertable(new SysAuthExternalLogin(_idGenerator.NextId())
        {
            TenantId = effectiveTenantId,
            UserId = userId,
            Provider = NormalizeProvider(info.Provider),
            ProviderKey = info.ProviderKey,
            DisplayName = Truncate(info.DisplayName, MaxDisplayNameLength),
            Email = Truncate(info.Email, MaxEmailLength),
            AvatarUrl = Truncate(info.AvatarUrl, MaxAvatarUrlLength),
            CreatedTime = _timeProvider.GetUtcNow().UtcDateTime
        }).ExecuteCommandAsync();
    }

    /// <summary>
    /// 删除该用户在指定提供商下的全部绑定记录
    /// </summary>
    /// <param name="userId">内部用户标识</param>
    /// <param name="provider">提供商名称</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <exception cref="ArgumentException">提供商名称为空白</exception>
    public async Task RemoveAsync(long userId, string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("提供商名称不能为空", nameof(provider));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedProvider = NormalizeProvider(provider);

        await GetClient().Deleteable<SysAuthExternalLogin>()
            .Where(item => item.UserId == userId && item.Provider == normalizedProvider)
            .ExecuteCommandAsync();
    }

    private ISqlSugarClient GetClient()
    {
        return _clientResolver.GetClientForEntity<SysAuthExternalLogin>();
    }

    private long ResolveTenantId(long? tenantId)
    {
        return tenantId ?? _currentTenant.Id ?? 0;
    }

    private static async Task<SysAuthExternalLogin?> FindAsync(ISqlSugarClient client, long tenantId, string provider, string providerKey)
    {
        var normalizedProvider = NormalizeProvider(provider);

        var candidates = await client.Queryable<SysAuthExternalLogin>()
            .Where(item => item.TenantId == tenantId && item.Provider == normalizedProvider && item.ProviderKey == providerKey)
            .ToListAsync();

        return candidates.FirstOrDefault(item => string.Equals(item.ProviderKey, providerKey, StringComparison.Ordinal));
    }

    private static string NormalizeProvider(string provider)
    {
        return provider.ToLowerInvariant();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        return value is null || value.Length <= maxLength ? value : value[..maxLength];
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `未指定租户时使用当前租户` 的 `inPlatform` 不为空：`ResolveTenantId` 在 `tenantId` 为空时落到了 0 而非当前租户——注意本用例是在租户 5 下建的绑定，平台上下文查不到才对。

- [ ] **Step 7: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 第三方登录绑定落库，按当前租户隔离并拒绝改绑"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 6: 注册补齐

**Files:**
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs`（整份替换）
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/XiHanAuthenticationSqlSugarModule.cs`
- Modify: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3、Task 5 的两个存储；Task 1 的配置类
- Produces: `AddXiHanAuthenticationSqlSugar` 注册三个存储与配置节

**参考来源（动手前先读）：**
- 主包注册：`framework/src/XiHan.Framework.Authentication/Extensions/DependencyInjection/XiHanAuthenticationServiceCollectionExtensions.cs:35-43`、`OAuth/XiHanOAuthServiceCollectionExtensions.cs:33-38`
- 语义：spec 第 4.6 节、「五个共同问题」顶替方式一行

**本任务禁止事项：** 硬约束 ⑦。不要按 OAuth 开关条件注册。模块类只调一个扩展方法。

- [ ] **Step 1: 写失败的测试**

修改 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RegistrationTests.cs`。

using 区追加：

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.OAuth;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.ExternalLogins;
using XiHan.Framework.Authentication.SqlSugar.Options;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;
```

在 `空参数抛出` 用例之后、类的结束大括号之前插入：

```csharp
    /// <summary>
    /// 刷新令牌存储被顶替为单例的 SqlSugar 实现
    /// </summary>
    [Fact]
    public void 刷新令牌存储被顶替为单例的SqlSugar实现()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<IRefreshTokenStore, DefaultRefreshTokenStore>();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IRefreshTokenStore));
        Assert.Equal(typeof(SqlSugarRefreshTokenStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// 第三方登录存储被顶替为作用域的 SqlSugar 实现
    /// </summary>
    [Fact]
    public void 第三方登录存储被顶替为作用域的SqlSugar实现()
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IExternalLoginStore, DefaultExternalLoginStore>();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IExternalLoginStore));
        Assert.Equal(typeof(SqlSugarExternalLoginStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 未启用第三方登录时也注册第三方登录存储
    /// </summary>
    [Fact]
    public void 未启用第三方登录时也注册第三方登录存储()
    {
        var services = new ServiceCollection();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IExternalLoginStore));
        Assert.Equal(typeof(SqlSugarExternalLoginStore), descriptor.ImplementationType);
    }

    /// <summary>
    /// 绑定配置节
    /// </summary>
    [Fact]
    public void 绑定配置节()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Authentication:SqlSugar:RefreshTokenReuseDetection"] = "false",
                ["XiHan:Authentication:SqlSugar:RefreshTokenReuseGracePeriod"] = "00:00:30",
                ["XiHan:Authentication:SqlSugar:RefreshTokenCleanupFrequency"] = "16"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddXiHanAuthenticationSqlSugar(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<XiHanAuthenticationSqlSugarOptions>>().Value;
        Assert.False(options.RefreshTokenReuseDetection);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RefreshTokenReuseGracePeriod);
        Assert.Equal(16, options.RefreshTokenCleanupFrequency);
    }

    /// <summary>
    /// 校验作用域的容器能解析刷新令牌存储
    /// </summary>
    [Fact]
    public void 校验作用域的容器能解析刷新令牌存储()
    {
        using var context = new AuthenticationTestContext(typeof(SysAuthRefreshToken));
        var services = new ServiceCollection();
        services.AddScoped<ISqlSugarClientResolver>(_ => context.Resolver);
        services.AddScoped<ICurrentTenant>(_ => context.Tenant);
        services.AddSingleton(context.IdGenerator);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var store = provider.GetRequiredService<IRefreshTokenStore>();
        store.Save("token-1", "1001", DateTime.UtcNow.AddDays(1));

        Assert.True(store.Validate("token-1", "1001"));
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：`刷新令牌存储被顶替为单例的SqlSugar实现` 失败（实现类型仍是 `DefaultRefreshTokenStore`）；`第三方登录存储被顶替为作用域的SqlSugar实现` 与 `未启用第三方登录时也注册第三方登录存储` 失败；`绑定配置节` 失败于默认值；`校验作用域的容器能解析刷新令牌存储` 失败（没有注册 `IRefreshTokenStore`）。

- [ ] **Step 3: 整份替换注册扩展**

把 `framework/src/XiHan.Framework.Authentication.SqlSugar/Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.OAuth;
using XiHan.Framework.Authentication.SqlSugar.ExternalLogins;
using XiHan.Framework.Authentication.SqlSugar.Options;
using XiHan.Framework.Authentication.SqlSugar.RefreshTokens;
using XiHan.Framework.Authentication.SqlSugar.Users;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 认证存储 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanAuthenticationSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换认证模块的用户、刷新令牌与第三方登录存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanAuthenticationSqlSugar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanAuthenticationSqlSugarOptions>(
            configuration.GetSection(XiHanAuthenticationSqlSugarOptions.SectionName));

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.Replace(ServiceDescriptor.Scoped<IUserStore, SqlSugarUserStore>());
        services.Replace(ServiceDescriptor.Singleton<IRefreshTokenStore, SqlSugarRefreshTokenStore>());
        services.Replace(ServiceDescriptor.Scoped<IExternalLoginStore, SqlSugarExternalLoginStore>());

        return services;
    }
}
```

- [ ] **Step 4: 更新模块类注释**

`framework/src/XiHan.Framework.Authentication.SqlSugar/XiHanAuthenticationSqlSugarModule.cs` 的 `<remarks>` 由：

```csharp
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 用户存储替换认证模块的内存用户存储。
/// </remarks>
```

改为：

```csharp
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 存储替换认证模块的用户、刷新令牌与第三方登录的内存存储。
/// 配置节：<c>XiHan:Authentication:SqlSugar</c>。
/// </remarks>
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `校验作用域的容器能解析刷新令牌存储` 报 `Cannot consume scoped service ... from singleton`：刷新令牌存储的构造函数注入了 Scoped 依赖（硬约束 ③）。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 以 Replace 顶替刷新令牌与第三方登录的内存存储"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 7: 包 README

**Files:**
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/README.md`（整份重写）

**Interfaces:**
- Consumes: 前六个任务与第 ① 份的全部产出
- Produces: 无

**参考来源（动手前先读）：**
- 已知边界：spec 第 7 节，以及第 ① 份 spec 第 7 节
- 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`

**本任务禁止事项：** 七段结构固定，不增不减。

- [ ] **Step 1: 重写包 README**

把 `framework/src/XiHan.Framework.Authentication.SqlSugar/README.md` 整份替换为：

````markdown
# XiHan.Framework.Authentication.SqlSugar

## 概述

`XiHan.Framework.Authentication` 的认证存储 SqlSugar 持久化提供程序。主包的用户、刷新令牌、第三方登录三个存储都是内存实现（用户存储还是作用域内的空字典），进程重启即丢、多实例之间不共享；本包把它们落到数据库。

## 核心能力

- `IUserStore` → `SqlSugarUserStore`（表 `sys_auth_user`）：用户名查找与唯一约束不区分大小写、按当前租户隔离、登录失败次数原子累加、契约外提供 `AddUserAsync`
- `IRefreshTokenStore` → `SqlSugarRefreshTokenStore`（表 `sys_auth_refresh_token`）：只存令牌的 SHA-256 哈希、撤销时标记而不删行、已撤销令牌再次出现时撤销同一主体的全部令牌、保存时顺带清理过期记录
- `IExternalLoginStore` → `SqlSugarExternalLoginStore`（表 `sys_auth_external_login`）：未指定租户时按当前租户、拒绝把第三方账号改绑到其他用户
- 三者均以 `Replace` 顶替主包的默认实现：用户与第三方登录为作用域，刷新令牌为单例
- 表结构由 `DbInitializer` 在应用启动时创建，**必须开启** `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization`（二者默认均为 `false`）

## 依赖关系

依赖 `XiHan.Framework.Authentication`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问、雪花主键）。

## 配置与约定

表名 `sys_auth_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。未开启自动建表时，首次读写即抛「表不存在」；`TableInitialization.Mode` 为 `OptIn` 时本包的表不会自动创建。

配置节 `XiHan:Authentication:SqlSugar`：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `RefreshTokenReuseDetection` | `true` | 已撤销的刷新令牌再次被校验时，撤销同一租户下同一主体的全部未撤销令牌 |
| `RefreshTokenReuseGracePeriod` | `00:00:00` | 令牌撤销后在此时长内再次出现只拒绝、不级联；为 0 时任何重复使用都级联 |
| `RefreshTokenCleanupFrequency` | `256` | 每保存多少次刷新令牌清理一次已过期记录；小于等于 0 时不清理 |

**用户**：`UserInfo.UserId` 是 `Basic_Id` 的十进制字符串，非正整数的标识视为不存在。用户名另存 `Normalized_User_Name`（`ToUpperInvariant()`），`Alice` 与 `alice` 是同一个用户——与主包大小写敏感的默认实现不同。所有读写都带当前租户条件（无租户为 0），租户与平台之间互不可见。存储层对密码哈希、恢复码、双因素密钥原样存取；**双因素密钥以明文落库**。`UpdateUserAsync` **不写密码哈希**，改密码只能经 `UpdatePasswordAsync`。同一请求作用域内对同一用户的多次读取返回同一个 `UserInfo` 实例。

**刷新令牌**：数据库与日志里都没有令牌原文。`JwtTokenService` 每次刷新签发新令牌、撤销旧令牌；旧令牌若再次出现，视为被盗用，同一租户下同一主体的全部令牌被撤销，用户**所有设备**都需要重新登录。契约没有令牌家族的概念，这是以主体近似家族的代价：持有该用户任一已撤销令牌的人，在该令牌过期前可以反复触发这一撤销。单页应用多标签页同时刷新也会触发，前端应对刷新请求加互斥，或调大 `RefreshTokenReuseGracePeriod`。级联撤销与过期清理在独立连接上执行、自动提交，**不随业务事务回滚**。同一令牌的并发刷新可能各得一个新令牌（契约没有原子的「校验并消费」）；持续刷新的会话没有绝对上限。三个方法同步访问数据库。重复保存同一令牌抛唯一约束异常，不覆盖。使用租户独立库时，刷新请求必须解析到与登录时相同的租户。

**第三方登录**：`tenantId` 为空时使用当前租户，而不是主包默认实现的 0。同一第三方账号已绑定其他用户时 `CreateAsync` 抛 `InvalidOperationException`，改绑须先 `RemoveAsync`。提供商名称统一小写、不区分大小写；提供商用户标识区分大小写。`RemoveAsync` 不看租户。显示名称、邮箱、头像地址超长时截断。无论是否启用 OAuth，本包都会注册第三方登录存储。

时间一律以 UTC 存储。下游若自己也 `Replace` 了这些契约，以模块装配顺序靠后者为准。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

开启自动建表：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableDbInitialization": true,
        "EnableTableInitialization": true
      }
    }
  }
}
```

创建用户（密码须先经 `IPasswordHasher` 哈希）：

```csharp
var userId = await userStore.AddUserAsync(new UserInfo
{
    Username = "alice",
    PasswordHash = passwordHasher.HashPassword(password),
    IsActive = true
});
```

## 扩展点

需要自定义任一存储时，实现主包对应的接口并以 `services.Replace(...)` 替换：`IUserStore` 与 `IExternalLoginStore` 须注册为作用域，`IRefreshTokenStore` 须注册为单例（`JwtTokenService` 是单例并在构造函数中持有它）。

## 目录结构

```
Entities/                        用户、刷新令牌、第三方登录实体
Mapping/                         用户映射与 UTC 换算
Options/                         配置
Users/                           用户存储
RefreshTokens/                   刷新令牌存储与令牌哈希
ExternalLogins/                  第三方登录存储
Extensions/DependencyInjection/  服务注册扩展
```
````

- [ ] **Step 2: 提交**

```bash
git add framework/src/XiHan.Framework.Authentication.SqlSugar/README.md
git commit -m "docs(authentication-sqlsugar): 补写刷新令牌与第三方登录的使用约定"
```

---

### Task 8: 文档站条目、模块清单与全量验收

**Files:**
- Create: `docs/packages/authentication-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`
- Modify: `framework/README.md`、`framework/README_cn.md`
- Modify: `README.md`、`README_cn.md`、`docs/index.md`、`docs/introduction.md`、`docs/why.md`（模块计数）

**Interfaces:**
- Consumes: 全部代码产出
- Produces: 无

**参考来源（动手前先读）：**
- 包文档范本：`docs/packages/auditing-sqlsugar.md`、`docs/packages/eventbus-sqlsugar.md`
- 同类提交范本：`git show 8704e7be --stat`（`docs(auditing-sqlsugar): 补写包文档与模块清单`，看它改了哪些文件、每处怎么改）
- spec 第 4.7 节

**本任务禁止事项：** 不改 `docs/packages/authentication.md`、`docs/changelog.md`、`docs/package.json`。不顺手改任何与本包无关的文档措辞。模块计数**不要照抄本计划写作时的数字**。

- [ ] **Step 1: 创建包文档**

`docs/packages/authentication-sqlsugar.md`：

````markdown
# XiHan.Framework.Authentication.SqlSugar

> 认证存储的 SqlSugar 持久化提供程序：用户、刷新令牌、第三方登录绑定三个存储落库，替换 [Authentication](./authentication) 的内存实现。

- **NuGet**：`XiHan.Framework.Authentication.SqlSugar`
- **模块类**：`XiHanAuthenticationSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Authentication](./authentication)（存储契约）、[Data](./data)（SqlSugar 客户端、雪花主键、建表）

## 概述

[Authentication](./authentication) 定义了三个存储契约并各带一个内存实现：`DefaultUserStore` 注册为作用域，每个请求都是空字典；`DefaultRefreshTokenStore` 以令牌明文为键存在进程内；`DefaultExternalLoginStore` 同样在进程内。它们只够跑通示例。本包把三者落到数据库，并补上数据库场景下才需要的安全处理：刷新令牌只存哈希、令牌重用检测、按租户隔离、失败计数原子累加。

## 何时使用

- 应用自己没有用户体系，直接使用框架的 `DefaultAuthenticationService` 做用户名密码登录、双因素、锁定
- 需要刷新令牌在进程重启后仍然有效、在多实例之间共享
- 需要第三方登录（OAuth）的绑定关系落库

已有自己用户表的应用，通常只需要本包的刷新令牌存储；此时可以不依赖本模块，而是在自己的模块里单独 `Replace` 刷新令牌存储。

## 安装与启用

```bash
dotnet add package XiHan.Framework.Authentication.SqlSugar
```

```csharp
[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]
public class MyModule : XiHanModule { }
```

`XiHanAuthenticationSqlSugarModule.ConfigureServices` 调用 `services.AddXiHanAuthenticationSqlSugar(configuration)`，以 `services.Replace` 顶替主包的注册：

| 契约 | 默认实现 | 本包实现 | 生命周期 |
| --- | --- | --- | --- |
| `IUserStore` | `DefaultUserStore` | `SqlSugarUserStore` | 作用域 |
| `IRefreshTokenStore` | `DefaultRefreshTokenStore` | `SqlSugarRefreshTokenStore` | 单例 |
| `IExternalLoginStore` | `DefaultExternalLoginStore`（仅启用 OAuth 时注册） | `SqlSugarExternalLoginStore` | 作用域 |

建表依赖 [Data](./data) 的两个开关，**默认都是 `false`**：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableDbInitialization": true,
        "EnableTableInitialization": true
      }
    }
  }
}
```

## 表结构

**`sys_auth_user`**：唯一索引 `(Tenant_Id, Normalized_User_Name)`。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `bigint` | 雪花主键，即 `UserInfo.UserId` |
| `Tenant_Id` | `bigint` | 0 为平台 |
| `User_Name` / `Normalized_User_Name` | 128 | 原样 / 大写 |
| `Password_Hash` | 512 | 原样存储上层给出的哈希 |
| `Email` / `Phone_Number` | 256 / 32 | 不唯一 |
| `Two_Factor_Enabled` / `Two_Factor_Secret` | `bool` / 256 | 密钥明文 |
| `Recovery_Codes` | 大文本 | 恢复码哈希的 JSON 数组 |
| `Is_Locked` / `Lockout_End` / `Failed_Login_Attempts` | | 锁定与失败计数 |
| `Last_Login_Time` / `Password_Changed_Time` | `datetime` | UTC |
| `Is_Active` / `Additional_Data` | `bool` / 大文本 | |

**`sys_auth_refresh_token`**：唯一索引 `Token_Hash`，普通索引 `(Tenant_Id, Subject)`、`Expires_At`。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `bigint` | 雪花主键 |
| `Tenant_Id` | `bigint` | 签发时的租户 |
| `Token_Hash` | 64 | SHA-256 大写十六进制 |
| `Subject` | 256 | 主体（用户标识） |
| `Expires_At` / `Created_Time` / `Revoked_Time` | `datetime` | UTC；`Revoked_Time` 为空表示未撤销 |

**`sys_auth_external_login`**：唯一索引 `(Tenant_Id, Provider, Provider_Key)`，普通索引 `(User_Id, Provider)`。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `bigint` | 雪花主键 |
| `Tenant_Id` / `User_Id` | `bigint` | |
| `Provider` | 64 | 小写 |
| `Provider_Key` | 256 | 原样，区分大小写 |
| `Display_Name` / `Email` / `Avatar_Url` | 256 / 256 / 2048 | 超长截断 |
| `Created_Time` | `datetime` | UTC |

## 工作原理

### 用户存储

每条 SQL 都带 `Tenant_Id = 当前租户（无租户为 0）`，不依赖全局租户过滤器——全局过滤器对多租户实体放行 `TenantId = 0` 的平台行，用它做隔离会让租户入口能登录平台账号。

`DefaultAuthenticationService` 的几条流程是「先读出用户对象，中途调其他存储方法改库，最后把最初读出的对象交给 `UpdateUserAsync`」。为了让这样写回的对象是最新的，存储在同一个实例（同一请求作用域）内对同一用户返回同一个 `UserInfo` 实例，并在 `UpdatePasswordAsync`、失败计数、锁定方法里同步修改它。`UpdateUserAsync` 不写密码列。

失败计数以 `SET Failed_Login_Attempts = Failed_Login_Attempts + 1` 在数据库侧累加，并发的失败登录不会丢失计数。

### 刷新令牌

```
登录     Save(T1)                     → 插入 Hash(T1)
刷新     Validate(T1) → Save(T2) → Remove(T1)
                                       → 插入 Hash(T2)，标记 Hash(T1) 已撤销
盗用     Validate(T1)（T1 已撤销）     → 拒绝，并撤销同租户同主体的全部未撤销令牌（含 T2）
```

级联撤销在 `CopyNew()` 开出的独立连接上执行并立即提交。刷新失败时下游通常抛业务异常、回滚工作单元，若级联走同一事务，撤销会被一起回滚。

过期记录的清理在保存时顺带进行：每保存 `RefreshTokenCleanupFrequency` 次，在独立连接上删除 `Expires_At` 早于当前时间的行。已撤销但未过期的行保留，用于重用检测。

存储注册为单例（`JwtTokenService` 是单例并在构造函数中持有它），每次调用经 `IServiceScopeFactory` 新建作用域解析数据库客户端；当前工作单元存于 `AsyncLocal`，新作用域里同样能登记到调用方的事务。

### 第三方登录

`tenantId` 为空时回退到当前租户。查到的绑定再在内存里用序数比较一次 `Provider_Key`，避免 MySQL / SQL Server 默认排序规则下把只差大小写的两个第三方标识当成同一个。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanAuthenticationSqlSugarModule` | 模块类 |
| `SqlSugarUserStore` | `IUserStore` 实现；另有 `Task<string> AddUserAsync(UserInfo user, CancellationToken)` |
| `SqlSugarRefreshTokenStore` | `IRefreshTokenStore` 实现 |
| `SqlSugarExternalLoginStore` | `IExternalLoginStore` 实现 |
| `RefreshTokenHasher` | `string Hash(string refreshToken)` |
| `AuthUserMapper` / `StorageTime` | 用户映射 / UTC 换算 |
| `XiHanAuthenticationSqlSugarOptions` | 配置 |
| `SysAuthUser` / `SysAuthRefreshToken` / `SysAuthExternalLogin` | 实体 |

## 配置

配置节 `XiHan:Authentication:SqlSugar`：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `RefreshTokenReuseDetection` | `true` | 重用检测开关 |
| `RefreshTokenReuseGracePeriod` | `00:00:00` | 撤销后在此时长内再次出现只拒绝、不级联 |
| `RefreshTokenCleanupFrequency` | `256` | 每保存多少次清理一次过期记录，≤ 0 不清理 |

## 使用示例

### 1. 创建用户

```csharp
public class UserSeeder(IUserStore userStore, IPasswordHasher passwordHasher)
{
    public async Task SeedAsync()
    {
        if (userStore is SqlSugarUserStore store &&
            await store.GetUserByUsernameAsync("admin") is null)
        {
            await store.AddUserAsync(new UserInfo
            {
                Username = "admin",
                PasswordHash = passwordHasher.HashPassword("Change#Me2026"),
                IsActive = true
            });
        }
    }
}
```

### 2. 放宽多标签页场景的重用检测

```json
{
  "XiHan": {
    "Authentication": {
      "SqlSugar": {
        "RefreshTokenReuseGracePeriod": "00:00:10"
      }
    }
  }
}
```

宽限期内的重复使用依然被拒绝，只是不级联撤销。

### 3. 只使用刷新令牌存储

应用自有用户表、只想让刷新令牌落库时，不依赖 `XiHanAuthenticationSqlSugarModule`，在自己的模块里：

```csharp
services.Configure<XiHanAuthenticationSqlSugarOptions>(
    configuration.GetSection(XiHanAuthenticationSqlSugarOptions.SectionName));
services.TryAddSingleton<TimeProvider>(TimeProvider.System);
services.Replace(ServiceDescriptor.Singleton<IRefreshTokenStore, SqlSugarRefreshTokenStore>());
```

## 扩展点 / 自定义

- **换任一存储**：实现主包接口并 `Replace`。`IUserStore` 与 `IExternalLoginStore` 须为作用域，`IRefreshTokenStore` 须为单例
- **换时间源**：在本模块之前注册自己的 `TimeProvider`，本包以 `TryAdd` 注册系统时钟

## 注意事项与最佳实践

- **`TryAdd` 不生效**。主包已占位，覆盖必须用 `Replace`
- **双因素密钥明文落库**。契约与框架没有字段级加密抽象，数据库泄漏即泄漏全部 TOTP 密钥
- **`UpdateUserAsync` 不写密码**。改密码只能经 `UpdatePasswordAsync`
- **用户名不区分大小写**。从大小写敏感的旧数据迁入时，仅大小写不同的重名会让唯一索引建不起来
- **重用检测会让用户所有设备下线**。契约没有令牌家族，以主体近似；持有任一已撤销令牌者可在其过期前反复触发
- **多标签页同时刷新**会触发重用检测，前端应对刷新请求加互斥，或设置宽限期
- **同一令牌的并发刷新**可能各得一个新令牌，契约没有原子的「校验并消费」
- **会话没有绝对上限**，持续刷新即不过期
- **刷新令牌存储同步访问数据库**，契约是同步的
- **第三方登录的 `tenantId` 为空时用当前租户**，与默认实现的 0 不同；已绑定其他用户时拒绝改绑
- **`OptIn` 建表模式**下本包的表不会自动创建

## 依赖模块

- [XiHan.Framework.Authentication](./authentication)（三个存储契约、`JwtTokenService`、`DefaultAuthenticationService`）
- [XiHan.Framework.Data](./data)（`ISqlSugarClientResolver`、`DbInitializer`、SqlSugar 传递依赖）

`IDistributedIdGenerator<long>` 经 `XiHanDataModule → XiHanDistributedIdsModule` 间接获得。

## 相关模块

- [XiHan.Framework.Security](./security)（`IPasswordHasher`）
- [XiHan.Framework.MultiTenancy](./multitenancy)（`ICurrentTenant`）
- [XiHan.Framework.Uow](./uow)（工作单元与事务）
- [XiHan.Framework.DistributedIds](./distributed-ids)（雪花主键）
````

- [ ] **Step 2: 侧边栏**

编辑 `docs/.vitepress/config.ts`，在「安全 · 认证 · 授权」组内这一行之后：

```ts
          pkg("Authentication 认证", "authentication"),
```

插入：

```ts
          pkg("Authentication.SqlSugar", "authentication-sqlsugar"),
```

- [ ] **Step 3: 包总览表**

编辑 `docs/packages/index.md`，在「4. 安全 · 认证 · 授权」表格里这一行之后：

```markdown
| [Authentication](./authentication) | 认证：JWT / OAuth2 / OIDC、令牌工厂、MFA、SSO |
```

插入：

```markdown
| [Authentication.SqlSugar](./authentication-sqlsugar) | 认证存储 SqlSugar 提供程序：用户、刷新令牌（只存哈希、重用检测）与第三方登录绑定落库，替换内存实现 |
```

- [ ] **Step 4: 两份 framework README 的模块清单**

编辑 `framework/README.md`，在这一行之后：

```markdown
| `Authentication` | Authentication: JWT / OAuth2 / OIDC, token factory, MFA, SSO |
```

插入：

```markdown
| `Authentication.SqlSugar` | SqlSugar persistence provider for authentication stores: users, refresh tokens (hash-only, reuse detection) and external-login bindings replace the in-memory defaults from `Authentication` |
```

编辑 `framework/README_cn.md`，在这一行之后：

```markdown
| `Authentication` | 认证：JWT / OAuth2 / OIDC、令牌工厂、MFA、SSO |
```

插入：

```markdown
| `Authentication.SqlSugar` | 认证存储 SqlSugar 提供程序：用户、刷新令牌（只存哈希、重用检测）与第三方登录绑定，以 Replace 顶替 Authentication 的内存实现 |
```

- [ ] **Step 5: 模块计数加一**

**不要依赖固定的行号或片段清单**——同系列的包会先后合入，行号与数字都会漂移。按下面的方法在实现时现查。

先从根 README 的徽章读出当前计数 **N**，并核对源码目录数：

```bash
N=$(grep -o 'Modules-[0-9]*-1f6feb' README.md | head -1 | cut -d- -f2); echo "N=$N"
ls -d framework/src/*/ | wc -l
```

`framework/src` 下的目录数此时应为 **N + 1**（本包已加入）。二者不符时先查清原因再继续——可能是 `dev` 上另一个包没有同步计数。

列出所有候选：

```bash
grep -rn "$N" README.md README_cn.md framework/README.md framework/README_cn.md
grep -rn "$N" docs --include="*.md" --exclude-dir=node_modules
```

逐条审阅每个命中，**只**把表示模块 / 包 / 包文档页数的 N 改为 N + 1，每处只改数字。写本计划时（N = 68）共 20 处、分布在 8 个文件，可作核对参照，不是清单：

- `README.md`、`README_cn.md` 各 3 处，**其中一处在 shields.io 徽章 URL 里（`Modules-N-1f6feb`），最容易漏**
- `framework/README.md`、`framework/README_cn.md` 各 3 处；其中「单测工程 / unit-test projects」那一处计的是测试项目数，本包新增了 `XiHan.Framework.Authentication.SqlSugar.Tests`，所以同样加一
- `docs/index.md` 2 处、`docs/introduction.md` 1 处（「参考手册（N 页）」）、`docs/why.md` 4 处（含「包参考 N 页」）、`docs/packages/index.md` 1 处

不属于计数的命中不要改，例如 `docs/why.md` 里的外部依赖数、版本号、行号等。根 `README.md` / `README_cn.md` 的「常用包」表**不加行**——它是精选清单，两个已有的 SqlSugar 包都不在其中；本包只在两份 `framework/README*` 的模块清单表里加行（Step 4）。

改完复核：

```bash
grep -rn "$N" README.md README_cn.md framework/README.md framework/README_cn.md
grep -rn "$N" docs --include="*.md" --exclude-dir=node_modules
```

预期：剩下的命中都不是模块计数。

- [ ] **Step 6: 文档站本地构建**

```bash
cd docs && pnpm install && pnpm build
```

预期：`build complete`，无死链告警。若报 `authentication-sqlsugar` 死链，检查文件名与侧边栏的 slug 是否一致。

- [ ] **Step 7: 全量验收**

回到仓库根目录：

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过。`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败与本包无关，重跑即可。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`）：

```bash
taskkill //F //IM "XiHan.Framework.Authentication.SqlSugar.Tests.exe"
```

- [ ] **Step 8: 注释复查**

通读本包全部 `.cs` 文件（两份计划新增的全部）的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即移出到提交信息。

- [ ] **Step 9: 提交**

```bash
git add docs/packages/authentication-sqlsugar.md docs/.vitepress/config.ts docs/packages/index.md docs/index.md docs/introduction.md docs/why.md framework/README.md framework/README_cn.md README.md README_cn.md
git commit -m "docs(authentication-sqlsugar): 补写包文档与模块清单"
```

---

## 完成标准

第 ② 份完成时（亦即整个包完成时）应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动除外）
- 数据库里没有刷新令牌原文，只有 64 位大写十六进制哈希
- 经 `JwtTokenService` 刷新后旧令牌不能再刷新；旧令牌重复使用使新令牌失效；宽限期内不级联
- 级联撤销在调用方事务回滚后依然生效（`级联撤销不随调用方事务回滚` PASS）
- 已撤销未过期的行在清理后保留，已过期的行被清理
- 第三方登录：`tenantId` 为空时按当前租户；拒绝改绑；`ProviderKey` 区分大小写
- 三个存储分别以 Scoped / Singleton / Scoped 顶替主包，`ValidateScopes = true` 的容器能解析
- 文档站构建通过；侧边栏、包总览、两份 framework README 的模块清单各有一行；全部模块计数（写本计划时为 8 个文件 20 处）加一
- 每个 `.cs` 文件带版权声明；注释只写做什么

## 已知边界（写入 PR 描述，不写进代码注释）

- **无令牌家族（契约缺口）**：重用检测以「同一租户、同一主体」近似，用户所有设备一并下线；持有任一已撤销令牌者可在其过期前反复触发
- **无原子消费（契约缺口）**：同一令牌的并发刷新可能各得一个新令牌
- **会话无绝对上限**：持续刷新即不过期
- **多标签页**：宽限期为 0 时同时刷新会触发级联；前端加互斥或调大宽限期
- **同步数据库访问**：刷新令牌三个方法同步访问数据库
- **级联与清理走独立连接**：自动提交，不随业务回滚；调用方事务若已写过同一主体的令牌行，级联会等锁至超时，失败只记日志
- **清理节奏**：按保存次数触发，多实例各自计数
- **独立库租户**：刷新请求必须解析到与登录时相同的租户
- **重复保存**：抛唯一约束异常，不覆盖（与默认实现不同）
- **第三方登录**：`tenantId` 为空用当前租户、拒绝静默改绑（均与默认实现不同）；`RemoveAsync` 不看租户；无论 OAuth 是否启用都注册
- **双因素密钥明文、`UpdateUserAsync` 不写密码、用户名大小写不敏感**：见第 ① 份
- **自动建表默认关闭**；`OptIn` 模式下本包的表不会自动创建
- **破坏性变更**：无。本包是新增的可选包，不引用它的应用行为不变。**逃生口**：不依赖 `XiHanAuthenticationSqlSugarModule`，改为在自己的模块里只 `Replace` 需要的那一个存储；或在更靠后的模块里再 `Replace` 回自己的实现；重用检测可经 `XiHan:Authentication:SqlSugar:RefreshTokenReuseDetection = false` 关闭

上游作者的验收标准，提交 PR 前逐条自查：

- 「0 警告 0 错误」是硬门槛
- 一个 PR 只做一件事：本 PR 只含本包、它的测试项目、第 4.7 节列出的文档与清单；不改 `docs/packages/authentication.md`、`docs/changelog.md`
- 注释只写「这段代码做什么」，判定靠通读
- 破坏性变更要在 PR 写明并同步文档，包括逃生口（见上）

## 下一份计划

本包到此完成，两份合为一个 PR。按拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 5 节），下一个包是 `Authorization.SqlSugar`，其 spec 与计划尚未编写。
