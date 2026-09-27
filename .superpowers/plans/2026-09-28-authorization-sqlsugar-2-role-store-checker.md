# Authorization.SqlSugar ②：角色存储与权限检查器 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付角色与用户角色关联两个实体、`IRoleStore` 的 SqlSugar 实现（删除角色同事务级联），以及顶替 `IPermissionChecker` 的 `SqlSugarPermissionChecker`，把一次权限判定从 `2 + 角色数` 条 SQL 降到至多 2 条。

**Architecture:** 用户角色关联存角色**标识**，所有联表条件同时带 `TenantId` 相等。删除角色先删两张关联表、后删角色行，已在事务里就直接执行，不在就用 `Ado.UseTranAsync` 自开并检查返回值。检查器用两条 SQL：先查直接授予（候选全部命中即返回），再用四表联表查经启用角色授予的启用权限。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-authorization-sqlsugar-2-role-store-checker-design.md`

> 该 spec 自成一体，实现 ② 所需的全部约束都在其中。

**Linear 议题:** <https://linear.app/elf-express/issue/EDDIE-10>

**前置:** ①（`.superpowers/plans/2026-09-28-authorization-sqlsugar-1-permission-store.md`）必须已完成。本计划依赖 ① 的三个实体、`JsonColumn`、`SqlSugarPermissionStore`、测试夹具 `AuthorizationTestContext` / `StubClientResolver`、注册扩展 `AddXiHanAuthorizationSqlSugar` 与 `RegistrationTests` / `EntityConventionTests`。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/authorization-sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-authorization`）。若 worktree 内看不到这些文件，请按上面的绝对路径读取。

> **动手前先读 ① 已落地的 `SqlSugarPermissionStore.cs` 与 `AuthorizationTestContext.cs`。** 本计划沿用它们的写法（`Client` 属性、查重再插入、夹具工厂方法），且要在夹具与两个测试文件里追加内容。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：`E:/source/XiHan/XiHan.Framework-authorization`，分支 `feat/authorization-sqlsugar`（① 已从 `dev` 开出）。上游是 `main`，**绝不在 `main` 上提交**。若 worktree 尚不存在：

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
// SqlSugar —— Interface/IQueryable.cs
ISugarQueryable<T, T2> ISugarQueryable<T>.InnerJoin<T2>(Expression<Func<T, T2, bool>> joinExpression)
ISugarQueryable<T, T2, T3> ISugarQueryable<T, T2>.InnerJoin<T3>(Expression<Func<T, T2, T3, bool>> joinExpression)
ISugarQueryable<T, T2, T3, T4> ISugarQueryable<T, T2, T3>.InnerJoin<T4>(Expression<Func<T, T2, T3, T4, bool>> joinExpression)
ISugarQueryable<T, T2> ISugarQueryable<T, T2>.Where(Expression<Func<T, T2, bool>> expression)
ISugarQueryable<T, T2, T3, T4> ISugarQueryable<T, T2, T3, T4>.Where(Expression<Func<T, T2, T3, T4, bool>> expression)
ISugarQueryable<TResult> ISugarQueryable<T, T2>.Select<TResult>(Expression<Func<T, T2, TResult>> expression)
ISugarQueryable<TResult> ISugarQueryable<T, T2, T3, T4>.Select<TResult>(Expression<Func<T, T2, T3, T4, TResult>> expression)
Task<bool> ISugarQueryable<T>.AnyAsync(CancellationToken token)                                   // 联表查询同样适用：Clone().Take(1).Select("1")
Task<bool> ISugarQueryable<T>.AnyAsync(Expression<Func<T, bool>> expression, CancellationToken token)
Task<T> ISugarQueryable<T>.FirstAsync(Expression<Func<T, bool>> expression, CancellationToken token)  // 无行时返回 null
Task<List<T>> ISugarQueryable<T>.ToListAsync(CancellationToken token)
Task<int> ISugarQueryable<T>.CountAsync()

// SqlSugar —— 更新 / 删除 / 插入
IUpdateable<T> IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T> IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IUpdateable<T>.ExecuteCommandAsync(CancellationToken token)
IDeleteable<T> IDeleteable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IDeleteable<T>.ExecuteCommandAsync(CancellationToken token)
Task<int> IInsertable<T>.ExecuteCommandAsync(CancellationToken token)

// SqlSugar —— Interface/IAdo.cs / Abstract/AdoProvider/AdoProvider.cs
bool IAdo.IsNoTran()                                                             // Transaction == null
Task<DbResult<bool>> IAdo.UseTranAsync(Func<Task> action, Action<Exception> errorCallBack = null)   // 失败不抛，返回 IsSuccess = false
void IAdo.BeginTran()
void IAdo.RollbackTran()
// DbResult<T>：bool IsSuccess、Exception ErrorException、string ErrorMessage、T Data

// SqlSugar —— 其他
bool IDbMaintenance.DropTable(string tableName)
Action<string, SugarParameter[]> AopProvider.OnLogExecuting { set; }             // ISqlSugarClient.Aop
QueryFilterProvider QueryFilterProvider.AddTableFilter<T>(Expression<Func<T, bool>> expression, FilterJoinPosition filterJoinType = FilterJoinPosition.On)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3, bool isUnique = false)

// 框架
ISqlSugarClient ISqlSugarClientResolver.GetCurrentClient()
long IDistributedIdGenerator<long>.NextId()
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

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。命令：`dotnet test --project <csproj> -c Release`。

**SQLite 临时库**：连接串必须带 `Pooling=False`（① 的夹具已带）。

**构建环境坑**：`MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留测试进程，`taskkill //F //IM "<name>.exe"` 后重建。

**已知的无关抖动**：全量测试偶发 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 失败（GC 时序），与本包无关，不要去追。

**提交信息**：中文 Conventional Commits，作用域 `authorization-sqlsugar`。**不加任何 AI 署名**。

**建表**：`EnableDbInitialization` 与 `EnableTableInitialization` 都默认 `false`，不开启不建表。

---

## 本计划特有的硬约束

**① 检查器的查询次数由测试钉死。**

直接授予命中 1 条 SQL，其余 2 条，多权限判定至多 2 条——与角色数无关。Task 4 用 `Client.Aop.OnLogExecuting` 数 SQL。**不许为了让别的测试通过而放宽这几个断言**，也不许在检查器里退回「按角色循环调 `GetRolePermissionsAsync`」的写法。

**② 两个 `IsEnabled` 条件都要在。**

经角色授予的路径同时要求 `role.IsEnabled` 与 `permission.IsEnabled`；直接授予要求 `permission.IsEnabled`。

**③ `UseTranAsync` 只在 `Ado.IsNoTran()` 为真时用，并且必须检查 `IsSuccess` 后重新抛出。**

它吞异常；已有事务时它会提交外层事务。

**④ 所有联表条件带 `TenantId` 相等。**

`member.TenantId == role.TenantId && member.RoleId == role.RoleId`，role → role_permission 同理。**权限定义表是全局的，与它联表只按 `PermissionName`。**

**⑤ 用户角色关联存角色标识，不存名称。**

**⑥ 只用 `GetCurrentClient()`，不手动赋 `TenantId`，不改主包任何文件。**

---

## File Structure

```
framework/src/XiHan.Framework.Authorization.SqlSugar/
  Entities/SysAuthzRole.cs                                             新建
  Entities/SysAuthzUserRole.cs                                         新建
  Mapping/RoleMapper.cs                                                新建
  Roles/SqlSugarRoleStore.cs                                           新建
  Permissions/SqlSugarPermissionChecker.cs                             新建
  Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs   修改：追加两行 Replace

framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/
  AuthorizationTestContext.cs                                          修改：追加两个工厂方法
  EntityConventionTests.cs                                             修改：追加两个新实体的用例
  RoleMapperTests.cs                                                   新建
  RoleStoreTests.cs                                                    新建
  UserRoleTests.cs                                                     新建
  RoleDeletionTests.cs                                                 新建
  PermissionCheckerTests.cs                                            新建
  TenantFilterTests.cs                                                 新建
  RegistrationTests.cs                                                 修改：追加两条 InlineData
```

---

### Task 1: 角色与用户角色关联实体

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzRole.cs`
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzUserRole.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/EntityConventionTests.cs`

**Interfaces:**
- Consumes: ① 的实体写法；`SugarMultiTenantEntity<long>`、`IStrictMultiTenantEntity`、`TableInitializationAttribute`
- Produces:
  - `SysAuthzRole`：`RoleId`、`RoleName`、`DisplayName`、`Description`、`IsEnabled`、`IsDefault`、`IsStatic`、`SortOrder`、`CreatedTime`（`DateTime`）、`LastModifiedTime`（`DateTime?`）、`Properties`，继承 `TenantId`
  - `SysAuthzUserRole`：`UserId`、`RoleId`，继承 `TenantId`
  - 两者均有 `public Xxx()` 与 `public Xxx(long basicId)`

**参考来源（动手前先读）：**
- ① 的 `Entities/SysAuthzRolePermission.cs`（三字段唯一索引写法）
- 字段来源：`framework/src/XiHan.Framework.Authorization/Roles/RoleDefinition.cs`

