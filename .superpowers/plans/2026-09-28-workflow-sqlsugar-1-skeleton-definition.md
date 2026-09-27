# Workflow.SqlSugar 第 1 份：包骨架、执行器与流程定义存储 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建 `XiHan.Framework.Workflow.SqlSugar` 包的骨架，落地三个存储共用的执行器（独立事务 + 固定连接），并以 SqlSugar 实现替换 `IWorkflowDefinitionStore`。

**Architecture:** 执行器 `WorkflowSqlSugarExecutor` 每次操作经 `IUnitOfWorkManager.Begin(requiresNew: true)` 开一个独立的事务型工作单元，再经 `ISqlSugarClientResolver.GetClient(配置标识)` 取该工作单元登记的连接，执行后立即提交。定义存储把 `WorkflowDefinition` 映射为 `sys_workflow_definition` 一行：标量字段各占一列，节点、连线、变量声明、扩展属性各存一个 JSON 列。注册用 `Replace` 顶替主包的 `TryAddSingleton`，生命周期 Scoped。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-workflow-sqlsugar-1-skeleton-definition-design.md`

**Linear 议题:** https://linear.app/elf-express/issue/EDDIE-14

**前置:** 无。依赖 `dev` 上已有的 `ISqlSugarClientResolver.GetClient`、`UnitOfWorkManager.Begin(requiresNew: true)` 的隔离连接、`TableInitializationAttribute.Target`。

> spec 自成一体，实现本计划所需的全部约束都在其中。**先读 spec 第 2、5 节再动手。**

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：从 `dev` 开 worktree，分支 `feat/workflow-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。三份计划在同一个 worktree、同一个分支上依次实现。

```bash
git -C E:/source/XiHan/XiHan.Framework worktree add ../XiHan.Framework-workflow -b feat/workflow-sqlsugar dev
```

以下所有路径相对 `E:/source/XiHan/XiHan.Framework-workflow`。spec 与计划若在 worktree 里看不到，按上面的绝对路径到主工作区读取。

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

**SqlSugar 签名只信源码**：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`，权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）。更新提供者的目录名是 `Abstract/UpdateProvider/`，**不是** `UpdateableProvider`。文档：`E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar —— Interface/ISqlSugarClient.cs
ISugarQueryable<T> Queryable<T>();
IInsertable<T> Insertable<T>(T insertObj) where T : class, new();
IUpdateable<T> Updateable<T>(T UpdateObj) where T : class, new();
IDeleteable<T> Deleteable<T>() where T : class, new();

// SqlSugar —— Interface/IQueryable.cs
ISugarQueryable<T> Where(Expression<Func<T, bool>> expression);
ISugarQueryable<T> WhereIF(bool isWhere, Expression<Func<T, bool>> expression);
ISugarQueryable<T> OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc);
ISugarQueryable<T> Take(int num);
ISugarQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> expression);
Task<List<T>> ToListAsync(CancellationToken token);
int Count();

// SqlSugar —— Interface/Insertable.cs、IUpdateable.cs、IDeleteable.cs
Task<int> ExecuteCommandAsync(CancellationToken token);          // 三者相同
IDeleteable<T> Where(Expression<Func<T, bool>> expression);

// SqlSugar —— Entities/Mapping/SugarMappingAttribute.cs:354
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)
// fieldName 按「属性名」匹配（CodeFirstProvider.cs:381），索引名里的 {table} 在建索引时替换为表名（CodeFirstProvider.cs:361-365）

// SqlSugar —— Infrastructure/StaticConfig.cs:16
public const string CodeFirst_BigString = "varcharmax,longtext,text,clob";

// 框架 —— XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver
ISqlSugarClient GetClient(string configId);    // 内部调 EnlistCurrentUnitOfWork

// 框架 —— XiHan.Framework.Uow.IUnitOfWorkManager / IUnitOfWork
IUnitOfWork Begin(XiHanUnitOfWorkOptions options, bool requiresNew = false);
Task CompleteAsync(CancellationToken cancellationToken = default);
// XiHan.Framework.Uow.Options.XiHanUnitOfWorkOptions
XiHanUnitOfWorkOptions(bool isTransactional = false, IsolationLevel? isolationLevel = null, int? timeout = null)

// 框架 —— XiHan.Framework.Data.SqlSugar.Initializers
[TableInitialization(Target = DbInitializationTarget.Platform)]   // Platform = 1 << 0

// 框架 —— XiHan.Framework.Data.SqlSugar.Options.XiHanSqlSugarCoreOptions
public string DefaultConfigId { get; set; } = "Default";
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**。权衡论证、踩坑叙事、设计理由、反事实推理（「否则会……」）一律进提交信息，不进注释
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以）
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀
- `public` 成员必须有 `<summary>`（`GenerateDocumentationFile` 全局开启，缺了会告警）

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`；全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，**不 Import `version.props`、不设 `AssemblyName`**。`Microsoft.Data.Sqlite` 与 MySQL 驱动经 `SqlSugarCore` 传递引入，**不需要额外 `PackageReference`**。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**真实数据库测试**：需要时用 `Assert.SkipWhen(...)` + 环境变量取地址，CI 自动跳过。范式：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxConcurrencyTests.cs`（读 `XIHAN_TEST_MYSQL`）。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本系列无关，不要去追。

**建表**：`XiHan:Data:SqlSugarCore` 的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**。不开启就不会自动建表，首次写入即报表不存在。第 3 份写进 README。

**提交信息**：中文 Conventional Commits，作用域 `workflow-sqlsugar`（例如 `feat(workflow-sqlsugar): ...`）。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

---

## 本计划特有的硬约束

**① 存储的每一次读写都经执行器，执行器必须 `requiresNew: true` + `isTransactional: true`。**

不得用 `GetClientForEntity<T>()`、`GetCurrentClient()`，不得只把写操作放进执行器。这些写法单元测试都全绿，故障只在多实例 + 请求事务里出现：锁释放时写入尚未提交，另一节点重放同一书签。Task 6 要求做一次「改成 `requiresNew: false` 后隔离用例变红」的验证。

**② 实体不得出现名为 `TenantId` 的属性，不得实现 `IMultiTenantEntity`。**

插入 AOP 按属性名改写租户列，测试夹具不挂 AOP、发现不了。租户列的属性名是 `OwnerTenantId`，列名 `Tenant_Id`。Task 2 的约定测试钉住这一条。

**③ JSON 列一律 `JsonSerializerOptions.Web`，不加任何转换器、不设 `DictionaryKeyPolicy`。**

必须与 `WorkflowValueConverter.ConvertTo` 的反序列化选项一致。

**④ `UpdateAsync` 是纯更新：全部列、按主键、0 行不补插；`InsertAsync` 是纯插入。**

不用 `Storageable` / `InsertOrUpdate`，不写 `IgnoreColumns(ignoreAllNullColumns: true)`，不调 `IsEnableUpdateVersionValidation()`。

**⑤ 测试代码里调 `Options.Create` 必须写全限定名 `Microsoft.Extensions.Options.Options.Create(...)`。**

测试命名空间 `XiHan.Framework.Workflow.SqlSugar.Tests` 的父命名空间里有 `XiHan.Framework.Workflow.SqlSugar.Options`，裸写 `Options.Create` 会解析成那个命名空间而编译失败。

---

## File Structure

```
framework/src/XiHan.Framework.Workflow.SqlSugar/
  XiHan.Framework.Workflow.SqlSugar.csproj                                   新建
  XiHanWorkflowSqlSugarModule.cs                                             新建
  Options/XiHanWorkflowSqlSugarOptions.cs                                    新建
  Entities/SysWorkflowDefinition.cs                                          新建
  Mapping/WorkflowJsonColumn.cs                                              新建（internal）
  Mapping/WorkflowDefinitionMapper.cs                                        新建
  Stores/WorkflowSqlSugarExecutor.cs                                         新建
  Stores/SqlSugarWorkflowDefinitionStore.cs                                  新建
  Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs   新建

framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/
  XiHan.Framework.Workflow.SqlSugar.Tests.csproj                             新建
  TestEntityTypes.cs                                                         新建（第 2、3 份各改一行）
  TestDoubles.cs                                                             新建
  WorkflowTestDatabase.cs                                                    新建
  ServiceRegistrationTests.cs                                                新建（第 2、3 份追加用例）
  WorkflowEntityConventionTests.cs                                           新建
  WorkflowDefinitionEntityTests.cs                                           新建
  WorkflowDefinitionMapperTests.cs                                           新建
  WorkflowSqlSugarExecutorTests.cs                                           新建
  SqlSugarWorkflowDefinitionStoreTests.cs                                    新建

framework/XiHan.Framework.slnx                                               修改：登记两个项目
```

