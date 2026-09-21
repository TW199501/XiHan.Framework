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

本包把 `IEventOutbox` 的注册生命周期由单例改为作用域——发件箱必须取得工作单元作用域的数据库连接才能与业务数据同事务。

事件行的落库由当前工作单元已登记的连接决定：恰好一个时写该库，一个都没有时写当前库，多于一个时抛 `InvalidOperationException`。因此业务代码应**先写业务数据、后发布事件**——反过来会让事件落在主库而业务落在模块库，两者不在同一个事务里，且不会报错。

一次领取的总量不超过 `maxCount`，配额在当前布局的各库间平均分配。

发件箱表由 `[TableInitialization(IncludeModuleConnections = true)]` 声明进入所有库，主库与每个模块库都会建出 `sys_event_outbox`。

进程重启后、首次流量之前，模块库尚未建连，该库中的待发事件暂时不会被领取；首次访问该模块库后即恢复。

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
