# Authentication.SqlSugar ①：包骨架与用户存储 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建 `XiHan.Framework.Authentication.SqlSugar` 包，以 SqlSugar 落库的 `SqlSugarUserStore` 顶替主包作用域内存版的 `DefaultUserStore`，并保证框架自带的 `DefaultAuthenticationService` 在它之上跑完整的登录、改密码、双因素流程时语义不变。

**Architecture:** 实体 `SysAuthUser`（表 `sys_auth_user`，雪花 `long` 主键，显式 `Tenant_Id` 列，`(Tenant_Id, Normalized_User_Name)` 唯一索引）。存储经 `ISqlSugarClientResolver.GetClientForEntity<SysAuthUser>()` 取客户端、参与工作单元事务；每条 SQL 显式带租户条件；失败计数用数据库侧 `SET x = x + 1`；存储实例内维护「标识 → `UserInfo`」的身份映射，让调用方「先读、中途改库、最后整份写回」的流程写回的是最新对象；`UpdateUserAsync` 不写密码列。注册以 `Replace` 顶替、生命周期 Scoped。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.221、xunit.v3 + Microsoft.Testing.Platform

**Spec:** `E:/source/XiHan/XiHan.Framework/.superpowers/specs/2026-09-28-authentication-sqlsugar-1-user-store-design.md`

> 该 spec 自成一体，实现所需的全部约束都在其中。**动手前先读它的第 5 节「会静默失效的陷阱」。**

**Linear 议题:** `https://linear.app/elf-express/issue/EDDIE-9`

**前置:** 无。

> 设计文档与计划提交在 `dev` 分支，实现在 worktree `E:/source/XiHan/XiHan.Framework-authentication`（分支 `feat/authentication-sqlsugar`）。worktree 从 `dev` 开出时这两份文件已在其中；若看不到，按上面的绝对路径读取。

## Global Constraints

每个任务的要求都隐含包含本节。

**工作目录与分支**：从 `dev` 开 worktree，上游是 `main`，**绝不在 `main` 上提交**。

```bash
git worktree add ../XiHan.Framework-authentication -b feat/authentication-sqlsugar dev
```

之后所有命令都在 `E:/source/XiHan/XiHan.Framework-authentication` 执行。

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

**SqlSugar 签名只信源码**：本包经 `XiHan.Framework.Data` 引用 `SqlSugarCore 5.1.4.221`，权威源码是 `E:/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/`（**不是** `Src/Asp.Net/`）；更新提供者目录名是 `Abstract/UpdateProvider/`，**不是** `UpdateableProvider`。文档在 `E:/source/platfrom-admin/docs/SqlSugar-docs/`。

**已核对的签名**（不要凭印象改写）：

```csharp
// SqlSugar（Interface/IQueryable.cs、Interface/Insertable.cs、Interface/IUpdateable.cs、Interface/ICodeFirst.cs）
ISugarQueryable<T> ISqlSugarClient.Queryable<T>()
ISugarQueryable<T> ISugarQueryable<T>.Where(Expression<Func<T, bool>> expression)
Task<T>    ISugarQueryable<T>.FirstAsync()                  // 无匹配时返回 default(T)
T          ISugarQueryable<T>.First()
Task<bool> ISugarQueryable<T>.AnyAsync()
Task<int>  ISugarQueryable<T>.CountAsync()
int        ISugarQueryable<T>.Count()
IInsertable<T> ISqlSugarClient.Insertable<T>(T insertObj)
Task<int>  IInsertable<T>.ExecuteCommandAsync()
int        IInsertable<T>.ExecuteCommand()
IUpdateable<T> ISqlSugarClient.Updateable<T>() where T : class, new()
IUpdateable<T> IUpdateable<T>.SetColumns(Expression<Func<T, T>> columns)       // UpdateableProvider.cs:851
IUpdateable<T> IUpdateable<T>.SetColumns(Expression<Func<T, bool>> columns)    // UpdateableProvider.cs:936，it => it.X == it.X + 1
IUpdateable<T> IUpdateable<T>.Where(Expression<Func<T, bool>> expression)
Task<int>  IUpdateable<T>.ExecuteCommandAsync()
void ICodeFirst.InitTables(Type entityType)
void ICodeFirst.InitTables(params Type[] entityTypes)
SugarIndexAttribute(string indexName, string fieldName1, OrderByType sortType1, string fieldName2, OrderByType sortType2, bool isUnique = false)
Dictionary<string, OrderByType> SugarIndexAttribute.IndexFields
bool SugarIndexAttribute.IsUnique

// 框架
ISqlSugarClient ISqlSugarClientResolver.GetClientForEntity<TEntity>()           // 接口默认方法
long? ICurrentTenant.Id                                                          // XiHan.Framework.MultiTenancy.Abstractions
TKey IDistributedIdGenerator<TKey>.NextId()                                      // XiHan.Framework.DistributedIds
IDistributedIdGenerator<long> IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload(ushort workerId = 1)
```

**编码约定**：

- 每个 `.cs` 文件以两行版权声明开头（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释一律**简体中文**，且**只写代码做什么**；权衡论证、踩坑叙事、设计理由、反事实推理（「否则会……」）一律进提交信息
- file-scoped namespace；**表达式体方法与构造函数在本仓库关闭**（属性与访问器可以）
- Options 类型命名 `XiHan{Feature}Options`，自带 `const string SectionName`，配置节 `XiHan:` 前缀（本份不新增 Options）
- `public` 成员必须有 `<summary>`（`GenerateDocumentationFile` 全局开启，缺了会告警）

**验收门槛**：`dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 必须 **0 警告 0 错误**。

**测试平台是 Microsoft.Testing.Platform，不是 VSTest**：

- **没有可用的筛选参数**——`--filter`、`--list-tests` 返回退出码 3。要跑单个测试类就整个项目跑
- **不要带 `--logger trx` / `--results-directory`**——会以退出码 5 失败
- 命令：`dotnet test --project <csproj> -c Release`；全量 `dotnet test --solution framework/XiHan.Framework.slnx -c Release`

**测试项目 csproj**：只 Import `netcore.props`、`common.props`、`test.props` 三个，**不 Import `version.props`、不设 `AssemblyName`**。`Microsoft.Data.Sqlite` 经 `SqlSugarCore` 传递引入，**不需要额外 `PackageReference`**。

**SQLite 临时库**：连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

**构建环境坑**：构建若报 `MSB3027` / `MSB3021` 说文件被 `XiHan.Framework.*.Tests.exe` 锁住，是残留的测试进程，`taskkill //F //IM "<name>.exe"` 后重建即可，不是代码问题。

**已知的无关抖动**：全量测试偶发 1 个失败 `XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas`（GC 时序，其源码注释自认会随机变红），与本包无关，不要去追。

**提交信息**：中文 Conventional Commits，作用域 `authentication-sqlsugar`（例如 `feat(authentication-sqlsugar): ...`）。**不加任何 AI 署名**——没有 `Co-Authored-By`、没有 "Generated with" 行。

**建表**：`XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization` **都默认 `false`**（`framework/src/XiHan.Framework.Data/SqlSugar/Options/XiHanSqlSugarCoreOptions.cs`）。不开启就不会自动建表，首次读写即报表不存在。这一点必须写进 README。

---

## 本计划特有的硬约束

**① 存储层不做任何哈希、加密、修剪或大小写变换于 `PasswordHash` / `RecoveryCodes` / `TwoFactorSecret`。** 它们原样进、原样出。用户名的规范化只写进 `Normalized_User_Name` 列。

**② `UpdateUserAsync` 不写 `Password_Hash` 列；身份映射必须实现。** 两者缺一，`DefaultAuthenticationService` 的改密码 / 启用双因素 / 成功登录三条流程会把旧快照写回库里，而**逐方法的测试全部是绿的**。Task 6 的流程测试是唯一的防线——**不要**把它改成用同一个存储实例读结果。

**③ 失败计数必须是 `SetColumns(item => item.FailedLoginAttempts == item.FailedLoginAttempts + 1)`**，不得读出来加一再写回。

**④ 每条 SQL 显式带 `item.TenantId == tenantId`。** 不依赖全局租户过滤器，实体不实现 `IMultiTenantEntity`。测试桩不装过滤器，漏写租户条件测试可能照样绿——以代码为准。

**⑤ `UPDATE` 受影响行数为 0 时，再查一次存在性才能判「不存在」。** MySQL `UseAffectedRows=true` 时值未变的更新返回 0，SQLite 测不出来。

**⑥ 注册用 `Replace`，生命周期 Scoped。** `TryAdd` 是空操作；Singleton 会让身份映射跨请求共享。

**⑦ 测试里 `Options.Create` 一律写全名 `Microsoft.Extensions.Options.Options.Create`。** 第 ② 份会新增命名空间 `XiHan.Framework.Authentication.SqlSugar.Options`，写简名的用例届时编译失败。

---

## File Structure

```
framework/src/XiHan.Framework.Authentication.SqlSugar/
  XiHan.Framework.Authentication.SqlSugar.csproj                        新建（Task 1）
  XiHanAuthenticationSqlSugarModule.cs                                  新建（Task 1），修改（Task 7）
  README.md                                                             新建（Task 1），重写（Task 8）
  Entities/SysAuthUser.cs                                               新建（Task 2）
  Mapping/StorageTime.cs                                                新建（Task 3）
  Mapping/AuthUserMapper.cs                                             新建（Task 3）
  Users/SqlSugarUserStore.cs                                            新建（Task 4），整份替换（Task 5）
  Extensions/DependencyInjection/
    XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs           新建（Task 7）

framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/
  XiHan.Framework.Authentication.SqlSugar.Tests.csproj                  新建（Task 2）
  Fakes/FakeCurrentTenant.cs                                            新建（Task 2）
  Fakes/MutableTimeProvider.cs                                          新建（Task 2）
  Fakes/StubClientResolver.cs                                           新建（Task 2）
  AuthenticationTestContext.cs                                          新建（Task 2），修改（Task 4、Task 5）
  AuthUserEntityTests.cs                                                新建（Task 2）
  AuthUserMapperTests.cs                                                新建（Task 3）
  UserStoreReadTests.cs                                                 新建（Task 4）
  UserStoreWriteTests.cs                                                新建（Task 5）
  AuthenticationFlowTests.cs                                            新建（Task 6）
  RegistrationTests.cs                                                  新建（Task 7）

framework/XiHan.Framework.slnx                                          修改（Task 1 源项目、Task 2 测试项目）
```

---

### Task 1: 包骨架与解决方案注册

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/XiHan.Framework.Authentication.SqlSugar.csproj`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/XiHanAuthenticationSqlSugarModule.cs`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/README.md`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: 无
- Produces: 程序集 `XiHan.Framework.Authentication.SqlSugar`，根命名空间同名；模块类型 `XiHanAuthenticationSqlSugarModule`

**参考来源（动手前先读）：**
- csproj 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHan.Framework.EventBus.SqlSugar.csproj`
- 模块类范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/XiHanSqlSugarEventBusModule.cs`
- README 七段结构：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`

**本任务禁止事项：** 不要写任何服务注册逻辑，本任务的模块类是空装配。不要新增 `PackageReference`——SqlSugar 经 `XiHan.Framework.Data` 传递引入。不要动主包 `XiHan.Framework.Authentication` 的任何文件。

- [ ] **Step 1: 创建 csproj**

`framework/src/XiHan.Framework.Authentication.SqlSugar/XiHan.Framework.Authentication.SqlSugar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\version.props" />
    <Import Project="..\..\props\nuget.props" />

    <PropertyGroup>
        <Title>XiHan.Framework.Authentication.SqlSugar</Title>
        <AssemblyName>XiHan.Framework.Authentication.SqlSugar</AssemblyName>
        <PackageId>XiHan.Framework.Authentication.SqlSugar</PackageId>
        <Description>曦寒框架认证存储 SqlSugar 持久化提供程序</Description>
        <OutputType>Library</OutputType>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\XiHan.Framework.Authentication\XiHan.Framework.Authentication.csproj" />
        <ProjectReference Include="..\XiHan.Framework.Data\XiHan.Framework.Data.csproj" />
    </ItemGroup>

</Project>
```

四个 props 的 Import 顺序不能变（`netcore` / `common` / `version` / `nuget`）。

- [ ] **Step 2: 创建模块类**

`framework/src/XiHan.Framework.Authentication.SqlSugar/XiHanAuthenticationSqlSugarModule.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Authentication.SqlSugar;

/// <summary>
/// 曦寒框架认证存储 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]</c> 即启用。
/// </remarks>
[DependsOn(
    typeof(XiHanAuthenticationModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuthenticationSqlSugarModule : XiHanModule
{
}
```

`XiHanAuthenticationModule` 位于命名空间 `XiHan.Framework.Authentication`，是本命名空间的上级，不需要 `using`。

- [ ] **Step 3: 注册进解决方案**

编辑 `framework/XiHan.Framework.slnx`，在 `/1.src/6.Infrastructure/` 文件夹内、这一行之后：

```xml
    <Project Path="src/XiHan.Framework.Authentication/XiHan.Framework.Authentication.csproj" Id="2438269a-f231-41bf-b911-a9d5556e1bc6" />
```

