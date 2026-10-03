# XiHan.Framework.Tasks.SqlSugar

## 概述

`XiHan.Framework.Tasks` 的 SqlSugar 持久化提供程序。主包的 `DefaultBackgroundJobStore` 是进程内实现，进程重启即丢、不跨实例；本包把后台作业落到数据库。

## 核心能力

- 后台作业实体 `sys_background_job`：入队参与当前工作单元的事务，多实例领取互斥（条件抢占 + 租约超时释放），不依赖分布式锁，也不依赖任何数据库方言特性
- 后台作业逐作业租约：执行中续租、按令牌完成与回写、释放租约，失去租约的 Worker 不会覆盖新持有者的结果
- 后台作业管理：重试已放弃的作业、请求取消作业，供主包的 `IBackgroundJobManagementService` 使用
- 以 `Replace` 顶替主包的 `IBackgroundJobStore`，生命周期保持单例

## 依赖关系

依赖 `XiHan.Framework.Tasks`（存储契约与轮询 Worker）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case。主键为作业自身的 `Guid`。

表结构由 `DbInitializer` 在应用启动时创建，这要求 `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 均为 `true`（二者默认均为 `false`）。都没开启又没有手工建表时，首次写入即抛「表不存在」。自行维护表结构时按本包实体的列定义建表。

配置节 `XiHan:Tasks:SqlSugar`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `BackgroundJobLeaseTimeout` | `00:05:00` | 后台作业租约时长，必须大于零；领取与每次续租都把租约延长到当前时间加该时长，超过该时长未续租、未删除也未回写的作业可被重新领取 |
| `MaxClaimBatchSize` | `50` | 单次领取的作业数量上限，必须大于零；实际领取数量取请求数量与该值的较小者 |

所有记录固定写入默认布局的主库，并在写库期间切换到宿主上下文。业务数据写在模块库或租户独立库时，后台作业的入队与业务不在同一个事务里，且不会报错。

后台作业：

- 执行语义为**至少一次**：进程在作业执行成功与删除之间退出，作业会在租约过期后再次执行，作业处理器需幂等
- 租约：本存储声明 `SupportsJobLease`。Worker 执行每个作业前先按令牌续租确认租约，执行中按主包的续租间隔（`XiHan:BackgroundJobs:JobLeaseRenewalIntervalSeconds`，默认取租约时长的四分之一）续租，结束后按令牌删除或回写；租约时长由 `BackgroundJobLeaseTimeout` 决定，主包的 `JobLeaseDurationSeconds` 只作用于进程内存储
- 续租以 `Claim_Token` 匹配且 `Claim_Time` 未早于「当前时间减租约时长」为条件，把 `Claim_Time` 推进到当前时间。续租未命中（租约已过期或已被另一实例领走）时 Worker 取消本地执行且不回写结果
- 同一轮领到的作业领取时刻相同，尚未执行到的作业不续租。一轮耗时超过租约时长时，这些作业在执行前的确认续租会失败而被跳过，等下一轮或由另一实例领取，不会被本实例重复执行
- Worker 因停机或锁续期失败提前结束一轮时，按令牌释放已领取但未执行的作业，作业立即可被再次领取
- 按令牌完成只看令牌：租约已过期但尚未被另一实例领取时，完成仍然命中
- 按令牌回写不会覆盖已登记的取消请求：取消标记取存储值与回写值的并集，并集为真时作业一律标记放弃
- SQL Server 未开启 RCSI 时，领取会被未提交的入队事务阻塞
- 放弃的作业保留在表里并标记 `Is_Abandoned = 1`，没有自动清理；可按标识查到，也可经管理服务重试
- 管理：本存储声明 `SupportsJobManagement`。重试只作用于已放弃的作业，清除放弃与取消标记、尝试次数归零、下次执行时间设为当前时间并结束租约，重复重试返回 `NoChange`。取消持有有效租约的作业时只登记取消请求（`CancellationRequested`），由持有租约的 Worker 在续租时得知并协作停止；其余未放弃的作业直接标记放弃并结束租约（`Cancelled`）；已放弃或已登记请求的作业返回 `NoChange`，不存在的返回 `NotFound`。取消是协作式的，不强制终止正在执行的代码
- 取消标记列 `Is_Cancellation_Requested` 可空，空值与 `0` 均表示未请求。框架的 `DbInitializer` 不修改已存在的表：新安装无需处理；若 `sys_background_job` 已由本包早期版本建立，需经 `IDbSchemaUpgrader` 或手工补这一列（示例见本节末尾），没有存在性检查的写法要先确认列不存在，以便重复执行。直接调用 SqlSugar `CodeFirst.InitTables` 的宿主会自动补列
- MySQL 连接串不要设 `UseAffectedRows=true`：同一秒内的续租会因列值未变返回 0 行而被判失去租约，保持 MySqlConnector 的默认
- 租约恰在到期那一刻本存储仍可续租（与领取的过期判定一致），进程内存储不可
- 多实例的时钟需要同步：时钟偏差大于续租间隔时，作业可能被另一实例提前重新领取
- 取消在两步条件更新之间作业恰被领取或释放时重新执行，最多三轮
- `UpdateAsync` 只更新已存在的作业，不存在时不插入；`InsertAsync` 遇主键重复时抛数据库异常；应用名为空与空字符串视为同一个应用
- 不要在事务型工作单元里调用领取：条件 `UPDATE` 持有的行锁要到工作单元提交才释放

存量表补 `Is_Cancellation_Requested` 列的示例：

```sql
-- MySQL：先查 information_schema.COLUMNS 确认列不存在再执行
ALTER TABLE sys_background_job ADD COLUMN Is_Cancellation_Requested TINYINT(1) NULL;
-- SQL Server：自带存在性检查
IF COL_LENGTH('sys_background_job', 'Is_Cancellation_Requested') IS NULL ALTER TABLE sys_background_job ADD Is_Cancellation_Requested BIT NULL;
-- PostgreSQL：自带存在性检查（列名按 SqlSugar 默认的自动小写）
ALTER TABLE sys_background_job ADD COLUMN IF NOT EXISTS is_cancellation_requested BOOLEAN NULL;
-- SQLite：先用 PRAGMA table_info(sys_background_job) 确认列不存在再执行
ALTER TABLE sys_background_job ADD COLUMN Is_Cancellation_Requested BIT NULL;
```

## 使用方式

在应用启动模块上声明依赖 `XiHanTasksSqlSugarModule`。之后若再调用主包的 `UseRedisBackgroundJobStore()`，Redis 存储会反过来覆盖本包——后调用者生效。定时任务存储（`IJobStore`）不在本包范围内，由应用自行实现。

## 扩展点

需要自定义存储行为时，实现 `IBackgroundJobStore`（`XiHan.Framework.Tasks.BackgroundJobs.Abstractions`）并在 DI 中 `Replace`。

## 目录结构

```
Entities/                        后台作业实体
Mapping/                         契约与实体的双向映射
Clients/                         宿主上下文的客户端访问器
BackgroundJobs/                  后台作业存储
Options/                         存储配置
Extensions/DependencyInjection/  服务注册扩展
```
