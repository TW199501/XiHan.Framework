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
> **本份最容易静默出错的地方**：`Provider_Name`/`Provider_Key` 两列在本设计里是**非空**列，`null` 在写入与查询前都要先归一化成哨兵值 `string.Empty`（§4.2）。如果某处代码手滑，直接拿调用方传来的原始 `providerKey`（可能是 `null`）去跟这两列比较，SqlSugar 会把它翻译成 `IS NULL`——但列里存的从来不是 SQL `NULL`，而是空字符串，`IS NULL` 永远为假。后果是：**全局设置（`providerKey` 恒为 `null`）的读写全部静默失效**，`GetOrNullAsync` 永远返回 `null`、`SetAsync` 永远走“插入新行”分支，且不抛任何异常——表现和“这个设置从未配置过”一模一样，日志里没有任何异常，唯一的破绽是同一个设置名下的行数会不断增长。第 5 节 §5.1 详细展开，务必确认四个方法都做了归一化再往下写。

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
3. `providerKey` 为 `null`（全局设置）与非 `null`（用户/租户设置）都能正确写入与查回，且反复写入同一个 `providerKey = null` 的设置命中的始终是同一行，不会越写越多
4. `GetAllAsync` 对请求的每个设置名都返回一条 `SettingValue`（含未命中的，值为 `null`），顺序与输入 `names` 一致
5. 同一 `(name, providerName, providerKey)` 上并发调用 `SetAsync` 时，数据库唯一索引保证最终只有一行，不产生重复数据
6. `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false` 0 警告 0 错误

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
  Entities/Mapping/SugarMappingAttribute.cs:341-467  SugarIndexAttribute：类级特性，最多 10 个字段 + IsUnique 标志，见 §4.1
  Abstract/EntityMaintenance/EntityMaintenance.cs:72-75              读取类上的 SugarIndexAttribute 集合，存进 EntityInfo.Indexs
  Abstract/CodeFirstProvider/CodeFirstProvider.cs:317-328,332-393    InitTables 内部对每个已建/新建的表调用 CreateIndex(entityInfo)，按 IndexFields 里的属性名（不是列名）解析出真实列名后建索引
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
| 索引 | `插入或更新Storageable.md`（背景阅读：SqlSugar 也有内建 upsert API，本包为何不用见 §4.3） |
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
| `[Index(nameof(A), nameof(B), IsUnique = true)]`（EF Core 的模型级索引） | `[SugarIndex(indexName, nameof(A), OrderByType.Asc, nameof(B), OrderByType.Asc, isUnique: true)]`，字段名传的是 **C# 属性名**，不是数据库列名（`EntityMaintenance.cs` 按 `PropertyName` 解析） |

### 2.4 本份特有的禁止事项

- **不要给 `SysSetting` 加 `[TableInitialization(IncludeModuleConnections = true)]`**。本包单库即可（§“五个共同问题”第 4 条），加了这个标注会让表在每个模块库都建一份，属过度设计
- **不要用 `Updateable(existing).IsEnableUpdateVersionValidation()`**。这会把 `SugarEntity<TKey>` 自带的 `Row_Version` 列变成乐观锁校验，两次并发写入会有一次抛 `VersionExceptions`——本包用唯一索引处理“同一设置被并发首次创建”这一种竞态，用“先查后写、后写为准”处理“同一设置被并发更新”这一种竞态，两者都不是乐观锁，见 §4.3 与 §7
- **不要在存储层做加密或校验**。`SettingManager.EncryptValue`/`Validator` 已在写入前处理完，`SetAsync` 收到的 `value` 是最终存储值，原样落库
- **不要为 `providerName`/`providerKey` 写死 `"G"`/`"U"` 的特判分支**。本背景第 1 节已指出 `"T"` 会被写入，将来也可能有新提供者名称，存储层必须是通用的三元组匹配
- **不要在 `SetAsync` 的 `catch` 块里按异常类型或消息文本判断“这是不是唯一约束冲突”**。SQLite/MySQL/PostgreSQL/SQL Server 的唯一约束异常类型与错误码各不相同，本包是通用类库、不认方言。§4.3 的处理方式是“捕获后立即按业务键重新查询，查到就说明确实是并发写入触发的约束冲突，查不到就重新抛出原始异常”——用查询结果反推异常性质，不解析异常本身

## 3. 非目标

- **不支持批量 `SetAsync`**。契约本身是单条写入，不新增批量重载
- **不做乐观锁**。见 §4.3、§7：并发首次创建同一设置由数据库唯一索引 + 一次重试兜底（不产生重复行）；并发更新同一个已存在的设置仍是“后写覆盖前写”，不引入版本校验
- **不做加密**。加密属于 `SettingManager` 的职责，已经完成
- **不接入 `XiHanSettingOptions.DefinitionProviders`/`ValueProviders` 的既有缺口**。`docs/packages/settings.md` 已详细记录这些字段目前不被 `SettingManager` 读取，那是主包的问题，与本包的存储层无关
- **不新增 `TenantSettingValueProvider`**。第 1 节指出的“T 只写不读”缺口不在本包范围，由 `XiHan.Framework.MultiTenancy` 未来的包补上（`docs/packages/settings.md` 末尾已经预留了这条关系）
- **不做多库遍历**。见“五个共同问题”第 4 条
- **不用 SqlSugar 内建的 `Storageable`（插入或更新）API**。它的匹配列同样要面对“可空复合键在唯一索引里各不相同”这个问题，并不比本包“归一化 + 唯一索引 + 先查后写”的组合更简单，改用它不会减少代码量，只会换一套要重新核实语义的 API

