# Upgrade.SqlSugar 升级版本存储（1）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `XiHan.Framework.Upgrade.SqlSugar` 包，为 `IUpgradeVersionStore` 提供 SqlSugar 落库实现，严格复现内存实现"原地回写调用方 `UpgradeVersionState` 对象"这一可观察行为，并用 `services.Replace` 顶替 `DefaultUpgradeVersionStore`。

**Architecture:** 两个实体——`SysUpgradeVersion`（每租户一行，`Tenant_Key` 字符串列区分宿主/租户）、`SysUpgradeMigrationHistory`（追加型，独立雪花主键）。纯静态映射 `UpgradeMapper` 提供实体↔模型转换与租户键构建。单个存储类 `SqlSugarUpgradeVersionStore` 实现全部 9 个契约方法；互斥由既有的 `IUpgradeLockProvider` 负责，本包不重复实现抢占协议，只用"插入失败后重查"应对 `GetOrCreateAsync` 的插入竞态。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-upgrade-sqlsugar-1-upgrade-version-design.md`

> 该 spec 自成一体，实现本计划所需的全部约束都在其中。

**Linear 议题**：https://linear.app/elf-express/issue/EDDIE-13

**前置：** 无。`XiHan.Framework.Upgrade`、`XiHan.Framework.Data` 均已发布，可直接引用。

---

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：实现从 `dev` 开 worktree：

```bash
git worktree add ../XiHan.Framework-upgrade -b feat/upgrade-sqlsugar dev
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

**SqlSugar 签名只信源码**：权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）。更新提供者目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。当前引用版本 `SqlSugarCore 5.1.4.221`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar
IInsertable<T>.ExecuteCommandAsync(CancellationToken token = default)
IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
IUpdateable<T>.ExecuteCommandAsync(CancellationToken token = default)
ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
ISugarQueryable<T>.OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)
ISugarQueryable<T>.Take(int num)
ISugarQueryable<T>.ToListAsync(CancellationToken token = default)
ISugarQueryable<T>.Single(Expression<Func<T, bool>> expression)   // Abstract/QueryableProvider/QueryableExecuteSql.cs:49，测试用的同步断言辅助方法
ISugarQueryable<T>.Count(Expression<Func<T, bool>> expression)    // Abstract/QueryableProvider/QueryableExecuteSql.cs:157，测试用的同步断言辅助方法
ICodeFirst.InitTables(params Type[] types)                        // Abstract/CodeFirstProvider/CodeFirstProvider.cs:133

// 框架（XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver，Scoped 注册）
ISqlSugarClient GetClientForEntity<TEntity>()

// 框架（XiHan.Framework.DistributedIds）
long IDistributedIdGenerator<long>.NextId()

// 框架（XiHan.Framework.MultiTenancy.Abstractions，经 XiHan.Framework.Upgrade 传递引入）
long? ICurrentTenant.Id
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

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：**没有可用的筛选参数**，**不要带 `--logger trx` / `--results-directory`**。要跑单个测试类就整个项目跑：

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
```

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，不 Import `version.props`、不设 `AssemblyName`。

**SQLite 临时库**：连接串必须带 `Pooling=False`。

**构建环境坑**：`MSB3027` / `MSB3021` 是残留测试进程占用输出文件，`taskkill //F //IM "<name>.exe"` 后重建，不是代码问题。

**已知的无关抖动**：`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发红，与本计划无关。

**提交信息**：中文 Conventional Commits，作用域 `upgrade-sqlsugar`。**不加任何 AI 署名。**

**建表**：`EnableDbInitialization` 与 `EnableTableInitialization` 默认都是 `false`；`IUpgradeVersionStore.EnsureTablesAsync` 不依赖这两个开关，内部直接 `CodeFirst.InitTables(...)`——写进包 README。

---

## 本计划特有的三条硬约束

**① `Set*`/`Update*` 四个方法必须原地回写调用方传入的 `version` 对象。**

`SetUpgradingAsync`、`SetUpgradeCompletedAsync`、`SetUpgradeFailedAsync`、`UpdateDbVersionAsync` 更新数据库之后，必须把同样的字段值写回传入的 `UpgradeVersionState version` 参数（它是可变类）。测试必须直接断言传入对象的字段，不能重新查询一份新对象来断言。

**② `GetOrCreateAsync` 的插入必须在失败后重查，不能只写"查不到就插入"。**

不建数据库唯一约束，靠"插入抛异常就按租户键重新查一次，查到就用查到的、查不到就把原始异常抛出去"应对并发建行。Task 3 的测试必须覆盖这条路径（手工预插入一行模拟竞态）。

**③ `HasMigrationHistoryAsync`/`GetLatestHistoryAsync`/`AddMigrationHistoryAsync` 都要按 `Tenant_Key` 过滤，不能只按 `Tenant_Id` 过滤。**

`Tenant_Id` 为 `null` 时不能依赖"等于 `null`"的 SQL 语义（各方言行为不一致），必须统一走 `UpgradeMapper.BuildTenantKey` 生成的非空字符串列比较。

---

## File Structure

```
framework/src/XiHan.Framework.Upgrade.SqlSugar/
  XiHan.Framework.Upgrade.SqlSugar.csproj
  XiHanUpgradeSqlSugarModule.cs
  README.md
  Entities/
    SysUpgradeVersion.cs
    SysUpgradeMigrationHistory.cs
  Mapping/
    UpgradeMapper.cs
  Extensions/DependencyInjection/
    XiHanUpgradeSqlSugarServiceCollectionExtensions.cs
  Services/
    SqlSugarUpgradeVersionStore.cs

framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/
  XiHan.Framework.Upgrade.SqlSugar.Tests.csproj
  EntityMappingTests.cs
  UpgradeVersionStoreTests.cs
```

---

### Task 1: 包骨架与解决方案注册

**Files:**
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/XiHan.Framework.Upgrade.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/XiHanUpgradeSqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/README.md`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: 无
- Produces: 程序集 `XiHan.Framework.Upgrade.SqlSugar`；模块类型 `XiHanUpgradeSqlSugarModule`

**参考来源（动手前先读）：**
- csproj / 模块类范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/`
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`