---

### Task 1: 项目骨架、选项与解决方案登记

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/XiHan.Framework.Workflow.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Options/XiHanWorkflowSqlSugarOptions.cs`
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/XiHanWorkflowSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/ServiceRegistrationTests.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: `XiHan.Framework.Workflow.XiHanWorkflowModule`、`XiHan.Framework.Data.XiHanDataModule`、`XiHan.Framework.Core.Extensions.DependencyInjection` 的 `services.GetConfiguration()`
- Produces:
  - `XiHanWorkflowSqlSugarOptions`：`const string SectionName = "XiHan:Workflow:SqlSugar"`、`string? ConfigId { get; set; }`
  - `XiHanWorkflowSqlSugarServiceCollectionExtensions.AddXiHanWorkflowSqlSugar(this IServiceCollection services, IConfiguration configuration)`：本任务只绑定选项，Task 4、5 与第 2、3 份逐步追加注册
  - `XiHanWorkflowSqlSugarModule`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`、`XiHanSqlSugarEventBusModule.cs`、`Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs`
- `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`
- `framework/XiHan.Framework.slnx` 的 `/1.src/6.Infrastructure/` 与 `/2.tests/1.UnitTests/` 两个文件夹

**本任务禁止事项：** 模块类里不写任何注册逻辑，只调扩展方法。测试 csproj 不 Import `version.props`、不设 `AssemblyName`、不加 `PackageReference`。

- [ ] **Step 1: 创建包 csproj**

`framework/src/XiHan.Framework.Workflow.SqlSugar/XiHan.Framework.Workflow.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Workflow.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Workflow.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Workflow.SqlSugar</PackageId>
        <Description>曦寒框架工作流定义、实例与书签的 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Workflow\XiHan.Framework.Workflow.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建选项**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Options/XiHanWorkflowSqlSugarOptions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Workflow.SqlSugar.Options;

/// <summary>
/// 工作流 SqlSugar 存储配置
/// </summary>
public class XiHanWorkflowSqlSugarOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Workflow:SqlSugar";

    /// <summary>
    /// 工作流数据表所在连接的配置标识，为空时使用数据访问的默认连接配置标识
    /// </summary>
    public string? ConfigId { get; set; }
}
```

- [ ] **Step 3: 创建测试项目**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Workflow.SqlSugar\XiHan.Framework.Workflow.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 4: 登记进解决方案**

修改 `framework/XiHan.Framework.slnx`。在 `/1.src/6.Infrastructure/` 文件夹内，把

```xml
    <Project Path="src/XiHan.Framework.Workflow/XiHan.Framework.Workflow.csproj" />
  </Folder>
```

改为

```xml
    <Project Path="src/XiHan.Framework.Workflow/XiHan.Framework.Workflow.csproj" />
    <Project Path="src/XiHan.Framework.Workflow.SqlSugar/XiHan.Framework.Workflow.SqlSugar.csproj" />
  </Folder>
```

在 `/2.tests/1.UnitTests/` 文件夹内，把

```xml
    <Project Path="test/XiHan.Framework.Workflow.Tests/XiHan.Framework.Workflow.Tests.csproj" />
```

改为

```xml
    <Project Path="test/XiHan.Framework.Workflow.Tests/XiHan.Framework.Workflow.Tests.csproj" />
    <Project Path="test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj" />
```

- [ ] **Step 5: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/ServiceRegistrationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Workflow.SqlSugar.Options;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 服务注册测试
/// </summary>
public class ServiceRegistrationTests
{
    /// <summary>
    /// 选项从配置节绑定
    /// </summary>
    [Fact]
    public void 选项从配置节绑定()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Workflow:SqlSugar:ConfigId"] = "Workflow"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddXiHanWorkflowSqlSugar(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<XiHanWorkflowSqlSugarOptions>>().Value;
        Assert.Equal("Workflow", options.ConfigId);
        Assert.Equal("XiHan:Workflow:SqlSugar", XiHanWorkflowSqlSugarOptions.SectionName);
    }
}
```

- [ ] **Step 6: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，命名空间 `XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection` 不存在（CS0234），`AddXiHanWorkflowSqlSugar` 无法解析。

- [ ] **Step 7: 创建注册扩展（本任务只绑定选项）**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Workflow.SqlSugar.Options;

namespace XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 工作流 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanWorkflowSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换工作流的进程内默认存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanWorkflowSqlSugar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanWorkflowSqlSugarOptions>(
            configuration.GetSection(XiHanWorkflowSqlSugarOptions.SectionName));

        return services;
    }
}
```

- [ ] **Step 8: 创建模块类**

`framework/src/XiHan.Framework.Workflow.SqlSugar/XiHanWorkflowSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Workflow.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Workflow.SqlSugar;

/// <summary>
/// 曦寒框架工作流 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanWorkflowSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 存储替换工作流的进程内默认存储。
/// 配置节：<c>XiHan:Workflow:SqlSugar</c>。
/// </remarks>
[DependsOn(
    typeof(XiHanWorkflowModule),
    typeof(XiHanDataModule)
)]
public class XiHanWorkflowSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context">服务配置上下文</param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddXiHanWorkflowSqlSugar(services.GetConfiguration());
    }
}
```

- [ ] **Step 9: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：`选项从配置节绑定` PASS。若报 `AddInMemoryCollection` 找不到，说明 `Microsoft.Extensions.Configuration` 未传递到测试项目——先确认 Step 4 的 slnx 与 Step 3 的项目引用无误再排查，不要加 `PackageReference`。

- [ ] **Step 10: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(workflow-sqlsugar): 新建工作流 SqlSugar 持久化包骨架"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: 定义实体与实体约定

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowDefinition.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestEntityTypes.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowEntityConventionTests.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowDefinitionEntityTests.cs`

**Interfaces:**
- Consumes: `XiHan.Framework.Data.SqlSugar.Entities.SugarEntity<TKey>`、`TableInitializationAttribute`、`DbInitializationTarget`
- Produces:
  - `SysWorkflowDefinition : SugarEntity<string>`，属性：`Code`、`Name`、`Version`、`Description`、`Category`、`Status`（`int`）、`EnableCompensation`、`NodesJson`、`TransitionsJson`、`VariablesJson`、`ExtraProperties`、`OwnerTenantId`（`long?`）、`CreationTime`、`PublishTime`；构造函数 `()` 与 `(string basicId)`
  - `TestEntityTypes.All`（`Type[]`）：全部工作流实体类型，第 2、3 份各改一行

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`（`SugarColumn` 写法）
- `framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs`
- `framework/src/XiHan.Framework.Data/SqlSugar/Extensions/EntityAuditExtensions.cs:155-190`（为什么不能叫 `TenantId`）
- spec §4.4