## 4. 设计

### 4.1 实体：非空的提供者列 + 唯一索引

`framework/src/XiHan.Framework.Settings.SqlSugar/Entities/SysSetting.cs`：

```csharp
[SugarTable("sys_setting")]
[SugarIndex("uk_sys_setting_key",
    nameof(SysSetting.SettingName), OrderByType.Asc,
    nameof(SysSetting.ProviderName), OrderByType.Asc,
    nameof(SysSetting.ProviderKey), OrderByType.Asc,
    isUnique: true)]
public class SysSetting : SugarEntity<long>
{
    public SysSetting() : base() { }
    public SysSetting(long basicId) : base(basicId) { }

    [SugarColumn(ColumnName = "Setting_Name", Length = 128, IsNullable = false, ColumnDescription = "设置名称")]
    public string SettingName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "Provider_Name", Length = 32, IsNullable = false, ColumnDescription = "提供者名称，未指定时存归一化占位符")]
    public string ProviderName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "Provider_Key", Length = 64, IsNullable = false, ColumnDescription = "提供者键，未指定时存归一化占位符")]
    public string ProviderKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "Setting_Value", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "设置值")]
    public string? SettingValue { get; set; }
}
```

继承 `SugarEntity<long>`（不是 `SugarCreationEntity<long>`）：本表不需要创建时间/创建人这类审计列，`Row_Version` 由基类自带但本包不激活其乐观锁校验（见 §4.3、禁止事项）。主键为雪花 `long`，经 `IDistributedIdGenerator<long>` 生成——契约本身不暴露任何 ID 类型，选它只是为了与 `Auditing.SqlSugar` 同构、不必新引入 GUID 生成路径。

**`Provider_Name`/`Provider_Key` 是非空列，不是 `ISettingStore` 接口签名 `string?` 的直接映射。** `ISettingStore` 的四个方法确实允许 `providerName`/`providerKey` 为 `null`，但“允许调用方传 `null`”与“数据库列本身可空”是两回事——本设计选择在存储边界把 `null` 归一化成哨兵值 `string.Empty`（§4.2），让数据库列**永远不出现 SQL `NULL`**，代价是需要在四个方法的入口各做一次归一化，换来的是一个真正对全部三列生效的复合唯一索引（若列可空，`NULL` 在唯一索引里“各不相同”，无法防止两行全局设置——`Provider_Key` 恒为归一化前的 `null`——互相重复）。

**唯一索引 `uk_sys_setting_key`** 覆盖 `(Setting_Name, Provider_Name, Provider_Key)` 三列，用 `[SugarIndex]` 声明。三个参数位是 **C# 属性名**（`nameof(SysSetting.SettingName)` 等），不是数据库列名——`EntityMaintenance.GetEntityInfoNoCache` 只是原样收集这个特性列表，真正解析在 `CodeFirstProvider.CreateIndex`：它按 `entityInfo.Columns.FirstOrDefault(z => z.PropertyName == it.Key)` 找到对应列后再建索引，传列名会找不到对应属性直接抛 `Check.ExceptionEasy`。索引随 `db.CodeFirst.InitTables(typeof(SysSetting))` 一并创建，不需要额外调用。

`Setting_Value` 用 `StaticConfig.CodeFirst_BigString`（各方言的大文本类型集合，`CodeFirstProvider` 按当前 `DbType` 挑选），不用定长 `varchar`：加密后的密文长度不可预测，也可能存放较长的 JSON 字符串。

**不加除唯一索引外的其他索引**：`Auditing.SqlSugar`、`EventBus.SqlSugar` 均未声明任何索引，设置表的数据量级远低于日志与事件队列，非唯一场景下全表扫描不构成问题；这里的唯一索引是为正确性而加，不是为查询性能。

### 4.2 归一化：`null` 与哨兵值 `string.Empty` 的转换

`SqlSugarSettingStore` 内部有一个私有静态帮助方法：

```csharp
private static string Normalize(string? value)
{
    return value ?? string.Empty;
}
```

四个契约方法的**第一步**都是把入参 `providerName`/`providerKey` 分别 `Normalize(...)`，后续查询/写入一律使用归一化后的值，**不再触碰原始的可空参数**。

**为什么要归一化，而不是保留可空列直接查询。** SqlSugar 的表达式引擎对 `==` 比较里的 `null` 有内置处理：`BinaryExpressionResolve.cs:256-278` 的 `Right(...)` 方法在右值参数 `ValueIsNull` 为真时，把生成的比较符从 `=`/`<>` 替换成 `IS`/`IS NOT`：

```csharp
string? providerKey = null;
.Where(item => item.ProviderKey == providerKey)
// providerKey 为 null 时翻译为 WHERE Provider_Key IS NULL
```