**本任务禁止事项：** 不要写任何服务注册逻辑，本任务的模块类是空装配。不要新增对 `XiHan.Framework.MultiTenancy.Abstractions` 的直接 `ProjectReference`——经 `XiHan.Framework.Upgrade` 传递引入即可。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/XiHan.Framework.Upgrade.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Upgrade.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Upgrade.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Upgrade.SqlSugar</PackageId>
        <Description>曦寒框架升级模块 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Upgrade\XiHan.Framework.Upgrade.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建模块类**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/XiHanUpgradeSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Upgrade;

namespace XiHan.Framework.Upgrade.SqlSugar;

/// <summary>
/// 曦寒框架升级模块 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanUpgradeSqlSugarModule))]</c> 即启用。
/// 本模块提供升级版本记录的 SqlSugar 落库实现，替换主包的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanUpgradeModule),
    typeof(XiHanDataModule)
)]
public class XiHanUpgradeSqlSugarModule : XiHanModule
{
}
```

- [ ] **Step 3: 注册进解决方案**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、`XiHan.Framework.Upgrade` 之后插入：

```xml
    <Project Path="src/XiHan.Framework.Upgrade.SqlSugar/XiHan.Framework.Upgrade.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/README.md`：

```markdown
# XiHan.Framework.Upgrade.SqlSugar

## 概述

`XiHan.Framework.Upgrade` 的 SqlSugar 持久化提供程序，为升级版本记录与迁移历史提供落库实现。

## 核心能力

- `IUpgradeVersionStore` 的 SqlSugar 实现：版本状态每租户一行、迁移历史追加写入
- `EnsureTablesAsync` 内部直接建表，不依赖全局的 `EnableTableInitialization` 开关
- `Set*`/`Update*` 系列方法更新数据库的同时，原地回写调用方传入的状态对象

## 依赖关系

依赖 `XiHan.Framework.Upgrade`（契约与模型）与 `XiHan.Framework.Data`（SqlSugar 数据访问、雪花 ID 生成器）。

## 配置与约定

表名 `sys_upgrade_version`、`sys_upgrade_migration_history`；主键 `Basic_Id` 为雪花 ID，非自增。用 `Tenant_Key` 字符串列（`host` 或 `tenant:{id}`）区分宿主与租户，不依赖对可空 `Tenant_Id` 的唯一性判断。不分表、不做多库路由。

`EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`，但本包的 `EnsureTablesAsync` 不受这两个开关影响——升级流程每次执行都会显式调用它来确保表存在。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanUpgradeSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

## 扩展点

注册以 `services.Replace` 顶替 `XiHan.Framework.Upgrade` 注册的 `DefaultUpgradeVersionStore`。多实例并发执行升级由 `IUpgradeLockProvider`（另一个契约）负责互斥，本包只负责数据存取。

## 目录结构

```
Entities/    版本状态与迁移历史实体
Mapping/     实体与模型的双向映射、租户键构建
Services/    SqlSugarUpgradeVersionStore
```
```

- [ ] **Step 5: 验证构建 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Upgrade.SqlSugar framework/XiHan.Framework.slnx
git commit -m "feat(upgrade-sqlsugar): 新增包骨架与模块装配"
```

---

### Task 2: 测试项目与两个实体

**Files:**
- Create: `framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/EntityMappingTests.cs`
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/Entities/SysUpgradeVersion.cs`
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/Entities/SysUpgradeMigrationHistory.cs`
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/Mapping/UpgradeMapper.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: `XiHan.Framework.Upgrade.Models.UpgradeVersionState`、`UpgradeMigrationHistory`
- Produces:
  - `SysUpgradeVersion`、`SysUpgradeMigrationHistory`（均继承 `SugarEntity<long>`），各自两个构造函数
  - `public static class UpgradeMapper`：`BuildTenantKey(long?)`、`NormalizeVersion(string?)`、`ToState(SysUpgradeVersion)`、`ToHistory(SysUpgradeMigrationHistory)`

**参考来源（动手前先读）：**
- 基类：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs`
- 模型字段与既有租户键逻辑：`framework/src/XiHan.Framework.Upgrade/Models/UpgradeVersionState.cs`、`UpgradeMigrationHistory.cs`、`Services/DefaultUpgradeVersionStore.cs`（`BuildTenantKey`、`NormalizeVersion` 两个私有静态方法，本包对应方法逻辑与之字面一致）

**本任务禁止事项：** 不要加 `[SplitTable]`。不要把 `BasicId` 设为自增。`Tenant_Key` 列不能为空（`IsNullable = false`）。

- [ ] **Step 1: 创建测试项目**

`framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Upgrade.SqlSugar\XiHan.Framework.Upgrade.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

在 `framework/XiHan.Framework.slnx` 的测试文件夹内、既有的 SqlSugar 测试项目之后插入：

```xml
    <Project Path="test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/EntityMappingTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Upgrade.SqlSugar.Entities;
using XiHan.Framework.Upgrade.SqlSugar.Mapping;

namespace XiHan.Framework.Upgrade.SqlSugar.Tests;

/// <summary>
/// 升级实体与映射测试
/// </summary>
public class EntityMappingTests
{
    /// <summary>
    /// 版本状态表名符合约定
    /// </summary>
    [Fact]
    public void 版本状态表名符合约定()
    {
        var table = typeof(SysUpgradeVersion).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_upgrade_version", table.TableName);
    }

    /// <summary>
    /// 迁移历史表名符合约定
    /// </summary>
    [Fact]
    public void 迁移历史表名符合约定()
    {
        var table = typeof(SysUpgradeMigrationHistory).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_upgrade_migration_history", table.TableName);
    }

    /// <summary>
    /// 租户键对宿主与租户分别生成不同格式
    /// </summary>
    [Fact]
    public void 租户键对宿主与租户分别生成不同格式()
    {
        Assert.Equal("host", UpgradeMapper.BuildTenantKey(null));
        Assert.Equal("tenant:7", UpgradeMapper.BuildTenantKey(7L));
    }

    /// <summary>
    /// 版本号规范化空白值为零版本
    /// </summary>
    [Fact]
    public void 版本号规范化空白值为零版本()
    {
        Assert.Equal("0.0.0", UpgradeMapper.NormalizeVersion(null));
        Assert.Equal("0.0.0", UpgradeMapper.NormalizeVersion("   "));
        Assert.Equal("1.2.3", UpgradeMapper.NormalizeVersion("  1.2.3  "));
    }

