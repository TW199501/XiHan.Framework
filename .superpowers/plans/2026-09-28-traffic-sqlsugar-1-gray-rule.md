# Traffic.SqlSugar 灰度规则仓储（1）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `XiHan.Framework.Traffic.SqlSugar` 包，为 `IGrayRuleRepository` 提供 SqlSugar 落库实现，内存缓存 + 到期刷新，避免路由热路径每次决策都查库；返回的规则对象运行时类型必须是 `GrayRule`。

**Architecture:** 单实体 `SysGrayRule`（主键为应用层赋值的 `string RuleId`，`SugarEntity<string>`）+ 纯静态映射 `GrayRuleMapper`（`SysGrayRule ↔ GrayRule`）+ 单个仓储类 `SqlSugarGrayRuleRepository`（Singleton，经 `IServiceScopeFactory` 按需开 Scoped 作用域刷新缓存）。仓储只读，写入由应用层直接对 `SysGrayRule` 表操作。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-traffic-sqlsugar-1-gray-rule-design.md`

> 该 spec 自成一体，实现本计划所需的全部约束都在其中。

**Linear 议题**：https://linear.app/elf-express/issue/EDDIE-12

**前置：** 无。`XiHan.Framework.Traffic`、`XiHan.Framework.Data` 均已发布，可直接引用。

---

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：实现从 `dev` 开 worktree：

```bash
git worktree add ../XiHan.Framework-traffic -b feat/traffic-sqlsugar dev
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

**SqlSugar 签名只信源码**：权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）。当前引用版本 `SqlSugarCore 5.1.4.221`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar
ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
ISugarQueryable<T>.ToListAsync(CancellationToken token = default)
IInsertable<T>.ExecuteCommandAsync(CancellationToken token = default)

// 框架（XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver，Scoped 注册）
ISqlSugarClient GetClientForEntity<TEntity>()

// 框架（XiHan.Framework.Traffic.Extensions.DependencyInjection.XiHanTrafficServiceCollectionExtensions，既有扩展，本计划直接调用不重写）
IServiceCollection ReplaceGrayRuleRepository<TRepository>(this IServiceCollection services)
    where TRepository : class, IGrayRuleRepository
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀
- `public` 成员必须有 `<summary>`

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：**没有可用的筛选参数**，**不要带 `--logger trx` / `--results-directory`**。要跑单个测试类就整个项目跑：

```bash
dotnet test --project framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj -c Release
```

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，不 Import `version.props`、不设 `AssemblyName`。

**SQLite 临时库**：连接串必须带 `Pooling=False`。

**构建环境坑**：`MSB3027` / `MSB3021` 是残留测试进程占用输出文件，`taskkill //F //IM "<name>.exe"` 后重建，不是代码问题。

**已知的无关抖动**：`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发红，与本计划无关。

**提交信息**：中文 Conventional Commits，作用域 `traffic-sqlsugar`。**不加任何 AI 署名。**

**建表**：`EnableDbInitialization` 与 `EnableTableInitialization` 默认都是 `false`，不开启则不会自动建表——写进包 README。

---

## 本计划特有的三条硬约束

**① `IGrayRuleRepository` 是 Singleton，`ISqlSugarClientResolver` 是 Scoped，不能直接构造函数注入。**

`SqlSugarGrayRuleRepository` 必须注入 `IServiceScopeFactory`，每次刷新时 `using var scope = _scopeFactory.CreateScope();` 取该 scope 内的 `ISqlSugarClientResolver`。直接注入 `ISqlSugarClientResolver` 会在 `validateScopes: true` 下于容器构建期直接报错。

**② `GetEnabledRulesAsync` 返回的元素运行时类型必须是 `GrayRule`。**

`GrayRuleMapper.ToModel` 必须 `new GrayRule { ... }` 返回具体类，不能自定义一个只实现 `IGrayRule` 的类型。`DefaultGrayRuleEngine.DecideAsync` 依赖 `(rule as GrayRule)?.TargetVersion` 取目标版本，类型不对会静默丢失该值，没有任何异常。Task 3 的测试必须显式断言 `is GrayRule` 并读取 `TargetVersion`。

**③ 本仓储不提供任何写方法。**

不要给 `SqlSugarGrayRuleRepository` 加 `AddRuleAsync`/`RemoveRuleAsync` 之类的公开方法——即便只是"方便测试"。测试直接对 `SqlSugarClient.Insertable(entity)` 操作数据库，不经仓储写入，这与"应用层直接管理"的设计定位一致（见 spec §1.1、§3）。

---

## File Structure

```
framework/src/XiHan.Framework.Traffic.SqlSugar/
  XiHan.Framework.Traffic.SqlSugar.csproj
  XiHanTrafficSqlSugarModule.cs
  README.md
  Entities/
    SysGrayRule.cs
  Mapping/
    GrayRuleMapper.cs
  Options/
    XiHanTrafficSqlSugarOptions.cs
  Extensions/DependencyInjection/
    XiHanTrafficSqlSugarServiceCollectionExtensions.cs
  Repositories/
    SqlSugarGrayRuleRepository.cs

framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/
  XiHan.Framework.Traffic.SqlSugar.Tests.csproj
  EntityMappingTests.cs
  GrayRuleRepositoryTests.cs
```

---

### Task 1: 包骨架与解决方案注册

**Files:**
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/XiHan.Framework.Traffic.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/XiHanTrafficSqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/README.md`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: 无
- Produces: 程序集 `XiHan.Framework.Traffic.SqlSugar`；模块类型 `XiHanTrafficSqlSugarModule`

