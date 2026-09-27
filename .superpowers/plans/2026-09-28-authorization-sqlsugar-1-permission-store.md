# Authorization.SqlSugar ①：包骨架与权限存储 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建 `XiHan.Framework.Authorization.SqlSugar` 包，交付权限定义与两张授予表的实体、`IPermissionStore` 的 SqlSugar 实现，并以 `Replace` 顶替主包的内存实现。

**Architecture:** 三个实体（`SysAuthzPermission` 全局、`SysAuthzUserPermission` 与 `SysAuthzRolePermission` 严格按租户隔离），表名统一 `sys_authz_` 前缀，主键雪花 `long`，业务键做成首列为 `TenantId` 的唯一索引。存储经 `ISqlSugarClientResolver.GetCurrentClient()` 取客户端，读用户 / 角色权限时授予表 INNER JOIN 定义表。字段搬运放在静态映射器里，可脱离数据库单测。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、System.Text.Json、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-authorization-sqlsugar-1-permission-store-design.md`

> 该 spec 自成一体，实现 ① 所需的全部约束都在其中。

**Linear 议题:** <https://linear.app/elf-express/issue/EDDIE-10>

**前置:** 无。本计划是 `Authorization.SqlSugar` 三份计划的第一份，②③ 依赖本计划的产出。

> 设计文档与计划提交在 `dev` 分支，实现在 `feat/authorization-sqlsugar` worktree（`E:/source/XiHan/XiHan.Framework-authorization`）。worktree 从 `dev` 开出，能看到这些文件；若看不到，请按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：实现从 `dev` 开 worktree，分支 `feat/authorization-sqlsugar`。上游是 `main`，**绝不在 `main` 上提交**。

```bash
git worktree add ../XiHan.Framework-authorization -b feat/authorization-sqlsugar dev
```

之后所有命令都在 `E:/source/XiHan/XiHan.Framework-authorization` 下执行。

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
ISugarQueryable<T, T2> ISugarQueryable<T, T2>.Where(Expression<Func<T, T2, bool>> expression)
ISugarQueryable<TResult> ISugarQueryable<T, T2>.Select<TResult>(Expression<Func<T, T2, TResult>> expression)
ISugarQueryable<T> ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
Task<bool> ISugarQueryable<T>.AnyAsync(Expression<Func<T, bool>> expression, CancellationToken token)
Task<T> ISugarQueryable<T>.FirstAsync(Expression<Func<T, bool>> expression, CancellationToken token)   // 无行时返回 null
Task<List<T>> ISugarQueryable<T>.ToListAsync(CancellationToken token)
Task<int> ISugarQueryable<T>.CountAsync()

// SqlSugar —— Interface/IUpdateable.cs / IDeleteable.cs / Insertable.cs
IUpdateable<T> ISqlSugarClient.Updateable<T>() where T : class, new()
IUpdateable<T> IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)
IUpdateable<T> IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IUpdateable<T>.ExecuteCommandAsync(CancellationToken token)
IDeleteable<T> ISqlSugarClient.Deleteable<T>() where T : class, new()
IDeleteable<T> IDeleteable<T>.Where(Expression<Func<T, bool>> expression)
Task<int> IDeleteable<T>.ExecuteCommandAsync(CancellationToken token)
IInsertable<T> ISqlSugarClient.Insertable<T>(T insertObj) where T : class, new()
Task<int> IInsertable<T>.ExecuteCommandAsync(CancellationToken token)

// SqlSugar —— Entities/Mapping/SugarMappingAttribute.cs:340-390（AllowMultiple = true；字段参数是属性名）
SugarIndexAttribute(string indexName, string fieldName, OrderByType sortType, bool isUnique = false)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, string fieldName3, OrderByType sortType3, bool isUnique = false)

// SqlSugar —— Interface/ICodeFirst.cs / IDbMaintenance.cs
void ICodeFirst.InitTables(params Type[] entityTypes)
List<DbTableInfo> IDbMaintenance.GetTableInfoList(bool isCache = true)

// 框架 —— XiHan.Framework.Data.SqlSugar.Clients.ISqlSugarClientResolver
ISqlSugarClient GetCurrentClient()
ISqlSugarClient GetClientForEntity(Type entityType)
ISqlSugarClient GetClient(string configId)
IReadOnlyCollection<string> GetAllConfigIds()
IReadOnlyList<string> GetCurrentLayoutConfigIds()
IEnumerable<ISqlSugarClient> GetAllClients()
ITenant AsTenant()

// 框架 —— XiHan.Framework.DistributedIds
long IDistributedIdGenerator<long>.NextId()
IDistributedIdGenerator<long> IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload(ushort workerId = 1)

// 框架 —— XiHan.Framework.Data.SqlSugar.Initializers.TableInitializationAttribute
bool Enabled { get; set; } = true;  string? Group { get; set; }  bool IncludeModuleConnections { get; set; }
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
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀（本包没有配置节）

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`，全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，**不 Import `version.props`、不设 `AssemblyName`**。`Microsoft.Data.Sqlite` 经 `SqlSugarCore` 传递引入，**不需要额外 `PackageReference`**。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本包无关，不要去追。

**提交信息**：中文 Conventional Commits，作用域 `authorization-sqlsugar`。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**。不开启就不会自动建表，首次读写即报表不存在。③ 会把这一点写进 README。

---

## 本计划特有的硬约束

**① 授予实体必须实现 `IStrictMultiTenantEntity`，权限定义实体不能实现 `IMultiTenantEntity`。**

选错不会报错，SQLite 测试也全绿（测试客户端没挂租户过滤器）。Task 1 用反射测试把它钉死，**不许为了让别的测试通过而删掉这些断言**。

**② 所有表名以 `sys_authz_` 开头。**

下游 `XiHan.BasicApp` 已有 `Sys_Role`、`Sys_Permission`。MySQL / SQL Server 对表名大小写不敏感，撞名时 CodeFirst 会去改那张业务表。

**③ 只用 `GetCurrentClient()`。**

测试桩的 `GetClientForEntity` 与 `GetClient` 直接抛 `NotSupportedException`。实现里调了它们，测试会立刻红——这是故意的，**不要改桩去放行**。

**④ 顶替用 `Replace`，不用 `TryAdd`。**

主包以 `TryAddScoped` 注册了 `IPermissionStore`，`TryAdd` 是空操作。

**⑤ 不截断、不过滤、不手动赋 `TenantId`。**

名称超长交给数据库报错；读取不过滤 `IsEnabled`；`TenantId` 由数据层 AOP 填写。

---

## File Structure