    /// <summary>
    /// 版本状态实体到模型的映射保留全部字段
    /// </summary>
    [Fact]
    public void 版本状态实体到模型的映射保留全部字段()
    {
        var entity = new SysUpgradeVersion(1001L)
        {
            TenantId = 7L,
            TenantKey = "tenant:7",
            AppVersion = "1.0.0",
            DbVersion = "1.0.0",
            MinSupportVersion = "0.9.0",
            IsUpgrading = true,
            UpgradeNode = "node-1",
            UpgradeStartTime = DateTimeOffset.UtcNow
        };

        var state = UpgradeMapper.ToState(entity);

        Assert.Equal(1001L, state.Id);
        Assert.Equal(7L, state.TenantId);
        Assert.Equal("1.0.0", state.AppVersion);
        Assert.Equal("1.0.0", state.DbVersion);
        Assert.Equal("0.9.0", state.MinSupportVersion);
        Assert.True(state.IsUpgrading);
        Assert.Equal("node-1", state.UpgradeNode);
    }

    /// <summary>
    /// 迁移历史实体到模型的映射保留全部字段
    /// </summary>
    [Fact]
    public void 迁移历史实体到模型的映射保留全部字段()
    {
        var executedTime = DateTimeOffset.UtcNow;
        var entity = new SysUpgradeMigrationHistory(2001L)
        {
            TenantId = 7L,
            TenantKey = "tenant:7",
            Version = "1.0.0",
            ScriptName = "0001.sql",
            ExecutedTime = executedTime,
            Success = true,
            NodeName = "node-1",
            ErrorMessage = null
        };

        var history = UpgradeMapper.ToHistory(entity);

        Assert.Equal(7L, history.TenantId);
        Assert.Equal("1.0.0", history.Version);
        Assert.Equal("0001.sql", history.ScriptName);
        Assert.Equal(executedTime, history.ExecutedTime);
        Assert.True(history.Success);
        Assert.Equal("node-1", history.NodeName);
        Assert.Null(history.ErrorMessage);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，实体与映射器不存在。

- [ ] **Step 4: 实现两个实体**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/Entities/SysUpgradeVersion.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Upgrade.SqlSugar.Entities;

/// <summary>
/// 升级版本状态实体，每个租户（或宿主）一行
/// </summary>
[SugarTable("sys_upgrade_version")]
public class SysUpgradeVersion : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysUpgradeVersion() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysUpgradeVersion(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 租户标识，null 表示宿主
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识")]
    public long? TenantId { get; set; }

    /// <summary>
    /// 租户键，host 或 tenant:{id}
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Key", Length = 64, IsNullable = false, ColumnDescription = "租户键")]
    public string TenantKey { get; set; } = string.Empty;

    /// <summary>
    /// 应用版本
    /// </summary>
    [SugarColumn(ColumnName = "App_Version", Length = 32, IsNullable = false, ColumnDescription = "应用版本")]
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>
    /// 数据库版本
    /// </summary>
    [SugarColumn(ColumnName = "Db_Version", Length = 32, IsNullable = false, ColumnDescription = "数据库版本")]
    public string DbVersion { get; set; } = "0.0.0";

    /// <summary>
    /// 最小支持版本
    /// </summary>
    [SugarColumn(ColumnName = "Min_Support_Version", Length = 32, IsNullable = true, ColumnDescription = "最小支持版本")]
    public string? MinSupportVersion { get; set; }

    /// <summary>
    /// 是否升级中
    /// </summary>
    [SugarColumn(ColumnName = "Is_Upgrading", IsNullable = false, ColumnDescription = "是否升级中")]
    public bool IsUpgrading { get; set; }

    /// <summary>
    /// 升级节点
    /// </summary>
    [SugarColumn(ColumnName = "Upgrade_Node", Length = 128, IsNullable = true, ColumnDescription = "升级节点")]
    public string? UpgradeNode { get; set; }

    /// <summary>
    /// 升级开始时间
    /// </summary>
    [SugarColumn(ColumnName = "Upgrade_Start_Time", IsNullable = true, ColumnDescription = "升级开始时间")]
    public DateTimeOffset? UpgradeStartTime { get; set; }
}
```

`framework/src/XiHan.Framework.Upgrade.SqlSugar/Entities/SysUpgradeMigrationHistory.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Upgrade.SqlSugar.Entities;

/// <summary>
/// 升级迁移历史实体，追加写入
/// </summary>
[SugarTable("sys_upgrade_migration_history")]
public class SysUpgradeMigrationHistory : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysUpgradeMigrationHistory() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysUpgradeMigrationHistory(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 租户标识
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识")]
    public long? TenantId { get; set; }

    /// <summary>
    /// 租户键，host 或 tenant:{id}
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Key", Length = 64, IsNullable = false, ColumnDescription = "租户键")]
    public string TenantKey { get; set; } = string.Empty;

    /// <summary>
    /// 版本
    /// </summary>
    [SugarColumn(ColumnName = "Version", Length = 32, IsNullable = false, ColumnDescription = "版本")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 脚本名称
    /// </summary>
    [SugarColumn(ColumnName = "Script_Name", Length = 256, IsNullable = false, ColumnDescription = "脚本名称")]
    public string ScriptName { get; set; } = string.Empty;

    /// <summary>
    /// 执行时间
    /// </summary>
    [SugarColumn(ColumnName = "Executed_Time", IsNullable = false, ColumnDescription = "执行时间")]
    public DateTimeOffset ExecutedTime { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    [SugarColumn(ColumnName = "Success", IsNullable = false, ColumnDescription = "是否成功")]
    public bool Success { get; set; }

    /// <summary>
    /// 节点名称
    /// </summary>
    [SugarColumn(ColumnName = "Node_Name", Length = 128, IsNullable = true, ColumnDescription = "节点名称")]
    public string? NodeName { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    [SugarColumn(ColumnName = "Error_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "错误信息")]
    public string? ErrorMessage { get; set; }
}
```

- [ ] **Step 5: 实现映射器**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/Mapping/UpgradeMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.SqlSugar.Entities;

namespace XiHan.Framework.Upgrade.SqlSugar.Mapping;

/// <summary>
/// 升级实体与模型的映射
/// </summary>
public static class UpgradeMapper
{
    /// <summary>
    /// 构建租户键
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <returns>host 或 tenant:{id}</returns>
    public static string BuildTenantKey(long? tenantId)
    {
        return tenantId.HasValue ? $"tenant:{tenantId.Value}" : "host";
    }

    /// <summary>
    /// 规范化版本值
    /// </summary>
    /// <param name="version">版本值</param>
    /// <returns>规范化后的版本，空白时返回 0.0.0</returns>
    public static string NormalizeVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim();
    }

    /// <summary>
    /// 把版本状态实体转换为模型
    /// </summary>
    /// <param name="entity">版本状态实体</param>
    /// <returns>版本状态模型</returns>
    public static UpgradeVersionState ToState(SysUpgradeVersion entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new UpgradeVersionState
        {
            Id = entity.BasicId,
            TenantId = entity.TenantId,
            AppVersion = entity.AppVersion,
            DbVersion = entity.DbVersion,
            MinSupportVersion = entity.MinSupportVersion,
            IsUpgrading = entity.IsUpgrading,
            UpgradeNode = entity.UpgradeNode,
            UpgradeStartTime = entity.UpgradeStartTime
        };
    }

    /// <summary>
    /// 把迁移历史实体转换为模型
    /// </summary>
    /// <param name="entity">迁移历史实体</param>
    /// <returns>迁移历史模型</returns>
    public static UpgradeMigrationHistory ToHistory(SysUpgradeMigrationHistory entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new UpgradeMigrationHistory
        {
            TenantId = entity.TenantId,
            Version = entity.Version,
            ScriptName = entity.ScriptName,
            ExecutedTime = entity.ExecutedTime,
            Success = entity.Success,
            NodeName = entity.NodeName,
            ErrorMessage = entity.ErrorMessage
        };
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
```

- [ ] **Step 7: 验证全解决方案 0 警告并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Upgrade.SqlSugar framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(upgrade-sqlsugar): 新增升级实体与映射"
```

---

### Task 3: 只读与追加方法（EnsureTables / GetOrCreate / History）

**Files:**
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/Services/SqlSugarUpgradeVersionStore.cs`
- Create: `framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/UpgradeVersionStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的实体与 `UpgradeMapper`；框架的 `ISqlSugarClientResolver`、`IDistributedIdGenerator<long>`、`ICurrentTenant?`
- Produces: `public class SqlSugarUpgradeVersionStore : IUpgradeVersionStore`，构造函数 `(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator, ICurrentTenant? currentTenant = null)`；本任务实现 `EnsureTablesAsync`、`GetOrCreateAsync`、`GetLatestHistoryAsync`、`AddMigrationHistoryAsync`、`HasMigrationHistoryAsync` 五个方法，其余四个（`SetUpgradingAsync` 等）留给 Task 4，本任务先给出**抛 `NotImplementedException` 的占位实现**以保证类型完整可编译

**参考来源（动手前先读）：**
- 契约与内存实现：`framework/src/XiHan.Framework.Upgrade/Abstractions/IUpgradeVersionStore.cs`、`Services/DefaultUpgradeVersionStore.cs`
- 插入竞态与回填语义：spec 第 4.3 节
- `ICurrentTenant`：`framework/src/XiHan.Framework.MultiTenancy.Abstractions/ICurrentTenant.cs`（经 `XiHan.Framework.Upgrade` 传递引入）

**本任务禁止事项：** 硬约束 ②③。另外**不要**引入任何唯一索引或分布式锁——插入竞态只用"失败后重查"应对，见 spec 4.3 节。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/UpgradeVersionStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlSugar;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.Services;
using XiHan.Framework.Upgrade.SqlSugar.Entities;
using XiHan.Framework.Upgrade.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Upgrade.SqlSugar.Services;

namespace XiHan.Framework.Upgrade.SqlSugar.Tests;

/// <summary>
/// 升级版本存储测试
/// </summary>
public class UpgradeVersionStoreTests
{
    /// <summary>
    /// 建表后能创建首行
    /// </summary>
    [Fact]
    public async Task 建表后能创建首行()
    {
        using var context = new UpgradeStoreTestContext();

        await context.Store.EnsureTablesAsync();
        var state = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");

        Assert.True(state.Id > 0);
        Assert.Equal("1.0.0", state.AppVersion);
        Assert.Equal("0.0.0", state.DbVersion);
        Assert.Equal("0.9.0", state.MinSupportVersion);
    }

    /// <summary>
    /// 二次调用返回同一行
    /// </summary>
    [Fact]
    public async Task 二次调用返回同一行()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();

        var first = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");
        var second = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");

        Assert.Equal(first.Id, second.Id);
    }

    /// <summary>
    /// 回填空白字段
    /// </summary>
    [Fact]
    public async Task 回填空白字段()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();

        context.Client.Insertable(new SysUpgradeVersion(999L)
        {
            TenantKey = "host",
            AppVersion = "",
            DbVersion = "0.0.0",
            MinSupportVersion = null,
            IsUpgrading = false
        }).ExecuteCommand();

        var state = await context.Store.GetOrCreateAsync("2.0.0", "1.0.0");

        Assert.Equal(999L, state.Id);
        Assert.Equal("2.0.0", state.AppVersion);
        Assert.Equal("1.0.0", state.MinSupportVersion);
    }

    /// <summary>
    /// 插入竞态时退回重查而不是抛异常或产生第二行
    /// </summary>
    [Fact]
    public async Task 插入竞态时退回重查而不是抛异常或产生第二行()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();

        // 模拟另一个实例已抢先插入同一租户键的行
        context.Client.Insertable(new SysUpgradeVersion(555L)
        {
            TenantKey = "host",
            AppVersion = "1.0.0",
            DbVersion = "0.0.0",
            MinSupportVersion = "0.9.0",
            IsUpgrading = false
        }).ExecuteCommand();

        var state = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");
        var count = context.Client.Queryable<SysUpgradeVersion>().Count(item => item.TenantKey == "host");

        Assert.Equal(555L, state.Id);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// 只有成功记录视为已执行
    /// </summary>
    [Fact]
    public async Task 只有成功记录视为已执行()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();

        await context.Store.AddMigrationHistoryAsync(new UpgradeMigrationHistory
        {
            Version = "1.0.0",
            ScriptName = "0001.sql",
            ExecutedTime = DateTimeOffset.UtcNow,
            Success = false
        });

        Assert.False(await context.Store.HasMigrationHistoryAsync("1.0.0", "0001.sql"));

        await context.Store.AddMigrationHistoryAsync(new UpgradeMigrationHistory
        {
            Version = "1.0.0",
            ScriptName = "0001.sql",
            ExecutedTime = DateTimeOffset.UtcNow,
            Success = true
        });

        Assert.True(await context.Store.HasMigrationHistoryAsync("1.0.0", "0001.sql"));
    }

    /// <summary>
    /// 返回最新的迁移历史
    /// </summary>
    [Fact]
    public async Task 返回最新的迁移历史()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();

        await context.Store.AddMigrationHistoryAsync(new UpgradeMigrationHistory
        {
            Version = "1.0.0",
            ScriptName = "0001.sql",
            ExecutedTime = DateTimeOffset.UtcNow.AddMinutes(-10),
            Success = true
        });
        await context.Store.AddMigrationHistoryAsync(new UpgradeMigrationHistory
        {
            Version = "1.1.0",
            ScriptName = "0002.sql",
            ExecutedTime = DateTimeOffset.UtcNow,
            Success = true
        });

        var latest = await context.Store.GetLatestHistoryAsync();

        Assert.NotNull(latest);
        Assert.Equal("0002.sql", latest!.ScriptName);
    }

    /// <summary>
    /// 空表返回 null
    /// </summary>
    [Fact]
    public async Task 空表返回null()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();

        var latest = await context.Store.GetLatestHistoryAsync();

        Assert.Null(latest);
    }
}

/// <summary>
/// 升级版本存储测试夹具
/// </summary>
internal sealed class UpgradeStoreTestContext : IDisposable
{
    private readonly string _databaseFile;

    /// <summary>
    /// 构造函数
    /// </summary>
    public UpgradeStoreTestContext()
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_upgrade_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        Store = new SqlSugarUpgradeVersionStore(
            new StubClientResolver(Client),
            IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());
    }

    /// <summary>
    /// SQLite 客户端
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 被测存储
    /// </summary>
    public SqlSugarUpgradeVersionStore Store { get; }

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

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarUpgradeVersionStore` 不存在。

- [ ] **Step 3: 实现存储类（本任务范围内的五个方法 + 四个占位方法）**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/Services/SqlSugarUpgradeVersionStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.SqlSugar.Entities;
using XiHan.Framework.Upgrade.SqlSugar.Mapping;

namespace XiHan.Framework.Upgrade.SqlSugar.Services;

/// <summary>
/// 升级版本存储 SqlSugar 实现
/// </summary>
public class SqlSugarUpgradeVersionStore : IUpgradeVersionStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly ICurrentTenant? _currentTenant;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="currentTenant">当前租户（可选）</param>
    public SqlSugarUpgradeVersionStore(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator,
        ICurrentTenant? currentTenant = null)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// 确保升级相关表存在
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public Task EnsureTablesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();
        client.CodeFirst.InitTables(typeof(SysUpgradeVersion), typeof(SysUpgradeMigrationHistory));

        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取或创建系统版本记录
    /// </summary>
    /// <param name="currentAppVersion">当前应用版本</param>
    /// <param name="minSupportVersion">最小支持版本</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>版本状态</returns>
    public async Task<UpgradeVersionState> GetOrCreateAsync(string currentAppVersion, string minSupportVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tenantId = _currentTenant?.Id;
        var tenantKey = UpgradeMapper.BuildTenantKey(tenantId);
        var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();

        var existing = await client.Queryable<SysUpgradeVersion>()
            .Where(item => item.TenantKey == tenantKey)
            .Take(1)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0)
        {
            return await BackfillIfNeededAsync(client, existing[0], currentAppVersion, minSupportVersion, cancellationToken);
        }

        var entity = new SysUpgradeVersion(_idGenerator.NextId())
        {
            TenantId = tenantId,
            TenantKey = tenantKey,
            AppVersion = UpgradeMapper.NormalizeVersion(currentAppVersion),
            DbVersion = "0.0.0",
            MinSupportVersion = UpgradeMapper.NormalizeVersion(minSupportVersion),
            IsUpgrading = false
        };

        try
        {
            await client.Insertable(entity).ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception)
        {
            var retryExisting = await client.Queryable<SysUpgradeVersion>()
                .Where(item => item.TenantKey == tenantKey)
                .Take(1)
                .ToListAsync(cancellationToken);

            if (retryExisting.Count > 0)
            {
                return UpgradeMapper.ToState(retryExisting[0]);
            }

            throw;
        }

        return UpgradeMapper.ToState(entity);
    }

    /// <summary>
    /// 获取最新迁移历史记录
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>迁移历史</returns>
    public async Task<UpgradeMigrationHistory?> GetLatestHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tenantKey = UpgradeMapper.BuildTenantKey(_currentTenant?.Id);
        var client = _clientResolver.GetClientForEntity<SysUpgradeMigrationHistory>();

        var latest = await client.Queryable<SysUpgradeMigrationHistory>()
            .Where(item => item.TenantKey == tenantKey)
            .OrderBy(item => item.ExecutedTime, OrderByType.Desc)
            .Take(1)
            .ToListAsync(cancellationToken);

        return latest.Count > 0 ? UpgradeMapper.ToHistory(latest[0]) : null;
    }

    /// <summary>
    /// 追加迁移历史记录
    /// </summary>
    /// <param name="history">迁移历史</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task AddMigrationHistoryAsync(UpgradeMigrationHistory history, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(history);

        var tenantId = history.TenantId ?? _currentTenant?.Id;
        var tenantKey = UpgradeMapper.BuildTenantKey(tenantId);
        var client = _clientResolver.GetClientForEntity<SysUpgradeMigrationHistory>();

        var entity = new SysUpgradeMigrationHistory(_idGenerator.NextId())
        {
            TenantId = tenantId,
            TenantKey = tenantKey,
            Version = UpgradeMapper.NormalizeVersion(history.Version),
            ScriptName = history.ScriptName,
            ExecutedTime = history.ExecutedTime,
            Success = history.Success,
            NodeName = history.NodeName,
            ErrorMessage = history.ErrorMessage
        };

        await client.Insertable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 是否已执行指定脚本（仅成功执行视为已执行）
    /// </summary>
    /// <param name="version">版本</param>
    /// <param name="scriptName">脚本名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否已执行</returns>
    public async Task<bool> HasMigrationHistoryAsync(string version, string scriptName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(scriptName))
        {
            return false;
        }

        var tenantKey = UpgradeMapper.BuildTenantKey(_currentTenant?.Id);
        var client = _clientResolver.GetClientForEntity<SysUpgradeMigrationHistory>();

        var matched = await client.Queryable<SysUpgradeMigrationHistory>()
            .Where(item => item.TenantKey == tenantKey
                && item.Success
                && item.Version == version
                && item.ScriptName == scriptName)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <summary>
    /// 设置升级中状态
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="nodeName">升级节点</param>
    /// <param name="startTime">开始时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task SetUpgradingAsync(UpgradeVersionState version, string nodeName, DateTimeOffset startTime, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("留待 Task 4 实现。");
    }

    /// <summary>
    /// 设置升级完成状态
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="appVersion">应用版本</param>
    /// <param name="dbVersion">数据库版本</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task SetUpgradeCompletedAsync(UpgradeVersionState version, string appVersion, string dbVersion, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("留待 Task 4 实现。");
    }

    /// <summary>
    /// 设置升级失败状态
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task SetUpgradeFailedAsync(UpgradeVersionState version, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("留待 Task 4 实现。");
    }

    /// <summary>
    /// 更新数据库版本
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="dbVersion">数据库版本</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpdateDbVersionAsync(UpgradeVersionState version, string dbVersion, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("留待 Task 4 实现。");
    }

    /// <summary>
    /// 回填空白的应用版本与最小支持版本
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="entity">已存在的实体</param>
    /// <param name="currentAppVersion">当前应用版本</param>
    /// <param name="minSupportVersion">最小支持版本</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>映射后的版本状态</returns>
    private static async Task<UpgradeVersionState> BackfillIfNeededAsync(
        ISqlSugarClient client,
        SysUpgradeVersion entity,
        string currentAppVersion,
        string minSupportVersion,
        CancellationToken cancellationToken)
    {
        var needsUpdate = false;

        if (string.IsNullOrWhiteSpace(entity.AppVersion))
        {
            entity.AppVersion = UpgradeMapper.NormalizeVersion(currentAppVersion);
            needsUpdate = true;
        }

        if (string.IsNullOrWhiteSpace(entity.MinSupportVersion))
        {
            entity.MinSupportVersion = UpgradeMapper.NormalizeVersion(minSupportVersion);
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            await client.Updateable<SysUpgradeVersion>()
                .SetColumns(item => new SysUpgradeVersion { AppVersion = entity.AppVersion, MinSupportVersion = entity.MinSupportVersion })
                .Where(item => item.BasicId == entity.BasicId)
                .ExecuteCommandAsync(cancellationToken);
        }

        return UpgradeMapper.ToState(entity);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
```

预期：本任务新增的 7 个测试全部 PASS（`Set*`/`Update*` 相关测试要到 Task 4 才会存在，本任务不写）。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Upgrade.SqlSugar framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests
git commit -m "feat(upgrade-sqlsugar): 实现版本获取与迁移历史的只读追加方法"
```

---

### Task 4: 状态变更方法（原地回写）与注册

**Files:**
- Modify: `framework/src/XiHan.Framework.Upgrade.SqlSugar/Services/SqlSugarUpgradeVersionStore.cs`
- Create: `framework/src/XiHan.Framework.Upgrade.SqlSugar/Extensions/DependencyInjection/XiHanUpgradeSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Upgrade.SqlSugar/XiHanUpgradeSqlSugarModule.cs`
- Modify: `framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/UpgradeVersionStoreTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarUpgradeVersionStore`
- Produces: `SetUpgradingAsync`/`SetUpgradeCompletedAsync`/`SetUpgradeFailedAsync`/`UpdateDbVersionAsync` 四个方法的完整实现；扩展方法 `IServiceCollection AddXiHanUpgradeSqlSugar(this IServiceCollection services)`

**参考来源（动手前先读）：**
- 原地回写语义：spec 第 4.4 节、第 6 节陷阱①
- 主包既有注册：`framework/src/XiHan.Framework.Upgrade/Extensions/XiHanUpgradeServiceCollectionExtensions.cs:29`

**本任务禁止事项：** 硬约束 ①。另外**不要**在 `version.Id <= 0` 时静默按租户键新建一行——必须抛 `ArgumentException`，见 spec 4.4 节。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/UpgradeVersionStoreTests.cs` 的 `UpgradeVersionStoreTests` 类末尾（最后一个 `[Fact]` 之后）追加：

```csharp
    /// <summary>
    /// 设置升级中状态后回写调用方对象且数据库同步更新
    /// </summary>
    [Fact]
    public async Task 设置升级中状态后回写调用方对象且数据库同步更新()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();
        var version = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");
        var startTime = DateTimeOffset.UtcNow;

        await context.Store.SetUpgradingAsync(version, "node-1", startTime);

        Assert.True(version.IsUpgrading);
        Assert.Equal("node-1", version.UpgradeNode);
        Assert.Equal(startTime, version.UpgradeStartTime);

        var reloaded = context.Client.Queryable<SysUpgradeVersion>().Single(item => item.BasicId == version.Id);
        Assert.True(reloaded.IsUpgrading);
        Assert.Equal("node-1", reloaded.UpgradeNode);
    }

    /// <summary>
    /// 设置升级完成状态后回写调用方对象
    /// </summary>
    [Fact]
    public async Task 设置升级完成状态后回写调用方对象()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();
        var version = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");
        await context.Store.SetUpgradingAsync(version, "node-1", DateTimeOffset.UtcNow);

        await context.Store.SetUpgradeCompletedAsync(version, "1.1.0", "1.1.0");

        Assert.False(version.IsUpgrading);
        Assert.Equal("1.1.0", version.AppVersion);
        Assert.Equal("1.1.0", version.DbVersion);

        var reloaded = context.Client.Queryable<SysUpgradeVersion>().Single(item => item.BasicId == version.Id);
        Assert.False(reloaded.IsUpgrading);
        Assert.Equal("1.1.0", reloaded.AppVersion);
    }

    /// <summary>
    /// 设置升级失败状态后回写调用方对象
    /// </summary>
    [Fact]
    public async Task 设置升级失败状态后回写调用方对象()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();
        var version = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");
        await context.Store.SetUpgradingAsync(version, "node-1", DateTimeOffset.UtcNow);

        await context.Store.SetUpgradeFailedAsync(version);

        Assert.False(version.IsUpgrading);

        var reloaded = context.Client.Queryable<SysUpgradeVersion>().Single(item => item.BasicId == version.Id);
        Assert.False(reloaded.IsUpgrading);
    }

