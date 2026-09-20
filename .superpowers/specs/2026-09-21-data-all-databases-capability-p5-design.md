# P5：Data 支持实体进入所有库与查询已登记连接 设计

- **日期**：2026-09-21
- **状态**：已评审通过，待实现
- **对应计划**：`.superpowers/plans/2026-09-21-data-all-databases-capability-p5.md`
- **所属 PR**：**独立 PR（第三个）**，只改 `XiHan.Framework.Data`，不碰 `EventBus.SqlSugar`
- **系列**：SqlSugar 持久化层 P5 / 规划 7 份，**现存 P1–P5**（P6–P7 待写）。P1–P2 `Auditing.SqlSugar`，P5 `Data`，P3–P4、P6–P7 `EventBus.SqlSugar`

> 本文档**自成一体**。实现 P5 所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChanges` / 变更追踪那一套。
>
> **本份修改的是框架的基础包 `XiHan.Framework.Data`，被其余 60 多个包依赖。** 两处改动都必须是**非破坏性**的：新增的特性属性默认关闭、新增的接口成员是默认实现。第 5 节说明为什么。

---

## 1. 背景与目标

本系列在实现 `XiHan.Framework.EventBus.SqlSugar` 的发件箱时撞到两个 `Data` 的能力缺口：

**缺口一：实体无法声明「进入所有库」。**

`DbEntityTypeProvider.IsModuleDataSourceAllowed` 的最后一句是：

```csharp
return !IsAnyModuleConnection(currentConfigId) ||
       DbInitializationFilters.MatchesAny(selection.SharedConnectionConfigIds, currentConfigId);
```

未标注 `[ModuleDataSource]` 的实体碰到模块库一律不建表，除非该 ConfigId 被 `TableInitializationOptions.SharedConnectionConfigIds` 放行。但那是**连接层级的白名单，不是按实体的**——放行它等于把所有未声明实体都拉进模块库。

框架因此没有「这张表要在每一个库都存在」的表达方式。发件箱、审计、分布式锁这类基础设施表都需要它。

**缺口二：拿不到当前工作单元已登记了哪些连接。**

`SqlSugarClientResolver` 把连接以 `SqlSugarTransactionClient:{configId}` 为键钉进 `IUnitOfWork.Items`，但前缀是 `private const`，外部包无从得知。要么硬编字符串（依赖别包的私有实现细节，改名即静默失效），要么由 `Data` 自己暴露。

**P5 补齐这两个能力，不实现任何使用它们的功能。** 使用方在 P6。

**成功标准**：

1. 实体可声明在模块库也建表，且默认行为完全不变
2. 可查询当前工作单元已登记的连接标识，无工作单元时返回空
3. 两处改动对现有实现者与调用方都是非破坏性的
4. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 要修改的文件本身与其邻居 —— 最高优先**

```
framework/src/XiHan.Framework.Data/SqlSugar/Initializers/
  TableInitializationAttribute.cs      要加属性的特性（现有属性：Enabled / Group / Target / ConnectionConfigIds）
  DbEntityTypeProvider.cs:65-137       ShouldInitialize 与 IsModuleDataSourceAllowed
  DbInitializationFilters.cs           现有的匹配助手
  DbInitializationTarget.cs            现有的目标库枚举
framework/src/XiHan.Framework.Data/SqlSugar/Clients/
  ISqlSugarClientResolver.cs           要加成员的接口（已有默认接口方法 GetClientForEntity<TEntity>）
  SqlSugarClientResolver.cs:230-300    钉连接进工作单元的实现，前缀常量在第 32-33 行
framework/src/XiHan.Framework.Data/SqlSugar/Routing/
  ModuleDataSourceAttribute.cs         模块分库语义与跨库边界
  ModuleDataSourceConfigIds.cs         ConfigId 派生规则
