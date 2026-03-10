namespace RepoInsightDashboard.Models;

public class ProjectInfo
{
    public string Name { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string? LastCommitHash { get; set; }
    public string? LastCommitMessage { get; set; }
    public string? LastCommitAuthor { get; set; }
    public DateTime? LastCommitDate { get; set; }
    public int TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public List<LanguageInfo> Languages { get; set; } = [];
    public string? CopilotInstructions { get; set; }
}

public class LanguageInfo
{
    public string Name { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
    public double Percentage { get; set; }
    public string Color { get; set; } = "#58a6ff";
}