    /// <summary>
    /// 更新数据库版本后回写调用方对象
    /// </summary>
    [Fact]
    public async Task 更新数据库版本后回写调用方对象()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();
        var version = await context.Store.GetOrCreateAsync("1.0.0", "0.9.0");

        await context.Store.UpdateDbVersionAsync(version, "1.2.0");

        Assert.Equal("1.2.0", version.DbVersion);

        var reloaded = context.Client.Queryable<SysUpgradeVersion>().Single(item => item.BasicId == version.Id);
        Assert.Equal("1.2.0", reloaded.DbVersion);
    }

    /// <summary>
    /// 未经 GetOrCreateAsync 的 version 拒绝写入
    /// </summary>
    [Fact]
    public async Task 未经GetOrCreateAsync的version拒绝写入()
    {
        using var context = new UpgradeStoreTestContext();
        await context.Store.EnsureTablesAsync();
        var version = new UpgradeVersionState { Id = 0 };

        await Assert.ThrowsAsync<ArgumentException>(
            () => context.Store.UpdateDbVersionAsync(version, "1.0.0"));
    }

    /// <summary>
    /// 注册扩展以 SqlSugar 存储顶替内存实现
    /// </summary>
    [Fact]
    public void 注册扩展顶替内存实现()
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IUpgradeVersionStore, DefaultUpgradeVersionStore>();

        services.AddXiHanUpgradeSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IUpgradeVersionStore));
        Assert.Equal(typeof(SqlSugarUpgradeVersionStore), descriptor.ImplementationType);
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
```

预期：新增的回写相关用例失败于 `NotImplementedException`；注册用例失败于 `AddXiHanUpgradeSqlSugar` 不存在。

- [ ] **Step 3: 实现四个状态变更方法**

把 `SqlSugarUpgradeVersionStore.cs` 里四个 `throw new NotImplementedException("留待 Task 4 实现。");` 占位方法，依次替换为：

```csharp
    /// <summary>
    /// 设置升级中状态
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="nodeName">升级节点</param>
    /// <param name="startTime">开始时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task SetUpgradingAsync(UpgradeVersionState version, string nodeName, DateTimeOffset startTime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePersisted(version);

        var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();

        await client.Updateable<SysUpgradeVersion>()
            .SetColumns(item => new SysUpgradeVersion { IsUpgrading = true, UpgradeNode = nodeName, UpgradeStartTime = startTime })
            .Where(item => item.BasicId == version.Id)
            .ExecuteCommandAsync(cancellationToken);

        version.IsUpgrading = true;
        version.UpgradeNode = nodeName;
        version.UpgradeStartTime = startTime;
    }

    /// <summary>
    /// 设置升级完成状态
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="appVersion">应用版本</param>
    /// <param name="dbVersion">数据库版本</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task SetUpgradeCompletedAsync(UpgradeVersionState version, string appVersion, string dbVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePersisted(version);

        var normalizedAppVersion = UpgradeMapper.NormalizeVersion(appVersion);
        var normalizedDbVersion = UpgradeMapper.NormalizeVersion(dbVersion);
        var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();

        await client.Updateable<SysUpgradeVersion>()
            .SetColumns(item => new SysUpgradeVersion { IsUpgrading = false, AppVersion = normalizedAppVersion, DbVersion = normalizedDbVersion })
            .Where(item => item.BasicId == version.Id)
            .ExecuteCommandAsync(cancellationToken);

        version.IsUpgrading = false;
        version.AppVersion = normalizedAppVersion;
        version.DbVersion = normalizedDbVersion;
    }

    /// <summary>
    /// 设置升级失败状态
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task SetUpgradeFailedAsync(UpgradeVersionState version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePersisted(version);

        var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();

        await client.Updateable<SysUpgradeVersion>()
            .SetColumns(item => new SysUpgradeVersion { IsUpgrading = false })
            .Where(item => item.BasicId == version.Id)
            .ExecuteCommandAsync(cancellationToken);

        version.IsUpgrading = false;
    }

    /// <summary>
    /// 更新数据库版本
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <param name="dbVersion">数据库版本</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task UpdateDbVersionAsync(UpgradeVersionState version, string dbVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePersisted(version);

        var normalizedDbVersion = UpgradeMapper.NormalizeVersion(dbVersion);
        var client = _clientResolver.GetClientForEntity<SysUpgradeVersion>();

        await client.Updateable<SysUpgradeVersion>()
            .SetColumns(item => new SysUpgradeVersion { DbVersion = normalizedDbVersion })
            .Where(item => item.BasicId == version.Id)
            .ExecuteCommandAsync(cancellationToken);

        version.DbVersion = normalizedDbVersion;
    }