framework/src/XiHan.Framework.Uow/IUnitOfWork.cs   Items 是公开的 Dictionary<string, object>
framework/test/XiHan.Framework.Data.Tests/
  ModuleDataSourceRoutingTests.cs      现有路由测试，新测试照此形状
  DbInitializationSelectionTests.cs    现有建表选取测试
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.Net/SqlSugar/
  Abstract/CodeFirstProvider/     建表
  Interface/IAdo.cs               事务
```

当前引用版本：`SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`：

| 任务 | 必读 |
| --- | --- |
| 多库与分库 | `SAAS分庫.md`、`跨庫查詢.md`、`多租戶基礎.md` |
| 建表 | `庫表管理DbMaintenance.md`、`實體管理EntityMaintenance.md` |
| 工作单元 | `UnitOfWork工作單元.md` |

仓库自身文档：`docs/packages/data.md`（模块分库、建表选取规则——**本份要更新它**）、`docs/guide/data.md`、`docs/guide/uow.md`。

### 2.3 禁止事项：EF Core 惯用法

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | SqlSugar 无 change tracking，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()` |
| `Migrations` / `Add-Migration` / `EnsureCreated()` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |

### 2.4 P5 特有的禁止事项

- **不改变任何现有默认行为**。新属性默认 `false`，新接口成员有默认实现。见 §5。
- **不把前缀常量改为 public**。暴露实现细节等于把它变成契约；正确做法是暴露语义方法。
- **不在本份里实现发件箱的多库写入或遍历**。那是 P6，混进来会让这个 PR 同时做两件事。
- **不动 `SharedConnectionConfigIds` 的现有语义**。它是连接层级白名单，继续保留，新属性是实体层级的另一条通路。
- **不改 `IUnitOfWork`**。`Items` 已经是公开的，够用。
- **不新增 `PackageReference`**。

## 3. 非目标

- **不实现发件箱多库写入与遍历**（P6）。
- **不做收件箱**（P7）。
- **不给 `OutboxConfig.DatabaseName` 接上消费方**。该字段目前被设置但无人读取，属框架既有缺口，与本份无关，单独处理。
- **不做跨库分布式事务**。框架明确不提供，本份不改变这一点。
- **不重构 `DbEntityTypeProvider`**。只在既有判定链上加一个分支。

## 4. 设计

### 4.1 能力一：实体声明进入模块库

在 `TableInitializationAttribute` 上新增一个属性：

```csharp
/// <summary>
/// 是否在模块库也建表，默认 false
/// </summary>
public bool IncludeModuleConnections { get; set; }
```

`DbEntityTypeProvider.IsModuleDataSourceAllowed` 的最终分支增加第三个析取项——实体标注了该属性即放行：

```
未声明模块数据源的实体，在模块库上建表当且仅当：
  当前连接不是模块库
  或 该 ConfigId 命中 SharedConnectionConfigIds
  或 实体的 [TableInitialization(IncludeModuleConnections = true)]
```

三条互不干扰：`SharedConnectionConfigIds` 是连接层级的整体放行，新属性是实体层级的单点放行。

**为什么放在 `TableInitializationAttribute` 而不是新造一个特性**：建表范围的其余四个维度（是否参与、分组、平台库/租户库、指定连接）都在这个特性上，再造一个会让「这张表建在哪」分散到两处。

**默认 `false`** 意味着现有所有实体的行为逐字不变。

### 4.2 能力二：查询已登记的连接

在 `ISqlSugarClientResolver` 上新增**默认接口方法**：

```csharp
/// <summary>
/// 获取当前工作单元已登记的连接配置标识
/// </summary>
/// <returns>已登记的连接配置标识；无事务型工作单元时返回空集合</returns>
IReadOnlyList<string> GetEnlistedConfigIds() => [];
```

默认实现返回空集合，因此**现有的其他实现者无需改动即可继续编译**——该接口已有 `GetClientForEntity<TEntity>()` 这一默认方法的先例。

