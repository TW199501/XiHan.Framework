# Settings.SqlSugar：设置存储（P1）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `XiHan.Framework.Settings.SqlSugar` 包，用一个 SqlSugar 落库实现顶替 `XiHan.Framework.Settings` 的 `NullSettingStore`；本包同时是「简单 CRUD Store」系列的范本，`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 三个后续小包照本计划的形状抄。完成后本包的独立 PR 可提交。

**Architecture:** 单实体 `SysSetting`（不分表、单库），四个契约方法直接在 `SqlSugarSettingStore` 里实现（不需要单独的 Mapping 层——契约的 `SettingValue` 本身就是名值对，没有复杂对象需要转换）。`Provider_Name`/`Provider_Key` 归一化为非空列（`null` → 哨兵值 `string.Empty`），配合三列复合唯一索引 `uk_sys_setting_key`：常见路径是先查后写，并发首次创建同一设置时数据库唯一索引 + 一次重新查询把竞态收敛成正常更新，不产生重复行、不抛异常给调用方。

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
| `[Index(nameof(A), nameof(B), IsUnique = true)]`（EF Core 模型级索引） | `[SugarIndex(indexName, nameof(A), OrderByType.Asc, nameof(B), OrderByType.Asc, isUnique: true)]`，字段名是 C# 属性名，不是列名 |

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

// Entities/Mapping/SugarMappingAttribute.cs:341-467
// 类级特性，AllowMultiple = true；字段位传的是 C# 属性名，由 CodeFirstProvider.CreateIndex
// 按 entityInfo.Columns.FirstOrDefault(z => z.PropertyName == it.Key) 解析出真实列名
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class SugarIndexAttribute : Attribute
{
    public SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1,
        string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3,
        bool isUnique = false);
}

// Abstract/CodeFirstProvider/CodeFirstProvider.cs:317-328
// db.CodeFirst.InitTables(type) 内部会对每个表调用 CreateIndex(entityInfo)，索引随建表一起创建，不需要额外调用
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

**本包不需要真实数据库测试层**（spec §6 已说明理由：唯一约束冲突是标准 SQL 行为，`catch` 块的处理逻辑不依赖任何方言细节；真正的写入时序竞态无法在任何单进程同步测试里确定性构造，加真实数据库层也验证不了这一点），`Assert.SkipWhen(...)` 那一套本计划不涉及。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，源码注释自认会随机变红），与本计划无关，不要去追它。