插入：

```xml
    <Project Path="src/XiHan.Framework.Authentication.SqlSugar/XiHan.Framework.Authentication.SqlSugar.csproj" />
```

- [ ] **Step 4: 创建 README 骨架**

`framework/src/XiHan.Framework.Authentication.SqlSugar/README.md`（Task 8 会整份重写）：

````markdown
# XiHan.Framework.Authentication.SqlSugar

## 概述

`XiHan.Framework.Authentication` 的认证存储 SqlSugar 持久化提供程序。

## 核心能力

- 模块装配骨架

## 依赖关系

依赖 `XiHan.Framework.Authentication`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问）。

## 配置与约定

表名 `sys_auth_` 前缀、全小写下划线；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

## 扩展点

需要自定义存储行为时，实现主包的存储接口并在 DI 中以 `Replace` 替换。

## 目录结构

```
XiHanAuthenticationSqlSugarModule.cs   模块装配
```
````

- [ ] **Step 5: 验证构建 0 警告**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。若出现 `XHFH001`，说明版权声明缺失或格式不符。

- [ ] **Step 6: 提交**

```bash
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/XiHan.Framework.slnx
git commit -m "feat(authentication-sqlsugar): 新增包骨架与模块装配"
```

---

### Task 2: 测试项目、替身与用户实体

**Files:**
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/Fakes/FakeCurrentTenant.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/Fakes/MutableTimeProvider.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/Fakes/StubClientResolver.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthUserEntityTests.cs`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Entities/SysAuthUser.cs`
- Modify: `framework/XiHan.Framework.slnx`

**Interfaces:**
- Consumes: Task 1 的程序集
- Produces:
  - `SysAuthUser : SugarEntity<long>`，构造函数 `()` 与 `(long basicId)`；属性见 Step 7
  - `AuthenticationTestContext(params Type[] entityTypes)`：属性 `SqlSugarClient Client`、`StubClientResolver Resolver`、`FakeCurrentTenant Tenant`、`MutableTimeProvider Clock`、`IDistributedIdGenerator<long> IdGenerator`
  - `FakeCurrentTenant`（`long? Id { get; set; }`）、`MutableTimeProvider(DateTimeOffset utcNow)`（`Advance(TimeSpan)`）、`StubClientResolver(ISqlSugarClient client)`（`List<Type> RequestedEntityTypes`）

**参考来源（动手前先读）：**
- 测试 csproj 范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/XiHan.Framework.EventBus.SqlSugar.Tests.csproj`
- 桩解析器范本：`framework/test/XiHan.Framework.Auditing.SqlSugar.Tests/LogWriterTests.cs` 末尾的 `StubClientResolver`
- 租户替身范本：`framework/test/XiHan.Framework.Auditing.Tests/Fakes/FakeCurrentTenant.cs`
- 实体范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/Entities/SysEventOutbox.cs`
- 列定义：spec 第 4.2 节

**本任务禁止事项：** 不要让 `SysAuthUser` 实现 `IMultiTenantEntity` 或任何框架租户接口（硬约束 ④）。不要加 `[SplitTable]`、`[TableInitialization]`。不要把主键设为自增。不要用 `DateTimeOffset` 做时间列。

- [ ] **Step 1: 创建测试项目**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <Import Project="..\..\props\netcore.props" />
    <Import Project="..\..\props\common.props" />
    <Import Project="..\..\props\test.props" />

    <ItemGroup>
        <ProjectReference Include="..\..\src\XiHan.Framework.Authentication.SqlSugar\XiHan.Framework.Authentication.SqlSugar.csproj" />
    </ItemGroup>

</Project>
```

编辑 `framework/XiHan.Framework.slnx`，在 `/2.tests/1.UnitTests/` 文件夹内、这一行之后：

```xml
    <Project Path="test/XiHan.Framework.Authentication.Tests/XiHan.Framework.Authentication.Tests.csproj" />
```

插入：

```xml
    <Project Path="test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj" />
```

- [ ] **Step 2: 创建租户替身**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/Fakes/FakeCurrentTenant.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;

/// <summary>
/// 当前租户替身
/// </summary>
internal sealed class FakeCurrentTenant : ICurrentTenant
{
    /// <summary>
    /// 当前租户是否可用
    /// </summary>
    public bool IsAvailable => Id.HasValue;

    /// <summary>
    /// 租户标识
    /// </summary>
    public long? Id { get; set; }

    /// <summary>
    /// 租户名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 临时切换租户，释放时还原
    /// </summary>
    /// <param name="id">租户标识</param>
    /// <param name="name">租户名称</param>
    /// <returns>还原器</returns>
    public IDisposable Change(long? id, string? name = null)
    {
        var restore = new RestoreScope(this, Id, Name);
        Id = id;
        Name = name;
        return restore;
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly FakeCurrentTenant _owner;
        private readonly long? _previousId;
        private readonly string? _previousName;

        public RestoreScope(FakeCurrentTenant owner, long? previousId, string? previousName)
        {
            _owner = owner;
            _previousId = previousId;
            _previousName = previousName;
        }

        public void Dispose()
        {
            _owner.Id = _previousId;
            _owner.Name = _previousName;
        }
    }
}
```

- [ ] **Step 3: 创建可调时钟**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/Fakes/MutableTimeProvider.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;

/// <summary>
/// 可手动推进的时间提供程序
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="utcNow">初始 UTC 时间</param>
    public MutableTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    /// <summary>
    /// 获取当前 UTC 时间
    /// </summary>
    /// <returns>当前 UTC 时间</returns>
    public override DateTimeOffset GetUtcNow()
    {
        return _utcNow;
    }

    /// <summary>
    /// 推进时间
    /// </summary>
    /// <param name="delta">推进量</param>
    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
    }
}
```

- [ ] **Step 4: 创建桩解析器**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/Fakes/StubClientResolver.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Clients;

namespace XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;

/// <summary>
/// 测试用客户端解析器，任何请求都返回同一个客户端
/// </summary>
internal sealed class StubClientResolver : ISqlSugarClientResolver
{
    private readonly ISqlSugarClient _client;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="client">客户端</param>
    public StubClientResolver(ISqlSugarClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 按实体类型请求过客户端的实体类型
    /// </summary>
    public List<Type> RequestedEntityTypes { get; } = [];

    /// <summary>
    /// 获取当前客户端
    /// </summary>
    /// <returns>客户端</returns>
    public ISqlSugarClient GetCurrentClient()
    {
        return _client;
    }

    /// <summary>
    /// 获取实体对应的客户端
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <returns>客户端</returns>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        RequestedEntityTypes.Add(entityType);
        return _client;
    }

    /// <summary>
    /// 按连接配置标识获取客户端
    /// </summary>
    /// <param name="configId">连接配置标识</param>
    /// <returns>客户端</returns>
    public ISqlSugarClient GetClient(string configId)
    {
        return _client;
    }

    /// <summary>
    /// 获取全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识集合</returns>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取当前布局的全部连接配置标识
    /// </summary>
    /// <returns>连接配置标识集合</returns>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 获取所有库的客户端
    /// </summary>
    /// <returns>客户端集合</returns>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [_client];
    }

    /// <summary>
    /// 获取底层多租户接口
    /// </summary>
    /// <returns>不返回，始终抛出</returns>
    /// <exception cref="NotSupportedException">始终抛出</exception>
    public ITenant AsTenant()
    {
        throw new NotSupportedException("测试桩不支持多租户切换。");
    }
}
```

