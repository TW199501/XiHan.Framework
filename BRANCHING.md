# 分支規範：本 repo 為【第 1 種：貢獻型 fork】

> 完整規範：[分支規範：三種 repo 類型](https://linear.app/elf-express/document/分支規範三種-repo-類型貢獻型-fork私有-fork自有專案-128bf6e5e1f9)（Linear）
> 本檔只記錄這個 repo 的設定。人與 AI 動手前請先讀完本檔，再讀 `AGENTS.md` 的「本 fork 的分支与 PR 流程」。

## 本 repo 資訊

| 項目                        | 值                                                |
| --------------------------- | ------------------------------------------------- |
| 類型                        | 第 1 種：貢獻型 fork（fork 原作者，會送 PR 回饋） |
| 原作者 repo（`upstream`） | `XiHanFun/XiHan.Framework`                      |
| 我們的 fork（`origin`）   | `TW199501/XiHan.Framework`                      |
| 下游私有 fork               | 無（下游應用以 NuGet 套件引用框架）               |
| 發佈來源                    | `release`；作者不收的修復若要自行發包，以 `release` 打包並發到 GitHub Packages，不在 nuget.org 註冊 |
| 上游貢獻規範                | https://github.com/XiHanFun/.github/blob/main/CONTRIBUTING.md |
| GitHub 預設分支             | `dev`（Claude 自動審查需要工作流在預設分支上）    |

## 遠端設定

```
git remote add upstream https://github.com/XiHanFun/XiHan.Framework.git
git remote -v
```

## 分支用途

| 分支                      | 用途                               | 可否提交                 | 從哪裡更新        |
| ------------------------- | ---------------------------------- | ------------------------ | ----------------- |
| `main`                  | 原作者 `main` 的鏡像，只用來同步 | **不提交，只快轉** | `upstream/main` |
| `dev`                   | 整合測試                           | 只能經由開發分支合併     | `main`          |
| `feat/`、`fix/`、`docs/`、`chore/`、`test/` | 開發                               | 可以                     | 從 `dev` 開出   |
| `pr/xxx`                | 送給原作者的 PR，一個分支一個主題  | 可以                     | 從 `main` 開出  |
| `release`               | 唯一的穩定版，對下游發佈的來源     | **不提交，只快轉** | 測過的 `dev`    |

開發分支命名 `<類型>/e-<Linear 編號>-<英文短名>`（沒有 Linear 編號時省略 `e-<編號>-`），例如 `fix/e-147-script-sh-path`。worktree 放在 repo 同層的 `../XiHan.Framework-<短名>`。

## 常用指令

同步原作者：

```
git fetch upstream
git log --oneline origin/main..upstream/main   # 先列出會帶進來的提交
git switch main
git merge --ff-only upstream/main
git push origin main
```

更新 dev（有衝突時先回報，同意後在 `merge/conflict-<YYYYMMDD>` 分支解決，經 PR 合回 `dev`）：

```
git switch dev
git merge main
git push origin dev
```

開發：

```
git fetch origin
git switch -c feat/e-<編號>-<短名> origin/dev
gh pr create --repo TW199501/XiHan.Framework --base dev --head feat/e-<編號>-<短名>
```

發佈給下游（`dev` 測試通過後）：

```
git fetch origin
git log --oneline origin/release..origin/dev   # 先列出會帶進去的提交，給使用者確認
git switch release
git merge --ff-only origin/dev
git push origin release
```

送 PR 給原作者：

```
git switch -c pr/<短名> main
git config branch.pr/<短名>.pushRemote origin   # 否則 git push 會推到作者的 repo
git cherry-pick -x <該主題的提交>       # 清單先給使用者確認；不 cherry-pick 合併提交
git push -u origin pr/<短名>
gh pr create --repo XiHanFun/XiHan.Framework --base main --head TW199501:pr/<短名>   # 使用者明確同意後才執行
```

## 分支保護設定

* **`main` 與 `release`**：禁止 force push、禁止刪除、只允許維護者 push。**不要**設「必須經過 Pull Request」：這兩條分支是在本機 `merge --ff-only` 後直接 push 的，GitHub 的 PR 合併會產生新提交或新 SHA，無法快轉。
* **`dev`**：禁止 force push、禁止刪除；開發分支（`feat/*`、`fix/*`、`docs/*`、`chore/*`、`test/*`、`merge/*`）一律經 PR 合併，維護者保留直接 push 權限，給沒有衝突的 `main` → `dev` 同步合併使用。

## 禁止事項

* 不在 `main`、`release` 上提交，也不 force push。
* 不把 `main` 當穩定版或部署、打包來源。
* 不直接在 `dev` 上開發。
* 不從 `dev` 或開發分支直接對原作者開 PR；一個 `pr/` 分支只放一個主題。
* 未經使用者明確同意，不對原作者的 repo 開 PR、留言或推送任何東西。
* `--ff-only` 失敗、合併或 cherry-pick 有衝突時，停下來回報，不要自行處理。
