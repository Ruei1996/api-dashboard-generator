using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public static class LanguageDetector
{
    private static readonly Dictionary<string, (string Name, string Color)> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs",    ("C#",         "#178600") },
        { ".go",    ("Go",         "#00ADD8") },
        { ".java",  ("Java",       "#b07219") },
        { ".kt",    ("Kotlin",     "#A97BFF") },
        { ".ts",    ("TypeScript", "#3178c6") },
        { ".tsx",   ("TypeScript", "#3178c6") },
        { ".js",    ("JavaScript", "#f1e05a") },
        { ".jsx",   ("JavaScript", "#f1e05a") },
        { ".mjs",   ("JavaScript", "#f1e05a") },
        { ".py",    ("Python",     "#3572A5") },
        { ".rs",    ("Rust",       "#dea584") },
        { ".rb",    ("Ruby",       "#701516") },
        { ".php",   ("PHP",        "#4F5D95") },
        { ".swift", ("Swift",      "#F05138") },
        { ".cpp",   ("C++",        "#f34b7d") },
        { ".c",     ("C",          "#555555") },
        { ".h",     ("C/C++",      "#555555") },
        { ".scala", ("Scala",      "#c22d40") },
        { ".sh",    ("Shell",      "#89e051") },
        { ".bash",  ("Shell",      "#89e051") },
        { ".ps1",   ("PowerShell", "#012456") },
        { ".yaml",  ("YAML",       "#cb171e") },
        { ".yml",   ("YAML",       "#cb171e") },
        { ".json",  ("JSON",       "#292929") },
        { ".xml",   ("XML",        "#0060ac") },
        { ".html",  ("HTML",       "#e34c26") },
        { ".css",   ("CSS",        "#563d7c") },
        { ".scss",  ("SCSS",       "#c6538c") },
        { ".sql",   ("SQL",        "#e38c00") },
        { ".md",    ("Markdown",   "#083fa1") },
        { ".tf",    ("Terraform",  "#7B42BC") },
        { ".proto", ("Protobuf",   "#4a4a4a") },
        { ".dart",  ("Dart",       "#00B4AB") },
    };

    public static string? GetLanguage(string extension)
        => ExtensionMap.TryGetValue(extension, out var info) ? info.Name : null;

    public static string GetColor(string extension)
        => ExtensionMap.TryGetValue(extension, out var info) ? info.Color : "#8b949e";

    public static List<LanguageInfo> Detect(List<Models.FileNode> files)
    {
        var groups = files
            .Where(f => !f.IsDirectory && f.Language != null)
            .GroupBy(f => f.Language!)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Size = g.Sum(f => f.SizeBytes),
                Color = GetColor(g.First().Extension)
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        var total = groups.Sum(g => g.Count);
        if (total == 0) return [];

        return groups.Select(g => new LanguageInfo
        {
            Name = g.Name,
            FileCount = g.Count,
            SizeBytes = g.Size,
            Percentage = Math.Round((double)g.Count / total * 100, 1),
            Color = g.Color
        }).ToList();
    }
}
