# Security.SqlSugar 密码历史存储（1）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `XiHan.Framework.Security.SqlSugar` 包，为 `IPasswordHistoryStore` 提供 SqlSugar 落库实现，并补一个契约之外的写入方法 `RecordPasswordAsync`，用 `services.Replace` 顶替 `DefaultPasswordHistoryStore`。

**Architecture:** 单实体 `SysPasswordHistory`（雪花 `long` 主键，`SugarCreationEntity<long>`），单个存储类 `SqlSugarPasswordHistoryStore` 同时实现契约方法（读）与补充方法（写+裁剪）。不分表、不做多库路由、参与调用方所在的当前工作单元事务。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-security-sqlsugar-1-password-history-design.md`

> 该 spec 自成一体，实现本计划所需的全部约束都在其中。

**Linear 议题**：https://linear.app/elf-express/issue/EDDIE-11

**前置：** 无。`XiHan.Framework.Security`、`XiHan.Framework.Data` 均已发布，可直接引用。

---

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：实现从 `dev` 开 worktree：

```bash
git worktree add ../XiHan.Framework-security -b feat/security-sqlsugar dev
```

上游是 `main`，**绝不在 `main` 上提交**。

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

**SqlSugar 签名只信源码**：本包引用 SqlSugarCore 5.1.4.221，权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）。文档：`E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar
IInsertable<T>.ExecuteCommandAsync(CancellationToken token = default)
IDeleteable<T>.In<PkType>(List<PkType> primaryKeyValues)
IDeleteable<T>.ExecuteCommandAsync(CancellationToken token = default)
ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
ISugarQueryable<T>.OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)
ISugarQueryable<T>.Skip(int num)          // Abstract/QueryableProvider/QueryableProvider.cs:1453
ISugarQueryable<T>.Take(int num)          // Abstract/QueryableProvider/QueryableProvider.cs:1458
ISugarQueryable<T>.Select<TResult>(Expression<Func<T, TResult>> expression)
ISugarQueryable<T>.ToListAsync(CancellationToken token = default)

// 框架（XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver）
ISqlSugarClient GetClientForEntity<TEntity>()   // 泛型扩展，内部调 GetClientForEntity(Type)

// 框架（XiHan.Framework.DistributedIds）
long IDistributedIdGenerator<long>.NextId()
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- `public` 成员必须有 `<summary>`

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑：

```bash
dotnet test --project framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj -c Release
```

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，不 Import `version.props`、不设 `AssemblyName`。`Microsoft.Data.Sqlite` 经 `SqlSugarCore` 传递引入，不需要额外 `PackageReference`。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本计划无关，不要去追它。

**提交信息**：中文 Conventional Commits，作用域 `security-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

**建表**：`EnableDbInitialization` 与 `EnableTableInitialization` 两个选项默认都是 `false`（`framework/src/XiHan.Framework.Data/SqlSugar/Options/XiHanSqlSugarCoreOptions.cs`）。不开启就不会自动建表，首次写入即报表不存在——这一点必须写进包 README。

---

## 本计划特有的两条硬约束

**① `RecordPasswordAsync` 是本计划的核心交付物，不是可选项。**

`IPasswordHistoryStore` 只有一个读方法，主包的写入路径（`DefaultPasswordHistoryStore.RecordPassword`）是个从未被调用过的静态方法。Task 3 必须同时交付读方法（契约）与写方法（补充），且测试必须覆盖"写入后能读回"的完整链路——不能只测试对着手工插入的行做查询。

**② 裁剪时 `Skip` 的排序方向不能写反。**

裁剪要删的是"超出上限的最旧记录"：按 `CreatedTime` **降序**排序、`Skip(maxHistoryCount)` **之后**的行才是要删的。写反会删掉最新记录。Task 3 的测试必须断言"保留的是哪几条"，不能只断言总条数。

---

## File Structure

```
framework/src/XiHan.Framework.Security.SqlSugar/
  XiHan.Framework.Security.SqlSugar.csproj      包定义，按序 Import 四个 props
  XiHanSecuritySqlSugarModule.cs                模块类，只做装配
  README.md                                     固定七段结构
  Entities/
    SysPasswordHistory.cs
  Extensions/DependencyInjection/
    XiHanSecuritySqlSugarServiceCollectionExtensions.cs
  Services/
    SqlSugarPasswordHistoryStore.cs

