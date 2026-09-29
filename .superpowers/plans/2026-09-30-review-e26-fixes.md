# 全面審查（E-26）修正計畫

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:test-driven-development. 每個行為修正都先寫會失敗的測試（RED），看它失敗，再實作（GREEN）。純文件修改不需要測試。

**Spec**：Linear E-26 及其子議題（E-17/E-18/E-19、E-27~E-38）。每個任務下方已經把該議題的內容完整抄進來，不需要再去讀 Linear。審查詳細內容的本機副本在 `C:\Users\EDDIE\AppData\Local\Temp\claude\E--source-XiHan-XiHan-Framework\90574393-d99d-4685-85bc-153dc25d1750\scratchpad\review\*.md`。

## Global Constraints

- 倉庫規範以根目錄 `AGENTS.md` 為準，先讀一遍。重點：
  - 每個 `.cs` 檔以兩行版權檔頭開頭。
  - 註解與 XML 文件一律**簡體中文**，而且**只寫「這段程式碼做什麼」**。權衡、理由、前後對比、踩坑敘事一律寫進提交信息，不寫進註解。
  - `public` 成員必須有 `<summary>`。
  - file-scoped namespace；表達式體**方法**與建構函式關閉（見 `framework/.editorconfig`）。
- **0 警告 0 錯誤是硬門檻**。驗證命令（在任務指定的 worktree 根目錄執行）：
  `dotnet build framework/XiHan.Framework.slnx -c Release -p:GeneratePackageOnBuild=false`
  - 例外：Task 3 之後的 `fix/review-e26` 分支上，`framework/tool` 的 4 個 CS9057 是已知既有警告，Task 13 會清掉。在那之前，「0 警告」指的是「除了這 4 個 CS9057 之外沒有新警告」。
- 測試跑的是 Microsoft.Testing.Platform：
  - **沒有篩選參數**（`--filter`、`--filter-method` 都不能用），也**不要**加 `--logger trx` 或 `--results-directory`。
  - 跑單一專案：`dotnet test --project framework/test/<專案>/<專案>.csproj -c Release`
- 資料庫測試用 SQLite 檔案庫，範式見 `framework/test/XiHan.Framework.Data.Tests/SoftDeletePurgeTests.cs`。在 SQL 送出前製造交錯的方式是 `Aop.OnLogExecuting`。CI 不起外部服務，不要寫依賴 MySQL 或 Redis 的測試。
- 提交信息用中文 Conventional Commits，作用域是模組的小寫名，例如 `fix(auditing-sqlsugar): ...`。**不要加任何 Co-Authored-By 或 AI 署名行。** 多行訊息先寫進暫存檔，再用 `git commit -F <檔案>`。
- **不要 push，不要開 PR，不要改 `global.json`，不要動 `main` 分支。**
- 不要宣稱倉庫裡有 `.codegraph/` 目錄；`CLAUDE.local.md` 不可提交。
- 已知的 SqlSugar 行為：
  - SQLite 上 `Skip` 不帶 `Take` 會丟掉 ORDER BY。
  - 測試裡 `Options.Create` 要寫全名：`Microsoft.Extensions.Options.Options.Create`。
  - `ExecuteCommandWithOptLockAsync(true)` 在送出 UPDATE 前就把新版本號寫進實體，只有影響 0 行時才還原；如果是執行本身拋例外，實體上會殘留新版本號。
  - SqlSugar 的樂觀鎖（OptLock）只支援單一實體。
- 多租戶語意：
  - 平台就是 0 號租戶。
  - `IMultiTenantEntity` 的讀取過濾是 `TenantId == 0 || TenantId == 當前租戶`。
  - 寫入經過 `EnsureWritableInCurrentTenant` / `TenantWriteGuard` 保護。
  - 切換租戶用 `ICurrentTenant.Change(tenantId)`。

## 分支與工作目錄

| 任務 | 工作目錄（worktree） | 分支 |
|---|---|---|
| Task 1 | `E:\source\XiHan\XiHan.Framework-pr19` | `fix/data-soft-delete-retry`（PR #122，基於 upstream/main） |
| Task 2 | `E:\source\XiHan\XiHan.Framework-pr18` | `fix/data-restore-optimistic-lock`（PR #123，基於 upstream/main） |
| Task 3 ~ 14 | `E:\source\XiHan\XiHan.Framework-review` | `fix/review-e26`（基於 `dev`） |

worktree 由 controller 事先建立好，實作者不要自己建。

---

## Task 1: PR #122 — 軟刪/恢復的失敗快照補上 RowVersion（E-19）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-pr19`（分支 `fix/data-soft-delete-retry`）

**問題**：`SqlSugarSoftDeleteRepository` 的私有 `DeletionState` 快照目前只存 `IsDeleted`、`DeletedTime`、`DeletedId`、`DeletedBy`。`SoftDeleteAsync(entity)` 走的是 `UpdateAsync` → `ExecuteEntityUpdateWithOptLockAsync` → `ExecuteCommandWithOptLockAsync(true)`，SqlSugar 在送出 UPDATE 前就把新版本號寫進 `entity.RowVersion`。如果執行本身拋例外（斷線、死結、逾時、約束違反），實體上會留下一個從沒寫進資料庫的版本號，之後用同一個實體重試，一定會拋 `ConcurrencyConflictException`。