```

并在 `BackfillIfNeededAsync` 私有方法之后追加一个共用的校验方法：

```csharp
    /// <summary>
    /// 校验版本状态是否来自 GetOrCreateAsync 的返回值
    /// </summary>
    /// <param name="version">版本状态</param>
    /// <exception cref="ArgumentNullException">version 为 null</exception>
    /// <exception cref="ArgumentException">version.Id 不是合法的已持久化标识</exception>
    private static void EnsurePersisted(UpgradeVersionState version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (version.Id <= 0)
        {
            throw new ArgumentException("version.Id 必须来自 GetOrCreateAsync 的返回值。", nameof(version));
        }
    }
```

- [ ] **Step 4: 实现注册扩展**

`framework/src/XiHan.Framework.Upgrade.SqlSugar/Extensions/DependencyInjection/XiHanUpgradeSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.SqlSugar.Services;

namespace XiHan.Framework.Upgrade.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 升级模块 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanUpgradeSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换升级版本记录的内存实现
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanUpgradeSqlSugar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IUpgradeVersionStore, SqlSugarUpgradeVersionStore>());

        return services;
    }
}
```

- [ ] **Step 5: 在模块里调用扩展**

把 `XiHanUpgradeSqlSugarModule.cs` 改为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Upgrade;
using XiHan.Framework.Upgrade.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Upgrade.SqlSugar;

/// <summary>
/// 曦寒框架升级模块 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanUpgradeSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 存储替换 <see cref="XiHanUpgradeModule"/> 注册的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanUpgradeModule),
    typeof(XiHanDataModule)
)]
public class XiHanUpgradeSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanUpgradeSqlSugar();
    }
}
```

