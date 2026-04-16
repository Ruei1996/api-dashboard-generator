// ============================================================
// FileNode.cs — Hierarchical file-tree node model
// ============================================================
// Architecture: plain mutable data class; populated by FileScanner.
//   Instances form a tree (Children list) for the file-tree panel in the dashboard,
//   and are also collected into a flat list for O(1) file lookups by analyzers.
//
// Design note: using a mutable class (not record) because FileScanner builds the
//   tree incrementally and must update Children during the DFS walk.
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Represents a single file or directory node in the repository's file tree,
/// as produced by <see cref="Analyzers.FileScanner"/>.
/// </summary>
/// <remarks>
/// Each <see cref="FileNode"/> is simultaneously a member of the hierarchical tree
/// (via <see cref="Children"/>) and a member of the flat file list returned by
/// <c>FileScanner.Scan</c>.  All analyzers operate on the flat list for O(1) lookups;
/// the tree is used only by <see cref="Generators.HtmlDashboardGenerator"/> to render
/// the expandable file-tree panel.
/// </remarks>
public class FileNode
{
    /// <summary>File or directory name without any leading path (e.g. <c>"Program.cs"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Path relative to the repository root, using forward slashes
    /// (e.g. <c>"src/Services/AnalysisOrchestrator.cs"</c>).
    /// Used by analyzers as the canonical identifier for logging and output.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path on the host file system.
    /// Used for all disk I/O (File.ReadAllText, File.OpenRead, etc.).
    /// Never stored in the HTML output — only <see cref="RelativePath"/> is rendered.
    /// </summary>
    public string AbsolutePath { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> if this node represents a directory; <c>false</c> for a regular file.
    /// Directory nodes have a populated <see cref="Children"/> list and a
    /// <see cref="SizeBytes"/> value that is the sum of their descendants.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// File size in bytes for regular files; total child size for directories.
    /// Used for the progress bar in the file-tree panel and for size-based sorting.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Human-readable language name derived from the file extension by
    /// <see cref="Analyzers.LanguageDetector"/> (e.g. <c>"C#"</c>, <c>"TypeScript"</c>).
    /// <c>null</c> for binary files, configuration files, or unrecognised extensions.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Lower-cased file extension including the dot (e.g. <c>".cs"</c>, <c>".go"</c>).
    /// Empty string for files with no extension.
    /// Pre-computed by <see cref="Analyzers.FileScanner"/> to avoid repeated
    /// <c>Path.GetExtension</c> calls in hot loops.
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the last file write; used for staleness warnings in the dashboard.</summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Direct children of this directory node.  Empty for regular files.
    /// The tree is capped at <c>FileScanner.MaxDepth = 60</c> levels to prevent
    /// stack overflows on pathological directory structures.
    /// </summary>
    public List<FileNode> Children { get; set; } = [];
}
