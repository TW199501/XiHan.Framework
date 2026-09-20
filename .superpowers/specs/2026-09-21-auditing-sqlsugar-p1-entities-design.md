# P1：Auditing.SqlSugar 包骨架与日志实体 设计

- **日期**：2026-09-21
- **状态**：已评审通过，待实现
- **对应计划**：`.superpowers/plans/2026-09-21-auditing-sqlsugar-p1-entities.md`
- **系列**：SqlSugar 持久化层 P1 / 共 6 份（P1–P2 为 `Auditing.SqlSugar`，P3–P6 为 `EventBus.SqlSugar`）

> 本文档**自成一体**。实现 P1 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套，且**单元测试仍可能通过**，问题要到运行期才暴露。
>
> 第 2 节规定三件事，缺一不可：代码去哪找、文档去哪找、哪些不能做。
> 第 5 节是 P1 唯一一条会**静默失效**的陷阱。

---

## 1. 背景与目标

`XiHan.Framework.Auditing` 已具备完整的审计日志采集链路：

```
5 条 Pipeline → ChannelLogQueue → 5 个 QueueWorker → 5 个 Writer 接口
```

管线、队列、后台消费者全部就绪，但 5 个 Writer 实现全是 `Null*Writer`——最后一步写进黑洞。

**P1 只做两件事**：建立 `XiHan.Framework.Auditing.SqlSugar` 包骨架，以及 5 个按月分表的日志实体。

**P1 结束时的状态**：表能被 `DbInitializer` 建出来，SQLite 下能写入并按时间区间查回，全解决方案 0 警告；**5 个 `Null*Writer` 仍未被替换**——写入器实现是 P2 的事。

**成功标准**：

1. 5 个实体存在，表名带 `sys_` 前缀与三个分表变量，分表字段为 `Created_Time`
2. SQLite 下 `CodeFirst.SplitTables().InitTables()` 能建出当月分表
3. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 框架既有实现 —— 最高优先，本包必须与其一致**

```
framework/src/XiHan.Framework.Data/SqlSugar/
  Entities/SugarCreationEntity.cs         列映射范本（Basic_Id / Created_Time / Row_Version）
  Entities/SugarFullAuditedEntity.cs      完整列映射示例
  Initializers/DbInitializer.cs:384-386   分表建表的调用方式
framework/src/XiHan.Framework.Domain/Entities/
  Abstracts/ISplitTableEntity.cs          分表标记接口
  EntityBase.cs                           BasicId 的可见性（见 §5）
framework/src/XiHan.Framework.Auditing/
  AccessLogRecord.cs  ApiLogRecord.cs  ExceptionLogRecord.cs
  LoginLogRecord.cs   OperationLogRecord.cs        实体要镜像的字段来源
framework/src/XiHan.Framework.EventBus.Kafka/
  XiHan.Framework.EventBus.Kafka.csproj   兄弟子包的 csproj 范本
  XiHanKafkaEventBusModule.cs             兄弟子包的模块类范本
framework/src/XiHan.Framework.Data/README.md        README 七段结构范本
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/
  IntegrationServices/SplitTableService.cs:78-80   分表字段对 DateTimeOffset 的处理
  Infrastructure/StaticConfig.cs:16                CodeFirst_BigString 常量
  Abstract/CodeFirstProvider/                      建表
```

当前引用版本：`SqlSugarCore 5.1.4.221`（见 `framework/src/XiHan.Framework.Data/XiHan.Framework.Data.csproj`）。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 按月分表 | `自動分表.md` |
| 建表与实体元数据 | `實體管理EntityMaintenance.md`、`庫表管理DbMaintenance.md` |
| 列类型配置 | `修改配置.md` |
| 主键 | `雪花ID.md` |

`sqlsugar-mcp` 的 notes 语料与该目录是同一批文件，直接读目录即可，无需接入 MCP。

仓库自身文档：`docs/packages/data.md`（表名约定 `sys_user` / `sys_tenant` / `sys_archive`、建表选取规则）。

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

### 2.4 P1 特有的禁止事项

- **不给记录模型加 SqlSugar 特性**。`OperationLogRecord` 等位于主包 `XiHan.Framework.Auditing`，主包不依赖 SqlSugar。实体是本包内的独立类型。
- **不写任何写入器**。P1 的模块类是空装配，`Null*Writer` 保持不变。
- **不新增 `PackageReference`**。SqlSugar 经 `XiHan.Framework.Data` 传递引入。
- **主键不能自增**。`IsIdentity` 必须为 `false`——分表要求主键不自增。
- **不改动采集链路任何一行**。

## 3. 非目标

- **不实现写入器**（P2）。
- **不接入 `DistributedIds`**。P1 不需要生成主键，测试里用 `DateTime.UtcNow.Ticks` 即可；雪花 ID 接入是 P2 的事。
- **不做 `EventBus.SqlSugar`**（P3–P6）。
- **不做另外 12 个 Store**。
- **不连接真实数据库**。P1 无并发语义，SQLite 足够。
- **不修复 `docs/packages/data.md` 第 170 行的列名表述笔误**（文档写 snake_case，代码实为 Pascal_Snake_Case）。单独 PR 处理。

## 4. 设计

### 4.1 包结构

沿用仓库既有的兄弟子包模式（`EventBus.Kafka`、`Bot.Telegram`）：主包不依赖实现包，实现包依赖主包与 `XiHan.Framework.Data`。

