# Tasks.SqlSugar ②：定时任务存储与文档站收尾 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以 SqlSugar 落库实现替换 `IJobStore`，让崩溃遗留的「运行中」实例在超时后不再阻塞调度；完成 `XiHan.Framework.Tasks.SqlSugar` 的文档站收尾，使本包的 PR 可以提交。

**Architecture:** 两个实体 `SysJobInstance`、`SysJobHistory`，时间一律存 UTC 的 `DateTime`、由 `JobStoreMapper` 归一。`SqlSugarJobStore` 经第 ① 份的 `TasksHostClientAccessor` 写默认布局主库。运行中实例带一个截止时刻（开始 + 超时 + 宽限），`GetRunningInstancesAsync` 只认截止时刻未到的。注册在第 ① 份的扩展方法里追加一行 `Replace`。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221（经 `XiHan.Framework.Data` 传递引入）、System.Text.Json、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-tasks-sqlsugar-2-scheduled-jobs-design.md`

> 该 spec 自成一体，实现本份所需的全部约束都在其中。动手前**先读完它的第 2 节与第 5 节**。

**Linear 议题:** https://linear.app/elf-express/issue/EDDIE-8

**前置:** 第 ① 份（`.superpowers/plans/2026-09-28-tasks-sqlsugar-1-background-jobs.md`）必须已完成。本计划依赖它产出的：`XiHanTasksSqlSugarOptions`、`TasksHostClientAccessor`、`AddXiHanTasksSqlSugar`、`SqlSugarBackgroundJobStore`，以及测试项目里的 `TasksTestContext`、`StubClientResolver`、`FakeClock`、`TasksSqlSugarRegistrationTests`。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/tasks-sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-tasks`）。worktree 若是在这两份文档提交之前开的就看不到它们，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：沿用第 ① 份的 worktree `E:/source/XiHan/XiHan.Framework-tasks`，分支 `feat/tasks-sqlsugar`。若 worktree 尚不存在：

```bash
git worktree add ../XiHan.Framework-tasks -b feat/tasks-sqlsugar dev
```

上游是 `main`，**绝不在 `main` 上提交**。以下所有路径与命令都相对于 worktree 根目录。

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

**SqlSugar 签名只信源码**：权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）；更新提供者的目录名是 `Abstract/UpdateProvider/`，不是 `UpdateableProvider`。文档：`E:/source/platfrom-admin/docs/SqlSugar-docs/`。

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
ISugarQueryable<T> OrderBy(Expression<Func<T, object>> expression, OrderByType type = OrderByType.Asc)
Task<bool> AnyAsync(Expression<Func<T, bool>> expression)
Task<T> FirstAsync()                                 // 无记录返回 default
Task<List<T>> ToListAsync()
Task<List<T>> ToPageListAsync(int pageIndex, int pageSize)   // 页码从 1 开始：Skip = (pageIndex - 1) * pageSize（QueryableHelper.cs:1744-1757）
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
bool IDbMaintenance.IsAnyIndex(string indexName)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)   // 字段名是属性名

// 第 ① 份的产出
public sealed class TasksHostClientAccessor
{
    public Task<TResult> ExecuteAsync<TResult>(Func<ISqlSugarClient, Task<TResult>> operation);
    public Task ExecuteAsync(Func<ISqlSugarClient, Task> operation);
}
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

**测试项目 csproj**：第 ① 份已建好，本计划不改它。

**SQLite 临时库**：连接串必须带 `Pooling=False`（第 ① 份的夹具已如此）。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本包无关，不要去追它。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**。README 与文档站条目必须写明。

**提交信息**：中文 Conventional Commits，作用域 `tasks-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

---

## 本计划特有的硬约束

**① 运行中实例必须带截止时刻，查询只认截止时刻未到的。**

崩溃遗留的「运行中」实例会让不允许并发的任务**永远**不再触发，只留一行警告日志（spec §5 ①）。截止时刻 = `(StartedAt ?? ScheduledAt) + max(0, JobInfo.TimeoutMilliseconds) + RunningInstanceGracePeriod`，三项缺一不可：漏掉宽限期或用错起点，真正在跑的长任务会被提前判定为「不在运行」，调度器再触发一份，同一任务并发两份。

**② 实体时间列一律是 UTC 的 `DateTime`。**

不要把实体属性声明成 `DateTimeOffset`——SQLite 读回会平移瞬时，UTC 的 CI 机器上看不出来（spec §5 ②）。映射层写入用 `value.UtcDateTime`，读回用 `new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))`。

**③ 保存实例不许因参数序列化失败而抛异常。**

`JobExecutor` 在执行任务**之前**保存实例，这里抛异常任务就不会执行（spec §5 ③）。捕获 `NotSupportedException` 与 `JsonException`，存 `null`。

**④ 写库必须经 `TasksHostClientAccessor`。**

存储里不许出现 `new SqlSugarClient`，不许注入 `ISqlSugarClientResolver`（spec §5 ④）。

---

## File Structure

```
framework/src/XiHan.Framework.Tasks.SqlSugar/
  Options/XiHanTasksSqlSugarOptions.cs               Task 1  追加 RunningInstanceGracePeriod
  Entities/SysJobInstance.cs                         Task 1  表 sys_job_instance
  Entities/SysJobHistory.cs                          Task 1  表 sys_job_history
  Mapping/JobStoreMapper.cs                          Task 1  契约 ↔ 实体，时间归一
  ScheduledJobs/SqlSugarJobStore.cs                  Task 2  IJobStore 的 7 个方法
  Extensions/DependencyInjection/
    XiHanTasksSqlSugarServiceCollectionExtensions.cs Task 3  追加 Replace IJobStore
  README.md                                          Task 4  整份替换

framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/
  XiHanTasksSqlSugarOptionsTests.cs                  Task 1  追加两个用例
  SysJobEntitiesTests.cs                             Task 1
  JobStoreMapperTests.cs                             Task 1
  TasksTestContext.cs                                Task 2  整份替换：建表追加两个实体、追加 JobStore
  JobStoreInstanceTests.cs                           Task 2
  JobStoreHistoryTests.cs                            Task 2
  TasksSqlSugarRegistrationTests.cs                  Task 3  整份替换：追加两个用例

docs/packages/tasks-sqlsugar.md                      Task 4  新建
docs/packages/index.md                               Task 4  模块清单追加一行
docs/.vitepress/config.ts                            Task 4  侧边栏追加一项
framework/README.md                                  Task 4  模块清单追加一行、计数加一
framework/README_cn.md                               Task 4  模块清单追加一行、计数加一
README.md / README_cn.md                             Task 4  只改模块计数（含徽章）
```

---

### Task 1: 选项、实体与映射

**Files:**
- Modify: `framework/src/XiHan.Framework.Tasks.SqlSugar/Options/XiHanTasksSqlSugarOptions.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysJobInstance.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysJobHistory.cs`
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/Mapping/JobStoreMapper.cs`
- Modify: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHanTasksSqlSugarOptionsTests.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/SysJobEntitiesTests.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/JobStoreMapperTests.cs`

**Interfaces:**
- Consumes: `XiHan.Framework.Data.SqlSugar.Entities.SugarEntity<TKey>`；`XiHan.Framework.Tasks.ScheduledJobs.Models` 下的 `JobInstance`、`JobHistory`、`JobInfo`、`JobStatus`、`JobTriggerType`
- Produces:
  - `XiHanTasksSqlSugarOptions.RunningInstanceGracePeriod`（`TimeSpan`，默认 1 分钟）
  - `public class SysJobInstance : SugarEntity<string>`，构造函数 `()`、`(string basicId)`；属性见 spec §4.2
  - `public class SysJobHistory : SugarEntity<string>`，构造函数 `()`、`(string basicId)`；属性见 spec §4.2
  - `public static class JobStoreMapper`：`SysJobInstance ToEntity(JobInstance instance, TimeSpan runningGracePeriod)`、`JobInstance ToJobInstance(SysJobInstance entity)`、`SysJobHistory ToEntity(JobHistory history)`、`JobHistory ToJobHistory(SysJobHistory entity)`

**参考来源（动手前先读）：**
- 同包实体范本：`framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysBackgroundJob.cs`
- 契约模型：`framework/src/XiHan.Framework.Tasks/ScheduledJobs/Models/JobInstance.cs`、`JobHistory.cs`、`JobInfo.cs`
- 超时的来源：`framework/src/XiHan.Framework.Tasks/ScheduledJobs/Pipeline/TimeoutMiddleware.cs:30`
- 设计：spec §4.1、§4.2、§4.3、§4.5

**本任务禁止事项：** 硬约束 ①②③。不加 `[SplitTable]`。属性名不用 `CreatedTime`、`ModifiedTime`、`IsDeleted`。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHanTasksSqlSugarOptionsTests.cs` 的类末尾（最后一个 `}` 之前）追加：

