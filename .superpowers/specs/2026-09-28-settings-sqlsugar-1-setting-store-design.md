# Settings.SqlSugar：设置存储（P1）设计

- **日期**：2026-09-28
- **状态**：待评审
- **对应计划**：`.superpowers/plans/2026-09-28-settings-sqlsugar-1-setting-store.md`
- **前置**：无（`XiHan.Framework.Settings`、`XiHan.Framework.Data` 均已发布）
- **所属 PR**：独立 PR（`Settings.SqlSugar`），一个包一个 PR
- **Linear 议题**：`https://linear.app/elf-express/issue/EDDIE-7`
- **系列**：SqlSugar 持久化剩余模块系列第 1 个包（拆分方案：`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md`）。本包是「简单 CRUD Store」的范本，`Security.SqlSugar`、`Traffic.SqlSugar`、`Upgrade.SqlSugar` 三个小包照本文档的形状抄

> 本文档**自成一体**。实现本包所需的全部约束都写在这里，不引用其他设计文档。系列中其他 spec 的共用约定在各自文档里重复一份——若发现不一致，以对应计划正在实现的那份为准并提出修正。

---

> ## 实现前必读
>
> **动手写任何一行代码之前，先读完第 2 节与第 5 节。**
>
> 技术栈是 **SqlSugar，不是 Entity Framework Core**。.NET 数据访问的默认惯性是 EF Core，若不先锁定参考来源，实现会不自觉写成 `DbContext` / `SaveChangesAsync` / 变更追踪那一套。
>
> **本份最容易静默出错的地方**：`ISettingStore.SetAsync` 没有原子的“存在则改、不存在则插”语义可用（本包引用的 SqlSugar 版本没有能安全处理可空复合键的声明式 upsert），只能先查后写。两次并发的 `SetAsync` 打在同一个从未存在过的 `(name, providerName, providerKey)` 上时，都会查到“不存在”从而各自插入一行——不会抛异常，`GetAllAsync` 若用 `ToDictionary` 收尾则会在探测到这两行重复数据时才第一次炸掉，且炸的位置离案发现场很远。第 5 节 §5.1 详细展开。

---

## 1. 背景与目标

`XiHan.Framework.Settings` 定义了完整的设置管理骨架（`ISettingManager` / `SettingDefinition` / 值提供者链），但持久化层只有一个空实现：

- `NullSettingStore`（`framework/src/XiHan.Framework.Settings/Stores/NullSettingStore.cs`）：读永远返回 `null`，写/删是空操作，用 `[Dependency(TryRegister = true)]` 注册为单例
- 契约 `ISettingStore`（`framework/src/XiHan.Framework.Settings/Stores/ISettingStore.cs`）只有 4 个方法：`GetOrNullAsync` / `GetAllAsync` / `SetAsync` / `DeleteAsync`，全部以 `(name, providerName, providerKey)` 三元组定位一条设置值

调用方链路（决定存储粒度，不是猜的，是读代码读出来的）：

- `GlobalSettingValueProvider`（`Providers/GlobalSettingValueProvider.cs:23-51`）：`providerName = "G"`、`providerKey = null`
- `UserSettingValueProvider`（`Providers/UserSettingValueProvider.cs:25-61`）：`providerName = "U"`、`providerKey = ICurrentUser.UserId.ToString()`
- `SettingManager.SetValueAsync`（`SettingManager.cs:144-175`）按 `SettingScope` 解析 `(providerName, providerKey)`：`Application → ("G", null)`、`User`/`Session → ("U", userId)`、`Tenant → ("T", tenantId)`（`SettingManager.cs:221-249`）
- `SettingManager.GetOrNullAsync` 的读路径只回退到 `_settingStore.GetOrNullAsync(name, "G", null)`（`SettingManager.cs:126`），**不会**回读 `"T"` 或 `"U"`——那两个的读取分别经 `UserSettingValueProvider` 与（尚不存在的）`TenantSettingValueProvider`

**"T"（租户）目前只写不读**：`SettingManager.ResolveProvider` 会把 `SettingScope.Tenant` 写成 `("T", tenantId)`，但当前值提供者链只注册了 `D`/`C`/`G`/`U` 四个（`XiHanSettingsServiceCollectionExtensions.cs:26-29`），没有 `TenantSettingValueProvider`。这是主包既有的行为缺口，**不在本包范围**，但决定了本存储必须对任意 `providerName` 一视同仁——不能只按 `"G"`/`"U"` 两个值硬编码分支，`"T"` 或将来任何新提供者名称都要用同一套通用逻辑处理。

**要交付什么**：新增 `XiHan.Framework.Settings.SqlSugar` 包，提供 `ISettingStore` 的 SqlSugar 落库实现，`services.Replace` 顶替 `NullSettingStore`。

**成功标准**：

