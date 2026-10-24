# 規則格式（rule schema）

規則用來**標註**掃描結果：類別、風險、建議做法，以及要不要預設勾選。規則不會自己執行任何動作；刪除一定要經過使用者在 UI 上確認（PLAN 0 節的硬性原則）。

- 內建規則：`rules/builtin/*.json`，打包在程式內，唯讀。
- 使用者規則：資料資料夾中的 `rules.user.json`（可攜模式在程式旁的 `WindowsTool.data/`，否則 Windows 為 `%APPDATA%\WindowsTool\`）。可以在 UI 的「規則」分頁編輯，也可以直接編輯檔案後按「重新讀取」。

## 檔案格式

```jsonc
{
  "version": 1,
  "rules": [ /* Rule … */ ],
  "disabledBuiltin": ["log-old"]   // 停用的內建分類規則（內建保護規則不能停用）
}
```

- 欄位名稱是 camelCase，列舉值是小寫字串。
- **不認識的欄位一律視為錯誤**（例如把 `pathGlob` 打成 `pathGlobs`），避免規則默默沒有作用。
- 允許 `//` 註解與結尾多餘的逗號。

## Rule

| 欄位 | 說明 |
|---|---|
| `id` | 小寫英文、數字、`.` `_` `-`，最長 64 字元。新增時留白會自動配發 `user-0001`… |
| `name` | 顯示名稱，必填，最長 100 字。 |
| `source` | `builtin` / `user` / `ai`。透過 UI 或 API 寫入的規則一律是 `user`（AI 提案保留 `ai`）。 |
| `enabled` | 預設 `true`。 |
| `kind` | `classify`（分類，預設）/ `protect`（保護）/ `caution`（提醒）。見下方說明。 |
| `platforms` | `["windows", "linux", "macos"]` 的子集合；省略代表所有平台。 |
| `scope` | `{ "type": "global" }`（預設）或 `{ "type": "location", "root": "D:/projects" }`。AI 提案預設只能是 `location`。 |
| `match` | 比對條件，見下方。 |
| `result` | 標註結果，見下方。 |
| `meta` | `createdUtc`、`updatedUtc`、`approvedBy`、`origin`、`hitCount`、`lastHitUtc`。由程式維護。 |

### 三種規則

| kind | 作用 | 範圍 |
|---|---|---|
| `protect` | 命中就是 **受保護**：不可勾選、不可執行，任何規則都蓋不過。 | 延伸到所有子孫；用 `excludeGlob` 開放特定子路徑。 |
| `caution` | 不保護，但把風險至少提高到 `result.risk`（`medium` 或 `high`），並取消預設勾選。 | 延伸到所有子孫。 |
| `classify` | 標註類別、風險、建議動作、是否預設勾選。 | 只有命中的項目本身。 |

裡面含有受保護項目的資料夾也不可勾選（處理它就會動到受保護的項目）。

### match（各條件之間是 AND，陣列內是 OR）

| 欄位 | 說明 |
|---|---|
| `kind` | `any`（預設）/ `dir` / `file`。 |
| `pathGlob` | **必填**。路徑樣式，見下方「路徑樣式」。 |
| `excludeGlob` | 符合任何一個就排除。保護 / 提醒規則用它開放例外路徑。 |
| `nameRegex` | 比對名稱（不含路徑）的正規表示式（Windows 不分大小寫；不會因為寫法而卡住）。 |
| `ext` | 副檔名（`log` 或 `.log` 都可以），只套用在檔案上。 |
| `minSize` | 最小大小（bytes）；資料夾是整個資料夾的大小。 |
| `minAgeDays` | 至少幾天沒有修改；資料夾以裡面最新的檔案為準。 |
| `siblingExists` | 同一層要有符合的項目（名稱，可用 `*` `?`），例如 `["package.json"]`、`["*.csproj"]`。 |
| `childExists` | 資料夾裡面要有符合的項目，例如 `[".git"]`。 |

### 路徑樣式（glob）

