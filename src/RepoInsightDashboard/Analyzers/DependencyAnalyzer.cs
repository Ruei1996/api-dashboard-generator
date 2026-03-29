using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Discovers and parses package manifests across all supported ecosystems
/// (NuGet, npm, Go Modules, Maven, Gradle, pip/Poetry, Cargo, Composer, Pub,
/// Hex/Mix, Swift PM, Rubygems) to produce a unified <see cref="PackageDependency"/> list.
/// Also builds a dependency graph capped at <see cref="MaxGraphNodes"/> for display.
/// </summary>
public class DependencyAnalyzer
{
    // Maximum number of packages rendered in the dependency graph to keep the UI readable.
    private const int MaxGraphNodes = 80;

    /// <summary>Scans all files and parses every recognised manifest into a flat dependency list.</summary>
    public List<PackageDependency> Analyze(List<FileNode> files)
    {
        var deps = new List<PackageDependency>();

        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            try
            {
                var results = file.Name.ToLowerInvariant() switch
                {
                    // JavaScript / Node.js
                    "package.json"              => ParsePackageJson(file),
                    // Go
                    "go.mod"                    => ParseGoMod(file),
                    // Python
                    "requirements.txt"          => ParseRequirementsTxt(file),
                    "pipfile"                   => ParsePipfile(file),
                    "pyproject.toml"            => ParsePyprojectToml(file),
                    "setup.py"                  => ParseSetupPy(file),
                    // Rust
                    "cargo.toml"                => ParseCargoToml(file),
                    // JVM
                    "pom.xml"                   => ParsePomXml(file),
                    "build.gradle"
                    or "build.gradle.kts"       => ParseGradle(file),
                    // Ruby
                    "gemfile"                   => ParseGemfile(file),
                    // PHP
                    "composer.json"             => ParseComposerJson(file),
                    // Dart / Flutter
                    "pubspec.yaml"              => ParsePubspecYaml(file),
                    // Elixir
                    "mix.exs"                   => ParseMixExs(file),
                    // Swift
                    "package.swift"             => ParsePackageSwift(file),
                    // .NET (matched by extension below)
                    _ when file.Extension is ".csproj" or ".vbproj" or ".fsproj"
                                                => ParseCsproj(file),
                    // Ruby gemspec
                    _ when file.Extension == ".gemspec"
                                                => ParseGemspec(file),
                    _                           => []
                };
                deps.AddRange(results);
            }
            catch { /* skip malformed manifests */ }
        }