**提交信息**：中文 Conventional Commits，作用域 `settings-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

## 本计划特有的硬约束

**① 注册必须用 `services.Replace`，不能用 `TryAdd`。**

`NullSettingStore` 标注 `[Dependency(TryRegister = true)]`（`framework/src/XiHan.Framework.Settings/Stores/NullSettingStore.cs:15`），`DefaultConventionalRegistrar.AddType`（`framework/src/XiHan.Framework.Core/DependencyInjection/DefaultConventionalRegistrar.cs:58-61`）据此用 `services.TryAdd(...)` 把它登记为单例。`XiHanSettingsSqlSugarModule` `[DependsOn(typeof(XiHanSettingsModule))]`，依赖模块先装配，此刻 `TryAdd` 是空操作、`NullSettingStore` 会留在容器里，设置读写全部悄悄退化为空实现且不报错。必须用 `services.Replace(ServiceDescriptor.Scoped<ISettingStore, SqlSugarSettingStore>())`。

**② 生命周期从单例改为作用域。**

`SqlSugarSettingStore` 依赖 `ISqlSugarClientResolver`（`XiHanDataServiceCollectionExtensions.cs:61` 注册为 `Scoped`），不能注册为单例——单例捕获作用域依赖会在容器验证阶段构造失败。`ISettingManager` 本身是 `IScopedDependency`，两个值提供者是 `ITransientDependency`，改成 `Scoped` 不会产生新的被捕获依赖问题。

**③ `Provider_Name`/`Provider_Key` 是非空列，四个方法的第一步永远是归一化。**

`SysSetting.ProviderName`/`ProviderKey` 声明为非空 `string`（不是 `string?`），`null` 在存储边界统一归一化为哨兵值 `string.Empty`（私有静态方法 `Normalize(string? value) => value ?? string.Empty`）。`GetOrNullAsync`/`GetAllAsync`/`SetAsync`/`DeleteAsync` 的第一步都是 `Normalize(providerName)`/`Normalize(providerKey)`，之后的查询/写入只使用归一化后的局部变量，**不再触碰方法参数本身**。

漏掉这一步的后果：SqlSugar 把 `item.ProviderKey == providerKey`（`providerKey` 为原始的 `null`）翻译成 `Provider_Key IS NULL`，但列里从来不存真正的 `NULL`，`IS NULL` 永远为假——全局设置（`providerKey` 恒为 `null`）的读写会**彻底静默失效**，不抛任何异常，表现和「从未配置过」一模一样。

**④ 三列复合唯一索引 `uk_sys_setting_key` 是正确性的地基，不是查询优化。**

`SysSetting` 上的 `[SugarIndex("uk_sys_setting_key", nameof(SysSetting.SettingName), OrderByType.Asc, nameof(SysSetting.ProviderName), OrderByType.Asc, nameof(SysSetting.ProviderKey), OrderByType.Asc, isUnique: true)]` 随 `db.CodeFirst.InitTables(typeof(SysSetting))` 一并创建（`CodeFirstProvider.cs:317-328` 的 `CreateIndex` 调用）。它保证同一个 `(Setting_Name, Provider_Name, Provider_Key)` 组合在数据库层面永远只有一行——这是 `SetAsync` 处理并发首次创建同一设置的**唯一**防线，不能删掉或误标成非唯一索引。

**⑤ `SetAsync` 里对唯一约束冲突的处理是「捕获后重新查询确认」，不是解析异常类型或错误码。**

```csharp
try
{
    await client.Insertable(entity).ExecuteCommandAsync();
}
catch (Exception)
{
    var winner = await FindAsync(client, name, normalizedProviderName, normalizedProviderKey);

    if (winner is null)
    {
        throw;
    }

    winner.SettingValue = value;
    await client.Updateable(winner).ExecuteCommandAsync();
}
```

**不要**把 `catch (Exception)` 收窄成 `catch (SqliteException ex) when (ex.SqliteErrorCode == 19)` 这类只认单一方言的写法——本地用 SQLite 跑的测试完全发现不了这个问题（因为测试环境本来就是 SQLite），等应用换成 MySQL/PostgreSQL 部署后，插入失败会因为异常类型不匹配而直接抛给调用方，`SetAsync` 在竞态下从「静默处理」退化成「对外抛异常」。`catch` 之后立即按业务键重新查询：查到就说明是并发写入触发的约束冲突，按更新处理；查不到就说明失败另有原因，`throw;`（不是 `throw ex;`）保留原始堆栈重新抛出。

**⑥ 不要调用 `IUpdateable<T>.IsEnableUpdateVersionValidation()`。**

`SugarEntity<long>` 基类的 `RowVersion` 列带 `[SugarColumn(IsEnableUpdateVersionValidation = true)]`，但这只是列的元数据标注，本身不启用任何校验——真正的开关是 `Updateable(...).IsEnableUpdateVersionValidation()` 这个链式调用（`UpdateableProvider.cs:399`）。本包的 `SetAsync` **不**调用它：两处 `Updateable(...).ExecuteCommandAsync()` 都是普通的整实体更新，不做乐观锁校验。Task 2 的测试要覆盖「连续两次 `SetAsync` 覆盖同一设置」且第二次不抛 `VersionExceptions`。

**⑦ `GetAllAsync` 用手写 `foreach` 写入 `Dictionary`，不能用 `rows.ToDictionary(...)`。**

正常写入路径下，唯一索引保证同一个键不会出现两行，`foreach` 和 `ToDictionary` 结果一致。但索引只在 `EnableTableInitialization` 开启并跑过 `CodeFirst.InitTables(...)` 之后才存在——若某个部署跳过建表初始化、手工建了一张没有这个索引的旧表，历史数据仍可能重复。`foreach` 逐条覆盖写入是零成本的防御：正常情况下结果和 `ToDictionary` 一样，出现历史脏数据时也不会抛 `ArgumentException`。

---

## File Structure

```
framework/src/XiHan.Framework.Settings.SqlSugar/
  XiHan.Framework.Settings.SqlSugar.csproj      包定义，按序 Import 四个 props
  XiHanSettingsSqlSugarModule.cs                模块类，只做装配
  README.md                                     固定七段结构
  Entities/
    SysSetting.cs                               唯一实体，三列复合唯一索引
  Stores/
    SqlSugarSettingStore.cs                     ISettingStore 的实现
  Extensions/DependencyInjection/
    XiHanSettingsSqlSugarServiceCollectionExtensions.cs   Replace 注册

framework/test/XiHan.Framework.Settings.SqlSugar.Tests/
  XiHan.Framework.Settings.SqlSugar.Tests.csproj
  EntityMappingTests.cs          实体元数据、建表、唯一索引的结构性测试
  SqlSugarSettingStoreTests.cs   四个契约方法的行为测试（含归一化、并发首次创建）
  RegistrationTests.cs           注册顶替断言
