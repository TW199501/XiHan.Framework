# Authorization.SqlSugar ③：策略存储与文档收尾 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付策略实体、`IPolicyStore` 的 SqlSugar 实现（含自定义要求的策略拒绝写入），并补齐新包收尾的文档：包 README、文档站页面与侧边栏、包索引、四份 README 的模块清单。

**Architecture:** `SysAuthzPolicy` 全局不分租户，三个集合字段存为 `CodeFirst_BigString` 的 JSON 文本，经 ① 的 `JsonColumn`（System.Text.Json）读写，读回时空列兜底为空集合。存储在碰数据库之前先校验参数与 `CustomRequirements`。文档照 `Auditing.SqlSugar` / `EventBus.SqlSugar` 的现有条目写。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、System.Text.Json、xunit.v3 + Microsoft.Testing.Platform、VitePress

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-authorization-sqlsugar-3-policy-store-docs-design.md`

> 该 spec 自成一体，实现 ③ 所需的全部约束都在其中。

**Linear 议题:** <https://linear.app/elf-express/issue/EDDIE-10>

**前置:** ①（`.superpowers/plans/2026-09-28-authorization-sqlsugar-1-permission-store.md`）与 ②（`.superpowers/plans/2026-09-28-authorization-sqlsugar-2-role-store-checker.md`）必须已完成。本计划依赖它们的五个实体、`JsonColumn`、两个存储、检查器、注册扩展、测试夹具与 `EntityConventionTests` / `RegistrationTests`。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/authorization-sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-authorization`）。若 worktree 内看不到这些文件，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：`E:/source/XiHan/XiHan.Framework-authorization`，分支 `feat/authorization-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。若 worktree 尚不存在：

```bash
git worktree add ../XiHan.Framework-authorization -b feat/authorization-sqlsugar dev
```

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

**SqlSugar 签名只信源码**：权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）。更新提供者的目录名是 `Abstract/UpdateProvider/`，**不是** `UpdateableProvider`。文档：`E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar
Task<bool> ISugarQueryable<T>.AnyAsync(Expression<Func<T, bool>> expression, CancellationToken token)
Task<T> ISugarQueryable<T>.FirstAsync(Expression<Func<T, bool>> expression, CancellationToken token)  // 无行时返回 null
Task<List<T>> ISugarQueryable<T>.ToListAsync(CancellationToken token)
Task<int> ISugarQueryable<T>.CountAsync()
IUpdateable<T> IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T> IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IUpdateable<T>.ExecuteCommandAsync(CancellationToken token)
IDeleteable<T> IDeleteable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IDeleteable<T>.ExecuteCommandAsync(CancellationToken token)
Task<int> IInsertable<T>.ExecuteCommandAsync(CancellationToken token)
SugarIndexAttribute(string indexName, string fieldName, OrderByType sortType, bool isUnique = false)

// 框架
ISqlSugarClient ISqlSugarClientResolver.GetCurrentClient()
long IDistributedIdGenerator<long>.NextId()

// 本包 ① 的产出（internal）
string? JsonColumn.SerializeOrNull<T>(T? value) where T : class
T? JsonColumn.DeserializeOrNull<T>(string? json) where T : class

// 主包
interface IAuthorizationRequirement { string Name { get; } Task<bool> EvaluateAsync(AuthorizationContext context); }
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事、设计理由、反事实推理写进提交信息
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以）
- `public` 成员必须有 `<summary>`；**不写 `<inheritdoc/>`**

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。命令：`dotnet test --project <csproj> -c Release`，全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`。

**SQLite 临时库**：连接串必须带 `Pooling=False`（① 的夹具已带）。

**构建环境坑**：`MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留测试进程，`taskkill //F //IM "<name>.exe"` 后重建。

**已知的无关抖动**：全量测试偶发 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 失败（GC 时序），与本包无关，不要去追。

**提交信息**：中文 Conventional Commits，作用域 `authorization-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` 都默认 `false`。不开启不建表——**这一条必须写进 README 与文档站页面**（Task 5、Task 6）。

**文档站**：本机预览命令 `cd docs && pnpm install && pnpm dev`。不要求执行；若执行，只看新页面能否从侧边栏点到。

---

## 本计划特有的硬约束

**① 含 `CustomRequirements` 的策略一律拒绝写入。**

创建与更新都抛 `NotSupportedException`，且在碰数据库之前抛。静默丢弃它等于绕过授权。

**② 三个集合读回时空列兜底为空集合，不是 `null`。**

`DefaultPolicyEvaluator` 直接访问 `policy.RequiredRoles.Count`。

**③ 不用 SqlSugar 的 `IsJson`，统一 `JsonColumn`。**

**④ 顶替用 `Replace`。**

**⑤ 文档只写本包。**

不改 `docs/packages/authorization.md`、`docs/guide/` 或其他包的页面。

---

## File Structure

```
framework/src/XiHan.Framework.Authorization.SqlSugar/
  Entities/SysAuthzPolicy.cs                                           新建
  Mapping/PolicyMapper.cs                                              新建
  Policies/SqlSugarPolicyStore.cs                                      新建
  Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs   修改：追加一行 Replace
  README.md                                                            新建

framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/
  AuthorizationTestContext.cs                                          修改：追加工厂方法
  EntityConventionTests.cs                                             修改：追加策略实体的用例
  PolicyMapperTests.cs                                                 新建
  PolicyStoreTests.cs                                                  新建
  RegistrationTests.cs                                                 修改：追加一条 InlineData

docs/packages/authorization-sqlsugar.md                                新建
docs/packages/index.md                                                 修改：加一行
docs/.vitepress/config.ts                                              修改：侧边栏加一项
framework/README.md                                                    修改：模块清单加一行
framework/README_cn.md                                                 修改：模块清单加一行
README.md                                                              修改：只改模块数（不加表格行）
README_cn.md                                                           修改：只改模块数（不加表格行）
（以上四份 README 与 docs/index.md、docs/introduction.md、docs/why.md、docs/packages/index.md 另把模块数 / 单测工程数加一，见 Task 7 Step 5）
```

---

### Task 1: 策略实体

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzPolicy.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/EntityConventionTests.cs`

**Interfaces:**
- Consumes: ① 的 `SysAuthzPermission`（全局实体的写法）
- Produces: `SysAuthzPolicy`：`PolicyName`、`DisplayName`、`Description`、`RequiredRoles`、`RequiredPermissions`、`RequiredClaims`、`IsEnabled`、`Properties`；`public SysAuthzPolicy()` 与 `public SysAuthzPolicy(long basicId)`

**参考来源（动手前先读）：** ① 的 `Entities/SysAuthzPermission.cs`；`framework/src/XiHan.Framework.Authorization/Policies/PolicyDefinition.cs`

**本任务禁止事项：** 不要让它继承 `SugarMultiTenantEntity`。不要给 JSON 列加 `IsJson = true`（硬约束 ③）。不要为 `CustomRequirements` 建列。

- [ ] **Step 1: 写失败的测试**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/EntityConventionTests.cs`。

把 `定义实体不按租户隔离` 的特性改为：

```csharp
    [Theory]
    [InlineData(typeof(SysAuthzPermission))]
    [InlineData(typeof(SysAuthzPolicy))]
```

在 `权限名称唯一` 方法之后插入：

```csharp
    /// <summary>
    /// 策略名称唯一
    /// </summary>
    [Fact]
    public async Task 策略名称唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewPolicy(context, "AdminOnly")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewPolicy(context, "AdminOnly")).ExecuteCommandAsync());
    }
```

在文件末尾最后一个私有方法之后、类的右花括号之前插入：

```csharp
    private static SysAuthzPolicy NewPolicy(AuthorizationTestContext context, string name)
    {
        return new SysAuthzPolicy(context.IdGenerator.NextId())
        {
            PolicyName = name,
            DisplayName = name,
            IsEnabled = true
        };
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SysAuthzPolicy`。