**参考来源（动手前先读）：**
- csproj / 模块类范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/`
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`

**本任务禁止事项：** 不要写任何服务注册逻辑，本任务的模块类是空装配。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.Traffic.SqlSugar/XiHan.Framework.Traffic.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Traffic.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Traffic.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Traffic.SqlSugar</PackageId>
        <Description>曦寒框架流量治理 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Traffic\XiHan.Framework.Traffic.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建模块类**

`framework/src/XiHan.Framework.Traffic.SqlSugar/XiHanTrafficSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Traffic;

namespace XiHan.Framework.Traffic.SqlSugar;

/// <summary>
/// 曦寒框架流量治理 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanTrafficSqlSugarModule))]</c> 即启用。
/// 本模块提供灰度规则的 SqlSugar 只读仓储，替换主包的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanTrafficModule),
    typeof(XiHanDataModule)
)]
public class XiHanTrafficSqlSugarModule : XiHanModule
{
}
```

- [ ] **Step 3: 注册进解决方案**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、`XiHan.Framework.Traffic` 之后插入：

```xml
    <Project Path="src/XiHan.Framework.Traffic.SqlSugar/XiHan.Framework.Traffic.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README**

`framework/src/XiHan.Framework.Traffic.SqlSugar/README.md`：

```markdown
# XiHan.Framework.Traffic.SqlSugar

## 概述

`XiHan.Framework.Traffic` 的 SqlSugar 持久化提供程序，为灰度规则提供只读落库查询与内存缓存。

## 核心能力

- `IGrayRuleRepository` 的 SqlSugar 只读实现：内存缓存 + 到期自动刷新 + 显式 `RefreshAsync`
- 表结构由 `DbInitializer` 在应用启动时创建（需开启 `EnableTableInitialization`）
- 仓储本身不提供写方法，规则的增删改由应用层直接对 `sys_gray_rule` 表操作

## 依赖关系

依赖 `XiHan.Framework.Traffic`（契约与规则模型）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

配置节 `XiHan:Traffic:SqlSugar`，`RefreshInterval` 控制缓存刷新间隔，默认 30 秒。

表名 `sys_gray_rule`；主键 `Basic_Id` 即规则的 `RuleId`（`string`，由应用层赋值，非自增）。不分表、不做多库路由。

`EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`，不开启则不会自动建表。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanTrafficSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

## 扩展点

注册经 `XiHan.Framework.Traffic` 既有的 `ReplaceGrayRuleRepository<TRepository>()` 完成，顶替默认的内存实现。多实例部署下每个实例各自独立刷新缓存，规则变更最坏情况下需要等到一个 `RefreshInterval` 才能在所有实例生效。

## 目录结构

```
Entities/        灰度规则实体
Mapping/         实体与模型的双向映射
Options/         缓存刷新间隔配置
Repositories/    只读仓储实现
```
```

- [ ] **Step 5: 验证构建 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Traffic.SqlSugar framework/XiHan.Framework.slnx
git commit -m "feat(traffic-sqlsugar): 新增包骨架与模块装配"
```

---

### Task 2: 测试项目、实体与映射

**Files:**
- Create: `framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/EntityMappingTests.cs`
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/Entities/SysGrayRule.cs`
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/Mapping/GrayRuleMapper.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: `XiHan.Framework.Traffic.GrayRouting.Models.GrayRule`、`XiHan.Framework.Traffic.GrayRouting.Enums.GrayRuleType`
- Produces:
  - `SysGrayRule`（`XiHan.Framework.Traffic.SqlSugar.Entities`），继承 `SugarEntity<string>`；公开构造函数两个——`SysGrayRule()` 与 `SysGrayRule(string basicId)`
  - `public static class GrayRuleMapper` 含 `ToModel(SysGrayRule) -> GrayRule` 与 `ToEntity(GrayRule) -> SysGrayRule`

**参考来源（动手前先读）：**
- 基类：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs`
- 模型字段：`framework/src/XiHan.Framework.Traffic/GrayRouting/Models/GrayRule.cs`
- 实体形状范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysOperationLog.cs`

**本任务禁止事项：** 不要加 `[SplitTable]`。不要把 `BasicId` 设为自增。`Configuration` 列不要用固定 `Length`，用 `ColumnDataType = StaticConfig.CodeFirst_BigString`。

- [ ] **Step 1: 创建测试项目**

`framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Traffic.SqlSugar\XiHan.Framework.Traffic.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

在 `framework/XiHan.Framework.slnx` 的测试文件夹内、既有的 SqlSugar 测试项目之后插入：

