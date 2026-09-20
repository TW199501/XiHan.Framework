# Data 支持实体进入所有库与查询已登记连接（P5）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 `XiHan.Framework.Data` 补两个能力——实体可声明在模块库也建表、可查询当前工作单元已登记的连接标识。两处都必须非破坏性。

**Architecture:** 建表能力做成 `TableInitializationAttribute` 上的新属性（默认 `false`），接进 `DbEntityTypeProvider.IsModuleDataSourceAllowed` 既有判定链的最后一个分支。连接查询做成 `ISqlSugarClientResolver` 的默认接口方法（默认返回空），由 `SqlSugarClientResolver` 覆写，前缀常量仍不外泄。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-21-data-all-databases-capability-p5-design.md`

> 该 spec 自成一体，实现 P5 所需的全部约束都在其中。**不要**去读 `2026-09-21-sqlsugar-persistence-design.md`——那是拆分前的总纲，已停用。

**所属 PR：独立 PR（第三个）。** 本计划**只改** `XiHan.Framework.Data` 与其测试、文档，不含任何 `EventBus.SqlSugar` 改动。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-sqlsugar`）。worktree 内看不到这些文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录**：`E:/source/XiHan/XiHan.Framework-sqlsugar`（分支 `feat/sqlsugar`）。

**技术栈是 SqlSugar，不是 Entity Framework Core。** 下列 EF Core 惯用法一律禁止：

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

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事写进提交信息
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台限制**：Microsoft.Testing.Platform，不是 VSTest。**没有可用的筛选参数**，`--filter`/`--list-tests` 返回退出码 3；**不要带 `--logger trx` / `--results-directory`**，会以退出码 5 失败。

**提交信息**：中文 Conventional Commits，作用域 `data`。**不加任何 AI 署名。**

---

## 本计划特有的三条硬约束

**① 改的是基础包，两处都必须非破坏性。**

`XiHan.Framework.Data` 被 `framework/src/` 下 60 多个包直接或间接依赖，下游应用更多。

- 新属性 `IncludeModuleConnections` **默认 `false`**：不标注它的实体行为逐字不变。若默认 `true`，所有未声明模块数据源的实体会突然在模块库建表，且没有任何编译期提示
- 新接口成员必须是**默认接口方法**：`ISqlSugarClientResolver` 是公开接口，下游可能有自定义实现（测试桩尤其常见）。新增抽象成员会让它们编译失败

**② 既有测试一个都不能改。**

如果为了让新代码通过而需要修改任何现有测试，说明默认行为变了——停下来，不要改测试。

**③ 前缀常量不许改成 `public`。**

`SqlSugarClientResolver` 的 `TransactionClientItemPrefix` 保持 `private const`。暴露它等于把字符串格式变成契约；正确做法是暴露语义方法 `GetEnlistedConfigIds()`，调用方拿到的是 ConfigId 列表，不是键格式。

---

## File Structure

```
framework/src/XiHan.Framework.Data/SqlSugar/
  Initializers/TableInitializationAttribute.cs   修改：加一个属性
  Initializers/DbEntityTypeProvider.cs           修改：判定链加一个分支
  Clients/ISqlSugarClientResolver.cs             修改：加一个默认接口方法
  Clients/SqlSugarClientResolver.cs              修改：覆写该方法

framework/test/XiHan.Framework.Data.Tests/
  ModuleDataSourceRoutingTests.cs   修改：追加建表选取测试（复用该文件既有夹具）
  EnlistedConfigIdsTests.cs         新增：已登记连接测试（自带桩，照仓库惯例）

docs/packages/data.md                修改：补建表选取规则与示例
```

---

### Task 1: 实体声明进入模块库