如果 `Provider_Key` 列本身可空、且确实允许存 `NULL`，这条翻译是对的。但本设计的列是**非空**列（§4.1），从不存真正的 `NULL`——如果哪里忘了先 `Normalize`，直接拿 `providerKey == null` 去比较，翻译出来的 `IS NULL` 永远为假，因为列里存的是 `string.Empty`，不是 `NULL`。**这正是 §5.1 的陷阱**：不是 SqlSugar 翻译错了，是列的可空性与查询条件的可空性不匹配。

**归一化后，`==` 比较回到最简单的字符串相等**，不再依赖 `IS NULL` 这条翻译规则——`item.ProviderKey == normalizedProviderKey`（两侧都保证非空）恒定翻译成 `WHERE Provider_Key = @p`，不存在“列到底有没有 NULL”这个变量。

**已知的、接受的边界**：调用方若显式传入空字符串 `""` 作为 `providerKey`（而不是 `null`），归一化后与传 `null` **完全等价**，两者会写到同一行。当前主包的三个调用点（`GlobalSettingValueProvider` 传 `null`、`UserSettingValueProvider` 传 `UserId.ToString()`、`SettingManager` 的 `"T"`/`"U"` 传 `tenantId`/`userId` 的 `ToString()`）都不会产生空字符串，这条边界目前不会触发，写进 §7。

### 4.3 写入：先查后写做常见路径，唯一索引 + 重试兜底竞态

```csharp
public async Task SetAsync(string name, string? value, string? providerName, string? providerKey)
{
    var client = _clientResolver.GetClientForEntity<SysSetting>();
    var normalizedProviderName = Normalize(providerName);
    var normalizedProviderKey = Normalize(providerKey);

    var existing = await FindAsync(client, name, normalizedProviderName, normalizedProviderKey);

    if (existing is not null)
    {
        existing.SettingValue = value;
        await client.Updateable(existing).ExecuteCommandAsync();
        return;
    }

    var entity = new SysSetting(_idGenerator.NextId())
    {
        SettingName = name,
        ProviderName = normalizedProviderName,
        ProviderKey = normalizedProviderKey,
        SettingValue = value
    };

    await OnBeforeInsertAsync(entity, CancellationToken.None);

    try
    {
        await client.Insertable(entity).ExecuteCommandAsync();
    }
    catch (Exception)
    {
        var winner = await FindAsync(client, name, normalizedProviderName, normalizedProviderKey);

        if (winner is null)
        {
            throw;
        }

        winner.SettingValue = value;
        await client.Updateable(winner).ExecuteCommandAsync();
    }
}

private static async Task<SysSetting?> FindAsync(
    ISqlSugarClient client, string name, string providerName, string providerKey)
{
    return await client.Queryable<SysSetting>()
        .FirstAsync(item => item.SettingName == name
            && item.ProviderName == providerName
            && item.ProviderKey == providerKey);
}

protected virtual Task OnBeforeInsertAsync(SysSetting entity, CancellationToken cancellationToken)
{
    return Task.CompletedTask;
}
```

`OnBeforeInsertAsync` 是留给测试的扩展点，正式代码里什么也不做；`SqlSugarSettingStore` 因此**不加 `sealed`**——测试项目里的一个子类会重写它，在“确认不存在”与“正式插入”之间的窗口里用另一个客户端抢先插入同一行，确定性地复现下面第 2 点描述的竞态，不需要真实的多线程/多进程并发，见 §6。

**两段设计，各自处理一种竞态**：

1. **常见路径——更新一个已存在的设置**：先查后改，`Updateable(existing)` 不激活乐观锁校验（§禁止事项），后写覆盖前写。这是绝大多数调用的形状（应用运行期间反复修改同一批设置）
2. **少见路径——两个调用者同时首次创建同一个设置**：都在第一步查询时看到“不存在”，都会进入 `Insertable`；`uk_sys_setting_key` 保证其中一个成功、另一个抛出唯一约束冲突异常。失败的那一方立即用同一个业务键重新查询——**查到就当竞态处理**（把它当成“先查后写”的正常更新分支，两次 `SetAsync` 都成功返回，表里只有一行，值是最后一次成功更新覆盖的结果）；**查不到就重新抛出原始的唯一约束冲突异常**。“查到”不是必然的，见下面两条边界

**边界一——事务型工作单元里，PostgreSQL 上这条恢复路径根本走不到重新查询这一步。** `_clientResolver.GetClientForEntity<SysSetting>()` 内部无条件调用 `EnlistCurrentUnitOfWork`（已核对 `SqlSugarClientResolver.cs:129-154, 256+`）：只要当前执行路径上存在一个事务型工作单元（不是本包主动选择参与，是 `ISqlSugarClientResolver` 的固有行为），本方法的读写就钉在这个事务上。SQLite 与 MySQL（默认逐语句提交语义）下，某条语句失败不影响同一事务里后续语句的执行，`catch` 块里的重新查询能正常跑。**PostgreSQL 不是这样**：一旦事务内任意一条语句返回错误（含唯一约束冲突），整个事务立即进入 `aborted` 状态（`25P02 current transaction is aborted, commands ignored until end of transaction block`），事务结束前的任何后续命令都会失败——`catch` 块里的 `FindAsync` 重新查询本身会抛出一个新的 `25P02` 异常，原始的唯一约束冲突异常被这个新异常盖过、丢失，且这不是 `SetAsync` 单独失败：调用方在同一个事务型工作单元里做的其他任何写入都会因为事务已中止而无法提交。**本设计因此放弃“并发首次创建同一设置不会向调用方抛异常”这条承诺**——真实情况是数据库相关的：SQLite/MySQL 下竞态通常被静默吸收，PostgreSQL 下（且处于事务型工作单元内）竞态会以异常形式暴露、并拖垮调用方当次事务，见 §7、“待确认的决策”。

