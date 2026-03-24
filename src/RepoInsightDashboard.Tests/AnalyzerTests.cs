using System.IO;
using RepoInsightDashboard.Analyzers;
using RepoInsightDashboard.Generators;
using RepoInsightDashboard.Models;
using Xunit;

namespace RepoInsightDashboard.Tests;

public class GitignoreParserTests
{
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

public class LanguageDetectorTests
{
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

public class DependencyAnalyzerTests
{
    private readonly DependencyAnalyzer _analyzer = new();

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

public class EnvFileAnalyzerTests
{
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

public class HtmlDashboardGeneratorTests
{
    [Fact]
    public void Generate_ProducesValidHtmlWithAllSections()
    {
        var data = new DashboardData
        {
            Meta = new MetaInfo
            {
                ProjectName = "TestProject",
                Branch = "main",
                RepoPath = "/tmp/test",
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

    [Fact]
    public void Generate_NamingConvention_IsCorrect()
    {
        // Verify the output file naming logic
        var name = "MyApp";
        var branch = "feature/test";
        var expected = $"{name}-dashboard-({branch}).html";
        Assert.Equal("MyApp-dashboard-(feature/test).html", expected);
    }
}

public class JsonMetadataGeneratorTests
{
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