```xml
    <Project Path="test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/EntityMappingTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Traffic.GrayRouting.Enums;
using XiHan.Framework.Traffic.GrayRouting.Models;
using XiHan.Framework.Traffic.SqlSugar.Entities;
using XiHan.Framework.Traffic.SqlSugar.Mapping;

namespace XiHan.Framework.Traffic.SqlSugar.Tests;

/// <summary>
/// 灰度规则实体与映射测试
/// </summary>
public class EntityMappingTests
{
    /// <summary>
    /// 表名符合约定
    /// </summary>
    [Fact]
    public void 表名符合约定()
    {
        var table = typeof(SysGrayRule).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_gray_rule", table.TableName);
    }

    /// <summary>
    /// 主键经构造函数传入且非自增
    /// </summary>
    [Fact]
    public void 主键经构造函数传入且非自增()
    {
        var entity = new SysGrayRule("rule-1");
        var property = typeof(SysGrayRule).GetProperty(nameof(SysGrayRule.BasicId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.Equal("rule-1", entity.BasicId);
        Assert.NotNull(column);
        Assert.False(column.IsIdentity);
    }

    /// <summary>
    /// 往返映射保留全部字段，含四个时间字段
    /// </summary>
    [Fact]
    public void 往返映射保留全部字段()
    {
        var effectiveTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var expiryTime = effectiveTime.AddDays(7);
        var model = new GrayRule
        {
            RuleId = "rule-1",
            RuleName = "百分比灰度",
            RuleType = GrayRuleType.Percentage,
            IsEnabled = true,
            Priority = 10,
            TargetVersion = "2.0.0",
            TargetServiceId = "order-service",
            Configuration = "{\"percentage\":10}",
            EffectiveTime = effectiveTime,
            ExpiryTime = expiryTime,
            CreatedTime = effectiveTime,
            UpdatedTime = effectiveTime,
            Remark = "备注"
        };

        var entity = GrayRuleMapper.ToEntity(model);
        var roundTrip = GrayRuleMapper.ToModel(entity);

        Assert.Equal(model.RuleId, roundTrip.RuleId);
        Assert.Equal(model.RuleName, roundTrip.RuleName);
        Assert.Equal(model.RuleType, roundTrip.RuleType);
        Assert.Equal(model.IsEnabled, roundTrip.IsEnabled);
        Assert.Equal(model.Priority, roundTrip.Priority);
        Assert.Equal(model.TargetVersion, roundTrip.TargetVersion);
        Assert.Equal(model.TargetServiceId, roundTrip.TargetServiceId);
        Assert.Equal(model.Configuration, roundTrip.Configuration);
        Assert.Equal(model.Remark, roundTrip.Remark);
        Assert.Equal(effectiveTime, roundTrip.EffectiveTime);
        Assert.Equal(expiryTime, roundTrip.ExpiryTime);
        Assert.Equal(effectiveTime, roundTrip.CreatedTime);
        Assert.Equal(effectiveTime, roundTrip.UpdatedTime);
    }

    /// <summary>
    /// 未标注时区的时间按 UTC 解释，往返后保持不变
    /// </summary>
    /// <remarks>
    /// 断言 <see cref="DateTimeKind.Unspecified"/> 输入按 UTC 解释后往返，期望值固定为
    /// <see cref="DateTime.SpecifyKind(DateTime, DateTimeKind)"/> 转换后的结果。
    /// </remarks>
    [Fact]
    public void 未标注时区的时间按UTC解释往返后保持不变()
    {
        var unspecified = new DateTime(2026, 9, 28, 10, 30, 0, DateTimeKind.Unspecified);
        var model = new GrayRule
        {
            RuleId = "rule-2",
            RuleName = "未标注时区",
            CreatedTime = unspecified,
            UpdatedTime = unspecified,
            EffectiveTime = unspecified,
            ExpiryTime = unspecified
        };

        var entity = GrayRuleMapper.ToEntity(model);
        var roundTrip = GrayRuleMapper.ToModel(entity);

        var expectedUtc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);

        Assert.Equal(expectedUtc, roundTrip.CreatedTime);
        Assert.Equal(expectedUtc, roundTrip.UpdatedTime);
        Assert.Equal(expectedUtc, roundTrip.EffectiveTime);
        Assert.Equal(expectedUtc, roundTrip.ExpiryTime);
        Assert.Equal(DateTimeKind.Utc, roundTrip.CreatedTime.Kind);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysGrayRule`、`GrayRuleMapper` 不存在。

- [ ] **Step 4: 实现实体**

`framework/src/XiHan.Framework.Traffic.SqlSugar/Entities/SysGrayRule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Traffic.SqlSugar.Entities;

/// <summary>
/// 灰度规则实体
/// </summary>
[SugarTable("sys_gray_rule")]
public class SysGrayRule : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysGrayRule() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键，即规则标识</param>
    public SysGrayRule(string basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 规则名称
    /// </summary>
    [SugarColumn(ColumnName = "Rule_Name", Length = 128, IsNullable = false, ColumnDescription = "规则名称")]
    public string RuleName { get; set; } = string.Empty;

    /// <summary>
    /// 规则类型
    /// </summary>
    [SugarColumn(ColumnName = "Rule_Type", IsNullable = false, ColumnDescription = "规则类型")]
    public int RuleType { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [SugarColumn(ColumnName = "Is_Enabled", IsNullable = false, ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 优先级
    /// </summary>
    [SugarColumn(ColumnName = "Priority", IsNullable = false, ColumnDescription = "优先级")]
    public int Priority { get; set; }

    /// <summary>
    /// 目标版本
    /// </summary>
    [SugarColumn(ColumnName = "Target_Version", Length = 64, IsNullable = true, ColumnDescription = "目标版本")]
    public string? TargetVersion { get; set; }

    /// <summary>
    /// 目标服务标识
    /// </summary>
    [SugarColumn(ColumnName = "Target_Service_Id", Length = 128, IsNullable = true, ColumnDescription = "目标服务标识")]
    public string? TargetServiceId { get; set; }

    /// <summary>
    /// 规则配置（JSON 格式）
    /// </summary>
    [SugarColumn(ColumnName = "Configuration", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "规则配置")]
    public string? Configuration { get; set; }

    /// <summary>
    /// 生效时间
    /// </summary>
    [SugarColumn(ColumnName = "Effective_Time", IsNullable = true, ColumnDescription = "生效时间")]
    public DateTimeOffset? EffectiveTime { get; set; }

    /// <summary>
    /// 失效时间
    /// </summary>
    [SugarColumn(ColumnName = "Expiry_Time", IsNullable = true, ColumnDescription = "失效时间")]
    public DateTimeOffset? ExpiryTime { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "创建时间")]
    public DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    [SugarColumn(ColumnName = "Updated_Time", IsNullable = true, ColumnDescription = "更新时间")]
    public DateTimeOffset? UpdatedTime { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    [SugarColumn(ColumnName = "Remark", Length = 512, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }
}
```