- [ ] **Step 3: 建实体**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzPolicy.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Authorization.SqlSugar.Entities;

/// <summary>
/// 授权策略实体
/// </summary>
[SugarTable("sys_authz_policy")]
[TableInitialization(Group = "Authorization")]
[SugarIndex("ux_authz_policy_name", nameof(SysAuthzPolicy.PolicyName), OrderByType.Asc, true)]
public class SysAuthzPolicy : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthzPolicy() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthzPolicy(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 策略名称
    /// </summary>
    [SugarColumn(ColumnName = "Policy_Name", Length = 256, IsNullable = false, ColumnDescription = "策略名称")]
    public string PolicyName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称
    /// </summary>
    [SugarColumn(ColumnName = "Display_Name", Length = 256, IsNullable = false, ColumnDescription = "显示名称")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 描述
    /// </summary>
    [SugarColumn(ColumnName = "Description", Length = 1024, IsNullable = true, ColumnDescription = "描述")]
    public string? Description { get; set; }

    /// <summary>
    /// 要求的角色名称的 JSON 数组
    /// </summary>
    [SugarColumn(ColumnName = "Required_Roles", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "要求的角色名称的 JSON 数组")]
    public string RequiredRoles { get; set; } = "[]";

    /// <summary>
    /// 要求的权限名称的 JSON 数组
    /// </summary>
    [SugarColumn(ColumnName = "Required_Permissions", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "要求的权限名称的 JSON 数组")]
    public string RequiredPermissions { get; set; } = "[]";

    /// <summary>
    /// 要求的声明的 JSON 对象
    /// </summary>
    [SugarColumn(ColumnName = "Required_Claims", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = false, ColumnDescription = "要求的声明的 JSON 对象")]
    public string RequiredClaims { get; set; } = "{}";

    /// <summary>
    /// 是否启用
    /// </summary>
    [SugarColumn(ColumnName = "Is_Enabled", IsNullable = false, ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 额外属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "额外属性的 JSON")]
    public string? Properties { get; set; }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 新增授权策略实体"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: 策略映射器

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/PolicyMapper.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PolicyMapperTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysAuthzPolicy`；① 的 `JsonColumn`；主包 `PolicyDefinition`、`IAuthorizationRequirement`、`AuthorizationContext`
- Produces: `public static class PolicyMapper`：`SysAuthzPolicy ToEntity(PolicyDefinition definition, long basicId)`、`PolicyDefinition ToDefinition(SysAuthzPolicy entity)`

**参考来源（动手前先读）：** ① 的 `Mapping/PermissionMapper.cs`；spec §4.3

**本任务禁止事项：** 映射器**不**校验、**不**读 `CustomRequirements`（校验在 Task 3 的存储里）。硬约束 ②③。

- [ ] **Step 1: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PolicyMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Policies;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Mapping;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 策略映射器测试
/// </summary>
public class PolicyMapperTests
{
    /// <summary>
    /// 策略定义往返后逐字段相等
    /// </summary>
    [Fact]
    public void 策略定义往返后逐字段相等()
    {
        var definition = new PolicyDefinition("AdminOnly", "仅管理员", "只允许管理员访问")
        {
            RequiredRoles = ["admin", "owner"],
            RequiredPermissions = ["User.Create", "User.Delete"],
            RequiredClaims = new Dictionary<string, string> { ["dept"] = "it", ["level"] = "" },
            IsEnabled = false
        };

        var entity = PolicyMapper.ToEntity(definition, 42L);

        Assert.Equal(42L, entity.BasicId);
        Assert.Equal("AdminOnly", entity.PolicyName);

        var restored = PolicyMapper.ToDefinition(entity);

        Assert.Equal("AdminOnly", restored.Name);
        Assert.Equal("仅管理员", restored.DisplayName);
        Assert.Equal("只允许管理员访问", restored.Description);
        Assert.Equal(new[] { "admin", "owner" }, restored.RequiredRoles);
        Assert.Equal(new[] { "User.Create", "User.Delete" }, restored.RequiredPermissions);
        Assert.Equal(2, restored.RequiredClaims.Count);
        Assert.Equal("it", restored.RequiredClaims["dept"]);
        Assert.Equal("", restored.RequiredClaims["level"]);
        Assert.False(restored.IsEnabled);
        Assert.Null(restored.Properties);
    }

    /// <summary>
    /// 空集合存为空数组与空对象
    /// </summary>
    [Fact]
    public void 空集合存为空数组与空对象()
    {
        var entity = PolicyMapper.ToEntity(new PolicyDefinition("P", "策略"), 1L);

        Assert.Equal("[]", entity.RequiredRoles);
        Assert.Equal("[]", entity.RequiredPermissions);
        Assert.Equal("{}", entity.RequiredClaims);
    }

    /// <summary>
    /// 列为空文本时读回空集合而不是空引用
    /// </summary>
    [Fact]
    public void 列为空文本时读回空集合()
    {
        var entity = new SysAuthzPolicy(1L)
        {
            PolicyName = "P",
            DisplayName = "策略",
            RequiredRoles = "",
            RequiredPermissions = " ",
            RequiredClaims = ""
        };

        var definition = PolicyMapper.ToDefinition(entity);

        Assert.NotNull(definition.RequiredRoles);
        Assert.Empty(definition.RequiredRoles);
        Assert.NotNull(definition.RequiredPermissions);
        Assert.Empty(definition.RequiredPermissions);
        Assert.NotNull(definition.RequiredClaims);
        Assert.Empty(definition.RequiredClaims);
    }

    /// <summary>
    /// 映射器不读取自定义要求
    /// </summary>
    [Fact]
    public void 映射器不读取自定义要求()
    {
        var definition = new PolicyDefinition("P", "策略")
        {
            CustomRequirements = [new AlwaysPassRequirement()]
        };

        var restored = PolicyMapper.ToDefinition(PolicyMapper.ToEntity(definition, 1L));

        Assert.Empty(restored.CustomRequirements);
    }

    /// <summary>
    /// 恒通过的自定义要求
    /// </summary>
    private sealed class AlwaysPassRequirement : IAuthorizationRequirement
    {
        /// <summary>
        /// 要求名称
        /// </summary>
        public string Name => "always-pass";

        /// <summary>
        /// 评估授权要求
        /// </summary>
        /// <param name="context">授权上下文</param>
        /// <returns>恒为 true</returns>
        public Task<bool> EvaluateAsync(AuthorizationContext context)
        {
            return Task.FromResult(true);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0103` / `CS0246` 找不到 `PolicyMapper`。

- [ ] **Step 3: 实现映射器**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/PolicyMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Policies;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Mapping;

/// <summary>
/// 策略定义与实体的映射
/// </summary>
/// <remarks>
/// 不映射 <see cref="PolicyDefinition.CustomRequirements"/>。
/// </remarks>
public static class PolicyMapper
{
    /// <summary>
    /// 策略定义映射为实体
    /// </summary>
    /// <param name="definition">策略定义</param>
    /// <param name="basicId">主键</param>
    /// <returns>策略实体</returns>
    public static SysAuthzPolicy ToEntity(PolicyDefinition definition, long basicId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new SysAuthzPolicy(basicId)
        {
            PolicyName = definition.Name,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            RequiredRoles = JsonColumn.SerializeOrNull(definition.RequiredRoles) ?? "[]",
            RequiredPermissions = JsonColumn.SerializeOrNull(definition.RequiredPermissions) ?? "[]",
            RequiredClaims = JsonColumn.SerializeOrNull(definition.RequiredClaims) ?? "{}",
            IsEnabled = definition.IsEnabled,
            Properties = JsonColumn.SerializeOrNull(definition.Properties)
        };
    }

    /// <summary>
    /// 实体映射为策略定义
    /// </summary>
    /// <remarks>
    /// 集合列为空时返回空集合。
    /// </remarks>
    /// <param name="entity">策略实体</param>
    /// <returns>策略定义</returns>
    public static PolicyDefinition ToDefinition(SysAuthzPolicy entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new PolicyDefinition(entity.PolicyName, entity.DisplayName, entity.Description)
        {
            RequiredRoles = JsonColumn.DeserializeOrNull<List<string>>(entity.RequiredRoles) ?? [],
            RequiredPermissions = JsonColumn.DeserializeOrNull<List<string>>(entity.RequiredPermissions) ?? [],
            RequiredClaims = JsonColumn.DeserializeOrNull<Dictionary<string, string>>(entity.RequiredClaims) ?? [],
            IsEnabled = entity.IsEnabled,
            Properties = JsonColumn.DeserializeOrNull<Dictionary<string, object>>(entity.Properties)
        };
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 新增策略映射器"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 策略存储

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Policies/SqlSugarPolicyStore.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PolicyStoreTests.cs`

**Interfaces:**
- Consumes: Task 1、Task 2 的产出
- Produces:
  - `public class SqlSugarPolicyStore : IPolicyStore`，构造函数 `(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator)`，契约的 5 个方法
  - 夹具新增 `SqlSugarPolicyStore CreatePolicyStore()`

**参考来源（动手前先读）：**
- 语义基准：`framework/src/XiHan.Framework.Authorization/Policies/DefaultPolicyStore.cs`（spec §1.2）
- 调用方：`framework/src/XiHan.Framework.Authorization/Policies/DefaultPolicyEvaluator.cs:252-346`
- ① 的 `Permissions/SqlSugarPermissionStore.cs`（`Client` 属性、`SetColumns` 写法）

**本任务禁止事项：** 硬约束 ①④。读取不过滤 `IsEnabled`。`null` 策略抛的是 `ArgumentException`（与默认实现一致），**不是** `ArgumentNullException`。

- [ ] **Step 1: 夹具加工厂方法**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`。

using 区在 `using XiHan.Framework.Authorization.SqlSugar.Permissions;` 之后追加：

```csharp
using XiHan.Framework.Authorization.SqlSugar.Policies;
```

在 `CreatePermissionChecker` 方法之后插入：

```csharp
    /// <summary>
    /// 创建策略存储
    /// </summary>
    /// <returns>策略存储</returns>
    public SqlSugarPolicyStore CreatePolicyStore()
    {
        return new SqlSugarPolicyStore(Resolver, IdGenerator);
    }
```

- [ ] **Step 2: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PolicyStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Policies;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 策略存储测试
/// </summary>
public class PolicyStoreTests
{
    /// <summary>
    /// 创建策略后按名称读回
    /// </summary>
    [Fact]
    public async Task 创建策略后按名称读回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await store.CreatePolicyAsync(new PolicyDefinition("AdminOnly", "仅管理员", "只允许管理员访问")
        {
            RequiredRoles = ["admin"],
            RequiredPermissions = ["User.Create"],
            RequiredClaims = new Dictionary<string, string> { ["dept"] = "it" },
            IsEnabled = false
        });

        var policy = await store.GetPolicyByNameAsync("AdminOnly");

        Assert.NotNull(policy);
        Assert.Equal("仅管理员", policy.DisplayName);
        Assert.Equal("只允许管理员访问", policy.Description);
        Assert.Equal(new[] { "admin" }, policy.RequiredRoles);
        Assert.Equal(new[] { "User.Create" }, policy.RequiredPermissions);
        Assert.Equal("it", policy.RequiredClaims["dept"]);
        Assert.False(policy.IsEnabled);
        Assert.Empty(policy.CustomRequirements);
    }

    /// <summary>
    /// 同名策略重复创建抛异常
    /// </summary>
    [Fact]
    public async Task 同名策略重复创建抛异常()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await store.CreatePolicyAsync(new PolicyDefinition("AdminOnly", "仅管理员"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreatePolicyAsync(new PolicyDefinition("AdminOnly", "另一个")));
    }

    /// <summary>
    /// 空策略或空名称抛参数异常
    /// </summary>
    [Fact]
    public async Task 空策略或空名称抛参数异常()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreatePolicyAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreatePolicyAsync(new PolicyDefinition()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdatePolicyAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdatePolicyAsync(new PolicyDefinition()));
    }

    /// <summary>
    /// 含自定义要求的策略拒绝创建且不写库
    /// </summary>
    [Fact]
    public async Task 含自定义要求的策略拒绝创建且不写库()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await Assert.ThrowsAsync<NotSupportedException>(() => store.CreatePolicyAsync(new PolicyDefinition("Custom", "自定义")
        {
            CustomRequirements = [new AlwaysPassRequirement()]
        }));

        Assert.Null(await store.GetPolicyByNameAsync("Custom"));
        Assert.Equal(0, await context.Client.Queryable<SysAuthzPolicy>().CountAsync());
    }

    /// <summary>
    /// 含自定义要求的策略拒绝更新且原策略不变
    /// </summary>
    [Fact]
    public async Task 含自定义要求的策略拒绝更新且原策略不变()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await store.CreatePolicyAsync(new PolicyDefinition("AdminOnly", "仅管理员") { RequiredRoles = ["admin"] });

        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdatePolicyAsync(new PolicyDefinition("AdminOnly", "被改")
        {
            RequiredRoles = ["other"],
            CustomRequirements = [new AlwaysPassRequirement()]
        }));

        var policy = await store.GetPolicyByNameAsync("AdminOnly");

        Assert.NotNull(policy);
        Assert.Equal("仅管理员", policy.DisplayName);
        Assert.Equal(new[] { "admin" }, policy.RequiredRoles);
    }

    /// <summary>
    /// 更新策略改写名称以外的全部字段
    /// </summary>
    [Fact]
    public async Task 更新策略改写名称以外的全部字段()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await store.CreatePolicyAsync(new PolicyDefinition("AdminOnly", "仅管理员")
        {
            RequiredRoles = ["admin"],
            RequiredPermissions = ["User.Create"],
            RequiredClaims = new Dictionary<string, string> { ["dept"] = "it" }
        });

        await store.UpdatePolicyAsync(new PolicyDefinition("AdminOnly", "管理员或所有者", "新描述")
        {
            RequiredRoles = ["admin", "owner"],
            RequiredPermissions = [],
            RequiredClaims = new Dictionary<string, string> { ["region"] = "cn" },
            IsEnabled = false
        });

        var policy = await store.GetPolicyByNameAsync("AdminOnly");

        Assert.NotNull(policy);
        Assert.Equal("管理员或所有者", policy.DisplayName);
        Assert.Equal("新描述", policy.Description);
        Assert.Equal(new[] { "admin", "owner" }, policy.RequiredRoles);
        Assert.Empty(policy.RequiredPermissions);
        Assert.Equal("cn", Assert.Single(policy.RequiredClaims).Value);
        Assert.False(policy.IsEnabled);
    }

    /// <summary>
    /// 更新不存在的策略抛异常
    /// </summary>
    [Fact]
    public async Task 更新不存在的策略抛异常()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdatePolicyAsync(new PolicyDefinition("Nope", "无")));
    }

    /// <summary>
    /// 删除策略后读不到，删除空名称或不存在的策略不抛异常
    /// </summary>
    [Fact]
    public async Task 删除策略后读不到()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await store.CreatePolicyAsync(new PolicyDefinition("AdminOnly", "仅管理员"));
        await store.DeletePolicyAsync("AdminOnly");
        await store.DeletePolicyAsync("AdminOnly");
        await store.DeletePolicyAsync("");

        Assert.Null(await store.GetPolicyByNameAsync("AdminOnly"));
    }

    /// <summary>
    /// 读取全部策略按名称排列且不过滤禁用
    /// </summary>
    [Fact]
    public async Task 读取全部策略按名称排列且不过滤禁用()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        await store.CreatePolicyAsync(new PolicyDefinition("B", "乙"));
        await store.CreatePolicyAsync(new PolicyDefinition("A", "甲") { IsEnabled = false });
        await store.CreatePolicyAsync(new PolicyDefinition("C", "丙"));

        var names = (await store.GetAllPoliciesAsync()).Select(policy => policy.Name).ToList();

        Assert.Equal(new[] { "A", "B", "C" }, names);
    }

    /// <summary>
    /// 空名称读取返回空
    /// </summary>
    [Fact]
    public async Task 空名称读取返回空()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePolicyStore();

        Assert.Null(await store.GetPolicyByNameAsync(""));
    }

    /// <summary>
    /// 恒通过的自定义要求
    /// </summary>
    private sealed class AlwaysPassRequirement : IAuthorizationRequirement
    {
        /// <summary>
        /// 要求名称
        /// </summary>
        public string Name => "always-pass";

        /// <summary>
        /// 评估授权要求
        /// </summary>
        /// <param name="context">授权上下文</param>
        /// <returns>恒为 true</returns>
        public Task<bool> EvaluateAsync(AuthorizationContext context)
        {
            return Task.FromResult(true);
        }
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SqlSugarPolicyStore`（夹具的 `CreatePolicyStore` 引用了它）。

- [ ] **Step 4: 实现策略存储**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Policies/SqlSugarPolicyStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Authorization.Policies;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Mapping;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Authorization.SqlSugar.Policies;

/// <summary>
/// 策略存储的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 不支持持久化 <see cref="PolicyDefinition.CustomRequirements"/>，含自定义要求的策略写入时抛出 <see cref="NotSupportedException"/>。
/// </remarks>
public class SqlSugarPolicyStore : IPolicyStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarPolicyStore(
        ISqlSugarClientResolver clientResolver,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 当前租户主库的客户端
    /// </summary>
    private ISqlSugarClient Client => _clientResolver.GetCurrentClient();

    /// <summary>
    /// 获取所有策略
    /// </summary>
    /// <remarks>
    /// 按名称序数升序返回，不过滤启用状态。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>策略列表</returns>
    public async Task<List<PolicyDefinition>> GetAllPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var entities = await Client.Queryable<SysAuthzPolicy>().ToListAsync(cancellationToken);

        return
        [
            .. entities
                .OrderBy(entity => entity.PolicyName, StringComparer.Ordinal)
                .Select(PolicyMapper.ToDefinition)
        ];
    }

    /// <summary>
    /// 根据名称获取策略
    /// </summary>
    /// <param name="policyName">策略名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>策略定义，不存在时返回空</returns>
    public async Task<PolicyDefinition?> GetPolicyByNameAsync(string policyName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(policyName))
        {
            return null;
        }

        var entity = await Client.Queryable<SysAuthzPolicy>()
            .FirstAsync(item => item.PolicyName == policyName, cancellationToken);

        return entity is null ? null : PolicyMapper.ToDefinition(entity);
    }

    /// <summary>
    /// 创建策略
    /// </summary>
    /// <param name="policy">策略定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ArgumentException">策略或策略名称为空</exception>
    /// <exception cref="NotSupportedException">策略含自定义要求</exception>
    /// <exception cref="InvalidOperationException">同名策略已存在</exception>
    public async Task CreatePolicyAsync(PolicyDefinition policy, CancellationToken cancellationToken = default)
    {
        EnsurePersistable(policy);

        var client = Client;

        if (await client.Queryable<SysAuthzPolicy>().AnyAsync(item => item.PolicyName == policy.Name, cancellationToken))
        {
            throw new InvalidOperationException($"策略 '{policy.Name}' 已存在");
        }

        await client.Insertable(PolicyMapper.ToEntity(policy, _idGenerator.NextId())).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 更新策略
    /// </summary>
    /// <remarks>
    /// 按名称匹配，改写名称以外的全部字段。
    /// </remarks>
    /// <param name="policy">策略定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ArgumentException">策略或策略名称为空</exception>
    /// <exception cref="NotSupportedException">策略含自定义要求</exception>
    /// <exception cref="InvalidOperationException">策略不存在</exception>
    public async Task UpdatePolicyAsync(PolicyDefinition policy, CancellationToken cancellationToken = default)
    {
        EnsurePersistable(policy);

        var client = Client;
        var existing = await client.Queryable<SysAuthzPolicy>()
            .FirstAsync(item => item.PolicyName == policy.Name, cancellationToken)
            ?? throw new InvalidOperationException($"策略 '{policy.Name}' 不存在");

        var updated = PolicyMapper.ToEntity(policy, existing.BasicId);

        await client.Updateable<SysAuthzPolicy>()
            .SetColumns(item => new SysAuthzPolicy
            {
                DisplayName = updated.DisplayName,
                Description = updated.Description,
                RequiredRoles = updated.RequiredRoles,
                RequiredPermissions = updated.RequiredPermissions,
                RequiredClaims = updated.RequiredClaims,
                IsEnabled = updated.IsEnabled,
                Properties = updated.Properties
            })
            .Where(item => item.BasicId == existing.BasicId)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 删除策略
    /// </summary>
    /// <param name="policyName">策略名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task DeletePolicyAsync(string policyName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(policyName))
        {
            return;
        }

        await Client.Deleteable<SysAuthzPolicy>()
            .Where(item => item.PolicyName == policyName)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 校验策略可以写入
    /// </summary>
    /// <param name="policy">策略定义</param>
    /// <exception cref="ArgumentException">策略或策略名称为空</exception>
    /// <exception cref="NotSupportedException">策略含自定义要求</exception>
    private static void EnsurePersistable(PolicyDefinition policy)
    {
        if (policy is null || string.IsNullOrEmpty(policy.Name))
        {
            throw new ArgumentException("策略或策略名称不能为空", nameof(policy));
        }

        if (policy.CustomRequirements is { Count: > 0 })
        {
            throw new NotSupportedException(
                $"策略 '{policy.Name}' 含自定义要求，SqlSugar 策略存储无法持久化自定义要求。" +
                "含自定义要求的策略请由应用自行实现的 IPolicyStore 提供。");
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `含自定义要求的策略拒绝创建且不写库` 失败于「没有抛异常」——实现漏了硬约束 ①，**改实现，不改断言**。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 实现策略存储，含自定义要求的策略拒绝写入"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 注册

**Files:**
- Modify: `framework/src/XiHan.Framework.Authorization.SqlSugar/Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarPolicyStore`
- Produces: `AddXiHanAuthorizationSqlSugar` 另以 `Replace` 注册 `IPolicyStore`

**参考来源（动手前先读）：** 主包 `XiHanAuthorizationServiceCollectionExtensions.cs:36`（确认 `TryAddScoped`）。

**本任务禁止事项：** 硬约束 ④。不要改 ① ② 已有的注册行。模块类不动。

- [ ] **Step 1: 写失败的测试**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RegistrationTests.cs`。

using 区追加：

```csharp
using XiHan.Framework.Authorization.Policies;
using XiHan.Framework.Authorization.SqlSugar.Policies;
```

把 `契约被顶替为SqlSugar实现` 的特性改为：

```csharp
    [Theory]
    [InlineData(typeof(IPermissionStore), typeof(SqlSugarPermissionStore))]
    [InlineData(typeof(IRoleStore), typeof(SqlSugarRoleStore))]
    [InlineData(typeof(IPermissionChecker), typeof(SqlSugarPermissionChecker))]
    [InlineData(typeof(IPolicyStore), typeof(SqlSugarPolicyStore))]
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：新数据失败，`Assert.Equal` 报期望 `SqlSugarPolicyStore`、实际 `DefaultPolicyStore`。

- [ ] **Step 3: 追加注册**

修改 `framework/src/XiHan.Framework.Authorization.SqlSugar/Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs`。

using 区追加：

```csharp
using XiHan.Framework.Authorization.Policies;
using XiHan.Framework.Authorization.SqlSugar.Policies;
```

在 `services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, SqlSugarPermissionChecker>());` 之后插入：

```csharp
        services.Replace(ServiceDescriptor.Scoped<IPolicyStore, SqlSugarPolicyStore>());