- 分隔符號 `/` 或 `\` 都可以；Windows 不分大小寫，Linux 分大小寫。
- `*` 比對一段名稱中的任意字元，`?` 比對一個字元；`**` 比對任意層資料夾（可以是零層）。
- 結尾的 `/**` 代表「裡面所有東西」，**不含資料夾本身**：`{LocalAppData}/Temp/**` 不會標註 Temp 資料夾本身。
- 不是完整路徑、也不以 `**` 開頭的樣式：global 規則代表「任何位置」（`*.bak` 等於 `**/*.bak`）；location 規則以 `scope.root` 為起點。
- `?:` 代表任何磁碟，例如 `?:/pagefile.sys`。
- 路徑變數：`{UserProfile}`、`{LocalAppData}`、`{RoamingAppData}`、`{Temp}`、`{ProgramData}`、`{Windows}`、`{ProgramFiles}`、`{ProgramFiles(x86)}`、`{SystemDrive}`、`{Documents}`、`{Desktop}`、`{Pictures}`、`{Videos}`、`{Music}`。也可以用 `%VAR%`、`${VAR}`、`$VAR` 形式的環境變數（`$VAR` 只有在環境變數存在時才展開，所以 `$Recycle.Bin` 照字面）。
- 過寬的樣式會被擋下（只限分類規則）：例如 `**`、`**/*`、`*.*`、`C:/**`、`/`、`{UserProfile}/**`、`C:/Users/**`、`{Windows}/**`。

### result

| 欄位 | 說明 |
|---|---|
| `category` | 類別名稱，最長 50 字。分類規則必填。 |
| `risk` | 分類規則：`low` / `medium` / `high`；提醒規則：風險下限 `medium` / `high`；保護規則不需要（一律 `protected`）。 |
| `suggestedAction` | `keep` / `review`（預設）/ `recycle` / `quarantine` / `delete` / `command`。 |
| `method` | `suggestedAction = command` 時必填，只能是白名單命令：`nuget-clear`、`npm-cache-clean`、`pip-cache-purge`、`docker-prune`、`dism-component-cleanup`、`wsl-compact`。 |
| `reason` | 說明，最長 500 字。 |
| `autoSelect` | 預設勾選。高風險、永久刪除、保護與提醒規則都不能預設勾選。 |

## 優先序

1. 保護規則（直接命中優先於從上層延伸；都命中時取最具體的）。
2. 分類規則：自訂 → AI → 內建。同一層級取**最具體**的：條件數 × 1000 + 符合的 glob 字面字元數；同分取建立時間較新的，再依 id。
3. 提醒規則：取風險最高的，套用在步驟 2 的結果上（提高風險、取消預設勾選）。

項目詳情會列出「命中的所有規則」以及哪一條生效、哪一條被蓋過。

## 範例

```jsonc
// 只在 D:/projects 底下，把 60 天沒動、超過 100 MB 的 node_modules 標成可回收
{
  "id": "user-0001",
  "name": "閒置的 node_modules",
  "scope": { "type": "location", "root": "D:/projects" },
  "match": {
    "kind": "dir",
    "pathGlob": ["**/node_modules"],
    "siblingExists": ["package.json"],
    "minSize": 104857600,
    "minAgeDays": 60,
    "excludeGlob": ["**/.git/**"]
  },
  "result": {
    "category": "可重建的相依套件",
    "risk": "low",
    "suggestedAction": "recycle",
    "reason": "可以用 npm install 重建",
    "autoSelect": true
  }
}

// 保護一個資料夾（裡面所有東西都不可勾選）
{ "id": "user-keep-photos", "name": "照片備份", "kind": "protect",
  "match": { "pathGlob": ["E:/PhotoBackup"] }, "result": { "category": "照片備份" } }
```

## 寫入前的檢查（Validator）

新增、修改、匯入規則時都會檢查（PLAN 8.4）：格式與欄位值、路徑變數與樣式、過寬的樣式、白名單命令、預設勾選的限制，以及「樣式是否位於受保護路徑底下」。指定掃描結果時會再試套用：回傳命中數量、總大小、前 20 大的樣本；命中受保護項目時擋下，並建議要加的 `excludeGlob`。