- [ ] **Step 5: 实现映射器**

`framework/src/XiHan.Framework.Traffic.SqlSugar/Mapping/GrayRuleMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Traffic.GrayRouting.Enums;
using XiHan.Framework.Traffic.GrayRouting.Models;
using XiHan.Framework.Traffic.SqlSugar.Entities;

namespace XiHan.Framework.Traffic.SqlSugar.Mapping;

/// <summary>
/// 灰度规则实体与模型的映射
/// </summary>
public static class GrayRuleMapper
{
    /// <summary>
    /// 把实体转换为模型
    /// </summary>
    /// <param name="entity">灰度规则实体</param>
    /// <returns>灰度规则模型</returns>
    public static GrayRule ToModel(SysGrayRule entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new GrayRule
        {
            RuleId = entity.BasicId,
            RuleName = entity.RuleName,
            RuleType = (GrayRuleType)entity.RuleType,
            IsEnabled = entity.IsEnabled,
            Priority = entity.Priority,
            TargetVersion = entity.TargetVersion,
            TargetServiceId = entity.TargetServiceId,
            Configuration = entity.Configuration,
            EffectiveTime = entity.EffectiveTime?.UtcDateTime,
            ExpiryTime = entity.ExpiryTime?.UtcDateTime,
            CreatedTime = entity.CreatedTime.UtcDateTime,
            UpdatedTime = entity.UpdatedTime?.UtcDateTime,
            Remark = entity.Remark
        };
    }

    /// <summary>
    /// 把模型转换为实体
    /// </summary>
    /// <param name="model">灰度规则模型</param>
    /// <returns>灰度规则实体</returns>
    public static SysGrayRule ToEntity(GrayRule model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new SysGrayRule(model.RuleId)
        {
            RuleName = model.RuleName,
            RuleType = (int)model.RuleType,
            IsEnabled = model.IsEnabled,
            Priority = model.Priority,
            TargetVersion = model.TargetVersion,
            TargetServiceId = model.TargetServiceId,
            Configuration = model.Configuration,
            EffectiveTime = ToUtcOffset(model.EffectiveTime),
            ExpiryTime = ToUtcOffset(model.ExpiryTime),
            CreatedTime = ToUtcOffset(model.CreatedTime),
            UpdatedTime = ToUtcOffset(model.UpdatedTime),
            Remark = model.Remark
        };
    }

    /// <summary>
    /// 把未标注时区的时间按 UTC 解释后转换为 DateTimeOffset
    /// </summary>
    /// <remarks>
    /// 把 <paramref name="value"/> 的 <see cref="DateTime.Kind"/> 统一视为 <see cref="DateTimeKind.Utc"/>
    /// 后再转换为 <see cref="DateTimeOffset"/>，忽略原有的 <see cref="DateTime.Kind"/> 标注。
    /// </remarks>
    /// <param name="value">原始时间</param>
    /// <returns>按 UTC 解释后的时间</returns>
    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>
    /// 把可空的未标注时区时间按 UTC 解释后转换为 DateTimeOffset
    /// </summary>
    /// <param name="value">原始时间</param>
    /// <returns>按 UTC 解释后的时间，输入为空时返回空</returns>
    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        return value.HasValue ? ToUtcOffset(value.Value) : null;
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj -c Release
```

- [ ] **Step 7: 验证全解决方案 0 警告并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Traffic.SqlSugar framework/test/XiHan.Framework.Traffic.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(traffic-sqlsugar): 新增灰度规则实体与映射"
```

---

### Task 3: 缓存仓储实现与注册

**Files:**
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/Options/XiHanTrafficSqlSugarOptions.cs`
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/Repositories/SqlSugarGrayRuleRepository.cs`
- Create: `framework/src/XiHan.Framework.Traffic.SqlSugar/Extensions/DependencyInjection/XiHanTrafficSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Traffic.SqlSugar/XiHanTrafficSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/GrayRuleRepositoryTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysGrayRule`、`GrayRuleMapper`；框架的 `IServiceScopeFactory`、`ISqlSugarClientResolver`；主包既有的 `ReplaceGrayRuleRepository<TRepository>()`
- Produces:
  - `public class SqlSugarGrayRuleRepository : IGrayRuleRepository`，构造函数 `(IServiceScopeFactory scopeFactory, IOptions<XiHanTrafficSqlSugarOptions> options)`
  - 扩展方法 `IServiceCollection AddXiHanTrafficSqlSugar(this IServiceCollection services)`

**参考来源（动手前先读）：**
- 契约与内存实现：`framework/src/XiHan.Framework.Traffic/GrayRouting/Abstractions/IGrayRuleRepository.cs`、`Implementations/DefaultGrayRuleRepository.cs`
- 既有的 Singleton 锁写法范本：`DefaultGrayRuleRepository.cs` 的 `private readonly Lock _writeLock = new();`
- 既有的替换扩展：`framework/src/XiHan.Framework.Traffic/Extensions/DependencyInjection/XiHanTrafficServiceCollectionExtensions.cs:59-64`
- 缓存与刷新语义：spec 第 4.3 节

**本任务禁止事项：** 硬约束 ①②③全部。另外**不要**用 `services.Replace(ServiceDescriptor.Singleton<...>())` 重新实现一遍——直接调用主包已提供的 `ReplaceGrayRuleRepository<TRepository>()`。

- [ ] **Step 1: 实现选项类**

`framework/src/XiHan.Framework.Traffic.SqlSugar/Options/XiHanTrafficSqlSugarOptions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Traffic.SqlSugar.Options;

