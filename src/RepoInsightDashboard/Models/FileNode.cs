namespace RepoInsightDashboard.Models;

public class FileNode
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string AbsolutePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public string? Language { get; set; }
    public string Extension { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public List<FileNode> Children { get; set; } = [];
}
