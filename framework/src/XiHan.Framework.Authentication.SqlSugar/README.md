# XiHan.Framework.Authentication.SqlSugar

## 概述

`XiHan.Framework.Authentication` 的认证存储 SqlSugar 持久化提供程序。主包的 `DefaultUserStore` 是注册为作用域的内存字典，每个请求拿到的都是空字典；本包把用户落到数据库。

## 核心能力

- 用户实体 `sys_auth_user` 与 `UserInfo` 的双向映射
- `IUserStore` 的 SqlSugar 实现 `SqlSugarUserStore`，以 `Replace` 顶替主包的内存实现，生命周期为作用域
- 用户名的查找与唯一约束不区分大小写
- 按当前租户隔离读写
- 登录失败次数在数据库侧原子累加
- 契约外提供 `AddUserAsync` 用于创建用户
- 表结构由 `DbInitializer` 在应用启动时创建，**必须开启** `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization`（二者默认均为 `false`）

## 依赖关系

依赖 `XiHan.Framework.Authentication`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问、雪花主键）。

## 配置与约定

表名 `sys_auth_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。未开启自动建表时，首次读写即抛「表不存在」；自行维护表结构时按实体的列定义建表。`XiHan:Data:SqlSugarCore:TableInitialization:Mode` 为 `OptIn` 时本包的表不会自动创建。

用户标识 `UserInfo.UserId` 是 `Basic_Id` 的十进制字符串。非正整数的用户标识视为不存在。

用户名另存一列 `Normalized_User_Name`（`ToUpperInvariant()`），查找与唯一索引 `(Tenant_Id, Normalized_User_Name)` 都用它，因此 `Alice` 与 `alice` 是同一个用户。这与主包 `DefaultUserStore` 的大小写敏感不同；从大小写敏感的旧数据迁入时，仅大小写不同的重名会让唯一索引建不起来。邮箱与手机号不唯一、不参与查找。

所有读写都带 `Tenant_Id = 当前租户（无租户时为 0）` 的条件，不依赖全局租户过滤器：租户上下文看不到平台用户，平台上下文也看不到租户用户。

存储层不对密码哈希、恢复码、双因素密钥做任何计算，原样存取。**双因素密钥 `Two_Factor_Secret` 以明文落库**，数据库泄漏即泄漏全部 TOTP 密钥。

`UpdateUserAsync` **不写密码哈希、登录失败次数与锁定状态**：改密码只能经 `UpdatePasswordAsync`，失败计数与锁定只能经对应的专用方法（解锁用户须调 `SetLockoutEndAsync(username, null)` 与 `ResetFailedLoginAttemptsAsync`）。直接改 `user.PasswordHash`、`user.IsLocked` 等再调 `UpdateUserAsync` 的代码，改动会被忽略。

同一个存储实例（即同一个请求作用域）内，对同一用户的多次读取返回同一个 `UserInfo` 实例；`UpdatePasswordAsync`、失败计数与锁定方法会同步修改该实例。因此在同一作用域内，已读出的用户对象不会反映其他请求在此期间对库的修改（失败计数与锁定三个字段除外）。

时间列一律以 UTC 存储：写入的 `Local` 时间先换算，`Unspecified` 按 UTC 处理；读出的时间标记为 `Utc`。

`RecoveryCodes` 与 `AdditionalData` 以 JSON 文本存储；`AdditionalData` 的值读回后是 `JsonElement`。

取消令牌只在访问数据库前检查，不传给 SqlSugar。

下游若自己也 `Replace` 了 `IUserStore`，以模块装配顺序靠后者为准。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

创建用户（密码须先经 `IPasswordHasher` 哈希）：

```csharp
var userId = await userStore.AddUserAsync(new UserInfo
{
    Username = "alice",
    PasswordHash = passwordHasher.HashPassword(password),
    IsActive = true
});
```

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.Authentication.Users.IUserStore` 并以 `services.Replace(ServiceDescriptor.Scoped<IUserStore, YourUserStore>())` 替换；实现须注册为作用域。

## 目录结构

```
Entities/                        用户实体
Mapping/                         契约与实体的双向映射、UTC 换算
Users/                           用户存储实现
Extensions/DependencyInjection/  服务注册扩展
```
