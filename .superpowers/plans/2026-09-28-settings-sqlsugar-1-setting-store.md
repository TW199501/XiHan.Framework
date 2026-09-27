# Settings.SqlSugar：设置存储（P1）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `XiHan.Framework.Settings.SqlSugar` 包，用一个 SqlSugar 落库实现顶替 `XiHan.Framework.Settings` 的 `NullSettingStore`；本包同时是「简单 CRUD Store」系列的范本，`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 三个后续小包照本计划的形状抄。完成后本包的独立 PR 可提交。

**Architecture:** 单实体 `SysSetting`（不分表、单库），四个契约方法直接在 `SqlSugarSettingStore` 里实现（不需要单独的 Mapping 层——契约的 `SettingValue` 本身就是名值对，没有复杂对象需要转换）。写入用「先查后写」而非数据库原子 upsert，读取对可能出现的重复行做防御式合并，不假设数据库唯一约束替自己兜底。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-settings-sqlsugar-1-setting-store-design.md`

> 该 spec 自成一体，实现本计划所需的全部约束都在其中。

**Linear 议题:** `https://linear.app/elf-express/issue/EDDIE-7`

**前置:** 无。`XiHan.Framework.Settings`、`XiHan.Framework.Data` 均已发布，不依赖任何进行中的其他计划。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/settings-sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-settings`）执行。worktree 内看不到这两个文档文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：从 `dev` 开 worktree，分支名 `feat/settings-sqlsugar`，上游是 `main`（**绝不在 `main` 上提交**）：

```bash
git worktree add ../XiHan.Framework-settings -b feat/settings-sqlsugar dev
```

**技术栈是 SqlSugar，不是 Entity Framework Core。** 下列 EF Core 惯用法一律禁止：

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | SqlSugar 无 change tracking，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join，本包不涉及 |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()`，本包不需要事务 |
| `Migrations` / `Add-Migration` / `EnsureCreated()` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |
| `DbSet.Find(key)` / `FirstOrDefaultAsync()` | `Queryable<T>().FirstAsync(expression)`，方法名是 `FirstAsync` 不是 `FirstOrDefaultAsync` |

**SqlSugar 签名只信源码**：

- 本包引用 `SqlSugarCore 5.1.4.221`，权威源码是 `/e/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`，那是 .NET Framework 变体）
- 文档：`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）

已核对的签名（本计划用到的全部方法）：

```csharp
// SqlSugarClient.cs:523
ISugarQueryable<T> Queryable<T>();

// Abstract/QueryableProvider/QueryableExecuteSqlAsync.cs:82-113
// 未命中返回 default(T)（引用类型即 null），不抛异常
Task<T> FirstAsync();
Task<T> FirstAsync(Expression<Func<T, bool>> expression);

// Abstract/DeleteProvider/DeleteableProvider.cs:274
IDeleteable<T> Where(Expression<Func<T, bool>> expression);

// SqlSugarClient.cs:192, 770
IInsertable<T> Insertable<T>(T insertObj) where T : class, new();
IUpdateable<T> Updateable<T>(T updateObj) where T : class, new();

// Abstract/UpdateProvider/UpdateableProvider.cs:180, 205；Abstract/DeleteProvider/DeleteableProvider.cs:52, 57（各自的 ExecuteCommandAsync 重载族）
Task<int> ExecuteCommandAsync();

// ExpressionsToSql/ResolveItems/BinaryExpressionResolve.cs:256-278
// item.ProviderKey == providerKey 在 providerKey 为 null 时翻译为 IS NULL，不是 = NULL
// ExpressionsToSql/ResolveItems/MethodCallExpressionResolve.cs:89-96
// names.Contains(item.SettingName) 翻译为 IN 查询（ContainsArray）
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**。权衡论证、踩坑叙事、设计理由、反事实推理（「否则会……」）一律进提交信息，不进注释
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以）
- `public` 成员必须有 `<summary>`（`GenerateDocumentationFile` 全局开启，缺了会告警）
- 本包无需任何 `XiHan{Feature}Options` 类型——四个契约方法不需要可配置项

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`、全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，**不 Import `version.props`、不设 `AssemblyName`**（范式：`XiHan.Framework.EventBus.SqlSugar.Tests.csproj`）。`Microsoft.Data.Sqlite` 经 `SqlSugarCore` 传递引入，**不需要额外 `PackageReference`**。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。范式：

```csharp
var client = new SqlSugarClient(new ConnectionConfig
{
    ConnectionString = $"DataSource={databaseFile};Pooling=False",
    DbType = DbType.Sqlite,
    IsAutoCloseConnection = true
});
```

**本包不需要真实数据库测试层**（spec §6 已说明理由：没有依赖具体方言的并发原语），`Assert.SkipWhen(...)` 那一套本计划不涉及。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，源码注释自认会随机变红），与本计划无关，不要去追它。

**提交信息**：中文 Conventional Commits，作用域 `settings-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

## 本计划特有的硬约束

**① 注册必须用 `services.Replace`，不能用 `TryAdd`。**

`NullSettingStore` 标注 `[Dependency(TryRegister = true)]`（`framework/src/XiHan.Framework.Settings/Stores/NullSettingStore.cs:15`），`DefaultConventionalRegistrar.AddType`（`framework/src/XiHan.Framework.Core/DependencyInjection/DefaultConventionalRegistrar.cs:58-61`）据此用 `services.TryAdd(...)` 把它登记为单例。`XiHanSettingsSqlSugarModule` `[DependsOn(typeof(XiHanSettingsModule))]`，依赖模块先装配，此刻 `TryAdd` 是空操作、`NullSettingStore` 会留在容器里，设置读写全部悄悄退化为空实现且不报错。必须用 `services.Replace(ServiceDescriptor.Scoped<ISettingStore, SqlSugarSettingStore>())`。

**② 生命周期从单例改为作用域。**

`SqlSugarSettingStore` 依赖 `ISqlSugarClientResolver`（`XiHanDataServiceCollectionExtensions.cs:61` 注册为 `Scoped`），不能注册为单例——单例捕获作用域依赖会在容器验证阶段构造失败。`ISettingManager` 本身是 `IScopedDependency`，两个值提供者是 `ITransientDependency`，改成 `Scoped` 不会产生新的被捕获依赖问题。

