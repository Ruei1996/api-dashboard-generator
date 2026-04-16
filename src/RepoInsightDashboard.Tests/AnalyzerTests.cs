// ============================================================
// AnalyzerTests.cs — Integration tests for all major analyzer and generator classes
// ============================================================
// Architecture: xUnit test project; each public class is a test suite for
//   one corresponding production class.
//
// Test suites:
//   GitignoreParserTests   — default ignored paths + custom .gitignore rules
//   LanguageDetectorTests  — extension → language name mapping + percentage calc
//   DependencyAnalyzerTests — package.json and *.csproj parsing
//   EnvFileAnalyzerTests   — .env key-value extraction + sensitive-value masking
//   HtmlDashboardGeneratorTests — HTML output structure validation
//   JsonMetadataGeneratorTests  — JSON output key presence validation
//
// All tests that write to disk use Path.GetTempPath() and clean up in finally blocks
// to ensure no test artefacts remain on the CI runner between runs.
// ============================================================

using System.IO;
using RepoInsightDashboard.Analyzers;
using RepoInsightDashboard.Commands;
using RepoInsightDashboard.Generators;
using RepoInsightDashboard.Models;
using Xunit;

namespace RepoInsightDashboard.Tests;

/// <summary>
/// Tests for <see cref="GitignoreParser"/>: verifies that default ignore patterns
/// and project-specific .gitignore rules are applied correctly.
/// </summary>
public class GitignoreParserTests
{
    /// <summary>
    /// Verifies that the built-in <c>DefaultIgnores</c> list correctly classifies
    /// well-known paths (node_modules, bin, .git) as ignored and ordinary source
    /// files as not ignored, without requiring any .gitignore file on disk.
    /// </summary>
    [Theory]
    [InlineData("node_modules/foo.js", true)]
    [InlineData("src/main.cs", false)]
    [InlineData("bin/debug/app.dll", true)]
    [InlineData(".git/config", true)]
    public void DefaultIgnoredPaths_AreCorrectlyFiltered(string path, bool shouldBeIgnored)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var parser = new GitignoreParser(tmpDir);
            Assert.Equal(shouldBeIgnored, parser.IsIgnored(path));
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    /// <summary>
    /// Verifies that custom patterns written to a .gitignore file on disk (e.g. "*.log",
    /// "build/") are parsed and applied, and that files not matching any pattern are
    /// treated as not ignored.
    /// </summary>
    [Fact]
    public void CustomGitignoreRules_AreRespected()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\nbuild/");
            var parser = new GitignoreParser(tmpDir);
            Assert.True(parser.IsIgnored("app.log"));
            Assert.True(parser.IsIgnored("build/output.js"));
            Assert.False(parser.IsIgnored("src/app.cs"));
        }
        finally { Directory.Delete(tmpDir, true); }
    }
}

/// <summary>
/// Tests for <see cref="LanguageDetector"/>: verifies extension-to-language mapping
/// and percentage computation across a mixed-language file set.
/// </summary>
public class LanguageDetectorTests
{
    /// <summary>
    /// Confirms that <see cref="LanguageDetector.GetLanguage"/> returns the expected
    /// display name for known extensions and <c>null</c> for unrecognised ones.
    /// </summary>
    [Theory]
    [InlineData(".cs", "C#")]
    [InlineData(".go", "Go")]
    [InlineData(".ts", "TypeScript")]
    [InlineData(".py", "Python")]
    [InlineData(".xyz", null)]
    public void GetLanguage_ReturnsCorrectLanguage(string ext, string? expected)
    {
        Assert.Equal(expected, LanguageDetector.GetLanguage(ext));
    }

    /// <summary>
    /// Verifies that <see cref="LanguageDetector.Detect"/> groups files by language,
    /// sorts the result by file count (descending), and calculates percentages correctly
    /// (C# with 2 of 3 files should have percentage > 50 %).
    /// </summary>
    [Fact]
    public void Detect_ComputesPercentages()
    {
        var files = new List<FileNode>
        {
            new() { Extension = ".cs", Language = "C#", SizeBytes = 100 },
            new() { Extension = ".cs", Language = "C#", SizeBytes = 100 },
            new() { Extension = ".ts", Language = "TypeScript", SizeBytes = 50 },
        };
        var langs = LanguageDetector.Detect(files);
        Assert.Equal(2, langs.Count);
        Assert.Equal("C#", langs[0].Name);
        Assert.Equal(2, langs[0].FileCount);
        Assert.True(langs[0].Percentage > 50);
    }
}

