using LibGit2Sharp;
using RepoInsightDashboard.Analyzers;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Services;

public class AnalysisOrchestrator
{
    private readonly bool _verbose;

    public AnalysisOrchestrator(bool verbose = false) => _verbose = verbose;

    public async Task<DashboardData> AnalyzeAsync(
        string repoPath, string theme, string? copilotToken, CancellationToken ct = default)
    {
        repoPath = Path.GetFullPath(repoPath);
        Log($"[RID] 開始分析：{repoPath}");

        var data = new DashboardData();
        data.Meta.RepoPath = repoPath;
        data.Meta.Theme = theme;

        // 1. Git Info
        Log("[RID] 讀取 Git 資訊...");
        var (projectName, branch) = GetGitInfo(repoPath);
        data.Meta.ProjectName = projectName;
        data.Meta.Branch = branch;
        data.Project.Name = projectName;
        data.Project.Branch = branch;
        data.Project.RepoPath = repoPath;

        // 2. File Scan
        Log("[RID] 掃描檔案...");
        var scanner = new FileScanner();
        var (fileTree, allFiles) = scanner.Scan(repoPath);
        data.FileTree = fileTree;
        data.Project.TotalFiles = allFiles.Count(f => !f.IsDirectory);
        data.Project.TotalSizeBytes = allFiles.Where(f => !f.IsDirectory).Sum(f => f.SizeBytes);

        // 3. Language Detection
        Log("[RID] 識別語言...");
        data.Project.Languages = LanguageDetector.Detect(allFiles);

        // 4. Copilot Instructions
        var copilotInstructions = allFiles.FirstOrDefault(f =>
            f.RelativePath.Contains(".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase));
        if (copilotInstructions != null)
        {
            data.Project.CopilotInstructions = File.ReadAllText(copilotInstructions.AbsolutePath);
            Log("[RID] 已讀取 copilot-instructions.md");
        }

        // 5. Dependency Analysis
        Log("[RID] 分析依賴套件...");
        var depAnalyzer = new DependencyAnalyzer();
        data.Packages = depAnalyzer.Analyze(allFiles);
        data.DependencyGraph = depAnalyzer.BuildGraph(data.Packages, data.Project);
        Log($"[RID] 找到 {data.Packages.Count} 個套件");

        // 6. Docker Analysis
        Log("[RID] 分析 Docker 配置...");
        var dockerAnalyzer = new DockerAnalyzer();
        data.Containers = dockerAnalyzer.Analyze(allFiles);
        data.Dockerfile = dockerAnalyzer.AnalyzeDockerfile(allFiles);

        // Fallback: synthesize container from Dockerfile when no docker-compose
        if (data.Containers.Count == 0 && data.Dockerfile != null)
        {
            data.Containers = dockerAnalyzer.SynthesizeContainersFromDockerfile(data.Dockerfile, projectName);
            Log($"[RID] 從 Dockerfile 合成 {data.Containers.Count} 個服務");
        }
        else
        {
            Log($"[RID] 找到 {data.Containers.Count} 個容器服務");
        }

        // 8. Swagger/API Analysis
        Log("[RID] 解析 API 文件...");
        var swaggerAnalyzer = new SwaggerAnalyzer();
        data.ApiEndpoints = swaggerAnalyzer.Analyze(allFiles);
        Log($"[RID] 找到 {data.ApiEndpoints.Count} 個 API 端點");

        // 9. API Trace Analysis
        Log("[RID] 追蹤 API 執行路徑...");
        var traceAnalyzer = new ApiTraceAnalyzer(repoPath, allFiles);
        data.ApiTraces = traceAnalyzer.AnalyzeTraces(data.ApiEndpoints);
        Log($"[RID] 完成 {data.ApiTraces.Count} 條 API 追蹤路徑");

        // 10. Env Variables
        Log("[RID] 提取環境變數...");
        var envAnalyzer = new EnvFileAnalyzer();
        data.EnvVariables = envAnalyzer.Analyze(allFiles);
        Log($"[RID] 找到 {data.EnvVariables.Count} 個環境變數");

        // 11. Test Analysis
        Log("[RID] 分析測試檔案...");
        var testAnalyzer = new TestAnalyzer();
        data.Tests = testAnalyzer.Analyze(allFiles);
        Log($"[RID] 測試：{data.Tests.TotalTestCount} 個（單元:{data.Tests.UnitTests.Sum(f => f.TestCases.Count)} 整合:{data.Tests.IntegrationTests.Sum(f => f.TestCases.Count)} 驗收:{data.Tests.AcceptanceTests.Sum(f => f.TestCases.Count)}）");

        // 12. Startup Sequence
        data.StartupSequence = BuildStartupSequence(data);

        // 13. Copilot Semantic Analysis
        var copilot = new CopilotSemanticAnalyzer(copilotToken);
        if (copilot.IsAvailable)
            Log("[RID] 呼叫 GitHub Copilot API 進行語義分析...");
        else
            Log("[RID] Copilot token 未提供，使用本地分析...");

        data.CopilotSummary = await copilot.GenerateProjectSummaryAsync(data, ct);
        data.DesignPatterns = await copilot.DetectDesignPatternsAsync(data, ct);
        data.SecurityRisks = await copilot.DetectSecurityRisksAsync(data, allFiles, repoPath, ct);

        Log("[RID] 分析完成！");
        return data;
    }

    private (string name, string branch) GetGitInfo(string repoPath)
    {
        var name = Path.GetFileName(repoPath);
        var branch = "main";
        try
        {
            if (Repository.IsValid(repoPath))
            {
                using var repo = new Repository(repoPath);
                branch = repo.Head?.FriendlyName ?? "main";
                var last = repo.Commits.FirstOrDefault();
                if (last != null)
                    Log($"[RID] 最後提交：{last.MessageShort} by {last.Author.Name}");
            }
        }
        catch { }
        return (name, branch);
    }

    private List<string> BuildStartupSequence(DashboardData data)
    {
        if (data.Containers.Count == 0) return [];
        var visited = new HashSet<string>();
        var order = new List<string>();

        void Visit(string name)
        {
            if (!visited.Add(name)) return;
            var c = data.Containers.FirstOrDefault(x => x.Name == name);
            if (c != null) foreach (var dep in c.DependsOn) Visit(dep);
            order.Add(name);
        }
        foreach (var c in data.Containers) Visit(c.Name);
        return order;
    }

    private void Log(string message)
    {
        if (_verbose) Console.WriteLine(message);
        else Console.Write(".");
    }
}