**边界二——即使数据库允许继续查询，`RepeatableRead`/`Snapshot` 隔离级别下也不保证“查到”。** 这两种隔离级别的查询看的是事务开始时刻的快照，看不到另一个事务在此之后才提交的行——`winner` 会是 `null`，代码走 `if (winner is null) { throw; }` 分支，把原始的唯一约束冲突异常重新抛给调用方。这是一次**安全的失败**：调用方看到的是一个含义明确的唯一约束冲突，不是被吞掉、也不是被误判成别的错误。PostgreSQL 默认隔离级别是 `Read Committed`（不受此条影响，但仍受边界一影响）；显式配置为 `Repeatable Read`，或 SQL Server 的 `Snapshot` 隔离级别，会受这条边界影响。

**为什么 `catch` 块不判断异常类型或解析错误码**：SQLite（`Microsoft.Data.Sqlite.SqliteException`，`SqliteErrorCode == 19`）、MySQL（`MySqlException.Number == 1062`）、PostgreSQL（`PostgresException.SqlState == "23505"`）对“唯一约束冲突”的异常类型与错误码各不相同，本包是不认方言的通用类库（禁止事项已列）。用“捕获后重新查询，查到就当竞态处理、查不到就重新抛出原始异常”这个方式，把“这次失败是不是竞态冲突”的判断从“猜异常类型”换成“看业务键此刻是否存在”——后者是每种数据库都一致的行为，前者不是。查不到时 `throw;`（不是 `throw ex;`）保留原始堆栈，确保这条路径只吞掉真正的竞态冲突，其他任何原因导致的插入失败都会原样传播给调用方。

**这不是乐观锁**，也不做重试上限——竞态只可能发生在“同一个从未存在过的键第一次被创建”这一瞬间，一旦任意一方创建成功，之后所有 `SetAsync` 调用都会在第一步的 `FindAsync` 里查到该行，走常见路径，不会再触发这段 `catch`。

### 4.4 读取

`GetOrNullAsync`：

```csharp
public async Task<string?> GetOrNullAsync(string name, string? providerName, string? providerKey)
{
    var client = _clientResolver.GetClientForEntity<SysSetting>();

    var entity = await FindAsync(client, name, Normalize(providerName), Normalize(providerKey));

    return entity?.SettingValue;
}
```

`FirstAsync(expression)` 未命中返回 `default(T)`（即 `null`，已核对 `QueryableExecuteSqlAsync.cs:82-113`），不抛异常，可以直接判空。

`GetAllAsync`：

```csharp
public async Task<List<SettingValue>> GetAllAsync(string[] names, string? providerName, string? providerKey)
{
    if (names.Length == 0)
    {
        return [];
    }

    var client = _clientResolver.GetClientForEntity<SysSetting>();
    var normalizedProviderName = Normalize(providerName);
    var normalizedProviderKey = Normalize(providerKey);

    var rows = await client.Queryable<SysSetting>()
        .Where(item => names.Contains(item.SettingName)
            && item.ProviderName == normalizedProviderName
            && item.ProviderKey == normalizedProviderKey)
        .ToListAsync();

    var valuesByName = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var row in rows)
    {
        valuesByName[row.SettingName] = row.SettingValue;
    }

    return [.. names.Select(name => new SettingValue(name, valuesByName.GetValueOrDefault(name)))];
}
```

**手写 `foreach` 而不是 `rows.ToDictionary(...)`**：`uk_sys_setting_key` 让“同一个键出现两行”在本包写入的数据里不会发生，但索引只在 `EnableTableInitialization` 开启并跑过 `CodeFirst.InitTables(...)` 之后才存在——若某个部署跳过建表初始化、手工建了一张没有这个索引的旧表，或者从未启用索引的更早版本升级上来，历史数据仍可能出现重复行。`foreach` 逐条覆盖写入是零成本的防御：正常情况下每个键至多一行，`foreach` 和 `ToDictionary` 结果一致；万一出现历史脏数据，前者“后一行覆盖前一行”后返回，后者直接抛 `ArgumentException`。`names.Length == 0` 提前返回，避免对空集合生成 `WHERE Setting_Name IN ()` 这类边界查询。

`Contains` 翻译为 `IN` 查询（`ContainsArray`，已核对 `MethodCallExpressionResolve.cs:89-96`），是 `names.Contains(item.SettingName)` 这种“本地集合 + 实体成员”的标准写法，不是 `string.Contains`（那是 `LIKE`）。

`DeleteAsync`：

```csharp
public async Task DeleteAsync(string name, string? providerName, string? providerKey)
{
    var client = _clientResolver.GetClientForEntity<SysSetting>();
    var normalizedProviderName = Normalize(providerName);
    var normalizedProviderKey = Normalize(providerKey);

    await client.Deleteable<SysSetting>()
        .Where(item => item.SettingName == name
            && item.ProviderName == normalizedProviderName
            && item.ProviderKey == normalizedProviderKey)
        .ExecuteCommandAsync();
}
```