1. 四个契约方法全部落库，语义与 `NullSettingStore` 对齐（未命中返回 `null` / 空值列表，写入即可读回，删除后读回 `null`）
2. 同一设置名在不同 `(providerName, providerKey)` 组合下各自独立存取，互不覆盖
3. `providerKey` 为 `null`（全局设置）与非 `null`（用户/租户设置）都能正确写入与查回，包括查询能正确翻译“等于 `null`”这一条件
4. `GetAllAsync` 对请求的每个设置名都返回一条 `SettingValue`（含未命中的，值为 `null`），顺序与输入 `names` 一致
5. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

## 2. 参考来源与禁止事项（强制）

### 2.1 代码参考（按优先级）

**① 要实现的契约与范本包 —— 最高优先**

```
framework/src/XiHan.Framework.Settings/
  Stores/ISettingStore.cs           要实现的契约，4 个方法
  Stores/NullSettingStore.cs        当前的空实现，被顶替的对象；注意 [Dependency(TryRegister = true)]
  Providers/GlobalSettingValueProvider.cs   "G" + null 的调用形状
  Providers/UserSettingValueProvider.cs     "U" + userId 的调用形状
  SettingManager.cs:144-175, 221-249         "T" 的写入路径、写空即删的语义
  Definitions/SettingValue.cs        GetAllAsync 的返回元素类型

framework/src/XiHan.Framework.EventBus.SqlSugar/     范本包（形状，不是内容）
  XiHan.Framework.EventBus.SqlSugar.csproj
  XiHanSqlSugarEventBusModule.cs
  Extensions/DependencyInjection/XiHanSqlSugarEventBusServiceCollectionExtensions.cs
  Entities/SysEventOutbox.cs         SugarEntity<TKey> 的用法（本包实体更简单，无分表无多库）

framework/src/XiHan.Framework.Auditing.SqlSugar/     范本包（形状，不是内容）
  Extensions/DependencyInjection/XiHanAuditingSqlSugarServiceCollectionExtensions.cs   services.Replace 的写法
  Writers/SqlSugarAccessLogWriter.cs   ISqlSugarClientResolver + IDistributedIdGenerator<long> 的组合用法

framework/src/XiHan.Framework.Data/SqlSugar/
  Entities/SugarEntity.cs                     实体基类，Basic_Id / Row_Version 列
  Clients/ISqlSugarClientResolver.cs           GetClientForEntity<T>() 的默认路由语义
  Initializers/TableInitializationAttribute.cs 建表参与方式（本包不需要 IncludeModuleConnections）
```

**② SqlSugar 源码 —— API 真实签名的唯一权威**

```
/e/source/external/SqlSugar/Src/Asp.NetCore2/SqlSugar/
  SqlSugarClient.cs:523                              Queryable<T>() 入口
  Abstract/QueryableProvider/QueryableExecuteSqlAsync.cs:82-113   FirstAsync() / FirstAsync(expression)，未命中返回 default(T)，不抛异常
  SqlSugarClient.cs:770                              Updateable<T>(T updateObj) 整实体更新入口
  Abstract/DeleteProvider/DeleteableProvider.cs:274  Where(Expression<Func<T,bool>>)
  Abstract/UpdateProvider/UpdateableHelper.cs:665-711 ValidateVersion()：仅在显式调用 IsEnableUpdateVersionValidation() 时才生效，本包不调用该方法，见 §5.2
  ExpressionsToSql/ResolveItems/BinaryExpressionResolve.cs:256-278  等值比较中右值为 null 时翻译成 IS/IS NOT，而不是 = NULL，见 §4.2
  ExpressionsToSql/ResolveItems/MethodCallExpressionResolve.cs:89-96 List.Contains(成员) 翻译为 ContainsArray（IN 查询）
```

> 树与目录名：本包引用 SqlSugarCore 5.1.4.221，权威源码是 `Src/Asp.NetCore2/`。同级 `Src/Asp.Net/` 是 .NET Framework 变体，公开 API 与 `Asp.NetCore2` 逐条一致、仅行号有少量偏移。更新提供者目录名是 `UpdateProvider`，不是 `UpdateableProvider`。

当前引用版本：`SqlSugarCore 5.1.4.221`。

### 2.2 文档参考

`/e/source/platfrom-admin/docs/SqlSugar-docs/`（70+ 篇繁体中文）：

| 任务 | 必读 |
| --- | --- |
| 插入 | `插入數據.md` |
| 更新 | `更新數據.md` |
| 查询与 IN 条件 | `簡單的查詢.md`、`異步查詢.md` |
| 删除 | 无独立文档，用法与更新/查询一致：`Deleteable<T>().Where(...).ExecuteCommandAsync()` |

仓库自身文档：`docs/packages/settings.md`（主包现状，含数处已核实的行为说明，**不要**在本包文档里重复其内容，用链接指回去）、`docs/packages/eventbus-sqlsugar.md`（同类落库范式的范本文档）。

### 2.3 禁止事项：EF Core 惯用法

