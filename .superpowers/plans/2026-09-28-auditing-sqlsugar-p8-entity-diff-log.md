# Auditing.SqlSugar 实体差异日志写入器（P8）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在已存在的 `Auditing.SqlSugar` 包里补上第 6 个实体（`SysDiffLog`）与第 6 个写入器（`SqlSugarEntityDiffLogWriter`），用 `services.Replace` 顶替 `NullEntityDiffLogWriter`；完成后 PR1（`Auditing.SqlSugar`）整体完成，可提交给上游。

**Architecture:** 与 P1/P2 建立的形状完全一致——实体按月分表、映射是纯静态函数、写入器经 `ISqlSugarClientResolver` 取客户端 + `IDistributedIdGenerator<long>` 取雪花主键、`Insertable(...).SplitTable().ExecuteCommandAsync()` 落库。**唯一的结构性差异**：本写入器经 `GetCurrentClient()` 而非 `GetClientForEntity<T>()` 取客户端——差异日志必须与触发它的业务写入同一个工作单元的连接，业务回滚时差异日志随之回滚，这是 `SqlSugarDiffLogAop` 与 `docs/guide/auditing.md` 明确写死的契约，不是本计划的设计选择。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-auditing-sqlsugar-p8-entity-diff-log-design.md`

> 该 spec 自成一体，实现 P8 所需的全部约束都在其中。

**Linear 议题：** `https://linear.app/elf-express/issue/EDDIE-5`

**前置:** P1（`.superpowers/plans/2026-09-21-auditing-sqlsugar-p1-entities.md`）与 P2（`.superpowers/plans/2026-09-21-auditing-sqlsugar-p2-writers.md`）必须已完成——本计划依赖它们产出的实体基类、`AuditingLogMapper`、注册扩展与测试项目。

---

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录**：`E:/source/XiHan/XiHan.Framework`，分支 `dev`。这**不是新包**，不需要 `git worktree`——`Auditing.SqlSugar` 已经在解决方案里，本计划只在既有包内新增文件，不新建 csproj、不改 `XiHan.Framework.slnx`。

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

**已核对的签名**（本计划用到的全部 SqlSugar API，均已在 P1/P2 的 CI 通过记录里验证过，不重复读源码）：

```csharp
// framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs:22
ISqlSugarClient GetCurrentClient();
// framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs:33
ISqlSugarClient GetClientForEntity(Type entityType);

// P1/P2 已验证可用，本计划逐字复用：
client.Insertable(entity).SplitTable().ExecuteCommandAsync();
db.CodeFirst.SplitTables().InitTables(typeof(TEntity));
db.Queryable<TEntity>().SplitTable(begin, end).Where(...).ToList();
```

**新核对的签名**（`SugarColumn` 特性类，`E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Entities/Mapping/SugarMappingAttribute.cs:86-91`）：

```csharp
private int _Length;
public int Length
{
    get { return _Length; }
    set { _Length = value; }
}
```

`_Length` 是 `int` 字段，没有显式初始化器，C# 对象初始化器语法（`[SugarColumn(...)]` 只设置了部分属性）不会触碰未提及的属性，因此未显式设置 `Length` 的列，该属性值就是 `int` 的默认值 `0`。Task 1 里 `SysDiffLog_大文本列用BigString且不设长度` 断言 `column.Length == 0` 由此成立。

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台限制**：Microsoft.Testing.Platform，不是 VSTest。**没有可用的筛选参数**，`--filter`/`--list-tests` 返回退出码 3；**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。要跑单个测试类就整个项目跑：

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

**SQLite 临时库**：连接串必须带 `Pooling=False`（本测试项目既有的 `CreateClient` 辅助方法已经这样写，直接复用即可）。

**已知的无关抖动**：全量测试（`dotnet test --solution ...`）偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序问题，其源码注释自认会随机变红），与本计划无关，出现时不要花时间排查。

