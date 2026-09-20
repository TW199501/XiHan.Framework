# EventBus.SqlSugar 包骨架与发件箱实体映射（P3）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立 `XiHan.Framework.EventBus.SqlSugar` 包骨架、定义 `sys_event_outbox` 实体、实现 `OutgoingEventInfo` 与实体的双向映射；`IEventOutbox` 的实现留给 P4。

**Architecture:** 沿用仓库既有兄弟子包模式（`EventBus.Redis`）。`OutgoingEventInfo` 属性只读、无参构造是 protected，不能当实体，因此另立实体类加双向映射，反向走它的公开四参构造函数。表不分表——发件箱是短命队列。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-21-eventbus-sqlsugar-p3-outbox-entity-design.md`

> 该 spec 自成一体，实现 P3 所需的全部约束都在其中。**不要**去读 `2026-09-21-sqlsugar-persistence-design.md`——那是拆分前的总纲，已停用。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-sqlsugar`）。worktree 内看不到这些文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录**：`E:/source/XiHan/XiHan.Framework-sqlsugar`（分支 `feat/sqlsugar`）。

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

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 表名 `sys_` 前缀、全小写下划线；列名 Pascal_Snake_Case（`Event_Name`、`Claim_Token`），每列带 `ColumnDescription` 简体中文说明

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台限制**：Microsoft.Testing.Platform，不是 VSTest。**没有可用的筛选参数**，`--filter`/`--list-tests` 返回退出码 3；**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。要跑单个测试类就整个项目跑。

**提交信息**：中文 Conventional Commits，作用域 `eventbus-sqlsugar`。**不加任何 AI 署名。**

---

## 本计划特有的五条硬约束

**① 主键是 `Guid`，不是雪花 `long`——与本系列 P1/P2 的约定不同。**

`IOutgoingEventInfo.Id` 是 `Guid`，`IEventOutbox.DeleteAsync(Guid id)` 按它定位。把它设成主键，删除就是主键查找。事件 `Id` 由 `DistributedEventBusBase.GuidGenerator`（`IDistributedIdGenerator<Guid>`，注册的是**顺序 Guid** 生成器）产生，没有索引碎片问题。**照搬 P1/P2 的雪花 long 主键会做错。**

**② 表不分表。**

P1/P2 的日志表按月分表，本表**不分**。发件箱是短命队列，发完即删，分表只会让 P4 的抢占跨表。不要因为「前两份都分表了」就顺手加 `[SplitTable]`。

**③ 主键只能经构造函数传入。**

`EntityBase<TKey>.BasicId` 是 `{ get; protected set; }`，不能用对象初始化器赋值。实体需要两个公开构造函数：无参的供 SqlSugar 物化，`(Guid basicId)` 的供映射使用。

**④ `DateTime` ↔ `DateTimeOffset` 往返归一到 UTC。**

契约的 `CreatedTime` 是 `DateTime`，实体列是 `DateTimeOffset`。入库按 `Kind` 分支，出库取 `.UtcDateTime`。往返**不是**逐位相等——原本 `Local` 的时间回来变成等价的 UTC。这是有意的归一，测试要断言它，**不要试图「修」成保留原 Kind**。

**⑤ `ExtraProperties` 的 JSON 往返不保留原始类型。**

`ExtraPropertyDictionary` 是 `Dictionary<string, object?>`，`System.Text.Json` 往返后值变成 `JsonElement`。**能保证的契约是 `GetCorrelationId()` 往返保真**（它内部 `?.ToString()`），不是字典深度相等。测试按前者断言。

---

## File Structure

```
framework/src/XiHan.Framework.EventBus.SqlSugar/
  XiHan.Framework.EventBus.SqlSugar.csproj
  XiHanSqlSugarEventBusModule.cs      P3 阶段为空装配
  README.md                           固定七段结构
  Entities/SysEventOutbox.cs
  Mapping/EventOutboxMapper.cs

framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/
  XiHan.Framework.EventBus.SqlSugar.Tests.csproj
  EventOutboxEntityTests.cs     实体元数据断言
  EventOutboxMapperTests.cs     映射往返，不碰数据库
  TableInitializationTests.cs   SQLite 建表与二进制往返
```

---

### Task 1: 包骨架与解决方案注册

