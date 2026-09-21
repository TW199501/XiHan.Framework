# XiHan.Framework.EventBus.SqlSugar

## 概述

`XiHan.Framework.EventBus` 的收发件箱 SqlSugar 持久化提供程序。默认的 `DefaultEventOutbox` 是进程内实现，事件与业务数据不在同一事务、进程退出即丢；本包把它们落到数据库。

## 核心能力

- 发件箱实体 `sys_event_outbox` 与 `OutgoingEventInfo` 的双向映射
- 入箱与业务数据落在同一事务、同一个库：业务写在哪个库，事件行就写在哪个库
- 发送端遍历当前布局的全部库，单个库不可达时只跳过该库
- 多实例领取互斥：条件抢占 + 领取超时释放，不依赖任何数据库方言特性
- 表结构由 `DbInitializer` 在应用启动时创建

## 依赖关系

依赖 `XiHan.Framework.EventBus`（收发件箱契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为事件自身的 `Guid` 标识，非自增。

配置节 `XiHan:EventBus:SqlSugar`，`ClaimTimeout` 控制领取超时，默认 5 分钟。

投递语义为**至少一次**：宿主在投递成功后才删除记录，若进程在投递与删除之间退出，记录会被重新领取并再次投递，消费端需幂等。

删除按库执行，某个库删除失败时只记录日志并跳过，不会抛给调用方；该库上的记录留在原地，之后每次轮询都会被重新领取、重新投递，直到该库恢复为止。

本包把 `IEventOutbox` 的注册生命周期由单例改为作用域——发件箱必须取得工作单元作用域的数据库连接才能与业务数据同事务。

事件行的落库由当前工作单元已登记的连接决定：恰好一个时写该库，一个都没有时写当前库，多于一个时抛 `InvalidOperationException`。因此业务代码应**先写业务数据、后发布事件**——反过来会让事件落在主库而业务落在模块库，两者不在同一个事务里，且不会报错。

领取配额在当前布局的各库间平均分配：每库最多领取 `maxCount` 除以库数的整数商，且每库至少领取 1 条；库数超过 `maxCount` 时，单次领取的总量等于库数。

发件箱表由 `[TableInitialization(IncludeModuleConnections = true)]` 声明进入所有库，主库与每个模块库都会建出 `sys_event_outbox`。这依赖 `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 均为 `true`（二者默认均为 `false`）才会自动建表。

从旧版本升级的既有部署，本版本会开始把事件行按业务落库位置路由进各模块库，因此升级前须确保 `sys_event_outbox` 在每个模块库中已存在——开启上述两个自动建表选项，或手工在各模块库中建出该表。二者都没做时，第一次向该模块库写入的事件会在建表失败（缺表）时报错，且这次失败发生在业务事务内部。

主库与静态配置的模块库在 `SqlSugarScope` 构建时一并建连，进程重启后不存在「主库先建连、模块库延后建连」的时间差，待发事件不会因此暂时无法被领取。

发送循环运行在无租户上下文的后台作用域，只遍历默认布局，租户独立库的发件箱不在本包范围。

## 使用方式

在应用启动模块上声明依赖 `XiHanSqlSugarEventBusModule`。

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.EventBus.Abstractions.Distributed` 下的 `IEventOutbox` / `IEventInbox` 并在 DI 中替换。

## 目录结构

```
Entities/                        收发件箱实体
Mapping/                         契约与实体的双向映射
Options/                         存储配置
Outbox/                          发件箱实现
Extensions/DependencyInjection/  服务注册扩展
```
