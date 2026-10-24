# WindowsTool 專案執行計畫

> 磁碟空間分析與清理工具：確定性的掃描與執行引擎、可沉澱的規則庫，加上可選的 AI 分析；另附**快速檔案搜尋**（見 9A）。
> 專案名稱：`WindowsTool`（2026-10-24 定案）。授權：MIT。前端：Vue 3（內嵌，見第 3 節）。

---

## 0. 交接說明（給執行者）

- **執行環境**：在 Windows 筆電上用 Claude Code 開發，用本機的 git 憑證推到 GitHub。
- **先決條件**：.NET 10 SDK、Git、GitHub repo（空的）、VS 2022/2026 或 VS Code + C# Dev Kit。
- **工作方式**：依第 17 節的分期進行，每完成一個 Phase 都要通過該 Phase 的驗收條件才能 commit / 開 PR。
- **硬性原則**（任何階段都不能違反）：
  1. AI 不負責執行任何檔案操作，只產出建議和規則提案。
  2. 規則只負責「分類 + 預設勾選」，刪除一定要經過使用者在 UI 上確認。
  3. 刪除預設是可以還原的（資源回收筒或隔離區），永久刪除要額外確認。
  4. 保護路徑規則永遠最優先，AI 和使用者規則都不能覆蓋。
- 文件中標示 **【實作時確認】** 的項目，代表外部工具的參數或 API 可能因版本不同而變動，要先查官方文件或 `--help` 再實作。

---

## 1. 背景與目標

### 1.1 痛點
- 現有工具（WizTree、WinDirStat、TreeSize）能列出大檔，但**不會判斷內容能不能刪、該怎麼刪**。
- 讓 AI 在終端機裡協助清理時，只能用口語描述哪些可以刪；內容一複雜，大量時間就花在來回對話上。
- 缺少一個**結構化的決策介面**：AI 的分析結果和人的審核決策之間，沒有共同的資料格式。

### 1.2 目標
1. 可以掃描**任何有權限存取的位置**（不只 C 槽），依條件篩選大型資料夾或檔案。
2. 預設功能（掃描、篩選、刪除、搬移）**完全離線**，不用 AI 也能用。
3. 內建常見規則，自動標註類別、風險和建議做法。
4. 使用者**主動要求**時才呼叫 AI，用來分析未知項目，或用自然語言建立個人化規則。
5. AI 產出的規則經使用者審核後**沉澱到本地 JSON**，越用越不需要 AI。
6. 免安裝 runtime，打包成單一執行檔，跨平台（Windows 為主，Linux / macOS 次之）。
7. AI 供應商可以切換：訂閱型 CLI（Claude / Codex / Gemini）、OpenAI 相容 API、本地模型。
8. **快速檔案搜尋**：在指定位置下，依檔名、副檔名、大小、日期等條件快速找檔案，速度明顯優於 Windows 檔案總管的搜尋（見 9A）。

### 1.3 非目標（第一版不做）
- 不做系統登錄（Registry）清理。
- 不做重複檔案比對（列為未來功能）。
- 不做常駐背景或排程清理。
- 不讀取檔案內容做深度分析（只送中繼資料給 AI，見 12.4）。

---

## 2. 設計原則

| 原則 | 說明 |
|---|---|
| 確定性優先 | 掃描、比對規則、執行都用確定性的程式碼完成，可以重複執行、可以測試。 |
| AI 是可選的 | 沒有設定任何 AI Provider 時，所有預設功能都要完整可用。 |
| 提案 → 審核 → 寫入 | AI 的輸出只是提案，要通過 schema 驗證、dry-run 影響範圍、保護規則檢查和使用者確認，才會生效。 |
| 預設可還原 | 資源回收筒或隔離區優先；永久刪除需要二次確認。 |
| 可追溯 | 每次執行都寫入操作紀錄（誰、何時、用哪條規則、做了什麼、結果）。 |
| 規則可攜 | 規則不寫死絕對路徑，用變數 token 和相對 glob，才能套用到任意位置。 |

---

## 3. 技術選型

| 層 | 選擇 | 理由 |
|---|---|---|
| 語言 / Runtime | **C# / .NET 10（LTS）** | 原生支援 self-contained 單檔發佈；可以從單一機器**交叉產出**多平台執行檔；掃描效能好。 |
| 後端 | ASP.NET Core Minimal API | 輕量，同一個行程同時提供 API 和靜態網頁。 |
| 前端 | 靜態 HTML + **Vue 3（`vue.global.prod.js` 3.5.43，內嵌）** | 檔案以 Embedded Resource 打包進 exe，不需要 Node 建置流程。Vue 只有單一檔案（約 170KB、MIT、無其他相依），下載後放進 `wwwroot/lib/` 並記錄 SHA256，**執行時不連外**；授權聲明寫在 `THIRD-PARTY-NOTICES.md`。元件用 JS 物件 + template 字串撰寫（不用 `.vue` 單檔元件）。 |
| 啟動方式 | 執行 exe → 綁定 `127.0.0.1` 隨機埠 → 自動開預設瀏覽器 | 天生跨平台；之後可以選擇性套上 Photino.NET 變成原生視窗。 |
| JSON | System.Text.Json + Source Generator | 為 trimming 做準備，避免反射相關的程式被裁掉。 |
| AI 抽象 | **Microsoft.Extensions.AI**（`IChatClient`）| 微軟官方抽象層；OpenAI 相容、Ollama、Anthropic 都有實作。【實作時確認各套件版本】|
| MCP（Phase 6）| ModelContextProtocol C# SDK | 讓外部 AI 客戶端驅動本工具。【實作時確認套件名稱與版本】|
| 測試 | **xUnit v3 + Shouldly** | FluentAssertions 從 v8 起改為商用授權，因此採用 Shouldly。.NET 10 SDK 搭配 xUnit v3 使用 Microsoft.Testing.Platform 執行器（設定在 `global.json`）。 |
| CI | GitHub Actions | 矩陣建置三平台並上傳 artifacts。 |

---

## 4. 系統架構