```csharp

    /// <summary>
    /// 运行中实例宽限期默认一分钟
    /// </summary>
    [Fact]
    public void 运行中实例宽限期默认一分钟()
    {
        var options = new XiHanTasksSqlSugarOptions();

        Assert.Equal(TimeSpan.FromMinutes(1), options.RunningInstanceGracePeriod);
    }

    /// <summary>
    /// 从配置节绑定宽限期
    /// </summary>
    [Fact]
    public void 从配置节绑定宽限期()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["XiHan:Tasks:SqlSugar:RunningInstanceGracePeriod"] = "00:03:00"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddXiHanTasksSqlSugar(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<XiHanTasksSqlSugarOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(3), options.RunningInstanceGracePeriod);
    }
```

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/SysJobEntitiesTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 定时任务实体建表测试
/// </summary>
public class SysJobEntitiesTests
{
    /// <summary>
    /// 任务实例表与执行历史表及索引能建出来
    /// </summary>
    [Fact]
    public void 任务实例表与执行历史表及索引能建出来()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_tasks_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
                ConnectionString = $"DataSource={databaseFile};Pooling=False",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });

            db.CodeFirst.InitTables(typeof(SysJobInstance), typeof(SysJobHistory));

            var tableNames = db.DbMaintenance.GetTableInfoList(false)
                .Select(table => table.Name)
                .ToList();

            Assert.Contains(tableNames, name => string.Equals(name, "sys_job_instance", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(tableNames, name => string.Equals(name, "sys_job_history", StringComparison.OrdinalIgnoreCase));
            Assert.True(db.DbMaintenance.IsAnyIndex("idx_sys_job_instance_name_status"));
            Assert.True(db.DbMaintenance.IsAnyIndex("idx_sys_job_history_name_started"));
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

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/JobStoreMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.Tasks.ScheduledJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Entities;
using XiHan.Framework.Tasks.SqlSugar.Mapping;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 定时任务映射测试
/// </summary>
public class JobStoreMapperTests
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 任务实例往返后字段一致
    /// </summary>
    [Fact]
    public void 任务实例往返后字段一致()
    {
        var scheduledAt = new DateTimeOffset(2030, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var instance = new JobInstance
        {
            InstanceId = "instance-1",
            JobName = "Report.Daily",
            JobInfo = new JobInfo
            {
                JobName = "Report.Daily",
                JobType = typeof(JobStoreMapperTests),
                TriggerType = JobTriggerType.Cron
            },
            Status = JobStatus.Failed,
            ScheduledAt = scheduledAt,
            StartedAt = scheduledAt.AddSeconds(1),
            CompletedAt = scheduledAt.AddSeconds(5),
            DurationMilliseconds = 4000,
            TriggerType = JobTriggerType.Cron,
            TenantId = 42,
            Parameters = new Dictionary<string, object?> { ["orderId"] = 1 },
            ErrorMessage = "模拟失败。",
            StackTrace = "at Report.Daily",
            RetryCount = 2,
            ExecutionNode = "node-1",
            TraceId = "trace-1"
        };

        var restored = JobStoreMapper.ToJobInstance(JobStoreMapper.ToEntity(instance, GracePeriod));

        Assert.Equal("instance-1", restored.InstanceId);
        Assert.Equal("Report.Daily", restored.JobName);
        Assert.Equal(JobStatus.Failed, restored.Status);
        Assert.Equal(scheduledAt, restored.ScheduledAt);
        Assert.Equal(TimeSpan.Zero, restored.ScheduledAt.Offset);
        Assert.Equal(instance.StartedAt, restored.StartedAt);
        Assert.Equal(instance.CompletedAt, restored.CompletedAt);
        Assert.Equal(4000L, restored.DurationMilliseconds);
        Assert.Equal(JobTriggerType.Cron, restored.TriggerType);
        Assert.Equal(42L, restored.TenantId);
        Assert.Equal("模拟失败。", restored.ErrorMessage);
        Assert.Equal("at Report.Daily", restored.StackTrace);
        Assert.Equal(2, restored.RetryCount);
        Assert.Equal("node-1", restored.ExecutionNode);
        Assert.Equal("trace-1", restored.TraceId);
        Assert.Equal("Report.Daily", restored.JobInfo.JobName);
        Assert.Equal(typeof(JobStoreMapperTests), restored.JobInfo.JobType);
        Assert.Equal(42L, restored.JobInfo.TenantId);

        Assert.NotNull(restored.Parameters);
        var orderId = Assert.IsType<JsonElement>(restored.Parameters["orderId"]);
        Assert.Equal(1, orderId.GetInt32());
    }

    /// <summary>
    /// 运行中实例的截止时刻为开始时间加超时再加宽限
    /// </summary>
    [Fact]
    public void 运行中实例的截止时刻为开始时间加超时再加宽限()
    {
        var startedAt = new DateTimeOffset(2030, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var instance = new JobInstance
        {
            JobName = "Report.Daily",
            JobInfo = new JobInfo { JobName = "Report.Daily", TimeoutMilliseconds = 60000 },
            Status = JobStatus.Running,
            ScheduledAt = startedAt.AddMinutes(-10),
            StartedAt = startedAt
        };

        var entity = JobStoreMapper.ToEntity(instance, GracePeriod);

        Assert.Equal(new DateTime(2030, 1, 1, 0, 2, 0, DateTimeKind.Utc), entity.RunningDeadline);
    }

    /// <summary>
    /// 非运行状态不设截止时刻
    /// </summary>
    [Fact]
    public void 非运行状态不设截止时刻()
    {
        var instance = new JobInstance
        {
            JobName = "Report.Daily",
            JobInfo = new JobInfo { JobName = "Report.Daily" },
            Status = JobStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow
        };

        Assert.Null(JobStoreMapper.ToEntity(instance, GracePeriod).RunningDeadline);
    }

    /// <summary>
    /// 参数无法序列化时存为空
    /// </summary>
    [Fact]
    public void 参数无法序列化时存为空()
    {
        var instance = new JobInstance
        {
            JobName = "Report.Daily",
            JobInfo = new JobInfo { JobName = "Report.Daily" },
            Parameters = new Dictionary<string, object?> { ["type"] = typeof(int) }
        };

        Assert.Null(JobStoreMapper.ToEntity(instance, GracePeriod).ParametersJson);
    }

    /// <summary>
    /// 无法解析的任务类型不赋值
    /// </summary>
    [Fact]
    public void 无法解析的任务类型不赋值()
    {
        var entity = new SysJobInstance("instance-1")
        {
            JobName = "Report.Daily",
            JobTypeName = "Not.Exists.Job, Not.Exists"
        };

        var restored = JobStoreMapper.ToJobInstance(entity);

        Assert.Null(restored.JobInfo.JobType);
    }

    /// <summary>
    /// 执行历史往返后字段一致
    /// </summary>
    [Fact]
    public void 执行历史往返后字段一致()
    {
        var startedAt = new DateTimeOffset(2030, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var history = new JobHistory
        {
            HistoryId = "history-1",
            InstanceId = "instance-1",
            JobName = "Report.Daily",
            Status = JobStatus.Succeeded,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(3),
            DurationMilliseconds = 3000,
            TenantId = 42,
            TriggerType = JobTriggerType.Manual,
            IsSuccess = true,
            ErrorMessage = null,
            StackTrace = null,
            RetryCount = 1,
            ExecutionNode = "node-1",
            TraceId = "trace-1",
            ParametersJson = "{\"orderId\":1}",
            Remarks = "手动触发"
        };

        var restored = JobStoreMapper.ToJobHistory(JobStoreMapper.ToEntity(history));

        Assert.Equal("history-1", restored.HistoryId);
        Assert.Equal("instance-1", restored.InstanceId);
        Assert.Equal("Report.Daily", restored.JobName);
        Assert.Equal(JobStatus.Succeeded, restored.Status);
        Assert.Equal(startedAt, restored.StartedAt);
        Assert.Equal(TimeSpan.Zero, restored.StartedAt.Offset);
        Assert.Equal(history.CompletedAt, restored.CompletedAt);
        Assert.Equal(3000L, restored.DurationMilliseconds);
        Assert.Equal(42L, restored.TenantId);
        Assert.Equal(JobTriggerType.Manual, restored.TriggerType);
        Assert.True(restored.IsSuccess);
        Assert.Equal(1, restored.RetryCount);
        Assert.Equal("node-1", restored.ExecutionNode);
        Assert.Equal("trace-1", restored.TraceId);
        Assert.Equal("{\"orderId\":1}", restored.ParametersJson);
        Assert.Equal("手动触发", restored.Remarks);
    }
}
```

`Assert.Equal(scheduledAt, restored.ScheduledAt)` 比较的是 `DateTimeOffset` 的瞬时，偏移不同也相等；紧随其后的 `Offset` 断言确认读回的偏移已归零。

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：编译失败——`XiHanTasksSqlSugarOptions` 没有 `RunningInstanceGracePeriod`（`CS1061`），`SysJobInstance`、`SysJobHistory`、`JobStoreMapper` 不存在（`CS0246`）。

- [ ] **Step 3: 追加选项**

在 `framework/src/XiHan.Framework.Tasks.SqlSugar/Options/XiHanTasksSqlSugarOptions.cs` 的 `BackgroundJobLeaseTimeout` 属性之后追加：

```csharp

    /// <summary>
    /// 运行中任务实例的宽限期，开始时间加任务超时再加宽限期之后，实例不再视为运行中
    /// </summary>
    public TimeSpan RunningInstanceGracePeriod { get; set; } = TimeSpan.FromMinutes(1);
```

- [ ] **Step 4: 创建两个实体**

`framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysJobInstance.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Entities;

/// <summary>
/// 定时任务实例实体，时间均为协调世界时
/// </summary>
[SugarTable("sys_job_instance")]
[SugarIndex("idx_sys_job_instance_name_status",
    nameof(SysJobInstance.JobName), OrderByType.Asc,
    nameof(SysJobInstance.Status), OrderByType.Asc)]
public class SysJobInstance : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysJobInstance() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">实例唯一标识</param>
    public SysJobInstance(string basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 任务名称
    /// </summary>
    [SugarColumn(ColumnName = "Job_Name", Length = 256, IsNullable = false, ColumnDescription = "任务名称")]
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// 任务类型的程序集限定名
    /// </summary>
    [SugarColumn(ColumnName = "Job_Type_Name", Length = 512, IsNullable = true, ColumnDescription = "任务类型的程序集限定名")]
    public string? JobTypeName { get; set; }

    /// <summary>
    /// 任务状态
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "任务状态")]
    public int Status { get; set; }

    /// <summary>
    /// 触发类型
    /// </summary>
    [SugarColumn(ColumnName = "Trigger_Type", IsNullable = false, ColumnDescription = "触发类型")]
    public int TriggerType { get; set; }

    /// <summary>
    /// 归属租户
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "归属租户")]
    public long? TenantId { get; set; }

    /// <summary>
    /// 计划执行时间
    /// </summary>
    [SugarColumn(ColumnName = "Scheduled_At", IsNullable = false, ColumnDescription = "计划执行时间")]
    public DateTime ScheduledAt { get; set; }

    /// <summary>
    /// 实际开始时间
    /// </summary>
    [SugarColumn(ColumnName = "Started_At", IsNullable = true, ColumnDescription = "实际开始时间")]
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    [SugarColumn(ColumnName = "Completed_At", IsNullable = true, ColumnDescription = "完成时间")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 执行耗时（毫秒）
    /// </summary>
    [SugarColumn(ColumnName = "Duration_Milliseconds", IsNullable = true, ColumnDescription = "执行耗时（毫秒）")]
    public long? DurationMilliseconds { get; set; }

    /// <summary>
    /// 运行截止时刻，仅运行中状态有值
    /// </summary>
    [SugarColumn(ColumnName = "Running_Deadline", IsNullable = true, ColumnDescription = "运行截止时刻，仅运行中状态有值")]
    public DateTime? RunningDeadline { get; set; }

    /// <summary>
    /// 重试次数
    /// </summary>
    [SugarColumn(ColumnName = "Retry_Count", IsNullable = false, ColumnDescription = "重试次数")]
    public int RetryCount { get; set; }

    /// <summary>
    /// 执行节点
    /// </summary>
    [SugarColumn(ColumnName = "Execution_Node", Length = 256, IsNullable = true, ColumnDescription = "执行节点")]
    public string? ExecutionNode { get; set; }

    /// <summary>
    /// 追踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = true, ColumnDescription = "追踪标识")]
    public string? TraceId { get; set; }

    /// <summary>
    /// 执行参数的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Parameters_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "执行参数的 JSON")]
    public string? ParametersJson { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    [SugarColumn(ColumnName = "Error_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "错误信息")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 堆栈跟踪
    /// </summary>
    [SugarColumn(ColumnName = "Stack_Trace", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "堆栈跟踪")]
    public string? StackTrace { get; set; }
}
```

`framework/src/XiHan.Framework.Tasks.SqlSugar/Entities/SysJobHistory.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Entities;

/// <summary>
/// 定时任务执行历史实体，时间均为协调世界时
/// </summary>
[SugarTable("sys_job_history")]
[SugarIndex("idx_sys_job_history_name_started",
    nameof(SysJobHistory.JobName), OrderByType.Asc,
    nameof(SysJobHistory.StartedAt), OrderByType.Desc)]
public class SysJobHistory : SugarEntity<string>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysJobHistory() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">历史记录唯一标识</param>
    public SysJobHistory(string basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 实例唯一标识
    /// </summary>
    [SugarColumn(ColumnName = "Instance_Id", Length = 128, IsNullable = false, ColumnDescription = "实例唯一标识")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 任务名称
    /// </summary>
    [SugarColumn(ColumnName = "Job_Name", Length = 256, IsNullable = false, ColumnDescription = "任务名称")]
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// 执行状态
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "执行状态")]
    public int Status { get; set; }

    /// <summary>
    /// 开始时间
    /// </summary>
    [SugarColumn(ColumnName = "Started_At", IsNullable = false, ColumnDescription = "开始时间")]
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    [SugarColumn(ColumnName = "Completed_At", IsNullable = true, ColumnDescription = "完成时间")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 执行耗时（毫秒）
    /// </summary>
    [SugarColumn(ColumnName = "Duration_Milliseconds", IsNullable = true, ColumnDescription = "执行耗时（毫秒）")]
    public long? DurationMilliseconds { get; set; }

    /// <summary>
    /// 归属租户
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = true, ColumnDescription = "归属租户")]
    public long? TenantId { get; set; }

    /// <summary>
    /// 触发类型
    /// </summary>
    [SugarColumn(ColumnName = "Trigger_Type", IsNullable = false, ColumnDescription = "触发类型")]
    public int TriggerType { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    [SugarColumn(ColumnName = "Is_Success", IsNullable = false, ColumnDescription = "是否成功")]
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    [SugarColumn(ColumnName = "Error_Message", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "错误信息")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 堆栈跟踪
    /// </summary>
    [SugarColumn(ColumnName = "Stack_Trace", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "堆栈跟踪")]
    public string? StackTrace { get; set; }

    /// <summary>
    /// 重试次数
    /// </summary>
    [SugarColumn(ColumnName = "Retry_Count", IsNullable = false, ColumnDescription = "重试次数")]
    public int RetryCount { get; set; }

    /// <summary>
    /// 执行节点
    /// </summary>
    [SugarColumn(ColumnName = "Execution_Node", Length = 256, IsNullable = true, ColumnDescription = "执行节点")]
    public string? ExecutionNode { get; set; }

    /// <summary>
    /// 追踪标识
    /// </summary>
    [SugarColumn(ColumnName = "Trace_Id", Length = 64, IsNullable = true, ColumnDescription = "追踪标识")]
    public string? TraceId { get; set; }

    /// <summary>
    /// 执行参数的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Parameters_Json", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "执行参数的 JSON")]
    public string? ParametersJson { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    [SugarColumn(ColumnName = "Remarks", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "备注")]
    public string? Remarks { get; set; }
}
```

- [ ] **Step 5: 创建映射**

`framework/src/XiHan.Framework.Tasks.SqlSugar/Mapping/JobStoreMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.Tasks.ScheduledJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Mapping;

/// <summary>
/// 定时任务实例与执行历史的契约与实体双向映射
/// </summary>
/// <remarks>
/// 实体中的时间均以协调世界时存储，读回时还原为偏移为零的 <see cref="DateTimeOffset"/>。
/// </remarks>
public static class JobStoreMapper
{
    /// <summary>
    /// 把任务实例转换为实体
    /// </summary>
    /// <remarks>
    /// 运行中状态的实例计算运行截止时刻：开始时间（为空时取计划时间）加任务超时再加宽限期。
    /// 执行参数无法序列化时存为空。
    /// </remarks>
    /// <param name="instance">任务实例</param>
    /// <param name="runningGracePeriod">运行中实例的宽限期</param>
    /// <returns>任务实例实体</returns>
    public static SysJobInstance ToEntity(JobInstance instance, TimeSpan runningGracePeriod)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new SysJobInstance(instance.InstanceId)
        {
            JobName = instance.JobName,
            JobTypeName = instance.JobInfo?.JobType?.AssemblyQualifiedName,
            Status = (int)instance.Status,
            TriggerType = (int)instance.TriggerType,
            TenantId = instance.TenantId,
            ScheduledAt = ToUtc(instance.ScheduledAt),
            StartedAt = ToUtc(instance.StartedAt),
            CompletedAt = ToUtc(instance.CompletedAt),
            DurationMilliseconds = instance.DurationMilliseconds,
            RunningDeadline = ComputeRunningDeadline(instance, runningGracePeriod),
            RetryCount = instance.RetryCount,
            ExecutionNode = instance.ExecutionNode,
            TraceId = instance.TraceId,
            ParametersJson = SerializeParameters(instance.Parameters),
            ErrorMessage = instance.ErrorMessage,
            StackTrace = instance.StackTrace
        };
    }

    /// <summary>
    /// 把实体转换为任务实例
    /// </summary>
    /// <remarks>
    /// 任务信息只还原任务名称、任务类型（能解析时）、触发类型与租户；执行参数的值还原为 <see cref="JsonElement"/>。
    /// </remarks>
    /// <param name="entity">任务实例实体</param>
    /// <returns>任务实例</returns>
    public static JobInstance ToJobInstance(SysJobInstance entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var jobInfo = new JobInfo
        {
            JobName = entity.JobName,
            TriggerType = (JobTriggerType)entity.TriggerType,
            TenantId = entity.TenantId
        };

        var jobType = ResolveType(entity.JobTypeName);
        if (jobType is not null)
        {
            jobInfo.JobType = jobType;
        }

        return new JobInstance
        {
            InstanceId = entity.BasicId,
            JobName = entity.JobName,
            JobInfo = jobInfo,
            Status = (JobStatus)entity.Status,
            ScheduledAt = FromUtc(entity.ScheduledAt),
            StartedAt = FromUtc(entity.StartedAt),
            CompletedAt = FromUtc(entity.CompletedAt),
            DurationMilliseconds = entity.DurationMilliseconds,
            TriggerType = (JobTriggerType)entity.TriggerType,
            TenantId = entity.TenantId,
            Parameters = DeserializeParameters(entity.ParametersJson),
            ErrorMessage = entity.ErrorMessage,
            StackTrace = entity.StackTrace,
            RetryCount = entity.RetryCount,
            ExecutionNode = entity.ExecutionNode,
            TraceId = entity.TraceId
        };
    }

    /// <summary>
    /// 把执行历史转换为实体
    /// </summary>
    /// <param name="history">执行历史</param>
    /// <returns>执行历史实体</returns>
    public static SysJobHistory ToEntity(JobHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        return new SysJobHistory(history.HistoryId)
        {
            InstanceId = history.InstanceId,
            JobName = history.JobName,
            Status = (int)history.Status,
            StartedAt = ToUtc(history.StartedAt),
            CompletedAt = ToUtc(history.CompletedAt),
            DurationMilliseconds = history.DurationMilliseconds,
            TenantId = history.TenantId,
            TriggerType = (int)history.TriggerType,
            IsSuccess = history.IsSuccess,
            ErrorMessage = history.ErrorMessage,
            StackTrace = history.StackTrace,
            RetryCount = history.RetryCount,
            ExecutionNode = history.ExecutionNode,
            TraceId = history.TraceId,
            ParametersJson = history.ParametersJson,
            Remarks = history.Remarks
        };
    }

    /// <summary>
    /// 把实体转换为执行历史
    /// </summary>
    /// <param name="entity">执行历史实体</param>
    /// <returns>执行历史</returns>
    public static JobHistory ToJobHistory(SysJobHistory entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new JobHistory
        {
            HistoryId = entity.BasicId,
            InstanceId = entity.InstanceId,
            JobName = entity.JobName,
            Status = (JobStatus)entity.Status,
            StartedAt = FromUtc(entity.StartedAt),
            CompletedAt = FromUtc(entity.CompletedAt),
            DurationMilliseconds = entity.DurationMilliseconds,
            TenantId = entity.TenantId,
            TriggerType = (JobTriggerType)entity.TriggerType,
            IsSuccess = entity.IsSuccess,
            ErrorMessage = entity.ErrorMessage,
            StackTrace = entity.StackTrace,
            RetryCount = entity.RetryCount,
            ExecutionNode = entity.ExecutionNode,
            TraceId = entity.TraceId,
            ParametersJson = entity.ParametersJson,
            Remarks = entity.Remarks
        };
    }

    /// <summary>
    /// 计算运行中实例的截止时刻，非运行中状态返回空
    /// </summary>
    private static DateTime? ComputeRunningDeadline(JobInstance instance, TimeSpan runningGracePeriod)
    {
        if (instance.Status != JobStatus.Running)
        {
            return null;
        }

        var startedAt = instance.StartedAt ?? instance.ScheduledAt;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, instance.JobInfo?.TimeoutMilliseconds ?? 0));

        return ToUtc(startedAt + timeout + runningGracePeriod);
    }

    /// <summary>
    /// 序列化执行参数，无法序列化时返回空
    /// </summary>
    private static string? SerializeParameters(IDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(parameters);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 反序列化执行参数
    /// </summary>
    private static Dictionary<string, object?>? DeserializeParameters(string? parametersJson)
    {
        return string.IsNullOrWhiteSpace(parametersJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson);
    }

    /// <summary>
    /// 按程序集限定名解析类型，解析不到时返回空
    /// </summary>
    private static Type? ResolveType(string? typeName)
    {
        return string.IsNullOrWhiteSpace(typeName)
            ? null
            : Type.GetType(typeName, throwOnError: false);
    }

    /// <summary>
    /// 转换为协调世界时
    /// </summary>
    private static DateTime ToUtc(DateTimeOffset value)
    {
        return value.UtcDateTime;
    }

    /// <summary>
    /// 转换为协调世界时
    /// </summary>
    private static DateTime? ToUtc(DateTimeOffset? value)
    {
        return value?.UtcDateTime;
    }

    /// <summary>
    /// 把协调世界时还原为偏移为零的时间
    /// </summary>
    private static DateTimeOffset FromUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>
    /// 把协调世界时还原为偏移为零的时间
    /// </summary>
    private static DateTimeOffset? FromUtc(DateTime? value)
    {
        return value.HasValue ? FromUtc(value.Value) : null;
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，含第 ① 份的全部用例。

若 `运行中实例的截止时刻为开始时间加超时再加宽限` 得到的值差 10 分钟，是起点用成了 `ScheduledAt`；差 1 分钟，是漏了宽限期（硬约束 ①）。

若 `参数无法序列化时存为空` 抛出了异常而不是返回空：用例的异常类型会出现在失败输出里，把它加进 `SerializeParameters` 的 `catch`，**不要**改成 `catch (Exception)`。

- [ ] **Step 7: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 新增定时任务实例与执行历史实体及映射"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。提交信息正文写明：时间列存 UTC 的 `DateTime` 是为了避开 `DateTimeOffset` 在 SQLite 上读回平移；参数序列化失败存空是为了不让保存实例阻止任务执行；运行截止时刻是为了让崩溃遗留的运行中实例在超时后不再阻塞调度。

---

### Task 2: 定时任务存储

**Files:**
- Create: `framework/src/XiHan.Framework.Tasks.SqlSugar/ScheduledJobs/SqlSugarJobStore.cs`
- Modify: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksTestContext.cs`（整份替换）
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/JobStoreInstanceTests.cs`
- Create: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/JobStoreHistoryTests.cs`

**Interfaces:**
- Consumes: Task 1 的实体、`JobStoreMapper`、`RunningInstanceGracePeriod`；第 ① 份的 `TasksHostClientAccessor`、`StubClientResolver`、`FakeClock`、`SqlSugarBackgroundJobStore`
- Produces:
  - `public class SqlSugarJobStore : IJobStore`，构造函数 `(TasksHostClientAccessor clientAccessor, IOptions<XiHanTasksSqlSugarOptions> options)`
  - `TasksTestContext` 追加属性 `SqlSugarJobStore JobStore`，建表追加 `SysJobInstance`、`SysJobHistory`

**参考来源（动手前先读）：**
- 被替换的实现：`framework/src/XiHan.Framework.Tasks/ScheduledJobs/Store/DefaultJobStore.cs`
- 调用方：`framework/src/XiHan.Framework.Tasks/ScheduledJobs/Executor/JobExecutor.cs:41-175`、`ScheduledJobs/Scheduler/CompositeJobScheduler.cs:253-300`
- 同包写法：`framework/src/XiHan.Framework.Tasks.SqlSugar/BackgroundJobs/SqlSugarBackgroundJobStore.cs`
- 设计：spec §4.4、§4.5、§5

**本任务禁止事项：** 硬约束 ①④。分页用 `ToPageListAsync(pageIndex, pageSize)`，**不要**自己算 `Skip`（spec §5 ⑥）。`GetRunningInstancesAsync` 的条件**必须**含 `RunningDeadline != null && RunningDeadline > now`。`CleanupHistoryAsync` **不许**删除运行中或等待中的实例。

- [ ] **Step 1: 扩展测试夹具**

把 `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksTestContext.cs` 整份替换为：

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
using XiHan.Framework.Tasks.SqlSugar.ScheduledJobs;

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
        Client.CodeFirst.InitTables(typeof(SysBackgroundJob), typeof(SysJobInstance), typeof(SysJobHistory));

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

        var options = Microsoft.Extensions.Options.Options.Create(new XiHanTasksSqlSugarOptions
        {
            BackgroundJobLeaseTimeout = leaseTimeout ?? TimeSpan.FromMinutes(5),
            RunningInstanceGracePeriod = TimeSpan.FromMinutes(1)
        });

        BackgroundJobStore = new SqlSugarBackgroundJobStore(Accessor, Clock, options);
        JobStore = new SqlSugarJobStore(Accessor, options);
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
    /// 被测定时任务存储
    /// </summary>
    public SqlSugarJobStore JobStore { get; }

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

与第 ① 份的版本相比只有三处不同：`InitTables` 多两个实体、选项多一个 `RunningInstanceGracePeriod`、多一个 `JobStore` 属性。第 ① 份的用例不受影响。

- [ ] **Step 2: 写失败的测试**

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/JobStoreInstanceTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.Tasks.ScheduledJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Entities;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 定时任务存储的任务实例测试
/// </summary>
public class JobStoreInstanceTests
{
    /// <summary>
    /// 保存后按标识查回任务实例
    /// </summary>
    [Fact]
    public async Task 保存后按标识查回任务实例()
    {
        using var context = new TasksTestContext();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1).ToOffset(TimeSpan.FromHours(8));
        var instance = NewInstance("Report.Daily", JobStatus.Running, startedAt);
        instance.TenantId = 42;
        instance.Parameters = new Dictionary<string, object?> { ["orderId"] = 1 };

        await context.JobStore.SaveJobInstanceAsync(instance);
        var found = await context.JobStore.GetJobInstanceAsync(instance.InstanceId);

        Assert.NotNull(found);
        Assert.Equal(instance.InstanceId, found.InstanceId);
        Assert.Equal("Report.Daily", found.JobName);
        Assert.Equal(JobStatus.Running, found.Status);
        Assert.Equal(42L, found.TenantId);
        Assert.Equal(typeof(JobStoreInstanceTests), found.JobInfo.JobType);
        AssertClose(startedAt, found.ScheduledAt);
        Assert.True(found.StartedAt.HasValue);
        AssertClose(startedAt, found.StartedAt.GetValueOrDefault());

        Assert.NotNull(found.Parameters);
        var orderId = Assert.IsType<JsonElement>(found.Parameters["orderId"]);
        Assert.Equal(1, orderId.GetInt32());
    }

    /// <summary>
    /// 查找不存在的实例返回空
    /// </summary>
    [Fact]
    public async Task 查找不存在的实例返回空()
    {
        using var context = new TasksTestContext();

        Assert.Null(await context.JobStore.GetJobInstanceAsync("not-exists"));
    }

    /// <summary>
    /// 保存空实例抛出参数异常
    /// </summary>
    [Fact]
    public async Task 保存空实例抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAsync<ArgumentNullException>(() => context.JobStore.SaveJobInstanceAsync(null!));
    }

    /// <summary>
    /// 重复保存同一实例覆盖原记录
    /// </summary>
    [Fact]
    public async Task 重复保存同一实例覆盖原记录()
    {
        using var context = new TasksTestContext();
        var instance = NewInstance("Report.Daily", JobStatus.Running, DateTimeOffset.UtcNow);
        await context.JobStore.SaveJobInstanceAsync(instance);

        instance.Status = JobStatus.Failed;
        instance.ErrorMessage = "模拟失败。";
        await context.JobStore.SaveJobInstanceAsync(instance);

        Assert.Equal(1, await context.Client.Queryable<SysJobInstance>().CountAsync());

        var found = await context.JobStore.GetJobInstanceAsync(instance.InstanceId);
        Assert.NotNull(found);
        Assert.Equal(JobStatus.Failed, found.Status);
        Assert.Equal("模拟失败。", found.ErrorMessage);
    }

    /// <summary>
    /// 以终止状态保存时补齐完成时间
    /// </summary>
    [Fact]
    public async Task 以终止状态保存时补齐完成时间()
    {
        using var context = new TasksTestContext();
        var instance = NewInstance("Report.Daily", JobStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-1));
        instance.CompletedAt = null;

        await context.JobStore.SaveJobInstanceAsync(instance);

        Assert.True(instance.CompletedAt.HasValue);
        var found = await context.JobStore.GetJobInstanceAsync(instance.InstanceId);
        Assert.NotNull(found);
        Assert.True(found.CompletedAt.HasValue);
    }

    /// <summary>
    /// 更新为终止状态时写入完成时间且不再算运行中
    /// </summary>
    [Fact]
    public async Task 更新为终止状态时写入完成时间且不再算运行中()
    {
        using var context = new TasksTestContext();
        var instance = NewInstance("Report.Daily", JobStatus.Running, DateTimeOffset.UtcNow);
        await context.JobStore.SaveJobInstanceAsync(instance);

        Assert.Single(await context.JobStore.GetRunningInstancesAsync("Report.Daily"));

        await context.JobStore.UpdateJobStatusAsync(instance.InstanceId, JobStatus.Succeeded);

        Assert.Empty(await context.JobStore.GetRunningInstancesAsync("Report.Daily"));
        var found = await context.JobStore.GetJobInstanceAsync(instance.InstanceId);
        Assert.NotNull(found);
        Assert.Equal(JobStatus.Succeeded, found.Status);
        Assert.True(found.CompletedAt.HasValue);
    }

    /// <summary>
    /// 更新不存在的实例不抛异常且不插入
    /// </summary>
    [Fact]
    public async Task 更新不存在的实例不抛异常且不插入()
    {
        using var context = new TasksTestContext();

        await context.JobStore.UpdateJobStatusAsync("not-exists", JobStatus.Failed);

        Assert.Equal(0, await context.Client.Queryable<SysJobInstance>().CountAsync());
    }

    /// <summary>
    /// 只返回该任务运行中的实例
    /// </summary>
    [Fact]
    public async Task 只返回该任务运行中的实例()
    {
        using var context = new TasksTestContext();
        var now = DateTimeOffset.UtcNow;
        var running = NewInstance("Report.Daily", JobStatus.Running, now);
        var succeeded = NewInstance("Report.Daily", JobStatus.Succeeded, now);
        var otherJob = NewInstance("Report.Weekly", JobStatus.Running, now);

        foreach (var instance in new[] { running, succeeded, otherJob })
        {
            await context.JobStore.SaveJobInstanceAsync(instance);
        }

        var result = await context.JobStore.GetRunningInstancesAsync("Report.Daily");

        var single = Assert.Single(result);
        Assert.Equal(running.InstanceId, single.InstanceId);
    }

    /// <summary>
    /// 超过截止时刻的运行中实例不再算运行中
    /// </summary>
    [Fact]
    public async Task 超过截止时刻的运行中实例不再算运行中()
    {
        using var context = new TasksTestContext();
        var stale = NewInstance("Report.Daily", JobStatus.Running, DateTimeOffset.UtcNow.AddHours(-2), timeoutMilliseconds: 1000);
        var active = NewInstance("Report.Daily", JobStatus.Running, DateTimeOffset.UtcNow, timeoutMilliseconds: 300000);
        await context.JobStore.SaveJobInstanceAsync(stale);
        await context.JobStore.SaveJobInstanceAsync(active);

        var result = await context.JobStore.GetRunningInstancesAsync("Report.Daily");

        var single = Assert.Single(result);
        Assert.Equal(active.InstanceId, single.InstanceId);

        var staleFound = await context.JobStore.GetJobInstanceAsync(stale.InstanceId);
        Assert.NotNull(staleFound);
        Assert.Equal(JobStatus.Running, staleFound.Status);
    }

    /// <summary>
    /// 任务名为空白时抛出参数异常
    /// </summary>
    [Fact]
    public async Task 任务名为空白时抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => context.JobStore.GetRunningInstancesAsync(" "));
    }

    /// <summary>
    /// 在租户上下文中保存时以宿主上下文写库
    /// </summary>
    [Fact]
    public async Task 在租户上下文中保存时以宿主上下文写库()
    {
        using var context = new TasksTestContext();
        var instance = NewInstance("Report.Daily", JobStatus.Running, DateTimeOffset.UtcNow);
        instance.TenantId = 42;

        using (context.Tenant.Change(42))
        {
            await context.JobStore.SaveJobInstanceAsync(instance);
        }

        Assert.NotEmpty(context.ExecutingTenantIds);
        Assert.All(context.ExecutingTenantIds, tenantId => Assert.Null(tenantId));

        var stored = await context.Client.Queryable<SysJobInstance>().FirstAsync();
        Assert.Equal(42L, stored.TenantId);
    }

    private static JobInstance NewInstance(
        string jobName,
        JobStatus status,
        DateTimeOffset startedAt,
        int timeoutMilliseconds = 300000)
    {
        return new JobInstance
        {
            JobName = jobName,
            JobInfo = new JobInfo
            {
                JobName = jobName,
                JobType = typeof(JobStoreInstanceTests),
                TimeoutMilliseconds = timeoutMilliseconds
            },
            Status = status,
            ScheduledAt = startedAt,
            StartedAt = startedAt,
            TriggerType = JobTriggerType.Cron
        };
    }

    private static void AssertClose(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.True(
            Math.Abs((expected - actual).TotalSeconds) < 1,
            $"期望 {expected:O}，实际 {actual:O}");
    }
}
```

`保存后按标识查回任务实例` 用 `+08:00` 偏移的输入：若实体时间列误用了 `DateTimeOffset`，在任何时区的机器上读回都会差出 8 小时，`AssertClose` 变红（硬约束 ②）。

`framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/JobStoreHistoryTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Tasks.ScheduledJobs.Models;

namespace XiHan.Framework.Tasks.SqlSugar.Tests;

/// <summary>
/// 定时任务存储的执行历史测试
/// </summary>
public class JobStoreHistoryTests
{
    /// <summary>
    /// 按开始时间倒序分页返回执行历史
    /// </summary>
    [Fact]
    public async Task 按开始时间倒序分页返回执行历史()
    {
        using var context = new TasksTestContext();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);
        var histories = Enumerable.Range(0, 5)
            .Select(index => NewHistory("Report.Daily", baseTime.AddHours(-index)))
            .ToList();

        for (var index = histories.Count - 1; index >= 0; index--)
        {
            await context.JobStore.SaveJobHistoryAsync(histories[index]);
        }

        await context.JobStore.SaveJobHistoryAsync(NewHistory("Report.Weekly", baseTime));

        var page1 = await context.JobStore.GetJobHistoryAsync("Report.Daily", 1, 2);
        var page2 = await context.JobStore.GetJobHistoryAsync("Report.Daily", 2, 2);
        var page3 = await context.JobStore.GetJobHistoryAsync("Report.Daily", 3, 2);

        string[] expected1 = [histories[0].HistoryId, histories[1].HistoryId];
        string[] expected2 = [histories[2].HistoryId, histories[3].HistoryId];
        Assert.Equal(expected1, page1.Select(history => history.HistoryId));
        Assert.Equal(expected2, page2.Select(history => history.HistoryId));
        var last = Assert.Single(page3);
        Assert.Equal(histories[4].HistoryId, last.HistoryId);
    }

    /// <summary>
    /// 历史标识为空时自动生成
    /// </summary>
    [Fact]
    public async Task 历史标识为空时自动生成()
    {
        using var context = new TasksTestContext();
        var history = NewHistory("Report.Daily", DateTimeOffset.UtcNow.AddHours(-1));
        history.HistoryId = string.Empty;
        history.IsSuccess = true;
        history.Remarks = "手动触发";

        await context.JobStore.SaveJobHistoryAsync(history);

        Assert.False(string.IsNullOrWhiteSpace(history.HistoryId));
        var single = Assert.Single(await context.JobStore.GetJobHistoryAsync("Report.Daily"));
        Assert.Equal(history.HistoryId, single.HistoryId);
        Assert.True(single.IsSuccess);
        Assert.Equal("手动触发", single.Remarks);
    }

    /// <summary>
    /// 保存空历史抛出参数异常
    /// </summary>
    [Fact]
    public async Task 保存空历史抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAsync<ArgumentNullException>(() => context.JobStore.SaveJobHistoryAsync(null!));
    }

    /// <summary>
    /// 查询参数非法时抛出参数异常
    /// </summary>
    [Fact]
    public async Task 查询参数非法时抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.JobStore.GetJobHistoryAsync("Report.Daily", 0, 20));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.JobStore.GetJobHistoryAsync("Report.Daily", 1, 0));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => context.JobStore.GetJobHistoryAsync(" "));
    }

    /// <summary>
    /// 清理早于保留期的历史与已结束实例
    /// </summary>
    [Fact]
    public async Task 清理早于保留期的历史与已结束实例()
    {
        using var context = new TasksTestContext();
        var now = DateTimeOffset.UtcNow;

        var oldHistory = NewHistory("Report.Daily", now.AddDays(-40));
        var recentHistory = NewHistory("Report.Daily", now.AddDays(-1));
        await context.JobStore.SaveJobHistoryAsync(oldHistory);
        await context.JobStore.SaveJobHistoryAsync(recentHistory);

        var oldSucceeded = NewInstance("Report.Daily", JobStatus.Succeeded, now.AddDays(-40));
        oldSucceeded.CompletedAt = now.AddDays(-40);
        var oldRunning = NewInstance("Report.Daily", JobStatus.Running, now.AddDays(-40));
        var recentSucceeded = NewInstance("Report.Daily", JobStatus.Succeeded, now.AddDays(-1));
        recentSucceeded.CompletedAt = now.AddDays(-1);

        foreach (var instance in new[] { oldSucceeded, oldRunning, recentSucceeded })
        {
            await context.JobStore.SaveJobInstanceAsync(instance);
        }

        await context.JobStore.CleanupHistoryAsync(30);

        var remaining = Assert.Single(await context.JobStore.GetJobHistoryAsync("Report.Daily"));
        Assert.Equal(recentHistory.HistoryId, remaining.HistoryId);
        Assert.Null(await context.JobStore.GetJobInstanceAsync(oldSucceeded.InstanceId));
        Assert.NotNull(await context.JobStore.GetJobInstanceAsync(oldRunning.InstanceId));
        Assert.NotNull(await context.JobStore.GetJobInstanceAsync(recentSucceeded.InstanceId));
    }

    /// <summary>
    /// 保留天数为负时抛出参数异常
    /// </summary>
    [Fact]
    public async Task 保留天数为负时抛出参数异常()
    {
        using var context = new TasksTestContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.JobStore.CleanupHistoryAsync(-1));
    }

    private static JobHistory NewHistory(string jobName, DateTimeOffset startedAt)
    {
        return new JobHistory
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            JobName = jobName,
            Status = JobStatus.Succeeded,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(1),
            TriggerType = JobTriggerType.Cron
        };
    }

    private static JobInstance NewInstance(string jobName, JobStatus status, DateTimeOffset startedAt)
    {
        return new JobInstance
        {
            JobName = jobName,
            JobInfo = new JobInfo { JobName = jobName },
            Status = status,
            ScheduledAt = startedAt,
            StartedAt = startedAt,
            TriggerType = JobTriggerType.Cron
        };
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0234`——`XiHan.Framework.Tasks.SqlSugar.ScheduledJobs` 尚不存在。

- [ ] **Step 4: 实现存储**

`framework/src/XiHan.Framework.Tasks.SqlSugar/ScheduledJobs/SqlSugarJobStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.Framework.Tasks.ScheduledJobs.Abstractions;
using XiHan.Framework.Tasks.ScheduledJobs.Models;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Entities;
using XiHan.Framework.Tasks.SqlSugar.Mapping;
using XiHan.Framework.Tasks.SqlSugar.Options;

namespace XiHan.Framework.Tasks.SqlSugar.ScheduledJobs;

/// <summary>
/// 定时任务存储的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 任务实例与执行历史写入默认布局的主库。
/// 运行中的实例在开始时间加任务超时再加 <see cref="XiHanTasksSqlSugarOptions.RunningInstanceGracePeriod"/> 之后不再视为运行中。
/// </remarks>
public class SqlSugarJobStore : IJobStore
{
    private readonly TasksHostClientAccessor _clientAccessor;
    private readonly XiHanTasksSqlSugarOptions _options;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientAccessor">宿主上下文客户端访问器</param>
    /// <param name="options">任务存储配置</param>
    public SqlSugarJobStore(
        TasksHostClientAccessor clientAccessor,
        IOptions<XiHanTasksSqlSugarOptions> options)
    {
        _clientAccessor = clientAccessor;
        _options = options.Value;
    }

    /// <summary>
    /// 保存任务实例，已存在则更新
    /// </summary>
    /// <remarks>
    /// 终止状态且完成时间为空时，把完成时间补为当前时间并回写到传入的实例上。
    /// </remarks>
    /// <param name="jobInstance">任务实例</param>
    public async Task SaveJobInstanceAsync(JobInstance jobInstance)
    {
        ArgumentNullException.ThrowIfNull(jobInstance);

        if (IsTerminal(jobInstance.Status))
        {
            jobInstance.CompletedAt ??= DateTimeOffset.UtcNow;
        }

        var entity = JobStoreMapper.ToEntity(jobInstance, _options.RunningInstanceGracePeriod);
        var instanceId = entity.BasicId;

        await _clientAccessor.ExecuteAsync(async client =>
        {
            var exists = await client.Queryable<SysJobInstance>()
                .AnyAsync(item => item.BasicId == instanceId);

            if (exists)
            {
                await client.Updateable(entity).ExecuteCommandAsync();
            }
            else
            {
                await client.Insertable(entity).ExecuteCommandAsync();
            }
        });
    }

    /// <summary>
    /// 更新任务实例状态，实例不存在时不做任何事
    /// </summary>
    /// <remarks>
    /// 终止状态同时把完成时间写为当前时间。
    /// </remarks>
    /// <param name="instanceId">实例唯一标识</param>
    /// <param name="status">状态</param>
    public async Task UpdateJobStatusAsync(string instanceId, JobStatus status)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        var statusValue = (int)status;

        if (IsTerminal(status))
        {
            var completedAt = DateTime.UtcNow;

            await _clientAccessor.ExecuteAsync(client => client.Updateable<SysJobInstance>()
                .SetColumns(item => new SysJobInstance
                {
                    Status = statusValue,
                    CompletedAt = completedAt
                })
                .Where(item => item.BasicId == instanceId)
                .ExecuteCommandAsync());

            return;
        }

        await _clientAccessor.ExecuteAsync(client => client.Updateable<SysJobInstance>()
            .SetColumns(item => new SysJobInstance
            {
                Status = statusValue
            })
            .Where(item => item.BasicId == instanceId)
            .ExecuteCommandAsync());
    }

    /// <summary>
    /// 保存任务执行历史，历史标识为空时生成新标识并回写到传入的历史上
    /// </summary>
    /// <param name="history">执行历史</param>
    public async Task SaveJobHistoryAsync(JobHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (string.IsNullOrWhiteSpace(history.HistoryId))
        {
            history.HistoryId = Guid.NewGuid().ToString("N");
        }

        var entity = JobStoreMapper.ToEntity(history);
        var historyId = entity.BasicId;

        await _clientAccessor.ExecuteAsync(async client =>
        {
            var exists = await client.Queryable<SysJobHistory>()
                .AnyAsync(item => item.BasicId == historyId);

            if (exists)
            {
                await client.Updateable(entity).ExecuteCommandAsync();
            }
            else
            {
                await client.Insertable(entity).ExecuteCommandAsync();
            }
        });
    }

    /// <summary>
    /// 获取任务实例
    /// </summary>
    /// <param name="instanceId">实例唯一标识</param>
    /// <returns>任务实例，不存在则为 null</returns>
    public async Task<JobInstance?> GetJobInstanceAsync(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        var entity = await _clientAccessor.ExecuteAsync(client => client.Queryable<SysJobInstance>()
            .Where(item => item.BasicId == instanceId)
            .FirstAsync());

        return entity is null ? null : JobStoreMapper.ToJobInstance(entity);
    }

    /// <summary>
    /// 获取任务执行历史，按开始时间倒序分页
    /// </summary>
    /// <param name="jobName">任务名称</param>
    /// <param name="pageIndex">页码，从 1 开始</param>
    /// <param name="pageSize">页大小</param>
    /// <returns>执行历史列表</returns>
    public async Task<IReadOnlyList<JobHistory>> GetJobHistoryAsync(string jobName, int pageIndex = 1, int pageSize = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "页码必须大于等于 1。");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "页大小必须大于等于 1。");
        }

        var entities = await _clientAccessor.ExecuteAsync(client => client.Queryable<SysJobHistory>()
            .Where(item => item.JobName == jobName)
            .OrderBy(item => item.StartedAt, OrderByType.Desc)
            .ToPageListAsync(pageIndex, pageSize));

        return [.. entities.Select(JobStoreMapper.ToJobHistory)];
    }

    /// <summary>
    /// 获取运行中的任务实例，运行截止时刻已过的实例不计入
    /// </summary>
    /// <param name="jobName">任务名称</param>
    /// <returns>运行中的任务实例列表</returns>
    public async Task<IReadOnlyList<JobInstance>> GetRunningInstancesAsync(string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        var runningStatus = (int)JobStatus.Running;
        var now = DateTime.UtcNow;

        var entities = await _clientAccessor.ExecuteAsync(client => client.Queryable<SysJobInstance>()
            .Where(item => item.JobName == jobName
                && item.Status == runningStatus
                && item.RunningDeadline != null
                && item.RunningDeadline > now)
            .ToListAsync());

        return [.. entities.Select(JobStoreMapper.ToJobInstance)];
    }

    /// <summary>
    /// 清理过期的执行历史与已结束的任务实例
    /// </summary>
    /// <remarks>
    /// 删除开始时间早于保留期的执行历史，以及状态为成功、失败或已取消且完成时间早于保留期的任务实例。
    /// </remarks>
    /// <param name="retentionDays">保留天数</param>
    public async Task CleanupHistoryAsync(int retentionDays)
    {
        if (retentionDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), retentionDays, "保留天数不能小于 0。");
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var succeeded = (int)JobStatus.Succeeded;
        var failed = (int)JobStatus.Failed;
        var canceled = (int)JobStatus.Canceled;

        await _clientAccessor.ExecuteAsync(async client =>
        {
            await client.Deleteable<SysJobHistory>()
                .Where(item => item.StartedAt < cutoff)
                .ExecuteCommandAsync();

            await client.Deleteable<SysJobInstance>()
                .Where(item => (item.Status == succeeded || item.Status == failed || item.Status == canceled)
                    && item.CompletedAt != null
                    && item.CompletedAt < cutoff)
                .ExecuteCommandAsync();
        });
    }

    /// <summary>
    /// 判断是否为终止状态
    /// </summary>
    private static bool IsTerminal(JobStatus status)
    {
        return status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS，含第 ① 份的全部用例。

排错指引：

- `保存后按标识查回任务实例` 的时间差出整数小时：实体时间列被声明成了 `DateTimeOffset`，或映射没走 `ToUtc` / `FromUtc`（硬约束 ②）
- `超过截止时刻的运行中实例不再算运行中` 返回两条：`GetRunningInstancesAsync` 缺了截止时刻条件（硬约束 ①）
- `按开始时间倒序分页返回执行历史` 第 1 页内容不对：核对是否直接把 `pageIndex` 传给了 `ToPageListAsync`
- `在租户上下文中保存时以宿主上下文写库` 失败：存储绕过了 `TasksHostClientAccessor`（硬约束 ④）

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 定时任务实例与执行历史落库"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。提交信息正文写明：运行中实例只认截止时刻未到的，避免崩溃遗留记录让不允许并发的任务永久停摆；清理同时删除过期的已结束实例，对应默认存储的数量淘汰。

---

### Task 3: 顶替主包的默认定时任务存储

**Files:**
- Modify: `framework/src/XiHan.Framework.Tasks.SqlSugar/Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksSqlSugarRegistrationTests.cs`（整份替换）

**Interfaces:**
- Consumes: Task 2 的 `SqlSugarJobStore`
- Produces: `AddXiHanTasksSqlSugar` 额外以单例顶替 `IJobStore`

**参考来源（动手前先读）：**
- 主包注册：`framework/src/XiHan.Framework.Tasks/ScheduledJobs/Extensions/DependencyInjection/XiHanTasksServiceCollectionExtensions.cs:60`
- 设计：spec §4.6、§5 ⑤

**本任务禁止事项：** 用 `Replace`，**不许**用 `TryAdd`。生命周期必须是 `Singleton`——`JobExecutor` 与 `CompositeJobScheduler` 都是单例且构造函数注入 `IJobStore`。不要改第 ① 份的两行注册。

- [ ] **Step 1: 写失败的测试**

把 `framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/TasksSqlSugarRegistrationTests.cs` 整份替换为：

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
using XiHan.Framework.Tasks.ScheduledJobs.Abstractions;
using XiHan.Framework.Tasks.ScheduledJobs.Store;
using XiHan.Framework.Tasks.SqlSugar.BackgroundJobs;
using XiHan.Framework.Tasks.SqlSugar.Clients;
using XiHan.Framework.Tasks.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Tasks.SqlSugar.ScheduledJobs;
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
    /// 定时任务存储被顶替为单例
    /// </summary>
    [Fact]
    public void 定时任务存储被顶替为单例()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<IJobStore, DefaultJobStore>();

        services.AddXiHanTasksSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IJobStore));
        Assert.Equal(typeof(SqlSugarJobStore), descriptor.ImplementationType);
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
        using var provider = BuildValidatingProvider();

        Assert.IsType<SqlSugarBackgroundJobStore>(provider.GetRequiredService<IBackgroundJobStore>());
    }

    /// <summary>
    /// 校验作用域时可从根容器解析定时任务存储
    /// </summary>
    [Fact]
    public void 校验作用域时可从根容器解析定时任务存储()
    {
        using var provider = BuildValidatingProvider();

        Assert.IsType<SqlSugarJobStore>(provider.GetRequiredService<IJobStore>());
    }

    private static ServiceProvider BuildValidatingProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FakeClock(TasksTestContext.BaseTime));
        services.AddTransient<ICurrentTenant>(_ => new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance));
        services.AddScoped<ISqlSugarClientResolver>(_ => throw new InvalidOperationException("解析存储时不应触达数据库。"));

        services.AddXiHanTasksSqlSugar(new ConfigurationBuilder().Build());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}
```

与第 ① 份的版本相比：新增 `定时任务存储被顶替为单例`、`校验作用域时可从根容器解析定时任务存储` 两个用例，并把构建容器的代码提成 `BuildValidatingProvider`。第 ① 份的三个用例语义不变。

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：`定时任务存储被顶替为单例` 失败（实现类型仍是 `DefaultJobStore`），`校验作用域时可从根容器解析定时任务存储` 失败（「No service for type IJobStore」）。其余用例 PASS。

- [ ] **Step 3: 追加注册**

在 `framework/src/XiHan.Framework.Tasks.SqlSugar/Extensions/DependencyInjection/XiHanTasksSqlSugarServiceCollectionExtensions.cs` 中：

using 区追加（按字母序放在 `XiHan.Framework.Tasks.BackgroundJobs.Abstractions` 之后）：

```csharp
using XiHan.Framework.Tasks.ScheduledJobs.Abstractions;
```

以及（放在 `XiHan.Framework.Tasks.SqlSugar.Options` 之后）：

```csharp
using XiHan.Framework.Tasks.SqlSugar.ScheduledJobs;
```

把：

```csharp
        services.Replace(ServiceDescriptor.Singleton<IBackgroundJobStore, SqlSugarBackgroundJobStore>());
```

改为：

```csharp
        services.Replace(ServiceDescriptor.Singleton<IBackgroundJobStore, SqlSugarBackgroundJobStore>());
        services.Replace(ServiceDescriptor.Singleton<IJobStore, SqlSugarJobStore>());
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Tasks.SqlSugar framework/test/XiHan.Framework.Tasks.SqlSugar.Tests
git commit -m "feat(tasks-sqlsugar): 以 SqlSugar 存储顶替默认定时任务存储"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 包 README、文档站收尾与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.Tasks.SqlSugar/README.md`（整份替换）
- Create: `docs/packages/tasks-sqlsugar.md`
- Modify: `docs/packages/index.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `framework/README.md`
- Modify: `framework/README_cn.md`
- Modify: `README.md`、`README_cn.md`（只改模块计数）

**Interfaces:**
- Consumes: 两份计划的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 文档站条目范本：`docs/packages/eventbus-sqlsugar.md`
- README 七段结构：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`
- 已知边界：第 ① 份 spec §7、本份 spec §7
- 新增模块要动的地方：仓库根 `CLAUDE.md`「新增一个模块要动的地方」

**本任务禁止事项：** 根 `README.md` / `README_cn.md` **只改模块计数，不往「常用包」表加行**（spec 待确认的决策第 9 条）。**不要**把计数写死成某个数字——实现时读当前值再加一（spec 待确认的决策第 12 条）。**不改** `docs/packages/tasks.md`、`docs/guide/` 下的任何文件。不改 `docs/package.json`、`docs/changelog.md`（版本号与更新日志由发版流程负责）。不把权衡论证写进代码注释。

- [ ] **Step 1: 补全包 README**

把 `framework/src/XiHan.Framework.Tasks.SqlSugar/README.md` 整份替换为：

````markdown
# XiHan.Framework.Tasks.SqlSugar

## 概述

`XiHan.Framework.Tasks` 的 SqlSugar 持久化提供程序。主包的 `DefaultBackgroundJobStore` 与 `DefaultJobStore` 都是进程内实现，进程重启即丢、不跨实例；本包把后台作业、定时任务实例与执行历史落到数据库。

## 核心能力

- 后台作业实体 `sys_background_job`：入队参与当前工作单元的事务，多实例领取互斥（条件抢占 + 租约超时释放），不依赖分布式锁，也不依赖任何数据库方言特性
- 定时任务实例 `sys_job_instance` 与执行历史 `sys_job_history`：执行记录落库，按任务名分页查询历史
- 运行中实例带截止时刻：执行途中崩溃遗留的「运行中」记录在超时后不再阻塞不允许并发的任务
- 以 `Replace` 顶替主包的 `IBackgroundJobStore` 与 `IJobStore`，生命周期保持单例

## 依赖关系

依赖 `XiHan.Framework.Tasks`（存储契约、轮询 Worker、调度器与执行器）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case。后台作业主键为作业自身的 `Guid`，任务实例与执行历史主键为契约自带的字符串标识。定时任务两张表的时间列均以协调世界时存储。

表结构由 `DbInitializer` 在应用启动时创建，这要求 `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 均为 `true`（二者默认均为 `false`）。都没开启又没有手工建表时，首次写入即抛「表不存在」。自行维护表结构时按本包实体的列定义建表。

配置节 `XiHan:Tasks:SqlSugar`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `BackgroundJobLeaseTimeout` | `00:05:00` | 后台作业租约时长，领取后超过该时长仍未删除或更新的作业可被重新领取 |
| `RunningInstanceGracePeriod` | `00:01:00` | 运行中任务实例的宽限期，开始时间加任务超时再加宽限期之后，实例不再视为运行中 |

所有记录固定写入默认布局的主库，并在写库期间切换到宿主上下文。业务数据写在模块库或租户独立库时，后台作业的入队与业务不在同一个事务里，且不会报错。

后台作业：

- 执行语义为**至少一次**：进程在作业执行成功与删除之间退出，作业会在租约过期后再次执行，作业处理器需幂等
- Worker 串行执行一轮领到的全部作业。未配置 Redis 时分布式锁只在进程内互斥，若一轮耗时超过租约时长，本轮尚未执行到的作业可能被另一实例领走并重复执行——此时调大 `BackgroundJobLeaseTimeout`，或调小 `XiHan:BackgroundJobs:MaxJobFetchCount`
- Worker 因停机或锁续期失败提前结束一轮时，已领取但未执行的作业要等租约过期才会被再次领取
- 放弃的作业保留在表里并标记 `Is_Abandoned = 1`，没有自动清理
- `UpdateAsync` 只更新已存在的作业，不存在时不插入；`InsertAsync` 遇主键重复时抛数据库异常；应用名为空与空字符串视为同一个应用
- 不要在事务型工作单元里调用领取：条件 `UPDATE` 持有的行锁要到工作单元提交才释放

定时任务：

- 每次执行新增一行实例与一行历史。框架不会自动清理，`XiHanJobOptions.HistoryRetentionDays` 也未被读取——应用需自行定期调用 `IJobStore.CleanupHistoryAsync`，它同时删除过期的执行历史与已结束的实例
- 多个节点共用一个库时，某节点的运行中实例会让其他节点跳过不允许并发的任务
- 截止时刻依赖协作式超时：任务代码不响应取消时，可能在截止时刻之后仍在运行，此时调度器会再触发一份
- 超过截止时刻的遗留实例仍标为 `Running`，只是不再阻塞调度
- `UpdateJobStatusAsync` 只写状态与完成时间，错误信息与耗时记录在执行历史里
- 读回的实例只还原任务信息的任务名、任务类型（能解析时）、触发类型与租户；执行参数的值读回为 `JsonElement`，无法序列化的参数存为空

## 使用方式

在应用启动模块上声明依赖 `XiHanTasksSqlSugarModule`。之后若再调用主包的 `UseRedisBackgroundJobStore()` 或 `XiHanJobBuilder.UseStore<T>()`，对应的存储会反过来覆盖本包——后调用者生效。

## 扩展点

需要自定义存储行为时，实现 `IBackgroundJobStore`（`XiHan.Framework.Tasks.BackgroundJobs.Abstractions`）或 `IJobStore`（`XiHan.Framework.Tasks.ScheduledJobs.Abstractions`）并在 DI 中 `Replace`。

## 目录结构

```
Entities/                        作业、任务实例与执行历史实体
Mapping/                         契约与实体的双向映射
Clients/                         宿主上下文的客户端访问器
BackgroundJobs/                  后台作业存储
ScheduledJobs/                   定时任务存储
Options/                         存储配置
Extensions/DependencyInjection/  服务注册扩展
```
````

- [ ] **Step 2: 新建文档站条目**

`docs/packages/tasks-sqlsugar.md`：

````markdown
# XiHan.Framework.Tasks.SqlSugar

> 后台作业与定时任务的 SqlSugar 持久化提供程序：作业入队与业务同事务、多实例领取互斥，定时任务实例与执行历史落库。替换 [Tasks](./tasks) 的进程内存储后，作业与执行记录才能跨进程重启与多实例共享。

- **NuGet**：`XiHan.Framework.Tasks.SqlSugar`
- **模块类**：`XiHanTasksSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Tasks](./tasks)（存储契约、轮询 Worker、调度器）、[Data](./data)（SqlSugar 客户端、工作单元连接登记、建表）

## 概述

[Tasks](./tasks) 的后台作业与定时任务各有一个存储契约：`IBackgroundJobStore` 与 `IJobStore`。两者的默认实现都是进程内字典——进程重启即丢，多实例之间也不共享。主包另带一个基于 Redis 的后台作业存储，但定时任务没有持久化选项。

本包把两者都落到数据表：

- **后台作业**：入队与业务同事务；N 个实例同时轮询，同一个作业只会被一个实例领走
- **定时任务**：每次执行的实例与历史落库，可按任务名分页查询；执行途中崩溃遗留的「运行中」记录在超时后自动失效，不会让不允许并发的任务永久停摆

## 何时使用

- 后台作业不能因进程重启而丢失，或应用以多实例部署
- 没有 Redis，或不希望后台作业的可靠性依赖 Redis
- 需要在管理后台查询定时任务的执行历史
- 已在用 [Data](./data)，希望作业与业务数据走同一套连接与事务

## 安装与启用

```bash
dotnet add package XiHan.Framework.Tasks.SqlSugar
```

在启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanTasksSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

建表需要开启 [Data](./data) 的库初始化与建表初始化，**两者默认都是关闭的**：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableDbInitialization": true,
        "EnableTableInitialization": true
      }
    }
  }
}
```

未开启又没有手工建表时，首次入队或首次执行定时任务即抛「表不存在」。

## 表结构

三张表都**不分表**，都写在默认布局的主库。

### `sys_background_job`

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `Guid`，主键 | 作业唯一标识 |
| `Row_Version` | `long` | 并发标识 |
| `Application_Name` | `string(128)`，非空 | 入队应用名，未指定时为空字符串 |
| `Tenant_Id` | `long`，可空 | 入队时的租户 |
| `Job_Name` | `string(256)`，非空 | 作业名称 |
| `Job_Args` | 大文本，非空 | 序列化后的作业参数 |
| `Try_Count` | `short` | 已尝试次数 |
| `Creation_Time` | `DateTime` | 创建时间 |
| `Next_Try_Time` | `DateTime` | 下次可执行时间 |
| `Last_Try_Time` | `DateTime`，可空 | 上次尝试时间 |
| `Is_Abandoned` | `bool` | 是否已放弃 |
| `Priority` | `int` | 优先级，值越大越优先 |
| `Claim_Token` | `string(64)`，可空 | 领取令牌 |
| `Claim_Time` | `DateTime`，可空 | 领取时刻，用于租约超时释放 |

后台作业的时间与 `IClock.Now` 同一口径。

### `sys_job_instance`

每次定时任务执行一行，主键为 `JobInstance.InstanceId`。主要列：`Job_Name`、`Job_Type_Name`、`Status`、`Trigger_Type`、`Tenant_Id`、`Scheduled_At`、`Started_At`、`Completed_At`、`Duration_Milliseconds`、`Running_Deadline`、`Retry_Count`、`Execution_Node`、`Trace_Id`、`Parameters_Json`、`Error_Message`、`Stack_Trace`。时间列均为协调世界时。

### `sys_job_history`

每次定时任务执行一行，主键为 `JobHistory.HistoryId`。主要列：`Instance_Id`、`Job_Name`、`Status`、`Started_At`、`Completed_At`、`Duration_Milliseconds`、`Tenant_Id`、`Trigger_Type`、`Is_Success`、`Error_Message`、`Stack_Trace`、`Retry_Count`、`Execution_Node`、`Trace_Id`、`Parameters_Json`、`Remarks`。时间列均为协调世界时。

## 工作原理

### 写库位置

所有写库都在宿主上下文里、对默认布局的主库进行。轮询 Worker 与调度器都运行在无租户上下文的后台作用域，只能看到宿主布局；作业若写进租户独立库，就永远不会被执行。

后台作业入队时若存在事务型工作单元，连接会登记进该工作单元，作业与业务数据同事务提交或回滚（业务也在主库时）。

### 后台作业的领取

分三步，全部使用方言无关的表达式 API：

1. 查出可领取作业（应用名相等、未放弃、已到期、未被领取或租约已过期）的主键，按优先级降序、已尝试次数升序、下次执行时间升序取一批
2. 条件 `UPDATE` 为这批主键盖上本次令牌与领取时刻，`WHERE` 里重复可领取条件
3. 按本次令牌取回真正抢到的作业

第 2 步的每行 `UPDATE` 是原子的，两个并发领取者只有一个能让某行在可领取状态下被改写。候选全被抢走时另选一批重试，最多三轮。

执行成功后 Worker 删除作业；失败或放弃后 Worker 回写作业，租约随之结束；进程在执行途中退出时，租约在超时后过期、作业重新可领取。

本包不依赖 Worker 的分布式锁：未配置 Redis 时该锁只在进程内互斥。

### 定时任务的运行中实例

不允许并发的任务在触发前会查询「是否有运行中实例」。执行途中进程退出会留下一条永远是「运行中」的记录。本包为运行中实例记录截止时刻（开始时间 + 任务超时 + 宽限期），查询只认截止时刻未到的实例。

## 配置

配置节 `XiHan:Tasks:SqlSugar`（`XiHanTasksSqlSugarOptions.SectionName`）。

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `BackgroundJobLeaseTimeout` | `TimeSpan` | `00:05:00` | 后台作业租约时长 |
| `RunningInstanceGracePeriod` | `TimeSpan` | `00:01:00` | 运行中任务实例的宽限期 |

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanTasksSqlSugarModule` | 模块类，声明依赖即启用 |
| `SqlSugarBackgroundJobStore` | `IBackgroundJobStore` 的 SqlSugar 实现，单例 |
| `SqlSugarJobStore` | `IJobStore` 的 SqlSugar 实现，单例 |
| `TasksHostClientAccessor` | 在宿主上下文中取默认布局主库客户端的访问器 |
| `SysBackgroundJob` / `SysJobInstance` / `SysJobHistory` | 三个实体 |
| `BackgroundJobMapper` / `JobStoreMapper` | 契约与实体的双向映射 |
| `XiHanTasksSqlSugarOptions` | 租约与宽限期配置 |

