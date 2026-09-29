# XiHan.Framework.Tasks.SqlSugar

## 概述

`XiHan.Framework.Tasks` 的 SqlSugar 持久化提供程序。主包的 `DefaultBackgroundJobStore` 是进程内实现，进程重启即丢、不跨实例；本包把后台作业落到数据库。

## 核心能力

- 后台作业实体 `sys_background_job` 与 `BackgroundJobInfo` 的双向映射
- 入队参与当前工作单元的事务：业务回滚，作业随之消失
- 多实例领取互斥：条件抢占 + 租约超时释放，不依赖分布式锁，也不依赖任何数据库方言特性
- 以 `Replace` 顶替主包的 `IBackgroundJobStore`，生命周期保持单例

## 依赖关系

依赖 `XiHan.Framework.Tasks`（存储契约与轮询 Worker）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键为作业自身的 `Guid` 标识，非自增。

表结构由 `DbInitializer` 在应用启动时创建，这要求 `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 均为 `true`（二者默认均为 `false`）。都没开启又没有手工建表时，首次入队即抛「表不存在」，并使所在业务事务一同失败。自行维护表结构时按本包实体的列定义建表。

配置节 `XiHan:Tasks:SqlSugar`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `BackgroundJobLeaseTimeout` | `00:05:00` | 后台作业租约时长，领取后超过该时长仍未删除或更新的作业可被重新领取 |

作业行固定写入默认布局的主库，并在写库期间切换到宿主上下文。业务数据写在模块库或租户独立库时，入队与业务不在同一个事务里，且不会报错。

执行语义为**至少一次**：进程在作业执行成功与删除之间退出，作业会在租约过期后再次执行，作业处理器需幂等。

Worker 串行执行一轮领到的全部作业。未配置 Redis 时分布式锁只在进程内互斥，若一轮耗时超过租约时长，本轮尚未执行到的作业可能被另一实例领走并重复执行——此时调大 `BackgroundJobLeaseTimeout`，或调小 `XiHan:BackgroundJobs:MaxJobFetchCount`。

Worker 因停机或锁续期失败提前结束一轮时，已领取但未执行的作业要等租约过期才会被再次领取。

放弃的作业保留在表里并标记 `Is_Abandoned = 1`，没有自动清理，需应用自行定期删除旧行。

`UpdateAsync` 只更新已存在的作业，不存在时不插入；`InsertAsync` 遇主键重复时抛数据库异常。应用名为空与空字符串视为同一个应用。

不要在事务型工作单元里调用领取：条件 `UPDATE` 持有的行锁要到工作单元提交才释放。

## 使用方式

在应用启动模块上声明依赖 `XiHanTasksSqlSugarModule`。之后若再调用主包的 `UseRedisBackgroundJobStore()`，Redis 存储会反过来顶替本包——后调用者生效。

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.Tasks.BackgroundJobs.Abstractions.IBackgroundJobStore` 并在 DI 中 `Replace`。

## 目录结构

```
Entities/                        作业实体
Mapping/                         契约与实体的双向映射
Clients/                         宿主上下文的客户端访问器
BackgroundJobs/                  后台作业存储
Options/                         存储配置
Extensions/DependencyInjection/  服务注册扩展
```