| 禁止 | SqlSugar 的对应写法 |
| --- | --- |
| `DbContext` / `DbSet<T>` / `SaveChangesAsync()` | 不存在。用 `Insertable` / `Updateable` / `Deleteable` + `ExecuteCommandAsync()` |
| 依赖变更追踪（改了对象就会保存） | SqlSugar 无 change tracking，必须显式执行 |
| `[Key]` `[Table]` `[Column]` / `OnModelCreating` | `[SugarTable]` / `[SugarColumn]` |
| `Include()` / `ThenInclude()` | `Includes()` 或手写 join，本包不涉及关联查询 |
| `AsNoTracking()` | 不存在，默认即不追踪 |
| `Database.BeginTransactionAsync()` | `Ado.BeginTranAsync()`，本包不需要事务 |
| `Migrations` / `Add-Migration` / `EnsureCreated()` | `CodeFirst.InitTables()` |
| `IQueryable<T>` + LINQ 扩展 | `ISugarQueryable<T>`，扩展方法不通用 |
| `DbSet.Find(key)` / `FirstOrDefaultAsync()` | `Queryable<T>().FirstAsync(expression)`，注意方法名是 `FirstAsync` 不是 `FirstOrDefaultAsync` |

### 2.4 本份特有的禁止事项

- **不要依赖数据库级别的复合唯一约束防止重复行**。本包引用的 SqlSugar 版本没有声明式的复合唯一索引特性（`[SugarIndex]` 在这个版本里不存在，仓库里也找不到任何先例），且 `ProviderKey` 可空——即便手写 DDL 建出复合唯一索引，NULL 在唯一索引里“各不相同”，无法防止两行 `ProviderKey` 都是 `NULL` 的全局设置重复。正确性由 §4.3 的“先查后写”流程与 §4.4 的去重读取共同保证，不是数据库兜底
- **不要给 `SysSetting` 加 `[TableInitialization(IncludeModuleConnections = true)]`**。本包单库即可（§“五个共同问题”第 4 条），加了这个标注会让表在每个模块库都建一份，属过度设计
- **不要用 `Updateable(existing).IsEnableUpdateVersionValidation()`**。这会把 `SugarEntity<TKey>` 自带的 `Row_Version` 列变成乐观锁校验，两次并发写入会有一次抛 `VersionExceptions`——本包的设计取舍是“先查后写、后写为准”，不是“检测冲突后拒绝”，见 §4.3 与 §7
- **不要在存储层做加密或校验**。`SettingManager.EncryptValue`/`Validator` 已在写入前处理完，`SetAsync` 收到的 `value` 是最终存储值，原样落库
- **不要为 `providerName`/`providerKey` 写死 `"G"`/`"U"` 的特判分支**。本背景第 1 节已指出 `"T"` 会被写入，将来也可能有新提供者名称，存储层必须是通用的三元组匹配

## 3. 非目标

- **不支持批量 `SetAsync`**。契约本身是单条写入，不新增批量重载
- **不做行级并发控制**。见 §4.3、§7，两个并发写入者互相覆盖是本包接受的已知限制，不引入乐观锁或分布式锁
- **不做加密**。加密属于 `SettingManager` 的职责，已经完成
- **不接入 `XiHanSettingOptions.DefinitionProviders`/`ValueProviders` 的既有缺口**。`docs/packages/settings.md` 已详细记录这些字段目前不被 `SettingManager` 读取，那是主包的问题，与本包的存储层无关
- **不新增 `TenantSettingValueProvider`**。第 1 节指出的“T 只写不读”缺口不在本包范围，由 `XiHan.Framework.MultiTenancy` 未来的包补上（`docs/packages/settings.md` 末尾已经预留了这条关系）
- **不做多库遍历**。见“五个共同问题”第 4 条

## 4. 设计

### 4.1 实体

`framework/src/XiHan.Framework.Settings.SqlSugar/Entities/SysSetting.cs`：

```csharp
[SugarTable("sys_setting")]
public class SysSetting : SugarEntity<long>
{
    public SysSetting() : base() { }
    public SysSetting(long basicId) : base(basicId) { }

    [SugarColumn(ColumnName = "Setting_Name", Length = 128, IsNullable = false, ColumnDescription = "设置名称")]
    public string SettingName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "Provider_Name", Length = 32, IsNullable = true, ColumnDescription = "提供者名称")]
    public string? ProviderName { get; set; }

    [SugarColumn(ColumnName = "Provider_Key", Length = 64, IsNullable = true, ColumnDescription = "提供者键")]
    public string? ProviderKey { get; set; }

    [SugarColumn(ColumnName = "Setting_Value", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "设置值")]
    public string? SettingValue { get; set; }
}
```

继承 `SugarEntity<long>`（不是 `SugarCreationEntity<long>`）：本表不需要创建时间/创建人这类审计列，`Row_Version` 由基类自带但本包不激活其乐观锁校验（见 §4.3、禁止事项）。主键为雪花 `long`，经 `IDistributedIdGenerator<long>` 生成——契约本身不暴露任何 ID 类型，选它只是为了与 `Auditing.SqlSugar` 同构、不必新引入 GUID 生成路径。