**Files:**
- Modify: `framework/src/XiHan.Framework.Data/SqlSugar/Initializers/TableInitializationAttribute.cs`
- Modify: `framework/src/XiHan.Framework.Data/SqlSugar/Initializers/DbEntityTypeProvider.cs`
- Modify: `framework/test/XiHan.Framework.Data.Tests/ModuleDataSourceRoutingTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `TableInitializationAttribute.IncludeModuleConnections`（`bool`，默认 `false`）

**参考来源（动手前先读）：**
- 要改的特性（现有属性 `Enabled` / `Group` / `Target` / `ConnectionConfigIds`）：`framework/src/XiHan.Framework.Data/SqlSugar/Initializers/TableInitializationAttribute.cs`
- 要改的判定：`framework/src/XiHan.Framework.Data/SqlSugar/Initializers/DbEntityTypeProvider.cs:108-137`（`IsModuleDataSourceAllowed`）
- 调用它的上游判定链：同文件 `ShouldInitialize`（第 65 行起）
- 模块库 ConfigId 派生规则：`framework/src/XiHan.Framework.Data/SqlSugar/Routing/ModuleDataSourceConfigIds.cs`
- 测试夹具（`CreateEntityTypeProvider` / `CreateOptions` / 测试实体的写法）：`framework/test/XiHan.Framework.Data.Tests/ModuleDataSourceRoutingTests.cs`

**本任务禁止事项：** 硬约束 ①②。另外**不要**新造一个特性——建表范围的其余四个维度都在 `TableInitializationAttribute` 上，分散到两处会让「这张表建在哪」难以查找。**不要**改动 `SharedConnectionConfigIds` 的现有语义，它是连接层级白名单，新属性是实体层级的另一条通路，两者并存。

- [ ] **Step 1: 写失败的测试**

在 `framework/test/XiHan.Framework.Data.Tests/ModuleDataSourceRoutingTests.cs` 中，**在最后一个 `[Fact]` 方法之后、第一个 `private static` 助手方法之前**插入：

```csharp
    [Fact]
    public void 未标注的实体默认不进模块库()
    {
        var provider = CreateEntityTypeProvider(new TableInitializationOptions());

        var types = provider.GetEntityTypes(ErpContext);

        Assert.DoesNotContain(typeof(PlainRoutingEntity), types);
    }

    [Fact]
    public void 标注进入模块库的实体在模块库建表()
    {
        var provider = CreateEntityTypeProvider(new TableInitializationOptions());

        var types = provider.GetEntityTypes(ErpContext);

        Assert.Contains(typeof(AllDatabasesRoutingEntity), types);
    }

    [Fact]
    public void 标注进入模块库的实体在主库照常建表()
    {
        var provider = CreateEntityTypeProvider(new TableInitializationOptions());

        var types = provider.GetEntityTypes(PlatformContext);

        Assert.Contains(typeof(AllDatabasesRoutingEntity), types);
    }

    [Fact]
    public void 标注进入模块库的实体在租户模块库建表()
    {
        var provider = CreateEntityTypeProvider(new TableInitializationOptions());

        var types = provider.GetEntityTypes(TenantErpContext);

        Assert.Contains(typeof(AllDatabasesRoutingEntity), types);
    }

    [Fact]
    public void 声明了模块数据源的实体不受新属性影响()
    {
        var provider = CreateEntityTypeProvider(new TableInitializationOptions());

        var erpTypes = provider.GetEntityTypes(ErpContext);
        var mesTypes = provider.GetEntityTypes(new DbInitializationContext("Default_Mes", null, isTenantDatabase: false));

        Assert.Contains(typeof(ErpRoutingEntity), erpTypes);
        Assert.DoesNotContain(typeof(ErpRoutingEntity), mesTypes);
    }

    [Fact]
    public void 标注进入模块库仍受建表开关约束()
    {
        var provider = CreateEntityTypeProvider(new TableInitializationOptions());

        var types = provider.GetEntityTypes(ErpContext);

        Assert.DoesNotContain(typeof(DisabledAllDatabasesRoutingEntity), types);
    }