```
┌─────────────────────────────── WindowsTool.exe ───────────────────────────────┐
│                                                                             │
│  [Web UI] (embedded static)  ←→  [Minimal API]  (127.0.0.1 + session token) │
│                                        │                                    │
│        ┌───────────────┬───────────────┼────────────────┬────────────────┐  │
│        ▼               ▼               ▼                ▼                ▼  │
│   [Scanner]     [Rule Engine]     [Executor]      [AI Gateway]     [Storage]│
│   確定性掃描     比對/分類/保護    dry-run/執行/     IChatClient       JSON 檔 │
│   (MFT 選用)     內建+使用者規則   紀錄/還原         多 Provider       設定/規則│
│                                                     │                 /紀錄  │
│                                                     ▼                       │
│                                   CLI(claude/codex/gemini) / OpenAI 相容 /   │
│                                   Anthropic / Ollama / LM Studio            │
└─────────────────────────────────────────────────────────────────────────────┘
                     ▲
                     │ (Phase 6) MCP Server 模式：外部 AI 客戶端呼叫 scan / list / propose_rule
```

> Scanner 和 **Searcher（9A 快速檔案搜尋）** 共用同一個底層的平行目錄走訪器 `ParallelWalker`（`Core/FileSystem/`），reparse point、拒絕存取、取消等行為一致。

### 4.1 主要流程

```
選擇位置 + 掃描條件
   → Scanner 產出 ScanResult
   → Rule Engine 標註（類別 / 風險 / 建議 / 命中規則）
   → UI 呈現（樹狀 + 表格 + 篩選 + 風險色標）
   → [選用] 使用者選取未知項目 → AI 分析 → 標註 + 規則提案
   → [選用] 規則提案審核（dry-run 影響範圍）→ 寫入 rules.user.json
   → 使用者勾選並指定動作 → 產生 Plan → dry-run 預覽
   → 確認執行 → Executor 執行 → ActionLog → 可還原
```

---

## 5. Solution 結構

```
WindowsTool/
├─ src/
│  ├─ WindowsTool.Core/            # 純邏輯，不依賴 ASP.NET
│  │  ├─ FileSystem/            # ParallelWalker（Scanner 與 Searcher 共用）
│  │  ├─ Scanning/              # IScanner, FileSystemScanner, (Windows) MftScanner
│  │  ├─ Search/                # SearchQuery, NameMatcher, FileSearcher（9A）
│  │  ├─ Rules/                 # Rule 模型, RuleEngine, GlobMatcher, PathTokens, Validator
│  │  ├─ Execution/             # Plan, IExecutor, ActionLog, Undo
│  │  ├─ Platform/              # ITrash, IProtectedPaths, 各 OS 實作
│  │  ├─ Storage/               # AppPaths（可攜模式 / 一般模式）、JSON 讀寫
│  │  └─ Models/                # ScanItem, Classification 等
│  ├─ WindowsTool.AI/              # AI Gateway、Providers、Prompts、Schema 驗證
│  │  ├─ Providers/             # CliChatClient, (使用 M.E.AI 的) OpenAI / Anthropic
│  │  ├─ Prompts/               # analyze.md, propose-rule.md（Embedded）
│  │  └─ Schemas/               # analyze.output.schema.json, rule.schema.json
│  ├─ WindowsTool.App/             # ASP.NET Core 主程式（Minimal API + 靜態 UI）
│  │  ├─ Endpoints/
│  │  ├─ wwwroot/               # 以 EmbeddedResource 打包
│  │  └─ Program.cs
│  └─ WindowsTool.Mcp/             # (Phase 6) MCP Server 模式，可併入 App 用參數切換
├─ rules/
│  └─ builtin/                  # windows.json, linux.json, macos.json, common.json
├─ tests/
│  ├─ WindowsTool.Core.Tests/
│  ├─ WindowsTool.App.Tests/      # 本機安全防護與 API 整合測試（WebApplicationFactory）
│  └─ WindowsTool.AI.Tests/       # Phase 4 才建立（空的測試專案在新執行器下會被判定失敗）
├─ docs/
│  ├─ PLAN.md                   # 本文件
│  ├─ rule-schema.md
│  └─ ai-providers.md
├─ .github/workflows/
│  ├─ ci.yml                    # build + test（PR）
│  └─ release.yml               # tag 觸發，三平台 publish + Release
├─ .vscode/launch.json
├─ Directory.Build.props        # 共用設定（Nullable, TreatWarningsAsErrors, LangVersion）
├─ Directory.Packages.props     # 集中管理 NuGet 套件版本
├─ global.json                  # 固定 SDK 版本、指定測試執行器
├─ WindowsTool.sln              # 放在 repo 根目錄
├─ README.md
└─ LICENSE
```

---

## 6. 資料儲存