`Provider_Name`/`Provider_Key` 都声明为可空字符串，与 `ISettingStore` 接口签名的 `string?` 完全对应，不假设调用方一定传非空值。

`Setting_Value` 用 `StaticConfig.CodeFirst_BigString`（各方言的大文本类型集合，`CodeFirstProvider` 按当前 `DbType` 挑选），不用定长 `varchar`：加密后的密文长度不可预测，也可能存放较长的 JSON 字符串。

**不加索引**：`Auditing.SqlSugar`、`EventBus.SqlSugar` 均未声明任何索引，本包沿用同一克制态度——设置表的数据量级远低于日志与事件队列，全表扫描在这个量级下不构成问题；真需要时可后续用手工 DDL 或 `DbMaintenance` 补，不在本份范围。

### 4.2 查询条件里的 `null`

`GetOrNullAsync`/`GetAllAsync`/`SetAsync`/`DeleteAsync` 全部要按 `(name, providerName, providerKey)` 精确匹配，其中 `providerKey` 经常是 `null`（全局设置）。SqlSugar 的表达式引擎对此有内置处理：`BinaryExpressionResolve.cs:256-278` 的 `Right(...)` 方法在右值参数 `ValueIsNull` 为真时，把生成的比较符从 `=`/`<>` 替换成 `IS`/`IS NOT`，即：

```csharp
string? providerKey = null;
.Where(item => item.ProviderKey == providerKey)
// 翻译为 WHERE Provider_Key IS NULL，不是 WHERE Provider_Key = NULL（永假）
```

因此可以放心直接写 `item.ProviderKey == providerKey`，不需要手写 `providerKey == null ? "IS NULL" : "= @p"` 这类分支。**这是本份最重要的一条 SqlSugar 行为确认**，没有它整个设计都要退回到拼字符串 SQL。

### 4.3 写入：先查后写，不是原子 upsert

`SetAsync` 的实现：

```csharp
public async Task SetAsync(string name, string? value, string? providerName, string? providerKey)
{
    var client = _clientResolver.GetClientForEntity<SysSetting>();

    var existing = await client.Queryable<SysSetting>()
        .FirstAsync(item => item.SettingName == name
            && item.ProviderName == providerName
            && item.ProviderKey == providerKey);

    if (existing is null)
    {
        var entity = new SysSetting(_idGenerator.NextId())
        {
            SettingName = name,
            ProviderName = providerName,
            ProviderKey = providerKey,
            SettingValue = value
        };

        await client.Insertable(entity).ExecuteCommandAsync();
    }
    else
    {
        existing.SettingValue = value;

        await client.Updateable(existing).ExecuteCommandAsync();
    }
}
```

`FirstAsync(expression)` 未命中返回 `default(T)`（即 `null`，已核对 `QueryableExecuteSqlAsync.cs:82-113`），不抛异常，可以直接判空。

`client.Updateable(existing)` 是整实体更新（按主键 `Basic_Id` 定位），**不调用** `.IsEnableUpdateVersionValidation()`，因此 `Row_Version` 只是被整体覆盖写入，不做乐观锁校验、不会抛 `VersionExceptions`——这是刻意的取舍，见 §4.4 与 §7。

**这不是原子操作**。两次并发的 `SetAsync` 打在同一个从未出现过的 `(name, providerName, providerKey)` 上时，两者都会在查询阶段看到“不存在”，都会各自 `Insertable` 一行——最终该组合下有两行数据，而不是一行被更新两次。本包接受这个已知限制（详见 §5.1、§7），不引入分布式锁或数据库唯一约束来堵它。

### 4.4 读取：对可能出现的重复行做防御，不假设唯一

`GetOrNullAsync`：

```csharp
public async Task<string?> GetOrNullAsync(string name, string? providerName, string? providerKey)
{
    var client = _clientResolver.GetClientForEntity<SysSetting>();

    var entity = await client.Queryable<SysSetting>()
        .FirstAsync(item => item.SettingName == name
            && item.ProviderName == providerName
            && item.ProviderKey == providerKey);

    return entity?.SettingValue;
}
```

`FirstAsync` 在出现重复行时任取一条，不抛异常——这是可以接受的读取语义（读到其中一条已写入的值，不是读到垃圾数据）。

`GetAllAsync`：

```csharp
public async Task<List<SettingValue>> GetAllAsync(string[] names, string? providerName, string? providerKey)
{
    if (names.Length == 0)
    {
        return [];
    }

    var client = _clientResolver.GetClientForEntity<SysSetting>();

    var rows = await client.Queryable<SysSetting>()
        .Where(item => names.Contains(item.SettingName)
            && item.ProviderName == providerName
            && item.ProviderKey == providerKey)
        .ToListAsync();

    var valuesByName = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var row in rows)
    {
        valuesByName[row.SettingName] = row.SettingValue;
    }

    return [.. names.Select(name => new SettingValue(name, valuesByName.GetValueOrDefault(name)))];
}
```