**Files:**
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: 无
- Produces: 程序集 `XiHan.Framework.EventBus.SqlSugar`，根命名空间同名；模块类型 `XiHanSqlSugarEventBusModule`

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.Redis/XiHan.Framework.EventBus.Redis.csproj`
- 模块类范本与命名惯例：`framework/src/XiHan.Framework.EventBus.Kafka/XiHanKafkaEventBusModule.cs`
- README 七段结构：`framework/src/XiHan.Framework.Data/README.md`

**本任务禁止事项：** 不要写任何服务注册逻辑，本任务的模块类是空装配。不要新增 `PackageReference`——SqlSugar 经 `XiHan.Framework.Data` 传递引入。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.EventBus.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.EventBus.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.EventBus.SqlSugar</PackageId>
        <Description>曦寒框架分布式事件总线收发件箱 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.EventBus\XiHan.Framework.EventBus.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

四个 props 的 Import 顺序不能变。

- [ ] **Step 2: 创建模块类**

`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.EventBus.SqlSugar;

/// <summary>
/// 曦寒框架分布式事件总线 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanSqlSugarEventBusModule))]</c> 即启用。
/// 本模块提供收发件箱的 SqlSugar 实体定义；收发件箱实现在后续版本提供。
/// </remarks>
[DependsOn(
    typeof(XiHanEventBusModule),
    typeof(XiHanDataModule)
)]
public class XiHanSqlSugarEventBusModule : XiHanModule
{
}
```

- [ ] **Step 3: 注册进解决方案**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、`XiHan.Framework.EventBus.Redis` 那一行之后插入：

```xml
    <Project Path="src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README**

`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`，沿用固定七段结构。三反引号照常写，下面为避免嵌套用四空格缩进表示代码块：

```markdown
# XiHan.Framework.EventBus.SqlSugar

## 概述

`XiHan.Framework.EventBus` 的收发件箱 SqlSugar 持久化提供程序。默认的 `DefaultEventOutbox` 是进程内实现，事件与业务数据不在同一事务、进程退出即丢；本包把它们落到数据库。

## 核心能力

- 发件箱实体 `sys_event_outbox` 与 `OutgoingEventInfo` 的双向映射
- 表结构由 `DbInitializer` 在应用启动时创建

## 依赖关系

依赖 `XiHan.Framework.EventBus`（收发件箱契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为事件自身的 `Guid` 标识，非自增。

## 使用方式

在应用启动模块上声明依赖 `XiHanSqlSugarEventBusModule`。

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.EventBus.Abstractions.Distributed` 下的 `IEventOutbox` / `IEventInbox` 并在 DI 中替换。

## 目录结构

    Entities/   收发件箱实体
    Mapping/    契约与实体的双向映射
```

- [ ] **Step 5: 验证构建 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/XiHan.Framework.slnx
git commit -m "feat(eventbus-sqlsugar): 新增包骨架与模块装配"
```

---

### Task 2: 发件箱实体

**Files:**
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventOutboxEntityTests.cs`
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: Task 1 的程序集
- Produces: `SysEventOutbox`（`XiHan.Framework.EventBus.SqlSugar.Entities`），继承 `SugarEntity<Guid>`；公开构造函数两个——`SysEventOutbox()` 与 `SysEventOutbox(Guid basicId)`；属性 `EventName`、`EventData`、`CreatedTime`、`ExtraProperties`、`Status`、`ClaimToken`、`ClaimTime`

**参考来源（动手前先读）：**
- 列映射范本：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarEntity.cs`（`Basic_Id` / `Row_Version` 的写法与 `protected set`）
- 完整列映射示例：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarFullAuditedEntity.cs`
- 契约字段来源：`framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/OutgoingEventInfo.cs`（注意 `MaxEventNameLength = 256`）
- 二进制列映射：`/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Abstract/CodeFirstProvider/CodeFirstProvider.cs:733`
- 大文本常量：`/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Infrastructure/StaticConfig.cs:16`
- 测试项目范本：`framework/test/XiHan.Framework.EventBus.Redis.Tests/XiHan.Framework.EventBus.Redis.Tests.csproj`

**本任务禁止事项：** 见本计划硬约束 ①②③。另外不要给 `Event_Data` 指定 `ColumnDataType`——`byte[]` 由 SqlSugar 各方言自行映射为 blob。不要把 `Status` 做成枚举列，保持 `int`（P4 的抢占 `UPDATE` 用它做条件）。

- [ ] **Step 1: 创建测试项目**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\test.props" />

    <PropertyGroup>
        <AssemblyName>XiHan.Framework.EventBus.SqlSugar.Tests</AssemblyName>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.EventBus.SqlSugar\XiHan.Framework.EventBus.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

在 `framework/XiHan.Framework.slnx` 的测试文件夹内、`XiHan.Framework.EventBus.Redis.Tests` 之后插入：

```xml
    <Project Path="test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventOutboxEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱实体映射测试