framework/test/XiHan.Framework.Security.SqlSugar.Tests/
  XiHan.Framework.Security.SqlSugar.Tests.csproj
  EntityMappingTests.cs
  PasswordHistoryStoreTests.cs
```

---

### Task 1: 包骨架与解决方案注册

**Files:**
- Create: `framework/src/XiHan.Framework.Security.SqlSugar/XiHan.Framework.Security.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Security.SqlSugar/XiHanSecuritySqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Security.SqlSugar/README.md`
- Modify: `framework/XiHan.Framework.slnx`（在 `/1.src/6.Infrastructure/` 文件夹内，`XiHan.Framework.Security` 之后插入一行）

**Interfaces:**
- Consumes: 无
- Produces: 程序集 `XiHan.Framework.Security.SqlSugar`；模块类型 `XiHanSecuritySqlSugarModule`

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- 模块类范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/XiHanAuditingSqlSugarModule.cs`
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`

**本任务禁止事项：** 不要写任何服务注册逻辑，本任务的模块类是空装配。不要新增 `PackageReference`——SqlSugar 与雪花 ID 生成器经 `XiHan.Framework.Data` 传递引入。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.Security.SqlSugar/XiHan.Framework.Security.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Security.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Security.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Security.SqlSugar</PackageId>
        <Description>曦寒框架安全模块 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Security\XiHan.Framework.Security.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建模块类**

`framework/src/XiHan.Framework.Security.SqlSugar/XiHanSecuritySqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Security;

namespace XiHan.Framework.Security.SqlSugar;

/// <summary>
/// 曦寒框架安全模块 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSecuritySqlSugarModule))]</c> 即启用。
/// 本模块提供密码历史记录的 SqlSugar 落库实现，替换主包的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanSecurityModule),
    typeof(XiHanDataModule)
)]
public class XiHanSecuritySqlSugarModule : XiHanModule
{
}
```

- [ ] **Step 3: 注册进解决方案**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、`XiHan.Framework.Security` 那一行之后插入：

```xml
    <Project Path="src/XiHan.Framework.Security.SqlSugar/XiHan.Framework.Security.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README**

`framework/src/XiHan.Framework.Security.SqlSugar/README.md`：

```markdown
# XiHan.Framework.Security.SqlSugar

## 概述

`XiHan.Framework.Security` 的 SqlSugar 持久化提供程序，为密码历史记录提供落库实现。

## 核心能力

- `IPasswordHistoryStore` 的 SqlSugar 实现：按用户查询最近的密码哈希
- 补充方法 `RecordPasswordAsync`：写入新密码哈希并按上限裁剪旧记录
- 表结构由 `DbInitializer` 在应用启动时创建（需开启 `EnableTableInitialization`）

## 依赖关系

依赖 `XiHan.Framework.Security`（契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问、雪花 ID 生成器）。

## 配置与约定

表名 `sys_password_history`；主键 `Basic_Id` 为雪花 ID，非自增。不分表、不做多库路由。

`EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`，不开启则不会自动建表，首次写入会报表不存在。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanSecuritySqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

密码修改成功后，显式调用 `RecordPasswordAsync` 记录新哈希——该方法不在 `IPasswordHistoryStore` 契约上，需注入 `SqlSugarPasswordHistoryStore` 具体类型或自行在应用层扩展契约。

## 扩展点

注册以 `services.Replace` 顶替 `XiHan.Framework.Security` 注册的 `DefaultPasswordHistoryStore`。应用侧若要再次替换，同样使用 `Replace`——`TryAdd` 不会生效。

## 目录结构

```
Entities/    密码历史实体
Services/    SqlSugarPasswordHistoryStore
```
```