**本任务禁止事项：** 硬约束 ②。不加 `[SplitTable]`。`SugarIndex` 的字段参数写 `nameof(属性)`，不写列名；索引名必须带 `{table}`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestEntityTypes.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 测试用的工作流实体类型清单
/// </summary>
internal static class TestEntityTypes
{
    /// <summary>
    /// 全部工作流实体类型
    /// </summary>
    public static Type[] All { get; } = [typeof(SysWorkflowDefinition)];
}
```

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowEntityConventionTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using System.Reflection;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 工作流实体的共同约定测试
/// </summary>
public class WorkflowEntityConventionTests
{
    /// <summary>
    /// 实体不参与全局租户过滤
    /// </summary>
    [Fact]
    public void 实体不实现多租户接口()
    {
        foreach (var entityType in TestEntityTypes.All)
        {
            Assert.False(typeof(IMultiTenantEntity).IsAssignableFrom(entityType), entityType.Name);
        }
    }

    /// <summary>
    /// 实体没有会被插入审计改写的租户属性名
    /// </summary>
    [Fact]
    public void 实体没有名为TenantId的属性()
    {
        foreach (var entityType in TestEntityTypes.All)
        {
            Assert.Null(entityType.GetProperty("TenantId"));
        }
    }

    /// <summary>
    /// 实体只在平台库建表
    /// </summary>
    [Fact]
    public void 实体只在平台库建表()
    {
        foreach (var entityType in TestEntityTypes.All)
        {
            var attribute = entityType.GetCustomAttribute<TableInitializationAttribute>(inherit: true);

            Assert.NotNull(attribute);
            Assert.Equal(DbInitializationTarget.Platform, attribute.Target);
            Assert.False(attribute.IncludeModuleConnections);
        }
    }

    /// <summary>
    /// 实体主键为字符串
    /// </summary>
    [Fact]
    public void 实体主键为字符串()
    {
        foreach (var entityType in TestEntityTypes.All)
        {
            var property = entityType.GetProperty("BasicId");

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property.PropertyType);
        }
    }

    /// <summary>
    /// 实体索引名都带表名占位
    /// </summary>
    [Fact]
    public void 索引名都带表名占位()
    {
        foreach (var entityType in TestEntityTypes.All)
        {
            foreach (var index in entityType.GetCustomAttributes<SugarIndexAttribute>(inherit: true))
            {
                Assert.Contains("{table}", index.IndexName, StringComparison.Ordinal);
            }
        }
    }
}
```

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowDefinitionEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using System.Reflection;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程定义实体测试
/// </summary>
public class WorkflowDefinitionEntityTests
{
    /// <summary>
    /// 表名固定
    /// </summary>
    [Fact]
    public void 表名固定()
    {
        var attribute = typeof(SysWorkflowDefinition).GetCustomAttribute<SugarTable>();

        Assert.NotNull(attribute);
        Assert.Equal("sys_workflow_definition", attribute.TableName);
    }

    /// <summary>
    /// 编码与版本唯一
    /// </summary>
    [Fact]
    public void 编码与版本唯一()
    {
        var index = Assert.Single(typeof(SysWorkflowDefinition).GetCustomAttributes<SugarIndexAttribute>());

        Assert.True(index.IsUnique);
        Assert.Equal([nameof(SysWorkflowDefinition.Code), nameof(SysWorkflowDefinition.Version)], index.IndexFields.Keys);
    }

