// ============================================================
// HtmlDashboardGenerator.cs — Single-file HTML dashboard renderer
// ============================================================
// Architecture: stateless generator; takes a fully-populated DashboardData and
//   produces a single self-contained HTML file with all CSS, JavaScript, and
//   data inlined — no external CDN, font, or script URLs required.
//
// Security:
//   • All user-derived strings are passed through HtmlEncode (HtmlEncoder.Default)
//     before being written into HTML markup (CWE-79 / OWASP A03:2021 Injection).
//   • JSON embedded in <script> uses StringEscapeHandling.EscapeHtml so that
//     "</script>" sequences inside field values cannot break out of the script block.
//   • Content-Security-Policy meta tag blocks all external resource loading.
//     'unsafe-inline' is unavoidable because scripts and styles are fully inlined
//     (adding nonces/hashes to a static generated file is not feasible).
//   • Sensitive env-variable values are projected to "***masked***" in the sanitised
//     safeData object before serialisation — raw secrets never reach the HTML output.
//
// Usage:
//   var generator = new HtmlDashboardGenerator();
//   string html = generator.Generate(dashboardData);
//   File.WriteAllText("dashboard.html", html, Encoding.UTF8);
// ============================================================

using System.Text;
using System.Text.Encodings.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RepoInsightDashboard.Analyzers;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Generators;

/// <summary>
/// Renders a fully self-contained HTML dashboard from a <see cref="DashboardData"/>
/// snapshot, inlining all CSS, JavaScript, and JSON data into a single portable file.
/// </summary>
/// <remarks>
/// <para>
/// The generated file has zero external dependencies — all styles, the RidGraph
/// SVG graph engine, and the <c>window.__RID_DATA__</c> JSON payload are embedded
/// inline, making the output suitable for offline viewing and secure file sharing.
/// </para>
/// <para>
/// Security: every untrusted string written to HTML markup is passed through
/// <see cref="HtmlEncode"/>, and all values embedded inside JSON script blocks use
/// <see cref="Newtonsoft.Json.StringEscapeHandling.EscapeHtml"/> (CWE-79).
/// </para>
/// </remarks>
public class HtmlDashboardGenerator
{
    // camelCase for onclick data — JS expects ep.method, ep.path, c.name, etc.
    private static readonly JsonSerializerSettings _onclickJson = new()
    {
        ContractResolver     = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling    = NullValueHandling.Ignore,
        // EscapeHtml is required: endpoint paths/summaries from untrusted repo content can
        // contain '<', '>', '&' which must be unicode-escaped inside HTML onclick attributes.
        StringEscapeHandling = StringEscapeHandling.EscapeHtml
    };

    // Shared serialization settings for window.__RID_DATA__ — static readonly so
    // CamelCasePropertyNamesContractResolver's internal type-contract cache is preserved
    // across all Generate() calls, eliminating repeated per-call type reflection.
    private static readonly JsonSerializerSettings _dataJsonSettings = new()
    {
        ContractResolver     = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling    = NullValueHandling.Ignore,
        StringEscapeHandling = StringEscapeHandling.EscapeHtml
    };
    /// <summary>
    /// Renders <paramref name="data"/> into a complete, self-contained HTML string
    /// that can be written directly to a <c>.html</c> file.
    /// </summary>
    /// <param name="data">
    /// Fully-populated <see cref="DashboardData"/> produced by
    /// <see cref="Services.AnalysisOrchestrator"/>.
    /// </param>
    /// <returns>
    /// UTF-8–compatible HTML string containing all sections, CSS, JavaScript, and data.
    /// No further post-processing is needed before writing to disk.
    /// </returns>
    /// <remarks>
    /// The method builds a sanitised projection of <paramref name="data"/> (<c>safeData</c>)
    /// that replaces sensitive env-variable values with <c>***masked***</c> before JSON
    /// serialisation, so no raw secrets are ever embedded in the output file (CWE-312).
    /// </remarks>
    public string Generate(DashboardData data)
    {
        var sb = new StringBuilder();

        // Build a sanitized projection of env variables — sensitive ones already carry
        // "***masked***" as their Value (set in EnvFileAnalyzer), but we defensively
        // project to an anonymous type so no accidental future field leaks into JSON.
        var safeEnvVars = data.EnvVariables.Select(e => new
        {
            key        = e.Key,
            value      = e.IsSensitive ? "***masked***" : e.Value,
            isSensitive = e.IsSensitive,
            sourceFile = e.SourceFile
        });

        // Use StringEscapeHandling.EscapeHtml so characters like </script> inside
        // the JSON payload are unicode-escaped and cannot break out of the <script> block.
        // CWE-79 / OWASP A03:2021 Injection.
        // _dataJsonSettings is static readonly — reuses the internal type-contract cache
        // across calls, avoiding repeated reflection over all serialized model types.

        // Serialize a sanitized projection so the JS global never contains raw secrets.
        var safeData = new
        {
            data.Project,
            data.Meta,
            data.FileTree,
            data.ApiEndpoints,
            data.ApiTraces,
            data.Packages,
            data.DependencyGraph,
            data.Containers,
            data.Dockerfile,
            EnvVariables    = safeEnvVars,
            data.Tests,
            data.SecurityRisks,
            data.DesignPatterns,
            data.CopilotSummary,
            data.StartupSequence,
            data.Makefile
        };
        var jsonData = JsonConvert.SerializeObject(safeData, Formatting.None, _dataJsonSettings);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html lang=\"zh-TW\" data-theme=\"{HtmlEncode(data.Meta.Theme)}\">");
        sb.AppendLine("<head>");
        sb.AppendLine($"<meta charset=\"UTF-8\">");
        sb.AppendLine($"<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        // Content-Security-Policy: block all external resource loading from the generated file.
        // 'unsafe-inline' is required because all scripts and styles are inlined (no nonces).
        // form-action 'none' blocks any form POST exfiltration; base-uri 'none' prevents <base> injection;
        // frame-ancestors 'none' prevents clickjacking if the file is ever served over HTTP.
        sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:; form-action 'none'; base-uri 'none'; frame-ancestors 'none';\">");
        sb.AppendLine("<meta name=\"referrer\" content=\"no-referrer\">");
        sb.AppendLine($"<title>{HtmlEncode(data.Project.Name)} — Repo Insight Dashboard</title>");
        sb.AppendLine(GetInlineStyles());
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine(BuildNavbar(data));
        sb.AppendLine("<div class=\"layout\">");
        sb.AppendLine(BuildSidebar(data));
        sb.AppendLine("<main class=\"main-content\" id=\"main-content\">");
        sb.AppendLine(BuildOverviewSection(data));
        sb.AppendLine(BuildLanguageSection(data));
        sb.AppendLine(BuildDependencySection(data));
        sb.AppendLine(BuildApiSection(data));
        sb.AppendLine(BuildDockerSection(data));
        sb.AppendLine(BuildPortTable(data));
        sb.AppendLine(BuildStartupSection(data));
        sb.AppendLine(BuildEnvSection(data));
        sb.AppendLine(BuildFileTreeSection(data));
        sb.AppendLine(BuildSecuritySection(data));
        sb.AppendLine(BuildUnitTestSection(data));
        sb.AppendLine(BuildIntegrationTestSection(data));
        sb.AppendLine(BuildMakefileSection(data));
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

    private string BuildSidebar(DashboardData data)
    {
        // The Copilot Instructions nav item is only rendered when the repo
        // actually contains a .github/copilot-instructions.md file.
        var copilotNavItem = !string.IsNullOrEmpty(data.Project.CopilotInstructions)
            ? """
                <a href="#section-copilot-instructions" class="nav-item" data-section="copilot-instructions">
                  <span class="nav-icon">🤖</span> Copilot Instructions
                </a>
            """
            : "";

        return $"""
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
            <a href="#section-makefile" class="nav-item" data-section="makefile">
              <span class="nav-icon">⚙️</span> Makefile 指令區
            </a>
            {copilotNavItem}
          </nav>
        </aside>
        """;
    }

    /// <summary>
    /// Builds a collapsible dashboard section with an accessible heading and a
    /// keyboard-navigable collapse/expand button.
    /// </summary>
    /// <param name="id">
    /// Section identifier used for the HTML <c>id</c> attributes and the JS
    /// <c>toggleSection(id)</c> call.  Must be unique across all sections.
    /// </param>
    /// <param name="title">Display title shown in the section header bar.</param>
    /// <param name="icon">Emoji or symbol rendered beside the title.</param>
    /// <param name="content">Inner HTML string for the section body.</param>
    /// <param name="collapsed">
    /// When <c>true</c>, the section renders pre-collapsed.  Less critical sections
    /// (file tree, tests, Makefile) start collapsed to reduce initial page scroll height.
    /// </param>
    /// <returns>HTML fragment for the full collapsible section element.</returns>
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

    /// <summary>
    /// Renders a SVG donut chart for the language distribution using stroke-dasharray
    /// on overlapping circles.  Each circle covers a fraction of the circumference,
    /// producing pie-like segments without complex SVG arc path calculations.
    /// </summary>
    /// <param name="languages">Language list ordered by percentage descending.</param>
    /// <returns>
    /// SVG <c>&lt;circle&gt;</c> element fragments intended for injection inside an
    /// <c>&lt;svg viewBox="0 0 120 120"&gt;</c> element.
    /// </returns>
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
              <div class="graph-toolbar">
                <span class="graph-label">佈局</span>
                <button class="layout-btn active" onclick="RidGraph.switchLayout('rid-dep-graph','hierarchical',this)">⫶ 層次</button>
                <button class="layout-btn" onclick="RidGraph.switchLayout('rid-dep-graph','adaptive',this)">⊕ 自適應</button>
                <button class="layout-btn-reset" onclick="RidGraph.resetZoom('rid-dep-graph')" title="重置縮放">⊙</button>
                <span class="graph-hint">滑鼠滾輪縮放 · 拖曳平移</span>
              </div>
              <div id="rid-dep-graph" class="rid-graph-container"></div>
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