### 6.1 位置
- **可攜模式**（預設優先）：exe 同層有 `WindowsTool.data/` 資料夾，或有 `portable` 標記檔時，所有資料都放在那裡，方便放進隨身碟帶著走。
- **一般模式**：Windows `%APPDATA%\WindowsTool\`；Linux `~/.config/windowstool/`；macOS `~/Library/Application Support/WindowsTool/`。

### 6.2 檔案

| 檔案 | 內容 | 可編輯 |
|---|---|---|
| `rules/builtin/*.json` | 內建規則（打包在 exe 內，唯讀）| 否 |
| `rules.user.json` | 使用者規則與 AI 沉澱的規則 | 是 |
| `settings.json` | 一般設定、掃描預設條件、隔離區路徑 | 是 |
| `ai.providers.json` | AI Provider 設定（**不存 API key 明文**，只存環境變數名稱）| 是 |
| `logs/actions-YYYYMM.jsonl` | 操作紀錄，一行一筆 | 否（只附加）|
| `quarantine/` | 隔離區，含 `manifest.jsonl` 供還原用 | — |
| `scans/` | （選用）掃描結果快取 | — |

---

## 7. 核心資料模型

### 7.1 ScanItem

```jsonc
{
  "id": "a1b2…",                // 路徑 hash
  "path": "C:/Users/kevin/AppData/Local/Temp",
  "name": "Temp",
  "kind": "dir",                // dir | file | link
  "size": 5368709120,           // 自身 + 子孫的邏輯大小
  "sizeOnDisk": 5100000000,     // 選用，Windows 可取得
  "fileCount": 18342,
  "dirCount": 912,
  "lastWriteUtc": "2026-10-20T03:12:00Z",
  "lastAccessUtc": null,        // 很多系統會停用存取時間，僅供參考
  "extStats": [ { "ext": ".tmp", "count": 9000, "size": 3000000000 } ],  // 只保留前 N 項
  "flags": ["reparsePoint", "accessDenied", "cloudPlaceholder", "hidden", "system"],
  "children": null              // 延遲載入；API 依 parentId 查詢
}
```

### 7.2 Classification（規則或 AI 標註的結果）

```jsonc
{
  "itemId": "a1b2…",
  "category": "暫存檔",
  "risk": "low",                // protected | high | medium | low | unknown
  "suggestedAction": "recycle", // keep | recycle | quarantine | delete | command | review
  "method": null,               // action = command 時才填 commandId
  "reason": "使用者暫存資料夾，關閉應用程式後可安全清除",
  "source": "builtin",          // builtin | user | ai | none
  "ruleId": "win-temp-user",
  "confidence": 1.0,            // 規則 = 1.0；AI 依回傳值
  "autoSelect": true
}
```

### 7.3 Plan / ActionLog

```jsonc
// Plan
{
  "planId": "…",
  "createdUtc": "…",
  "items": [
    { "path": "…", "action": "recycle", "ruleId": "win-temp-user", "expectedSize": 123 }
  ],
  "dryRun": { "ok": 41, "blocked": 2, "warnings": ["…"] }
}

// ActionLog（jsonl 每行一筆）
{ "ts": "…", "planId": "…", "path": "…", "action": "quarantine",
  "result": "ok|failed|skipped", "error": null, "bytes": 123,
  "undo": { "type": "move", "from": "…/quarantine/…", "to": "原路徑" } }
```

---

## 8. 規則引擎

### 8.1 Rule Schema

```jsonc
{
  "id": "user-0012",
  "name": "閒置的 node_modules",
  "source": "ai",               // builtin | user | ai
  "enabled": true,
  "kind": "classify",           // classify | protect
  "platforms": ["windows", "linux", "macos"],
  "scope": { "type": "global" },  // global | location(root=...) ；once 不落地
  "match": {
    "kind": "dir",              // dir | file | any
    "pathGlob": ["**/node_modules"],
    "nameRegex": null,
    "ext": null,
    "minSize": 104857600,
    "minAgeDays": 60,
    "siblingExists": ["package.json"],
    "childExists": null,
    "excludeGlob": ["**/.git/**"]
  },
  "result": {
    "category": "可重建的相依套件",
    "risk": "low",
    "suggestedAction": "recycle",
    "method": null,
    "reason": "可以用 npm install 重建；60 天沒有異動的專案",
    "autoSelect": false
  },
  "meta": {
    "createdUtc": "2026-10-24T00:00:00Z",
    "approvedBy": "user",
    "origin": { "fromItemPath": "D:/proj/foo/node_modules", "provider": "claude-cli" },
    "hitCount": 0,
    "lastHitUtc": null
  }
}
```

### 8.2 比對語意
- `match` 內各條件之間是 **AND**；陣列型欄位內部是 **OR**。
- 路徑先正規化：分隔符統一成 `/`，Windows 不分大小寫，Linux / macOS 分大小寫。
- **Path Token**（跨平台展開）：`{UserProfile}`、`{LocalAppData}`、`{RoamingAppData}`、`{Temp}`、`{ProgramData}`、`{Windows}`、`{ProgramFiles}`。也支援 `%VAR%` / `$VAR` 形式的環境變數。
- Glob 支援 `*`、`**`、`?`。可以用 `Microsoft.Extensions.FileSystemGlobbing`，或自己實作比對器，但必須有完整的單元測試。

### 8.3 優先序
1. `kind = protect` 的規則（內建保護 + 使用者自訂保護）：命中就是 `risk = protected`，**不可勾選、不可執行**。
2. 使用者規則（`source = user`）。
3. AI 沉澱規則（`source = ai`）。
4. 內建規則（`source = builtin`）。
5. 同一層級多條命中時，取**最具體**的：以條件數加上 glob 長度計分，分數相同取最新建立的。
- UI 要能顯示「命中的所有規則」以及「最後生效的是哪一條」。

### 8.4 規則寫入驗證（Validator）
新增或修改任何規則（特別是 AI 提案）都要經過以下檢查：
1. 符合 JSON Schema。
2. `pathGlob` 不能是過寬的樣式，例如 `**`、`**/*`、`C:/**`、`/`。
3. 對**目前的掃描結果**做 dry-run，回傳命中數量、總大小和樣本（前 20 筆）。
4. 命中任何保護路徑就**擋下**，並列出衝突的項目。
5. `suggestedAction = command` 的規則只能引用**白名單 commandId**（見 10.3），不能夾帶任意命令字串。
6. AI 提案的 `scope` 預設是 `location`，要改成 `global` 必須由使用者明確選擇。

---

## 9. 掃描器

### 9.1 功能
- 輸入：根路徑（任意可存取的位置）、條件（最小大小、副檔名、修改天數、要不要包含隱藏檔或系統檔、深度上限、排除 glob）。
- 輸出：`ScanResult`（樹狀彙總 + 篩選後的清單）。
- 用 SSE（`/api/scans/{id}/events`）回報進度（目前路徑、已掃數量、已掃大小），而且可以取消。

### 9.2 實作要點
- 用 `Directory.EnumerateFileSystemEntries` 或 `FileSystemEnumerable<T>` 做非遞迴的 work-queue；目錄層級平行化，並設定上限（`MaxDegreeOfParallelism`）。
- **Reparse point（Junction / Symlink）預設不跟進**，標記成 `reparsePoint`，避免重複計算和無限遞迴。
- 拒絕存取的路徑要標記 `accessDenied` 並統計數量，UI 要顯示「N 個路徑無法讀取，大小可能被低估」。
- **記憶體控管**：目錄節點全部保留；檔案節點只保留「≥ 門檻」或「每個目錄前 N 大」的項目，其餘只計入彙總。目標是千萬級的檔案也能在合理記憶體內完成。
- Windows 選用 **MFT 快速掃描**（需要系統管理員權限、NTFS、磁碟根目錄）：Phase 7 才做，前面的 Phase 只用一般掃描。
- 特殊情況：
  - OneDrive / iCloud 雲端預留位置：標記 `cloudPlaceholder`，邏輯大小 ≠ 實際佔用。
  - Hardlink（例如 WinSxS）：大小會重複計算，相關規則的 reason 要加註說明。
  - 網路磁碟：允許掃描，但 UI 要提示速度較慢。

---

## 9A. 快速檔案搜尋（FileSearch）

### 9A.1 為什麼檔案總管慢
- 檔案總管搜尋在**有索引的位置**會查 Windows Search 索引（但很多位置不在索引範圍）；在**沒索引的位置**則逐一走訪，同時讀取屬性、縮圖、Shell 中繼資料，還會邊找邊更新 UI，因此非常慢。
- 本工具只做「列舉目錄項目 + 比對中繼資料」，不讀檔案內容、不經過 Shell，並且平行走訪，預期能快上一個數量級。

### 9A.2 功能
- **位置**：一或多個根路徑（例如同時找 `C:/` 和 `D:/`）。
- **條件**（全部為 AND）：
  - 檔名：`子字串`（預設，不分大小寫）／`萬用字元`（`*.pdf`、`報告*202?*`）／`正規表示式`（選用）。可以只比對檔名，或比對完整路徑。
  - 副檔名清單（例如 `.mp4 .mkv`）
  - 大小範圍（最小 / 最大）
  - 修改日期範圍
  - 類型：檔案 / 資料夾 / 全部
  - 是否包含隱藏檔與系統檔、深度上限、排除 glob（預設排除 `$Recycle.Bin`、`System Volume Information`）
- **結果**：邊找邊串流到 UI（SSE，約每 200ms 或每 500 筆送一批），可以隨時取消；顯示名稱、所在資料夾、大小、修改時間；可排序與二次篩選。
- **結果上限**：預設 10,000 筆（可設定），超過就停止並提示「結果過多，請縮小條件」。
- **操作**（唯讀為主）：開啟檔案、**在檔案總管中顯示**（`explorer.exe /select,"<path>"`）、複製路徑、匯出 CSV。
- 搜尋頁**不提供刪除**。Phase 3 之後可以加上「加入執行計畫」，但必須走第 10 節的 Plan → dry-run → 確認流程，保護規則一樣有效。
- 最近使用的搜尋條件可以儲存（`settings.json` 的 `recentSearches`）。

### 9A.3 實作要點
- 用 `FileSystemEnumerable<T>` 自訂 `ShouldIncludePredicate` / `ShouldRecursePredicate`，直接在 `FileSystemEntry`（`ReadOnlySpan<char>` 檔名）上比對，**不符合的項目不配置字串**，降低 GC 壓力。
- `EnumerationOptions`：`IgnoreInaccessible = true`、`AttributesToSkip` 依「隱藏 / 系統」選項設定、不跟進 reparse point。
- 和 Scanner 共用 `ParallelWalker`：目錄層級平行化（`MaxDegreeOfParallelism` 預設等於 CPU 核心數，網路磁碟降為 2）。
- 萬用字元轉成預先編譯的比對器（不要每筆都建 Regex）；正規表示式使用 `RegexOptions.Compiled | CultureInvariant`，並設比對逾時。
- **Phase 7 加速**：系統管理員 + NTFS 時，改讀 MFT，整顆磁碟的檔名搜尋可以在數秒內完成（類似 Everything）；選用 USN Journal 增量更新索引。一般模式永遠可用，作為 fallback。

### 9A.4 API

```
POST   /api/search                       # { roots[], query } → { searchId }
GET    /api/search/{id}/events           # SSE：結果批次 + 進度（已走訪目錄數、目前路徑）+ 完成 / 截斷
DELETE /api/search/{id}                  # 取消
POST   /api/search/{id}/export           # 匯出 CSV
POST   /api/shell/reveal                 # { path } → 在檔案總管中顯示（只接受搜尋或掃描結果中出現過的路徑）
POST   /api/shell/open                   # { path } → 用預設程式開啟（同上限制）
```

---

## 10. 執行器

### 10.1 動作

| 動作 | 說明 | 可還原 |
|---|---|---|
| `recycle` | 移到資源回收筒 / 垃圾桶 | 是（透過 OS）|
| `quarantine` | 搬到隔離區，寫入 manifest | 是（工具內一鍵還原）|
| `move` | 搬到使用者指定的位置 | 是（紀錄原路徑）|
| `delete` | 永久刪除，**需要二次確認**，UI 要輸入確認文字 | 否 |
| `command` | 執行白名單清理命令 | 依命令而定 |

### 10.2 流程
1. 產生 Plan。
2. **Dry-run**：重新檢查存在性、權限、保護規則、是否被鎖定（盡量偵測）、目的地空間是否足夠。
3. UI 顯示預覽（筆數、總大小、被擋下的項目和原因）。
4. 使用者確認後執行，逐筆寫入 ActionLog。單筆失敗不中斷，最後彙總結果。
5. 隔離區提供「還原」和「清空 N 天前的隔離項目」。

### 10.3 白名單命令（內建，可擴充但需要使用者手動加入）

| commandId | 命令 | 平台 |
|---|---|---|
| `nuget-clear` | `dotnet nuget locals all --clear` | all |
| `npm-cache-clean` | `npm cache clean --force` | all |
| `pip-cache-purge` | `pip cache purge` | all |
| `docker-prune` | `docker system prune`（不加 `-a`，`-a` 另立一個 id）| all |
| `dism-component-cleanup` | `dism /online /cleanup-image /StartComponentCleanup` | windows（需要系統管理員權限）|
| `wsl-compact` | 說明型，只提供操作指引，不自動執行 | windows |

- 命令一律顯示完整內容，使用者確認後才執行；stdout 和 stderr 都寫入紀錄。
- AI **不能**新增白名單命令，只能引用既有的 commandId。

---

## 11. Web UI 與 API

### 11.1 本機服務安全（重要）
- 只綁定 `127.0.0.1`，使用隨機埠。
- 每次啟動產生一組 **session token**，開瀏覽器時帶在 URL fragment，前端存在記憶體裡，之後每個 API 都要帶 `Authorization` header。
- 驗證 `Host` header 只能是 `127.0.0.1:<port>` 或 `localhost:<port>`，防 DNS rebinding。
- 不開 CORS。
- 目的是**避免其他網頁或本機程式呼叫刪除 API**。
- **實作細節（Phase 0 定案）**：
  - Token 是 32 bytes 隨機值（base64url），以固定時間比較驗證；**不接受**放在 query string 的 token。
  - 前端讀出 fragment 中的 token 後，存進 `sessionStorage`（只限該分頁），並從網址列移除。這樣重新整理頁面不會失效，關掉分頁就消失；程式重啟後舊 token 自動作廢。
  - SSE 一律用 `fetch()` 串流讀取，不用 `EventSource`，因為 `EventSource` 無法帶 `Authorization` header。
  - 所有回應都加上安全標頭：`Content-Security-Policy`（只允許 `'self'`）、`X-Frame-Options: DENY`、`X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`；`/api/*` 加上 `Cache-Control: no-store`。
  - Vue 全域版在執行時編譯 template，需要在 CSP 的 `script-src` 加上 `'unsafe-eval'`；Phase 1 導入 Vue 時一併處理並記錄。

### 11.2 頁面
1. **掃描**：選擇位置（輸入路徑 + 最近使用清單 + 磁碟清單）、條件表單、進度條。
2. **結果**：左側樹狀（依大小排序）；右側表格可篩選類別、風險、來源和大小，並有風險色標、勾選、批次指定動作；項目詳情顯示 extStats、flags 和命中的規則。
3. **AI 面板**：顯示目前 Provider；「分析選取項目」按鈕；自然語言建規則的輸入框；顯示結果和規則提案。
4. **規則審核**：提案 diff、dry-run 命中數量與樣本、衝突警示、scope 選擇、接受 / 修改 / 拒絕。
5. **規則管理**：列表、啟用 / 停用、編輯（JSON 編輯器 + 表單）、匯入 / 匯出、命中統計。
6. **執行計畫**：預覽、確認、結果。
7. **紀錄與隔離區**：查詢、還原、清空。
8. **設定**：一般設定、AI Providers、隔離區路徑、可攜模式狀態。
9. **快速搜尋**：位置、條件表單、串流結果表格、在檔案總管中顯示 / 開啟 / 複製路徑 / 匯出（見 9A）。

### 11.3 API（草案）

```
GET    /api/system/info                  # OS、權限、磁碟清單、資料路徑
POST   /api/scans                        # { root, options } → { scanId }
GET    /api/scans/{id}/events            # SSE 進度
DELETE /api/scans/{id}                   # 取消
GET    /api/scans/{id}/items?parentId=&sort=&minSize=&risk=&category=&q=
GET    /api/scans/{id}/items/{itemId}

# 快速搜尋：見 9A.4（/api/search、/api/shell/*）

GET    /api/rules                        # ?source=builtin|user|ai
POST   /api/rules/validate               # { rule, scanId } → 驗證結果 + dry-run 命中
POST   /api/rules                        # 寫入（必須先通過 validate）
PUT    /api/rules/{id}
DELETE /api/rules/{id}
POST   /api/rules/import | GET /api/rules/export

POST   /api/plans                        # 產生 + dry-run
POST   /api/plans/{id}/execute
GET    /api/logs?from=&to=&planId=
POST   /api/quarantine/{entryId}/restore
POST   /api/quarantine/purge?olderThanDays=

GET    /api/ai/providers
PUT    /api/ai/providers/active
POST   /api/ai/test                      # 連線測試
POST   /api/ai/analyze                   # { scanId, itemIds[] }
POST   /api/ai/propose-rule              # { scanId, instruction, contextItemIds[] }
```

---

## 12. AI 層

### 12.1 兩條路徑（重要觀念）
- **訂閱**（ChatGPT Plus、Claude Pro/Max）**不會附帶 API 存取權**，只能透過官方 CLI 的無頭模式使用 → `cli` 類型的 Provider。
- **API / 本地模型** → `openai-compatible` 或 `anthropic` 類型的 Provider。

### 12.2 Provider 抽象
- 核心介面：`Microsoft.Extensions.AI.IChatClient`。
- 實作：
  - `CliChatClient`（自行實作 `IChatClient`）：啟動子行程，prompt 從 stdin 送入，從 stdout 解析結果，設定逾時，可以取消。
  - OpenAI 相容：用 M.E.AI 的 OpenAI 實作並設定 `baseUrl`，涵蓋 OpenAI、Ollama、LM Studio、vLLM、llama.cpp server、OpenRouter、DeepSeek、Gemini 相容端點等。
  - Anthropic：官方 C# SDK 的 `IChatClient` 實作。【實作時確認】
- 上層服務 `AiGateway` 只依賴 `IChatClient`，負責組 prompt、驗證 schema、重試和逾時。

### 12.3 設定檔 `ai.providers.json`

```jsonc
{
  "activeProvider": "claude-cli",
  "providers": {
    "claude-cli": {
      "type": "cli",
      "command": "claude",
      "args": ["-p", "--output-format", "json"],   // 【實作時確認】是否需要停用工具的參數；輸出是 envelope，要取出 result 欄位
      "resultPath": "$.result",
      "timeoutSec": 180
    },
    "codex-cli": {
      "type": "cli",
      "command": "codex",
      "args": ["exec"],                            // 【實作時確認】JSON 輸出參數
      "timeoutSec": 180
    },
    "gemini-cli": {
      "type": "cli",
      "command": "gemini",
      "args": ["-p"],                              // 【實作時確認】
      "timeoutSec": 180
    },
    "ollama": {
      "type": "openai-compatible",
      "baseUrl": "http://localhost:11434/v1",
      "model": "qwen3:14b",
      "apiKeyEnv": null
    },
    "openai": {
      "type": "openai-compatible",
      "baseUrl": "https://api.openai.com/v1",
      "model": "<model-id>",
      "apiKeyEnv": "OPENAI_API_KEY"
    },
    "anthropic": {
      "type": "anthropic",
      "model": "<model-id>",
      "apiKeyEnv": "ANTHROPIC_API_KEY"
    }
  }
}
```

- API key **只從環境變數或 OS 憑證庫**（Windows Credential Manager，選用）讀取，不寫入 JSON。
- 模型 id 不寫死在程式碼裡，一律由設定決定。
- UI 提供「連線測試」。

### 12.4 送給 AI 的內容（隱私與成本）
- 預設**只送中繼資料**：路徑、大小、檔案數和子目錄數、時間戳記、flags、extStats、前 N 大的子項目名稱和大小、最多 30 筆檔名樣本。
- 選用（設定開關，預設關閉）：讀取小型描述檔（`README*`、`package.json`、`*.csproj`、`*.sln`、`manifest.json`、`*.ini`），單檔 ≤ 16KB。
- 選用：路徑中的使用者名稱以 `{UserProfile}` 取代後再送出。
- 單次分析的項目數上限可設定（預設 20），超過就分批。

### 12.5 Prompt 與輸出 Schema
- Prompt 存在 `WindowsTool.AI/Prompts/*.md`（Embedded），所有 Provider 共用。
- 系統指示要明確寫出：只能輸出 JSON；不得建議刪除保護路徑；不確定時 `risk = unknown` 並設 `needsMoreInfo = true`；規則提案預設 `scope = location`；`command` 只能引用提供的 commandId 清單。

**analyze 輸出：**

```jsonc
{
  "items": [
    {
      "path": "…",
      "category": "Visual Studio 元件快取",
      "risk": "medium",
      "confidence": 0.8,
      "suggestedAction": "review",
      "method": null,
      "reason": "…",
      "needsMoreInfo": false
    }
  ],
  "ruleProposals": [ /* Rule（符合 8.1 schema，source 由程式填入 ai）*/ ]
}
```

**propose-rule 輸出：** `{ "ruleProposals": [Rule], "explanation": "…" }`

### 12.6 驗證與重試
1. 從回應中擷取 JSON，容許外層包有 markdown code fence。
2. 用 JSON Schema 驗證；失敗時把錯誤訊息回饋給模型重試，最多 2 次。
3. 仍然失敗就把原始回應顯示在 UI，標記「解析失敗」，不影響其他功能。
4. 規則提案一律進入 8.4 的 Validator，接著進入 UI 審核頁。

### 12.7 MCP Server 模式（Phase 6）
- 執行 `WindowsTool.exe --mcp`（stdio），對外提供這些工具：`scan`、`get_scan_items`、`get_rules`、`validate_rule`、`propose_rule`（只寫入**待審核佇列**）、`create_plan`（只產生 Plan，不執行）。
- **不提供 execute 工具**。執行一律回到 UI，由使用者確認。
- 用途：使用者可以用已經訂閱的 AI 客戶端（Claude Desktop / Claude Code / VS Code 等）直接驅動本工具。

---

## 13. 保護規則（內建，`kind = protect`）

**Windows（起始清單）：**
- `{Windows}/**`，但允許 `{Windows}/Temp/**`、`{Windows}/SoftwareDistribution/Download/**` 這類有明確內建分類規則的子路徑，例外清單要明確列出
- `{Windows}/WinSxS/**`、`{Windows}/Installer/**`（只能用 DISM 或不處理）
- `{ProgramFiles}/**`、`{ProgramFiles(x86)}/**`（只提示，不提供刪除）
- `C:/pagefile.sys`、`C:/hiberfil.sys`、`C:/swapfile.sys`（改用系統設定處理）
- `C:/System Volume Information/**`、`C:/$Recycle.Bin/**`、`C:/Recovery/**`
- `{UserProfile}/Documents|Desktop|Pictures|Videos|Music/**`：不保護，但 `risk` 至少是 `high`，並且 `autoSelect = false`
- 所有 `.git` 目錄：至少 `high`

**Linux / macOS：** `/bin`、`/sbin`、`/usr`、`/etc`、`/boot`、`/lib*`、`/System`、`/Library`（macOS 系統層）、`/proc`、`/sys`、`/dev`。

使用者可以新增保護規則，但**不能停用內建保護規則**。如果確實有需要，可以在設定中開啟「進階模式」，加上警示並寫入紀錄。

---

## 14. 內建分類規則（起始清單，Windows）

| id | 樣式 | 類別 | 風險 | 建議 |
|---|---|---|---|---|
| win-temp-user | `{LocalAppData}/Temp/**` | 暫存檔 | low | recycle（autoSelect，條件 minAgeDays ≥ 2）|
| win-temp-sys | `{Windows}/Temp/**` | 系統暫存 | low | recycle（需要系統管理員權限）|
| win-wu-download | `{Windows}/SoftwareDistribution/Download/**` | Windows Update 下載快取 | medium | review |
| win-crashdumps | `{LocalAppData}/CrashDumps/**`、`{Windows}/Minidump/**`、`*.dmp` | 當機傾印 | low | recycle |
| dev-node-modules | `**/node_modules` + sibling `package.json` | 可重建相依 | low | recycle |
| dev-dotnet-binobj | `**/bin`、`**/obj` + sibling `*.csproj` | 建置輸出 | low | recycle |
| dev-vs-cache | `**/.vs` + sibling `*.sln` | VS 暫存 | low | recycle |
| dev-nuget | `{UserProfile}/.nuget/packages` | NuGet 快取 | low | command:nuget-clear |
| dev-npm-cache | `{LocalAppData}/npm-cache` | npm 快取 | low | command:npm-cache-clean |
| dev-pip-cache | `{LocalAppData}/pip/Cache` | pip 快取 | low | command:pip-cache-purge |
| browser-cache | Chrome/Edge `*/User Data/*/Cache`、`Code Cache`、`GPUCache` | 瀏覽器快取 | low | recycle（瀏覽器要先關閉）|
| docker-vhdx | `**/docker_data.vhdx`、`**/ext4.vhdx` | Docker / WSL 磁碟映像 | high | review（command:docker-prune 或壓縮指引）|
| win-installer-cache | `{ProgramData}/Package Cache/**` | 安裝程式快取 | high | keep（刪除會導致無法修復或解除安裝）|
| downloads-old | `{UserProfile}/Downloads/**`、minAgeDays ≥ 180 | 舊下載檔 | medium | review |
| log-old | `**/*.log`、minAgeDays ≥ 90、minSize ≥ 10MB | 舊記錄檔 | medium | review |

> 路徑細節與實際位置，在 Phase 2 時要在真實的 Windows 機器上逐條驗證。

---

## 15. 平台差異抽象

| 介面 | Windows | Linux | macOS |
|---|---|---|---|
| `ITrash` | `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/DeleteDirectory`（`RecycleOption.SendToRecycleBin`），或 `SHFileOperation` / `IFileOperation` P/Invoke【實作時確認在 .NET 10 下的行為】| freedesktop Trash spec（`~/.local/share/Trash`）自行實作，或呼叫 `gio trash` | `NSFileManager trashItem`（透過 `osascript` 或 P/Invoke），或搬到 `~/.Trash` |
| `IProtectedPaths` | 13 節的 Windows 清單 | Linux 清單 | macOS 清單 |
| `IPathTokens` | `Environment.GetFolderPath` | XDG 對應 | macOS 對應 |
| `IPrivilege` | 判斷是否為系統管理員；提供「以系統管理員重新啟動」 | `geteuid() == 0` | 同 Linux |
| `MftScanner` | 有（Phase 7）| 無 | 無 |

---

## 16. 打包、發佈與開發除錯

### 16.1 發佈指令

```bash
# Windows x64 單一 exe（self-contained，免安裝 runtime）
dotnet publish src/WindowsTool.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish/win-x64

