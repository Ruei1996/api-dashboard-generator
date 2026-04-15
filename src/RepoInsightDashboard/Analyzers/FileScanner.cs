// ============================================================
// FileScanner.cs — Repository file tree walker
// ============================================================
// Architecture: stateless service class; instantiated once per analyze run.
// Pattern: recursive DFS with gitignore filtering and forced-include overrides.
//
// Security:
//   Symlink targets are resolved and validated against the repo root before
//   recursion, preventing directory-traversal via crafted symlinks (CWE-22).
//
// Usage:
//   var scanner = new FileScanner();
//   var (tree, allFiles) = scanner.Scan("/path/to/repo");
// ============================================================

using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Recursively walks a repository's directory tree, applies .gitignore filtering,
/// and returns both a hierarchical <see cref="FileNode"/> tree and a flat file list.
/// </summary>
/// <remarks>
/// <para>
/// Files listed in <c>ForceInclude</c> are always returned regardless of .gitignore rules.
/// This ensures that files critical to the analysis (e.g. <c>.env</c>, <c>service.swagger.json</c>)
/// are never silently skipped, even if the developer added them to .gitignore.
/// </para>
/// <para>
/// Symlinks that resolve outside the repository root are silently skipped to prevent
/// directory-traversal attacks via crafted <c>ln -s /etc repo/link</c> paths (CWE-22).
/// </para>
/// <para>
/// The recursive descent is capped at <c>MaxDepth = 60</c> levels to guard against
/// pathological monorepos or maliciously deep directory structures that could cause
/// a <see cref="StackOverflowException"/>.
/// </para>
/// </remarks>
public class FileScanner
{
    // Files that must be included even if .gitignore would exclude them.
    // These are files the rid tool specifically needs to function correctly.
    private static readonly HashSet<string> ForceInclude = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "service.swagger.json", "copilot-instructions.md",
        "Makefile", "GNUmakefile", "makefile"
    };

    // Maximum directory depth to recurse into.
    // 60 levels covers any realistic repository layout (monorepos, deeply nested packages)
    // while preventing a stack overflow if a symlink loop somehow bypasses the reparse-point
    // check below, or if the repo contains an unusually deep generated directory structure.
    private const int MaxDepth = 60;

    /// <summary>
    /// Scans the repository rooted at <paramref name="repoPath"/> and returns its file tree
    /// alongside a flat list of all discovered non-directory nodes.
    /// </summary>
    /// <param name="repoPath">
    /// Absolute path to the repository root.  Must exist and be a directory.
    /// </param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    ///   <item><see cref="FileNode"/> <c>Tree</c> — the root node of the hierarchical tree.</item>
    ///   <item><c>List&lt;FileNode&gt;</c> <c>AllFiles</c> — flat list of every file node (directories excluded).</item>
    /// </list>
    /// </returns>
    public (FileNode Tree, List<FileNode> AllFiles) Scan(string repoPath)
    {
        var parser = new GitignoreParser(repoPath);
        var allFiles = new List<FileNode>();
        var root = new FileNode
        {
            Name = Path.GetFileName(repoPath),
            RelativePath = "",
            AbsolutePath = repoPath,
            IsDirectory = true
        };

        ScanDirectory(repoPath, repoPath, root, allFiles, parser, depth: 0);
        return (root, allFiles);
    }

    /// <summary>
    /// Recursively populates <paramref name="parentNode"/> and <paramref name="allFiles"/>
    /// by iterating the contents of <paramref name="currentPath"/>.
    /// </summary>
    /// <param name="basePath">Absolute path to the repository root (unchanged across recursion).</param>
    /// <param name="currentPath">Absolute path to the directory currently being processed.</param>
    /// <param name="parentNode">The <see cref="FileNode"/> that will receive child entries.</param>
    /// <param name="allFiles">Accumulator for all discovered file nodes (directories excluded).</param>
    /// <param name="parser">Gitignore rule engine seeded from the repository root.</param>
    /// <param name="depth">Current recursion depth; compared against <see cref="MaxDepth"/>.</param>
    private void ScanDirectory(string basePath, string currentPath, FileNode parentNode,
        List<FileNode> allFiles, GitignoreParser parser, int depth)
    {
        // MaxDepth guard: abort recursion if the directory tree is unreasonably deep.
        // This prevents a StackOverflowException on pathological repos and limits CPU
        // time spent traversing auto-generated or infinitely-symlinked structures.
        if (depth > MaxDepth) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(currentPath).OrderBy(d => d))
            {
                var relativePath = Path.GetRelativePath(basePath, dir).Replace('\\', '/');

                // Symlink escape prevention (CWE-22 — Path Traversal):
                // A crafted repository could contain a symlink like:
                //   repo/evil -> /etc
                // Without this check, ScanDirectory would descend into /etc and expose
                // system files.  We resolve the symlink's final target and ensure it
                // stays inside basePath before recursing.
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        var resolved = Path.GetFullPath(target.FullName);
                        // Ensure the trailing separator is present before the prefix check.
                        // Without it, "/repo-name-evil".StartsWith("/repo-name") would be true,
                        // allowing a symlink to a sibling directory to escape the repo root guard.
                        // StringComparison.Ordinal is intentional: byte-exact to prevent
                        // case-folding or Unicode normalisation tricks.
                        var safeBase = basePath.TrimEnd(Path.DirectorySeparatorChar)
                                      + Path.DirectorySeparatorChar;
                        if (!resolved.StartsWith(safeBase, StringComparison.Ordinal))
                            continue; // Target escapes repo root — skip entirely.
                    }
                }

                if (!IsForceIncludedPath(relativePath) && parser.IsIgnored(relativePath + "/"))
                    continue;

                var dirNode = new FileNode
                {
                    Name = Path.GetFileName(dir),
                    RelativePath = relativePath,
                    AbsolutePath = dir,
                    IsDirectory = true
                };
                parentNode.Children.Add(dirNode);
                ScanDirectory(basePath, dir, dirNode, allFiles, parser, depth + 1);
            }

            foreach (var file in Directory.GetFiles(currentPath).OrderBy(f => f))
            {
                var relativePath = Path.GetRelativePath(basePath, file).Replace('\\', '/');
                var fileName = Path.GetFileName(file);

                var isForced = IsForceIncludedPath(relativePath);
                if (!isForced && parser.IsIgnored(relativePath))
                    continue;

                var info = new FileInfo(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var fileNode = new FileNode
                {
                    Name = fileName,
                    RelativePath = relativePath,
                    AbsolutePath = file,
                    IsDirectory = false,
                    SizeBytes = info.Length,
                    Extension = ext,
                    LastModified = info.LastWriteTime,
                    // Try extension first; fall back to special filename detection.
                    // e.g. "Dockerfile" has no extension, so LanguageDetector.GetLanguage("")
                    // returns null and the filename-based overload handles it.
                    Language = LanguageDetector.GetLanguage(ext)
                               ?? LanguageDetector.GetLanguageForFile(ext, fileName)
                };
                parentNode.Children.Add(fileNode);
                allFiles.Add(fileNode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // IOException:               DirectoryNotFoundException, network path errors, race-deleted dirs.
            // UnauthorizedAccessException: permission-denied directories.
            // NOTE: UnauthorizedAccessException does NOT derive from IOException in .NET —
            //       both derive independently from SystemException — so both must be caught explicitly.
            // All of these are non-fatal: skip the unreadable entry and continue scanning.
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the file at <paramref name="relativePath"/> is in the
    /// <see cref="ForceInclude"/> set and must always be returned regardless of .gitignore.
    /// </summary>
    private static bool IsForceIncludedPath(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return ForceInclude.Contains(fileName);
    }
}