```
framework/src/XiHan.Framework.Authorization.SqlSugar/
  XiHan.Framework.Authorization.SqlSugar.csproj                        新建
  XiHanAuthorizationSqlSugarModule.cs                                  新建
  Entities/SysAuthzPermission.cs                                       新建
  Entities/SysAuthzUserPermission.cs                                   新建
  Entities/SysAuthzRolePermission.cs                                   新建
  Mapping/JsonColumn.cs                                                新建（internal）
  Mapping/PermissionMapper.cs                                          新建
  Permissions/SqlSugarPermissionStore.cs                               新建
  Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs   新建

framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/
  XiHan.Framework.Authorization.SqlSugar.Tests.csproj                  新建
  AuthorizationTestContext.cs                                          新建（含 StubClientResolver）
  EntityConventionTests.cs                                             新建
  PermissionMapperTests.cs                                             新建
  PermissionStoreTests.cs                                              新建
  RegistrationTests.cs                                                 新建

framework/XiHan.Framework.slnx                                         修改：注册两个项目
```

---

### Task 1: 包骨架、测试项目与三个实体

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/XiHan.Framework.Authorization.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzPermission.cs`
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzUserPermission.cs`
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzRolePermission.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/EntityConventionTests.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: `XiHan.Framework.Data.SqlSugar.Entities.SugarEntity<long>`、`SugarMultiTenantEntity<long>`；`XiHan.Framework.Domain.Entities.Abstracts.IStrictMultiTenantEntity`；`XiHan.Framework.Data.SqlSugar.Initializers.TableInitializationAttribute`
- Produces:
  - `SysAuthzPermission`（属性 `PermissionName`、`DisplayName`、`Description`、`ParentName`、`Tag`、`IsEnabled`、`SortOrder`、`Properties`）
  - `SysAuthzUserPermission`（`UserId`、`PermissionName`，继承 `TenantId`）
  - `SysAuthzRolePermission`（`RoleId`、`PermissionName`，继承 `TenantId`）
  - 三者均有 `public Xxx()` 与 `public Xxx(long basicId)` 两个构造函数
  - 测试夹具 `AuthorizationTestContext`：`static Type[] EntityTypes`、`SqlSugarClient Client`、`StubClientResolver Resolver`、`IDistributedIdGenerator<long> IdGenerator`
  - 测试桩 `StubClientResolver(ISqlSugarClient client)`：`GetCurrentClient()` 返回该客户端，`GetClientForEntity` / `GetClient` 抛 `NotSupportedException`

**参考来源（动手前先读）：**
- 实体写法：`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`
- 多租户基类：`framework/src/XiHan.Framework.Data/SqlSugar/Entities/SugarMultiTenantEntity.cs`
- 严格隔离接口：`framework/src/XiHan.Framework.Domain/Entities/Abstracts/IStrictMultiTenantEntity.cs`
- 包 csproj 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- 测试 csproj 范本：`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/XiHan.Framework.Auditing.SqlSugar.Tests.csproj`
- 索引字段按属性名匹配：`E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/Abstract/CodeFirstProvider/CodeFirstProvider.cs:381-392`

**本任务禁止事项：** 硬约束 ①②。不要加 `[SplitTable]`。不要给 `[SugarIndex]` 写列名字符串——一律 `nameof(类名.属性)`。不要设 `IncludeModuleConnections`。

- [ ] **Step 1: 建包的 csproj**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/XiHan.Framework.Authorization.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Authorization.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Authorization.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Authorization.SqlSugar</PackageId>
        <Description>曦寒框架授权存储 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Authorization\XiHan.Framework.Authorization.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: 建测试项目、夹具与约定测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Authorization.SqlSugar\XiHan.Framework.Authorization.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 授权存储测试夹具，提供一个临时 SQLite 库与各存储实例
/// </summary>
internal sealed class AuthorizationTestContext : IDisposable
{
    private readonly string _databaseFile;

    /// <summary>
    /// 构造函数，建出本包全部实体的表
    /// </summary>
    public AuthorizationTestContext()
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_authz_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        Client.CodeFirst.InitTables(EntityTypes);
        Resolver = new StubClientResolver(Client);
    }

    /// <summary>
    /// 本包程序集中全部标注了 SugarTable 的实体类型
    /// </summary>
    public static Type[] EntityTypes { get; } =
        [.. typeof(SysAuthzPermission).Assembly.GetTypes().Where(type => type.GetCustomAttribute<SugarTable>() is not null)];

    /// <summary>
    /// 临时库的客户端
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 只放行当前客户端的解析器
    /// </summary>
    public StubClientResolver Resolver { get; }

    /// <summary>
    /// 主键生成器
    /// </summary>
    public IDistributedIdGenerator<long> IdGenerator { get; } = IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload();

    /// <summary>
    /// 释放客户端并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        Client.Dispose();

        if (File.Exists(_databaseFile))
        {
            File.Delete(_databaseFile);
        }
    }
}