## 注意事项与最佳实践

- **后台作业的执行语义是至少一次**。作业处理器必须幂等。
- **租约要大于一轮的执行耗时**。未配置 Redis 的多实例部署下，一轮耗时超过租约时，尚未执行到的作业可能被另一实例重复领取。调大 `BackgroundJobLeaseTimeout`，或调小 `XiHan:BackgroundJobs:MaxJobFetchCount`。
- **`GetWaitingJobsAsync` 是领取不是查询**。调用后作业已被盖上令牌，不要在别处当作只读查询复用，也不要在事务型工作单元里调用它。
- **执行记录只增不减**。框架不会自动清理，需应用定期调用 `IJobStore.CleanupHistoryAsync`；放弃的后台作业同样需要应用自行清理。
- **运行中实例对所有节点可见**。多节点共用一个库时，不允许并发的任务在节点之间也互斥。
- **跨库写入不是一个事务**。业务数据在模块库或租户独立库时，作业的入队与业务各自提交。
- **两个存储的生命周期仍是单例**，与主包一致。之后调用 `UseRedisBackgroundJobStore()` 或 `XiHanJobBuilder.UseStore<T>()` 会覆盖本包。

## 扩展点 / 自定义

需要完全自定义存储行为时，实现 `IBackgroundJobStore` 或 `IJobStore` 并在 DI 中 `Replace`。

