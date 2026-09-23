# SqlSugar 持久化：剩余模块的拆分方案

- **日期**：2026-09-23
- **状态**：待评审
- **性质**：**分解文档，不是设计文档。** 它决定拆几个包、每个包多少份计划、按什么顺序做；每个包的实际设计仍要各自写 spec。

---

## 1. 现状

框架自身所有状态的 Store，落库情况如下。全仓库 `Default*` / `Null*` 的 Store 与 Writer 共 **32 个**：

| 类别 | 个数 | 说明 |
| --- | --- | --- |
| 已落库 | — | `Auditing.SqlSugar` 的 5 个日志写入器、`EventBus.SqlSugar` 的发件箱 |
| 配置型，**不算缺口** | 10 | Bot 五个渠道的 config store、`DefaultTenantStore`（读配置档）、`DefaultOpenApiSecurityClientStore` |
| 主包空实现，**留着是对的** | 6 | `Auditing` 的 5 个 `Null*Writer` 靠 `Replace` 顶替，属正常设计；第 6 个 `NullEntityDiffLogWriter` 例外，见下 |
| **真正待落库** | **16** | 本文档的对象 |

**16 个里有 2 个已在进行中**：`EventBus` 的收件箱（P7，spec 未写）、`Auditing` 的
`NullEntityDiffLogWriter`（它在 `Auditing/` 根目录而非 `Auditing/Writers/`，P1/P2 扫的是后者所以漏了）。

**剩下 14 个分布在 8 个模块**，连一份 spec 都还没有。

## 2. 契约实测大小

不是估的，是数出来的：

| 模块 | Store 与方法数 | 合计方法 | 实体数（估） |
| --- | --- | --- | --- |
| Workflow | Definition 9 / Instance 13 / Bookmark 10 | **32** | 3 |
| Authorization | Permission 8 / Role 11 / Policy 5 | **24** | 3–5（含关联表） |
| Authentication | User 9 / RefreshToken 3 / ExternalLogin 3 | **15** | 3 |
| Tasks | BackgroundJob 5 / Job 7 | **12** | 2 |
| Upgrade | UpgradeVersion 9 | 9 | 1 |
| Settings | Setting 4 | 4 | 1 |
| Traffic | GrayRule 3 | 3 | 1 |
| Security | PasswordHistory 1 | 1 | 1 |
| Bot.Telegram | ConversationState | — | 1 |

## 3. 拆成 8 个包

沿用仓库既有的兄弟子包模式（`Auditing.SqlSugar`、`EventBus.SqlSugar`），一个模块一个包：

```
XiHan.Framework.Tasks.SqlSugar
XiHan.Framework.Authorization.SqlSugar
XiHan.Framework.Authentication.SqlSugar
XiHan.Framework.Workflow.SqlSugar
XiHan.Framework.Settings.SqlSugar
XiHan.Framework.Security.SqlSugar
XiHan.Framework.Traffic.SqlSugar
XiHan.Framework.Upgrade.SqlSugar
```

**不合并四个小包。** `Security`（1 个方法）、`Traffic`（3）、`Settings`（4）、`Upgrade`（9）看起来
适合并成一个「杂项持久化」包，但合并后下游只要一个设置存储，就得同时吃进 Traffic 与 Upgrade
的依赖，违反分层。

**`Bot.Telegram` 的对话状态**是第 9 个候选，渠道专属、价值低，排最后或不做。

## 4. 计划份数：13–14

按实际耗用推算：`Auditing.SqlSugar` 的 5 实体 + 5 写入器用了 2 份；`EventBus.SqlSugar` 语义最重，
用了 4 份（P3 实体、P4 入箱与领取、P6 多库、P7 收件箱）。

| 包 | 份数 | 依据 |
| --- | --- | --- |
| Settings / Security / Traffic / Upgrade | 各 1 | 纯 CRUD，无并发语义 |
| Tasks | 2 | **背景作业的领取是发件箱那套并发问题的翻版**——租约、超时释放、多实例互斥，可直接复用 P4/P6 已被真实数据库并发测试验证过的三步抢占协议 |
| Authentication | 2 | 15 个方法 3 个实体；refresh token 的撤销与轮换语义需单独设计 |
| Authorization | 2–3 | 24 个方法 + 关联表；难点在权限解析的查询形状，不在写入 |
| Workflow | 3 | 32 个方法 3 个实体；bookmark 比对是查询密集型，最难的一块 |

加上进行中的 P7（1 份）与 `NullEntityDiffLogWriter`（改动足够小，按 bounded 直接实现，不需要计划文档）。

## 5. 建议顺序

1. **`Settings.SqlSugar`** —— 最小、每个应用都要，用它把「简单 CRUD Store」的形状立成范本，后面四个小包照抄
2. **`Tasks.SqlSugar`** —— 价值高，且领取逻辑能直接沿用已验证过的抢占协议
3. **`Authentication.SqlSugar`**
4. **`Authorization.SqlSugar`** —— 3 与 4 是下游最跑不掉的两个
5. **`Security` + `Traffic` + `Upgrade`** —— 三个小包可以一轮做掉
6. **`Workflow.SqlSugar`** —— 最大也最小众，放最后

## 6. 每个包都要回答的共同问题

写各自的 spec 时，下列问题每份都要答，答案不必相同：

- **分表与否**。`Auditing.SqlSugar` 的 5 张日志表按月分表，`sys_event_outbox` 不分表（短命队列）。
  按数据是否短命、是否需要保留期决定。
- **主键类型**。`Auditing` 用雪花 `long`，发件箱用契约自带的 `Guid`。由契约的标识类型决定，不要照搬。
- **是否参与工作单元事务**。发件箱必须与业务同事务（注册为 Scoped、经 `ISqlSugarClientResolver`
  取客户端）；审计日志则不必。
- **是否需要多库**。P6 给发件箱补了多库遍历。其余 Store 多为全局配置，大概率单库即可，
  但 `Tasks` 的作业可能随业务分库。
- **顶替方式**。主包若用 `TryAdd` 注册默认实现，子包必须用 `Replace`，`TryAdd` 是空操作。

## 7. 规模对照

已完成 6 份计划（P1–P6），覆盖 2 个包 6 个契约。剩余 8 个包 14 个契约，约 13–14 份计划——
**工作量是已完成部分的两倍以上**。建议按实际需要挑，不必一次做完；第 5 节的顺序即按「下游跑不掉
的程度」排的。