```

完成后该方法体应为：

```csharp
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IPermissionStore, SqlSugarPermissionStore>());
        services.TryAddScoped<SqlSugarPermissionStore>();
        services.Replace(ServiceDescriptor.Scoped<IRoleStore, SqlSugarRoleStore>());
        services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, SqlSugarPermissionChecker>());
        services.Replace(ServiceDescriptor.Scoped<IPolicyStore, SqlSugarPolicyStore>());

        return services;
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 以 Replace 顶替策略存储"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 5: 包 README

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/README.md`

**Interfaces:**
- Consumes: ①②③ 的全部代码产出
- Produces: 无

**参考来源（动手前先读）：**
- 七段结构：`framework/src/XiHan.Framework.Auditing.SqlSugar/README.md`
- 已知边界写法：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md` 的「配置与约定」
- 三份 spec 的第 7 节

**本任务禁止事项：** 七段标题与顺序固定：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构。不要写权衡论证的来龙去脉，写「是什么、怎么用、有什么限制」。

- [ ] **Step 1: 写 README**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/README.md`，内容如下（最外层的四个反引号只是本计划的包裹，文件里不要写它们）：

````markdown
# XiHan.Framework.Authorization.SqlSugar

## 概述

`XiHan.Framework.Authorization` 的授权存储 SqlSugar 持久化提供程序。主包的 `IPermissionStore`、`IRoleStore`、`IPolicyStore` 默认实现是作用域生命周期的内存字典，每个请求拿到的都是空存储；本包把权限、角色、策略及其关联落到数据库，并以直接查表的实现替换 `IPermissionChecker`。

## 核心能力

- 六张表：`sys_authz_permission`、`sys_authz_user_permission`、`sys_authz_role_permission`、`sys_authz_role`、`sys_authz_user_role`、`sys_authz_policy`
- `SqlSugarPermissionStore`、`SqlSugarRoleStore`、`SqlSugarPolicyStore` 以 `Replace` 顶替主包的三个内存存储
- `SqlSugarPermissionChecker` 顶替 `IPermissionChecker`：一次权限判定至多执行 2 条 SQL（直接授予命中时 1 条），与用户的角色数、一次判定的权限数无关
- 删除角色时在同一事务内级联删除其用户关联与角色权限；已处于事务型工作单元中时并入该事务
- 角色与各类授予按租户严格隔离，权限定义与策略全局共享
- 表结构由 `DbInitializer` 在应用启动时创建，**必须开启** `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization`（二者默认均为 `false`）

## 依赖关系

依赖 `XiHan.Framework.Authorization`（存储与检查器契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问、租户过滤、雪花主键）。

## 配置与约定

本包没有自己的配置节。

表名 `sys_authz_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。契约里的角色标识、权限名、策略名、用户标识存为普通列，按租户组成唯一索引。表名刻意不用 `sys_role`、`sys_permission`，以免与应用自有的同名业务表冲突。