## 依赖模块

- [Tasks](./tasks)：存储契约、轮询 Worker、调度器与执行器
- [Data](./data)：SqlSugar 客户端解析、工作单元连接登记、建表初始化

## 相关模块

- [MultiTenancy](./multitenancy)：写库期间切换到的宿主上下文
- [Uow](./uow)：后台作业入队所参与的工作单元
- [EventBus.SqlSugar](./eventbus-sqlsugar)：同一套条件抢占领取协议的发件箱实现
- [Auditing.SqlSugar](./auditing-sqlsugar)：同一套落库范式的审计日志实现
````

- [ ] **Step 3: 更新文档站侧边栏与模块清单**

编辑 `docs/.vitepress/config.ts`，在「存储 · 模板 · 任务 · 治理」分组里、这一行之后：

```ts
          pkg("Tasks 定时任务", "tasks"),
```

插入：

```ts
          pkg("Tasks.SqlSugar", "tasks-sqlsugar"),
```

编辑 `docs/packages/index.md`，在这一行之后：

```markdown
| [Tasks](./tasks) | 定时任务：调度引擎（Cron/间隔/延迟）、后台服务基类、多租户感知 |
```

插入：

```markdown
| [Tasks.SqlSugar](./tasks-sqlsugar) | 任务 SqlSugar 持久化提供程序：后台作业入队同事务、多实例领取互斥，定时任务实例与执行历史落库 |
```

