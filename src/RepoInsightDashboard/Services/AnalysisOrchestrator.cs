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

        // Single pass over allFiles for both count and size (avoids two LINQ enumerations).
        long totalSize = 0; int totalCount = 0;
        foreach (var f in allFiles) { if (f.IsDirectory) continue; totalCount++; totalSize += f.SizeBytes; }
        data.Project.TotalFiles     = totalCount;
        data.Project.TotalSizeBytes = totalSize;

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

        // 5–11. Run independent analyzers concurrently — none share mutable state.
        // This reduces wall-clock time from the sum of all analyzers to the slowest one.
        Log("[RID] 並行執行分析器...");
        var depAnalyzer    = new DependencyAnalyzer();
        var dockerAnalyzer = new DockerAnalyzer();
        var swaggerAnalyzer = new SwaggerAnalyzer();
        var envAnalyzer    = new EnvFileAnalyzer();
        var testAnalyzer   = new TestAnalyzer();

        var packagesTask    = Task.Run(() => depAnalyzer.Analyze(allFiles), ct);
        var containersTask  = Task.Run(() => dockerAnalyzer.Analyze(allFiles), ct);
        var dockerfileTask  = Task.Run(() => dockerAnalyzer.AnalyzeDockerfile(allFiles), ct);
        var swaggerTask     = Task.Run(() => swaggerAnalyzer.Analyze(allFiles), ct);
        var envTask         = Task.Run(() => envAnalyzer.Analyze(allFiles), ct);
        var testsTask       = Task.Run(() => testAnalyzer.Analyze(allFiles), ct);

        await Task.WhenAll(packagesTask, containersTask, dockerfileTask, swaggerTask, envTask, testsTask);

        data.Packages      = packagesTask.Result;
        data.DependencyGraph = depAnalyzer.BuildGraph(data.Packages, data.Project);
        Log($"[RID] 找到 {data.Packages.Count} 個套件");

        data.Dockerfile    = dockerfileTask.Result;
        data.Containers    = containersTask.Result;
        if (data.Containers.Count == 0 && data.Dockerfile != null)
        {
            data.Containers = dockerAnalyzer.SynthesizeContainersFromDockerfile(data.Dockerfile, projectName);
            Log($"[RID] 從 Dockerfile 合成 {data.Containers.Count} 個服務");
        }
        else Log($"[RID] 找到 {data.Containers.Count} 個容器服務");

        data.ApiEndpoints  = swaggerTask.Result;
        Log($"[RID] 找到 {data.ApiEndpoints.Count} 個 API 端點");

        data.EnvVariables  = envTask.Result;
        Log($"[RID] 找到 {data.EnvVariables.Count} 個環境變數");

        data.Tests         = testsTask.Result;
        Log($"[RID] 測試：{data.Tests.TotalTestCount} 個");

        // 9. API Trace Analysis (depends on ApiEndpoints — must run after swagger)
        Log("[RID] 追蹤 API 執行路徑...");
        var traceAnalyzer = new ApiTraceAnalyzer(repoPath, allFiles);
        data.ApiTraces = traceAnalyzer.AnalyzeTraces(data.ApiEndpoints);
        Log($"[RID] 完成 {data.ApiTraces.Count} 條 API 追蹤路徑");

        // 12. Startup Sequence
        data.StartupSequence = BuildStartupSequence(data);

        // 13. Copilot Semantic Analysis
        var copilot = new CopilotSemanticAnalyzer(copilotToken);
        if (copilot.IsAvailable)
            Log("[RID] 呼叫 GitHub Copilot API 進行語義分析...");
        else
            Log("[RID] Copilot token 未提供，使用本地分析...");

        // Run three independent Copilot API calls concurrently — reduces worst-case
        // latency from 180 s (3 × 60 s timeout in sequence) to ~60 s.
        var summaryTask  = copilot.GenerateProjectSummaryAsync(data, ct);
        var patternsTask = copilot.DetectDesignPatternsAsync(data, ct);
        var risksTask    = copilot.DetectSecurityRisksAsync(data, allFiles, repoPath, ct);
        await Task.WhenAll(summaryTask, patternsTask, risksTask);
        data.CopilotSummary  = summaryTask.Result;
        data.DesignPatterns  = patternsTask.Result;
        data.SecurityRisks   = risksTask.Result;

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