- [ ] **Step 5: 验证构建 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Security.SqlSugar framework/XiHan.Framework.slnx
git commit -m "feat(security-sqlsugar): 新增包骨架与模块装配"
```

---

### Task 2: 测试项目与密码历史实体

**Files:**
- Create: `framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Security.SqlSugar.Tests/EntityMappingTests.cs`
- Create: `framework/src/XiHan.Framework.Security.SqlSugar/Entities/SysPasswordHistory.cs`
- Modify: `framework/XiHan.Framework.slnx`（测试项目注册）

**Interfaces:**
- Consumes: 无
- Produces: `SysPasswordHistory`（`XiHan.Framework.Security.SqlSugar.Entities`），继承 `SugarCreationEntity<long>`；公开构造函数两个——`SysPasswordHistory()` 与 `SysPasswordHistory(long basicId)`

**参考来源（动手前先读）：**
- 基类：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarCreationEntity.cs`
- 实体形状范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysOperationLog.cs`（逐字照抄结构：两个构造函数、`[SugarColumn]` 写法）
- 密码哈希格式（确认列宽）：`framework/src/XiHan.Framework.Security/Password/PasswordHasher.cs:52`

**本任务禁止事项：** 不要加 `[SplitTable]`——不分表。不要把 `BasicId` 做成自增（`IsIdentity` 必须为 `false`）。

- [ ] **Step 1: 创建测试项目**

`framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Security.SqlSugar\XiHan.Framework.Security.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

在 `framework/XiHan.Framework.slnx` 的测试文件夹内、`XiHan.Framework.Security.Tests`（若存在）或 `XiHan.Framework.Auditing.SqlSugar.Tests` 之后插入：

```xml
    <Project Path="test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Security.SqlSugar.Tests/EntityMappingTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Security.SqlSugar.Entities;

namespace XiHan.Framework.Security.SqlSugar.Tests;

/// <summary>
/// 密码历史实体映射测试
/// </summary>
public class EntityMappingTests
{
    /// <summary>
    /// 表名符合约定
    /// </summary>
    [Fact]
    public void 表名符合约定()
    {
        var table = typeof(SysPasswordHistory).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_password_history", table.TableName);
    }

    /// <summary>
    /// 用户标识列名使用帕斯卡下划线
    /// </summary>
    [Fact]
    public void 用户标识列名使用帕斯卡下划线()
    {
        var property = typeof(SysPasswordHistory).GetProperty(nameof(SysPasswordHistory.UserId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("User_Id", column.ColumnName);
        Assert.False(column.IsNullable);
    }

    /// <summary>
    /// 密码哈希列非空且有足够列宽
    /// </summary>
    [Fact]
    public void 密码哈希列非空且有足够列宽()
    {
        var property = typeof(SysPasswordHistory).GetProperty(nameof(SysPasswordHistory.PasswordHash));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Password_Hash", column.ColumnName);
        Assert.False(column.IsNullable);
        Assert.True(column.Length >= 512);
    }

    /// <summary>
    /// 主键经构造函数传入
    /// </summary>
    [Fact]
    public void 主键经构造函数传入()
    {
        var entity = new SysPasswordHistory(1001L);

        Assert.Equal(1001L, entity.BasicId);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysPasswordHistory` 类型不存在。

- [ ] **Step 4: 实现实体**

`framework/src/XiHan.Framework.Security.SqlSugar/Entities/SysPasswordHistory.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Security.SqlSugar.Entities;

/// <summary>
/// 密码历史记录实体
/// </summary>
[SugarTable("sys_password_history")]
public class SysPasswordHistory : SugarCreationEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysPasswordHistory() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysPasswordHistory(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = false, ColumnDescription = "用户标识")]
    public long UserId { get; set; }

    /// <summary>
    /// 密码哈希
    /// </summary>
    [SugarColumn(ColumnName = "Password_Hash", Length = 512, IsNullable = false, ColumnDescription = "密码哈希")]
    public string PasswordHash { get; set; } = string.Empty;
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj -c Release
```

预期：4 个测试全部 PASS。

- [ ] **Step 6: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

