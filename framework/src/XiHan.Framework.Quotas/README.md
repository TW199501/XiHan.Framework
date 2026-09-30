# XiHan.Framework.Quotas

## 概述

XiHan.Framework.Quotas 提供租户用量配额的契约与进程内默认存储：按「租户 + 配额项 + 计量周期」记账，
以预留—提交—释放三段式承载一次业务消耗，保证同一周期内的并发预留互相原子可见，超额、冲突与终态不可回退
一律以类型化结果返回。

本模块只交付配额。租户功能开关由 `XiHan.Framework.MultiTenancy` 的 `ITenantFeatureChecker` 提供，
使用者权限由 `XiHan.Framework.Authorization` 提供，三者职责正交，必须由调用方各自检查。

## 核心能力

- 政策与储存分离：`IQuotaPolicyProvider` 决定「本周期允许多少」，`IQuotaStore` 决定「已经用了多少」，
  两者都可被应用替换；无限额是显式政策（`QuotaPolicy.Unlimited`），与「查不到政策」严格区分。
- 原子预留：`ReserveAsync` 先占额度，业务成功 `CommitAsync` 转为已用，业务失败 `ReleaseAsync` 归还额度；
  同一「租户 + 配额项 + 操作标识」重试不重复扣额，换预留量或换政策版本判冲突。
- 有界默认存储：`DefaultQuotaStore` 零外部依赖，条目数与桶数各有上限，过期预留与去重记录按保留期回收，
  达到上限先回收再拒绝，绝不驱逐仍在占用额度的活动预留。

## 依赖关系

模块类为 `XiHanQuotasModule`，依赖 `XiHanMultiTenancyAbstractionsModule` 与 `XiHanTimingModule`；
依赖关系通过 DependsOn 进行组合，具体依赖以模块类声明为准。

本模块不引用 Settings、Web、ORM、Authorization 或多租户实现包，相关边界由测试锁住。

## 配置与约定

配置节 `XiHan:Quotas`：

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `DefaultReservationTtl` | `00:05:00` | 预留默认存活时长，超时未提交即过期并归还额度 |
| `MaxTrackedReservations` | `100000` | 进程内默认存储可跟踪的预留条目上限 |
| `MaxTrackedBuckets` | `20000` | 进程内默认存储可跟踪的配额桶上限 |
| `NonPeriodicTombstoneRetention` | `24.00:00:00` | 不周期政策的去重记录保留时长 |

约定：

- 租户标识为 `long`，平台就是 0 号租户，`ICurrentTenant.Id` 为 null 与 0 同义。
- 计量周期一律按 UTC 对齐：日为当日 00:00，周为 ISO 周星期一 00:00，月为当月 1 日 00:00；
  `QuotaPeriod.None` 表示累计型，跨周期不清零。
- 预留标识不含周期：周期在预留时确定并随记录保存，周期滚动后的迟到结算只改动它原来那个周期桶。
- 去重记录有保留期：带周期的政策保留一整个周期，不周期用 `NonPeriodicTombstoneRetention` 兜底；
  保留期内同标识重试不重复扣额，超出保留期后同标识重用视为新预留，不提供永久幂等保证。
- 非法配置在启动校验期失败（`ValidateOnStart`），不必等到首次解析存储才暴露。

## 使用方式

```csharp
[DependsOn(typeof(XiHanQuotasModule))]
public class MyModule : XiHanModule
{
}
```

功能开关、权限与配额必须各自检查，配额侧不再重复提供前两者：

```csharp
if (!await featureChecker.IsEnabledAsync("saas.export"))
{
    // 该租户未被授权此功能（含功能未知），拒绝并说明原因
}

if (!await authorizationService.AuthorizeAsync(userId, "saas.export.create"))
{
    // 使用者无权限，与租户授权互不蕴含
}

var quota = await quotaService.ReserveAsync("export.rows", 1, operationId, ct);

if (!quota.Allowed)
{
    // quota.Status 区分超额、缺政策、冲突与容量不足
}

try
{
    // 执行业务
    await quotaService.CommitAsync("export.rows", operationId, ct);
}
catch
{
    await quotaService.ReleaseAsync("export.rows", operationId, ct);
    throw;
}
```

调用方必须持久化 `operationId` 并至少一次重投提交：`Expired`、`NotFound`、`TerminalConflict`
都表示这一笔用量未计入，需要调用方补记或告警，存储不提供绕过预留的直扣入口。

## 扩展点

- `IQuotaPolicyProvider`：接入应用的配额来源（数据库、配置或计费系统）；返回 null 表示无政策，调用方必须拒绝。
- `IQuotaStore`：替换为持久化实现（见 `XiHan.Framework.Quotas.SqlSugar`），多实例部署必须共享同一存储实现。
- `IQuotaService`：按当前租户编排政策与存储，需要自定义租户来源或补偿策略时可替换。

## 已知限制

- `DefaultQuotaStore` 只保证本进程内的有界原子性，进程重启即丢失已提交用量，也不提供跨实例一致性。
- 无限额配额不参与计数，`QuotaUsage` 对其恒为零；需要观测无上限配额的消耗量请另走事件或指标记录。
- 政策由无限额改成有限额后用量从零点起算，无限额期间的消耗不计入新上限；反向改回无限额时，
  既有已记账的预留仍能正常结算或到期释放。
- 租户功能开关在平台态（无租户上下文）取不到租户级特性值，只能靠调用方传入的默认值决定放行，
  这是 `ITenantFeatureChecker` 的既有口径，本模块不改实现也不为它兜底。

## 目录结构

```text
XiHan.Framework.Quotas/
├── README.md
├── Abstractions/        IQuotaPolicyProvider、IQuotaStore、IQuotaService
├── Models/              政策、周期、预留、请求与类型化结果
├── Providers/           一律返回无政策的安全默认实现
├── Stores/              有界进程内默认存储
├── Services/            按当前租户编排的门面
├── Options/             XiHan:Quotas 配置节
├── Extensions/          服务注册扩展
└── XiHanQuotasModule.cs
```