**③ 查询条件里的 `null` 直接用 `==` 比较，不要手写字符串 SQL。**

`item.ProviderKey == providerKey`（`providerKey` 可能是 `null`）会被 SqlSugar 正确翻译成 `IS NULL`（已核对 `BinaryExpressionResolve.cs:256-278`）。如果图省事换成字符串拼接，`providerKey` 为 `null` 时会拼出 `Provider_Key = ''` 或类似结果，全局设置（`providerKey` 恒为 `null`）会全部查不到值，且不报错，只是读回 `null`，与「设置从未写入」表现完全一样。

**④ `GetAllAsync` 用手写 `foreach` 写入 `Dictionary`，不能用 `rows.ToDictionary(...)`。**

`SetAsync` 是「先查后写」而非原子 upsert（本包引用的 SqlSugar 版本没有能安全处理可空复合键的声明式 upsert，也没有声明式复合唯一索引特性）。两个并发的 `SetAsync` 同时首次写同一个 `(name, providerName, providerKey)` 会各自插入一行。`ToDictionary` 遇到重复键抛 `ArgumentException`，且只有在真的发生过这种竞态后才会触发——"只测正常路径"的测试永远发现不了这个问题，上线后才会在一个和两次并发写入完全无关的读请求上炸出来。手写 `foreach` 逐条覆盖写入普通 `Dictionary` 则永不抛异常，语义与 `SetAsync` 的「后写覆盖前写」一致。Task 3 的测试要故意插入两行重复数据来验证这一点，不能只测「正常情况不重复」。

**⑤ 不要调用 `IUpdateable<T>.IsEnableUpdateVersionValidation()`。**

`SugarEntity<long>` 基类的 `RowVersion` 列带 `[SugarColumn(IsEnableUpdateVersionValidation = true)]`，但这只是列的元数据标注，本身不启用任何校验——真正的开关是 `Updateable(...).IsEnableUpdateVersionValidation()` 这个链式调用（`UpdateableProvider.cs:399`）。本包的 `SetAsync` **不**调用它：`Updateable(existing).ExecuteCommandAsync()` 就是普通的整实体更新，不做乐观锁校验。Task 3 的测试要覆盖「连续两次 `SetAsync` 覆盖同一设置」且第二次不抛 `VersionExceptions`。

---

## File Structure

```
framework/src/XiHan.Framework.Settings.SqlSugar/
  XiHan.Framework.Settings.SqlSugar.csproj      包定义，按序 Import 四个 props
  XiHanSettingsSqlSugarModule.cs                模块类，只做装配
  README.md                                     固定七段结构
  Entities/
    SysSetting.cs                               唯一实体
  Stores/
    SqlSugarSettingStore.cs                     ISettingStore 的实现
  Extensions/DependencyInjection/
    XiHanSettingsSqlSugarServiceCollectionExtensions.cs   Replace 注册

framework/test/XiHan.Framework.Settings.SqlSugar.Tests/
  XiHan.Framework.Settings.SqlSugar.Tests.csproj
  EntityMappingTests.cs          实体元数据 + SQLite 建表集成测试
  SqlSugarSettingStoreTests.cs   四个契约方法的行为测试（含竞态防御用例）
  RegistrationTests.cs           注册顶替断言
```

不设 `Mapping/` 目录：契约的 `SettingValue` 是纯名值对，`SqlSugarSettingStore` 直接在方法体内构造/读取 `SysSetting`，没有复杂对象转换需要抽成单独的映射类。

---

### Task 1: 包骨架、实体与建表测试

**Files:**
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/XiHan.Framework.Settings.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/XiHanSettingsSqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/README.md`
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/Entities/SysSetting.cs`
- Create: `framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Settings.SqlSugar.Tests/EntityMappingTests.cs`
- Modify: `framework/XiHan.Framework.slnx`（src 与 test 两处注册）

**Interfaces:**
- Consumes: `XiHan.Framework.Settings`（本任务尚不引用其类型，下个任务才用到 `ISettingStore`）、`XiHan.Framework.Data`（`SugarEntity<long>`）
- Produces: 程序集 `XiHan.Framework.Settings.SqlSugar`；`SysSetting`（`XiHan.Framework.Settings.SqlSugar.Entities`），继承 `SugarEntity<long>`，两个公开构造函数——`SysSetting()` 与 `SysSetting(long basicId)`；模块类型 `XiHanSettingsSqlSugarModule`（本任务暂无 `ConfigureServices` 覆写，Task 3 补上）

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- 实体基类：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs`（`Basic_Id`/`Row_Version` 列由基类提供，不要在 `SysSetting` 里重复声明）
- 大文本列类型：`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`（`StaticConfig.CodeFirst_BigString` 的用法）
- README 七段结构范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`
- 测试项目范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`
- SQLite 建表 API：`/e/source/platfrom-admin/docs/SqlSugar-docs/`（`CodeFirst.InitTables(type)`，本实体不分表，不需要 `SplitTables()`）

**本任务禁止事项：** 不要给 `SysSetting` 加 `[TableInitialization(IncludeModuleConnections = true)]`（本包单库，见 spec 共同问题第 4 条）。不要给 `SysSetting` 加任何索引特性。不要在这个任务里写 `SqlSugarSettingStore` 或注册逻辑——严格按 TDD 顺序，本任务只到「实体能被建出表」为止。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.Settings.SqlSugar/XiHan.Framework.Settings.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Settings.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Settings.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Settings.SqlSugar</PackageId>
        <Description>曦寒框架设置管理 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Settings\XiHan.Framework.Settings.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

四个 props 的 Import 顺序不能变（`netcore` / `common` / `version` / `nuget`）。

- [ ] **Step 2: 创建模块类骨架**

`framework/src/XiHan.Framework.Settings.SqlSugar/XiHanSettingsSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Settings;

namespace XiHan.Framework.Settings.SqlSugar;

/// <summary>
/// 曦寒框架设置管理 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSettingsSqlSugarModule))]</c> 即启用。
/// 本模块提供设置值的 SqlSugar 实体定义；存储实现的注册在后续任务提供。
/// </remarks>
[DependsOn(
    typeof(XiHanSettingsModule),
    typeof(XiHanDataModule)
)]
public class XiHanSettingsSqlSugarModule : XiHanModule
{
}
```

- [ ] **Step 3: 注册进解决方案（src）**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、`XiHan.Framework.Serialization` 那一行之后、`XiHan.Framework.Tasks` 之前插入：

```xml
    <Project Path="src/XiHan.Framework.Settings.SqlSugar/XiHan.Framework.Settings.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README**