    /// <summary>
    /// 主键经构造函数传入
    /// </summary>
    [Fact]
    public void 主键经构造函数传入()
    {
        var entity = new SysWorkflowDefinition("1001");

        Assert.Equal("1001", entity.BasicId);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysWorkflowDefinition` 不存在（CS0246）。

- [ ] **Step 3: 创建实体**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Entities/SysWorkflowDefinition.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Workflow.SqlSugar.Entities;

/// <summary>
/// 流程定义实体
/// </summary>
[SugarTable("sys_workflow_definition")]
[TableInitialization(Target = DbInitializationTarget.Platform)]
[SugarIndex("ux_{table}_code_version", nameof(Code), OrderByType.Asc, nameof(Version), OrderByType.Asc, true)]
public class SysWorkflowDefinition : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysWorkflowDefinition() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">定义标识</param>
    public SysWorkflowDefinition(string basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 流程编码
    /// </summary>
    [SugarColumn(ColumnName = "Code", Length = 128, IsNullable = false, ColumnDescription = "流程编码")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 流程名称
    /// </summary>
    [SugarColumn(ColumnName = "Name", Length = 256, IsNullable = false, ColumnDescription = "流程名称")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 版本号
    /// </summary>
    [SugarColumn(ColumnName = "Version", IsNullable = false, ColumnDescription = "版本号")]
    public int Version { get; set; }

    /// <summary>
    /// 描述
    /// </summary>
    [SugarColumn(ColumnName = "Description", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "描述")]
    public string? Description { get; set; }

    /// <summary>
    /// 分类
    /// </summary>
    [SugarColumn(ColumnName = "Category", Length = 128, IsNullable = true, ColumnDescription = "分类")]
    public string? Category { get; set; }

    /// <summary>
    /// 状态，0 草稿，1 已发布，2 已停用，3 已归档
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "状态，0 草稿，1 已发布，2 已停用，3 已归档")]
    public int Status { get; set; }

    /// <summary>
    /// 是否启用补偿
    /// </summary>
    [SugarColumn(ColumnName = "Enable_Compensation", IsNullable = false, ColumnDescription = "是否启用补偿")]
    public bool EnableCompensation { get; set; }

    /// <summary>
    /// 节点集合的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Nodes_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "节点集合的 JSON")]
    public string NodesJson { get; set; } = "[]";

    /// <summary>
    /// 连线集合的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Transitions_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "连线集合的 JSON")]
    public string TransitionsJson { get; set; } = "[]";

    /// <summary>
    /// 启动变量声明的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Variables_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "启动变量声明的 JSON")]
    public string VariablesJson { get; set; } = "[]";

    /// <summary>
    /// 扩展属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Extra_Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "扩展属性的 JSON")]
    public string ExtraProperties { get; set; } = "{}";

    /// <summary>
    /// 租户标识，为空表示平台级定义
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识，为空表示平台级定义")]
    public long? OwnerTenantId { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Creation_Time", IsNullable = false, ColumnDescription = "创建时间")]
    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 发布时间
    /// </summary>
    [SugarColumn(ColumnName = "Publish_Time", IsNullable = true, ColumnDescription = "发布时间")]
    public DateTime? PublishTime { get; set; }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增流程定义实体"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 3: JSON 列与定义映射

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowJsonColumn.cs`
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowDefinitionMapper.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowDefinitionMapperTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysWorkflowDefinition`；`XiHan.Framework.Workflow.Abstractions.Definitions` 的 `WorkflowDefinition`、`WorkflowNode`、`WorkflowTransition`、`WorkflowVariableDefinition`
- Produces:
  - `internal static class WorkflowJsonColumn`：`string Serialize<T>(T value)`、`T Deserialize<T>(string? json) where T : class, new()`（第 2、3 份复用）
  - `public static class WorkflowDefinitionMapper`：`SysWorkflowDefinition ToEntity(WorkflowDefinition definition)`、`WorkflowDefinition ToDefinition(SysWorkflowDefinition entity)`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.Abstractions/Runtime/WorkflowValueConverter.cs`（第 87 行的反序列化选项）
- `framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventOutboxMapper.cs`
- spec §4.5

**本任务禁止事项：** 硬约束 ③。不要复用 `WorkflowDefinitionJsonSerializer`——它带 `JsonStringEnumConverter` 且序列化整份定义。映射不做任何默认值替换以外的转换（空字符串与 `null` 原样保留）。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowDefinitionMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.Abstractions.Runtime;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Mapping;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程定义映射测试
/// </summary>
public class WorkflowDefinitionMapperTests
{
    /// <summary>
    /// 往返后标量字段一致
    /// </summary>
    [Fact]
    public void 往返后标量字段一致()
    {
        var definition = CreateDefinition();

        var entity = WorkflowDefinitionMapper.ToEntity(definition);
        var restored = WorkflowDefinitionMapper.ToDefinition(entity);

        Assert.Equal("1001", entity.BasicId);
        Assert.Equal(7, entity.OwnerTenantId);
        Assert.Equal((int)WorkflowDefinitionStatus.Published, entity.Status);
        Assert.Equal(definition.Id, restored.Id);
        Assert.Equal(definition.Code, restored.Code);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.Version, restored.Version);
        Assert.Equal(definition.Description, restored.Description);
        Assert.Equal(definition.Category, restored.Category);
        Assert.Equal(definition.Status, restored.Status);
        Assert.Equal(definition.EnableCompensation, restored.EnableCompensation);
        Assert.Equal(definition.TenantId, restored.TenantId);
        Assert.Equal(definition.CreationTime, restored.CreationTime);
        Assert.Equal(definition.PublishTime, restored.PublishTime);
        Assert.Equal("{\"zoom\":1}", restored.ExtraProperties["layout"]);
    }

    /// <summary>
    /// 往返后图结构一致
    /// </summary>
    [Fact]
    public void 往返后图结构一致()
    {
        var restored = WorkflowDefinitionMapper.ToDefinition(WorkflowDefinitionMapper.ToEntity(CreateDefinition()));

        var node = Assert.Single(restored.Nodes);
        Assert.Equal("wait", node.Id);
        Assert.Equal(WorkflowActivityTypes.Delay, node.ActivityType);
        Assert.Equal(300, WorkflowValueConverter.ConvertTo<int>(node.Properties["Duration"]));
        Assert.Equal(60, node.TimeoutSeconds);
        Assert.True(node.ContinueOnError);
        Assert.NotNull(node.RetryPolicy);
        Assert.Equal(3, node.RetryPolicy.MaxAttempts);

        var transition = Assert.Single(restored.Transitions);
        Assert.Equal("start", transition.SourceNodeId);
        Assert.Equal("wait", transition.TargetNodeId);
        Assert.Equal("days > 1", transition.Condition);
        Assert.Equal(2, transition.Priority);
        Assert.True(transition.IsDefault);

        var variable = Assert.Single(restored.Variables);
        Assert.Equal("days", variable.Name);
        Assert.True(variable.Required);
        Assert.Equal(1, WorkflowValueConverter.ConvertTo<int>(variable.DefaultValue));
    }

    /// <summary>
    /// 空白 JSON 列读回为空集合
    /// </summary>
    [Fact]
    public void 空白JSON列读回为空集合()
    {
        var entity = new SysWorkflowDefinition("1002")
        {
            Code = "empty",
            Name = "空",
            NodesJson = string.Empty,
            TransitionsJson = " ",
            VariablesJson = string.Empty,
            ExtraProperties = string.Empty
        };

        var restored = WorkflowDefinitionMapper.ToDefinition(entity);

        Assert.Empty(restored.Nodes);
        Assert.Empty(restored.Transitions);
        Assert.Empty(restored.Variables);
        Assert.Empty(restored.ExtraProperties);
    }

    /// <summary>
    /// 变量名的大小写保持不变
    /// </summary>
    [Fact]
    public void 字典键大小写保持不变()
    {
        var definition = CreateDefinition();
        definition.ExtraProperties["CanvasLayout"] = "x";

        var restored = WorkflowDefinitionMapper.ToDefinition(WorkflowDefinitionMapper.ToEntity(definition));

        Assert.True(restored.ExtraProperties.ContainsKey("CanvasLayout"));
        Assert.Equal("Duration", Assert.Single(restored.Nodes).Properties.Keys.Single());
    }

    private static WorkflowDefinition CreateDefinition()
    {
        return new WorkflowDefinition
        {
            Id = "1001",
            Code = "leave",
            Name = "请假",
            Version = 3,
            Description = "请假审批",
            Category = "hr",
            Status = WorkflowDefinitionStatus.Published,
            EnableCompensation = true,
            Nodes =
            [
                new WorkflowNode
                {
                    Id = "wait",
                    Name = "等待",
                    ActivityType = WorkflowActivityTypes.Delay,
                    Properties = new Dictionary<string, object?> { ["Duration"] = 300 },
                    RetryPolicy = new WorkflowRetryPolicy { MaxAttempts = 3 },
                    TimeoutSeconds = 60,
                    ContinueOnError = true
                }
            ],
            Transitions =
            [
                new WorkflowTransition
                {
                    Id = "t1",
                    SourceNodeId = "start",
                    TargetNodeId = "wait",
                    Condition = "days > 1",
                    Priority = 2,
                    IsDefault = true
                }
            ],
            Variables =
            [
                new WorkflowVariableDefinition { Name = "days", Required = true, DefaultValue = 1 }
            ],
            TenantId = 7,
            CreationTime = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc),
            PublishTime = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc),
            ExtraProperties = new Dictionary<string, string> { ["layout"] = "{\"zoom\":1}" }
        };
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`WorkflowDefinitionMapper` 不存在。

- [ ] **Step 3: 创建 JSON 列工具**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowJsonColumn.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;

namespace XiHan.Framework.Workflow.SqlSugar.Mapping;

/// <summary>
/// 工作流 JSON 列的序列化工具，选项与 WorkflowValueConverter 的反序列化选项一致
/// </summary>
internal static class WorkflowJsonColumn
{
    /// <summary>
    /// 序列化为 JSON 文本
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    /// <param name="value">值</param>
    /// <returns>JSON 文本</returns>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// 从 JSON 文本反序列化，空白文本返回新实例
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    /// <param name="json">JSON 文本</param>
    /// <returns>反序列化结果</returns>
    public static T Deserialize<T>(string? json)
        where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new T();
        }

        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? new T();
    }
}
```

- [ ] **Step 4: 创建定义映射**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowDefinitionMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.SqlSugar.Entities;

namespace XiHan.Framework.Workflow.SqlSugar.Mapping;

/// <summary>
/// 流程定义契约与实体的双向映射
/// </summary>
public static class WorkflowDefinitionMapper
{
    /// <summary>
    /// 把流程定义转换为实体
    /// </summary>
    /// <param name="definition">流程定义</param>
    /// <returns>定义实体</returns>
    public static SysWorkflowDefinition ToEntity(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new SysWorkflowDefinition(definition.Id)
        {
            Code = definition.Code,
            Name = definition.Name,
            Version = definition.Version,
            Description = definition.Description,
            Category = definition.Category,
            Status = (int)definition.Status,
            EnableCompensation = definition.EnableCompensation,
            NodesJson = WorkflowJsonColumn.Serialize(definition.Nodes),
            TransitionsJson = WorkflowJsonColumn.Serialize(definition.Transitions),
            VariablesJson = WorkflowJsonColumn.Serialize(definition.Variables),
            ExtraProperties = WorkflowJsonColumn.Serialize(definition.ExtraProperties),
            OwnerTenantId = definition.TenantId,
            CreationTime = definition.CreationTime,
            PublishTime = definition.PublishTime
        };
    }

    /// <summary>
    /// 把实体转换为流程定义
    /// </summary>
    /// <param name="entity">定义实体</param>
    /// <returns>流程定义</returns>
    public static WorkflowDefinition ToDefinition(SysWorkflowDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new WorkflowDefinition
        {
            Id = entity.BasicId,
            Code = entity.Code,
            Name = entity.Name,
            Version = entity.Version,
            Description = entity.Description,
            Category = entity.Category,
            Status = (WorkflowDefinitionStatus)entity.Status,
            EnableCompensation = entity.EnableCompensation,
            Nodes = WorkflowJsonColumn.Deserialize<List<WorkflowNode>>(entity.NodesJson),
            Transitions = WorkflowJsonColumn.Deserialize<List<WorkflowTransition>>(entity.TransitionsJson),
            Variables = WorkflowJsonColumn.Deserialize<List<WorkflowVariableDefinition>>(entity.VariablesJson),
            ExtraProperties = WorkflowJsonColumn.Deserialize<Dictionary<string, string>>(entity.ExtraProperties),
            TenantId = entity.OwnerTenantId,
            CreationTime = entity.CreationTime,
            PublishTime = entity.PublishTime
        };
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。若 `字典键大小写保持不变` 失败，说明序列化选项被改过（设了 `DictionaryKeyPolicy`），回到 Step 3 核对，**不要**改断言。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增流程定义映射与 JSON 列工具"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 4: 执行器与测试夹具

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/WorkflowSqlSugarExecutor.cs`
- Modify: `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestDoubles.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowTestDatabase.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowSqlSugarExecutorTests.cs`
- Modify: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/ServiceRegistrationTests.cs`

**Interfaces:**
- Consumes: `ISqlSugarClientResolver`、`IUnitOfWorkManager`、`IOptions<XiHanSqlSugarCoreOptions>`、`IOptions<XiHanWorkflowSqlSugarOptions>`
- Produces:
  - `public sealed class WorkflowSqlSugarExecutor`：构造 `(ISqlSugarClientResolver clientResolver, IUnitOfWorkManager unitOfWorkManager, IOptions<XiHanSqlSugarCoreOptions> coreOptions, IOptions<XiHanWorkflowSqlSugarOptions> options)`；属性 `string ConfigId`；方法 `Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation, CancellationToken cancellationToken)`
  - 测试夹具 `WorkflowTestDatabase`：`const string ConfigId = "Default"`；工厂 `CreateSqlite()`、`CreateMySql(string connectionString)`；属性 `DbType`、`ConnectionString`、`Scope`、`UnitOfWorkManager`、`Resolver`、`Executor`；方法 `SqlSugarClient CreateProbeClient()`
  - 测试替身 `FixedTenantConnectionResolver`、`NoTenant`、`PassThroughConnectionConfigurator`、`StubModuleDataSourceConnectionResolver`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs` 的 `GetClient` 与 `EnlistCurrentUnitOfWork`
- `framework/src/XiHan.Framework.Uow/UnitOfWorkManager.cs:43-70`
- `framework/test/XiHan.Framework.Data.Tests/RequiresNewIsolationTests.cs`（夹具与替身照抄；注意其 remarks 讲的 SQLite 单写者限制）
- spec §4.2 与 §5 ①

