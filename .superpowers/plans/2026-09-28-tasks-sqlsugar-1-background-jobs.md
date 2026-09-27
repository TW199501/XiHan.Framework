# Tasks.SqlSugar ①：包骨架与后台作业存储 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `XiHan.Framework.Tasks.SqlSugar` 包，以 SqlSugar 落库实现替换 `IBackgroundJobStore`，多实例领取互斥且被真实 MySQL 并发测试证明。

**Architecture:** 单例存储经 `TasksHostClientAccessor` 在每次操作里新建作用域、切到宿主上下文、取默认布局主库的客户端。领取沿用发件箱已验证的三步抢占协议（选候选 → 条件 `UPDATE` 盖令牌 → 按令牌取回），外加 `Claim_Time` 租约实现超时释放。以 `Replace` 顶替主包的 `TryAddSingleton` 默认存储。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221（经 `XiHan.Framework.Data` 传递引入）、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-tasks-sqlsugar-1-background-jobs-design.md`

> 该 spec 自成一体，实现本份所需的全部约束都在其中。动手前**先读完它的第 2 节与第 5 节**。

**Linear 议题:** https://linear.app/elf-express/issue/EDDIE-8

**前置:** 无代码前置。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/tasks-sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-tasks`）。worktree 若是在这两份文档提交之前开的就看不到它们，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：从 `dev` 开 worktree，分支 `feat/tasks-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。

```bash
git worktree add ../XiHan.Framework-tasks -b feat/tasks-sqlsugar dev
```

以下所有路径与命令都相对于 `E:/source/XiHan/XiHan.Framework-tasks`。

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

**SqlSugar 签名只信源码**：权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`，那是 .NET Framework 变体）；更新提供者的目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。文档：`E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar —— Interface/ISqlSugarClient.cs
ISugarQueryable<T> Queryable<T>()
IInsertable<T> Insertable<T>(T insertObj) where T : class, new()
IUpdateable<T> Updateable<T>() where T : class, new()
IUpdateable<T> Updateable<T>(T UpdateObj) where T : class, new()
IDeleteable<T> Deleteable<T>() where T : class, new()

// Interface/IQueryable.cs
ISugarQueryable<T> Where(Expression<Func<T, bool>> expression)
ISugarQueryable<T> OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)   // 多次调用按顺序追加（QueryableProvider.cs:1355-1370）
ISugarQueryable<T> Take(int num)
ISugarQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> expression)
Task<List<T>> ToListAsync()
Task<T> FirstAsync()                     // 无记录返回 default（QueryableExecuteSqlAsync.cs:82-104）
Task<int> CountAsync()

// Interface/IUpdateable.cs
IUpdateable<T> SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T> Where(Expression<Func<T, bool>> expression)
Task<int> ExecuteCommandAsync()

// Interface/Insertable.cs、Interface/IDeleteable.cs
Task<int> IInsertable<T>.ExecuteCommandAsync()
IDeleteable<T> IDeleteable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IDeleteable<T>.ExecuteCommandAsync()

// 建表与索引
void ICodeFirst.InitTables(params Type[] entityTypes)
List<DbTableInfo> IDbMaintenance.GetTableInfoList(bool isCache = true)
bool IDbMaintenance.IsAnyIndex(string indexName)
List<string> IDbMaintenance.GetIndexList(string tableName)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3, bool isUnique = false)
// 字段名是属性名：CodeFirstProvider.cs:383 按 PropertyName 找列

// AOP —— Abstract/AopProvider/AopProvider.cs:23
Action<object, DataFilterModel> AopProvider.DataExecuting { set; }

// 框架
ISqlSugarClient ISqlSugarClientResolver.GetCurrentClient()          // 按当前租户解析布局主库，并登记进环境工作单元
IDisposable ICurrentTenant.Change(long? id, string? name = null)
CurrentTenant(ICurrentTenantAccessor currentTenantAccessor)          // XiHan.Framework.MultiTenancy
AsyncLocalCurrentTenantAccessor.Instance                             // XiHan.Framework.MultiTenancy
DateTime IClock.Now                                                  // XiHan.Framework.Timing；默认配置下是本地时间
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**。权衡论证、踩坑叙事、设计理由、反事实推理（「否则会……」）一律进提交信息，不进注释
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以，lambda 不受影响）
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀
- `public` 成员必须有 `<summary>`（`GenerateDocumentationFile` 全局开启，缺了会告警）

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`、全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，**不 Import `version.props`、不设 `AssemblyName`**。`Microsoft.Data.Sqlite` 与 MySQL 驱动经 `SqlSugarCore` 传递引入，**不需要额外 `PackageReference`**。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**真实数据库测试**：用 `Assert.SkipWhen(...)` + 环境变量 `XIHAN_TEST_MYSQL` 取地址，CI 自动跳过。范式：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本包无关，不要去追它。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**（`framework/src/XiHan.Framework.Data/SqlSugar/Options/XiHanSqlSugarCoreOptions.cs:213,218`）。不开启就不会自动建表，首次写入即报表不存在。README 必须写明。

**提交信息**：中文 Conventional Commits，作用域 `tasks-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

---

## 本计划特有的硬约束

**① 抢占的 `WHERE` 必须同时含候选主键集合与可领取条件。**

只按主键更新，并发领取者会互相覆盖令牌，**同一个作业被执行两次**，不报错、SQLite 测试照样全绿。Task 5 给出的抢占代码里，`Where` 的条件一个字都不要删。Task 7 用真实 MySQL 证明它，并做一次「删掉条件后测试变红」的反向验证。

**② 令牌在重试循环体内每轮新建。**

不要把令牌提到循环外、更不要存进字段——存储是单例，字段会被所有调用共享，上一批尚未释放的作业会被下一次取回步骤一起捞回来。

**③ 领取的「当前时间」只能是 `IClock.Now`。**

不要写 `DateTime.UtcNow` 或 `DateTime.Now`。夹具的时钟固定在 2030 年，误用系统时钟的实现会立刻让领取用例变红——不要为了让它变绿去改夹具的时间。

**④ 写库必须经 `TasksHostClientAccessor`。**

存储里不许出现 `new SqlSugarClient`，不许注入 `ISqlSugarClientResolver`。访问器负责「新作用域 + 宿主上下文 + 登记进环境工作单元」三件事，绕过它三件事都会静默丢失。

---

## File Structure

```
framework/src/XiHan.Framework.Tasks.SqlSugar/
  XiHan.Framework.Tasks.SqlSugar.csproj              Task 1  包定义，按序 Import 四个 props
  XiHanTasksSqlSugarModule.cs                        Task 1  模块类，只做装配
  README.md                                          Task 1 创建，Task 8 补全
  Options/XiHanTasksSqlSugarOptions.cs               Task 1  配置节 XiHan:Tasks:SqlSugar
  Extensions/DependencyInjection/
    XiHanTasksSqlSugarServiceCollectionExtensions.cs Task 1 创建，Task 6 补全注册
  Entities/SysBackgroundJob.cs                       Task 2  表 sys_background_job
  Mapping/BackgroundJobMapper.cs                     Task 2  契约 ↔ 实体
  Clients/TasksHostClientAccessor.cs                 Task 3  宿主上下文的客户端访问器
  BackgroundJobs/SqlSugarBackgroundJobStore.cs       Task 4 创建，Task 5 补全领取

framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/
  XiHan.Framework.Tasks.SqlSugar.Tests.csproj        Task 1
  XiHanTasksSqlSugarOptionsTests.cs                  Task 1
  SysBackgroundJobTests.cs                           Task 2
  BackgroundJobMapperTests.cs                        Task 2
  StubClientResolver.cs                              Task 3  记录解析时租户的桩解析器
  TasksHostClientAccessorTests.cs                    Task 3
  FakeClock.cs                                       Task 4
  TasksTestContext.cs                                Task 4  SQLite 夹具
  BackgroundJobStoreTests.cs                         Task 4
  BackgroundJobClaimTests.cs                         Task 5
  TasksSqlSugarRegistrationTests.cs                  Task 6
  BackgroundJobConcurrencyTests.cs                   Task 7  真实 MySQL

framework/XiHan.Framework.slnx                       Task 1  注册两个项目
```

---

### Task 1: 包骨架、选项与测试项目

**Files:**
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/XiHan.Framework.Tasks.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/XiHanTasksSqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Options/XiHanTasksSqlSugarOptions.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/README.md`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHanTasksSqlSugarOptionsTests.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: `XiHan.Framework.Tasks.XiHanTasksModule`、`XiHan.Framework.Data.XiHanDataModule`
- Produces:
  - 程序集 `XiHan.Framework.Tasks.SqlSugar`，根命名空间 `XiHan.Framework.Tasks.SqlSugar`
  - `public class XiHanTasksSqlSugarModule : XiHanModule`
  - `public class XiHanTasksSqlSugarOptions`：`const string SectionName = "XiHan:Tasks:SqlSugar"`，`TimeSpan BackgroundJobLeaseTimeout { get; set; }`（默认 5 分钟）
  - `public static IServiceCollection AddXiHanTasksSqlSugar(this IServiceCollection services, IConfiguration configuration)`（本任务只绑定选项）

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- 模块类范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`
- 测试 csproj 范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`
- README 七段结构：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`

**本任务禁止事项：** 不要新增任何 `PackageReference`。不要在模块类里写注册逻辑。本任务的注册扩展**只绑定选项**，不注册任何存储。不要改 `XiHan.Framework.Tasks` 的任何文件。

- [ ] **Step 1: 创建测试项目与失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Tasks.SqlSugar\XiHan.Framework.Tasks.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHanTasksSqlSugarOptionsTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Tasks.SqlSugar.Options;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 任务 SqlSugar 存储配置测试
/// </summary>
public class XiHanTasksSqlSugarOptionsTests
{
    /// <summary>
    /// 后台作业租约默认五分钟
    /// </summary>
    [Fact]
    public void 后台作业租约默认五分钟()
    {
        var options = new XiHanTasksSqlSugarOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), options.BackgroundJobLeaseTimeout);
    }

    /// <summary>
    /// 配置节名称带框架前缀
    /// </summary>
    [Fact]
    public void 配置节名称带框架前缀()
    {
        Assert.Equal("XiHan:Tasks:SqlSugar", XiHanTasksSqlSugarOptions.SectionName);
    }

    /// <summary>
    /// 从配置节绑定租约时长
    /// </summary>
    [Fact]
    public void 从配置节绑定租约时长()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Tasks:SqlSugar:BackgroundJobLeaseTimeout"] = "00:10:00"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddXiHanTasksSqlSugar(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<XiHanTasksSqlSugarOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(10), options.BackgroundJobLeaseTimeout);
    }
}
```

