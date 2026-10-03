# XiHan.Framework.Quotas.SqlSugar

> 配额账本的 SqlSugar 持久化实现：用唯一索引当去重闸门、用一条条件更新在数据库内判定扣额，让多实例共享同一份额度。

- **NuGet**：`XiHan.Framework.Quotas.SqlSugar`
- **模块类**：`XiHanQuotasSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Quotas](./quotas)（`IQuotaStore` 契约与政策模型）、[Data](./data)（SqlSugar 客户端、建表）

## 概述

[Quotas](./quotas) 的默认实现 `DefaultQuotaStore` 只保证本进程内的有界原子性。多实例部署时各实例各记各的账，配额形同虚设。

本包把存储换成两张数据库表。扣额判定不下放到应用侧「先读再写」，而是压进一条带条件的 `UPDATE`，由数据库保证并发下的正确性。

## 何时使用

- 多个进程或节点要共享同一份租户配额
- 需要配额账本在重启后仍然成立

## 安装与启用

```bash
dotnet add package XiHan.Framework.Quotas.SqlSugar
```

```csharp
[DependsOn(typeof(XiHanQuotasSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

建表需要开启 [Data](./data) 的建表初始化，**默认是关闭的**：

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

两张表只登记到平台库，不随租户切库。

## 工作原理

### 两步预留，顺序不能反

1. 先按唯一索引 `(Tenant_Id, Quota_Key, Operation_Id)` 插入预留行——这一步是去重闸门，并发对手抢同一个操作标识时只有一个能插进去。
2. 再对桶做条件更新：

```sql
UPDATE sys_quota_bucket
   SET Reserved = Reserved + @amount
 WHERE Basic_Id = @bucketId
   AND Committed + Reserved <= @limit - @amount;
```

影响行数为 0 就是额度不足，此时回滚整个工作单元，刚插入的预留行一并撤销，不会留下一条永远占着去重窗口的失败记录。

### 独立于调用方事务

每个操作另开一个事务型工作单元（`requiresNew`）。预留必须独立于业务事务先落账，否则业务回滚会把已经放行的预留一起退回去，「先占额度」的语义失效。这条由测试锁住。

### 不受环境租户过滤器影响

两张表都带 `Tenant_Id` 列，但**刻意不实现** `IMultiTenantEntity`。数据层的租户过滤只对实现该接口的实体生效；一旦实现，按显式租户标识的跨租户读写会被静默改写，表现为「额度永远为零」。测试里用一张实现该接口的探针表反证过滤器确实在工作。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `SqlSugarQuotaStore` | `IQuotaStore` 的持久化实现 |
| `SysQuotaBucket` | 「租户 + 配额项 + 计量周期」的账本行 |
| `SysQuotaReservation` | 一次预留及其终态历史 |
| `XiHanQuotasSqlSugarOptions` | 配置节 `XiHan:Quotas:SqlSugar` |
| `AddXiHanQuotasSqlSugar` | 用 `Replace` 覆盖默认存储，生命周期 Scoped |

## 配置

```json
{
  "XiHan": {
    "Quotas": {
      "SqlSugar": {
        "ConfigId": ""
      }
    }
  }
}
```

`ConfigId` 留空时使用 [Data](./data) 的默认连接。

## 表结构

| 表 | 用途 | 关键约束 |
| --- | --- | --- |
| `sys_quota_bucket` | 周期账本（`Committed`、`Reserved`） | 唯一索引 `(Tenant_Id, Quota_Key, Period, Period_Start)` |
| `sys_quota_reservation` | 预留与终态历史 | 唯一索引 `(Tenant_Id, Quota_Key, Operation_Id)` |

`Period_Start` 落库为 UTC `DateTime`；不周期政策使用 `DateTime.MinValue` 作为周期起点。

## 扩展点 / 自定义

实现 `IQuotaStore` 换其它介质时，必须保持三条口径：预留与结算不得参与调用方事务；去重窗口不得短于所属周期的保留期；不得把「未找到」回报成零用量。

## 注意事项与最佳实践

- **累计型起点用 `DateTime.MinValue`。** 该列必须能表示这一时刻，SQL Server 需要 `datetime2`。
- **过期回收是被动的。** 过期预留只在该桶被访问时回收（含用量查询），本包不带后台清理任务；长期无人访问的过期行需要由应用的清理策略处理。
- **历史库首次建唯一索引会失败。** 表里已有重复 `(Tenant_Id, Quota_Key, Operation_Id)` 时启动期即报错，需先清重。
- **旧周期行不会自动消失。** 迟到结算要能落回原桶，因此历史行需要自行归档，别指望周期滚动把它们带走。

## 依赖模块

- [Quotas](./quotas)
- [Data](./data)

## 相关模块

- [Traffic.SqlSugar](./traffic-sqlsugar)（同族持久化提供程序的写法参照）