- [ ] **Step 6: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests/XiHan.Framework.Upgrade.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：全部测试 PASS（尤其四个"回写调用方对象"用例与"未经 `GetOrCreateAsync` 的 `version` 拒绝写入"）；构建 0 Warning(s) 0 Error(s)。

- [ ] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Upgrade.SqlSugar framework/test/XiHan.Framework.Upgrade.SqlSugar.Tests
git commit -m "feat(upgrade-sqlsugar): 实现升级状态变更方法并替换内存版"
```

---

### Task 5: 文档与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.Upgrade.SqlSugar/README.md`
- Create: `docs/packages/upgrade-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`（包索引表）
- Modify: `README.md`、`README_cn.md`（模块计数）
- Modify: `framework/README.md`、`framework/README_cn.md`（模块清单与计数）

**Interfaces:**
- Consumes: 前四个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 已知边界清单：spec 第 8 节
- 同类子包文档范本：`docs/packages/auditing-sqlsugar.md`
- 包索引表结构与既有行：`docs/packages/index.md`（第 85 行 `EventBus.SqlSugar`、第 99 行 `Auditing.SqlSugar`，均是链到子包页面的一行 + 一句话说明）

**本任务禁止事项：** 不要顺手改与本 PR 无关的文档。不要声称仓库有 `.codegraph/` 目录。**不要**往 `README.md`/`README_cn.md` 里加表格行——那两份是"常用包"精选清单，不是逐包穷举，只改计数。**不要**把模块计数当成本计划撰写时的某个固定数字硬编码进 commit——同系列的 Security.SqlSugar、Traffic.SqlSugar 等包会依次合并，先合并的包已经把计数改过，写死的数字对后合并的包必然是错的。