- [ ] **Step 5: 创建测试夹具**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;
using XiHan.Framework.DistributedIds;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 认证存储测试夹具，提供一个临时 SQLite 库与存储所需的替身
/// </summary>
internal sealed class AuthenticationTestContext : IDisposable
{
    private readonly string _databaseFile;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="entityTypes">要建表的实体类型，为空时只建用户表</param>
    public AuthenticationTestContext(params Type[] entityTypes)
    {
        _databaseFile = Path.Combine(Path.GetTempPath(), $"xihan_auth_{Guid.NewGuid():N}.db");

        Client = new SqlSugarClient(new ConnectionConfig
        {
            // 关闭连接池，用例结束后驱动不再持有临时库文件句柄
            ConnectionString = $"DataSource={_databaseFile};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });

        if (entityTypes.Length == 0)
        {
            Client.CodeFirst.InitTables(typeof(SysAuthUser));
        }
        else
        {
            Client.CodeFirst.InitTables(entityTypes);
        }

        Resolver = new StubClientResolver(Client);
    }

    /// <summary>
    /// 临时库的客户端
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 桩解析器
    /// </summary>
    public StubClientResolver Resolver { get; }

    /// <summary>
    /// 当前租户替身
    /// </summary>
    public FakeCurrentTenant Tenant { get; } = new();

    /// <summary>
    /// 可调时钟
    /// </summary>
    public MutableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>
    /// 雪花主键生成器
    /// </summary>
    public IDistributedIdGenerator<long> IdGenerator { get; } = IdGeneratorFactory.CreateSnowflakeIdGenerator_LowWorkload();

    /// <summary>
    /// 释放客户端并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        Client.Dispose();

        string[] files = [_databaseFile, $"{_databaseFile}-wal", $"{_databaseFile}-shm"];
        foreach (var file in files)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
```

- [ ] **Step 6: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthUserEntityTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using SqlSugar;
using XiHan.Framework.Authentication.SqlSugar.Entities;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 用户实体测试
/// </summary>
public class AuthUserEntityTests
{
    /// <summary>
    /// 表名为认证用户表
    /// </summary>
    [Fact]
    public void 表名为认证用户表()
    {
        var table = typeof(SysAuthUser).GetCustomAttribute<SugarTable>();

        Assert.NotNull(table);
        Assert.Equal("sys_auth_user", table.TableName);
    }

    /// <summary>
    /// 用户名唯一索引覆盖租户与规范化用户名
    /// </summary>
    [Fact]
    public void 用户名唯一索引覆盖租户与规范化用户名()
    {
        var index = Assert.Single(typeof(SysAuthUser).GetCustomAttributes<SugarIndexAttribute>());
        string[] expected = [nameof(SysAuthUser.TenantId), nameof(SysAuthUser.NormalizedUserName)];

        Assert.True(index.IsUnique);
        Assert.Equal(expected, index.IndexFields.Keys.ToArray());
    }

    /// <summary>
    /// 密码哈希列非空且长度为 512
    /// </summary>
    [Fact]
    public void 密码哈希列非空且长度为512()
    {
        var column = typeof(SysAuthUser)
            .GetProperty(nameof(SysAuthUser.PasswordHash))!
            .GetCustomAttribute<SugarColumn>();

        Assert.NotNull(column);
        Assert.Equal("Password_Hash", column.ColumnName);
        Assert.Equal(512, column.Length);
        Assert.False(column.IsNullable);
    }

    /// <summary>
    /// 同租户重复的规范化用户名被唯一索引拒绝
    /// </summary>
    [Fact]
    public void 同租户重复的规范化用户名被唯一索引拒绝()
    {
        using var context = new AuthenticationTestContext();

        context.Client.Insertable(NewEntity(1, 0, "alice")).ExecuteCommand();

        Assert.ThrowsAny<Exception>(() => context.Client.Insertable(NewEntity(2, 0, "Alice")).ExecuteCommand());
    }

    /// <summary>
    /// 不同租户可以使用相同用户名
    /// </summary>
    [Fact]
    public void 不同租户可以使用相同用户名()
    {
        using var context = new AuthenticationTestContext();

        context.Client.Insertable(NewEntity(1, 1, "alice")).ExecuteCommand();
        context.Client.Insertable(NewEntity(2, 2, "alice")).ExecuteCommand();

        Assert.Equal(2, context.Client.Queryable<SysAuthUser>().Count());
    }

    private static SysAuthUser NewEntity(long id, long tenantId, string userName)
    {
        return new SysAuthUser(id)
        {
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
    }
}
```

- [ ] **Step 7: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到类型或命名空间名 `SysAuthUser` / `XiHan.Framework.Authentication.SqlSugar.Entities`。

- [ ] **Step 8: 创建用户实体**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Entities/SysAuthUser.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;

namespace XiHan.Framework.Authentication.SqlSugar.Entities;

/// <summary>
/// 认证用户实体
/// </summary>
[SugarTable("sys_auth_user")]
[SugarIndex("ux_{table}_tenant_normalized_user_name", nameof(TenantId), OrderByType.Asc, nameof(NormalizedUserName), OrderByType.Asc, true)]
public class SysAuthUser : SugarEntity<long>
{
    /// <summary>
    /// 构造函数，供 SqlSugar 物化实体使用
    /// </summary>
    public SysAuthUser() : base()
    {
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="basicId">用户标识</param>
    public SysAuthUser(long basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 租户标识，0 表示平台
    /// </summary>
    [SugarColumn(ColumnName = "Tenant_Id", IsNullable = false, ColumnDescription = "租户标识，0 表示平台")]
    public long TenantId { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [SugarColumn(ColumnName = "User_Name", Length = 128, IsNullable = false, ColumnDescription = "用户名")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// 规范化用户名
    /// </summary>
    [SugarColumn(ColumnName = "Normalized_User_Name", Length = 128, IsNullable = false, ColumnDescription = "规范化用户名")]
    public string NormalizedUserName { get; set; } = string.Empty;

    /// <summary>
    /// 密码哈希
    /// </summary>
    [SugarColumn(ColumnName = "Password_Hash", Length = 512, IsNullable = false, ColumnDescription = "密码哈希")]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// 邮箱
    /// </summary>
    [SugarColumn(ColumnName = "Email", Length = 256, IsNullable = true, ColumnDescription = "邮箱")]
    public string? Email { get; set; }

    /// <summary>
    /// 手机号
    /// </summary>
    [SugarColumn(ColumnName = "Phone_Number", Length = 32, IsNullable = true, ColumnDescription = "手机号")]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// 是否启用双因素认证
    /// </summary>
    [SugarColumn(ColumnName = "Two_Factor_Enabled", IsNullable = false, ColumnDescription = "是否启用双因素认证")]
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// 双因素认证密钥
    /// </summary>
    [SugarColumn(ColumnName = "Two_Factor_Secret", Length = 256, IsNullable = true, ColumnDescription = "双因素认证密钥")]
    public string? TwoFactorSecret { get; set; }

    /// <summary>
    /// 恢复码哈希的 JSON 数组
    /// </summary>
    [SugarColumn(ColumnName = "Recovery_Codes", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "恢复码哈希的 JSON 数组")]
    public string? RecoveryCodes { get; set; }

    /// <summary>
    /// 是否锁定
    /// </summary>
    [SugarColumn(ColumnName = "Is_Locked", IsNullable = false, ColumnDescription = "是否锁定")]
    public bool IsLocked { get; set; }

    /// <summary>
    /// 锁定结束时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Lockout_End", IsNullable = true, ColumnDescription = "锁定结束时间（UTC）")]
    public DateTime? LockoutEnd { get; set; }

    /// <summary>
    /// 登录失败次数
    /// </summary>
    [SugarColumn(ColumnName = "Failed_Login_Attempts", IsNullable = false, ColumnDescription = "登录失败次数")]
    public int FailedLoginAttempts { get; set; }

    /// <summary>
    /// 最后登录时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Last_Login_Time", IsNullable = true, ColumnDescription = "最后登录时间（UTC）")]
    public DateTime? LastLoginTime { get; set; }

    /// <summary>
    /// 密码修改时间（UTC）
    /// </summary>
    [SugarColumn(ColumnName = "Password_Changed_Time", IsNullable = true, ColumnDescription = "密码修改时间（UTC）")]
    public DateTime? PasswordChangedTime { get; set; }

    /// <summary>
    /// 是否激活
    /// </summary>
    [SugarColumn(ColumnName = "Is_Active", IsNullable = false, ColumnDescription = "是否激活")]
    public bool IsActive { get; set; }

    /// <summary>
    /// 附加数据的 JSON 对象
    /// </summary>
    [SugarColumn(ColumnName = "Additional_Data", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "附加数据的 JSON 对象")]
    public string? AdditionalData { get; set; }
}
```

- [ ] **Step 9: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：5 个用例全部 PASS。

若 `同租户重复的规范化用户名被唯一索引拒绝` 失败（第二条插入成功），检查 `SugarIndex` 的最后一个参数是否为 `true`，以及索引字段写的是 `nameof(...)`（属性名）而不是列名。

- [ ] **Step 10: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests framework/XiHan.Framework.slnx
git commit -m "feat(authentication-sqlsugar): 新增认证用户实体与测试项目"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 3: 时间换算与用户映射

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Mapping/StorageTime.cs`
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Mapping/AuthUserMapper.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthUserMapperTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysAuthUser`；主包的 `XiHan.Framework.Authentication.Users.UserInfo`
- Produces:
  - `StorageTime`：`DateTime ToUtc(DateTime)`、`DateTime? ToUtc(DateTime?)`、`DateTime FromStorage(DateTime)`、`DateTime? FromStorage(DateTime?)`
  - `AuthUserMapper`：`string NormalizeUserName(string)`、`SysAuthUser ToEntity(UserInfo, long basicId, long tenantId)`、`UserInfo ToUserInfo(SysAuthUser)`、`string SerializeRecoveryCodes(List<string>?)`、`List<string> DeserializeRecoveryCodes(string?)`、`string? SerializeAdditionalData(Dictionary<string, object>?)`、`Dictionary<string, object>? DeserializeAdditionalData(string?)`

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Authentication/Users/UserInfo.cs`（15 个属性）
- 映射语义：spec 第 4.3 节
- 映射类范本：`framework/src/XiHan.Framework.Auditing.SqlSugar/Mapping/AuditingLogMapper.cs`

**本任务禁止事项：** 硬约束 ①——映射层不碰 `PasswordHash`、`RecoveryCodes` 元素、`TwoFactorSecret` 的内容。不要用 SqlSugar 的 `IsJson`。不要在 `FromStorage` 里调 `ToUniversalTime()`。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthUserMapperTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 用户映射测试
/// </summary>
public class AuthUserMapperTests
{
    /// <summary>
    /// 用户名规范化为大写
    /// </summary>
    [Fact]
    public void 用户名规范化为大写()
    {
        Assert.Equal("ALICE", AuthUserMapper.NormalizeUserName("Alice"));
    }

    /// <summary>
    /// 往返保留全部字段
    /// </summary>
    [Fact]
    public void 往返保留全部字段()
    {
        var lockoutEnd = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
        var lastLoginTime = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);
        var passwordChangedTime = new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);
        var user = new UserInfo
        {
            UserId = "ignored",
            Username = "Alice",
            PasswordHash = "1:1000:SHA256:c2FsdA==:aGFzaA==",
            Email = "alice@example.com",
            PhoneNumber = "13800000000",
            TwoFactorEnabled = true,
            TwoFactorSecret = "JBSWY3DPEHPK3PXP",
            RecoveryCodes = ["hash-1", "hash-2"],
            IsLocked = true,
            LockoutEnd = lockoutEnd,
            FailedLoginAttempts = 3,
            LastLoginTime = lastLoginTime,
            PasswordChangedTime = passwordChangedTime,
            IsActive = false
        };

        var entity = AuthUserMapper.ToEntity(user, 42L, 7L);
        var restored = AuthUserMapper.ToUserInfo(entity);

        Assert.Equal(42L, entity.BasicId);
        Assert.Equal(7L, entity.TenantId);
        Assert.Equal("ALICE", entity.NormalizedUserName);
        Assert.Equal("42", restored.UserId);
        Assert.Equal("Alice", restored.Username);
        Assert.Equal("1:1000:SHA256:c2FsdA==:aGFzaA==", restored.PasswordHash);
        Assert.Equal("alice@example.com", restored.Email);
        Assert.Equal("13800000000", restored.PhoneNumber);
        Assert.True(restored.TwoFactorEnabled);
        Assert.Equal("JBSWY3DPEHPK3PXP", restored.TwoFactorSecret);
        string[] expectedCodes = ["hash-1", "hash-2"];
        Assert.Equal(expectedCodes, restored.RecoveryCodes);
        Assert.True(restored.IsLocked);
        Assert.Equal(lockoutEnd, restored.LockoutEnd);
        Assert.Equal(3, restored.FailedLoginAttempts);
        Assert.Equal(lastLoginTime, restored.LastLoginTime);
        Assert.Equal(passwordChangedTime, restored.PasswordChangedTime);
        Assert.False(restored.IsActive);
        Assert.Null(restored.AdditionalData);
    }

    /// <summary>
    /// 本地时间写入前换算为 UTC
    /// </summary>
    [Fact]
    public void 本地时间写入前换算为UTC()
    {
        var local = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Local);

        var entity = AuthUserMapper.ToEntity(new UserInfo { Username = "alice", LockoutEnd = local }, 1L, 0L);

        Assert.NotNull(entity.LockoutEnd);
        Assert.Equal(local.ToUniversalTime(), entity.LockoutEnd.Value);
        Assert.Equal(DateTimeKind.Utc, entity.LockoutEnd.Value.Kind);
    }

    /// <summary>
    /// 未指定时区的时间按 UTC 处理
    /// </summary>
    [Fact]
    public void 未指定时区的时间按UTC处理()
    {
        var unspecified = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Unspecified);

        var entity = AuthUserMapper.ToEntity(new UserInfo { Username = "alice", LockoutEnd = unspecified }, 1L, 0L);

        Assert.NotNull(entity.LockoutEnd);
        Assert.Equal(unspecified.Ticks, entity.LockoutEnd.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, entity.LockoutEnd.Value.Kind);
    }

    /// <summary>
    /// 读出的时间标记为 UTC 且不平移
    /// </summary>
    [Fact]
    public void 读出的时间标记为UTC且不平移()
    {
        var stored = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Unspecified);
        var entity = new SysAuthUser(1L)
        {
            UserName = "alice",
            NormalizedUserName = "ALICE",
            LockoutEnd = stored
        };

        var restored = AuthUserMapper.ToUserInfo(entity);

        Assert.NotNull(restored.LockoutEnd);
        Assert.Equal(stored.Ticks, restored.LockoutEnd.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, restored.LockoutEnd.Value.Kind);
    }

    /// <summary>
    /// 空恢复码序列化为空数组
    /// </summary>
    [Fact]
    public void 空恢复码序列化为空数组()
    {
        Assert.Equal("[]", AuthUserMapper.SerializeRecoveryCodes([]));
        Assert.Equal("[]", AuthUserMapper.SerializeRecoveryCodes(null));
        Assert.Empty(AuthUserMapper.DeserializeRecoveryCodes(null));
        Assert.Empty(AuthUserMapper.DeserializeRecoveryCodes(" "));
    }

    /// <summary>
    /// 附加数据往返后值为 JsonElement
    /// </summary>
    [Fact]
    public void 附加数据往返后值为JsonElement()
    {
        var json = AuthUserMapper.SerializeAdditionalData(new Dictionary<string, object> { ["source"] = "import" });

        var restored = AuthUserMapper.DeserializeAdditionalData(json);

        Assert.NotNull(restored);
        var value = Assert.IsType<JsonElement>(restored["source"]);
        Assert.Equal("import", value.GetString());
    }

    /// <summary>
    /// 空附加数据不写入
    /// </summary>
    [Fact]
    public void 空附加数据不写入()
    {
        Assert.Null(AuthUserMapper.SerializeAdditionalData(null));
        Assert.Null(AuthUserMapper.DeserializeAdditionalData(null));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到 `AuthUserMapper` / 命名空间 `XiHan.Framework.Authentication.SqlSugar.Mapping`。

- [ ] **Step 3: 创建时间换算**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Mapping/StorageTime.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.Authentication.SqlSugar.Mapping;

/// <summary>
/// 存储层时间换算
/// </summary>
/// <remarks>
/// 写入前把时间换算为 UTC，未指定时区的值按 UTC 处理；读出后把时间标记为 UTC，不做换算。
/// </remarks>
public static class StorageTime
{
    /// <summary>
    /// 把写入值换算为 UTC
    /// </summary>
    /// <param name="value">写入值</param>
    /// <returns>UTC 时间</returns>
    public static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
    }

    /// <summary>
    /// 把可空写入值换算为 UTC
    /// </summary>
    /// <param name="value">写入值</param>
    /// <returns>UTC 时间，输入为空时返回空</returns>
    public static DateTime? ToUtc(DateTime? value)
    {
        return value.HasValue ? ToUtc(value.Value) : null;
    }

    /// <summary>
    /// 把读出值标记为 UTC
    /// </summary>
    /// <param name="value">读出值</param>
    /// <returns>标记为 UTC 的时间</returns>
    public static DateTime FromStorage(DateTime value)
    {
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>
    /// 把可空读出值标记为 UTC
    /// </summary>
    /// <param name="value">读出值</param>
    /// <returns>标记为 UTC 的时间，输入为空时返回空</returns>
    public static DateTime? FromStorage(DateTime? value)
    {
        return value.HasValue ? FromStorage(value.Value) : null;
    }
}
```

- [ ] **Step 4: 创建用户映射**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Mapping/AuthUserMapper.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Mapping;

/// <summary>
/// 用户信息与用户实体的双向映射
/// </summary>
public static class AuthUserMapper
{
    /// <summary>
    /// 规范化用户名
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <returns>按不变区域转为大写的用户名</returns>
    public static string NormalizeUserName(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return userName.ToUpperInvariant();
    }

    /// <summary>
    /// 把用户信息映射为实体
    /// </summary>
    /// <remarks>
    /// 忽略 <see cref="UserInfo.UserId"/>，主键取 <paramref name="basicId"/>。
    /// </remarks>
    /// <param name="user">用户信息</param>
    /// <param name="basicId">用户标识</param>
    /// <param name="tenantId">租户标识</param>
    /// <returns>用户实体</returns>
    public static SysAuthUser ToEntity(UserInfo user, long basicId, long tenantId)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new SysAuthUser(basicId)
        {
            TenantId = tenantId,
            UserName = user.Username,
            NormalizedUserName = NormalizeUserName(user.Username),
            PasswordHash = user.PasswordHash,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            TwoFactorEnabled = user.TwoFactorEnabled,
            TwoFactorSecret = user.TwoFactorSecret,
            RecoveryCodes = SerializeRecoveryCodes(user.RecoveryCodes),
            IsLocked = user.IsLocked,
            LockoutEnd = StorageTime.ToUtc(user.LockoutEnd),
            FailedLoginAttempts = user.FailedLoginAttempts,
            LastLoginTime = StorageTime.ToUtc(user.LastLoginTime),
            PasswordChangedTime = StorageTime.ToUtc(user.PasswordChangedTime),
            IsActive = user.IsActive,
            AdditionalData = SerializeAdditionalData(user.AdditionalData)
        };
    }

    /// <summary>
    /// 把实体映射为用户信息
    /// </summary>
    /// <param name="entity">用户实体</param>
    /// <returns>用户信息</returns>
    public static UserInfo ToUserInfo(SysAuthUser entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new UserInfo
        {
            UserId = entity.BasicId.ToString(CultureInfo.InvariantCulture),
            Username = entity.UserName,
            PasswordHash = entity.PasswordHash,
            Email = entity.Email,
            PhoneNumber = entity.PhoneNumber,
            TwoFactorEnabled = entity.TwoFactorEnabled,
            TwoFactorSecret = entity.TwoFactorSecret,
            RecoveryCodes = DeserializeRecoveryCodes(entity.RecoveryCodes),
            IsLocked = entity.IsLocked,
            LockoutEnd = StorageTime.FromStorage(entity.LockoutEnd),
            FailedLoginAttempts = entity.FailedLoginAttempts,
            LastLoginTime = StorageTime.FromStorage(entity.LastLoginTime),
            PasswordChangedTime = StorageTime.FromStorage(entity.PasswordChangedTime),
            IsActive = entity.IsActive,
            AdditionalData = DeserializeAdditionalData(entity.AdditionalData)
        };
    }

    /// <summary>
    /// 序列化恢复码
    /// </summary>
    /// <param name="recoveryCodes">恢复码哈希列表</param>
    /// <returns>JSON 数组，输入为空时返回空数组</returns>
    public static string SerializeRecoveryCodes(List<string>? recoveryCodes)
    {
        return JsonSerializer.Serialize(recoveryCodes ?? []);
    }

    /// <summary>
    /// 反序列化恢复码
    /// </summary>
    /// <param name="json">JSON 数组</param>
    /// <returns>恢复码哈希列表，输入为空白时返回空列表</returns>
    public static List<string> DeserializeRecoveryCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    /// <summary>
    /// 序列化附加数据
    /// </summary>
    /// <param name="additionalData">附加数据</param>
    /// <returns>JSON 对象，输入为空时返回空</returns>
    public static string? SerializeAdditionalData(Dictionary<string, object>? additionalData)
    {
        return additionalData is null ? null : JsonSerializer.Serialize(additionalData);
    }

    /// <summary>
    /// 反序列化附加数据
    /// </summary>
    /// <remarks>
    /// 值以 <see cref="JsonElement"/> 返回。
    /// </remarks>
    /// <param name="json">JSON 对象</param>
    /// <returns>附加数据，输入为空白时返回空</returns>
    public static Dictionary<string, object>? DeserializeAdditionalData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 新增用户信息与实体的双向映射"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 4: 用户存储的读取与添加

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Users/SqlSugarUserStore.cs`
- Modify: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/UserStoreReadTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SysAuthUser`、夹具；Task 3 的 `AuthUserMapper`
- Produces:
  - `SqlSugarUserStore(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator)`（Task 5 追加第四个参数）
  - `Task<UserInfo?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)`
  - `Task<UserInfo?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)`
  - `Task<string> AddUserAsync(UserInfo user, CancellationToken cancellationToken = default)`
  - 其余 7 个契约方法本任务抛 `NotImplementedException`，Task 5 整份替换本文件时实现
  - 夹具新增 `SqlSugarUserStore CreateUserStore()`

**参考来源（动手前先读）：**
- 契约：`framework/src/XiHan.Framework.Authentication/Users/IUserStore.cs`
- 内存语义：`framework/src/XiHan.Framework.Authentication/Users/DefaultUserStore.cs:35-63`、`178-212`
- 存储语义：spec 第 4.4 节

**本任务禁止事项：** 硬约束 ①④。读取方法必须**先查库、再取映射**，不要在查库之前按标识从映射里直接返回（租户切换后会拿到别的租户的用户）。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/UserStoreReadTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 用户存储读取与添加测试
/// </summary>
public class UserStoreReadTests
{
    /// <summary>
    /// 按用户名查找不区分大小写
    /// </summary>
    [Fact]
    public async Task 按用户名查找不区分大小写()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("Alice"));

        var found = await context.CreateUserStore().GetUserByUsernameAsync("ALICE");

        Assert.NotNull(found);
        Assert.Equal("Alice", found.Username);
    }

    /// <summary>
    /// 按实体类型解析客户端
    /// </summary>
    [Fact]
    public async Task 按实体类型解析客户端()
    {
        using var context = new AuthenticationTestContext();

        await context.CreateUserStore().GetUserByUsernameAsync("alice");

        Assert.Contains(typeof(SysAuthUser), context.Resolver.RequestedEntityTypes);
    }

    /// <summary>
    /// 按用户标识查找
    /// </summary>
    [Fact]
    public async Task 按用户标识查找()
    {
        using var context = new AuthenticationTestContext();
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        var found = await context.CreateUserStore().GetUserByIdAsync(userId);

        Assert.NotNull(found);
        Assert.Equal(userId, found.UserId);
        Assert.Equal("alice", found.Username);
    }

    /// <summary>
    /// 非正整数的用户标识返回空
    /// </summary>
    [Fact]
    public async Task 非正整数的用户标识返回空()
    {
        using var context = new AuthenticationTestContext();
        var store = context.CreateUserStore();

        Assert.Null(await store.GetUserByIdAsync("abc"));
        Assert.Null(await store.GetUserByIdAsync(""));
        Assert.Null(await store.GetUserByIdAsync("-1"));
        Assert.Null(await store.GetUserByIdAsync("0"));
    }

    /// <summary>
    /// 空白用户名返回空
    /// </summary>
    [Fact]
    public async Task 空白用户名返回空()
    {
        using var context = new AuthenticationTestContext();

        Assert.Null(await context.CreateUserStore().GetUserByUsernameAsync(" "));
    }

    /// <summary>
    /// 不同租户的同名用户互不可见
    /// </summary>
    [Fact]
    public async Task 不同租户的同名用户互不可见()
    {
        using var context = new AuthenticationTestContext();
        context.Tenant.Id = 1;
        await context.CreateUserStore().AddUserAsync(NewUser("alice", "a1@example.com"));
        context.Tenant.Id = 2;
        await context.CreateUserStore().AddUserAsync(NewUser("alice", "a2@example.com"));

        context.Tenant.Id = 1;
        var inFirst = await context.CreateUserStore().GetUserByUsernameAsync("alice");
        context.Tenant.Id = 2;
        var inSecond = await context.CreateUserStore().GetUserByUsernameAsync("alice");

        Assert.Equal("a1@example.com", inFirst?.Email);
        Assert.Equal("a2@example.com", inSecond?.Email);
    }

    /// <summary>
    /// 平台上下文看不到租户用户
    /// </summary>
    [Fact]
    public async Task 平台上下文看不到租户用户()
    {
        using var context = new AuthenticationTestContext();
        context.Tenant.Id = 1;
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        context.Tenant.Id = null;

        Assert.Null(await context.CreateUserStore().GetUserByUsernameAsync("alice"));
    }

    /// <summary>
    /// 租户上下文看不到平台用户
    /// </summary>
    [Fact]
    public async Task 租户上下文看不到平台用户()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("admin"));

        context.Tenant.Id = 1;

        Assert.Null(await context.CreateUserStore().GetUserByUsernameAsync("admin"));
    }

    /// <summary>
    /// 按标识查找不跨租户
    /// </summary>
    [Fact]
    public async Task 按标识查找不跨租户()
    {
        using var context = new AuthenticationTestContext();
        context.Tenant.Id = 1;
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        context.Tenant.Id = 2;

        Assert.Null(await context.CreateUserStore().GetUserByIdAsync(userId));
    }

    /// <summary>
    /// 同一作用域内重复查找返回同一实例
    /// </summary>
    [Fact]
    public async Task 同一作用域内重复查找返回同一实例()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        var store = context.CreateUserStore();

        var byName = await store.GetUserByUsernameAsync("alice");
        Assert.NotNull(byName);
        var byId = await store.GetUserByIdAsync(byName.UserId);

        Assert.Same(byName, byId);
    }

    /// <summary>
    /// 不同作用域返回不同实例
    /// </summary>
    [Fact]
    public async Task 不同作用域返回不同实例()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        var first = await context.CreateUserStore().GetUserByUsernameAsync("alice");
        var second = await context.CreateUserStore().GetUserByUsernameAsync("alice");

        Assert.NotSame(first, second);
    }

    /// <summary>
    /// 添加用户生成雪花标识并回写
    /// </summary>
    [Fact]
    public async Task 添加用户生成雪花标识并回写()
    {
        using var context = new AuthenticationTestContext();
        var user = NewUser("alice");

        var userId = await context.CreateUserStore().AddUserAsync(user);

        Assert.True(long.Parse(userId) > 0);
        Assert.Equal(userId, user.UserId);
    }

    /// <summary>
    /// 添加用户沿用调用方给定的数字标识
    /// </summary>
    [Fact]
    public async Task 添加用户沿用调用方给定的数字标识()
    {
        using var context = new AuthenticationTestContext();
        var user = NewUser("alice");
        user.UserId = "123456789";

        var userId = await context.CreateUserStore().AddUserAsync(user);

        Assert.Equal("123456789", userId);
        Assert.NotNull(await context.CreateUserStore().GetUserByIdAsync("123456789"));
    }

    /// <summary>
    /// 添加非数字标识的用户抛出参数异常
    /// </summary>
    [Fact]
    public async Task 添加非数字标识的用户抛出参数异常()
    {
        using var context = new AuthenticationTestContext();
        var user = NewUser("alice");
        user.UserId = "alice-id";

        await Assert.ThrowsAsync<ArgumentException>(() => context.CreateUserStore().AddUserAsync(user));
    }

    /// <summary>
    /// 添加空白用户名抛出参数异常
    /// </summary>
    [Fact]
    public async Task 添加空白用户名抛出参数异常()
    {
        using var context = new AuthenticationTestContext();

        await Assert.ThrowsAsync<ArgumentException>(() => context.CreateUserStore().AddUserAsync(NewUser(" ")));
    }

    /// <summary>
    /// 用户名仅大小写不同也视为重复
    /// </summary>
    [Fact]
    public async Task 用户名仅大小写不同也视为重复()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("Alice"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.CreateUserStore().AddUserAsync(NewUser("alice")));
    }

    /// <summary>
    /// 添加用户原样保存密码哈希
    /// </summary>
    [Fact]
    public async Task 添加用户原样保存密码哈希()
    {
        using var context = new AuthenticationTestContext();
        var user = NewUser("alice");
        user.PasswordHash = "1:1000:SHA256:c2FsdA==:aGFzaA==";

        await context.CreateUserStore().AddUserAsync(user);

        var entity = context.Client.Queryable<SysAuthUser>().First();
        Assert.Equal("1:1000:SHA256:c2FsdA==:aGFzaA==", entity.PasswordHash);
        Assert.Equal("alice", entity.UserName);
        Assert.Equal("ALICE", entity.NormalizedUserName);
    }

    private static UserInfo NewUser(string username, string? email = null)
    {
        return new UserInfo
        {
            Username = username,
            PasswordHash = "hash",
            Email = email,
            IsActive = true
        };
    }
}
```

- [ ] **Step 2: 给夹具加存储工厂**

修改 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`。

using 区追加：

```csharp
using XiHan.Framework.Authentication.SqlSugar.Users;
```

在 `IdGenerator` 属性之后、`Dispose` 之前插入：

```csharp
    /// <summary>
    /// 创建用户存储，每次返回新实例，代表一个新的请求作用域
    /// </summary>
    /// <returns>用户存储</returns>
    public SqlSugarUserStore CreateUserStore()
    {
        return new SqlSugarUserStore(Resolver, Tenant, IdGenerator);
    }
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到 `SqlSugarUserStore` / 命名空间 `XiHan.Framework.Authentication.SqlSugar.Users`。

- [ ] **Step 4: 创建用户存储（读取与添加）**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Users/SqlSugarUserStore.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using SqlSugar;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Authentication.Users;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.Users;

/// <summary>
/// 用户 SqlSugar 存储
/// </summary>
/// <remarks>
/// 读写都限定在当前租户内。同一实例内对同一用户的多次读取返回同一个 <see cref="UserInfo"/> 实例。
/// </remarks>
public class SqlSugarUserStore : IUserStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly Dictionary<long, UserInfo> _loadedUsers = [];

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="idGenerator">主键生成器</param>
    public SqlSugarUserStore(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IDistributedIdGenerator<long> idGenerator)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _idGenerator = idGenerator;
    }

