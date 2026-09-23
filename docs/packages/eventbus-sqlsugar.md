# XiHan.Framework.EventBus.SqlSugar

> 事件发件箱的 SqlSugar 持久化提供程序：入箱与业务数据落在同一事务，多实例领取互斥。替换 [EventBus](./eventbus) 的进程内发件箱后，发件箱模式才真正成立。

- **NuGet**：`XiHan.Framework.EventBus.SqlSugar`
- **模块类**：`XiHanSqlSugarEventBusModule`
- **所在层**：基础设施层
- **关键依赖**：[EventBus](./eventbus)（收发件箱契约）、[Data](./data)（SqlSugar 客户端、工作单元连接登记、建表）

## 概述

[EventBus](./eventbus) 实现了完整的发件箱骨架：事件在工作单元内入箱，后台服务轮询取出、投递、删除。但它的 `IEventOutbox` 默认实现是进程内字典——事件与业务数据不在同一事务，进程退出即丢，跨实例也不共享。

本包把发件箱落到数据表，补上两条保证：

- **入箱与业务同事务**：业务回滚，事件随之消失；业务提交，事件必然在库
- **多实例领取互斥**：N 个实例同时轮询，同一条记录只会被一个实例领走

收件箱（`IEventInbox`）不在本包当前范围内，仍是进程内实现。

## 何时使用

- 用了分布式事件总线，且要求「业务成功才发事件、业务失败绝不发事件」
- 应用以多实例部署，不能容忍同一事件被重复投递
- 已在用 [Data](./data)，希望发件箱与业务数据走同一套连接与事务

不需要本包的场景：单实例、事件可丢、或事件与业务数据本就不要求一致。

## 安装与启用

```bash
dotnet add package XiHan.Framework.EventBus.SqlSugar
```

在启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanSqlSugarEventBusModule))]
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
        "EnableTableInitialization": true
      }
    }
  }
}
```

`sys_event_outbox` 不是分表，没有 SqlSugar 插入时自动建表的兜底。未开启建表初始化又没有手工建表时，首次入箱即抛「表不存在」，并使所在业务事务一同失败。

## 表结构

单张表 `sys_event_outbox`，**不分表**——发件箱是短命队列，投递成功即删除。

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `Basic_Id` | `Guid`，主键，非自增 | 事件唯一标识，直接取 `OutgoingEventInfo.Id` |
| `Row_Version` | `long` | 并发标识 |
| `Event_Name` | `string(256)`，非空 | 事件名 |
| `Event_Data` | `byte[]`，非空 | 序列化后的事件数据 |
| `Created_Time` | `DateTimeOffset`，非空 | 事件创建时间 |
| `Extra_Properties` | 大文本，可空 | 扩展属性的 JSON |
| `Status` | `int`，非空 | 0 待发送，1 已领取 |
| `Claim_Token` | `string(64)`，可空 | 领取令牌 |
| `Claim_Time` | `DateTimeOffset`，可空 | 领取时刻，用于超时释放 |

主键用事件自身的 `Guid` 而非雪花 `long`：契约按 `Guid` 定位记录，`DeleteAsync(Guid)` 因此是主键查找。该 `Guid` 由框架的顺序 Guid 生成器产出，不会造成索引碎片。

## 工作原理

### 入箱

`DistributedEventBusBase` 在工作单元的作用域内解析发件箱实现，因此本包的 `SqlSugarEventOutbox` 注册为 `Scoped`，经 `ISqlSugarClientResolver` 取得**当前工作单元已登记的那条连接**。事件行与业务数据因此落在同一个事务上。

### 领取

宿主的发送循环是「取待发 → 投递 → 删除」，没有分布式锁。去重由本包的领取实现完成，分三步，全部使用方言无关的表达式 API：

1. 查出可领取记录（待发送，或已领取但超时）的主键，按创建时间升序取一批
2. 条件 `UPDATE` 抢占这批主键，`WHERE` 里重复可领取条件
3. 按本次令牌取回真正抢到的记录

第 2 步的每行 `UPDATE` 是原子的，两个并发领取者只有一个能把某行从可领取改成已领取。候选全被抢走时另选一批重试，最多三轮；返回空集合表示确实没有可领取的记录。

不使用 `FOR UPDATE SKIP LOCKED`、`UPDATE ... LIMIT` 等方言特性——本框架是通用类库，代价是多一次往返。

### 超时释放

`Claim_Time` 早于「当前时刻 − 领取超时」的已领取记录重新变为可领取，避免实例异常退出导致记录永久滞留。

## 配置

配置节 `XiHan:EventBus:SqlSugar`（`XiHanSqlSugarEventBoxOptions.SectionName`）。

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `ClaimTimeout` | `TimeSpan` | `00:05:00` | 领取超时，超过该时长仍未删除的已领取记录可被重新领取 |

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `XiHanSqlSugarEventBusModule` | 模块类，声明依赖即启用 |
| `SqlSugarEventOutbox` | `IEventOutbox` 的 SqlSugar 实现，注册为 `Scoped` |
| `SysEventOutbox` | 发件箱实体 |
| `EventOutboxMapper` | 契约与实体的双向映射 |
| `XiHanSqlSugarEventBoxOptions` | 领取超时配置 |

## 注意事项与最佳实践

- **投递语义是至少一次**。宿主在投递成功后才删除记录，若进程在投递与删除之间退出，记录会被重新领取并再次投递。消费端必须幂等。
- **`IEventOutbox` 的生命周期由单例改为作用域**。发件箱必须取得工作单元作用域的连接才能与业务数据同事务。框架内没有构造函数注入该接口的地方，不会产生被捕获依赖。
- **`GetWaitingEventsAsync` 是领取不是查询**。调用后记录已被标记为已领取，不要在别处当作只读查询复用。
- **`filter` 参数未支持**。传入非空值会抛 `NotSupportedException`，而不是静默忽略——静默忽略会让调用方以为筛选生效、实际领走全部记录。
- **跨库写入不是一个事务**。业务实体经 `[ModuleDataSource]` 落在模块库时，本包当前仍把事件写进解析出的那一个库；框架不提供跨库分布式事务。

## 扩展点 / 自定义

需要完全自定义存储行为时，实现 `IEventOutbox` 并在 DI 中 `Replace`，同时把 `XiHanDistributedEventBusOptions.Outboxes` 的 `ImplementationType` 指向自己的类型。

## 依赖模块

- [EventBus](./eventbus)：收发件箱契约与投递循环
- [Data](./data)：SqlSugar 客户端解析、工作单元连接登记、建表初始化

## 相关模块

- [EventBus.RabbitMQ](./eventbus-rabbitmq) / [EventBus.Kafka](./eventbus-kafka) / [EventBus.Redis](./eventbus-redis)：投递用的 Broker 提供程序，与本包正交——本包管「事件怎么存」，它们管「事件怎么发出去」
- [Uow](./uow)：入箱所参与的工作单元
- [Auditing.SqlSugar](./auditing-sqlsugar)：同一套落库范式的审计日志实现
