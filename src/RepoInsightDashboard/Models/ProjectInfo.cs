// ============================================================
// ProjectInfo.cs — High-level repository metadata models
// ============================================================
// Architecture: plain data classes (no logic); ProjectInfo is populated by
//   AnalysisOrchestrator (Git metadata + file scan) and LanguageDetector.
//   LanguageInfo objects are sorted by file count (desc) and stored in
//   ProjectInfo.Languages for the language breakdown chart.
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// High-level metadata about the analysed repository, combining Git metadata
/// with aggregated file-scan statistics.
/// </summary>
public class ProjectInfo
{
    /// <summary>Repository directory basename (e.g. <c>"my-api-service"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the repository root on the analysis machine.</summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>Current Git branch name (e.g. <c>"main"</c>, <c>"develop"</c>).</summary>
    public string Branch { get; set; } = "main";

    /// <summary>SHA-1 hash of the most recent commit; <c>null</c> if Git is unavailable.</summary>
    public string? LastCommitHash { get; set; }

    /// <summary>Subject line of the most recent commit message; <c>null</c> if Git is unavailable.</summary>
    public string? LastCommitMessage { get; set; }

    /// <summary>Author name from the most recent commit; <c>null</c> if Git is unavailable.</summary>
    public string? LastCommitAuthor { get; set; }

    /// <summary>UTC timestamp of the most recent commit; <c>null</c> if Git is unavailable.</summary>
    public DateTime? LastCommitDate { get; set; }

    /// <summary>
    /// Total number of regular (non-directory) files discovered during the scan.
    /// Computed in a single pass by <see cref="Services.AnalysisOrchestrator"/> to avoid
    /// an extra O(n) LINQ enumeration.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Sum of all file sizes in bytes, computed in the same single pass as <see cref="TotalFiles"/>.
    /// Displayed in the dashboard Overview panel as a human-readable size (KB / MB / GB).
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Language breakdown, sorted by file count (largest first).
    /// Populated by <see cref="Analyzers.LanguageDetector.Detect"/> and used to render
    /// the language donut chart and progress bars.
    /// </summary>
    public List<LanguageInfo> Languages { get; set; } = [];

    /// <summary>
    /// Content of the repository's <c>.github/copilot-instructions.md</c> file (up to 512 KB).
    /// When present, this text is prepended to AI prompts in
    /// <see cref="Analyzers.CopilotSemanticAnalyzer"/> to give the model project-specific context.
    /// <c>null</c> when no such file exists.
    /// </summary>
    public string? CopilotInstructions { get; set; }
}

/// <summary>
/// Aggregated statistics for a single programming language detected in the repository.
/// </summary>
public class LanguageInfo
{
    /// <summary>Human-readable language name (e.g. <c>"TypeScript"</c>, <c>"Go"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Number of source files written in this language.</summary>
    public int FileCount { get; set; }

    /// <summary>Total size in bytes of all files written in this language.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Proportion of source files in this language as a percentage (0–100).
    /// Computed by <see cref="Analyzers.LanguageDetector.Detect"/> using file count,
    /// not byte size, to match GitHub's language breakdown convention.
    /// </summary>
    public double Percentage { get; set; }

    /// <summary>
    /// Hex colour string used for the dashboard chart (e.g. <c>"#178600"</c> for C#).
    /// Values mirror the GitHub Linguist colour palette so the chart matches
    /// what developers see on GitHub repository pages.
    /// </summary>
    public string Color { get; set; } = "#58a6ff";
}