# Linux x64 / macOS arm64 同上，改 -r linux-x64 / osx-arm64
```

- 預計大小約 70–100MB；開 `-p:PublishTrimmed=true` 可以降到 30–40MB，但**必須**先完成 JSON source generator 並跑完整的 E2E 測試再開。
- Native AOT 不列入第一版。
- Windows exe 要附上 `app.manifest`：`asInvoker`，需要權限時由 UI 提供「以系統管理員重新啟動」。
- macOS 的簽章 / 公證（notarization）列為待決事項。沒有簽章的話，使用者要手動允許執行。

### 16.2 GitHub Actions
- `ci.yml`：PR 和 push 時在 `windows-latest` 和 `ubuntu-latest` 上 build + test。
- `release.yml`：推 `v*` tag 時，以矩陣產出 `win-x64`、`linux-x64`、`osx-arm64`，打包成 zip（含 README、LICENSE），建立 GitHub Release 並上傳。
- 一般平台可以從 ubuntu runner 交叉發佈；只有 Windows 專屬測試要在 windows runner 上跑。

### 16.3 本機開發與除錯
- `WindowsTool.App/Properties/launchSettings.json`：`ASPNETCORE_ENVIRONMENT=Development`、`WindowsTool__Port=5179`。開發模式下 session token 固定為 `dev`（可用 `WindowsTool__DevToken` 改），並從磁碟讀取 wwwroot（改完重新整理即生效）；正式環境一律使用隨機 token 與隨機埠，並讀取 exe 內嵌的檔案。
- 命令列參數：`--no-browser`（不自動開瀏覽器）、`--port <n>`（指定埠號）。
- `.vscode/launch.json` 和 `tasks.json`：F5 啟動並自動開瀏覽器。
- 測試資料：`tests/fixtures/` 提供腳本，產生假的目錄樹（含 node_modules、bin/obj、symlink、無權限目錄），**不要在真實的 C 槽上跑破壞性測試**。

---

## 17. 分期計畫與驗收條件

### Phase 0｜專案骨架（0.5–1 天）
- 建立 solution / 專案、`Directory.Build.props`、`.gitignore`、`.editorconfig`、README、LICENSE（建議 MIT）。
- App 可以啟動，自動開瀏覽器，顯示 Hello 頁面；session token 和 Host 驗證生效。
- CI（build + test）綠燈。
- ✅ 驗收：`dotnet run` 能開頁；`dotnet publish -r win-x64` 產出的單一 exe 可以在**沒有安裝 .NET** 的 Windows 上執行。
- **進度（2026-10-24）**：已完成，分支 `phase-0-scaffold`。單一 exe 約 48MB；在 PATH 中沒有 dotnet、`DOTNET_ROOT` 指向不存在路徑的環境下可正常執行。「全新、沒裝過 .NET 的機器」實測需另外在乾淨的 VM 上確認（CI 會產出 exe 供下載）。

### Phase 1｜掃描 + 唯讀 UI（2–3 天）
- Scanner（一般模式）、進度 SSE、取消功能、記憶體控管。
- 結果頁的樹狀和表格、排序、篩選、項目詳情。
- ✅ 驗收：掃描一個含 10 萬個以上檔案的資料夾能正確彙總大小（和 OS 回報的誤差在合理範圍內）；symlink 不重複計算；無權限路徑有標記並計數；可以中途取消。

### Phase 1.5｜快速檔案搜尋（1–2 天）
- `SearchQuery`、`NameMatcher`（子字串 / 萬用字元 / 正規表示式）、`FileSearcher`（使用 Phase 1 的 `ParallelWalker`）。
- 搜尋頁：條件表單、SSE 串流結果、取消、排序、在檔案總管中顯示、開啟、複製路徑、匯出 CSV。
- ✅ 驗收：
  - 在 fixture 目錄上，各種條件組合的結果與參考答案（測試中用 `Directory.EnumerateFileSystemEntries` 暴力比對）**完全一致**。
  - 在含 10 萬個以上檔案的真實資料夾上，第一批結果在 1 秒內出現；完整走訪時間不比 `cmd /c dir /s /b` 慢（實測數據記錄在 PR）。
  - symlink / junction 不跟進；無權限路徑略過並計數；可以中途取消；超過結果上限會截斷並提示。
  - `shell/reveal`、`shell/open` 只接受結果中出現過的路徑。

### Phase 2｜規則引擎 + 內建規則（2–3 天）
- Rule 模型、GlobMatcher、PathTokens、優先序、保護規則、Validator。
- 內建的 Windows 規則（第 13、14 節）並在真實機器上驗證路徑。
- 規則管理頁（列表、啟用 / 停用、手動新增和編輯）。
- ✅ 驗收：規則引擎的單元測試涵蓋率 ≥ 85%；保護路徑**一律不可勾選**；過寬 glob 會被擋下；dry-run 命中數量正確。

### Phase 3｜執行器（2–3 天）
- `ITrash`（先完成 Windows）、隔離區 + manifest、move、delete 二次確認、白名單命令。
- Plan、dry-run 預覽、執行結果、ActionLog、還原。
- ✅ 驗收：在 fixture 目錄完成「recycle → 從資源回收筒手動還原」「quarantine → 工具內還原」；檔案被鎖定時，單筆失敗不中斷；每次執行都有完整紀錄。
- **此時第一個可用版本完成（v0.1，不含 AI）**

### Phase 4｜AI 分析（2–3 天）
- `AiGateway`、`CliChatClient`（先接 claude）、OpenAI 相容（先測 Ollama）。
- Provider 設定頁、連線測試、AI 面板、送出的中繼資料可以預覽。
- Schema 驗證 + 重試。
- ✅ 驗收：同一批項目在 claude-cli 和 ollama 都能拿到合法的 JSON 標註；沒有設定任何 Provider 時，所有預設功能不受影響；AI 逾時或失敗只顯示錯誤，不讓 UI 崩潰。

### Phase 5｜規則提案與沉澱（2 天）
- analyze 附帶的規則提案和自然語言建規則（propose-rule）。
- 規則審核頁：diff、dry-run 影響範圍、衝突、scope 選擇、接受後寫入 `rules.user.json`。
- 規則命中統計（hitCount / lastHitUtc）。
- ✅ 驗收：AI 提出的過寬或衝突規則會被擋下並說明原因；接受後重新掃描，同類項目會由規則自動標註，**不再呼叫 AI**。
- **v0.2**

### Phase 6｜MCP Server 模式（1–2 天）
- `--mcp` stdio 模式，提供 12.7 節的工具（不含執行）。
- ✅ 驗收：從 Claude Desktop 或 Claude Code 掛上之後，能完成掃描、查詢、提案；提案出現在 UI 的待審核佇列。

### Phase 7｜強化與多平台（視需要）
- Windows MFT 快速掃描（系統管理員模式），同時套用到快速搜尋（9A.3）。
- Linux / macOS 的 `ITrash` 與保護清單、release 三平台實測。
- Trimming 瘦身、Photino 原生視窗（選用）、掃描結果快取（SQLite，選用）、重複檔案偵測（選用）。
- **v1.0**

---

## 18. 測試策略
- **單元測試**：GlobMatcher、PathTokens、規則優先序、Validator、Schema 驗證、AI 回應解析（用錄好的回應當 fixture）。
- **整合測試**：用 fixture 目錄樹跑 Scanner → RuleEngine → Plan → Executor（只用 quarantine / move）。
- **AI 測試**：`FakeChatClient` 回放固定回應，CI 不連任何真實 AI。
- **Windows 專屬測試**：`[Trait("os","windows")]`，只在 windows runner 上跑。
- **手動驗收清單**：每個 Phase 結束時在真實機器上照驗收條件逐項確認，結果記錄在 PR 描述。

---

## 19. 風險與對策

| 風險 | 對策 |
|---|---|
| 誤刪重要檔案 | 保護規則最優先；預設可還原；永久刪除二次確認；dry-run；完整紀錄 |
| AI 規則過度泛化 | 預設 scope = location；Validator 擋過寬 glob；寫入前 dry-run 顯示影響範圍 |
| 本機 API 被其他程式或網頁呼叫 | 只綁 127.0.0.1、session token、Host 驗證、不開 CORS |
| 小型 / 本地模型 JSON 不穩 | Schema 驗證 + 重試；失敗不影響核心功能 |
| CLI 參數隨版本變動 | CLI 參數放設定檔而不寫死；提供連線測試；文件標註版本 |
| 掃描大量檔案時記憶體爆掉 | 檔案節點只保留門檻以上；目錄彙總；選用的 SQLite 快取 |
| Hardlink / 雲端預留檔的大小誤導 | 加 flags 標記並在 UI 說明；WinSxS 走保護規則 |
| 單檔 exe 被防毒誤判 | 先不壓縮也不 trim 做對照；必要時考慮程式碼簽章 |
| 公司網路限制 AI 連線 | AI 是選用功能；支援本地模型（Ollama / LM Studio）|

---

## 20. 待決事項
1. ~~正式專案名稱與 LICENSE~~ → **已定案（2026-10-24）**：`WindowsTool`、MIT。
2. ~~前端要用原生 JS 還是 Vue 3~~ → **已定案（2026-10-24）**：Vue 3.5.43，下載後內嵌，執行時不連外。
3. 掃描結果要不要持久化（SQLite），還是每次重新掃描。
4. macOS 要不要做簽章 / 公證。
5. Windows 要不要提供 Credential Manager 存放 API key。
6. 是否需要多語系（預設繁體中文；程式碼和規則 id 用英文）。

---

## 附錄 A：給 Claude Code 的起手指令建議

```
請閱讀 docs/PLAN.md，依「17. 分期計畫」從 Phase 0 開始執行。
規則：
- 每個 Phase 開一個 branch（phase-0-scaffold 等），完成驗收條件後再 commit 並開 PR。
- 遵守「0. 交接說明」的硬性原則。
- 標示【實作時確認】的項目，先查證（--help / 官方文件）再實作，並把查證結果補回 docs。
- 破壞性測試只能在 tests/fixtures 產生的目錄上進行。
- 每個 Phase 結束時，回報：完成項目、驗收結果、未解問題、下一步。
```

## 附錄 B：名詞對照

| 名詞 | 說明 |
|---|---|
| 規則沉澱 | AI 的判斷經使用者審核後，轉成本地規則，之後不用再呼叫 AI |
| 保護規則 | `kind = protect`，命中的項目不可被選取或執行 |
| 隔離區 | 工具自己管理的暫存刪除區，可以一鍵還原 |
| Provider | AI 供應來源：CLI（訂閱）/ OpenAI 相容 / Anthropic / 本地模型 |
| Plan | 一次要執行的操作清單，必須先經過 dry-run |
