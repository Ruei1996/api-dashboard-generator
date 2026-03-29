using System.CommandLine;
using RepoInsightDashboard.Generators;
using RepoInsightDashboard.Services;

namespace RepoInsightDashboard.Commands;

public static class AnalyzeCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<DirectoryInfo>(
            name: "repo-path",
            description: "要分析的程式碼庫路徑（預設為當前目錄）",
            getDefaultValue: () => new DirectoryInfo(Directory.GetCurrentDirectory()));

        var outputOpt = new Option<DirectoryInfo?>(
            aliases: ["--output", "-o"],
            description: "輸出目錄（預設：~/Downloads/api-dashboard-result/）");

        var tokenOpt = new Option<string?>(
            aliases: ["--copilot-token"],
            description: "GitHub Copilot API Token（可選，啟用 AI 語義分析）");

        var themeOpt = new Option<string>(
            aliases: ["--theme"],
            description: "Dashboard 主題：dark | light | auto",
            getDefaultValue: () => "dark");
        themeOpt.FromAmong("dark", "light", "auto");

        var noAiOpt = new Option<bool>(
            aliases: ["--no-ai"],
            description: "跳過 Copilot 語義分析");

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "顯示詳細分析日誌");

        var cmd = new Command("analyze", "分析程式碼庫並生成 Dashboard")
        {
            pathArg, outputOpt, tokenOpt, themeOpt, noAiOpt, verboseOpt
        };

        cmd.SetHandler(async (repoDir, outputDir, token, theme, noAi, verbose) =>
        {
            await RunAsync(repoDir, outputDir, noAi ? null : token, theme, verbose);
        }, pathArg, outputOpt, tokenOpt, themeOpt, noAiOpt, verboseOpt);

        return cmd;
    }

    private static async Task RunAsync(
        DirectoryInfo repoDir,
        DirectoryInfo? outputDir,
        string? copilotToken,
        string theme,
        bool verbose)
    {
        if (!repoDir.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[錯誤] 路徑不存在：{repoDir.FullName}");
            Console.ResetColor();
            Environment.Exit(1);
        }

        // Determine output directory
        var outputPath = outputDir?.FullName
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
               "Downloads", "api-dashboard-result");

        Directory.CreateDirectory(outputPath);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"""
            ┌─────────────────────────────────────┐
            │  Repo Insight Dashboard v1.0.0       │
            │  分析目標：{repoDir.FullName.PadRight(27)}│
            │  輸出目錄：{outputPath.PadRight(27)}│
            └─────────────────────────────────────┘
            """);
        Console.ResetColor();

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            if (!verbose) Console.Write("分析中 ");

            var orchestrator = new AnalysisOrchestrator(verbose);
            var data = await orchestrator.AnalyzeAsync(repoDir.FullName, theme, copilotToken, cts.Token);

            if (!verbose) Console.WriteLine(" 完成！");

            // Sanitize names for filesystem
            var projectName = SanitizeFileName(data.Meta.ProjectName);
            var branchName  = SanitizeFileName(data.Meta.Branch);

            var htmlFile = Path.Combine(outputPath, $"{projectName}-dashboard-({branchName}).html");
            var jsonFile = Path.Combine(outputPath, $"{projectName}-dashboard-meta-data-({branchName}).json");

            // Canonicalize both paths and assert they are still inside the chosen output
            // directory — prevents path-traversal if repo name contains '..' segments (CWE-22).
            var canonicalOutput = Path.GetFullPath(outputPath) + Path.DirectorySeparatorChar;
            htmlFile = Path.GetFullPath(htmlFile);
            jsonFile = Path.GetFullPath(jsonFile);
            if (!htmlFile.StartsWith(canonicalOutput, StringComparison.Ordinal) ||
                !jsonFile.StartsWith(canonicalOutput, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Derived output file path escapes the output directory. " +
                    "Check that the repository name and branch do not contain '..' segments.");

            // Generate HTML
            Console.Write("生成 HTML Dashboard...");
            var htmlGen = new HtmlDashboardGenerator();
            var html = htmlGen.Generate(data);
            await File.WriteAllTextAsync(htmlFile, html, System.Text.Encoding.UTF8, cts.Token);
            Console.WriteLine($" ✅");

            // Generate JSON
            Console.Write("生成 JSON 元資料...");
            var jsonGen = new JsonMetadataGenerator();
            var json = jsonGen.Generate(data);
            await File.WriteAllTextAsync(jsonFile, json, System.Text.Encoding.UTF8, cts.Token);
            Console.WriteLine($" ✅");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"""

                ═══════════════════════════════════════
                ✅ 分析完成！
                ───────────────────────────────────────
                📊 HTML: {htmlFile}
                📄 JSON: {jsonFile}
                ───────────────────────────────────────
                統計：
                  • 語言：{data.Project.Languages.Count} 種
                  • 套件：{data.Packages.Count} 個
                  • API：{data.ApiEndpoints.Count} 個端點
                  • 服務：{data.Containers.Count} 個容器
                  • 風險：{data.SecurityRisks.Count} 項
                ═══════════════════════════════════════
                """);
            Console.ResetColor();

            // Try to open in browser on macOS/Linux
            TryOpenBrowser(htmlFile);
        }
        catch (OperationCanceledException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine("\n[警告] 分析超時（5分鐘限制），輸出可能不完整。");
            Console.ResetColor();
            Environment.Exit(2);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[錯誤] {ex.Message}");
            if (verbose) Console.Error.WriteLine(ex.StackTrace);
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Union([':', '/', '\\', '*', '?']).ToArray();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim('_');
    }

    private static void TryOpenBrowser(string filePath)
    {
        try
        {
            var url = new Uri(filePath).AbsoluteUri;
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { /* non-critical */ }
    }
}