- [ ] **Step 1: 新增包文档**

`docs/packages/upgrade-sqlsugar.md`，按 `docs/packages/auditing-sqlsugar.md` 的结构组织，至少包含：包定位、两张表结构、`Tenant_Key` 设计理由、`Set*`/`Update*` 原地回写行为、互斥依赖 `IUpgradeLockProvider` 这一已知边界。

- [ ] **Step 2: 挂上侧边栏**

在 `docs/.vitepress/config.ts` 的 packages 分组里、`upgrade` 条目之后插入 `upgrade-sqlsugar` 条目。

- [ ] **Step 3: 登记进包索引**

`docs/packages/index.md` 是包索引表，按分层分组，第 105-114 行的「8. 存储 · 模板 · 任务 · 治理」一组里已有：

```markdown
| [Upgrade](./upgrade) | 升级引擎：版本存储、迁移执行、分布式锁、启动自动检查 |
| [Script](./script) | 脚本引擎：基于 Roslyn 的 C# 动态脚本、编译校验与超时 |
```

在 `[Upgrade](./upgrade)` 那一行**之后**、`[Script](./script)` 那一行**之前**插入：

```markdown
| [Upgrade.SqlSugar](./upgrade-sqlsugar) | 升级版本记录的 SqlSugar 持久化提供程序：版本状态与迁移历史落库，替换内存实现 |
```