编辑 `framework/XiHan.Framework.slnx`：

在 `/1.src/6.Infrastructure/` 文件夹内、`<Project Path="src/XiHan.Framework.Tasks/XiHan.Framework.Tasks.csproj" />` 那一行**之后**插入：

```xml
    <Project Path="src/XiHan.Framework.Tasks.SqlSugar/XiHan.Framework.Tasks.SqlSugar.csproj" />
```

在 `/2.tests/1.UnitTests/` 文件夹内、`<Project Path="test/XiHan.Framework.Tasks.Tests/XiHan.Framework.Tasks.Tests.csproj" />` 那一行**之后**插入：

```xml
    <Project Path="test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：还原或编译失败——被引用的 `XiHan.Framework.Tasks.SqlSugar.csproj` 尚不存在（`MSB9008` / `NU1105` 一类的「找不到项目」错误）。

- [ ] **Step 3: 创建包**

`framework/src/XiHan.Framework.Tasks.SqlSugar/XiHan.Framework.Tasks.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Tasks.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Tasks.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Tasks.SqlSugar</PackageId>
        <Description>曦寒框架后台作业与定时任务 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Tasks\XiHan.Framework.Tasks.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

`framework/src/XiHan.Framework.Tasks.SqlSugar/Options/XiHanTasksSqlSugarOptions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Tasks.SqlSugar.Options;

/// <summary>
/// 任务 SqlSugar 存储配置
/// </summary>
public class XiHanTasksSqlSugarOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Tasks:SqlSugar";

    /// <summary>
    /// 后台作业租约时长，领取后超过该时长仍未删除或更新的作业可被重新领取
    /// </summary>
    public TimeSpan BackgroundJobLeaseTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
```

`framework/src/XiHan.Framework.Tasks.SqlSugar/Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Tasks.SqlSugar.Options;

namespace XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 任务 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanTasksSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换任务模块的进程内存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanTasksSqlSugar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanTasksSqlSugarOptions>(
            configuration.GetSection(XiHanTasksSqlSugarOptions.SectionName));

        return services;
    }
}
```

`framework/src/XiHan.Framework.Tasks.SqlSugar/XiHanTasksSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Tasks.SqlSugar;

/// <summary>
/// 曦寒框架任务 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanTasksSqlSugarModule))]</c> 即启用。
/// 配置节：<c>XiHan:Tasks:SqlSugar</c>。
/// </remarks>
[DependsOn(
    typeof(XiHanTasksModule),
    typeof(XiHanDataModule)
)]
public class XiHanTasksSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddXiHanTasksSqlSugar(services.GetConfiguration());
    }
}
```

`XiHanTasksModule` 位于 `XiHan.Framework.Tasks` 命名空间，是本命名空间的外层，**不需要**再写 `using XiHan.Framework.Tasks;`。

`framework/src/XiHan.Framework.Tasks.SqlSugar/README.md`（初版，Task 8 补全）：

````markdown
# XiHan.Framework.Tasks.SqlSugar

## 概述

`XiHan.Framework.Tasks` 的 SqlSugar 持久化提供程序。

## 核心能力

- 包骨架与配置节 `XiHan:Tasks:SqlSugar`

## 依赖关系

依赖 `XiHan.Framework.Tasks`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

配置节 `XiHan:Tasks:SqlSugar`。

## 使用方式

在应用启动模块上声明依赖 `XiHanTasksSqlSugarModule`。

## 扩展点

无。

## 目录结构

```
Options/                         存储配置
Extensions/DependencyInjection/  服务注册扩展
```
````

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：3 个用例全部 PASS。

若报 `CS0246` 找不到 `ServiceCollection` 或 `AddInMemoryCollection`：它们经 `XiHan.Framework.Core` 传递引入，同样的写法在 `framework/test/XiHan.Framework.Auditing.Tests/Options/XiHanAuditingLogQueueOptionsTests.cs` 已编译通过。先核对 using 是否齐全，**不要**新增 `PackageReference`。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(tasks-sqlsugar): 新增包骨架与配置节"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。若出现 `XHFH001`，说明版权声明缺失或格式不符。

---

### Task 2: 后台作业实体与映射

**Files:**
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysBackgroundJob.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Mapping/BackgroundJobMapper.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/SysBackgroundJobTests.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobMapperTests.cs`

**Interfaces:**
- Consumes: `XiHan.Framework.Data.SqlSugar.Entities.SugarEntity<TKey>`；`XiHan.Framework.Tasks.BackgroundJobs.Models.BackgroundJobInfo`、`BackgroundJobPriority`
- Produces:
  - `public class SysBackgroundJob : SugarEntity<Guid>`，两个构造函数 `()` 与 `(Guid basicId)`；属性 `string ApplicationName`、`long? TenantId`、`string JobName`、`string JobArgs`、`short TryCount`、`DateTime CreationTime`、`DateTime NextTryTime`、`DateTime? LastTryTime`、`bool IsAbandoned`、`int Priority`、`string? ClaimToken`、`DateTime? ClaimTime`
  - `public static class BackgroundJobMapper`：`SysBackgroundJob ToEntity(BackgroundJobInfo info)`、`BackgroundJobInfo ToJobInfo(SysBackgroundJob entity)`、`string ToApplicationKey(string? applicationName)`

**参考来源（动手前先读）：**
- 实体写法：`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`
- 映射写法：`framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventOutboxMapper.cs`
- 契约模型：`framework/src/XiHan.Framework.Tasks/BackgroundJobs/Models/BackgroundJobInfo.cs`
- 设计：spec §4.3、§4.4

**本任务禁止事项：** 不加 `[SplitTable]`、不加 `[TableInitialization(...)]`。属性名**不许**用 `CreatedTime`、`ModifiedTime`、`IsDeleted`（spec §5 ⑦）——`CreationTime` 保持原名。`Priority` 存 `int`，不要改成枚举类型。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/SysBackgroundJobTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 后台作业实体建表测试
/// </summary>
public class SysBackgroundJobTests
{
    /// <summary>
    /// 后台作业表与索引能建出来
    /// </summary>
    [Fact]
    public void 后台作业表与索引能建出来()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_tasks_{Guid.NewGuid():N}.db");

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.InitTables(typeof(SysBackgroundJob));

            var tableNames = db.DbMaintenance.GetTableInfoList(false)
                .Select(table => table.Name)
                .ToList();

            Assert.Contains(tableNames, name => string.Equals(name, "sys_background_job", StringComparison.OrdinalIgnoreCase));
            Assert.True(db.DbMaintenance.IsAnyIndex("idx_sys_background_job_waiting"));
            Assert.True(db.DbMaintenance.IsAnyIndex("idx_sys_background_job_claim_token"));
        }
        finally
        {
            DeleteQuietly(databaseFile);
        }
    }

    private static SqlSugarClient CreateClient(string databaseFile)
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
            ConnectionString = $"DataSource={databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
    }

    private static void DeleteQuietly(string databaseFile)
    {
        if (File.Exists(databaseFile))
        {
            File.Delete(databaseFile);
        }
    }
}
```

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Mapping;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 后台作业映射测试
/// </summary>
public class BackgroundJobMapperTests
{
    /// <summary>
    /// 契约与实体往返后字段一致
    /// </summary>
    [Fact]
    public void 契约与实体往返后字段一致()
    {
        var info = new BackgroundJobInfo
        {
            Id = Guid.NewGuid(),
            ApplicationName = "Shop",
            TenantId = 42,
            JobName = "Order.Close",
            JobArgs = "{\"orderId\":1}",
            TryCount = 3,
            CreationTime = new DateTime(2030, 1, 1, 8, 0, 0),
            NextTryTime = new DateTime(2030, 1, 1, 9, 0, 0),
            LastTryTime = new DateTime(2030, 1, 1, 8, 30, 0),
            IsAbandoned = true,
            Priority = BackgroundJobPriority.High
        };

        var restored = BackgroundJobMapper.ToJobInfo(BackgroundJobMapper.ToEntity(info));

        Assert.Equal(info.Id, restored.Id);
        Assert.Equal("Shop", restored.ApplicationName);
        Assert.Equal(42L, restored.TenantId);
        Assert.Equal("Order.Close", restored.JobName);
        Assert.Equal("{\"orderId\":1}", restored.JobArgs);
        Assert.Equal((short)3, restored.TryCount);
        Assert.Equal(info.CreationTime, restored.CreationTime);
        Assert.Equal(info.NextTryTime, restored.NextTryTime);
        Assert.Equal(info.LastTryTime, restored.LastTryTime);
        Assert.True(restored.IsAbandoned);
        Assert.Equal(BackgroundJobPriority.High, restored.Priority);
    }

    /// <summary>
    /// 应用名为空时存为空字符串并还原为空
    /// </summary>
    [Fact]
    public void 应用名为空时存为空字符串并还原为空()
    {
        var info = new BackgroundJobInfo
        {
            Id = Guid.NewGuid(),
            ApplicationName = null,
            JobName = "Order.Close",
            JobArgs = "{}"
        };

        var entity = BackgroundJobMapper.ToEntity(info);
        var restored = BackgroundJobMapper.ToJobInfo(entity);

        Assert.Equal(string.Empty, entity.ApplicationName);
        Assert.Null(restored.ApplicationName);
    }

    /// <summary>
    /// 转换为实体时清空租约
    /// </summary>
    [Fact]
    public void 转换为实体时清空租约()
    {
        var info = new BackgroundJobInfo
        {
            Id = Guid.NewGuid(),
            JobName = "Order.Close",
            JobArgs = "{}",
            Priority = BackgroundJobPriority.High
        };

        var entity = BackgroundJobMapper.ToEntity(info);

        Assert.Null(entity.ClaimToken);
        Assert.Null(entity.ClaimTime);
        Assert.Equal(25, entity.Priority);
        Assert.Equal(info.Id, entity.BasicId);
    }

