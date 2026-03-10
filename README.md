# Repo Insight Dashboard (rid)

> 基於 .NET 10 的 CLI 工具，利用靜態分析與 GitHub Copilot API 語義分析任何程式碼庫，生成可互動、離線可用的 HTML Dashboard。

## 功能特色

- **多語言支援**：C#、Go、Java、TypeScript、JavaScript、Python、Rust、Ruby 等
- **依賴分析**：npm / NuGet / Go Modules / Maven / pip / Cargo
- **Docker 解析**：Dockerfile + docker-compose 架構圖、Port 映射
- **API 洞察**：自動解析 Swagger/OpenAPI，生成端點列表
- **Call Graph**：函式呼叫關係圖（Mermaid）
- **環境變數**：自動遮罩敏感值
- **Copilot 語義分析**：可選，需提供 token
- **離線 HTML**：單一檔案，所有資源內聯，無需網路

## 安裝

```bash
dotnet tool install --global --project src/RepoInsightDashboard
# 或直接 build
dotnet build src/RepoInsightDashboard
```

## 使用方式

```bash
# 分析當前目錄
rid analyze .

# 分析指定路徑
rid analyze /path/to/my-project

# 啟用 AI 語義分析
rid analyze /path/to/repo --copilot-token ghp_xxxxx

# 指定輸出目錄
rid analyze . --output ~/Desktop/reports

# 詳細模式
rid analyze . --verbose

# 輕色主題
rid analyze . --theme light
```

## 輸出檔案

```
~/Downloads/api-dashboard-result/
├── {ProjectName}-dashboard-({BranchName}).html    # 互動 Dashboard
└── {ProjectName}-dashboard-meta-data-({BranchName}).json  # 元資料
```

## Dashboard 功能

| 區塊 | 功能 |
|------|------|
| 概覽 | 統計卡片、AI 摘要、設計模式 |
| 語言分佈 | 圓餅圖 + 橫條圖 |
| 依賴關係圖 | Mermaid 圖表 + 套件列表 |
| 函式呼叫圖 | 節點呼叫關係（Mermaid） |
| API 端點 | 可篩選表格，點擊查看參數 |
| Docker 架構 | 拓撲圖 + 服務卡片 |
| Port 映射 | 服務/Host/Container Port 對照 |
| 啟動流程 | 時序圖 + Gantt 圖 |
| 環境變數 | 敏感值遮罩，點擊顯示 |
| 檔案樹 | 互動折疊樹狀圖 |
| 安全分析 | 風險等級分類 |

## 開發

```bash
# Build
dotnet build src/

# Test
dotnet test src/

# Run
dotnet run --project src/RepoInsightDashboard -- analyze .
```

## 技術棧

- **.NET 10 / C#**：System.CommandLine, LibGit2Sharp, YamlDotNet, Microsoft.OpenApi.Readers
- **前端**：Mermaid.js（CDN + fallback）, CSS Variables, Vanilla JS
- **AI**：GitHub Copilot API（OpenAI-compatible endpoint）