按条件删除，命中 0 行不报错。

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

### 5.1 忘记归一化：全局设置的读写会永久静默失败

`Provider_Name`/`Provider_Key` 是非空列（§4.1），从不存 SQL `NULL`。四个契约方法的入口都必须先 `Normalize(...)` 再使用；一旦某处漏掉、直接拿原始的可空 `providerKey` 参数去构造查询条件：

```csharp
// 错误示范：providerKey 未归一化
.FirstAsync(item => item.SettingName == name && item.ProviderKey == providerKey)
```

`providerKey` 为 `null`（全局设置的标准调用形状）时，SqlSugar 把 `item.ProviderKey == providerKey` 翻译成 `Provider_Key IS NULL`（§4.2 引用的翻译规则本身没有错）。但列里从来没有真正的 `NULL`——`SetAsync` 一旦漏掉归一化，插入的行会把 `ProviderKey` 留成 CLR 默认值 `string.Empty`（属性声明里的 `= string.Empty`），而查询用 `IS NULL` 去找，两者永远对不上。

**后果是全局设置的读写彻底静默失效**：`GetOrNullAsync` 永远返回 `null`（表现和“从未配置”一样），`SetAsync` 每次都判定“不存在”从而插入新行（`Provider_Key` 全部是 `string.Empty`，但唯一索引比较的是归一化后的字符串是否相同——如果 `SetAsync` 本身也没归一化，它插入的每一行 `ProviderKey` 都是同一个 `string.Empty`，唯一索引反而会在第二次调用时正确抛出冲突，只是 `catch` 块里的 `FindAsync` 如果同样没归一化、用原始 `null` 去查，一样查不到、`winner is null`、原始异常被重新抛出——这时才会第一次看到异常，而且是在“为什么写第二次会抛错”这种让人摸不着头绪的位置）。**不会在“只测正常路径”的测试里暴露**，除非测试用例专门覆盖“反复用 `providerKey = null` 写同一个设置，断言表里只有一行”。

规避方式：四个方法的第一行永远是 `Normalize(providerName)`/`Normalize(providerKey)`，且之后的代码只使用归一化后的局部变量，不再触碰方法参数本身。§6 的测试列表专门覆盖这一点。

### 5.2 误用 `IsEnableUpdateVersionValidation()` 会让第二次写入必然失败

`SugarEntity<TKey>` 基类的 `RowVersion` 属性带 `[SugarColumn(IsEnableUpdateVersionValidation = true)]`（`SugarEntity.cs:34-35`）。这个特性**只标注了列的元数据**，本身不启用任何校验——真正的校验开关是 `IUpdateable<T>.IsEnableUpdateVersionValidation()` 这个链式调用（`UpdateableProvider.cs:399` 设置 `IsVersionValidation = true`，`UpdateableHelper.cs:665` 的 `ValidateVersion()` 只在这个标志为真时才执行版本比对）。

如果实现时照抄了某个用到乐观锁的范例、顺手加上了这个链式调用，后果是：`existing` 对象的 `RowVersion` 是查询时刻的值，`Updateable(existing).IsEnableUpdateVersionValidation().ExecuteCommandAsync()` 执行前会重新查一次库比对版本号——只要两次 `SetAsync` 之间隔了另一次任意写入（哪怕是同一个调用者自己连续调用两次 `SetValueAsync`，如果 `RowVersion` 没有正确自增），第二次就会抛 `VersionExceptions`。**这个失败不会在单元测试里出现**，除非测试恰好覆盖“连续两次 `SetAsync`”这个序列——而这正是最常见的使用方式（先设默认值、再让用户改）。第一次全绿的测试反而会掩盖这个问题，直到验收阶段跑“写两次再读”才会暴露。

规避方式：**不要**在 `SqlSugarSettingStore` 里的任何地方调用 `.IsEnableUpdateVersionValidation()`。§6 的测试策略里有一条用例专门覆盖“连续两次 `SetAsync` 覆盖同一个设置”。

### 5.3 在 `SetAsync` 的 `catch` 块里解析异常类型，会在换数据库时悄悄失效

如果实现时把 §4.3 的 `catch (Exception)` 改窄成 `catch (SqliteException ex) when (ex.SqliteErrorCode == 19)` 这类只认单一方言的写法，**本地用 SQLite 跑的测试完全不会发现问题**——因为测试环境就是 SQLite。等应用换成 MySQL 或 PostgreSQL 部署，唯一约束冲突抛出的是 `MySqlException`/`PostgresException`，不再匹配这个 `catch` 子句，异常直接原样抛给调用方，`SetAsync` 在竞态下从“静默处理成功”退化成“对外抛出未处理异常”。§4.3 已经解释了为什么用“重新查询确认”代替“解析异常类型”——这条陷阱是给后续维护者的提醒，不要因为本地测试只用 SQLite 就把 `catch` 收窄。

### 5.4 本地 SQLite 测试全绿，不代表 PostgreSQL 生产环境不会因为一次设置竞态而丢事务