- [ ] **Step 4: 更新框架模块清单**

编辑 `framework/README.md`，在这一行之后：

```markdown
| `Tasks` | Scheduled tasks and background jobs: scheduling engine, background services, tenant awareness |
```

插入：

```markdown
| `Tasks.SqlSugar` | SqlSugar persistence provider for tasks: background-job enqueue joins the business transaction and claiming is mutually exclusive across instances; scheduled-job instances and execution history are persisted |
```

编辑 `framework/README_cn.md`，在这一行之后：

```markdown
| `Tasks` | 定时任务与后台作业：调度引擎、后台服务、多租户感知 |
```

插入：

```markdown
| `Tasks.SqlSugar` | 任务 SqlSugar 持久化提供程序：后台作业入队与业务同事务、多实例领取互斥，定时任务实例与执行历史落库 |
```

- [ ] **Step 5: 模块计数与测试工程计数各加一**

八个 SqlSugar 包先后落地，计数在你动手时可能已经不是写本计划时的 68。**先读当前值，再加一，不要照抄任何写死的数字。**

先读出当前的模块计数（取根 README 徽章里的数字）：

```bash
grep -n "Modules-[0-9]*-1f6feb" README.md README_cn.md
```

记下徽章里的数字为 `N`（写本计划时 `N = 68`）。然后列出四个文件里所有出现 `N` 的地方：

