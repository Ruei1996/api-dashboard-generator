using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class FileScanner
{
    private static readonly HashSet<string> ForceInclude = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "service.swagger.json", "copilot-instructions.md"
    };

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

        ScanDirectory(repoPath, repoPath, root, allFiles, parser);
        return (root, allFiles);
    }

    private void ScanDirectory(string basePath, string currentPath, FileNode parentNode,
        List<FileNode> allFiles, GitignoreParser parser)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(currentPath).OrderBy(d => d))
            {
                var relativePath = Path.GetRelativePath(basePath, dir).Replace('\\', '/');

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
                ScanDirectory(basePath, dir, dirNode, allFiles, parser);
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
                    Language = LanguageDetector.GetLanguage(ext)
                };
                parentNode.Children.Add(fileNode);
                allFiles.Add(fileNode);
            }
        }
        catch (UnauthorizedAccessException) { /* skip */ }
    }

    private static bool IsForceIncludedPath(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return ForceInclude.Contains(fileName);
    }
}