未开启上述两个建表开关时不会建表，首次调用任何一个存储或检查器即报「表不存在」。实体标注了 `[TableInitialization(Group = "Authorization")]`：`TableInitialization.Mode` 为 `OptIn` 时同样会建这六张表；`All` 模式下可用 `ExcludedGroups: ["Authorization"]` 跳过它们。

角色、用户角色关联、用户权限、角色权限四张表实现 `IStrictMultiTenantEntity`：租户态只看本租户的行，平台态只看 `TenantId = 0` 的行。多租户应用须保持 `XiHan:Data:SqlSugarCore:EnableTenantFilter` 为 `true`（默认值）。`TenantId` 由数据层在插入时按当前租户上下文填写，调用方无需传入。权限定义与策略不分租户，租户态调用它们的写方法会改到所有租户共用的数据，应只在平台态开放。

所有读写经 `ISqlSugarClientResolver.GetCurrentClient()`，存在事务型工作单元时自动并入。

与主包默认实现的差异：

- 用户角色关联保存角色标识而非名称，角色改名后成员关系保持
- 删除角色会一并删除其角色权限，以同一标识重建的角色不继承旧权限
- 含 `CustomRequirements` 的策略无法落库，`CreatePolicyAsync` / `UpdatePolicyAsync` 抛 `NotSupportedException`；这类策略需由应用自行实现的 `IPolicyStore` 提供