/// </summary>
public class EventOutboxEntityTests
{
    /// <summary>
    /// 表名带 sys 前缀且不含分表变量
    /// </summary>
    [Fact]
    public void 表名带前缀且不含分表变量()
    {
        var table = typeof(SysEventOutbox).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_event_outbox", table.TableName);
    }

    /// <summary>
    /// 发件箱不分表
    /// </summary>
    [Fact]
    public void 发件箱不分表()
    {
        Assert.Null(typeof(SysEventOutbox).GetCustomAttribute<SplitTableAttribute>());
    }

    /// <summary>
    /// 主键是事件自身的标识且非自增
    /// </summary>
    [Fact]
    public void 主键是事件标识且非自增()
    {
        var property = typeof(SysEventOutbox).GetProperty(nameof(SysEventOutbox.BasicId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.Equal(typeof(Guid), property.PropertyType);
        Assert.NotNull(column);
        Assert.True(column.IsPrimaryKey);
        Assert.False(column.IsIdentity);
    }

    /// <summary>
    /// 事件名长度上限与契约一致
    /// </summary>
    [Fact]
    public void 事件名长度上限与契约一致()
    {
        var property = typeof(SysEventOutbox).GetProperty(nameof(SysEventOutbox.EventName));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Event_Name", column.ColumnName);
        Assert.Equal(256, column.Length);
    }

    /// <summary>
    /// 列名使用帕斯卡下划线
    /// </summary>
    [Theory]
    [InlineData(nameof(SysEventOutbox.EventData), "Event_Data")]
    [InlineData(nameof(SysEventOutbox.CreatedTime), "Created_Time")]
    [InlineData(nameof(SysEventOutbox.ExtraProperties), "Extra_Properties")]
    [InlineData(nameof(SysEventOutbox.Status), "Status")]
    [InlineData(nameof(SysEventOutbox.ClaimToken), "Claim_Token")]
    [InlineData(nameof(SysEventOutbox.ClaimTime), "Claim_Time")]
    public void 列名使用帕斯卡下划线(string propertyName, string expectedColumnName)
    {
        var property = typeof(SysEventOutbox).GetProperty(propertyName);
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal(expectedColumnName, column.ColumnName);
    }

    /// <summary>
    /// 主键经构造函数传入
    /// </summary>
    [Fact]
    public void 主键经构造函数传入()
    {
        var id = Guid.NewGuid();

        var entity = new SysEventOutbox(id);

        Assert.Equal(id, entity.BasicId);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysEventOutbox` 类型不存在。

- [ ] **Step 4: 实现实体**

`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Entities;

/// <summary>
/// 发件箱实体
/// </summary>
[SugarTable("sys_event_outbox")]
public class SysEventOutbox : SugarEntity<Guid>
{
    /// <summary>
    /// 待发送状态
    /// </summary>
    public const int StatusPending = 0;

    /// <summary>
    /// 已领取状态
    /// </summary>
    public const int StatusClaimed = 1;

    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysEventOutbox() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">事件唯一标识</param>
    public SysEventOutbox(Guid basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 事件名称
    /// </summary>
    [SugarColumn(ColumnName = "Event_Name", Length = 256, IsNullable = false, ColumnDescription = "事件名称")]
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// 序列化后的事件数据
    /// </summary>
    [SugarColumn(ColumnName = "Event_Data", IsNullable = false, ColumnDescription = "序列化后的事件数据")]
    public byte[] EventData { get; set; } = [];

    /// <summary>
    /// 事件创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "事件创建时间")]
    public DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 扩展属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Extra_Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "扩展属性的 JSON")]
    public string? ExtraProperties { get; set; }

    /// <summary>
    /// 发送状态，0 待发送，1 已领取
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "发送状态，0 待发送，1 已领取")]
    public int Status { get; set; }

    /// <summary>
    /// 领取令牌
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Token", Length = 64, IsNullable = true, ColumnDescription = "领取令牌")]
    public string? ClaimToken { get; set; }

    /// <summary>
    /// 领取时刻
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Time", IsNullable = true, ColumnDescription = "领取时刻")]
    public DateTimeOffset? ClaimTime { get; set; }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 6: 验证构建 0 警告并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(eventbus-sqlsugar): 新增发件箱实体"
```

---

### Task 3: 双向映射

**Files:**
- Create: `framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventOutboxMapper.cs`
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventOutboxMapperTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysEventOutbox`
- Produces: `public static class EventOutboxMapper`，两个方法——`ToEntity(OutgoingEventInfo info) → SysEventOutbox`、`ToEventInfo(SysEventOutbox entity) → OutgoingEventInfo`

**参考来源（动手前先读）：**
- 契约与公开构造函数：`framework/src/XiHan.Framework.EventBus.Abstractions/Distributed/OutgoingEventInfo.cs`（公开四参构造 `(Guid id, string eventName, byte[] eventData, DateTime createdTime)`；`ExtraProperties` getter 公开）
- 关联标识常量：`framework/src/XiHan.Framework.EventBus.Abstractions/EventBusConsts.cs:14`（`CorrelationIdHeaderName = "X-Correlation-Id"`）
- 扩展属性字典类型：`framework/src/XiHan.Framework.ObjectMapping/Extensions/Data/ExtraPropertyDictionary.cs`（就是 `Dictionary<string, object?>`）
- 时钟语义：`framework/src/XiHan.Framework.Timing/Clock.cs:33`（`Now` 依 `Options.Kind` 返回 `DateTime.UtcNow` 或 `DateTime.Now`，`Kind` 恒有值）

**本任务禁止事项：** 见本计划硬约束 ④⑤。另外不要改 `OutgoingEventInfo`。不要引入映射框架。不要把 `Status` / `ClaimToken` / `ClaimTime` 纳入映射——它们是存储侧状态，不属于事件本身。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/EventOutboxMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Mapping;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱映射测试
/// </summary>
public class EventOutboxMapperTests
{
    /// <summary>
    /// 事件标识、名称与数据往返保真
    /// </summary>
    [Fact]
    public void 事件标识名称与数据往返保真()
    {
        var id = Guid.NewGuid();
        byte[] data = [1, 2, 3, 250];
        var info = new OutgoingEventInfo(id, "Order.Created", data, DateTime.UtcNow);

        var restored = EventOutboxMapper.ToEventInfo(EventOutboxMapper.ToEntity(info));

        Assert.Equal(id, restored.Id);
        Assert.Equal("Order.Created", restored.EventName);
        Assert.Equal(data, restored.EventData);
    }

    /// <summary>
    /// 协调世界时往返后时刻不变且类型为协调世界时
    /// </summary>
    [Fact]
    public void 协调世界时往返后时刻不变()
    {
        var created = new DateTime(2026, 9, 21, 10, 30, 0, DateTimeKind.Utc);
        var info = new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], created);

        var restored = EventOutboxMapper.ToEventInfo(EventOutboxMapper.ToEntity(info));

        Assert.Equal(DateTimeKind.Utc, restored.CreatedTime.Kind);
        Assert.Equal(created, restored.CreatedTime);
    }

