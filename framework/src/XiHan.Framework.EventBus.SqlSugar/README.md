# XiHan.Framework.EventBus.SqlSugar

## 概述

`XiHan.Framework.EventBus` 的收发件箱 SqlSugar 持久化提供程序。默认的 `DefaultEventOutbox` 是进程内实现，事件与业务数据不在同一事务、进程退出即丢；本包把它们落到数据库。

## 核心能力

- 发件箱实体 `sys_event_outbox` 与 `OutgoingEventInfo` 的双向映射
- 表结构由 `DbInitializer` 在应用启动时创建

## 依赖关系

依赖 `XiHan.Framework.EventBus`（收发件箱契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为事件自身的 `Guid` 标识，非自增。

## 使用方式

在应用启动模块上声明依赖 `XiHanSqlSugarEventBusModule`。

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.EventBus.Abstractions.Distributed` 下的 `IEventOutbox` / `IEventInbox` 并在 DI 中替换。

## 目录结构

```
Entities/   收发件箱实体
Mapping/    契约与实体的双向映射
```
