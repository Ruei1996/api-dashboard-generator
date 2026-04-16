# Repo-Insight-Dashboard — 模組規格說明書

> 版本：1.1.0 | 更新日期：2026-04-15 | 遵循：規格驅動開發 (SDD)

---

## 1. 專案概述

**工具名稱**：`rid` (Repo Insight Dashboard)
**技術棧**：.NET 10 (C# 12), GitHub Copilot API, Mermaid.js, Vanilla JS
**輸出物**：單一自包含 HTML Dashboard（無需網路）+ JSON 元資料
**安裝方式**：`dotnet tool install` 全域安裝，命令名稱 `rid`

---

## 2. 模組架構

```
RepoInsightDashboard/
├── Program.cs                       # CLI 入口，System.CommandLine 路由
├── Commands/
│   └── AnalyzeCommand.cs            # `rid analyze <path>` 命令定義
├── Analyzers/
│   ├── IAnalyzer.cs                 # 分析器泛型介面 IAnalyzer<TResult>
│   ├── FileScanner.cs               # 遞迴掃描，整合 .gitignore 解析，符號連結防逃逸
│   ├── GitignoreParser.cs           # 解析 .gitignore 規則 (MAF Pattern Matching)
│   ├── LanguageDetector.cs          # 依副檔名統計語言比例
│   ├── DependencyAnalyzer.cs        # 解析多語言套件管理檔案
│   ├── ApiTraceAnalyzer.cs          # API 端點追蹤（Handler → Service → Repo → SQL）
│   ├── DockerAnalyzer.cs            # Dockerfile / docker-compose.yml 解析，ENV 遮罩
│   ├── SwaggerAnalyzer.cs           # OpenAPI 3.x / Swagger 2.x 解析，$ref 展開
│   ├── EnvFileAnalyzer.cs           # .env 檔案變數提取，敏感值自動遮罩
│   ├── MakefileAnalyzer.cs          # Makefile 讀取或自動生成
│   ├── TestAnalyzer.cs              # 單元/整合/驗收測試探索
│   └── CopilotSemanticAnalyzer.cs   # GitHub Copilot API 語義摘要與安全掃描
├── Models/
│   ├── DashboardData.cs             # 完整 Dashboard 資料聚合根
│   ├── ProjectInfo.cs               # 專案名稱、語言、Git 資訊
│   ├── DependencyGraph.cs           # 節點 + 邊的依賴圖模型
│   ├── ApiEndpoint.cs               # API 端點（含 ExampleJson per response）
│   ├── ApiTrace.cs                  # API 追蹤路徑（含 SQL queries）
│   ├── ContainerInfo.cs             # Container / Port 映射
│   ├── EnvVariable.cs               # 環境變數模型
│   ├── FileNode.cs                  # 檔案樹節點
│   ├── MakefileInfo.cs              # Makefile 內容與 target 清單
│   └── TestInfo.cs                  # 測試套件摘要（TestSuiteInfo / TestFile / MockInfo）
├── Generators/
│   ├── HtmlDashboardGenerator.cs    # 生成單一 HTML (Inline CSS/JS/Mermaid)
│   └── JsonMetadataGenerator.cs     # 生成 JSON 元資料
└── Services/
    ├── AnalysisOrchestrator.cs      # 協調所有分析器、三階段並行管線
    └── DotEnvLoader.cs              # 啟動時載入 .env 到環境變數
```

---

## 3. 命令列介面規格

```bash
rid analyze <repo-path> [options]

Arguments:
  <repo-path>       要分析的程式碼庫路徑（預設：當前目錄）

Options:
  --output, -o      輸出目錄（預設：~/Downloads/api-dashboard-result/）
  --copilot-token   GitHub Copilot API Token（可選，啟用 AI 語義分析）
  --theme           dark | light | auto（預設：dark）
  --no-ai           跳過 Copilot 語義分析
  --verbose, -v     顯示詳細分析日誌（預設：輸出進度點）
  --version         顯示版本號（由 System.CommandLine 內建處理）
```

> **注意**：`--theme auto` 由前端 JS 偵測系統偏好；`dark` 為後端預設值。

---

## 4. 分析器規格

### 4.1 FileScanner
- 遞迴掃描 `repo-path`，最大深度 60 層
- 讀取並套用 `.gitignore` 規則（MAF Pattern Matching）
- **強制納入**（不受 .gitignore 影響）：
  - `.env`
  - `service.swagger.json`
  - `copilot-instructions.md`（位於 `.github/`）
  - `Makefile`、`GNUmakefile`、`makefile`
- 符號連結（symlink）防逃逸：解析目標後驗證仍在 `repo-path` 內（含尾端路徑分隔符）
- 排除：`node_modules/`、`bin/`、`obj/`、`.git/`
- 輸出：`(FileNode Tree, List<FileNode> AllFiles)`

### 4.2 LanguageDetector
| 副檔名 | 語言 |
|--------|------|
| `.cs` | C# |
| `.go` | Go |
| `.java` | Java |
| `.ts` / `.tsx` | TypeScript |
| `.js` / `.jsx` | JavaScript |
| `.py` | Python |
| `.rs` | Rust |
| `.rb` | Ruby |
| `.proto` | Protocol Buffers |
| `.php` | PHP |
| `.kt` | Kotlin |

### 4.3 DependencyAnalyzer
| 檔案 | 解析項目 |
|------|---------|
| `package.json` | dependencies、devDependencies |
| `*.csproj` | PackageReference |
| `go.mod` | require |
| `pom.xml` | dependency |
| `requirements.txt` | 逐行套件 |
| `Cargo.toml` | [dependencies] |
| `Gemfile` | gem |

### 4.4 DockerAnalyzer
- 解析 `Dockerfile`：FROM、EXPOSE、CMD、ENV
  - ENV 值若符合敏感關鍵字（PASSWORD、SECRET、TOKEN、KEY、API_KEY 等）自動遮罩為 `***masked***`
- 解析 `docker-compose.yml`：services、ports、depends_on、environment
  - 使用 `YamlDotNet`，忽略未知屬性（相容 Compose v3 `deploy:` 等擴充欄位）
- 無 docker-compose 時從 Dockerfile 合成單服務 ContainerInfo
- 輸出：`(List<ContainerInfo>, DockerfileInfo?)`，Container 拓撲用於 Mermaid graph TD

### 4.5 SwaggerAnalyzer
- 支援 OpenAPI 3.x JSON/YAML 與 Swagger 2.x（透過 `Microsoft.OpenApi.Readers`）
- 提取：paths、HTTP methods、parameters（query/path/header/cookie）、responses、request body
- **`$ref` 解析**：response schema 的 `$ref` 會遞迴展開至實際 schema，並呼叫 `GenerateExampleJson()` 產生範例 JSON 實例
  - `OpenApiAny` enum 值以具體型別（`OpenApiString`、`OpenApiInteger` 等）正確轉換，不回傳 CLR 型別名
- 輸出：`List<ApiEndpoint>`，每個 `ApiResponse` 含 `SchemaJson` 與 `ExampleJson`

### 4.6 ApiTraceAnalyzer
- 追蹤每個 API 端點的執行路徑：Handler → Service → Repository → SQL
- 上限：每次分析最多 120 個端點（CPU 密集保護）
- **支援語言**：Go（grpc-gateway / gin / echo / chi）、C# / .NET、Python（FastAPI / Flask / Django）、Java / Kotlin（Spring）、TypeScript / JavaScript（NestJS / Express）、PHP（Laravel）、Ruby（Rails）
- **SQL 偵測**（以 Go + sqlc 為例）：
  1. 掃描 `.sql` 檔案，解析 `-- name: FuncName :exec/one/many` 格式
  2. 解析 sqlc 參數（`sqlc.arg(name)`、`@name`）
  3. 在 Go call-site 追蹤傳入的 struct literal，以實際字面值取代 placeholder
  4. 組合後 SQL 以 `'param_value'` 格式顯示佔位值（無 `/* */` 註解噪音）
- **ORM 偵測**：未發現 SQL 時，若偵測到 GORM / SQLAlchemy / ActiveRecord / Hibernate / Prisma / TypeORM / Sequelize / Mongoose / ent / bun / SQLBoiler，在 dashboard 顯示提示訊息
- **效能優化**：
  - `_fileLineCache`：同一檔案在管線中只讀一次，後續從記憶體回傳
  - 18 個熱路徑 Regex 宣告為 `static readonly Regex`（`RegexOptions.Compiled`）
- 輸出：`List<ApiTrace>`（含 `TraceStep[]`、`SqlQuery[]`、`SqlParameter[]`）

### 4.7 MakefileAnalyzer
- 掃描 `allFiles` 中是否存在 `Makefile`、`GNUmakefile`、`makefile`
  - **存在**：直接讀取完整內容，解析 target 名稱
  - **不存在**：依偵測到的語言/工具（Go、Node.js、Python、Rust、Java、Ruby、buf、sqlc、mockery、Docker）自動生成符合專案狀況的 Makefile 完整指令與程式碼註解
- target 名稱以 `\r\n` / `\n` 相容方式解析（跨平台）
- 輸出：`MakefileInfo`（`Exists`、`Content`、`FilePath`、`Targets`）

### 4.8 TestAnalyzer
- 依副檔名與路徑慣例識別測試框架（xUnit、Go testing、pytest、JUnit、RSpec 等）
- 分類：unit、integration、acceptance
- 提取：測試檔案路徑、套件名、測試 case 名稱（含子測試）
- 偵測 mock 檔案（mockery 生成的 `mock_*.go` 等），提取被 mock 的介面名
- 輸出：`TestSuiteInfo`（`UnitTests`、`IntegrationTests`、`AcceptanceTests`、`Mocks`）

### 4.9 CopilotSemanticAnalyzer
- 端點：`https://api.githubcopilot.com/chat/completions`
- 模型：`gpt-4o`（可透過環境變數 `COPILOT_MODEL` 覆蓋）
- 功能（三者並行呼叫，最大化吞吐量）：
  1. `GenerateProjectSummaryAsync`：生成專案摘要（250 字以內，繁體中文）；無 token 時使用本地 fallback
  2. `DetectDesignPatternsAsync`：識別設計模式（DDD / CQRS / Microservices / RESTful 等）；回應為 JSON 陣列，包含 LLM preamble 容錯解析
  3. `DetectSecurityRisksAsync`：結合 Regex 靜態掃描與 AI 深度分析，輸出 OWASP Top 10 分類風險清單
- **安全防護**：
  - 代碼內容送 Copilot API 前截斷至 2,500 字元（防止 prompt injection 擴大攻擊面）
  - 敏感 env var 值以 `***masked***` 取代後才進入 prompt
  - secret 存在判斷以 `!= "***masked***"` 為準（避免誤報）
- 無 token 時所有函式均回傳本地 fallback 值，不發出任何網路請求

---

## 5. 分析管線（AnalysisOrchestrator）

```
Phase 1 — 串行初始化
  ├─ GetGitInfo()           → Meta.ProjectName, Meta.Branch
  ├─ FileScanner.Scan()     → FileTree, AllFiles
  ├─ LanguageDetector       → Project.Languages
  └─ 讀取 copilot-instructions.md（< 512 KB）

Phase 2 — 並行分析（Task.WhenAll）
  ├─ DependencyAnalyzer     → Packages
  ├─ DockerAnalyzer         → Containers, Dockerfile
  ├─ SwaggerAnalyzer        → ApiEndpoints
  ├─ EnvFileAnalyzer        → EnvVariables
  ├─ TestAnalyzer           → Tests
  └─ MakefileAnalyzer       → Makefile
  │
  ├─ DependencyAnalyzer.BuildGraph() → DependencyGraph  （串行，依賴 Packages）
  └─ ApiTraceAnalyzer       → ApiTraces                 （串行，依賴 ApiEndpoints）

Phase 3 — 並行 AI 語義分析（Task.WhenAll）
  ├─ GenerateProjectSummaryAsync   → CopilotSummary
  ├─ DetectDesignPatternsAsync     → DesignPatterns
  └─ DetectSecurityRisksAsync      → SecurityRisks
```

---

## 6. Dashboard HTML 規格

### 6.1 頁面佈局
```
┌──────────────────────────────────────────────────────────────┐
│  NAVBAR: [Logo] [專案名] [分支] [🌙/☀️] [搜尋框]             │
├───────────────┬──────────────────────────────────────────────┤
│               │  ┌─ SECTION ───────────────────────────────┐ │
│  SIDEBAR      │  │  [▼] 標題                [摺疊/展開]     │ │
│               │  │  內容區塊                               │ │
│  📊 專案概覽  │  └─────────────────────────────────────────┘ │
│  🔤 語言分佈  │                                              │
│  📦 依賴關係  │  ┌─ SECTION ───────────────────────────────┐ │
│  🔌 API 端點  │  │  Mermaid 圖表 / 表格 / 程式碼            │ │
│  🐳 Docker    │  └─────────────────────────────────────────┘ │
│  🔗 Port 映射 │                                              │
│  🚀 啟動流程  │                                              │
│  🔑 環境變數  │                                              │
│  🗂️ 檔案樹    │                                              │
│  🛡️ 安全分析  │                                              │
│  🧪 單元測試  │                                              │
│  ⚗️ 整合測試  │                                              │
│  ⚙️ Makefile  │                                              │
│  🤖 Copilot*  │  * 僅在 .github/copilot-instructions.md 存在時顯示
└───────────────┴──────────────────────────────────────────────┘
```

### 6.2 Section 清單
| # | Section ID | 標題 | 說明 | 預設狀態 |
|---|-----------|------|------|---------|
| 1 | `overview` | 📊 專案概覽 | 統計卡片（檔案數、語言數、API 數、服務數）+ AI 摘要 | 展開 |
| 2 | `languages` | 🔤 語言分佈 | SVG 環形圖（純 CSS，無外部依賴） | 展開 |
| 3 | `dependencies` | 📦 依賴關係圖 | Mermaid graph LR + 套件表格 | 展開 |
| 4 | `api` | 🔌 API 端點 | 可篩選表格；點擊展開 Detail Panel（參數、回應、SQL Trace） | 展開 |
| 5 | `docker` | 🐳 Docker 架構 | Mermaid graph TD Container 拓撲 | 展開 |
| 6 | `ports` | 🔗 Port 映射表 | 服務 → Host Port → Container Port → 協議 | 折疊 |
| 7 | `startup` | 🚀 啟動流程 | Mermaid sequenceDiagram + Gantt（預估啟動耗時） | 折疊 |
| 8 | `env` | 🔑 環境變數 | 遮罩表格（點擊 value 欄位切換顯示） | 折疊 |
| 9 | `filetree` | 🗂️ 檔案樹 | 互動折疊樹（目錄/檔案/大小） | 折疊 |
| 10 | `security` | 🛡️ 安全分析 | OWASP Top 10 風險表格（Critical → Info 排序） | 展開 |
| 11 | `unittests` | 🧪 單元測試 | 測試檔案列表 + test case 展開 + mock 清單 | 折疊 |
| 12 | `inttests` | ⚗️ 整合/驗收測試 | 同上，分類為 integration / acceptance | 折疊 |
| 13 | `makefile` | ⚙️ Makefile 指令區 | 完整 Makefile 原文（含程式碼註解） | 折疊 |
| 14 | `copilot-instructions` | 🤖 Copilot Instructions | `.github/copilot-instructions.md` 全文（條件顯示） | 折疊 |

### 6.3 API 端點 Detail Panel

點擊端點列後展開下方子面板，包含三個頁籤：

**總覽**
- 端點基本資訊（operationId、tag、deprecated 標記）
- 請求參數表（名稱、位置、型別、必填、說明）
- 回應列表（狀態碼、說明、Content-Type）
- **Response Body Schema**：優先顯示 `exampleJson`（`$ref` 已遞迴展開的實際 JSON 範例），fallback 至 `schemaJson`（JSON Schema 格式）

**SQL 語法**
- 若有偵測到：顯示查詢名稱、來源 `.sql` 檔、參數列表、原始 SQL、**含參數佔位值 SQL**
  - 含參數佔位值格式：`'param_value'`，不含 `/* param_name */` 噪音
- 若未偵測到但有 ORM：顯示偵測到的 ORM 框架名稱與提示訊息
- 若無任何資料：顯示「未偵測到 SQL 語法」

**執行路徑**
- 顯示 TraceStep 清單（層次：Handler → Service → Repository → External）
- 每步顯示：檔案路徑、函式名、程式碼行數範圍

### 6.4 互動功能
- 全域搜尋：即時過濾所有 Section 內容
- 摺疊/展開：每個 Section 獨立控制，Header 點擊切換
- Mermaid 圖表節點點擊：彈出 Detail Modal 顯示服務詳情
- 深淺色切換：CSS 變數，初始主題由 `data.Meta.Theme` 決定（無畫面閃爍）
- Sidebar 錨點導航：點擊跳轉 + highlight active section
- Sidebar 收合鈕：`◀/▶` 切換，收合後寬度縮至最小

### 6.5 安全措施
- `Content-Security-Policy` meta tag：`default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:;`（封鎖所有外部資源）
- JSON 序列化 `StringEscapeHandling.EscapeHtml`：防止 `<`、`>`、`&`、`'` 注入
- 開啟新視窗使用 `Blob URL`（`URL.createObjectURL`）取代已棄用的 `document.write`
- Port 號碼在 HTML 輸出前透過 `escHtml()` 跳脫
- 輸出路徑 CWE-22 防護：生成 `htmlFile`、`jsonFile` 後驗證仍在 `canonicalOutput` 目錄內

### 6.6 CSS 設計 Token
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

## 7. 輸出規格

```
~/Downloads/api-dashboard-result/
├── {ProjectName}-dashboard-({BranchName}).html
└── {ProjectName}-dashboard-meta-data-({BranchName}).json
```

**範例**：
- `micro_service_donates-dashboard-(main).html`
- `micro_service_donates-dashboard-meta-data-(main).json`

> 檔名中的非法字元（`:`、`/`、`\`、`*`、`?` 等）自動替換為 `_`。

---

## 8. JSON 元資料結構

> 以下結構由 `JsonMetadataGenerator.Generate()` 輸出，只包含機器可讀的穩定欄位。
> 完整的 `DashboardData` 資料（含 FileTree、DependencyGraph 等）僅存在於 HTML 內嵌的
> `window.__RID_DATA__` JSON blob 中。

```json
{
  "meta": {
    "generatedAt": "ISO8601",
    "toolVersion": "1.0.0",
    "projectName": "string",
    "branch": "string",
    "repoPath": "string (basename only — full host path is omitted)"
  },
  "languages": [
    { "name": "string", "fileCount": 0, "percentage": 0.0, "color": "#hex" }
  ],
  "dependencies": [
    { "name": "string", "version": "string", "type": "production|dev|peer", "ecosystem": "npm|nuget|go|maven|pip|cargo|…", "sourceFile": "string" }
  ],
  "apiEndpoints": [
    {
      "method": "GET|POST|PUT|DELETE|PATCH|QUERY|MUTATION|SUBSCRIPTION|RPC|STREAM",
      "path": "string",
      "summary": "string",
      "description": "string",
      "tag": "string",
      "operationId": "string",
      "isDeprecated": false,
      "parameters": [
        { "name": "string", "location": "query|path|header|cookie", "type": "string", "required": true, "description": "string" }
      ],
      "responses": [
        { "statusCode": "string", "description": "string" }
      ]
    }
  ],
  "apiTraces": [
    {
      "method": "string",
      "path": "string",
      "handlerFile": "string",
      "handlerFunction": "string",
      "steps": [
        { "order": 0, "layer": "Handler|Service|Repository|External", "file": "string", "function": "string", "description": "string", "startLine": 0, "endLine": 0, "calledFunctions": [] }
      ],
      "sqlQueries": [
        { "name": "string", "operation": "SELECT|INSERT|UPDATE|DELETE", "rawSql": "string", "composedSql": "string", "sourceFile": "string", "functionName": "string", "parameters": [{ "name": "string", "type": "string", "placeholder": "string" }] }
      ]
    }
  ],
  "containers": [
    {
      "name": "string",
      "image": "string",
      "buildContext": "string",
      "ports": [{ "hostPort": 0, "containerPort": 0, "protocol": "tcp|udp|grpc" }],
      "envVars": [{ "key": "string", "value": "string", "masked": true }],
      "dependsOn": ["string"]
    }
  ],
  "envVariables": [
    { "key": "string", "value": "string", "masked": true, "sourceFile": "string" }
  ],
  "copilotSummary": "string",
  "designPatterns": ["微服務架構", "RESTful API"],
  "securityRisks": [
    { "level": "critical|high|warning|info", "title": "string", "description": "string", "filePath": "string | null" }
  ],
  "startupSequence": ["db", "redis", "api"],
  "dockerfile": {
    "baseImage": "string",
    "exposedPorts": [0],
    "stages": ["string"],
    "workDir": "string"
  }
}
```

---

## 9. 安全防護規格

| 防護項目 | 實作位置 | 說明 |
|---------|---------|------|
| ENV 敏感值遮罩 | `EnvFileAnalyzer`、`DockerAnalyzer` | PASSWORD / SECRET / TOKEN / KEY 等關鍵字的值替換為 `***masked***` |
| 符號連結防逃逸 | `FileScanner` | 解析後路徑必須以 `repoPath + /` 為前綴（含尾端分隔符，防 `/repo-evil` 誤判） |
| 檔案大小限制 | `ApiTraceAnalyzer`（5 MB）、`AnalysisOrchestrator`（512 KB for copilot-instructions） | 防 OOM / DoS |
| 輸出路徑驗證 | `AnalyzeCommand` | 驗證最終輸出檔路徑在 `outputDir` 內（CWE-22） |
| HTML 輸出跳脫 | `HtmlDashboardGenerator` | `HtmlEncoder.Default.Encode`（跳脫 `'`，比 `WebUtility.HtmlEncode` 更嚴格） |
| JSON 序列化跳脫 | `HtmlDashboardGenerator` | `StringEscapeHandling.EscapeHtml` |
| CSP 標頭 | `HtmlDashboardGenerator` | 阻擋所有外部資源請求（`default-src 'none'`）|
| Prompt 內容截斷 | `CopilotSemanticAnalyzer` | 送至 AI 的原始碼截斷至 2,500 字元 |
| False-positive 防護 | `CopilotSemanticAnalyzer` | Secret 判斷以 `!= "***masked***"` 為基準 |

---

## 10. 品質保證

- **建置**：`dotnet build src/RepoInsightDashboard.slnx`（0 errors, 0 warnings）
- **測試**：`dotnet test src/RepoInsightDashboard.slnx`（18/18 pass）
- **離線執行**：所有 CSS / JS / Mermaid.js 均 inline，斷網後 HTML 仍完整運作
- **字元編碼**：UTF-8，支援繁體中文及各語言識別字符
- **5 分鐘超時**：`AnalyzeCommand` 設定 `CancellationTokenSource(TimeSpan.FromMinutes(5))`，超時輸出警告並退出碼 2
