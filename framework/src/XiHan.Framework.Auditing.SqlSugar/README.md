# XiHan.Framework.Auditing.SqlSugar

## 概述
`XiHan.Framework.Auditing` 的 SqlSugar 持久化提供程序，提供审计日志的实体定义与落库实现。

## 核心能力
- 5 类审计日志（访问 / 接口 / 异常 / 登录 / 操作）的 SqlSugar 实体
- 按月自动分表，表名形如 `sys_operation_log_20260901`
- 表结构由 `DbInitializer` 在应用启动时创建

## 依赖关系
依赖 `XiHan.Framework.Auditing`（日志记录模型与写入器契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定
表名 `sys_` 前缀、全小写下划线；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。分表字段为 `Created_Time`。

## 使用方式
在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuditingSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

## 扩展点
需要自定义落库行为时，实现 `XiHan.Framework.Auditing.Writers` 下的对应接口并在 DI 中替换。

## 目录结构
```text
XiHan.Framework.Auditing.SqlSugar/
  Entities/
    SysAccessLog.cs
    SysApiLog.cs
    SysExceptionLog.cs
    SysLoginLog.cs
    SysOperationLog.cs
  README.md
  XiHanAuditingSqlSugarModule.cs
```