`framework/src/XiHan.Framework.Settings.SqlSugar/README.md`：

```markdown
# XiHan.Framework.Settings.SqlSugar

## 概述

`XiHan.Framework.Settings` 的 SqlSugar 持久化提供程序，把设置存储 `ISettingStore` 从空实现替换为落库实现。

## 核心能力

- `ISettingStore` 的 SqlSugar 实现，四个契约方法（读单个、批量读、写、删）全部落库
- 按 `(设置名, 提供者名, 提供者键)` 三元组定位一条设置值，全局/租户/用户等任意作用域通用
- 表结构由 `DbInitializer` 在应用启动时创建（需开启建表初始化，见下）

## 依赖关系

依赖 `XiHan.Framework.Settings`（`ISettingStore` 契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_setting`；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增；不分表、单库。

建表需要开启 `XiHan.Framework.Data` 的建表初始化，**默认是关闭的**：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableTableInitialization": true
      }
    }
  }
}
```

未开启建表初始化又没有手工建表时，首次读写即抛「表不存在」。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanSettingsSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

## 扩展点

需要自定义落库行为时，实现 `ISettingStore` 并用 `services.Replace` 顶替本包的注册。

## 目录结构

```
Entities/    设置值实体
Stores/      ISettingStore 的 SqlSugar 实现
```
```

- [ ] **Step 5: 写失败的测试**

`framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Settings.SqlSugar\XiHan.Framework.Settings.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

在 `framework/XiHan.Framework.slnx` 的 `/2.tests/1.UnitTests/` 文件夹内、`XiHan.Framework.Serialization.Tests` 那一行之后、`XiHan.Framework.Settings.Tests` 之前插入：

```xml
    <Project Path="test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj" />
```

`framework/test/XiHan.Framework.Settings.SqlSugar.Tests/EntityMappingTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Settings.SqlSugar.Entities;

namespace XiHan.Framework.Settings.SqlSugar.Tests;

/// <summary>
/// 设置值实体映射测试
/// </summary>
public class EntityMappingTests
{
    /// <summary>
    /// 表名为 sys_setting
    /// </summary>
    [Fact]
    public void SysSetting_表名为固定表名()
    {
        var table = typeof(SysSetting).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_setting", table.TableName);
    }

    /// <summary>
    /// 设置名列使用 Pascal_Snake_Case 且非空
    /// </summary>
    [Fact]
    public void SysSetting_设置名列非空()
    {
        var property = typeof(SysSetting).GetProperty(nameof(SysSetting.SettingName));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Setting_Name", column.ColumnName);
        Assert.False(column.IsNullable);
    }

    /// <summary>
    /// 提供者名与提供者键列均可空
    /// </summary>
    [Fact]
    public void SysSetting_提供者名与提供者键均可空()
    {
        var providerName = typeof(SysSetting).GetProperty(nameof(SysSetting.ProviderName))!.GetCustomAttribute<SugarColumn>();
        var providerKey = typeof(SysSetting).GetProperty(nameof(SysSetting.ProviderKey))!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(providerName);
        Assert.True(providerName.IsNullable);
        Assert.Equal("Provider_Name", providerName.ColumnName);

        Assert.NotNull(providerKey);
        Assert.True(providerKey.IsNullable);
        Assert.Equal("Provider_Key", providerKey.ColumnName);
    }

    /// <summary>
    /// 设置值列使用大文本类型
    /// </summary>
    [Fact]
    public void SysSetting_设置值列使用大文本类型()
    {
        var column = typeof(SysSetting).GetProperty(nameof(SysSetting.SettingValue))!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Setting_Value", column.ColumnName);
        Assert.Equal(StaticConfig.CodeFirst_BigString, column.ColumnDataType);
    }

    /// <summary>
    /// 能建出 sys_setting 表
    /// </summary>
    [Fact]
    public void SysSetting_能建出表()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_settings_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"DataSource={databaseFile};Pooling=False",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

            db.CodeFirst.InitTables(typeof(SysSetting));

            var tableNames = db.DbMaintenance.GetTableInfoList(false).Select(table => table.Name).ToList();

            Assert.Contains("sys_setting", tableNames, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(databaseFile))
            {
                File.Delete(databaseFile);
            }
        }
    }
}
```

- [ ] **Step 6: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysSetting` 类型不存在。

- [ ] **Step 7: 实现实体**

`framework/src/XiHan.Framework.Settings.SqlSugar/Entities/SysSetting.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Settings.SqlSugar.Entities;

/// <summary>
/// 设置值实体
/// </summary>
[SugarTable("sys_setting")]
public class SysSetting : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysSetting() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysSetting(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 设置名称
    /// </summary>
    [SugarColumn(ColumnName = "Setting_Name", Length = 128, IsNullable = false, ColumnDescription = "设置名称")]
    public string SettingName { get; set; } = string.Empty;

    /// <summary>
    /// 提供者名称
    /// </summary>
    [SugarColumn(ColumnName = "Provider_Name", Length = 32, IsNullable = true, ColumnDescription = "提供者名称")]
    public string? ProviderName { get; set; }

    /// <summary>
    /// 提供者键
    /// </summary>
    [SugarColumn(ColumnName = "Provider_Key", Length = 64, IsNullable = true, ColumnDescription = "提供者键")]
    public string? ProviderKey { get; set; }

    /// <summary>
    /// 设置值
    /// </summary>
    [SugarColumn(ColumnName = "Setting_Value", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "设置值")]
    public string? SettingValue { get; set; }
}
```

- [ ] **Step 8: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：5 个测试全部 PASS。

- [ ] **Step 9: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。若出现 `XHFH001`，说明版权声明缺失或格式不符。

- [ ] **Step 10: 提交**

```bash
git add framework/src/XiHan.Framework.Settings.SqlSugar framework/test/XiHan.Framework.Settings.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(settings-sqlsugar): 新增包骨架与设置值实体"
```

---

