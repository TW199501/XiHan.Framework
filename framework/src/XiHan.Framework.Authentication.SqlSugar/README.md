# XiHan.Framework.Authentication.SqlSugar

## 概述

`XiHan.Framework.Authentication` 的认证存储 SqlSugar 持久化提供程序。

## 核心能力

- 模块装配骨架

## 依赖关系

依赖 `XiHan.Framework.Authentication`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_auth_` 前缀、全小写下划线；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

## 扩展点

需要自定义存储行为时，实现主包的存储接口并在 DI 中以 `Replace` 替换。

## 目录结构

```
XiHanAuthenticationSqlSugarModule.cs   模块装配
```