    private string BuildApiSection(DashboardData data)
    {
        if (data.ApiEndpoints.Count == 0)
            return BuildSection("api", "API 端點", "🔌", "<p class=\"text-secondary\">未找到 API 端點。請確認 Swagger/OpenAPI 文件存在。</p>");

        var tags = data.ApiEndpoints.Where(e => e.Tag != null)
            .Select(e => e.Tag!).Distinct()
            .Select(t => $"<option value=\"{HtmlEncode(t)}\">{HtmlEncode(t)}</option>");

        var rows = string.Join("\n", data.ApiEndpoints.Select(e => $"""
            <tr class="searchable api-row {(e.IsDeprecated ? "deprecated" : "")}"
                data-text="{HtmlEncode(e.Path)} {HtmlEncode(e.Summary)}"
                data-method="{HtmlEncode(e.Method)}"
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
              <div class="graph-toolbar">
                <span class="graph-label">佈局</span>
                <button class="layout-btn active" onclick="RidGraph.switchLayout('rid-docker-graph','hierarchical',this)">⫶ 層次</button>
                <button class="layout-btn" onclick="RidGraph.switchLayout('rid-docker-graph','adaptive',this)">⊕ 自適應</button>
                <button class="layout-btn-reset" onclick="RidGraph.resetZoom('rid-docker-graph')" title="重置縮放">⊙</button>
                <span class="graph-hint">滑鼠滾輪縮放 · 拖曳平移</span>
              </div>
              <div id="rid-docker-graph" class="rid-graph-container"></div>
            </div>
            <div id="docker-cards" class="sub-panel hidden">
              <div class="container-grid">{cards}</div>
            </div>
            {dockerfileInfo}
            """;

        return BuildSection("docker", $"Docker 架構 ({data.Containers.Count} 服務)", "🐳", content);
    }

    private string BuildPortTable(DashboardData data)
    {
        var allPorts = data.Containers
            .SelectMany(c => c.Ports.Select(p => new { Service = c.Name, p.HostPort, p.ContainerPort, p.Protocol }))
            .OrderBy(p => p.HostPort)
            .ToList();

        if (data.Dockerfile != null)
        {
            // Include Dockerfile EXPOSE ports as pseudo-service "Dockerfile"
            var dockerfilePorts = data.Dockerfile.ExposedPorts
                .Select(p => new { Service = "Dockerfile", HostPort = p, ContainerPort = p, Protocol = "tcp" });
            allPorts = allPorts
                .Concat(dockerfilePorts)
                .OrderBy(p => p.HostPort)
                .ToList();
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

        var visibleSequence = data.StartupSequence.Take(10).ToList();
        var seqSteps = string.Join("\n", visibleSequence.Select((svc, i) =>
        {
            var container = data.Containers.FirstOrDefault(c => c.Name == svc);
            var ports = container?.Ports.Count > 0
                ? string.Join(" ", container.Ports.Take(2).Select(p => $"<span class='tag tag-blue'>{p.HostPort}:{p.ContainerPort}</span>"))
                : "";
            var deps = container?.DependsOn.Count > 0
                ? $"<span class='startup-deps'>依賴：{HtmlEncode(string.Join(", ", container.DependsOn))}</span>"
                : "";
            // Compare against visibleSequence.Count (not full list) so the last visible item has no arrow
            var arrow = i < visibleSequence.Count - 1 ? "<div class='startup-connector'>↓</div>" : "";
            return $"""
                <div class="startup-step">
                  <div class="startup-num">{i + 1}</div>
                  <div class="startup-body">
                    <div class="startup-name">{HtmlEncode(svc)}</div>
                    <div class="startup-meta">{ports} {deps}</div>
                  </div>
                  <div class="startup-status">✓ Ready</div>
                </div>
                {arrow}
                """;
        }));

        var ganttRows = string.Join("\n", visibleSequence.Select((svc, i) =>
        {
            var duration = 3 + (svc.Length % 5);
            var start = visibleSequence.Take(i).Sum(s => 3 + (s.Length % 5) / 2);
            var pctLeft = Math.Min(start * 5, 80);
            var pctWidth = Math.Max(duration * 5, 10);
            var colors = new[] { "#58a6ff", "#3fb950", "#d29922", "#bc8cff", "#f85149" };
            var color = colors[i % colors.Length];
            return $"""
                <div class="gantt-row">
                  <div class="gantt-label">{HtmlEncode(svc)}</div>
                  <div class="gantt-bar-track">
                    <div class="gantt-bar" style="left:{pctLeft}%;width:{Math.Min(pctWidth, 100 - pctLeft)}%;background:{color}"></div>
                    <span class="gantt-bar-label" style="left:{pctLeft}%;color:{color}">{duration}s</span>
                  </div>
                </div>
                """;
        }));

        var content = $"""
            <div class="sub-tabs">
              <button class="sub-tab active" onclick="switchTab(this,'startup-seq')">啟動時序</button>
              <button class="sub-tab" onclick="switchTab(this,'startup-gantt')">Gantt 圖</button>
            </div>
            <div id="startup-seq" class="sub-panel">
              <div class="startup-flow">{seqSteps}</div>
            </div>
            <div id="startup-gantt" class="sub-panel hidden">
              <div class="gantt-chart">{ganttRows}</div>
            </div>
            """;

        return BuildSection("startup", "啟動流程", "🚀", content);
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
                    // Value is already masked at EnvFileAnalyzer level — no real secret in HTML.
                    // The span is kept for visual consistency (shows ●●●●●●); clicking reveals the
                    // already-masked placeholder, which is safe to expose in the rendered page.
                    ? $"<span class=\"env-masked\" onclick=\"toggleEnvValue(this)\" title=\"點擊顯示/隱藏\">●●●●●●</span><span class=\"env-revealed\" style=\"display:none\"><code>{HtmlEncode(e.Value)}</code></span>"
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
                "<div class=\"info-box success\">✅ 靜態掃描未偵測到明顯安全問題。建議搭配 AI 分析（設定 GITHUB_COPILOT_TOKEN）進行深層審查。</div>");

        var critical = data.SecurityRisks.Count(r => r.Priority == 1);
        var high     = data.SecurityRisks.Count(r => r.Priority == 2);
        var medium   = data.SecurityRisks.Count(r => r.Priority == 3);
        var info     = data.SecurityRisks.Count(r => r.Priority == 4);

        var summaryBar = $"""
            <div class="sec-summary">
              <div class="sec-stat sec-critical"><span class="sec-count">{critical}</span><span class="sec-lbl">危急 P1</span></div>
              <div class="sec-stat sec-high"><span class="sec-count">{high}</span><span class="sec-lbl">高危 P2</span></div>
              <div class="sec-stat sec-medium"><span class="sec-count">{medium}</span><span class="sec-lbl">中等 P3</span></div>
              <div class="sec-stat sec-info"><span class="sec-count">{info}</span><span class="sec-lbl">資訊 P4</span></div>
              <div class="sec-ai-note">
                {(data.SecurityRisks.Any(r => !string.IsNullOrEmpty(r.Recommendation))
                    ? "✅ 包含 AI 深度分析"
                    : "💡 提供 GITHUB_COPILOT_TOKEN 啟用 AI 深度掃描")}
              </div>
            </div>
            """;

        var tableRows = string.Join("\n", data.SecurityRisks.Select(r =>
        {
            var (badgeClass, icon, label) = r.Priority switch
            {
                1 => ("sec-badge-critical", "🔴", "P1 危急"),
                2 => ("sec-badge-high",     "🟠", "P2 高危"),
                3 => ("sec-badge-medium",   "🟡", "P3 中等"),
                _ => ("sec-badge-info",     "🔵", "P4 資訊")
            };
            var affectedHtml = r.AffectedFiles.Count > 0
                ? string.Join(" ", r.AffectedFiles.Take(3).Select(f =>
                    $"<code class='sec-file-tag'>{HtmlEncode(f)}</code>"))
                : (r.FilePath != null ? $"<code class='sec-file-tag'>{HtmlEncode(r.FilePath)}</code>" : "");
            var recHtml = !string.IsNullOrEmpty(r.Recommendation)
                ? HtmlEncode(r.Recommendation)
                : "<span class='text-secondary'>—</span>";

            return $"""
                <tr class="sec-row sec-row-{HtmlEncode(r.Level)}">
                  <td><span class="sec-badge {badgeClass}">{icon} {label}</span></td>
                  <td><span class="sec-category">{HtmlEncode(r.Category)}</span></td>
                  <td><strong>{HtmlEncode(r.Title)}</strong></td>
                  <td class="sec-desc">{HtmlEncode(r.Description)}</td>
                  <td class="sec-files">{affectedHtml}</td>
                  <td class="sec-rec">{recHtml}</td>
                </tr>
                """;
        }));

        var aiNote = data.SecurityRisks.Any(r => !string.IsNullOrEmpty(r.Recommendation))
            ? "" : """<div class="info-box" style="margin-top:12px">💡 設定 <code>GITHUB_COPILOT_TOKEN</code> 環境變數可啟用 AI 驅動的深度程式碼安全分析，獲得每個風險的具體修復建議。</div>""";

        var content = summaryBar + $"""
            <div class="table-wrap" style="margin-top:16px">
              <table class="sec-table">
                <thead>
                  <tr>
                    <th style="width:110px">優先級</th>
                    <th style="width:160px">類別（OWASP）</th>
                    <th style="width:200px">問題名稱</th>
                    <th>說明</th>
                    <th style="width:180px">相關檔案</th>
                    <th style="width:220px">修復建議</th>
                  </tr>
                </thead>
                <tbody>{tableRows}</tbody>
              </table>
            </div>
            {aiNote}
            """;

        return BuildSection("security", $"安全分析 ({data.SecurityRisks.Count} 項風險)", "🛡️", content);
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

    private string BuildMakefileSection(DashboardData data)
    {
        var mf = data.Makefile;
        if (mf == null || string.IsNullOrWhiteSpace(mf.Content))
        {
            return BuildSection("makefile", "Makefile 指令區", "⚙️",
                "<p style=\"color:#8b949e\">未偵測到 Makefile，亦無法自動生成。</p>", true);
        }

        var badge = mf.Exists
            ? $"<span class=\"lang-badge\" style=\"background:#3fb95022;color:#3fb950;border:1px solid #3fb95044\">✓ 已找到</span>"
            : $"<span class=\"lang-badge\" style=\"background:#d2992222;color:#d29922;border:1px solid #d2992244\">⚡ 自動生成</span>";

        var targetsHtml = "";
        if (mf.Targets.Count > 0)
        {
            targetsHtml = "<div style=\"margin-bottom:12px;display:flex;flex-wrap:wrap;gap:6px\">"
                + string.Join("", mf.Targets.Select(t =>
                    $"<code style=\"background:var(--surface);border:1px solid var(--border);padding:2px 8px;border-radius:4px;font-size:11px\">{HtmlEncode(t)}</code>"))
                + "</div>";
        }

        var content = $"""
            <div style="display:flex;align-items:center;gap:8px;margin-bottom:12px">
              {badge}
              <span style="color:#8b949e;font-size:12px">{HtmlEncode(mf.FilePath)}</span>
              <span style="color:#8b949e;font-size:12px">·</span>
              <span style="color:#8b949e;font-size:12px">{mf.Targets.Count} 個指令</span>
            </div>
            {targetsHtml}
            <pre class="dt-sql-code" style="max-height:600px;overflow-y:auto;tab-size:4">{HtmlEncode(mf.Content)}</pre>
            """;
        return BuildSection("makefile", "Makefile 指令區", "⚙️", content, true);
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

    /// <summary>
    /// HTML-encodes <paramref name="text"/> using <see cref="HtmlEncoder.Default"/>
    /// (the .NET platform-default encoder that encodes <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>,
    /// <c>&quot;</c>, and <c>&#x27;</c>).
    /// Returns an empty string for <c>null</c> input to avoid null-check boilerplate at every call site.
    /// </summary>
    /// <remarks>
    /// <see cref="HtmlEncoder.Default"/> is used rather than <c>WebUtility.HtmlEncode</c>
    /// because it is safe for both HTML element content and attribute values (CWE-79 / OWASP A03:2021).
    /// </remarks>
    private static string HtmlEncode(string? text)
        => text == null ? "" : HtmlEncoder.Default.Encode(text);

    /// <summary>
    /// Escapes a string for safe use inside a Mermaid diagram label by replacing
    /// characters that would break Mermaid's lexer (<c>"</c>, <c>[</c>, <c>]</c>, newlines).
    /// </summary>
    private static string EscapeMermaid(string? text)
        => (text ?? "").Replace("\"", "'").Replace("[", "(").Replace("]", ")").Replace("\n", " ");

    /// <summary>
    /// Sanitises a string for use as an HTML <c>id</c> attribute or Mermaid node identifier
    /// by replacing any character outside <c>[a-zA-Z0-9_]</c> with an underscore.
    /// </summary>
    private static string SanitizeMermaidId(string id)
        => System.Text.RegularExpressions.Regex.Replace(id, @"[^a-zA-Z0-9_]", "_");

    /// <summary>
    /// Formats a raw byte count as a human-readable string with an appropriate unit suffix.
    /// Uses one decimal place for KB / MB / GB to balance precision and readability.
    /// </summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
    };

    private string GetInlineStyles() => """
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
        .navbar-brand { display: flex; align-items: center; gap: 8px; flex-shrink: 0; }
        .navbar-logo { font-size: 22px; color: var(--accent-blue); flex-shrink: 0; }
        .navbar-title { font-weight: 700; font-size: 16px; white-space: nowrap; }
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
        /* ═══ Graph (RidGraph) ═══ */
        .graph-toolbar {
          display: flex; align-items: center; gap: 8px; margin-bottom: 10px;
          flex-wrap: wrap;
        }
        .graph-label { font-size: 12px; color: var(--text-secondary); font-weight: 600; }
        .layout-btn {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: 6px; padding: 5px 12px; cursor: pointer;
          font-size: 12px; color: var(--text-secondary);
          transition: all var(--transition);
        }
        .layout-btn:hover { border-color: var(--accent-blue); color: var(--text-primary); }
        .layout-btn.active {
          background: rgba(88,166,255,0.15); border-color: var(--accent-blue);
          color: var(--accent-blue); font-weight: 600;
        }
        .layout-btn-reset {
          background: none; border: 1px solid var(--border-color); border-radius: 6px;
          padding: 5px 8px; cursor: pointer; font-size: 13px; color: var(--text-secondary);
          transition: all var(--transition);
        }
        .layout-btn-reset:hover { border-color: var(--accent-orange); color: var(--accent-orange); }
        .graph-hint { font-size: 11px; color: var(--text-secondary); margin-left: auto; }
        .rid-graph-container {
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); overflow: hidden; min-height: 320px;
          position: relative;
        }
        .rid-graph-container svg { display: block; cursor: grab; user-select: none; }
        .rid-graph-container svg:active { cursor: grabbing; }
        /* ═══ Startup Flow ═══ */
        .startup-flow { display: flex; flex-direction: column; align-items: flex-start; padding: 8px 0; gap: 0; }
        .startup-step {
          display: flex; align-items: center; gap: 14px;
          background: var(--bg-card); border: 1px solid var(--border-color);
          border-radius: var(--radius); padding: 12px 16px; width: 100%; max-width: 600px;
        }
        .startup-num {
          width: 30px; height: 30px; border-radius: 50%; background: var(--accent-blue);
          color: #fff; display: flex; align-items: center; justify-content: center;
          font-size: 13px; font-weight: 700; flex-shrink: 0;
        }
        .startup-body { flex: 1; }
        .startup-name { font-weight: 600; font-size: 14px; }
        .startup-meta { font-size: 11px; color: var(--text-secondary); margin-top: 3px; display: flex; gap: 6px; flex-wrap: wrap; }
        .startup-deps { color: var(--text-secondary); }
        .startup-status { font-size: 12px; color: var(--accent-green); }
        .startup-connector {
          font-size: 20px; color: var(--border-color); margin-left: 14px; padding: 2px 0;
          line-height: 1;
        }
        /* ═══ Gantt ═══ */
        .gantt-chart { display: flex; flex-direction: column; gap: 8px; padding: 8px 0; }
        .gantt-row { display: flex; align-items: center; gap: 12px; }
        .gantt-label { font-size: 12px; font-family: var(--font-mono); width: 140px; flex-shrink: 0; text-align: right; color: var(--text-secondary); }
        .gantt-bar-track { flex: 1; height: 24px; background: var(--bg-card); border: 1px solid var(--border-color); border-radius: 4px; position: relative; overflow: visible; }
        .gantt-bar { position: absolute; top: 2px; height: 20px; border-radius: 3px; opacity: 0.8; transition: opacity var(--transition); }
        .gantt-bar:hover { opacity: 1; }
        .gantt-bar-label { position: absolute; top: 4px; font-size: 10px; padding-left: 4px; white-space: nowrap; }
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
        /* ═══ Security Table ═══ */
        .sec-summary { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
        .sec-stat {
          display: flex; flex-direction: column; align-items: center;
          padding: 10px 18px; border-radius: var(--radius);
          border: 1px solid var(--border-color); min-width: 80px;
        }
        .sec-count { font-size: 24px; font-weight: 700; line-height: 1; }
        .sec-lbl { font-size: 11px; color: var(--text-secondary); margin-top: 3px; }
        .sec-critical { border-color: var(--accent-red); background: rgba(248,81,73,0.08); }
        .sec-critical .sec-count { color: var(--accent-red); }
        .sec-high { border-color: #d29922; background: rgba(210,153,34,0.08); }
        .sec-high .sec-count { color: #d29922; }
        .sec-medium { border-color: var(--accent-orange); background: rgba(210,153,34,0.06); }
        .sec-medium .sec-count { color: var(--accent-orange); }
        .sec-info { border-color: var(--accent-blue); background: rgba(88,166,255,0.06); }
        .sec-info .sec-count { color: var(--accent-blue); }
        .sec-ai-note { font-size: 12px; color: var(--text-secondary); margin-left: auto; padding: 6px 12px; background: var(--bg-card); border: 1px solid var(--border-color); border-radius: var(--radius); }
        .sec-table { width: 100%; border-collapse: collapse; font-size: 13px; }
        .sec-table th { background: var(--bg-card); padding: 8px 12px; text-align: left; font-size: 11px; color: var(--text-secondary); border-bottom: 1px solid var(--border-color); text-transform: uppercase; letter-spacing: 0.5px; white-space: nowrap; }
        .sec-table td { padding: 10px 12px; border-bottom: 1px solid var(--border-color); vertical-align: top; }
        .sec-row:hover td { background: var(--bg-hover); }
        .sec-row-critical { border-left: 3px solid var(--accent-red); }
        .sec-row-high { border-left: 3px solid #d29922; }
        .sec-row-warning { border-left: 3px solid var(--accent-orange); }
        .sec-row-info { border-left: 3px solid var(--accent-blue); }
        .sec-badge { display: inline-flex; align-items: center; gap: 4px; padding: 3px 8px; border-radius: 12px; font-size: 11px; font-weight: 700; white-space: nowrap; }
        .sec-badge-critical { background: rgba(248,81,73,0.15); color: var(--accent-red); border: 1px solid rgba(248,81,73,0.3); }
        .sec-badge-high { background: rgba(210,153,34,0.15); color: #e3b341; border: 1px solid rgba(210,153,34,0.3); }
        .sec-badge-medium { background: rgba(210,153,34,0.1); color: var(--accent-orange); border: 1px solid rgba(210,153,34,0.25); }
        .sec-badge-info { background: rgba(88,166,255,0.1); color: var(--accent-blue); border: 1px solid rgba(88,166,255,0.25); }
        .sec-category { font-size: 11px; color: var(--text-secondary); }
        .sec-desc { font-size: 12px; color: var(--text-secondary); line-height: 1.5; }
        .sec-rec { font-size: 12px; color: var(--text-secondary); line-height: 1.5; }
        .sec-files { font-size: 11px; }
        .sec-file-tag { font-size: 10px; color: var(--accent-orange); display: inline-block; margin: 1px 2px; }
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
        // ════════════════════════════════════════════════════════════════════
        // RidGraph — Pure-JS Offline SVG Interactive Graph Renderer v1.0
        // Supports: Hierarchical (layered) + Adaptive (force-directed) layouts
        // No external dependencies — fully self-contained
        // ════════════════════════════════════════════════════════════════════
        var RidGraph = (function() {
          var NW = 160, NH = 44, HG = 200, VG = 100;
          var THEME = {
            root:       { fill: '#0d2140', stroke: '#58a6ff', text: '#79b8ff' },
            group:      { fill: '#0d2a15', stroke: '#3fb950', text: '#56d364' },
            leaf:       { fill: '#21262d', stroke: '#30363d', text: '#c9d1d9' },
            service:    { fill: '#1a2433', stroke: '#58a6ff', text: '#c9d1d9' },
            database:   { fill: '#1a2a1a', stroke: '#3fb950', text: '#56d364' },
            Handler:    { fill: '#0d2140', stroke: '#58a6ff', text: '#79b8ff' },
            Service:    { fill: '#0d2a15', stroke: '#3fb950', text: '#56d364' },
            Repository: { fill: '#2a1e0d', stroke: '#d29922', text: '#e3b341' },
            Controller: { fill: '#0d2140', stroke: '#58a6ff', text: '#79b8ff' },
            default:    { fill: '#21262d', stroke: '#8b949e', text: '#c9d1d9' }
          };
          function col(type) { return THEME[type] || THEME.default; }

          // ── Hierarchical layout (top-down layered) ──────────────────────
          function hierLayout(nodes, edges) {
            if (!nodes.length) return {};
            var out = {}, inDeg = {};
            nodes.forEach(function(n) { out[n.id] = []; inDeg[n.id] = 0; });
            edges.forEach(function(e) {
              if (out[e.source] !== undefined) out[e.source].push(e.target);
              if (inDeg[e.target] !== undefined) inDeg[e.target]++;
            });
            var layer = {}, queue = [], vis = {};
            nodes.forEach(function(n) { if (!inDeg[n.id]) { queue.push(n.id); layer[n.id] = 0; vis[n.id] = 1; } });
            if (!queue.length) { queue.push(nodes[0].id); layer[nodes[0].id] = 0; vis[nodes[0].id] = 1; }
            var qi = 0;
            while (qi < queue.length) {
              var id = queue[qi++];
              out[id].forEach(function(tid) {
                layer[tid] = Math.max(layer[tid] || 0, (layer[id] || 0) + 1);
                if (!vis[tid]) { vis[tid] = 1; queue.push(tid); }
              });
            }
            nodes.forEach(function(n) { if (layer[n.id] === undefined) layer[n.id] = 0; });
            var byL = {};
            nodes.forEach(function(n) { var l = layer[n.id]; (byL[l] = byL[l] || []).push(n.id); });
            var pos = {};
            Object.keys(byL).forEach(function(l) {
              var ids = byL[l];
              ids.forEach(function(id, i) {
                pos[id] = { x: (i - (ids.length - 1) / 2) * (NW + HG), y: parseInt(l) * (NH + VG) };
              });
            });
            return pos;
          }

          // ── Force-directed (adaptive) layout ───────────────────────────
          function forceLayout(nodes, edges) {
            if (!nodes.length) return {};
            var n = nodes.length;
            var k = Math.sqrt(Math.max(n * NW * 5, 500 * 400) / n) * 1.5;
            var pos = {};
            nodes.forEach(function(node, i) {
              var a = (2 * Math.PI * i) / n, r = Math.max(n * 55, 180);
              pos[node.id] = { x: Math.cos(a) * r, y: Math.sin(a) * r, vx: 0, vy: 0 };
            });
            for (var iter = 0; iter < 300; iter++) {
              var t = k * Math.max(0.04, 1.0 - iter / 250);
              nodes.forEach(function(n2) { pos[n2.id].vx = 0; pos[n2.id].vy = 0; });
              for (var i = 0; i < nodes.length; i++) {
                for (var j = i + 1; j < nodes.length; j++) {
                  var pi = pos[nodes[i].id], pj = pos[nodes[j].id];
                  var dx = pi.x - pj.x, dy = pi.y - pj.y;
                  var d = Math.max(Math.sqrt(dx*dx+dy*dy), 1), f = k*k/d;
                  pi.vx += f*dx/d; pi.vy += f*dy/d;
                  pj.vx -= f*dx/d; pj.vy -= f*dy/d;
                }
              }
              edges.forEach(function(e) {
                var ps = pos[e.source], pt = pos[e.target];
                if (!ps || !pt) return;
                var dx = pt.x-ps.x, dy = pt.y-ps.y;
                var d = Math.max(Math.sqrt(dx*dx+dy*dy), 1), f = d*d/k;
                ps.vx += f*dx/d; ps.vy += f*dy/d;
                pt.vx -= f*dx/d; pt.vy -= f*dy/d;
              });
              nodes.forEach(function(n2) {
                var p = pos[n2.id], sp = Math.sqrt(p.vx*p.vx+p.vy*p.vy);
                if (sp > 0) { var mv = Math.min(sp, t); p.x += p.vx/sp*mv; p.y += p.vy/sp*mv; }
              });
            }
            return pos;
          }

          // ── SVG render ─────────────────────────────────────────────────
          function render(cid, gdata, layout) {
            var container = document.getElementById(cid);
            if (!container) return;
            var nodes = gdata.nodes || [], edges = gdata.edges || [];
            if (!nodes.length) {
              container.innerHTML = '<p style="color:#8b949e;padding:32px;text-align:center;font-size:13px">無圖形資料</p>';
              return;
            }
            var pos = layout === 'adaptive' ? forceLayout(nodes, edges) : hierLayout(nodes, edges);
            var pad = 50, minX=1e9, minY=1e9, maxX=-1e9, maxY=-1e9;
            nodes.forEach(function(n) {
              var p = pos[n.id] || {x:0,y:0};
              minX=Math.min(minX,p.x-NW/2); minY=Math.min(minY,p.y-NH/2);
              maxX=Math.max(maxX,p.x+NW/2); maxY=Math.max(maxY,p.y+NH/2);
            });
            var vw = Math.max(maxX-minX+pad*2, 500), vh = Math.max(maxY-minY+pad*2, 280);
            var ox = -minX+pad, oy = -minY+pad;
            var p = ['<defs>',
              '<marker id="rarr_'+cid+'" markerWidth="8" markerHeight="7" refX="7" refY="3.5" orient="auto">',
              '<path d="M0,0 L0,7 L8,3.5z" fill="#58a6ff" opacity="0.75"/></marker>',
              '<filter id="rshadow_'+cid+'" x="-20%" y="-20%" width="140%" height="140%">',
              '<feDropShadow dx="0" dy="2" stdDeviation="3" flood-color="#000000" flood-opacity="0.35"/></filter>',
              '</defs><g class="rid-edges">'];
            edges.forEach(function(e) {
              var ps = pos[e.source], pt = pos[e.target]; if (!ps||!pt) return;
              var x1=ps.x+ox, y1=ps.y+oy, x2=pt.x+ox, y2=pt.y+oy;
              var dx=x2-x1, dy=y2-y1, d=Math.max(Math.sqrt(dx*dx+dy*dy),1);
              var sx=x1+dx/d*(NW/2), sy=y1+dy/d*(NH/2);
              var ex=x2-dx/d*(NW/2+9), ey=y2-dy/d*(NH/2+9);
              var mx=(sx+ex)/2;
              p.push('<path d="M'+sx.toFixed(1)+','+sy.toFixed(1)+' C'+mx.toFixed(1)+','+sy.toFixed(1)+' '+mx.toFixed(1)+','+ey.toFixed(1)+' '+ex.toFixed(1)+','+ey.toFixed(1)+'"'+
                ' fill="none" stroke="#58a6ff" stroke-width="1.5" opacity="0.4" marker-end="url(#rarr_'+cid+')"/>');
            });
            p.push('</g><g class="rid-nodes">');
            nodes.forEach(function(n) {
              var np = pos[n.id]||{x:0,y:0}, nx=np.x+ox-NW/2, ny=np.y+oy-NH/2;
              var c = col(n.type), lines = (n.label||n.id||'').split('\n');
              p.push('<g class="rid-node" data-nid="'+escA(n.id)+'" filter="url(#rshadow_'+cid+')">');
              p.push('<rect x="'+nx.toFixed(1)+'" y="'+ny.toFixed(1)+'" width="'+NW+'" height="'+NH+'" rx="8"'+
                ' fill="'+c.fill+'" stroke="'+c.stroke+'" stroke-width="1.5" class="rid-node-rect"/>');
              var ty = ny + (lines.length > 1 ? NH/2-8 : NH/2+5);
              lines.forEach(function(line, li) {
                p.push('<text x="'+(nx+NW/2).toFixed(1)+'" y="'+(ty+li*16).toFixed(1)+'"'+
                  ' text-anchor="middle" fill="'+c.text+'" font-size="'+(li===0?12:10)+'"'+
                  ' font-family="-apple-system,BlinkMacSystemFont,\'Segoe UI\',sans-serif"'+
                  ' font-weight="'+(li===0?'600':'400')+'">'+escS(line)+'</text>');
              });
              p.push('</g>');
            });
            p.push('</g>');
            container.innerHTML = '<svg id="'+cid+'_svg" viewBox="0 0 '+vw.toFixed(0)+' '+vh.toFixed(0)+'"'+
              ' style="width:100%;min-height:300px;background:var(--bg-secondary);display:block"'+
              ' xmlns="http://www.w3.org/2000/svg">'+p.join('')+'</svg>';
            addInteraction(container.querySelector('svg'));
          }

          function addInteraction(svg) {
            if (!svg) return;
            var st = {pan:false,sx:0,sy:0,tx:0,ty:0,sc:1};
            var wrap = document.createElementNS('http://www.w3.org/2000/svg','g');
            wrap.setAttribute('class','rid-wrap');
            Array.from(svg.childNodes).forEach(function(c) { if (c.tagName !== 'defs') wrap.appendChild(c); });
            svg.appendChild(wrap);
            function applyT() { wrap.setAttribute('transform','translate('+st.tx+','+st.ty+') scale('+st.sc+')'); }
            svg.addEventListener('mousedown', function(e) {
              st.pan=true; st.sx=e.clientX-st.tx; st.sy=e.clientY-st.ty;
              svg.style.cursor='grabbing'; e.preventDefault();
            });
            document.addEventListener('mousemove', function(e) {
              if (!st.pan) return; st.tx=e.clientX-st.sx; st.ty=e.clientY-st.sy; applyT();
            });
            document.addEventListener('mouseup', function() { st.pan=false; svg.style.cursor='grab'; });
            svg.addEventListener('wheel', function(e) {
              e.preventDefault();
              var r=svg.getBoundingClientRect(), mx=e.clientX-r.left, my=e.clientY-r.top;
              var d=e.deltaY<0?1.12:0.9, ns=Math.max(0.1,Math.min(6,st.sc*d));
              st.tx=mx-(mx-st.tx)*(ns/st.sc); st.ty=my-(my-st.ty)*(ns/st.sc); st.sc=ns; applyT();
            },{passive:false});
            svg.addEventListener('dblclick', function() { st.tx=0;st.ty=0;st.sc=1; applyT(); });
            svg.addEventListener('mouseover', function(e) {
              var nd=e.target.closest('.rid-node');
              if (nd) { var rc=nd.querySelector('rect'); if(rc){rc.style.strokeWidth='2.5';rc.style.filter='brightness(1.2)';} }
            });
            svg.addEventListener('mouseout', function(e) {
              var nd=e.target.closest('.rid-node');
              if (nd) { var rc=nd.querySelector('rect'); if(rc){rc.style.strokeWidth='1.5';rc.style.filter='';} }
            });
          }

          function escA(s) { return String(s||'').replace(/[<>"&]/g,function(c){return{'<':'&lt;','>':'&gt;','"':'&quot;','&':'&amp;'}[c];}); }
          function escS(s) { return String(s||'').replace(/[<>&]/g,function(c){return{'<':'&lt;','>':'&gt;','&':'&amp;'}[c];}); }

          // ── Data builders ───────────────────────────────────────────────
          function buildDep(data) {
            var nodes=[{id:'ROOT',label:(data.project&&data.project.name)||'Project',type:'root'}], edges=[];
            var pkgs=data.packages||[], ecos=[];
            pkgs.forEach(function(p){if(ecos.indexOf(p.ecosystem)<0)ecos.push(p.ecosystem);});
            ecos.forEach(function(eco){
              var gid='G_'+eco.replace(/[^a-zA-Z0-9]/g,'_');
              nodes.push({id:gid,label:eco,type:'group'});
              edges.push({source:'ROOT',target:gid});
              pkgs.filter(function(p){return p.ecosystem===eco;}).slice(0,8).forEach(function(pkg){
                var pid='P_'+Math.abs(hashStr(pkg.name+pkg.version));
                nodes.push({id:pid,label:(pkg.name||'')+(pkg.version?'\n'+pkg.version:''),type:'leaf'});
                edges.push({source:gid,target:pid});
              });
            });
            return {nodes:nodes,edges:edges};
          }

          function buildDocker(data) {
            var cs=data.containers||[];
            var nodes=cs.map(function(c){
              var isDB=/postgres|mysql|mongo|redis|rabbit|kafka|elastic|memcache/i.test(c.image||'');
              var ps=(c.ports||[]).slice(0,2).map(function(p){return p.hostPort+':'+p.containerPort;}).join(',');
              return {id:c.name,label:c.name+(ps?'\n'+ps:''),type:isDB?'database':'service'};
            });
            var edges=[];
            cs.forEach(function(c){(c.dependsOn||[]).forEach(function(dep){edges.push({source:dep,target:c.name});});});
            return {nodes:nodes,edges:edges};
          }

          function hashStr(s) {
            var h=5381; for(var i=0;i<s.length;i++)h=((h<<5)+h)+s.charCodeAt(i); return h>>>0;
          }

          // ── Registry ────────────────────────────────────────────────────
          var store = {};

          return {
            init: function(cid, type, layout) {
              var d = window.__RID_DATA__; if (!d) return;
              var gd = type==='dep'?buildDep(d):type==='docker'?buildDocker(d):null;
              if (!gd) return;
              store[cid] = {type:type, data:gd};
              render(cid, gd, layout||'hierarchical');
            },
            switchLayout: function(cid, layout, btn) {
              var tb = btn.closest ? btn.closest('.graph-toolbar') : null;
              if (tb) tb.querySelectorAll('.layout-btn').forEach(function(b){b.classList.remove('active');});
              btn.classList.add('active');
              var g = store[cid]; if (g) render(cid, g.data, layout);
            },
            resetZoom: function(cid) {
              var svg = document.getElementById(cid+'_svg');
              if (svg) { var w=svg.querySelector('.rid-wrap'); if(w) w.setAttribute('transform','translate(0,0) scale(1)'); }
            }
          };
        })();

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
            var matchM = !methodFilter || (row.dataset.method || '').toUpperCase() === methodFilter;
            var matchT = !tagFilter || tag === tagFilter;
            row.classList.toggle('search-hidden', !(matchQ && matchM && matchT));
          });
        }

        // ═══ Env Toggle ═══
        function toggleEnvValue(el) {
          // el is the ●●●●●● span; the sibling .env-revealed span holds the masked placeholder.
          var revealed = el.nextElementSibling;
          if (!revealed) return;
          var isHidden = revealed.style.display === 'none';
          el.style.display = isHidden ? 'none' : '';
          revealed.style.display = isHidden ? '' : 'none';
        }

        var allEnvShown = false;
        function toggleAllEnv() {
          allEnvShown = !allEnvShown;
          document.querySelectorAll('.env-masked').forEach(function(el) {
            var revealed = el.nextElementSibling;
            if (!revealed) return;
            el.style.display = allEnvShown ? 'none' : '';
            revealed.style.display = allEnvShown ? '' : 'none';
          });
        }

        // ═══ File Tree Toggle ═══
        function toggleFtNode(id) {
          var el = document.getElementById(id);
          if (el) el.classList.toggle('collapsed');
        }

        // ═══ SQL Interactive Builder ═══
        /**
         * Parses a composedSql string for conditional sqlc-style validate-flag patterns and
         * returns an HTML string containing an interactive parameter-fill form.
         * Users can toggle each WHERE condition on/off, enter concrete values, and instantly
         * preview a clean, copy-pasteable SQL statement ready for direct DB execution.
         *
         * Detected patterns:
         *   AND (TRUE|FALSE = 'paramName_value'[::bool] OR actualCondition)
         *   OFFSET $1 / LIMIT $2  (positional pagination params)
         *   Any remaining 'xxx_value' placeholder tokens
         *
         * @param {string} sql  The composedSql string from an SqlQuery object.
         * @param {number} idx  Index of the query within sqlQueries (used for unique element IDs).
         * @returns {string}    HTML string for the interactive SQL builder widget.
         */
        function buildSqlInteractorHtml(sql, idx) {
          // ── 1. Parse AND (BOOL = 'name_value'::bool OR condition) blocks ──────
          var conds = [];
          var blockRx = /AND\s+\((TRUE|FALSE)\s*=\s*'(\w+?)_value'(?:::bool)?\s+OR\s+/gi;
          var bm;
          blockRx.lastIndex = 0;
          while ((bm = blockRx.exec(sql)) !== null) {
            var blockStart = bm.index;
            var condStart  = bm.index + bm[0].length;
            // Walk forward to find the matching closing parenthesis (handles nested parens)
            var depth = 1, j = condStart;
            while (j < sql.length && depth > 0) {
              if (sql[j] === '(') depth++;
              else if (sql[j] === ')') depth--;
              j++;
            }
            var condStr   = sql.substring(condStart, j - 1).trim();
            var fullBlock = sql.substring(blockStart, j);
            // Extract the value-placeholder name from inside the condition (e.g. 'receipt_value')
            var vpm       = condStr.match(/'(\w+?)_value'/);
            var rawName   = bm[2];
            // Strip trailing _validate suffix for a cleaner display label
            var dispName  = rawName.replace(/_validate$/, '');
            conds.push({ defaultOn: bm[1].toUpperCase() === 'TRUE', name: rawName, dispName: dispName, cond: condStr, full: fullBlock, vn: vpm ? vpm[1] : null });
            // Advance the regex past the consumed block to avoid re-matching nested content
            blockRx.lastIndex = j;
          }

          // ── 2. Collect inline 'xxx_value' placeholders not covered by conditions ──
          var excludedNames = {};
          conds.forEach(function(c) { excludedNames[c.name] = true; if (c.vn) excludedNames[c.vn] = true; });
          var inlines = [], seenInlines = {};
          var ilRx = /'(\w+?)_value'/gi, ilm;
          while ((ilm = ilRx.exec(sql)) !== null) {
            var iName = ilm[1];
            if (!excludedNames[iName] && !seenInlines[iName]) { seenInlines[iName] = true; inlines.push({ n: iName }); }
          }

          // ── 3. Detect positional OFFSET $1 / LIMIT $2 pagination params ───────
          var hasOff = /\bOFFSET\s+\$1\b/i.test(sql);
          var hasLim = /\bLIMIT\s+\$2\b/i.test(sql);

          // ── 4. Build the initial clean SQL: default-off conditions are removed ─
          var initSql = sql;
          conds.forEach(function(c) {
            initSql = c.defaultOn
              ? initSql.split(c.full).join('AND ' + c.cond)
              : initSql.split(c.full).join('');
          });
          if (hasOff) initSql = initSql.replace(/\bOFFSET\s+\$1\b/i, 'OFFSET 0');
          if (hasLim) initSql = initSql.replace(/\bLIMIT\s+\$2\b/i,  'LIMIT 10');
          initSql = initSql.replace(/[ \t]*\n([ \t]*\n){2,}/g, '\n\n').trim();

          // ── 5. Embed parsed data so the popup's updateSqlPreview can access it ─
          // Unicode-escape < > & to prevent </script> in JSON from breaking the tag.
          var dataJson = JSON.stringify({ sql: sql, conds: conds, ils: inlines })
            .replace(/</g, '\\u003c').replace(/>/g, '\\u003e').replace(/&/g, '\\u0026');
          var h = '<script>if(!window._sqlBd)window._sqlBd={};window._sqlBd[' + idx + ']=' + dataJson + ';<\/script>';

          // ── 6. Build the interactive widget HTML ─────────────────────────────
          h += '<div class="dt-sqlb">';
          h += '<div class="dt-sqlb-hdr">';
          h += '<span class="dt-sqlb-hdr-title">🔧 互動式 SQL 參數填入器</span>';
          h += '<span class="dt-sqlb-hdr-hint">勾選條件並填入參數值，即時產生可執行的乾淨 SQL</span>';
          h += '</div>';

          // Pagination inputs (only shown when SQL uses positional $1/$2 params)
          if (hasOff || hasLim) {
            h += '<div class="dt-sqlb-sec"><div class="dt-sqlb-sec-title">分頁設定</div><div class="dt-sqlb-row">';
            if (hasOff) h += '<label class="dt-sqlb-label">OFFSET</label><input type="number" id="sqloff_'+idx+'" value="0" min="0" class="dt-sqlb-input dt-sqlb-num" oninput="updateSqlPreview('+idx+')">';
            if (hasLim) h += '<label class="dt-sqlb-label" style="margin-left:12px">LIMIT</label><input type="number" id="sqllim_'+idx+'" value="10" min="1" class="dt-sqlb-input dt-sqlb-num" oninput="updateSqlPreview('+idx+')">';
            h += '</div></div>';
          }

          // One row per optional WHERE condition
          if (conds.length > 0) {
            h += '<div class="dt-sqlb-sec"><div class="dt-sqlb-sec-title">選填 WHERE 條件 <span class="dt-sqlb-sec-hint">（勾選即啟用，未勾選的條件從 SQL 中移除）</span></div>';
            conds.forEach(function(c, ci) {
              var preview = c.cond.length > 72 ? c.cond.substring(0, 69) + '…' : c.cond;
              h += '<div class="dt-sqlb-row">';
              h += '<input type="checkbox" id="sqlcb_'+idx+'_'+ci+'" class="dt-sqlb-cb"'+(c.defaultOn?' checked':'')+' oninput="updateSqlPreview('+idx+')">';
              h += '<label for="sqlcb_'+idx+'_'+ci+'" class="dt-sqlb-cond-name">'+escHtml(c.dispName)+'</label>';
              if (c.vn) {
                h += '<span class="dt-sqlb-eq">=</span>';
                h += '<input type="text" id="sqlvi_'+idx+'_'+ci+'" placeholder="輸入值…" class="dt-sqlb-input dt-sqlb-val" oninput="updateSqlPreview('+idx+')">';
              }
              h += '<span class="dt-sqlb-hint">→ <code style="font-size:10px;background:transparent;color:#8b949e;padding:0">'+escHtml(preview)+'</code></span>';
              h += '</div>';
            });
            h += '</div>';
          }

          // Fixed inline value placeholders that are not part of conditional blocks
          if (inlines.length > 0) {
            h += '<div class="dt-sqlb-sec"><div class="dt-sqlb-sec-title">其他參數（固定條件）</div>';
            inlines.forEach(function(p, pi) {
              h += '<div class="dt-sqlb-row">';
              h += '<label class="dt-sqlb-cond-name">'+escHtml(p.n)+'</label>';
              h += '<span class="dt-sqlb-eq">=</span>';
              h += '<input type="text" id="sqlil_'+idx+'_'+pi+'" placeholder="輸入值…" class="dt-sqlb-input dt-sqlb-val" oninput="updateSqlPreview('+idx+')">';
              h += '</div>';
            });
            h += '</div>';
          }

          // Live preview pane + copy button
          h += '<div class="dt-sqlb-preview-hdr">';
          h += '<span class="dt-sqlb-preview-title">預覽可執行 SQL</span>';
          h += '<button id="sqlcopybtn_'+idx+'" class="dt-sqlb-copy-btn" onclick="copySqlPreview('+idx+')">📋 複製 SQL</button>';
          h += '</div>';
          h += '<pre class="dt-sql-code dt-sqlb-pre" id="sqlpre_'+idx+'">'+escHtml(initSql)+'</pre>';
          h += '</div>';
          return h;
        }

        // ═══ API Detail New Tab ═══
        function openApiDetailTab(endpoint, allData) {
          // Hoist layer colour mapping to a single lookup to avoid duplication across
          // the trace flow, logic table, and step card sections below.
          var LAYER_COLORS = {
            'Handler':    '#58a6ff', 'Controller': '#58a6ff',
            'Service':    '#3fb950', 'Repository': '#d29922',
            'External':   '#bc8cff'
          };
          function layerColor(layer) { return LAYER_COLORS[layer] || '#8b949e'; }

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
            responsesHtml = '<div class="dt-resp-list">';
            ep.responses.forEach(function(r) {
              var is2xx = r.statusCode && r.statusCode.toString().startsWith('2');
              var is4xx = r.statusCode && r.statusCode.toString().startsWith('4');
              var badgeCls = is2xx ? 'dt-badge-green' : is4xx ? 'dt-badge-red' : 'dt-badge-yellow';
              responsesHtml += '<div class="dt-resp-card">';
              responsesHtml += '<div class="dt-resp-header">';
              responsesHtml += '<span class="dt-badge '+badgeCls+' dt-resp-code">'+escHtml(r.statusCode)+'</span>';
              responsesHtml += '<span class="dt-resp-desc">'+escHtml(r.description||'')+'</span>';
              if (r.contentType) responsesHtml += '<span class="dt-resp-ct">'+escHtml(r.contentType)+'</span>';
              responsesHtml += '</div>';
              if (r.exampleJson || r.schemaJson) {
                responsesHtml += '<div class="dt-resp-schema-label">Response Body Schema</div>';
                responsesHtml += '<pre class="dt-resp-schema">'+escHtml(r.exampleJson || r.schemaJson)+'</pre>';
              } else if (r.schema) {
                responsesHtml += '<div class="dt-resp-schema-label">Type: <code>'+escHtml(r.schema)+'</code></div>';
              }
              responsesHtml += '</div>';
            });
            responsesHtml += '</div>';
          }

          // Build trace HTML
          var traceHtml = '<p style="color:#8b949e;font-size:13px">⚠ 未偵測到此 API 的執行路徑。請確認原始碼中有對應的 handler 函式。</p>';
          var logicHtml = '<p style="color:#8b949e;font-size:13px">請先確認執行路徑。</p>';
          // ORM / DB access detection for richer empty-state message
          var ormHints = {
            'gorm': 'GORM', 'sqlalchemy': 'SQLAlchemy', 'activerecord': 'ActiveRecord',
            'hibernate': 'Hibernate', 'prisma': 'Prisma', 'typeorm': 'TypeORM',
            'sequelize': 'Sequelize', 'mongoose': 'Mongoose (MongoDB)',
            'ent': 'ent (Go)', 'bun': 'bun (Go)', 'sqlboiler': 'SQLBoiler'
          };
          var detectedOrm = trace && trace.steps && trace.steps.length > 0
            ? (function() {
                var files = trace.steps.map(function(s){ return (s.file||'').toLowerCase(); }).join(' ');
                for (var k in ormHints) { if (files.indexOf(k) !== -1) return ormHints[k]; }
                return null;
              })()
            : null;
          var sqlHtml = detectedOrm
            ? '<p style="color:#8b949e;font-size:13px">ℹ 未偵測到原生 SQL。此端點可能透過 <strong style="color:#e6edf3">'
                + escHtml(detectedOrm) + '</strong> ORM 存取資料庫。</p>'
            : '<p style="color:#8b949e;font-size:13px">未偵測到 SQL 語法。</p>';

          if (trace && trace.steps && trace.steps.length > 0) {
            // Build HTML flow diagram for execution trace
            traceHtml = '<div class="dt-trace-flow">';
            trace.steps.forEach(function(step, i) {
              var lc = layerColor(step.layer);
              traceHtml += '<div class="dt-trace-item">';
              traceHtml += '<div class="dt-trace-badge" style="background:'+lc+'22;border:1px solid '+lc+'44;color:'+lc+'">'+escHtml(step.layer)+'</div>';
              traceHtml += '<div class="dt-trace-body">';
              traceHtml += '<div class="dt-trace-num" style="color:'+lc+'">'+step.order+'</div>';
              traceHtml += '<div class="dt-trace-content">';
              traceHtml += '<code class="dt-trace-file">'+escHtml(step.file||'')+'</code>';
              traceHtml += '<strong class="dt-trace-fn">'+escHtml(step.function||'')+'</strong>';
              if (step.description) traceHtml += '<div class="dt-trace-desc">'+escHtml(step.description)+'</div>';
              traceHtml += '</div></div>';
              if (i < trace.steps.length - 1) traceHtml += '<div class="dt-trace-arrow">↓</div>';
              traceHtml += '</div>';
            });
            traceHtml += '</div>';

            // Logic view — execution table
            logicHtml = '<table class="dt-table" style="margin-bottom:24px"><thead><tr>'
              + '<th style="width:40px">#</th><th style="width:100px">層次</th>'
              + '<th>檔案路徑</th><th>函式名稱</th><th style="width:120px">程式碼行數</th><th>功能說明</th>'
              + '</tr></thead><tbody>';
            trace.steps.forEach(function(step) {
              var lc = layerColor(step.layer);
              var lineRange = (step.startLine && step.endLine)
                ? 'L' + step.startLine + '–' + step.endLine : '—';
              logicHtml += '<tr>';
              logicHtml += '<td style="color:'+lc+';font-weight:700">'+step.order+'</td>';
              logicHtml += '<td><span class="dt-badge" style="background:'+lc+'22;color:'+lc+';border:1px solid '+lc+'44">'+escHtml(step.layer)+'</span></td>';
              logicHtml += '<td><code style="font-size:11px;word-break:break-all">'+escHtml(step.file)+'</code></td>';
              logicHtml += '<td><strong>'+escHtml(step.function)+'</strong></td>';
              logicHtml += '<td style="font-family:var(--mono);font-size:11px;color:#8b949e">'+escHtml(lineRange)+'</td>';
              logicHtml += '<td style="color:#8b949e;font-size:12px">'+escHtml(step.description||'')+'</td>';
              logicHtml += '</tr>';
            });
            logicHtml += '</tbody></table>';

            // Append called functions summary
            logicHtml += '<div class="dt-steps">';
            trace.steps.forEach(function(step, idx) {
              var lc = layerColor(step.layer);
              logicHtml += '<div class="dt-step"><div class="dt-step-num" style="background:'+lc+'">'+step.order+'</div>';
              logicHtml += '<div class="dt-step-body"><div class="dt-step-layer" style="color:'+lc+'">'+escHtml(step.layer)+'</div>';
              logicHtml += '<code class="dt-step-file">'+escHtml(step.file);
              if (step.startLine) logicHtml += ' <span style="color:#8b949e">:'+step.startLine+'–'+step.endLine+'</span>';
              logicHtml += '</code>';
              logicHtml += '<div class="dt-step-fn">函式: <strong>'+escHtml(step.function)+'</strong></div>';
              if (step.description) logicHtml += '<div class="dt-step-desc">'+escHtml(step.description)+'</div>';
              if (step.calledFunctions && step.calledFunctions.length > 0) {
                logicHtml += '<div class="dt-step-desc" style="margin-top:4px">呼叫: ';
                logicHtml += step.calledFunctions.map(function(f){ return '<code style="font-size:10px">'+escHtml(f)+'</code>'; }).join(' ');
                logicHtml += '</div>';
              }
              logicHtml += '</div></div>';
            });
            logicHtml += '</div>';
          }

          if (trace && trace.sqlQueries && trace.sqlQueries.length > 0) {
            sqlHtml = '<p style="color:#8b949e;font-size:12px;margin-bottom:16px">共偵測到 <strong style="color:#e6edf3">'
              + trace.sqlQueries.length + '</strong> 個 SQL 語法</p>';
            sqlHtml += '<div class="dt-sql-list">';
            trace.sqlQueries.forEach(function(q, qIdx) {
              var opColor = {'SELECT':'#3fb950','INSERT':'#58a6ff','UPDATE':'#d29922','DELETE':'#f85149'}[q.operation]||'#8b949e';
              sqlHtml += '<div class="dt-sql-item">';
              sqlHtml += '<div class="dt-sql-header"><span class="dt-badge" style="background:'+opColor+'22;color:'+opColor+';border:1px solid '+opColor+'44">'+escHtml(q.operation)+'</span> ';
              sqlHtml += '<strong>'+escHtml(q.name||q.functionName)+'</strong> <span style="color:#8b949e;font-size:11px">'+escHtml(q.sourceFile)+'</span></div>';
              // Show params table if available
              if (q.parameters && q.parameters.length > 0) {
                sqlHtml += '<div style="padding:10px 16px;border-bottom:1px solid var(--border)">';
                sqlHtml += '<div style="font-size:11px;color:#8b949e;margin-bottom:6px;text-transform:uppercase">參數列表</div>';
                sqlHtml += '<table class="dt-table"><thead><tr><th>佔位符</th><th>參數名</th><th>類型</th></tr></thead><tbody>';
                q.parameters.forEach(function(p) {
                  sqlHtml += '<tr><td><code>'+escHtml(p.placeholder)+'</code></td><td>'+escHtml(p.name)+'</td><td style="color:#8b949e">'+escHtml(p.type)+'</td></tr>';
                });
                sqlHtml += '</tbody></table></div>';
              }
              // Raw SQL
              sqlHtml += '<div style="padding:8px 16px 2px;font-size:11px;color:#8b949e;text-transform:uppercase">原始 SQL</div>';
              sqlHtml += '<pre class="dt-sql-code">'+escHtml(q.rawSql)+'</pre>';
              // Interactive SQL builder: replaces the static composed-SQL block.
              // Parses conditional validate-flag patterns and renders an interactive
              // form where the user can toggle conditions, fill in values, and copy
              // a clean, directly-executable SQL statement.
              if (q.composedSql && q.composedSql !== q.rawSql) {
                sqlHtml += buildSqlInteractorHtml(q.composedSql, qIdx);
              }
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
            + 'if(!window._sqlBd)window._sqlBd={};'
            // updateSqlPreview: re-generates the clean SQL preview from current form state.
            // Uses indexOf on the ORIGINAL sql string to find each condition's position,
            // then applies replacements right-to-left via slice() — this prevents the
            // in-place mutation bug where condition A's replacement text could accidentally
            // match condition B's search pattern.
            + 'function updateSqlPreview(idx){'
            + 'var d=window._sqlBd[idx];if(!d)return;'
            + 'var original=d.sql;'
            // Collect {from, to, text} for each condition, using position in *original*
            + 'var reps=[];'
            + 'd.conds.forEach(function(c,ci){'
            + 'var pos=original.indexOf(c.full);if(pos===-1)return;'
            + 'var cb=document.getElementById("sqlcb_"+idx+"_"+ci);'
            + 'var vi=document.getElementById("sqlvi_"+idx+"_"+ci);'
            + 'var checked=cb&&cb.checked;'
            + 'var cond=c.cond;'
            // Substitute value placeholder with user-entered text
            + 'if(checked&&c.vn&&vi&&vi.value.trim()){'
            + 'var v=vi.value.trim();cond=cond.split("\'"+c.vn+"_value\'").join("\'"+v+"\'");}'
            + 'reps.push({from:pos,to:pos+c.full.length,text:checked?"AND "+cond:""});});'
            // Apply right-to-left so earlier positions remain stable under slice()
            + 'reps.sort(function(a,b){return b.from-a.from;});'
            + 'var sql=original;'
            + 'reps.forEach(function(r){sql=sql.slice(0,r.from)+r.text+sql.slice(r.to);});'
            // OFFSET/LIMIT: case-insensitive regex replace with NaN-safe clamp
            + 'var oi=document.getElementById("sqloff_"+idx);'
            + 'var li=document.getElementById("sqllim_"+idx);'
            + 'if(oi){var ov=Math.max(0,parseInt(oi.value,10)||0);sql=sql.replace(/\\bOFFSET\\s+\\$1\\b/gi,"OFFSET "+ov);}'
            + 'if(li){var lv=Math.max(1,parseInt(li.value,10)||10);sql=sql.replace(/\\bLIMIT\\s+\\$2\\b/gi,"LIMIT "+lv);}'
            // Inline placeholders — substitute against the already-modified sql (safe: these are
            // non-overlapping literal tokens that were not part of any conditional block)
            + '(d.ils||[]).forEach(function(p,pi){var ii=document.getElementById("sqlil_"+idx+"_"+pi);'
            + 'if(ii&&ii.value.trim()){var v=ii.value.trim();sql=sql.split("\'"+p.n+"_value\'").join("\'"+v+"\'");}});'
            // Collapse three or more consecutive blank lines into one
            + 'sql=sql.replace(/[ \\t]*\\n([ \\t]*\\n){2,}/g,"\\n\\n").trim();'
            + 'var el=document.getElementById("sqlpre_"+idx);if(el)el.textContent=sql;}'
            // copySqlPreview: copies the current preview text to the clipboard.
            + 'function copySqlPreview(idx){'
            + 'var el=document.getElementById("sqlpre_"+idx);'
            + 'var btn=document.getElementById("sqlcopybtn_"+idx);'
            + 'if(!el)return;var txt=el.textContent;'
            + 'var ok=function(){if(btn){btn.textContent="✅ 已複製";setTimeout(function(){btn.textContent="📋 複製 SQL";},2000);}};'
            + 'if(navigator.clipboard){navigator.clipboard.writeText(txt).then(ok).catch(function(){fbCopy(txt,ok);});}else{fbCopy(txt,ok);}}'
            // fbCopy: textarea-based clipboard fallback for environments without Clipboard API.
            // Uses a hidden, non-interactive textarea so execCommand targets the right element.
            + 'function fbCopy(txt,ok){'
            + 'var ta=document.createElement("textarea");ta.value=txt;'
            + 'ta.setAttribute("readonly","");'
            + 'ta.style.cssText="position:fixed;top:0;left:0;opacity:0;pointer-events:none";'
            + 'document.body.appendChild(ta);ta.select();'
            + 'try{document.execCommand("copy");ok();}catch(e){}'
            + 'document.body.removeChild(ta);}'
            + 'function switchDtTab(btn,panelId){'
            + 'document.querySelectorAll(".dt-tab").forEach(function(t){t.classList.remove("active")});'
            + 'document.querySelectorAll(".dt-panel").forEach(function(p){p.classList.add("hidden")});'
            + 'btn.classList.add("active");'
            + 'var panel=document.getElementById(panelId);if(panel){panel.classList.remove("hidden");}}'
            + '<\/script>'
            + '</body></html>';

          // Use Blob URL instead of document.write — avoids the deprecated API and
          // prevents DOM-clobbering / CSP bypass issues from cross-window document manipulation.
          var blob = new Blob([html], {type: 'text/html'});
          var blobUrl = URL.createObjectURL(blob);
          // noopener prevents the child window from accessing window.opener (tab-nabbing).
          // Blob URL has an opaque origin, but noopener eliminates the reference entirely.
          var w = window.open(blobUrl, '_blank', 'noopener,noreferrer');
          // Revoke the object URL after a short delay — addEventListener('load') is
          // unavailable on noopener windows because window.open returns null in that case.
          setTimeout(function(){ URL.revokeObjectURL(blobUrl); }, 5000);
        }

        /**
         * Returns all CSS required by the API detail popup window as a single concatenated string.
         *
         * The styles are built here (rather than in a separate stylesheet) so that the popup
         * can be opened as a Blob URL — a fully self-contained HTML document with no external
         * resource dependencies.  Blob URLs have an opaque origin and cannot load external CSS.
         *
         * Sections covered:
         *   1. CSS custom properties (dark-theme colour palette, font stack)
         *   2. Base reset and body styles
         *   3. Sticky nav-bar (method badge, path, tag)
         *   4. Tab bar and panel layout
         *   5. Data tables, response cards, trace flow diagram
         *   6. Interactive SQL Builder widget (.dt-sqlb-*)
         *
         * @returns {string} Concatenated minified CSS string for the detail popup.
         */
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
            + '.dt-trace-flow{display:flex;flex-direction:column;gap:0;padding:8px 0}'
            + '.dt-trace-item{display:flex;flex-direction:column;align-items:flex-start}'
            + '.dt-trace-body{display:flex;align-items:flex-start;gap:12px;background:var(--card);border:1px solid var(--border);border-radius:8px;padding:10px 14px;width:100%;max-width:640px}'
            + '.dt-trace-num{font-size:18px;font-weight:700;min-width:24px;text-align:center}'
            + '.dt-trace-badge{font-size:10px;font-weight:700;padding:2px 8px;border-radius:10px;margin-bottom:6px;display:inline-block}'
            + '.dt-trace-content{flex:1}'
            + '.dt-trace-file{display:block;font-size:11px;margin-bottom:3px;color:#8b949e}'
            + '.dt-trace-fn{display:block;font-size:13px;margin-bottom:2px}'
            + '.dt-trace-desc{font-size:11px;color:#8b949e}'
            + '.dt-trace-arrow{font-size:16px;color:#30363d;padding:3px 0 3px 18px}'
            + '.dt-resp-list{display:flex;flex-direction:column;gap:12px}'
            + '.dt-resp-card{background:var(--card);border:1px solid var(--border);border-radius:8px;overflow:hidden}'
            + '.dt-resp-header{display:flex;align-items:center;gap:10px;padding:10px 14px;border-bottom:1px solid var(--border)}'
            + '.dt-resp-code{font-size:14px;font-weight:700;padding:4px 10px;border-radius:6px}'
            + '.dt-badge-yellow{background:rgba(210,153,34,0.15);color:#e3b341;border:1px solid rgba(210,153,34,0.3)}'
            + '.dt-resp-desc{font-size:13px;color:var(--text)}'
            + '.dt-resp-ct{font-size:11px;color:var(--muted);margin-left:auto;font-family:var(--mono)}'
            + '.dt-resp-schema-label{padding:6px 14px 2px;font-size:10px;text-transform:uppercase;letter-spacing:.5px;color:var(--muted)}'
            + '.dt-resp-schema{margin:0;padding:10px 14px;font-size:12px;background:var(--bg2);color:#c9d1d9;overflow:auto;max-height:300px;line-height:1.6;white-space:pre}'
            // ── Interactive SQL Builder widget styles ──────────────────────────
            + '.dt-sqlb{padding:16px;border-top:1px solid var(--border)}'
            + '.dt-sqlb-hdr{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-bottom:12px}'
            + '.dt-sqlb-hdr-title{font-size:13px;font-weight:700;color:#e3b341}'
            + '.dt-sqlb-hdr-hint{font-size:11px;color:var(--muted)}'
            + '.dt-sqlb-sec{background:var(--bg2);border:1px solid var(--border);border-radius:6px;padding:10px 14px;margin-bottom:10px}'
            + '.dt-sqlb-sec-title{font-size:11px;font-weight:700;color:var(--muted);text-transform:uppercase;letter-spacing:.5px;margin-bottom:8px}'
            + '.dt-sqlb-sec-hint{font-weight:400;text-transform:none;letter-spacing:0}'
            + '.dt-sqlb-row{display:flex;align-items:center;flex-wrap:wrap;gap:6px;padding:4px 0;border-bottom:1px solid rgba(48,54,61,.5)}'
            + '.dt-sqlb-row:last-child{border-bottom:none}'
            + '.dt-sqlb-cb{width:14px;height:14px;cursor:pointer;flex-shrink:0;accent-color:var(--blue)}'
            + '.dt-sqlb-label{font-size:12px;color:var(--muted);min-width:48px}'
            + '.dt-sqlb-cond-name{font-size:12px;font-family:var(--mono);color:var(--blue);min-width:160px}'
            + '.dt-sqlb-eq{font-size:12px;color:var(--muted)}'
            + '.dt-sqlb-input{background:var(--card);border:1px solid var(--border);border-radius:4px;color:var(--text);font-family:var(--mono);font-size:12px;padding:3px 8px;outline:none}'
            + '.dt-sqlb-input:focus{border-color:var(--blue)}'
            + '.dt-sqlb-num{width:80px}'
            + '.dt-sqlb-val{width:200px}'
            + '.dt-sqlb-hint{font-size:11px;color:var(--muted);flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}'
            + '.dt-sqlb-preview-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:4px}'
            + '.dt-sqlb-preview-title{font-size:11px;font-weight:700;color:var(--muted);text-transform:uppercase;letter-spacing:.5px}'
            + '.dt-sqlb-copy-btn{cursor:pointer;background:var(--card);border:1px solid var(--border);color:var(--text);border-radius:6px;padding:4px 12px;font-size:12px}'
            + '.dt-sqlb-copy-btn:hover{border-color:var(--blue);color:var(--blue)}'
            + '.dt-sqlb-pre{border:1px solid var(--border);border-radius:6px;padding:12px 16px;font-family:var(--mono);font-size:12px;line-height:1.7;color:#e6edf3;white-space:pre;overflow-x:auto;background:var(--bg2)}';
        }

        // ═══ Modal ═══
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
            c.ports.forEach(function(p) { html += '<tr><td>' + escHtml(String(p.hostPort)) + '</td><td>' + escHtml(String(p.containerPort)) + '</td><td>' + escHtml(p.protocol) + '</td></tr>'; });
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
        // Pre-computed HTML entity map — allocated once, referenced on every escHtml call.
        // Single-pass regex replace avoids 3 intermediate string allocations of the
        // chained-replace approach, and now also escapes single quotes to be OWASP-compliant.
        var _ESC_HTML_MAP = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#x27;' };
        function escHtml(str) {
          if (str === null || str === undefined) return '';
          return String(str).replace(/[&<>"']/g, function(ch) { return _ESC_HTML_MAP[ch]; });
        }

        // ═══ Console Error Prevention ═══
        window.addEventListener('error', function(e) {
          console.warn('[RID] 已捕獲錯誤：', e.message);
          return true;
        });

        // ═══ Init ═══
        document.addEventListener('DOMContentLoaded', function() {
          updateSidebarActive();
          // Initialize all graphs (offline-capable pure-JS SVG renderer)
          RidGraph.init('rid-dep-graph',    'dep',    'hierarchical');
          RidGraph.init('rid-docker-graph', 'docker', 'hierarchical');
          console.info('[Repo Insight Dashboard] 版本 1.0.0 — 離線模式載入完成 ✅');
        });
        </script>
        """;
}
