# Repo Insight Dashboard (rid)

> 基於 .NET 10 的 CLI 工具，利用靜態分析與 GitHub Copilot API 語義分析任何程式碼庫，生成可互動、離線可用的 HTML Dashboard。

## 功能特色

- **多語言支援**：C#、Go、Java、TypeScript、JavaScript、Python、Rust、Ruby、PHP 等
- **依賴分析**：npm / NuGet / Go Modules / Maven / Gradle / pip / Poetry / Cargo / Composer / Pub / Mix / Swift PM / Rubygems
- **Docker 解析**：Dockerfile + docker-compose 架構圖、Port 映射（純 Dockerfile 也能合成服務卡片）
- **API 洞察**：自動解析 Swagger/OpenAPI 3.x / GraphQL schema / gRPC proto，點擊端點查看參數、回應 Schema 與 SQL Trace
- **API Trace**：追蹤多語言 API 呼叫鏈（Handler → Service → Repository → SQL），支援 Go / C# / Python / Java / TypeScript / PHP / Ruby
- **環境變數**：自動遮罩敏感值（password / token / secret / key 等），排除測試 env 檔案
- **Makefile**：讀取現有 Makefile 或依偵測到的語言/工具自動生成
- **測試分析**：Unit / Integration / Acceptance 測試統計與分類，Mock 檔案清單
- **安全分析**：七項 OWASP Top 10 靜態 Regex 掃描 + 可選 Copilot AI 深度審查
- **Copilot 語義分析**：可選，需提供 `GITHUB_COPILOT_TOKEN`；無 token 時使用本地 fallback
- **離線 HTML**：單一檔案，所有 CSS / JS 內聯，無需網路

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
| 概覽 | 統計卡片（檔案數、語言數、API 端點數、容器數）、AI 摘要、設計模式 |
| 語言分佈 | SVG 環形圖 + 橫條圖（GitHub Linguist 色系） |
| 依賴關係圖 | RidGraph SVG 依賴圖 + 套件列表（含 ecosystem badge） |
| API 端點 | 可篩選表格；點擊展開 Detail Panel（總覽 / SQL 語法 / 執行路徑三個頁籤） |
| Docker 架構 | 拓撲圖 + 服務卡片（支援純 Dockerfile 合成） |
| Port 映射 | 服務 / Host Port / Container Port / 協議對照表 |
| 啟動流程 | 依 depends_on 拓撲排序的啟動順序 |
| 環境變數 | 敏感值遮罩，點擊 value 欄位顯示/隱藏，排除測試環境檔案 |
| 單元測試 | 測試統計、分類清單、Mock 列表 |
| 整合/驗收測試 | 測試統計、分類清單 |
| 檔案樹 | 互動折疊樹狀圖（含語言標籤與檔案大小） |
| 安全分析 | OWASP Top 10 風險等級分類（critical → info 排序） |
| Makefile | 完整 Makefile 原文或自動生成的指令集（含 target 清單） |
| Copilot Instructions | `.github/copilot-instructions.md` 全文（僅在檔案存在時顯示） |

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
│   ├── IAnalyzer.cs            # 分析器泛型介面 IAnalyzer<TResult>
│   ├── ApiTraceAnalyzer.cs     # 追蹤 API 執行路徑（Handler→Service→Repo→SQL）
│   ├── CopilotSemanticAnalyzer.cs  # GitHub Copilot AI 語義摘要、設計模式與安全分析
│   ├── DependencyAnalyzer.cs   # 多生態系套件管理檔案解析
│   ├── DockerAnalyzer.cs       # Dockerfile / docker-compose 解析，ENV 遮罩
│   ├── EnvFileAnalyzer.cs      # .env 環境變數提取，敏感值自動遮罩
│   ├── FileScanner.cs          # 遞迴掃描，整合 .gitignore，符號連結防逃逸
│   ├── GitignoreParser.cs      # .gitignore 規則解析（MAF Pattern Matching）
│   ├── LanguageDetector.cs     # 程式語言識別（GitHub Linguist 色系）
│   ├── MakefileAnalyzer.cs     # Makefile 讀取或依工具/語言自動生成
│   ├── SwaggerAnalyzer.cs      # OpenAPI / GraphQL / gRPC proto 解析
│   └── TestAnalyzer.cs         # Unit / Integration / Acceptance 測試探索
├── Generators/
│   ├── HtmlDashboardGenerator.cs   # 生成單一自包含 HTML Dashboard
│   └── JsonMetadataGenerator.cs    # 生成 JSON 元資料（供 CI / AI 工具消費）
├── Models/
│   ├── ApiEndpoint.cs          # API 端點（含參數、回應、request body）
│   ├── ApiTrace.cs             # API 追蹤路徑（TraceStep、SqlQuery、SqlParameter）
│   ├── ContainerInfo.cs        # 容器服務、Port 映射、Dockerfile 解析結果
│   ├── DashboardData.cs        # 完整 Dashboard 資料聚合根（含 MetaInfo、SecurityRisk）
│   ├── DependencyGraph.cs      # 依賴圖節點與邊（PackageDependency）
│   ├── EnvVariable.cs          # 環境變數模型（含 IsSensitive 旗標）
│   ├── FileNode.cs             # 檔案樹節點（IsDirectory / Children / Language）
│   ├── MakefileInfo.cs         # Makefile 內容與 target 清單
│   ├── ProjectInfo.cs          # 專案名稱、語言統計、Git 資訊（LanguageInfo）
│   └── TestInfo.cs             # 測試套件摘要（TestSuiteInfo / TestFile / MockInfo）
├── Services/
│   ├── AnalysisOrchestrator.cs # 三階段並行分析管線協調器
│   └── DotEnvLoader.cs         # 啟動時從 .env 載入允許的環境變數（CWE-426 防護）
└── Program.cs                  # CLI 入口點（System.CommandLine）
```

## 技術棧

- **.NET 10 / C#**：System.CommandLine, LibGit2Sharp, YamlDotNet, Microsoft.OpenApi.Readers, Newtonsoft.Json
- **前端**：Mermaid.js（CDN + offline fallback）、CSS Variables、Vanilla JS
- **AI**：GitHub Copilot API（OpenAI-compatible endpoint），無 token 時使用本地靜態分析
