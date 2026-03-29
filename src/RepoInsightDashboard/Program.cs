using System.CommandLine;
using RepoInsightDashboard.Commands;
using RepoInsightDashboard.Services;

// Load .env file (CWD first, then ~/.config/rid/.env) before anything else so that
// GITHUB_COPILOT_TOKEN and COPILOT_MODEL are available to all subsystems.
// Real OS environment variables are never overwritten (priority: OS env > .env file > defaults).
var envFile = DotEnvLoader.Load();
if (envFile != null)
    // Diagnostics always go to stderr so piped stdout output stays clean (CWE-200).
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
