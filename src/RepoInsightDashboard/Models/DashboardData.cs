// ============================================================
// DashboardData.cs — Root aggregate model for all analysis results
// ============================================================
// Architecture: passive data object; fully populated by AnalysisOrchestrator
//   and then passed read-only to HtmlDashboardGenerator and CopilotSemanticAnalyzer.
//
// Design decisions:
//   • All collection properties are initialised to empty lists so consumers
//     never need null-guards; null is reserved for genuinely optional scalars.
//   • MetaInfo is a separate class (not inlined) to keep DashboardData focused
//     on analysis results and allow MetaInfo to be serialised independently
//     for future tooling integrations (e.g. CI badges, JSON export).
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Root aggregate model that holds all analysis results for a single repository.
/// Produced by <see cref="Services.AnalysisOrchestrator"/> and consumed by
/// <see cref="Generators.HtmlDashboardGenerator"/> to render the HTML dashboard.
/// </summary>
public class DashboardData
{
    /// <summary>Tool-level metadata: generation timestamp, tool version, colour theme, and repository path.</summary>
    public MetaInfo Meta { get; set; } = new();

    /// <summary>High-level project statistics: name, branch, language breakdown, total files and size.</summary>
    public ProjectInfo Project { get; set; } = new();

    /// <summary>Graph structure of package dependencies (nodes = packages, edges = declared relationships).</summary>
    public DependencyGraph DependencyGraph { get; set; } = new();

    /// <summary>All third-party packages discovered from go.mod, package.json, requirements.txt, pom.xml, etc.</summary>
    public List<PackageDependency> Packages { get; set; } = [];

    /// <summary>API endpoints discovered from OpenAPI specs, GraphQL schemas, and gRPC proto files.</summary>
    public List<ApiEndpoint> ApiEndpoints { get; set; } = [];

    /// <summary>
    /// Execution trace paths for up to 120 endpoints (Handler → Service → Repository → SQL).
    /// Populated by <see cref="Analyzers.ApiTraceAnalyzer"/> after <see cref="ApiEndpoints"/> is ready.
    /// </summary>
    public List<ApiTrace> ApiTraces { get; set; } = [];

    /// <summary>Container services discovered from docker-compose files, or synthesised from a Dockerfile.</summary>
    public List<ContainerInfo> Containers { get; set; } = [];

    /// <summary>Parsed Dockerfile metadata; <c>null</c> when no Dockerfile exists in the repository.</summary>
    public DockerfileInfo? Dockerfile { get; set; }

    /// <summary>All environment variables found in .env files and Dockerfile ENV instructions.</summary>
    public List<EnvVariable> EnvVariables { get; set; } = [];

    /// <summary>Root node of the hierarchical file tree produced by <see cref="Analyzers.FileScanner"/>.</summary>
    public FileNode FileTree { get; set; } = new();

    /// <summary>
    /// AI-generated or locally-templated project summary (Markdown-compatible).
    /// <c>null</c> only if generation failed unexpectedly; the local fallback normally ensures a value.
    /// </summary>
    public string? CopilotSummary { get; set; }

    /// <summary>Detected architectural design patterns (e.g. "微服務架構", "RESTful API", "容器化部署").</summary>
    public List<string> DesignPatterns { get; set; } = [];

    /// <summary>
    /// Prioritised security risks from static regex analysis and optional AI deep scan.
    /// Sorted by <see cref="SecurityRisk.Priority"/> (1 = critical … 4 = info).
    /// </summary>
    public List<SecurityRisk> SecurityRisks { get; set; } = [];

    /// <summary>
    /// Topologically-sorted container startup order derived from <c>depends_on</c> declarations.
    /// Services with no dependencies appear first; this is the order Docker Compose boots them.
    /// </summary>
    public List<string> StartupSequence { get; set; } = [];

    /// <summary>Unit, integration, and acceptance test files discovered in the repository.</summary>
    public TestSuiteInfo Tests { get; set; } = new();

    /// <summary>Content and target list of the repository's Makefile, or an auto-generated one when absent.</summary>
    public MakefileInfo? Makefile { get; set; }
}

/// <summary>
/// Tool-level metadata attached to every analysis run.
/// Embedded in the generated HTML as part of <c>window.__RID_DATA__.meta</c>
/// for display in the dashboard header.
/// </summary>
public class MetaInfo
{
    /// <summary>UTC timestamp when analysis completed; displayed in the dashboard Overview section.</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Semantic version of the <c>rid</c> CLI tool that produced this report.</summary>
    public string ToolVersion { get; set; } = "1.0.0";

    /// <summary>Repository directory basename (e.g. "my-api-service").</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Git branch name at the time of analysis (e.g. "main", "develop").</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>Absolute path to the repository root on the machine that ran the analysis.</summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>
    /// Dashboard colour theme: <c>"dark"</c> (default) or <c>"light"</c>.
    /// Stored here so the JavaScript theme-toggle can restore the initial preference
    /// on the first page load before the user interacts.
    /// </summary>
    public string Theme { get; set; } = "dark";
}

/// <summary>
/// Represents a single security risk identified by static analysis or AI code review,
/// aligned to the OWASP Top 10 category taxonomy.
/// </summary>
public class SecurityRisk
{
    /// <summary>
    /// Severity label used for CSS class selection: <c>"info"</c>, <c>"warning"</c>,
    /// <c>"high"</c>, or <c>"critical"</c>.
    /// Matches the <c>sec-row-{level}</c> CSS classes in the dashboard stylesheet.
    /// </summary>
    public string Level { get; set; } = "info"; // info, warning, high, critical

    /// <summary>
    /// Numeric priority for sort order: 1 = Critical (fix immediately), 2 = High,
    /// 3 = Medium / Warning, 4 = Info / Low.
    /// Lower values sort first; P1 critical risks appear at the top of the security table.
    /// </summary>
    public int Priority { get; set; } = 4;       // 1=Critical, 2=High, 3=Medium/Warning, 4=Info

    /// <summary>
    /// OWASP Top 10 (2021) category label displayed in the dashboard table
    /// (e.g. <c>"A1-注入攻擊"</c>, <c>"A3-敏感資料暴露"</c>, <c>"A7-身份驗證與存取控制失效"</c>).
    /// </summary>
    public string Category { get; set; } = "";    // e.g. "A1-注入攻擊", "A3-敏感資料暴露"

    /// <summary>Short, actionable title describing the risk (Traditional Chinese).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Detailed explanation of the risk, including evidence from the scanned files.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Concrete remediation guidance.  Non-empty for AI-analysed risks and for the
    /// seven built-in regex-pattern checks in <see cref="Analyzers.CopilotSemanticAnalyzer"/>.
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;

    /// <summary>
    /// Relative path of the primary source file where the risk was detected;
    /// <c>null</c> when the risk is project-wide (e.g. missing TLS configuration).
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>Specific line number within <see cref="FilePath"/>; <c>null</c> when line-level attribution is unavailable.</summary>
    public int? LineNumber { get; set; }

    /// <summary>
    /// Up to five relative file paths that exhibit the same risk pattern.
    /// Displayed as code tags in the security table's "相關檔案" column.
    /// </summary>
    public List<string> AffectedFiles { get; set; } = [];
}