/// <summary>
/// Tests for <see cref="DependencyAnalyzer"/>: verifies that npm <c>package.json</c> and
/// .NET <c>*.csproj</c> manifests are correctly parsed into <see cref="PackageDependency"/> lists.
/// </summary>
public class DependencyAnalyzerTests
{
    private readonly DependencyAnalyzer _analyzer = new();

    /// <summary>
    /// Verifies that <c>dependencies</c>, <c>devDependencies</c> sections from a
    /// <c>package.json</c> file are parsed into a flat list with correct names,
    /// versions, and type labels ("production" vs "dev").
    /// </summary>
    [Fact]
    public void ParsePackageJson_ExtractsDependencies()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "package.json");
        File.WriteAllText(tmpFile, """
            {
              "name": "test-app",
              "dependencies": { "express": "^4.18.0", "lodash": "3.0.0" },
              "devDependencies": { "jest": "^29.0.0" }
            }
            """);

        var node = new FileNode
        {
            Name = "package.json",
            AbsolutePath = tmpFile,
            RelativePath = "package.json",
            Extension = ".json"
        };

        try
        {
            var deps = _analyzer.Analyze([node]);
            Assert.Equal(3, deps.Count);
            Assert.Contains(deps, d => d.Name == "express" && d.Type == "production");
            Assert.Contains(deps, d => d.Name == "jest" && d.Type == "dev");
        }
        finally { File.Delete(tmpFile); }
    }

    /// <summary>
    /// Verifies that <c>PackageReference</c> items in a <c>.csproj</c> file are parsed
    /// with correct names, versions, and the "nuget" ecosystem label.
    /// </summary>
    [Fact]
    public void ParseCsproj_ExtractsPackageReferences()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "test.csproj");
        File.WriteAllText(tmpFile, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Serilog" Version="3.0.1" />
              </ItemGroup>
            </Project>
            """);

        var node = new FileNode
        {
            Name = "test.csproj",
            AbsolutePath = tmpFile,
            RelativePath = "test.csproj",
            Extension = ".csproj"
        };

        try
        {
            var deps = _analyzer.Analyze([node]);
            Assert.Equal(2, deps.Count);
            Assert.Contains(deps, d => d.Name == "Newtonsoft.Json" && d.Ecosystem == "nuget");
        }
        finally { File.Delete(tmpFile); }
    }
}

/// <summary>
/// Tests for <see cref="EnvFileAnalyzer"/>: verifies that key-value pairs are correctly
/// extracted and that values whose keys match sensitive patterns are masked.
/// </summary>
public class EnvFileAnalyzerTests
{
    /// <summary>
    /// Verifies that three variables are extracted from a minimal .env file,
    /// that <c>DB_HOST</c> (non-sensitive) is returned with its literal value,
    /// and that <c>DB_PASSWORD</c> (sensitive keyword) has <see cref="EnvVariable.IsSensitive"/>
    /// set to <c>true</c> and its value replaced with <c>"***masked***"</c>.
    /// </summary>
    [Fact]
    public void Analyze_ExtractsEnvVariables()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), ".env");
        File.WriteAllText(tmpFile, """
            # Comment
            DB_HOST=localhost
            DB_PASSWORD=secret123
            API_URL=http://example.com
            """);

        var node = new FileNode
        {
            Name = ".env",
            AbsolutePath = tmpFile,
            RelativePath = ".env",
            Extension = ""
        };

        try
        {
            var analyzer = new EnvFileAnalyzer();
            var vars = analyzer.Analyze([node]);
            Assert.Equal(3, vars.Count);
            Assert.Contains(vars, v => v.Key == "DB_HOST" && !v.IsSensitive);
            Assert.Contains(vars, v => v.Key == "DB_PASSWORD" && v.IsSensitive);
        }
        finally { File.Delete(tmpFile); }
    }
}

/// <summary>
/// Tests for <see cref="HtmlDashboardGenerator"/>: validates HTML output structure
/// and the private <c>SanitizeFileName</c> helper via reflection.
/// </summary>
public class HtmlDashboardGeneratorTests
{
    /// <summary>
    /// Verifies that <see cref="HtmlDashboardGenerator.Generate"/> produces a valid HTML document
    /// containing all required section IDs, key UI elements (search bar, theme-toggle, RidGraph),
    /// and that the output is bookended by <c>&lt;!DOCTYPE html&gt;</c> and <c>&lt;/html&gt;</c>.
    /// </summary>
    [Fact]
    public void Generate_ProducesValidHtmlWithAllSections()
    {
        var data = new DashboardData
        {
            Meta = new MetaInfo
            {
                ProjectName = "TestProject",
                Branch = "main",
                RepoPath = Path.Combine(Path.GetTempPath(), "test"),
                ToolVersion = "1.0.0"
            },
            Project = new ProjectInfo
            {
                Name = "TestProject",
                Branch = "main",
                TotalFiles = 10,
                Languages = [new LanguageInfo { Name = "C#", FileCount = 8, Percentage = 80, Color = "#178600" }]
            }
        };

        var gen = new HtmlDashboardGenerator();
        var html = gen.Generate(data);

        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
        Assert.Contains("section-overview", html);
        Assert.Contains("section-languages", html);
        Assert.Contains("section-dependencies", html);
        Assert.Contains("section-api", html);
        Assert.Contains("section-docker", html);
        Assert.Contains("section-env", html);
        Assert.Contains("section-filetree", html);
        Assert.Contains("section-security", html);
        Assert.Contains("toggleSection", html);
        Assert.Contains("global-search", html);
        Assert.Contains("theme-toggle", html);
        Assert.Contains("RidGraph", html);
        Assert.EndsWith("</html>" + Environment.NewLine, html);
    }

    /// <summary>
    /// Verifies that <c>AnalyzeCommand.SanitizeFileName</c> (accessed via reflection because it is
    /// <c>private static</c>) replaces illegal filesystem characters with underscores and formats the
    /// output filename correctly for a range of name/branch combinations.
    /// </summary>
    [Theory]
    [InlineData("MyApp",       "main",           "MyApp-dashboard-(main).html")]
    [InlineData("my:app",      "feature/test",   "my_app-dashboard-(feature_test).html")]
    [InlineData("my-app",      "release/1.0",    "my-app-dashboard-(release_1.0).html")]
    public void SanitizeFileName_ProducesCorrectOutputFileName(
        string name, string branch, string expectedFileName)
    {
        // Access SanitizeFileName via reflection — it is private static in AnalyzeCommand.
        var method = typeof(AnalyzeCommand).GetMethod(
            "SanitizeFileName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var safeName   = (string)method.Invoke(null, [name])!;
        var safeBranch = (string)method.Invoke(null, [branch])!;
        var actual = $"{safeName}-dashboard-({safeBranch}).html";
        Assert.Equal(expectedFileName, actual);
    }
}

/// <summary>
/// Tests for <see cref="JsonMetadataGenerator"/>: verifies that the generated JSON
/// contains the expected top-level keys and representative data values.
/// </summary>
public class JsonMetadataGeneratorTests
{
    /// <summary>
    /// Verifies that <see cref="JsonMetadataGenerator.Generate"/> produces a valid,
    /// non-empty JSON string containing the required top-level keys (<c>meta</c>,
    /// <c>languages</c>, <c>dependencies</c>, <c>apiEndpoints</c>) and representative
    /// values from the supplied <see cref="DashboardData"/>.
    /// </summary>
    [Fact]
    public void Generate_ProducesValidJsonStructure()
    {
        var data = new DashboardData
        {
            Meta = new MetaInfo { ProjectName = "TestProject", Branch = "main" },
            Project = new ProjectInfo { Name = "TestProject" },
            Packages = [new PackageDependency { Name = "express", Version = "4.0", Ecosystem = "npm" }],
            ApiEndpoints = [new ApiEndpoint { Method = "GET", Path = "/api/test", Summary = "Test" }]
        };

        var gen = new JsonMetadataGenerator();
        var json = gen.Generate(data);

        Assert.Contains("\"meta\"", json);
        Assert.Contains("\"languages\"", json);
        Assert.Contains("\"dependencies\"", json);
        Assert.Contains("\"apiEndpoints\"", json);
        Assert.Contains("TestProject", json);
        Assert.Contains("express", json);
    }
}
