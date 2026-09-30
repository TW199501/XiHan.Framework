# 用量配额

框架把「这个租户这个周期还能用多少」独立成一等契约：政策与账本分离、扣额走预留—提交—释放三段式。本章讲三段式怎么用最安全、为什么功能开关不在这里、以及单进程默认存储的边界在哪。

完整 API 见 [Quotas](../packages/quotas) 与 [Quotas.SqlSugar](../packages/quotas-sqlsugar)。

## 三件事不要混在一起

| 问题 | 谁来回答 | 在哪个包 |
| --- | --- | --- |
| 这个**使用者**能不能做这个操作 | `IPermissionChecker` / `IAuthorizationService` | [Authorization](../packages/authorization) |
| 这个**租户**能不能用这个功能 | `ITenantFeatureChecker` | [MultiTenancy](../packages/multitenancy) |
| 这个租户这个周期**还能用多少** | `IQuotaService` / `IQuotaStore` | [Quotas](../packages/quotas) |

三者正交，必须各自检查：租户被授权某功能不代表使用者有权限，额度充足也不代表前两项通过。配额包刻意不再定义一套「功能授权」契约——功能开关在多租户包里已经存在，重复建设会让同一个 `Feature:` 字符串域长出两套语义。

## 三段式怎么用

```csharp
// 1. 功能与权限各自检查
if (!await featureChecker.IsEnabledAsync("saas.export"))
{
    // 该租户未被授权（含功能未知）
}

// 2. 占额度
var quota = await quotaService.ReserveAsync("export.rows", 1, operationId, ct);

if (!quota.Allowed)
{
    // 用 quota.Status 区分，不要用Allowed一把盖掉
}

// 3. 业务成功才定稿，失败必须归还
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

### `operationId` 必须由你持久化

预留标识是「租户 + 配额项 + 操作标识」，它是唯一的幂等凭据。重试必须复用同一个 `operationId`；换一个新的就是另扣一笔。

调用方要持久化它并至少一次重投提交，因为存在一个真实的窗口：业务已经做完、`CommitAsync` 没成功。此时返回的是 `Expired` / `NotFound` / `TerminalConflict` 之一，**都表示这一笔用量没有计入**。配额不提供绕过预留的直扣入口，补记或告警是你的责任。

### 不要「先放行再回滚」

超额时实现返回类型化结果而不抛异常，也不存在「先扣成功再退回」的路径。同理，`Release` 之后不能再 `Commit`，终态不可回退。

## 政策：无限额与「没政策」是两件事

```csharp
QuotaPolicy.Limited(1000, QuotaPeriod.Day, "v1")   // 每天 1000
QuotaPolicy.Unlimited("v1")                        // 显式无限额
// FindPolicyAsync 返回 null                       // 没有政策：必须拒绝
```

`IQuotaPolicyProvider` 返回 `null` 表示该租户没有这条配额项的政策，调用方必须拒绝，绝不能当成无限额。默认实现一律返回 `null`，所以不接政策来源时任何预留都会被拒。

无限额**不参与计数**：`QuotaUsage` 对它恒为零。要观测无上限配额的消耗量，请走事件或指标记录，不要指望配额计数器。

## 周期怎么算

| 周期 | 起点（一律 UTC） |
| --- | --- |
| `None` | 不切分，累计不清零 |
| `Day` | 当日 00:00 |
| `Week` | ISO 周星期一 00:00 |
| `Month` | 当月 1 日 00:00 |

周期在预留时就定死并随记录保存。周期滚动后迟到的提交只改动它原来那个周期桶，不会污染新周期——这也是「同一 `operationId` 跨周期重试不会在新周期另扣一笔」的原因。

## 选哪种存储

| 实现 | 适用 | 边界 |
| --- | --- | --- |
| `DefaultQuotaStore` | 单进程、开发测试 | 不跨实例；进程重启连同已提交用量一起清零 |
| [Quotas.SqlSugar](../packages/quotas-sqlsugar) | 多实例、需要重启后仍成立 | 表只建平台库；过期行被动回收，需自行清理 |

多实例部署不要用默认实现，配额会各记各的。

## 配额不是限流

[Traffic](../packages/traffic) 的 `IRateLimitPolicy` 与 [Bot](../packages/bot) 的 `RateLimitPipeline` 是流量整形：按时间窗放行，请求被拒就是没执行。配额是**业务计量**：先占额度，业务成功才算消耗。需要「每秒最多 N 个请求」时用限流，需要「每月最多 N 万行导出」时用配额。

## 常见问题

### 为什么预留要独立于业务事务

预留必须比业务先落账。如果它加入调用方的工作单元，业务回滚会连同预留一起撤销，「先占额度」失效——两个并发请求可能都以为自己占到了额度。[Quotas.SqlSugar](../packages/quotas-sqlsugar) 的每个操作都另开一个 `requiresNew` 的事务型工作单元，这条由测试锁住。

### 去重窗口能当永久幂等用吗

不能。带周期政策的去重记录保留一整个周期，不周期政策用 `NonPeriodicTombstoneRetention` 兜底。超出保留期后同一个 `operationId` 会被当成新预留。

### 平台（0 号租户）怎么算

平台就是 0 号租户，`ICurrentTenant.Id` 为 `null` 与 `0` 同义，配额侧照常记账。但**功能开关侧不一样**：`ITenantFeatureChecker` 在平台态取不到租户级特性值，只能靠传入的 `defaultValue` 决定放行，平台无法持有功能基线。这是既有口径，配额不改它也不替它兜底。

## 下一步

- [Quotas](../packages/quotas)：配置项全表与 API 清单
- [Quotas.SqlSugar](../packages/quotas-sqlsugar)：表结构、条件更新与部署注意事项
- [多租户](./multi-tenancy)：功能开关与租户上下文