**本任务禁止事项：** 硬约束 ④⑥。`[SugarIndex]` 字段一律 `nameof(类名.属性)`。不要把 `CreatedTime` 改成 `DateTimeOffset`——契约是 `DateTime`。

- [ ] **Step 1: 写失败的测试**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/EntityConventionTests.cs`。

在 `关联实体按租户严格隔离` 的两条 `[InlineData]` 之后追加两条，使其变为：

```csharp
    [Theory]
    [InlineData(typeof(SysAuthzUserPermission))]
    [InlineData(typeof(SysAuthzRolePermission))]
    [InlineData(typeof(SysAuthzRole))]
    [InlineData(typeof(SysAuthzUserRole))]
    public void 关联实体按租户严格隔离(Type entityType)
```

在 `角色权限授予在同一租户内唯一` 方法之后、`NewPermission` 私有方法之前插入：

```csharp
    /// <summary>
    /// 角色标识与角色名称在同一租户内各自唯一，不同租户可重复
    /// </summary>
    [Fact]
    public async Task 角色标识与名称在同一租户内唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewRole(context, 0, "r1", "admin")).ExecuteCommandAsync();
        await context.Client.Insertable(NewRole(context, 1, "r1", "admin")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewRole(context, 0, "r1", "other")).ExecuteCommandAsync());
        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewRole(context, 0, "r2", "admin")).ExecuteCommandAsync());
    }

    /// <summary>
    /// 用户角色关联在同一租户内唯一，不同租户可重复
    /// </summary>
    [Fact]
    public async Task 用户角色关联在同一租户内唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewUserRole(context, 0, "u1", "r1")).ExecuteCommandAsync();
        await context.Client.Insertable(NewUserRole(context, 1, "u1", "r1")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewUserRole(context, 0, "u1", "r1")).ExecuteCommandAsync());
    }
```

在文件末尾最后一个私有方法 `NewRolePermission` 之后、类的右花括号之前插入：

```csharp
    private static SysAuthzRole NewRole(AuthorizationTestContext context, long tenantId, string roleId, string roleName)
    {
        return new SysAuthzRole(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            RoleId = roleId,
            RoleName = roleName,
            DisplayName = roleName,
            IsEnabled = true,
            CreatedTime = DateTime.UtcNow
        };
    }

    private static SysAuthzUserRole NewUserRole(AuthorizationTestContext context, long tenantId, string userId, string roleId)
    {
        return new SysAuthzUserRole(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            UserId = userId,
            RoleId = roleId
        };
    }
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SysAuthzRole`、`SysAuthzUserRole`。

- [ ] **Step 3: 建两个实体**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzRole.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Entities;

/// <summary>
/// 角色实体
/// </summary>
[SugarTable("sys_authz_role")]
[TableInitialization(Group = "Authorization")]
[SugarIndex("ux_authz_role_tenant_id",
    nameof(SysAuthzRole.TenantId), OrderByType.Asc,
    nameof(SysAuthzRole.RoleId), OrderByType.Asc,
    true)]
[SugarIndex("ux_authz_role_tenant_name",
    nameof(SysAuthzRole.TenantId), OrderByType.Asc,
    nameof(SysAuthzRole.RoleName), OrderByType.Asc,
    true)]
public class SysAuthzRole : SugarMultiTenantEntity<long>, IStrictMultiTenantEntity
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthzRole() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthzRole(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 角色标识
    /// </summary>
    [SugarColumn(ColumnName = "Role_Id", Length = 128, IsNullable = false, ColumnDescription = "角色标识")]
    public string RoleId { get; set; } = string.Empty;

    /// <summary>
    /// 角色名称
    /// </summary>
    [SugarColumn(ColumnName = "Role_Name", Length = 128, IsNullable = false, ColumnDescription = "角色名称")]
    public string RoleName { get; set; } = string.Empty;

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
    /// 是否启用
    /// </summary>
    [SugarColumn(ColumnName = "Is_Enabled", IsNullable = false, ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 是否为默认角色
    /// </summary>
    [SugarColumn(ColumnName = "Is_Default", IsNullable = false, ColumnDescription = "是否为默认角色")]
    public bool IsDefault { get; set; }

    /// <summary>
    /// 是否为静态角色
    /// </summary>
    [SugarColumn(ColumnName = "Is_Static", IsNullable = false, ColumnDescription = "是否为静态角色")]
    public bool IsStatic { get; set; }

    /// <summary>
    /// 排序
    /// </summary>
    [SugarColumn(ColumnName = "Sort_Order", IsNullable = false, ColumnDescription = "排序")]
    public int SortOrder { get; set; }

    /// <summary>
    /// 创建时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "创建时间（UTC）")]
    public DateTime CreatedTime { get; set; }

    /// <summary>
    /// 最后修改时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Last_Modified_Time", IsNullable = true, ColumnDescription = "最后修改时间（UTC）")]
    public DateTime? LastModifiedTime { get; set; }

    /// <summary>
    /// 额外属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "额外属性的 JSON")]
    public string? Properties { get; set; }
}
```

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzUserRole.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Entities;

/// <summary>
/// 用户角色关联实体
/// </summary>
[SugarTable("sys_authz_user_role")]
[TableInitialization(Group = "Authorization")]
[SugarIndex("ux_authz_ur_tenant_user_role",
    nameof(SysAuthzUserRole.TenantId), OrderByType.Asc,
    nameof(SysAuthzUserRole.UserId), OrderByType.Asc,
    nameof(SysAuthzUserRole.RoleId), OrderByType.Asc,
    true)]
[SugarIndex("ix_authz_ur_tenant_role",
    nameof(SysAuthzUserRole.TenantId), OrderByType.Asc,
    nameof(SysAuthzUserRole.RoleId), OrderByType.Asc)]