```

不设 `Mapping/` 目录：契约的 `SettingValue` 是纯名值对，`SqlSugarSettingStore` 直接在方法体内构造/读取 `SysSetting`，没有复杂对象转换需要抽成单独的映射类。

---

### Task 1: 包骨架、实体（含唯一索引）与建表测试

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
- Produces: 程序集 `XiHan.Framework.Settings.SqlSugar`；`SysSetting`（`XiHan.Framework.Settings.SqlSugar.Entities`），继承 `SugarEntity<long>`，两个公开构造函数——`SysSetting()` 与 `SysSetting(long basicId)`；三个字符串属性 `SettingName`/`ProviderName`/`ProviderKey`（均非空 `string`，不是 `string?`）+ 可空的 `SettingValue`；类级 `[SugarIndex("uk_sys_setting_key", ...)]`；模块类型 `XiHanSettingsSqlSugarModule`（本任务暂无 `ConfigureServices` 覆写，Task 3 补上）

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- 实体基类：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs`（`Basic_Id`/`Row_Version` 列由基类提供，不要在 `SysSetting` 里重复声明）
- 大文本列类型：`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`（`StaticConfig.CodeFirst_BigString` 的用法）
- 唯一索引特性：`/e/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Entities/Mapping/SugarMappingAttribute.cs:341-467`（`SugarIndexAttribute` 的构造函数重载，字段位是属性名）
- README 七段结构范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`
- 测试项目范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`
- SQLite 建表 API：`/e/source/platfrom-admin/docs/SqlSugar-docs/`（`CodeFirst.InitTables(type)`，本实体不分表，不需要 `SplitTables()`）

**本任务禁止事项：** 不要给 `SysSetting` 加 `[TableInitialization(IncludeModuleConnections = true)]`（本包单库，见 spec 共同问题第 4 条）。不要把 `ProviderName`/`ProviderKey` 声明成 `string?`——它们是非空列，`null` 的处理在 Store 层（Task 2），实体本身不应该出现可空的提供者字段。不要在这个任务里写 `SqlSugarSettingStore` 或注册逻辑——严格按 TDD 顺序，本任务只到「实体能被建出表、唯一索引确实生效」为止。

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
- 三列复合唯一索引保证同一组合永远只有一行，并发首次创建同一设置不会产生重复数据
- 表结构由 `DbInitializer` 在应用启动时创建（需开启建表初始化，见下）

## 依赖关系

依赖 `XiHan.Framework.Settings`（`ISettingStore` 契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_setting`；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增；`Provider_Name`/`Provider_Key` 为非空列，未指定时存归一化占位符（空字符串）；不分表、单库。

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
    /// 提供者名与提供者键列均为非空列
    /// </summary>
    [Fact]
    public void SysSetting_提供者名与提供者键均非空()
    {
        var providerName = typeof(SysSetting).GetProperty(nameof(SysSetting.ProviderName))!.GetCustomAttribute<SugarColumn>();
        var providerKey = typeof(SysSetting).GetProperty(nameof(SysSetting.ProviderKey))!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(providerName);
        Assert.False(providerName.IsNullable);
        Assert.Equal("Provider_Name", providerName.ColumnName);

        Assert.NotNull(providerKey);
        Assert.False(providerKey.IsNullable);
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
    /// 声明了覆盖三列的唯一索引
    /// </summary>
    [Fact]
    public void SysSetting_声明了唯一索引()
    {
        var index = typeof(SysSetting).GetCustomAttribute<SugarIndexAttribute>();

        Assert.NotNull(index);
        Assert.True(index.IsUnique);
        Assert.Equal(3, index.IndexFields.Count);
        Assert.Contains(nameof(SysSetting.SettingName), index.IndexFields.Keys);
        Assert.Contains(nameof(SysSetting.ProviderName), index.IndexFields.Keys);
        Assert.Contains(nameof(SysSetting.ProviderKey), index.IndexFields.Keys);
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

    /// <summary>
    /// 相同键的第二次插入被唯一索引拒绝
    /// </summary>
    [Fact]
    public void SysSetting_相同键的第二次插入被唯一索引拒绝()
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

            db.Insertable(new SysSetting(1L)
            {
                SettingName = "App.PageSize",
                ProviderName = "G",
                ProviderKey = string.Empty,
                SettingValue = "20"
            }).ExecuteCommand();

            Assert.ThrowsAny<Exception>(() => db.Insertable(new SysSetting(2L)
            {
                SettingName = "App.PageSize",
                ProviderName = "G",
                ProviderKey = string.Empty,
                SettingValue = "30"
            }).ExecuteCommand());
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
[SugarIndex("uk_sys_setting_key",
    nameof(SettingName), OrderByType.Asc,
    nameof(ProviderName), OrderByType.Asc,
    nameof(ProviderKey), OrderByType.Asc,
    isUnique: true)]
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
    /// 提供者名称，未指定时存归一化占位符
    /// </summary>
    [SugarColumn(ColumnName = "Provider_Name", Length = 32, IsNullable = false, ColumnDescription = "提供者名称，未指定时存归一化占位符")]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// 提供者键，未指定时存归一化占位符
    /// </summary>
    [SugarColumn(ColumnName = "Provider_Key", Length = 64, IsNullable = false, ColumnDescription = "提供者键，未指定时存归一化占位符")]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// 设置值
    /// </summary>
    [SugarColumn(ColumnName = "Setting_Value", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "设置值")]
    public string? SettingValue { get; set; }
}
```