**关键点：手写 `foreach` 写入 `Dictionary`，不用 `rows.ToDictionary(...)`。** `ToDictionary` 遇到重复键会抛 `ArgumentException`；`foreach` 逐条覆盖写入则是“后来的行覆盖前面的行”，与 `SetAsync` 的“先查后写、后写为准”是同一套语义，行为一致且不抛异常。`names.Length == 0` 提前返回，避免对空集合生成 `WHERE Setting_Name IN ()` 这类边界查询。

`Contains` 翻译为 `IN` 查询（`ContainsArray`，已核对 `MethodCallExpressionResolve.cs:89-96`），是 `names.Contains(item.SettingName)` 这种“本地集合 + 实体成员”的标准写法，不是 `string.Contains`（那是 `LIKE`）。

`DeleteAsync`：

```csharp
public async Task DeleteAsync(string name, string? providerName, string? providerKey)
{
    var client = _clientResolver.GetClientForEntity<SysSetting>();

    await client.Deleteable<SysSetting>()
        .Where(item => item.SettingName == name
            && item.ProviderName == providerName
            && item.ProviderKey == providerKey)
        .ExecuteCommandAsync();
}
```

按条件删除，命中 0 行或多行都不报错——多行是 §4.3 竞态的产物时，一次 `DeleteAsync` 会把重复行一并清掉，这也是可接受的收敛行为。

### 4.5 客户端解析：单库，走 `GetClientForEntity`

四个方法一律用 `_clientResolver.GetClientForEntity<SysSetting>()`，不是 `GetCurrentClient()`。`SysSetting` 未标注 `[DataSource(...)]`，因此 `GetClientForEntity` 等价于 `GetCurrentClient()`（已核对 `ISqlSugarClientResolver.cs:24-33` 的文档注释），行为上与直接调用后者完全一样；选它只是与 `Auditing.SqlSugar` 的写入器保持同一种调用习惯，为将来若某个设置需要按 `[DataSource]` 路由到别的库预留空位，不需要因此改动方法体。

**不需要多库遍历**（对照 `EventBus.SqlSugar` 的 P6）：设置是全局配置数据，不会像事件队列那样分布在业务写入所在的库，见“五个共同问题”第 4 条。

### 4.6 注册

`framework/src/XiHan.Framework.Settings.SqlSugar/Extensions/DependencyInjection/XiHanSettingsSqlSugarServiceCollectionExtensions.cs`：

```csharp
public static IServiceCollection AddXiHanSettingsSqlSugar(this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);

    services.Replace(ServiceDescriptor.Scoped<ISettingStore, SqlSugarSettingStore>());

    return services;
}
```

**必须用 `Replace`，不能用 `TryAdd`。** `NullSettingStore` 标注 `[Dependency(TryRegister = true)]`，`DefaultConventionalRegistrar.AddType`（`framework/src/XiHan.Framework.Core/DependencyInjection/DefaultConventionalRegistrar.cs:58-61`）据此在类型扫描阶段用 `services.TryAdd(...)` 把它登记为 `ISettingStore` 的单例实现。`XiHanSettingsSqlSugarModule` `[DependsOn(typeof(XiHanSettingsModule))]`，依赖模块先装配，`NullSettingStore` 此时已经在容器里，本包若也用 `TryAdd` 则是空操作——`NullSettingStore` 原地留下，日志无报错，设置读写全部悄悄退化为空实现。

**生命周期从单例改为作用域。** `NullSettingStore` 是 `ISingletonDependency`；`SqlSugarSettingStore` 依赖 `ISqlSugarClientResolver`（`XiHanDataServiceCollectionExtensions.cs:61` 注册为 `Scoped`），若仍注册为单例会在容器验证阶段构造失败（单例捕获作用域依赖）。`ISettingManager`（消费 `ISettingStore` 的地方）本身也是 `IScopedDependency`，`GlobalSettingValueProvider`/`UserSettingValueProvider` 是 `ITransientDependency`，改成 `Scoped` 不会产生被捕获依赖的问题。

模块类：

```csharp
[DependsOn(
    typeof(XiHanSettingsModule),
    typeof(XiHanDataModule)
)]
public class XiHanSettingsSqlSugarModule : XiHanModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddXiHanSettingsSqlSugar();
    }
}
```

模块类只做装配，逻辑全在扩展方法里。

## 5. 会静默失效的陷阱

### 5.1 先查后写的竞态被 `ToDictionary` 放大成运行时异常

已经在 §4.3、§4.4 讲过设计取舍：先查后写不是原子操作，重复行是本包接受的已知限制。**这里要单独强调的是“如果不按 §4.4 的方式写 `GetAllAsync`，这个限制会从一个良性的读写不一致，变成一个会在生产环境随机爆炸的 `ArgumentException`”**：

