# P2：Auditing.SqlSugar 日志写入器 设计

- **日期**：2026-09-21
- **状态**：已评审通过，待实现
- **对应计划**：`.superpowers/plans/2026-09-21-auditing-sqlsugar-p2-writers.md`
- **前置**：P1（`.superpowers/specs/2026-09-21-auditing-sqlsugar-p1-entities-design.md`）必须已完成
- **系列**：SqlSugar 持久化层 P2 / 共 6 份（P1–P2 为 `Auditing.SqlSugar`，P3–P6 为 `EventBus.SqlSugar`）

> 本文档**自成一体**。实现 P2 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套，且**单元测试仍可能通过**，问题要到运行期才暴露。
>
> 第 2 节规定三件事，缺一不可：代码去哪找、文档去哪找、哪些不能做。
> 第 5 节列出四条**会静默失效**的陷阱——错了不报错、测试照样绿、日志照样丢。其中第一条是本份 spec 最容易踩的。

---

## 1. 背景与目标

P1 已建立 `XiHan.Framework.Auditing.SqlSugar` 包骨架与 5 个按月分表的日志实体，表能建出来，但 `XiHan.Framework.Auditing` 的 5 个 `Null*Writer` 仍在，日志依然写进黑洞。

**P2 把最后一步接上**：实现 5 个 SqlSugar 写入器，并以 `services.Replace` 顶替 5 个空实现。

**P2 结束时的状态**：应用声明 `[DependsOn(typeof(XiHanAuditingSqlSugarModule))]` 后，5 类日志自动落到按月分表的数据表，主键为雪花 ID。**P1 + P2 完成后 PR1 可提交。**

**成功标准**：

1. 5 个写入器全部经 `Replace` 顶替 `Null*Writer`，有测试断言
2. 日志经写入器落到当月分表，主键非零
3. 映射可脱离数据库单测
4. 包文档、VitePress 侧边栏、根 README 模块清单均已更新
5. 全解决方案构建 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 框架既有实现 —— 最高优先，本包必须与其一致**

```
framework/src/XiHan.Framework.Auditing/
  Writers/IOperationLogWriter.cs 及另外 4 个接口     写入器契约
  Writers/NullOperationLogWriter.cs                  被替换的空实现
  Workers/OperationLogQueueWorker.cs                 队列消费者如何调用写入器
  Extensions/DependencyInjection/
    XiHanAuditingServiceCollectionExtensions.cs:47-52  空写入器的注册位置（决定必须用 Replace）
  AccessLogRecord.cs  ApiLogRecord.cs  ExceptionLogRecord.cs
  LoginLogRecord.cs   OperationLogRecord.cs           映射的源字段
framework/src/XiHan.Framework.Data/SqlSugar/
  Clients/ISqlSugarClientResolver.cs                 如何取得正确的客户端
  Repository/SqlSugarRepositoryBase.cs               CRUD 的既定写法
framework/src/XiHan.Framework.DistributedIds/
  IDistributedIdGenerator.cs                         NextId() 返回 TKey
  Extensions/DependencyInjection/                    AddSingleton 出 IDistributedIdGenerator<long>
framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/   P1 产出的 5 个实体
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/
  Abstract/InsertableProvider/SplitInsertable.cs:42   分表插入的执行方法（注意无取消令牌重载）
  Abstract/InsertableProvider/InsertableProvider.cs:780  Insertable().SplitTable()
```

当前引用版本：`SqlSugarCore 5.1.4.221`。

**③ Admin.NET 生产参考**

```
/e/source/platfrom-admin/Admin.NET.Core/Logging/DatabaseLoggingWriter.cs
```

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 插入数据 | `插入數據.md` |
| 分表写入 | `自動分表.md` |
| 主键 | `雪花ID.md` |
| 异步操作 | `非同步操作.md` |

`sqlsugar-mcp` 的 notes 语料与该目录是同一批文件，直接读目录即可，无需接入 MCP。

仓库自身文档：`docs/packages/auditing.md`（本包新增文档的范本）、`docs/packages/data.md`。

### 2.3 禁止事项：EF Core 惯用法

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | SqlSugar 无 change tracking，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()` |
| `Migrations` / `Add-Migration` / `EnsureCreated()` | `CodeFirst.SplitTables().InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

### 2.4 P2 特有的禁止事项

- **不引入映射框架**。记录到实体是一一对应的平铺赋值，引入 AutoMapper 或 `XiHan.Framework.ObjectMapping` 会让上游审查质疑必要性。
- **不在写入器里开事务**。审计写入不参与业务事务（见 §4.4）。
- **不在写入器里做批量缓冲**。`WriteAsync` 是逐条契约，缓冲会让进程退出时丢数据。
- **不在映射里生成主键或取当前时间**。两者由调用方传入，映射才是纯函数。
- **不重做脱敏**（见 §5 第四条）。
- **不改动采集链路任何一行**。Pipeline、队列、Worker 全部保持原样。
- **不改 `IXxxLogWriter` 契约**。
- **不新增 `ProjectReference`**（见 §4.1 依赖可得性）。