    /// <summary>
    /// 应用名键把空值转换为空字符串
    /// </summary>
    [Fact]
    public void 应用名键把空值转换为空字符串()
    {
        Assert.Equal(string.Empty, BackgroundJobMapper.ToApplicationKey(null));
        Assert.Equal("Shop", BackgroundJobMapper.ToApplicationKey("Shop"));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0234` / `CS0246`——`XiHan.Framework.Tasks.SqlSugar.Entities`、`XiHan.Framework.Tasks.SqlSugar.Mapping` 尚不存在。

- [ ] **Step 3: 创建实体**

`framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysBackgroundJob.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Entities;

/// <summary>
/// 后台作业实体
/// </summary>
[SugarTable("sys_background_job")]
[SugarIndex("idx_sys_background_job_waiting",
    nameof(SysBackgroundJob.ApplicationName), OrderByType.Asc,
    nameof(SysBackgroundJob.IsAbandoned), OrderByType.Asc,
    nameof(SysBackgroundJob.NextTryTime), OrderByType.Asc)]
[SugarIndex("idx_sys_background_job_claim_token",
    nameof(SysBackgroundJob.ClaimToken), OrderByType.Asc)]
public class SysBackgroundJob : SugarEntity<Guid>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysBackgroundJob() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">作业唯一标识</param>
    public SysBackgroundJob(Guid basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 入队应用名称，空字符串表示未指定
    /// </summary>
    [SugarColumn(ColumnName = "Application_Name", Length = 128, IsNullable = false, ColumnDescription = "入队应用名称，空字符串表示未指定")]
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>
    /// 入队时的租户标识
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "入队时的租户标识")]
    public long? TenantId { get; set; }

    /// <summary>
    /// 作业名称
    /// </summary>
    [SugarColumn(ColumnName = "Job_Name", Length = 256, IsNullable = false, ColumnDescription = "作业名称")]
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// 序列化后的作业参数
    /// </summary>
    [SugarColumn(ColumnName = "Job_Args", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "序列化后的作业参数")]
    public string JobArgs { get; set; } = string.Empty;

    /// <summary>
    /// 已尝试次数
    /// </summary>
    [SugarColumn(ColumnName = "Try_Count", IsNullable = false, ColumnDescription = "已尝试次数")]
    public short TryCount { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Creation_Time", IsNullable = false, ColumnDescription = "创建时间")]
    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 下次可执行时间
    /// </summary>
    [SugarColumn(ColumnName = "Next_Try_Time", IsNullable = false, ColumnDescription = "下次可执行时间")]
    public DateTime NextTryTime { get; set; }

    /// <summary>
    /// 上次尝试时间
    /// </summary>
    [SugarColumn(ColumnName = "Last_Try_Time", IsNullable = true, ColumnDescription = "上次尝试时间")]
    public DateTime? LastTryTime { get; set; }

    /// <summary>
    /// 是否已放弃
    /// </summary>
    [SugarColumn(ColumnName = "Is_Abandoned", IsNullable = false, ColumnDescription = "是否已放弃")]
    public bool IsAbandoned { get; set; }

    /// <summary>
    /// 优先级，值越大越优先
    /// </summary>
    [SugarColumn(ColumnName = "Priority", IsNullable = false, ColumnDescription = "优先级，值越大越优先")]
    public int Priority { get; set; }

    /// <summary>
    /// 领取令牌
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Token", Length = 64, IsNullable = true, ColumnDescription = "领取令牌")]
    public string? ClaimToken { get; set; }

    /// <summary>
    /// 领取时刻
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Time", IsNullable = true, ColumnDescription = "领取时刻")]
    public DateTime? ClaimTime { get; set; }
}
```

- [ ] **Step 4: 创建映射**

`framework/src/XiHan.Framework.Tasks.SqlSugar/Mapping/BackgroundJobMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Mapping;

/// <summary>
/// 后台作业契约与实体的双向映射
/// </summary>
public static class BackgroundJobMapper
{
    /// <summary>
    /// 把后台作业信息转换为实体，领取令牌与领取时刻置空
    /// </summary>
    /// <param name="info">后台作业信息</param>
    /// <returns>后台作业实体</returns>
    public static SysBackgroundJob ToEntity(BackgroundJobInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new SysBackgroundJob(info.Id)
        {
            ApplicationName = ToApplicationKey(info.ApplicationName),
            TenantId = info.TenantId,
            JobName = info.JobName,
            JobArgs = info.JobArgs,
            TryCount = info.TryCount,
            CreationTime = info.CreationTime,
            NextTryTime = info.NextTryTime,
            LastTryTime = info.LastTryTime,
            IsAbandoned = info.IsAbandoned,
            Priority = (int)info.Priority,
            ClaimToken = null,
            ClaimTime = null
        };
    }

    /// <summary>
    /// 把实体转换为后台作业信息，空字符串的应用名还原为空
    /// </summary>
    /// <param name="entity">后台作业实体</param>
    /// <returns>后台作业信息</returns>
    public static BackgroundJobInfo ToJobInfo(SysBackgroundJob entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new BackgroundJobInfo
        {
            Id = entity.BasicId,
            ApplicationName = entity.ApplicationName.Length == 0 ? null : entity.ApplicationName,
            TenantId = entity.TenantId,
            JobName = entity.JobName,
            JobArgs = entity.JobArgs,
            TryCount = entity.TryCount,
            CreationTime = entity.CreationTime,
            NextTryTime = entity.NextTryTime,
            LastTryTime = entity.LastTryTime,
            IsAbandoned = entity.IsAbandoned,
            Priority = (BackgroundJobPriority)entity.Priority
        };
    }