§4.3“边界一”已经说清楚机制：本包的读写会被 `ISqlSugarClientResolver.GetClientForEntity` 无条件登记进当前事务型工作单元（不是本包的选择）。SQLite 下这件事没有后果——`catch` 块的重新查询照常执行、竞态被吸收。**这正是这条陷阱危险的地方**：本份 §6 的全部单元测试都跑在 SQLite 上，PostgreSQL 特有的“整个事务因一条语句失败而中止”这条行为在 SQLite 层面永远观察不到、永远是绿的。第一次真正暴露是在生产环境的动态 API 请求里——业务代码在同一个工作单元里先写了别的东西，又调用了一次 `SettingManager.SetValueAsync` 触发本包的并发首次创建分支，PostgreSQL 报 `25P02`，整个请求的事务被回滚，包括那些看起来跟设置毫无关系的业务写入。规避方式见 §4.3“边界一”与“待确认的决策”的 `requiresNew` 选项，不是本包能在代码层面单方面修掉的问题——记录在案（§7），不是遗漏。

## 6. 测试策略

**只需要一层：SQLite，CI 强门禁执行。** 唯一索引冲突是标准 SQL 行为（各数据库对唯一约束的支持是通用能力，不是方言特性），§4.3 的处理逻辑本身也不依赖任何方言细节（见 §5.3），SQLite 足以验证全部设计点。`Assert.SkipWhen(...)` 那一套本包不需要。

必测用例（覆盖 §4、§5 的每一条设计点）：

- **写入后能读回**：`SetAsync` 一个新的 `(name, "G", null)`，`GetOrNullAsync` 返回相同值
- **不同 `providerKey` 互不覆盖**：同一 `name`、`providerName` 为 `"U"`，两个不同的 `providerKey` 各写各的值，读回互不干扰
- **`providerKey` 为 `null` 时能正确匹配，且反复写入命中同一行**（覆盖 §5.1）：连续两次对 `(name, "G", null)` 调 `SetAsync`，`GetOrNullAsync` 读到第二次写入的值；再对同一实体直接查表断言只有一行——这是归一化是否生效的直接证据，不是间接推断
- **`providerKey` 为 `null` 与显式传别的键不会混淆**：写入 `(name, "G", null)` 后，用 `(name, "G", "somekey")` 查询不命中
- **未命中返回 `null`**：从未写入的 `(name, providerName, providerKey)` 调 `GetOrNullAsync` 返回 `null`，不抛异常
- **删除后读回 `null`**：写入 → 删除 → `GetOrNullAsync` 返回 `null`
- **`GetAllAsync` 对未命中的名称也返回条目**：请求 3 个名称，只有 1 个存在值，返回的 3 条 `SettingValue` 里未命中的 2 条 `Value` 为 `null`，顺序与输入一致
- **`GetAllAsync` 对空数组直接返回空列表**：不发任何 SQL
- **唯一索引确实阻止重复插入**（结构性验证，覆盖 §4.1）：绕过 `SqlSugarSettingStore`，直接用原始 `SqlSugarClient` 对同一个 `(SettingName, ProviderName, ProviderKey)` 组合 `Insertable` 两次，断言第二次抛异常——这是整个 §4.3 竞态处理设计成立的地基，必须单独验证，不能只靠间接测试假设它存在
- **写入命中已存在的行时更新而不是重复插入**：先用原始客户端直接插入一行，再调 `SetAsync` 写同一个键，验证只剩一行、值是最新写入的——这条覆盖的是“先查后写”的常见路径
- **插入前被竞争对手抢先创建同一设置时回退为更新**（确定性覆盖 §4.3 的 `catch` 分支）：`SqlSugarSettingStore` 暴露一个 `protected virtual Task OnBeforeInsertAsync(SysSetting entity, CancellationToken cancellationToken)` 扩展点，在“确认不存在”之后、`Insertable` 之前调用，默认空实现。测试项目定义一个子类，在这个钩子里用另一个指向同一个 SQLite 文件的客户端抢先插入同一行，精确复现“先查阶段判定不存在、写入阶段才分出先后”这个时序，不依赖真实的多线程/多进程并发。断言 `SetAsync` 正常返回、表里只剩一行、值是本次写入的值。**反向验证**：临时删掉 `SetAsync` 里的 `catch` 块重跑这条用例，确认它会变红，证明测试确实在验证这段代码而不是凑巧通过；确认后撤销这个临时改动。`SqlSugarSettingStore` 因此不加 `sealed`——这个钩子就是留给测试用的扩展点，正式代码路径上什么也不做
- **连续两次 `SetAsync` 覆盖同一设置**：第一次写入值 A，第二次写入值 B，读回 B（覆盖成功且不抛 `VersionExceptions`，覆盖 §5.2）
- **注册顶替**：`AddXiHanSettingsSqlSugar` 之后 `ISettingStore` 的描述符指向 `SqlSugarSettingStore`、生命周期为 `Scoped`（模拟主包已用 `TryAdd` 登记 `NullSettingStore` 单例的前提下）

SQLite 临时库连接串必须带 `Pooling=False`，否则用例结束后驱动仍持有文件句柄，清理会抛 `IOException`。

测试项目 Import `props/test.props`（xunit.v3 + Microsoft.Testing.Platform）。**没有可用的筛选参数**（`--filter`、`--list-tests` 返回退出码 3），**不要带 `--logger trx` / `--results-directory`**（退出码 5）。要跑单个测试类就整个项目跑。

## 7. 已知边界

写入 PR 描述与包 README，**不写进代码注释**：