**要求**：
1. `DeletionState` 一併快照 `RowVersion`（`TEntity` 實作 `IEntityBase<TKey>`，其中有 `long RowVersion { get; set; }`），`ApplyTo` 時還原。同步更新 `DeletionState` 的 `<summary>` 與 `<param>`（簡體中文，只描述快照了哪些欄位）。
2. **先寫測試（RED）**：在 `framework/test/XiHan.Framework.Data.Tests/SoftDeleteRetryTests.cs` 新增用例 `SoftDeleteAsync_DatabaseErrorThenRetried_ShouldPersist`：
   - 在建構函式的 SqlSugarScope 設定回呼裡掛 `client.Aop.OnLogExecuting`。以一個欄位（例如 `_failNextUpdate`）控制：遇到以 `UPDATE` 開頭的 SQL 時，把欄位清掉，再拋 `InvalidOperationException("模拟数据库错误")`。
   - 流程：播一筆活動行 → 讀出實體 → 設 `_failNextUpdate = true` → `SoftDeleteAsync(note)` 應拋例外 → 斷言 `note.IsDeleted == false`、`note.RowVersion` 等於讀出時的值 → 再呼叫一次 `SoftDeleteAsync(note)` → 斷言資料庫中該行 `IsDeleted == true`。
   - 先確認這個測試在目前的程式碼上會失敗（重試時拋 `ConcurrencyConflictException`，或 RowVersion 斷言失敗），再實作。
3. 已有的 `SoftDeleteAsync_VersionConflict_ShouldKeepEntityState` 仍須通過。

**驗收**：
- Data.Tests 全部通過。
- 全方案建置 0 警告 0 錯誤。
- 提交一次，訊息範例：`fix(data): 软删失败快照补上行版本，数据库错误后同一实体可重试`。正文寫明：SqlSugar 樂觀鎖在執行拋例外時不會還原版本號；先前「行版本不在快照內」的判斷只在版本衝突時成立，這次更正。

## Task 2: PR #123 — 批量恢復原子化與重複主鍵（E-18）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-pr18`（分支 `fix/data-restore-optimistic-lock`）

**背景**：`SqlSugarRepositoryBase.UpdateRangeIncludingDeletedAsync` 目前是「預讀一次後，逐個實體走 `ExecuteEntityUpdateWithOptLockAsync`」，自己不開交易。第 k 筆失敗時，前 k-1 筆已經提交；這時呼叫端手上實體的記憶體狀態，會和資料庫不一致。

**要求**：
1. **沒有外層交易時，自己開交易**：
   - 在逐筆更新前檢查 `DbClient.Ado.Transaction`。
   - 為 `null`（沒有外層交易）時，用 `DbClient.Ado.BeginTranAsync()` 開交易；全部成功就 `CommitTranAsync()`，任何例外就 `RollbackTranAsync()` 後重新拋出。
   - 已經有外層交易時，直接逐筆執行，不要巢狀開交易。
   - 實作前先用 SqlSugar 5.1.4.221 確認 `Ado.Transaction` 在「工作單元已釘住交易」時確實非 null。確認方式寫進報告。
2. **重複主鍵**：
   - 同一個物件參照出現多次時，去重後只更新一次。
   - 不同物件參照但主鍵相同時，拋 `ArgumentException`（簡體中文訊息，說明集合中同一主鍵對應多個不同實例）。
   - 檢查要在預讀之前做。
3. XML 文件：
   - 更新 `UpdateRangeIncludingDeletedAsync` 的 `<remarks>`：改成「無外層交易時在本方法內開交易，任一實體失敗則整批回滾；有外層交易時由外層決定」。
   - 補上 `<exception cref="ArgumentException">`。
   - 刪掉原本「已寫入的行由外層工作單元的事務決定是否回滾」的說法。
4. `docs/packages/data.md`：軟刪除那一段同步更新批量恢復的語意（原子、重複主鍵會拋例外）。另外補一句：並發物理刪除時的例外型別由 `InvalidOperationException` 變為 `ConcurrencyConflictException`，並寫明目前沒有關閉樂觀鎖的開關。
5. **先寫測試（RED）**，放在 `framework/test/XiHan.Framework.Data.Tests/SoftDeleteRestoreConcurrencyTests.cs`：
   - `RestoreRangeAsync_SecondEntityStale_WithoutTransaction_ShouldRollBackFirst`：播兩筆已刪除行；先讀兩份過期副本；再經新副本恢復並修改第 2 筆（使它的版本前進）；接著 `RestoreRangeAsync([stale1, stale2])`，應拋 `ConcurrencyConflictException`；斷言資料庫中第 1 筆**仍是已刪除**（交易已回滾）。
   - `RestoreRangeAsync_DuplicateKeyDistinctInstances_ShouldThrowArgumentException`：同一主鍵讀出兩個不同實例，一起傳入 → `ArgumentException`；斷言資料庫未改動。
   - `RestoreRangeAsync_SameInstanceTwice_ShouldRestoreOnce`：同一個參照傳兩次 → 成功，資料庫已恢復。
6. 既有測試全部仍須通過。

**驗收**：
- Data.Tests 全部通過。
- 全方案建置 0 警告 0 錯誤。
- 文件站建置成功：`pnpm --dir docs install --frozen-lockfile` 之後執行 `pnpm --dir docs build`。
- 提交一次，範例：`fix(data): 批量恢复无外层事务时整批原子提交，拒绝同主键多实例`。