```bash
grep -n "N" README.md README_cn.md framework/README.md framework/README_cn.md
```

（把命令里的 `N` 换成实际数字。）写本计划时共 12 处，每个文件 3 处，行号会随先落地的包漂移，**以 grep 结果为准**：

| 文件 | 写本计划时的行 | 内容 | 改为 |
| --- | --- | --- | --- |
| `README.md` | 8 | `N modular components` | `N+1` |
| `README.md` | 20 | 徽章 URL `Modules-N-1f6feb`（数字藏在 URL 中间，最容易漏） | `Modules-(N+1)-1f6feb` |
| `README.md` | 55 | `all N packages` | `N+1` |
| `README_cn.md` | 8、20、55 | 同上三处（`N 个模块化组件`、徽章、`N 个包`） | `N+1` |
| `framework/README.md` | 55 | `N modules, one per project` | `N+1` |
| `framework/README.md` | 186 | `src/ ... (N modules)` | `N+1` |
| `framework/README.md` | 198 | `test/ ... N unit-test projects` —— **测试工程计数**，与模块计数是两个数 | 当前值 + 1（本包新增了一个测试项目） |
| `framework/README_cn.md` | 55、186、198 | 同上三处，198 行是测试工程计数 | 同上 |

注意：`framework/README*.md` 第 198 行那一处是**测试工程**计数，它当前恰好也等于模块计数，但两者独立——若 grep 出的测试工程计数与模块计数不同，按它自己的当前值加一。