行号是撰写时的快照，实现时用 `grep -n "\[Upgrade\](\./upgrade)"` 现场核对插入位置，不要凭行号硬改。

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

`framework/README.md` 与 `framework/README_cn.md` 除了计数，还要在模块清单表格中紧随 `XiHan.Framework.Upgrade` 之后加入 `XiHan.Framework.Upgrade.SqlSugar` 一行，描述用"升级版本记录 SqlSugar 持久化"。

- [ ] **Step 5: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

若构建因残留测试进程占用输出文件而失败：

```bash
taskkill //F //IM "XiHan.Framework.Upgrade.SqlSugar.Tests.exe"
```

- [ ] **Step 6: 注释复查**

通读本计划改动过的 `.cs` 文件的注释，判定标准是是否在讲论证、权衡、叙事，发现即移出到提交信息。

- [ ] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Upgrade.SqlSugar/README.md docs README.md README_cn.md framework/README.md framework/README_cn.md
git commit -m "docs(upgrade-sqlsugar): 补写包文档与模块清单"
```

---

## 完成标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `IUpgradeVersionStore` 经 `services.Replace` 顶替为 `SqlSugarUpgradeVersionStore`
- 四个 `Set*`/`Update*` 方法均有"回写调用方对象"的测试断言
- `GetOrCreateAsync` 的插入竞态有测试覆盖
- 包 README、`docs/packages/upgrade-sqlsugar.md`、VitePress 侧边栏、模块清单均已更新

## 已知边界（写入 PR 描述，不写进代码注释）

- **插入竞态不是数据库级强保证**：靠"插入失败重查"兜底，不建唯一约束
- **孤儿 `Id` 静默无操作**：见 spec 陷阱③（本包用 `EnsurePersisted` 拦截了 `Id <= 0` 的情况，但拦不住"传入一个不存在但为正数的 `Id`"这种更边缘的误用）
- **`HasMigrationHistoryAsync` 的大小写敏感性**：与内存实现的 `OrdinalIgnoreCase` 比较不完全一致
- **互斥依赖另一个契约**：`IUpgradeLockProvider` 目前仍是进程内实现，跨进程/跨机器部署时锁本身不是分布式的

## 下一份计划

本包只有一份计划。系列内三个小包（`Security.SqlSugar`、`Traffic.SqlSugar`、`Upgrade.SqlSugar`）均已各自成文，互不依赖，可并行实现。
