using System.Text;
using System.Text.Encodings.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RepoInsightDashboard.Analyzers;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Generators;

public class HtmlDashboardGenerator
{
    // camelCase for onclick data — JS expects ep.method, ep.path, c.name, etc.
    private static readonly JsonSerializerSettings _onclickJson = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };
    public string Generate(DashboardData data)
    {
        var sb = new StringBuilder();
        var jsonData = JsonConvert.SerializeObject(data, Formatting.None,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-TW\" data-theme=\"dark\">");
        sb.AppendLine("<head>");
        sb.AppendLine($"<meta charset=\"UTF-8\">");
        sb.AppendLine($"<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>{HtmlEncode(data.Project.Name)} — Repo Insight Dashboard</title>");
        sb.AppendLine(GetInlineStyles(data.Meta.Theme));
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine(BuildNavbar(data));
        sb.AppendLine("<div class=\"layout\">");
        sb.AppendLine(BuildSidebar());
        sb.AppendLine("<main class=\"main-content\" id=\"main-content\">");
        sb.AppendLine(BuildOverviewSection(data));
        sb.AppendLine(BuildLanguageSection(data));
        sb.AppendLine(BuildDependencySection(data));
        sb.AppendLine(BuildCallGraphSection(data));
        sb.AppendLine(BuildApiSection(data));
        sb.AppendLine(BuildDockerSection(data));
        sb.AppendLine(BuildPortTable(data));
        sb.AppendLine(BuildStartupSection(data));
        sb.AppendLine(BuildEnvSection(data));
        sb.AppendLine(BuildFileTreeSection(data));
        sb.AppendLine(BuildSecuritySection(data));
        sb.AppendLine(BuildUnitTestSection(data));
        sb.AppendLine(BuildIntegrationTestSection(data));
        if (!string.IsNullOrEmpty(data.Project.CopilotInstructions))
            sb.AppendLine(BuildCopilotInstructionsSection(data));
        sb.AppendLine("</main>");
        sb.AppendLine("</div>");
        sb.AppendLine(BuildDetailModal());
        sb.AppendLine($"<script>window.__RID_DATA__ = {jsonData};</script>");
        sb.AppendLine(GetInlineScripts());
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private string BuildNavbar(DashboardData data) => $"""
        <nav class="navbar" role="navigation">
          <div class="navbar-brand">
            <span class="navbar-logo">⬡</span>
            <span class="navbar-title">{HtmlEncode(data.Project.Name)}</span>
            <span class="navbar-badge">{HtmlEncode(data.Project.Branch)}</span>
          </div>
          <div class="navbar-search">
            <input type="search" id="global-search" placeholder="搜尋 API、服務、檔案..." autocomplete="off" />
            <span class="search-icon">⌕</span>
          </div>
          <div class="navbar-actions">
            <span class="navbar-stat" title="Total Files">📁 {data.Project.TotalFiles}</span>
            <span class="navbar-stat" title="API Endpoints">🔌 {data.ApiEndpoints.Count} API</span>
            <span class="navbar-stat" title="Containers">🐳 {data.Containers.Count} 服務</span>
            <button id="sidebar-toggle" class="btn-icon" title="展開/收合側欄" aria-label="Toggle Sidebar">◀</button>
            <button id="theme-toggle" class="btn-icon" title="切換主題" aria-label="切換深淺色主題">🌙</button>
            <button id="collapse-all" class="btn-icon" title="全部摺疊">⊟</button>
            <button id="expand-all" class="btn-icon" title="全部展開">⊞</button>
          </div>
        </nav>
        """;

    private string BuildSidebar() => """
        <aside class="sidebar" id="sidebar" role="complementary">
          <nav class="sidebar-nav" aria-label="區塊導航">
            <a href="#section-overview" class="nav-item active" data-section="overview">
              <span class="nav-icon">📊</span> 專案概覽
            </a>
            <a href="#section-languages" class="nav-item" data-section="languages">
              <span class="nav-icon">🔤</span> 語言分佈
            </a>
            <a href="#section-dependencies" class="nav-item" data-section="dependencies">
              <span class="nav-icon">📦</span> 依賴關係
            </a>
            <a href="#section-callgraph" class="nav-item" data-section="callgraph">
              <span class="nav-icon">🕸️</span> 呼叫圖
            </a>
            <a href="#section-api" class="nav-item" data-section="api">
              <span class="nav-icon">🔌</span> API 端點
            </a>
            <a href="#section-docker" class="nav-item" data-section="docker">
              <span class="nav-icon">🐳</span> Docker 架構
            </a>
            <a href="#section-ports" class="nav-item" data-section="ports">
              <span class="nav-icon">🔗</span> Port 映射
            </a>
            <a href="#section-startup" class="nav-item" data-section="startup">
              <span class="nav-icon">🚀</span> 啟動流程
            </a>
            <a href="#section-env" class="nav-item" data-section="env">
              <span class="nav-icon">🔑</span> 環境變數
            </a>
            <a href="#section-filetree" class="nav-item" data-section="filetree">
              <span class="nav-icon">🗂️</span> 檔案樹
            </a>
            <a href="#section-security" class="nav-item" data-section="security">
              <span class="nav-icon">🛡️</span> 安全分析
            </a>
            <a href="#section-unittests" class="nav-item" data-section="unittests">
              <span class="nav-icon">🧪</span> 單元測試
            </a>
            <a href="#section-inttests" class="nav-item" data-section="inttests">
              <span class="nav-icon">⚗️</span> 整合測試
            </a>
          </nav>
        </aside>
        """;

    private string BuildSection(string id, string title, string icon, string content, bool collapsed = false) => $"""
        <section class="section {(collapsed ? "collapsed" : "")}" id="section-{id}" aria-labelledby="heading-{id}">
          <div class="section-header" onclick="toggleSection('{id}')" role="button" tabindex="0"
               onkeydown="if(event.key==='Enter'||event.key===' ')toggleSection('{id}')">
            <h2 class="section-title" id="heading-{id}">
              <span class="section-icon">{icon}</span>
              {title}
            </h2>
            <button class="collapse-btn" aria-expanded="{(!collapsed).ToString().ToLower()}"
                    aria-controls="body-{id}" title="摺疊/展開">
              <span class="collapse-icon">{(collapsed ? "▶" : "▼")}</span>
            </button>
          </div>
          <div class="section-body" id="body-{id}" role="region">
            {content}
          </div>
        </section>
        """;

    private string BuildOverviewSection(DashboardData data)
    {
        var langs = string.Join("、", data.Project.Languages.Take(5).Select(l => l.Name));
        var summary = HtmlEncode(data.CopilotSummary ?? "");
        var patterns = data.DesignPatterns.Count > 0
            ? string.Join("", data.DesignPatterns.Select(p => $"<span class=\"tag tag-blue\">{HtmlEncode(p)}</span>"))
            : "<span class=\"text-secondary\">未偵測到</span>";

        var content = $"""
            <div class="stats-grid">
              <div class="stat-card">
                <div class="stat-value">{data.Project.TotalFiles}</div>
                <div class="stat-label">總檔案數</div>
              </div>
              <div class="stat-card">
                <div class="stat-value">{data.Project.Languages.Count}</div>
                <div class="stat-label">程式語言</div>
              </div>
              <div class="stat-card">
                <div class="stat-value">{data.Packages.Count}</div>
                <div class="stat-label">外部依賴</div>
              </div>
              <div class="stat-card">
                <div class="stat-value">{data.ApiEndpoints.Count}</div>
                <div class="stat-label">API 端點</div>
              </div>
              <div class="stat-card">
                <div class="stat-value">{data.Containers.Count}</div>
                <div class="stat-label">容器服務</div>
              </div>
              <div class="stat-card">
                <div class="stat-value">{data.EnvVariables.Count}</div>
                <div class="stat-label">環境變數</div>
              </div>
            </div>
            {(!string.IsNullOrEmpty(data.CopilotSummary) ? $"<div class=\"info-box\">{summary}</div>" : "")}
            <div class="meta-grid">
              <div class="meta-row"><span class="meta-key">分支</span><span class="meta-val tag tag-green">{HtmlEncode(data.Project.Branch)}</span></div>
              <div class="meta-row"><span class="meta-key">語言</span><span class="meta-val">{HtmlEncode(langs)}</span></div>
              <div class="meta-row"><span class="meta-key">設計模式</span><span class="meta-val">{patterns}</span></div>
              <div class="meta-row"><span class="meta-key">分析時間</span><span class="meta-val">{HtmlEncode(data.Meta.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"))}</span></div>
              <div class="meta-row"><span class="meta-key">工具版本</span><span class="meta-val">{HtmlEncode(data.Meta.ToolVersion)}</span></div>
            </div>
            """;

        return BuildSection("overview", "專案概覽", "📊", content);
    }

    private string BuildLanguageSection(DashboardData data)
    {
        if (data.Project.Languages.Count == 0)
            return BuildSection("languages", "語言分佈", "🔤", "<p class=\"text-secondary\">未偵測到程式語言。</p>");

        var langBars = string.Join("\n", data.Project.Languages.Select(l => $"""
            <div class="lang-bar-row">
              <span class="lang-name">{HtmlEncode(l.Name)}</span>
              <div class="lang-bar-track">
                <div class="lang-bar-fill" style="width:{l.Percentage}%;background:{HtmlEncode(l.Color)}"
                     title="{l.FileCount} 檔案 ({l.Percentage}%)"></div>
              </div>
              <span class="lang-pct">{l.Percentage}%</span>
              <span class="lang-count text-secondary">{l.FileCount} 檔</span>
            </div>
            """));

        var donutSegments = BuildDonutChart(data.Project.Languages);

        var content = $"""
            <div class="lang-layout">
              <div class="lang-donut-wrap">
                <svg class="donut-chart" viewBox="0 0 120 120" aria-label="語言分佈圓餅圖">
                  {donutSegments}
                </svg>
              </div>
              <div class="lang-bars">{langBars}</div>
            </div>
            """;

        return BuildSection("languages", "語言分佈", "🔤", content);
    }

    private string BuildDonutChart(List<LanguageInfo> languages)
    {
        var segments = new StringBuilder();
        var circumference = 2 * Math.PI * 40;
        var offset = 0.0;
        var cx = 60; var cy = 60; var r = 40;

        foreach (var lang in languages)
        {
            var fraction = lang.Percentage / 100.0;
            var dashLen = fraction * circumference;
            var gapLen = circumference - dashLen;
            var rotation = offset / circumference * 360 - 90;

            segments.AppendLine(
                $"<circle class=\"donut-seg\" cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" " +
                $"fill=\"none\" stroke=\"{HtmlEncode(lang.Color)}\" stroke-width=\"18\" " +
                $"stroke-dasharray=\"{dashLen:F2} {gapLen:F2}\" " +
                $"transform=\"rotate({rotation:F2} {cx} {cy})\" " +
                $"data-lang=\"{HtmlEncode(lang.Name)}\" data-pct=\"{lang.Percentage}\">" +
                $"<title>{HtmlEncode(lang.Name)}: {lang.Percentage}%</title></circle>");
            offset += fraction * circumference;
        }
        return segments.ToString();
    }

    private string BuildDependencySection(DashboardData data)
    {
        if (data.Packages.Count == 0)
            return BuildSection("dependencies", "依賴關係圖", "📦", "<p class=\"text-secondary\">未找到套件依賴。</p>");

        var mermaid = BuildDependencyMermaid(data);
        var ecosystems = data.Packages.GroupBy(p => p.Ecosystem)
            .Select(g => $"<span class=\"tag tag-purple\">{HtmlEncode(g.Key)} ({g.Count()})</span>");

        var tableRows = string.Join("\n", data.Packages.Take(50).Select(p => $"""
            <tr class="searchable" data-text="{HtmlEncode(p.Name)} {HtmlEncode(p.Version)}">
              <td><code>{HtmlEncode(p.Name)}</code></td>
              <td><span class="tag tag-blue">{HtmlEncode(p.Version)}</span></td>
              <td><span class="tag {(p.Type == "dev" ? "tag-orange" : "tag-green")}">{HtmlEncode(p.Type)}</span></td>
              <td class="text-secondary">{HtmlEncode(p.Ecosystem)}</td>
              <td class="text-secondary">{HtmlEncode(p.SourceFile)}</td>
            </tr>
            """));

        var content = $"""
            <div class="sub-tabs">
              <button class="sub-tab active" onclick="switchTab(this,'dep-graph')">依賴圖</button>
              <button class="sub-tab" onclick="switchTab(this,'dep-table')">套件列表</button>
            </div>
            <div class="ecosystems">{string.Join(" ", ecosystems)}</div>
            <div id="dep-graph" class="sub-panel">
              <div class="mermaid-wrap">
                <div class="mermaid" data-id="dep-mermaid">{HtmlEncode(mermaid)}</div>
              </div>
            </div>
            <div id="dep-table" class="sub-panel hidden">
              <input type="search" class="table-search" placeholder="過濾套件..." oninput="filterTable(this)" data-target="dep-pkg-table" />
              <div class="table-wrap">
                <table id="dep-pkg-table" class="data-table">
                  <thead><tr><th>套件名稱</th><th>版本</th><th>類型</th><th>生態系</th><th>來源</th></tr></thead>
                  <tbody>{tableRows}</tbody>
                </table>
              </div>
            </div>
            """;

        return BuildSection("dependencies", $"依賴關係圖 ({data.Packages.Count})", "📦", content);
    }

    private string BuildDependencyMermaid(DashboardData data)
    {
        var sb = new StringBuilder("graph LR\n");
        var root = $"ROOT[\"{EscapeMermaid(data.Project.Name)}\"]";
        sb.AppendLine($"  ROOT[\"{EscapeMermaid(data.Project.Name)}\"]");

        var ecosystemGroups = data.Packages.Take(30).GroupBy(p => p.Ecosystem);
        foreach (var grp in ecosystemGroups)
        {
            var grpId = $"G_{grp.Key}";
            sb.AppendLine($"  subgraph {grpId} [\"{EscapeMermaid(grp.Key)}\"]");
            foreach (var pkg in grp.Take(8))
            {
                var nodeId = $"P_{SanitizeMermaidId(pkg.Name)}";
                sb.AppendLine($"    {nodeId}[\"{EscapeMermaid(pkg.Name)} {EscapeMermaid(pkg.Version)}\"]");
            }
            sb.AppendLine("  end");
            sb.AppendLine($"  ROOT --> {grpId}");
        }
        return sb.ToString();
    }

    private string BuildCallGraphSection(DashboardData data)
    {
        if (data.CallGraph.Nodes.Count == 0)
            return BuildSection("callgraph", "函式呼叫圖", "🕸️", "<p class=\"text-secondary\">未偵測到函式呼叫關係。</p>", true);

        var mermaid = BuildCallGraphMermaid(data.CallGraph);
        var content = $"""
            <div class="mermaid-wrap">
              <div class="mermaid" data-id="call-mermaid">{HtmlEncode(mermaid)}</div>
            </div>
            <p class="text-secondary" style="margin-top:8px;font-size:12px">
              顯示前 {Math.Min(data.CallGraph.Nodes.Count, 30)} 個節點，{data.CallGraph.Edges.Count} 條呼叫邊。點擊節點查看詳情。
            </p>
            """;

        return BuildSection("callgraph", $"函式呼叫圖 ({data.CallGraph.Nodes.Count} 節點)", "🕸️", content, data.CallGraph.Nodes.Count > 20);
    }

    private string BuildCallGraphMermaid(CallGraph cg)
    {
        var sb = new StringBuilder("graph TD\n");
        foreach (var node in cg.Nodes.Take(30))
        {
            var id = SanitizeMermaidId(node.Id);
            var label = EscapeMermaid(node.Name.Length > 20 ? node.Name[..20] + "…" : node.Name);
            sb.AppendLine($"  {id}[\"{label}\"]");
        }
        var nodeIds = cg.Nodes.Take(30).Select(n => n.Id).ToHashSet();
        foreach (var edge in cg.Edges.Take(50))
        {
            if (!nodeIds.Contains(edge.Caller) || !nodeIds.Contains(edge.Callee)) continue;
            sb.AppendLine($"  {SanitizeMermaidId(edge.Caller)} --> {SanitizeMermaidId(edge.Callee)}");
        }
        return sb.ToString();
    }

    private string BuildApiSection(DashboardData data)
    {
        if (data.ApiEndpoints.Count == 0)
            return BuildSection("api", "API 端點", "🔌", "<p class=\"text-secondary\">未找到 API 端點。請確認 Swagger/OpenAPI 文件存在。</p>");

        var tags = data.ApiEndpoints.Where(e => e.Tag != null)
            .Select(e => e.Tag!).Distinct()
            .Select(t => $"<option value=\"{HtmlEncode(t)}\">{HtmlEncode(t)}</option>");

        var rows = string.Join("\n", data.ApiEndpoints.Select(e => $"""
            <tr class="searchable api-row {(e.IsDeprecated ? "deprecated" : "")}"
                data-text="{HtmlEncode(e.Method)} {HtmlEncode(e.Path)} {HtmlEncode(e.Summary)}"
                data-tag="{HtmlEncode(e.Tag ?? "")}"
                onclick="openApiDetailTab({JsonConvert.SerializeObject(e, _onclickJson).Replace("\"", "&quot;")}, window.__RID_DATA__)"
                style="cursor:pointer">
              <td><span class="method-badge method-{HtmlEncode(e.Method.ToLowerInvariant())}">{HtmlEncode(e.Method)}</span></td>
              <td><code class="path">{HtmlEncode(e.Path)}</code></td>
              <td>{HtmlEncode(e.Summary)}</td>
              <td>{HtmlEncode(e.Tag ?? "")}</td>
              <td>{(e.IsDeprecated ? "<span class=\"tag tag-red\">棄用</span>" : "")}</td>
            </tr>
            """));

        var content = $"""
            <div class="filter-bar">
              <input type="search" class="table-search" placeholder="搜尋路徑、說明..."
                     oninput="filterApiTable(this)" id="api-search" />
              <select id="api-method-filter" onchange="filterApiTable(document.getElementById('api-search'))"
                      class="select-filter">
                <option value="">所有方法</option>
                <option value="GET">GET</option>
                <option value="POST">POST</option>
                <option value="PUT">PUT</option>
                <option value="DELETE">DELETE</option>
                <option value="PATCH">PATCH</option>
              </select>
              <select id="api-tag-filter" onchange="filterApiTable(document.getElementById('api-search'))"
                      class="select-filter">
                <option value="">所有標籤</option>
                {string.Join("\n", tags)}
              </select>
            </div>
            <div class="table-wrap">
              <table id="api-table" class="data-table">
                <thead>
                  <tr>
                    <th style="width:80px">方法</th>
                    <th>路徑</th>
                    <th>說明</th>
                    <th style="width:100px">標籤</th>
                    <th style="width:60px">狀態</th>
                  </tr>
                </thead>
                <tbody>{rows}</tbody>
              </table>
            </div>
            <p class="text-secondary" style="font-size:12px;margin-top:8px">點擊任一行 → 開新頁查看執行路徑、邏輯與 SQL</p>
            """;

        return BuildSection("api", $"API 端點 ({data.ApiEndpoints.Count})", "🔌", content);
    }

    private string BuildDockerSection(DashboardData data)
    {
        if (data.Containers.Count == 0 && data.Dockerfile == null)
            return BuildSection("docker", "Docker 架構", "🐳", "<p class=\"text-secondary\">未找到 Docker 相關設定。</p>");

        var mermaid = BuildDockerMermaid(data);
        var cards = string.Join("\n", data.Containers.Select(c => $"""
            <div class="container-card" onclick="showContainerDetail({JsonConvert.SerializeObject(c, _onclickJson).Replace("\"", "&quot;")})"
                 style="cursor:pointer" title="點擊查看詳情">
              <div class="container-header">
                <span class="container-icon">🐳</span>
                <strong>{HtmlEncode(c.Name)}</strong>
              </div>
              <div class="container-image text-secondary">{HtmlEncode(c.Image)}</div>
              {(c.Ports.Count > 0 ? $"<div class=\"container-ports\">{string.Join(" ", c.Ports.Select(p => $"<span class='tag tag-blue'>{p.HostPort}:{p.ContainerPort}</span>"))}</div>" : "")}
              {(c.DependsOn.Count > 0 ? $"<div class=\"text-secondary\" style=\"font-size:11px\">依賴：{HtmlEncode(string.Join(", ", c.DependsOn))}</div>" : "")}
            </div>
            """));

        var dockerfileInfo = data.Dockerfile != null ? $"""
            <div class="info-box" style="margin-top:16px">
              <strong>Dockerfile</strong>：基底映像 <code>{HtmlEncode(data.Dockerfile.BaseImage)}</code>
              {(data.Dockerfile.ExposedPorts.Count > 0 ? $"，暴露 Port：{string.Join(", ", data.Dockerfile.ExposedPorts)}" : "")}
              {(data.Dockerfile.Stages.Count > 1 ? $"，多階段構建：{HtmlEncode(string.Join(" → ", data.Dockerfile.Stages))}" : "")}
            </div>
            """ : "";

        var content = $"""
            <div class="sub-tabs">
              <button class="sub-tab active" onclick="switchTab(this,'docker-diagram')">架構圖</button>
              <button class="sub-tab" onclick="switchTab(this,'docker-cards')">服務卡片</button>
            </div>
            <div id="docker-diagram" class="sub-panel">
              <div class="mermaid-wrap">
                <div class="mermaid" data-id="docker-mermaid">{HtmlEncode(mermaid)}</div>
              </div>
            </div>
            <div id="docker-cards" class="sub-panel hidden">
              <div class="container-grid">{cards}</div>
            </div>
            {dockerfileInfo}
            """;

        return BuildSection("docker", $"Docker 架構 ({data.Containers.Count} 服務)", "🐳", content);
    }

    private string BuildDockerMermaid(DashboardData data)
    {
        var sb = new StringBuilder("graph TD\n");
        sb.AppendLine("  subgraph DOCKER [\"🐳 Docker 服務拓撲\"]");
        foreach (var c in data.Containers)
        {
            var id = SanitizeMermaidId(c.Name);
            var ports = c.Ports.Count > 0 ? $"\\n{string.Join(",", c.Ports.Take(3).Select(p => $"{p.HostPort}:{p.ContainerPort}"))}" : "";
            sb.AppendLine($"    {id}[\"{EscapeMermaid(c.Name)}{ports}\"]");
        }
        sb.AppendLine("  end");

        foreach (var c in data.Containers)
        {
            var fromId = SanitizeMermaidId(c.Name);
            foreach (var dep in c.DependsOn)
                sb.AppendLine($"  {SanitizeMermaidId(dep)} --> {fromId}");
        }
        return sb.ToString();
    }

    private string BuildPortTable(DashboardData data)
    {
        var allPorts = data.Containers
            .SelectMany(c => c.Ports.Select(p => new { Service = c.Name, p.HostPort, p.ContainerPort, p.Protocol }))
            .OrderBy(p => p.HostPort)
            .ToList();

        if (data.Dockerfile != null)
        {
            // also include Dockerfile exposed ports
        }

        if (allPorts.Count == 0)
            return BuildSection("ports", "Port 映射表", "🔗", "<p class=\"text-secondary\">未找到 Port 映射。</p>", true);

        var rows = string.Join("\n", allPorts.Select(p => $"""
            <tr>
              <td><strong>{HtmlEncode(p.Service)}</strong></td>
              <td><span class="tag tag-green">{p.HostPort}</span></td>
              <td><span class="tag tag-blue">{p.ContainerPort}</span></td>
              <td class="text-secondary">{HtmlEncode(p.Protocol)}</td>
              <td><a href="http://localhost:{p.HostPort}" target="_blank" class="link-external">localhost:{p.HostPort} ↗</a></td>
            </tr>
            """));

        var content = $"""
            <div class="table-wrap">
              <table class="data-table">
                <thead><tr><th>服務</th><th>Host Port</th><th>Container Port</th><th>協定</th><th>快速連結</th></tr></thead>
                <tbody>{rows}</tbody>
              </table>
            </div>
            """;

        return BuildSection("ports", $"Port 映射表 ({allPorts.Count})", "🔗", content);
    }

    private string BuildStartupSection(DashboardData data)
    {
        if (data.StartupSequence.Count == 0)
            return BuildSection("startup", "啟動流程", "🚀", "<p class=\"text-secondary\">未偵測到容器服務啟動順序。</p>", true);

        var seqMermaid = BuildSequenceMermaid(data);
        var ganttMermaid = BuildGanttMermaid(data);

        var content = $"""
            <div class="sub-tabs">
              <button class="sub-tab active" onclick="switchTab(this,'startup-seq')">時序圖</button>
              <button class="sub-tab" onclick="switchTab(this,'startup-gantt')">Gantt 圖</button>
            </div>
            <div id="startup-seq" class="sub-panel">
              <div class="mermaid-wrap"><div class="mermaid">{HtmlEncode(seqMermaid)}</div></div>
            </div>
            <div id="startup-gantt" class="sub-panel hidden">
              <div class="mermaid-wrap"><div class="mermaid">{HtmlEncode(ganttMermaid)}</div></div>
            </div>
            """;

        return BuildSection("startup", "啟動流程 (時序 + Gantt)", "🚀", content);
    }

    private string BuildSequenceMermaid(DashboardData data)
    {
        var sb = new StringBuilder("sequenceDiagram\n");
        sb.AppendLine("  autonumber");
        sb.AppendLine("  participant SYS as 系統");
        foreach (var svc in data.StartupSequence.Take(8))
            sb.AppendLine($"  participant {SanitizeMermaidId(svc)} as {EscapeMermaid(svc)}");

        sb.AppendLine("  Note over SYS: 啟動初始化");
        foreach (var svc in data.StartupSequence.Take(8))
        {
            sb.AppendLine($"  SYS->>{SanitizeMermaidId(svc)}: 啟動 {EscapeMermaid(svc)}");
            sb.AppendLine($"  {SanitizeMermaidId(svc)}-->>SYS: Ready");
        }
        return sb.ToString();
    }

    private string BuildGanttMermaid(DashboardData data)
    {
        var sb = new StringBuilder("gantt\n");
        sb.AppendLine("  title 服務啟動時序");
        sb.AppendLine("  dateFormat  X");
        sb.AppendLine("  axisFormat %s秒");
        sb.AppendLine("  section 啟動階段");

        var startTime = 0;
        foreach (var svc in data.StartupSequence.Take(10))
        {
            var duration = 3 + (svc.Length % 5);
            sb.AppendLine($"  {EscapeMermaid(svc)}: {startTime}, {startTime + duration}");
            startTime += duration / 2;
        }
        return sb.ToString();
    }

    private string BuildEnvSection(DashboardData data)
    {
        if (data.EnvVariables.Count == 0)
            return BuildSection("env", "環境變數", "🔑", "<p class=\"text-secondary\">未找到環境變數（.env 不存在或為空）。</p>", true);

        var rows = string.Join("\n", data.EnvVariables.Select(e => $"""
            <tr class="searchable" data-text="{HtmlEncode(e.Key)}">
              <td><code>{HtmlEncode(e.Key)}</code></td>
              <td class="env-value-cell">
                {(e.IsSensitive
                    ? $"<span class=\"env-masked\" data-value=\"{HtmlEncode(e.Value)}\" onclick=\"toggleEnvValue(this)\" title=\"點擊顯示/隱藏\">●●●●●●</span>"
                    : $"<code>{HtmlEncode(e.Value)}</code>")}
              </td>
              <td>{(e.IsSensitive ? "<span class=\"tag tag-red\">敏感</span>" : "<span class=\"tag tag-green\">安全</span>")}</td>
              <td class="text-secondary">{HtmlEncode(e.SourceFile)}</td>
            </tr>
            """));

        var content = $"""
            <div class="filter-bar">
              <input type="search" class="table-search" placeholder="搜尋變數名..."
                     oninput="filterTable(this)" data-target="env-table" />
              <button class="btn-sm" onclick="toggleAllEnv()">顯示/隱藏所有值</button>
            </div>
            <div class="table-wrap">
              <table id="env-table" class="data-table">
                <thead><tr><th>Key</th><th>Value</th><th>安全性</th><th>來源</th></tr></thead>
                <tbody>{rows}</tbody>
              </table>
            </div>
            """;

        return BuildSection("env", $"環境變數 ({data.EnvVariables.Count})", "🔑", content);
    }

    private string BuildFileTreeSection(DashboardData data)
    {
        var treeHtml = BuildFileTreeHtml(data.FileTree, 0);
        var content = $"""
            <div class="file-tree" id="file-tree-root">
              {treeHtml}
            </div>
            <p class="text-secondary" style="font-size:12px;margin-top:8px">
              共 {data.Project.TotalFiles} 個檔案，總大小 {FormatBytes(data.Project.TotalSizeBytes)}
            </p>
            """;

        return BuildSection("filetree", "檔案樹", "🗂️", content, true);
    }

    private string BuildFileTreeHtml(FileNode node, int depth)
    {
        var sb = new StringBuilder();
        if (depth > 5) return ""; // Limit depth

        if (node.IsDirectory)
        {
            var hasChildren = node.Children.Count > 0;
            var collapseId = $"ft_{depth}_{SanitizeMermaidId(node.Name)}_{node.GetHashCode():X4}";
            sb.AppendLine($"""
                <div class="ft-dir" data-depth="{depth}">
                  <div class="ft-row ft-dir-row" onclick="toggleFtNode('{collapseId}')">
                    <span class="ft-icon">📁</span>
                    <span class="ft-name">{HtmlEncode(node.Name)}</span>
                    <span class="ft-count text-secondary">({node.Children.Count})</span>
                  </div>
                  <div class="ft-children" id="{collapseId}">
                """);
            foreach (var child in node.Children.Take(50))
                sb.Append(BuildFileTreeHtml(child, depth + 1));
            if (node.Children.Count > 50)
                sb.AppendLine($"<div class=\"ft-more text-secondary\">... 還有 {node.Children.Count - 50} 個項目</div>");
            sb.AppendLine("</div></div>");
        }
        else
        {
            var langColor = LanguageDetector.GetColor(node.Extension);
            sb.AppendLine($"""
                <div class="ft-file" data-depth="{depth}">
                  <div class="ft-row">
                    <span class="ft-dot" style="background:{HtmlEncode(langColor)}"></span>
                    <span class="ft-name">{HtmlEncode(node.Name)}</span>
                    <span class="ft-size text-secondary">{FormatBytes(node.SizeBytes)}</span>
                  </div>
                </div>
                """);
        }
        return sb.ToString();
    }

    private string BuildSecuritySection(DashboardData data)
    {
        if (data.SecurityRisks.Count == 0)
            return BuildSection("security", "安全分析", "🛡️",
                "<div class=\"info-box success\">✅ 未偵測到明顯安全問題。</div>");

        var items = string.Join("\n", data.SecurityRisks.Select(r => $"""
            <div class="risk-item risk-{HtmlEncode(r.Level)}">
              <div class="risk-header">
                <span class="risk-icon">{r.Level switch { "critical" => "🔴", "warning" => "🟡", _ => "🔵" }}</span>
                <strong>{HtmlEncode(r.Title)}</strong>
                <span class="tag {r.Level switch { "critical" => "tag-red", "warning" => "tag-orange", _ => "tag-blue" }}">{HtmlEncode(r.Level)}</span>
              </div>
              <p class="risk-desc">{HtmlEncode(r.Description)}</p>
              {(r.FilePath != null ? $"<code class=\"risk-file\">{HtmlEncode(r.FilePath)}</code>" : "")}
            </div>
            """));

        return BuildSection("security", $"安全分析 ({data.SecurityRisks.Count})", "🛡️", items);
    }

    private string BuildUnitTestSection(DashboardData data)
    {
        var unitFiles = data.Tests.UnitTests;
        var unitCount = unitFiles.Sum(f => f.TestCases.Count);

        if (unitCount == 0 && unitFiles.Count == 0)
            return BuildSection("unittests", "單元測試", "🧪",
                "<p class=\"text-secondary\">未偵測到單元測試檔案。</p>", true);

        var statCards = $"""
            <div class="stats-grid" style="grid-template-columns:repeat(auto-fill,minmax(120px,1fr));margin-bottom:16px">
              <div class="stat-card"><div class="stat-value">{unitFiles.Count}</div><div class="stat-label">測試檔案</div></div>
              <div class="stat-card"><div class="stat-value">{unitCount}</div><div class="stat-label">測試函式</div></div>
              <div class="stat-card"><div class="stat-value">{unitFiles.Sum(f => f.TestCases.Count(t => t.HasSubtests))}</div><div class="stat-label">含子測試</div></div>
              <div class="stat-card"><div class="stat-value">{data.Tests.Mocks.Count}</div><div class="stat-label">Mock 物件</div></div>
            </div>
            """;

        var rows = string.Join("\n", unitFiles.Select(f => {
            var cases = string.Join("", f.TestCases.Select(tc =>
                $"<li class='test-case' title='{HtmlEncode(tc.Description ?? "")}'>" +
                $"<span class='test-icon'>✓</span> {HtmlEncode(tc.Name)}" +
                (tc.HasSubtests ? $" <span class='tag tag-blue'>{tc.Subtests.Count} 子測試</span>" : "") +
                "</li>"));
            return $"""
                <div class="test-file-card">
                  <div class="test-file-header" onclick="this.nextElementSibling.classList.toggle('collapsed')">
                    <span class="test-file-icon">📄</span>
                    <code class="test-file-path">{HtmlEncode(f.FilePath)}</code>
                    <span class="tag tag-green">{f.TestCases.Count} tests</span>
                  </div>
                  <ul class="test-case-list">{cases}</ul>
                </div>
                """;
        }));

        var mockRows = data.Tests.Mocks.Count > 0
            ? $"<h4 style=\"margin:16px 0 8px;font-size:13px\">Mock 物件 ({data.Tests.Mocks.Count})</h4>" +
              $"<div class=\"mock-grid\">{string.Join("", data.Tests.Mocks.Select(m =>
                $"<div class='mock-card'><strong>{HtmlEncode(m.Name)}</strong><div class='text-secondary' style='font-size:11px'>{HtmlEncode(m.FilePath)}</div><div style='margin-top:4px'>{string.Join(" ", m.Methods.Take(5).Select(met => $"<span class='tag tag-purple'>{HtmlEncode(met)}</span>"))}</div></div>"))}</div>"
            : "";

        var content = statCards + rows + mockRows;
        return BuildSection("unittests", $"單元測試 ({unitCount} tests / {unitFiles.Count} 檔案)", "🧪", content, true);
    }

    private string BuildIntegrationTestSection(DashboardData data)
    {
        var intFiles = data.Tests.IntegrationTests;
        var accFiles = data.Tests.AcceptanceTests;
        var intCount = intFiles.Sum(f => f.TestCases.Count);
        var accCount = accFiles.Sum(f => f.TestCases.Count);
        var total = intCount + accCount;

        if (total == 0)
            return BuildSection("inttests", "整合 / 驗收測試", "⚗️",
                "<p class=\"text-secondary\">未偵測到整合或驗收測試檔案。</p>", true);

        var statCards = $"""
            <div class="stats-grid" style="grid-template-columns:repeat(auto-fill,minmax(120px,1fr));margin-bottom:16px">
              <div class="stat-card"><div class="stat-value">{intFiles.Count}</div><div class="stat-label">整合測試檔</div></div>
              <div class="stat-card"><div class="stat-value">{intCount}</div><div class="stat-label">整合測試數</div></div>
              <div class="stat-card"><div class="stat-value">{accFiles.Count}</div><div class="stat-label">驗收測試檔</div></div>
              <div class="stat-card"><div class="stat-value">{accCount}</div><div class="stat-label">驗收測試數</div></div>
            </div>
            """;

        string RenderFiles(List<Models.TestFile> files, string label) {
            if (files.Count == 0) return "";
            var rows = string.Join("\n", files.Select(f => {
                var cases = string.Join("", f.TestCases.Select(tc =>
                    $"<li class='test-case'><span class='test-icon'>✓</span> {HtmlEncode(tc.Name)}</li>"));
                return $"""
                    <div class="test-file-card">
                      <div class="test-file-header" onclick="this.nextElementSibling.classList.toggle('collapsed')">
                        <span class="test-file-icon">📄</span>
                        <code class="test-file-path">{HtmlEncode(f.FilePath)}</code>
                        <span class="tag tag-orange">{f.TestCases.Count} tests</span>
                      </div>
                      <ul class="test-case-list">{cases}</ul>
                    </div>
                    """;
            }));
            return $"<h4 style='margin-bottom:8px;font-size:13px;color:var(--accent-orange)'>{label}</h4>{rows}";
        }

        var content = statCards
            + RenderFiles(intFiles, $"整合測試 ({intCount})")
            + RenderFiles(accFiles, $"驗收測試 ({accCount})");

        return BuildSection("inttests", $"整合 / 驗收測試 ({total})", "⚗️", content, true);
    }

    private string BuildCopilotInstructionsSection(DashboardData data)
    {
        var content = $"""
            <div class="info-box" style="white-space:pre-wrap;font-family:var(--font-mono);font-size:12px">
              {HtmlEncode(data.Project.CopilotInstructions!)}
            </div>
            """;
        return BuildSection("copilot-instructions", "Copilot Instructions", "🤖", content, true);
    }

    private string BuildDetailModal() => """
        <div id="detail-modal" class="modal-overlay" onclick="closeModal(event)" role="dialog" aria-modal="true" aria-hidden="true">
          <div class="modal-box" onclick="event.stopPropagation()">
            <div class="modal-header">
              <h3 id="modal-title">詳細資訊</h3>
              <button class="modal-close" onclick="closeModal()" aria-label="關閉">✕</button>
            </div>
            <div class="modal-body" id="modal-body"></div>
          </div>
        </div>
        """;

    private static string HtmlEncode(string? text)
        => text == null ? "" : HtmlEncoder.Default.Encode(text);

    private static string EscapeMermaid(string? text)
        => (text ?? "").Replace("\"", "'").Replace("[", "(").Replace("]", ")").Replace("\n", " ");

    private static string SanitizeMermaidId(string id)
        => System.Text.RegularExpressions.Regex.Replace(id, @"[^a-zA-Z0-9_]", "_");

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
    };

    private string GetInlineStyles(string theme) => """
        <style>
        /* ═══ CSS Design Tokens ═══ */
        :root {
          --bg-primary: #0d1117;
          --bg-secondary: #161b22;
          --bg-card: #21262d;
          --bg-hover: #30363d;
          --border-color: #30363d;
          --text-primary: #e6edf3;
          --text-secondary: #8b949e;
          --accent-blue: #58a6ff;
          --accent-green: #3fb950;
          --accent-orange: #d29922;
          --accent-red: #f85149;
          --accent-purple: #bc8cff;
          --font-sans: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
          --font-mono: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace;
          --radius: 8px;
          --sidebar-width: 220px;
          --navbar-height: 56px;
          --transition: 0.2s ease;
        }
        [data-theme="light"] {
          --bg-primary: #ffffff;
          --bg-secondary: #f6f8fa;
          --bg-card: #ffffff;
          --bg-hover: #eaf0f6;
          --border-color: #d0d7de;
          --text-primary: #1f2328;
          --text-secondary: #656d76;
        }
        /* ═══ Reset & Base ═══ */
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        html { scroll-behavior: smooth; }
        body {
          font-family: var(--font-sans);
          background: var(--bg-primary);
          color: var(--text-primary);
          font-size: 14px;
          line-height: 1.6;
          min-height: 100vh;
        }
        code {
          font-family: var(--font-mono);
          font-size: 12px;
          background: var(--bg-hover);
          padding: 2px 6px;
          border-radius: 4px;
          color: var(--accent-blue);
        }
        a { color: var(--accent-blue); text-decoration: none; }
        a:hover { text-decoration: underline; }
        /* ═══ Scrollbar ═══ */
        ::-webkit-scrollbar { width: 6px; height: 6px; }
        ::-webkit-scrollbar-track { background: var(--bg-secondary); }
        ::-webkit-scrollbar-thumb { background: var(--border-color); border-radius: 3px; }
        /* ═══ Navbar ═══ */
        .navbar {
          position: fixed; top: 0; left: 0; right: 0; z-index: 100;
          height: var(--navbar-height);
          background: var(--bg-secondary);
          border-bottom: 1px solid var(--border-color);
          display: flex; align-items: center; gap: 12px; padding: 0 16px;
        }
        .navbar-brand { display: flex; align-items: center; gap: 8px; min-width: 0; }
        .navbar-logo { font-size: 22px; color: var(--accent-blue); }
        .navbar-title { font-weight: 700; font-size: 16px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 180px; }
        .navbar-badge { background: var(--bg-hover); border: 1px solid var(--border-color); border-radius: 12px; padding: 2px 10px; font-size: 11px; color: var(--accent-green); white-space: nowrap; }
        .navbar-search { flex: 1; max-width: 400px; position: relative; }
        .navbar-search input {
          width: 100%; background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: 20px; padding: 6px 12px 6px 32px; color: var(--text-primary);
          font-size: 13px; outline: none; transition: border-color var(--transition);
        }
        .navbar-search input:focus { border-color: var(--accent-blue); }
        .search-icon { position: absolute; left: 10px; top: 50%; transform: translateY(-50%); color: var(--text-secondary); font-size: 14px; pointer-events: none; }
        .navbar-actions { display: flex; align-items: center; gap: 6px; margin-left: auto; }
        .navbar-stat { font-size: 12px; color: var(--text-secondary); padding: 4px 8px; background: var(--bg-card); border-radius: var(--radius); border: 1px solid var(--border-color); white-space: nowrap; }
        .btn-icon {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 6px 10px; cursor: pointer;
          color: var(--text-primary); font-size: 14px; transition: background var(--transition);
        }
        .btn-icon:hover { background: var(--bg-hover); }
        .btn-sm {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 4px 10px; cursor: pointer;
          color: var(--text-primary); font-size: 12px; transition: background var(--transition);
        }
        .btn-sm:hover { background: var(--bg-hover); }
        /* ═══ Layout ═══ */
        .layout {
          display: flex;
          margin-top: var(--navbar-height);
          min-height: calc(100vh - var(--navbar-height));
        }
        /* ═══ Sidebar ═══ */
        .sidebar {
          width: var(--sidebar-width); min-width: var(--sidebar-width);
          background: var(--bg-secondary); border-right: 1px solid var(--border-color);
          position: sticky; top: var(--navbar-height); height: calc(100vh - var(--navbar-height));
          overflow-y: auto; overflow-x: hidden; padding: 16px 0;
          transition: width 0.25s ease, min-width 0.25s ease, padding 0.25s ease;
        }
        .sidebar.sidebar-collapsed {
          width: 0; min-width: 0; padding: 0; border-right: none; overflow: hidden;
        }
        .sidebar.sidebar-collapsed .sidebar-nav { opacity: 0; }
        .sidebar-nav { display: flex; flex-direction: column; gap: 2px; transition: opacity 0.15s ease; }
        .nav-item {
          display: flex; align-items: center; gap: 8px;
          padding: 8px 16px; color: var(--text-secondary);
          border-radius: 0; text-decoration: none; font-size: 13px;
          transition: background var(--transition), color var(--transition);
          border-left: 3px solid transparent; white-space: nowrap;
        }
        .nav-item:hover { background: var(--bg-hover); color: var(--text-primary); text-decoration: none; }
        .nav-item.active { color: var(--accent-blue); border-left-color: var(--accent-blue); background: rgba(88,166,255,0.1); }
        .nav-icon { font-size: 14px; width: 18px; text-align: center; }
        /* ═══ Main Content ═══ */
        .main-content {
          flex: 1; min-width: 0;
          padding: 24px; display: flex; flex-direction: column; gap: 20px;
        }
        /* ═══ Section ═══ */
        .section {
          background: var(--bg-secondary);
          border: 1px solid var(--border-color);
          border-radius: var(--radius);
          overflow: hidden;
          transition: border-color var(--transition);
        }
        .section:hover { border-color: var(--accent-blue); }
        .section-header {
          display: flex; align-items: center; justify-content: space-between;
          padding: 14px 20px; cursor: pointer;
          background: var(--bg-card); user-select: none;
          border-bottom: 1px solid var(--border-color);
        }
        .section-header:hover { background: var(--bg-hover); }
        .section-title {
          display: flex; align-items: center; gap: 10px;
          font-size: 15px; font-weight: 600; color: var(--text-primary);
        }
        .section-icon { font-size: 16px; }
        .collapse-btn {
          background: none; border: none; cursor: pointer;
          color: var(--text-secondary); font-size: 12px; padding: 2px 6px;
        }
        .section-body { padding: 20px; }
        .section.collapsed .section-body { display: none; }
        .section.collapsed .section-header { border-bottom: none; }
        /* ═══ Stats Grid ═══ */
        .stats-grid {
          display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 12px;
          margin-bottom: 16px;
        }
        .stat-card {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 16px; text-align: center;
          transition: transform var(--transition), border-color var(--transition);
        }
        .stat-card:hover { transform: translateY(-2px); border-color: var(--accent-blue); }
        .stat-value { font-size: 28px; font-weight: 700; color: var(--accent-blue); }
        .stat-label { font-size: 12px; color: var(--text-secondary); margin-top: 4px; }
        /* ═══ Meta Grid ═══ */
        .meta-grid { display: flex; flex-direction: column; gap: 8px; }
        .meta-row { display: flex; align-items: center; gap: 12px; padding: 6px 0; border-bottom: 1px solid var(--border-color); }
        .meta-row:last-child { border-bottom: none; }
        .meta-key { font-size: 12px; color: var(--text-secondary); min-width: 80px; }
        .meta-val { font-size: 13px; }
        /* ═══ Tags ═══ */
        .tag { display: inline-block; padding: 2px 8px; border-radius: 12px; font-size: 11px; font-weight: 500; }
        .tag-blue { background: rgba(88,166,255,0.15); color: var(--accent-blue); border: 1px solid rgba(88,166,255,0.3); }
        .tag-green { background: rgba(63,185,80,0.15); color: var(--accent-green); border: 1px solid rgba(63,185,80,0.3); }
        .tag-orange { background: rgba(210,153,34,0.15); color: var(--accent-orange); border: 1px solid rgba(210,153,34,0.3); }
        .tag-red { background: rgba(248,81,73,0.15); color: var(--accent-red); border: 1px solid rgba(248,81,73,0.3); }
        .tag-purple { background: rgba(188,140,255,0.15); color: var(--accent-purple); border: 1px solid rgba(188,140,255,0.3); }
        /* ═══ Info Box ═══ */
        .info-box {
          background: rgba(88,166,255,0.07); border: 1px solid rgba(88,166,255,0.2);
          border-radius: var(--radius); padding: 12px 16px; font-size: 13px;
          line-height: 1.7; margin-bottom: 16px;
        }
        .info-box.success { background: rgba(63,185,80,0.07); border-color: rgba(63,185,80,0.2); }
        /* ═══ Language Chart ═══ */
        .lang-layout { display: flex; align-items: flex-start; gap: 24px; }
        .lang-donut-wrap { width: 140px; min-width: 140px; }
        .donut-chart { width: 140px; height: 140px; }
        .lang-bars { flex: 1; display: flex; flex-direction: column; gap: 10px; }
        .lang-bar-row { display: flex; align-items: center; gap: 10px; }
        .lang-name { font-size: 12px; min-width: 90px; color: var(--text-primary); }
        .lang-bar-track { flex: 1; height: 8px; background: var(--bg-hover); border-radius: 4px; overflow: hidden; }
        .lang-bar-fill { height: 100%; border-radius: 4px; transition: width 0.6s ease; }
        .lang-pct { font-size: 11px; color: var(--text-secondary); min-width: 35px; text-align: right; }
        .lang-count { font-size: 11px; min-width: 40px; }
        /* ═══ Tables ═══ */
        .table-wrap { overflow-x: auto; }
        .data-table { width: 100%; border-collapse: collapse; font-size: 13px; }
        .data-table th {
          background: var(--bg-card); padding: 8px 12px; text-align: left;
          font-size: 11px; font-weight: 600; color: var(--text-secondary);
          border-bottom: 1px solid var(--border-color); text-transform: uppercase; letter-spacing: 0.5px;
          position: sticky; top: 0; z-index: 1;
        }
        .data-table td { padding: 8px 12px; border-bottom: 1px solid var(--border-color); color: var(--text-primary); }
        .data-table tr:hover td { background: var(--bg-hover); }
        .data-table tr.hidden { display: none; }
        .data-table tr.deprecated td { opacity: 0.5; text-decoration: line-through; }
        .path { font-size: 12px; color: var(--accent-blue); }
        /* ═══ Method Badges ═══ */
        .method-badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 10px; font-weight: 700; min-width: 48px; text-align: center; }
        .method-get { background: rgba(63,185,80,0.2); color: #3fb950; }
        .method-post { background: rgba(88,166,255,0.2); color: #58a6ff; }
        .method-put { background: rgba(210,153,34,0.2); color: #d29922; }
        .method-delete { background: rgba(248,81,73,0.2); color: #f85149; }
        .method-patch { background: rgba(188,140,255,0.2); color: #bc8cff; }
        .method-options,.method-head { background: rgba(139,148,158,0.2); color: #8b949e; }
        /* ═══ Filter Bar ═══ */
        .filter-bar { display: flex; align-items: center; gap: 8px; margin-bottom: 12px; flex-wrap: wrap; }
        .table-search, .select-filter {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 6px 10px; color: var(--text-primary);
          font-size: 12px; outline: none;
        }
        .table-search { flex: 1; min-width: 150px; }
        .table-search:focus, .select-filter:focus { border-color: var(--accent-blue); }
        .select-filter { cursor: pointer; }
        /* ═══ Sub-Tabs ═══ */
        .sub-tabs { display: flex; gap: 4px; margin-bottom: 12px; border-bottom: 1px solid var(--border-color); }
        .sub-tab {
          background: none; border: none; border-bottom: 2px solid transparent;
          padding: 8px 14px; cursor: pointer; font-size: 13px; color: var(--text-secondary);
          transition: color var(--transition), border-color var(--transition);
          margin-bottom: -1px;
        }
        .sub-tab:hover { color: var(--text-primary); }
        .sub-tab.active { color: var(--accent-blue); border-bottom-color: var(--accent-blue); font-weight: 600; }
        .sub-panel.hidden { display: none; }
        /* ═══ Mermaid ═══ */
        .mermaid-wrap {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 16px; overflow: auto;
          min-height: 80px;
        }
        .mermaid { font-family: var(--font-sans) !important; }
        .mermaid svg { max-width: 100%; }
        /* ═══ Container Grid ═══ */
        .container-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
        .container-card {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 14px;
          transition: border-color var(--transition), transform var(--transition);
        }
        .container-card:hover { border-color: var(--accent-blue); transform: translateY(-2px); }
        .container-header { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; }
        .container-image { font-size: 11px; margin-bottom: 8px; font-family: var(--font-mono); }
        .container-ports { display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 4px; }
        /* ═══ Env Variables ═══ */
        .env-masked { cursor: pointer; color: var(--text-secondary); font-family: var(--font-mono); font-size: 12px; letter-spacing: 2px; }
        .env-masked:hover { color: var(--accent-orange); }
        /* ═══ File Tree ═══ */
        .file-tree { font-family: var(--font-mono); font-size: 12px; line-height: 1.8; }
        .ft-row { display: flex; align-items: center; gap: 6px; padding: 2px 4px; border-radius: 4px; cursor: pointer; }
        .ft-row:hover { background: var(--bg-hover); }
        .ft-dir { margin-left: 14px; }
        .ft-file { margin-left: 14px; }
        .ft-dir-row { color: var(--accent-blue); }
        .ft-name { color: var(--text-primary); }
        .ft-count { font-size: 10px; }
        .ft-size { font-size: 10px; margin-left: auto; }
        .ft-dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
        .ft-more { padding: 2px 4px; font-size: 11px; font-style: italic; }
        .ft-children { overflow: hidden; transition: max-height 0.2s ease; }
        .ft-children.collapsed { display: none; }
        /* ═══ Test Sections ═══ */
        .test-file-card { background:var(--bg-card); border:1px solid var(--border-color); border-radius:var(--radius); margin-bottom:8px; overflow:hidden; }
        .test-file-header { display:flex; align-items:center; gap:8px; padding:10px 14px; cursor:pointer; background:var(--bg-hover); }
        .test-file-header:hover { background:var(--bg-card); }
        .test-file-path { font-size:11px; color:var(--accent-blue); flex:1; }
        .test-file-icon { font-size:13px; }
        .test-case-list { list-style:none; padding:8px 14px; display:flex; flex-direction:column; gap:4px; }
        .test-case-list.collapsed { display:none; }
        .test-case { display:flex; align-items:center; gap:6px; font-size:12px; padding:2px 0; }
        .test-icon { color:var(--accent-green); font-size:11px; }
        .mock-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(200px,1fr)); gap:8px; margin-top:8px; }
        .mock-card { background:var(--bg-card); border:1px solid var(--border-color); border-radius:var(--radius); padding:10px 12px; }
        /* ═══ Risk Items ═══ */
        .risk-item { background: var(--bg-card); border: 1px solid var(--border-color); border-radius: var(--radius); padding: 12px 16px; margin-bottom: 8px; }
        .risk-critical { border-left: 3px solid var(--accent-red); }
        .risk-warning { border-left: 3px solid var(--accent-orange); }
        .risk-info { border-left: 3px solid var(--accent-blue); }
        .risk-header { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
        .risk-desc { font-size: 12px; color: var(--text-secondary); margin-bottom: 4px; }
        .risk-file { font-size: 11px; color: var(--accent-orange); }
        /* ═══ Modal ═══ */
        .modal-overlay {
          display: none; position: fixed; inset: 0; z-index: 200;
          background: rgba(0,0,0,0.7); backdrop-filter: blur(4px);
          justify-content: center; align-items: center;
        }
        .modal-overlay.active { display: flex; }
        .modal-box {
          background: var(--bg-secondary); border: 1px solid var(--border-color);
          border-radius: 12px; width: min(90vw, 640px); max-height: 80vh;
          overflow-y: auto; animation: modalIn 0.2s ease;
        }
        @keyframes modalIn { from { opacity: 0; transform: scale(0.95) translateY(-10px); } to { opacity: 1; transform: scale(1) translateY(0); } }
        .modal-header { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px; border-bottom: 1px solid var(--border-color); }
        .modal-header h3 { font-size: 15px; font-weight: 600; }
        .modal-close { background: none; border: none; cursor: pointer; font-size: 18px; color: var(--text-secondary); padding: 2px 6px; border-radius: 4px; }
        .modal-close:hover { background: var(--bg-hover); color: var(--text-primary); }
        .modal-body { padding: 20px; }
        /* ═══ Search Highlight ═══ */
        .search-hidden { display: none !important; }
        mark.highlight { background: rgba(210,153,34,0.3); color: var(--accent-orange); border-radius: 2px; }
        /* ═══ Utilities ═══ */
        .text-secondary { color: var(--text-secondary); }
        .ecosystems { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 12px; }
        .link-external { color: var(--accent-blue); font-size: 12px; }
        .hidden { display: none !important; }
        /* ═══ Responsive ═══ */
        @media (max-width: 768px) {
          .sidebar { display: none; }
          .main-content { padding: 12px; }
          .stats-grid { grid-template-columns: repeat(3, 1fr); }
          .lang-layout { flex-direction: column; }
          .navbar-search { display: none; }
        }
        </style>
        """;

    private string GetInlineScripts() => """
        <script>
        // ═══ Mermaid CDN inline (minimal) ═══
        // Load Mermaid from CDN with fallback to inline rendering
        (function() {
          var script = document.createElement('script');
          script.src = 'https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js';
          script.onload = function() {
            mermaid.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'loose',
              themeVariables: { primaryColor: '#21262d', primaryTextColor: '#e6edf3', primaryBorderColor: '#30363d',
                lineColor: '#58a6ff', background: '#0d1117', nodeBorder: '#30363d' }
            });
            renderMermaidDiagrams();
          };
          script.onerror = function() { showMermaidFallback(); };
          document.head.appendChild(script);
        })();

        function renderMermaidDiagrams() {
          document.querySelectorAll('.mermaid').forEach(function(el) {
            var code = el.textContent.trim();
            if (!code) return;
            var id = 'mermaid_' + Math.random().toString(36).substr(2,9);
            try {
              mermaid.render(id, code).then(function(res) {
                el.innerHTML = res.svg;
                el.querySelectorAll('[id]').forEach(function(n) {
                  if (n.tagName !== 'SVG') n.style.cursor = 'pointer';
                  n.addEventListener('click', function(e) { handleNodeClick(e, n); });
                });
              }).catch(function(err) {
                el.innerHTML = '<pre style="color:var(--accent-orange);font-size:11px">⚠ 圖表渲染失敗\\n' + code.substring(0,200) + '</pre>';
              });
            } catch(e) {}
          });
        }

        function showMermaidFallback() {
          document.querySelectorAll('.mermaid').forEach(function(el) {
            el.innerHTML = '<pre style="font-size:11px;color:var(--text-secondary);overflow:auto">' + el.textContent.trim() + '</pre>';
          });
        }

        // ═══ Sidebar Toggle ═══
        var sidebarOpen = true;
        document.getElementById('sidebar-toggle').addEventListener('click', function() {
          sidebarOpen = !sidebarOpen;
          var sidebar = document.getElementById('sidebar');
          sidebar.classList.toggle('sidebar-collapsed', !sidebarOpen);
          this.textContent = sidebarOpen ? '◀' : '▶';
          this.title = sidebarOpen ? '收合側欄' : '展開側欄';
        });

        // ═══ Theme Toggle ═══
        var isDark = true;
        document.getElementById('theme-toggle').addEventListener('click', function() {
          isDark = !isDark;
          document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');
          this.textContent = isDark ? '🌙' : '☀️';
          // Re-render mermaid for theme change
          if (typeof mermaid !== 'undefined') {
            mermaid.initialize({ startOnLoad: false, theme: isDark ? 'dark' : 'default' });
            renderMermaidDiagrams();
          }
        });

        // ═══ Section Collapse ═══
        function toggleSection(id) {
          var section = document.getElementById('section-' + id);
          if (!section) return;
          section.classList.toggle('collapsed');
          var btn = section.querySelector('.collapse-btn');
          var icon = section.querySelector('.collapse-icon');
          var isCollapsed = section.classList.contains('collapsed');
          if (btn) btn.setAttribute('aria-expanded', String(!isCollapsed));
          if (icon) icon.textContent = isCollapsed ? '▶' : '▼';
          updateSidebarActive();
        }

        document.getElementById('collapse-all').addEventListener('click', function() {
          document.querySelectorAll('.section').forEach(function(s) {
            s.classList.add('collapsed');
            var icon = s.querySelector('.collapse-icon');
            if (icon) icon.textContent = '▶';
          });
        });

        document.getElementById('expand-all').addEventListener('click', function() {
          document.querySelectorAll('.section').forEach(function(s) {
            s.classList.remove('collapsed');
            var icon = s.querySelector('.collapse-icon');
            if (icon) icon.textContent = '▼';
          });
        });

        // ═══ Sidebar Active ═══
        function updateSidebarActive() {
          var sections = document.querySelectorAll('.section');
          var scrollY = window.scrollY + 80;
          var current = '';
          sections.forEach(function(s) {
            if (s.offsetTop <= scrollY) current = s.id.replace('section-','');
          });
          document.querySelectorAll('.nav-item').forEach(function(a) {
            a.classList.toggle('active', a.dataset.section === current);
          });
        }
        window.addEventListener('scroll', updateSidebarActive, { passive: true });

        document.querySelectorAll('.nav-item').forEach(function(a) {
          a.addEventListener('click', function(e) {
            e.preventDefault();
            var target = document.querySelector(this.getAttribute('href'));
            if (target) {
              if (target.classList.contains('collapsed')) toggleSection(target.id.replace('section-',''));
              target.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }
          });
        });

        // ═══ Sub-Tabs ═══
        function switchTab(btn, panelId) {
          var parent = btn.closest('.section-body') || btn.parentElement.parentElement;
          parent.querySelectorAll('.sub-tab').forEach(function(t) { t.classList.remove('active'); });
          parent.querySelectorAll('.sub-panel').forEach(function(p) { p.classList.add('hidden'); });
          btn.classList.add('active');
          var panel = document.getElementById(panelId);
          if (panel) {
            panel.classList.remove('hidden');
            // Render mermaid if present
            if (typeof mermaid !== 'undefined') {
              panel.querySelectorAll('.mermaid').forEach(function(el) {
                if (!el.querySelector('svg')) renderMermaidDiagrams();
              });
            }
          }
        }

        // ═══ Global Search ═══
        var searchDebounce;
        document.getElementById('global-search').addEventListener('input', function() {
          clearTimeout(searchDebounce);
          var q = this.value.trim().toLowerCase();
          searchDebounce = setTimeout(function() { globalSearch(q); }, 200);
        });

        function globalSearch(q) {
          if (!q) {
            document.querySelectorAll('.search-hidden').forEach(function(el) { el.classList.remove('search-hidden'); });
            document.querySelectorAll('.section').forEach(function(s) { s.classList.remove('collapsed'); });
            return;
          }
          document.querySelectorAll('.section').forEach(function(section) {
            var hasMatch = false;
            section.querySelectorAll('.searchable').forEach(function(row) {
              var text = (row.dataset.text || row.textContent).toLowerCase();
              var match = text.includes(q);
              row.classList.toggle('search-hidden', !match);
              if (match) hasMatch = true;
            });
            if (hasMatch && section.classList.contains('collapsed'))
              section.classList.remove('collapsed');
          });
        }

        // ═══ Table Filter ═══
        function filterTable(input) {
          var q = input.value.toLowerCase();
          var tableId = input.dataset.target;
          var table = document.getElementById(tableId);
          if (!table) return;
          table.querySelectorAll('tbody tr').forEach(function(row) {
            var text = (row.dataset.text || row.textContent).toLowerCase();
            row.classList.toggle('search-hidden', q && !text.includes(q));
          });
        }

        function filterApiTable(input) {
          var q = input.value ? input.value.toLowerCase() : '';
          var methodFilter = document.getElementById('api-method-filter')?.value || '';
          var tagFilter = document.getElementById('api-tag-filter')?.value || '';
          document.querySelectorAll('#api-table tbody tr').forEach(function(row) {
            var text = (row.dataset.text || '').toLowerCase();
            var tag = row.dataset.tag || '';
            var matchQ = !q || text.includes(q);
            var matchM = !methodFilter || text.startsWith(methodFilter.toLowerCase());
            var matchT = !tagFilter || tag === tagFilter;
            row.classList.toggle('search-hidden', !(matchQ && matchM && matchT));
          });
        }

        // ═══ Env Toggle ═══
        function toggleEnvValue(el) {
          var isHidden = el.textContent === '●●●●●●';
          el.textContent = isHidden ? (el.dataset.value || '(empty)') : '●●●●●●';
          el.style.color = isHidden ? 'var(--accent-orange)' : 'var(--text-secondary)';
        }

        var allEnvShown = false;
        function toggleAllEnv() {
          allEnvShown = !allEnvShown;
          document.querySelectorAll('.env-masked').forEach(function(el) {
            el.textContent = allEnvShown ? (el.dataset.value || '(empty)') : '●●●●●●';
            el.style.color = allEnvShown ? 'var(--accent-orange)' : 'var(--text-secondary)';
          });
        }

        // ═══ File Tree Toggle ═══
        function toggleFtNode(id) {
          var el = document.getElementById(id);
          if (el) el.classList.toggle('collapsed');
        }

        // ═══ API Detail New Tab ═══
        function openApiDetailTab(endpoint, allData) {
          var ep = typeof endpoint === 'string' ? JSON.parse(endpoint) : endpoint;
          var trace = (allData && allData.apiTraces || []).find(function(t) {
            return t.method === ep.method && t.path === ep.path;
          });

          var methodColor = {'GET':'#3fb950','POST':'#58a6ff','PUT':'#d29922','DELETE':'#f85149','PATCH':'#bc8cff'}[ep.method] || '#8b949e';

          var paramsHtml = '';
          if (ep.parameters && ep.parameters.length) {
            paramsHtml = '<table class="dt-table"><thead><tr><th>名稱</th><th>位置</th><th>類型</th><th>必填</th><th>說明</th></tr></thead><tbody>';
            ep.parameters.forEach(function(p) {
              paramsHtml += '<tr><td><code>'+escHtml(p.name)+'</code></td><td>'+escHtml(p.location)+'</td><td>'+escHtml(p.type)+'</td><td>'+(p.required?'✅':'')+'</td><td>'+escHtml(p.description||'')+'</td></tr>';
            });
            paramsHtml += '</tbody></table>';
          }

          var responsesHtml = '';
          if (ep.responses && ep.responses.length) {
            responsesHtml = '<table class="dt-table"><thead><tr><th>狀態碼</th><th>說明</th></tr></thead><tbody>';
            ep.responses.forEach(function(r) {
              responsesHtml += '<tr><td><span class="dt-badge '+(r.statusCode.startsWith('2')?'dt-badge-green':'dt-badge-red')+'">'+escHtml(r.statusCode)+'</span></td><td>'+escHtml(r.description)+'</td></tr>';
            });
            responsesHtml += '</tbody></table>';
          }

          // Build trace HTML
          var traceHtml = '<p style="color:#8b949e;font-size:13px">⚠ 未偵測到此 API 的執行路徑。請確認原始碼中有對應的 handler 函式。</p>';
          var logicHtml = '<p style="color:#8b949e;font-size:13px">請先確認執行路徑。</p>';
          var sqlHtml = '<p style="color:#8b949e;font-size:13px">未偵測到 SQL 語法。</p>';

          if (trace && trace.steps && trace.steps.length > 0) {
            // Build Mermaid sequence diagram
            var mermaidCode = 'sequenceDiagram\n  autonumber\n';
            var layers = trace.steps.map(function(s){ return s.layer; });
            [...new Set(layers)].forEach(function(l) { mermaidCode += '  participant '+l+'\n'; });
            for (var i = 0; i < trace.steps.length - 1; i++) {
              var s = trace.steps[i], next = trace.steps[i+1];
              mermaidCode += '  '+s.layer+'->>'+next.layer+': '+escHtml(s.function||'')+'\n';
              mermaidCode += '  '+next.layer+'-->>'+s.layer+': return\n';
            }

            traceHtml = '<div id="dt-mermaid-container"><div class="mermaid">'+mermaidCode+'</div></div>';

            logicHtml = '<div class="dt-steps">';
            trace.steps.forEach(function(step, idx) {
              var layerColor = {'Handler':'#58a6ff','Service':'#3fb950','Repository':'#d29922','External':'#bc8cff'}[step.layer]||'#8b949e';
              logicHtml += '<div class="dt-step"><div class="dt-step-num" style="background:'+layerColor+'">'+step.order+'</div>';
              logicHtml += '<div class="dt-step-body"><div class="dt-step-layer" style="color:'+layerColor+'">'+escHtml(step.layer)+'</div>';
              logicHtml += '<code class="dt-step-file">'+escHtml(step.file)+'</code>';
              logicHtml += '<div class="dt-step-fn">函式: <strong>'+escHtml(step.function)+'</strong></div>';
              if (step.description) logicHtml += '<div class="dt-step-desc">'+escHtml(step.description)+'</div>';
              logicHtml += '</div></div>';
            });
            logicHtml += '</div>';
          }

          if (trace && trace.sqlQueries && trace.sqlQueries.length > 0) {
            sqlHtml = '<div class="dt-sql-list">';
            trace.sqlQueries.forEach(function(q) {
              var opColor = {'SELECT':'#3fb950','INSERT':'#58a6ff','UPDATE':'#d29922','DELETE':'#f85149'}[q.operation]||'#8b949e';
              sqlHtml += '<div class="dt-sql-item">';
              sqlHtml += '<div class="dt-sql-header"><span class="dt-badge" style="background:'+opColor+'22;color:'+opColor+';border:1px solid '+opColor+'44">'+escHtml(q.operation)+'</span> ';
              sqlHtml += '<strong>'+escHtml(q.name)+'</strong> <span style="color:#8b949e;font-size:11px">'+escHtml(q.sourceFile)+'</span></div>';
              sqlHtml += '<pre class="dt-sql-code">'+escHtml(q.rawSql)+'</pre>';
              sqlHtml += '</div>';
            });
            sqlHtml += '</div>';
          }

          var html = '<!DOCTYPE html><html lang="zh-TW" data-theme="dark"><head><meta charset="UTF-8"><title>'+escHtml(ep.method)+' '+escHtml(ep.path)+'</title>'
            + '<style>'+getDtStyles()+'</style></head><body>'
            + '<div class="dt-navbar"><span class="dt-back" onclick="window.close()">✕ 關閉</span>'
            + '<span class="dt-method" style="background:'+methodColor+'22;color:'+methodColor+';border:1px solid '+methodColor+'44">'+escHtml(ep.method)+'</span>'
            + '<span class="dt-path">'+escHtml(ep.path)+'</span>'
            + (ep.tag ? '<span class="dt-tag">'+escHtml(ep.tag)+'</span>' : '')
            + '</div>'
            + '<div class="dt-tabs"><button class="dt-tab active" onclick="switchDtTab(this,\'dt-overview\')">概覽</button>'
            + '<button class="dt-tab" onclick="switchDtTab(this,\'dt-trace\')">📍 執行路徑</button>'
            + '<button class="dt-tab" onclick="switchDtTab(this,\'dt-logic\')">🔧 執行邏輯</button>'
            + '<button class="dt-tab" onclick="switchDtTab(this,\'dt-sql\')">🗃️ SQL 語法</button>'
            + '</div>'
            + '<div class="dt-content">'
            + '<div id="dt-overview" class="dt-panel">'
            + (ep.summary ? '<p class="dt-summary">'+escHtml(ep.summary)+'</p>' : '')
            + (ep.description ? '<p style="color:#8b949e;margin-bottom:16px">'+escHtml(ep.description)+'</p>' : '')
            + (paramsHtml ? '<h3 class="dt-section-title">請求參數</h3>'+paramsHtml : '')
            + (responsesHtml ? '<h3 class="dt-section-title" style="margin-top:20px">回應</h3>'+responsesHtml : '')
            + '</div>'
            + '<div id="dt-trace" class="dt-panel hidden">'+traceHtml+'</div>'
            + '<div id="dt-logic" class="dt-panel hidden">'+logicHtml+'</div>'
            + '<div id="dt-sql" class="dt-panel hidden">'+sqlHtml+'</div>'
            + '</div>'
            + '<script>'
            + 'function switchDtTab(btn,panelId){'
            + 'document.querySelectorAll(".dt-tab").forEach(function(t){t.classList.remove("active")});'
            + 'document.querySelectorAll(".dt-panel").forEach(function(p){p.classList.add("hidden")});'
            + 'btn.classList.add("active");'
            + 'var panel=document.getElementById(panelId);if(panel){panel.classList.remove("hidden");}'
            + 'if(panelId==="dt-trace"&&typeof mermaid!=="undefined"){mermaid.init(undefined,document.querySelectorAll(".mermaid"));}}'
            + '(function(){'
            + 'var s=document.createElement("script");'
            + 's.src="https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js";'
            + 's.onload=function(){mermaid.initialize({startOnLoad:true,theme:"dark"});};'
            + 'document.head.appendChild(s);})();'
            + '<\/script>'
            + '</body></html>';

          var w = window.open('', '_blank');
          if (w) { w.document.open(); w.document.write(html); w.document.close(); }
        }

        function getDtStyles() {
          return ':root{--bg:#0d1117;--bg2:#161b22;--card:#21262d;--border:#30363d;--text:#e6edf3;--muted:#8b949e;--blue:#58a6ff;--green:#3fb950;--font:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;--mono:"SFMono-Regular",Consolas,monospace}'
            + '*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}'
            + 'body{font-family:var(--font);background:var(--bg);color:var(--text);min-height:100vh}'
            + 'code{font-family:var(--mono);font-size:12px;background:var(--card);padding:2px 6px;border-radius:4px;color:var(--blue)}'
            + '.dt-navbar{position:sticky;top:0;z-index:10;background:var(--bg2);border-bottom:1px solid var(--border);padding:12px 20px;display:flex;align-items:center;gap:12px}'
            + '.dt-back{cursor:pointer;color:var(--muted);font-size:14px;padding:4px 8px;border-radius:4px;background:var(--card);border:1px solid var(--border)}'
            + '.dt-back:hover{color:var(--text)}'
            + '.dt-method{padding:3px 10px;border-radius:12px;font-size:12px;font-weight:700}'
            + '.dt-path{font-family:var(--mono);font-size:14px;font-weight:600}'
            + '.dt-tag{padding:2px 8px;border-radius:12px;font-size:11px;background:rgba(88,166,255,.15);color:var(--blue);border:1px solid rgba(88,166,255,.3)}'
            + '.dt-tabs{display:flex;gap:2px;padding:0 20px;background:var(--bg2);border-bottom:1px solid var(--border)}'
            + '.dt-tab{background:none;border:none;border-bottom:2px solid transparent;padding:10px 16px;cursor:pointer;font-size:13px;color:var(--muted);margin-bottom:-1px}'
            + '.dt-tab:hover{color:var(--text)}'
            + '.dt-tab.active{color:var(--blue);border-bottom-color:var(--blue);font-weight:600}'
            + '.dt-content{max-width:1200px;margin:0 auto;padding:24px 20px}'
            + '.dt-panel.hidden{display:none}'
            + '.dt-summary{font-size:15px;margin-bottom:12px;line-height:1.6}'
            + '.dt-section-title{font-size:13px;font-weight:600;margin-bottom:8px;color:var(--muted);text-transform:uppercase;letter-spacing:.5px}'
            + '.dt-table{width:100%;border-collapse:collapse;font-size:13px}'
            + '.dt-table th{background:var(--card);padding:8px 12px;text-align:left;font-size:11px;color:var(--muted);border-bottom:1px solid var(--border);text-transform:uppercase}'
            + '.dt-table td{padding:8px 12px;border-bottom:1px solid var(--border)}'
            + '.dt-table tr:hover td{background:var(--card)}'
            + '.dt-badge{display:inline-block;padding:2px 8px;border-radius:12px;font-size:11px;font-weight:700}'
            + '.dt-badge-green{background:rgba(63,185,80,.15);color:#3fb950;border:1px solid rgba(63,185,80,.3)}'
            + '.dt-badge-red{background:rgba(248,81,73,.15);color:#f85149;border:1px solid rgba(248,81,73,.3)}'
            + '.dt-steps{display:flex;flex-direction:column;gap:12px}'
            + '.dt-step{display:flex;gap:14px;align-items:flex-start}'
            + '.dt-step-num{width:28px;height:28px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;color:#fff;flex-shrink:0}'
            + '.dt-step-body{flex:1;background:var(--card);border:1px solid var(--border);border-radius:8px;padding:12px 16px}'
            + '.dt-step-layer{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;margin-bottom:4px}'
            + '.dt-step-file{font-size:11px;display:block;margin-bottom:4px}'
            + '.dt-step-fn{font-size:13px;margin-bottom:2px}'
            + '.dt-step-desc{font-size:12px;color:var(--muted)}'
            + '.dt-sql-list{display:flex;flex-direction:column;gap:16px}'
            + '.dt-sql-item{background:var(--card);border:1px solid var(--border);border-radius:8px;overflow:hidden}'
            + '.dt-sql-header{padding:10px 16px;display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--border)}'
            + '.dt-sql-code{padding:16px;font-family:var(--mono);font-size:12px;line-height:1.7;overflow-x:auto;color:#e6edf3;white-space:pre}'
            + '::-webkit-scrollbar{width:6px;height:6px}::-webkit-scrollbar-track{background:var(--bg2)}::-webkit-scrollbar-thumb{background:var(--border);border-radius:3px}'
            + '.mermaid svg{max-width:100%}';
        }

        // ═══ Modal ═══
        function showApiDetail(endpoint) {
          var ep = typeof endpoint === 'string' ? JSON.parse(endpoint) : endpoint;
          document.getElementById('modal-title').textContent = ep.method + ' ' + ep.path;
          var html = '';
          if (ep.summary) html += '<p style="margin-bottom:12px">' + escHtml(ep.summary) + '</p>';
          if (ep.description) html += '<p class="text-secondary" style="margin-bottom:12px">' + escHtml(ep.description) + '</p>';
          if (ep.parameters && ep.parameters.length) {
            html += '<h4 style="margin-bottom:8px;font-size:13px">參數</h4><table class="data-table"><thead><tr><th>名稱</th><th>位置</th><th>類型</th><th>必填</th><th>說明</th></tr></thead><tbody>';
            ep.parameters.forEach(function(p) {
              html += '<tr><td><code>' + escHtml(p.name) + '</code></td><td>' + escHtml(p.location) + '</td><td>' + escHtml(p.type) + '</td><td>' + (p.required ? '✅' : '') + '</td><td>' + escHtml(p.description||'') + '</td></tr>';
            });
            html += '</tbody></table>';
          }
          if (ep.responses && ep.responses.length) {
            html += '<h4 style="margin:12px 0 8px;font-size:13px">回應</h4><table class="data-table"><thead><tr><th>狀態碼</th><th>說明</th></tr></thead><tbody>';
            ep.responses.forEach(function(r) {
              html += '<tr><td><span class="tag ' + (r.statusCode.startsWith('2') ? 'tag-green' : 'tag-red') + '">' + escHtml(r.statusCode) + '</span></td><td>' + escHtml(r.description) + '</td></tr>';
            });
            html += '</tbody></table>';
          }
          document.getElementById('modal-body').innerHTML = html;
          openModal();
        }

        function showContainerDetail(container) {
          var c = typeof container === 'string' ? JSON.parse(container) : container;
          document.getElementById('modal-title').textContent = '🐳 ' + c.name;
          var html = '<div class="meta-grid">';
          html += '<div class="meta-row"><span class="meta-key">映像檔</span><code>' + escHtml(c.image) + '</code></div>';
          if (c.restartPolicy) html += '<div class="meta-row"><span class="meta-key">重啟策略</span><span>' + escHtml(c.restartPolicy) + '</span></div>';
          if (c.dependsOn && c.dependsOn.length) html += '<div class="meta-row"><span class="meta-key">依賴服務</span><span>' + c.dependsOn.map(function(d){return '<span class="tag tag-purple">'+escHtml(d)+'</span>';}).join(' ') + '</span></div>';
          html += '</div>';
          if (c.ports && c.ports.length) {
            html += '<h4 style="margin:12px 0 8px;font-size:13px">Port 映射</h4><table class="data-table"><thead><tr><th>Host</th><th>Container</th><th>協定</th></tr></thead><tbody>';
            c.ports.forEach(function(p) { html += '<tr><td>' + p.hostPort + '</td><td>' + p.containerPort + '</td><td>' + escHtml(p.protocol) + '</td></tr>'; });
            html += '</tbody></table>';
          }
          if (c.envVariables && c.envVariables.length) {
            html += '<h4 style="margin:12px 0 8px;font-size:13px">環境變數 (' + c.envVariables.length + ')</h4><table class="data-table"><thead><tr><th>Key</th><th>Value</th></tr></thead><tbody>';
            c.envVariables.slice(0,20).forEach(function(e) {
              html += '<tr><td><code>' + escHtml(e.key) + '</code></td><td>' + (e.isSensitive ? '<span class="tag tag-red">●●●●</span>' : '<code>' + escHtml(e.value) + '</code>') + '</td></tr>';
            });
            html += '</tbody></table>';
          }
          document.getElementById('modal-body').innerHTML = html;
          openModal();
        }

        function handleNodeClick(e, node) {
          var label = node.textContent || node.getAttribute('aria-label') || '';
          if (!label.trim()) return;
          document.getElementById('modal-title').textContent = '節點詳情';
          document.getElementById('modal-body').innerHTML = '<p><strong>' + escHtml(label.trim()) + '</strong></p>';
          openModal();
        }

        function openModal() {
          var overlay = document.getElementById('detail-modal');
          overlay.classList.add('active');
          overlay.setAttribute('aria-hidden', 'false');
          document.addEventListener('keydown', handleEscape);
        }

        function closeModal(e) {
          if (e && e.target !== document.getElementById('detail-modal')) return;
          var overlay = document.getElementById('detail-modal');
          overlay.classList.remove('active');
          overlay.setAttribute('aria-hidden', 'true');
          document.removeEventListener('keydown', handleEscape);
        }

        function handleEscape(e) { if (e.key === 'Escape') closeModal(); }

        // ═══ Utils ═══
        function escHtml(str) {
          if (!str) return '';
          return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
        }

        // ═══ Console Error Prevention ═══
        window.addEventListener('error', function(e) {
          console.warn('[RID] 已捕獲錯誤：', e.message);
          return true;
        });

        // ═══ Init ═══
        document.addEventListener('DOMContentLoaded', function() {
          updateSidebarActive();
          console.info('[Repo Insight Dashboard] 版本 1.0.0 — 載入完成 ✅');
        });
        </script>
        """;
}