**本任务禁止事项：** 硬约束 ①⑤。执行器里不 `new SqlSugarClient`、不 `CopyNew()`、不直接 `Ado.BeginTran()`。隔离用例里外层工作单元**不得**触碰同一个 SQLite 库。

- [ ] **Step 1: 创建测试替身**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/TestDoubles.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Routing;
using XiHan.Framework.Data.SqlSugar.Tenanting;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 固定返回同一连接配置标识的租户连接解析器
/// </summary>
internal sealed class FixedTenantConnectionResolver : ISqlSugarTenantConnectionResolver
{
    private readonly string _configId;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    public FixedTenantConnectionResolver(string configId)
    {
        _configId = configId;
    }

    /// <summary>
    /// 解析当前租户连接配置标识
    /// </summary>
    /// <returns>固定的连接配置标识</returns>
    public string ResolveCurrentConfigId()
    {
        return _configId;
    }

    /// <summary>
    /// 根据租户标识解析连接配置标识
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="tenantName">租户名称</param>
    /// <returns>固定的连接配置标识</returns>
    public string ResolveConfigId(long? tenantId, string? tenantName = null)
    {
        return _configId;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>只含固定标识的集合</returns>
    public IReadOnlyCollection<string> GetConfigIds()
    {
        return [_configId];
    }

    /// <summary>
    /// 获取全部模块数据源名
    /// </summary>
    /// <returns>空集合</returns>
    public IReadOnlyCollection<string> GetModuleDataSourceNames()
    {
        return [];
    }
}

/// <summary>
/// 无租户上下文替身
/// </summary>
internal sealed class NoTenant : ICurrentTenant
{
    /// <summary>
    /// 当前租户是否可用，恒为 false
    /// </summary>
    public bool IsAvailable => false;

    /// <summary>
    /// 当前租户标识，恒为 null
    /// </summary>
    public long? Id => null;

    /// <summary>
    /// 当前租户名称，恒为 null
    /// </summary>
    public string? Name => null;

    /// <summary>
    /// 切换租户，返回不做任何事的作用域
    /// </summary>
    /// <param name="id">租户标识</param>
    /// <param name="name">租户名称</param>
    /// <returns>空作用域</returns>
    public IDisposable Change(long? id, string? name = null)
    {
        return new NoopScope();
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// 不改写连接配置的配置器替身
/// </summary>
internal sealed class PassThroughConnectionConfigurator : ISqlSugarConnectionConfigurator
{
    /// <summary>
    /// 配置连接作用域，不做任何改写
    /// </summary>
    /// <param name="provider">连接作用域提供器</param>
    public void Configure(SqlSugarScopeProvider provider)
    {
    }

    /// <summary>
    /// 确保租户连接，本测试不涉及
    /// </summary>
    /// <param name="tenant">多连接容器</param>
    /// <param name="descriptor">租户连接描述符</param>
    /// <returns>不返回，始终抛出</returns>
    public SqlSugarScopeProvider EnsureTenantConnection(ITenant tenant, SqlSugarTenantConnection descriptor)
    {
        throw new NotSupportedException("工作流存储测试不涉及库隔离租户的动态连接。");
    }
}

/// <summary>
/// 模块数据源连接解析器替身，被调用即说明路由走错了分支
/// </summary>
internal sealed class StubModuleDataSourceConnectionResolver : IModuleDataSourceConnectionResolver
{
    /// <summary>
    /// 解析模块数据源客户端，本测试不涉及
    /// </summary>
    /// <param name="moduleDataSource">模块数据源名</param>
    /// <param name="parentConfigId">父连接配置标识</param>
    /// <returns>不返回，始终抛出</returns>
    public ISqlSugarClient ResolveClient(string moduleDataSource, string parentConfigId)
    {
        throw new InvalidOperationException("工作流存储不应触发模块数据源路由。");
    }
}
```

- [ ] **Step 2: 创建测试夹具**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowTestDatabase.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.Data.SqlSugar.Routing;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Abstracts;
using XiHan.Framework.Workflow.SqlSugar.Options;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 工作流存储测试夹具：一个库、真实的客户端解析器与工作单元管理器
/// </summary>
internal sealed class WorkflowTestDatabase : IDisposable
{
    /// <summary>
    /// 连接配置标识
    /// </summary>
    public const string ConfigId = "Default";

    private readonly ServiceProvider _serviceProvider;
    private readonly string? _sqliteFile;

    private WorkflowTestDatabase(DbType dbType, string connectionString, string? sqliteFile)
    {
        DbType = dbType;
        ConnectionString = connectionString;
        _sqliteFile = sqliteFile;

        Scope = new SqlSugarScope(
        [
            new ConnectionConfig
            {
                ConfigId = ConfigId,
                ConnectionString = connectionString,
                DbType = dbType,
                IsAutoCloseConnection = true
            }
        ]);
        Scope.GetConnectionScope(ConfigId).CodeFirst.InitTables(TestEntityTypes.All);

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkEventPublisher, NullUnitOfWorkEventPublisher>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        _serviceProvider = services.BuildServiceProvider();
        UnitOfWorkManager = _serviceProvider.GetRequiredService<IUnitOfWorkManager>();

        Resolver = new SqlSugarClientResolver(
            Scope,
            new FixedTenantConnectionResolver(ConfigId),
            new EntityModuleDataSourceResolver(),
            new StubModuleDataSourceConnectionResolver(),
            UnitOfWorkManager,
            new NoTenant(),
            new PassThroughConnectionConfigurator(),
            []);

        Executor = new WorkflowSqlSugarExecutor(
            Resolver,
            UnitOfWorkManager,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarCoreOptions()),
            Microsoft.Extensions.Options.Options.Create(new XiHanWorkflowSqlSugarOptions()));
    }

    /// <summary>
    /// 数据库类型
    /// </summary>
    public DbType DbType { get; }

    /// <summary>
    /// 连接串
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// 多连接容器
    /// </summary>
    public SqlSugarScope Scope { get; }

    /// <summary>
    /// 工作单元管理器
    /// </summary>
    public IUnitOfWorkManager UnitOfWorkManager { get; }

    /// <summary>
    /// 客户端解析器
    /// </summary>
    public SqlSugarClientResolver Resolver { get; }

    /// <summary>
    /// 被测执行器
    /// </summary>
    public WorkflowSqlSugarExecutor Executor { get; }

    /// <summary>
    /// 创建临时 SQLite 库
    /// </summary>
    /// <returns>测试夹具</returns>
    public static WorkflowTestDatabase CreateSqlite()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_workflow_{Guid.NewGuid():N}.db");

        // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
        return new WorkflowTestDatabase(DbType.Sqlite, $"DataSource={databaseFile};Pooling=False", databaseFile);
    }

    /// <summary>
    /// 连接真实 MySQL 库
    /// </summary>
    /// <param name="connectionString">连接串</param>
    /// <returns>测试夹具</returns>
    public static WorkflowTestDatabase CreateMySql(string connectionString)
    {
        return new WorkflowTestDatabase(DbType.MySql, connectionString, null);
    }

    /// <summary>
    /// 创建一条与夹具无关的新连接，只能看到已提交的数据
    /// </summary>
    /// <returns>探针客户端</returns>
    public SqlSugarClient CreateProbeClient()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConfigId = "Probe",
            ConnectionString = ConnectionString,
            DbType = DbType,
            IsAutoCloseConnection = true
        });
    }

    /// <summary>
    /// 释放连接并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        _serviceProvider.Dispose();
        Scope.Dispose();

        if (_sqliteFile is not null && File.Exists(_sqliteFile))
        {
            File.Delete(_sqliteFile);
        }
    }
}
```

- [ ] **Step 3: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/WorkflowSqlSugarExecutorTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.Uow.Options;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Options;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 执行器测试
/// </summary>
public class WorkflowSqlSugarExecutorTests : IDisposable
{
    private readonly WorkflowTestDatabase _database = WorkflowTestDatabase.CreateSqlite();

    /// <summary>
    /// 写入不随外层工作单元回滚
    /// </summary>
    [Fact]
    public async Task 写入不随外层工作单元回滚()
    {
        using (_database.UnitOfWorkManager.Begin(new XiHanUnitOfWorkOptions(isTransactional: true)))
        {
            await _database.Executor.ExecuteAsync(
                client => client.Insertable(NewDefinition("isolated")).ExecuteCommandAsync(CancellationToken.None),
                CancellationToken.None);

            // 外层不 Complete，随 Dispose 回滚
        }

        using var probe = _database.CreateProbeClient();
        Assert.Equal(1, probe.Queryable<SysWorkflowDefinition>().Count());
    }

    /// <summary>
    /// 操作抛出异常时写入回滚
    /// </summary>
    [Fact]
    public async Task 操作抛出异常时写入回滚()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.Executor.ExecuteAsync<int>(
            async client =>
            {
                await client.Insertable(NewDefinition("rollback")).ExecuteCommandAsync(CancellationToken.None);
                throw new InvalidOperationException("模拟失败");
            },
            CancellationToken.None));

        using var probe = _database.CreateProbeClient();
        Assert.Equal(0, probe.Queryable<SysWorkflowDefinition>().Count());
    }

