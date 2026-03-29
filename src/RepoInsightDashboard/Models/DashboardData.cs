namespace RepoInsightDashboard.Models;

public class DashboardData
{
    public MetaInfo Meta { get; set; } = new();
    public ProjectInfo Project { get; set; } = new();
    public DependencyGraph DependencyGraph { get; set; } = new();
    public List<PackageDependency> Packages { get; set; } = [];
    public List<ApiEndpoint> ApiEndpoints { get; set; } = [];
    public List<ApiTrace> ApiTraces { get; set; } = [];
    public List<ContainerInfo> Containers { get; set; } = [];
    public DockerfileInfo? Dockerfile { get; set; }
    public List<EnvVariable> EnvVariables { get; set; } = [];
    public FileNode FileTree { get; set; } = new();
    public string? CopilotSummary { get; set; }
    public List<string> DesignPatterns { get; set; } = [];
    public List<SecurityRisk> SecurityRisks { get; set; } = [];
    public List<string> StartupSequence { get; set; } = [];
    public TestSuiteInfo Tests { get; set; } = new();
}

public class MetaInfo
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string ToolVersion { get; set; } = "1.0.0";
    public string ProjectName { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;
    public string Theme { get; set; } = "dark";
}

public class SecurityRisk
{
    public string Level { get; set; } = "info"; // info, warning, high, critical
    public int Priority { get; set; } = 4;       // 1=Critical, 2=High, 3=Medium/Warning, 4=Info
    public string Category { get; set; } = "";    // e.g. "A1-注入攻擊", "A3-敏感資料暴露"
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public List<string> AffectedFiles { get; set; } = [];
}