/// <summary>
/// 流量治理 SqlSugar 持久化配置
/// </summary>
public class XiHanTrafficSqlSugarOptions
{
    /// <summary>
    /// 配置节名称
    /// </summary>
    public const string SectionName = "XiHan:Traffic:SqlSugar";

    /// <summary>
    /// 缓存刷新间隔
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/GrayRuleRepositoryTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Traffic.Extensions.DependencyInjection;
using XiHan.Framework.Traffic.GrayRouting.Abstractions;
using XiHan.Framework.Traffic.GrayRouting.Enums;
using XiHan.Framework.Traffic.GrayRouting.Implementations;
using XiHan.Framework.Traffic.GrayRouting.Models;
using XiHan.Framework.Traffic.SqlSugar.Entities;
using XiHan.Framework.Traffic.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Traffic.SqlSugar.Options;
using XiHan.Framework.Traffic.SqlSugar.Repositories;

namespace XiHan.Framework.Traffic.SqlSugar.Tests;

/// <summary>
/// 灰度规则仓储测试
/// </summary>
public class GrayRuleRepositoryTests
{
    /// <summary>
    /// 返回的启用规则运行时类型是 GrayRule 且保留目标版本
    /// </summary>
    [Fact]
    public async Task 返回的启用规则运行时类型是GrayRule且保留目标版本()
    {
        using var context = new GrayRuleTestContext();
        context.Client.Insertable(new SysGrayRule("rule-1")
        {
            RuleName = "百分比灰度",
            RuleType = (int)GrayRuleType.Percentage,
            IsEnabled = true,
            Priority = 1,
            TargetVersion = "2.0.0",
            CreatedTime = DateTimeOffset.UtcNow
        }).ExecuteCommand();

        await context.Repository.RefreshAsync();
        var rules = await context.Repository.GetEnabledRulesAsync();

        var rule = Assert.Single(rules);
        Assert.IsType<GrayRule>(rule);
        Assert.Equal("2.0.0", ((GrayRule)rule).TargetVersion);
    }

    /// <summary>
    /// 只返回启用的规则
    /// </summary>
    [Fact]
    public async Task 只返回启用的规则()
    {
        using var context = new GrayRuleTestContext();
        context.Client.Insertable(new SysGrayRule("rule-enabled") { RuleName = "启用", IsEnabled = true, CreatedTime = DateTimeOffset.UtcNow }).ExecuteCommand();
        context.Client.Insertable(new SysGrayRule("rule-disabled") { RuleName = "禁用", IsEnabled = false, CreatedTime = DateTimeOffset.UtcNow }).ExecuteCommand();

        await context.Repository.RefreshAsync();
        var rules = await context.Repository.GetEnabledRulesAsync();

        Assert.Single(rules);
        Assert.Equal("rule-enabled", rules[0].RuleId);
    }

    /// <summary>
    /// 按标识能查到禁用的规则
    /// </summary>
    [Fact]
    public async Task 按标识能查到禁用的规则()
    {
        using var context = new GrayRuleTestContext();
        context.Client.Insertable(new SysGrayRule("rule-disabled") { RuleName = "禁用", IsEnabled = false, CreatedTime = DateTimeOffset.UtcNow }).ExecuteCommand();

        await context.Repository.RefreshAsync();
        var rule = await context.Repository.GetRuleByIdAsync("rule-disabled");

        Assert.NotNull(rule);
    }

    /// <summary>
    /// 查不存在的标识返回 null
    /// </summary>
    [Fact]
    public async Task 查不存在的标识返回null()
    {
        using var context = new GrayRuleTestContext();

        var rule = await context.Repository.GetRuleByIdAsync("not-exist");

        Assert.Null(rule);
    }

    /// <summary>
    /// 显式刷新后能立即读到新写入的规则
    /// </summary>
    [Fact]
    public async Task 显式刷新后能立即读到新写入的规则()
    {
        using var context = new GrayRuleTestContext(refreshInterval: TimeSpan.FromMinutes(10));
        await context.Repository.RefreshAsync();

        context.Client.Insertable(new SysGrayRule("rule-new") { RuleName = "新规则", IsEnabled = true, CreatedTime = DateTimeOffset.UtcNow }).ExecuteCommand();
        await context.Repository.RefreshAsync();

        var rules = await context.Repository.GetEnabledRulesAsync();

        Assert.Contains(rules, item => item.RuleId == "rule-new");
    }