### Task 2: Store 实现（四个契约方法）

**Files:**
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/Stores/SqlSugarSettingStore.cs`
- Create: `framework/test/XiHan.Framework.Settings.SqlSugar.Tests/SqlSugarSettingStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysSetting`；`XiHan.Framework.Settings` 的 `ISettingStore`、`SettingValue`；`XiHan.Framework.Data` 的 `ISqlSugarClientResolver`；`XiHan.Framework.DistributedIds` 的 `IDistributedIdGenerator<long>`
- Produces: `public class SqlSugarSettingStore : ISettingStore`，构造函数 `SqlSugarSettingStore(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator)`

**参考来源（动手前先读）：**
- 契约：`framework/src/XiHan.Framework.Settings/Stores/ISettingStore.cs`
- 客户端解析：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`（`GetClientForEntity<T>()` 默认路由语义）
- 同类用法：`framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarAccessLogWriter.cs`（`ISqlSugarClientResolver` + `IDistributedIdGenerator<long>` 的组合）
- ID 生成器注册可得性：`XiHan.Framework.Data` 已 `[DependsOn(typeof(XiHanDistributedIdsModule))]`，本模块经 `XiHan.Framework.Data` 间接获得 `IDistributedIdGenerator<long>`，**不需要**新增 `ProjectReference` 或 `DependsOn`
- 测试用桩：`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs` 里的 `StubClientResolver`（固定返回同一个客户端）与 `IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload()` 的用法

**本任务禁止事项：** 见「本计划特有的硬约束」①②③④⑤全部五条。另外不要给 `SqlSugarSettingStore` 加任何缓存——设置读取是否需要缓存由调用方（`SettingManager`/上层应用）决定，本包只做存储透传。不要给方法加 `CancellationToken` 参数——`ISettingStore` 契约本身没有。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Settings.SqlSugar.Tests/SqlSugarSettingStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.Settings.SqlSugar.Entities;
using XiHan.Framework.Settings.SqlSugar.Stores;

namespace XiHan.Framework.Settings.SqlSugar.Tests;

/// <summary>
/// 设置存储测试
/// </summary>
public class SqlSugarSettingStoreTests : IDisposable
{
    private readonly string _databaseFile;
    private readonly SqlSugarClient _client;
    private readonly SqlSugarSettingStore _store;

    /// <summary>
    /// 构造函数，建出临时 SQLite 库与被测存储
    /// </summary>
    public SqlSugarSettingStoreTests()
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_settings_{Guid.NewGuid():N}.db");

        _client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        _client.CodeFirst.InitTables(typeof(SysSetting));

        _store = new SqlSugarSettingStore(
            new StubClientResolver(_client),
            IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());
    }

    /// <summary>
    /// 释放临时库
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();

        if (File.Exists(_databaseFile))
        {
            File.Delete(_databaseFile);
        }
    }

    /// <summary>
    /// 写入后能读回相同值
    /// </summary>
    [Fact]
    public async Task 写入后能读回相同值()
    {
        await _store.SetAsync("App.PageSize", "20", "G", null);

        var value = await _store.GetOrNullAsync("App.PageSize", "G", null);

        Assert.Equal("20", value);
    }

    /// <summary>
    /// 不同提供者键互不覆盖
    /// </summary>
    [Fact]
    public async Task 不同提供者键互不覆盖()
    {
        await _store.SetAsync("App.PageSize", "10", "U", "user-1");
        await _store.SetAsync("App.PageSize", "30", "U", "user-2");

        Assert.Equal("10", await _store.GetOrNullAsync("App.PageSize", "U", "user-1"));
        Assert.Equal("30", await _store.GetOrNullAsync("App.PageSize", "U", "user-2"));
    }

    /// <summary>
    /// 提供者键为 null 时能精确匹配，不会被非 null 的键命中
    /// </summary>
    [Fact]
    public async Task 提供者键为Null时能精确匹配()
    {
        await _store.SetAsync("App.PageSize", "20", "G", null);

        Assert.Equal("20", await _store.GetOrNullAsync("App.PageSize", "G", null));
        Assert.Null(await _store.GetOrNullAsync("App.PageSize", "G", "somekey"));
    }

    /// <summary>
    /// 未命中返回 null
    /// </summary>
    [Fact]
    public async Task 未命中返回Null()
    {
        var value = await _store.GetOrNullAsync("Not.Exists", "G", null);

        Assert.Null(value);
    }

    /// <summary>
    /// 连续两次写入覆盖同一设置且不抛异常
    /// </summary>
    [Fact]
    public async Task 连续两次写入覆盖同一设置()
    {
        await _store.SetAsync("App.PageSize", "20", "G", null);
        await _store.SetAsync("App.PageSize", "50", "G", null);

        var value = await _store.GetOrNullAsync("App.PageSize", "G", null);

        Assert.Equal("50", value);
    }

    /// <summary>
    /// 删除后读回 null
    /// </summary>
    [Fact]
    public async Task 删除后读回Null()
    {
        await _store.SetAsync("App.PageSize", "20", "G", null);
        await _store.DeleteAsync("App.PageSize", "G", null);

        var value = await _store.GetOrNullAsync("App.PageSize", "G", null);

        Assert.Null(value);
    }

    /// <summary>
    /// 批量读取对未命中的名称也返回条目，顺序与输入一致
    /// </summary>
    [Fact]
    public async Task 批量读取对未命中的名称也返回条目()
    {
        await _store.SetAsync("App.PageSize", "20", "G", null);

        var values = await _store.GetAllAsync(["App.PageSize", "App.Theme", "App.Locale"], "G", null);

        Assert.Equal(3, values.Count);
        Assert.Equal("App.PageSize", values[0].Name);
        Assert.Equal("20", values[0].Value);
        Assert.Equal("App.Theme", values[1].Name);
        Assert.Null(values[1].Value);
        Assert.Equal("App.Locale", values[2].Name);
        Assert.Null(values[2].Value);
    }

    /// <summary>
    /// 批量读取对空数组直接返回空列表
    /// </summary>
    [Fact]
    public async Task 批量读取对空数组返回空列表()
    {
        var values = await _store.GetAllAsync([], "G", null);

        Assert.Empty(values);
    }

    /// <summary>
    /// 批量读取遇到重复行时不抛异常，返回其中一个值
    /// </summary>
    [Fact]
    public async Task 批量读取遇到重复行时不抛异常()
    {
        var idGenerator = IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload();

        await _client.Insertable(new SysSetting(idGenerator.NextId())
        {
            SettingName = "App.Duplicated",
            ProviderName = "G",
            ProviderKey = null,
            SettingValue = "first"
        }).ExecuteCommandAsync();

        await _client.Insertable(new SysSetting(idGenerator.NextId())
        {
            SettingName = "App.Duplicated",
            ProviderName = "G",
            ProviderKey = null,
            SettingValue = "second"
        }).ExecuteCommandAsync();

        var values = await _store.GetAllAsync(["App.Duplicated"], "G", null);

        Assert.Single(values);
        Assert.Contains(values[0].Value, new[] { "first", "second" });
    }
}

