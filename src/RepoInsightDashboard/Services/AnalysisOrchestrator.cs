// ============================================================
// AnalysisOrchestrator.cs — Top-level analysis pipeline coordinator
// ============================================================
// Architecture: orchestrator / facade over all analyzer classes.
//   Follows the "fat coordinator, thin analyzers" pattern:
//   each analyzer is a stateless, single-responsibility class;
//   this class owns the sequencing, concurrency, and result assembly.
//
// Concurrency model:
//   Five analyzers (dependency, docker, swagger, env, tests) are independent
//   of each other's output and run concurrently via Task.WhenAll.
//   ApiTraceAnalyzer is intentionally excluded from that group because it
//   requires data.ApiEndpoints, which is produced by SwaggerAnalyzer.
//   Three Copilot API calls also run concurrently via a second Task.WhenAll.
//
// Usage:
//   var orchestrator = new AnalysisOrchestrator(verbose: true);
//   DashboardData data = await orchestrator.AnalyzeAsync(repoPath, "dark", token, ct);
// ============================================================

using LibGit2Sharp;
using RepoInsightDashboard.Analyzers;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Services;

/// <summary>
/// Coordinates the full repository analysis pipeline, executing independent analyzers
/// concurrently and assembling their results into a single <see cref="DashboardData"/> object.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline runs in three sequential phases:
/// <list type="number">
///   <item>
///     <b>Serial setup</b> — Git metadata, file scan, language detection, and Copilot instructions.
///     These must complete in order because later phases depend on the file list.
///   </item>
///   <item>
///     <b>Concurrent analyzers</b> — Dependency, Docker, Swagger, EnvFile, and Test analyzers
///     run in parallel via <c>Task.WhenAll</c>, then <c>ApiTraceAnalyzer</c> runs serially
///     because it requires the endpoint list produced by <c>SwaggerAnalyzer</c>.
///   </item>
///   <item>
///     <b>Concurrent Copilot calls</b> — Summary, design-pattern, and security-risk requests
///     are sent to the Copilot API concurrently to minimise wall-clock latency.
///   </item>
/// </list>
/// </para>
/// </remarks>
public class AnalysisOrchestrator
{
    private readonly bool _verbose;

    /// <summary>
    /// Initialises the orchestrator.
    /// </summary>
    /// <param name="verbose">
    /// When <c>true</c>, progress messages are written to stdout as full lines.
    /// When <c>false</c>, a single dot is printed per step to indicate liveness
    /// without flooding CI logs.
    /// </param>
    public AnalysisOrchestrator(bool verbose = false) => _verbose = verbose;

    /// <summary>
    /// Runs the complete repository analysis pipeline and returns a fully-populated
    /// <see cref="DashboardData"/> object ready for rendering.
    /// </summary>
    /// <param name="repoPath">
    /// Path to the repository root directory.  Relative paths are resolved to
    /// absolute via <see cref="Path.GetFullPath"/> before any file I/O occurs.
    /// </param>
    /// <param name="theme">
    /// Dashboard colour theme name (e.g. "dark", "light") stored in
    /// <see cref="DashboardData.Meta"/> and passed through to the renderer.
    /// </param>
    /// <param name="copilotToken">
    /// Optional GitHub Copilot API token.  When null or empty, all AI analysis
    /// falls back to local heuristics and no network calls are made.
    /// </param>
    /// <param name="ct">
    /// Cancellation token propagated to all async operations.  Pressing Ctrl+C
    /// will abort the pipeline cleanly; partially-computed results are discarded.
    /// </param>
    /// <returns>
    /// A <see cref="DashboardData"/> containing file tree, language breakdown,
    /// API endpoints, security risks, design patterns, and a project summary.
    /// </returns>
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

        // Single-pass file stats: iterate allFiles ONCE to compute both TotalFiles and TotalSizeBytes.
        // Two separate LINQ expressions (.Count() + .Sum()) would traverse the list twice — O(2n).
        // For large repos (50 k+ files) this halves the iteration cost of this step.
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

        // 5–10. Run five independent analyzers concurrently via Task.WhenAll.
        // None of these analyzers share mutable state, so they are safe to run in parallel.
        // Wall-clock time is bounded by the slowest analyzer instead of their sum:
        //   serial:   T(dep) + T(docker) + T(swagger) + T(env) + T(tests)
        //   parallel: max(T(dep), T(docker), T(swagger), T(env), T(tests))
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

        // ApiTraceAnalyzer runs AFTER the concurrent batch because it requires data.ApiEndpoints
        // (produced by SwaggerAnalyzer above).  Including it in the Task.WhenAll group would
        // cause a race condition: it might start before swaggerTask completes and operate on
        // an empty endpoint list, producing zero trace paths silently.
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

    /// <summary>
    /// Reads the repository's Git metadata (project name from directory, current branch name,
    /// and most recent commit message) using LibGit2Sharp.
    /// </summary>
    /// <param name="repoPath">Absolute path to the repository root.</param>
    /// <returns>
    /// A tuple of (name, branch) where name is the directory basename and branch is the
    /// friendly branch name, defaulting to "main" if the repository is invalid or bare.
    /// </returns>
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

    /// <summary>
    /// Performs a topological sort of container services based on their <c>depends_on</c>
    /// declarations and returns an ordered startup sequence.
    /// </summary>
    /// <param name="data">Dashboard data containing the container list with dependency edges.</param>
    /// <returns>
    /// Ordered list of container names from least-dependent to most-dependent.
    /// Returns an empty list when no containers are present.
    /// </returns>
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

    /// <summary>
    /// Writes a progress message to stdout (verbose mode) or a single dot (quiet mode).
    /// </summary>
    private void Log(string message)
    {
        if (_verbose) Console.WriteLine(message);
        else Console.Write(".");
    }
}
