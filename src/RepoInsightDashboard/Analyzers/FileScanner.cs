using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Recursively walks a repository's directory tree, respects .gitignore rules,
/// and returns a flat list of <see cref="FileNode"/> objects with language tags.
/// Symlink-escape and depth-overflow protection are built in.
/// </summary>
public class FileScanner
{
    // Files that must be included even if .gitignore would exclude them.
    private static readonly HashSet<string> ForceInclude = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "service.swagger.json", "copilot-instructions.md"
    };

    // Maximum directory depth to recurse to, preventing stack overflow on pathological repos.
    private const int MaxDepth = 60;

    /// <summary>
    /// Scans the repository at <paramref name="repoPath"/> and returns a tree representation
    /// alongside a flat list of all discovered files (directories excluded).
    /// </summary>
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

    private void ScanDirectory(string basePath, string currentPath, FileNode parentNode,
        List<FileNode> allFiles, GitignoreParser parser, int depth)
    {
        // Guard against infinite recursion or symlink loops.
        if (depth > MaxDepth) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(currentPath).OrderBy(d => d))
            {
                var relativePath = Path.GetRelativePath(basePath, dir).Replace('\\', '/');

                // Reject symlinks that point outside the repo root to prevent directory
                // traversal via crafted ln -s /etc repo/link attacks (CWE-22).
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        var resolved = Path.GetFullPath(target.FullName);
                        if (!resolved.StartsWith(basePath, StringComparison.Ordinal))
                            continue;
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
                    Language = LanguageDetector.GetLanguage(ext)
                               ?? LanguageDetector.GetLanguageForFile(ext, fileName)
                };
                parentNode.Children.Add(fileNode);
                allFiles.Add(fileNode);
            }
        }
        catch (IOException) { /* skip inaccessible or unreadable directories */ }
    }

    private static bool IsForceIncludedPath(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return ForceInclude.Contains(fileName);
    }
}