/// <summary>
/// 测试用客户端解析器，固定返回同一个客户端
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
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
    /// <returns>固定客户端</returns>
    public ISqlSugarClient GetCurrentClient()
    {
        return _client;
    }

    /// <summary>
    /// 获取实体对应的客户端
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>固定客户端</returns>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        return _client;
    }

    /// <summary>
    /// 按连接配置标识获取客户端
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    /// <returns>固定客户端</returns>
    public ISqlSugarClient GetClient(string configId)
    {
        return _client;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>固定标识集合</returns>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取当前布局的全部连接配置标识
    /// </summary>
    /// <returns>固定标识集合</returns>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取所有库的客户端
    /// </summary>
    /// <returns>固定客户端集合</returns>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [_client];
    }

    /// <summary>
    /// 获取当前工作单元已登记的连接配置标识
    /// </summary>
    /// <returns>空集合，测试桩不模拟工作单元登记</returns>
    public IReadOnlyList<string> GetEnlistedConfigIds()
    {
        return [];
    }

    /// <summary>
    /// 获取底层多租户接口
    /// </summary>
    /// <returns>不返回，始终抛出</returns>
    /// <exception cref="NotSupportedException">始终抛出</exception>
    public ITenant AsTenant()
    {
        throw new NotSupportedException("测试桩不支持多租户切换。");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarSettingStore` 不存在（`XiHan.Framework.Settings.SqlSugar.Stores` 命名空间也不存在）。

- [ ] **Step 3: 实现 Store**

`framework/src/XiHan.Framework.Settings.SqlSugar/Stores/SqlSugarSettingStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.Settings.Definitions;
using XiHan.Framework.Settings.SqlSugar.Entities;
using XiHan.Framework.Settings.Stores;

namespace XiHan.Framework.Settings.SqlSugar.Stores;

/// <summary>
/// 设置存储的 SqlSugar 实现
/// </summary>
public class SqlSugarSettingStore : ISettingStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarSettingStore(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 获取设置值
    /// </summary>
    /// <param name="name">设置名称</param>
    /// <param name="providerName">提供者名称</param>
    /// <param name="providerKey">提供者键</param>
    /// <returns>设置值，未命中返回 null</returns>
    public async Task<string?> GetOrNullAsync(string name, string? providerName, string? providerKey)
    {
        var client = _clientResolver.GetClientForEntity<SysSetting>();

        var entity = await client.Queryable<SysSetting>()
            .FirstAsync(item => item.SettingName == name
                && item.ProviderName == providerName
                && item.ProviderKey == providerKey);

        return entity?.SettingValue;
    }

    /// <summary>
    /// 获取所有设置值
    /// </summary>
    /// <param name="names">设置名称数组</param>
    /// <param name="providerName">提供者名称</param>
    /// <param name="providerKey">提供者键</param>
    /// <returns>设置值列表，未命中的名称对应值为 null，顺序与输入一致</returns>
    public async Task<List<SettingValue>> GetAllAsync(string[] names, string? providerName, string? providerKey)
    {
        if (names.Length == 0)
        {
            return [];
        }

        var client = _clientResolver.GetClientForEntity<SysSetting>();

        var rows = await client.Queryable<SysSetting>()
            .Where(item => names.Contains(item.SettingName)
                && item.ProviderName == providerName
                && item.ProviderKey == providerKey)
            .ToListAsync();

        var valuesByName = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            valuesByName[row.SettingName] = row.SettingValue;
        }

        return [.. names.Select(name => new SettingValue(name, valuesByName.GetValueOrDefault(name)))];
    }

    /// <summary>
    /// 设置值
    /// </summary>
    /// <param name="name">设置名称</param>
    /// <param name="value">设置值</param>
    /// <param name="providerName">提供者名称</param>
    /// <param name="providerKey">提供者键</param>
    public async Task SetAsync(string name, string? value, string? providerName, string? providerKey)
    {
        var client = _clientResolver.GetClientForEntity<SysSetting>();

        var existing = await client.Queryable<SysSetting>()
            .FirstAsync(item => item.SettingName == name
                && item.ProviderName == providerName
                && item.ProviderKey == providerKey);

        if (existing is null)
        {
            var entity = new SysSetting(_idGenerator.NextId())
            {
                SettingName = name,
                ProviderName = providerName,
                ProviderKey = providerKey,
                SettingValue = value
            };

            await client.Insertable(entity).ExecuteCommandAsync();
        }
        else
        {
            existing.SettingValue = value;

            await client.Updateable(existing).ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 删除设置值
    /// </summary>
    /// <param name="name">设置名称</param>
    /// <param name="providerName">提供者名称</param>
    /// <param name="providerKey">提供者键</param>
    public async Task DeleteAsync(string name, string? providerName, string? providerKey)
    {
        var client = _clientResolver.GetClientForEntity<SysSetting>();

        await client.Deleteable<SysSetting>()
            .Where(item => item.SettingName == name
                && item.ProviderName == providerName
                && item.ProviderKey == providerKey)
            .ExecuteCommandAsync();
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（Task 1 的 5 个 + 本任务的 9 个）。

- [ ] **Step 5: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：0 Warning(s) 0 Error(s)。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Settings.SqlSugar/Stores framework/test/XiHan.Framework.Settings.SqlSugar.Tests/SqlSugarSettingStoreTests.cs
git commit -m "feat(settings-sqlsugar): 新增设置存储的 SqlSugar 实现"
```

---

### Task 3: 注册扩展、模块接线与顶替测试

**Files:**
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/Extensions/DependencyInjection/XiHanSettingsSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Settings.SqlSugar/XiHanSettingsSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Settings.SqlSugar.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SqlSugarSettingStore`
- Produces: `public static IServiceCollection AddXiHanSettingsSqlSugar(this IServiceCollection services)`；`XiHanSettingsSqlSugarModule.ConfigureServices` 覆写

**参考来源（动手前先读）：**
- 顶替写法范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`
- 顶替断言测试范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxRegistrationTests.cs`
- `NullSettingStore` 的注册方式：`framework/src/XiHan.Framework.Settings/Stores/NullSettingStore.cs:15`（`[Dependency(TryRegister = true)]`）

**本任务禁止事项：** 见「本计划特有的硬约束」①。不要在 `ConfigureServices` 里写除了调用扩展方法之外的任何逻辑——模块类只做装配。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Settings.SqlSugar.Tests/RegistrationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Settings.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Settings.SqlSugar.Stores;
using XiHan.Framework.Settings.Stores;

namespace XiHan.Framework.Settings.SqlSugar.Tests;

/// <summary>
/// 注册扩展测试
/// </summary>
public class RegistrationTests
{
    /// <summary>
    /// 注册扩展以 SqlSugar 存储顶替空存储
    /// </summary>
    [Fact]
    public void 注册扩展顶替空存储()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<ISettingStore, NullSettingStore>();

        services.AddXiHanSettingsSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(ISettingStore));
        Assert.Equal(typeof(SqlSugarSettingStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`AddXiHanSettingsSqlSugar` 不存在。

- [ ] **Step 3: 实现注册扩展**

`framework/src/XiHan.Framework.Settings.SqlSugar/Extensions/DependencyInjection/XiHanSettingsSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Settings.SqlSugar.Stores;
using XiHan.Framework.Settings.Stores;

namespace XiHan.Framework.Settings.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 设置管理 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanSettingsSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换设置管理的空存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanSettingsSqlSugar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<ISettingStore, SqlSugarSettingStore>());

        return services;
    }
}
```

`Replace` 位于 `Microsoft.Extensions.DependencyInjection.Extensions` 命名空间，需要额外 `using`。

- [ ] **Step 4: 在模块里调用扩展**

把 `XiHanSettingsSqlSugarModule.cs` 改为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Settings.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Settings.SqlSugar;

/// <summary>
/// 曦寒框架设置管理 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSettingsSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 存储替换 <see cref="XiHanSettingsModule"/> 注册的空存储。
/// </remarks>
[DependsOn(
    typeof(XiHanSettingsModule),
    typeof(XiHanDataModule)
)]
public class XiHanSettingsSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanSettingsSqlSugar();
    }
}
```

`using XiHan.Framework.Settings;` 这一行要删掉——`XiHanSettingsModule` 现在只在 `[DependsOn]` 里以命名空间限定的方式被引用（`XiHan.Framework.Settings` 命名空间与本包自己的命名空间 `XiHan.Framework.Settings.SqlSugar` 前缀相同，`XiHanSettingsModule` 可以不加 `using` 直接写全名，或按下面这样保留 `using XiHan.Framework.Settings;`——两种写法任选，保留更清晰）：

```csharp
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Settings;
using XiHan.Framework.Settings.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Settings.SqlSugar;
```

- [ ] **Step 5: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS（累计 15 个）；构建 0 Warning(s) 0 Error(s)。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Settings.SqlSugar framework/test/XiHan.Framework.Settings.SqlSugar.Tests/RegistrationTests.cs
git commit -m "feat(settings-sqlsugar): 接线注册扩展并顶替空存储"
```

---

### Task 4: 文档与 PR 收尾

**Files:**
- Modify: `framework/src/XiHan.Framework.Settings.SqlSugar/README.md`
- Create: `docs/packages/settings-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`（包索引总览页的目录行 + 模块计数）
- Modify: `README.md`（根 · 英文，模块计数）
- Modify: `README_cn.md`（根 · 中文，模块计数）
- Modify: `framework/README.md`（模块清单 + 计数）
- Modify: `framework/README_cn.md`（模块清单 + 计数）

**Interfaces:**
- Consumes: 前三个任务的全部产出
- Produces: 无（终端任务）

**参考来源（动手前先读）：**
- 包文档范本：`docs/packages/eventbus-sqlsugar.md`（已读，结构照抄：概述 / 何时使用 / 安装与启用 / 表结构 / 工作原理 / 配置 / 主要 API / 注意事项 / 扩展点 / 依赖模块 / 相关模块）
- 侧边栏结构：`docs/.vitepress/config.ts`，「多租户 · 配置 · 校验」分组，`pkg("Settings 设置", "settings")` 所在行（约第 154 行）
- 根 README 模块计数：`README.md`、`README_cn.md` 各出现 3 处（badge、标语、文档站介绍行，约第 8、20、55 行），**没有逐包表格**——不要去找一个不存在的表格
- `framework/README.md`/`framework/README_cn.md` 模块表格：`Settings` 行（各自第 103 行附近），以及各 3 处计数（约第 55、186、198 行，其中第 198 行数的是测试项目数）
- **模块计数不是固定的 `68`**：本系列后续还有 `Tasks`/`Authentication`/`Authorization`/`Workflow`/`Security`/`Traffic`/`Upgrade` 七个包陆续落地，每个包合并时这个数字都会再 +1。动手改之前先读一遍这些文件里当前实际写的数字，不要假设是某个具体值
- **不要碰** `docs/packages/settings.md`：它记录的是主包既有缺口（`DefinitionProviders`/`ValueProviders` 未接线、`"T"` 只写不读等），与本包无关，改它等于顺手审一份无关的文档 PR
- **不要碰**架构 ASCII 图（`framework/README.md`/`framework/README_cn.md` 的 `Architecture Overview` 代码块）：`EventBus.SqlSugar`、`Auditing.SqlSugar` 落地时都没有把 `.SqlSugar` 子包加进这张图，保持一致

**本任务禁止事项：** 不要顺手重写与本 PR 无关的文档。不要在文档里声称仓库有 `.codegraph/` 目录。不要给 `docs/packages/settings-sqlsugar.md` 编造 `docs/packages/settings.md` 里没有核实过的行为——凡是引用主包行为的地方用链接指回去，不要复述。

- [ ] **Step 1: 更新包 README 的「使用方式」小节**

在 `framework/src/XiHan.Framework.Settings.SqlSugar/README.md` 的「扩展点」一节末尾追加一行：

```markdown

存储以 `services.Replace` 顶替 `XiHan.Framework.Settings` 注册的空实现（`NullSettingStore` 用 `[Dependency(TryRegister = true)]` 登记）。应用侧若要再次替换，同样使用 `Replace`——`TryAdd` 不会生效。
```

- [ ] **Step 2: 新增包文档**

`docs/packages/settings-sqlsugar.md`：

```markdown
# XiHan.Framework.Settings.SqlSugar

> 设置存储的 SqlSugar 持久化提供程序：按 (设置名, 提供者名, 提供者键) 三元组落库，替换 [Settings](./settings) 的空存储后设置读写才真正持久化。

- **NuGet**：`XiHan.Framework.Settings.SqlSugar`
- **模块类**：`XiHanSettingsSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Settings](./settings)（`ISettingStore` 契约）、[Data](./data)（SqlSugar 客户端、建表）

## 概述

[Settings](./settings) 提供了完整的设置定义、值提供者链与作用域读写骨架，但持久化层只有 `NullSettingStore`——读永远是 `null`，写是空操作，设置改了也不会被记住。

本包把设置值落到数据表，四个契约方法（`GetOrNullAsync` / `GetAllAsync` / `SetAsync` / `DeleteAsync`）全部实现为真实的库操作。

## 何时使用

- 用了 `ISettingManager` 做运行时可改的配置项，且希望修改能跨进程重启保留
- 需要按全局 / 用户（未来还有租户）等不同作用域各自持久化设置值

不需要本包的场景：只用编译期常量或 appsettings 静态配置，不需要运行时可写设置。

## 安装与启用

```bash
dotnet add package XiHan.Framework.Settings.SqlSugar
```

```csharp
[DependsOn(typeof(XiHanSettingsSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

建表需要开启 [Data](./data) 的建表初始化，**默认是关闭的**：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableTableInitialization": true
      }
    }
  }
}
```

`sys_setting` 不是分表，未开启建表初始化又没有手工建表时，首次读写即抛「表不存在」。

## 表结构

单张表 `sys_setting`，不分表：

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `long`，主键，非自增 | 雪花 ID |
| `Row_Version` | `long` | 版本标识列（本包不激活乐观锁校验，见「注意事项」） |
| `Setting_Name` | `string(128)`，非空 | 设置名称 |
| `Provider_Name` | `string(32)`，可空 | 提供者名称，如 `"G"`（全局）、`"U"`（用户） |
| `Provider_Key` | `string(64)`，可空 | 提供者键，如用户 ID；全局设置为 `null` |
| `Setting_Value` | 大文本，可空 | 设置值，写空即等价于从未写入 |

**没有数据库级别的复合唯一约束**：`(Setting_Name, Provider_Name, Provider_Key)` 的唯一性由应用层的「先查后写」保证，不是数据库兜底，见「注意事项」。

## 工作原理

### 读写

四个方法都先经 `ISqlSugarClientResolver.GetClientForEntity<SysSetting>()` 取客户端（`SysSetting` 未声明 `[DataSource]`，等价于取当前库），再按 `(Setting_Name, Provider_Name, Provider_Key)` 精确匹配一行。`Provider_Key` 为 `null` 时（全局设置）查询条件会被 SqlSugar 翻译为 `IS NULL`，不是 `= NULL`。

`SetAsync` 是「先查后写」：查到已有行就整行更新，查不到就插入新行；不是数据库原子 upsert。

## 配置

无需任何 `XiHan:` 配置节——四个契约方法不需要可配置项。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanSettingsSqlSugarModule` | 模块类，声明依赖即启用 |
| `SqlSugarSettingStore` | `ISettingStore` 的 SqlSugar 实现，注册为 `Scoped` |
| `SysSetting` | 设置值实体 |

## 注意事项与最佳实践

- **并发写同一设置无原子性**：两个并发写入者同时首次写同一 `(name, providerName, providerKey)` 会各自插入一行；`GetOrNullAsync`/`GetAllAsync` 对重复行采用「任取/后者覆盖前者」的方式读取，不抛异常，但也不保证读到哪一行。这是已知限制，不引入锁
- **不做乐观锁**：`Row_Version` 列存在但未激活校验（未调用 SqlSugar 的 `IsEnableUpdateVersionValidation()`），后写覆盖前写是既定语义
- **`"T"`（租户）提供者当前只写不读**：这是 [Settings](./settings) 主包的既有行为，`SettingManager.ResolveProvider` 会把 `Tenant` 作用域写成 `("T", tenantId)`，但读取路径尚未接入对应的值提供者；本包的存储层对任意 `providerName` 一视同仁，缺口不在本包
- **加密与校验在上层完成**：`SettingManager` 负责加密（`XiHanAesOptions`）与自定义校验（`SettingDefinition.Validator`），本包收到的 `value` 是最终存储值，原样落库

## 扩展点 / 自定义

需要完全自定义存储行为时，实现 `ISettingStore` 并在 DI 中 `Replace`。

## 依赖模块

- [Settings](./settings)：`ISettingStore` 契约与设置管理骨架
- [Data](./data)：SqlSugar 客户端解析、建表初始化

## 相关模块

- [Auditing.SqlSugar](./auditing-sqlsugar) / [EventBus.SqlSugar](./eventbus-sqlsugar)：同一套落库范式的其他实现
```

- [ ] **Step 3: 挂上侧边栏**

在 `docs/.vitepress/config.ts` 的「多租户 · 配置 · 校验」分组里、`pkg("Settings 设置", "settings")` 之后插入：

```typescript
          pkg("Settings.SqlSugar", "settings-sqlsugar"),
```

（裸包名不带中文后缀，与 `pkg("EventBus.SqlSugar", "eventbus-sqlsugar")`、`pkg("Auditing.SqlSugar", "auditing-sqlsugar")` 的既有写法一致。）

- [ ] **Step 4: 新增 `docs/packages/index.md` 的目录行**

`docs/packages/index.md` 是包索引总览页，每个包一行。`EventBus.SqlSugar`（第 85 行）、`Auditing.SqlSugar`（第 99 行）都在这里各占一行，本包遗漏了会导致索引页找不到它。

行放在第 5 节「多租户 · 配置 · 校验」的表格里、`[Settings](./settings)` 那一行（第 72 行）之后：

```markdown
| [Settings.SqlSugar](./settings-sqlsugar) | 设置管理持久化：`ISettingStore` 的 SqlSugar 落库实现 |
```

（格式与相邻两个 SqlSugar 子包一致：`[包名](./页面 slug)` + 一句话描述，描述文字与 Step 2 新增的 `docs/packages/settings-sqlsugar.md` 页首引用块保持同一措辞。）

**这一步对后续三个小包同样适用**：`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 各自在自己所属的分组表格里、紧跟主包那一行之后加一行，格式相同，不需要另外发明。

`docs/packages/index.md` 第 3 行也有一处模块计数（`XiHan.Framework 由 **NN 个 NuGet 包**组成`），一并计入下面 Step 5 的计数核对范围，不要漏改。

- [ ] **Step 5: 更新 framework/README.md 与 framework/README_cn.md**

在 `framework/README.md` 的模块表格里、`Settings` 那一行之后插入：

```markdown
| `Settings.SqlSugar` | Settings persistence: SqlSugar-backed `ISettingStore` implementation |
```

在 `framework/README_cn.md` 对应位置插入：

```markdown
| `Settings.SqlSugar` | 设置管理持久化：`ISettingStore` 的 SqlSugar 落库实现 |
```

两个文件里各有 3 处模块计数字符串：

- `framework/README.md:55`（形如 `NN modules, one per project ...`）、`:186`（ASCII 目录树注释 `# sources (NN modules)`）、`:198`（ASCII 目录树注释 `# tests (one per src project, NN unit-test projects)` —— 这一处数的是测试项目数，本计划的 Task 1 新增了 `XiHan.Framework.Settings.SqlSugar.Tests`，同样要 +1）
- `framework/README_cn.md` 的对应三处（`:55`、`:186`、`:198`，后者同样是测试项目数）

**不要硬编码「68 → 69」这类具体数字**：本系列后续还有七个包陆续落地，每次合并这个数字都会再变。正确做法是**先读这几处当前实际写的数字，在此基础上 +1**，而不是假设它就是某个固定值——这两个文件（连同 Step 4 的 `docs/packages/index.md` 第 3 行、下面 Step 6 的根 README）加起来总共 13 处这样的计数字符串，一次性用 `grep -n` 之类的方式定位到全部，逐一确认后再改，改完用 `grep` 复查这五个文件里的数字是否已经全部一致（同一个文件内的几处计数、以及跨文件之间，应该始终相等）。

**不要**改动 `Architecture Overview` 下面的 ASCII 图代码块——`EventBus.SqlSugar`、`Auditing.SqlSugar` 都没有出现在那张图里，保持一致。

- [ ] **Step 6: 更新根 README.md 与 README_cn.md**

这两个文件**没有逐包表格**，只有 3 处模块计数字符串（badge 图标、`<p>` 标语、文档站介绍行），同样**不要硬编码具体数字**——先读当前值再 +1：

- `README.md`：约第 8 行 `<p>...NN modular components...</p>`、第 20 行 `Modules-NN-1f6feb` badge、第 55 行 `for all NN packages`
- `README_cn.md`：约第 8 行 `<p>...NN 个模块化组件...</p>`、第 20 行同一个 badge、第 55 行 `NN 个包的逐包 API 文档`

改完后这五个文件（`README.md`、`README_cn.md`、`framework/README.md`、`framework/README_cn.md`、`docs/packages/index.md`）里所有的计数字符串必须是**同一个数字**——五个文件、总共 13 处，缺一处没改就会出现前后矛盾的计数，肉眼或 `grep -c` 都能一眼看出。

- [ ] **Step 7: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 Warning(s) 0 Error(s)；全部测试通过（已知无关抖动见 Global Constraints，不要因为它去改本 PR 的代码）。

- [ ] **Step 8: 复查注释是否混入论证**

逐个通读本 PR 新增的 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [ ] **Step 9: 提交**

```bash
git add framework/src/XiHan.Framework.Settings.SqlSugar/README.md docs README.md README_cn.md framework/README.md framework/README_cn.md
git commit -m "docs(settings-sqlsugar): 补写包文档、侧边栏与模块清单"
```

---

## 完成标准

PR 可提交时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动见 Global Constraints）
- `ISettingStore` 经 `services.Replace` 顶替，有测试断言生命周期为 `Scoped`
- 四个契约方法都有 SQLite 落库测试覆盖，包括 §5.1/§5.2 两个陷阱各自的用例（重复行不抛异常、连续两次写入不抛 `VersionExceptions`）
- 包 README、`docs/packages/settings-sqlsugar.md`、VitePress 侧边栏、`docs/packages/index.md` 目录行、`framework/README.md`/`framework/README_cn.md` 模块清单与计数、根 `README.md`/`README_cn.md` 计数均已更新
- 新增代码的注释只说明代码做什么

## 已知边界（写入 PR 描述，不写进代码注释）

- **并发写同一设置无原子性**：`SetAsync` 是先查后写，不是数据库级 upsert。已知限制，见 spec §7
- **无数据库级复合唯一约束**：正确性完全依赖应用层的先查后写与去重读取
- **不做乐观锁**：`Row_Version` 列存在但不激活校验，后写覆盖前写
- **建表默认关闭**：`EnableTableInitialization` 默认 `false`，配置层面的逃生口是把它打开
- **"T"（租户）只写不读**：主包既有缺口，不在本包范围
- **不做批量写入**：`SetAsync` 每次一条 SQL 往返

## 下一份计划

拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md`）建议的下一个包是 `Tasks.SqlSugar`（背景作业领取可复用 `EventBus.SqlSugar` P4/P6 已验证过的三步抢占协议），随后是 `Authentication.SqlSugar`、`Authorization.SqlSugar`；`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 三个小包应直接照抄本计划建立的形状（单实体、无 Mapping 层、先查后写、GetAllAsync 手写 `foreach` 去重）。