```

并在该文件末尾的测试实体区（与 `PlainRoutingEntity` 等并列）追加两个实体：

```csharp
    /// <summary>
    /// 未声明模块数据源但要求在模块库也建表的实体。
    /// </summary>
    [SugarTable("test_routing_all_databases")]
    [TableInitialization(IncludeModuleConnections = true)]
    private sealed class AllDatabasesRoutingEntity : IEntityBase
    {
        public long RowVersion { get; set; }
    }

    /// <summary>
    /// 要求在模块库建表但整体关闭了建表的实体。
    /// </summary>
    [SugarTable("test_routing_all_databases_disabled")]
    [TableInitialization(false, IncludeModuleConnections = true)]
    private sealed class DisabledAllDatabasesRoutingEntity : IEntityBase
    {
        public long RowVersion { get; set; }
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Data.Tests/XiHan.Framework.Data.Tests.csproj -c Release
```

预期：编译失败，`TableInitializationAttribute` 没有 `IncludeModuleConnections`。

- [ ] **Step 3: 给特性加属性**

在 `TableInitializationAttribute` 的 `ConnectionConfigIds` 属性之后追加：

```csharp
    /// <summary>
    /// 是否在模块库也建表，默认 false
    /// </summary>
    public bool IncludeModuleConnections { get; set; }
```

- [ ] **Step 4: 让判定链认它**

把 `DbEntityTypeProvider.IsModuleDataSourceAllowed` 的最后一句：

```csharp
        return !IsAnyModuleConnection(currentConfigId) ||
               DbInitializationFilters.MatchesAny(selection.SharedConnectionConfigIds, currentConfigId);
```

改为：

```csharp
        if (!IsAnyModuleConnection(currentConfigId) ||
            DbInitializationFilters.MatchesAny(selection.SharedConnectionConfigIds, currentConfigId))
        {
            return true;
        }

        return entityType.GetCustomAttribute<TableInitializationAttribute>(inherit: true)?.IncludeModuleConnections == true;
```

同时把该方法的 `<remarks>` 里「未声明的实体不进模块库，除非该连接被 `SharedConnectionConfigIds` 放行」一句，补成三条通路：不是模块库、连接被 `SharedConnectionConfigIds` 放行、实体标注了 `IncludeModuleConnections`。

文件顶部若尚未引入 `System.Reflection`，补上 `using`（`GetCustomAttribute` 的所在）。

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Data.Tests/XiHan.Framework.Data.Tests.csproj -c Release
```

预期：全部 PASS，**且既有测试一个都没改**（硬约束 ②）。

- [ ] **Step 6: 验证全解决方案 0 警告并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Data framework/test/XiHan.Framework.Data.Tests
git commit -m "feat(data): 实体可声明在模块库也建表"
```

---

### Task 2: 查询当前工作单元已登记的连接

**Files:**
- Modify: `framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`
- Modify: `framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs`
- Create: `framework/test/XiHan.Framework.Data.Tests/EnlistedConfigIdsTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `ISqlSugarClientResolver.GetEnlistedConfigIds()` → `IReadOnlyList<string>`，默认实现返回空集合

**参考来源（动手前先读）：**
- 要改的接口（已有默认接口方法 `GetClientForEntity<TEntity>()` 的先例）：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/ISqlSugarClientResolver.cs`
- 钉连接的实现与前缀常量：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs:32-33`（常量）、`230-300`（`EnlistCurrentUnitOfWork` 的判定条件）
- 工作单元的 `Items`：`framework/src/XiHan.Framework.Uow/IUnitOfWork.cs:34`（公开的 `Dictionary<string, object>`）
- 测试夹具与桩（`FixedTenantConnectionResolver` / `StubModuleDataSourceConnectionResolver` / `PassThroughConnectionConfigurator` / `NoTenant`，以及如何构造 `SqlSugarClientResolver` 与 `IUnitOfWorkManager`）：`framework/test/XiHan.Framework.Data.Tests/RequiresNewIsolationTests.cs`

**本任务禁止事项：** 硬约束 ①③。另外**不要**让调用方感知键格式——返回的是 ConfigId 列表。**不要**承诺返回顺序。

- [ ] **Step 1: 写失败的测试**

创建 `framework/test/XiHan.Framework.Data.Tests/EnlistedConfigIdsTests.cs`。

桩类照仓库惯例由测试文件自带——把 `RequiresNewIsolationTests.cs` 里的 `FixedTenantConnectionResolver`、`StubModuleDataSourceConnectionResolver`、`PassThroughConnectionConfigurator`、`NoTenant` 四个 `private sealed class` **逐字复制**到本文件的测试类内（仓库里 `NoTenant` 已在三个测试文件各存一份，这是既定模式）。

夹具的搭建（`SqlSugarScope`、`ServiceCollection`、`IUnitOfWorkManager`、`SqlSugarClientResolver` 的构造）同样照 `RequiresNewIsolationTests.cs` 的构造函数复制，连接串用 SQLite 临时文件。

测试方法：

```csharp
    [Fact]
    public void 无工作单元时返回空集合()
    {
        Assert.Empty(_resolver.GetEnlistedConfigIds());
    }

    [Fact]
    public void 非事务工作单元返回空集合()
    {
        using var unitOfWork = _unitOfWorkManager.Begin(new XiHanUnitOfWorkOptions { IsTransactional = false });

        _ = _resolver.GetCurrentClient();

        Assert.Empty(_resolver.GetEnlistedConfigIds());
    }

    [Fact]
    public void 事务工作单元内解析后返回该连接标识()
    {
        using var unitOfWork = _unitOfWorkManager.Begin(new XiHanUnitOfWorkOptions { IsTransactional = true });

        _ = _resolver.GetCurrentClient();

        Assert.Contains(OuterConfigId, _resolver.GetEnlistedConfigIds());
    }

    [Fact]
    public void 解析多个连接后全部返回()
    {
        using var unitOfWork = _unitOfWorkManager.Begin(new XiHanUnitOfWorkOptions { IsTransactional = true });

        _ = _resolver.GetClient(OuterConfigId);
        _ = _resolver.GetClient(InnerConfigId);

        var enlisted = _resolver.GetEnlistedConfigIds();

        Assert.Contains(OuterConfigId, enlisted);
        Assert.Contains(InnerConfigId, enlisted);
    }
```

`XiHanUnitOfWorkOptions` 的实际属性名与 `Begin` 的签名以 `framework/src/XiHan.Framework.Uow/IUnitOfWorkManager.cs` 与 `Options/XiHanUnitOfWorkOptions.cs` 为准，照 `RequiresNewIsolationTests.cs` 里既有的调用方式写。

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Data.Tests/XiHan.Framework.Data.Tests.csproj -c Release
```

预期：编译失败，`GetEnlistedConfigIds` 不存在。

- [ ] **Step 3: 给接口加默认方法**

在 `ISqlSugarClientResolver` 的 `GetCurrentLayoutConfigIds()` 之后追加：

```csharp
    /// <summary>
    /// 获取当前工作单元已登记的连接配置标识
    /// </summary>
    /// <remarks>
    /// 无当前工作单元、工作单元非事务型或已结束时返回空集合。
    /// 返回顺序不作承诺，调用方不得依赖顺序表达优先级。
    /// </remarks>
    /// <returns>已登记的连接配置标识</returns>
    IReadOnlyList<string> GetEnlistedConfigIds()
    {
        return [];
    }
```

默认实现返回空集合，现有的其他实现者无需改动即可继续编译（硬约束 ①）。

- [ ] **Step 4: 在实现里覆写**

在 `SqlSugarClientResolver` 内追加：

```csharp
    /// <summary>
    /// 获取当前工作单元已登记的连接配置标识
    /// </summary>
    /// <remarks>
    /// 无当前工作单元、工作单元非事务型或已结束时返回空集合。
    /// 返回顺序不作承诺，调用方不得依赖顺序表达优先级。
    /// </remarks>
    /// <returns>已登记的连接配置标识</returns>
    public IReadOnlyList<string> GetEnlistedConfigIds()
    {
        var unitOfWork = _unitOfWorkManager.Current;
        if (unitOfWork is null ||
            unitOfWork.IsDisposed ||
            unitOfWork.IsCompleted ||
            !unitOfWork.Options.IsTransactional)
        {
            return [];
        }

        var keyPrefix = $"{TransactionClientItemPrefix}:";

        return [.. unitOfWork.Items.Keys
            .Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal))
            .Select(key => key[keyPrefix.Length..])];
    }
