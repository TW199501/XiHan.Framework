# XiHan.Framework.Quotas

> 租户用量配额的契约与进程内默认存储：按「租户 + 配额项 + 计量周期」记账，以预留—提交—释放三段式承载一次业务消耗，超额与冲突都以类型化结果返回。

- **NuGet**：`XiHan.Framework.Quotas`
- **模块类**：`XiHanQuotasModule`
- **所在层**：基础设施层
- **关键依赖**：[MultiTenancy.Abstractions](./multitenancy-abstractions)（`ICurrentTenant`）、[Timing](./timing)（`IClock`）

## 概述

SaaS 场景常见两类限制：一类问「这个租户能不能用这个功能」，一类问「这个租户这个周期还能用多少」。前者是[多租户](./multitenancy)里 `ITenantFeatureChecker` 的职责，后者是本包。

本包只交付配额，不再定义一套并行的功能授权契约——功能开关已在 `XiHan.Framework.MultiTenancy` 存在，重复建设会让同一个「功能」字符串域长出两套互不打通的语义。使用者权限同理由 [Authorization](./authorization) 负责。三者正交，必须由调用方各自检查。

配额的核心是**两段式**：`ReserveAsync` 先占额度，业务成功后 `CommitAsync` 转为已用，业务失败后 `ReleaseAsync` 归还额度。这样业务超时、进程崩溃都不会把额度永久吃掉。

## 何时使用

- 需要按租户限制用量（导出行数、AI token、存储空间字节数等），而不是限制请求速率
- 多实例部署要共享同一份额度时，引用 [Quotas.SqlSugar](./quotas-sqlsugar) 换掉默认存储

不要用本包做限流：[Traffic](./traffic) 的 `IRateLimitPolicy` 与 Bot 的 `RateLimitPipeline` 是流量整形，语义是按时间窗放行，没有「业务成功才算消耗」这层。

## 安装与启用

```bash
dotnet add package XiHan.Framework.Quotas
```

```csharp
[DependsOn(typeof(XiHanQuotasModule))]
public class YourAppModule : XiHanModule
{
}
```

未接入政策来源时，本包注册的都是安全默认实现：`IQuotaPolicyProvider` 一律返回「无政策」，任何预留都会被拒。要放行任何用量都必须自己提供政策来源。

## 工作原理

### 政策与储存分离

`IQuotaPolicyProvider` 回答「本周期允许多少」，`IQuotaStore` 回答「已经用了多少」。政策缺失与无限额是两件事：`FindPolicyAsync` 返回 `null` 表示没有政策（必须拒绝），`QuotaPolicy.Unlimited(version)` 才是显式无限额。

### 原子预留与去重

预留标识是「租户 + 配额项 + 操作标识」，**不含周期**。周期在预留时确定并随记录保存，所以周期滚动后迟到的提交只改动它原来那个周期桶，不会污染新周期。同一标识重试不重复扣额；换预留量或换政策版本判冲突。

### 有界存储

`DefaultQuotaStore` 的条目数与桶数分别受 `MaxTrackedReservations`、`MaxTrackedBuckets` 约束。到达上限时先回收过期预留与超出保留期的去重记录，仍放不下才按容量不足拒绝，**绝不驱逐仍在占用额度的活动预留**。累计型桶承载跨周期不清零的账目，有账就不驱逐。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `IQuotaService` | 按当前租户编排政策与存储，日常入口 |
| `IQuotaPolicyProvider` | 政策来源，由应用实现 |
| `IQuotaStore` | 预留 / 提交 / 释放 / 查用量，可替换为持久化实现 |
| `QuotaPolicy` | `Limited(limit, period, version)` 或 `Unlimited(version)` |
| `QuotaPeriod` | `None` / `Day` / `Week` / `Month`，按 UTC 对齐 |
| `QuotaReserveResult` | 带 `Allowed` 与 `Status`，区分放行、重放、超额、冲突、容量不足、缺政策、无限额 |
| `QuotaSettlementResult` | 提交与释放的结果，区分已定稿、幂等命中、未找到、过期、终态冲突 |
| `QuotaUsage` | 本周期 `Committed` / `Reserved` / `Limit` / `Remaining` |
| `XiHanQuotasOptions` | 配置节 `XiHan:Quotas` |

## 配置

```json
{
  "XiHan": {
    "Quotas": {
      "DefaultReservationTtl": "00:05:00",
      "MaxTrackedReservations": 100000,
      "MaxTrackedBuckets": 20000,
      "NonPeriodicTombstoneRetention": "24:00:00"
    }
  }
}
```

四项都在启动校验期检查（必须为正），不会拖到首次解析存储才失败。

## 使用示例

```csharp
var quota = await quotaService.ReserveAsync("export.rows", 1, operationId, ct);

if (!quota.Allowed)
{
    // 用 quota.Status 区分：超额 / 缺政策 / 冲突 / 容量不足
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

`operationId` 必须由调用方持久化，并至少一次重投提交——见「注意事项」。

## 扩展点 / 自定义

- 政策来源：注册自己的 `IQuotaPolicyProvider`（数据库、配置或计费系统）。
- 存储：注册 `IQuotaStore` 覆盖默认实现，或直接用 [Quotas.SqlSugar](./quotas-sqlsugar)。
- 编排：替换 `IQuotaService` 以自定义租户来源或补偿策略。

替换存储的实现必须守住三条口径：预留与结算不得参与调用方事务；去重窗口不得短于所属周期的保留期；不得把「无政策」或「未找到」回报成零用量或无限额。

## 注意事项与最佳实践

- **默认存储只在单进程内成立。** `DefaultQuotaStore` 不跨实例，进程重启会连同已提交用量一起清零。多实例必须换持久化存储。
- **去重窗口不是永久幂等。** 带周期政策保留一整个周期，不周期政策用 `NonPeriodicTombstoneRetention` 兜底；超出保留期后同一个 `operationId` 会被当成新预留。
- **没有补记入口。** `Expired` / `NotFound` / `TerminalConflict` 都表示这一笔用量未计入，存储不提供绕过预留的直扣，需要调用方自己补记或告警。
- **无限额不参与计数。** 需要观测无上限配额的消耗量，请走事件或指标记录，不要指望 `QuotaUsage`。
- **政策切换有口径变化。** 无限额改有限额后用量从零点起算；反向改回无限额时既有已记账的预留仍能正常结算或到期释放。
- **本包不管功能开关。** 「未知功能默认拒绝」由 `ITenantFeatureChecker.IsEnabledAsync(name, defaultValue: false)` 承担；它在平台态（无租户上下文）取不到租户级特性值，只能靠传入的默认值决定放行。

## 依赖模块

- [MultiTenancy.Abstractions](./multitenancy-abstractions)
- [Timing](./timing)

## 相关模块

- [MultiTenancy](./multitenancy)（功能开关）
- [Authorization](./authorization)（使用者权限）
- [Quotas.SqlSugar](./quotas-sqlsugar)（持久化存储）
- [Traffic](./traffic)（限流，语义不同）