`SqlSugarClientResolver` 覆写它：扫描当前工作单元 `Items` 中以 `SqlSugarTransactionClient:` 为前缀的键，截取其后的 ConfigId 返回。前缀常量仍是 `private const`，只有它自己知道——**调用方拿到的是语义，不是字符串格式**。

无当前工作单元、工作单元非事务型、已释放或已完成时返回空集合，与 `EnlistCurrentUnitOfWork` 的判定保持一致。

返回顺序按 `Items` 的枚举顺序，**不承诺稳定**；调用方不得依赖顺序表达优先级。这一点写入 XML 文档注释。

### 4.3 文档

更新 `docs/packages/data.md`：

- 在建表选取规则一节，把「未声明的实体不进模块库」补成三条通路，并给出 `[TableInitialization(IncludeModuleConnections = true)]` 的示例
- 在模块分库一节说明：基础设施表（发件箱、审计、分布式锁）需要在每个库都存在时用这个属性

## 5. 为什么两处都必须非破坏性

`XiHan.Framework.Data` 是基础包，`framework/src/` 下有 60 多个包直接或间接依赖它，下游应用更多。

**新属性默认 `false`**：任何现有实体不标注它，`IsModuleDataSourceAllowed` 的结果与改动前逐字相同。若默认 `true`，所有未声明模块数据源的实体会突然开始在模块库建表——表结构变更、迁移风险、且没有任何编译期提示。

**新接口成员是默认实现**：`ISqlSugarClientResolver` 是公开接口，下游可能有自定义实现（测试桩尤其常见）。新增抽象成员会让它们**编译失败**；默认实现则让它们照常工作，只是拿不到已登记连接——对不使用该能力的实现者无影响。

> 这两条是本份唯一的风险点。改动本身很小，但它在基础包上，一旦默认行为变了，影响面是整个框架。

## 6. 测试策略

全部可在 CI 裸跑，不需要外部服务。

**建表选取** —— 在 `framework/test/XiHan.Framework.Data.Tests/` 内：

- 未标注的实体在模块库**不**建表（回归，锁住现有行为）
- 标注 `IncludeModuleConnections = true` 的实体在模块库建表
- 标注 `IncludeModuleConnections = true` 的实体在非模块库照常建表
- 声明了 `[ModuleDataSource]` 的实体不受新属性影响
- `SharedConnectionConfigIds` 放行的连接行为不变

**已登记连接** ——

- 无当前工作单元时返回空集合
- 非事务型工作单元返回空集合
- 事务型工作单元内解析过客户端后，返回该 ConfigId
- 解析过多个 ConfigId 后，全部返回

测试项目 Import `props/test.props`，xunit.v3 + Microsoft.Testing.Platform。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 返回顺序不承诺 | `GetEnlistedConfigIds()` 按 `Items` 枚举顺序返回，调用方不得依赖顺序 |
| 仍无跨库事务 | 一个实体在多个库都有表，不意味着跨库写入是原子的。框架仍不提供跨库分布式事务 |
| 属性只影响建表 | `IncludeModuleConnections` 只决定建不建表，不改变运行期的连接解析——实体仍按 `[ModuleDataSource]` 或租户上下文路由 |
| 默认实现返回空 | 自定义 `ISqlSugarClientResolver` 实现若不覆写，使用该能力的功能会退化为单库行为，不会报错 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**。新增任何警告都可能被上游退回
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿，**既有测试一个都不能改**——改了就说明默认行为变了
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- `docs/packages/data.md` 已更新
- 提交信息为中文 Conventional Commits，作用域 `data`，**不加任何 AI 署名**
- **本 PR 只改 `XiHan.Framework.Data` 与其测试、文档**，不含任何 `EventBus.SqlSugar` 改动

## 9. 下一份

P6（`.superpowers/specs/2026-09-21-eventbus-sqlsugar-p6-multi-database-design.md`，待写）：发件箱多库——入箱写进业务所在的库、领取与删除遍历当前布局的全部库，使用本份补齐的两个能力。