- [ ] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Security.SqlSugar/Entities framework/test/XiHan.Framework.Security.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(security-sqlsugar): 新增密码历史实体与映射测试"
```

---

### Task 3: 存储实现（读+写）与注册

**Files:**
- Create: `framework/src/XiHan.Framework.Security.SqlSugar/Services/SqlSugarPasswordHistoryStore.cs`
- Create: `framework/src/XiHan.Framework.Security.SqlSugar/Extensions/DependencyInjection/XiHanSecuritySqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Security.SqlSugar/XiHanSecuritySqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Security.SqlSugar.Tests/PasswordHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysPasswordHistory`；框架的 `ISqlSugarClientResolver`、`IDistributedIdGenerator<long>`
- Produces:
  - `public class SqlSugarPasswordHistoryStore : IPasswordHistoryStore`
  - `Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken ct = default)`（契约方法）
  - `Task RecordPasswordAsync(long userId, string passwordHash, int maxHistoryCount = 10, CancellationToken ct = default)`（补充方法，不在契约上）
  - 扩展方法 `IServiceCollection AddXiHanSecuritySqlSugar(this IServiceCollection services)`

**参考来源（动手前先读）：**
- 契约与内存实现的裁剪逻辑：`framework/src/XiHan.Framework.Security/Services/IPasswordHistoryStore.cs`、`DefaultPasswordHistoryStore.cs`
- 客户端解析：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`
- 主包既有注册（确认 `TryAddScoped` 的位置）：`framework/src/XiHan.Framework.Security/Extensions/DependencyInjection/XiHanSecurityServiceCollectionExtensions.cs:39`
- 雪花 ID 生成器：`framework/src/XiHan.Framework.DistributedIds/Extensions/DependencyInjection/`（`AddSingleton` 出 `IDistributedIdGenerator<long>`）

**本任务禁止事项：** 硬约束 ①②全部。另外不要在存储层调用 `VerifyPassword` 或做任何哈希计算。不要给 `RecordPasswordAsync` 加事务包裹（`Ado.BeginTranAsync()`）——两步操作各自经 `GetClientForEntity` 复用同一客户端即可，若调用方本身在事务型工作单元中会自然获得事务保护，见 spec 4.3 节。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Security.SqlSugar.Tests/PasswordHistoryStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlSugar;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.Security.Services;
using XiHan.Framework.Security.SqlSugar.Entities;
using XiHan.Framework.Security.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Security.SqlSugar.Services;

namespace XiHan.Framework.Security.SqlSugar.Tests;

/// <summary>
/// 密码历史存储测试
/// </summary>
public class PasswordHistoryStoreTests
{
    /// <summary>
    /// 写入后能读回
    /// </summary>
    [Fact]
    public async Task 写入后能读回()
    {
        using var context = new PasswordHistoryTestContext();

        await context.Store.RecordPasswordAsync(1L, "hash-1");

        var recent = await context.Store.GetRecentPasswordHashesAsync(1L, 10);

        Assert.Single(recent);
        Assert.Equal("hash-1", recent[0]);
    }

    /// <summary>
    /// 读回顺序为旧到新
    /// </summary>
    [Fact]
    public async Task 读回顺序为旧到新()
    {
        using var context = new PasswordHistoryTestContext();

        await context.Store.RecordPasswordAsync(1L, "hash-1");
        await context.Store.RecordPasswordAsync(1L, "hash-2");
        await context.Store.RecordPasswordAsync(1L, "hash-3");

        var recent = await context.Store.GetRecentPasswordHashesAsync(1L, 10);

        Assert.Equal(["hash-1", "hash-2", "hash-3"], recent);
    }

    /// <summary>
    /// 超过上限时裁剪最旧记录
    /// </summary>
    [Fact]
    public async Task 超过上限时裁剪最旧记录()
    {
        using var context = new PasswordHistoryTestContext();

        await context.Store.RecordPasswordAsync(1L, "hash-1", maxHistoryCount: 2);
        await context.Store.RecordPasswordAsync(1L, "hash-2", maxHistoryCount: 2);
        await context.Store.RecordPasswordAsync(1L, "hash-3", maxHistoryCount: 2);

        var recent = await context.Store.GetRecentPasswordHashesAsync(1L, 10);

        Assert.Equal(["hash-2", "hash-3"], recent);
    }

    /// <summary>
    /// 不同用户的历史互不影响
    /// </summary>
    [Fact]
    public async Task 不同用户的历史互不影响()
    {
        using var context = new PasswordHistoryTestContext();

        await context.Store.RecordPasswordAsync(1L, "user1-hash");
        await context.Store.RecordPasswordAsync(2L, "user2-hash");

        var user1Recent = await context.Store.GetRecentPasswordHashesAsync(1L, 10);

        Assert.Single(user1Recent);
        Assert.Equal("user1-hash", user1Recent[0]);
    }

    /// <summary>
    /// count 非正数时返回空集合
    /// </summary>
    [Fact]
    public async Task count非正数时返回空集合()
    {
        using var context = new PasswordHistoryTestContext();

        await context.Store.RecordPasswordAsync(1L, "hash-1");

        var recent = await context.Store.GetRecentPasswordHashesAsync(1L, 0);

        Assert.Empty(recent);
    }