grep 命中的行里若有与计数无关的同一数字（例如某个版本号、端口），不要改。改完再跑一次：

```bash
grep -n "Modules-[0-9]*-1f6feb" README.md README_cn.md
grep -rn "N" README.md README_cn.md framework/README.md framework/README_cn.md
```

预期：两个徽章都是 `N+1`；第二条命令（`N` 仍为旧值）不再命中任何计数位置。

根 README 的「常用包」表**不加行**。

- [ ] **Step 6: 文档站构建（本机有 pnpm 时）**

```bash
cd docs && pnpm install && pnpm build
```

预期：构建成功，没有 `dead link` 报错。本机没有 pnpm 时跳过本步，并在 PR 描述里注明未做文档站构建。

- [ ] **Step 7: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过（真实数据库测试在无环境变量时跳过）。若唯一的失败是 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`，那是已知的 GC 时序抖动，与本包无关，重跑一次即可。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`），先结束残留的测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.Tasks.SqlSugar.Tests.exe"
```

- [ ] **Step 8: 本机真库回归**

设置 `XIHAN_TEST_MYSQL` 后重跑测试项目，确认第 ① 份的 `并发领取时作业不重复` 仍然通过：

```bash
export XIHAN_TEST_MYSQL="Server=localhost;Port=3306;Database=xihan_test;Uid=root;Pwd=your_password;AllowPublicKeyRetrieval=true;SslMode=None;"
dotnet test --project framework/test/XiHan.Framework.Tasks.SqlSugar.Tests/XiHan.Framework.Tasks.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 9: 注释复查**

