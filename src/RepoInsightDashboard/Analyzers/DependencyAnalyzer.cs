using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using RepoInsightDashboard.Models;
using YamlDotNet.Serialization;

namespace RepoInsightDashboard.Analyzers;

public class DependencyAnalyzer
{
    public List<PackageDependency> Analyze(List<FileNode> files)
    {
        var deps = new List<PackageDependency>();

        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            try
            {
                var results = file.Name.ToLowerInvariant() switch
                {
                    "package.json" => ParsePackageJson(file),
                    "go.mod" => ParseGoMod(file),
                    "requirements.txt" => ParseRequirementsTxt(file),
                    "cargo.toml" => ParseCargoToml(file),
                    "pom.xml" => ParsePomXml(file),
                    "build.gradle" or "build.gradle.kts" => ParseGradle(file),
                    _ when file.Extension == ".csproj" || file.Extension == ".vbproj" => ParseCsproj(file),
                    _ => []
                };
                deps.AddRange(results);
            }
            catch { /* skip malformed files */ }
        }

        return deps;
    }

    private List<PackageDependency> ParsePackageJson(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var json = JObject.Parse(File.ReadAllText(file.AbsolutePath));

        void AddDeps(JToken? section, string type)
        {
            if (section is not JObject obj) return;
            foreach (var prop in obj.Properties())
                deps.Add(new PackageDependency
                {
                    Name = prop.Name,
                    Version = prop.Value.ToString().TrimStart('^', '~', '>', '=', '<'),
                    Type = type,
                    SourceFile = file.RelativePath,
                    Ecosystem = "npm"
                });
        }

        AddDeps(json["dependencies"], "production");
        AddDeps(json["devDependencies"], "dev");
        AddDeps(json["peerDependencies"], "peer");
        return deps;
    }

    private List<PackageDependency> ParseGoMod(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var inRequire = false;
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var trimmed = line.Trim();
            if (trimmed == "require (") { inRequire = true; continue; }
            if (trimmed == ")") { inRequire = false; continue; }
            if (inRequire || trimmed.StartsWith("require "))
            {
                var content = trimmed.StartsWith("require ") ? trimmed[8..] : trimmed;
                var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && !parts[0].StartsWith("//"))
                    deps.Add(new PackageDependency
                    {
                        Name = parts[0],
                        Version = parts[1],
                        Type = "production",
                        SourceFile = file.RelativePath,
                        Ecosystem = "go"
                    });
            }
        }
        return deps;
    }

    private List<PackageDependency> ParseRequirementsTxt(FileNode file)
    {
        return File.ReadAllLines(file.AbsolutePath)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#') && !l.StartsWith('-'))
            .Select(l =>
            {
                var match = Regex.Match(l, @"^([A-Za-z0-9_\-\.]+)([><=!~]{0,2})([\d\.]+.*)?$");
                return new PackageDependency
                {
                    Name = match.Success ? match.Groups[1].Value : l,
                    Version = match.Success ? match.Groups[3].Value : "",
                    Type = "production",
                    SourceFile = file.RelativePath,
                    Ecosystem = "pip"
                };
            }).ToList();
    }

    private List<PackageDependency> ParseCargoToml(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var inDeps = false;
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var trimmed = line.Trim();
            if (trimmed == "[dependencies]" || trimmed == "[dev-dependencies]")
            {
                inDeps = true; continue;
            }
            if (trimmed.StartsWith('[')) { inDeps = false; continue; }
            if (!inDeps) continue;

            var match = Regex.Match(trimmed, @"^([A-Za-z0-9_\-]+)\s*=\s*[""']?([\d\.\^~>=<\*]+)[""']?");
            if (match.Success)
                deps.Add(new PackageDependency
                {
                    Name = match.Groups[1].Value,
                    Version = match.Groups[2].Value,
                    Type = "production",
                    SourceFile = file.RelativePath,
                    Ecosystem = "cargo"
                });
        }
        return deps;
    }

    private List<PackageDependency> ParsePomXml(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var doc = XDocument.Load(file.AbsolutePath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        foreach (var dep in doc.Descendants(ns + "dependency"))
        {
            var groupId = dep.Element(ns + "groupId")?.Value ?? "";
            var artifactId = dep.Element(ns + "artifactId")?.Value ?? "";
            var version = dep.Element(ns + "version")?.Value ?? "";
            var scope = dep.Element(ns + "scope")?.Value ?? "production";

            deps.Add(new PackageDependency
            {
                Name = $"{groupId}:{artifactId}",
                Version = version,
                Type = scope == "test" ? "dev" : "production",
                SourceFile = file.RelativePath,
                Ecosystem = "maven"
            });
        }
        return deps;
    }

    private List<PackageDependency> ParseGradle(FileNode file)
    {
        var deps = new List<PackageDependency>();
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var match = Regex.Match(line.Trim(),
                @"(implementation|api|testImplementation|compileOnly)[(\s]+['""]([^:]+):([^:]+):([^'""\)]+)['""]");
            if (!match.Success) continue;
            deps.Add(new PackageDependency
            {
                Name = $"{match.Groups[2].Value}:{match.Groups[3].Value}",
                Version = match.Groups[4].Value,
                Type = match.Groups[1].Value.StartsWith("test") ? "dev" : "production",
                SourceFile = file.RelativePath,
                Ecosystem = "gradle"
            });
        }
        return deps;
    }

    private List<PackageDependency> ParseCsproj(FileNode file)
    {
        var doc = XDocument.Load(file.AbsolutePath);
        return doc.Descendants("PackageReference")
            .Select(e => new PackageDependency
            {
                Name = e.Attribute("Include")?.Value ?? "",
                Version = e.Attribute("Version")?.Value ?? e.Element("Version")?.Value ?? "",
                Type = "production",
                SourceFile = file.RelativePath,
                Ecosystem = "nuget"
            })
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .ToList();
    }

    public DependencyGraph BuildGraph(List<PackageDependency> packages, ProjectInfo project)
    {
        var graph = new DependencyGraph();
        var rootId = "root";
        graph.Nodes.Add(new DependencyNode
        {
            Id = rootId,
            Name = project.Name,
            Version = "",
            Type = "internal",
            Description = "Root project"
        });

        var seen = new HashSet<string>();
        foreach (var pkg in packages.Take(50)) // Limit for readability
        {
            var nodeId = $"pkg_{pkg.Ecosystem}_{pkg.Name}".Replace(".", "_").Replace("/", "_").Replace(":", "_");
            if (!seen.Add(nodeId)) continue;

            graph.Nodes.Add(new DependencyNode
            {
                Id = nodeId,
                Name = pkg.Name,
                Version = pkg.Version,
                Type = "library",
                SourceFile = pkg.SourceFile
            });
            graph.Edges.Add(new DependencyEdge
            {
                From = rootId,
                To = nodeId,
                Label = pkg.Type
            });
        }
        return graph;
    }
}
