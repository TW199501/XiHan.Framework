# Auditing.SqlSugar 包骨架与日志实体（P1）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立 `XiHan.Framework.Auditing.SqlSugar` 包骨架与 5 个按月分表的日志实体，使 `DbInitializer` 能建出对应表；Writer 实现留给 P2。

**Architecture:** 沿用仓库既有兄弟子包模式（`EventBus.Kafka`）。日志实体定义在本包内、继承 `SugarCreationEntity<long>` 并实现 `ISplitTableEntity`，`Auditing` 主包的记录模型（`OperationLogRecord` 等）保持为不带持久化关注点的 POCO，不被迫依赖 SqlSugar。分表字段复用基类的 `CreatedTime`（`DateTimeOffset`，SqlSugar 原生支持）。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-21-sqlsugar-persistence-design.md`

> 设计文档提交在 `dev` 分支，本计划在 `feat/sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-sqlsugar`）执行。worktree 内看不到该文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录**：`E:/source/XiHan/XiHan.Framework-sqlsugar`（分支 `feat/sqlsugar`，基底 `upstream/main`，`pushRemote=origin`）。

**技术栈是 SqlSugar，不是 Entity Framework Core。** 下列 EF Core 惯用法一律禁止：

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | SqlSugar 无 change tracking，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()` |
| `Migrations` / `Add-Migration` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001` 检查）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息，不写进代码
- file-scoped namespace、`Nullable` 与 `ImplicitUsings` 已全局启用
- 表名 `sys_` 前缀、全小写下划线；列名 Pascal_Snake_Case（`Trace_Id`、`Status_Code`），每列带 `ColumnDescription` 简体中文说明
- 主键列 `Basic_Id`，类型 `long`，`IsIdentity = false`

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。新增任何警告都会被上游退回。

**测试平台限制**：本仓库用 Microsoft.Testing.Platform（`global.json` 的 `test.runner`），不是 VSTest。**没有可用的筛选参数**——`--filter-method`、`--list-tests`、`--filter` 均返回退出码 3。**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。要跑单个测试类就整个项目跑。

**提交信息**：中文 Conventional Commits，作用域用模块小写名，例如 `feat(auditing-sqlsugar): 新增操作日志实体`。**不加任何 AI 署名。**

---

## File Structure

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  XiHan.Framework.Auditing.SqlSugar.csproj      包定义，按序 Import 四个 props
  XiHanAuditingSqlSugarModule.cs                模块类，只做装配
  README.md                                     固定七段结构
  Entities/
    SysAccessLog.cs      SysApiLog.cs      SysExceptionLog.cs
    SysLoginLog.cs       SysOperationLog.cs

framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  XiHan.Framework.Auditing.SqlSugar.Tests.csproj
  EntityMappingTests.cs       实体元数据断言（表名、列名、分表字段）
  TableInitializationTests.cs SQLite 建表集成测试
```

每个实体一个文件，职责单一。实体之间无依赖，可独立审查。

---

### Task 1: 包骨架与解决方案注册

**Files:**
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/XiHan.Framework.Auditing.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/XiHanAuditingSqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/README.md`
- Modify: `framework/XiHan.Framework.slnx`（在 `/1.src/6.Infrastructure/` 文件夹内，`XiHan.Framework.Auditing` 之后插入一行）

**Interfaces:**
- Consumes: 无
- Produces: 程序集 `XiHan.Framework.Auditing.SqlSugar`，根命名空间 `XiHan.Framework.Auditing.SqlSugar`；模块类型 `XiHanAuditingSqlSugarModule`（供后续任务与下游 `[DependsOn]` 引用）

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.Kafka/XiHan.Framework.EventBus.Kafka.csproj`
- 模块类范本：`framework/src/XiHan.Framework.EventBus.Kafka/XiHanKafkaEventBusModule.cs`
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`

**本任务禁止事项：** 不要写任何服务注册逻辑，本任务的模块类是空装配。不要新增 `PackageReference`——SqlSugar 经 `XiHan.Framework.Data` 传递引入。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.Auditing.SqlSugar/XiHan.Framework.Auditing.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Auditing.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Auditing.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Auditing.SqlSugar</PackageId>
        <Description>曦寒框架审计日志 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Auditing\XiHan.Framework.Auditing.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

四个 props 的 Import 顺序不能变（`netcore` / `common` / `version` / `nuget`）。

- [ ] **Step 2: 创建模块类**

`framework/src/XiHan.Framework.Auditing.SqlSugar/XiHanAuditingSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Auditing.SqlSugar;

