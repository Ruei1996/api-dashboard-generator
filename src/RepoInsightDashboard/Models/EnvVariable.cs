namespace RepoInsightDashboard.Models;

public class EnvVariable
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public string? Comment { get; set; }
}