    /// <summary>
    /// 注册扩展以 SqlSugar 存储顶替内存实现
    /// </summary>
    [Fact]
    public void 注册扩展顶替内存实现()
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IPasswordHistoryStore, DefaultPasswordHistoryStore>();

        services.AddXiHanSecuritySqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IPasswordHistoryStore));
        Assert.Equal(typeof(SqlSugarPasswordHistoryStore), descriptor.ImplementationType);
    }
}

/// <summary>
/// 密码历史存储测试夹具，提供一个临时 SQLite 库与被测存储实例
/// </summary>
internal sealed class PasswordHistoryTestContext : IDisposable
{
    private readonly string _databaseFile;

    /// <summary>
    /// 构造函数
    /// </summary>
    public PasswordHistoryTestContext()
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_password_history_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        Client.CodeFirst.InitTables(typeof(SysPasswordHistory));

        Store = new SqlSugarPasswordHistoryStore(
            new StubClientResolver(Client),
            IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());
    }

    /// <summary>
    /// 被测存储
    /// </summary>
    public SqlSugarPasswordHistoryStore Store { get; }

    /// <summary>
    /// SQLite 客户端
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 释放客户端并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        Client.Dispose();

        if (File.Exists(_databaseFile))
        {
            File.Delete(_databaseFile);
        }
    }
}
```

- [ ] **Step 2: 补一个可复用的桩客户端解析器**

`PasswordHistoryStoreTests.cs` 用到的 `StubClientResolver` 尚不存在，在同文件末尾追加：

```csharp
/// <summary>
/// 测试用客户端解析器，固定返回同一个客户端
/// </summary>
internal sealed class StubClientResolver : XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver
{
    private readonly ISqlSugarClient _client;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="client">固定返回的客户端</param>
    public StubClientResolver(ISqlSugarClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 获取当前客户端
    /// </summary>
    public ISqlSugarClient GetCurrentClient()
    {
        return _client;
    }

    /// <summary>
    /// 获取实体对应的客户端
    /// </summary>
    /// <param name="entityType">实体类型</param>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        return _client;
    }

    /// <summary>
    /// 按连接配置标识获取客户端
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    public ISqlSugarClient GetClient(string configId)
    {
        return _client;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取当前布局的全部连接配置标识
    /// </summary>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取当前工作单元已登记的连接配置标识
    /// </summary>
    public IReadOnlyList<string> GetEnlistedConfigIds()
    {
        return [];
    }

    /// <summary>
    /// 获取所有库的客户端
    /// </summary>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [_client];
    }

    /// <summary>
    /// 获取底层多租户接口
    /// </summary>
    /// <exception cref="NotSupportedException">始终抛出</exception>
    public ITenant AsTenant()
    {
        throw new NotSupportedException("测试桩不支持多租户切换。");
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarPasswordHistoryStore`、`AddXiHanSecuritySqlSugar` 不存在。

- [ ] **Step 4: 实现存储类**

`framework/src/XiHan.Framework.Security.SqlSugar/Services/SqlSugarPasswordHistoryStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.Security.Services;
using XiHan.Framework.Security.SqlSugar.Entities;

namespace XiHan.Framework.Security.SqlSugar.Services;

/// <summary>
/// 密码历史记录 SqlSugar 存储
/// </summary>
public class SqlSugarPasswordHistoryStore : IPasswordHistoryStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarPasswordHistoryStore(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 获取用户最近的密码哈希列表，按记录时间由旧到新排列
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="count">获取数量</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>密码哈希列表</returns>
    public async Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return [];
        }

        var client = _clientResolver.GetClientForEntity<SysPasswordHistory>();

        var recent = await client.Queryable<SysPasswordHistory>()
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.CreatedTime, OrderByType.Desc)
            .Take(count)
            .Select(item => item.PasswordHash)
            .ToListAsync(ct);

