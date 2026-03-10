using System.CommandLine;
using RepoInsightDashboard.Commands;

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