## Task 3: 在 review 分支整合兩個 PR 並驗證合併後行為

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`（分支 `fix/review-e26`，建立時與 `dev` 同一個 commit）

**要求**：
1. `git merge --no-ff fix/data-soft-delete-retry`，再 `git merge --no-ff fix/data-restore-optimistic-lock`（兩個本地分支已包含 Task 1、Task 2 的提交）。合併訊息範例：`merge: 合并 fix/data-soft-delete-retry（#122 修正）`。有衝突就解決，兩邊的語意都要保留。
2. **先寫測試（RED 或確認已綠）**，在 `SoftDeleteRetryTests.cs` 或 `SoftDeleteRestoreConcurrencyTests.cs` 新增 `RestoreRangeAsync_PartialFailureWithoutTransaction_ShouldKeepMemoryConsistentWithDatabase`：
   - 兩筆已刪除行；第 2 筆製造版本衝突；呼叫 `RestoreRangeAsync` 應拋例外。
   - 斷言：兩個實體在記憶體中都仍是 `IsDeleted == true`，資料庫中兩筆也都仍是已刪除。
   - 這個用例驗證 #122 的整批還原與 #123 的原子交易合在一起後是一致的。如果它一寫下去就通過，照實寫進報告即可，它是合併後的回歸保護。
3. Data.Tests 全部通過；全方案建置不得有新警告（只允許既有的 4 個 CS9057）。
4. 新測試另外提交一次，範例：`test(data): 批量恢复部分失败时内存状态与数据库一致`。

## Task 4: Auditing.SqlSugar — 寫入器按記錄租戶落戳並切入該租戶（E-27，Critical）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**問題**：上游提交 `a25f9d37` 在 `framework/src/XiHan.Framework.Auditing/{Access,Api,Exception,Login,Operation}LogRecord.cs` 新增了 `TenantId`。它的 remarks 寫明：「写入器按它落戳并切入该租户写入，不依赖写入时的环境上下文——排队异步写入时环境里已没有请求的租户」。本包這 5 類日誌的實體沒有租戶欄，mapper 不映射，寫入器也不切換租戶。`EntityDiffLog` 那一條已經處理過，可以參考它的做法（`Entities/SysDiffLog.cs`、`Mapping/AuditingLogMapper.cs` 中 diff log 的部分）。

**要求**：
1. 先讀 5 個 Record 的 `TenantId` 型別（可空或不可空）與 remarks，以及 diff log 現有的做法。
2. 5 個實體（`framework/src/XiHan.Framework.Auditing.SqlSugar/Entities/Sys{Access,Api,Exception,Login,Operation}Log.cs`）加上 `Tenant_Id` 欄：
   - 型別與 Record 對應。
   - 欄名與註解風格比照 `SysDiffLog`。
   - 是否加索引也比照 `SysDiffLog`。
   - **不要**讓這些實體實作 `IMultiTenantEntity`，除非 `SysDiffLog` 也這樣做。維持一致，並在報告中說明依據。
3. `AuditingLogMapper` 映射這 5 類的 `TenantId`。
4. 5 個寫入器（`Writers/SqlSugar{Access,Api,Exception,Login,Operation}LogWriter.cs`）：把「取得客戶端並插入」包在 `using (_currentTenant.Change(record.TenantId))` 內，比照 diff log 寫入器（若它有這樣做）或上游其他按記錄切租戶的寫法。注入 `ICurrentTenant`。
5. **先寫測試（RED）**，在 `framework/test/XiHan.Framework.Auditing.SqlSugar.Tests`：
   - mapper 測試：5 類都斷言 `TenantId` 有映射。
   - 寫入器測試：至少覆蓋一類（例如 AccessLog）。在環境租戶是 A、記錄的 TenantId 是 B 的情況下寫入，斷言落庫的 `Tenant_Id == B`，並斷言取客戶端時的當前租戶是 B（可用記錄當前租戶的替身解析器驗證）。另外 4 類至少有落戳斷言。
6. 文件：`docs/packages/auditing-sqlsugar.md` 與包的 `README.md` 補上表結構的 `Tenant_Id` 欄，以及「寫入器按記錄的租戶落戳並切入該租戶」。另外兩點：
   - 「指定库位」一段原本寫「给实体标注 `[ModuleDataSource]`」，但實體在包內、使用者無法標註。改寫成「繼承實體，或替換 `IEntityModuleDataSourceResolver`」，實作前先讀該介面確認這兩條路都可行。
   - 注意事項補一條：MySQL 上每月第一筆 diff log 若在業務交易中觸發 `SplitTable()` 自動建分表，DDL 會隱式提交該交易；建議預先建好分表。只寫文件，不改程式。
7. 驗收：
   - Auditing.SqlSugar.Tests 通過。
   - 全方案建置沒有新警告。
   - 文件站建置成功。
   - 提交訊息範例：`fix(auditing-sqlsugar): 五类日志按记录租户落戳并切入该租户写入`。

## Task 5: Authentication.SqlSugar — 刷新令牌並發兌換與其他（E-32）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **並發兌換（Important）**：`framework/src/XiHan.Framework.Authentication.SqlSugar/RefreshTokens/SqlSugarRefreshTokenStore.cs` 的 `Remove`（約 150-170 行）。
   - 目前的做法：條件 UPDATE 影響 0 行後，用 `TokenHash == h && RevokedTime != null` 判斷是否已撤銷。在 MySQL 的 RR 隔離級別下，這條 SELECT 讀的是交易快照，會看到「尚未撤銷」而靜默返回，同一枚令牌因此可以被兌換兩次。
   - 改法：影響 0 行時，改判「令牌是否存在」（`TokenHash == h`，不帶 `RevokedTime` 條件）。存在就拋與現在相同的 `InvalidOperationException("刷新令牌已被撤销。")`；不存在就維持現有行為，先讀清楚現在不存在時做什麼，照舊保留。
   - **測試**：SQLite 沒有快照讀，無法重現 MySQL RR 的情況。改用 `Aop.OnLogExecuting` 在條件 UPDATE 執行前，經另一條連線把該令牌撤銷並提交，使 UPDATE 影響 0 行，斷言 `Remove` 拋 `InvalidOperationException`。
     - 這個用例在修正前很可能已經是綠的（SQLite 會讀到已提交的撤銷），照實寫進報告，把它當回歸保護即可。修法本身邏輯簡單，允許沒有 RED。