public class SysAuthzUserRole : SugarMultiTenantEntity<long>, IStrictMultiTenantEntity
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthzUserRole() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthzUserRole(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", Length = 128, IsNullable = false, ColumnDescription = "用户标识")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// 角色标识
    /// </summary>
    [SugarColumn(ColumnName = "Role_Id", Length = 128, IsNullable = false, ColumnDescription = "角色标识")]
    public string RoleId { get; set; } = string.Empty;
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。夹具按反射建表，`全部实体的表都能建出来`、`全部实体的表名以sys_authz_开头`、`全部实体声明Authorization建表分组` 自动覆盖两个新实体。

- [ ] **Step 5: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 新增角色与用户角色关联实体"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: 角色映射器

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/RoleMapper.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RoleMapperTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysAuthzRole`；① 的 `JsonColumn`；主包的 `RoleDefinition`
- Produces: `public static class RoleMapper`：`SysAuthzRole ToEntity(RoleDefinition definition, long basicId)`、`RoleDefinition ToDefinition(SysAuthzRole entity)`

**参考来源（动手前先读）：** ① 的 `Mapping/PermissionMapper.cs`；`framework/src/XiHan.Framework.Authorization/Roles/RoleDefinition.cs`

**本任务禁止事项：** 不要截断任何字段。读回时间一律 `SpecifyKind(Utc)`，不做时区换算（写入的就是 UTC 刻度）。

- [ ] **Step 1: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RoleMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Mapping;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 角色映射器测试
/// </summary>
public class RoleMapperTests
{
    /// <summary>
    /// 角色定义映射为实体时逐字段对应
    /// </summary>
    [Fact]
    public void 角色定义映射为实体时逐字段对应()
    {
        var createdTime = new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc);
        var definition = new RoleDefinition("r1", "editor", "编辑", "可编辑内容")
        {
            IsEnabled = false,
            IsDefault = true,
            IsStatic = true,
            Order = 5,
            CreatedTime = createdTime,
            LastModifiedTime = createdTime.AddHours(1)
        };

        var entity = RoleMapper.ToEntity(definition, 42L);

        Assert.Equal(42L, entity.BasicId);
        Assert.Equal("r1", entity.RoleId);
        Assert.Equal("editor", entity.RoleName);
        Assert.Equal("编辑", entity.DisplayName);
        Assert.Equal("可编辑内容", entity.Description);
        Assert.False(entity.IsEnabled);
        Assert.True(entity.IsDefault);
        Assert.True(entity.IsStatic);
        Assert.Equal(5, entity.SortOrder);
        Assert.Equal(createdTime, entity.CreatedTime);
        Assert.Equal(createdTime.AddHours(1), entity.LastModifiedTime);
        Assert.Null(entity.Properties);
    }

    /// <summary>
    /// 实体映射回角色定义时逐字段对应且时间标记为 UTC
    /// </summary>
    [Fact]
    public void 实体映射回角色定义时逐字段对应且时间标记为UTC()
    {
        var stored = new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Unspecified);
        var entity = new SysAuthzRole(1L)
        {
            RoleId = "r1",
            RoleName = "editor",
            DisplayName = "编辑",
            Description = "可编辑内容",
            IsEnabled = true,
            IsDefault = true,
            IsStatic = false,
            SortOrder = 5,
            CreatedTime = stored,
            LastModifiedTime = stored.AddHours(1)
        };

        var definition = RoleMapper.ToDefinition(entity);

        Assert.Equal("r1", definition.Id);
        Assert.Equal("editor", definition.Name);
        Assert.Equal("编辑", definition.DisplayName);
        Assert.Equal("可编辑内容", definition.Description);
        Assert.True(definition.IsEnabled);
        Assert.True(definition.IsDefault);
        Assert.False(definition.IsStatic);
        Assert.Equal(5, definition.Order);
        Assert.Equal(DateTimeKind.Utc, definition.CreatedTime.Kind);
        Assert.Equal(stored.Ticks, definition.CreatedTime.Ticks);
        Assert.NotNull(definition.LastModifiedTime);
        Assert.Equal(DateTimeKind.Utc, definition.LastModifiedTime.Value.Kind);
        Assert.Equal(stored.AddHours(1).Ticks, definition.LastModifiedTime.Value.Ticks);
    }

    /// <summary>
    /// 未修改过的角色读回时修改时间为空
    /// </summary>
    [Fact]
    public void 未修改过的角色读回时修改时间为空()
    {
        var entity = new SysAuthzRole(1L)
        {
            RoleId = "r1",
            RoleName = "editor",
            DisplayName = "编辑",
            CreatedTime = DateTime.UtcNow
        };

        Assert.Null(RoleMapper.ToDefinition(entity).LastModifiedTime);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0103` / `CS0246` 找不到 `RoleMapper`。

- [ ] **Step 3: 实现映射器**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/RoleMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Mapping;

/// <summary>
/// 角色定义与实体的映射
/// </summary>
public static class RoleMapper
{
    /// <summary>
    /// 角色定义映射为实体
    /// </summary>
    /// <param name="definition">角色定义</param>
    /// <param name="basicId">主键</param>
    /// <returns>角色实体</returns>
    public static SysAuthzRole ToEntity(RoleDefinition definition, long basicId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new SysAuthzRole(basicId)
        {
            RoleId = definition.Id,
            RoleName = definition.Name,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            IsEnabled = definition.IsEnabled,
            IsDefault = definition.IsDefault,
            IsStatic = definition.IsStatic,
            SortOrder = definition.Order,
            CreatedTime = definition.CreatedTime,
            LastModifiedTime = definition.LastModifiedTime,
            Properties = JsonColumn.SerializeOrNull(definition.Properties)
        };
    }

    /// <summary>
    /// 实体映射为角色定义
    /// </summary>
    /// <remarks>
    /// 创建时间与修改时间标记为 UTC。
    /// </remarks>
    /// <param name="entity">角色实体</param>
    /// <returns>角色定义</returns>
    public static RoleDefinition ToDefinition(SysAuthzRole entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new RoleDefinition(entity.RoleId, entity.RoleName, entity.DisplayName, entity.Description)
        {
            IsEnabled = entity.IsEnabled,
            IsDefault = entity.IsDefault,
            IsStatic = entity.IsStatic,
            Order = entity.SortOrder,
            CreatedTime = AsUtc(entity.CreatedTime),
            LastModifiedTime = entity.LastModifiedTime.HasValue ? AsUtc(entity.LastModifiedTime.Value) : null,
            Properties = JsonColumn.DeserializeOrNull<Dictionary<string, object>>(entity.Properties)
        };
    }

    /// <summary>
    /// 把时间标记为 UTC，不改变刻度
    /// </summary>
    /// <param name="value">时间</param>
    /// <returns>标记为 UTC 的时间</returns>
    private static DateTime AsUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
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
git commit -m "feat(authorization-sqlsugar): 新增角色映射器"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 角色存储

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Roles/SqlSugarRoleStore.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RoleStoreTests.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/UserRoleTests.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RoleDeletionTests.cs`

**Interfaces:**
- Consumes: Task 1 的两个实体、Task 2 的 `RoleMapper`、① 的 `SysAuthzRolePermission` 与 `SqlSugarPermissionStore`（测试造数用）
- Produces:
  - `public class SqlSugarRoleStore : IRoleStore`，构造函数 `(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator)`，契约的 11 个方法
  - 夹具新增 `SqlSugarRoleStore CreateRoleStore()`

**参考来源（动手前先读）：**
- 语义基准：`framework/src/XiHan.Framework.Authorization/Roles/DefaultRoleStore.cs`（spec §1.2 表逐条对应，§1.3 的唯一差异）
- 事务：`E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Abstract/AdoProvider/AdoProvider.cs:261-391`
- 工作单元登记即开事务：`framework/src/XiHan.Framework.Data/SqlSugar/Clients/SqlSugarClientResolver.cs` 的 `EnlistCurrentUnitOfWork`
- ① 的 `Permissions/SqlSugarPermissionStore.cs`（`Client` 属性与查重再插入）

**本任务禁止事项：** 硬约束 ③④⑤⑥。**不要**在 `DeleteRoleAsync` 里检查 `IsStatic`。**不要**让 `UpdateRoleAsync` 改写 `CreatedTime` 或 `RoleId`。**不要**在 `GetUserRolesAsync` / `IsInRoleAsync` 里过滤 `IsEnabled`。

- [ ] **Step 1: 夹具加工厂方法**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`。

using 区在 `using XiHan.Framework.Authorization.SqlSugar.Permissions;` 之后追加：

```csharp
using XiHan.Framework.Authorization.SqlSugar.Roles;
```

在 `CreatePermissionStore` 方法之后插入：

```csharp
    /// <summary>
    /// 创建角色存储
    /// </summary>
    /// <returns>角色存储</returns>
    public SqlSugarRoleStore CreateRoleStore()
    {
        return new SqlSugarRoleStore(Resolver, IdGenerator);
    }
```

- [ ] **Step 2: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RoleStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Roles;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 角色存储的角色读写测试
/// </summary>
public class RoleStoreTests
{
    /// <summary>
    /// 创建角色后可按标识与名称读回
    /// </summary>
    [Fact]
    public async Task 创建角色后可按标识与名称读回()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑", "可编辑内容")
        {
            IsDefault = true,
            IsStatic = true,
            Order = 5
        });

        var byId = await roles.GetRoleByIdAsync("r1");

        Assert.NotNull(byId);
        Assert.Equal("editor", byId.Name);
        Assert.Equal("编辑", byId.DisplayName);
        Assert.Equal("可编辑内容", byId.Description);
        Assert.True(byId.IsEnabled);
        Assert.True(byId.IsDefault);
        Assert.True(byId.IsStatic);
        Assert.Equal(5, byId.Order);
        Assert.Equal(DateTimeKind.Utc, byId.CreatedTime.Kind);
        Assert.True(byId.CreatedTime >= before);
        Assert.Null(byId.LastModifiedTime);

        var byName = await roles.GetRoleByNameAsync("editor");

        Assert.NotNull(byName);
        Assert.Equal("r1", byName.Id);
    }

    /// <summary>
    /// 创建角色时标识重复抛异常
    /// </summary>
    [Fact]
    public async Task 创建角色时标识重复抛异常()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roles.CreateRoleAsync(new RoleDefinition("r1", "viewer", "查看")));
    }

    /// <summary>
    /// 创建角色时名称重复抛异常
    /// </summary>
    [Fact]
    public async Task 创建角色时名称重复抛异常()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roles.CreateRoleAsync(new RoleDefinition("r2", "editor", "编辑")));
    }

    /// <summary>
    /// 创建角色的参数校验
    /// </summary>
    [Fact]
    public async Task 创建角色的参数校验()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => roles.CreateRoleAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => roles.CreateRoleAsync(new RoleDefinition("", "editor", "编辑")));
        await Assert.ThrowsAsync<ArgumentException>(() => roles.CreateRoleAsync(new RoleDefinition("r1", "", "编辑")));
    }

    /// <summary>
    /// 更新角色改写字段并记录修改时间，不改创建时间
    /// </summary>
    [Fact]
    public async Task 更新角色改写字段并记录修改时间且不改创建时间()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        var createdTime = (await roles.GetRoleByIdAsync("r1"))!.CreatedTime;

        var update = new RoleDefinition("r1", "writer", "写作者", "新描述")
        {
            IsEnabled = false,
            IsDefault = true,
            Order = 9
        };

        await roles.UpdateRoleAsync(update);

        Assert.NotNull(update.LastModifiedTime);

        var stored = await roles.GetRoleByIdAsync("r1");

        Assert.NotNull(stored);
        Assert.Equal("writer", stored.Name);
        Assert.Equal("写作者", stored.DisplayName);
        Assert.Equal("新描述", stored.Description);
        Assert.False(stored.IsEnabled);
        Assert.True(stored.IsDefault);
        Assert.Equal(9, stored.Order);
        Assert.NotNull(stored.LastModifiedTime);
        Assert.Equal(DateTimeKind.Utc, stored.LastModifiedTime.Value.Kind);
        Assert.Equal(createdTime, stored.CreatedTime);
        Assert.Null(await roles.GetRoleByNameAsync("editor"));
    }

    /// <summary>
    /// 更新不存在的角色抛异常
    /// </summary>
    [Fact]
    public async Task 更新不存在的角色抛异常()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roles.UpdateRoleAsync(new RoleDefinition("nope", "nope", "无")));
    }

    /// <summary>
    /// 改名与其他角色冲突时抛异常
    /// </summary>
    [Fact]
    public async Task 改名与其他角色冲突时抛异常()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.CreateRoleAsync(new RoleDefinition("r2", "viewer", "查看"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roles.UpdateRoleAsync(new RoleDefinition("r2", "editor", "查看")));
    }

    /// <summary>
    /// 更新角色的参数校验
    /// </summary>
    [Fact]
    public async Task 更新角色的参数校验()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => roles.UpdateRoleAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => roles.UpdateRoleAsync(new RoleDefinition("", "editor", "编辑")));
    }

    /// <summary>
    /// 读取全部角色按排序与名称排列
    /// </summary>
    [Fact]
    public async Task 读取全部角色按排序与名称排列()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "b", "乙") { Order = 1 });
        await roles.CreateRoleAsync(new RoleDefinition("r2", "a", "甲") { Order = 1 });
        await roles.CreateRoleAsync(new RoleDefinition("r3", "c", "丙") { Order = 0 });

        var names = (await roles.GetAllRolesAsync()).Select(role => role.Name).ToList();

        Assert.Equal(new[] { "c", "a", "b" }, names);
    }

    /// <summary>
    /// 空参数读取返回空值
    /// </summary>
    [Fact]
    public async Task 空参数读取返回空值()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        Assert.Null(await roles.GetRoleByIdAsync(""));
        Assert.Null(await roles.GetRoleByNameAsync(""));
        Assert.Empty(await roles.GetUserRolesAsync(""));
        Assert.False(await roles.IsInRoleAsync("", "editor"));
        Assert.False(await roles.IsInRoleAsync("u1", ""));
        Assert.Empty(await roles.GetUsersInRoleAsync(""));
    }
}
```

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/UserRoleTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 角色存储的用户角色关联测试
/// </summary>
public class UserRoleTests
{
    /// <summary>
    /// 加入不存在的角色抛异常
    /// </summary>
    [Fact]
    public async Task 加入不存在的角色抛异常()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.AddUserToRoleAsync("u1", "nope"));
    }

    /// <summary>
    /// 加入角色后可读回用户角色并判定在角色中
    /// </summary>
    [Fact]
    public async Task 加入角色后可读回用户角色并判定在角色中()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.AddUserToRoleAsync("u1", "editor");

        Assert.Equal("r1", Assert.Single(await roles.GetUserRolesAsync("u1")).Id);
        Assert.True(await roles.IsInRoleAsync("u1", "editor"));
        Assert.False(await roles.IsInRoleAsync("u2", "editor"));
    }

    /// <summary>
    /// 重复加入角色只保留一行
    /// </summary>
    [Fact]
    public async Task 重复加入角色只保留一行()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.AddUserToRoleAsync("u1", "editor");
        await roles.AddUserToRoleAsync("u1", "editor");

        Assert.Equal(1, await context.Client.Queryable<SysAuthzUserRole>().CountAsync());
    }

    /// <summary>
    /// 移出角色后不再在角色中，移出不存在的角色不抛异常
    /// </summary>
    [Fact]
    public async Task 移出角色后不再在角色中()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.AddUserToRoleAsync("u1", "editor");
        await roles.RemoveUserFromRoleAsync("u1", "editor");
        await roles.RemoveUserFromRoleAsync("u1", "nope");

        Assert.False(await roles.IsInRoleAsync("u1", "editor"));
        Assert.Empty(await roles.GetUserRolesAsync("u1"));
    }

    /// <summary>
    /// 读取角色中的用户
    /// </summary>
    [Fact]
    public async Task 读取角色中的用户()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.CreateRoleAsync(new RoleDefinition("r2", "viewer", "查看"));
        await roles.AddUserToRoleAsync("u2", "editor");
        await roles.AddUserToRoleAsync("u1", "editor");
        await roles.AddUserToRoleAsync("u3", "viewer");

        var userIds = (await roles.GetUsersInRoleAsync("editor")).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "u1", "u2" }, userIds);
    }

    /// <summary>
    /// 角色改名后用户仍在角色中并保有角色权限
    /// </summary>
    [Fact]
    public async Task 角色改名后用户仍在角色中并保有角色权限()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();
        var permissions = context.CreatePermissionStore();

        await permissions.AddOrUpdatePermissionAsync(new PermissionDefinition("P1", "一"));
        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await permissions.GrantPermissionToRoleAsync("r1", "P1");
        await roles.AddUserToRoleAsync("u1", "editor");

        await roles.UpdateRoleAsync(new RoleDefinition("r1", "writer", "写作者"));

        Assert.True(await roles.IsInRoleAsync("u1", "writer"));
        Assert.False(await roles.IsInRoleAsync("u1", "editor"));

        var role = Assert.Single(await roles.GetUserRolesAsync("u1"));

        Assert.Equal("P1", Assert.Single(await permissions.GetRolePermissionsAsync(role.Id)).Name);
    }

    /// <summary>
    /// 判定在角色中与读取用户角色都不看角色是否启用
    /// </summary>
    [Fact]
    public async Task 判定在角色中与读取用户角色都不看角色是否启用()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "ghost", "停用") { IsEnabled = false });
        await roles.AddUserToRoleAsync("u1", "ghost");

        Assert.True(await roles.IsInRoleAsync("u1", "ghost"));
        Assert.False(Assert.Single(await roles.GetUserRolesAsync("u1")).IsEnabled);
    }

    /// <summary>
    /// 关联行与角色行租户不一致时不配对
    /// </summary>
    [Fact]
    public async Task 关联行与角色行租户不一致时不配对()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await context.Client.Insertable(new SysAuthzRole(context.IdGenerator.NextId())
        {
            TenantId = 2,
            RoleId = "r9",
            RoleName = "admin",
            DisplayName = "admin",
            IsEnabled = true,
            CreatedTime = DateTime.UtcNow
        }).ExecuteCommandAsync();

        await context.Client.Insertable(new SysAuthzUserRole(context.IdGenerator.NextId())
        {
            TenantId = 1,
            UserId = "u9",
            RoleId = "r9"
        }).ExecuteCommandAsync();

        Assert.Empty(await roles.GetUserRolesAsync("u9"));
        Assert.False(await roles.IsInRoleAsync("u9", "admin"));
        Assert.Empty(await roles.GetUsersInRoleAsync("admin"));
    }
}
```

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RoleDeletionTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 删除角色的级联与事务测试
/// </summary>
public class RoleDeletionTests
{
    /// <summary>
    /// 删除角色时一并删除其用户关联与角色权限，不动其他角色
    /// </summary>
    [Fact]
    public async Task 删除角色时一并删除用户关联与角色权限()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();
        var permissions = context.CreatePermissionStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.CreateRoleAsync(new RoleDefinition("r2", "viewer", "查看"));
        await roles.AddUserToRoleAsync("u1", "editor");
        await roles.AddUserToRoleAsync("u2", "editor");
        await roles.AddUserToRoleAsync("u1", "viewer");
        await permissions.GrantPermissionToRoleAsync("r1", "P1");
        await permissions.GrantPermissionToRoleAsync("r2", "P2");

