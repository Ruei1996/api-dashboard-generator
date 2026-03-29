// ============================================================
// Program.cs — rid CLI entry point
// ============================================================
// Architecture: top-level statements (C# 9+), single executable entry point.
// Responsibilities: (1) bootstrap env-var loading, (2) wire up System.CommandLine tree,
//                  (3) delegate to AnalyzeCommand for all real work.
// ============================================================

using System.CommandLine;
using RepoInsightDashboard.Commands;
using RepoInsightDashboard.Services;

// DotEnvLoader.Load() MUST be called before any other application code — including
// System.CommandLine argument parsing — because:
//   1. The analyze command reads GITHUB_COPILOT_TOKEN and COPILOT_MODEL from the
//      environment.  System.CommandLine resolves default values from env vars at
//      parse time, so the token must already be present in the environment by then.
//   2. If Load() were called inside AnalyzeCommand.Handler, any System.CommandLine
//      --copilot-token default that relies on the env var would silently be null.
//   3. DotEnvLoader.Load() is idempotent and thread-safe, so calling it here is safe
//      even in test harnesses that invoke Program.Main multiple times.
var envFile = DotEnvLoader.Load();
if (envFile != null)
    // Diagnostics are written to stderr (Console.Error), not stdout (Console.WriteLine).
    // This keeps the stdout stream clean for callers that pipe rid's output into
    // jq, grep, or CI log parsers, and prevents info-level noise from being
    // mistaken for JSON/HTML output (CWE-200 — information exposure through log files).
    Console.Error.WriteLine($"[rid] Loaded config from: {envFile}");

var rootCmd = new RootCommand("Repo Insight Dashboard — 程式碼庫語義分析工具")
{
    AnalyzeCommand.Build()
};

// Default: show help if no subcommand given
rootCmd.SetHandler(() =>
{
    Console.WriteLine("使用方式: rid analyze <repo-path> [options]");
    Console.WriteLine("執行 'rid analyze --help' 查看所有選項");
});

return await rootCmd.InvokeAsync(args);
