# XiHan.Framework.Auditing.SqlSugar

> 审计日志的 SqlSugar 持久化提供程序：5 类日志实体（按月分表）与 5 个写入器，替换 [Auditing](./auditing) 的空写入器后日志才真正落库。

- **NuGet**：`XiHan.Framework.Auditing.SqlSugar`
- **模块类**：`XiHanAuditingSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Auditing](./auditing)（记录模型与写入器契约）、[Data](./data)（SqlSugar 客户端、雪花主键、建表）

## 概述

[Auditing](./auditing) 负责「采集什么、怎么异步化、怎么脱敏」，但它的 5 个 `IXxxLogWriter` 默认实现全是空实现——日志采集到就被丢弃。本包补上最后一步：把这 5 类日志写进数据表。

启用本包后，[Web.Api](./web-api) 各中间件与过滤器采集到的记录会自动落到 5 张按月分表的数据表，应用侧不需要再写任何写入代码。

本包只提供**这 5 类日志**的落库实现。实体变更日志（`IEntityDiffLogWriter`）不在本包范围内，仍是空实现。

## 何时使用

- 需要把访问 / 操作 / 异常 / 接口 / 登录日志存进关系型数据库
- 日志量级大到需要按月切表，同时希望建表由框架在启动时完成
- 已在用 [Data](./data)，希望审计日志与业务数据走同一套多数据源、读写分离与租户路由

## 安装与启用

```bash
dotnet add package XiHan.Framework.Auditing.SqlSugar
```

```csharp
[DependsOn(typeof(XiHanAuditingSqlSugarModule))]
public class MyModule : XiHanModule { }
```

`XiHanAuditingSqlSugarModule.ConfigureServices` 调用 `services.AddXiHanAuditingSqlSugar()`，以 `services.Replace` 逐个顶替 [Auditing](./auditing) 用 `TryAddScoped` 注册的 5 个空写入器：

| 契约 | 默认实现 | 本包实现 |
| --- | --- | --- |
| `IAccessLogWriter` | `NullAccessLogWriter` | `SqlSugarAccessLogWriter` |
| `IApiLogWriter` | `NullApiLogWriter` | `SqlSugarApiLogWriter` |
| `IExceptionLogWriter` | `NullExceptionLogWriter` | `SqlSugarExceptionLogWriter` |
| `ILoginLogWriter` | `NullLoginLogWriter` | `SqlSugarLoginLogWriter` |
| `IOperationLogWriter` | `NullOperationLogWriter` | `SqlSugarOperationLogWriter` |

本包**没有自己的配置节**，两条既有配置决定它的行为：

- **建表**：`XiHan:Data:SqlSugarCore:EnableTableInitialization`（默认 `false`）。打开后 [Data](./data) 的 `DbInitializer` 扫描全部 `[SugarTable]` 实体，对这 5 个实体走 `CodeFirst.SplitTables().InitTables()` 建出当月分表
- **队列**：`XiHan:Auditing:LogQueue` 的 5 个 `EnableXxxLogQueue`（默认全 `false`，即同步写入器调用），语义见 [Auditing](./auditing)

## 表结构

```
sys_access_log   sys_api_log   sys_exception_log
sys_login_log    sys_operation_log
```

| 约定 | 值 |
| --- | --- |
| 表名 | `sys_` 前缀、全小写下划线 |
| 分表 | `[SplitTable(SplitType.Month)]` 按月；`[SugarTable]` 模板含 `{year}{month}{day}` 三个变量，实际表名形如 `sys_operation_log_20260901` |
| 分表字段 | `Created_Time`（`DateTimeOffset`，实体上 `override` 基类属性并标注 `[SplitField]`） |
| 列名 | Pascal_Snake_Case，每列带简体中文 `ColumnDescription` |
| 主键 | `Basic_Id`，`long`，`IsIdentity = false`，雪花 ID |
| 实体基类 | `SugarCreationEntity<long>`，实现 `ISplitTableEntity` |
| 大文本列 | `ColumnDataType = StaticConfig.CodeFirst_BigString`，由 SqlSugar 按当前数据库方言挑选类型 |

`SysLoginLog` 的 `Login_Time` 是记录模型自带的业务时间，与分表字段 `Created_Time` 各占一列：前者由应用写入，后者由写入器在落库时生成。

5 张表的列与 [Auditing](./auditing) 的记录模型逐字段对应，外加基类的 `Basic_Id`、`Row_Version`、`Created_Time`、`Created_Id`、`Created_By`。

## 工作原理

```text
采集端（Web.Api 中间件 / 过滤器）—— 已脱敏
   └→ IXxxLogPipeline.WriteAsync(record)
         ├ 队列关闭（默认）→ 直接 await IXxxLogWriter.WriteAsync(record)
         └ 队列打开         → XxxLogQueueWorker 攒批 → 逐条 WriteAsync
                                                │
                                                ▼
                          AuditingLogMapper.ToEntity(record, id, createdTime)
                                                │  纯函数：主键与时间由调用方传入
                                                ▼
                          ISqlSugarClientResolver.GetClientForEntity<TEntity>()
                                                │
                                                ▼
                          Insertable(entity).SplitTable().ExecuteCommandAsync()