`[SugarIndex]` 的字段位用 `nameof(SettingName)` 等（类内可以省略类型前缀），传的是 C# 属性名，不是 `Setting_Name` 这样的列名——传列名会在建表时找不到对应属性而抛异常。

- [ ] **Step 8: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：7 个测试全部 PASS。

- [ ] **Step 9: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。若出现 `XHFH001`，说明版权声明缺失或格式不符。

- [ ] **Step 10: 提交**

```bash
git add framework/src/XiHan.Framework.Settings.SqlSugar framework/test/XiHan.Framework.Settings.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(settings-sqlsugar): 新增包骨架、设置值实体与唯一索引"
```

---

### Task 2: Store 实现（四个契约方法 + 归一化 + 并发首次创建兜底）

**Files:**
- Create: `framework/src/XiHan.Framework.Settings.SqlSugar/Stores/SqlSugarSettingStore.cs`
- Create: `framework/test/XiHan.Framework.Settings.SqlSugar.Tests/SqlSugarSettingStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysSetting`；`XiHan.Framework.Settings` 的 `ISettingStore`、`SettingValue`；`XiHan.Framework.Data` 的 `ISqlSugarClientResolver`；`XiHan.Framework.DistributedIds` 的 `IDistributedIdGenerator<long>`
- Produces: `public class SqlSugarSettingStore : ISettingStore`，构造函数 `SqlSugarSettingStore(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator)`；私有静态方法 `Normalize(string?)`、`FindAsync(ISqlSugarClient, string, string, string)`