已知边界：

- `DefaultPolicyEvaluator` 对策略的 `RequiredPermissions` 仍逐个判定，每个权限 1 至 2 条 SQL
- 应用自己 `Replace` 的 `IPermissionChecker` 优先于本包的实现，此时判定次数由应用的实现决定
- 名称比较由数据库排序规则决定：MySQL、SQL Server 默认不区分大小写，PostgreSQL、SQLite 区分
- 查重与插入之间不加锁，并发写入同一条授权或同名角色 / 策略时，后到者撞唯一索引抛出数据库异常
- 删除权限定义不删除已有的授予行，定义补回后授予立即重新生效
- 静态角色（`IsStatic`）可以删除，`IsInRoleAsync` 不检查角色是否启用，均与默认实现一致
- 权限名超过 256 字符、用户或角色标识超过 128 字符时，严格模式的数据库直接报错，本包不截断
- `Properties` 以 System.Text.Json 存取，读回的字典值为 `JsonElement`
- SqlSugar 的异步方法把取消令牌写进 `Ado.CancellationToken` 且执行后不清除，同一作用域内后续不带令牌的调用会继承它

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuthorizationSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

开启建表：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableDbInitialization": true,
        "EnableTableInitialization": true
      }
    }
  }
}
```

权限定义不在 `IPermissionStore` 契约里，通过具体类型写入（例如在数据种子中）：

```csharp
var permissionStore = serviceProvider.GetRequiredService<SqlSugarPermissionStore>();

await permissionStore.AddPermissionsAsync(
[
    new PermissionDefinition("User.Create", "创建用户"),
    new PermissionDefinition("User.Delete", "删除用户")
]);
```

## 扩展点

三个存储与检查器都以 `services.Replace` 注册。应用侧要再次替换，同样使用 `Replace`——`TryAdd` 不会生效。

本包不缓存判定结果。需要缓存时，在 `IPermissionChecker` 外套一层装饰器，并在应用自己的授权写路径上作废缓存。

## 目录结构

```text
XiHan.Framework.Authorization.SqlSugar/
  Entities/
    SysAuthzPermission.cs
    SysAuthzPolicy.cs
    SysAuthzRole.cs
    SysAuthzRolePermission.cs
    SysAuthzUserPermission.cs
    SysAuthzUserRole.cs
  Extensions/
    DependencyInjection/
      XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs
  Mapping/
    JsonColumn.cs
    PermissionMapper.cs
    PolicyMapper.cs
    RoleMapper.cs
  Permissions/
    SqlSugarPermissionChecker.cs
    SqlSugarPermissionStore.cs
  Policies/
    SqlSugarPolicyStore.cs
  Roles/
    SqlSugarRoleStore.cs
  README.md
  XiHanAuthorizationSqlSugarModule.cs