## 3. 非目标

- **不做另外 12 个 Store**（权限、角色、用户、设置、租户、灰度、工作流、升级等）。本包只做审计日志，其余照本包建立的范式后续补齐。
- **不给写入器契约加批量重载**。属 `XiHan.Framework.Auditing` 主包的改动，是独立 PR。
- **不做日志查询 API、保留期清理、归档**。
- **不做 `EventBus.SqlSugar`**（P3–P6）。
- **不连接真实数据库**。本包无并发语义，SQLite 足够。
- **不修复 `docs/packages/data.md` 第 170 行的列名表述笔误**。单独 PR 处理。

## 4. 设计

### 4.1 新增结构

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  XiHanAuditingSqlSugarModule.cs                    修改：调用注册扩展
  Extensions/DependencyInjection/
    XiHanAuditingSqlSugarServiceCollectionExtensions.cs   新增：Replace 注册
  Mapping/AuditingLogMapper.cs                      新增：5 个纯静态映射方法
  Writers/
    SqlSugarAccessLogWriter.cs      SqlSugarApiLogWriter.cs
    SqlSugarExceptionLogWriter.cs   SqlSugarLoginLogWriter.cs
    SqlSugarOperationLogWriter.cs

framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  AuditingLogMapperTests.cs     新增：映射纯函数测试，不碰数据库
  LogWriterTests.cs             新增：SQLite 落库与注册断言