- 用 `rows.ToDictionary(item => item.SettingName, item => item.SettingValue)` 收尾时，只有当某个设置名真的出现过并发首次写入才会命中重复行——单元测试如果只测“正常路径”（没有故意构造重复行），这条分支永远不会被触发，`dotnet test` 全绿
- 上线后一旦两个请求同时对同一个新设置调用 `SetValueAsync`，下一次任何人调用 `GetAllValuesAsync` 就会抛 `ArgumentException: An item with the same key has already been added`，而且现场往往离两次并发写入已经过了很久，排查时完全看不出因果关系

**规避方式已经在 §4.4 落实**：手写 `foreach` 覆盖写入普通 `Dictionary`，永不抛异常。写测试时要故意插入两行相同 `(SettingName, ProviderName, ProviderKey)` 的记录，断言 `GetAllAsync` 不抛异常且能返回其中一个值——这是本份测试策略里唯一需要“伪造”竞态结果、而不是真的并发调用来触发的用例。

### 5.2 误用 `IsEnableUpdateVersionValidation()` 会让第二次写入必然失败

`SugarEntity<TKey>` 基类的 `RowVersion` 属性带 `[SugarColumn(IsEnableUpdateVersionValidation = true)]`（`SugarEntity.cs:34-35`）。这个特性**只标注了列的元数据**，本身不启用任何校验——真正的校验开关是 `IUpdateable<T>.IsEnableUpdateVersionValidation()` 这个链式调用（`UpdateableProvider.cs:399` 设置 `IsVersionValidation = true`，`UpdateableHelper.cs:665` 的 `ValidateVersion()` 只在这个标志为真时才执行版本比对）。

如果实现时照抄了某个用到乐观锁的范例、顺手加上了这个链式调用，后果是：`existing` 对象的 `RowVersion` 是查询时刻的值，`Updateable(existing).IsEnableUpdateVersionValidation().ExecuteCommandAsync()` 执行前会重新查一次库比对版本号——只要两次 `SetAsync` 之间隔了另一次任意写入（哪怕是同一个调用者自己连续调用两次 `SetValueAsync`，如果 `RowVersion` 没有正确自增），第二次就会抛 `VersionExceptions`。**这个失败不会在单元测试里出现**，除非测试恰好覆盖“连续两次 `SetAsync`”这个序列——而这正是最常见的使用方式（先设默认值、再让用户改）。第一次全绿的测试反而会掩盖这个问题，直到验收阶段跑“写两次再读”才会暴露。

规避方式：**不要**在 `SqlSugarSettingStore` 里的任何地方调用 `.IsEnableUpdateVersionValidation()`。§6 的测试策略里有一条用例专门覆盖“连续两次 `SetAsync` 覆盖同一个设置”。

### 5.3 `Provider_Key` 为 `null` 时如果手写字符串 SQL 会全部查不到

如果实现时图省事把 `Where` 换成字符串拼接（`$"Provider_Key = '{providerKey}'"` 之类），`providerKey` 为 `null` 时会拼出 `Provider_Key = ''` 或 `Provider_Key = 'null'`，两者都不等价于 `IS NULL`，全局设置（`providerKey` 恒为 `null`）会全部查不到值——而且不报错，只是读回 `null`，与“设置从未写入”的表现完全一样，从日志和异常里都看不出任何异常迹象。§4.2 已经确认表达式版本的 `==` 能正确处理这一点，本条只是提醒不要在后续维护中“优化”成字符串拼接。

## 6. 测试策略

**只需要一层：SQLite，CI 强门禁执行。** 与 `EventBus.SqlSugar` 不同，本包没有任何方言相关的并发原语（没有 `FOR UPDATE`、没有跨实例抢占），§5.1 的竞态是应用层逻辑决定的，SQLite 与真实数据库的行为一致，不需要额外的真实数据库层来验证方言差异。`Assert.SkipWhen(...)` 这一套本包不需要。

必测用例（覆盖 §4、§5 的每一条设计点）：