**提交信息**：中文 Conventional Commits，作用域 `auditing-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

---

## 本计划特有的硬约束

**① 客户端解析方法用 `GetCurrentClient()`，绝对不能用 `GetClientForEntity<SysDiffLog>()`。**

这是本计划与 P2 五个写入器**唯一**的结构性差异，也是最容易顺手抄错的地方——两者在没有活动事务型工作单元时返回值完全相同（见 spec §5②），错了不会让任何"数据有没有落库"的测试失败。Task 2 的测试必须直接断言调用的是哪个方法，不能只断言数据落库。

**② 不要给写入器的 `Insertable(...)` 挂 `.EnableDiffLogEvent(...)`。**

挂了会让写审计的这条 INSERT 再次触发 `SqlSugarDiffLogAop` 的 `OnDiffLogEvent`。P2 的五个写入器本来就没挂这个，本计划延续不挂即可，不需要新增任何防递归代码。

**③ `IEntityDiffLogWriter`/`EntityDiffLogRecord` 在 `XiHan.Framework.Auditing` 根命名空间，不在 `.Writers` 下，且不需要额外 `using`。**

两者所在的 `XiHan.Framework.Auditing` 是本计划新增的 `Writers/SqlSugarEntityDiffLogWriter.cs`（命名空间 `XiHan.Framework.Auditing.SqlSugar.Writers`）与 `Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`（命名空间 `XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection`）的**外层命名空间**，C# 名称解析沿外层链查找，两个文件都不需要为它们添加 `using XiHan.Framework.Auditing;`。这与 P2 五个写入器需要显式 `using XiHan.Framework.Auditing.Writers;`（那是平级命名空间，不在外层链上）是两回事，照抄 P2 的 using 列表会多出一行不必要的 using，实现时留意 IDE 是否提示多余 using。

**④ 不要重做脱敏，不要引入 `IClock`。**

与 P2 硬约束③④相同：`LogSanitizer` 已在 `SqlSugarDiffLogAop` 里对列值做过脱敏，记录到达写入器时已经处理过；创建时间直接用 `DateTimeOffset.UtcNow`。

---

## File Structure

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  Entities/SysDiffLog.cs                            新增：实体
  Mapping/AuditingLogMapper.cs                       修改：追加第 6 个 ToEntity 重载
  Writers/SqlSugarEntityDiffLogWriter.cs             新增：写入器
  Extensions/DependencyInjection/
    XiHanAuditingSqlSugarServiceCollectionExtensions.cs   修改：追加 Replace 注册
  README.md                                          修改：核心能力一节

framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  EntityMappingTests.cs           修改：追加 SysDiffLog 的反射断言
  TableInitializationTests.cs     修改：建表测试数组追加 SysDiffLog
  AuditingLogMapperTests.cs       修改：追加映射纯函数测试
  LogWriterTests.cs               修改：追加写入器测试与顶替断言

docs/packages/auditing-sqlsugar.md   修改：去掉「实体变更日志仍为空实现」的旧说明
```

---

### Task 1: 实体 `SysDiffLog`

**Files:**
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysDiffLog.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/TableInitializationTests.cs`

**Interfaces:**
- Consumes: `SugarCreationEntity<long>`、`ISplitTableEntity`（均来自 `XiHan.Framework.Data`/`XiHan.Framework.Domain`，P1 已在 5 个兄弟实体上用过）
- Produces: `public class SysDiffLog : SugarCreationEntity<long>, ISplitTableEntity`，14 个属性 + 两个公开构造函数（无参、`(long basicId)`）

**参考来源（动手前先读）：**
- 实体字段权威来源：`framework/src/XiHan.Framework.Auditing/EntityDiffLogRecord.cs`（14 个属性，本任务逐一映射）
- 形状范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysOperationLog.cs`（逐字照抄 `SplitTable`/`SugarTable`/基类/两个构造函数的写法，只换字段）
- 测试范本：`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs`、`TableInitializationTests.cs`

**本任务禁止事项：** 不要把 `EntityId`（被审计实体的主键值）与 `BasicId`（这条审计记录自己的雪花主键）混淆——两者语义完全不同，`EntityId` 是普通业务列，不是主键（spec §5③）。不要给 `BeforeData`/`AfterData`/`ChangedFields` 设 `Length`——三者是 `ColumnDataType = StaticConfig.CodeFirst_BigString`，不设长度上限。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs` 的 `EntityMappingTests` 类末尾（`SysLoginLog_登录时间与创建时间分列` 方法之后）追加：

```csharp
    /// <summary>
    /// 差异日志表名带前缀与三个分表变量
    /// </summary>
    [Fact]
    public void SysDiffLog_表名带前缀与三个分表变量()
    {
        var table = typeof(SysDiffLog).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_diff_log_{year}{month}{day}", table.TableName);
    }

    /// <summary>
    /// 差异日志按月分表且分表字段为创建时间
    /// </summary>
    [Fact]
    public void SysDiffLog_按月分表且分表字段为创建时间()
    {
        var split = typeof(SysDiffLog).GetCustomAttribute<SplitTableAttribute>();
        Assert.NotNull(split);
        Assert.Equal(SplitType.Month, split!.SplitType);

        var property = typeof(SysDiffLog).GetProperty(nameof(SysDiffLog.CreatedTime));
        Assert.NotNull(property);
        Assert.NotNull(property!.GetCustomAttribute<SplitFieldAttribute>());

        Assert.True(typeof(ISplitTableEntity).IsAssignableFrom(typeof(SysDiffLog)));
    }

    /// <summary>
    /// 差异日志的实体标识列不是主键，只是普通业务列
    /// </summary>
    [Fact]
    public void SysDiffLog_实体标识列不是主键()
    {
        var property = typeof(SysDiffLog).GetProperty(nameof(SysDiffLog.EntityId));
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Entity_Id", column!.ColumnName);
        Assert.False(column.IsPrimaryKey);
    }

    /// <summary>
    /// 差异日志的大文本列用 BigString 且不设长度上限
    /// </summary>
    [Theory]
    [InlineData(nameof(SysDiffLog.BeforeData), "Before_Data")]
    [InlineData(nameof(SysDiffLog.AfterData), "After_Data")]
    [InlineData(nameof(SysDiffLog.ChangedFields), "Changed_Fields")]
    public void SysDiffLog_大文本列用BigString且不设长度(string propertyName, string expectedColumnName)
    {
        var property = typeof(SysDiffLog).GetProperty(propertyName);
        var column = property!.GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal(expectedColumnName, column!.ColumnName);
        Assert.Equal(StaticConfig.CodeFirst_BigString, column.ColumnDataType);
        Assert.Equal(0, column.Length);
    }

    /// <summary>
    /// 审计类型默认值为 EntityChange
    /// </summary>
    [Fact]
    public void SysDiffLog_审计类型默认值为EntityChange()
    {
        var entity = new SysDiffLog();

        Assert.Equal("EntityChange", entity.AuditType);
    }