**参考来源（动手前先读）：**
- 契约：`framework/src/XiHan.Framework.Settings/Stores/ISettingStore.cs`
- 客户端解析：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`（`GetClientForEntity<T>()` 默认路由语义）
- 同类用法：`framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarAccessLogWriter.cs`（`ISqlSugarClientResolver` + `IDistributedIdGenerator<long>` 的组合）
- ID 生成器注册可得性：`XiHan.Framework.Data` 已 `[DependsOn(typeof(XiHanDistributedIdsModule))]`，本模块经 `XiHan.Framework.Data` 间接获得 `IDistributedIdGenerator<long>`，**不需要**新增 `ProjectReference` 或 `DependsOn`
- 测试用桩：`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs` 里的 `StubClientResolver`（固定返回同一个客户端）与 `IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload()` 的用法

**本任务禁止事项：** 见「本计划特有的硬约束」③④⑤⑥⑦全部五条。另外不要给 `SqlSugarSettingStore` 加任何缓存——设置读取是否需要缓存由调用方（`SettingManager`/上层应用）决定，本包只做存储透传。不要给方法加 `CancellationToken` 参数——`ISettingStore` 契约本身没有。

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
    /// 提供者键为 null 时反复写入只保留一行，验证归一化生效
    /// </summary>
    [Fact]
    public async Task 提供者键为Null时反复写入只保留一行()
    {
        await _store.SetAsync("App.PageSize", "20", "G", null);
        await _store.SetAsync("App.PageSize", "30", "G", null);

        var rows = await _client.Queryable<SysSetting>()
            .Where(item => item.SettingName == "App.PageSize" && item.ProviderName == "G")
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("30", rows[0].SettingValue);
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
    /// 写入命中已被其他写入者创建的行时更新而不是重复插入
    /// </summary>
    [Fact]
    public async Task 写入命中已存在的行时更新而不是重复插入()
    {
        var idGenerator = IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload();

        await _client.Insertable(new SysSetting(idGenerator.NextId())
        {
            SettingName = "App.NewSetting",
            ProviderName = "G",
            ProviderKey = string.Empty,
            SettingValue = "first"
        }).ExecuteCommandAsync();

        await _store.SetAsync("App.NewSetting", "second", "G", null);

        var rows = await _client.Queryable<SysSetting>()
            .Where(item => item.SettingName == "App.NewSetting")
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("second", rows[0].SettingValue);
    }

    /// <summary>
    /// 插入前被竞争对手抢先创建同一设置时，回退为更新且只剩一行
    /// </summary>
    [Fact]
    public async Task 插入前被竞争对手抢先创建同一设置时回退为更新()
    {
        var idGenerator = IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload();

        using var competingClient = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        var racingStore = new RacingSqlSugarSettingStore(
            new StubClientResolver(_client),
            idGenerator,
            competingClient,
            () => new SysSetting(idGenerator.NextId())
            {
                SettingName = "App.Racing",
                ProviderName = "G",
                ProviderKey = string.Empty,
                SettingValue = "first"
            });

        await racingStore.SetAsync("App.Racing", "second", "G", null);

        var rows = await _client.Queryable<SysSetting>()
            .Where(item => item.SettingName == "App.Racing")
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("second", rows[0].SettingValue);
    }
}

/// <summary>
/// 测试用 Store 子类，在插入前的钩子里通过另一个客户端抢先插入同一个键，模拟“先查阶段都判定不存在、写入阶段才分出先后”的竞态
/// </summary>
internal sealed class RacingSqlSugarSettingStore : SqlSugarSettingStore
{
    private readonly ISqlSugarClient _competingClient;
    private readonly Func<SysSetting> _competingEntityFactory;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="competingClient">代表竞争对手的另一个客户端，指向同一个数据库文件</param>
    /// <param name="competingEntityFactory">竞争对手抢先插入的行</param>
    public RacingSqlSugarSettingStore(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator,
        ISqlSugarClient competingClient,
        Func<SysSetting> competingEntityFactory)
        : base(clientResolver, idGenerator)
    {
        _competingClient = competingClient;
        _competingEntityFactory = competingEntityFactory;
    }

    /// <summary>
    /// 在基类确认目标行不存在、正式插入之前，抢先用另一个客户端插入同一个键
    /// </summary>
    /// <param name="entity">即将插入的实体</param>
    /// <param name="cancellationToken">取消令牌</param>
    protected override async Task OnBeforeInsertAsync(SysSetting entity, CancellationToken cancellationToken)
    {
        await _competingClient.Insertable(_competingEntityFactory()).ExecuteCommandAsync();
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

using SqlSugar;
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

        var entity = await FindAsync(client, name, Normalize(providerName), Normalize(providerKey));

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
        var normalizedProviderName = Normalize(providerName);
        var normalizedProviderKey = Normalize(providerKey);

        var rows = await client.Queryable<SysSetting>()
            .Where(item => names.Contains(item.SettingName)
                && item.ProviderName == normalizedProviderName
                && item.ProviderKey == normalizedProviderKey)
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
        var normalizedProviderName = Normalize(providerName);
        var normalizedProviderKey = Normalize(providerKey);

        var existing = await FindAsync(client, name, normalizedProviderName, normalizedProviderKey);

        if (existing is not null)
        {
            existing.SettingValue = value;
            await client.Updateable(existing).ExecuteCommandAsync();
            return;
        }

        var entity = new SysSetting(_idGenerator.NextId())
        {
            SettingName = name,
            ProviderName = normalizedProviderName,
            ProviderKey = normalizedProviderKey,
            SettingValue = value
        };

        await OnBeforeInsertAsync(entity, CancellationToken.None);

        try
        {
            await client.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            var winner = await FindAsync(client, name, normalizedProviderName, normalizedProviderKey);

            if (winner is null)
            {
                throw;
            }

            winner.SettingValue = value;
            await client.Updateable(winner).ExecuteCommandAsync();
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
        var normalizedProviderName = Normalize(providerName);
        var normalizedProviderKey = Normalize(providerKey);

        await client.Deleteable<SysSetting>()
            .Where(item => item.SettingName == name
                && item.ProviderName == normalizedProviderName
                && item.ProviderKey == normalizedProviderKey)
            .ExecuteCommandAsync();
    }

    /// <summary>
    /// 把可空的提供者字段归一化为非空的哨兵值
    /// </summary>
    /// <param name="value">原始值</param>
    /// <returns>归一化后的值</returns>
    private static string Normalize(string? value)
    {
        return value ?? string.Empty;
    }

    /// <summary>
    /// 按业务键查找一行设置
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="name">设置名称</param>
    /// <param name="providerName">已归一化的提供者名称</param>
    /// <param name="providerKey">已归一化的提供者键</param>
    /// <returns>命中的实体，未命中返回 null</returns>
    private static async Task<SysSetting?> FindAsync(
        ISqlSugarClient client, string name, string providerName, string providerKey)
    {
        return await client.Queryable<SysSetting>()
            .FirstAsync(item => item.SettingName == name
                && item.ProviderName == providerName
                && item.ProviderKey == providerKey);
    }

    /// <summary>
    /// 插入前的扩展点，供测试注入并发写入
    /// </summary>
    /// <param name="entity">即将插入的实体</param>
    /// <param name="cancellationToken">取消令牌</param>
    protected virtual Task OnBeforeInsertAsync(SysSetting entity, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

`SqlSugarSettingStore` 不加 `sealed`：`OnBeforeInsertAsync` 是留给测试的扩展点，测试项目里的 `RacingSqlSugarSettingStore`（Task 2 Step 1）需要能继承它并重写这个方法，在“确认不存在”与“正式插入”之间的窗口里用另一个客户端抢先插入同一行，从而确定性地复现 §4.3 描述的竞态时序，不依赖真实的多线程/多进程并发。

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（Task 1 的 7 个 + 本任务的 11 个）。

- [ ] **Step 5: 反向验证——确认新增的竞态测试真的在测 `catch` 分支**

临时把 `SetAsync` 的 `try`/`catch` 改成不做任何恢复（例如把 `catch (Exception) { ... }` 整块删掉，只留 `await client.Insertable(entity).ExecuteCommandAsync();`），重新跑：

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
```

