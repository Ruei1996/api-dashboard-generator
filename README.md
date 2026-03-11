# Repo Insight Dashboard (rid)

> 基於 .NET 10 的 CLI 工具，利用靜態分析與 GitHub Copilot API 語義分析任何程式碼庫，生成可互動、離線可用的 HTML Dashboard。

## 功能特色

- **多語言支援**：C#、Go、Java、TypeScript、JavaScript、Python、Rust、Ruby 等
- **依賴分析**：npm / NuGet / Go Modules / Maven / pip / Cargo
- **Docker 解析**：Dockerfile + docker-compose 架構圖、Port 映射（純 Dockerfile 也能合成服務卡片）
- **API 洞察**：自動解析 Swagger/OpenAPI，點擊端點開新分頁查看執行路徑、邏輯與 SQL
- **API Trace**：追蹤 Go API 呼叫鏈（Handler → Service → Repository → SQL）
- **Call Graph**：函式呼叫關係圖（Mermaid）
- **環境變數**：自動遮罩敏感值，排除測試 env 檔案
- **測試分析**：Unit / Integration / Acceptance 測試統計與分類
- **Copilot 語義分析**：可選，需提供 token
- **離線 HTML**：單一檔案，所有資源內聯，無需網路

## 安裝

### 方式一：全域安裝（推薦）

```bash
# 打包並安裝為全域工具
cd src/RepoInsightDashboard
dotnet pack -c Release -o /tmp/rid-nupkg
dotnet tool install -g --add-source /tmp/rid-nupkg RepoInsightDashboard
```

確認 `~/.dotnet/tools` 已在 PATH 中（若尚未加入，執行以下指令）：

```bash
# zsh 用戶
echo 'export PATH="$PATH:/Users/$USER/.dotnet/tools"' >> ~/.zprofile
source ~/.zprofile
```

驗證安裝：

```bash
rid --version
# 輸出：1.0.0+...
```

### 方式二：直接執行（開發用）

```bash
dotnet run --project src/RepoInsightDashboard -- analyze /path/to/repo
```

### 更新工具

```bash
# 重新打包後更新
cd src/RepoInsightDashboard
dotnet pack -c Release -o /tmp/rid-nupkg
dotnet tool update -g --add-source /tmp/rid-nupkg RepoInsightDashboard
```

## 使用方式

```bash
# 分析當前目錄
rid analyze .

# 分析指定路徑
rid analyze /path/to/my-project

# 啟用 AI 語義分析（需 GitHub Copilot token）
rid analyze /path/to/repo --copilot-token ghp_xxxxx

# 指定輸出目錄
rid analyze . --output ~/Desktop/reports

# 詳細模式（顯示每個分析步驟）
rid analyze . --verbose

# 淺色主題
rid analyze . --theme light
```

## 輸出檔案

預設輸出至 `~/Downloads/api-dashboard-result/`：

```
~/Downloads/api-dashboard-result/
├── {ProjectName}-dashboard-({BranchName}).html    # 互動 Dashboard（離線可用）
└── {ProjectName}-dashboard-meta-data-({BranchName}).json  # 完整元資料（可提供給 AI 閱讀）
```

## Dashboard 功能

| 區塊 | 功能 |
|------|------|
| 概覽 | 統計卡片、AI 摘要、設計模式、安全風險 |
| 語言分佈 | 圓餅圖 + 橫條圖 |
| 依賴關係圖 | Mermaid 圖表 + 套件列表 |
| 函式呼叫圖 | 層次呼叫關係（Mermaid） |
| API 端點 | 可篩選表格，點擊開新頁查看詳細資訊 |
| API 詳細頁 | 概覽 / 執行路徑（時序圖）/ 執行邏輯 / SQL 語法 |
| Docker 架構 | 拓撲圖 + 服務卡片（支援純 Dockerfile） |
| Port 映射 | 服務/Host/Container Port 對照 |
| 啟動流程 | 依賴順序排列 |
| 環境變數 | 敏感值遮罩，點擊顯示，排除測試環境檔案 |
| 單元測試 | 測試統計、分類清單、Mock 列表 |
| 整合/驗收測試 | 測試統計、分類清單 |
| 檔案樹 | 互動折疊樹狀圖 |
| 安全分析 | 風險等級分類（critical / warning / info） |

## 讓 AI 閱讀分析結果

生成的 JSON 元資料檔案包含完整的分析資訊，可直接提供給 AI（如 Claude）閱讀：

```
請閱讀這個專案分析結果，並回答我的問題：
[貼上 JSON 檔案路徑或內容]
```

## 開發

```bash
# Build
dotnet build src/

# Test
dotnet test src/

# 直接執行
dotnet run --project src/RepoInsightDashboard -- analyze .

# 重新打包並更新全域工具
cd src/RepoInsightDashboard
dotnet pack -c Release -o /tmp/rid-nupkg && dotnet tool update -g --add-source /tmp/rid-nupkg RepoInsightDashboard
```

## 專案架構

```
src/RepoInsightDashboard/
├── Analyzers/
│   ├── ApiTraceAnalyzer.cs     # 追蹤 API 執行路徑（Handler→Service→Repo→SQL）
│   ├── CallGraphAnalyzer.cs    # 函式呼叫圖分析
│   ├── DependencyAnalyzer.cs   # 依賴套件分析
│   ├── DockerAnalyzer.cs       # Docker / docker-compose 解析
│   ├── EnvFileAnalyzer.cs      # 環境變數提取
│   ├── FileScanner.cs          # 檔案掃描（支援 .gitignore）
│   ├── GitignoreParser.cs      # .gitignore 規則解析
│   ├── LanguageDetector.cs     # 程式語言識別
│   ├── SwaggerAnalyzer.cs      # OpenAPI/Swagger 解析
│   └── TestAnalyzer.cs         # 測試檔案分析
├── Generators/
│   └── HtmlDashboardGenerator.cs  # HTML Dashboard 生成
├── Models/
│   ├── ApiTrace.cs             # API 追蹤資料模型
│   ├── DashboardData.cs        # 主要資料模型
│   ├── DockerModels.cs         # Docker 相關模型
│   ├── TestInfo.cs             # 測試資訊模型
│   └── ...
├── Services/
│   ├── AnalysisOrchestrator.cs # 分析流程協調
│   └── CopilotSemanticAnalyzer.cs  # GitHub Copilot AI 分析
└── Program.cs                  # CLI 入口點
```

## 技術棧

- **.NET 10 / C#**：System.CommandLine, LibGit2Sharp, YamlDotNet, Microsoft.OpenApi.Readers, Newtonsoft.Json
- **前端**：Mermaid.js（CDN + offline fallback）、CSS Variables、Vanilla JS
- **AI**：GitHub Copilot API（OpenAI-compatible endpoint），無 token 時使用本地靜態分析