        recent.Reverse();
        return recent;
    }

    /// <summary>
    /// 记录新密码哈希，并把该用户的历史记录裁剪到上限之内
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="passwordHash">密码哈希</param>
    /// <param name="maxHistoryCount">最大历史记录数</param>
    /// <param name="ct">取消令牌</param>
    public async Task RecordPasswordAsync(long userId, string passwordHash, int maxHistoryCount = 10, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var client = _clientResolver.GetClientForEntity<SysPasswordHistory>();

        var entity = new SysPasswordHistory(_idGenerator.NextId())
        {
            UserId = userId,
            PasswordHash = passwordHash,
            CreatedTime = DateTimeOffset.UtcNow
        };

        await client.Insertable(entity).ExecuteCommandAsync(ct);

        var staleIds = await client.Queryable<SysPasswordHistory>()
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.CreatedTime, OrderByType.Desc)
            .Skip(maxHistoryCount)
            .Select(item => item.BasicId)
            .ToListAsync(ct);

        if (staleIds.Count > 0)
        {
            await client.Deleteable<SysPasswordHistory>().In(staleIds).ExecuteCommandAsync(ct);
        }
    }
}
```

- [ ] **Step 5: 实现注册扩展**

`framework/src/XiHan.Framework.Security.SqlSugar/Extensions/DependencyInjection/XiHanSecuritySqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Security.Services;
using XiHan.Framework.Security.SqlSugar.Services;

namespace XiHan.Framework.Security.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 安全模块 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanSecuritySqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换密码历史记录的内存实现
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanSecuritySqlSugar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IPasswordHistoryStore, SqlSugarPasswordHistoryStore>());

        return services;
    }
}
```

- [ ] **Step 6: 在模块里调用扩展**

把 `XiHanSecuritySqlSugarModule.cs` 改为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Security;
using XiHan.Framework.Security.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Security.SqlSugar;

/// <summary>
/// 曦寒框架安全模块 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSecuritySqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 存储替换 <see cref="XiHanSecurityModule"/> 注册的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanSecurityModule),
    typeof(XiHanDataModule)
)]
public class XiHanSecuritySqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanSecuritySqlSugar();
    }
}
```

- [ ] **Step 7: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Security.SqlSugar.Tests/XiHan.Framework.Security.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS（含"超过上限时裁剪最旧记录"必须验证保留的是 `["hash-2", "hash-3"]` 而不仅仅是"条数为 2"）；构建 0 Warning(s) 0 Error(s)。

- [ ] **Step 8: 提交**

```bash
git add framework/src/XiHan.Framework.Security.SqlSugar framework/test/XiHan.Framework.Security.SqlSugar.Tests
git commit -m "feat(security-sqlsugar): 新增密码历史存储实现并替换内存版"
```

---

### Task 4: 文档与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.Security.SqlSugar/README.md`
- Create: `docs/packages/security-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`（包索引表）
- Modify: `README.md`、`README_cn.md`（模块计数）
- Modify: `framework/README.md`、`framework/README_cn.md`（模块清单与计数）

**Interfaces:**
- Consumes: 前三个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 已知边界清单：spec 第 8 节
- 同类子包文档范本：`docs/packages/auditing-sqlsugar.md`
- 侧边栏结构：`docs/.vitepress/config.ts`（找到 packages 分组中 `security` 的位置）
- 包索引表结构与既有行：`docs/packages/index.md`（第 85 行 `EventBus.SqlSugar`、第 99 行 `Auditing.SqlSugar`，均是链到子包页面的一行 + 一句话说明）

**本任务禁止事项：** 不要顺手改与本 PR 无关的文档。不要在文档里声称仓库有 `.codegraph/` 目录。**不要**往 `README.md`/`README_cn.md` 里加表格行——那两份是"常用包"精选清单，不是逐包穷举（`Auditing.SqlSugar`、`EventBus.SqlSugar` 都不在其中），只改计数。**不要**把模块计数当成本计划撰写时的某个固定数字硬编码进 commit——同系列的 Traffic.SqlSugar、Upgrade.SqlSugar 等包会依次合并，先合并的包已经把计数改过，写死的数字对后合并的包必然是错的。

- [ ] **Step 1: 新增包文档**

`docs/packages/security-sqlsugar.md`，按 `docs/packages/auditing-sqlsugar.md` 的结构组织，至少包含：包定位、表结构（`sys_password_history`）、主键与列名约定、启用方式（`[DependsOn]`）、`RecordPasswordAsync` 暂无调用方这一已知边界。

- [ ] **Step 2: 挂上侧边栏**

在 `docs/.vitepress/config.ts` 的 packages 分组里、`security` 条目之后插入 `security-sqlsugar` 条目。

- [ ] **Step 3: 登记进包索引**