```

判定条件与 `EnlistCurrentUnitOfWork` 保持一致（不含 `IsReserved` 与 `IsRolledback`——前者不影响已登记的事实，后者在此只读场景不需要抛异常）。

`TransactionClientItemPrefix` 保持 `private const`，不改可见性（硬约束 ③）。

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Data.Tests/XiHan.Framework.Data.Tests.csproj -c Release
```

预期：全部 PASS，既有测试一个都没改。

- [ ] **Step 6: 验证全解决方案 0 警告并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Data framework/test/XiHan.Framework.Data.Tests
git commit -m "feat(data): 可查询当前工作单元已登记的连接标识"
```

---

### Task 3: 文档与全量验收

**Files:**
- Modify: `docs/packages/data.md`

**Interfaces:**
- Consumes: Task 1、Task 2 的产出
- Produces: 无（终端任务）

**参考来源（动手前先读）：**
- 要改的文档：`docs/packages/data.md`（建表选取规则一节、模块分库一节）
- 既有示例的写法：同文件第 375-395 行、455-475 行

**本任务禁止事项：** 不要顺手重写与本 PR 无关的文档。上游明确要求「一个 PR 只做一件事」。不要在文档里声称仓库有 `.codegraph/` 目录。

- [ ] **Step 1: 补建表选取规则**

在 `docs/packages/data.md` 的建表选取规则一节，把「未声明的实体不进模块库」补成三条通路，并加示例：

```csharp
// 基础设施表：每个库都要有一份（发件箱、审计、分布式锁等）
[SugarTable("sys_event_outbox")]
[TableInitialization(IncludeModuleConnections = true)]
public class SysEventOutbox : SugarEntity<Guid> { }
```

说明三条通路：当前连接不是模块库、该 ConfigId 命中 `SharedConnectionConfigIds`、实体标注了 `IncludeModuleConnections`。并点明 `SharedConnectionConfigIds` 是连接层级的整体放行，新属性是实体层级的单点放行，两者并存。

- [ ] **Step 2: 补模块分库一节**

在模块分库一节追加一段：基础设施表若需要在每个库都存在，用 `[TableInitialization(IncludeModuleConnections = true)]`；该属性只决定建不建表，**不改变运行期的连接解析**——实体仍按 `[ModuleDataSource]` 或租户上下文路由。

- [ ] **Step 3: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 0 Warning(s) 0 Error(s)；全部测试通过。

- [ ] **Step 4: 确认改动范围**

```bash
git diff --name-only upstream/main...HEAD
```

预期：本 PR 的改动**只涉及** `framework/src/XiHan.Framework.Data/`、`framework/test/XiHan.Framework.Data.Tests/`、`docs/packages/data.md`。若出现任何 `EventBus.SqlSugar` 或 `Auditing.SqlSugar` 的文件，说明混进了别的 PR 的改动。

- [ ] **Step 5: 复查注释是否混入论证**

通读本计划改动的 `.cs` 文件的注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。发现即移出到提交信息。

- [ ] **Step 6: 提交**

```bash
git add docs/packages/data.md
git commit -m "docs(data): 补写实体进入所有库的声明方式"
```

---

## 完成标准

P5 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- **既有测试一个都没改**
- 未标注 `IncludeModuleConnections` 的实体行为逐字不变
- `ISqlSugarClientResolver` 的自定义实现无需改动即可编译
- `TransactionClientItemPrefix` 仍是 `private const`
- 改动范围只含 `Data` 及其测试与文档

## 已知边界（写入 PR 描述，不写进代码注释）

- **返回顺序不承诺**：`GetEnlistedConfigIds()` 按 `Items` 枚举顺序返回。
- **仍无跨库事务**：一个实体在多个库都有表，不意味着跨库写入是原子的。
- **属性只影响建表**：`IncludeModuleConnections` 不改变运行期连接解析。
- **默认实现返回空**：自定义 `ISqlSugarClientResolver` 若不覆写，使用该能力的功能会退化为单库行为，不会报错。

## 下一份计划（P6，本计划完成后再写）

发件箱多库：入箱写进业务所在的库、领取与删除遍历当前布局的全部库，使用本份补齐的两个能力。