    /// <summary>
    /// 未到期时不重新查库，读到的是旧缓存
    /// </summary>
    [Fact]
    public async Task 未到期时不重新查库读到的是旧缓存()
    {
        using var context = new GrayRuleTestContext(refreshInterval: TimeSpan.FromMinutes(10));
        await context.Repository.RefreshAsync();

        context.Client.Insertable(new SysGrayRule("rule-new") { RuleName = "新规则", IsEnabled = true, CreatedTime = DateTimeOffset.UtcNow }).ExecuteCommand();
        var rules = await context.Repository.GetEnabledRulesAsync();

        Assert.DoesNotContain(rules, item => item.RuleId == "rule-new");
    }

    /// <summary>
    /// 到期后自动重新查库
    /// </summary>
    [Fact]
    public async Task 到期后自动重新查库()
    {
        using var context = new GrayRuleTestContext(refreshInterval: TimeSpan.FromMilliseconds(1));
        await context.Repository.RefreshAsync();

        context.Client.Insertable(new SysGrayRule("rule-new") { RuleName = "新规则", IsEnabled = true, CreatedTime = DateTimeOffset.UtcNow }).ExecuteCommand();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var rules = await context.Repository.GetEnabledRulesAsync();

        Assert.Contains(rules, item => item.RuleId == "rule-new");
    }

    /// <summary>
    /// 注册扩展以 SqlSugar 仓储顶替内存实现
    /// </summary>
    [Fact]
    public void 注册扩展顶替内存实现()
    {
        var services = new ServiceCollection();
        services.AddGrayRouting();

        services.AddXiHanTrafficSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IGrayRuleRepository));
        Assert.Equal(typeof(SqlSugarGrayRuleRepository), descriptor.ImplementationType);
    }
}

/// <summary>
/// 灰度规则仓储测试夹具
/// </summary>
internal sealed class GrayRuleTestContext : IDisposable
{
    private readonly string _databaseFile;
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="refreshInterval">缓存刷新间隔，默认 30 秒</param>
    public GrayRuleTestContext(TimeSpan? refreshInterval = null)
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_gray_rule_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        Client.CodeFirst.InitTables(typeof(SysGrayRule));

        var services = new ServiceCollection();
        services.AddSingleton<ISqlSugarClientResolver>(new StubClientResolver(Client));
        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Repository = new SqlSugarGrayRuleRepository(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new XiHanTrafficSqlSugarOptions { RefreshInterval = refreshInterval ?? TimeSpan.FromSeconds(30) }));
    }

    /// <summary>
    /// SQLite 客户端
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 被测仓储
    /// </summary>
    public SqlSugarGrayRuleRepository Repository { get; }

    /// <summary>
    /// 释放服务容器、客户端并删除临时库文件
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
dotnet test --project framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarGrayRuleRepository`、`AddXiHanTrafficSqlSugar` 不存在。

- [ ] **Step 4: 实现仓储**

`framework/src/XiHan.Framework.Traffic.SqlSugar/Repositories/SqlSugarGrayRuleRepository.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Traffic.GrayRouting.Abstractions;
using XiHan.Framework.Traffic.GrayRouting.Models;
using XiHan.Framework.Traffic.SqlSugar.Entities;
using XiHan.Framework.Traffic.SqlSugar.Mapping;
using XiHan.Framework.Traffic.SqlSugar.Options;

namespace XiHan.Framework.Traffic.SqlSugar.Repositories;

/// <summary>
/// 灰度规则 SqlSugar 只读仓储
/// </summary>
/// <remarks>
/// 只负责查询与缓存，不提供写方法；规则的增删改由应用层直接对 sys_gray_rule 表操作。
/// </remarks>
public class SqlSugarGrayRuleRepository : IGrayRuleRepository
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly XiHanTrafficSqlSugarOptions _options;
    private readonly Lock _refreshLock = new();

    private volatile Dictionary<string, GrayRule> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastRefreshTime = DateTimeOffset.MinValue;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="scopeFactory">服务范围工厂，用于按需解析 Scoped 的客户端解析器</param>
    /// <param name="options">缓存刷新配置</param>
    public SqlSugarGrayRuleRepository(
        IServiceScopeFactory scopeFactory,
        IOptions<XiHanTrafficSqlSugarOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    /// <summary>
    /// 获取所有启用的灰度规则
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>规则列表</returns>
    public async Task<List<IGrayRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken);

        return [.. _cache.Values.Where(rule => rule.IsEnabled)];
    }

    /// <summary>
    /// 根据规则标识获取规则
    /// </summary>
    /// <param name="ruleId">规则标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>规则</returns>
    public async Task<IGrayRule?> GetRuleByIdAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken);

        return _cache.GetValueOrDefault(ruleId);
    }

    /// <summary>
    /// 强制从数据库重新加载全部规则
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var clientResolver = scope.ServiceProvider.GetRequiredService<ISqlSugarClientResolver>();
        var client = clientResolver.GetClientForEntity<SysGrayRule>();

        var entities = await client.Queryable<SysGrayRule>().ToListAsync(cancellationToken);

        var loaded = new Dictionary<string, GrayRule>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            loaded[entity.BasicId] = GrayRuleMapper.ToModel(entity);
        }

        lock (_refreshLock)
        {
            _cache = loaded;
            _lastRefreshTime = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// 缓存到期时刷新，未到期直接返回
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastRefreshTime < _options.RefreshInterval)
        {
            return;
        }

        await RefreshAsync(cancellationToken);
    }
}
```

- [ ] **Step 5: 实现注册扩展**

`framework/src/XiHan.Framework.Traffic.SqlSugar/Extensions/DependencyInjection/XiHanTrafficSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Traffic.Extensions.DependencyInjection;
using XiHan.Framework.Traffic.SqlSugar.Options;
using XiHan.Framework.Traffic.SqlSugar.Repositories;