| 项 | 说明 |
| --- | --- |
| 本包的读写会自动登记进当前事务型工作单元 | `ISqlSugarClientResolver.GetClientForEntity` 内部无条件调用 `EnlistCurrentUnitOfWork`（`SqlSugarClientResolver.cs:129-154, 256+`），只要调用路径上存在事务型工作单元（例如动态 API 默认的请求级工作单元），本包的全部读写就钉在这个事务上，与业务写入同生共死。这不是本包主动选择的行为，也没有开关能关闭，只能靠“待确认的决策”里的 `requiresNew` 选项绕开 |
| 并发首次创建同一设置：SQLite/MySQL 下由唯一索引兜底且不抛异常，PostgreSQL 事务内会抛异常并拖垮调用方事务 | 两个调用者同时对同一个从未存在过的 `(name, providerName, providerKey)` 调 `SetAsync`：SQLite/MySQL 下一个成功插入，另一个命中 `uk_sys_setting_key` 冲突后自动转为更新，最终只有一行、不抛异常给调用方。**PostgreSQL 下，若当前处于事务型工作单元内，情况不同**：唯一约束冲突会让整个事务进入 `aborted` 状态，`catch` 块里的重新查询本身失败，原始异常连同这次冲突一起把调用方当次事务拖入无法提交的状态，见 §4.3“边界一”“边界二”、§5.4。若数据库层的这个索引因为跳过建表初始化或手工建表而缺失，唯一性保证本身也不成立 |
| 并发更新同一个已存在的设置无原子性 | 两个调用者同时更新同一个已存在的设置，走的是普通的先查后改，后写覆盖前写，不做乐观锁，也不告知调用方“被别人抢先改过” |
| 空字符串与 `null` 的 `providerKey` 不可区分 | `Normalize` 把两者都归一化为同一个哨兵值，调用方若显式传入空字符串会与该设置的“无特定键”版本读写同一行。当前主包三个调用点都不会产生这种输入，属已接受的边界假设 |
| Oracle 把空字符串当作 `NULL`，归一化哨兵值在 Oracle 上不成立 | 本包用 `string.Empty` 表示“无特定提供者/键”，前提是数据库真的把空字符串存成一个具体的、可参与唯一约束比较的值。Oracle 的 `VARCHAR2` 把 `''` 等同于 `NULL`——`Provider_Key = ''` 在 Oracle 上会被存成 `NULL`，唯一索引又退化回“NULL 各不相同”的老问题，全局设置的唯一性保证失效。当前仓库未把 Oracle 列为支持方言，接入前需要换一个非空字符串哨兵（如 `"-"` 之类的占位符） |
| 建了同名索引就不会再检查其定义是否一致 | `CodeFirstProvider.CreateIndex` 只按索引名判断“是否已存在”（`IsAnyIndex(item.IndexName)`），存在就跳过，不比对字段组成或是否唯一。若某个历史环境曾经手工建过一个同名但非唯一的索引，本包声明的唯一索引永远不会被真正创建，唯一性保证悄悄不成立且没有任何报错 |
| 对已有重复数据的旧表升级会在启动时建索引失败 | 若某个部署在引入本包之前就已经有 `sys_setting` 表、且 `(Setting_Name, Provider_Name, Provider_Key)` 有历史重复行，首次跑 `CodeFirst.InitTables(...)` 尝试建唯一索引时，数据库会因为违反约束而拒绝建索引，这个异常会在应用启动阶段抛出。升级前必须先清理重复数据 |
| `catch (Exception)` 不区分方言 | `SetAsync` 捕获插入失败后统一按“重新查询确认”处理，见 §4.3、§5.3；查不到时原样重新抛出，不吞掉非竞态原因的失败 |
| 不做乐观锁 | `Row_Version` 列存在但不激活校验（不调用 `IsEnableUpdateVersionValidation()`） |
| 建表默认关闭 | `EnableTableInitialization` 默认 `false`（`XiHanSqlSugarCoreOptions.cs`），不开启则 `sys_setting`（含其唯一索引）不存在，首次调用 `SetAsync`/`GetOrNullAsync` 即报表不存在。README 需要提示这一点，配置层面的逃生口就是开启该选项 |
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

**3. 是否参与工作单元事务？—— 会，但不是本包主动选择的，是 `ISqlSugarClientResolver` 的固有行为。**
`GetClientForEntity` 内部无条件调用 `EnlistCurrentUnitOfWork`（`SqlSugarClientResolver.cs:129-154, 256+`）：只要调用路径上存在一个事务型工作单元（例如动态 API 默认的请求级工作单元），本包的读写就会被钉在这个事务上，和业务写入同生共死；没有事务型工作单元时，退化为无事务的独立提交。这不是“设置是否需要跟着业务事务走”这种设计取舍能决定的——`SqlSugarSettingStore` 没有绕开它的开关，除非改用 `GetCurrentClient()`（但会丢失 §4.5 提到的 `[DataSource]` 路由能力）。这一点直接决定了 §4.3 的竞态处理在 PostgreSQL 上不成立，见 §4.3、§7、“待确认的决策”。