    /// <summary>
    /// 已取消的令牌不执行操作
    /// </summary>
    [Fact]
    public async Task 已取消的令牌不执行操作()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var invoked = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _database.Executor.ExecuteAsync(
            client =>
            {
                invoked = true;
                return Task.FromResult(0);
            },
            cancellation.Token));

        Assert.False(invoked);
    }

    /// <summary>
    /// 连接配置标识默认取数据访问的默认连接
    /// </summary>
    [Fact]
    public void 连接配置标识默认取数据访问的默认连接()
    {
        var executor = CreateExecutor(configId: "  ");

        Assert.Equal("Default", executor.ConfigId);
    }

    /// <summary>
    /// 连接配置标识可由选项覆盖
    /// </summary>
    [Fact]
    public void 连接配置标识可由选项覆盖()
    {
        var executor = CreateExecutor(configId: " Workflow ");

        Assert.Equal("Workflow", executor.ConfigId);
    }

    /// <summary>
    /// 释放测试夹具
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private WorkflowSqlSugarExecutor CreateExecutor(string? configId)
    {
        return new WorkflowSqlSugarExecutor(
            _database.Resolver,
            _database.UnitOfWorkManager,
            Microsoft.Extensions.Options.Options.Create(new XiHanSqlSugarCoreOptions()),
            Microsoft.Extensions.Options.Options.Create(new XiHanWorkflowSqlSugarOptions { ConfigId = configId }));
    }

    private static SysWorkflowDefinition NewDefinition(string code)
    {
        return new SysWorkflowDefinition(Guid.NewGuid().ToString("N"))
        {
            Code = code,
            Name = code,
            Version = 1,
            CreationTime = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc)
        };
    }
}
```

`xUnit2013` 会对 `Assert.Equal(0, ...Count())` 这类「与 0/1 比较集合大小」报警，但这里比较的是 `int` 返回值而非集合，不触发。若本机仍报，改为 `Assert.Equal(0, count)` 先赋给局部变量。

在 `ServiceRegistrationTests` 类末尾（`选项从配置节绑定` 之后、类的右花括号之前）追加：

```csharp
    /// <summary>
    /// 执行器注册为作用域服务
    /// </summary>
    [Fact]
    public void 执行器注册为作用域服务()
    {
        var services = CreateServices(registerSqlSugarFirst: false);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(WorkflowSqlSugarExecutor));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static IServiceCollection CreateServices(bool registerSqlSugarFirst)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        if (registerSqlSugarFirst)
        {
            services.AddXiHanWorkflowSqlSugar(configuration);
            services.AddXiHanWorkflow(configuration);
        }
        else
        {
            services.AddXiHanWorkflow(configuration);
            services.AddXiHanWorkflowSqlSugar(configuration);
        }

        return services;
    }
```

之后所有新增的注册用例都插在 `CreateServices` 之前。在该文件 using 区追加：

```csharp
using XiHan.Framework.Workflow.Extensions.DependencyInjection;
using XiHan.Framework.Workflow.SqlSugar.Stores;
```

- [ ] **Step 4: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`WorkflowSqlSugarExecutor` 不存在。

- [ ] **Step 5: 创建执行器**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/WorkflowSqlSugarExecutor.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Options;
using XiHan.Framework.Workflow.SqlSugar.Options;

namespace XiHan.Framework.Workflow.SqlSugar.Stores;

/// <summary>
/// 工作流存储的数据库执行器
/// </summary>
/// <remarks>
/// 每次操作在一个新开的事务型工作单元内执行并在返回前提交，不加入调用方的工作单元；
/// 操作固定作用在 <see cref="ConfigId"/> 指向的连接上，与当前租户上下文无关。
/// </remarks>
public sealed class WorkflowSqlSugarExecutor
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="unitOfWorkManager">工作单元管理器</param>
    /// <param name="coreOptions">数据访问配置</param>
    /// <param name="options">工作流 SqlSugar 存储配置</param>
    public WorkflowSqlSugarExecutor(
        ISqlSugarClientResolver clientResolver,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<XiHanSqlSugarCoreOptions> coreOptions,
        IOptions<XiHanWorkflowSqlSugarOptions> options)
    {
        _clientResolver = clientResolver;
        _unitOfWorkManager = unitOfWorkManager;

        var configuredConfigId = options.Value.ConfigId;
        ConfigId = string.IsNullOrWhiteSpace(configuredConfigId)
            ? coreOptions.Value.DefaultConfigId
            : configuredConfigId.Trim();
    }

    /// <summary>
    /// 工作流数据表所在连接的配置标识
    /// </summary>
    public string ConfigId { get; }

    /// <summary>
    /// 在独立的事务型工作单元内执行一次数据库操作并提交
    /// </summary>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="operation">数据库操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<ISqlSugarClient, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        using var unitOfWork = _unitOfWorkManager.Begin(
            new XiHanUnitOfWorkOptions(isTransactional: true),
            requiresNew: true);

        var client = _clientResolver.GetClient(ConfigId);
        var result = await operation(client);

        await unitOfWork.CompleteAsync(cancellationToken);
        return result;
    }
}
```

- [ ] **Step 6: 注册执行器**

修改 `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`。

using 区追加：

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Workflow.SqlSugar.Stores;
```

把

```csharp
        services.Configure<XiHanWorkflowSqlSugarOptions>(
            configuration.GetSection(XiHanWorkflowSqlSugarOptions.SectionName));

        return services;
```

改为

```csharp
        services.Configure<XiHanWorkflowSqlSugarOptions>(
            configuration.GetSection(XiHanWorkflowSqlSugarOptions.SectionName));

        services.TryAddScoped<WorkflowSqlSugarExecutor>();

        return services;
```

- [ ] **Step 7: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

**若 `写入不随外层工作单元回滚` 失败且读到 0 行**，说明写入加入了外层事务——核对 Step 5 的 `requiresNew: true` 与 `isTransactional: true`，不要改断言。**若报 `database is locked`**，说明用例里的外层工作单元碰了同一个库，外层只能开、不能写。

- [ ] **Step 8: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 新增独立事务执行器"
```

提交信息正文写明取舍（注释里不写）：

```
工作流存储的每次读写都在 requiresNew 的事务型工作单元内执行并立即提交。
引擎靠实例级分布式锁串行化同一实例的推进，锁释放时写入必须已提交；
加入调用方工作单元会让写入推迟到请求结束才提交，另一节点在此之间拿到锁会读到旧状态。
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 5: 定义存储与注册替换

**Files:**
- Create: `framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowDefinitionStore.cs`
- Modify: `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`
- Create: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/SqlSugarWorkflowDefinitionStoreTests.cs`
- Modify: `framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/ServiceRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `WorkflowDefinitionMapper`、Task 4 的 `WorkflowSqlSugarExecutor`、契约 `IWorkflowDefinitionStore`
- Produces: `public class SqlSugarWorkflowDefinitionStore : IWorkflowDefinitionStore`，构造 `(WorkflowSqlSugarExecutor executor)`；注册 `services.Replace(ServiceDescriptor.Scoped<IWorkflowDefinitionStore, SqlSugarWorkflowDefinitionStore>())`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Workflow.Abstractions/Stores/IWorkflowDefinitionStore.cs`
- `framework/src/XiHan.Framework.Workflow/Stores/DefaultWorkflowDefinitionStore.cs`（语义基准）
- `framework/src/XiHan.Framework.Workflow/Definitions/WorkflowDefinitionManager.cs`（全部调用方）
- spec §4.6