```

写入器做三件事：向 `IDistributedIdGenerator<long>` 取主键、取 `DateTimeOffset.UtcNow` 作为创建时间、把记录交给映射器后插入对应分表。字段搬运全在 `AuditingLogMapper`，它是静态纯方法，可脱离数据库单测。

客户端经 `ISqlSugarClientResolver` 取得，因此这 5 张表遵循 [Data](./data) 的多数据源与租户路由规则：默认落当前租户的主库。

**审计写入不参与业务事务。** 日志由后台 Worker 在业务请求之外消费，写入器不开事务、不加入工作单元——业务回滚不应带走已经发生的审计记录。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `SysAccessLog` / `SysApiLog` / `SysExceptionLog` / `SysLoginLog` / `SysOperationLog` | 5 张分表的实体，各带无参与 `(long basicId)` 两个公开构造函数 |
| `AuditingLogMapper` | 静态映射器，5 个按记录类型重载的 `ToEntity(record, basicId, createdTime)` |
| `SqlSugarAccessLogWriter` 等 5 个 | 写入器实现（`Scoped`），构造参数为 `ISqlSugarClientResolver` 与 `IDistributedIdGenerator<long>` |
| `XiHanAuditingSqlSugarServiceCollectionExtensions` | `AddXiHanAuditingSqlSugar()`：以 `Replace` 注册 5 个写入器 |
| `XiHanAuditingSqlSugarModule` | 模块类，`[DependsOn(XiHanAuditingModule, XiHanDataModule)]`，只做装配 |

## 使用示例

### 1. 启用并建表

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

```csharp
[DependsOn(typeof(XiHanAuditingSqlSugarModule))]
public class AppFoundationModule : XiHanModule { }
```

启动时 `DbInitializer` 建出 5 张当月分表；此后每月首次写入时由 SqlSugar 补建新分表。

### 2. 跨月查询日志

分表后必须显式给出时间区间，SqlSugar 才会合并多张分表：

```csharp
var client = clientResolver.GetClientForEntity<SysOperationLog>();

var logs = await client.Queryable<SysOperationLog>()
    .Where(item => item.TraceId == traceId)
    .SplitTable(begin, end)
    .ToListAsync();
```

### 3. 换掉某个写入器

应用侧要加自己的处理（如同时推送到消息队列），注册顺序在本模块之后，并同样使用 `Replace`：

```csharp
services.Replace(ServiceDescriptor.Scoped<IOperationLogWriter, MyOperationLogWriter>());
```

## 扩展点 / 自定义

- **换落库方式**：`Replace` 掉任一 `IXxxLogWriter`，例如改写成批量 `INSERT` 或同时转发到外部收集器
- **换映射规则**：`AuditingLogMapper` 的 5 个方法按记录类型重载，写入器自行映射即可绕开
- **指定库位**：给实体标注 `[ModuleDataSource("XXX")]` 即按 [Data](./data) 的模块分库路由，审计表随实体所在库

## 注意事项与最佳实践

- **`TryAdd` 不生效**。[Auditing](./auditing) 已用 `TryAddScoped` 占了 5 个空实现，覆盖必须用 `Replace`。用错的表现是：注册"成功"、无报错、日志依然不落库。
- **建表时机**。打开 `EnableTableInitialization` 时启动即建表；未打开时，SqlSugar 的分表插入会在目标分表缺失时自行建表——日志不会丢，但表结构问题（权限、字符集）会推迟到首条日志才暴露。生产环境建议开启动期初始化。
- **跨月查询要显式 `SplitTable(begin, end)`**，否则只命中单个分表。
- **逐条插入**。`IXxxLogWriter.WriteAsync` 是单条契约，Worker 攒批后仍逐条调用，高吞吐下是 N 次数据库往返；需要批量写入请替换写入器。
- **`CreatedTime` 按 UTC 写入**，分表切换点随之按 UTC 划分，跨时区部署时切表时刻不等于本地零点。
- **脱敏在采集端完成**。记录到达写入器时已脱敏，写入器不再处理，重复脱敏会二次遮蔽已遮蔽的内容。
- **实体变更日志仍为空实现**。`IEntityDiffLogWriter` 不在本包范围，需要它请自行实现。
- **日志落库失败不重试**。Worker 捕获写入异常仅记 `LogWarning`，这一批日志会丢失，需要可靠性请在写入器内自行兜底。

## 依赖模块

- [XiHan.Framework.Auditing](./auditing)（记录模型、写入器契约）
- [XiHan.Framework.Data](./data)（`ISqlSugarClientResolver`、`DbInitializer`、SqlSugar 传递依赖）

`IDistributedIdGenerator<long>` 经 `XiHanDataModule → XiHanDistributedIdsModule` 间接获得，本包不额外声明依赖。

## 相关模块

- [XiHan.Framework.Web.Api](./web-api)（采集 5 类日志的中间件与过滤器）
- [XiHan.Framework.DistributedIds](./distributed-ids)（雪花主键）
- [XiHan.Framework.Logging](./logging)（运行日志，与审计日志是两回事）