        await roles.DeleteRoleAsync("r1");

        Assert.Null(await roles.GetRoleByIdAsync("r1"));
        Assert.NotNull(await roles.GetRoleByIdAsync("r2"));

        var remainingMember = Assert.Single(await context.Client.Queryable<SysAuthzUserRole>().ToListAsync());
        Assert.Equal("r2", remainingMember.RoleId);

        var remainingGrant = Assert.Single(await context.Client.Queryable<SysAuthzRolePermission>().ToListAsync());
        Assert.Equal("r2", remainingGrant.RoleId);
    }

    /// <summary>
    /// 删除后以同一标识重建角色不继承旧授权
    /// </summary>
    [Fact]
    public async Task 删除后以同一标识重建角色不继承旧授权()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();
        var permissions = context.CreatePermissionStore();

        await permissions.AddOrUpdatePermissionAsync(new PermissionDefinition("P1", "一"));
        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.AddUserToRoleAsync("u1", "editor");
        await permissions.GrantPermissionToRoleAsync("r1", "P1");

        await roles.DeleteRoleAsync("r1");
        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));

        Assert.Empty(await permissions.GetRolePermissionsAsync("r1"));
        Assert.False(await roles.IsInRoleAsync("u1", "editor"));
    }

    /// <summary>
    /// 删除不存在或空标识的角色不抛异常
    /// </summary>
    [Fact]
    public async Task 删除不存在或空标识的角色不抛异常()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.DeleteRoleAsync("nope");
        await roles.DeleteRoleAsync("");

        Assert.Empty(await roles.GetAllRolesAsync());
    }

    /// <summary>
    /// 静态角色同样可以删除
    /// </summary>
    [Fact]
    public async Task 静态角色同样可以删除()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "admin", "管理员") { IsStatic = true });
        await roles.DeleteRoleAsync("r1");

        Assert.Null(await roles.GetRoleByIdAsync("r1"));
    }

    /// <summary>
    /// 级联删除中途失败时整体回滚并抛出
    /// </summary>
    [Fact]
    public async Task 级联删除中途失败时整体回滚并抛出()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.AddUserToRoleAsync("u1", "editor");

        context.Client.DbMaintenance.DropTable("sys_authz_role_permission");

        await Assert.ThrowsAnyAsync<Exception>(() => roles.DeleteRoleAsync("r1"));

        Assert.NotNull(await roles.GetRoleByIdAsync("r1"));
        Assert.Equal(1, await context.Client.Queryable<SysAuthzUserRole>().CountAsync());
    }

    /// <summary>
    /// 外层事务回滚时删除一并回滚
    /// </summary>
    [Fact]
    public async Task 外层事务回滚时删除一并回滚()
    {
        using var context = new AuthorizationTestContext();
        var roles = context.CreateRoleStore();

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.AddUserToRoleAsync("u1", "editor");

        context.Client.Ado.BeginTran();
        await roles.DeleteRoleAsync("r1");
        context.Client.Ado.RollbackTran();

        Assert.NotNull(await roles.GetRoleByIdAsync("r1"));
        Assert.Equal(1, await context.Client.Queryable<SysAuthzUserRole>().CountAsync());
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SqlSugarRoleStore`（夹具的 `CreateRoleStore` 引用了它）。

- [ ] **Step 4: 实现角色存储**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Roles/SqlSugarRoleStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.ExceptionServices;
using SqlSugar;
using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Mapping;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Authorization.SqlSugar.Roles;

/// <summary>
/// 角色存储的 SqlSugar 实现
/// </summary>
public class SqlSugarRoleStore : IRoleStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarRoleStore(
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
    /// 获取用户的角色列表
    /// </summary>
    /// <remarks>
    /// 含已禁用的角色，按排序值与名称排列。
    /// </remarks>
    /// <param name="userId">用户ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>角色列表</returns>
    public async Task<List<RoleDefinition>> GetUserRolesAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return [];
        }

        var entities = await Client.Queryable<SysAuthzUserRole>()
            .InnerJoin<SysAuthzRole>((member, role) => member.TenantId == role.TenantId && member.RoleId == role.RoleId)
            .Where((member, role) => member.UserId == userId)
            .Select((member, role) => role)
            .ToListAsync(cancellationToken);

        return ToOrderedDefinitions(entities);
    }

    /// <summary>
    /// 检查用户是否在指定角色中
    /// </summary>
    /// <remarks>
    /// 不检查角色是否启用。
    /// </remarks>
    /// <param name="userId">用户ID</param>
    /// <param name="roleName">角色名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否在角色中</returns>
    public async Task<bool> IsInRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleName))
        {
            return false;
        }

        return await Client.Queryable<SysAuthzUserRole>()
            .InnerJoin<SysAuthzRole>((member, role) => member.TenantId == role.TenantId && member.RoleId == role.RoleId)
            .Where((member, role) => member.UserId == userId && role.RoleName == roleName)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// 将用户添加到角色
    /// </summary>
    /// <remarks>
    /// 已在角色中时不重复写入；关联记录保存角色标识。
    /// </remarks>
    /// <param name="userId">用户ID</param>
    /// <param name="roleName">角色名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="InvalidOperationException">角色不存在</exception>
    public async Task AddUserToRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleName))
        {
            return;
        }

        var client = Client;
        var role = await client.Queryable<SysAuthzRole>()
            .FirstAsync(item => item.RoleName == roleName, cancellationToken)
            ?? throw new InvalidOperationException($"角色 '{roleName}' 不存在");

        var joined = await client.Queryable<SysAuthzUserRole>()
            .AnyAsync(member => member.UserId == userId && member.RoleId == role.RoleId, cancellationToken);

        if (joined)
        {
            return;
        }

        var entity = new SysAuthzUserRole(_idGenerator.NextId())
        {
            UserId = userId,
            RoleId = role.RoleId
        };

        await client.Insertable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 从角色中移除用户
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="roleName">角色名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RemoveUserFromRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleName))
        {
            return;
        }

        var client = Client;
        var role = await client.Queryable<SysAuthzRole>()
            .FirstAsync(item => item.RoleName == roleName, cancellationToken);

        if (role is null)
        {
            return;
        }

        await client.Deleteable<SysAuthzUserRole>()
            .Where(member => member.UserId == userId && member.RoleId == role.RoleId)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 获取所有角色
    /// </summary>
    /// <remarks>
    /// 按排序值升序、同排序值按名称序数升序返回。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>角色列表</returns>
    public async Task<List<RoleDefinition>> GetAllRolesAsync(CancellationToken cancellationToken = default)
    {
        var entities = await Client.Queryable<SysAuthzRole>().ToListAsync(cancellationToken);

        return ToOrderedDefinitions(entities);
    }

    /// <summary>
    /// 根据名称获取角色
    /// </summary>
    /// <param name="roleName">角色名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>角色定义，不存在时返回空</returns>
    public async Task<RoleDefinition?> GetRoleByNameAsync(string roleName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleName))
        {
            return null;
        }

        var entity = await Client.Queryable<SysAuthzRole>()
            .FirstAsync(item => item.RoleName == roleName, cancellationToken);

        return entity is null ? null : RoleMapper.ToDefinition(entity);
    }

    /// <summary>
    /// 根据ID获取角色
    /// </summary>
    /// <param name="roleId">角色ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>角色定义，不存在时返回空</returns>
    public async Task<RoleDefinition?> GetRoleByIdAsync(string roleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleId))
        {
            return null;
        }

        var entity = await Client.Queryable<SysAuthzRole>()
            .FirstAsync(item => item.RoleId == roleId, cancellationToken);

        return entity is null ? null : RoleMapper.ToDefinition(entity);
    }

    /// <summary>
    /// 创建角色
    /// </summary>
    /// <param name="role">角色定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ArgumentNullException">角色定义为空</exception>
    /// <exception cref="ArgumentException">角色ID或名称为空</exception>
    /// <exception cref="InvalidOperationException">角色ID或名称已存在</exception>
    public async Task CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (string.IsNullOrEmpty(role.Id))
        {
            throw new ArgumentException("角色ID不能为空", nameof(role));
        }

        if (string.IsNullOrEmpty(role.Name))
        {
            throw new ArgumentException("角色名称不能为空", nameof(role));
        }

        var client = Client;

        if (await client.Queryable<SysAuthzRole>().AnyAsync(item => item.RoleId == role.Id, cancellationToken))
        {
            throw new InvalidOperationException($"角色ID '{role.Id}' 已存在");
        }

        if (await client.Queryable<SysAuthzRole>().AnyAsync(item => item.RoleName == role.Name, cancellationToken))
        {
            throw new InvalidOperationException($"角色名称 '{role.Name}' 已存在");
        }

        await client.Insertable(RoleMapper.ToEntity(role, _idGenerator.NextId())).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 更新角色
    /// </summary>
    /// <remarks>
    /// 按角色ID匹配，改写角色ID与创建时间以外的全部字段，并把传入对象的最后修改时间设为当前 UTC 时间。
    /// </remarks>
    /// <param name="role">角色定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ArgumentNullException">角色定义为空</exception>
    /// <exception cref="ArgumentException">角色ID为空</exception>
    /// <exception cref="InvalidOperationException">角色不存在，或新名称已被其他角色使用</exception>
    public async Task UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (string.IsNullOrEmpty(role.Id))
        {
            throw new ArgumentException("角色ID不能为空", nameof(role));
        }

        var client = Client;
        var existing = await client.Queryable<SysAuthzRole>()
            .FirstAsync(item => item.RoleId == role.Id, cancellationToken)
            ?? throw new InvalidOperationException($"角色ID '{role.Id}' 不存在");

        if (!string.Equals(existing.RoleName, role.Name, StringComparison.Ordinal))
        {
            var taken = await client.Queryable<SysAuthzRole>()
                .AnyAsync(item => item.RoleName == role.Name && item.BasicId != existing.BasicId, cancellationToken);

            if (taken)
            {
                throw new InvalidOperationException($"角色名称 '{role.Name}' 已被其他角色使用");
            }
        }

        role.LastModifiedTime = DateTime.UtcNow;
        var updated = RoleMapper.ToEntity(role, existing.BasicId);

        await client.Updateable<SysAuthzRole>()
            .SetColumns(item => new SysAuthzRole
            {
                RoleName = updated.RoleName,
                DisplayName = updated.DisplayName,
                Description = updated.Description,
                IsEnabled = updated.IsEnabled,
                IsDefault = updated.IsDefault,
                IsStatic = updated.IsStatic,
                SortOrder = updated.SortOrder,
                LastModifiedTime = updated.LastModifiedTime,
                Properties = updated.Properties
            })
            .Where(item => item.BasicId == existing.BasicId)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 删除角色
    /// </summary>
    /// <remarks>
    /// 在同一事务内依次删除该角色的用户关联、角色权限与角色本身；当前已在事务中时并入该事务。
    /// </remarks>
    /// <param name="roleId">角色ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task DeleteRoleAsync(string roleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleId))
        {
            return;
        }

        var client = Client;
        var role = await client.Queryable<SysAuthzRole>()
            .FirstAsync(item => item.RoleId == roleId, cancellationToken);

        if (role is null)
        {
            return;
        }

        await ExecuteInTransactionAsync(client, async () =>
        {
            await client.Deleteable<SysAuthzUserRole>()
                .Where(member => member.TenantId == role.TenantId && member.RoleId == role.RoleId)
                .ExecuteCommandAsync(cancellationToken);

            await client.Deleteable<SysAuthzRolePermission>()
                .Where(grant => grant.TenantId == role.TenantId && grant.RoleId == role.RoleId)
                .ExecuteCommandAsync(cancellationToken);

            await client.Deleteable<SysAuthzRole>()
                .Where(item => item.BasicId == role.BasicId)
                .ExecuteCommandAsync(cancellationToken);
        });
    }

    /// <summary>
    /// 获取角色中的用户ID列表
    /// </summary>
    /// <param name="roleName">角色名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>去重后的用户ID列表</returns>
    public async Task<List<string>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleName))
        {
            return [];
        }

        var userIds = await Client.Queryable<SysAuthzRole>()
            .InnerJoin<SysAuthzUserRole>((role, member) => member.TenantId == role.TenantId && member.RoleId == role.RoleId)
            .Where((role, member) => role.RoleName == roleName)
            .Select((role, member) => member.UserId)
            .ToListAsync(cancellationToken);

        return [.. userIds.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 按排序值与名称排列并映射为角色定义
    /// </summary>
    /// <param name="entities">角色实体</param>
    /// <returns>角色定义列表</returns>
    private static List<RoleDefinition> ToOrderedDefinitions(List<SysAuthzRole> entities)
    {
        return
        [
            .. entities
                .OrderBy(entity => entity.SortOrder)
                .ThenBy(entity => entity.RoleName, StringComparer.Ordinal)
                .Select(RoleMapper.ToDefinition)
        ];
    }

    /// <summary>
    /// 在事务内执行；已在事务中时直接执行
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="action">要执行的操作</param>
    private static async Task ExecuteInTransactionAsync(ISqlSugarClient client, Func<Task> action)
    {
        if (!client.Ado.IsNoTran())
        {
            await action();
            return;
        }

        var result = await client.Ado.UseTranAsync(action);

        if (!result.IsSuccess)
        {
            ExceptionDispatchInfo.Capture(result.ErrorException).Throw();
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

**若 `外层事务回滚时删除一并回滚` 失败**（回滚后角色没了），说明 `ExecuteInTransactionAsync` 在已有事务时仍走了 `UseTranAsync`，把外层事务提交了（硬约束 ③）。**若 `级联删除中途失败时整体回滚并抛出` 失败于「没有抛异常」**，说明漏了 `IsSuccess` 检查。两处都改实现，不改断言。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 实现角色存储，删除角色时同事务级联"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 权限检查器

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Permissions/SqlSugarPermissionChecker.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PermissionCheckerTests.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/TenantFilterTests.cs`

**Interfaces:**
- Consumes: ① ② 的五个实体；主包 `IPermissionChecker`、`DefaultPermissionChecker`（测试对照用）
- Produces:
  - `public class SqlSugarPermissionChecker : IPermissionChecker`，构造函数 `(ISqlSugarClientResolver clientResolver)`，契约的 5 个方法
  - 夹具新增 `SqlSugarPermissionChecker CreatePermissionChecker()`

**参考来源（动手前先读）：**
- 判定语义基准：`framework/src/XiHan.Framework.Authorization/Permissions/DefaultPermissionChecker.cs`（spec §1.2 下半表）
- 调用方与次数：spec §4.1 的表
- 四表联表的签名：`E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Interface/IQueryable.cs:476-700`

**本任务禁止事项：** 硬约束 ①②④⑥。不要在检查器里注入或调用 `IPermissionStore` / `IRoleStore`。不要加缓存、不要在实例上存任何状态。不要用 `UnionAll`。

- [ ] **Step 1: 夹具加工厂方法**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`，在 `CreateRoleStore` 方法之后插入（`XiHan.Framework.Authorization.SqlSugar.Permissions` 的 using 已在 ① 加过）：

```csharp
    /// <summary>
    /// 创建权限检查器
    /// </summary>
    /// <returns>权限检查器</returns>
    public SqlSugarPermissionChecker CreatePermissionChecker()
    {
        return new SqlSugarPermissionChecker(Resolver);
    }
```

- [ ] **Step 2: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PermissionCheckerTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Roles;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 权限检查器测试
/// </summary>
/// <remarks>
/// 造数：u1 在启用角色 editor（授予 P1、P5）与禁用角色 ghost（授予 P2）中，直接授予 P3、P4；
/// P4、P5 的定义已禁用；P6 有定义但未授予；PX 无定义。
/// </remarks>
public class PermissionCheckerTests
{
    /// <summary>
    /// 判定单个权限
    /// </summary>
    /// <param name="permissionName">权限名称</param>
    /// <param name="expected">期望结果</param>
    [Theory]
    [InlineData("P1", true)]
    [InlineData("P2", false)]
    [InlineData("P3", true)]
    [InlineData("P4", false)]
    [InlineData("P5", false)]
    [InlineData("P6", false)]
    [InlineData("PX", false)]
    public async Task 判定单个权限(string permissionName, bool expected)
    {
        using var context = new AuthorizationTestContext();
        await SeedAsync(context);

        Assert.Equal(expected, await context.CreatePermissionChecker().IsGrantedAsync("u1", permissionName));
    }

    /// <summary>
    /// 与默认检查器在同一份数据上的判定一致
    /// </summary>
    [Fact]
    public async Task 与默认检查器判定一致()
    {
        using var context = new AuthorizationTestContext();
        await SeedAsync(context);

        var expected = new DefaultPermissionChecker(context.CreatePermissionStore(), context.CreateRoleStore());
        var actual = context.CreatePermissionChecker();

        foreach (var name in new[] { "P1", "P2", "P3", "P4", "P5", "P6", "PX" })
        {
            Assert.Equal(await expected.IsGrantedAsync("u1", name), await actual.IsGrantedAsync("u1", name));
        }

        Assert.Equal(
            (await expected.GetGrantedPermissionsAsync("u1")).Order(StringComparer.Ordinal),
            (await actual.GetGrantedPermissionsAsync("u1")).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 任意一个与全部权限的判定
    /// </summary>
    [Fact]
    public async Task 任意一个与全部权限的判定()
    {
        using var context = new AuthorizationTestContext();
        await SeedAsync(context);
        var checker = context.CreatePermissionChecker();

        Assert.True(await checker.IsAnyGrantedAsync("u1", ["P2", "P1"]));
        Assert.False(await checker.IsAnyGrantedAsync("u1", ["P2", "P6"]));
        Assert.False(await checker.IsAnyGrantedAsync("u1", []));

        Assert.True(await checker.IsAllGrantedAsync("u1", ["P1", "P3"]));
        Assert.False(await checker.IsAllGrantedAsync("u1", ["P1", "P2"]));
        Assert.False(await checker.IsAllGrantedAsync("u1", ["P1", ""]));
        Assert.False(await checker.IsAllGrantedAsync("u1", []));
    }

    /// <summary>
    /// 获取已授予权限合并直接与角色授予且去重
    /// </summary>
    [Fact]
    public async Task 获取已授予权限合并直接与角色授予且去重()
    {
        using var context = new AuthorizationTestContext();
        var (permissions, _) = await SeedAsync(context);
        await permissions.GrantPermissionToUserAsync("u1", "P1");

        var granted = (await context.CreatePermissionChecker().GetGrantedPermissionsAsync("u1"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "P1", "P3" }, granted);
    }

    /// <summary>
    /// 空用户或空权限一律判定为无
    /// </summary>
    [Fact]
    public async Task 空用户或空权限一律判定为无()
    {
        using var context = new AuthorizationTestContext();
        await SeedAsync(context);
        var checker = context.CreatePermissionChecker();

        Assert.False(await checker.IsGrantedAsync("", "P1"));
        Assert.False(await checker.IsGrantedAsync("u1", ""));
        Assert.False(await checker.IsAnyGrantedAsync("", ["P1"]));
        Assert.False(await checker.IsAllGrantedAsync("", ["P1"]));
        Assert.Empty(await checker.GetGrantedPermissionsAsync(""));
    }

    /// <summary>
    /// 权限是否存在不看启用状态
    /// </summary>
    [Fact]
    public async Task 权限是否存在不看启用状态()
    {
        using var context = new AuthorizationTestContext();
        await SeedAsync(context);
        var checker = context.CreatePermissionChecker();

        Assert.True(await checker.PermissionExistsAsync("P4"));
        Assert.False(await checker.PermissionExistsAsync("PX"));
        Assert.False(await checker.PermissionExistsAsync(""));
    }

    /// <summary>
    /// 直接授予命中时只执行一条 SQL
    /// </summary>
    [Fact]
    public async Task 直接授予命中时只执行一条SQL()
    {
        using var context = new AuthorizationTestContext();
        await SeedAsync(context);
        var checker = context.CreatePermissionChecker();

        var sqlCount = 0;
        context.Client.Aop.OnLogExecuting = (_, _) => sqlCount++;

        Assert.True(await checker.IsGrantedAsync("u1", "P3"));
        Assert.Equal(1, sqlCount);
    }

    /// <summary>
    /// 判定与角色数、权限数无关，至多执行两条 SQL
    /// </summary>
    [Fact]
    public async Task 判定与角色数权限数无关至多执行两条SQL()
    {
        using var context = new AuthorizationTestContext();
        var (permissions, roles) = await SeedAsync(context);

        for (var index = 0; index < 5; index++)
        {
            await roles.CreateRoleAsync(new RoleDefinition($"extra{index}", $"extra{index}", "附加"));
            await roles.AddUserToRoleAsync("u1", $"extra{index}");
            await permissions.GrantPermissionToRoleAsync($"extra{index}", "P6");
        }

        var checker = context.CreatePermissionChecker();
        var sqlCount = 0;
        context.Client.Aop.OnLogExecuting = (_, _) => sqlCount++;

        Assert.True(await checker.IsGrantedAsync("u1", "P1"));
        Assert.Equal(2, sqlCount);

        sqlCount = 0;
        Assert.True(await checker.IsAllGrantedAsync("u1", ["P1", "P3", "P6"]));
        Assert.Equal(2, sqlCount);

        sqlCount = 0;
        await checker.GetGrantedPermissionsAsync("u1");
        Assert.Equal(2, sqlCount);
    }

    /// <summary>
    /// 关联行、角色行、角色权限行租户不一致时不授予
    /// </summary>
    [Fact]
    public async Task 关联行与角色行租户不一致时不授予()
    {
        using var context = new AuthorizationTestContext();
        var checker = context.CreatePermissionChecker();

        await InsertPermissionAsync(context, "T1");
        await InsertPermissionAsync(context, "T2");

        await InsertRoleAsync(context, 2, "r9");
        await InsertUserRoleAsync(context, 1, "u9", "r9");
        await InsertRolePermissionAsync(context, 2, "r9", "T1");

        await InsertRoleAsync(context, 1, "r8");
        await InsertUserRoleAsync(context, 1, "u8", "r8");
        await InsertRolePermissionAsync(context, 2, "r8", "T1");
        await InsertRolePermissionAsync(context, 1, "r8", "T2");

        Assert.False(await checker.IsGrantedAsync("u9", "T1"));
        Assert.False(await checker.IsGrantedAsync("u8", "T1"));
        Assert.True(await checker.IsGrantedAsync("u8", "T2"));
    }

    /// <summary>
    /// 造数
    /// </summary>
    private static async Task<(SqlSugarPermissionStore Permissions, SqlSugarRoleStore Roles)> SeedAsync(AuthorizationTestContext context)
    {
        var permissions = context.CreatePermissionStore();
        var roles = context.CreateRoleStore();

        await permissions.AddPermissionsAsync(
        [
            new PermissionDefinition("P1", "经启用角色"),
            new PermissionDefinition("P2", "经禁用角色"),
            new PermissionDefinition("P3", "直接授予"),
            new PermissionDefinition("P4", "直接授予但定义禁用") { IsEnabled = false },
            new PermissionDefinition("P5", "经启用角色但定义禁用") { IsEnabled = false },
            new PermissionDefinition("P6", "未授予")
        ]);

        await roles.CreateRoleAsync(new RoleDefinition("r1", "editor", "编辑"));
        await roles.CreateRoleAsync(new RoleDefinition("r2", "ghost", "停用") { IsEnabled = false });
        await roles.AddUserToRoleAsync("u1", "editor");
        await roles.AddUserToRoleAsync("u1", "ghost");

        await permissions.GrantPermissionToRoleAsync("r1", "P1");
        await permissions.GrantPermissionToRoleAsync("r1", "P5");
        await permissions.GrantPermissionToRoleAsync("r2", "P2");
        await permissions.GrantPermissionToUserAsync("u1", "P3");
        await permissions.GrantPermissionToUserAsync("u1", "P4");

        return (permissions, roles);
    }

    private static async Task InsertPermissionAsync(AuthorizationTestContext context, string name)
    {
        await context.Client.Insertable(new SysAuthzPermission(context.IdGenerator.NextId())
        {
            PermissionName = name,
            DisplayName = name,
            IsEnabled = true
        }).ExecuteCommandAsync();
    }

    private static async Task InsertRoleAsync(AuthorizationTestContext context, long tenantId, string roleId)
    {
        await context.Client.Insertable(new SysAuthzRole(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            RoleId = roleId,
            RoleName = roleId,
            DisplayName = roleId,
            IsEnabled = true,
            CreatedTime = DateTime.UtcNow
        }).ExecuteCommandAsync();
    }

    private static async Task InsertUserRoleAsync(AuthorizationTestContext context, long tenantId, string userId, string roleId)
    {
        await context.Client.Insertable(new SysAuthzUserRole(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            UserId = userId,
            RoleId = roleId
        }).ExecuteCommandAsync();
    }

    private static async Task InsertRolePermissionAsync(AuthorizationTestContext context, long tenantId, string roleId, string permissionName)
    {
        await context.Client.Insertable(new SysAuthzRolePermission(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            RoleId = roleId,
            PermissionName = permissionName
        }).ExecuteCommandAsync();
    }
}
```

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/TenantFilterTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 严格租户过滤器下的隔离测试
/// </summary>
public class TenantFilterTests
{
    /// <summary>
    /// 开启严格租户过滤时只见本租户的角色与授权
    /// </summary>
    [Fact]
    public async Task 开启严格租户过滤时只见本租户的角色与授权()
    {
        using var context = new AuthorizationTestContext();

        foreach (var name in new[] { "P1", "P2" })
        {
            await context.Client.Insertable(new SysAuthzPermission(context.IdGenerator.NextId())
            {
                PermissionName = name,
                DisplayName = name,
                IsEnabled = true
            }).ExecuteCommandAsync();
        }

        foreach (var (tenantId, permissionName) in new[] { (5L, "P1"), (6L, "P2") })
        {
            await context.Client.Insertable(new SysAuthzRole(context.IdGenerator.NextId())
            {
                TenantId = tenantId,
                RoleId = "r1",
                RoleName = "admin",
                DisplayName = "管理员",
                IsEnabled = true,
                CreatedTime = DateTime.UtcNow
            }).ExecuteCommandAsync();

            await context.Client.Insertable(new SysAuthzUserRole(context.IdGenerator.NextId())
            {
                TenantId = tenantId,
                UserId = "u1",
                RoleId = "r1"
            }).ExecuteCommandAsync();

            await context.Client.Insertable(new SysAuthzRolePermission(context.IdGenerator.NextId())
            {
                TenantId = tenantId,
                RoleId = "r1",
                PermissionName = permissionName
            }).ExecuteCommandAsync();
        }

        context.Client.QueryFilter.AddTableFilter<IStrictMultiTenantEntity>(entity => entity.TenantId == 5);

        var roles = context.CreateRoleStore();
        var checker = context.CreatePermissionChecker();

        Assert.Single(await roles.GetAllRolesAsync());
        Assert.NotNull(await roles.GetRoleByNameAsync("admin"));
        Assert.Single(await roles.GetUserRolesAsync("u1"));
        Assert.Equal(new[] { "u1" }, await roles.GetUsersInRoleAsync("admin"));
        Assert.True(await checker.IsGrantedAsync("u1", "P1"));
        Assert.False(await checker.IsGrantedAsync("u1", "P2"));
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SqlSugarPermissionChecker`（夹具的 `CreatePermissionChecker` 引用了它）。

- [ ] **Step 4: 实现权限检查器**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Permissions/SqlSugarPermissionChecker.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Clients;

namespace XiHan.Framework.Authorization.SqlSugar.Permissions;

/// <summary>
/// 权限检查器的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 直接查询授权表：一次判定先查用户直接授予的启用权限，未全部命中时再查经启用角色授予的启用权限。
/// </remarks>
public class SqlSugarPermissionChecker : IPermissionChecker
{
    private readonly ISqlSugarClientResolver _clientResolver;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    public SqlSugarPermissionChecker(ISqlSugarClientResolver clientResolver)
    {
        _clientResolver = clientResolver;
    }

    /// <summary>
    /// 当前租户主库的客户端
    /// </summary>
    private ISqlSugarClient Client => _clientResolver.GetCurrentClient();

    /// <summary>
    /// 检查是否有指定权限
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否有权限</returns>
    public async Task<bool> IsGrantedAsync(string userId, string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(permissionName))
        {
            return false;
        }

        var granted = await GetGrantedNamesAsync(userId, [permissionName], cancellationToken);

        return granted.Contains(permissionName);
    }

    /// <summary>
    /// 检查是否有任意一个权限
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="permissionNames">权限名称列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否有任意一个权限</returns>
    public async Task<bool> IsAnyGrantedAsync(string userId, List<string> permissionNames, CancellationToken cancellationToken = default)
    {
        var names = permissionNames.ToList();
        if (names.Count == 0 || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var candidates = ToCandidates(names);
        if (candidates.Count == 0)
        {
            return false;
        }

        var granted = await GetGrantedNamesAsync(userId, candidates, cancellationToken);

        return names.Any(granted.Contains);
    }

    /// <summary>
    /// 检查是否有所有权限
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="permissionNames">权限名称列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否有所有权限；列表为空或含空名称时返回 false</returns>
    public async Task<bool> IsAllGrantedAsync(string userId, List<string> permissionNames, CancellationToken cancellationToken = default)
    {
        var names = permissionNames.ToList();
        if (names.Count == 0 || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var candidates = ToCandidates(names);
        if (candidates.Count == 0)
        {
            return false;
        }

        var granted = await GetGrantedNamesAsync(userId, candidates, cancellationToken);

        return names.All(granted.Contains);
    }

    /// <summary>
    /// 获取用户的所有权限
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>直接授予与经启用角色授予的启用权限名称，已去重</returns>
    public async Task<List<string>> GetGrantedPermissionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return [];
        }

        var granted = await GetGrantedNamesAsync(userId, null, cancellationToken);

        return [.. granted];
    }

    /// <summary>
    /// 检查权限是否存在
    /// </summary>
    /// <remarks>
    /// 有权限定义即视为存在，不检查启用状态。
    /// </remarks>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否存在</returns>
    public async Task<bool> PermissionExistsAsync(string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(permissionName))
        {
            return false;
        }

        return await Client.Queryable<SysAuthzPermission>()
            .AnyAsync(permission => permission.PermissionName == permissionName, cancellationToken);
    }

    /// <summary>
    /// 去掉空名称并去重
    /// </summary>
    /// <param name="names">权限名称</param>
    /// <returns>候选权限名称</returns>
    private static List<string> ToCandidates(List<string> names)
    {
        return [.. names.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 查询用户已被授予的启用权限名称
    /// </summary>
    /// <remarks>
    /// 先查直接授予；候选不为空且已全部命中时直接返回，否则再查经启用角色授予的部分。
    /// </remarks>
    /// <param name="userId">用户ID</param>
    /// <param name="candidates">只查这些权限，为空表示不限</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>已授予的权限名称</returns>
    private async Task<HashSet<string>> GetGrantedNamesAsync(string userId, List<string>? candidates, CancellationToken cancellationToken)
    {
        var client = Client;

        var directQuery = client.Queryable<SysAuthzUserPermission>()
            .InnerJoin<SysAuthzPermission>((grant, permission) => permission.PermissionName == grant.PermissionName)
            .Where((grant, permission) => grant.UserId == userId && permission.IsEnabled);

        var roleQuery = client.Queryable<SysAuthzUserRole>()
            .InnerJoin<SysAuthzRole>((member, role) => role.TenantId == member.TenantId && role.RoleId == member.RoleId)
            .InnerJoin<SysAuthzRolePermission>((member, role, grant) => grant.TenantId == role.TenantId && grant.RoleId == role.RoleId)
            .InnerJoin<SysAuthzPermission>((member, role, grant, permission) => permission.PermissionName == grant.PermissionName)
            .Where((member, role, grant, permission) => member.UserId == userId && role.IsEnabled && permission.IsEnabled);

        if (candidates is not null)
        {
            List<string> names = candidates;

            directQuery = directQuery.Where((grant, permission) => names.Contains(permission.PermissionName));
            roleQuery = roleQuery.Where((member, role, grant, permission) => names.Contains(permission.PermissionName));
        }

        var directNames = await directQuery
            .Select((grant, permission) => permission.PermissionName)
            .ToListAsync(cancellationToken);

        var granted = new HashSet<string>(directNames, StringComparer.Ordinal);

        if (candidates is not null && candidates.All(granted.Contains))
        {
            return granted;
        }

        var roleNames = await roleQuery
            .Select((member, role, grant, permission) => permission.PermissionName)
            .ToListAsync(cancellationToken);

        granted.UnionWith(roleNames);

        return granted;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- `判定单个权限` 的 P2 或 P5 失败 → 漏了 `role.IsEnabled` 或 `permission.IsEnabled`（硬约束 ②）
- `关联行与角色行租户不一致时不授予` 失败 → 联表漏了 `TenantId` 相等（硬约束 ④）
- 数 SQL 的用例多出条数 → 检查是否在检查器里调了存储、或对每个角色单独查询（硬约束 ①）

**一律改实现，不改断言。**

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 新增 SqlSugar 权限检查器，每次判定至多两次查询"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 5: 注册

**Files:**
- Modify: `framework/src/XiHan.Framework.Authorization.SqlSugar/Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarRoleStore`、Task 4 的 `SqlSugarPermissionChecker`；① 的注册扩展
- Produces: `AddXiHanAuthorizationSqlSugar` 另以 `Replace` 注册 `IRoleStore`、`IPermissionChecker`

**参考来源（动手前先读）：** 主包 `XiHanAuthorizationServiceCollectionExtensions.cs:30-34`（确认两者都是 `TryAddScoped`）。

**本任务禁止事项：** 不要用 `TryAdd`。不要改 ① 已有的两行。模块类不动。

- [ ] **Step 1: 写失败的测试**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RegistrationTests.cs`。

using 区追加：

```csharp
using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Roles;
```

把 `契约被顶替为SqlSugar实现` 的特性改为：

```csharp
    [Theory]
    [InlineData(typeof(IPermissionStore), typeof(SqlSugarPermissionStore))]
    [InlineData(typeof(IRoleStore), typeof(SqlSugarRoleStore))]
    [InlineData(typeof(IPermissionChecker), typeof(SqlSugarPermissionChecker))]
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：`契约被顶替为SqlSugar实现` 的两条新数据失败，`Assert.Equal` 报期望 `SqlSugarRoleStore` / `SqlSugarPermissionChecker`、实际 `DefaultRoleStore` / `DefaultPermissionChecker`。

- [ ] **Step 3: 追加注册**

修改 `framework/src/XiHan.Framework.Authorization.SqlSugar/Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs`。

using 区追加：

```csharp
using XiHan.Framework.Authorization.Roles;
using XiHan.Framework.Authorization.SqlSugar.Roles;
```

在 `services.TryAddScoped<SqlSugarPermissionStore>();` 之后插入：

```csharp
        services.Replace(ServiceDescriptor.Scoped<IRoleStore, SqlSugarRoleStore>());
        services.Replace(ServiceDescriptor.Scoped<IPermissionChecker, SqlSugarPermissionChecker>());
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
git commit -m "feat(authorization-sqlsugar): 以 Replace 顶替角色存储与权限检查器"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 6: 全量验收与注释复查

**Files:** 无新增；只在复查发现问题时修改本计划产出的 `.cs` 文件。

**Interfaces:**
- Consumes: Task 1–5 的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：** spec 第 5 节与第 8 节。

**本任务禁止事项：** **不要**写包 README 与 `docs/` 条目——那是 ③ 的内容。**不要**把权衡论证写进代码注释。

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

- [ ] **Step 2: 注释复查**

通读本计划新增与修改的全部 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算。重点看 `SqlSugarRoleStore.DeleteRoleAsync` 与 `ExecuteInTransactionAsync`、`SqlSugarPermissionChecker` 的类注释：它们只应说「做什么」，不应解释「为什么要级联」「为什么两条 SQL」「`UseTranAsync` 会吞异常」——这些进提交信息。

同时确认：没有 `<inheritdoc/>`；每个 `public` 成员都有 `<summary>`；每个文件以两行版权声明开头。

- [ ] **Step 3: 若 Step 2 有改动则提交**

```bash
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "style(authorization-sqlsugar): 注释只保留代码行为说明"
```

没有改动就跳过本步。

---

## 完成标准

② 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- `SysAuthzRole`、`SysAuthzUserRole` 可赋值给 `IStrictMultiTenantEntity`，唯一索引生效
- 角色 CRUD、用户角色关联的语义与 `DefaultRoleStore` 一致，唯一差异是改名后成员关系保持
- 删除角色级联删除用户关联与角色权限；同标识重建不继承；中途失败整体回滚并抛出；外层事务回滚时一并回滚
- `SqlSugarPermissionChecker` 与 `DefaultPermissionChecker` 在同一份数据上判定一致
- 直接授予命中 1 条 SQL，否则 2 条；多权限判定与获取全部权限至多 2 条，与角色数无关
- 关联行与角色行 `TenantId` 不一致时不配对、不授予
- 挂上严格租户过滤器后只见本租户的角色与授权
- `IRoleStore`、`IPermissionChecker` 各只有一个描述符，实现为 SqlSugar 类型、`Scoped`

## 已知边界（写入 PR 描述，不写进代码注释）

- **策略评估仍逐权限查询**：`DefaultPolicyEvaluator` 对 `RequiredPermissions` 逐个调 `IsGrantedAsync`，p 个权限约 2p 条 SQL
- **检查器被应用替换**：应用自己 `Replace` 的 `IPermissionChecker` 优先，热路径次数由应用决定
- **与默认实现的一处差异**：用户角色关联存角色标识，改名后成员关系保持
- **静态角色可删**、**`IsInRoleAsync` 不看启用**：均与默认实现一致
- **需要租户过滤器**：单表读写依赖 `EnableTenantFilter`（默认 `true`）；联表已带 `TenantId`，不会跨租户配对
- **并发创建同名角色**：后到者撞唯一索引抛数据库异常而非 `InvalidOperationException`
- **名称大小写**：由数据库排序规则决定
- **取消令牌的残留**：SqlSugar 把令牌写进 `Context.Ado.CancellationToken` 且不清除

## 下一份计划

③（`.superpowers/plans/2026-09-28-authorization-sqlsugar-3-policy-store-docs.md`）：`SysAuthzPolicy` 实体、策略映射器与 `IPolicyStore` 的 5 个方法（含自定义要求的策略拒绝写入），以及包 README、`docs/packages/authorization-sqlsugar.md`、侧边栏、包索引页与四份 README 的模块清单。三份完成后本包方可提 PR。