/// <summary>
/// 测试用客户端解析器，只允许经当前客户端访问
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
{
    private readonly ISqlSugarClient _client;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="client">当前客户端</param>
    public StubClientResolver(ISqlSugarClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 获取当前客户端
    /// </summary>
    /// <returns>当前客户端</returns>
    public ISqlSugarClient GetCurrentClient()
    {
        return _client;
    }

    /// <summary>
    /// 按实体路由，授权存储不应调用
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>不返回</returns>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        throw new NotSupportedException("授权存储应统一经 GetCurrentClient 取客户端。");
    }

    /// <summary>
    /// 按连接标识取客户端，授权存储不应调用
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    /// <returns>不返回</returns>
    public ISqlSugarClient GetClient(string configId)
    {
        throw new NotSupportedException("授权存储应统一经 GetCurrentClient 取客户端。");
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识</returns>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取当前布局的连接配置标识
    /// </summary>
    /// <returns>连接配置标识</returns>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取全部客户端
    /// </summary>
    /// <returns>客户端集合</returns>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [_client];
    }

    /// <summary>
    /// 获取底层多租户接口
    /// </summary>
    /// <returns>不返回</returns>
    public ITenant AsTenant()
    {
        throw new NotSupportedException("测试桩不支持多租户切换。");
    }
}
```

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/EntityConventionTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 实体约定测试
/// </summary>
public class EntityConventionTests
{
    /// <summary>
    /// 全部实体的表名以 sys_authz_ 开头
    /// </summary>
    [Fact]
    public void 全部实体的表名以sys_authz_开头()
    {
        Assert.NotEmpty(AuthorizationTestContext.EntityTypes);

        foreach (var entityType in AuthorizationTestContext.EntityTypes)
        {
            var tableName = entityType.GetCustomAttribute<SugarTable>()!.TableName;

            Assert.StartsWith("sys_authz_", tableName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 全部实体声明 Authorization 建表分组且只建在主库
    /// </summary>
    [Fact]
    public void 全部实体声明Authorization建表分组()
    {
        foreach (var entityType in AuthorizationTestContext.EntityTypes)
        {
            var attribute = entityType.GetCustomAttribute<TableInitializationAttribute>(inherit: true);

            Assert.NotNull(attribute);
            Assert.True(attribute.Enabled);
            Assert.Equal("Authorization", attribute.Group);
            Assert.False(attribute.IncludeModuleConnections);
        }
    }

    /// <summary>
    /// 关联实体按租户严格隔离
    /// </summary>
    /// <param name="entityType">实体类型</param>
    [Theory]
    [InlineData(typeof(SysAuthzUserPermission))]
    [InlineData(typeof(SysAuthzRolePermission))]
    public void 关联实体按租户严格隔离(Type entityType)
    {
        Assert.True(typeof(IStrictMultiTenantEntity).IsAssignableFrom(entityType));
    }

    /// <summary>
    /// 定义实体不按租户隔离
    /// </summary>
    /// <param name="entityType">实体类型</param>
    [Theory]
    [InlineData(typeof(SysAuthzPermission))]
    public void 定义实体不按租户隔离(Type entityType)
    {
        Assert.False(typeof(IMultiTenantEntity).IsAssignableFrom(entityType));
    }

    /// <summary>
    /// 全部实体的表都能建出来
    /// </summary>
    [Fact]
    public void 全部实体的表都能建出来()
    {
        using var context = new AuthorizationTestContext();

        var tableNames = context.Client.DbMaintenance.GetTableInfoList(false)
            .Select(table => table.Name)
            .ToList();

        foreach (var entityType in AuthorizationTestContext.EntityTypes)
        {
            var expected = entityType.GetCustomAttribute<SugarTable>()!.TableName;

            Assert.Contains(tableNames, name => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 权限名称唯一
    /// </summary>
    [Fact]
    public async Task 权限名称唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewPermission(context, "User.Create")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewPermission(context, "User.Create")).ExecuteCommandAsync());
    }

    /// <summary>
    /// 用户权限授予在同一租户内唯一，不同租户可重复
    /// </summary>
    [Fact]
    public async Task 用户权限授予在同一租户内唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewUserPermission(context, 0, "u1", "A")).ExecuteCommandAsync();
        await context.Client.Insertable(NewUserPermission(context, 1, "u1", "A")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewUserPermission(context, 0, "u1", "A")).ExecuteCommandAsync());
    }

    /// <summary>
    /// 角色权限授予在同一租户内唯一，不同租户可重复
    /// </summary>
    [Fact]
    public async Task 角色权限授予在同一租户内唯一()
    {
        using var context = new AuthorizationTestContext();

        await context.Client.Insertable(NewRolePermission(context, 0, "r1", "A")).ExecuteCommandAsync();
        await context.Client.Insertable(NewRolePermission(context, 1, "r1", "A")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Client.Insertable(NewRolePermission(context, 0, "r1", "A")).ExecuteCommandAsync());
    }

    private static SysAuthzPermission NewPermission(AuthorizationTestContext context, string name)
    {
        return new SysAuthzPermission(context.IdGenerator.NextId())
        {
            PermissionName = name,
            DisplayName = name,
            IsEnabled = true
        };
    }

    private static SysAuthzUserPermission NewUserPermission(AuthorizationTestContext context, long tenantId, string userId, string permissionName)
    {
        return new SysAuthzUserPermission(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            UserId = userId,
            PermissionName = permissionName
        };
    }

    private static SysAuthzRolePermission NewRolePermission(AuthorizationTestContext context, long tenantId, string roleId, string permissionName)
    {
        return new SysAuthzRolePermission(context.IdGenerator.NextId())
        {
            TenantId = tenantId,
            RoleId = roleId,
            PermissionName = permissionName
        };
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SysAuthzPermission`、`SysAuthzUserPermission`、`SysAuthzRolePermission`。

- [ ] **Step 4: 建三个实体**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzPermission.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;

namespace XiHan.Framework.Authorization.SqlSugar.Entities;