```
````

- [ ] **Step 2: 核对目录结构与实际文件一致**

```bash
find framework/src/XiHan.Framework.Authorization.SqlSugar -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" | sort
```

预期：输出的 16 个 `.cs` 文件与 README「目录结构」列出的一一对应。有出入就改 README。

- [ ] **Step 3: 提交**

```bash
git add framework/src/XiHan.Framework.Authorization.SqlSugar/README.md
git commit -m "docs(authorization-sqlsugar): 新增包 README"
```

---

### Task 6: 文档站页面、侧边栏与包索引

**Files:**
- Create: `docs/packages/authorization-sqlsugar.md`
- Modify: `docs/.vitepress/config.ts`
- Modify: `docs/packages/index.md`

**Interfaces:**
- Consumes: Task 5 的 README（内容一致）
- Produces: 无

**参考来源（动手前先读）：**
- `docs/packages/auditing-sqlsugar.md`、`docs/packages/eventbus-sqlsugar.md`（头部元数据与章节）
- `docs/.vitepress/config.ts` 第 139–147 行「安全 · 认证 · 授权」分组
- `docs/packages/index.md` 「## 4. 安全 · 认证 · 授权」

**本任务禁止事项：** 硬约束 ⑤：不改 `docs/packages/authorization.md` 及任何其他页面。

- [ ] **Step 1: 写文档站页面**

创建 `docs/packages/authorization-sqlsugar.md`，内容如下（最外层的四个反引号只是本计划的包裹）：

````markdown
# XiHan.Framework.Authorization.SqlSugar

> 授权存储的 SqlSugar 持久化提供程序：权限、角色、策略及其关联落库，并以直接查表的检查器替换默认权限检查器。替换 [Authorization](./authorization) 的内存存储后，RBAC 数据才真正跨请求保留。

- **NuGet**：`XiHan.Framework.Authorization.SqlSugar`
- **模块类**：`XiHanAuthorizationSqlSugarModule`
- **所在层**：基础设施层
- **关键依赖**：[Authorization](./authorization)（存储与检查器契约）、[Data](./data)（SqlSugar 客户端、租户过滤、雪花主键、建表）

## 概述

[Authorization](./authorization) 定义了 `IPermissionStore`、`IRoleStore`、`IPolicyStore` 三个存储契约，默认实现是**作用域生命周期**的内存字典——每个请求拿到一个全新的空存储，授予、角色、策略都留不过一个请求。

本包补上两件事：

- **落库**：三个存储的 SqlSugar 实现，六张表
- **热路径**：默认的 `DefaultPermissionChecker` 对用户的每个启用角色各查一次角色权限，一次判定要 `2 + 角色数` 次查询，判定多个权限时再乘以权限数。本包以 `SqlSugarPermissionChecker` 替换它，一次判定至多 2 条 SQL

## 何时使用

- 需要把 RBAC 数据（权限定义、角色、授予、策略）存进关系型数据库
- 使用框架自带的 `[PermissionAuthorize]` / `IAuthorizationService`，希望授权变更即时生效
- 已在用 [Data](./data)，希望授权数据随租户隔离

应用若已有自己的角色、权限表和检查器（例如在应用层基于授权快照判定），不需要本包。

## 安装与启用

```bash
dotnet add package XiHan.Framework.Authorization.SqlSugar
```

```csharp
[DependsOn(typeof(XiHanAuthorizationSqlSugarModule))]
public class MyModule : XiHanModule { }
```

`XiHanAuthorizationSqlSugarModule.ConfigureServices` 调用 `services.AddXiHanAuthorizationSqlSugar()`，以 `services.Replace` 顶替 [Authorization](./authorization) 用 `TryAddScoped` 注册的默认实现：

| 契约 | 默认实现 | 本包实现 |
| --- | --- | --- |
| `IPermissionStore` | `DefaultPermissionStore` | `SqlSugarPermissionStore`（具体类型另以作用域注册） |
| `IRoleStore` | `DefaultRoleStore` | `SqlSugarRoleStore` |
| `IPolicyStore` | `DefaultPolicyStore` | `SqlSugarPolicyStore` |
| `IPermissionChecker` | `DefaultPermissionChecker` | `SqlSugarPermissionChecker` |

本包**没有自己的配置节**。建表需要打开 [Data](./data) 的两个开关（默认均为 `false`）：

```json
{
  "XiHan": {
    "Data": {
      "SqlSugarCore": {
        "EnableDbInitialization": true,
        "EnableTableInitialization": true
      }
    }
  }
}
```

不打开就不会建表，首次调用任何一个存储或检查器即报「表不存在」。

## 表结构

| 表 | 内容 | 租户 | 唯一索引 |
| --- | --- | --- | --- |
| `sys_authz_permission` | 权限定义 | 全局 | `Permission_Name` |
| `sys_authz_user_permission` | 用户直接授予 | 严格隔离 | `Tenant_Id, User_Id, Permission_Name` |
| `sys_authz_role_permission` | 角色授予 | 严格隔离 | `Tenant_Id, Role_Id, Permission_Name` |
| `sys_authz_role` | 角色 | 严格隔离 | `Tenant_Id, Role_Id`；`Tenant_Id, Role_Name` |
| `sys_authz_user_role` | 用户角色关联 | 严格隔离 | `Tenant_Id, User_Id, Role_Id` |
| `sys_authz_policy` | 策略 | 全局 | `Policy_Name` |

| 约定 | 值 |
| --- | --- |
| 表名 | `sys_authz_` 前缀、全小写下划线，不分表；刻意避开 `sys_role`、`sys_permission` 这类常见业务表名 |
| 列名 | Pascal_Snake_Case，每列带简体中文 `ColumnDescription` |
| 主键 | `Basic_Id`，`long`，雪花 ID，非自增 |
| 业务键 | 契约里的角色标识、权限名、策略名、用户标识存为普通列，按租户组成唯一索引 |
| 关联 | 关联表存契约里的字符串标识，不存其他表的 `Basic_Id`；用户角色关联存**角色标识** |
| 集合字段 | 策略的 `RequiredRoles`、`RequiredPermissions`、`RequiredClaims` 与各定义的 `Properties` 存为 JSON 文本 |
| 建表分组 | `[TableInitialization(Group = "Authorization")]`，`OptIn` 模式也会建；`All` 模式可用 `ExcludedGroups` 排除 |

「严格隔离」即实体实现 `IStrictMultiTenantEntity`：租户态只看本租户的行，平台态只看 `TenantId = 0` 的行。

## 工作原理

### 一次权限判定

```text
SqlSugarPermissionChecker.IsGrantedAsync(userId, name)
  ① 直接授予
     user_permission ⋈ permission(启用)             WHERE User_Id = ? AND 名称 IN (…)
     全部命中 → 返回
  ② 经角色授予
     user_role ⋈ role(启用) ⋈ role_permission ⋈ permission(启用)
       各跳联表条件都带 Tenant_Id 相等                WHERE User_Id = ? AND 名称 IN (…)
  结果 = ① ∪ ②
```

`IsAnyGrantedAsync`、`IsAllGrantedAsync` 把全部候选权限放进同一对查询；`GetGrantedPermissionsAsync` 不带候选条件。每条查询的每一跳都有首列等值的索引。

判定语义与 `DefaultPermissionChecker` 一致：直接授予只看权限是否启用；经角色授予要求角色与权限都启用；`PermissionExistsAsync` 只看定义是否存在。

### 删除角色

```text
按 Role_Id 读出角色行
在同一事务内：
  DELETE user_role       WHERE Tenant_Id = 该行 AND Role_Id = 该行
  DELETE role_permission WHERE Tenant_Id = 该行 AND Role_Id = 该行
  DELETE role            WHERE Basic_Id = 该行