    /// <summary>
    /// 本地时间往返后归一为协调世界时且时刻等价
    /// </summary>
    [Fact]
    public void 本地时间往返后归一为协调世界时()
    {
        var created = new DateTime(2026, 9, 21, 10, 30, 0, DateTimeKind.Local);
        var info = new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], created);

        var restored = EventOutboxMapper.ToEventInfo(EventOutboxMapper.ToEntity(info));

        Assert.Equal(DateTimeKind.Utc, restored.CreatedTime.Kind);
        Assert.Equal(created.ToUniversalTime(), restored.CreatedTime);
    }

    /// <summary>
    /// 关联标识往返保真
    /// </summary>
    [Fact]
    public void 关联标识往返保真()
    {
        var info = new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], DateTime.UtcNow);
        info.SetCorrelationId("corr-123");

        var restored = EventOutboxMapper.ToEventInfo(EventOutboxMapper.ToEntity(info));

        Assert.Equal("corr-123", restored.GetCorrelationId());
    }

    /// <summary>
    /// 没有扩展属性时往返不抛异常
    /// </summary>
    [Fact]
    public void 没有扩展属性时往返不抛异常()
    {
        var info = new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], DateTime.UtcNow);

        var restored = EventOutboxMapper.ToEventInfo(EventOutboxMapper.ToEntity(info));

        Assert.Null(restored.GetCorrelationId());
    }

    /// <summary>
    /// 入库时状态为待发送且未被领取
    /// </summary>
    [Fact]
    public void 入库时状态为待发送且未被领取()
    {
        var info = new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", [1], DateTime.UtcNow);

        var entity = EventOutboxMapper.ToEntity(info);

        Assert.Equal(SysEventOutbox.StatusPending, entity.Status);
        Assert.Null(entity.ClaimToken);
        Assert.Null(entity.ClaimTime);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`EventOutboxMapper` 不存在。

- [ ] **Step 3: 实现映射器**

`framework/src/XiHan.Framework.EventBus.SqlSugar/Mapping/EventOutboxMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;

namespace XiHan.Framework.EventBus.SqlSugar.Mapping;

/// <summary>
/// 发件箱契约与实体的双向映射
/// </summary>
public static class EventOutboxMapper
{
    /// <summary>
    /// 把出站事件信息转换为实体
    /// </summary>
    /// <param name="info">出站事件信息</param>
    /// <returns>发件箱实体</returns>
    public static SysEventOutbox ToEntity(OutgoingEventInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new SysEventOutbox(info.Id)
        {
            EventName = info.EventName,
            EventData = info.EventData,
            CreatedTime = ToOffset(info.CreatedTime),
            ExtraProperties = info.ExtraProperties.Count == 0
                ? null
                : JsonSerializer.Serialize(info.ExtraProperties),
            Status = SysEventOutbox.StatusPending,
            ClaimToken = null,
            ClaimTime = null
        };
    }

    /// <summary>
    /// 把实体转换为出站事件信息
    /// </summary>
    /// <param name="entity">发件箱实体</param>
    /// <returns>出站事件信息</returns>
    public static OutgoingEventInfo ToEventInfo(SysEventOutbox entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var info = new OutgoingEventInfo(
            entity.BasicId,
            entity.EventName,
            entity.EventData,
            entity.CreatedTime.UtcDateTime);

        if (string.IsNullOrWhiteSpace(entity.ExtraProperties))
        {
            return info;
        }

        var properties = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.ExtraProperties);
        if (properties is null)
        {
            return info;
        }

        foreach (var pair in properties)
        {
            info.ExtraProperties[pair.Key] = pair.Value;
        }

        return info;
    }

    /// <summary>
    /// 按时间类型转换为带偏移的时间，未指定类型的按协调世界时处理
    /// </summary>
    /// <param name="value">时间</param>
    /// <returns>带偏移的时间</returns>
    private static DateTimeOffset ToOffset(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero)
        };
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `关联标识往返保真` 失败且实际值是 `JsonElement` 的字符串表示（带引号），说明反序列化出的 `JsonElement` 的 `ToString()` 行为与预期不同——此时在 `ToEventInfo` 里把 `JsonElement` 且 `ValueKind == JsonValueKind.String` 的值取 `GetString()` 后再写入字典。这是已知的类型保真边界，见硬约束 ⑤。

- [ ] **Step 5: 验证构建 0 警告并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.EventBus.SqlSugar framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "feat(eventbus-sqlsugar): 新增发件箱契约与实体的双向映射"
```

---

### Task 4: SQLite 建表与二进制往返

**Files:**
- Create: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/TableInitializationTests.cs`
- Modify: `framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`

**Interfaces:**
- Consumes: Task 2 的实体、Task 3 的映射
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- SQLite 测试用法：`framework/test/XiHan.Framework.Data.Tests/` 下引用 SQLite 的测试文件
- 建表 API：`/e/source/platfrom-admin/docs/SqlSugar-docs/庫表管理DbMaintenance.md`
- 二进制列映射：`/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/Abstract/CodeFirstProvider/CodeFirstProvider.cs:733`

**本任务禁止事项：** 不要用 EF Core 的 `EnsureCreated()` / `Migrate()`。建表只能走 `CodeFirst.InitTables(...)`——**不要**加 `SplitTables()`，本表不分表。不要连接真实数据库。

- [ ] **Step 1: 给测试项目加 SQLite 引用**

在 `XiHan.Framework.EventBus.SqlSugar.Tests.csproj` 的 `ItemGroup` 中追加：

```xml
        <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.9" />
```

若还原报版本冲突，以 `dotnet list package --include-transitive` 查出 `XiHan.Framework.Data` 实际传递的 `Microsoft.Data.Sqlite` 版本并对齐。

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/TableInitializationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.SqlSugar.Entities;
using XiHan.Framework.EventBus.SqlSugar.Mapping;

namespace XiHan.Framework.EventBus.SqlSugar.Tests;

/// <summary>
/// 发件箱建表测试
/// </summary>
public class TableInitializationTests
{
    /// <summary>
    /// 发件箱表能建出来
    /// </summary>
    [Fact]
    public void 发件箱表能建出来()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_outbox_{Guid.NewGuid():N}.db");

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.InitTables(typeof(SysEventOutbox));

            var tableNames = db.DbMaintenance.GetTableInfoList(false)
                .Select(table => table.Name)
                .ToList();

            Assert.Contains(tableNames, name => string.Equals(name, "sys_event_outbox", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteQuietly(databaseFile);
        }
    }

    /// <summary>
    /// 事件数据以二进制往返后逐字节相等
    /// </summary>
    [Fact]
    public void 事件数据以二进制往返后逐字节相等()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_outbox_{Guid.NewGuid():N}.db");

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.InitTables(typeof(SysEventOutbox));

            byte[] data = [0, 1, 127, 128, 255];
            var info = new OutgoingEventInfo(Guid.NewGuid(), "Order.Created", data, DateTime.UtcNow);
            info.SetCorrelationId("corr-db");

            db.Insertable(EventOutboxMapper.ToEntity(info)).ExecuteCommand();

            var stored = db.Queryable<SysEventOutbox>()
                .Where(item => item.BasicId == info.Id)
                .First();

            Assert.NotNull(stored);
            Assert.Equal(data, stored.EventData);

            var restored = EventOutboxMapper.ToEventInfo(stored);

            Assert.Equal(info.Id, restored.Id);
            Assert.Equal("Order.Created", restored.EventName);
            Assert.Equal("corr-db", restored.GetCorrelationId());
            Assert.Equal(SysEventOutbox.StatusPending, stored.Status);
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
            ConnectionString = $"Data Source={databaseFile}",
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

- [ ] **Step 3: 运行测试**

```bash
dotnet test --project framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 SQLite 下 `Guid` 主键查询不命中，检查 `ConnectionConfig` 是否需要 `MoreSettings` 调整 Guid 存储形式；以实际行为为准，不要改成字符串主键——那会偏离契约的 `Guid` 身份。

- [ ] **Step 4: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 Warning(s) 0 Error(s)；全部测试通过。

- [ ] **Step 5: 复查注释是否混入论证**

通读本任务链新增的 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [ ] **Step 6: 提交**

```bash
git add framework/test/XiHan.Framework.EventBus.SqlSugar.Tests
git commit -m "test(eventbus-sqlsugar): 新增 SQLite 建表与二进制往返集成测试"
```

---

## 完成标准

P3 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `sys_event_outbox` 能在 SQLite 下建出来，**不分表**
- 主键为 `Guid`，非自增，经构造函数传入
- 映射往返：`Id`、`EventName`、`EventData` 保真；`CreatedTime` 归一到 UTC；`GetCorrelationId()` 保真
- `DefaultEventOutbox` **仍是容器里的实现**——入箱与领取属 P4

## 已知边界（写入 PR 描述，不写进代码注释）

- **`ExtraProperties` 类型保真**：JSON 往返后值为 `JsonElement`。框架目前只用它存关联标识，够用；若日后要存复杂对象需带类型信息。
- **时间归一**：`Local` 时间往返后变 UTC，时刻等价但 `Kind` 不同。
- **状态列未使用**：`Status` / `Claim_Token` / `Claim_Time` 在 P3 只有定义，P4 才赋予行为。
- **发件箱尚未生效**：P3 结束时事件仍在内存。

## 下一份计划（P4，本计划完成后再写）

`IEventOutbox` 实现：入箱的事务参与（必须用工作单元 scope 的客户端，自建连接会退化成两个独立事务且测试照样通过）与原子领取（条件 `UPDATE` 抢占 + 超时释放）。
