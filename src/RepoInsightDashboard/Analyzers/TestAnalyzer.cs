using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class TestAnalyzer
{
    public TestSuiteInfo Analyze(List<FileNode> files)
    {
        var info = new TestSuiteInfo();
        var testFiles = files.Where(f => !f.IsDirectory && IsTestFile(f)).ToList();

        foreach (var file in testFiles)
        {
            try
            {
                var content = File.ReadAllText(file.AbsolutePath);
                var testFile = ParseTestFile(file, content);

                var category = ClassifyTest(file.RelativePath);
                testFile.Category = category;

                switch (category)
                {
                    case "unit": info.UnitTests.Add(testFile); break;
                    case "integration": info.IntegrationTests.Add(testFile); break;
                    case "acceptance": info.AcceptanceTests.Add(testFile); break;
                    default: info.UnitTests.Add(testFile); break;
                }
            }
            catch { }
        }

        // Find mocks
        var mockFiles = files.Where(f => !f.IsDirectory
            && (f.RelativePath.Contains("/mock") || f.Name.StartsWith("mock_"))
            && f.Extension == ".go").ToList();
        foreach (var mf in mockFiles)
        {
            try
            {
                info.Mocks.Add(ParseMockFile(mf));
            }
            catch { }
        }

        info.TotalTestCount = info.UnitTests.Sum(f => f.TestCases.Count)
            + info.IntegrationTests.Sum(f => f.TestCases.Count)
            + info.AcceptanceTests.Sum(f => f.TestCases.Count);

        return info;
    }

    private TestFile ParseTestFile(FileNode file, string content)
    {
        var tf = new TestFile
        {
            FilePath = file.RelativePath,
            Package = ExtractGoPackage(content)
        };

        // Go test functions: func Test_xxx(t *testing.T) or func TestXxx(t *testing.T)
        var testMatches = Regex.Matches(content, @"func\s+(Test\w+)\s*\(t\s+\*testing\.T\)");
        foreach (Match m in testMatches)
        {
            var testName = m.Groups[1].Value;
            var body = ExtractFunctionBodySimple(content, m.Index);

            // Find t.Run subtests
            var subtests = Regex.Matches(body, @"t\.Run\([""']([^""']+)[""']")
                .Cast<Match>().Select(sm => sm.Groups[1].Value).ToList();

            tf.TestCases.Add(new TestCase
            {
                Name = testName,
                Description = DeriveTestDescription(testName),
                HasSubtests = subtests.Count > 0,
                Subtests = subtests
            });
        }

        return tf;
    }

    private MockInfo ParseMockFile(FileNode file)
    {
        var content = File.ReadAllText(file.AbsolutePath);
        var structMatch = Regex.Match(content, @"type\s+(Mock\w+)\s+struct");
        var methods = Regex.Matches(content, @"func\s+\(m\s+\*\w+\)\s+(\w+)\s*\(")
            .Cast<Match>().Select(m => m.Groups[1].Value).ToList();

        var interfaceMatch = Regex.Match(content, @"mock\.Mock\s*\}[\s\S]*?//.*?(\w+Interface|\w+Service|\w+Repository)");

        return new MockInfo
        {
            Name = structMatch.Success ? structMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(file.Name),
            FilePath = file.RelativePath,
            Methods = methods,
            InterfaceMocked = interfaceMatch.Success ? interfaceMatch.Groups[1].Value : ""
        };
    }

    private string ClassifyTest(string relativePath)
    {
        var lower = relativePath.ToLowerInvariant();
        if (lower.Contains("/integration/") || lower.Contains("integration_test"))
            return "integration";
        if (lower.Contains("/acceptance") || lower.Contains("acceptance_test"))
            return "acceptance";
        return "unit";
    }

    private bool IsTestFile(FileNode file) =>
        file.Extension == ".go" && file.Name.EndsWith("_test.go")
        || file.Extension == ".cs" && file.Name.EndsWith("Tests.cs")
        || file.Extension == ".java" && (file.Name.EndsWith("Test.java") || file.Name.EndsWith("Tests.java"))
        || file.Extension == ".py" && file.Name.StartsWith("test_")
        || file.Extension == ".ts" && (file.Name.EndsWith(".spec.ts") || file.Name.EndsWith(".test.ts"))
        || file.Extension == ".js" && (file.Name.EndsWith(".spec.js") || file.Name.EndsWith(".test.js"));

    private string ExtractGoPackage(string content)
    {
        var m = Regex.Match(content, @"^package\s+(\w+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : "";
    }

    private string ExtractFunctionBodySimple(string content, int startIdx)
    {
        var braceIdx = content.IndexOf('{', startIdx);
        if (braceIdx < 0) return "";
        var depth = 1;
        var i = braceIdx + 1;
        while (i < content.Length && depth > 0)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}') depth--;
            i++;
        }
        var end = Math.Min(i, content.Length);
        return end > braceIdx + 1 ? content[(braceIdx + 1)..end] : "";
    }

    private string DeriveTestDescription(string testName)
    {
        // Test_getDashboardUserSourceCount → get Dashboard User Source Count
        var noPrefix = testName.StartsWith("Test_") ? testName[5..] : testName[4..];
        return Regex.Replace(noPrefix, @"([A-Z])", " $1").Replace("_", " ").Trim();
    }
}
