# XiHan.Framework.Tasks.SqlSugar

## 概述

`XiHan.Framework.Tasks` 的 SqlSugar 持久化提供程序。

## 核心能力

- 包骨架与配置节 `XiHan:Tasks:SqlSugar`

## 依赖关系

依赖 `XiHan.Framework.Tasks`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

配置节 `XiHan:Tasks:SqlSugar`。

## 使用方式

在应用启动模块上声明依赖 `XiHanTasksSqlSugarModule`。

## 扩展点

无。

## 目录结构

```
Options/                         存储配置
Extensions/DependencyInjection/  服务注册扩展
```