/// <summary>
/// 曦寒框架审计日志 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuditingSqlSugarModule))]</c> 即启用。
/// 本模块提供审计日志的 SqlSugar 实体定义；日志写入器在后续版本提供。
/// </remarks>
[DependsOn(
    typeof(XiHanAuditingModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuditingSqlSugarModule : XiHanModule
{
}
```

- [ ] **Step 3: 注册进解决方案**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、`XiHan.Framework.Auditing` 那一行之后插入：

```xml
    <Project Path="src/XiHan.Framework.Auditing.SqlSugar/XiHan.Framework.Auditing.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README**

`framework/src/XiHan.Framework.Auditing.SqlSugar/README.md`，沿用固定七段结构：

```markdown
# XiHan.Framework.Auditing.SqlSugar

## 概述

`XiHan.Framework.Auditing` 的 SqlSugar 持久化提供程序，提供审计日志的实体定义与落库实现。

## 核心能力

- 5 类审计日志（访问 / 接口 / 异常 / 登录 / 操作）的 SqlSugar 实体
- 按月自动分表，表名形如 `sys_operation_log_20260901`
- 表结构由 `DbInitializer` 在应用启动时创建

## 依赖关系

依赖 `XiHan.Framework.Auditing`（日志记录模型与写入器契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。分表字段为 `Created_Time`。

## 使用方式

在应用启动模块上声明依赖：

​```csharp
[DependsOn(typeof(XiHanAuditingSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
​```

## 扩展点

需要自定义落库行为时，实现 `XiHan.Framework.Auditing.Writers` 下的对应接口并在 DI 中替换。

## 目录结构

​```
Entities/   5 类日志的 SqlSugar 实体
​```
```

（上面 README 内容里的 `​```` 是为在本计划中转义而加的零宽字符，写入文件时使用正常的三反引号。）

- [ ] **Step 5: 验证构建 0 警告**

在 `E:/source/XiHan/XiHan.Framework-sqlsugar` 执行：

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。若出现 `XHFH001`，说明版权声明缺失或格式不符。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar framework/XiHan.Framework.slnx
git commit -m "feat(auditing-sqlsugar): 新增包骨架与模块装配"
```

---

### Task 2: 测试项目与操作日志实体

**Files:**
- Create: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysOperationLog.cs`
- Modify: `framework/XiHan.Framework.slnx`（测试项目注册）

**Interfaces:**
- Consumes: Task 1 的程序集与命名空间
- Produces: `SysOperationLog`（`XiHan.Framework.Auditing.SqlSugar.Entities`），继承 `SugarCreationEntity<long>`，实现 `ISplitTableEntity`；后续任务的 4 个实体照此形状

**参考来源（动手前先读）：**
- 列映射范本：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarCreationEntity.cs`（`Basic_Id` / `Created_Time` / `Row_Version` 的写法）
- 分表 API：`/e/source/platfrom-admin/docs/SqlSugar-docs/自動分表.md`
- 分表字段类型处理：`/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/IntegrationServices/SplitTableService.cs:78-80`（确认 `DateTimeOffset` 受支持）
- 源记录模型：`framework/src/XiHan.Framework.Auditing/OperationLogRecord.cs`
- 测试项目范本：`framework/test/XiHan.Framework.Auditing.Tests/XiHan.Framework.Auditing.Tests.csproj`

**本任务禁止事项：** 不要给 `OperationLogRecord` 加任何 SqlSugar 特性——主包不依赖 SqlSugar。不要用 `[Table]` / `[Column]`。不要把主键设为自增（`IsIdentity` 必须为 `false`，分表要求主键不自增）。

**关键点：** `[SugarTable]` 的表名模板**必须同时包含 `{year}{month}{day}` 三个变量**，即使按月分表也一样——这是 SqlSugar 为日后改分表粒度保留的兼容要求。分表字段通过 `override` 基类的 `CreatedTime` 并在 override 上标注 `[SplitField]` 实现。

- [ ] **Step 1: 创建测试项目**

`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\test.props" />

    <PropertyGroup>
        <AssemblyName>XiHan.Framework.Auditing.SqlSugar.Tests</AssemblyName>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Auditing.SqlSugar\XiHan.Framework.Auditing.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

在 `framework/XiHan.Framework.slnx` 的测试文件夹内、`XiHan.Framework.Auditing.Tests` 之后插入：

```xml
    <Project Path="test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Tests;

/// <summary>
/// 日志实体映射测试
/// </summary>
public class EntityMappingTests
{
    /// <summary>
    /// 操作日志表名使用 sys_ 前缀并带三个分表变量
    /// </summary>
    [Fact]
    public void SysOperationLog_表名带前缀与三个分表变量()
    {
        var table = typeof(SysOperationLog).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_operation_log_{year}{month}{day}", table.TableName);
    }

    /// <summary>
    /// 操作日志按月分表
    /// </summary>
    [Fact]
    public void SysOperationLog_按月分表()
    {
        var split = typeof(SysOperationLog).GetCustomAttribute<SplitTableAttribute>();

        Assert.NotNull(split);
        Assert.Equal(SplitType.Month, split.SplitType);
    }

    /// <summary>
    /// 分表字段标注在 CreatedTime 上
    /// </summary>
    [Fact]
    public void SysOperationLog_分表字段为创建时间()
    {
        var property = typeof(SysOperationLog).GetProperty(nameof(SysOperationLog.CreatedTime));

        Assert.NotNull(property);
        Assert.NotNull(property.GetCustomAttribute<SplitFieldAttribute>());
    }

    /// <summary>
    /// 实体实现分表标记接口
    /// </summary>
    [Fact]
    public void SysOperationLog_实现分表标记接口()
    {
        Assert.True(typeof(ISplitTableEntity).IsAssignableFrom(typeof(SysOperationLog)));
    }

    /// <summary>
    /// 列名使用 Pascal_Snake_Case
    /// </summary>
    [Fact]
    public void SysOperationLog_列名使用帕斯卡下划线()
    {
        var property = typeof(SysOperationLog).GetProperty(nameof(SysOperationLog.TraceId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Trace_Id", column.ColumnName);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysOperationLog` 类型不存在。

- [ ] **Step 4: 实现实体**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysOperationLog.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Entities;

/// <summary>
/// 操作日志实体
/// </summary>
[SplitTable(SplitType.Month)]
[SugarTable("sys_operation_log_{year}{month}{day}")]
public class SysOperationLog : SugarCreationEntity<long>, ISplitTableEntity
{
    /// <summary>
    /// 创建时间，同时作为分表字段
    /// </summary>
    [SplitField]
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "创建时间")]
    public override DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 跟踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = false, ColumnDescription = "跟踪标识")]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// 会话标识
    /// </summary>
    [SugarColumn(ColumnName = "Session_Id", Length = 64, IsNullable = true, ColumnDescription = "会话标识")]
    public string? SessionId { get; set; }

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = true, ColumnDescription = "用户标识")]
    public long? UserId { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnName = "User_Name", Length = 128, IsNullable = true, ColumnDescription = "用户名")]
    public string? UserName { get; set; }

    /// <summary>
    /// 控制器
    /// </summary>
    [SugarColumn(ColumnName = "Controller_Name", Length = 256, IsNullable = true, ColumnDescription = "控制器")]
    public string? ControllerName { get; set; }

    /// <summary>
    /// 动作
    /// </summary>
    [SugarColumn(ColumnName = "Action_Name", Length = 256, IsNullable = true, ColumnDescription = "动作")]
    public string? ActionName { get; set; }

    /// <summary>
    /// 请求方法
    /// </summary>
    [SugarColumn(ColumnName = "Method", Length = 16, IsNullable = false, ColumnDescription = "请求方法")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径
    /// </summary>
    [SugarColumn(ColumnName = "Path", Length = 512, IsNullable = false, ColumnDescription = "请求路径")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 请求参数
    /// </summary>
    [SugarColumn(ColumnName = "Request_Params", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求参数")]
    public string? RequestParams { get; set; }

    /// <summary>
    /// 响应结果
    /// </summary>
    [SugarColumn(ColumnName = "Response_Result", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "响应结果")]
    public string? ResponseResult { get; set; }

    /// <summary>
    /// 状态码
    /// </summary>
    [SugarColumn(ColumnName = "Status_Code", IsNullable = false, ColumnDescription = "状态码")]
    public int StatusCode { get; set; }

    /// <summary>
    /// 耗时毫秒
    /// </summary>
    [SugarColumn(ColumnName = "Elapsed_Milliseconds", IsNullable = false, ColumnDescription = "耗时毫秒")]
    public long ElapsedMilliseconds { get; set; }

    /// <summary>
    /// 来源地址
    /// </summary>
    [SugarColumn(ColumnName = "Remote_Ip", Length = 64, IsNullable = true, ColumnDescription = "来源地址")]
    public string? RemoteIp { get; set; }

    /// <summary>
    /// 用户代理
    /// </summary>
    [SugarColumn(ColumnName = "User_Agent", Length = 512, IsNullable = true, ColumnDescription = "用户代理")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    [SugarColumn(ColumnName = "Error_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "错误信息")]
    public string? ErrorMessage { get; set; }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：5 个测试全部 PASS。

关于 `StaticConfig.CodeFirst_BigString`：定义在 `/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Infrastructure/StaticConfig.cs:16`，值为 `"varcharmax,longtext,text,clob"`——各方言的大文本类型清单，`CodeFirstProvider` 按当前 `DbType` 从中挑选。这是大文本列的可移植写法，**不要**改成写死的 `"text"` 或 `"longtext"`。

- [ ] **Step 6: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：0 Warning(s) 0 Error(s)。

- [ ] **Step 7: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar/Entities framework/test/XiHan.Framework.Auditing.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(auditing-sqlsugar): 新增操作日志实体与映射测试"
```

---

### Task 3: 其余四个日志实体

**Files:**
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysAccessLog.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysApiLog.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysExceptionLog.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysLoginLog.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs`

**Interfaces:**
- Consumes: Task 2 确立的实体形状（`SugarCreationEntity<long>` + `ISplitTableEntity` + `[SplitField]` on `CreatedTime`）
- Produces: `SysAccessLog` / `SysApiLog` / `SysExceptionLog` / `SysLoginLog`，均在 `XiHan.Framework.Auditing.SqlSugar.Entities`

**参考来源（动手前先读）：**
- 源记录模型：`framework/src/XiHan.Framework.Auditing/AccessLogRecord.cs`、`ApiLogRecord.cs`、`ExceptionLogRecord.cs`、`LoginLogRecord.cs`
- 实体形状：本计划 Task 2 的 `SysOperationLog`（逐字照抄结构，不要自创变体）

**本任务禁止事项：** 同 Task 2。另外 `SysLoginLog` 的 `LoginTime` 是记录模型自带的业务时间，**不要**用它做分表字段——分表字段统一是 `CreatedTime`，两者语义不同。

- [ ] **Step 1: 追加失败的测试**

在 `EntityMappingTests.cs` 末尾（类的最后一个方法之后）追加：

```csharp
    /// <summary>
    /// 四个实体的表名均带 sys_ 前缀与三个分表变量
    /// </summary>
    [Theory]
    [InlineData(typeof(SysAccessLog), "sys_access_log_{year}{month}{day}")]
    [InlineData(typeof(SysApiLog), "sys_api_log_{year}{month}{day}")]
    [InlineData(typeof(SysExceptionLog), "sys_exception_log_{year}{month}{day}")]
    [InlineData(typeof(SysLoginLog), "sys_login_log_{year}{month}{day}")]
    public void 其余日志实体_表名符合约定(Type entityType, string expectedTableName)
    {
        var table = entityType.GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal(expectedTableName, table.TableName);
    }

    /// <summary>
    /// 四个实体均按月分表且分表字段为创建时间
    /// </summary>
    [Theory]
    [InlineData(typeof(SysAccessLog))]
    [InlineData(typeof(SysApiLog))]
    [InlineData(typeof(SysExceptionLog))]
    [InlineData(typeof(SysLoginLog))]
    public void 其余日志实体_按月分表且分表字段为创建时间(Type entityType)
    {
        var split = entityType.GetCustomAttribute<SplitTableAttribute>();
        Assert.NotNull(split);
        Assert.Equal(SplitType.Month, split.SplitType);

        var property = entityType.GetProperty("CreatedTime");
        Assert.NotNull(property);
        Assert.NotNull(property.GetCustomAttribute<SplitFieldAttribute>());

        Assert.True(typeof(ISplitTableEntity).IsAssignableFrom(entityType));
    }

    /// <summary>
    /// 登录日志的业务时间与分表字段是两个不同的列
    /// </summary>
    [Fact]
    public void SysLoginLog_登录时间与创建时间分列()
    {
        var loginTime = typeof(SysLoginLog).GetProperty(nameof(SysLoginLog.LoginTime));
        var column = loginTime!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Login_Time", column.ColumnName);
        Assert.Null(loginTime.GetCustomAttribute<SplitFieldAttribute>());
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，四个类型不存在。

- [ ] **Step 3: 实现 SysAccessLog**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysAccessLog.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Entities;

/// <summary>
/// 访问日志实体
/// </summary>
[SplitTable(SplitType.Month)]
[SugarTable("sys_access_log_{year}{month}{day}")]
public class SysAccessLog : SugarCreationEntity<long>, ISplitTableEntity
{
    /// <summary>
    /// 创建时间，同时作为分表字段
    /// </summary>
    [SplitField]
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "创建时间")]
    public override DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 跟踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = false, ColumnDescription = "跟踪标识")]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = true, ColumnDescription = "用户标识")]
    public long? UserId { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnName = "User_Name", Length = 128, IsNullable = true, ColumnDescription = "用户名")]
    public string? UserName { get; set; }

    /// <summary>
    /// 会话标识
    /// </summary>
    [SugarColumn(ColumnName = "Session_Id", Length = 64, IsNullable = true, ColumnDescription = "会话标识")]
    public string? SessionId { get; set; }

    /// <summary>
    /// 资源名称
    /// </summary>
    [SugarColumn(ColumnName = "Resource_Name", Length = 256, IsNullable = true, ColumnDescription = "资源名称")]
    public string? ResourceName { get; set; }

    /// <summary>
    /// 请求方法
    /// </summary>
    [SugarColumn(ColumnName = "Method", Length = 16, IsNullable = false, ColumnDescription = "请求方法")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径
    /// </summary>
    [SugarColumn(ColumnName = "Path", Length = 512, IsNullable = false, ColumnDescription = "请求路径")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 查询字符串
    /// </summary>
    [SugarColumn(ColumnName = "Query_String", Length = 2048, IsNullable = true, ColumnDescription = "查询字符串")]
    public string? QueryString { get; set; }

    /// <summary>
    /// 请求体
    /// </summary>
    [SugarColumn(ColumnName = "Request_Body", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求体")]
    public string? RequestBody { get; set; }

    /// <summary>
    /// 状态码
    /// </summary>
    [SugarColumn(ColumnName = "Status_Code", IsNullable = false, ColumnDescription = "状态码")]
    public int StatusCode { get; set; }

    /// <summary>
    /// 来源地址
    /// </summary>
    [SugarColumn(ColumnName = "Remote_Ip", Length = 64, IsNullable = true, ColumnDescription = "来源地址")]
    public string? RemoteIp { get; set; }

    /// <summary>
    /// 用户代理
    /// </summary>
    [SugarColumn(ColumnName = "User_Agent", Length = 512, IsNullable = true, ColumnDescription = "用户代理")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// 来源页面
    /// </summary>
    [SugarColumn(ColumnName = "Referer", Length = 512, IsNullable = true, ColumnDescription = "来源页面")]
    public string? Referer { get; set; }

    /// <summary>
    /// 耗时毫秒
    /// </summary>
    [SugarColumn(ColumnName = "Elapsed_Milliseconds", IsNullable = false, ColumnDescription = "耗时毫秒")]
    public long ElapsedMilliseconds { get; set; }

    /// <summary>
    /// 响应大小
    /// </summary>
    [SugarColumn(ColumnName = "Response_Size", IsNullable = false, ColumnDescription = "响应大小")]
    public long ResponseSize { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    [SugarColumn(ColumnName = "Error_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "错误信息")]
    public string? ErrorMessage { get; set; }
}
```

- [ ] **Step 4: 实现 SysApiLog**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysApiLog.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Entities;

/// <summary>
/// 接口日志实体
/// </summary>
[SplitTable(SplitType.Month)]
[SugarTable("sys_api_log_{year}{month}{day}")]
public class SysApiLog : SugarCreationEntity<long>, ISplitTableEntity
{
    /// <summary>
    /// 创建时间，同时作为分表字段
    /// </summary>
    [SplitField]
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "创建时间")]
    public override DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 跟踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = false, ColumnDescription = "跟踪标识")]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = true, ColumnDescription = "用户标识")]
    public long? UserId { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnName = "User_Name", Length = 128, IsNullable = true, ColumnDescription = "用户名")]
    public string? UserName { get; set; }

    /// <summary>
    /// 客户端标识
    /// </summary>
    [SugarColumn(ColumnName = "Client_Id", Length = 128, IsNullable = true, ColumnDescription = "客户端标识")]
    public string? ClientId { get; set; }

    /// <summary>
    /// 应用标识
    /// </summary>
    [SugarColumn(ColumnName = "App_Id", Length = 128, IsNullable = true, ColumnDescription = "应用标识")]
    public string? AppId { get; set; }

    /// <summary>
    /// 签名是否有效
    /// </summary>
    [SugarColumn(ColumnName = "Is_Signature_Valid", IsNullable = false, ColumnDescription = "签名是否有效")]
    public bool IsSignatureValid { get; set; } = true;

    /// <summary>
    /// 签名算法
    /// </summary>
    [SugarColumn(ColumnName = "Signature_Algorithm", Length = 64, IsNullable = true, ColumnDescription = "签名算法")]
    public string? SignatureAlgorithm { get; set; }

    /// <summary>
    /// 请求方法
    /// </summary>
    [SugarColumn(ColumnName = "Method", Length = 16, IsNullable = false, ColumnDescription = "请求方法")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径
    /// </summary>
    [SugarColumn(ColumnName = "Path", Length = 512, IsNullable = false, ColumnDescription = "请求路径")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 接口名称
    /// </summary>
    [SugarColumn(ColumnName = "Api_Name", Length = 256, IsNullable = true, ColumnDescription = "接口名称")]
    public string? ApiName { get; set; }

    /// <summary>
    /// 控制器
    /// </summary>
    [SugarColumn(ColumnName = "Controller_Name", Length = 256, IsNullable = true, ColumnDescription = "控制器")]
    public string? ControllerName { get; set; }

    /// <summary>
    /// 动作
    /// </summary>
    [SugarColumn(ColumnName = "Action_Name", Length = 256, IsNullable = true, ColumnDescription = "动作")]
    public string? ActionName { get; set; }

    /// <summary>
    /// 请求参数
    /// </summary>
    [SugarColumn(ColumnName = "Request_Params", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求参数")]
    public string? RequestParams { get; set; }

    /// <summary>
    /// 请求体
    /// </summary>
    [SugarColumn(ColumnName = "Request_Body", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求体")]
    public string? RequestBody { get; set; }

    /// <summary>
    /// 响应体
    /// </summary>
    [SugarColumn(ColumnName = "Response_Body", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "响应体")]
    public string? ResponseBody { get; set; }

    /// <summary>
    /// 状态码
    /// </summary>
    [SugarColumn(ColumnName = "Status_Code", IsNullable = false, ColumnDescription = "状态码")]
    public int StatusCode { get; set; }

    /// <summary>
    /// 来源地址
    /// </summary>
    [SugarColumn(ColumnName = "Remote_Ip", Length = 64, IsNullable = true, ColumnDescription = "来源地址")]
    public string? RemoteIp { get; set; }

    /// <summary>
    /// 用户代理
    /// </summary>
    [SugarColumn(ColumnName = "User_Agent", Length = 512, IsNullable = true, ColumnDescription = "用户代理")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// 来源页面
    /// </summary>
    [SugarColumn(ColumnName = "Referer", Length = 512, IsNullable = true, ColumnDescription = "来源页面")]
    public string? Referer { get; set; }

    /// <summary>
    /// 耗时毫秒
    /// </summary>
    [SugarColumn(ColumnName = "Elapsed_Milliseconds", IsNullable = false, ColumnDescription = "耗时毫秒")]
    public long ElapsedMilliseconds { get; set; }

    /// <summary>
    /// 请求大小
    /// </summary>
    [SugarColumn(ColumnName = "Request_Size", IsNullable = false, ColumnDescription = "请求大小")]
    public long RequestSize { get; set; }

    /// <summary>
    /// 响应大小
    /// </summary>
    [SugarColumn(ColumnName = "Response_Size", IsNullable = false, ColumnDescription = "响应大小")]
    public long ResponseSize { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    [SugarColumn(ColumnName = "Is_Success", IsNullable = false, ColumnDescription = "是否成功")]
    public bool IsSuccess { get; set; } = true;

    /// <summary>
    /// 错误信息
    /// </summary>
    [SugarColumn(ColumnName = "Error_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "错误信息")]
    public string? ErrorMessage { get; set; }
}
```

- [ ] **Step 5: 实现 SysExceptionLog**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysExceptionLog.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Entities;

/// <summary>
/// 异常日志实体
/// </summary>
[SplitTable(SplitType.Month)]
[SugarTable("sys_exception_log_{year}{month}{day}")]
public class SysExceptionLog : SugarCreationEntity<long>, ISplitTableEntity
{
    /// <summary>
    /// 创建时间，同时作为分表字段
    /// </summary>
    [SplitField]
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "创建时间")]
    public override DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 跟踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = false, ColumnDescription = "跟踪标识")]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = true, ColumnDescription = "用户标识")]
    public long? UserId { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnName = "User_Name", Length = 128, IsNullable = true, ColumnDescription = "用户名")]
    public string? UserName { get; set; }

    /// <summary>
    /// 请求路径
    /// </summary>
    [SugarColumn(ColumnName = "Path", Length = 512, IsNullable = true, ColumnDescription = "请求路径")]
    public string? Path { get; set; }

    /// <summary>
    /// 请求方法
    /// </summary>
    [SugarColumn(ColumnName = "Method", Length = 16, IsNullable = true, ColumnDescription = "请求方法")]
    public string? Method { get; set; }

    /// <summary>
    /// 控制器
    /// </summary>
    [SugarColumn(ColumnName = "Controller_Name", Length = 256, IsNullable = true, ColumnDescription = "控制器")]
    public string? ControllerName { get; set; }

    /// <summary>
    /// 动作
    /// </summary>
    [SugarColumn(ColumnName = "Action_Name", Length = 256, IsNullable = true, ColumnDescription = "动作")]
    public string? ActionName { get; set; }

    /// <summary>
    /// 状态码
    /// </summary>
    [SugarColumn(ColumnName = "Status_Code", IsNullable = false, ColumnDescription = "状态码")]
    public int StatusCode { get; set; }

    /// <summary>
    /// 异常类型
    /// </summary>
    [SugarColumn(ColumnName = "Exception_Type", Length = 512, IsNullable = false, ColumnDescription = "异常类型")]
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>
    /// 异常消息
    /// </summary>
    [SugarColumn(ColumnName = "Exception_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "异常消息")]
    public string ExceptionMessage { get; set; } = string.Empty;

    /// <summary>
    /// 异常堆栈
    /// </summary>
    [SugarColumn(ColumnName = "Exception_Stack_Trace", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "异常堆栈")]
    public string? ExceptionStackTrace { get; set; }

    /// <summary>
    /// 请求头
    /// </summary>
    [SugarColumn(ColumnName = "Request_Headers", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求头")]
    public string? RequestHeaders { get; set; }

    /// <summary>
    /// 请求参数
    /// </summary>
    [SugarColumn(ColumnName = "Request_Params", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求参数")]
    public string? RequestParams { get; set; }

    /// <summary>
    /// 请求体
    /// </summary>
    [SugarColumn(ColumnName = "Request_Body", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "请求体")]
    public string? RequestBody { get; set; }

    /// <summary>
    /// 来源地址
    /// </summary>
    [SugarColumn(ColumnName = "Remote_Ip", Length = 64, IsNullable = true, ColumnDescription = "来源地址")]
    public string? RemoteIp { get; set; }

    /// <summary>
    /// 用户代理
    /// </summary>
    [SugarColumn(ColumnName = "User_Agent", Length = 512, IsNullable = true, ColumnDescription = "用户代理")]
    public string? UserAgent { get; set; }
}
```

- [ ] **Step 6: 实现 SysLoginLog**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysLoginLog.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Entities;

/// <summary>
/// 登录日志实体
/// </summary>
[SplitTable(SplitType.Month)]
[SugarTable("sys_login_log_{year}{month}{day}")]
public class SysLoginLog : SugarCreationEntity<long>, ISplitTableEntity
{
    /// <summary>
    /// 创建时间，同时作为分表字段
    /// </summary>
    [SplitField]
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "创建时间")]
    public override DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 跟踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = true, ColumnDescription = "跟踪标识")]
    public string? TraceId { get; set; }

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", IsNullable = true, ColumnDescription = "用户标识")]
    public long? UserId { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnName = "User_Name", Length = 128, IsNullable = true, ColumnDescription = "用户名")]
    public string? UserName { get; set; }

    /// <summary>
    /// 会话标识
    /// </summary>
    [SugarColumn(ColumnName = "Session_Id", Length = 64, IsNullable = true, ColumnDescription = "会话标识")]
    public string? SessionId { get; set; }

    /// <summary>
    /// 登录结果
    /// </summary>
    [SugarColumn(ColumnName = "Login_Result", IsNullable = false, ColumnDescription = "登录结果")]
    public int LoginResult { get; set; }

    /// <summary>
    /// 结果消息
    /// </summary>
    [SugarColumn(ColumnName = "Message", Length = 512, IsNullable = true, ColumnDescription = "结果消息")]
    public string? Message { get; set; }

    /// <summary>
    /// 登录地址
    /// </summary>
    [SugarColumn(ColumnName = "Login_Ip", Length = 64, IsNullable = true, ColumnDescription = "登录地址")]
    public string? LoginIp { get; set; }

    /// <summary>
    /// 用户代理
    /// </summary>
    [SugarColumn(ColumnName = "User_Agent", Length = 512, IsNullable = true, ColumnDescription = "用户代理")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// 设备标识
    /// </summary>
    [SugarColumn(ColumnName = "Device_Id", Length = 128, IsNullable = true, ColumnDescription = "设备标识")]
    public string? DeviceId { get; set; }

    /// <summary>
    /// 登录时间
    /// </summary>
    [SugarColumn(ColumnName = "Login_Time", IsNullable = false, ColumnDescription = "登录时间")]
    public DateTimeOffset LoginTime { get; set; }
}
```

- [ ] **Step 7: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 8: 验证全解决方案 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：0 Warning(s) 0 Error(s)。

- [ ] **Step 9: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar/Entities framework/test/XiHan.Framework.Auditing.SqlSugar.Tests
git commit -m "feat(auditing-sqlsugar): 新增访问、接口、异常、登录日志实体"
```

---

### Task 4: SQLite 建表集成测试

**Files:**
- Create: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/TableInitializationTests.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj`（加 SQLite 包引用）

**Interfaces:**
- Consumes: Task 2、Task 3 的 5 个实体类型
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- SQLite 测试用法：`framework/test/XiHan.Framework.Data.Tests/` 下引用 SQLite 的测试文件
- 分表建表 API：`/e/source/platfrom-admin/docs/SqlSugar-docs/自動分表.md` 第二节
- 框架建表实现：`framework/src/XiHan.Framework.Data/SqlSugar/Initializers/DbInitializer.cs:384-386`

**本任务禁止事项：** 不要用 EF Core 的 `EnsureCreated()` / `Migrate()`。建表只能走 `CodeFirst.SplitTables().InitTables(...)`。不要连接真实 MySQL——真库测试属于后续计划。

- [ ] **Step 1: 给测试项目加 SQLite 引用**

在 `XiHan.Framework.Auditing.SqlSugar.Tests.csproj` 的 `ItemGroup` 中追加（版本与 `XiHan.Framework.Data` 的传递依赖一致）：

```xml
        <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.9" />
```

若还原报版本冲突，以 `dotnet list package --include-transitive` 查出 `XiHan.Framework.Data` 实际传递的 `Microsoft.Data.Sqlite` 版本并对齐。

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/TableInitializationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;

namespace XiHan.Framework.Auditing.SqlSugar.Tests;

/// <summary>
/// 日志表建表测试
/// </summary>
public class TableInitializationTests
{
    /// <summary>
    /// 五类日志实体都能建出当月分表
    /// </summary>
    [Fact]
    public void 五类日志实体都能建出当月分表()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_auditing_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databaseFile}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

            Type[] entityTypes =
            [
                typeof(SysAccessLog),
                typeof(SysApiLog),
                typeof(SysExceptionLog),
                typeof(SysLoginLog),
                typeof(SysOperationLog)
            ];

            foreach (var entityType in entityTypes)
            {
                db.CodeFirst.SplitTables().InitTables(entityType);
            }

            var tableNames = db.DbMaintenance.GetTableInfoList(false)
                .Select(table => table.Name)
                .ToList();

            Assert.Contains(tableNames, name => name.StartsWith("sys_access_log_", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(tableNames, name => name.StartsWith("sys_api_log_", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(tableNames, name => name.StartsWith("sys_exception_log_", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(tableNames, name => name.StartsWith("sys_login_log_", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(tableNames, name => name.StartsWith("sys_operation_log_", StringComparison.OrdinalIgnoreCase));
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
    /// 操作日志写入后能按时间区间查回
    /// </summary>
    [Fact]
    public void 操作日志写入后能按时间区间查回()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_auditing_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databaseFile}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

            db.CodeFirst.SplitTables().InitTables(typeof(SysOperationLog));

            var now = DateTimeOffset.UtcNow;
            var log = new SysOperationLog
            {
                CreatedTime = now,
                TraceId = "trace-1",
                Method = "POST",
                Path = "/Order",
                StatusCode = 200,
                ElapsedMilliseconds = 12
            };

            db.Insertable(log).SplitTable().ExecuteCommand();

            var found = db.Queryable<SysOperationLog>()
                .SplitTable(now.DateTime.AddDays(-1), now.DateTime.AddDays(1))
                .Where(item => item.TraceId == "trace-1")
                .ToList();

            Assert.Single(found);
            Assert.Equal("/Order", found[0].Path);
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

- [ ] **Step 3: 运行测试**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `Basic_Id` 因未赋值导致主键冲突，在测试里显式赋一个 `long` 值（例如 `DateTime.UtcNow.Ticks`）；雪花 ID 的接入属于 P2 的写入器任务，本任务不引入 `DistributedIds` 依赖。

- [ ] **Step 4: 验证全解决方案构建与测试**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 警告 0 错误；全部测试通过。

- [ ] **Step 5: 提交**

```bash
git add framework/test/XiHan.Framework.Auditing.SqlSugar.Tests
git commit -m "test(auditing-sqlsugar): 新增 SQLite 建表与分表读写集成测试"
```

---

## 完成标准

P1 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 5 个日志实体存在，表名带 `sys_` 前缀与三个分表变量，分表字段为 `Created_Time`
- SQLite 下能建出当月分表并完成一次写入与区间查询
- 5 个 `Null*Writer` **仍未被替换**——写入器实现属于 P2

## 下一份计划（P2，本计划完成后再写）

`Auditing.SqlSugar` 的 5 个写入器实现、雪花 ID 接入、记录模型到实体的映射、DI 注册替换 `Null*Writer`、文档（`docs/packages/`、VitePress 侧边栏、根 README）。完成后 PR1 可提交。