namespace XiHan.Framework.Traffic.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 流量治理 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanTrafficSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 仓储替换灰度规则的内存实现
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanTrafficSqlSugar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ReplaceGrayRuleRepository<SqlSugarGrayRuleRepository>();

        return services;
    }

    /// <summary>
    /// 以 SqlSugar 仓储替换灰度规则的内存实现，并绑定配置节
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanTrafficSqlSugar(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<XiHanTrafficSqlSugarOptions>(configuration.GetSection(XiHanTrafficSqlSugarOptions.SectionName));

        return services.AddXiHanTrafficSqlSugar();
    }
}
```

- [ ] **Step 6: 在模块里调用扩展**

把 `XiHanTrafficSqlSugarModule.cs` 改为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;
using XiHan.Framework.Traffic;
using XiHan.Framework.Traffic.SqlSugar.Extensions.DependencyInjection;

namespace XiHan.Framework.Traffic.SqlSugar;

/// <summary>
/// 曦寒框架流量治理 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanTrafficSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 只读仓储替换 <see cref="XiHanTrafficModule"/> 注册的内存实现。
/// </remarks>
[DependsOn(
    typeof(XiHanTrafficModule),
    typeof(XiHanDataModule)
)]
public class XiHanTrafficSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;
        var configuration = services.GetConfiguration();

        services.AddXiHanTrafficSqlSugar(configuration);
    }
}
```

- [ ] **Step 7: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Traffic.SqlSugar.Tests/XiHan.Framework.Traffic.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS（尤其"返回的启用规则运行时类型是 GrayRule 且保留目标版本"）；构建 0 Warning(s) 0 Error(s)。

- [ ] **Step 8: 提交**

```bash
git add framework/src/XiHan.Framework.Traffic.SqlSugar framework/test/XiHan.Framework.Traffic.SqlSugar.Tests
git commit -m "feat(traffic-sqlsugar): 新增灰度规则缓存仓储并替换内存版"
```

---

### Task 4: 文档与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.Traffic.SqlSugar/README.md`
- Create: `docs/packages/traffic-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`（包索引表与计数）
- Modify: `README.md`、`README_cn.md`（模块计数）
- Modify: `framework/README.md`、`framework/README_cn.md`（模块清单与计数）
- Modify: `docs/index.md`、`docs/introduction.md`、`docs/why.md`（文档站计数）

**Interfaces:**
- Consumes: 前三个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 已知边界清单：spec 第 8 节
- 同类子包文档范本：`docs/packages/auditing-sqlsugar.md`
- 包索引表结构与既有行：`docs/packages/index.md`（第 85 行 `EventBus.SqlSugar`、第 99 行 `Auditing.SqlSugar`，均是链到子包页面的一行 + 一句话说明）

**本任务禁止事项：** 不要顺手改与本 PR 无关的文档。不要声称仓库有 `.codegraph/` 目录。**不要**往 `README.md`/`README_cn.md` 里加表格行——那两份是"常用包"精选清单，不是逐包穷举，只改计数。**不要**把模块计数当成本计划撰写时的某个固定数字硬编码进 commit——同系列的 Security.SqlSugar、Upgrade.SqlSugar 等包会依次合并，先合并的包已经把计数改过，写死的数字对后合并的包必然是错的。

- [ ] **Step 1: 新增包文档**

`docs/packages/traffic-sqlsugar.md`，按 `docs/packages/auditing-sqlsugar.md` 的结构组织，至少包含：包定位（只读仓储）、表结构（`sys_gray_rule`）、缓存刷新配置（`XiHan:Traffic:SqlSugar:RefreshInterval`）、`(rule as GrayRule)` 这一运行时类型约束、多实例缓存不同步的已知边界。

- [ ] **Step 2: 挂上侧边栏**

在 `docs/.vitepress/config.ts` 的 packages 分组里、`traffic` 条目之后插入 `traffic-sqlsugar` 条目。

- [ ] **Step 3: 登记进包索引**

`docs/packages/index.md` 是包索引表，按分层分组，第 105-114 行的「8. 存储 · 模板 · 任务 · 治理」一组里已有：

```markdown
| [Traffic](./traffic) | 流量治理：灰度路由（百分比/用户/租户/请求头）、限流与熔断策略接口 |
| [Upgrade](./upgrade) | 升级引擎：版本存储、迁移执行、分布式锁、启动自动检查 |
```

在 `[Traffic](./traffic)` 那一行**之后**、`[Upgrade](./upgrade)` 那一行**之前**插入：

```markdown
| [Traffic.SqlSugar](./traffic-sqlsugar) | 灰度规则的 SqlSugar 只读仓储：内存缓存 + 到期自动刷新，替换内存实现 |
```

行号是撰写时的快照，实现时用 `grep -n "\[Traffic\](\./traffic)"` 现场核对插入位置，不要凭行号硬改。

本文件开头第 3 行「XiHan.Framework 由 **N 个 NuGet 包**组成」的计数不在本步处理，随 Step 4 的站内计数扫描一并 +1。

- [ ] **Step 4: 更新模块计数与模块清单**

模块总数字符串**不要假设是某个具体数字**——先从 `README.md` 的 shields.io 徽章读出当前计数 `N`（`Modules-N-1f6feb`）。