2. **登入失敗計數回滾（Important，依裁定只改文件）**：不改程式。在 `docs/packages/authentication-sqlsugar.md` 與包 README 的注意事項補一條：`IncrementFailedLoginAttemptsAsync` / `SetLockoutEndAsync` 參與當前工作單元；下游在登入失敗時若拋例外讓交易回滾，失敗計數與鎖定會一起回滾，因此登入失敗時應以回傳值表示失敗，不要拋例外，或在獨立的工作單元中記錄失敗。
3. **Minor**：
   - `SqlSugarUserStore.AddUserAsync`（約 364 行）與 `SqlSugarExternalLoginStore.CreateAsync`（約 103 行）是「先查再插」。插入撞到唯一索引時，捕獲例外後重查：確認已存在就拋 XML 文件承諾的 `InvalidOperationException`（訊息沿用現有的「已存在」訊息）；不存在就原樣重新拋出。做法比照 `framework/src/XiHan.Framework.Upgrade.SqlSugar/Services/SqlSugarUpgradeVersionStore.cs` 的衝突重查。為兩者各補一個以 `OnLogExecuting` 在 INSERT 前插入衝突行的測試。
   - 文件補一句：實體帶 `RowVersion` 版本驗證欄，但本包的存儲以 `SetColumns` 更新、不遞增版本。
4. 驗收：
   - Authentication.SqlSugar.Tests 通過。
   - 全方案建置沒有新警告。
   - 提交訊息範例：`fix(authentication-sqlsugar): 撤销判定改为令牌存在性，唯一冲突按契约转抛`（若有需要，文件另外提交一次）。

## Task 6: Workflow.SqlSugar — 變數屬性名往返與 Code 大小寫（E-30）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **POCO 屬性名（Important）**：
   - 問題：`framework/src/XiHan.Framework.Workflow.SqlSugar/Mapping/WorkflowJsonColumn.cs` 用 `JsonSerializerOptions.Web`，其中 `PropertyNamingPolicy` 是 CamelCase。
   - 改法：改用一個靜態唯讀選項 `new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = null }`。確認 `PropertyNameCaseInsensitive` 仍為 true，並確認字典鍵原樣保留：`DictionaryKeyPolicy` 在 Web 預設是 null，但要確認。
   - **先寫測試（RED）**，放在 Workflow.SqlSugar.Tests：把一個含 POCO 值（例如 `new Order { Amount = 100 }`）的變數字典存進實例，讀回後用框架的表達式求值 `order.Amount > 50`，應得 true。實作前先讀 `XiHan.Framework.Workflow` 裡 `ExpressionParser` 與 `WorkflowValueConverter` 的用法，找到最貼近引擎實際行為的求值入口。
2. **Code 大小寫（Important）**：
   - 問題：`Stores/SqlSugarWorkflowDefinitionStore.cs` 中以 Code 查詢的方法（`FindLatestPublishedAsync`、`GetMaxVersionAsync`，以及其他以 Code 為條件的 `Find*`），和 `SqlSugarWorkflowInstanceStore.cs` 以 Code 查詢的方法。
   - 改法：資料庫條件取回候選後，再用 `StringComparison.Ordinal` 過濾，做法照抄 `SqlSugarWorkflowBookmarkStore.cs` 現有的 ordinal 後過濾。排序（版本降序取第一）必須在過濾之後才生效。
   - 測試：SQLite 預設區分大小寫，測不出 MySQL 的行為。改用單元層級的測試：在庫裡放 `Leave` v2 與 `leave` v1，查 `leave` 必須得到 v1。這在 SQLite 上本來就會通過，所以要確認它確實經過了 ordinal 過濾的程式路徑，實作後再寫；如實報告它不是 RED。
   - 文件補一句：唯一索引 `(Code, Version)` 在不區分大小寫的排序規則下，`Leave` 與 `leave` 會互相衝突，Code 請保持大小寫一致。
3. **Minor**：
   - `docs/packages/workflow-sqlsugar.md`「注意事项」改寫讀回型別：讀回後的值都是 `JsonElement`，需要經 `WorkflowVariables.Get<T>` / `WorkflowValueConverter` 取值。先讀程式碼確認，照實寫。
   - `docs/guide/workflow.md`「换成持久化存储」的範例：依賴 Scoped 資料服務時改用 `Scoped` 註冊，並說明理由僅限一句「依赖 Scoped 服务时须用 Scoped」。
   - `docs/packages/workflow-sqlsugar.md` 補一句：書籤 `UpdateAsync` 在行不存在時不插入，這點與記憶體實作不同。
4. 驗收：
   - Workflow.SqlSugar.Tests 通過。
   - 全方案建置沒有新警告。
   - 文件站建置成功。
   - 提交訊息範例：`fix(workflow-sqlsugar): 变量序列化保留属性名，编码查询按序数过滤`。

## Task 7: Traffic.SqlSugar — 快取單飛刷新與庫宕機時保留舊規則（E-35）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**檔案**：`framework/src/XiHan.Framework.Traffic.SqlSugar/Repositories/SqlSugarGrayRuleRepository.cs`