```

当前已处于事务型工作单元时并入该事务，由工作单元统一提交或回滚；否则自开事务，失败时整体回滚并把异常抛给调用方。

### 事务与客户端

所有读写经 `ISqlSugarClientResolver.GetCurrentClient()`，存在事务型工作单元时自动并入——「创建用户 + 分配角色」可以在同一个事务里完成。六张表始终在当前租户的主库，不按实体分库。

## 主要 API / 类型

| 类型 | 说明 |
| --- | --- |
| `SqlSugarPermissionStore` | `IPermissionStore` 实现；另有 `AddOrUpdatePermissionAsync`、`AddPermissionsAsync`、`RemovePermissionAsync` 维护权限定义 |
| `SqlSugarRoleStore` | `IRoleStore` 实现 |
| `SqlSugarPolicyStore` | `IPolicyStore` 实现 |
| `SqlSugarPermissionChecker` | `IPermissionChecker` 实现 |
| `SysAuthzPermission` 等 6 个实体 | 表实体，各带无参与 `(long basicId)` 两个公开构造函数 |
| `PermissionMapper` / `RoleMapper` / `PolicyMapper` | 定义与实体的静态映射器 |
| `XiHanAuthorizationSqlSugarServiceCollectionExtensions` | `AddXiHanAuthorizationSqlSugar()`：以 `Replace` 注册上述实现 |
| `XiHanAuthorizationSqlSugarModule` | 模块类，`[DependsOn(XiHanAuthorizationModule, XiHanDataModule)]`，只做装配 |

## 使用示例

### 1. 播种权限定义

权限定义不在 `IPermissionStore` 契约里，通过具体类型写入：

```csharp
var permissionStore = serviceProvider.GetRequiredService<SqlSugarPermissionStore>();

await permissionStore.AddPermissionsAsync(
[
    new PermissionDefinition("User.Create", "创建用户"),
    new PermissionDefinition("User.Delete", "删除用户")
]);
```

### 2. 建角色并授权

```csharp
await roleStore.CreateRoleAsync(new RoleDefinition("editor", "editor", "编辑"));
await permissionStore.GrantPermissionToRoleAsync("editor", "User.Create");
await roleStore.AddUserToRoleAsync(userId, "editor");
```

`GrantPermissionToRoleAsync` 的第一个参数是**角色标识**，`AddUserToRoleAsync` 的第二个参数是**角色名称**——这是契约本身的约定。

### 3. 判定

```csharp
var granted = await permissionChecker.IsGrantedAsync(userId, "User.Create");
```

## 扩展点 / 自定义

- **再次替换**：三个存储与检查器都以 `services.Replace` 注册，应用侧也要用 `Replace`；`TryAdd` 不生效
- **缓存判定**：本包不缓存。需要时在 `IPermissionChecker` 外套装饰器，并在应用自己的授权写路径上作废
- **自定义要求**：含 `CustomRequirements` 的策略无法落库，由应用自行实现 `IPolicyStore` 提供

## 注意事项与最佳实践

- **建表开关默认关闭**。`EnableDbInitialization` 与 `EnableTableInitialization` 都要打开
- **保持租户过滤器开启**。单表读写依赖 `EnableTenantFilter`（默认 `true`）收紧到当前租户；关闭后 `GetRoleByNameAsync` 等会看到别的租户的同名角色。联表条件已带 `Tenant_Id`，不会跨租户配对
- **权限定义与策略全局可写**。这两张表不分租户，租户态调用写方法会改到所有租户共用的数据，应只在平台态开放
- **含自定义要求的策略写入即抛 `NotSupportedException`**。这是为了不让一条要求在落库时悄悄消失
- **策略评估仍逐权限判定**。`DefaultPolicyEvaluator` 对 `RequiredPermissions` 逐个调 `IsGrantedAsync`，p 个权限约 2p 条 SQL
- **应用自己的检查器优先**。应用 `Replace` 了 `IPermissionChecker` 时，本包的检查器不生效
- **名称比较交给数据库**。MySQL、SQL Server 默认不区分大小写，PostgreSQL、SQLite 区分
- **并发重复写入**。查重与插入之间不加锁，后到者撞唯一索引抛数据库异常
- **删除权限定义不删授予**。定义补回后授予立即重新生效
- **与默认实现的差异**：用户角色关联存角色标识，改名后成员关系保持；删除角色会删除其角色权限
- **与默认实现一致的行为**：静态角色可以删除；`IsInRoleAsync` 不检查角色是否启用
- **`Properties` 读回为 `JsonElement`**，不是写入时的原始类型

## 依赖模块

- [XiHan.Framework.Authorization](./authorization)（存储与检查器契约）
- [XiHan.Framework.Data](./data)（`ISqlSugarClientResolver`、租户过滤器、`DbInitializer`、SqlSugar 传递依赖）

`IDistributedIdGenerator<long>` 经 `XiHanDataModule → XiHanDistributedIdsModule` 间接获得，本包不额外声明依赖。

## 相关模块

- [XiHan.Framework.MultiTenancy](./multitenancy)（租户上下文）
- [XiHan.Framework.Auditing.SqlSugar](./auditing-sqlsugar)、[XiHan.Framework.EventBus.SqlSugar](./eventbus-sqlsugar)：同一套落库范式的其他子包
````

- [ ] **Step 2: 侧边栏加一项**

修改 `docs/.vitepress/config.ts`。在「安全 · 认证 · 授权」分组内这一行：

```ts
          pkg("Authorization 授权", "authorization"),
```

之后插入：

```ts
          pkg("Authorization.SqlSugar", "authorization-sqlsugar"),