- **写入后能读回**：`SetAsync` 一个新的 `(name, "G", null)`，`GetOrNullAsync` 返回相同值
- **不同 `providerKey` 互不覆盖**：同一 `name`、`providerName` 为 `"U"`，两个不同的 `providerKey` 各写各的值，读回互不干扰
- **`providerKey` 为 `null` 时能正确匹配**：写入 `(name, "G", null)` 后，用 `(name, "G", null)` 查询命中；用 `(name, "G", "somekey")` 查询不命中（验证 §4.2 的 `IS NULL` 翻译，不是误判为“无条件匹配”）
- **未命中返回 `null`**：从未写入的 `(name, providerName, providerKey)` 调 `GetOrNullAsync` 返回 `null`，不抛异常
- **连续两次 `SetAsync` 覆盖同一设置**：第一次写入值 A，第二次写入值 B，读回 B（覆盖成功且不抛 `VersionExceptions`，覆盖 §5.2）
- **`DeleteAsync` 后读回 `null`**：写入 → 删除 → `GetOrNullAsync` 返回 `null`
- **`GetAllAsync` 对未命中的名称也返回条目**：请求 3 个名称，只有 1 个存在值，返回的 3 条 `SettingValue` 里未命中的 2 条 `Value` 为 `null`，顺序与输入一致
- **`GetAllAsync` 对空数组直接返回空列表**：不发任何 SQL（可通过传入一个会抛异常的客户端桩间接验证，或至少断言返回 `[]`）
- **`GetAllAsync` 对重复行不抛异常**（覆盖 §5.1）：手工向 SQLite 插入两行相同 `(SettingName, ProviderName, ProviderKey)` 但不同 `SettingValue` 的记录，调用 `GetAllAsync` 断言不抛 `ArgumentException`，且返回值是其中一个而非 null
- **注册顶替**：`AddXiHanSettingsSqlSugar` 之后 `ISettingStore` 的描述符指向 `SqlSugarSettingStore`、生命周期为 `Scoped`（模拟主包已用 `TryAdd` 登记 `NullSettingStore` 单例的前提下）

SQLite 临时库连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

测试项目 Import `props/test.props`（xunit.v3 + Microsoft.Testing.Platform）。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 并发写同一设置无原子性 | `SetAsync` 是先查后写，不是数据库级 upsert。两个并发写入者同时首次写同一 `(name, providerName, providerKey)` 会各自插入一行；`GetOrNullAsync`/`GetAllAsync` 对这种重复行采用“任取/后者覆盖前者”的方式读取，不抛异常，但也不保证读到哪一行。已知限制，不引入锁 |
| 无数据库级复合唯一约束 | 本包引用的 SqlSugar 版本没有声明式复合唯一索引，`ProviderKey` 可空也使得该约束即便手写 DDL 也无法覆盖“两行都是 `NULL`”的情况。正确性完全依赖应用层 |
| 不做乐观锁 | `Row_Version` 列存在但不激活校验（不调用 `IsEnableUpdateVersionValidation()`），后写覆盖前写是本包的既定语义 |
| 建表默认关闭 | `EnableTableInitialization` 默认 `false`（`XiHanSqlSugarCoreOptions.cs`），不开启则 `sys_setting` 不存在，首次调用 `SetAsync`/`GetOrNullAsync` 即报表不存在。README 需要提示这一点，配置层面的逃生口就是开启该选项 |
| "T"（租户）只写不读 | 主包既有缺口（见 §1），本包的存储层对 `"T"` 与其他任意 `providerName` 一视同仁地支持读写，缺口的位置在 `SettingManager` 的读取路径，不在本包 |
| 不做批量写入 | `ISettingStore` 契约是单条写入，`SetAsync` 每次一条 SQL 往返；`GetAllAsync` 已是批量读，`SetAsync` 若要批量需要改契约，不在本包范围 |

## 8. 验收标准

- `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` **0 警告 0 错误**
- `dotnet test --solution framework/XiHan.Framework.slnx -c Release` 全绿（已知无关抖动：`XiHan.Framework.Script.Tests.Core.MemoryUsageTests.Complete_ConvertsGcCountsIntoDeltas` 偶发失败，与本系列无关，源码注释自认会随机变红，无需追查）
- `ISettingStore` 经 `services.Replace` 顶替，`NullSettingStore` 不再被解析到（除非应用自己再次 `Replace` 回去）
- 每个 `.cs` 文件带两行版权声明（分析器 `XHFH001`）：
  ```csharp
  // Copyright (c) 2021-Present XiHanFun and contributors.
  // Licensed under the MIT License. See LICENSE in the project root for license information.
  ```
- 注释与 XML 文档注释为简体中文，且**只说明代码做什么**。权衡论证、踩坑叙事、前后对比的故事、设计理由、反事实推理一律移出到提交信息。判定靠通读，不靠比对字面词
- file-scoped namespace；表达式体**方法/构造函数**在本仓库明确关闭
- 包 README 沿用固定七段结构：概述 / 核心能力 / 依赖关系 / 配置与约定 / 使用方式 / 扩展点 / 目录结构
- 提交信息为中文 Conventional Commits，作用域 `settings-sqlsugar`，**不加任何 AI 署名**
- 一个 PR 只做一件事：不顺手重写与本 PR 无关的文档（尤其是 `docs/packages/settings.md`，它记录的主包现存缺口不是本 PR 的职责）

## 五个共同问题

每个 SqlSugar 落库包都要在自己的 spec 里回答这五个问题，逐条明确作答：

**1. 分表与否？—— 不分表。**
设置项数量级远小于日志（`Auditing.SqlSugar`）与事件队列（`EventBus.SqlSugar`），既不是短命队列也不是海量流水；单表足够，不涉及保留期或归档策略。