**要求**：
1. **單飛刷新（Important）**：
   - 用 `SemaphoreSlim(1,1)` 取代「只保護字典替換」的 `lock`。`EnsureFreshAsync` 先判斷是否到期；到期才 `WaitAsync`，取得鎖後**再判斷一次**是否仍需刷新（別的呼叫可能剛刷新完），需要才查庫。
   - `_lastRefreshTime` 改存 `long` UTC ticks，用 `Volatile.Read` / `Volatile.Write` 讀寫。
   - `RefreshAsync`（公開的強制刷新）同樣在鎖內查庫。
2. **失敗保留舊快取（Important）**：
   - 查庫拋例外時：記一條 warning（注入 `ILogger<SqlSugarGrayRuleRepository>`，比照同包或上游的日誌用法）；**不**替換 `_cache`；把下次刷新時間往後推一段退避時間，退避時間取 `RefreshInterval` 與 5 秒兩者中較小的值，也就是 `_lastRefreshTime` 設成 `now - RefreshInterval + backoff`。
   - `EnsureFreshAsync` 內的刷新失敗不向外拋，讓讀取方法回傳舊快取。
   - 公開的 `RefreshAsync` 仍向外拋例外：它是「強制刷新」，呼叫方需要知道失敗。刷新失敗時的快取與退避處理仍要做。
   - 快取從未成功載入過（`_cache` 為空且從未成功）時，照樣回傳空集合，不拋例外，行為和現在的 `NotGray` 一致。
3. **先寫測試（RED）**，放在 Traffic.SqlSugar.Tests：
   - 並發測試：快取到期時用 `Task.WhenAll` 並發 N 個讀取；以 `OnLogExecuting` 計算 `sys_gray_rule` 的 SELECT 次數，斷言只有 1 次。
   - 宕機測試：先成功載入一條規則 → 讓下一次查庫拋例外（`OnLogExecuting` 對 SELECT 拋例外）→ 讓快取到期 → 讀取仍回傳舊規則 → 退避期間再讀取時，不再查庫。
4. **Minor**：
   - 刷新時用 `IUnitOfWorkManager.Begin(new UnitOfWorkOptions { IsTransactional = false }, requiresNew: true)` 包起來，避免掛進呼叫方的環境工作單元。先讀 `XiHan.Framework.Uow` 的實際 API 名稱與參數，照實使用。
   - 第 73 行附近的註解，以及 `docs/packages/traffic-sqlsugar.md`「工作原理」中的「宿主」改成「平台（0 号租户）」。
   - `docs/packages/traffic-sqlsugar.md` 的表結構把 `Created_Time`、`Updated_Time` 拆成兩列，並標明 `Updated_Time` 可空（對照 `SysGrayRule.cs`）。
   - 文件注意事項補上「库不可用时保留上次成功加载的规则并退避重试」。
5. 驗收：
   - Traffic.SqlSugar.Tests 通過。
   - 全方案建置沒有新警告。
   - 文件站建置成功。
   - 提交訊息範例：`fix(traffic-sqlsugar): 规则缓存单飞刷新，库不可用时保留旧规则并退避`。

## Task 8: Security.SqlSugar — 具體型別可注入、排序決勝、索引（E-36）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **具體型別可注入（Important）**：
   - 在 `framework/src/XiHan.Framework.Security.SqlSugar/Extensions/DependencyInjection/` 的註冊方法中，改成：`services.TryAddScoped<SqlSugarPasswordHistoryStore>();`，再用 `services.Replace(ServiceDescriptor.Scoped<IPasswordHistoryStore>(sp => sp.GetRequiredService<SqlSugarPasswordHistoryStore>()))`。這樣同一個作用域內，介面與具體型別拿到的是同一個實例。
   - **先寫測試（RED）**：用 `ServiceCollection` 呼叫註冊方法並 `BuildServiceProvider`，在一個作用域內解析 `SqlSugarPasswordHistoryStore`，應成功，而且與解析 `IPasswordHistoryStore` 得到的是同一個實例。需要的依賴比照現有註冊測試的做法補上替身。
2. **Minor**：
   - `SqlSugarPasswordHistoryStore` 讀取與裁剪的排序（約 51、84 行）都加上 `BasicId` 作為第二排序鍵，方向與 `CreatedTime` 一致。
   - `SysPasswordHistory` 加 `[SugarIndex]`：索引名 `idx_sys_password_history_user`，欄位 `User_Id` 升序、`Created_Time` 降序。索引命名比照同倉庫其他 SugarIndex 的慣例，先讀一個現有例子。
   - 補一個測試：同一個 `CreatedTime` 的兩筆記錄，裁剪保留 `BasicId` 較大的那筆。
3. 文件：`docs/packages/security-sqlsugar.md` 與包 README 的說明維持「注入 `SqlSugarPasswordHistoryStore` 後呼叫 `RecordPasswordAsync`」，現在這樣寫是對的。表結構補上新索引。
4. 驗收：
   - Security.SqlSugar.Tests 通過。
   - 全方案建置沒有新警告。
   - 提交訊息範例：`fix(security-sqlsugar): 具体类型可注入，排序补决胜键并加用户索引`。

