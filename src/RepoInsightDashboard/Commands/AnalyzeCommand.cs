// ============================================================
// AnalyzeCommand.cs — System.CommandLine "analyze" sub-command
// ============================================================
// Architecture: static command-builder; wires CLI arguments/options to
//   AnalysisOrchestrator and the two generators (HTML + JSON).
//
// CLI surface:
//   rid analyze <repo-path>
//       [--output/-o <dir>]
//       [--theme dark|light|auto]
//       [--no-ai]
//       [--verbose/-v]
//       [--copilot-token <token>]  ← DEPRECATED (CWE-214: token visible in ps aux)
//
// Security (CWE-22 — Path Traversal):
//   The derived output file paths (projectName + branchName) are canonicalised via
//   Path.GetFullPath and validated against the resolved output directory before any
//   File.WriteAll call, preventing an attacker-controlled repository name from
//   escaping the intended output location (e.g. "../../etc/cron.d/evil").
//
// Security (CWE-214 — Sensitive Information in Process Environment):
//   --copilot-token is deprecated; tokens passed on the CLI appear in process
//   tables (ps aux) and shell history.  Users are redirected to the
//   GITHUB_COPILOT_TOKEN environment variable or ~/.config/rid/.env instead.
// ============================================================

using System.CommandLine;
using RepoInsightDashboard.Generators;
using RepoInsightDashboard.Services;

namespace RepoInsightDashboard.Commands;

/// <summary>
/// Builds and registers the <c>analyze</c> sub-command for the <c>rid</c> CLI tool.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Build"/> once at startup to get a <see cref="Command"/> that can be
/// added to the root <see cref="System.CommandLine.RootCommand"/>.  All real work is
/// delegated to <see cref="Services.AnalysisOrchestrator"/>, which runs the analysis
/// pipeline and returns a fully-populated <see cref="Models.DashboardData"/>.
/// </para>
/// </remarks>
public static class AnalyzeCommand
{
    /// <summary>
    /// Creates and returns a configured <see cref="Command"/> named <c>analyze</c>
    /// with all its arguments, options, and handler wired up.
    /// </summary>
    /// <returns>
    /// A ready-to-use <see cref="Command"/> that can be attached to a
    /// <see cref="System.CommandLine.RootCommand"/>.
    /// </returns>
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
            description: "[DEPRECATED] 請改用 GITHUB_COPILOT_TOKEN 環境變數 — CLI 旗標會出現在 ps aux 和 shell history 中（CWE-214）");

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
            // Deprecation warning: --copilot-token exposes the token in process tables and
            // shell history. Advise users to switch to the env var instead.
            if (!string.IsNullOrEmpty(token))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine("[警告] --copilot-token 已棄用：token 會出現在 ps aux 和 shell history 中。");
                Console.Error.WriteLine("       請改用 GITHUB_COPILOT_TOKEN 環境變數或 ~/.config/rid/.env。");
                Console.ResetColor();
            }
            await RunAsync(repoDir, outputDir, noAi ? null : token, theme, verbose);
        }, pathArg, outputOpt, tokenOpt, themeOpt, noAiOpt, verboseOpt);

        return cmd;
    }

    /// <summary>
    /// Runs the full analysis pipeline, writes the HTML and JSON output files,
    /// and attempts to open the HTML file in the default browser.
    /// </summary>
    /// <param name="repoDir">Repository root directory (validated to exist before delegation).</param>
    /// <param name="outputDir">
    /// Target output directory; defaults to <c>~/Downloads/api-dashboard-result/</c>
    /// when <c>null</c>.
    /// </param>
    /// <param name="copilotToken">
    /// Optional Copilot API token.  <c>null</c> when <c>--no-ai</c> is set or when the
    /// flag was not supplied and no env var is present.
    /// </param>
    /// <param name="theme">Dashboard colour theme name ("dark", "light", or "auto").</param>
    /// <param name="verbose">When <c>true</c>, writes detailed progress lines to stdout.</param>
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

    /// <summary>
    /// Replaces characters that are illegal in file names (including platform-independent
    /// extras like <c>:</c>, <c>/</c>, <c>*</c>, <c>?</c>) with underscores.
    /// </summary>
    /// <param name="name">Raw string to sanitise (e.g. a branch name or project name).</param>
    /// <returns>
    /// A file-system-safe version of <paramref name="name"/> with invalid characters
    /// replaced by <c>_</c> and leading/trailing underscores stripped.
    /// </returns>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Union([':', '/', '\\', '*', '?']).ToArray();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim('_');
    }

    /// <summary>
    /// Attempts to open <paramref name="filePath"/> in the system's default browser.
    /// Uses <c>open</c> on macOS and <c>xdg-open</c> on Linux.
    /// Failures are silently swallowed because this is a convenience feature;
    /// the output files have already been written successfully at this point.
    /// </summary>
    /// <param name="filePath">Absolute path to the generated HTML dashboard file.</param>
    private static void TryOpenBrowser(string filePath)
    {
        try
        {
            var uri = new Uri(filePath);
            // Whitelist: only open file:// URIs that resolve to the expected output path.
            // Prevents a crafted repo-name from redirecting open/xdg-open to a non-file URL.
            if (!uri.IsFile) return;
            var url = uri.AbsoluteUri;
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { /* non-critical */ }
    }
}
