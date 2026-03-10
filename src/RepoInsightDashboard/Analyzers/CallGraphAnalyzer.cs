using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class CallGraphAnalyzer
{
    private static readonly int MaxNodes = 80;

    public CallGraph Analyze(List<FileNode> files)
    {
        var graph = new CallGraph();
        var codeFiles = files.Where(f => !f.IsDirectory && IsCodeFile(f)).Take(100).ToList();

        foreach (var file in codeFiles)
        {
            try
            {
                var language = file.Language;
                if (language == null) continue;

                var content = File.ReadAllText(file.AbsolutePath);
                var nodes = language switch
                {
                    "C#" => ExtractCSharpNodes(file, content),
                    "Go" => ExtractGoNodes(file, content),
                    "Java" or "Kotlin" => ExtractJavaNodes(file, content),
                    "TypeScript" or "JavaScript" => ExtractJsNodes(file, content),
                    "Python" => ExtractPythonNodes(file, content),
                    _ => []
                };

                graph.Nodes.AddRange(nodes);
                if (graph.Nodes.Count >= MaxNodes) break;
            }
            catch { /* skip */ }
        }

        // Build call edges from method invocations
        BuildCallEdges(graph, files);

        return graph;
    }

    private List<CallNode> ExtractCSharpNodes(FileNode file, string content)
    {
        var nodes = new List<CallNode>();
        var classMatch = Regex.Match(content, @"(?:public|internal|private|protected)?\s+(?:partial\s+)?(?:class|interface|record)\s+(\w+)");
        var className = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(file.Name);

        var methodMatches = Regex.Matches(content,
            @"(?:public|private|protected|internal|static|async|override|virtual)\s+(?:[\w<>\[\]]+\s+)+(\w+)\s*\(([^)]*)\)");
        foreach (Match match in methodMatches)
        {
            var lineCount = content[..match.Index].Count(c => c == '\n') + 1;
            nodes.Add(new CallNode
            {
                Id = $"{file.RelativePath}::{className}::{match.Groups[1].Value}",
                Name = $"{className}.{match.Groups[1].Value}",
                FilePath = file.RelativePath,
                LineNumber = lineCount,
                Type = "method",
                Namespace = className
            });
        }
        return nodes;
    }

    private List<CallNode> ExtractGoNodes(FileNode file, string content)
    {
        var nodes = new List<CallNode>();
        var funcMatches = Regex.Matches(content, @"func\s+(?:\([^)]+\)\s+)?(\w+)\s*\(");
        foreach (Match match in funcMatches)
        {
            var lineCount = content[..match.Index].Count(c => c == '\n') + 1;
            nodes.Add(new CallNode
            {
                Id = $"{file.RelativePath}::{match.Groups[1].Value}",
                Name = match.Groups[1].Value,
                FilePath = file.RelativePath,
                LineNumber = lineCount,
                Type = "function"
            });
        }
        return nodes;
    }

    private List<CallNode> ExtractJavaNodes(FileNode file, string content)
    {
        var nodes = new List<CallNode>();
        var classMatch = Regex.Match(content, @"(?:public|private)?\s+(?:class|interface|enum)\s+(\w+)");
        var className = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(file.Name);

        var methodMatches = Regex.Matches(content,
            @"(?:public|private|protected|static|final|synchronized)\s+(?:[\w<>]+\s+)+(\w+)\s*\(");
        foreach (Match match in methodMatches)
        {
            var lineCount = content[..match.Index].Count(c => c == '\n') + 1;
            nodes.Add(new CallNode
            {
                Id = $"{file.RelativePath}::{className}::{match.Groups[1].Value}",
                Name = $"{className}.{match.Groups[1].Value}",
                FilePath = file.RelativePath,
                LineNumber = lineCount,
                Type = "method",
                Namespace = className
            });
        }
        return nodes;
    }

    private List<CallNode> ExtractJsNodes(FileNode file, string content)
    {
        var nodes = new List<CallNode>();
        var funcMatches = Regex.Matches(content,
            @"(?:export\s+)?(?:async\s+)?function\s+(\w+)\s*\(|const\s+(\w+)\s*=\s*(?:async\s*)?\(|(?:class\s+\w+[^{]*\{[^}]*?)(\w+)\s*\([^)]*\)\s*\{");
        foreach (Match match in funcMatches)
        {
            var name = match.Groups[1].Value.IsNullOrWhitespace()
                ? match.Groups[2].Value.IsNullOrWhitespace() ? match.Groups[3].Value : match.Groups[2].Value
                : match.Groups[1].Value;
            if (string.IsNullOrEmpty(name)) continue;

            var lineCount = content[..match.Index].Count(c => c == '\n') + 1;
            nodes.Add(new CallNode
            {
                Id = $"{file.RelativePath}::{name}",
                Name = name,
                FilePath = file.RelativePath,
                LineNumber = lineCount,
                Type = "function"
            });
        }
        return nodes;
    }

    private List<CallNode> ExtractPythonNodes(FileNode file, string content)
    {
        var nodes = new List<CallNode>();
        var funcMatches = Regex.Matches(content, @"def\s+(\w+)\s*\(");
        foreach (Match match in funcMatches)
        {
            var lineCount = content[..match.Index].Count(c => c == '\n') + 1;
            nodes.Add(new CallNode
            {
                Id = $"{file.RelativePath}::{match.Groups[1].Value}",
                Name = match.Groups[1].Value,
                FilePath = file.RelativePath,
                LineNumber = lineCount,
                Type = "function"
            });
        }
        return nodes;
    }

    private void BuildCallEdges(CallGraph graph, List<FileNode> files)
    {
        if (graph.Nodes.Count == 0) return;
        var nodeNames = graph.Nodes
            .GroupBy(n => n.Name.Split('.').Last())
            .ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var node in graph.Nodes.Take(30))
        {
            try
            {
                var file = files.FirstOrDefault(f => f.RelativePath == node.FilePath);
                if (file == null) continue;

                var content = File.ReadAllText(file.AbsolutePath);
                var lines = content.Split('\n');
                var startLine = Math.Max(0, node.LineNumber - 1);
                var endLine = Math.Min(lines.Length, startLine + 30);
                var methodBody = string.Join("\n", lines[startLine..endLine]);

                foreach (var (name, targetId) in nodeNames)
                {
                    if (targetId == node.Id) continue;
                    if (Regex.IsMatch(methodBody, $@"\b{Regex.Escape(name)}\s*\("))
                    {
                        graph.Edges.Add(new CallEdge
                        {
                            Caller = node.Id,
                            Callee = targetId,
                            LineNumber = node.LineNumber
                        });
                    }
                }
            }
            catch { /* skip */ }
        }
    }

    private static bool IsCodeFile(FileNode file) =>
        file.Language is "C#" or "Go" or "Java" or "Kotlin" or "TypeScript" or "JavaScript" or "Python" or "Rust";
}

internal static class StringExt
{
    public static bool IsNullOrWhitespace(this string? s) => string.IsNullOrWhiteSpace(s);
}
