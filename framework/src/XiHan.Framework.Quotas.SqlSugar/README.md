# XiHan.Framework.Quotas.SqlSugar

## 概述

XiHan.Framework.Quotas.SqlSugar 用 SqlSugar 把配额账本落到数据库，替换
`XiHan.Framework.Quotas` 的进程内默认存储，使多实例部署共享同一份额度口径。
扣额判定下推到数据库的一条条件更新里完成，不依赖「先读再写」的应用侧锁。

## 核心能力

- 原子预留：先以唯一索引 `(Tenant_Id, Quota_Key, Operation_Id)` 占住预留标识，
  再用 `Committed + Reserved <= 上限 - 本次预留量` 的条件更新判定扣额；对手抢先占走标识时返回重放或冲突，
  额度不足时整个操作回滚，不留半成品记录。
- 独立事务：每个操作另开一个事务型工作单元（`requiresNew`），不加入调用方事务，
  因此业务回滚不会带走已放行的预留。
- 平台库单点：配额表只建在平台库且不受环境租户过滤器影响，支持按显式租户标识跨租户读写与查询用量。

## 依赖关系

模块类为 `XiHanQuotasSqlSugarModule`，依赖 `XiHanQuotasModule` 与 `XiHanDataModule`；
依赖关系通过 DependsOn 进行组合，具体依赖以模块类声明为准。

## 配置与约定

配置节 `XiHan:Quotas:SqlSugar`：

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `ConfigId` | 空 | 配额表所在连接配置标识，留空用数据访问层的默认连接 |

两张表由实体声明参与建表初始化，只登记到平台库：

| 表 | 用途 | 关键约束 |
| --- | --- | --- |
| `sys_quota_bucket` | 一个「租户 + 配额项 + 计量周期」的账本 | 唯一索引 `(Tenant_Id, Quota_Key, Period, Period_Start)` |
| `sys_quota_reservation` | 一次预留与其终态历史 | 唯一索引 `(Tenant_Id, Quota_Key, Operation_Id)` |

## 使用方式

```csharp
[DependsOn(typeof(XiHanQuotasSqlSugarModule))]
public class MyModule : XiHanModule
{
}
```

```csharp
builder.Services.AddXiHanQuotasSqlSugar(configuration);
```

两种注册方式等价，模块已代为调用；`IQuotaStore` 被替换为 Scoped 生命周期的本包实现。

## 扩展点

本包本身就是 `IQuotaStore` 的一个持久化实现；需要其它存储介质时按同样方式实现契约并 `Replace` 注册。
实现必须保持三条既有口径：预留与结算不得参与调用方事务；去重窗口不得短于所属周期的保留期；
不得把「无政策」或「未找到」回报成零用量或无限额。

## 已知限制

- 累计型政策的周期起始落库为 `DateTime.MinValue`，该列必须能表示这一时刻（SQL Server 需 datetime2）。
- 过期预留的回收发生在被访问的桶上（含用量查询），本包不带后台清理任务；
  长期无人访问的过期记录会留在表里，需要由应用的清理策略处理。
- 对已有数据的历史库首次建立唯一索引会在启动期失败，需先清重。
- 跨周期迟到的提交只改动旧桶，因此持久化实现的历史行不会随周期滚动被回收，需自行归档。

## 目录结构

```text
XiHan.Framework.Quotas.SqlSugar/
├── README.md
├── Entities/            SysQuotaBucket、SysQuotaReservation
├── Stores/              SqlSugarQuotaStore
├── Options/             XiHan:Quotas:SqlSugar 配置节
├── Extensions/          服务注册扩展
└── XiHanQuotasSqlSugarModule.cs
```