**本任务禁止事项：** 硬约束 ①④。`GetMaxVersionAsync` 不用 `MaxAsync`。`GetListAsync` 的表达式里不出现 `status.Value`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/SqlSugarWorkflowDefinitionStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Workflow.Abstractions;
using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Stores;

namespace XiHan.Framework.Workflow.SqlSugar.Tests;

/// <summary>
/// 流程定义存储测试
/// </summary>
public class SqlSugarWorkflowDefinitionStoreTests : IDisposable
{
    private readonly WorkflowTestDatabase _database = WorkflowTestDatabase.CreateSqlite();
    private readonly SqlSugarWorkflowDefinitionStore _store;

    /// <summary>
    /// 构造函数
    /// </summary>
    public SqlSugarWorkflowDefinitionStoreTests()
    {
        _store = new SqlSugarWorkflowDefinitionStore(_database.Executor);
    }

    /// <summary>
    /// 插入后按标识可查到
    /// </summary>
    [Fact]
    public async Task 插入后按标识可查到()
    {
        var definition = NewDefinition("leave", 1);
        definition.Nodes.Add(new WorkflowNode { Id = "start", Name = "开始", ActivityType = WorkflowActivityTypes.Start });

        await _store.InsertAsync(definition);
        var found = await _store.FindAsync(definition.Id);

        Assert.NotNull(found);
        Assert.Equal("leave", found.Code);
        Assert.Equal("start", Assert.Single(found.Nodes).Id);
    }

    /// <summary>
    /// 标识不存在时返回空
    /// </summary>
    [Fact]
    public async Task 标识不存在时返回空()
    {
        Assert.Null(await _store.FindAsync("missing"));
    }

    /// <summary>
    /// 按编码与版本查找
    /// </summary>
    [Fact]
    public async Task 按编码与版本查找()
    {
        await _store.InsertAsync(NewDefinition("leave", 1));
        var second = NewDefinition("leave", 2);
        await _store.InsertAsync(second);

        var found = await _store.FindByVersionAsync("leave", 2);

        Assert.NotNull(found);
        Assert.Equal(second.Id, found.Id);
        Assert.Null(await _store.FindByVersionAsync("leave", 3));
    }

    /// <summary>
    /// 最新已发布版本忽略更高的草稿
    /// </summary>
    [Fact]
    public async Task 最新已发布版本忽略更高的草稿()
    {
        await _store.InsertAsync(NewDefinition("leave", 1, WorkflowDefinitionStatus.Published));
        var published = NewDefinition("leave", 2, WorkflowDefinitionStatus.Published);
        await _store.InsertAsync(published);
        await _store.InsertAsync(NewDefinition("leave", 3, WorkflowDefinitionStatus.Draft));
        await _store.InsertAsync(NewDefinition("other", 9, WorkflowDefinitionStatus.Published));

        var latest = await _store.FindLatestPublishedAsync("leave");

        Assert.NotNull(latest);
        Assert.Equal(published.Id, latest.Id);
        Assert.Null(await _store.FindLatestPublishedAsync("missing"));
    }

    /// <summary>
    /// 编码不存在时最大版本为零
    /// </summary>
    [Fact]
    public async Task 编码不存在时最大版本为零()
    {
        Assert.Equal(0, await _store.GetMaxVersionAsync("missing"));
    }

    /// <summary>
    /// 最大版本取编码下的最大值
    /// </summary>
    [Fact]
    public async Task 最大版本取编码下的最大值()
    {
        await _store.InsertAsync(NewDefinition("leave", 1));
        await _store.InsertAsync(NewDefinition("leave", 5));
        await _store.InsertAsync(NewDefinition("other", 9));

        Assert.Equal(5, await _store.GetMaxVersionAsync("leave"));
    }

    /// <summary>
    /// 列表按编码升序版本降序并按条件过滤
    /// </summary>
    [Fact]
    public async Task 列表按编码升序版本降序并按条件过滤()
    {
        await _store.InsertAsync(NewDefinition("b", 1, WorkflowDefinitionStatus.Published));
        await _store.InsertAsync(NewDefinition("a", 1, WorkflowDefinitionStatus.Published));
        await _store.InsertAsync(NewDefinition("a", 2, WorkflowDefinitionStatus.Draft));

        var all = await _store.GetListAsync();
        Assert.Equal(["a:2", "a:1", "b:1"], all.Select(item => $"{item.Code}:{item.Version}"));

        var onlyA = await _store.GetListAsync(code: "a");
        Assert.Equal([2, 1], onlyA.Select(item => item.Version));

        var published = await _store.GetListAsync(status: WorkflowDefinitionStatus.Published);
        Assert.Equal(["a:1", "b:1"], published.Select(item => $"{item.Code}:{item.Version}"));
    }

    /// <summary>
    /// 更新写回全部字段包括清空的可空字段
    /// </summary>
    [Fact]
    public async Task 更新写回全部字段包括清空的可空字段()
    {
        var definition = NewDefinition("leave", 1, WorkflowDefinitionStatus.Published);
        definition.Description = "旧描述";
        await _store.InsertAsync(definition);

        definition.Status = WorkflowDefinitionStatus.Disabled;
        definition.Description = null;
        definition.PublishTime = null;
        definition.Nodes.Add(new WorkflowNode { Id = "end", Name = "结束", ActivityType = WorkflowActivityTypes.End });
        await _store.UpdateAsync(definition);

        var found = await _store.FindAsync(definition.Id);
        Assert.NotNull(found);
        Assert.Equal(WorkflowDefinitionStatus.Disabled, found.Status);
        Assert.Null(found.Description);
        Assert.Null(found.PublishTime);
        Assert.Equal("end", Assert.Single(found.Nodes).Id);
    }

    /// <summary>
    /// 更新不存在的标识不新建行
    /// </summary>
    [Fact]
    public async Task 更新不存在的标识不新建行()
    {
        await _store.UpdateAsync(NewDefinition("ghost", 1));

        using var probe = _database.CreateProbeClient();
        Assert.Equal(0, probe.Queryable<SysWorkflowDefinition>().Count());
    }