    /// <summary>
    /// 把应用名转换为存储键，空值转换为空字符串
    /// </summary>
    /// <param name="applicationName">应用名</param>
    /// <returns>存储键</returns>
    public static string ToApplicationKey(string? applicationName)
    {
        return applicationName ?? string.Empty;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `后台作业表与索引能建出来` 在 `IsAnyIndex` 处失败：先在用例里临时打印 `db.DbMaintenance.GetIndexList("sys_background_job")` 看实际建出的索引名，按实际情况修正实体的 `SugarIndex`，**不要删掉索引断言**。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 新增后台作业实体与映射"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 宿主上下文的客户端访问器

**Files:**
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Clients/TasksHostClientAccessor.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/StubClientResolver.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksHostClientAccessorTests.cs`

**Interfaces:**
- Consumes: `XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver`；`XiHan.Framework.MultiTenancy.Abstractions.ICurrentTenant`；`Microsoft.Extensions.DependencyInjection.IServiceScopeFactory`
- Produces:
  - `public sealed class TasksHostClientAccessor`，构造函数 `(IServiceScopeFactory scopeFactory, ICurrentTenant currentTenant)`；方法 `Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation)`、`Task ExecuteAsync(Func<ISqlSugarClient, Task> operation)`
  - 测试桩 `internal sealed class StubClientResolver : ISqlSugarClientResolver`，构造函数 `(ISqlSugarClient client, ICurrentTenant currentTenant)`，属性 `List<long?> ObservedTenantIds`（每次 `GetCurrentClient` / `GetClientForEntity` 记录一次当时的租户）

**参考来源（动手前先读）：**
- 解析器接口：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`
- `GetCurrentClient` 如何按租户解析：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs:79-117`
- 租户切换：`framework/src/XiHan.Framework.MultiTenancy/CurrentTenant.cs`
- 设计：spec §4.5、§5 ⑤⑥

**本任务禁止事项：** 硬约束 ④。`Change(null)` 必须包住**整个** `await operation(client)`，不能只包住解析客户端那一行（spec §5 ⑤：审计 AOP 在 SQL 执行时读租户）。不要缓存解析器或客户端到字段里。

- [ ] **Step 1: 写测试桩**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/StubClientResolver.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 测试用客户端解析器，始终返回同一个客户端并记录解析时的租户
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
{
    /// <summary>
    /// 主库的连接配置标识
    /// </summary>
    public const string MainConfigId = "Default";

    private readonly ISqlSugarClient _client;
    private readonly ICurrentTenant _currentTenant;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="currentTenant">当前租户</param>
    public StubClientResolver(ISqlSugarClient client, ICurrentTenant currentTenant)
    {
        _client = client;
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// 每次解析当前客户端时的租户标识
    /// </summary>
    public List<long?> ObservedTenantIds { get; } = [];

    /// <summary>
    /// 获取当前客户端并记录当时的租户
    /// </summary>
    /// <returns>客户端</returns>
    public ISqlSugarClient GetCurrentClient()
    {
        ObservedTenantIds.Add(_currentTenant.Id);
        return _client;
    }

    /// <summary>
    /// 获取实体对应的客户端
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>客户端</returns>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        return GetCurrentClient();
    }

    /// <summary>
    /// 按连接配置标识获取客户端
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    /// <returns>客户端</returns>
    public ISqlSugarClient GetClient(string configId)
    {
        return _client;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识集合</returns>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return [MainConfigId];
    }

    /// <summary>
    /// 获取当前布局的全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识集合</returns>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return [MainConfigId];
    }

    /// <summary>
    /// 获取当前工作单元已登记的连接配置标识
    /// </summary>
    /// <returns>空集合</returns>
    public IReadOnlyList<string> GetEnlistedConfigIds()
    {
        return [];
    }

    /// <summary>
    /// 获取所有库的客户端
    /// </summary>
    /// <returns>客户端集合</returns>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [_client];
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

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksHostClientAccessorTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Tasks.SqlSugar.Clients;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 宿主上下文客户端访问器测试
/// </summary>
public class TasksHostClientAccessorTests
{
    /// <summary>
    /// 在租户上下文中调用时操作期间处于宿主上下文
    /// </summary>
    [Fact]
    public async Task 在租户上下文中调用时操作期间处于宿主上下文()
    {
        using var client = CreateClient();
        var currentTenant = CreateCurrentTenant();
        var resolver = new StubClientResolver(client, currentTenant);
        using var provider = BuildProvider(_ => resolver);
        var accessor = new TasksHostClientAccessor(provider.GetRequiredService<IServiceScopeFactory>(), currentTenant);
        long? tenantDuringOperation = -1;

        using (currentTenant.Change(42))
        {
            await accessor.ExecuteAsync(_ =>
            {
                tenantDuringOperation = currentTenant.Id;
                return Task.CompletedTask;
            });

            Assert.Equal(42L, currentTenant.Id);
        }

        Assert.Null(tenantDuringOperation);
        var observed = Assert.Single(resolver.ObservedTenantIds);
        Assert.Null(observed);
    }

    /// <summary>
    /// 有返回值的操作把结果交回调用方
    /// </summary>
    [Fact]
    public async Task 有返回值的操作把结果交回调用方()
    {
        using var client = CreateClient();
        var currentTenant = CreateCurrentTenant();
        var resolver = new StubClientResolver(client, currentTenant);
        using var provider = BuildProvider(_ => resolver);
        var accessor = new TasksHostClientAccessor(provider.GetRequiredService<IServiceScopeFactory>(), currentTenant);

        var result = await accessor.ExecuteAsync(_ => Task.FromResult(7));

        Assert.Equal(7, result);
    }

    /// <summary>
    /// 每次操作都从新作用域解析客户端解析器
    /// </summary>
    [Fact]
    public async Task 每次操作都从新作用域解析客户端解析器()
    {
        using var client = CreateClient();
        var currentTenant = CreateCurrentTenant();
        var resolveCount = 0;
        using var provider = BuildProvider(_ =>
        {
            resolveCount++;
            return new StubClientResolver(client, currentTenant);
        });
        var accessor = new TasksHostClientAccessor(provider.GetRequiredService<IServiceScopeFactory>(), currentTenant);

        await accessor.ExecuteAsync(_ => Task.CompletedTask);
        await accessor.ExecuteAsync(_ => Task.CompletedTask);

        Assert.Equal(2, resolveCount);
    }

    private static SqlSugarClient CreateClient()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
    }

    private static ICurrentTenant CreateCurrentTenant()
    {
        return new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
    }

    private static ServiceProvider BuildProvider(Func<IServiceProvider, ISqlSugarClientResolver> resolverFactory)
    {
        var services = new ServiceCollection();
        services.AddScoped(resolverFactory);
        return services.BuildServiceProvider();
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0234`——`XiHan.Framework.Tasks.SqlSugar.Clients` 尚不存在。

- [ ] **Step 4: 实现访问器**

`framework/src/XiHan.Framework.Tasks.SqlSugar/Clients/TasksHostClientAccessor.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Tasks.SqlSugar.Clients;

/// <summary>
/// 任务存储的数据库访问器，在宿主上下文中取得默认布局主库的客户端执行操作
/// </summary>
/// <remarks>
/// 每次操作新建一个服务作用域解析 <see cref="ISqlSugarClientResolver"/>；
/// 操作执行期间当前租户切换为宿主，操作结束后恢复。
/// 存在事务型环境工作单元时，客户端由解析器登记进该工作单元。
/// </remarks>
public sealed class TasksHostClientAccessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentTenant _currentTenant;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="scopeFactory">服务作用域工厂</param>
    /// <param name="currentTenant">当前租户</param>
    public TasksHostClientAccessor(IServiceScopeFactory scopeFactory, ICurrentTenant currentTenant)
    {
        _scopeFactory = scopeFactory;
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// 在宿主上下文中执行有返回值的数据库操作
    /// </summary>
    /// <typeparam name="TResult">返回值类型</typeparam>
    /// <param name="operation">数据库操作</param>
    /// <returns>操作的返回值</returns>
    public async Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var scope = _scopeFactory.CreateScope();

        using (_currentTenant.Change(null))
        {
            var client = scope.ServiceProvider
                .GetRequiredService<ISqlSugarClientResolver>()
                .GetCurrentClient();

            return await operation(client);
        }
    }

    /// <summary>
    /// 在宿主上下文中执行无返回值的数据库操作
    /// </summary>
    /// <param name="operation">数据库操作</param>
    /// <returns>任务</returns>
    public async Task ExecuteAsync(Func<ISqlSugarClient, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteAsync(async client =>
        {
            await operation(client);
            return true;
        });
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 新增宿主上下文的客户端访问器"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。提交信息正文写明：每次操作新建作用域是为了让单例存储不捕获作用域级解析器，而工作单元参与经 `AsyncLocal` 环境工作单元照常生效；切到宿主上下文是为了让作业只落 Worker 轮询的宿主主库，并避开审计 AOP 按属性名改写 `TenantId`。

---

### Task 4: 后台作业存储的增删改查

**Files:**
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/FakeClock.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksTestContext.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysBackgroundJob`、`BackgroundJobMapper`；Task 3 的 `TasksHostClientAccessor`、`StubClientResolver`；`XiHan.Framework.Timing.IClock`
- Produces:
  - `public class SqlSugarBackgroundJobStore : IBackgroundJobStore`，构造函数 `(TasksHostClientAccessor clientAccessor, IClock clock, IOptions<XiHanTasksSqlSugarOptions> options)`
  - 测试夹具 `internal sealed class TasksTestContext : IDisposable`，构造函数 `(TimeSpan? leaseTimeout = null)`；静态属性 `DateTime BaseTime`（2030-01-01 00:00 UTC）；实例属性 `SqlSugarClient Client`、`ICurrentTenant Tenant`、`StubClientResolver Resolver`、`TasksHostClientAccessor Accessor`、`FakeClock Clock`、`SqlSugarBackgroundJobStore BackgroundJobStore`、`List<long?> ExecutingTenantIds`
  - 测试时钟 `public sealed class FakeClock : IClock`，构造函数 `(DateTime now)`，`DateTime Now { get; set; }`

**参考来源（动手前先读）：**
- 被替换的实现：`framework/src/XiHan.Framework.Tasks/BackgroundJobs/DefaultBackgroundJobStore.cs`、`RedisBackgroundJobStore.cs`
- 可控时钟范本：`framework/test/XiHan.Framework.Tasks.Tests/BackgroundJobs/Fakes/FakeClock.cs`
- SQLite 夹具范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxTestContext.cs`
- 设计：spec §4.6、§5 ④⑤

**本任务禁止事项：** 硬约束 ④。`UpdateAsync` 必须用 `Updateable(entity)` 整行更新（租约列随 `ToEntity` 置空），不要改成 `SetColumns` 只列业务字段（spec §5 ④）。`UpdateAsync` 不许「不存在就插入」。本任务的 `GetWaitingJobsAsync` 按 Step 4 给出的过渡实现返回空集合，Task 5 整体替换。

- [ ] **Step 1: 写测试时钟与夹具**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/FakeClock.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Timing;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 可控时钟
/// </summary>
public sealed class FakeClock : IClock
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="now">当前时间</param>
    public FakeClock(DateTime now)
    {
        Now = now;
    }

    /// <summary>
    /// 当前时间，可由用例推进
    /// </summary>
    public DateTime Now { get; set; }

    /// <summary>
    /// 时间类型
    /// </summary>
    public DateTimeKind Kind => DateTimeKind.Utc;

    /// <summary>
    /// 是否支持多时区
    /// </summary>
    public bool SupportsMultipleTimezone => false;

    /// <summary>
    /// 规范化时间
    /// </summary>
    /// <param name="dateTime">时间</param>
    /// <returns>规范化时间</returns>
    public DateTime Normalize(DateTime dateTime)
    {
        return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
    }

    /// <summary>
    /// 转换为用户时间
    /// </summary>
    /// <param name="utcDateTime">UTC 时间</param>
    /// <returns>用户时间</returns>
    public DateTime ConvertToUserTime(DateTime utcDateTime)
    {
        return utcDateTime;
    }

    /// <summary>
    /// 转换为用户时间
    /// </summary>
    /// <param name="dateTimeOffset">时间偏移</param>
    /// <returns>用户时间</returns>
    public DateTimeOffset ConvertToUserTime(DateTimeOffset dateTimeOffset)
    {
        return dateTimeOffset;
    }

    /// <summary>
    /// 转换为 UTC 时间
    /// </summary>
    /// <param name="dateTime">时间</param>
    /// <returns>UTC 时间</returns>
    public DateTime ConvertToUtc(DateTime dateTime)
    {
        return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
    }
}
```

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksTestContext.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Entities;
using XiHan.Framework.Tasks.SqlSugar.Options;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 任务存储测试夹具，提供一个临时 SQLite 库与被测存储
/// </summary>
internal sealed class TasksTestContext : IDisposable
{
    private readonly string _databaseFile;
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="leaseTimeout">后台作业租约时长，默认五分钟</param>
    public TasksTestContext(TimeSpan? leaseTimeout = null)
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_tasks_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        Client.CodeFirst.InitTables(typeof(SysBackgroundJob));

        Tenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
        Client.Aop.DataExecuting = (_, _) => ExecutingTenantIds.Add(Tenant.Id);

        Resolver = new StubClientResolver(Client, Tenant);

        var services = new ServiceCollection();
        services.AddScoped<ISqlSugarClientResolver>(_ => Resolver);
        _serviceProvider = services.BuildServiceProvider();

        Accessor = new TasksHostClientAccessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Tenant);

        Clock = new FakeClock(BaseTime);

        BackgroundJobStore = new SqlSugarBackgroundJobStore(
            Accessor,
            Clock,
            Microsoft.Extensions.Options.Options.Create(new XiHanTasksSqlSugarOptions
            {
                BackgroundJobLeaseTimeout = leaseTimeout ?? TimeSpan.FromMinutes(5)
            }));
    }

    /// <summary>
    /// 用例的基准时间，远离真实的当前时间
    /// </summary>
    public static DateTime BaseTime { get; } = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 临时库客户端
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 当前租户
    /// </summary>
    public ICurrentTenant Tenant { get; }

    /// <summary>
    /// 记录解析时租户的客户端解析器
    /// </summary>
    public StubClientResolver Resolver { get; }

    /// <summary>
    /// 宿主上下文客户端访问器
    /// </summary>
    public TasksHostClientAccessor Accessor { get; }

    /// <summary>
    /// 可控时钟
    /// </summary>
    public FakeClock Clock { get; }

    /// <summary>
    /// 被测后台作业存储
    /// </summary>
    public SqlSugarBackgroundJobStore BackgroundJobStore { get; }

    /// <summary>
    /// 每次触发数据执行事件时的租户标识
    /// </summary>
    public List<long?> ExecutingTenantIds { get; } = [];

    /// <summary>
    /// 释放客户端并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        _serviceProvider.Dispose();
        Client.Dispose();