## Task 9: EventBus.SqlSugar — 庫隔離租戶 fail-closed、選項驗證與其他（E-28）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **庫隔離租戶的事件不投遞（Important）**：
   - 問題：`Outbox/SqlSugarEventOutbox.cs` 入箱時用 `GetCurrentClient()`，在庫隔離租戶下會解析到租戶庫；發送循環只遍歷預設佈局，所以這些事件永遠不會投遞。
   - 改法：入箱時判斷當前佈局是否與平台佈局相同。做法是先讀 `ISqlSugarClientResolver` / `SqlSugarClientResolver` 找出可用的判斷方式，例如比較 `GetCurrentLayoutConfigIds()` 在當前租戶與 `Change(null)` 下是否相同，或 resolver 有更直接的 API。不同時拋 `InvalidOperationException`（簡體中文，說明發件箱不支援租戶獨立庫），屬於 fail-closed。
   - **先寫測試（RED）**：用替身 resolver 模擬「當前佈局 ≠ 平台佈局」，入箱應拋例外；相同時照常入箱。
   - 文件：把「租户独立库不在范围内」改寫成「租户独立库下入箱会抛异常」。
2. **選項驗證（Important）**：
   - 在 `XiHanSqlSugarEventBoxOptions` 的註冊處，改用 `services.AddOptions<XiHanSqlSugarEventBoxOptions>().Bind(...).Validate(o => o.ClaimTimeout > TimeSpan.Zero && o.InboxRetentionPeriod > TimeSpan.Zero, "<简体中文讯息>").ValidateOnStart()`。先讀現有的註冊方式，Bind 的節名沿用現有的。
   - 測試：`ClaimTimeout = TimeSpan.Zero` 時，取 `IOptions<>.Value` 應拋 `OptionsValidationException`。
3. **diff log 與發件箱衝突（Important，依裁定只改文件）**：
   - 不改程式。在 `docs/packages/eventbus-sqlsugar.md` 的「发件箱入箱」注意事項，以及 `docs/packages/auditing-sqlsugar.md`，補一條：業務實體位於模組庫又開啟 `EnableDiffLog` 時，diff log 寫入器會把主庫也登記進工作單元，登記變成兩個庫；此時在同一工作單元內以 `onUnitOfWorkComplete: false` 發布分散式事件，會觸發「多于一个登记连接」的例外。避開方式：事件改在工作單元完成時發布（`onUnitOfWorkComplete: true`），或該類實體不開 diff log。
   - 先讀 `SqlSugarEventOutbox.ResolveEnqueueClient` 與 diff log 寫入器，確認這個描述正確再寫。
4. **Minor**：
   - `Inbox/SqlSugarEventInbox.cs` 的 `RetryLaterAsync` 與 `MarkAsHandledAsync`（約 182、244 行），WHERE 條件加上「目前狀態為已領取」（實際的狀態常數以程式碼為準），已完結的狀態不回退。補測試：先 `MarkAsHandledAsync`，再呼叫 `RetryLaterAsync`，狀態仍是已處理。
   - `SysEventOutbox` 加 `(Status, Created_Time)` 的 `[SugarIndex]`，命名比照收件箱。
   - 文件補兩點：發件箱投遞失敗要等滿 `ClaimTimeout` 才重試，且沒有重試次數上限；配額 `maxCount / 库数` 在積壓集中於單一庫時，吞吐會被壓低。
5. 驗收：
   - EventBus.SqlSugar.Tests 通過。
   - 全方案建置沒有新警告。
   - 文件站建置成功。
   - 提交訊息範例：`fix(eventbus-sqlsugar): 租户独立库入箱失败即拒，选项启动校验，收件箱状态不回退`。

## Task 10: Tasks.SqlSugar / Tasks — 領取批量上限、同交易入隊測試、契約文件（E-29）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **領取批量上限（Important）**：
   - 在 `XiHanTasksSqlSugarOptions` 新增 `MaxClaimBatchSize`（`int`，預設 50），附簡體中文 `<summary>`。
   - `SqlSugarBackgroundJobStore` 的領取方法把實際領取數量截到 `Math.Min(maxResultCount, MaxClaimBatchSize)`。
   - 設定值小於等於 0 時啟動驗證失敗，比照 Task 9 的 `Validate().ValidateOnStart()`；先讀這個選項現有的註冊方式。
   - **先寫測試（RED）**：入隊 60 筆，呼叫領取並傳入 `maxResultCount = 1000`，只應領到 50 筆。
   - 文件補上新選項，並說明它讓一輪共用同一個租約的作業數受限。
2. **同交易入隊測試（Important）**：
   - 在 Tasks.SqlSugar.Tests 以真實的 `UnitOfWorkManager` 與 `SqlSugarClientResolver`，接上 SQLite 檔案庫，補兩個用例：
     - 交易型工作單元內入隊後回滾，查不到作業；
     - 提交後查得到。
   - 先讀 Data.Tests 或 Tasks.SqlSugar.Tests 中已有的、用真實 resolver 的測試設定。沒有的話，照 `framework/test/XiHan.Framework.Data.Tests/RequiresNewIsolationTests.cs` 的組裝方式。
   - 這是補測試，不改行為，預期一寫下去就綠。報告中要寫明你如何確認它真的經過交易（例如回滾用例在把入隊改成走獨立連線時會失敗；做法是暫時修改、確認失敗後再還原）。
3. **契約文件（Important）**：
   - `framework/src/XiHan.Framework.Tasks` 中 `IJobStore.SaveJobInstanceAsync` 的 `<summary>` 註明：同一實例會被保存多次，已存在時應更新。
   - 在 Tasks.Tests 為 `JobExecutor` 補**成功路徑**測試：成功執行後，存儲收到的實例帶有耗時與完成時間。現有兩個測試只覆蓋失敗路徑。