        return deps;
    }

    // ── JavaScript / Node.js ─────────────────────────────────────────────────

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
        AddDeps(json["optionalDependencies"], "optional");
        return deps;
    }

    // ── Go ────────────────────────────────────────────────────────────────────

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

    // ── Python ────────────────────────────────────────────────────────────────

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

    private List<PackageDependency> ParsePipfile(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var section = "";
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var trimmed = line.Trim();
            if (trimmed == "[packages]") { section = "production"; continue; }
            if (trimmed == "[dev-packages]") { section = "dev"; continue; }
            if (trimmed.StartsWith('[')) { section = ""; continue; }
            if (string.IsNullOrEmpty(section) || trimmed.StartsWith('#')) continue;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;
            var name = trimmed[..eqIdx].Trim();
            var ver = trimmed[(eqIdx + 1)..].Trim().Trim('"', '\'', '*');
            deps.Add(new PackageDependency
            {
                Name = name, Version = ver, Type = section,
                SourceFile = file.RelativePath, Ecosystem = "pip"
            });
        }
        return deps;
    }

    private List<PackageDependency> ParsePyprojectToml(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var content = File.ReadAllText(file.AbsolutePath);

        // PEP 517/518 [project] dependencies = ["package>=version"]
        // Timeout guards against ReDoS on crafted TOML files (CWE-400).
        var pep517Match = Regex.Match(content,
            @"\[project\][\s\S]*?dependencies\s*=\s*\[([\s\S]*?)\]",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        if (pep517Match.Success)
        {
            foreach (Match m in Regex.Matches(pep517Match.Groups[1].Value,
                @"""([A-Za-z0-9_\-\.]+)([><=!~;][^""]*)?"""))
                deps.Add(new PackageDependency
                {
                    Name = m.Groups[1].Value, Version = m.Groups[2].Value.TrimStart('>', '<', '=', '~', '!').Trim(),
                    Type = "production", SourceFile = file.RelativePath, Ecosystem = "pip"
                });
        }

        // Poetry [tool.poetry.dependencies]
        var poetryMatch = Regex.Match(content,
            @"\[tool\.poetry\.dependencies\]([\s\S]*?)(?=\[|$)", RegexOptions.IgnoreCase);
        if (poetryMatch.Success)
            ParseTomlDepsSection(poetryMatch.Groups[1].Value, "production", file, deps, "pip");

        var poetryDevMatch = Regex.Match(content,
            @"\[tool\.poetry(?:\.group\.dev)?\.dev-dependencies\]([\s\S]*?)(?=\[|$)",
            RegexOptions.IgnoreCase);
        if (poetryDevMatch.Success)
            ParseTomlDepsSection(poetryDevMatch.Groups[1].Value, "dev", file, deps, "pip");

        return deps;
    }

    // Parses a TOML dependency section with three supported value formats:
    //   name = "version"               (quoted string)
    //   name = { version = "version" } (inline table)
    //   name = *                        (bare wildcard)
    private static void ParseTomlDepsSection(string section, string type,
        FileNode file, List<PackageDependency> deps, string ecosystem)
    {
        // Three alternations — see code-review issue #3 for why [^{] (one char) was wrong.
        const string pattern =
            @"^([A-Za-z0-9_\-\.]+)\s*=\s*" +
            @"(?:" +
                @"""([^""]*)""" +                              // alt 1: quoted version string
                @"|\{[^}]*?version\s*=\s*""([^""]*)""[^}]*\}" + // alt 2: inline table with version key
                @"|(\*)" +                                     // alt 3: bare wildcard
            @")";

        foreach (Match m in Regex.Matches(section, pattern, RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value;
            if (name.Equals("python", StringComparison.OrdinalIgnoreCase)) continue;
            var ver = m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Success ? m.Groups[3].Value
                    : m.Groups[4].Value; // "*"
            deps.Add(new PackageDependency
            {
                Name = name, Version = ver.TrimStart('^', '~', '>', '=', '<'),
                Type = type, SourceFile = file.RelativePath, Ecosystem = ecosystem
            });
        }
    }

    private List<PackageDependency> ParseSetupPy(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var content = File.ReadAllText(file.AbsolutePath);
        // install_requires=[...] or install_requires = [...]
        var m = Regex.Match(content, @"install_requires\s*=\s*\[([\s\S]*?)\]");
        if (!m.Success) return deps;
        foreach (Match pkg in Regex.Matches(m.Groups[1].Value,
            @"""([A-Za-z0-9_\-\.]+)([><=!~][^""]*)?"""))
            deps.Add(new PackageDependency
            {
                Name = pkg.Groups[1].Value,
                Version = pkg.Groups[2].Value.TrimStart('>', '<', '=', '~', '!'),
                Type = "production", SourceFile = file.RelativePath, Ecosystem = "pip"
            });
        return deps;
    }

    // ── Rust ──────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParseCargoToml(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var currentSection = "";
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var trimmed = line.Trim();
            if (trimmed == "[dependencies]") { currentSection = "production"; continue; }
            if (trimmed == "[dev-dependencies]") { currentSection = "dev"; continue; }
            if (trimmed == "[build-dependencies]") { currentSection = "build"; continue; }
            if (trimmed.StartsWith('[')) { currentSection = ""; continue; }
            if (string.IsNullOrEmpty(currentSection)) continue;

            var match = Regex.Match(trimmed, @"^([A-Za-z0-9_\-]+)\s*=\s*[""']?([\d\.\^~>=<\*]+)[""']?");
            if (match.Success)
                deps.Add(new PackageDependency
                {
                    Name = match.Groups[1].Value,
                    Version = match.Groups[2].Value,
                    Type = currentSection,
                    SourceFile = file.RelativePath,
                    Ecosystem = "cargo"
                });
        }
        return deps;
    }

    // Safe XML reader settings — disables DTD processing to prevent XXE injection attacks.
    private static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    // Helper that loads an XDocument from a path with XXE protection.
    private static XDocument SafeLoadXml(string path)
    {
        using var reader = XmlReader.Create(path, SafeXmlSettings);
        return XDocument.Load(reader);
    }

    // ── JVM ───────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParsePomXml(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var doc = SafeLoadXml(file.AbsolutePath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        foreach (var dep in doc.Descendants(ns + "dependency"))
        {
            var groupId    = dep.Element(ns + "groupId")?.Value ?? "";
            var artifactId = dep.Element(ns + "artifactId")?.Value ?? "";
            var version    = dep.Element(ns + "version")?.Value ?? "";
            var scope      = dep.Element(ns + "scope")?.Value ?? "production";

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
                @"(implementation|api|testImplementation|compileOnly|runtimeOnly)[(\s]+['""]([^:]+):([^:]+):([^'""\)]+)['""]");
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

    // ── .NET ──────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParseCsproj(FileNode file)
    {
        var doc = SafeLoadXml(file.AbsolutePath);
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

    // ── Ruby ──────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParseGemfile(FileNode file)
    {
        var deps = new List<PackageDependency>();
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;
            // gem 'sinatra', '~> 2.0'  /  gem "rspec", group: :test
            var m = Regex.Match(trimmed,
                @"gem\s+['""]([^'""]+)['""](?:\s*,\s*['""]([^'""]+)['""])?(?:.*?group:\s*:?(\w+))?");
            if (!m.Success) continue;
            var group = m.Groups[3].Success ? m.Groups[3].Value : "";
            deps.Add(new PackageDependency
            {
                Name = m.Groups[1].Value,
                Version = m.Groups[2].Success ? m.Groups[2].Value.TrimStart('~', '>', '=', '<', ' ') : "",
                Type = group is "test" or "development" ? "dev" : "production",
                SourceFile = file.RelativePath,
                Ecosystem = "rubygems"
            });
        }
        return deps;
    }

    private List<PackageDependency> ParseGemspec(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var content = File.ReadAllText(file.AbsolutePath);
        // s.add_runtime_dependency 'rack', '~> 2.0'
        foreach (Match m in Regex.Matches(content,
            @"\.add_(?:runtime_|development_)?dependency\s+['""]([^'""]+)['""](?:\s*,\s*['""]([^'""]*)['""])?"))
            deps.Add(new PackageDependency
            {
                Name = m.Groups[1].Value,
                Version = m.Groups[2].Success ? m.Groups[2].Value.TrimStart('~', '>', '=', '<', ' ') : "",
                Type = m.Value.Contains("development") ? "dev" : "production",
                SourceFile = file.RelativePath,
                Ecosystem = "rubygems"
            });
        return deps;
    }

    // ── PHP ───────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParseComposerJson(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var json = JObject.Parse(File.ReadAllText(file.AbsolutePath));

        void AddDeps(JToken? section, string type)
        {
            if (section is not JObject obj) return;
            foreach (var prop in obj.Properties())
            {
                if (prop.Name == "php") continue; // skip runtime version specifier
                deps.Add(new PackageDependency
                {
                    Name = prop.Name,
                    Version = prop.Value.ToString().TrimStart('^', '~', '>', '=', '<'),
                    Type = type,
                    SourceFile = file.RelativePath,
                    Ecosystem = "composer"
                });
            }
        }

        AddDeps(json["require"], "production");
        AddDeps(json["require-dev"], "dev");
        return deps;
    }

    // ── Dart / Flutter ────────────────────────────────────────────────────────

    private List<PackageDependency> ParsePubspecYaml(FileNode file)
    {
        var deps = new List<PackageDependency>();
        string? currentSection = null;
        foreach (var line in File.ReadAllLines(file.AbsolutePath))
        {
            var trimmed = line.TrimEnd();
            if (trimmed == "dependencies:") { currentSection = "production"; continue; }
            if (trimmed == "dev_dependencies:") { currentSection = "dev"; continue; }
            // Any top-level key resets the section
            if (trimmed.Length > 0 && !trimmed.StartsWith(' ') && !trimmed.StartsWith('\t'))
            { currentSection = null; continue; }
            if (currentSection == null) continue;

            // "  package_name: ^1.0.0"  or  "  flutter:\n    sdk: flutter"
            var m = Regex.Match(trimmed, @"^\s+([A-Za-z0-9_]+):\s*(.*)$");
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            var ver = m.Groups[2].Value.Trim().TrimStart('^', '~', '>', '=', '<');
            if (name is "sdk" or "flutter" || string.IsNullOrWhiteSpace(ver)) continue;
            deps.Add(new PackageDependency
            {
                Name = name, Version = ver,
                Type = currentSection, SourceFile = file.RelativePath, Ecosystem = "pub"
            });
        }
        return deps;
    }

    // ── Elixir ────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParseMixExs(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var content = File.ReadAllText(file.AbsolutePath);
        // defp deps do ... end  — extract the block
        var m = Regex.Match(content, @"defp?\s+deps\s+do([\s\S]*?)end");
        if (!m.Success) return deps;
        // {:phoenix, "~> 1.6"}  or  {:plug, "~> 1.0", only: :test}
        foreach (Match dep in Regex.Matches(m.Groups[1].Value,
            @"\{:(\w+),\s*""([^""]+)""(?:.*?only:\s*:?(\w+))?"))
            deps.Add(new PackageDependency
            {
                Name = dep.Groups[1].Value,
                Version = dep.Groups[2].Value.TrimStart('~', '>', '=', '<'),
                Type = dep.Groups[3].Success && dep.Groups[3].Value == "test" ? "dev" : "production",
                SourceFile = file.RelativePath,
                Ecosystem = "hex"
            });
        return deps;
    }

    // ── Swift ─────────────────────────────────────────────────────────────────

    private List<PackageDependency> ParsePackageSwift(FileNode file)
    {
        var deps = new List<PackageDependency>();
        var content = File.ReadAllText(file.AbsolutePath);
        // .package(url: "https://github.com/vapor/vapor.git", from: "4.0.0")
        foreach (Match m in Regex.Matches(content,
            @"\.package\s*\(\s*url:\s*""([^""]+)""\s*,\s*(?:from|exact|upToNextMajor|upToNextMinor):\s*""([^""]+)"""))
        {
            var url = m.Groups[1].Value;
            // Derive a short package name from the URL
            var name = Path.GetFileNameWithoutExtension(url.TrimEnd('/').Split('/').Last());
            deps.Add(new PackageDependency
            {
                Name = name,
                Version = m.Groups[2].Value,
                Type = "production",
                SourceFile = file.RelativePath,
                Ecosystem = "swiftpm"
            });
        }
        return deps;
    }

    // ── Dependency Graph ──────────────────────────────────────────────────────

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
        // Limit graph size so the dashboard SVG remains renderable.
        foreach (var pkg in packages.Take(MaxGraphNodes))
        {
            var nodeId = $"pkg_{pkg.Ecosystem}_{pkg.Name}"
                .Replace(".", "_").Replace("/", "_").Replace(":", "_");
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