        if (File.Exists(_databaseFile))
        {
            File.Delete(_databaseFile);
        }
    }
}
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 后台作业存储增删改查测试
/// </summary>
public class BackgroundJobStoreTests
{
    /// <summary>
    /// 插入后按标识查回字段一致
    /// </summary>
    [Fact]
    public async Task 插入后按标识查回字段一致()
    {
        using var context = new TasksTestContext();
        var lastTryTime = TasksTestContext.BaseTime.AddMinutes(-5);
        var job = NewJob();
        job.ApplicationName = "Shop";
        job.TenantId = 42;
        job.TryCount = 2;
        job.LastTryTime = lastTryTime;
        job.Priority = BackgroundJobPriority.High;

        await context.BackgroundJobStore.InsertAsync(job);
        var found = await context.BackgroundJobStore.FindAsync(job.Id);

        Assert.NotNull(found);
        Assert.Equal(job.Id, found.Id);
        Assert.Equal("Shop", found.ApplicationName);
        Assert.Equal(42L, found.TenantId);
        Assert.Equal(job.JobName, found.JobName);
        Assert.Equal(job.JobArgs, found.JobArgs);
        Assert.Equal((short)2, found.TryCount);
        Assert.Equal(BackgroundJobPriority.High, found.Priority);
        Assert.False(found.IsAbandoned);
        AssertClose(job.CreationTime, found.CreationTime);
        AssertClose(job.NextTryTime, found.NextTryTime);
        Assert.True(found.LastTryTime.HasValue);
        AssertClose(lastTryTime, found.LastTryTime.GetValueOrDefault());
    }

    /// <summary>
    /// 查找不存在的作业返回空
    /// </summary>
    [Fact]
    public async Task 查找不存在的作业返回空()
    {
        using var context = new TasksTestContext();

        Assert.Null(await context.BackgroundJobStore.FindAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// 插入空作业抛出参数异常
    /// </summary>
    [Fact]
    public async Task 插入空作业抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAsync<ArgumentNullException>(() => context.BackgroundJobStore.InsertAsync(null!));
    }

    /// <summary>
    /// 更新空作业抛出参数异常
    /// </summary>
    [Fact]
    public async Task 更新空作业抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAsync<ArgumentNullException>(() => context.BackgroundJobStore.UpdateAsync(null!));
    }

    /// <summary>
    /// 删除后查不到且重复删除不抛异常
    /// </summary>
    [Fact]
    public async Task 删除后查不到且重复删除不抛异常()
    {
        using var context = new TasksTestContext();
        var job = NewJob();
        await context.BackgroundJobStore.InsertAsync(job);

        await context.BackgroundJobStore.DeleteAsync(job.Id);
        await context.BackgroundJobStore.DeleteAsync(job.Id);

        Assert.Null(await context.BackgroundJobStore.FindAsync(job.Id));
    }

    /// <summary>
    /// 更新覆盖失败回写的字段
    /// </summary>
    [Fact]
    public async Task 更新覆盖失败回写的字段()
    {
        using var context = new TasksTestContext();
        var job = NewJob();
        await context.BackgroundJobStore.InsertAsync(job);

        var nextTryTime = TasksTestContext.BaseTime.AddMinutes(1);
        job.TryCount = 1;
        job.LastTryTime = TasksTestContext.BaseTime;
        job.NextTryTime = nextTryTime;
        await context.BackgroundJobStore.UpdateAsync(job);

        var found = await context.BackgroundJobStore.FindAsync(job.Id);

        Assert.NotNull(found);
        Assert.Equal((short)1, found.TryCount);
        AssertClose(nextTryTime, found.NextTryTime);
        Assert.True(found.LastTryTime.HasValue);
    }

    /// <summary>
    /// 放弃的作业保留在表中并标记放弃
    /// </summary>
    [Fact]
    public async Task 放弃的作业保留在表中并标记放弃()
    {
        using var context = new TasksTestContext();
        var job = NewJob();
        await context.BackgroundJobStore.InsertAsync(job);

        job.IsAbandoned = true;
        await context.BackgroundJobStore.UpdateAsync(job);

        var found = await context.BackgroundJobStore.FindAsync(job.Id);

        Assert.NotNull(found);
        Assert.True(found.IsAbandoned);
    }

    /// <summary>
    /// 更新不存在的作业不会插入
    /// </summary>
    [Fact]
    public async Task 更新不存在的作业不会插入()
    {
        using var context = new TasksTestContext();

        await context.BackgroundJobStore.UpdateAsync(NewJob());

        Assert.Equal(0, await context.Client.Queryable<SysBackgroundJob>().CountAsync());
    }

    /// <summary>
    /// 在租户上下文中入队时以宿主上下文写库并保留作业租户
    /// </summary>
    [Fact]
    public async Task 在租户上下文中入队时以宿主上下文写库并保留作业租户()
    {
        using var context = new TasksTestContext();
        var job = NewJob();
        job.TenantId = 42;

        using (context.Tenant.Change(42))
        {
            await context.BackgroundJobStore.InsertAsync(job);
        }

        Assert.NotEmpty(context.ExecutingTenantIds);
        Assert.All(context.ExecutingTenantIds, tenantId => Assert.Null(tenantId));
        Assert.NotEmpty(context.Resolver.ObservedTenantIds);
        Assert.All(context.Resolver.ObservedTenantIds, tenantId => Assert.Null(tenantId));

        var stored = await context.Client.Queryable<SysBackgroundJob>().FirstAsync();
        Assert.Equal(42L, stored.TenantId);
    }

    private static BackgroundJobInfo NewJob()
    {
        return new BackgroundJobInfo
        {
            Id = Guid.NewGuid(),
            JobName = "Order.Close",
            JobArgs = "{\"orderId\":1}",
            CreationTime = TasksTestContext.BaseTime.AddMinutes(-30),
            NextTryTime = TasksTestContext.BaseTime.AddMinutes(-1)
        };
    }

    private static void AssertClose(DateTime expected, DateTime actual)
    {
        Assert.True(
            Math.Abs((expected - actual).TotalSeconds) < 1,
            $"期望 {expected:O}，实际 {actual:O}");
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0234`——`XiHan.Framework.Tasks.SqlSugar.BackgroundJobs` 尚不存在。

- [ ] **Step 4: 实现存储**

`framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Tasks.BackgroundJobs.Abstractions;
using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Entities;
using XiHan.Framework.Tasks.SqlSugar.Mapping;
using XiHan.Framework.Tasks.SqlSugar.Options;
using XiHan.Framework.Timing;

namespace XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;

/// <summary>
/// 后台作业存储的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 作业行写入默认布局的主库；存在事务型环境工作单元时，入队参与该工作单元的事务。
/// </remarks>
public class SqlSugarBackgroundJobStore : IBackgroundJobStore
{
    private readonly TasksHostClientAccessor _clientAccessor;
    private readonly IClock _clock;
    private readonly XiHanTasksSqlSugarOptions _options;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientAccessor">宿主上下文客户端访问器</param>
    /// <param name="clock">时钟</param>
    /// <param name="options">任务存储配置</param>
    public SqlSugarBackgroundJobStore(
        TasksHostClientAccessor clientAccessor,
        IClock clock,
        IOptions<XiHanTasksSqlSugarOptions> options)
    {
        _clientAccessor = clientAccessor;
        _clock = clock;
        _options = options.Value;
    }

    /// <summary>
    /// 按标识查找作业，已放弃的作业同样返回
    /// </summary>
    /// <param name="jobId">作业标识</param>
    /// <returns>作业信息，不存在则为 null</returns>
    public async Task<BackgroundJobInfo?> FindAsync(Guid jobId)
    {
        var entity = await _clientAccessor.ExecuteAsync(client => client.Queryable<SysBackgroundJob>()
            .Where(item => item.BasicId == jobId)
            .FirstAsync());

        return entity is null ? null : BackgroundJobMapper.ToJobInfo(entity);
    }

    /// <summary>
    /// 插入作业
    /// </summary>
    /// <param name="jobInfo">作业信息</param>
    /// <returns>任务</returns>
    public async Task InsertAsync(BackgroundJobInfo jobInfo)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);

        var entity = BackgroundJobMapper.ToEntity(jobInfo);

        await _clientAccessor.ExecuteAsync(client => client.Insertable(entity).ExecuteCommandAsync());
    }

    /// <summary>
    /// 获取待执行作业
    /// </summary>
    /// <param name="applicationName">应用名</param>
    /// <param name="maxResultCount">最大返回数量</param>
    /// <returns>待执行作业列表</returns>
    public Task<List<BackgroundJobInfo>> GetWaitingJobsAsync(string? applicationName, int maxResultCount)
    {
        return Task.FromResult(new List<BackgroundJobInfo>());
    }

    /// <summary>
    /// 删除作业
    /// </summary>
    /// <param name="jobId">作业标识</param>
    /// <returns>任务</returns>
    public async Task DeleteAsync(Guid jobId)
    {
        await _clientAccessor.ExecuteAsync(client => client.Deleteable<SysBackgroundJob>()
            .Where(item => item.BasicId == jobId)
            .ExecuteCommandAsync());
    }

    /// <summary>
    /// 更新作业并释放领取租约，作业不存在时不插入
    /// </summary>
    /// <param name="jobInfo">作业信息</param>
    /// <returns>任务</returns>
    public async Task UpdateAsync(BackgroundJobInfo jobInfo)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);

        var entity = BackgroundJobMapper.ToEntity(jobInfo);

        await _clientAccessor.ExecuteAsync(client => client.Updateable(entity).ExecuteCommandAsync());
    }
}
```

`GetWaitingJobsAsync` 这里是过渡实现，Task 5 Step 3 给出完整代码并整体替换它。`_clock` 与 `_options` 在本任务尚未被读取，Task 5 会用到它们；**不要**为此删掉构造参数。

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `在租户上下文中入队时以宿主上下文写库并保留作业租户` 失败于 `ExecutingTenantIds` 含 42：说明 `Change(null)` 没有包住 `await operation(client)`，回到 Task 3 核对。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 后台作业存储的增删改查"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。若出现任何与 `_clock` / `_options` 未被读取相关的警告，不要带着警告提交：先完成 Task 5，把两个任务合为一次提交后再验证构建。

---

### Task 5: 原子领取

**Files:**
- Modify: `framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobClaimTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `SqlSugarBackgroundJobStore`、`TasksTestContext`、`FakeClock`
- Produces: `GetWaitingJobsAsync` 的完整实现；新增私有方法 `Task<List<SysBackgroundJob>> ClaimAsync(ISqlSugarClient client, string applicationKey, int maxResultCount)`