`docs/packages/index.md` 是包索引表，按分层分组，第 58-65 行的「4. 安全 · 认证 · 授权」一组里已有：

```markdown
| [Security](./security) | 安全与加密：BouncyCastle 企业级密码学、密钥管理、密码哈希、数据保护 |
| [Authentication](./authentication) | 认证：JWT / OAuth2 / OIDC、令牌工厂、MFA、SSO |
```

在 `[Security](./security)` 那一行**之后**、`[Authentication](./authentication)` 那一行**之前**插入：

```markdown
| [Security.SqlSugar](./security-sqlsugar) | 密码历史记录的 SqlSugar 持久化提供程序：按用户查询与写入密码哈希、按上限裁剪历史 |
```

行号是撰写时的快照，实现时用 `grep -n "\[Security\](\./security)"` 现场核对插入位置，不要凭行号硬改。

同文件开头第 3 行「XiHan.Framework 由 **N 个 NuGet 包**组成」的计数，与 Step 4（下一步）的模块计数是同一个数字，一并 +1，不要漏改这一处——它和 `README.md`/`framework/README.md` 里的计数字符串是分开维护的，改了那些不会连带改这里。

- [ ] **Step 4: 更新模块计数与模块清单**

模块总数字符串**不要假设是某个具体数字**——先探测当前值：

```bash
grep -n "Modules-[0-9]\+-1f6feb" README.md README_cn.md
grep -n "[0-9]\+ 个" README.md README_cn.md framework/README.md framework/README_cn.md
```

确认当前计数 `N` 后，把下列 **12 处**全部改成 `N+1`（`framework/README.md`、`framework/README_cn.md` 里"模块数"与"测试项目数"是两个独立计数，本包新增了 1 个源码项目、也新增了 1 个测试项目，两者都要 +1）：

| 文件 | 内容 |
| --- | --- |
| `README.md` | 正文里的模块计数（1 处）、shields.io 徽章 URL 里的 `Modules-N-1f6feb`（1 处）、README 顶部/摘要处的计数（1 处） |
| `README_cn.md` | 同上 3 处 |
| `framework/README.md` | 正文计数（1 处）、模块目录小计（1 处）、测试项目计数（1 处） |
| `framework/README_cn.md` | 同上 3 处 |

用 `grep -n "N"` 逐个文件核对一遍具体行号再改，不要凭经验猜行号——前面已合并的包会让行号漂移。

`README.md`、`README_cn.md` 只改计数，**不加表格行**。

`framework/README.md` 与 `framework/README_cn.md` 除了计数，还要在模块清单表格中紧随 `XiHan.Framework.Security` 之后加入 `XiHan.Framework.Security.SqlSugar` 一行，描述用"密码历史 SqlSugar 持久化"。

- [ ] **Step 5: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 警告 0 错误；全部测试通过（`Script.Tests` 的既有随机失败与本计划无关）。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败，先结束残留测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.Security.SqlSugar.Tests.exe"
```

- [ ] **Step 6: 注释复查**

通读本计划改动过的 `.cs` 文件的注释。判定标准是是否在讲论证、权衡、叙事——发现即移出到提交信息。

- [ ] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Security.SqlSugar/README.md docs README.md README_cn.md framework/README.md framework/README_cn.md
git commit -m "docs(security-sqlsugar): 补写包文档与模块清单"
```

---

## 完成标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `IPasswordHistoryStore` 经 `services.Replace` 顶替为 `SqlSugarPasswordHistoryStore`
- `RecordPasswordAsync` 存在且有测试覆盖"写入后能读回""超上限裁剪最旧记录"
- 包 README、`docs/packages/security-sqlsugar.md`、VitePress 侧边栏、模块清单均已更新

## 已知边界（写入 PR 描述，不写进代码注释）

- **`RecordPasswordAsync` 暂无调用方**：全仓库当前没有任何代码在密码修改成功后调用它，`DefaultPasswordHistoryStore.RecordPassword` 同样如此，这是主包既有的设计缺口
- **非事务场景下写入不是原子的**：不在工作单元中调用时，插入与裁剪各自独立提交
- **无并发去重保护**：同一用户极短时间内并发调用可能写入两条历史记录

## 下一份计划

本包只有一份计划。系列内下一个包见 `.superpowers/plans/2026-09-28-traffic-sqlsugar-1-gray-rule.md`（独立计划，互不依赖）。