预期：**只有**「插入前被竞争对手抢先创建同一设置时回退为更新」这一条变红（唯一索引冲突异常直接抛给了测试），其余用例不受影响——这确认了这条测试确实在验证 `catch` 分支的行为，不是凑巧通过。确认后**撤销**这个临时改动，把 `SetAsync` 恢复成 Step 3 的实现，再跑一次确认全部转回绿色。

- [ ] **Step 6: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：0 Warning(s) 0 Error(s)。

- [ ] **Step 7: 提交**

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

- [ ] **Step 5: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Settings.SqlSugar.Tests/XiHan.Framework.Settings.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS（累计 19 个）；构建 0 Warning(s) 0 Error(s)。

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
- 根 README：`README.md`、`README_cn.md` 各出现 3 处模块计数（badge、标语、文档站介绍行），**没有逐包表格**——不要去找一个不存在的表格
- `framework/README.md`/`framework/README_cn.md` 模块表格：`Settings` 行（各自第 103 行附近）
- **模块计数一共分布在 8 个文件、20 处，不只是 4 个 README**：`docs/index.md`（2 处）、`docs/introduction.md`（1 处）、`docs/why.md`（4 处）也各有模块计数字符串，Step 6 有完整的 `grep` 定位与核对流程，不要只改 README 就以为改全了
- **模块计数不是固定的 `68`**：本系列后续还有 `Tasks`/`Authentication`/`Authorization`/`Workflow`/`Security`/`Traffic`/`Upgrade` 七个包陆续落地，每个包合并时这个数字都会再 +1。动手改之前先用 `grep` 读一遍当前实际写的数字，不要假设是某个具体值
- **不要碰** `docs/packages/settings.md`：它记录的是主包既有缺口（`DefinitionProviders`/`ValueProviders` 未接线、`"T"` 只写不读等），与本包无关，改它等于顺手审一份无关的文档 PR
- **不要碰**架构 ASCII 图（`framework/README.md`/`framework/README_cn.md` 的 `Architecture Overview` 代码块）：`EventBus.SqlSugar`、`Auditing.SqlSugar` 落地时都没有把 `.SqlSugar` 子包加进这张图，保持一致

**本任务禁止事项：** 不要顺手重写与本 PR 无关的文档。不要在文档里声称仓库有 `.codegraph/` 目录。不要给 `docs/packages/settings-sqlsugar.md` 编造 `docs/packages/settings.md` 里没有核实过的行为——凡是引用主包行为的地方用链接指回去，不要复述。

- [ ] **Step 1: 更新包 README 的「扩展点」小节**

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
| `Provider_Name` | `string(32)`，非空 | 提供者名称，如 `"G"`（全局）、`"U"`（用户）；未指定时存归一化占位符（空字符串） |
| `Provider_Key` | `string(64)`，非空 | 提供者键，如用户 ID；全局设置时存归一化占位符（空字符串） |
| `Setting_Value` | 大文本，可空 | 设置值，写空即等价于从未写入 |

**`(Setting_Name, Provider_Name, Provider_Key)` 有复合唯一索引 `uk_sys_setting_key`**：同一组合在数据库层面永远只有一行，并发首次创建同一设置不会产生重复数据，见「注意事项」。

## 工作原理

### 读写

四个方法都先经 `ISqlSugarClientResolver.GetClientForEntity<SysSetting>()` 取客户端（`SysSetting` 未声明 `[DataSource]`，等价于取当前库），把入参 `providerName`/`providerKey` 归一化（`null` → 空字符串）后按 `(Setting_Name, Provider_Name, Provider_Key)` 精确匹配一行。

`SetAsync` 是「先查后写」：查到已有行就整行更新；查不到就插入新行。若插入因唯一索引冲突失败（两个调用者同时首次创建同一设置），会重新按业务键查询——查到就转为更新，最终只留一行；查不到（例如隔离级别看不见另一事务已提交的行）就把原始的唯一约束冲突异常重新抛给调用方。

**在事务型工作单元内，`GetClientForEntity` 会把本包的读写钉在当前事务上**（`ISqlSugarClientResolver.GetClientForEntity` 内部无条件登记进当前工作单元，不是本包可以关闭的行为）。这意味着：SQLite/MySQL 下，上一段的“重新查询转为更新”通常按预期工作；**PostgreSQL 下，一旦某条语句在事务内失败（含唯一约束冲突），整个事务立即进入 `aborted` 状态，同一事务里的后续命令（包括这次重新查询）也会失败**——竞态因此在 PostgreSQL 上会以异常形式暴露，并连带拖垮调用方当次业务事务里的其他写入，不是“`SetAsync` 单独失败”这么轻。