**参考来源（动手前先读）：**
- 已验证的协议：`framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs:155-208`
- 过滤与排序的权威：`framework/src/XiHan.Framework.Tasks/BackgroundJobs/DefaultBackgroundJobStore.cs:62-74`
- 设计：spec §4.7、§5 ①②③

**本任务禁止事项：** 硬约束 ①②③。**不要**使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT`、`UPDATE TOP` 或任何方言相关 SQL。**不要**删掉三轮重试循环——SQLite 用例覆盖不到它，删掉也不会有任何测试变红。**不要**在领取里开事务。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobClaimTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 后台作业领取测试
/// </summary>
public class BackgroundJobClaimTests
{
    /// <summary>
    /// 只领取到期未放弃且应用名匹配的作业
    /// </summary>
    [Fact]
    public async Task 只领取到期未放弃且应用名匹配的作业()
    {
        using var context = new TasksTestContext();
        var due = NewJob("Shop");
        var future = NewJob("Shop", TasksTestContext.BaseTime.AddMinutes(10));
        var abandoned = NewJob("Shop");
        abandoned.IsAbandoned = true;
        var otherApplication = NewJob("Crm");
        var noApplication = NewJob(null);

        foreach (var job in new[] { due, future, abandoned, otherApplication, noApplication })
        {
            await context.BackgroundJobStore.InsertAsync(job);
        }

        var claimed = await context.BackgroundJobStore.GetWaitingJobsAsync("Shop", 10);

        var single = Assert.Single(claimed);
        Assert.Equal(due.Id, single.Id);
    }

    /// <summary>
    /// 应用名为空时只领取应用名为空的作业
    /// </summary>
    [Fact]
    public async Task 应用名为空时只领取应用名为空的作业()
    {
        using var context = new TasksTestContext();
        var noApplication = NewJob(null);
        await context.BackgroundJobStore.InsertAsync(noApplication);
        await context.BackgroundJobStore.InsertAsync(NewJob("Shop"));

        var claimed = await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10);

        var single = Assert.Single(claimed);
        Assert.Equal(noApplication.Id, single.Id);
        Assert.Null(single.ApplicationName);
    }

    /// <summary>
    /// 按优先级降序重试次数升序下次执行时间升序排序
    /// </summary>
    [Fact]
    public async Task 按优先级降序重试次数升序下次执行时间升序排序()
    {
        using var context = new TasksTestContext();
        var low = NewJob(null);
        low.Priority = BackgroundJobPriority.Low;
        var highRetried = NewJob(null);
        highRetried.Priority = BackgroundJobPriority.High;
        highRetried.TryCount = 2;
        var highLater = NewJob(null, TasksTestContext.BaseTime.AddMinutes(-5));
        highLater.Priority = BackgroundJobPriority.High;
        var highEarlier = NewJob(null, TasksTestContext.BaseTime.AddMinutes(-10));
        highEarlier.Priority = BackgroundJobPriority.High;

        foreach (var job in new[] { low, highRetried, highLater, highEarlier })
        {
            await context.BackgroundJobStore.InsertAsync(job);
        }

        var claimed = await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10);

        Guid[] expected = [highEarlier.Id, highLater.Id, highRetried.Id, low.Id];
        Assert.Equal(expected, claimed.Select(job => job.Id));
    }

    /// <summary>
    /// 最多返回指定数量
    /// </summary>
    [Fact]
    public async Task 最多返回指定数量()
    {
        using var context = new TasksTestContext();
        for (var index = 0; index < 5; index++)
        {
            await context.BackgroundJobStore.InsertAsync(NewJob(null));
        }

        var claimed = await context.BackgroundJobStore.GetWaitingJobsAsync(null, 3);

        Assert.Equal(3, claimed.Count);
    }

    /// <summary>
    /// 数量上限为零时返回空且不领取
    /// </summary>
    [Fact]
    public async Task 数量上限为零时返回空且不领取()
    {
        using var context = new TasksTestContext();
        await context.BackgroundJobStore.InsertAsync(NewJob(null));

        Assert.Empty(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 0));
        Assert.Single(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));
    }

    /// <summary>
    /// 已领取且租约未过期的作业不会被再次领取
    /// </summary>
    [Fact]
    public async Task 已领取且租约未过期的作业不会被再次领取()
    {
        using var context = new TasksTestContext();
        await context.BackgroundJobStore.InsertAsync(NewJob(null));

        Assert.Single(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));

        context.Clock.Now = TasksTestContext.BaseTime.AddMinutes(4);

        Assert.Empty(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));
    }

    /// <summary>
    /// 租约过期后作业可被再次领取
    /// </summary>
    [Fact]
    public async Task 租约过期后作业可被再次领取()
    {
        using var context = new TasksTestContext();
        var job = NewJob(null);
        await context.BackgroundJobStore.InsertAsync(job);
        await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10);

        context.Clock.Now = TasksTestContext.BaseTime.AddMinutes(6);

        var claimed = await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10);

        var single = Assert.Single(claimed);
        Assert.Equal(job.Id, single.Id);
    }

    /// <summary>
    /// 更新后释放租约并在下次执行时间到期后可再次领取
    /// </summary>
    [Fact]
    public async Task 更新后释放租约并在下次执行时间到期后可再次领取()
    {
        using var context = new TasksTestContext();
        await context.BackgroundJobStore.InsertAsync(NewJob(null));

        var job = Assert.Single(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));
        job.TryCount++;
        job.LastTryTime = context.Clock.Now;
        job.NextTryTime = context.Clock.Now.AddMinutes(2);
        await context.BackgroundJobStore.UpdateAsync(job);

        Assert.Empty(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));

        context.Clock.Now = TasksTestContext.BaseTime.AddMinutes(3);

        var again = Assert.Single(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));
        Assert.Equal(job.Id, again.Id);
        Assert.Equal((short)1, again.TryCount);
    }

    /// <summary>
    /// 领取以注入的时钟为准
    /// </summary>
    [Fact]
    public async Task 领取以注入的时钟为准()
    {
        using var context = new TasksTestContext();
        await context.BackgroundJobStore.InsertAsync(NewJob(null));

        Assert.Single(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));

        var stored = await context.Client.Queryable<SysBackgroundJob>().FirstAsync();
        Assert.NotNull(stored.ClaimToken);
        Assert.True(stored.ClaimTime.HasValue);
        Assert.True(
            Math.Abs((stored.ClaimTime.GetValueOrDefault() - TasksTestContext.BaseTime).TotalSeconds) < 1,
            $"领取时刻应取注入时钟的当前时间，实际 {stored.ClaimTime:O}");
    }

    /// <summary>
    /// 删除后不再被领取
    /// </summary>
    [Fact]
    public async Task 删除后不再被领取()
    {
        using var context = new TasksTestContext();
        var job = NewJob(null);
        await context.BackgroundJobStore.InsertAsync(job);

        await context.BackgroundJobStore.DeleteAsync(job.Id);

        Assert.Empty(await context.BackgroundJobStore.GetWaitingJobsAsync(null, 10));
    }

    private static BackgroundJobInfo NewJob(string? applicationName, DateTime? nextTryTime = null)
    {
        return new BackgroundJobInfo
        {
            Id = Guid.NewGuid(),
            ApplicationName = applicationName,
            JobName = "Order.Close",
            JobArgs = "{\"orderId\":1}",
            CreationTime = TasksTestContext.BaseTime.AddMinutes(-30),
            NextTryTime = nextTryTime ?? TasksTestContext.BaseTime.AddMinutes(-1)
        };
    }
}
```

夹具的时钟固定在 2030 年、作业时间也围绕 2030 年构造：一个误用 `DateTime.UtcNow` 的实现会认为所有作业都还没到期，这组用例全部领不到东西。

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：除 `数量上限为零时返回空且不领取`（第一句断言通过、第二句失败）与 `删除后不再被领取`（过渡实现恒返回空，恰好通过）外，其余领取用例失败于 `Assert.Single` / `Assert.Equal`——过渡实现恒返回空集合。

- [ ] **Step 3: 实现领取**

修改 `framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs`，把 `GetWaitingJobsAsync` 整个方法（含 XML 注释）替换为下面两个方法：

```csharp
    /// <summary>
    /// 领取一批待执行作业
    /// </summary>
    /// <remarks>
    /// 本方法在返回前会为作业盖上领取令牌与领取时刻，不是纯查询。
    /// 过滤条件为应用名相等、未放弃、下次执行时间不晚于当前时间、且未被领取或租约已过期；
    /// 按优先级降序、已尝试次数升序、下次执行时间升序排序。
    /// 租约时长由 <see cref="XiHanTasksSqlSugarOptions.BackgroundJobLeaseTimeout"/> 配置，
    /// 删除或更新作业会结束租约。应用名为空与空字符串视为同一个应用。
    /// </remarks>
    /// <param name="applicationName">应用名</param>
    /// <param name="maxResultCount">最大返回数量</param>
    /// <returns>本次领取到的作业</returns>
    public async Task<List<BackgroundJobInfo>> GetWaitingJobsAsync(string? applicationName, int maxResultCount)
    {
        if (maxResultCount <= 0)
        {
            return [];
        }

        var applicationKey = BackgroundJobMapper.ToApplicationKey(applicationName);

        var claimed = await _clientAccessor.ExecuteAsync(client => ClaimAsync(client, applicationKey, maxResultCount));

        return [.. claimed.Select(BackgroundJobMapper.ToJobInfo)];
    }

    /// <summary>
    /// 在指定库上领取一批作业
    /// </summary>
    /// <remarks>
    /// 选中的候选被其他实例抢先领走时另选一批重试，最多三轮；返回空集合表示确实没有可领取的作业。
    /// </remarks>
    /// <param name="client">客户端</param>
    /// <param name="applicationKey">应用名存储键</param>
    /// <param name="maxResultCount">最多领取的条数</param>
    /// <returns>本次领取到的作业实体</returns>
    private async Task<List<SysBackgroundJob>> ClaimAsync(
        ISqlSugarClient client,
        string applicationKey,
        int maxResultCount)
    {
        const int maxClaimAttempts = 3;

        for (var attempt = 0; attempt < maxClaimAttempts; attempt++)
        {
            var now = _clock.Now;
            var leaseExpiredBefore = now - _options.BackgroundJobLeaseTimeout;
            var claimToken = Guid.NewGuid().ToString("N");

            var candidateIds = await client.Queryable<SysBackgroundJob>()
                .Where(item => item.ApplicationName == applicationKey
                    && item.IsAbandoned == false
                    && item.NextTryTime <= now
                    && (item.ClaimTime == null || item.ClaimTime < leaseExpiredBefore))
                .OrderBy(item => item.Priority, OrderByType.Desc)
                .OrderBy(item => item.TryCount)
                .OrderBy(item => item.NextTryTime)
                .Take(maxResultCount)
                .Select(item => item.BasicId)
                .ToListAsync();

            if (candidateIds.Count == 0)
            {
                return [];
            }

            var affected = await client.Updateable<SysBackgroundJob>()
                .SetColumns(item => new SysBackgroundJob
                {
                    ClaimToken = claimToken,
                    ClaimTime = now
                })
                .Where(item => candidateIds.Contains(item.BasicId)
                    && item.ApplicationName == applicationKey
                    && item.IsAbandoned == false
                    && item.NextTryTime <= now
                    && (item.ClaimTime == null || item.ClaimTime < leaseExpiredBefore))
                .ExecuteCommandAsync();

            // 候选全被其他实例抢走，另选一批重试
            if (affected == 0)
            {
                continue;
            }

            return await client.Queryable<SysBackgroundJob>()
                .Where(item => item.ClaimToken == claimToken)
                .OrderBy(item => item.Priority, OrderByType.Desc)
                .OrderBy(item => item.TryCount)
                .OrderBy(item => item.NextTryTime)
                .ToListAsync();
        }

        return [];
    }
```