```

映射与写入分离：映射是纯函数、可脱库单测；写入器只负责取客户端、取主键、执行插入。

模块类**只做装配不写逻辑**——`ConfigureServices` 里只调一个 `services.AddXiHanAuditingSqlSugar()`，实现放在 `Extensions/DependencyInjection/`。

**依赖可得性**：`IDistributedIdGenerator<long>` 由 `XiHanDistributedIdsModule` 注册（`AddSingleton`），而 `XiHanDataModule` 已 `[DependsOn(typeof(XiHanDistributedIdsModule))]`，本包经 `XiHanDataModule` 间接获得，**不需要**新增 `ProjectReference` 或 `DependsOn`。

### 4.2 P1 已确立的约定（本份只需知道，不要改动）

写文档（§4.6）时需要引用这些；写代码时只需知道实体已经这样定义好了。

**五张表**，按月分表，实际表名带月份后缀（如 `sys_operation_log_20260901`）：

```
sys_access_log   sys_api_log   sys_exception_log
sys_login_log    sys_operation_log
```

| 约定 | 值 |
| --- | --- |
| 表名 | `sys_` 前缀、全小写下划线，依据 `docs/packages/data.md` |
| 分表 | `[SplitTable(SplitType.Month)]`，`[SugarTable]` 模板必须含 `{year}{month}{day}` 三个变量 |
| 分表字段 | `Created_Time`（`override` 基类 `CreatedTime` 并标注 `[SplitField]`） |
| 列名 | Pascal_Snake_Case，如 `Basic_Id`、`Trace_Id`、`Status_Code`，每列带 `ColumnDescription` |
| 主键 | 列名 `Basic_Id`，`long`，`IsIdentity = false`，雪花 ID |
| 实体基类 | `SugarCreationEntity<long>`，实现 `ISplitTableEntity` |
| 大文本列 | `ColumnDataType = StaticConfig.CodeFirst_BigString` |

`SysLoginLog` 的 `Login_Time` 是业务时间，与分表字段 `Created_Time` 各占一列。

**这些在 P2 一律不改**。发现需要调整的，停下来提出，不要在 P2 里顺手改——那会让 PR 同时包含实体变更与写入器实现两件事。

### 4.3 映射

`AuditingLogMapper` 是静态类，提供 5 个按参数类型重载的方法，签名统一为：

```
ToEntity(TRecord record, long basicId, DateTimeOffset createdTime) → TEntity
```

主键与创建时间由调用方传入。映射本身不生成 ID、不取当前时间、不做脱敏——这样它是纯函数，可脱离数据库单测。

字段一一对应平铺赋值。`SysLoginLog` 的 `LoginTime` 来自记录模型的同名业务字段，与 `CreatedTime` 各自独立。

### 4.4 写入

写入器经 `ISqlSugarClientResolver.GetClientForEntity<T>()` 取得客户端，用 `Insertable(entity).SplitTable().ExecuteCommandAsync()` 落库。

主键取自注入的 `IDistributedIdGenerator<long>.NextId()`（返回 `long`）。创建时间取 `DateTimeOffset.UtcNow`（原因见 §5 第三条）。

**审计写入不参与业务事务**。日志由后台 Worker 从队列消费，本就在业务请求之外，不应因业务回滚而丢失。写入器内不开事务、不加入工作单元。

**调用方式**：队列 Worker 的 `FlushAsync` 建一个 scope、解析一次写入器、对批次内每条记录逐条调用 `WriteAsync`。写入器注册为 `Scoped`。

### 4.5 注册

扩展方法 `AddXiHanAuditingSqlSugar(this IServiceCollection services)` 以 `services.Replace(ServiceDescriptor.Scoped<...>())` 逐个顶替 5 个 `Null*Writer`。`Replace` 位于 `Microsoft.Extensions.DependencyInjection.Extensions` 命名空间。

### 4.6 文档

- 更新包 `README.md` 的「核心能力」与「扩展点」两节
- 新增 `docs/packages/auditing-sqlsugar.md`，按 `docs/packages/auditing.md` 的结构组织
- `docs/.vitepress/config.ts` 的 packages 分组里、`auditing` 之后插入条目
- 根 `README.md` 与 `README_cn.md` 的模块清单加一行；若有模块总数（原为 66）一并 +1

## 5. 四条会静默失效的陷阱

这四条是本份 spec 最容易出错的地方。共同特征：**错了不报错、测试照样绿、日志照样丢**。

**① 注册必须用 `Replace`，`TryAdd` 是空操作。**

`XiHanAuditingModule` 在它的 `ConfigureServices` 里以 `TryAddScoped` 注册了 5 个 `Null*Writer`（`XiHanAuditingServiceCollectionExtensions.cs:48-52`）。本模块 `[DependsOn(typeof(XiHanAuditingModule))]`，依赖模块的 `ConfigureServices` **先执行**，因此本模块再用 `TryAdd` 是空操作，`Null*Writer` 会留在容器里，日志照样丢且无任何报错。主包代码注释本身就写明「应用侧用 Replace/自身注册覆盖」。

**② 主键只能经构造函数传入。**

`EntityBase<TKey>.BasicId` 是 `{ get; protected set; }`，**不能用对象初始化器赋值**。P1 的实体各有两个公开构造函数，映射里用 `new SysOperationLog(basicId) { ... }`。

**③ 创建时间用 `DateTimeOffset.UtcNow`，不要引入 `IClock`。**

`XiHan.Framework.Timing` 在本包的依赖链上**不可传递获得**——`Data`、`Auditing`、`Core`、`Uow`、`Domain`、`DistributedIds`、`MultiTenancy`、`Security` 均未引用它。为一个时间戳新增 `ProjectReference` 不划算。

**④ 不要重做脱敏。**

`LogSanitizer` 已在采集端执行——见 `framework/src/XiHan.Framework.Web.Api/Filters/XiHanActionLoggingFilter.cs:96-97`、`Middlewares/XiHanApiLoggingMiddleware.cs:161`、`Logging/ExceptionLogReporter.cs:65-67`。记录到达写入器时已脱敏，重复调用会二次遮蔽已遮蔽的内容。

**附带一条编译期会失败的**：`SplitInsertable.ExecuteCommandAsync()` **没有**接受 `CancellationToken` 的重载（`SplitInsertable.cs:42` 只有 `ExecuteCommand()`、`ExecuteCommandAsync()`、`ExecuteReturnSnowflakeId*`）。取消令牌只能在执行前 `ThrowIfCancellationRequested()` 检查一次，不要试图传进去，也不要为此改用非分表的 `Insertable` 重载——那会写错表。

## 6. 测试策略

CI 在 ubuntu 运行且不启动任何外部服务。P2 的测试全部可在 CI 裸跑：

**纯函数层** —— `AuditingLogMapper` 的 5 个映射，断言字段一一对应、主键与创建时间来自入参、`SysLoginLog` 的 `LoginTime` 与 `CreatedTime` 各自独立。不碰数据库。

**SQLite 集成层** —— 经写入器写入后能按时间区间查回，且主键非零。测试用一个固定返回同一客户端的 `ISqlSugarClientResolver` 桩。

**注册断言层** —— 先以 `TryAddScoped` 注册 `Null*Writer` 模拟主包行为，再调 `AddXiHanAuditingSqlSugar()`，断言容器里该服务类型只剩一个描述符且实现类型是 SqlSugar 版本。这一条直接守住 §5 第一条陷阱。

**不需要真实数据库**。本包无并发语义——写入是单条 `INSERT`，没有抢占、没有状态机。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 逐条插入 | `IXxxLogWriter.WriteAsync` 是单条契约，队列 Worker 批量取出后仍逐条调用（`OperationLogQueueWorker.FlushAsync`）。高吞吐下是 N 次往返。批量写入需给写入器契约加重载，属主包改动，不在本包范围 |
| 分表查询 | 跨月查询需显式 `SplitTable(begin, end)`，仓储层未暴露该能力，属框架既有缺口 |
| 不参与业务事务 | 有意设计，见 §4.4 |
| 时区 | `CreatedTime` 以 UTC 写入，分表边界按 UTC 划分。跨时区部署时分表切换点不随本地时间 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 5 个写入器全部经 `Replace` 顶替 `Null*Writer`，有测试断言
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `auditing-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本 PR 无关的文档

## 9. 下一份

P3（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p3-outbox-entity-design.md`，待写）：`XiHan.Framework.EventBus.SqlSugar` 的包骨架与发件箱实体、`OutgoingEventInfo` 双向映射。