/// <summary>
/// 权限定义实体
/// </summary>
[SugarTable("sys_authz_permission")]
[TableInitialization(Group = "Authorization")]
[SugarIndex("ux_authz_perm_name", nameof(SysAuthzPermission.PermissionName), OrderByType.Asc, true)]
public class SysAuthzPermission : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthzPermission() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthzPermission(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 权限名称
    /// </summary>
    [SugarColumn(ColumnName = "Permission_Name", Length = 256, IsNullable = false, ColumnDescription = "权限名称")]
    public string PermissionName { get; set; } = string.Empty;

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
    /// 父权限名称
    /// </summary>
    [SugarColumn(ColumnName = "Parent_Name", Length = 256, IsNullable = true, ColumnDescription = "父权限名称")]
    public string? ParentName { get; set; }

    /// <summary>
    /// 分组名称
    /// </summary>
    [SugarColumn(ColumnName = "Tag", Length = 128, IsNullable = true, ColumnDescription = "分组名称")]
    public string? Tag { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [SugarColumn(ColumnName = "Is_Enabled", IsNullable = false, ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 排序
    /// </summary>
    [SugarColumn(ColumnName = "Sort_Order", IsNullable = false, ColumnDescription = "排序")]
    public int SortOrder { get; set; }

    /// <summary>
    /// 额外属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "额外属性的 JSON")]
    public string? Properties { get; set; }
}
```

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzUserPermission.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Entities;

/// <summary>
/// 用户直接权限授予实体
/// </summary>
[SugarTable("sys_authz_user_permission")]
[TableInitialization(Group = "Authorization")]
[SugarIndex("ux_authz_up_tenant_user_perm",
    nameof(SysAuthzUserPermission.TenantId), OrderByType.Asc,
    nameof(SysAuthzUserPermission.UserId), OrderByType.Asc,
    nameof(SysAuthzUserPermission.PermissionName), OrderByType.Asc,
    true)]
public class SysAuthzUserPermission : SugarMultiTenantEntity<long>, IStrictMultiTenantEntity
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthzUserPermission() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthzUserPermission(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 用户标识
    /// </summary>
    [SugarColumn(ColumnName = "User_Id", Length = 128, IsNullable = false, ColumnDescription = "用户标识")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// 权限名称
    /// </summary>
    [SugarColumn(ColumnName = "Permission_Name", Length = 256, IsNullable = false, ColumnDescription = "权限名称")]
    public string PermissionName { get; set; } = string.Empty;
}
```

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Entities/SysAuthzRolePermission.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Initializers;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.Framework.Authorization.SqlSugar.Entities;

/// <summary>
/// 角色权限授予实体
/// </summary>
[SugarTable("sys_authz_role_permission")]
[TableInitialization(Group = "Authorization")]
[SugarIndex("ux_authz_rp_tenant_role_perm",
    nameof(SysAuthzRolePermission.TenantId), OrderByType.Asc,
    nameof(SysAuthzRolePermission.RoleId), OrderByType.Asc,
    nameof(SysAuthzRolePermission.PermissionName), OrderByType.Asc,
    true)]
public class SysAuthzRolePermission : SugarMultiTenantEntity<long>, IStrictMultiTenantEntity
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthzRolePermission() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">主键</param>
    public SysAuthzRolePermission(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 角色标识
    /// </summary>
    [SugarColumn(ColumnName = "Role_Id", Length = 128, IsNullable = false, ColumnDescription = "角色标识")]
    public string RoleId { get; set; } = string.Empty;

    /// <summary>
    /// 权限名称
    /// </summary>
    [SugarColumn(ColumnName = "Permission_Name", Length = 256, IsNullable = false, ColumnDescription = "权限名称")]
    public string PermissionName { get; set; } = string.Empty;
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `全部实体的表都能建出来` 之外的唯一性用例失败（第二次插入没抛异常），说明索引没建出来——先查 `[SugarIndex]` 的字段参数是不是属性名，**不要把断言改成 `Assert.Null`**。

- [ ] **Step 6: 注册进解决方案**

修改 `framework/XiHan.Framework.slnx`。

在 `/1.src/6.Infrastructure/` 文件夹内这一行：

```xml
    <Project Path="src/XiHan.Framework.Authorization/XiHan.Framework.Authorization.csproj" />
```

之后插入：

```xml
    <Project Path="src/XiHan.Framework.Authorization.SqlSugar/XiHan.Framework.Authorization.SqlSugar.csproj" />
```

在 `/2.tests/1.UnitTests/` 文件夹内这一行：

```xml
    <Project Path="test/XiHan.Framework.Authorization.Tests/XiHan.Framework.Authorization.Tests.csproj" />
```

之后插入：

```xml
    <Project Path="test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj" />
```

- [ ] **Step 7: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(authorization-sqlsugar): 新增包骨架与权限定义、授予实体"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 2: JSON 列辅助与权限映射器

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/JsonColumn.cs`
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/PermissionMapper.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PermissionMapperTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SysAuthzPermission`；主包的 `XiHan.Framework.Authorization.Permissions.PermissionDefinition`
- Produces:
  - `internal static class JsonColumn`：`string? SerializeOrNull<T>(T? value) where T : class`、`T? DeserializeOrNull<T>(string? json) where T : class`（② ③ 复用）
  - `public static class PermissionMapper`：`SysAuthzPermission ToEntity(PermissionDefinition definition, long basicId)`、`PermissionDefinition ToDefinition(SysAuthzPermission entity)`

**参考来源（动手前先读）：**
- 字段来源：`framework/src/XiHan.Framework.Authorization/Permissions/PermissionDefinition.cs`
- 纯函数映射器范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping/AuditingLogMapper.cs`

**本任务禁止事项：** 不要在映射器里截断任何字段（硬约束 ⑤）。不要引入 Newtonsoft.Json——虽然 SqlSugar 传递引入了它，本包统一用 System.Text.Json。映射器不访问数据库、不取主键，主键由调用方传入。

- [ ] **Step 1: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PermissionMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Mapping;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 权限映射器测试
/// </summary>
public class PermissionMapperTests
{
    /// <summary>
    /// 权限定义映射为实体时逐字段对应
    /// </summary>
    [Fact]
    public void 权限定义映射为实体时逐字段对应()
    {
        var definition = new PermissionDefinition("User.Create", "创建用户", "允许创建用户")
        {
            ParentName = "User",
            Tag = "用户",
            IsEnabled = false,
            Order = 7
        };

        var entity = PermissionMapper.ToEntity(definition, 42L);

        Assert.Equal(42L, entity.BasicId);
        Assert.Equal("User.Create", entity.PermissionName);
        Assert.Equal("创建用户", entity.DisplayName);
        Assert.Equal("允许创建用户", entity.Description);
        Assert.Equal("User", entity.ParentName);
        Assert.Equal("用户", entity.Tag);
        Assert.False(entity.IsEnabled);
        Assert.Equal(7, entity.SortOrder);
        Assert.Null(entity.Properties);
    }

    /// <summary>
    /// 实体映射回权限定义时逐字段对应
    /// </summary>
    [Fact]
    public void 实体映射回权限定义时逐字段对应()
    {
        var entity = new SysAuthzPermission(1L)
        {
            PermissionName = "User.Delete",
            DisplayName = "删除用户",
            Description = "允许删除用户",
            ParentName = "User",
            Tag = "用户",
            IsEnabled = true,
            SortOrder = 9
        };

        var definition = PermissionMapper.ToDefinition(entity);

        Assert.Equal("User.Delete", definition.Name);
        Assert.Equal("删除用户", definition.DisplayName);
        Assert.Equal("允许删除用户", definition.Description);
        Assert.Equal("User", definition.ParentName);
        Assert.Equal("用户", definition.Tag);
        Assert.True(definition.IsEnabled);
        Assert.Equal(9, definition.Order);
        Assert.Null(definition.Properties);
    }

    /// <summary>
    /// 扩展属性以 JSON 往返，值读回为 JsonElement
    /// </summary>
    [Fact]
    public void 扩展属性以JSON往返且值读回为JsonElement()
    {
        var definition = new PermissionDefinition("A", "甲")
        {
            Properties = new Dictionary<string, object>
            {
                ["level"] = 3,
                ["label"] = "敏感"
            }
        };

        var entity = PermissionMapper.ToEntity(definition, 1L);

        Assert.False(string.IsNullOrWhiteSpace(entity.Properties));

        var restored = PermissionMapper.ToDefinition(entity);

        Assert.NotNull(restored.Properties);
        Assert.Equal(3, Assert.IsType<JsonElement>(restored.Properties["level"]).GetInt32());
        Assert.Equal("敏感", Assert.IsType<JsonElement>(restored.Properties["label"]).GetString());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `PermissionMapper`（`XiHan.Framework.Authorization.SqlSugar.Mapping` 命名空间不存在）。

- [ ] **Step 3: 实现 JSON 列辅助与映射器**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/JsonColumn.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;

namespace XiHan.Framework.Authorization.SqlSugar.Mapping;

/// <summary>
/// JSON 列的序列化辅助
/// </summary>
internal static class JsonColumn
{
    /// <summary>
    /// 序列化为 JSON 文本，值为空时返回空
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    /// <param name="value">要序列化的值</param>
    /// <returns>JSON 文本</returns>
    public static string? SerializeOrNull<T>(T? value) where T : class
    {
        return value is null ? null : JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// 从 JSON 文本反序列化，文本为空时返回空
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    /// <param name="json">JSON 文本</param>
    /// <returns>反序列化后的值</returns>
    public static T? DeserializeOrNull<T>(string? json) where T : class
    {
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);
    }
}
```

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Mapping/PermissionMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Mapping;

/// <summary>
/// 权限定义与实体的映射
/// </summary>
public static class PermissionMapper
{
    /// <summary>
    /// 权限定义映射为实体
    /// </summary>
    /// <param name="definition">权限定义</param>
    /// <param name="basicId">主键</param>
    /// <returns>权限定义实体</returns>
    public static SysAuthzPermission ToEntity(PermissionDefinition definition, long basicId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new SysAuthzPermission(basicId)
        {
            PermissionName = definition.Name,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            ParentName = definition.ParentName,
            Tag = definition.Tag,
            IsEnabled = definition.IsEnabled,
            SortOrder = definition.Order,
            Properties = JsonColumn.SerializeOrNull(definition.Properties)
        };
    }

    /// <summary>
    /// 实体映射为权限定义
    /// </summary>
    /// <param name="entity">权限定义实体</param>
    /// <returns>权限定义</returns>
    public static PermissionDefinition ToDefinition(SysAuthzPermission entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new PermissionDefinition(entity.PermissionName, entity.DisplayName, entity.Description)
        {
            ParentName = entity.ParentName,
            Tag = entity.Tag,
            IsEnabled = entity.IsEnabled,
            Order = entity.SortOrder,
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
git commit -m "feat(authorization-sqlsugar): 新增权限定义映射器与 JSON 列辅助"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 权限存储

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Permissions/SqlSugarPermissionStore.cs`
- Modify: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PermissionStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的三个实体、Task 2 的 `PermissionMapper` 与 `JsonColumn`；`ISqlSugarClientResolver`、`IDistributedIdGenerator<long>`
- Produces:
  - `public class SqlSugarPermissionStore : IPermissionStore`，构造函数 `(ISqlSugarClientResolver clientResolver, IDistributedIdGenerator<long> idGenerator)`
  - 契约的 8 个方法
  - `Task<bool> AddOrUpdatePermissionAsync(PermissionDefinition permission, CancellationToken cancellationToken = default)`
  - `Task AddPermissionsAsync(List<PermissionDefinition> permissions, CancellationToken cancellationToken = default)`
  - `Task<bool> RemovePermissionAsync(string permissionName, CancellationToken cancellationToken = default)`
  - 夹具新增 `SqlSugarPermissionStore CreatePermissionStore()`

**参考来源（动手前先读）：**
- 语义基准：`framework/src/XiHan.Framework.Authorization/Permissions/DefaultPermissionStore.cs`（spec §1.2 的表逐条对应）
- 契约：`framework/src/XiHan.Framework.Authorization/Permissions/IPermissionStore.cs`
- 构造与取主键：`framework/src/XiHan.Framework.Auditing.SqlSugar/Writers/SqlSugarLoginLogWriter.cs`
- 条件更新写法：`framework/src/XiHan.Framework.EventBus.SqlSugar/Outbox/SqlSugarEventOutbox.cs` 中的 `SetColumns`
- 联表只取一张表：`E:/source/platfrom-admin/docs/SqlSugar-docs/Select用法.md` 4.5 节

**本任务禁止事项：** 硬约束 ③⑤。读取**不过滤** `IsEnabled`。授予**不校验**定义是否存在。删除定义**不级联**删除授予。不要调 `GetClientForEntity`——测试桩会抛异常。

- [ ] **Step 1: 夹具加工厂方法**

修改 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/AuthorizationTestContext.cs`。

using 区在 `using XiHan.Framework.Authorization.SqlSugar.Entities;` 之后追加：

```csharp
using XiHan.Framework.Authorization.SqlSugar.Permissions;
```

在 `IdGenerator` 属性之后、`Dispose` 方法之前插入：

```csharp
    /// <summary>
    /// 创建权限存储
    /// </summary>
    /// <returns>权限存储</returns>
    public SqlSugarPermissionStore CreatePermissionStore()
    {
        return new SqlSugarPermissionStore(Resolver, IdGenerator);
    }
```

- [ ] **Step 2: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/PermissionStoreTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Entities;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 权限存储测试
/// </summary>
public class PermissionStoreTests
{
    /// <summary>
    /// 新增权限定义后可按名称读回
    /// </summary>
    [Fact]
    public async Task 新增权限定义后可按名称读回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        var added = await store.AddOrUpdatePermissionAsync(new PermissionDefinition("User.Create", "创建用户", "允许创建用户")
        {
            ParentName = "User",
            Tag = "用户",
            Order = 3
        });

        Assert.True(added);

        var permission = await store.GetPermissionByNameAsync("User.Create");

        Assert.NotNull(permission);
        Assert.Equal("创建用户", permission.DisplayName);
        Assert.Equal("允许创建用户", permission.Description);
        Assert.Equal("User", permission.ParentName);
        Assert.Equal("用户", permission.Tag);
        Assert.Equal(3, permission.Order);
        Assert.True(permission.IsEnabled);
    }

    /// <summary>
    /// 同名权限定义再次写入时更新而不新增
    /// </summary>
    [Fact]
    public async Task 同名权限定义再次写入时更新而不新增()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("User.Create", "创建用户"));
        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("User.Create", "新建用户") { IsEnabled = false });

        var permission = Assert.Single(await store.GetAllPermissionsAsync());

        Assert.Equal("新建用户", permission.DisplayName);
        Assert.False(permission.IsEnabled);
    }

    /// <summary>
    /// 空名称的权限定义不写入
    /// </summary>
    [Fact]
    public async Task 空名称的权限定义不写入()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        Assert.False(await store.AddOrUpdatePermissionAsync(new PermissionDefinition()));
        Assert.Empty(await store.GetAllPermissionsAsync());
    }

    /// <summary>
    /// 批量写入权限定义后按排序与名称读回
    /// </summary>
    [Fact]
    public async Task 批量写入权限定义后按排序与名称读回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddPermissionsAsync(
        [
            new PermissionDefinition("B", "乙") { Order = 1 },
            new PermissionDefinition("A", "甲") { Order = 1 },
            new PermissionDefinition("C", "丙") { Order = 0 },
            new PermissionDefinition()
        ]);

        var names = (await store.GetAllPermissionsAsync()).Select(permission => permission.Name).ToList();

        Assert.Equal(new[] { "C", "A", "B" }, names);
    }

    /// <summary>
    /// 删除权限定义返回是否删到并且之后读不到
    /// </summary>
    [Fact]
    public async Task 删除权限定义返回是否删到并且之后读不到()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("A", "甲"));

        Assert.True(await store.RemovePermissionAsync("A"));
        Assert.False(await store.RemovePermissionAsync("A"));
        Assert.Null(await store.GetPermissionByNameAsync("A"));
    }

    /// <summary>
    /// 授予用户权限后按用户读回
    /// </summary>
    [Fact]
    public async Task 授予用户权限后按用户读回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("User.Create", "创建用户"));
        await store.GrantPermissionToUserAsync("u1", "User.Create");

        Assert.Equal("User.Create", Assert.Single(await store.GetUserPermissionsAsync("u1")).Name);
        Assert.Empty(await store.GetUserPermissionsAsync("u2"));
    }

    /// <summary>
    /// 重复授予用户权限只保留一行
    /// </summary>
    [Fact]
    public async Task 重复授予用户权限只保留一行()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.GrantPermissionToUserAsync("u1", "A");
        await store.GrantPermissionToUserAsync("u1", "A");

        Assert.Equal(1, await context.Client.Queryable<SysAuthzUserPermission>().CountAsync());
    }

    /// <summary>
    /// 撤销用户权限后不再返回
    /// </summary>
    [Fact]
    public async Task 撤销用户权限后不再返回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("A", "甲"));
        await store.GrantPermissionToUserAsync("u1", "A");
        await store.RevokePermissionFromUserAsync("u1", "A");

        Assert.Empty(await store.GetUserPermissionsAsync("u1"));
        Assert.Equal(0, await context.Client.Queryable<SysAuthzUserPermission>().CountAsync());
    }

    /// <summary>
    /// 未定义的权限即使已授予也不返回，补回定义后立即生效
    /// </summary>
    [Fact]
    public async Task 未定义的权限即使已授予也不返回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.GrantPermissionToUserAsync("u1", "Ghost");

        Assert.Empty(await store.GetUserPermissionsAsync("u1"));

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("Ghost", "幽灵"));

        Assert.Equal("Ghost", Assert.Single(await store.GetUserPermissionsAsync("u1")).Name);
    }

    /// <summary>
    /// 删除权限定义不删除授予行
    /// </summary>
    [Fact]
    public async Task 删除权限定义不删除授予行()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("A", "甲"));
        await store.GrantPermissionToUserAsync("u1", "A");
        await store.GrantPermissionToRoleAsync("r1", "A");
        await store.RemovePermissionAsync("A");

        Assert.Equal(1, await context.Client.Queryable<SysAuthzUserPermission>().CountAsync());
        Assert.Equal(1, await context.Client.Queryable<SysAuthzRolePermission>().CountAsync());
    }

    /// <summary>
    /// 读取用户权限不过滤已禁用的定义
    /// </summary>
    [Fact]
    public async Task 读取用户权限不过滤已禁用的定义()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("A", "甲") { IsEnabled = false });
        await store.GrantPermissionToUserAsync("u1", "A");

        Assert.False(Assert.Single(await store.GetUserPermissionsAsync("u1")).IsEnabled);
    }

    /// <summary>
    /// 角色权限按角色标识读取且互不影响
    /// </summary>
    [Fact]
    public async Task 角色权限按角色标识读取且互不影响()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddPermissionsAsync([new PermissionDefinition("P1", "一"), new PermissionDefinition("P2", "二")]);
        await store.GrantPermissionToRoleAsync("r1", "P1");
        await store.GrantPermissionToRoleAsync("r2", "P2");

        Assert.Equal("P1", Assert.Single(await store.GetRolePermissionsAsync("r1")).Name);
        Assert.Equal("P2", Assert.Single(await store.GetRolePermissionsAsync("r2")).Name);
        Assert.Empty(await store.GetUserPermissionsAsync("r1"));
    }

    /// <summary>
    /// 重复授予角色权限只保留一行，撤销后不再返回
    /// </summary>
    [Fact]
    public async Task 重复授予角色权限只保留一行且撤销后不再返回()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.AddOrUpdatePermissionAsync(new PermissionDefinition("P1", "一"));
        await store.GrantPermissionToRoleAsync("r1", "P1");
        await store.GrantPermissionToRoleAsync("r1", "P1");

        Assert.Equal(1, await context.Client.Queryable<SysAuthzRolePermission>().CountAsync());

        await store.RevokePermissionFromRoleAsync("r1", "P1");

        Assert.Empty(await store.GetRolePermissionsAsync("r1"));
    }

    /// <summary>
    /// 空参数时不写库也不抛异常
    /// </summary>
    [Fact]
    public async Task 空参数时不写库也不抛异常()
    {
        using var context = new AuthorizationTestContext();
        var store = context.CreatePermissionStore();

        await store.GrantPermissionToUserAsync("", "A");
        await store.GrantPermissionToUserAsync("u1", "");
        await store.GrantPermissionToRoleAsync("", "A");
        await store.GrantPermissionToRoleAsync("r1", "");
        await store.RevokePermissionFromUserAsync("", "A");
        await store.RevokePermissionFromRoleAsync("r1", "");

        Assert.Empty(await store.GetUserPermissionsAsync(""));
        Assert.Empty(await store.GetRolePermissionsAsync(""));
        Assert.Null(await store.GetPermissionByNameAsync(""));
        Assert.False(await store.RemovePermissionAsync(""));
        Assert.Equal(0, await context.Client.Queryable<SysAuthzUserPermission>().CountAsync());
        Assert.Equal(0, await context.Client.Queryable<SysAuthzRolePermission>().CountAsync());
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246` 找不到 `SqlSugarPermissionStore`（夹具的 `CreatePermissionStore` 引用了它）。

- [ ] **Step 4: 实现权限存储**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Permissions/SqlSugarPermissionStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Entities;
using XiHan.Framework.Authorization.SqlSugar.Mapping;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Authorization.SqlSugar.Permissions;

/// <summary>
/// 权限存储的 SqlSugar 实现
/// </summary>
public class SqlSugarPermissionStore : IPermissionStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly IDistributedIdGenerator<long> _idGenerator;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarPermissionStore(
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
    /// 获取用户直接拥有的权限列表
    /// </summary>
    /// <remarks>
    /// 只返回有权限定义的授予，不含经角色得到的权限，不过滤启用状态。
    /// </remarks>
    /// <param name="userId">用户ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限列表</returns>
    public async Task<List<PermissionDefinition>> GetUserPermissionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return [];
        }

        var entities = await Client.Queryable<SysAuthzUserPermission>()
            .InnerJoin<SysAuthzPermission>((grant, permission) => grant.PermissionName == permission.PermissionName)
            .Where((grant, permission) => grant.UserId == userId)
            .Select((grant, permission) => permission)
            .ToListAsync(cancellationToken);

        return [.. entities.Select(PermissionMapper.ToDefinition)];
    }

    /// <summary>
    /// 获取角色的权限列表
    /// </summary>
    /// <remarks>
    /// 只返回有权限定义的授予，不过滤启用状态。
    /// </remarks>
    /// <param name="roleId">角色ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限列表</returns>
    public async Task<List<PermissionDefinition>> GetRolePermissionsAsync(string roleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleId))
        {
            return [];
        }

        var entities = await Client.Queryable<SysAuthzRolePermission>()
            .InnerJoin<SysAuthzPermission>((grant, permission) => grant.PermissionName == permission.PermissionName)
            .Where((grant, permission) => grant.RoleId == roleId)
            .Select((grant, permission) => permission)
            .ToListAsync(cancellationToken);

        return [.. entities.Select(PermissionMapper.ToDefinition)];
    }

    /// <summary>
    /// 授予用户权限
    /// </summary>
    /// <remarks>
    /// 已授予时不重复写入；不校验权限定义是否存在。
    /// </remarks>
    /// <param name="userId">用户ID</param>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task GrantPermissionToUserAsync(string userId, string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(permissionName))
        {
            return;
        }

        var client = Client;
        var granted = await client.Queryable<SysAuthzUserPermission>()
            .AnyAsync(grant => grant.UserId == userId && grant.PermissionName == permissionName, cancellationToken);

        if (granted)
        {
            return;
        }

        var entity = new SysAuthzUserPermission(_idGenerator.NextId())
        {
            UserId = userId,
            PermissionName = permissionName
        };

        await client.Insertable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 撤销用户权限
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RevokePermissionFromUserAsync(string userId, string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(permissionName))
        {
            return;
        }

        await Client.Deleteable<SysAuthzUserPermission>()
            .Where(grant => grant.UserId == userId && grant.PermissionName == permissionName)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 授予角色权限
    /// </summary>
    /// <remarks>
    /// 已授予时不重复写入；不校验角色与权限定义是否存在。
    /// </remarks>
    /// <param name="roleId">角色ID</param>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task GrantPermissionToRoleAsync(string roleId, string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(permissionName))
        {
            return;
        }

        var client = Client;
        var granted = await client.Queryable<SysAuthzRolePermission>()
            .AnyAsync(grant => grant.RoleId == roleId && grant.PermissionName == permissionName, cancellationToken);

        if (granted)
        {
            return;
        }

        var entity = new SysAuthzRolePermission(_idGenerator.NextId())
        {
            RoleId = roleId,
            PermissionName = permissionName
        };

        await client.Insertable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 撤销角色权限
    /// </summary>
    /// <param name="roleId">角色ID</param>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task RevokePermissionFromRoleAsync(string roleId, string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(permissionName))
        {
            return;
        }

        await Client.Deleteable<SysAuthzRolePermission>()
            .Where(grant => grant.RoleId == roleId && grant.PermissionName == permissionName)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 获取所有权限定义
    /// </summary>
    /// <remarks>
    /// 按排序值升序、同排序值按名称序数升序返回。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限定义列表</returns>
    public async Task<List<PermissionDefinition>> GetAllPermissionsAsync(CancellationToken cancellationToken = default)
    {
        var entities = await Client.Queryable<SysAuthzPermission>().ToListAsync(cancellationToken);

        return
        [
            .. entities
                .OrderBy(entity => entity.SortOrder)
                .ThenBy(entity => entity.PermissionName, StringComparer.Ordinal)
                .Select(PermissionMapper.ToDefinition)
        ];
    }

    /// <summary>
    /// 根据名称获取权限定义
    /// </summary>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限定义，不存在时返回空</returns>
    public async Task<PermissionDefinition?> GetPermissionByNameAsync(string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(permissionName))
        {
            return null;
        }

        var entity = await Client.Queryable<SysAuthzPermission>()
            .FirstAsync(permission => permission.PermissionName == permissionName, cancellationToken);

        return entity is null ? null : PermissionMapper.ToDefinition(entity);
    }

    /// <summary>
    /// 添加或更新权限定义
    /// </summary>
    /// <remarks>
    /// 按名称匹配：不存在时新增，存在时改写名称以外的全部字段。
    /// </remarks>
    /// <param name="permission">权限定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>定义为空或名称为空时返回 false，否则返回 true</returns>
    public async Task<bool> AddOrUpdatePermissionAsync(PermissionDefinition permission, CancellationToken cancellationToken = default)
    {
        if (permission is null || string.IsNullOrEmpty(permission.Name))
        {
            return false;
        }

        var client = Client;
        var existing = await client.Queryable<SysAuthzPermission>()
            .FirstAsync(entity => entity.PermissionName == permission.Name, cancellationToken);

        if (existing is null)
        {
            await client.Insertable(PermissionMapper.ToEntity(permission, _idGenerator.NextId()))
                .ExecuteCommandAsync(cancellationToken);

            return true;
        }

        var updated = PermissionMapper.ToEntity(permission, existing.BasicId);

        await client.Updateable<SysAuthzPermission>()
            .SetColumns(entity => new SysAuthzPermission
            {
                DisplayName = updated.DisplayName,
                Description = updated.Description,
                ParentName = updated.ParentName,
                Tag = updated.Tag,
                IsEnabled = updated.IsEnabled,
                SortOrder = updated.SortOrder,
                Properties = updated.Properties
            })
            .Where(entity => entity.BasicId == existing.BasicId)
            .ExecuteCommandAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// 批量添加或更新权限定义
    /// </summary>
    /// <remarks>
    /// 跳过空定义与名称为空的定义，其余逐条调用 <see cref="AddOrUpdatePermissionAsync"/>。
    /// </remarks>
    /// <param name="permissions">权限定义列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task AddPermissionsAsync(List<PermissionDefinition> permissions, CancellationToken cancellationToken = default)
    {
        if (permissions is null)
        {
            return;
        }

        foreach (var permission in permissions.Where(item => item is not null && !string.IsNullOrEmpty(item.Name)))
        {
            await AddOrUpdatePermissionAsync(permission, cancellationToken);
        }
    }

    /// <summary>
    /// 删除权限定义
    /// </summary>
    /// <remarks>
    /// 只删除定义，不删除已有的授予行。
    /// </remarks>
    /// <param name="permissionName">权限名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>删除到了定义返回 true</returns>
    public async Task<bool> RemovePermissionAsync(string permissionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(permissionName))
        {
            return false;
        }

        var deleted = await Client.Deleteable<SysAuthzPermission>()
            .Where(entity => entity.PermissionName == permissionName)
            .ExecuteCommandAsync(cancellationToken);

        return deleted > 0;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若任何用例报 `NotSupportedException: 授权存储应统一经 GetCurrentClient 取客户端。`，说明实现里调了 `GetClientForEntity` 或 `GetClient`——改实现，不改桩（硬约束 ③）。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "feat(authorization-sqlsugar): 实现权限存储"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 注册扩展与模块类

**Files:**
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs`
- Create: `framework/src/XiHan.Framework.Authorization.SqlSugar/XiHanAuthorizationSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `SqlSugarPermissionStore`；主包的 `AddXiHanAuthorization(IServiceCollection, IConfiguration)`、`XiHanAuthorizationModule`；`XiHan.Framework.Data.XiHanDataModule`
- Produces:
  - `public static IServiceCollection AddXiHanAuthorizationSqlSugar(this IServiceCollection services)`（② ③ 往里追加 `Replace`）
  - `public class XiHanAuthorizationSqlSugarModule : XiHanModule`

**参考来源（动手前先读）：**
- 主包注册（确认是 `TryAddScoped`）：`framework/src/XiHan.Framework.Authorization/Extensions/DependencyInjection/XiHanAuthorizationServiceCollectionExtensions.cs:30-36`
- `Replace` 写法：`framework/src/XiHan.Framework.Auditing.SqlSugar/Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs`
- 模块类范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/XiHanAuditingSqlSugarModule.cs`
- 注册测试范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxRegistrationTests.cs`

**本任务禁止事项：** 硬约束 ④。模块类的 `ConfigureServices` **只**调 `AddXiHanAuthorizationSqlSugar()`，不写任何其他逻辑。不要新增 Options 类型或配置节。

- [ ] **Step 1: 写失败的测试**

创建 `framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/RegistrationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XiHan.Framework.Authorization.Extensions.DependencyInjection;
using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Authorization.SqlSugar.Permissions;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Authorization.SqlSugar.Tests;

/// <summary>
/// 注册测试
/// </summary>
public class RegistrationTests
{
    /// <summary>
    /// 契约被顶替为 SqlSugar 实现
    /// </summary>
    /// <param name="serviceType">契约类型</param>
    /// <param name="implementationType">期望的实现类型</param>
    [Theory]
    [InlineData(typeof(IPermissionStore), typeof(SqlSugarPermissionStore))]
    public void 契约被顶替为SqlSugar实现(Type serviceType, Type implementationType)
    {
        var services = BuildServices();

        var descriptor = Assert.Single(services, item => item.ServiceType == serviceType);

        Assert.Equal(implementationType, descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 权限存储的具体类型以作用域注册
    /// </summary>
    [Fact]
    public void 权限存储的具体类型以作用域注册()
    {
        var services = BuildServices();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(SqlSugarPermissionStore));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 模块依赖授权模块与数据模块
    /// </summary>
    [Fact]
    public void 模块依赖授权模块与数据模块()
    {
        var attribute = typeof(XiHanAuthorizationSqlSugarModule).GetCustomAttribute<DependsOnAttribute>();

        Assert.NotNull(attribute);
        Assert.Contains(typeof(XiHanAuthorizationModule), attribute.DependedTypes);
        Assert.Contains(typeof(XiHanDataModule), attribute.DependedTypes);
    }

    /// <summary>
    /// 先注册主包默认实现，再注册本包
    /// </summary>
    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();

        services.AddXiHanAuthorization(new ConfigurationBuilder().Build());
        services.AddXiHanAuthorizationSqlSugar();

        return services;
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authorization.SqlSugar.Tests/XiHan.Framework.Authorization.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0234` / `CS0246` 找不到 `XiHan.Framework.Authorization.SqlSugar.Extensions.DependencyInjection` 与 `XiHanAuthorizationSqlSugarModule`。

- [ ] **Step 3: 实现注册扩展与模块类**

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/Extensions/DependencyInjection/XiHanAuthorizationSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Authorization.Permissions;
using XiHan.Framework.Authorization.SqlSugar.Permissions;

namespace XiHan.Framework.Authorization.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 授权 SqlSugar 存储服务集合扩展
/// </summary>
public static class XiHanAuthorizationSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 实现替换授权模块的内存存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanAuthorizationSqlSugar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<IPermissionStore, SqlSugarPermissionStore>());
        services.TryAddScoped<SqlSugarPermissionStore>();

        return services;
    }
}
```

创建 `framework/src/XiHan.Framework.Authorization.SqlSugar/XiHanAuthorizationSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authorization.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Authorization.SqlSugar;

/// <summary>
/// 曦寒框架授权 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuthorizationSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 实现替换 <see cref="XiHanAuthorizationModule"/> 注册的内存存储。
/// </remarks>
[DependsOn(
    typeof(XiHanAuthorizationModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuthorizationSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context">服务配置上下文</param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanAuthorizationSqlSugar();
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
git commit -m "feat(authorization-sqlsugar): 以 Replace 顶替权限存储并新增模块类"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 5: 全量验收与注释复查

**Files:** 无新增；只在复查发现问题时修改本计划产出的 `.cs` 文件。

**Interfaces:**
- Consumes: Task 1–4 的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：** spec 第 5 节（陷阱）与第 8 节（验收标准）。

**本任务禁止事项：** **不要**写包 README、`docs/` 条目或根 README 的模块清单——那是 ③ 的收尾内容，一个 PR 里按份提交。**不要**把权衡论证写进代码注释。

- [ ] **Step 1: 全量构建与测试**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过。若唯一的失败是 `MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`，那是已知无关抖动，重跑即可。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`），先结束残留的测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.Authorization.SqlSugar.Tests.exe"
```

- [ ] **Step 2: 注释复查**

通读本计划新增的全部 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即删掉，把内容挪进下一次提交的信息里。

同时确认：没有 `<inheritdoc/>`；每个 `public` 成员都有 `<summary>`；每个文件以两行版权声明开头。

- [ ] **Step 3: 若 Step 2 有改动则提交**

```bash
git add framework/src/XiHan.Framework.Authorization.SqlSugar framework/test/XiHan.Framework.Authorization.SqlSugar.Tests
git commit -m "style(authorization-sqlsugar): 注释只保留代码行为说明"
```

没有改动就跳过本步。

---

## 完成标准

① 完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿
- 三个实体表名以 `sys_authz_` 开头，标注 `[TableInitialization(Group = "Authorization")]`
- 两个授予实体可赋值给 `IStrictMultiTenantEntity`；`SysAuthzPermission` 不可赋值给 `IMultiTenantEntity`
- 同名权限定义、同一租户内的重复授予都被唯一索引拒绝；不同租户可各有一行
- `GetUserPermissionsAsync` 只返回直接授予且有定义的权限，不过滤启用状态
- 定义缺失的授予不返回，补回定义后立即返回
- 删除权限定义不删除授予行
- 空参数不写库、不抛异常
- `IPermissionStore` 只有一个描述符，实现为 `SqlSugarPermissionStore`、`Scoped`；具体类型另以 `Scoped` 注册
- 解决方案里注册了包与测试项目

## 已知边界（写入 PR 描述，不写进代码注释）

- **名称大小写**：比较交给数据库排序规则。MySQL / SQL Server 默认大小写不敏感，PostgreSQL / SQLite 默认敏感；默认实现是序数比较
- **并发重复授予**：查重与插入之间无锁，并发授予同一权限时后到者撞唯一索引抛异常，结果状态正确
- **删除定义不级联**：与默认实现一致，定义补回后授予立即重新生效
- **权限定义全局可写**：定义表不分租户，租户态调用定义维护方法会改到所有租户共用的定义
- **`Properties` 的值类型**：读回为 `JsonElement`
- **超长名称**：严格模式的数据库报错，MySQL 非严格模式静默截断，本包不兜底

## 下一份计划

②（`.superpowers/plans/2026-09-28-authorization-sqlsugar-2-role-store-checker.md`）：`SysAuthzRole`、`SysAuthzUserRole` 两个实体与角色映射器；`IRoleStore` 的 11 个方法，含删除角色时在同一事务内级联删除用户关联与角色权限；`SqlSugarPermissionChecker` 顶替 `IPermissionChecker`，把一次判定降到至多 2 次查询。