```
framework/src/XiHan.Framework.Auditing.SqlSugar/
  XiHan.Framework.Auditing.SqlSugar.csproj
  XiHanAuditingSqlSugarModule.cs      P1 阶段为空装配
  README.md                           固定七段结构
  Entities/
    SysAccessLog.cs   SysApiLog.cs   SysExceptionLog.cs
    SysLoginLog.cs    SysOperationLog.cs

framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/
  EntityMappingTests.cs          实体元数据断言
  TableInitializationTests.cs    SQLite 建表与读写
```

每个实体一个文件，职责单一，实体之间无依赖，可独立审查。

- csproj 按序 Import `netcore` / `common` / `version` / `nuget` 四个 props，使用 `Microsoft.NET.Sdk`
- 模块类命名 `XiHanAuditingSqlSugarModule`，置于项目根目录
- 注册进 `framework/XiHan.Framework.slnx` 的 `/1.src/6.Infrastructure/` 文件夹，紧随 `XiHan.Framework.Auditing` 之后
- 测试项目注册进 slnx 的测试文件夹，Import `props/test.props`

### 4.2 表与实体

五张表分别镜像 `AccessLogRecord` / `ApiLogRecord` / `ExceptionLogRecord` / `LoginLogRecord` / `OperationLogRecord` 的字段：

```
sys_access_log   sys_api_log   sys_exception_log
sys_login_log    sys_operation_log
```

**表名**：`sys_` 前缀、全小写下划线。依据 `docs/packages/data.md`。

**分表**：按月。实体标注 `[SplitTable(SplitType.Month)]`，`[SugarTable]` 的模板**必须同时含 `{year}{month}{day}` 三个变量**——这是 SqlSugar 为日后改分表粒度保留的兼容要求，即使按月也要写全，即 `"sys_operation_log_{year}{month}{day}"`。实体实现 `ISplitTableEntity`。

**分表字段**：复用基类的 `CreatedTime`。它是 `DateTimeOffset`，SqlSugar 原生支持——`SplitTableService.cs:78-80` 对 `DateTimeOffset` 取 `.DateTime`。实现方式是 `override` 基类属性并在 override 上标注 `[SplitField]`。

`SysLoginLog` 的 `LoginTime` 是记录模型自带的业务时间，**不是**分表字段，两者语义不同，各占一列。

**列名**：Pascal_Snake_Case，与 `SugarFullAuditedEntity` 一致（`Basic_Id`、`Row_Version`、`Created_Time`、`Is_Deleted`）。新增列同例：`Trace_Id`、`Status_Code`、`Elapsed_Milliseconds`。每列必须带 `ColumnDescription` 简体中文说明。

**大文本列**：`ColumnDataType = StaticConfig.CodeFirst_BigString`。该常量定义在 `StaticConfig.cs:16`，值为 `"varcharmax,longtext,text,clob"`——各方言的大文本类型清单，`CodeFirstProvider` 按当前 `DbType` 从中挑选。这是可移植写法，**不要**改成写死的 `"text"` 或 `"longtext"`。

**基类**：`SugarCreationEntity<long>`。它提供 `Basic_Id`、`Row_Version`、`Created_Time`、`Created_Id`、`Created_By`。日志只需创建审计，不需要修改与软删除，因此不用 `SugarFullAuditedEntity`。

**主键**：列名 `Basic_Id`，类型 `long`，`IsIdentity = false`。

### 4.3 数据库可移植性

只使用 SqlSugar 的通用表达式 API 与方言无关的常量，不写针对特定数据库的原生 SQL 或写死的列类型。

## 5. 一条会静默失效的陷阱

**主键只能经构造函数传入。**

`EntityBase<TKey>.BasicId` 是 `{ get; protected set; }`，**不能用对象初始化器赋值**。`EntityBase<TKey>` 与 `CreationEntityBase<TKey>` 提供 `protected` 的 `(TKey basicId)` 构造函数。

因此每个实体需要两个公开构造函数：

- 无参的，供 SqlSugar 物化实体
- `(long basicId)` 的，供调用方指定主键

漏掉后者的后果：调用方无法设置主键，所有行的 `Basic_Id` 都是 `0`，第二条插入即主键冲突。这一条在 P2 的写入器里会立刻用到。

## 6. 测试策略

CI 在 ubuntu 运行且不启动任何外部服务。P1 的测试全部可在 CI 裸跑：

**实体元数据层（反射断言）** —— 表名带 `sys_` 前缀与三个分表变量、`SplitType.Month`、`[SplitField]` 在 `CreatedTime` 上、实现 `ISplitTableEntity`、列名为 Pascal_Snake_Case、`SysLoginLog` 的 `LoginTime` 与 `CreatedTime` 分列且前者无 `[SplitField]`。

**SQLite 集成层** —— `CodeFirst.SplitTables().InitTables()` 能为 5 个实体建出当月分表；写入一条操作日志后用 `SplitTable(begin, end)` 按时间区间查回。

**不需要真实数据库**。P1 无并发语义。

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 分表查询 | 跨月查询需显式 `SplitTable(begin, end)`，仓储层未暴露该能力，属框架既有缺口 |
| 时区 | `CreatedTime` 以 UTC 写入，分表边界按 UTC 划分。跨时区部署时分表切换点不随本地时间 |
| 表结构变更 | 实体改字段后需重跑 `CodeFirst.SplitTables().InitTables()` 同步既有分表，框架经 `DbInitializer` 在启动时处理 |

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
- 5 个 `Null*Writer` **仍未被替换**
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `auditing-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本 PR 无关的文档

## 9. 下一份

P2（`.superpowers/specs/2026-09-21-auditing-sqlsugar-p2-writers-design.md`）：5 个写入器、纯函数映射、雪花 ID 接入、`Replace` 注册、文档。P1 + P2 完成后 PR1 可提交。