通读本计划新增或改动的每个 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即移出到提交信息。

特别检查：`JobStoreMapper` 与 `SqlSugarJobStore` 的 `<remarks>` 只描述截止时刻怎么算、清理删什么，**不解释**为什么需要截止时刻、为什么要归一到 UTC。

- [ ] **Step 10: 提交**

```bash
git add framework/src/XiHan.Framework.Tasks.SqlSugar/README.md docs/packages/tasks-sqlsugar.md docs/packages/index.md docs/.vitepress/config.ts framework/README.md framework/README_cn.md README.md README_cn.md
git commit -m "docs(tasks-sqlsugar): 补写包说明与文档站条目"
```

---

## 完成标准

本计划完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（真实数据库测试在无环境变量时跳过）
- `AddXiHanTasksSqlSugar` 之后容器里 `IBackgroundJobStore`、`IJobStore` 各只有一条注册，实现类型为本包的类型，生命周期 `Singleton`；`ValidateScopes = true` 时均可从根容器解析
- `IJobStore` 的 7 个方法行为与 `DefaultJobStore` 一致（参数校验、补完成时间、补历史标识、倒序分页）
- 超过截止时刻的运行中实例不出现在 `GetRunningInstancesAsync` 的结果里，但 `GetJobInstanceAsync` 仍如实返回其 `Running` 状态
- 以 `+08:00` 写入的时间读回后瞬时不变
- 在租户上下文里保存实例时，SQL 执行期间当前租户为 `null`，行上保留实例自带的租户
- `CleanupHistoryAsync` 删除过期历史与过期的已结束实例，保留运行中实例
- 包 README 七段结构、`docs/packages/tasks-sqlsugar.md`、`docs/packages/index.md`、`docs/.vitepress/config.ts`、`framework/README.md`、`framework/README_cn.md` 全部更新；四个 README 的模块计数（含两个徽章）与 `framework/README*.md` 的测试工程计数各按当前值加一；根 README 的「常用包」表未加行
- 本机配置 `XIHAN_TEST_MYSQL` 后第 ① 份的并发测试仍通过

## 已知边界（写入 PR 描述，不写进代码注释）

- **执行记录只增不减**：框架不调用 `CleanupHistoryAsync`，`XiHanJobOptions.HistoryRetentionDays` 未被读取，需应用定期清理。
- **清理也删除已结束实例**：契约只写了「清理历史」，本包扩大到终止状态的实例。
- **运行中实例对所有节点可见**：多节点共用一个库时，不允许并发的任务在节点之间也互斥，与内存存储「只看本进程」不同。
- **截止时刻依赖协作式超时**：任务代码不响应取消时，可能在截止时刻之后仍在运行，调度器会再触发一份。
- **遗留记录不改状态**：超过截止时刻的遗留实例仍标为 `Running`。
- **实例状态字段有限**：`UpdateJobStatusAsync` 只写状态与完成时间，错误信息与耗时在历史行里。
- **`JobInfo` 只还原最小快照**；**参数读回为 `JsonElement`**，无法序列化的参数存为空。
- **只落宿主主库**，与第 ① 份一致。
- **自定义存储会覆盖本包**：之后调用 `XiHanJobBuilder.UseStore<T>()` 或 `UseRedisBackgroundJobStore()` 按「后调用者生效」覆盖。
- **生命周期不变**：两个存储仍为 `Singleton`，不构成破坏性变更，无需配置层面的逃生口；要退回内存存储，去掉对 `XiHanTasksSqlSugarModule` 的依赖即可。
- **第 ① 份的全部已知边界**一并写入 PR 描述，连同反向验证的两个数字。
- **上游验收标准**：0 警告 0 错误是硬门槛；一个 PR 只做一件事——本 PR 只含 `Tasks.SqlSugar` 包、它的测试、slnx 注册与它的文档站条目，根 README 只改计数、不改「常用包」表，不含 `docs/packages/tasks.md` 的改动；注释只写代码做什么。

## 下一份计划

无。`Tasks.SqlSugar` 的两份计划到此完成，可以按仓库 `CLAUDE.md`「与上游协作」一节的流程从 `upstream/main` 开 worktree 整理并提交 PR（实现分支 `feat/tasks-sqlsugar` 基于 `dev`，提 PR 前需把本包的提交整理到基于 `upstream/main` 的分支上）。