    /// <summary>
    /// 根据用户名获取用户，不区分大小写
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户信息，不存在时返回空</returns>
    public async Task<UserInfo?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var entity = await FindByUserNameAsync(GetClient(), GetTenantId(), username);

        return entity is null ? null : Track(entity);
    }

    /// <summary>
    /// 根据用户标识获取用户
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户信息，不存在或标识不是正整数时返回空</returns>
    public async Task<UserInfo?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseUserId(userId, out var id))
        {
            return null;
        }

        var tenantId = GetTenantId();
        var entity = await GetClient().Queryable<SysAuthUser>()
            .Where(item => item.BasicId == id && item.TenantId == tenantId)
            .FirstAsync();

        return entity is null ? null : Track(entity);
    }

    /// <summary>
    /// 更新用户信息
    /// </summary>
    /// <param name="user">用户信息</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpdateUserAsync(UserInfo user, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 更新用户密码
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="passwordHash">密码哈希</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpdatePasswordAsync(string userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 获取登录失败次数
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>失败次数</returns>
    public Task<int> GetFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 记录登录失败
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task IncrementFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 重置登录失败次数
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task ResetFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 设置账户锁定时间
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="lockoutEnd">锁定结束时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task SetLockoutEndAsync(string username, DateTime? lockoutEnd, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 获取账户锁定结束时间
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>锁定结束时间</returns>
    public Task<DateTime?> GetLockoutEndAsync(string username, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 添加用户
    /// </summary>
    /// <remarks>
    /// 密码哈希按原样写入。用户标识为空时生成新标识并回写到 <paramref name="user"/>。
    /// </remarks>
    /// <param name="user">用户信息</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户标识</returns>
    /// <exception cref="ArgumentException">用户名为空白，或用户标识不是正整数</exception>
    /// <exception cref="InvalidOperationException">当前租户内已存在同名用户（不区分大小写）</exception>
    public async Task<string> AddUserAsync(UserInfo user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            throw new ArgumentException("用户名不能为空", nameof(user));
        }

        long id;
        if (string.IsNullOrWhiteSpace(user.UserId))
        {
            id = _idGenerator.NextId();
        }
        else if (!TryParseUserId(user.UserId, out id))
        {
            throw new ArgumentException("用户ID必须是正整数", nameof(user));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = GetClient();
        var tenantId = GetTenantId();

        if (await FindByUserNameAsync(client, tenantId, user.Username) is not null)
        {
            throw new InvalidOperationException($"用户名 {user.Username} 已存在");
        }

        await client.Insertable(AuthUserMapper.ToEntity(user, id, tenantId)).ExecuteCommandAsync();

        user.UserId = id.ToString(CultureInfo.InvariantCulture);
        _loadedUsers[id] = user;

        return user.UserId;
    }

    private ISqlSugarClient GetClient()
    {
        return _clientResolver.GetClientForEntity<SysAuthUser>();
    }

    private long GetTenantId()
    {
        return _currentTenant.Id ?? 0;
    }

    private UserInfo Track(SysAuthUser entity)
    {
        if (_loadedUsers.TryGetValue(entity.BasicId, out var loaded))
        {
            return loaded;
        }

        var user = AuthUserMapper.ToUserInfo(entity);
        _loadedUsers[entity.BasicId] = user;

        return user;
    }

    private static async Task<SysAuthUser?> FindByUserNameAsync(ISqlSugarClient client, long tenantId, string username)
    {
        var normalizedUserName = AuthUserMapper.NormalizeUserName(username);

        return await client.Queryable<SysAuthUser>()
            .Where(item => item.TenantId == tenantId && item.NormalizedUserName == normalizedUserName)
            .FirstAsync();
    }

    private static bool TryParseUserId(string? userId, out long id)
    {
        return long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
```

七个抛 `NotImplementedException` 的方法由 Task 5 整份替换本文件时实现；本任务的用例不调用它们。

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

若 `租户上下文看不到平台用户` 或 `按标识查找不跨租户` 失败，说明某条查询漏了 `item.TenantId == tenantId`（硬约束 ④）。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 用户存储支持按租户读取与添加用户"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 5: 用户存储的写入

**Files:**
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/Users/SqlSugarUserStore.cs`（整份替换）
- Modify: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/UserStoreWriteTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `SqlSugarUserStore`、夹具
- Produces:
  - 构造函数变为 `SqlSugarUserStore(ISqlSugarClientResolver clientResolver, ICurrentTenant currentTenant, IDistributedIdGenerator<long> idGenerator, TimeProvider timeProvider)`
  - 七个写入 / 计数 / 锁定方法的实现
  - 私有方法 `ExistsAsync`、`RefreshSecurityStateAsync`、`SyncSecurityState`

**参考来源（动手前先读）：**
- 调用方：`framework/src/XiHan.Framework.Authentication/Users/DefaultAuthenticationService.cs:57-118`、`232-270`、`316-351`、`403-495`
- 内存语义：`framework/src/XiHan.Framework.Authentication/Users/DefaultUserStore.cs:68-172`
- 字段自增写法：`E:/source/platfrom-admin/docs/SqlSugar-docs/更新數據.md` 第 2.2 节
- 陷阱：spec 第 5 节 ①②⑤⑥

**本任务禁止事项：** 硬约束 ①②③④⑤。不要用 `Updateable(entity)`（按对象更新）——统一用 `Updateable<SysAuthUser>().SetColumns(...).Where(...)`。`UpdateUserAsync` 的 `SetColumns` 里**不许出现 `PasswordHash`**。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/UserStoreWriteTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 用户存储写入测试
/// </summary>
public class UserStoreWriteTests
{
    private static readonly DateTime FutureLockoutEnd = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PastLockoutEnd = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 更新用户信息不改写密码哈希
    /// </summary>
    [Fact]
    public async Task 更新用户信息不改写密码哈希()
    {
        using var context = new AuthenticationTestContext();
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        var editor = context.CreateUserStore();
        var stale = await editor.GetUserByIdAsync(userId);
        Assert.NotNull(stale);
        await context.CreateUserStore().UpdatePasswordAsync(userId, "new-hash");

        stale.Email = "alice@example.com";
        await editor.UpdateUserAsync(stale);

        var stored = await context.CreateUserStore().GetUserByIdAsync(userId);
        Assert.NotNull(stored);
        Assert.Equal("new-hash", stored.PasswordHash);
        Assert.Equal("alice@example.com", stored.Email);
    }

    /// <summary>
    /// 更新用户信息写入其余字段
    /// </summary>
    [Fact]
    public async Task 更新用户信息写入其余字段()
    {
        using var context = new AuthenticationTestContext();
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        var store = context.CreateUserStore();
        var user = await store.GetUserByIdAsync(userId);
        Assert.NotNull(user);

        var lastLoginTime = new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc);
        user.Username = "Alice2";
        user.Email = "alice@example.com";
        user.PhoneNumber = "13800000000";
        user.TwoFactorEnabled = true;
        user.TwoFactorSecret = "JBSWY3DPEHPK3PXP";
        user.RecoveryCodes = ["hash-1", "hash-2"];
        user.IsActive = false;
        user.LastLoginTime = lastLoginTime;
        await store.UpdateUserAsync(user);

        var stored = await context.CreateUserStore().GetUserByUsernameAsync("alice2");
        Assert.NotNull(stored);
        Assert.Equal(userId, stored.UserId);
        Assert.Equal("Alice2", stored.Username);
        Assert.Equal("alice@example.com", stored.Email);
        Assert.Equal("13800000000", stored.PhoneNumber);
        Assert.True(stored.TwoFactorEnabled);
        Assert.Equal("JBSWY3DPEHPK3PXP", stored.TwoFactorSecret);
        string[] expectedCodes = ["hash-1", "hash-2"];
        Assert.Equal(expectedCodes, stored.RecoveryCodes);
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.LastLoginTime);
        Assert.Equal(lastLoginTime, stored.LastLoginTime.Value);
        Assert.Equal(DateTimeKind.Utc, stored.LastLoginTime.Value.Kind);
    }

    /// <summary>
    /// 更新不存在的用户抛出
    /// </summary>
    [Fact]
    public async Task 更新不存在的用户抛出()
    {
        using var context = new AuthenticationTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.CreateUserStore().UpdateUserAsync(new UserInfo { UserId = "999", Username = "ghost" }));
    }

    /// <summary>
    /// 更新非数字用户标识抛出参数异常
    /// </summary>
    [Fact]
    public async Task 更新非数字用户标识抛出参数异常()
    {
        using var context = new AuthenticationTestContext();

        await Assert.ThrowsAsync<ArgumentException>(
            () => context.CreateUserStore().UpdateUserAsync(new UserInfo { UserId = "abc", Username = "ghost" }));
    }

    /// <summary>
    /// 更新其他租户的用户视为不存在
    /// </summary>
    [Fact]
    public async Task 更新其他租户的用户视为不存在()
    {
        using var context = new AuthenticationTestContext();
        context.Tenant.Id = 1;
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        context.Tenant.Id = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.CreateUserStore().UpdateUserAsync(new UserInfo { UserId = userId, Username = "alice" }));
    }

    /// <summary>
    /// 更新密码原样写入哈希
    /// </summary>
    [Fact]
    public async Task 更新密码原样写入哈希()
    {
        using var context = new AuthenticationTestContext();
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        await context.CreateUserStore().UpdatePasswordAsync(userId, "1:1000:SHA256:c2FsdA==:aGFzaA==");

        var entity = context.Client.Queryable<SysAuthUser>().First();
        Assert.Equal("1:1000:SHA256:c2FsdA==:aGFzaA==", entity.PasswordHash);
    }

    /// <summary>
    /// 更新密码的参数校验
    /// </summary>
    [Fact]
    public async Task 更新密码的参数校验()
    {
        using var context = new AuthenticationTestContext();
        var userId = await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        var store = context.CreateUserStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdatePasswordAsync(userId, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdatePasswordAsync(" ", "hash"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdatePasswordAsync("999", "hash"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdatePasswordAsync("abc", "hash"));
    }

    /// <summary>
    /// 失败次数在数据库侧累加
    /// </summary>
    [Fact]
    public async Task 失败次数在数据库侧累加()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        var first = context.CreateUserStore();
        var second = context.CreateUserStore();
        await first.GetUserByUsernameAsync("alice");

        await second.IncrementFailedLoginAttemptsAsync("alice");
        await first.IncrementFailedLoginAttemptsAsync("alice");

        Assert.Equal(2, await context.CreateUserStore().GetFailedLoginAttemptsAsync("alice"));
    }

    /// <summary>
    /// 重置失败次数
    /// </summary>
    [Fact]
    public async Task 重置失败次数()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        var store = context.CreateUserStore();
        await store.IncrementFailedLoginAttemptsAsync("alice");
        await store.IncrementFailedLoginAttemptsAsync("alice");

        await store.ResetFailedLoginAttemptsAsync("alice");

        Assert.Equal(0, await context.CreateUserStore().GetFailedLoginAttemptsAsync("alice"));
    }

    /// <summary>
    /// 写入方法同步更新已加载的实例
    /// </summary>
    [Fact]
    public async Task 写入方法同步更新已加载的实例()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        var store = context.CreateUserStore();
        var user = await store.GetUserByUsernameAsync("alice");
        Assert.NotNull(user);

        await store.UpdatePasswordAsync(user.UserId, "new-hash");
        await store.IncrementFailedLoginAttemptsAsync("alice");
        await store.SetLockoutEndAsync("alice", FutureLockoutEnd);

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.True(user.IsLocked);
        Assert.Equal(FutureLockoutEnd, user.LockoutEnd);
    }

    /// <summary>
    /// 锁定结束时间在未来时标记为锁定
    /// </summary>
    [Fact]
    public async Task 锁定结束时间在未来时标记为锁定()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        await context.CreateUserStore().SetLockoutEndAsync("alice", FutureLockoutEnd);

        var store = context.CreateUserStore();
        var lockoutEnd = await store.GetLockoutEndAsync("alice");
        var user = await store.GetUserByUsernameAsync("alice");
        Assert.NotNull(lockoutEnd);
        Assert.Equal(FutureLockoutEnd, lockoutEnd.Value);
        Assert.Equal(DateTimeKind.Utc, lockoutEnd.Value.Kind);
        Assert.True(user?.IsLocked);
    }

    /// <summary>
    /// 锁定结束时间已过去时不标记为锁定
    /// </summary>
    [Fact]
    public async Task 锁定结束时间已过去时不标记为锁定()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        await context.CreateUserStore().SetLockoutEndAsync("alice", PastLockoutEnd);

        var user = await context.CreateUserStore().GetUserByUsernameAsync("alice");
        Assert.False(user?.IsLocked);
        Assert.Equal(PastLockoutEnd, user?.LockoutEnd);
    }

    /// <summary>
    /// 清除锁定结束时间
    /// </summary>
    [Fact]
    public async Task 清除锁定结束时间()
    {
        using var context = new AuthenticationTestContext();
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        await context.CreateUserStore().SetLockoutEndAsync("alice", FutureLockoutEnd);

        await context.CreateUserStore().SetLockoutEndAsync("alice", null);

        var store = context.CreateUserStore();
        Assert.Null(await store.GetLockoutEndAsync("alice"));
        Assert.False((await store.GetUserByUsernameAsync("alice"))?.IsLocked);
    }

    /// <summary>
    /// 计数操作不跨租户
    /// </summary>
    [Fact]
    public async Task 计数操作不跨租户()
    {
        using var context = new AuthenticationTestContext();
        context.Tenant.Id = 1;
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));
        context.Tenant.Id = 2;
        await context.CreateUserStore().AddUserAsync(NewUser("alice"));

        await context.CreateUserStore().IncrementFailedLoginAttemptsAsync("alice");

        Assert.Equal(1, await context.CreateUserStore().GetFailedLoginAttemptsAsync("alice"));
        context.Tenant.Id = 1;
        Assert.Equal(0, await context.CreateUserStore().GetFailedLoginAttemptsAsync("alice"));
    }

    /// <summary>
    /// 不存在的用户名计数操作静默忽略
    /// </summary>
    [Fact]
    public async Task 不存在的用户名计数操作静默忽略()
    {
        using var context = new AuthenticationTestContext();
        var store = context.CreateUserStore();

        await store.IncrementFailedLoginAttemptsAsync("ghost");
        await store.ResetFailedLoginAttemptsAsync("ghost");
        await store.SetLockoutEndAsync("ghost", FutureLockoutEnd);

        Assert.Equal(0, await store.GetFailedLoginAttemptsAsync("ghost"));
        Assert.Null(await store.GetLockoutEndAsync("ghost"));
        Assert.Equal(0, await context.Client.Queryable<SysAuthUser>().CountAsync());
    }

    private static UserInfo NewUser(string username)
    {
        return new UserInfo
        {
            Username = username,
            PasswordHash = "hash",
            IsActive = true
        };
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：`UserStoreWriteTests` 的 15 个用例全部失败于 `System.NotImplementedException`；Task 2–4 的用例保持 PASS。

- [ ] **Step 3: 整份替换用户存储**

把 `framework/src/XiHan.Framework.Authentication.SqlSugar/Users/SqlSugarUserStore.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using SqlSugar;
using XiHan.Framework.Authentication.SqlSugar.Entities;
using XiHan.Framework.Authentication.SqlSugar.Mapping;
using XiHan.Framework.Authentication.Users;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.DistributedIds;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.Framework.Authentication.SqlSugar.Users;

/// <summary>
/// 用户 SqlSugar 存储
/// </summary>
/// <remarks>
/// 读写都限定在当前租户内。同一实例内对同一用户的多次读取返回同一个 <see cref="UserInfo"/> 实例，
/// 更新密码、失败计数与锁定时间的方法会同步修改该实例。
/// </remarks>
public class SqlSugarUserStore : IUserStore
{
    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDistributedIdGenerator<long> _idGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<long, UserInfo> _loadedUsers = [];

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="idGenerator">主键生成器</param>
    /// <param name="timeProvider">时间提供程序</param>
    public SqlSugarUserStore(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IDistributedIdGenerator<long> idGenerator,
        TimeProvider timeProvider)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _idGenerator = idGenerator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 根据用户名获取用户，不区分大小写
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户信息，不存在时返回空</returns>
    public async Task<UserInfo?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var entity = await FindByUserNameAsync(GetClient(), GetTenantId(), username);

        return entity is null ? null : Track(entity);
    }

    /// <summary>
    /// 根据用户标识获取用户
    /// </summary>
    /// <param name="userId">用户标识</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户信息，不存在或标识不是正整数时返回空</returns>
    public async Task<UserInfo?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseUserId(userId, out var id))
        {
            return null;
        }

        var tenantId = GetTenantId();
        var entity = await GetClient().Queryable<SysAuthUser>()
            .Where(item => item.BasicId == id && item.TenantId == tenantId)
            .FirstAsync();

        return entity is null ? null : Track(entity);
    }

    /// <summary>
    /// 更新用户信息
    /// </summary>
    /// <remarks>
    /// 写入除密码哈希外的全部字段；密码只经 <see cref="UpdatePasswordAsync"/> 修改。
    /// </remarks>
    /// <param name="user">用户信息</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <exception cref="ArgumentException">用户信息为空、用户标识不是正整数或用户名为空白</exception>
    /// <exception cref="InvalidOperationException">当前租户内不存在该用户</exception>
    public async Task UpdateUserAsync(UserInfo user, CancellationToken cancellationToken = default)
    {
        if (user is null || !TryParseUserId(user.UserId, out var id))
        {
            throw new ArgumentException("用户信息或用户ID无效", nameof(user));
        }

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            throw new ArgumentException("用户名不能为空", nameof(user));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = GetClient();
        var tenantId = GetTenantId();
        var values = AuthUserMapper.ToEntity(user, id, tenantId);
        var userName = values.UserName;
        var normalizedUserName = values.NormalizedUserName;
        var email = values.Email;
        var phoneNumber = values.PhoneNumber;
        var twoFactorEnabled = values.TwoFactorEnabled;
        var twoFactorSecret = values.TwoFactorSecret;
        var recoveryCodes = values.RecoveryCodes;
        var isLocked = values.IsLocked;
        var lockoutEnd = values.LockoutEnd;
        var failedLoginAttempts = values.FailedLoginAttempts;
        var lastLoginTime = values.LastLoginTime;
        var passwordChangedTime = values.PasswordChangedTime;
        var isActive = values.IsActive;
        var additionalData = values.AdditionalData;

        var affected = await client.Updateable<SysAuthUser>()
            .SetColumns(item => new SysAuthUser
            {
                UserName = userName,
                NormalizedUserName = normalizedUserName,
                Email = email,
                PhoneNumber = phoneNumber,
                TwoFactorEnabled = twoFactorEnabled,
                TwoFactorSecret = twoFactorSecret,
                RecoveryCodes = recoveryCodes,
                IsLocked = isLocked,
                LockoutEnd = lockoutEnd,
                FailedLoginAttempts = failedLoginAttempts,
                LastLoginTime = lastLoginTime,
                PasswordChangedTime = passwordChangedTime,
                IsActive = isActive,
                AdditionalData = additionalData
            })
            .Where(item => item.BasicId == id && item.TenantId == tenantId)
            .ExecuteCommandAsync();

        if (affected == 0 && !await ExistsAsync(client, id, tenantId))
        {
            throw new InvalidOperationException($"用户 {user.UserId} 不存在");
        }

        _loadedUsers[id] = user;
    }

    /// <summary>
    /// 更新用户密码
    /// </summary>
    /// <remarks>
    /// 密码哈希按原样写入。
    /// </remarks>
    /// <param name="userId">用户标识</param>
    /// <param name="passwordHash">密码哈希</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <exception cref="ArgumentException">用户标识或密码哈希为空白</exception>
    /// <exception cref="InvalidOperationException">当前租户内不存在该用户</exception>
    public async Task UpdatePasswordAsync(string userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("用户ID和密码哈希不能为空");
        }

        if (!TryParseUserId(userId, out var id))
        {
            throw new InvalidOperationException($"用户 {userId} 不存在");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = GetClient();
        var tenantId = GetTenantId();

        var affected = await client.Updateable<SysAuthUser>()
            .SetColumns(item => new SysAuthUser { PasswordHash = passwordHash })
            .Where(item => item.BasicId == id && item.TenantId == tenantId)
            .ExecuteCommandAsync();

        if (affected == 0 && !await ExistsAsync(client, id, tenantId))
        {
            throw new InvalidOperationException($"用户 {userId} 不存在");
        }

        if (_loadedUsers.TryGetValue(id, out var loaded))
        {
            loaded.PasswordHash = passwordHash;
        }
    }

    /// <summary>
    /// 获取登录失败次数
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>失败次数，用户不存在时返回 0</returns>
    public async Task<int> GetFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return 0;
        }

        var entity = await RefreshSecurityStateAsync(GetClient(), GetTenantId(), username);

        return entity?.FailedLoginAttempts ?? 0;
    }

    /// <summary>
    /// 记录登录失败，失败次数在数据库侧加一
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    public async Task IncrementFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var client = GetClient();
        var tenantId = GetTenantId();
        var normalizedUserName = AuthUserMapper.NormalizeUserName(username);

        await client.Updateable<SysAuthUser>()
            .SetColumns(item => item.FailedLoginAttempts == item.FailedLoginAttempts + 1)
            .Where(item => item.TenantId == tenantId && item.NormalizedUserName == normalizedUserName)
            .ExecuteCommandAsync();

        await RefreshSecurityStateAsync(client, tenantId, username);
    }

    /// <summary>
    /// 重置登录失败次数
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    public async Task ResetFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var client = GetClient();
        var tenantId = GetTenantId();
        var normalizedUserName = AuthUserMapper.NormalizeUserName(username);

        await client.Updateable<SysAuthUser>()
            .SetColumns(item => new SysAuthUser { FailedLoginAttempts = 0 })
            .Where(item => item.TenantId == tenantId && item.NormalizedUserName == normalizedUserName)
            .ExecuteCommandAsync();

        await RefreshSecurityStateAsync(client, tenantId, username);
    }

    /// <summary>
    /// 设置账户锁定时间
    /// </summary>
    /// <remarks>
    /// 锁定结束时间晚于当前时间时标记为锁定。
    /// </remarks>
    /// <param name="username">用户名</param>
    /// <param name="lockoutEnd">锁定结束时间，为空表示解除锁定</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    public async Task SetLockoutEndAsync(string username, DateTime? lockoutEnd, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var client = GetClient();
        var tenantId = GetTenantId();
        var normalizedUserName = AuthUserMapper.NormalizeUserName(username);
        var lockoutEndUtc = StorageTime.ToUtc(lockoutEnd);
        var isLocked = lockoutEndUtc.HasValue && lockoutEndUtc.Value > _timeProvider.GetUtcNow().UtcDateTime;

        await client.Updateable<SysAuthUser>()
            .SetColumns(item => new SysAuthUser
            {
                LockoutEnd = lockoutEndUtc,
                IsLocked = isLocked
            })
            .Where(item => item.TenantId == tenantId && item.NormalizedUserName == normalizedUserName)
            .ExecuteCommandAsync();

        await RefreshSecurityStateAsync(client, tenantId, username);
    }

    /// <summary>
    /// 获取账户锁定结束时间
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>锁定结束时间（UTC），用户不存在或未锁定时返回空</returns>
    public async Task<DateTime?> GetLockoutEndAsync(string username, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var entity = await RefreshSecurityStateAsync(GetClient(), GetTenantId(), username);

        return StorageTime.FromStorage(entity?.LockoutEnd);
    }

    /// <summary>
    /// 添加用户
    /// </summary>
    /// <remarks>
    /// 密码哈希按原样写入。用户标识为空时生成新标识并回写到 <paramref name="user"/>。
    /// </remarks>
    /// <param name="user">用户信息</param>
    /// <param name="cancellationToken">取消令牌，仅在访问数据库前检查</param>
    /// <returns>用户标识</returns>
    /// <exception cref="ArgumentException">用户名为空白，或用户标识不是正整数</exception>
    /// <exception cref="InvalidOperationException">当前租户内已存在同名用户（不区分大小写）</exception>
    public async Task<string> AddUserAsync(UserInfo user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            throw new ArgumentException("用户名不能为空", nameof(user));
        }

        long id;
        if (string.IsNullOrWhiteSpace(user.UserId))
        {
            id = _idGenerator.NextId();
        }
        else if (!TryParseUserId(user.UserId, out id))
        {
            throw new ArgumentException("用户ID必须是正整数", nameof(user));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = GetClient();
        var tenantId = GetTenantId();

        if (await FindByUserNameAsync(client, tenantId, user.Username) is not null)
        {
            throw new InvalidOperationException($"用户名 {user.Username} 已存在");
        }

        await client.Insertable(AuthUserMapper.ToEntity(user, id, tenantId)).ExecuteCommandAsync();

        user.UserId = id.ToString(CultureInfo.InvariantCulture);
        _loadedUsers[id] = user;

        return user.UserId;
    }

    private ISqlSugarClient GetClient()
    {
        return _clientResolver.GetClientForEntity<SysAuthUser>();
    }

    private long GetTenantId()
    {
        return _currentTenant.Id ?? 0;
    }

    private UserInfo Track(SysAuthUser entity)
    {
        if (_loadedUsers.TryGetValue(entity.BasicId, out var loaded))
        {
            return loaded;
        }

        var user = AuthUserMapper.ToUserInfo(entity);
        _loadedUsers[entity.BasicId] = user;

        return user;
    }

    private async Task<SysAuthUser?> RefreshSecurityStateAsync(ISqlSugarClient client, long tenantId, string username)
    {
        var entity = await FindByUserNameAsync(client, tenantId, username);
        if (entity is not null)
        {
            SyncSecurityState(entity);
        }

        return entity;
    }

    private void SyncSecurityState(SysAuthUser entity)
    {
        if (!_loadedUsers.TryGetValue(entity.BasicId, out var loaded))
        {
            return;
        }

        loaded.FailedLoginAttempts = entity.FailedLoginAttempts;
        loaded.IsLocked = entity.IsLocked;
        loaded.LockoutEnd = StorageTime.FromStorage(entity.LockoutEnd);
    }

    private static async Task<SysAuthUser?> FindByUserNameAsync(ISqlSugarClient client, long tenantId, string username)
    {
        var normalizedUserName = AuthUserMapper.NormalizeUserName(username);

        return await client.Queryable<SysAuthUser>()
            .Where(item => item.TenantId == tenantId && item.NormalizedUserName == normalizedUserName)
            .FirstAsync();
    }

    private static async Task<bool> ExistsAsync(ISqlSugarClient client, long id, long tenantId)
    {
        return await client.Queryable<SysAuthUser>()
            .Where(item => item.BasicId == id && item.TenantId == tenantId)
            .AnyAsync();
    }

    private static bool TryParseUserId(string? userId, out long id)
    {
        return long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
```

- [ ] **Step 4: 同步夹具的构造调用**

把 `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationTestContext.cs` 中 `CreateUserStore` 的方法体由：

```csharp
        return new SqlSugarUserStore(Resolver, Tenant, IdGenerator);
```

改为：

```csharp
        return new SqlSugarUserStore(Resolver, Tenant, IdGenerator, Clock);
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

逐项排查：

- `失败次数在数据库侧累加` 得到 1：`IncrementFailedLoginAttemptsAsync` 写成了读-改-写（硬约束 ③）
- `更新用户信息不改写密码哈希` 得到 `"hash"`：`UpdateUserAsync` 的 `SetColumns` 里出现了 `PasswordHash`（硬约束 ②）
- `写入方法同步更新已加载的实例` 失败：`UpdatePasswordAsync` 没改映射里的实例，或 `RefreshSecurityStateAsync` 没被调用
- `清除锁定结束时间` 失败于 SQL 异常：检查 `SetColumns` 的 `LockoutEnd = lockoutEndUtc` 在值为 `null` 时生成的 SQL；若 SqlSugar 在 T 形式下对 `null` 生成了非法 SQL，改用两次 bool 形式 `.SetColumns(item => item.LockoutEnd == lockoutEndUtc).SetColumns(item => item.IsLocked == isLocked)`，并在提交信息里记下这一点

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 用户存储支持更新、原子失败计数与锁定"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

提交信息正文写明：`UpdateUserAsync` 不写密码列、身份映射、失败计数原子累加三件事的理由（spec 第 5 节 ①②），代码注释里不写。

---

### Task 6: 认证服务端到端流程

**Files:**
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationFlowTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `SqlSugarUserStore`；主包的 `DefaultAuthenticationService`（命名空间 `XiHan.Framework.Authentication`）、`JwtTokenService`、`JwtOptions`、`DefaultRefreshTokenStore`（`XiHan.Framework.Authentication.Jwt`）、`OtpService`、`OtpOptions`（`XiHan.Framework.Authentication.Otp`）；`PasswordHasher`、`PasswordHasherOptions`、`PasswordPolicyOptions`（`XiHan.Framework.Security.Password`）
- Produces: 无（验证任务）

**参考来源（动手前先读）：**
- `framework/src/XiHan.Framework.Authentication/Users/DefaultAuthenticationService.cs` 全文
- `framework/src/XiHan.Framework.Security/Password/PasswordHasher.cs`（`NeedsRehash` 比较迭代次数，105-128 行）
- spec 第 1.2 节的调用顺序表、第 5 节 ①

**本任务禁止事项：** 硬约束 ②。每个流程的「执行」与「读库核对」**必须用不同的存储实例**（`context.CreateUserStore()` 各调一次）。不要为了让用例变绿去改 `DefaultAuthenticationService`。

本任务只新增用例。Task 5 正确时它们**直接通过**；任一失败都说明 Task 5 的身份映射或列选择有缺口，回 Task 5 修，不要改用例。

- [ ] **Step 1: 写流程测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationFlowTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.Jwt;
using XiHan.Framework.Authentication.Otp;
using XiHan.Framework.Authentication.Users;
using XiHan.Framework.Security.Password;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 认证服务在 SqlSugar 用户存储上的端到端流程测试
/// </summary>
public class AuthenticationFlowTests
{
    private const string OldPassword = "Rk4@Wz8&Hs3%";
    private const string NewPassword = "Tq7#Lm2!Vx9$";
    private const string WrongPassword = "Wrong#Pass9x";

    private static readonly PasswordHasher Hasher = CreateHasher(1000);

    /// <summary>
    /// 修改密码后旧密码失效新密码生效
    /// </summary>
    [Fact]
    public async Task 修改密码后旧密码失效新密码生效()
    {
        using var context = new AuthenticationTestContext();
        var userId = await SeedUserAsync(context);

        var changed = await CreateService(context.CreateUserStore()).ChangePasswordAsync(userId, OldPassword, NewPassword);

        Assert.True(changed);
        var withOld = await CreateService(context.CreateUserStore()).AuthenticateAsync("alice", OldPassword);
        var withNew = await CreateService(context.CreateUserStore()).AuthenticateAsync("alice", NewPassword);
        Assert.False(withOld.Succeeded);
        Assert.True(withNew.Succeeded);
    }

    /// <summary>
    /// 启用双因素后恢复码被保存
    /// </summary>
    [Fact]
    public async Task 启用双因素后恢复码被保存()
    {
        using var context = new AuthenticationTestContext();
        var userId = await SeedUserAsync(context);

        var setup = await CreateService(context.CreateUserStore()).EnableTwoFactorAuthenticationAsync(userId);

        var stored = await context.CreateUserStore().GetUserByIdAsync(userId);
        Assert.NotNull(stored);
        Assert.True(stored.TwoFactorEnabled);
        Assert.Equal(setup.Secret, stored.TwoFactorSecret);
        Assert.NotEmpty(stored.RecoveryCodes);
        Assert.Equal(setup.RecoveryCodes.Count, stored.RecoveryCodes.Count);
        Assert.DoesNotContain(setup.RecoveryCodes[0], stored.RecoveryCodes);
    }

    /// <summary>
    /// 恢复码使用一次后失效
    /// </summary>
    [Fact]
    public async Task 恢复码使用一次后失效()
    {
        using var context = new AuthenticationTestContext();
        var userId = await SeedUserAsync(context);
        var setup = await CreateService(context.CreateUserStore()).EnableTwoFactorAuthenticationAsync(userId);
        var code = setup.RecoveryCodes[0];

        var first = await CreateService(context.CreateUserStore()).VerifyRecoveryCodeAsync(userId, code);
        var second = await CreateService(context.CreateUserStore()).VerifyRecoveryCodeAsync(userId, code);

        Assert.True(first);
        Assert.False(second);
    }

    /// <summary>
    /// 登录成功后失败次数清零
    /// </summary>
    [Fact]
    public async Task 登录成功后失败次数清零()
    {
        using var context = new AuthenticationTestContext();
        await SeedUserAsync(context);
        await CreateService(context.CreateUserStore()).AuthenticateAsync("alice", WrongPassword);
        await CreateService(context.CreateUserStore()).AuthenticateAsync("alice", WrongPassword);
        Assert.Equal(2, await context.CreateUserStore().GetFailedLoginAttemptsAsync("alice"));

        var result = await CreateService(context.CreateUserStore()).AuthenticateAsync("alice", OldPassword);

        Assert.True(result.Succeeded);
        var stored = await context.CreateUserStore().GetUserByUsernameAsync("alice");
        Assert.NotNull(stored);
        Assert.Equal(0, stored.FailedLoginAttempts);
        Assert.NotNull(stored.LastLoginTime);
    }

    /// <summary>
    /// 连续失败达到阈值后账户锁定
    /// </summary>
    [Fact]
    public async Task 连续失败达到阈值后账户锁定()
    {
        using var context = new AuthenticationTestContext();
        await SeedUserAsync(context);
        var policy = new PasswordPolicyOptions { MaxFailedAccessAttempts = 3 };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await CreateService(context.CreateUserStore(), policy: policy).AuthenticateAsync("alice", WrongPassword);
        }

        var stored = await context.CreateUserStore().GetUserByUsernameAsync("alice");
        Assert.NotNull(stored);
        Assert.True(stored.IsLocked);
        Assert.NotNull(stored.LockoutEnd);

        var result = await CreateService(context.CreateUserStore(), policy: policy).AuthenticateAsync("alice", OldPassword);
        Assert.False(result.Succeeded);
        Assert.True(result.IsLockedOut);
    }

    /// <summary>
    /// 登录时重新哈希的密码被保存
    /// </summary>
    [Fact]
    public async Task 登录时重新哈希的密码被保存()
    {
        using var context = new AuthenticationTestContext();
        await SeedUserAsync(context);

        var result = await CreateService(context.CreateUserStore(), CreateHasher(2000)).AuthenticateAsync("alice", OldPassword);

        Assert.True(result.Succeeded);
        var stored = await context.CreateUserStore().GetUserByUsernameAsync("alice");
        Assert.NotNull(stored);
        Assert.StartsWith("1:2000:", stored.PasswordHash);
    }

    private static DefaultAuthenticationService CreateService(
        IUserStore store,
        PasswordHasher? hasher = null,
        PasswordPolicyOptions? policy = null)
    {
        var jwtTokenService = new JwtTokenService(
            Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                SecretKey = "xihan-authentication-sqlsugar-tests-secret-key-0123456789",
                Issuer = "xihan-tests",
                Audience = "xihan-tests"
            }),
            new DefaultRefreshTokenStore());

        return new DefaultAuthenticationService(
            store,
            hasher ?? Hasher,
            jwtTokenService,
            new OtpService(Microsoft.Extensions.Options.Options.Create(new OtpOptions())),
            Microsoft.Extensions.Options.Options.Create(policy ?? new PasswordPolicyOptions()));
    }

    private static PasswordHasher CreateHasher(int iterations)
    {
        return new PasswordHasher(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
        {
            Iterations = iterations
        }));
    }

    private static async Task<string> SeedUserAsync(AuthenticationTestContext context)
    {
        return await context.CreateUserStore().AddUserAsync(new UserInfo
        {
            Username = "alice",
            PasswordHash = Hasher.HashPassword(OldPassword),
            IsActive = true
        });
    }
}
```

两个密码都满足 `PasswordPolicyOptions` 默认策略（长度 ≥ 8、含大小写、数字、特殊字符，不含三连顺序字符与三连重复字符，不在弱口令表中）——`ChangePasswordAsync` 会校验新密码强度，改动它们前先对照 `DefaultAuthenticationService.cs:123-227`。

- [ ] **Step 2: 运行测试**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。

逐项排查（全部回 Task 5 修，不改用例）：

- `修改密码后旧密码失效新密码生效` 失败于 `withOld.Succeeded == true`：`UpdateUserAsync` 写了密码列
- `启用双因素后恢复码被保存` 失败于恢复码为空：身份映射失效——`GenerateRecoveryCodesAsync` 内部那次 `GetUserByIdAsync` 没有拿到外层的同一个实例
- `登录成功后失败次数清零` 得到 2：`ResetFailedLoginAttemptsAsync` 没把 0 同步到映射里的实例，随后的 `UpdateUserAsync` 把旧计数写回
- `连续失败达到阈值后账户锁定` 失败：检查 `SetLockoutEndAsync` 的 `isLocked` 计算与 `GetLockoutEndAsync` 的返回值

- [ ] **Step 3: 提交**

```bash
git add framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/AuthenticationFlowTests.cs
git commit -m "test(authentication-sqlsugar): 认证服务在落库用户存储上的端到端流程"
```

---

### Task 7: 服务注册

**Files:**
- Create: `framework/src/XiHan.Framework.Authentication.SqlSugar/Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs`
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/XiHanAuthenticationSqlSugarModule.cs`
- Create: `framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `SqlSugarUserStore`
- Produces: `IServiceCollection AddXiHanAuthenticationSqlSugar(this IServiceCollection services, IConfiguration configuration)`，命名空间 `XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection`

**参考来源（动手前先读）：**
- 主包注册：`framework/src/XiHan.Framework.Authentication/Extensions/DependencyInjection/XiHanAuthenticationServiceCollectionExtensions.cs:43`
- 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs`
- 注册测试范本：`framework/test/XiHan.Framework.EventBus.SqlSugar.Tests/OutboxRegistrationTests.cs`

**本任务禁止事项：** 硬约束 ⑥。模块类只调一个扩展方法，不写逻辑。

- [ ] **Step 1: 写失败的测试**

`framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/RegistrationTests.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Authentication.SqlSugar.Tests.Fakes;
using XiHan.Framework.Authentication.SqlSugar.Users;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Tests;

/// <summary>
/// 服务注册测试
/// </summary>
public class RegistrationTests
{
    /// <summary>
    /// 用户存储被顶替为作用域的 SqlSugar 实现
    /// </summary>
    [Fact]
    public void 用户存储被顶替为作用域的SqlSugar实现()
    {
        var services = new ServiceCollection();
        services.TryAddScoped<IUserStore, DefaultUserStore>();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IUserStore));
        Assert.Equal(typeof(SqlSugarUserStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// 注册系统时间提供程序
    /// </summary>
    [Fact]
    public void 注册系统时间提供程序()
    {
        var services = new ServiceCollection();

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(TimeProvider));
        Assert.Same(TimeProvider.System, descriptor.ImplementationInstance);
    }

    /// <summary>
    /// 已注册的时间提供程序不被覆盖
    /// </summary>
    [Fact]
    public void 已注册的时间提供程序不被覆盖()
    {
        var services = new ServiceCollection();
        var custom = new MutableTimeProvider(DateTimeOffset.UtcNow);
        services.AddSingleton<TimeProvider>(custom);

        services.AddXiHanAuthenticationSqlSugar(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(TimeProvider));
        Assert.Same(custom, descriptor.ImplementationInstance);
    }

    /// <summary>
    /// 空参数抛出
    /// </summary>
    [Fact]
    public void 空参数抛出()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddXiHanAuthenticationSqlSugar(configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddXiHanAuthenticationSqlSugar(null!));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：编译失败，`CS0246`：找不到命名空间 `XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection`。

- [ ] **Step 3: 创建注册扩展**

`framework/src/XiHan.Framework.Authentication.SqlSugar/Extensions/DependencyInjection/XiHanAuthenticationSqlSugarServiceCollectionExtensions.cs`：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Authentication.SqlSugar.Users;
using XiHan.Framework.Authentication.Users;

namespace XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection;

/// <summary>
/// 认证存储 SqlSugar 服务集合扩展
/// </summary>
public static class XiHanAuthenticationSqlSugarServiceCollectionExtensions
{
    /// <summary>
    /// 以 SqlSugar 存储替换认证模块的内存存储
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddXiHanAuthenticationSqlSugar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.Replace(ServiceDescriptor.Scoped<IUserStore, SqlSugarUserStore>());

        return services;
    }
}
```

- [ ] **Step 4: 模块类调用注册扩展**

把 `framework/src/XiHan.Framework.Authentication.SqlSugar/XiHanAuthenticationSqlSugarModule.cs` 整份替换为：

```csharp
// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Authentication.SqlSugar.Extensions.DependencyInjection;
using XiHan.Framework.Core.Extensions.DependencyInjection;
using XiHan.Framework.Core.Modularity;
using XiHan.Framework.Data;

namespace XiHan.Framework.Authentication.SqlSugar;

/// <summary>
/// 曦寒框架认证存储 SqlSugar 持久化模块
/// </summary>
/// <remarks>
/// 在应用模块上 <c>[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]</c> 即启用。
/// 本模块以 SqlSugar 用户存储替换认证模块的内存用户存储。
/// </remarks>
[DependsOn(
    typeof(XiHanAuthenticationModule),
    typeof(XiHanDataModule)
)]
public class XiHanAuthenticationSqlSugarModule : XiHanModule
{
    /// <summary>
    /// 服务配置
    /// </summary>
    /// <param name="context"></param>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var services = context.Services;

        services.AddXiHanAuthenticationSqlSugar(services.GetConfiguration());
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test --project framework/test/XiHan.Framework.Authentication.SqlSugar.Tests/XiHan.Framework.Authentication.SqlSugar.Tests.csproj -c Release
```

预期：全部 PASS。若 `用户存储被顶替为作用域的SqlSugar实现` 报 `Assert.Single` 找到两个或实现类型仍是 `DefaultUserStore`，检查是否误用了 `TryAddScoped` 或 `AddScoped`。

- [ ] **Step 6: 验证构建并提交**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
git add framework/src/XiHan.Framework.Authentication.SqlSugar framework/test/XiHan.Framework.Authentication.SqlSugar.Tests
git commit -m "feat(authentication-sqlsugar): 以 Replace 顶替主包的内存用户存储"
```

预期：`Build succeeded.`，**0 Warning(s) 0 Error(s)**。

---

### Task 8: 包 README 与全量验收

**Files:**
- Modify: `framework/src/XiHan.Framework.Authentication.SqlSugar/README.md`（整份重写）

**Interfaces:**
- Consumes: 前七个任务的全部产出
- Produces: 无（终端验证任务）

**参考来源（动手前先读）：**
- 已知边界清单：spec 第 7 节
- README 范本：`framework/src/XiHan.Framework.EventBus.SqlSugar/README.md`

**本任务禁止事项：** **不要**碰 `docs/`、根 `README.md` / `README_cn.md`、`framework/README.md` / `framework/README_cn.md`——那是第 ② 份的收尾内容。不要把权衡论证写进代码注释。

- [ ] **Step 1: 重写包 README**

把 `framework/src/XiHan.Framework.Authentication.SqlSugar/README.md` 整份替换为：

````markdown
# XiHan.Framework.Authentication.SqlSugar

## 概述

`XiHan.Framework.Authentication` 的认证存储 SqlSugar 持久化提供程序。主包的 `DefaultUserStore` 是注册为作用域的内存字典，每个请求拿到的都是空字典；本包把用户落到数据库。

## 核心能力

- 用户实体 `sys_auth_user` 与 `UserInfo` 的双向映射
- `IUserStore` 的 SqlSugar 实现 `SqlSugarUserStore`，以 `Replace` 顶替主包的内存实现，生命周期为作用域
- 用户名的查找与唯一约束不区分大小写
- 按当前租户隔离读写
- 登录失败次数在数据库侧原子累加
- 契约外提供 `AddUserAsync` 用于创建用户
- 表结构由 `DbInitializer` 在应用启动时创建，**必须开启** `XiHan:Data:SqlSugarCore` 下的 `EnableDbInitialization` 与 `EnableTableInitialization`（二者默认均为 `false`）

## 依赖关系

依赖 `XiHan.Framework.Authentication`（存储契约）与 `XiHan.Framework.Data`（SqlSugar 数据访问、雪花主键）。

## 配置与约定

表名 `sys_auth_` 前缀、全小写下划线，不分表；列名 Pascal_Snake_Case；主键 `Basic_Id` 为雪花 ID，非自增。未开启自动建表时，首次读写即抛「表不存在」；自行维护表结构时按实体的列定义建表。`XiHan:Data:SqlSugarCore:TableInitialization:Mode` 为 `OptIn` 时本包的表不会自动创建。

用户标识 `UserInfo.UserId` 是 `Basic_Id` 的十进制字符串。非正整数的用户标识视为不存在。

用户名另存一列 `Normalized_User_Name`（`ToUpperInvariant()`），查找与唯一索引 `(Tenant_Id, Normalized_User_Name)` 都用它，因此 `Alice` 与 `alice` 是同一个用户。这与主包 `DefaultUserStore` 的大小写敏感不同；从大小写敏感的旧数据迁入时，仅大小写不同的重名会让唯一索引建不起来。邮箱与手机号不唯一、不参与查找。

所有读写都带 `Tenant_Id = 当前租户（无租户时为 0）` 的条件，不依赖全局租户过滤器：租户上下文看不到平台用户，平台上下文也看不到租户用户。

存储层不对密码哈希、恢复码、双因素密钥做任何计算，原样存取。**双因素密钥 `Two_Factor_Secret` 以明文落库**，数据库泄漏即泄漏全部 TOTP 密钥。

`UpdateUserAsync` **不写密码哈希**，改密码只能经 `UpdatePasswordAsync`。直接改 `user.PasswordHash` 再调 `UpdateUserAsync` 的代码，改动会被忽略。

同一个存储实例（即同一个请求作用域）内，对同一用户的多次读取返回同一个 `UserInfo` 实例；`UpdatePasswordAsync`、失败计数与锁定方法会同步修改该实例。因此在同一作用域内，已读出的用户对象不会反映其他请求在此期间对库的修改（失败计数与锁定三个字段除外）。

时间列一律以 UTC 存储：写入的 `Local` 时间先换算，`Unspecified` 按 UTC 处理；读出的时间标记为 `Utc`。

`RecoveryCodes` 与 `AdditionalData` 以 JSON 文本存储；`AdditionalData` 的值读回后是 `JsonElement`。

取消令牌只在访问数据库前检查，不传给 SqlSugar。

下游若自己也 `Replace` 了 `IUserStore`，以模块装配顺序靠后者为准。

## 使用方式

在应用启动模块上声明依赖：

```csharp
[DependsOn(typeof(XiHanAuthenticationSqlSugarModule))]
public class YourAppModule : XiHanModule
{
}
```

创建用户（密码须先经 `IPasswordHasher` 哈希）：

```csharp
var userId = await userStore.AddUserAsync(new UserInfo
{
    Username = "alice",
    PasswordHash = passwordHasher.HashPassword(password),
    IsActive = true
});
```

## 扩展点

需要自定义存储行为时，实现 `XiHan.Framework.Authentication.Users.IUserStore` 并以 `services.Replace(ServiceDescriptor.Scoped<IUserStore, YourUserStore>())` 替换；实现须注册为作用域。

## 目录结构

```
Entities/                        用户实体
Mapping/                         契约与实体的双向映射、UTC 换算
Users/                           用户存储实现
Extensions/DependencyInjection/  服务注册扩展
```
````

- [ ] **Step 2: 全量验收**

```bash
dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false
dotnet test --solution framework/XiHan.Framework.slnx -c Release
```

预期：构建 **0 Warning(s) 0 Error(s)**；全部测试通过。`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败与本包无关，重跑即可。

若构建因 `XiHan.Framework.*.Tests.exe` 占用输出文件而失败（`MSB3027` / `MSB3021`），先结束残留的测试进程再重跑：

```bash
taskkill //F //IM "XiHan.Framework.Authentication.SqlSugar.Tests.exe"
```

- [ ] **Step 3: 注释复查**

通读本计划新增的全部 `.cs` 文件的注释与 XML 文档注释。判定标准不是比对字面词，而是判断是否在讲论证、权衡、叙事——前后对比的故事、设计理由、反事实推理都算，一个触发词没有也算。发现即移出到提交信息。

- [ ] **Step 4: 提交**

```bash
git add framework/src/XiHan.Framework.Authentication.SqlSugar/README.md
git commit -m "docs(authentication-sqlsugar): 补写用户存储的使用约定与已知边界"
```

---

## 完成标准

第 ① 份完成时应满足：

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动除外）
- `SysAuthUser` 表名 `sys_auth_user`，`(Tenant_Id, Normalized_User_Name)` 唯一索引生效
- `DefaultAuthenticationService` 的六条流程在本存储上语义正确（Task 6 全绿）
- 两个存储实例交替累加失败计数，库里的值等于累加总次数
- 用户名查找大小写不敏感；同租户仅大小写不同的用户名被拒绝
- 租户与平台之间互不可见，按用户名与按标识都一样
- `PasswordHash` 原样写入与读出
- `IUserStore` 注册为 `SqlSugarUserStore`、Scoped
- 每个 `.cs` 文件带版权声明；注释只写做什么

## 已知边界（写入 PR 描述，不写进代码注释）

- **双因素密钥明文落库**：契约与框架没有字段级加密抽象，数据库泄漏即泄漏全部 TOTP 密钥
- **`UpdateUserAsync` 不写密码**：与 `DefaultUserStore` 不同，改密码只能经 `UpdatePasswordAsync`
- **用户名大小写不敏感**：与 `DefaultUserStore` 不同；旧数据中仅大小写不同的重名会让唯一索引建不起来
- **身份映射不刷新**：同一请求作用域内，除失败计数与锁定三个字段外，已读出的用户对象不反映其他请求的修改
- **非数字 `UserId`**：视为不存在
- **自动建表默认关闭**：`EnableDbInitialization` 与 `EnableTableInitialization` 默认均为 `false`；`OptIn` 模式下本包表不会自动创建
- **`AdditionalData` 值类型**：往返后为 `JsonElement`
- **与下游自有实现并存**：以模块装配顺序靠后者为准
- **取消令牌**：只在访问数据库前检查
- **破坏性变更**：无。本包是新增的可选包，不引用它的应用行为不变；引用它即以 `Replace` 顶替 `IUserStore`，**逃生口**是不依赖 `XiHanAuthenticationSqlSugarModule`，或在更靠后的模块里再 `Replace` 回自己的实现

上游作者的验收标准，提交 PR 前逐条自查：

- 「0 警告 0 错误」是硬门槛
- 一个 PR 只做一件事：第 ① 份不碰 `docs/` 与模块清单，它们在第 ② 份与本包其余代码一起提交
- 注释只写「这段代码做什么」，判定靠通读
- 破坏性变更要在 PR 写明并同步文档，包括逃生口

## 下一份计划

第 ② 份（`.superpowers/plans/2026-09-28-authentication-sqlsugar-2-refresh-token-external-login.md`）：`IRefreshTokenStore` 的 SqlSugar 实现（只存 SHA-256 哈希、撤销标记、重用检测与级联撤销、过期清理）、`IExternalLoginStore` 的 SqlSugar 实现、配置节 `XiHan:Authentication:SqlSugar`，以及文档站条目、侧边栏、四份 README 的模块清单与各处模块计数。两份完成后方可提交 PR。