**4. 是否需要多库？—— 不需要，单库即可。**
设置是全局配置，不会像 `EventBus.SqlSugar` 的事件那样分布式地跟随业务实体落在不同的模块库。`GetClientForEntity<SysSetting>()` 在未来若真的需要按 `[DataSource(...)]` 路由，可以直接在实体上加标注而不改动 `SqlSugarSettingStore` 的任何方法体——这也是选它而非 `GetCurrentClient()` 的原因。

**5. 顶替方式？—— `Replace`，不能用 `TryAdd`。**
`NullSettingStore` 用 `[Dependency(TryRegister = true)]`，经 `DefaultConventionalRegistrar.AddType` 内部转译为 `services.TryAdd(...)`（`DefaultConventionalRegistrar.cs:58-61`）。`XiHanSettingsSqlSugarModule` 依赖 `XiHanSettingsModule`，装配顺序上后者先跑，`NullSettingStore` 已经登记，本包若也用 `TryAdd` 是空操作。

## 待确认的决策

用户需要审的地方——每一条都是本设计替用户做的选择，不是既定事实：

| 决策 | 选定的默认值 | 理由 | 若想改，影响什么 |
| --- | --- | --- | --- |
| 主键类型 | 雪花 `long`（`IDistributedIdGenerator<long>`） | 契约不暴露 ID 类型，选它只是与 `Auditing.SqlSugar` 同构 | 改成 `Guid` 需要新增顺序 GUID 生成器依赖（`EventBus.SqlSugar` 已有先例），实体基类从 `SugarEntity<long>` 换成 `SugarEntity<Guid>`，其余逻辑不变 |
| 提供者列的可空性与并发首次创建的处理方式 | `Provider_Name`/`Provider_Key` 改为非空列，`null` 归一化为哨兵值 `string.Empty`；三列复合唯一索引 `uk_sys_setting_key`；并发首次创建靠索引冲突 + 一次重新查询兜底，并发更新仍是后写覆盖前写 | 这是本轮评审的结论：早先草案曾以为该版本 SqlSugar 没有声明式复合唯一索引特性（`[SugarIndex]`），核实后发现存在（`Entities/Mapping/SugarMappingAttribute.cs:341-467`），因此改为让数据库真正保证唯一性，而不是容忍重复行 | 若不想要非空列的语义变化（例如下游代码直接读了 `Provider_Key` 列并依赖它可能是 `NULL`），需要回退到可空列 + 放弃唯一索引 + 在读取端做重复行容错，`Security`/`Traffic`/`Upgrade` 三个小包若有类似顾虑可以各自独立决定，不必照抄 |
| `Setting_Value` 列类型 | 大文本（`StaticConfig.CodeFirst_BigString`），不是定长 `varchar` | 加密后密文长度不可预测，且不排除存放较长 JSON 的设置值 | 若确定所有设置值都很短，可改成 `Length = 2000` 左右的定长列以换取更好的索引/存储效率，但需要先盘点现有设置项的实际值长度 |
| 是否给 `SysSetting` 做 SQLite 之外的真实数据库测试 | 不做 | 唯一约束冲突是标准 SQL 行为，`catch` 块的处理逻辑本身不依赖任何方言细节（§5.3），SQLite 足以验证全部设计点；§4.3 的 `OnBeforeInsertAsync` 扩展点已经能在 SQLite 上确定性复现竞态时序 | 若日后要验证特定数据库方言的字符串比较/大小写敏感度差异，需要新增一层 `Assert.SkipWhen` 测试，范式见 `EventBus.SqlSugar` |
| `SetAsync` 是否应该另开一个 `requiresNew` 工作单元，避免拖累调用方的业务事务 | 不这样做，维持现状（读写钉在调用方当前的工作单元上） | 这是本轮评审明确指出、但按“不改变现有语义”的原则暂不采纳的一个选项：`IUnitOfWorkManager.Begin(requiresNew: true)` 能让 `SetAsync` 的写入独立于调用方的业务事务提交，PostgreSQL 下的竞态冲突也不会再拖垮业务事务。但这个改动本身有取舍，不是无条件的改进——见下一列 | 采用后，设置的写入会**即使业务事务回滚也仍然提交**（例如：业务在同一次请求里先改了几个设置、又因为别的校验失败回滚了整个请求，按现状这几个设置的改动会跟着回滚；改成 `requiresNew` 后这几个设置的改动会独立提交、不随业务回滚）。这对“设置是全局配置，通常不需要跟业务事务保持一致”的场景可能是更合理的语义，但对“这批设置修改本就是这次业务操作的一部分、业务失败就该一起回滚”的场景反而是退化。是否采用需要看具体应用场景，`Security`/`Traffic`/`Upgrade` 三个小包若也依赖 `SqlSugarClientResolver` 的同一个行为，应该各自独立评估，不能假设“不改”对它们也是对的 |

## 9. 下一份

拆分方案（`.superpowers/specs/2026-09-23-sqlsugar-remaining-modules-decomposition.md`）第 5 节建议的下一个包是 `Tasks.SqlSugar`（背景作业领取可直接复用 `EventBus.SqlSugar` P4/P6 已验证过的三步抢占协议），随后是 `Authentication.SqlSugar`、`Authorization.SqlSugar`；`Security.SqlSugar`/`Traffic.SqlSugar`/`Upgrade.SqlSugar` 三个小包应直接照抄本文档建立的形状（归一化 + 唯一索引 + 先查后写，而不是容忍重复行）。