4. **Minor**：
   - `SqlSugarJobStore.UpdateJobStatusAsync`（約 83-100 行）：終止狀態只在 `CompletedAt` 為 null 時才寫入 `CompletedAt`。
   - `CleanupHistoryAsync`（約 238 行）一併清掉 `Status == Running` 且 `RunningDeadline < cutoff` 的遺留實例。
   - `sys_job_instance.CompletedAt`、`sys_job_history.StartedAt` 各加單欄索引。
   - 文件補兩點：提前結束的一輪不會釋放租約；SQL Server 未開 RCSI 時，領取會被未提交的入隊交易阻塞。
   - 以上各補對應測試（索引除外）。
5. 驗收：
   - Tasks.SqlSugar.Tests、Tasks.Tests 通過。
   - 全方案建置沒有新警告。
   - 文件站建置成功。
   - 分兩次提交：
     - Tasks 核心：`docs(tasks): 注明 IJobStore 实例可能被多次保存，补 JobExecutor 成功路径测试`；
     - SqlSugar 包：`fix(tasks-sqlsugar): 领取批量上限，终止时间不被覆盖，清理遗留运行实例`。

## Task 11: Authorization / Upgrade / Settings — 文件與小修（E-31、E-33、E-34）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **Authorization（Important，依裁定只改文件）**：
   - `framework/src/XiHan.Framework.Authorization.SqlSugar/README.md` 與 `docs/packages/authorization-sqlsugar.md`：把「权限定义与策略全局可写 / 所有租户共用」改寫為：「权限定义与策略存放在当前租户布局的主库：共享库部署下各租户共用平台播种的数据；租户独立库部署下每个租户库各有一份，须在每个租户库内播种，平台态写入只影响平台库」。
   - 找出並修正文件中與此矛盾的句子，例如「六张表始终在当前租户的主库」要和上面的說法一致。
   - 先讀 `SqlSugarClientResolver` 的佈局邏輯，確認這個描述正確。
   - 播種範例改成先 `using var scope = serviceProvider.CreateScope();`，再從 `scope.ServiceProvider` 解析。
   - 文件註明 `AddPermissionsAsync` 逐條寫入、不保證原子性。
2. **Upgrade（Minor）**：
   - `docs/packages/upgrade-sqlsugar.md`「相关模块」中指向 `./settings-sqlsugar` 的連結保留（dev 上存在），不處理。
   - PostgreSQL 交易中止的說明，把 `TryCreateBaselineAsync` 一併寫進去。
   - `SqlSugarUpgradeVersionStore.HasMigrationHistoryAsync`（約 256 行）：查詢前對 `version` 做與寫入時相同的 `NormalizeVersion`。**先寫測試（RED）**：以前後帶空白的版本字串查詢，應查得到。
   - 大小寫比對的差異只寫進文件。
3. **Settings（Minor）**：
   - `SqlSugarSettingStore.cs` 約 119 行的 catch：重查 `FindAsync` 如果又拋例外，保留原始插入例外。做法：`catch (Exception ex)`，重查包在內層 try 中；內層失敗時 `throw new AggregateException(ex, inner)`；重查確認不存在時 `throw;`。測試：以 `OnLogExecuting` 讓 INSERT 拋例外、再讓接下來的 SELECT 也拋例外，斷言例外中含原始的插入例外。
   - `docs/packages/settings-sqlsugar.md` 的建表配置補上 `EnableDbInitialization`，比照其他 SqlSugar 頁的寫法。
4. 驗收：
   - 相關測試專案通過。
   - 全方案建置沒有新警告。
   - 文件站建置成功。
   - 分包提交：`docs(authorization-sqlsugar): ...`、`fix(upgrade-sqlsugar): ...`、`fix(settings-sqlsugar): ...`。

## Task 12: 文件站 — 主包頁與指南仍寫「必須自己實作」、架構圖補新包（E-38）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. 以下各處把「必須 / 需要自行實作」改寫成：「用 SqlSugar 可直接依赖 [XiHan.Framework.X.SqlSugar](./x-sqlsugar)（指南裡寫成 `../packages/x-sqlsugar`），或自行实现」。句式比照 `docs/guide/upgrade.md` 已有的寫法，先讀它。
   - `docs/packages/security.md` 約 223-224 行：另外把只提靜態 `RecordPassword` 的地方補上 `SqlSugarPasswordHistoryStore.RecordPasswordAsync`。
   - `docs/packages/tasks.md` 約 405、416 行
   - `docs/packages/authorization.md` 約 185 行
   - `docs/packages/authentication.md` 約 129、275 行
   - `docs/packages/workflow.md` 約 54-56 行
   - `docs/guide/authentication.md` 約 102 行
   - `docs/guide/authorization.md` 約 180 行
   - 行號是審查當時的位置，以實際內容為準。改完後全文搜尋「自行实现 / 自己实现 / 必须实现 / 由应用实现 / 替换为数据库实现」等說法，逐一確認是否都已和 SqlSugar 包的存在一致。
2. `framework/README.md` 與 `framework/README_cn.md` 的依賴架構圖（約 157-185 行）：在相應位置加一行，標出 10 個 `*.SqlSugar` 持久化子包，形式比照圖中兄弟子包（例如 `EventBus.RabbitMQ`）的畫法。根目錄 `README.md` / `README_cn.md` 若有模組清單，補上這 10 個包，寫法與清單既有格式一致；若根 README 只有計數沒有清單，就不處理，並在報告中說明。
3. `docs/guide/auditing.md` 失敗處理那一格：把多句的 PostgreSQL / SQL Server 交易行為，移到表格後方的獨立 `::: warning` 區塊，表格內只留一句摘要。
4. 驗收：
   - 文件站建置成功，沒有死鏈。
   - 提交訊息範例：`docs: 主包页与指南对齐 SqlSugar 持久化子包，架构图补新包`。