**2. 主键类型？—— 雪花 `long`，经 `IDistributedIdGenerator<long>`。**
`ISettingStore` 契约本身不暴露任何标识类型（4 个方法都只接收/返回 `name`/`value`/`providerName`/`providerKey`，没有 ID），存储层的主键类型是完全自由的选择。选雪花 `long` 是为了与 `Auditing.SqlSugar` 同构、复用已经过验证的生成器依赖链（`XiHan.Framework.Data` 已传递引入 `XiHan.Framework.DistributedIds`），不需要新引入 `Guid` 生成路径。

**3. 是否参与工作单元事务？—— 不参与。**
设置是独立于业务实体的全局配置数据，写入不需要跟随任何业务事务一起提交或回滚。`SqlSugarSettingStore` 注册为 `Scoped` 只是因为它依赖的 `ISqlSugarClientResolver` 是 `Scoped`，不是为了参与工作单元——它不经 `EnlistCurrentUnitOfWork` 登记连接，也不关心当前是否处于某个工作单元内。

**4. 是否需要多库？—— 不需要，单库即可。**
设置是全局配置，不会像 `EventBus.SqlSugar` 的事件那样分布式地跟随业务实体落在不同的模块库。`GetClientForEntity<SysSetting>()` 在未来若真的需要按 `[DataSource(...)]` 路由，可以直接在实体上加标注而不改动 `SqlSugarSettingStore` 的任何方法体——这也是选它而非 `GetCurrentClient()` 的原因。

**5. 顶替方式？—— `Replace`，不能用 `TryAdd`。**
`NullSettingStore` 用 `[Dependency(TryRegister = true)]`，经 `DefaultConventionalRegistrar.AddType` 内部转译为 `services.TryAdd(...)`（`DefaultConventionalRegistrar.cs:58-61`）。`XiHanSettingsSqlSugarModule` 依赖 `XiHanSettingsModule`，装配顺序上后者先跑，`NullSettingStore` 已经登记，本包若也用 `TryAdd` 是空操作。

## 待确认的决策

用户需要审的地方——每一条都是本设计替用户做的选择，不是既定事实：

| 决策 | 选定的默认值 | 理由 | 若想改，影响什么 |
| --- | --- | --- | --- |
| 主键类型 | 雪花 `long`（`IDistributedIdGenerator<long>`） | 契约不暴露 ID 类型，选它只是与 `Auditing.SqlSugar` 同构 | 改成 `Guid` 需要新增顺序 GUID 生成器依赖（`EventBus.SqlSugar` 已有先例），实体基类从 `SugarEntity<long>` 换成 `SugarEntity<Guid>`，其余逻辑不变 |
| 并发写入的处理方式 | 先查后写，不加锁、不做乐观并发校验，后写覆盖前写 | 设置项的写入频率低、冲突概率低，为一个低频路径引入分布式锁或重试与收益不成比例；`SugarEntity<TKey>` 自带的 `Row_Version` 列足够支持日后升级为乐观锁而不改表结构 | 若要严格防止并发覆盖，需要在 `Updateable(existing)` 后追加 `.IsEnableUpdateVersionValidation()`，并给 `SetAsync` 加重试循环处理 `VersionExceptions`；首次插入的竞态仍需要额外方案（如应用层分布式锁），不是加了乐观锁就自动解决 |
| `Setting_Value` 列类型 | 大文本（`StaticConfig.CodeFirst_BigString`），不是定长 `varchar` | 加密后密文长度不可预测，且不排除存放较长 JSON 的设置值 | 若确定所有设置值都很短，可改成 `Length = 2000` 左右的定长列以换取更好的索引/存储效率，但需要先盘点现有设置项的实际值长度 |
| 是否加索引 | 不加 | 设置表数据量小，全表扫描不构成问题；本包引用的 SqlSugar 版本没有声明式复合索引特性，手写 DDL 增加复杂度不划算 | 若设置项数量增长到需要索引的量级，可用 `DbMaintenance` API 或手工 DDL 补一个 `(Setting_Name, Provider_Name, Provider_Key)` 的普通（非唯一）索引 |
| 是否给 `SysSetting` 做 SQLite 之外的真实数据库测试 | 不做 | 本包没有依赖具体数据库方言的并发原语，§5.1/§5.2 的两个陷阱在 SQLite 与 MySQL/PostgreSQL 下行为一致 | 若日后要验证特定数据库方言的字符串比较/大小写敏感度差异，需要新增一层 `Assert.SkipWhen` 测试，范式见 `EventBus.SqlSugar` |

## 9. 下一份

拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md`）第 5 节建议的下一个包是 `Tasks.SqlSugar`（背景作业领取可直接复用 `EventBus.SqlSugar` P4/P6 已验证过的三步抢占协议），随后是 `Authentication.SqlSugar`、`Authorization.SqlSugar`；`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 三个小包应直接照抄本文档建立的形状。