**不要**用 `[0-9]\+ 个`/`[0-9]\+ 页` 这类正则模糊匹配——在当前仓库上能打出约 70 条命中，绝大多数是版本号、章节数等无关数字，且不排除 `node_modules`。改用精确的单词匹配：

```bash
N=$(grep -o "Modules-[0-9]\+-1f6feb" README.md | grep -o "[0-9]\+")
echo "当前计数：$N"

grep -rn --include=*.md -w "$N" README.md README_cn.md framework/README.md framework/README_cn.md docs --exclude-dir=node_modules
```

把命中里"确实是模块计数"的那些改成 `N+1`。下表列出撰写时已知的 8 个文件、20 处命中（含英文文案——`[0-9]\+ 个`/`[0-9]\+ 页` 的正则会漏掉这些，`-w` 精确匹配不会）：

| 文件 | 内容 |
| --- | --- |
| `README.md` | 正文里的模块计数（1 处，中文）、shields.io 徽章 URL 里的 `Modules-N-1f6feb`（1 处）、英文 tagline/badge 附近的计数（1 处） |
| `README_cn.md` | 同上 3 处 |
| `framework/README.md` | 正文计数（1 处）、模块目录小计（1 处）、测试项目计数（1 处，本包新增了 1 个测试项目，一并 +1） |
| `framework/README_cn.md` | 同上 3 处 |
| `docs/index.md` | 标语行「N 个可独立引用的 NuGet 包」（1 处）、卡片说明「N 个包按七层组织」（1 处） |
| `docs/introduction.md` | 模块总览表格行「参考手册（N 页）」（1 处） |
| `docs/why.md` | 「拆成 N 个可独立引用的 NuGet 包」「N 个包可以单独引用」「包参考 N 页」「N 个包逐一查阅」共 4 处 |
| `docs/packages/index.md` | 开头「由 N 个 NuGet 包组成」（1 处，与 Step 3 提到的同一处） |

行号是撰写时的快照，会随前面已合并的包漂移，以本步 grep 的实际输出为准，不要按下表行号直接改。**每一处命中都要人工确认它确实是模块计数**，不要把版本号、章节数（如「开发指南 38 章」）、无关统计数字一并改掉——上表已列出全部需要改的位置，命中但不在表里的数字保持原样。

改完后跑两次验证：

```bash
grep -rn --include=*.md -w "$((N + 1))" README.md README_cn.md framework/README.md framework/README_cn.md docs --exclude-dir=node_modules | wc -l
# 属于模块计数的命中应为 20；数字可能与无关文字巧合（如 N+1=69 时 docs/guide/distributed-ids.md 的「约 69 年」），逐条看输出，巧合命中不计入

grep -rn --include=*.md -w "$((N + 1))" README.md README_cn.md framework/README.md framework/README_cn.md docs --exclude-dir=node_modules | cut -d: -f1 | sort -u | wc -l
# 扣除巧合命中所在的文件后应为 8（8 个文件各至少 1 处命中）

grep -rn --include=*.md -w "$N" README.md README_cn.md framework/README.md framework/README_cn.md docs --exclude-dir=node_modules
# 不应再出现任何属于模块计数的命中；若还有命中，人工确认是巧合数字还是漏改
```

`README.md`、`README_cn.md`、`docs/index.md`、`docs/introduction.md`、`docs/why.md`、`docs/packages/index.md` 只改计数，**不加表格行**——这些是概览/首页文案，不是逐包穷举表（`docs/packages/index.md` 的表格行已在 Step 3 单独处理）。

`framework/README.md` 与 `framework/README_cn.md` 除了计数，还要在模块清单表格中紧随 `XiHan.Framework.Traffic` 之后加入 `XiHan.Framework.Traffic.SqlSugar` 一行，描述用"灰度规则 SqlSugar 持久化"。

- [ ] **Step 5: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

若构建因残留测试进程占用输出文件而失败：

```bash
taskkill //F //IM "XiHan.Framework.Traffic.SqlSugar.Tests.exe"
```

- [ ] **Step 6: 注释复查**

通读本计划改动过的 `.cs` 文件的注释，判定标准是是否在讲论证、权衡、叙事，发现即移出到提交信息。

- [ ] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Traffic.SqlSugar/README.md docs README.md README_cn.md framework/README.md framework/README_cn.md
git commit -m "docs(traffic-sqlsugar): 补写包文档与模块清单"
```

---

## 完成标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `IGrayRuleRepository` 经 `ReplaceGrayRuleRepository<SqlSugarGrayRuleRepository>()` 顶替
- `GetEnabledRulesAsync` 返回的元素运行时类型是 `GrayRule`，有测试断言
- 缓存到期自动刷新、显式 `RefreshAsync` 立即生效、未到期不查库，三者均有测试覆盖
- 包 README、`docs/packages/traffic-sqlsugar.md`、VitePress 侧边栏、模块清单均已更新

## 已知边界（写入 PR 描述，不写进代码注释）

- **多实例缓存不同步**：每个实例各自独立刷新，规则变更最坏情况下需要等一个 `RefreshInterval` 才能在所有实例生效
- **规则写入完全由应用层负责**：本包不提供任何写方法
- **`Configuration` 列不做 JSON 校验**：存取时按字符串原样处理

## 下一份计划

本包只有一份计划。系列内下一个包见 `.superpowers/plans/2026-09-28-upgrade-sqlsugar-1-upgrade-version.md`（独立计划，互不依赖）。
