# WindowsTool

磁碟空間分析與清理工具，附帶快速檔案搜尋。

- **掃描**：找出任何位置下的大型資料夾與檔案。
- **規則**：內建常見規則，自動標註類別、風險與建議做法；可以自訂規則。
- **清理**：刪除預設進資源回收筒或隔離區，可以還原；保護路徑永遠不能被選取。
- **快速搜尋**：依檔名、副檔名、大小、日期，在指定位置快速找檔案，比檔案總管的搜尋快很多。
- **AI（選用）**：使用者主動要求時，才請 AI 分析未知項目或建立規則；AI 不會直接操作任何檔案。

完全離線可用。打包成單一執行檔，不需要安裝 .NET。

> 目前進度：**Phase 0（專案骨架）**。完整計畫見 [docs/PLAN.md](docs/PLAN.md)。

## 使用方式

執行 `WindowsTool.exe`，程式會在本機啟動服務，並自動用預設瀏覽器開啟操作畫面。關閉主控台視窗即可結束程式。

| 參數 | 說明 |
|---|---|
| `--no-browser` | 不自動開瀏覽器（網址會顯示在主控台上） |
| `--port <n>` | 指定埠號（預設隨機） |

### 安全性

- 服務只監聽 `127.0.0.1`，外部電腦連不進來。
- 每次啟動都會產生新的連線金鑰（session token），沒有金鑰的程式或網頁無法呼叫 API。
- 會檢查 Host header，防止 DNS rebinding 攻擊；不開放 CORS。

## 開發

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（版本見 `global.json`）。

```bash
# 啟動（Development：固定埠 5179、token 固定為 dev、直接讀磁碟上的 wwwroot）
dotnet run --project src/WindowsTool.App

# 測試
dotnet test

# 發佈 Windows 單一執行檔
dotnet publish src/WindowsTool.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

VS Code：按 F5 啟動（`.vscode/launch.json`）。Visual Studio：直接開啟 `WindowsTool.sln`。

### 專案結構

```
src/
  WindowsTool.Core/   純邏輯：掃描、搜尋、規則、執行（不依賴 ASP.NET）
  WindowsTool.AI/     AI Gateway 與各 Provider（Phase 4）
  WindowsTool.App/    主程式：Minimal API + 內嵌的前端頁面
tests/
  WindowsTool.Core.Tests/
  WindowsTool.App.Tests/   本機安全防護與 API 的整合測試
docs/
  PLAN.md             專案執行計畫
```

## 授權

[MIT](LICENSE)