## Task 13: Docs MCP / Web.Mcp — 清掉 CS9057、重複死碼、明文綁定與敘事註解（E-37）

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. **CS9057**：
   - 從以下 4 個 csproj 移除對 `XiHan.Framework.Analyzers` 的 `OutputItemType="Analyzer"` 引用：
     - `framework/tool/XiHan.Framework.Docs.Mcp/XiHan.Framework.Docs.Mcp.csproj`
     - `framework/tool/XiHan.Framework.Docs.Mcp.Web/XiHan.Framework.Docs.Mcp.Web.csproj`
     - `framework/test/XiHan.Framework.Docs.Mcp.Tests/*.csproj`
     - `framework/test/XiHan.Framework.Docs.Mcp.Web.Tests/*.csproj`
   - 做完後，全方案建置必須是**真正的 0 警告**。
2. **重複死碼**：
   - `git rm framework/src/XiHan.Framework.Web.Mcp/Filters/McpToolExposureFilter.cs framework/test/XiHan.Framework.Web.Mcp.Tests/Filters/McpToolExposureFilterTests.cs`。
   - 之後 `git diff upstream/main -- framework/src/XiHan.Framework.Web.Mcp framework/test/XiHan.Framework.Web.Mcp.Tests` 應為空；若不為空，把差異寫進報告，不要擅自再刪。
3. **明文綁定**：
   - `framework/tool/XiHan.Framework.Docs.Mcp.Web/README.md` 約 103 行，把 `0.0.0.0` 改成 `127.0.0.1`，並指向同目錄 deploy 下的反向代理範例。
   - 在 `Program.cs`（或 Kestrel 設定處）設定 `MaxRequestBodySize = 64 * 1024`。
   - `SearchDocs` 工具的 `query` 參數超過 512 字元時，回傳錯誤訊息，不執行檢索。比照該工具現有的錯誤回傳方式。
   - 補測試：超長 query 會被拒。
   - 工具把 `ex.Message` 回給遠端的地方（`DocsMcpTools.cs` 約 164、294、378 行）：改成回傳不含例外訊息的通用錯誤，例外本身記到 log。先確認工具類別能取得 `ILogger`，取不到就只改回傳內容，並在報告中說明。
4. **敘事註解**：以下各處，把講理由、權衡、踩坑過程的註解改寫成只描述「做什麼」的一句話（簡體中文），刪掉的論述整理成一段文字寫進提交信息。
   - `framework/tool/XiHan.Framework.Docs.Mcp.Web/XiHan.Framework.Docs.Mcp.Web.csproj` 約 37-44 行
   - `DocsMcpOptions.cs` 約 323-333 行
   - `DocIndex.cs` 約 19-23 行
   - `XiHanDocsMcpWebOptionsValidator.cs` 約 256-276 行
   - `DocsMcpTools.cs` 約 26-40、87-91、199-206 行
   - `GoldenQueryTests.cs` 的 remarks
   - `.github/codecoverage.settings.xml` 約 3-31 行
   - 行號以實際內容為準。判斷標準不是看有沒有「为什么 / 宁可 / 否则」這類字眼，而是看這段是不是在講論證、權衡或事件經過；前後對比的故事、設計理由、反事實推理都算，**必須逐段人工通讀**。上面列的只是審查員的舉例，同目錄下其他檔案也要一併通讀：`framework/tool/**`、`framework/test/XiHan.Framework.Docs.Mcp*/**`。
5. **Minor**：
   - `.mcp.json.example` 裡的本機絕對路徑 `E:/source/XiHan/XiHan.Framework/...` 改成佔位符 `<repo-root>/...`。
   - slnx 中 `/4.tool/2.DocsMcp/` 與 `/4.tool/3.DocsMcpWeb/` 合併成一個資料夾 `/4.tool/2.DocsMcp/`。
   - `.github/workflows/` 中 `actions/checkout`、`actions/setup-node` 釘到 commit SHA，版本與註解格式比照 `claude-code-action` 的釘法。要取得對應 tag 的 SHA，用 `git ls-remote https://github.com/actions/checkout refs/tags/v5*`，找出確切的 tag 與 SHA，並寫進報告。
6. 驗收：
   - 全方案建置**0 警告 0 錯誤**。
   - Docs.Mcp.Tests、Docs.Mcp.Web.Tests、Web.Mcp.Tests 通過。
   - 分兩次提交：
     - `build(docs-mcp): 移除分析器引用消除 CS9057，删除与上游重复的 McpToolExposureFilter`；
     - `fix(docs-mcp): 仅绑定本机，限制请求体与查询长度，不向远端回传异常信息；注释只保留行为描述`。

## Task 14: 全量驗證

**工作目錄**：`E:\source\XiHan\XiHan.Framework-review`

**要求**：
1. 全方案建置：**0 警告 0 錯誤**。
2. 全量測試：`dotnet test --solution framework/XiHan.Framework.slnx -c Release`，全部通過（依賴外部服務的測試會自行跳過）。偶發失敗先重跑一次；仍失敗就寫進報告，不修改與本計畫無關的程式碼。
3. 文件站建置成功。
4. 不需要提交；報告寫明三個命令的結果數字。