    /// <summary>
    /// 重复主键插入抛出异常
    /// </summary>
    [Fact]
    public async Task 重复主键插入抛出异常()
    {
        var definition = NewDefinition("leave", 1);
        await _store.InsertAsync(definition);

        definition.Version = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => _store.InsertAsync(definition));
    }

    /// <summary>
    /// 同编码同版本插入抛出异常
    /// </summary>
    [Fact]
    public async Task 同编码同版本插入抛出异常()
    {
        await _store.InsertAsync(NewDefinition("leave", 1));

        await Assert.ThrowsAnyAsync<Exception>(() => _store.InsertAsync(NewDefinition("leave", 1)));
    }

    /// <summary>
    /// 删除后查不到
    /// </summary>
    [Fact]
    public async Task 删除后查不到()
    {
        var definition = NewDefinition("leave", 1);
        await _store.InsertAsync(definition);

        await _store.DeleteAsync(definition.Id);

        Assert.Null(await _store.FindAsync(definition.Id));
    }

    /// <summary>
    /// 释放测试夹具
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private static WorkflowDefinition NewDefinition(
        string code,
        int version,
        WorkflowDefinitionStatus status = WorkflowDefinitionStatus.Draft)
    {
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Code = code,
            Name = code,
            Version = version,
            Status = status,
            CreationTime = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc),
            PublishTime = status == WorkflowDefinitionStatus.Published
                ? new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc)
                : null
        };
    }
}
```

在 `ServiceRegistrationTests` 类末尾（`CreateServices` 之前）追加：

```csharp
    /// <summary>
    /// 定义存储被替换为作用域实现
    /// </summary>
    /// <param name="registerSqlSugarFirst">是否先注册本包</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 定义存储被替换为作用域实现(bool registerSqlSugarFirst)
    {
        var services = CreateServices(registerSqlSugarFirst);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IWorkflowDefinitionStore));
        Assert.Equal(typeof(SqlSugarWorkflowDefinitionStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
```

并在该文件 using 区追加：

```csharp
using XiHan.Framework.Workflow.Abstractions.Stores;
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarWorkflowDefinitionStore` 不存在。

- [ ] **Step 3: 创建定义存储**

`framework/src/XiHan.Framework.Workflow.SqlSugar/Stores/SqlSugarWorkflowDefinitionStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Workflow.Abstractions.Definitions;
using XiHan.Framework.Workflow.Abstractions.Stores;
using XiHan.Framework.Workflow.SqlSugar.Entities;
using XiHan.Framework.Workflow.SqlSugar.Mapping;

namespace XiHan.Framework.Workflow.SqlSugar.Stores;

/// <summary>
/// SqlSugar 流程定义存储
/// </summary>
public class SqlSugarWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly WorkflowSqlSugarExecutor _executor;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="executor">工作流存储的数据库执行器</param>
    public SqlSugarWorkflowDefinitionStore(WorkflowSqlSugarExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// 按标识查找定义
    /// </summary>
    /// <param name="id">定义标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>定义（不存在返回 null）</returns>
    public async Task<WorkflowDefinition?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowDefinition>()
                .Where(item => item.BasicId == id)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return entities.Count == 0 ? null : WorkflowDefinitionMapper.ToDefinition(entities[0]);
    }

    /// <summary>
    /// 按编码和版本查找定义
    /// </summary>
    /// <param name="code">流程编码</param>
    /// <param name="version">版本号</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>定义（不存在返回 null）</returns>
    public async Task<WorkflowDefinition?> FindByVersionAsync(string code, int version, CancellationToken cancellationToken = default)
    {
        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowDefinition>()
                .Where(item => item.Code == code && item.Version == version)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return entities.Count == 0 ? null : WorkflowDefinitionMapper.ToDefinition(entities[0]);
    }

    /// <summary>
    /// 查找编码下最新的已发布定义
    /// </summary>
    /// <param name="code">流程编码</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>定义（不存在返回 null）</returns>
    public async Task<WorkflowDefinition?> FindLatestPublishedAsync(string code, CancellationToken cancellationToken = default)
    {
        const int published = (int)WorkflowDefinitionStatus.Published;

        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowDefinition>()
                .Where(item => item.Code == code && item.Status == published)
                .OrderBy(item => item.Version, OrderByType.Desc)
                .Take(1)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return entities.Count == 0 ? null : WorkflowDefinitionMapper.ToDefinition(entities[0]);
    }

    /// <summary>
    /// 获取编码下的最大版本号
    /// </summary>
    /// <param name="code">流程编码</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>最大版本号（编码不存在返回 0）</returns>
    public async Task<int> GetMaxVersionAsync(string code, CancellationToken cancellationToken = default)
    {
        var versions = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowDefinition>()
                .Where(item => item.Code == code)
                .OrderBy(item => item.Version, OrderByType.Desc)
                .Select(item => item.Version)
                .Take(1)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return versions.Count == 0 ? 0 : versions[0];
    }

    /// <summary>
    /// 查询定义列表
    /// </summary>
    /// <param name="code">流程编码（为空表示不过滤）</param>
    /// <param name="status">状态（为空表示不过滤）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>定义列表（按编码升序、版本降序）</returns>
    public async Task<List<WorkflowDefinition>> GetListAsync(
        string? code = null,
        WorkflowDefinitionStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var statusValue = (int)(status ?? default);

        var entities = await _executor.ExecuteAsync(
            client => client.Queryable<SysWorkflowDefinition>()
                .WhereIF(code is not null, item => item.Code == code)
                .WhereIF(status is not null, item => item.Status == statusValue)
                .OrderBy(item => item.Code)
                .OrderBy(item => item.Version, OrderByType.Desc)
                .ToListAsync(cancellationToken),
            cancellationToken);

        return [.. entities.Select(WorkflowDefinitionMapper.ToDefinition)];
    }

    /// <summary>
    /// 插入定义
    /// </summary>
    /// <param name="definition">定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task InsertAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowDefinitionMapper.ToEntity(definition);

        await _executor.ExecuteAsync(
            client => client.Insertable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 更新定义，标识不存在时不做任何事
    /// </summary>
    /// <param name="definition">定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task UpdateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        var entity = WorkflowDefinitionMapper.ToEntity(definition);

        await _executor.ExecuteAsync(
            client => client.Updateable(entity).ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 删除定义
    /// </summary>
    /// <param name="id">定义标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _executor.ExecuteAsync(
            client => client.Deleteable<SysWorkflowDefinition>()
                .Where(item => item.BasicId == id)
                .ExecuteCommandAsync(cancellationToken),
            cancellationToken);
    }
}
```

- [ ] **Step 4: 注册替换**

修改 `framework/src/XiHan.Framework.Workflow.SqlSugar/Extensions/DependencyInjection/XiHanWorkflowSqlSugarServiceCollectionExtensions.cs`。

using 区追加：

```csharp
using XiHan.Framework.Workflow.Abstractions.Stores;
```

把

```csharp
        services.TryAddScoped<WorkflowSqlSugarExecutor>();

        return services;
```

改为

```csharp
        services.TryAddScoped<WorkflowSqlSugarExecutor>();
        services.Replace(ServiceDescriptor.Scoped<IWorkflowDefinitionStore, SqlSugarWorkflowDefinitionStore>());

        return services;
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

**若 `更新不存在的标识不新建行` 失败**，说明 `UpdateAsync` 被写成了 upsert（硬约束 ④）。**若 `更新写回全部字段包括清空的可空字段` 失败于 `Description`/`PublishTime`**，说明更新忽略了空值列。两者都改实现，不改断言。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Workflow.SqlSugar framework/test/XiHan.Framework.Workflow.SqlSugar.Tests
git commit -m "feat(workflow-sqlsugar): 以 SqlSugar 实现替换流程定义存储"
```

预期：**0 Warning(s) 0 Error(s)**。

---

### Task 6: 本份验收

**Files:** 无新增（只验证）

- [ ] **Step 1: 全量构建与测试**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；测试全绿（已知抖动 `MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 除外）。

- [ ] **Step 2: 拿掉关键条件，确认测试变红**

临时把 `Stores/WorkflowSqlSugarExecutor.cs` 里的 `requiresNew: true` 改成 `requiresNew: false`，运行：

```bash
dotnet test --project framework/test/XiHan.Framework.Workflow.SqlSugar.Tests/XiHan.Framework.Workflow.SqlSugar.Tests.csproj -c Release
```

预期：`写入不随外层工作单元回滚` **FAIL**（读到 0 行）。若它仍然通过，说明夹具没有走真实的解析器登记路径——停下来排查夹具，不要继续。

确认变红后改回 `requiresNew: true`，重跑确认全绿，并用 `git diff` 确认执行器没有残留改动。

- [ ] **Step 3: 注释复查**

通读本计划新建的每个 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

---

## 完成标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `IWorkflowDefinitionStore` 只有一条描述符，实现为 `SqlSugarWorkflowDefinitionStore`，Scoped；两种注册顺序结果相同
- 所有工作流实体不实现 `IMultiTenantEntity`、没有 `TenantId` 属性、只在平台库建表、主键为 `string`、索引名带 `{table}`
- 外层事务型工作单元回滚不撤销存储写入；改成 `requiresNew: false` 时该用例变红
- 8 个方法的语义与 `DefaultWorkflowDefinitionStore` 一致；`UpdateAsync` 不新建行；重复主键与重复 `(Code, Version)` 插入抛异常

## 已知边界（写入 PR 描述，不写进代码注释）

- **多实例应上 Redis 锁**：默认 `DefaultDistributedLock` 只在进程内互斥；第 3 份的书签删除守卫保证同一书签只推进一次，但不同书签的并发恢复仍会最后写入覆盖；第 3 份在启动时记录警告
- **工作流写入不随业务回滚**：业务事务里启动流程后业务回滚，流程实例仍在
- **SQLite + 外层事务**：外层工作单元已写过同一个 SQLite 库时，存储的独立连接会撞 `database is locked`
- **并发创建同编码定义**：唯一索引让后到者抛数据库异常，调用方需重试
- **插入与更新不是 upsert**：与默认内存实现不同
- **表只建在平台库**：`ConfigId` 指向租户独立库不受支持
- **每次操作一个事务**：有外层工作单元时每次存储调用各占一条物理连接
- **编码比较随排序规则**：MySQL 默认不区分大小写，`Leave` 与 `leave` 视为同一编码

## 下一份计划

第 2 份（`.superpowers/plans/2026-09-28-workflow-sqlsugar-2-instance.md`）：实例与节点实例两个实体、`SqlSugarWorkflowInstanceStore` 的 10 个方法、节点实例执行顺序的持久化，以及 MySQL 上的隔离可见性用例。