```

在 `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/TableInitializationTests.cs` 的 `五类日志实体都能建出当月分表` 测试里，把方法名与实体数组改为：

```csharp
    [Fact]
    public void 六类日志实体都能建出当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            Type[] entityTypes =
            [
                typeof(SysAccessLog),
                typeof(SysApiLog),
                typeof(SysExceptionLog),
                typeof(SysLoginLog),
                typeof(SysOperationLog),
                typeof(SysDiffLog)
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
            Assert.Contains(tableNames, name => name.StartsWith("sys_diff_log_", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SysDiffLog` 不存在。

- [ ] **Step 3: 实现实体**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysDiffLog.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Auditing.SqlSugar.Entities;

/// <summary>
/// 实体差异日志实体
/// </summary>
[SplitTable(SplitType.Month)]
[SugarTable("sys_diff_log_{year}{month}{day}")]
public class SysDiffLog : SugarCreationEntity<long>, ISplitTableEntity
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysDiffLog() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysDiffLog(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 创建时间，同时作为分表字段
    /// </summary>
    [SplitField]
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "创建时间")]
    public override DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 审计类型
    /// </summary>
    [SugarColumn(ColumnName = "Audit_Type", Length = 32, IsNullable = false, ColumnDescription = "审计类型")]
    public string AuditType { get; set; } = "EntityChange";

    /// <summary>
    /// 操作类型（Create/Update/Delete/Restore）
    /// </summary>
    [SugarColumn(ColumnName = "Operation_Type", Length = 16, IsNullable = false, ColumnDescription = "操作类型")]
    public string OperationType { get; set; } = string.Empty;

    /// <summary>
    /// 实体类型
    /// </summary>
    [SugarColumn(ColumnName = "Entity_Type", Length = 256, IsNullable = false, ColumnDescription = "实体类型")]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// 实体标识
    /// </summary>
    [SugarColumn(ColumnName = "Entity_Id", Length = 256, IsNullable = true, ColumnDescription = "实体标识")]
    public string? EntityId { get; set; }

    /// <summary>
    /// 前值 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Before_Data", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "前值 JSON")]
    public string? BeforeData { get; set; }

    /// <summary>
    /// 后值 JSON
    /// </summary>
    [SugarColumn(ColumnName = "After_Data", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "后值 JSON")]
    public string? AfterData { get; set; }

    /// <summary>
    /// 变更字段 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Changed_Fields", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "变更字段 JSON")]
    public string? ChangedFields { get; set; }

    /// <summary>
    /// 请求路径
    /// </summary>
    [SugarColumn(ColumnName = "Request_Path", Length = 512, IsNullable = true, ColumnDescription = "请求路径")]
    public string? RequestPath { get; set; }

    /// <summary>
    /// 请求方法
    /// </summary>
    [SugarColumn(ColumnName = "Request_Method", Length = 16, IsNullable = true, ColumnDescription = "请求方法")]
    public string? RequestMethod { get; set; }

    /// <summary>
    /// 操作 IP
    /// </summary>
    [SugarColumn(ColumnName = "Operation_Ip", Length = 64, IsNullable = true, ColumnDescription = "操作 IP")]
    public string? OperationIp { get; set; }

    /// <summary>
    /// 请求标识
    /// </summary>
    [SugarColumn(ColumnName = "Request_Id", Length = 64, IsNullable = true, ColumnDescription = "请求标识")]
    public string? RequestId { get; set; }

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
    /// 租户标识
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "租户标识")]
    public long? TenantId { get; set; }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/SysDiffLog.cs framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/EntityMappingTests.cs framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/TableInitializationTests.cs
git commit -m "feat(auditing-sqlsugar): 新增实体差异日志实体"
```

---

### Task 2: 映射、写入器与注册

**Files:**
- Modify: `framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping/AuditingLogMapper.cs`
- Create: `framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarEntityDiffLogWriter.cs`
- Modify: `framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/AuditingLogMapperTests.cs`
- Modify: `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysDiffLog`；`framework/src/XiHan.Framework.Auditing/EntityDiffLogRecord.cs`；`framework/src/XiHan.Framework.Auditing/IEntityDiffLogWriter.cs`；框架既有的 `ISqlSugarClientResolver`、`IDistributedIdGenerator<long>`
- Produces: `AuditingLogMapper.ToEntity(EntityDiffLogRecord, long, DateTimeOffset) → SysDiffLog`；`SqlSugarEntityDiffLogWriter : IEntityDiffLogWriter`；`AddXiHanAuditingSqlSugar` 追加对 `IEntityDiffLogWriter` 的 `Replace`

**参考来源（动手前先读）：**
- 记录字段：`framework/src/XiHan.Framework.Auditing/EntityDiffLogRecord.cs`
- 客户端解析语义：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`（`GetCurrentClient()` 与 `GetClientForEntity(Type)` 的注释，两者的差异是本任务的核心）
- 写入器契约：`framework/src/XiHan.Framework.Auditing/IEntityDiffLogWriter.cs`、`NullEntityDiffLogWriter.cs`
- 事务契约的权威说明：`framework/src/XiHan.Framework.Data/SqlSugar/Auditing/SqlSugarDiffLogAop.cs:16-28`（类型 `<remarks>`）、`docs/guide/auditing.md:148-182`
- 现有注册位置：`framework/src/XiHan.Framework.Auditing/Extensions/DependencyInjection/XiHanAuditingServiceCollectionExtensions.cs:56`（确认 `TryAddScoped` 的位置）
- 映射与写入器形状范本：本包 `Mapping/AuditingLogMapper.cs`、`Writers/SqlSugarOperationLogWriter.cs`
- 测试形状范本：`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs` 里已有的 `StubClientResolver`（`GetCurrentClientCalls`/`GetClientCalls`/`RequestedEntityTypes` 三个计数字段已经存在，本任务直接复用，不需要改动这个桩类本身）

**本任务禁止事项：** 见「本计划特有的硬约束」全部四条。另外不要修改 `StubClientResolver` 的实现——它已经具备本任务需要的全部计数能力（`GetCurrentClientCalls`、`RequestedEntityTypes`），只是此前没有测试断言过 `GetCurrentClientCalls`。

- [ ] **Step 1: 写失败的测试——映射**

在 `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/AuditingLogMapperTests.cs` 的 `AuditingLogMapperTests` 类末尾（`未超列宽的文本与空值原样保留` 方法之后）追加：

```csharp
    [Fact]
    public void 实体差异日志映射保留全部字段()
    {
        var record = new EntityDiffLogRecord
        {
            AuditType = "EntityChange",
            OperationType = "Update",
            EntityType = "Order",
            EntityId = "1001",
            BeforeData = "{\"Status\":\"Pending\"}",
            AfterData = "{\"Status\":\"Paid\"}",
            ChangedFields = "[{\"Field\":\"Status\",\"Before\":\"Pending\",\"After\":\"Paid\"}]",
            RequestPath = "/api/orders/1001",
            RequestMethod = "PUT",
            OperationIp = "127.0.0.1",
            RequestId = "req-1",
            UserId = 42,
            UserName = "tester",
            TenantId = 7
        };

        var entity = AuditingLogMapper.ToEntity(record, 1009L, CreatedTime);

        Assert.Equal(1009L, entity.BasicId);
        Assert.Equal(CreatedTime, entity.CreatedTime);
        Assert.Equal("EntityChange", entity.AuditType);
        Assert.Equal("Update", entity.OperationType);
        Assert.Equal("Order", entity.EntityType);
        Assert.Equal("1001", entity.EntityId);
        Assert.Equal("{\"Status\":\"Pending\"}", entity.BeforeData);
        Assert.Equal("{\"Status\":\"Paid\"}", entity.AfterData);
        Assert.Equal("[{\"Field\":\"Status\",\"Before\":\"Pending\",\"After\":\"Paid\"}]", entity.ChangedFields);
        Assert.Equal("/api/orders/1001", entity.RequestPath);
        Assert.Equal("PUT", entity.RequestMethod);
        Assert.Equal("127.0.0.1", entity.OperationIp);
        Assert.Equal("req-1", entity.RequestId);
        Assert.Equal(42L, entity.UserId);
        Assert.Equal("tester", entity.UserName);
        Assert.Equal(7L, entity.TenantId);
    }

    [Fact]
    public void 实体差异日志未显式设置审计类型时保留默认值()
    {
        var record = new EntityDiffLogRecord
        {
            OperationType = "Create",
            EntityType = "Order"
        };

        var entity = AuditingLogMapper.ToEntity(record, 1010L, CreatedTime);

        Assert.Equal("EntityChange", entity.AuditType);
    }

    [Fact]
    public void 实体差异日志的大文本字段不截断()
    {
        var longJson = "{\"Data\":\"" + new string('x', 5000) + "\"}";
        var record = new EntityDiffLogRecord
        {
            OperationType = "Update",
            EntityType = "Order",
            BeforeData = longJson,
            AfterData = longJson,
            ChangedFields = longJson
        };

        var entity = AuditingLogMapper.ToEntity(record, 1011L, CreatedTime);

        Assert.Equal(longJson, entity.BeforeData);
        Assert.Equal(longJson, entity.AfterData);
        Assert.Equal(longJson, entity.ChangedFields);
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`AuditingLogMapper.ToEntity(EntityDiffLogRecord, ...)` 重载不存在。

- [ ] **Step 3: 实现映射重载**

在 `framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping/AuditingLogMapper.cs` 的 `Clamp` 私有方法**之前**（即 `ToEntity(OperationLogRecord, ...)` 方法之后）插入：

```csharp
    /// <summary>
    /// 把实体差异日志记录转换为实体
    /// </summary>
    /// <param name="record">实体差异日志记录</param>
    /// <param name="basicId">主键</param>
    /// <param name="createdTime">创建时间</param>
    /// <returns>实体差异日志实体</returns>
    public static SysDiffLog ToEntity(EntityDiffLogRecord record, long basicId, DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SysDiffLog(basicId)
        {
            CreatedTime = createdTime,
            AuditType = Clamp(record.AuditType, 32),
            OperationType = Clamp(record.OperationType, 16),
            EntityType = Clamp(record.EntityType, 256),
            EntityId = Clamp(record.EntityId, 256),
            BeforeData = record.BeforeData,
            AfterData = record.AfterData,
            ChangedFields = record.ChangedFields,
            RequestPath = Clamp(record.RequestPath, 512),
            RequestMethod = Clamp(record.RequestMethod, 16),
            OperationIp = Clamp(record.OperationIp, 64),
            RequestId = Clamp(record.RequestId, 64),
            UserId = record.UserId,
            UserName = Clamp(record.UserName, 128),
            TenantId = record.TenantId
        };
    }
```

`EntityDiffLogRecord` 与 `SysDiffLog` 都不需要新增 `using`：前者在 `XiHan.Framework.Auditing` 根命名空间（本文件所在 `XiHan.Framework.Auditing.SqlSugar.Mapping` 的外层链上），后者已由文件顶部既有的 `using XiHan.Framework.Auditing.SqlSugar.Entities;` 覆盖。

- [ ] **Step 4: 运行映射测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS（写入器与注册测试此时还未写，不影响本步）。

- [ ] **Step 5: 写失败的测试——写入器与注册**

在 `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs` 的 `LogWriterTests` 类里、`操作日志写入器按实体类型路由并落入当月分表` 方法之后追加：

```csharp
    [Fact]
    public async Task 实体差异日志写入器经当前工作单元客户端写入并落入当月分表()
    {
        var databaseFile = NewDatabasePath();

        try
        {
            using var db = CreateClient(databaseFile);

            db.CodeFirst.SplitTables().InitTables(typeof(SysDiffLog));

            var resolver = new StubClientResolver(db);
            var writer = new SqlSugarEntityDiffLogWriter(
                resolver,
                IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload());

            var before = DateTimeOffset.UtcNow;
            await writer.WriteAsync(new EntityDiffLogRecord
            {
                OperationType = "Update",
                EntityType = "Order",
                EntityId = "1001",
                ChangedFields = "[{\"Field\":\"Status\"}]"
            });
            var after = DateTimeOffset.UtcNow;

            AssertUsedCurrentClient(resolver);

            var range = CurrentUtcMonthRange();
            var found = db.Queryable<SysDiffLog>()
                .SplitTable(range[0], range[1])
                .Where(item => item.EntityId == "1001")
                .ToList();

            var row = Assert.Single(found);
            Assert.NotEqual(0L, row.BasicId);
            Assert.Equal("Update", row.OperationType);
            Assert.Equal("EntityChange", row.AuditType);
            AssertCreatedTimeNearNow(row.CreatedTime, before, after);
        }
        finally
        {
            DeleteDatabase(databaseFile);
        }
    }
```

再把既有的 `注册扩展顶替空写入器` 与 `五个写入器全部被顶替` 之间的那个 `[Theory]`（连同它的 `[InlineData]` 列表）整体替换为：

```csharp
    /// <summary>
    /// 六个写入器全部被注册扩展顶替
    /// </summary>
    [Theory]
    [InlineData(typeof(IAccessLogWriter), typeof(SqlSugarAccessLogWriter))]
    [InlineData(typeof(IApiLogWriter), typeof(SqlSugarApiLogWriter))]
    [InlineData(typeof(IEntityDiffLogWriter), typeof(SqlSugarEntityDiffLogWriter))]
    [InlineData(typeof(IExceptionLogWriter), typeof(SqlSugarExceptionLogWriter))]
    [InlineData(typeof(ILoginLogWriter), typeof(SqlSugarLoginLogWriter))]
    [InlineData(typeof(IOperationLogWriter), typeof(SqlSugarOperationLogWriter))]
    public void 六个写入器全部被顶替(Type serviceType, Type expectedImplementationType)
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IAccessLogWriter, NullAccessLogWriter>();
        services.TryAddScoped<IApiLogWriter, NullApiLogWriter>();
        services.TryAddScoped<IEntityDiffLogWriter, NullEntityDiffLogWriter>();
        services.TryAddScoped<IExceptionLogWriter, NullExceptionLogWriter>();
        services.TryAddScoped<ILoginLogWriter, NullLoginLogWriter>();
        services.TryAddScoped<IOperationLogWriter, NullOperationLogWriter>();

        services.AddXiHanAuditingSqlSugar();

        var descriptor = Assert.Single(services, item => item.ServiceType == serviceType);
        Assert.Equal(expectedImplementationType, descriptor.ImplementationType);
    }
```

（原方法名 `五个写入器全部被顶替` 改为 `六个写入器全部被顶替`，`[InlineData]` 增加一行，`Arrange` 部分增加一行 `TryAddScoped`，其余不变。）

最后在 `AssertRouted` 私有方法**之后**追加一个新的断言辅助方法：

```csharp
    private static void AssertUsedCurrentClient(StubClientResolver resolver)
    {
        Assert.Empty(resolver.RequestedEntityTypes);
        Assert.Equal(1, resolver.GetCurrentClientCalls);
        Assert.Equal(0, resolver.GetClientCalls);
    }
```

- [ ] **Step 6: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`SqlSugarEntityDiffLogWriter` 不存在。

- [ ] **Step 7: 实现写入器**

`framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarEntityDiffLogWriter.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Auditing.SqlSugar.Entities;
using XiHan.Framework.Auditing.SqlSugar.Mapping;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Auditing.SqlSugar.Writers;

/// <summary>
/// 实体差异日志 SqlSugar 写入器
/// </summary>
public class SqlSugarEntityDiffLogWriter : IEntityDiffLogWriter
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarEntityDiffLogWriter(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 写入实体差异日志
    /// </summary>
    /// <param name="record">实体差异日志记录</param>
    /// <param name="cancellationToken">取消令牌，仅在写入前检查，不传递给数据库</param>
    public async Task WriteAsync(EntityDiffLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = AuditingLogMapper.ToEntity(record, _idGenerator.NextId(), DateTimeOffset.UtcNow);
        var client = _clientResolver.GetCurrentClient();

        await client.Insertable(entity).SplitTable().ExecuteCommandAsync();
    }
}
```

注意本文件**没有** `using XiHan.Framework.Auditing.Writers;`——`IEntityDiffLogWriter` 与 `EntityDiffLogRecord` 都在 `XiHan.Framework.Auditing` 根命名空间，是本文件命名空间 `XiHan.Framework.Auditing.SqlSugar.Writers` 的外层链，不需要显式 using（本计划特有硬约束③）。

- [ ] **Step 8: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 9: 实现注册**

把 `framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs` 的方法体改为：

```csharp
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IAccessLogWriter, SqlSugarAccessLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IApiLogWriter, SqlSugarApiLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IEntityDiffLogWriter, SqlSugarEntityDiffLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IExceptionLogWriter, SqlSugarExceptionLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<ILoginLogWriter, SqlSugarLoginLogWriter>());
        services.Replace(ServiceDescriptor.Scoped<IOperationLogWriter, SqlSugarOperationLogWriter>());

        return services;
```

不需要新增 `using`——`IEntityDiffLogWriter` 在 `XiHan.Framework.Auditing` 根命名空间，是本文件命名空间 `XiHan.Framework.Auditing.SqlSugar.Extensions.DependencyInjection` 的外层链（本计划特有硬约束③）。

- [ ] **Step 10: 运行测试并验证构建**

```bash
dotnet test --project framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj -c Release
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：测试全部 PASS；构建 0 Warning(s) 0 Error(s)。

- [ ] **Step 11: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar framework/test/XiHan.Framework.Auditing.SqlSugar.Tests
git commit -m "feat(auditing-sqlsugar): 新增实体差异日志写入器并替换空实现"
```

---

### Task 3: 文档更新与 PR 收尾

**Files:**
- Modify: `framework/src/XiHan.Framework.Auditing.SqlSugar/README.md`
- Modify: `docs/packages/auditing-sqlsugar.md`

**Interfaces:**
- Consumes: Task 1、Task 2 的全部产出
- Produces: 无（终端任务）

**参考来源（动手前先读）：**
- 现有 README 的「核心能力」「目录结构」两节（本任务只改这两处相关内容）
- `docs/packages/auditing-sqlsugar.md` 第 16 行「实体变更日志（`IEntityDiffLogWriter`）不在本包范围内，仍是空实现」、第 94-101 行「写入器自身不开事务……」「这与 `IEntityDiffLogWriter`『必须与业务同事务』的契约方向相反」、第 103-111 行「主要 API / 类型」表格、第 171 行「实体变更日志仍为空实现」——这几处都需要改写为「已实现」

**本任务禁止事项：** 不要顺手重写与本 PR 无关的文档段落——只改与「实体差异日志从空实现变为已实现」直接相关的文字。不要在文档里声称仓库有 `.codegraph/` 目录。不要碰 `docs/.vitepress/config.ts` 或根 `README.md`——`auditing-sqlsugar` 这个包本身在 P1 已经注册过侧边栏与模块清单，本次只是包内新增内容，不是新包，侧边栏与根 README 都不需要改动。

- [ ] **Step 1: 更新包 README**

把「核心能力」一节改为：

```markdown
## 核心能力
- 6 类审计日志（访问 / 接口 / 异常 / 登录 / 操作 / 实体差异）的 SqlSugar 实体与写入器
- 按月自动分表，表名形如 `sys_operation_log_20260901`
- 表结构由 `DbInitializer` 在应用启动时创建，需开启 `XiHan:Data:SqlSugarCore:EnableTableInitialization`（默认 `false`）
- 主键为雪花 ID，由 `XiHan.Framework.DistributedIds` 生成
- 实体差异日志写入器与触发它的业务写入同一个工作单元事务，业务回滚时差异日志随之回滚；其余 5 类日志不参与业务事务
```

把「目录结构」一节的代码块改为：

```markdown
## 目录结构
```text
XiHan.Framework.Auditing.SqlSugar/
  Entities/
    SysAccessLog.cs
    SysApiLog.cs
    SysDiffLog.cs
    SysExceptionLog.cs
    SysLoginLog.cs
    SysOperationLog.cs
  Extensions/
    DependencyInjection/
      XiHanAuditingSqlSugarServiceCollectionExtensions.cs
  Mapping/
    AuditingLogMapper.cs
  Writers/
    SqlSugarAccessLogWriter.cs
    SqlSugarApiLogWriter.cs
    SqlSugarEntityDiffLogWriter.cs
    SqlSugarExceptionLogWriter.cs
    SqlSugarLoginLogWriter.cs
    SqlSugarOperationLogWriter.cs
  README.md
  XiHanAuditingSqlSugarModule.cs
```
```

- [ ] **Step 2: 更新包文档站页面**

在 `docs/packages/auditing-sqlsugar.md` 做以下几处改写（保持其余内容不动）：

第 3 行摘要改为：

```markdown
> 审计日志的 SqlSugar 持久化提供程序：6 类日志实体（按月分表）与 6 个写入器，替换 [Auditing](./auditing) 的空写入器后日志才真正落库。
```

第 12-16 行改为：

```markdown
[Auditing](./auditing) 负责「采集什么、怎么异步化、怎么脱敏」，但它的 6 个写入器契约（5 个 `IXxxLogWriter` 加 `IEntityDiffLogWriter`）默认实现全是空实现——日志采集到就被丢弃。本包补上最后一步：把这 6 类日志写进数据表。

启用本包后，[Web.Api](./web-api) 各中间件与过滤器采集到的记录、以及 SqlSugar 原生 `Aop.OnDiffLogEvent` 产出的实体差异记录，会自动落到 6 张按月分表的数据表，应用侧不需要再写任何写入代码。
```

第 35-43 行的表格追加一行：

```markdown
| `IEntityDiffLogWriter` | `NullEntityDiffLogWriter` | `SqlSugarEntityDiffLogWriter` |
```

第 52-55 行的表名列表改为：

```
sys_access_log   sys_api_log   sys_diff_log
sys_exception_log   sys_login_log   sys_operation_log
```

第 94-101 行（写入器事务契约那一段）改为：

```markdown
**写入器自身不开事务，但「是否与业务事务同行」取决于取客户端的方法。** 访问 / 接口 / 异常 / 登录 / 操作这 5 类写入器经 `GetClientForEntity<TEntity>()` 取客户端；实体差异日志写入器经 `GetCurrentClient()` 取客户端——两者都会在存在事务型工作单元时自动登记进该事务（[Data](./data) 的 `EnlistCurrentUnitOfWork` 规则），区别在于 `GetClientForEntity` 支持 `[ModuleDataSource]` 按实体路由、`GetCurrentClient` 固定解析当前布局主库。

- **访问 / 接口 / 异常 / 操作** 这 4 类由框架采集，写日志的时机在 `await next()` 之后，此刻业务工作单元已经结束，因此实际不受它影响。开启队列（`XiHan:Auditing:LogQueue`）时更是由后台 Worker 在请求之外消费。
- **登录日志**由应用在自己的登录分支里调 `ILoginLogPipeline`，若这个调用发生在有活动事务型工作单元的作用域内，登录日志会与那笔事务同行。
- **实体差异日志**必须与触发它的业务写入同一个事务——`SqlSugarDiffLogAop` 在业务写操作的同一个调用栈内同步调用写入器，业务回滚时差异日志随之回滚。详见[审计日志指南](../guide/auditing)「写入器的事务契约」一节。
```

第 103-111 行「主要 API / 类型」表格追加两行：

```markdown
| `SysDiffLog` | 实体差异日志实体，字段与 `EntityDiffLogRecord` 逐一对应 |
| `SqlSugarEntityDiffLogWriter` | 实体差异日志写入器（`Scoped`），经 `GetCurrentClient()` 而非 `GetClientForEntity` 取客户端 |
```

第 171 行改为：

```markdown
- **实体差异日志固定落主库**。`SqlSugarEntityDiffLogWriter` 经 `GetCurrentClient()` 取客户端，不支持 `[ModuleDataSource]` 路由；业务实体声明了模块数据源时，该实体的差异日志仍落在当前布局主库。
```

- [ ] **Step 3: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 Warning(s) 0 Error(s)；测试全部 PASS（已知的 `Script.Tests` GC 时序随机失败与本计划无关，出现时不算阻塞）。

- [ ] **Step 4: 复查注释是否混入论证**

逐个通读本计划新增/修改的 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息，不留在代码里。

- [ ] **Step 5: 提交**

```bash
git add framework/src/XiHan.Framework.Auditing.SqlSugar/README.md docs/packages/auditing-sqlsugar.md
git commit -m "docs(auditing-sqlsugar): 补写实体差异日志的包文档与文档站页面"
```

---

## 完成标准

PR1（`Auditing.SqlSugar`）整体完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `IEntityDiffLogWriter` 经 `services.Replace` 顶替为 `SqlSugarEntityDiffLogWriter`，有测试断言
- 差异日志写入器经 `GetCurrentClient()`（不是 `GetClientForEntity()`）取客户端，有测试直接断言调用方法，不只断言数据落库
- `SysDiffLog` 按月分表落库，14 个属性全部映射，`BeforeData`/`AfterData`/`ChangedFields` 原样保留不截断
- 包 README、`docs/packages/auditing-sqlsugar.md` 均已更新，去掉「实体变更日志仍为空实现」的旧说明
- 新增代码的注释只说明代码做什么

## 已知边界（写入 PR 描述，不写进代码注释）

- **差异日志固定落主库**：`GetCurrentClient()` 不支持 `[ModuleDataSource]` 路由；业务实体声明了模块数据源时，该实体的差异日志仍落在当前布局主库，不跟随业务数据分库。这是「与业务同事务」的直接代价——差异日志与业务实体分处两个物理连接时，不可能同时钉进同一个事务。
- **差异日志的事务方向与其余 5 类日志相反**：访问/接口/异常/登录/操作「尽量不与业务同生共死」，差异日志「必须同生共死」。应用侧自定义写入器时需要留意选哪一种方向。
- **高频变更下的写放大**：同一实体短时间内多次变更，每次都产生一条独立的 `SysDiffLog` 行，不去重、不合并。
- **队列模式不适用于差异日志**：与其余 5 类日志不同，差异日志没有队列开关，`SqlSugarDiffLogAop` 里是同步调用，写入耗时直接计入业务请求耗时。
- **议题描述与实际契约的出入**：Linear 议题 EDDIE-5 的调研笔记称差异日志写入器「不参与工作单元事务」，经核对源码与 `docs/guide/auditing.md` 后确认正确行为是「参与」，本计划按后者实现，详见 spec §8。

## 下一份计划

本计划完成后 PR1（`Auditing.SqlSugar`）可以提交给上游。下一份按拆分方案 `.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 中 PR2（`EventBus.SqlSugar`）范围内尚未实现的部分继续。