```

- [ ] **Step 3: 包索引加一行**

修改 `docs/packages/index.md`。在「## 4. 安全 · 认证 · 授权」的表中这一行：

```markdown
| [Authorization](./authorization) | 授权：RBAC、策略授权、声明授权 |
```

之后插入：

```markdown
| [Authorization.SqlSugar](./authorization-sqlsugar) | 授权存储 SqlSugar 提供程序：权限、角色、策略落库，查表式权限检查器每次判定至多两条 SQL |
```

- [ ] **Step 4: 核对链接目标存在**

```bash
ls docs/packages/authorization.md docs/packages/data.md docs/packages/multitenancy.md docs/packages/auditing-sqlsugar.md docs/packages/eventbus-sqlsugar.md
```

预期：五个文件都存在。缺哪个就把对应链接改成纯文本，不要去新建那个页面。

- [ ] **Step 5: 提交**

```bash
git add docs/packages/authorization-sqlsugar.md docs/packages/index.md docs/.vitepress/config.ts
git commit -m "docs(authorization-sqlsugar): 新增文档站页面并登记侧边栏与包索引"
```

---

### Task 7: 框架 README 的模块清单与模块数

**Files:**
- Modify: `framework/README.md`
- Modify: `framework/README_cn.md`
- Modify: `README.md`
- Modify: `README_cn.md`
- Modify: `docs/index.md`、`docs/introduction.md`、`docs/why.md`、`docs/packages/index.md`（只改模块数）

**Interfaces:** 无

**参考来源（动手前先读）：** 四个文件中 `Authorization` 一行的上下文；`Auditing.SqlSugar`、`EventBus.SqlSugar` 在 `framework/README*.md` 里的写法。

**本任务禁止事项：** 除 Step 1–2 各加一行、Step 3 把数量 N 改成 N+1 之外，不改其他任何一行。不要把「68 → 69」写死，数字以实现时读到的为准。

- [ ] **Step 1: framework/README.md**

在这一行：

```markdown
| `Authorization` | Authorization: RBAC, policy-based, claims-based |
```

之后插入：

```markdown
| `Authorization.SqlSugar` | SqlSugar persistence provider for authorization: permission, role and policy stores plus a query-based permission checker (at most two SQL statements per check) |
```

- [ ] **Step 2: framework/README_cn.md**

在这一行：

```markdown
| `Authorization` | 授权：RBAC、策略授权、声明授权 |
```

之后插入：

```markdown
| `Authorization.SqlSugar` | 授权存储 SqlSugar 持久化提供程序：权限、角色、策略三个存储落库，查表式权限检查器每次判定至多两条 SQL |
```

根 `README.md` / `README_cn.md` 的「常用包」表**不加行**：它是精选的常用包列表，`Auditing.SqlSugar`、`EventBus.SqlSugar` 都不在其中。根 README 只做下一步的模块数加一。

- [ ] **Step 3: 模块数与测试工程数各加一**

**不要写死数字。** 系列里 8 个包先后合入，编写本计划时是 68，到你实现时可能已经变了。先读当前值：

```bash
grep -n "Modules-[0-9]*-1f6feb" README.md
```

记下徽章里的数字 N（编写本计划时 N = 68），然后在下列文件里查出全部命中：

```bash
grep -n "N" README.md README_cn.md framework/README.md framework/README_cn.md docs/index.md docs/introduction.md docs/packages/index.md docs/why.md
```

（把 `N` 换成实际数字。）逐条人工判断，**只改表示模块 / 包 / 单测工程数量的那些命中**，改成 N+1。编写本计划时的位置如下，行号会随先合入的包漂移，以 grep 结果为准：

| 文件 | 编写时的行 | 内容 |
| --- | --- | --- |
| `README.md` | 8 | `68 modular components` |
| `README.md` | 20 | 徽章 URL `Modules-68-1f6feb`——**数字嵌在 URL 里，最容易漏** |
| `README.md` | 55 | `all 68 packages` |
| `README_cn.md` | 8 / 20 / 55 | 同上三处（`68 个模块化组件`、徽章 URL、`68 个包`） |
| `framework/README.md` | 55 / 186 | `68 modules` / `(68 modules)` |
| `framework/README.md` | 198 | `68 unit-test projects`——**单测工程数**，本包新增了测试项目，同样加一 |
| `framework/README_cn.md` | 55 / 186 / 198 | 同上三处（198 是单测工程数） |
| `docs/index.md` | 9 / 41 | `68 个可独立引用的 NuGet 包` / `68 个包按七层组织` |
| `docs/introduction.md` | 57 | `（68 页）` |
| `docs/packages/index.md` | 3 | `**68 个 NuGet 包**` |
| `docs/why.md` | 86 / 123 / 160 | 三处 `68 个包` |

同一数字在这些文件里若表示别的东西（版本号、端口、行号引用等），**不要改**。单测工程数若在实现时已与模块数不同，也按「当前值 + 1」各自处理。

- [ ] **Step 4: 核对改动**

```bash
git diff --stat
git diff README.md README_cn.md framework/README.md framework/README_cn.md docs/index.md docs/introduction.md docs/packages/index.md docs/why.md
grep -n "Modules-[0-9]*-1f6feb" README.md README_cn.md
```

预期：`framework/README.md` 与 `framework/README_cn.md` 各多一行模块清单（Step 1–2），根 `README.md` / `README_cn.md` **没有**新增表格行；其余差异全部是数量 N → N+1；两个徽章 URL 都是 `Modules-{N+1}-1f6feb`。

- [ ] **Step 5: 提交**

```bash
git add README.md README_cn.md framework/README.md framework/README_cn.md docs/index.md docs/introduction.md docs/packages/index.md docs/why.md
git commit -m "docs(authorization-sqlsugar): 模块清单登记 Authorization.SqlSugar，模块数加一"
```

---

### Task 8: 全量验收与注释复查

**Files:** 无新增；只在复查发现问题时修改本包的 `.cs` 文件。

**Interfaces:**
- Consumes: ①②③ 的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：** 三份 spec 的第 5 节与第 8 节。

**本任务禁止事项：** 不要为了让测试变绿而改断言。不要把权衡论证写进代码注释。

- [ ] **Step 1: 全量构建与测试**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过。唯一允许的失败是已知抖动 `MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`，重跑即可。

构建报 `MSB3027` / `MSB3021` 时：

```bash
taskkill //F //IM "XiHan.Framework.Authorization.SqlSugar.Tests.exe"
```

- [ ] **Step 2: 注释复查（全包）**

通读 `framework/src/XiHan.Framework.Authorization.SqlSugar/` 与 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/` 下**全部** `.cs` 文件的注释与 XML 文档注释，不只本计划新增的。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即删掉，内容挪进提交信息。

同时确认：没有 `<inheritdoc/>`；每个 `public` 成员都有 `<summary>`；每个文件以两行版权声明开头。

- [ ] **Step 3: 若 Step 2 有改动则提交**

```bash
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "style(authorization-sqlsugar): 注释只保留代码行为说明"
```

没有改动就跳过本步。

- [ ] **Step 4: 核对文档六处**

逐项确认，缺一处都不算完成：

1. `framework/src/XiHan.Framework.Authorization.SqlSugar/XiHan.Framework.Authorization.SqlSugar.csproj` 按序 Import `netcore` / `common` / `version` / `nuget` 四个 props（①）
2. `XiHanAuthorizationSqlSugarModule.cs` 只调 `AddXiHanAuthorizationSqlSugar()`；扩展方法在 `Extensions/DependencyInjection/`（①）
3. 包 `README.md` 七段齐全（Task 5）
4. `framework/XiHan.Framework.slnx` 注册了包（`/1.src/6.Infrastructure/`）与测试项目（`/2.tests/1.UnitTests/`）（①）
5. `docs/packages/authorization-sqlsugar.md` + `docs/.vitepress/config.ts` 侧边栏 + `docs/packages/index.md`（Task 6）
6. `framework/README.md`、`framework/README_cn.md` 的模块清单各加一行；根 `README.md` / `README_cn.md` 的「常用包」表不加行；四份 README 与文档站四页中的模块数 / 单测工程数（含两个徽章 URL）均已 +1（Task 7）

---

## 完成标准

③ 完成时（即整个包完成时）应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `SysAuthzPolicy` 不分租户，策略名唯一
- 策略的三个集合往返逐项相等；列为空时读回空集合
- 含 `CustomRequirements` 的策略，创建与更新都抛 `NotSupportedException`，库里数据不变
- `CreatePolicyAsync(null)` 抛 `ArgumentException`（与默认实现一致）
- `IPermissionStore`、`IRoleStore`、`IPolicyStore`、`IPermissionChecker` 各只有一个描述符，均为本包的 SqlSugar 实现、`Scoped`
- 新包的六处文档齐全

## 已知边界（写入 PR 描述，不写进代码注释）

PR 描述汇总三份的边界：

- **自定义要求不能落库**：含 `CustomRequirements` 的策略写入即抛 `NotSupportedException`，需由应用自行实现 `IPolicyStore`
- **权限定义与策略全局可写**：两张表不分租户，写操作应只在平台态开放
- **策略评估仍逐权限查询**：`DefaultPolicyEvaluator` 对 `RequiredPermissions` 逐个判定
- **应用自己的检查器优先**
- **与默认实现的差异**：用户角色关联存角色标识，改名后成员关系保持；删除角色级联删除角色权限
- **与默认实现一致的行为**：静态角色可删；`IsInRoleAsync` 不看启用；删除权限定义不删授予
- **需要租户过滤器**：`EnableTenantFilter` 须为 `true`
- **名称大小写**：由数据库排序规则决定
- **并发重复写入**：后到者撞唯一索引抛数据库异常
- **`Properties` 读回为 `JsonElement`**
- **取消令牌残留**：SqlSugar 不清除 `Ado.CancellationToken`
- **破坏性变更**：无。本包是新增包，只在应用显式依赖 `XiHanAuthorizationSqlSugarModule` 时生效；不想用时不依赖即可，没有需要额外关闭的开关
- **根 README 的「常用包」表**：沿用 `Auditing.SqlSugar`、`EventBus.SqlSugar` 的惯例，不加行；根 README 只改模块数

## 下一份计划

本包到此完成，可按 fork 协作流程从 `upstream/main` 开 worktree 提 PR。按拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md` 第 5 节），下一个包是 `Security` + `Traffic` + `Upgrade` 三个小包。
