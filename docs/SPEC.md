# Repo-Insight-Dashboard — 模組規格說明書

> 版本：1.0.0 | 更新日期：2026-03-11 | 遵循：規格驅動開發 (SDD)

---

## 1. 專案概述

**工具名稱**：`rid` (Repo Insight Dashboard)
**技術棧**：.NET 10 (C#), GitHub Copilot API, Mermaid.js, Vanilla JS
**輸出物**：單一 HTML Dashboard + JSON 元資料

---

## 2. 模組架構

```
RepoInsightDashboard/
├── Program.cs                       # CLI 入口，System.CommandLine 路由
├── Commands/
│   └── AnalyzeCommand.cs            # `rid analyze <path>` 命令定義
├── Analyzers/
│   ├── IAnalyzer.cs                 # 分析器介面
│   ├── FileScanner.cs               # 遞迴掃描，整合 .gitignore 解析
│   ├── GitignoreParser.cs           # 解析 .gitignore 規則
│   ├── LanguageDetector.cs          # 依副檔名統計語言比例
│   ├── DependencyAnalyzer.cs        # 解析 package.json/csproj/go.mod/pom.xml
│   ├── CallGraphAnalyzer.cs         # 函式呼叫鏈提取 (Regex + Roslyn)
│   ├── DockerAnalyzer.cs            # Dockerfile / docker-compose.yml 解析
│   ├── SwaggerAnalyzer.cs           # OpenAPI 3.x / Swagger 2.x 解析
│   ├── EnvFileAnalyzer.cs           # .env 檔案變數提取
│   └── CopilotSemanticAnalyzer.cs   # GitHub Copilot API 語義摘要
├── Models/
│   ├── DashboardData.cs             # 完整 Dashboard 資料聚合根
│   ├── ProjectInfo.cs               # 專案名稱、語言、Git 資訊
│   ├── DependencyGraph.cs           # 節點 + 邊的依賴圖模型
│   ├── ApiEndpoint.cs               # API 端點描述
│   ├── ContainerInfo.cs             # Container / Port 映射
│   ├── EnvVariable.cs               # 環境變數模型
│   └── FileNode.cs                  # 檔案樹節點
├── Generators/
│   ├── HtmlDashboardGenerator.cs    # 生成單一 HTML (Inline CSS/JS)
│   └── JsonMetadataGenerator.cs     # 生成 JSON 元資料
└── Services/
    └── AnalysisOrchestrator.cs      # 協調所有分析器、聚合結果
```

---

## 3. 命令列介面規格

```bash
rid analyze <repo-path> [options]

Options:
  --output, -o      輸出目錄 (預設: ~/Downloads/api-dashboard-result/)
  --copilot-token   GitHub Copilot API Token (可選，啟用語義分析)
  --theme           dark | light | auto (預設: auto)
  --no-ai           跳過 Copilot 語義分析
  --verbose, -v     顯示詳細分析日誌
  --version         顯示版本號
```

---

## 4. 分析器規格

### 4.1 FileScanner
- 遞迴掃描 `repo-path`
- 讀取並套用 `.gitignore` 規則 (使用 MAF Pattern Matching)
- **強制納入**：`.env`、`service.swagger.json`、`.github/copilot-instructions.md`
- 排除：`node_modules/`, `bin/`, `obj/`, `.git/`
- 輸出：`List<FileNode>`

### 4.2 LanguageDetector
| 副檔名 | 語言 |
|--------|------|
| .cs    | C#   |
| .go    | Go   |
| .java  | Java |
| .ts/.tsx | TypeScript |
| .js/.jsx | JavaScript |
| .py    | Python |
| .rs    | Rust |
| .rb    | Ruby |

### 4.3 DependencyAnalyzer
| 檔案 | 解析項目 |
|------|---------|
| `package.json` | dependencies, devDependencies |
| `*.csproj` | PackageReference |
| `go.mod` | require |
| `pom.xml` | dependency |
| `requirements.txt` | 逐行套件 |
| `Cargo.toml` | [dependencies] |

### 4.4 DockerAnalyzer
- 解析 `Dockerfile`：FROM、EXPOSE、CMD、ENV
- 解析 `docker-compose.yml`：services、ports、depends_on、environment
- 輸出：Container 拓撲圖資料 (Mermaid graph TD)

### 4.5 SwaggerAnalyzer
- 支援 OpenAPI 3.x JSON/YAML 與 Swagger 2.x
- 提取：paths、methods、parameters、responses、schemas
- 自動翻譯欄位描述為繁體中文（透過 Copilot API）
- 輸出：`List<ApiEndpoint>`

### 4.6 CopilotSemanticAnalyzer
- 端點：`https://api.githubcopilot.com/chat/completions`
- 模型：`gpt-4o`
- 功能：
  - 生成專案摘要（250字以內，繁體中文）
  - 識別設計模式（DDD / CQRS / Microservices 等）
  - 識別安全風險
  - 翻譯 Swagger 欄位說明

---

## 5. Dashboard HTML 規格

### 5.1 頁面佈局
```
┌─────────────────────────────────────────────────────┐
│  NAVBAR: [Logo] [專案名] [分支] [🌙/☀️] [搜尋框]    │
├──────────────┬──────────────────────────────────────┤
│              │  ┌─ SECTION ──────────────────────┐  │
│  SIDEBAR     │  │  [▼] 標題     [摺疊/展開]       │  │
│              │  │  內容區塊                        │  │
│  [▸] 概覽    │  └────────────────────────────────┘  │
│  [▸] 語言    │                                       │
│  [▸] 依賴圖  │  ┌─ SECTION ──────────────────────┐  │
│  [▸] API     │  │  Mermaid 圖表                    │  │
│  [▸] Docker  │  └────────────────────────────────┘  │
│  [▸] 環境變數│                                       │
│  [▸] 檔案樹  │                                       │
└──────────────┴──────────────────────────────────────┘
```

### 5.2 Section 清單
1. **概覽** — 專案統計卡片（檔案數、語言數、API 數、服務數）
2. **語言分佈** — 環形圖（純 Canvas/SVG）
3. **依賴關係圖** — Mermaid graph LR
4. **函式呼叫圖** — Mermaid graph TD
5. **API 端點** — 可篩選表格（Method / Path / 說明）
6. **Docker 架構** — Mermaid graph TD（Container 拓撲）
7. **Port 映射表** — 表格（服務 → 內部 Port → 外部 Port）
8. **啟動時序圖** — Mermaid sequenceDiagram
9. **啟動 Gantt** — Mermaid gantt
10. **環境變數** — 遮罩表格（Value 預設隱藏）
11. **檔案樹** — 互動式折疊樹狀圖

### 5.3 互動功能
- 全域搜尋：即時過濾所有 Section 內容
- 摺疊/展開：每個 Section 獨立控制，Header 點擊切換
- 節點點擊：Mermaid 圖表節點點擊彈出 Detail Modal
- 深淺色切換：CSS 變數 `--bg-primary` / `--text-primary` 等
- Sidebar 錨點導航：點擊跳轉 + highlight active

### 5.4 CSS 設計 Token
```css
:root {
  --bg-primary: #0d1117;
  --bg-secondary: #161b22;
  --bg-card: #21262d;
  --border-color: #30363d;
  --text-primary: #e6edf3;
  --text-secondary: #8b949e;
  --accent-blue: #58a6ff;
  --accent-green: #3fb950;
  --accent-orange: #d29922;
  --accent-red: #f85149;
  --accent-purple: #bc8cff;
}
[data-theme="light"] {
  --bg-primary: #ffffff;
  --bg-secondary: #f6f8fa;
  --bg-card: #ffffff;
  --border-color: #d0d7de;
  --text-primary: #1f2328;
  --text-secondary: #656d76;
}
```

---

## 6. 輸出規格

```
~/Downloads/api-dashboard-result/
├── {ProjectName}-dashboard-({BranchName}).html
└── {ProjectName}-dashboard-meta-data-({BranchName}).json
```

**範例**：
- `MyApp-dashboard-(main).html`
- `MyApp-dashboard-meta-data-(main).json`

---

## 7. JSON 元資料結構

```json
{
  "meta": {
    "generatedAt": "ISO8601",
    "toolVersion": "1.0.0",
    "projectName": "string",
    "branch": "string",
    "repoPath": "string"
  },
  "languages": [{ "name": "string", "fileCount": 0, "percentage": 0.0 }],
  "dependencies": [{ "name": "string", "version": "string", "type": "string" }],
  "apiEndpoints": [{ "method": "string", "path": "string", "summary": "string", "parameters": [] }],
  "containers": [{ "name": "string", "image": "string", "ports": [], "envVars": [], "dependsOn": [] }],
  "callGraph": { "nodes": [], "edges": [] },
  "envVariables": [{ "key": "string", "value": "string", "masked": true }],
  "copilotSummary": "string",
  "designPatterns": [],
  "securityRisks": []
}
```

---

## 8. 品質保證

- HTML 輸出必須通過 W3C 語義結構驗證
- 所有 Mermaid 圖表使用 `mermaid.initialize({ startOnLoad: true })`
- 主控台無 `console.error` 輸出
- 離線測試：斷網後開啟 HTML 仍完整運作
- 字元編碼：UTF-8（支援中文）