对照硬约束逐条自查：

- 抢占的 `Where` 以 `candidateIds.Contains(item.BasicId)` 开头，**后面紧跟**与候选查询逐字相同的四个可领取条件（硬约束 ①）
- `claimToken` 在 `for` 循环体内声明（硬约束 ②）
- `now` 取自 `_clock.Now`，`leaseExpiredBefore` 由它推出（硬约束 ③）

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

排错指引：

- `按优先级降序重试次数升序下次执行时间升序排序` 顺序不对：核对三个 `OrderBy` 的顺序与第一个的 `OrderByType.Desc`，候选查询与取回查询两处都要有
- `更新后释放租约并在下次执行时间到期后可再次领取` 最后一步领不到：`UpdateAsync` 没有清掉租约（spec §5 ④），核对它是否仍是 `Updateable(entity)` 且 `ToEntity` 把 `ClaimToken` / `ClaimTime` 置空
- 所有领取用例都领不到：检查是否误用了系统时钟（硬约束 ③）

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 后台作业领取改为条件抢占加租约"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。提交信息正文写明：沿用发件箱已被真实 MySQL 并发测试验证过的三步抢占协议；抢占的 `WHERE` 同时含候选主键与可领取条件，只按主键更新会让并发领取者互相覆盖令牌；不依赖 Worker 的分布式锁，因为未配置 Redis 时它只在进程内互斥。

---

### Task 6: 顶替主包的默认存储

**Files:**
- Modify: `framework/src/XiHan.Framework.Tasks.SqlSugar/Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksSqlSugarRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `TasksHostClientAccessor`、Task 5 的 `SqlSugarBackgroundJobStore`
- Produces: `AddXiHanTasksSqlSugar` 额外注册 `TasksHostClientAccessor`（单例）并以单例顶替 `IBackgroundJobStore`

**参考来源（动手前先读）：**
- 主包注册：`framework/src/XiHan.Framework.Tasks/BackgroundJobs/Extensions/DependencyInjection/XiHanBackgroundJobsServiceCollectionExtensions.cs:32`（`TryAddSingleton`）与 `:62`（Redis 版的 `Replace`）
- 设计：spec §4.8、§5 ⑥⑧

**本任务禁止事项：** 顶替用 `Replace`，**不许**用 `TryAdd`（spec §5 ⑧）。生命周期必须是 `Singleton`，不要改成 `Scoped`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksSqlSugarRegistrationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Tasks.BackgroundJobs;
using XiHan.Framework.Tasks.BackgroundJobs.Abstractions;
using XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Timing;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 任务 SqlSugar 存储注册测试
/// </summary>
public class TasksSqlSugarRegistrationTests
{
    /// <summary>
    /// 后台作业存储被顶替为单例
    /// </summary>
    [Fact]
    public void 后台作业存储被顶替为单例()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<IBackgroundJobStore, DefaultBackgroundJobStore>();

        services.AddXiHanTasksSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IBackgroundJobStore));
        Assert.Equal(typeof(SqlSugarBackgroundJobStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// 客户端访问器注册为单例
    /// </summary>
    [Fact]
    public void 客户端访问器注册为单例()
    {
        var services = new ServiceCollection();

        services.AddXiHanTasksSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(TasksHostClientAccessor));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// 校验作用域时可从根容器解析后台作业存储
    /// </summary>
    [Fact]
    public void 校验作用域时可从根容器解析后台作业存储()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FakeClock(TasksTestContext.BaseTime));
        services.AddTransient<ICurrentTenant>(_ => new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance));
        services.AddScoped<ISqlSugarClientResolver>(_ => throw new InvalidOperationException("解析存储时不应触达数据库。"));

        services.AddXiHanTasksSqlSugar(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        Assert.IsType<SqlSugarBackgroundJobStore>(provider.GetRequiredService<IBackgroundJobStore>());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：三个新用例失败——`后台作业存储被顶替为单例` 的实现类型仍是 `DefaultBackgroundJobStore`；`客户端访问器注册为单例` 在 `Assert.Single` 处找不到注册；`校验作用域时可从根容器解析后台作业存储` 抛「No service for type IBackgroundJobStore」。

- [ ] **Step 3: 补全注册**

把 `framework/src/XiHan.Framework.Tasks.SqlSugar/Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Tasks.BackgroundJobs.Abstractions;
using XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Options;

namespace XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 任务 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanTasksSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换任务模块的进程内存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanTasksSqlSugar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanTasksSqlSugarOptions>(
            configuration.GetSection(XiHanTasksSqlSugarOptions.SectionName));

        services.TryAddSingleton<TasksHostClientAccessor>();
        services.Replace(ServiceDescriptor.Singleton<IBackgroundJobStore, SqlSugarBackgroundJobStore>());

        return services;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，含 Task 1 的 `从配置节绑定租约时长`。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 以 SqlSugar 存储顶替默认后台作业存储"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。提交信息正文写明：主包用 `TryAddSingleton` 注册默认存储，本包必须 `Replace`；生命周期保持单例，避免下游注入 `IBackgroundJobManager` 的单例出现被捕获依赖。

---

### Task 7: 真实 MySQL 并发测试与反向验证

**Files:**
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobConcurrencyTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `TasksHostClientAccessor`、`StubClientResolver`；Task 4 的 `FakeClock`；Task 5 的 `SqlSugarBackgroundJobStore`
- Produces: 无（验证任务）

**参考来源（动手前先读）：**
- 范式：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`
- 设计：spec §6.2

**本任务禁止事项：** 不要用 SQLite 做并发测试——它的写锁是库级串行化，天然不竞争，证明不了任何事。**不要**为了让测试变绿去放宽断言（例如改成「总数大于等于 200」或对标识去重后再比较）。反向验证的临时改动**绝不提交**。

- [ ] **Step 1: 写并发测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobConcurrencyTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy;
using XiHan.Framework.Tasks.BackgroundJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Entities;
using XiHan.Framework.Tasks.SqlSugar.Options;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 后台作业并发领取测试，需要真实数据库
/// </summary>
/// <remarks>
/// 地址取环境变量 <c>XIHAN_TEST_MYSQL</c>，未设置时整类跳过。
/// </remarks>
public class BackgroundJobConcurrencyTests
{
    private const string SkipReason = "未设置 XIHAN_TEST_MYSQL，跳过真实数据库并发测试。";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("XIHAN_TEST_MYSQL");

    /// <summary>
    /// 并发领取时同一个作业只会被一个调用方领到
    /// </summary>
    [Fact]
    public async Task 并发领取时作业不重复()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        const int jobCount = 200;
        const int workerCount = 8;

        var now = DateTime.UtcNow;

        using var setupClient = CreateClient();
        setupClient.CodeFirst.InitTables(typeof(SysBackgroundJob));
        await setupClient.Deleteable<SysBackgroundJob>().ExecuteCommandAsync();

        var (setupStore, setupProvider) = CreateStore(setupClient, now);
        using (setupProvider)
        {
            for (var index = 0; index < jobCount; index++)
            {
                await setupStore.InsertAsync(new BackgroundJobInfo
                {
                    Id = Guid.NewGuid(),
                    JobName = "Order.Close",
                    JobArgs = "{}",
                    CreationTime = now.AddMinutes(-10),
                    NextTryTime = now.AddMinutes(-10).AddSeconds(index)
                });
            }
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            using var client = CreateClient();
            var (store, provider) = CreateStore(client, now);
            using (provider)
            {
                var claimedIds = new List<Guid>();

                while (true)
                {
                    var batch = await store.GetWaitingJobsAsync(null, 10);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    claimedIds.AddRange(batch.Select(item => item.Id));
                }

                return claimedIds;
            }
        })).ToArray();

        var results = await Task.WhenAll(workers);

        var allClaimed = results.SelectMany(ids => ids).ToList();

        Assert.Equal(allClaimed.Count, allClaimed.Distinct().Count());
        Assert.Equal(jobCount, allClaimed.Count);

        await setupClient.Deleteable<SysBackgroundJob>().ExecuteCommandAsync();
    }

    private static SqlSugarClient CreateClient()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = ConnectionString,
            DbType = DbType.MySql,
            IsAutoCloseConnection = true
        });
    }

    private static (SqlSugarBackgroundJobStore Store, ServiceProvider Provider) CreateStore(SqlSugarClient client, DateTime now)
    {
        var currentTenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
        var resolver = new StubClientResolver(client, currentTenant);

        var services = new ServiceCollection();
        services.AddScoped<ISqlSugarClientResolver>(_ => resolver);
        var provider = services.BuildServiceProvider();

        var accessor = new TasksHostClientAccessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            currentTenant);

        var store = new SqlSugarBackgroundJobStore(
            accessor,
            new FakeClock(now),
            Microsoft.Extensions.Options.Options.Create(new XiHanTasksSqlSugarOptions
            {
                BackgroundJobLeaseTimeout = TimeSpan.FromMinutes(5)
            }));

        return (store, provider);
    }
}
```