## 配置

无需任何 `XiHan:` 配置节——四个契约方法不需要可配置项。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanSettingsSqlSugarModule` | 模块类，声明依赖即启用 |
| `SqlSugarSettingStore` | `ISettingStore` 的 SqlSugar 实现，注册为 `Scoped` |
| `SysSetting` | 设置值实体 |

## 注意事项与最佳实践

- **并发首次创建同一设置由唯一索引兜底，但不保证不抛异常**：数据库唯一约束保证最终只有一行；SQLite/MySQL 下竞态通常被静默吸收转为更新，**PostgreSQL 下且处于事务型工作单元内时，竞态会以异常形式暴露、并拖垮调用方当次事务的其他写入**——本包的读写会被自动登记进当前事务（`ISqlSugarClientResolver.GetClientForEntity` 的固有行为，不是本包可以关闭的开关）。并发更新同一个已存在的设置仍是普通的先查后改，后写覆盖前写，不做乐观锁
- **空字符串与 `null` 的 `providerKey` 不可区分**：两者归一化后是同一个值，会读写同一行。当前主包的调用点都不会传空字符串，这是已接受的边界
- **Oracle 把空字符串当作 `NULL`**：本包用空字符串做归一化哨兵值，这个设计在 Oracle 上不成立——`Provider_Key = ''` 会被 Oracle 存成 `NULL`，全局设置的唯一索引语义随之退化回“NULL 各不相同”的老问题。当前仓库未把 Oracle 列为支持方言，记录在案，接入 Oracle 前需要换一个非空字符串哨兵
- **不做乐观锁**：`Row_Version` 列存在但未激活校验（未调用 SqlSugar 的 `IsEnableUpdateVersionValidation()`）
- **`"T"`（租户）提供者当前只写不读**：这是 [Settings](./settings) 主包的既有行为，`SettingManager.ResolveProvider` 会把 `Tenant` 作用域写成 `("T", tenantId)`，但读取路径尚未接入对应的值提供者；本包的存储层对任意 `providerName` 一视同仁，缺口不在本包
- **加密与校验在上层完成**：`SettingManager` 负责加密（`XiHanAesOptions`）与自定义校验（`SettingDefinition.Validator`），本包收到的 `value` 是最终存储值，原样落库
- **升级到带唯一索引的版本时，历史脏数据会让建表在启动时失败**：`CodeFirst.InitTables(...)` 在检测到同名索引已存在时会跳过、不会重建；但对一张已经存在、且 `(Setting_Name, Provider_Name, Provider_Key)` 有重复行的旧表首次创建这个索引时，数据库会因为违反唯一约束而拒绝建索引，应用因此在启动阶段报错。升级前需要先清理重复数据

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

- [ ] **Step 5: 新增 `framework/README.md` 与 `framework/README_cn.md` 的模块清单行**

在 `framework/README.md` 的模块表格里、`Settings` 那一行之后插入：

```markdown
| `Settings.SqlSugar` | Settings persistence: SqlSugar-backed `ISettingStore` implementation |
```

在 `framework/README_cn.md` 对应位置插入：

```markdown
| `Settings.SqlSugar` | 设置管理持久化：`ISettingStore` 的 SqlSugar 落库实现 |
```

**不要**改动 `Architecture Overview` 下面的 ASCII 图代码块——`EventBus.SqlSugar`、`Auditing.SqlSugar` 都没有出现在那张图里，保持一致。模块计数字符串在下一步统一处理，这一步只管清单行。

- [ ] **Step 6: 全局替换模块计数（8 个文件、20 处，`grep` 是唯一事实来源）**

模块计数不是只在 4 个 README 里出现，`docs/` 下还有 4 处。**不要凭第 4、5 步看到的行号猜全部位置**，用 `grep` 一次性定位：

```bash
grep -o 'Modules-[0-9]\+-' README.md
```

读到的数字记为 `N`（写这份计划时是 68，实现时以实际读到的为准）。用 `grep` 定位当前所有出现位置：

```bash
grep -rn -w "$N" README.md README_cn.md framework/README.md framework/README_cn.md docs --include='*.md' | grep -v node_modules
```

预期模块计数命中 **8 个文件、20 处**（若有巧合数字命中，逐条排除），大致分布（仅供核对，行号可能因本 PR 前面几步的编辑而略有偏移，一切以这条 `grep` 的实际输出为准）：

| 文件 | 处数 | 大致位置 |
| --- | --- | --- |
| `README.md` | 3 | 约 8、20、55 行（`<p>` 标语、badge、文档站介绍行） |
| `README_cn.md` | 3 | 约 8、20、55 行（同上，中文版） |
| `framework/README.md` | 3 | 约 55、186、198 行（`198` 数的是测试项目数，本计划 Task 1 新增了 `XiHan.Framework.Settings.SqlSugar.Tests`，同样要 +1） |
| `framework/README_cn.md` | 3 | 约 55、186、198 行（同上） |
| `docs/packages/index.md` | 1 | 约第 3 行（`由 **N 个 NuGet 包**组成`） |
| `docs/index.md` | 2 | 约 9、41 行 |
| `docs/introduction.md` | 1 | 约第 57 行 |
| `docs/why.md` | 4 | 约 86、123、142、160 行 |

逐处把 `N` 改成 `N+1`（含徽章 URL 里的数字、`<p>` 标语、正文叙述、表格单元格），**不要跳过表面上看起来是「同一句话」的重复行**——`docs/why.md` 里有 4 处独立的句子都提到这个数字，各自都要改。

改完后重新跑同一条命令确认零命中：

```bash
grep -rn -w "$N" README.md README_cn.md framework/README.md framework/README_cn.md docs --include='*.md' | grep -v node_modules
```

预期：不再有属于模块计数的命中。数字可能与无关文字巧合（例如 `docs/guide/distributed-ids.md` 的「约 69 年」），逐条看输出，巧合命中保持原样、不要改。再确认新数字的模块计数命中为 20 处：

```bash
grep -rn -w "$((N + 1))" README.md README_cn.md framework/README.md framework/README_cn.md docs --include='*.md' | grep -v node_modules | wc -l
```

预期：扣除巧合命中后为 `20`（N+1 为 69 时会多出上面那条「约 69 年」，总数显示 21）。

**这一步对后续三个小包同样适用**：每个包落地时都用同一套「先 `grep` 读数、改完再 `grep` 复查旧数字不再有模块计数命中、再 `grep` 确认新数字的模块计数命中为 20 处」的流程，不要凭记忆枚举文件清单——文件集合本身可能随文档站演进而变化，`grep` 的结果才是事实。

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
- 四个契约方法都有 SQLite 落库测试覆盖，包括归一化（§5.1）、唯一索引结构性验证、`OnBeforeInsertAsync` 扩展点确定性复现的并发首次创建竞态、连续两次写入不抛 `VersionExceptions`（§5.2）四类用例
- 包 README、`docs/packages/settings-sqlsugar.md`、VitePress 侧边栏、`docs/packages/index.md` 目录行、`framework/README.md`/`framework/README_cn.md` 模块清单与计数、根 `README.md`/`README_cn.md` 计数均已更新
- 新增代码的注释只说明代码做什么

## 已知边界（写入 PR 描述，不写进代码注释）

- **本包的读写会自动登记进当前事务型工作单元**：`ISqlSugarClientResolver.GetClientForEntity` 的固有行为，不是本包可以关闭的开关，见「待确认的决策」的 `requiresNew` 选项
- **并发首次创建同一设置：SQLite/MySQL 下由唯一索引兜底且不抛异常，PostgreSQL 事务内会抛异常并拖垮调用方事务**：PostgreSQL 上一旦事务内某条语句失败（含唯一约束冲突），整个事务立即中止，`catch` 块的重新查询也会失败，原始异常连同当次事务的其他写入一起报错。若唯一索引本身因跳过建表初始化或手工建表而缺失，唯一性保证也不成立
- **并发更新同一个已存在的设置无原子性**：普通的先查后改，后写覆盖前写，不做乐观锁
- **空字符串与 `null` 的 `providerKey` 不可区分**：两者归一化后读写同一行，当前主包调用点不会触发
- **Oracle 把空字符串当作 `NULL`**：本包用空字符串做归一化哨兵值，这个设计在 Oracle 上不成立，接入 Oracle 前需要换一个非空哨兵
- **建了同名索引就不会再检查其定义是否一致，对有历史重复数据的旧表升级会在启动时建索引失败**：两者都属于 `CodeFirst.InitTables(...)` 的既有行为，升级前需要先核实索引定义、清理重复数据
- **不做乐观锁**：`Row_Version` 列存在但不激活校验
- **建表默认关闭**：`EnableTableInitialization` 默认 `false`，配置层面的逃生口是把它打开
- **"T"（租户）只写不读**：主包既有缺口，不在本包范围
- **不做批量写入**：`SetAsync` 每次一条 SQL 往返

## 下一份计划

拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md`）建议的下一个包是 `Tasks.SqlSugar`（背景作业领取可复用 `EventBus.SqlSugar` P4/P6 已验证过的三步抢占协议），随后是 `Authentication.SqlSugar`、`Authorization.SqlSugar`；`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 三个小包应直接照抄本计划建立的形状（归一化非空列 + 复合唯一索引 + 先查后写 + 唯一约束冲突重新查询兜底，而不是容忍重复行）。
