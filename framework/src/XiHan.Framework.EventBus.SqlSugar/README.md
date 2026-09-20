# XiHan.Framework.EventBus.SqlSugar

## 概述

`XiHan.Framework.EventBus` 的收发件箱 SqlSugar 持久化提供程序。默认的 `DefaultEventOutbox` 是进程内实现，事件与业务数据不在同一事务、进程退出即丢；本包把它们落到数据库。

## 核心能力

- 发件箱实体 `sys_event_outbox` 与 `OutgoingEventInfo` 的双向映射
- 入箱与业务数据落在同一事务：业务回滚，事件随之消失
- 多实例领取互斥：条件抢占 + 领取超时释放，不依赖任何数据库方言特性
- 表结构由 `DbInitializer` 在应用启动时创建

## 依赖关系

依赖 `XiHan.Framework.EventBus`（收发件箱契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为事件自身的 `Guid` 标识，非自增。

配置节 `XiHan:EventBus:SqlSugar`，`ClaimTimeout` 控制领取超时，默认 5 分钟。

投递语义为**至少一次**：宿主在投递成功后才删除记录，若进程在投递与删除之间退出，记录会被重新领取并再次投递，消费端需幂等。

本包把 `IEventOutbox` 的注册生命周期由单例改为作用域——发件箱必须取得工作单元作用域的数据库连接才能与业务数据同事务。

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