每个领取者持有**独立的** `SqlSugarClient` 与存储实例，时钟固定在同一时刻——租约在测试期间不会过期，已领取的作业不会被再次领取，因此「总数恰为 200」是可以精确断言的。

- [ ] **Step 2: 确认 CI 下跳过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，`并发领取时作业不重复` 显示为 skipped。

- [ ] **Step 3: 本机真库验证**

准备一个可用的 MySQL 实例并设置环境变量：

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

PowerShell 下写法：`$env:XIHAN_TEST_MYSQL = "Server=...;"`。

预期：`并发领取时作业不重复` PASS。

- [ ] **Step 4: 反向验证——拿掉可领取条件后测试必须变红**

这是证明测试没有空转的唯一办法。**临时**修改 `framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs` 中 `ClaimAsync` 的抢占语句，把：

```csharp
                .Where(item => candidateIds.Contains(item.BasicId)
                    && item.ApplicationName == applicationKey
                    && item.IsAbandoned == false
                    && item.NextTryTime <= now
                    && (item.ClaimTime == null || item.ClaimTime < leaseExpiredBefore))
```

改为：

```csharp
                .Where(item => candidateIds.Contains(item.BasicId))
```

保持 `XIHAN_TEST_MYSQL` 已设置，运行：

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：

- `并发领取时作业不重复` **FAIL**，失败于 `Assert.Equal(allClaimed.Count, allClaimed.Distinct().Count())` 或 `Assert.Equal(jobCount, allClaimed.Count)`。**记下失败输出里的两个数字**（领取总数与去重后数量），写进 PR 描述
- 其余 SQLite 用例**全部仍为 PASS**——这正是「单线程测试照样全绿」的实证，同样写进 PR 描述

若该测试在改动后**仍然通过**：把 `workerCount` 临时调到 16、`jobCount` 调到 1000 再跑一次；仍不变红就停下来报告，**不要**宣称验证完成——那意味着测试没有制造出真正的竞态。

然后**还原改动**：

```bash
git checkout -- framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs
git status
```

预期：`git status` 显示该文件无改动（只剩本任务新增的测试文件未提交）。再跑一次测试，`并发领取时作业不重复` 重新 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/BackgroundJobConcurrencyTests.cs
git commit -m "test(tasks-sqlsugar): 后台作业并发领取的真实数据库测试"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。提交信息正文写入 Step 3 的通过结果与 Step 4 反向验证的两个数字。

---

### Task 8: README 与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.Tasks.SqlSugar/README.md`（整份替换）

**Interfaces:**
- Consumes: 前七个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- README 七段结构与语气：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`
- 已知边界清单：spec §7

**本任务禁止事项：** **不要**改 `docs/`、根 `README.md` / `README_cn.md`、`framework/README.md` / `framework/README_cn.md`——那是第 ② 份的收尾内容。**不要**把权衡论证写进代码注释。

- [ ] **Step 1: 补全包 README**

把 `framework/src/XiHan.Framework.Tasks.SqlSugar/README.md` 整份替换为：

````markdown
# XiHan.Framework.Tasks.SqlSugar

## 概述

`XiHan.Framework.Tasks` 的 SqlSugar 持久化提供程序。主包的 `DefaultBackgroundJobStore` 是进程内实现，进程重启即丢、不跨实例；本包把后台作业落到数据库。

## 核心能力

- 后台作业实体 `sys_background_job` 与 `BackgroundJobInfo` 的双向映射
- 入队参与当前工作单元的事务：业务回滚，作业随之消失
- 多实例领取互斥：条件抢占 + 租约超时释放，不依赖分布式锁，也不依赖任何数据库方言特性
- 以 `Replace` 顶替主包的 `IBackgroundJobStore`，生命周期保持单例

## 依赖关系

依赖 `XiHan.Framework.Tasks`（存储契约与轮询 Worker）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为作业自身的 `Guid` 标识，非自增。

表结构由 `DbInitializer` 在应用启动时创建，这要求 `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 均为 `true`（二者默认均为 `false`）。都没开启又没有手工建表时，首次入队即抛「表不存在」，并使所在业务事务一同失败。自行维护表结构时按本包实体的列定义建表。

配置节 `XiHan:Tasks:SqlSugar`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `BackgroundJobLeaseTimeout` | `00:05:00` | 后台作业租约时长，领取后超过该时长仍未删除或更新的作业可被重新领取 |

作业行固定写入默认布局的主库，并在写库期间切换到宿主上下文。业务数据写在模块库或租户独立库时，入队与业务不在同一个事务里，且不会报错。

执行语义为**至少一次**：进程在作业执行成功与删除之间退出，作业会在租约过期后再次执行，作业处理器需幂等。

Worker 串行执行一轮领到的全部作业。未配置 Redis 时分布式锁只在进程内互斥，若一轮耗时超过租约时长，本轮尚未执行到的作业可能被另一实例领走并重复执行——此时调大 `BackgroundJobLeaseTimeout`，或调小 `XiHan:BackgroundJobs:MaxJobFetchCount`。

Worker 因停机或锁续期失败提前结束一轮时，已领取但未执行的作业要等租约过期才会被再次领取。

放弃的作业保留在表里并标记 `Is_Abandoned = 1`，没有自动清理，需应用自行定期删除旧行。

`UpdateAsync` 只更新已存在的作业，不存在时不插入；`InsertAsync` 遇主键重复时抛数据库异常。应用名为空与空字符串视为同一个应用。

不要在事务型工作单元里调用领取：条件 `UPDATE` 持有的行锁要到工作单元提交才释放。

## 使用方式

在应用启动模块上声明依赖 `XiHanTasksSqlSugarModule`。之后若再调用主包的 `UseRedisBackgroundJobStore()`，Redis 存储会反过来顶替本包——后调用者生效。

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.Tasks.BackgroundJobs.Abstractions.IBackgroundJobStore` 并在 DI 中 `Replace`。

## 目录结构

```
Entities/                        作业实体
Mapping/                         契约与实体的双向映射
Clients/                         宿主上下文的客户端访问器
BackgroundJobs/                  后台作业存储
Options/                         存储配置
Extensions/DependencyInjection/  服务注册扩展
```
````

- [ ] **Step 2: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过（真实数据库测试在无环境变量时跳过）。若唯一的失败是 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`，那是已知的 GC 时序抖动，与本包无关，重跑一次即可。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`），先结束残留的测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.Tasks.SqlSugar.Tests.exe"
```

- [ ] **Step 3: 注释复查**

通读本计划新增的每个 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即移出到提交信息。

特别检查：`TasksHostClientAccessor` 的 `<remarks>` 只描述「新建作用域、切换宿主、登记工作单元」这三件事**做了什么**，不解释**为什么**；`SqlSugarBackgroundJobStore.ClaimAsync` 里那一行 `// 候选全被其他实例抢走，另选一批重试` 描述的是代码行为，可以保留。

- [ ] **Step 4: 提交**

```bash
git add framework/src/XiHan.Framework.Tasks.SqlSugar/README.md
git commit -m "docs(tasks-sqlsugar): 补写后台作业存储的使用约定"
```

---

## 完成标准

本计划完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真实数据库测试在无环境变量时跳过）
- `XiHan.Framework.Tasks.SqlSugar` 与其测试项目已注册进 `framework/XiHan.Framework.slnx`
- `AddXiHanTasksSqlSugar` 之后容器里只有一条 `IBackgroundJobStore` 注册，实现为 `SqlSugarBackgroundJobStore`，生命周期 `Singleton`
- `ValidateScopes = true` 时可从根容器解析 `IBackgroundJobStore`
- 领取的过滤、排序、限量与 `DefaultBackgroundJobStore` 一致；应用名为空只匹配为空
- 已领取未过期的作业不被再次领取；租约过期后可再次领取；`UpdateAsync` 释放租约
- 在租户上下文入队时，SQL 执行期间当前租户为 `null`，作业行保留入队时的租户
- 本机配置 `XIHAN_TEST_MYSQL` 后并发测试通过；反向验证确认拿掉可领取条件后该测试变红，两个数字已记录
- 包 README 为七段结构，写明建表开关默认关闭

## 已知边界（写入 PR 描述，不写进代码注释）

- **至少一次执行**：进程在执行成功与删除之间退出，作业会在租约过期后再次执行，作业处理器需幂等。
- **租约短于单轮耗时**：未配置 Redis 时 Worker 的锁只在进程内互斥，一轮耗时超过租约时，本轮未执行到的作业可能被另一实例重复领取。调大租约或调小 `MaxJobFetchCount`。
- **提前结束的一轮**：已领取但未执行的作业最长延迟一个租约时长才会被再次领取；`Default` 与 `Redis` 版下一轮即可领到。
- **更新不再是插入或更新**：`UpdateAsync` 对不存在的作业不插入，与 `Default` / `Redis` 版不同。
- **重复插入抛异常**：`InsertAsync` 遇主键重复时抛数据库异常，与 `Default` 版的覆盖不同。
- **放弃的作业只增不减**：没有自动清理，也没有 TTL。
- **空字符串应用名**：`""` 与 `null` 被视为同一个应用。
- **只落宿主主库**：业务写在模块库或租户库时，入队与业务不在同一个事务。
- **不要在业务工作单元里领取**：行锁要到工作单元提交才释放。
- **抢占的受影响行数口径**：每轮抢占都写入新令牌与新时刻，MySQL 两种口径下都大于 0；若日后改成「仅在必要时改列」需重新评估。
- **建表默认关闭**：两个建表开关默认 `false`。
- **生命周期不变**：`IBackgroundJobStore` 仍为 `Singleton`，不构成破坏性变更，无需配置层面的逃生口；要退回内存存储，去掉对 `XiHanTasksSqlSugarModule` 的依赖即可。
- **上游验收标准**：0 警告 0 错误是硬门槛；一个 PR 只做一件事（本包的两份计划合为一个 PR，不顺手改无关文档）；注释只写代码做什么。

## 下一份计划

第 ② 份（`.superpowers/plans/2026-09-28-tasks-sqlsugar-2-scheduled-jobs.md`）：`IJobStore` 的 SqlSugar 实现——`sys_job_instance`、`sys_job_history` 两个实体、运行中实例的截止时刻、历史清理，以及本包的文档站收尾（`docs/packages/tasks-sqlsugar.md`、`docs/.vitepress/config.ts` 侧边栏、`framework/README.md` 与 `framework/README_cn.md` 的模块清单）。两份都完成后本包的 PR 方可提交。
