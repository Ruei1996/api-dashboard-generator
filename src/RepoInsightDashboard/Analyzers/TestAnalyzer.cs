using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Discovers test files across all supported languages (Go, Python, JS/TS, Java/Kotlin,
/// C#, Ruby, PHP, Rust), parses test cases from each using language-specific regex patterns,
/// classifies tests into unit/integration/acceptance categories, and detects mock/stub files.
/// </summary>
public class TestAnalyzer
{
    /// <summary>
    /// Returns a <see cref="TestSuiteInfo"/> with all discovered tests grouped by category,
    /// plus mock/stub file metadata. Files larger than 2 MB are skipped to prevent OOM.
    /// </summary>
    public TestSuiteInfo Analyze(List<FileNode> files)
    {
        var info = new TestSuiteInfo();
        var testFiles = files.Where(f => !f.IsDirectory && IsTestFile(f)).ToList();

        foreach (var file in testFiles)
        {
            try
            {
                // Skip very large files to avoid OOM — pathological repos can have huge generated test files.
                if (file.SizeBytes > 2_000_000) continue;
                var content = File.ReadAllText(file.AbsolutePath);
                var testFile = ParseTestFile(file, content);
                testFile.Category = ClassifyTest(file.RelativePath);

                switch (testFile.Category)
                {
                    case "unit":        info.UnitTests.Add(testFile); break;
                    case "integration": info.IntegrationTests.Add(testFile); break;
                    case "acceptance":  info.AcceptanceTests.Add(testFile); break;
                    default:            info.UnitTests.Add(testFile); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                System.Diagnostics.Debug.WriteLine($"[TestAnalyzer] Skipping '{file.RelativePath}': {ex.Message}");
            }
        }

        // Mock / stub file detection — language-aware
        var mockFiles = files.Where(f => !f.IsDirectory && IsMockFile(f)).ToList();
        foreach (var mf in mockFiles)
        {
            try { info.Mocks.Add(ParseMockFile(mf)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[TestAnalyzer] Skipping mock '{mf.RelativePath}': {ex.Message}");
            }
        }

        info.TotalTestCount =
            info.UnitTests.Sum(f => f.TestCases.Count)
            + info.IntegrationTests.Sum(f => f.TestCases.Count)
            + info.AcceptanceTests.Sum(f => f.TestCases.Count);

        return info;
    }

    // ── Routing ────────────────────────────────────────────────────────────────

    private TestFile ParseTestFile(FileNode file, string content) =>
        file.Extension.ToLowerInvariant() switch
        {
            ".go"             => ParseGoTestFile(file, content),
            ".py"             => ParsePythonTestFile(file, content),
            ".js" or ".jsx"
            or ".ts" or ".tsx"
            or ".mjs" or ".cjs" => ParseJsTestFile(file, content),
            ".java" or ".kt"  => ParseJavaTestFile(file, content),
            ".cs"             => ParseCSharpTestFile(file, content),
            ".rb"             => ParseRubyTestFile(file, content),
            ".php"            => ParsePhpTestFile(file, content),
            ".rs"             => ParseRustTestFile(file, content),
            _                 => new TestFile { FilePath = file.RelativePath }
        };

    // ── Go ─────────────────────────────────────────────────────────────────────

    private TestFile ParseGoTestFile(FileNode file, string content)
    {
        var tf = new TestFile
        {
            FilePath = file.RelativePath,
            Package = ExtractGoPackage(content)
        };

        var testMatches = Regex.Matches(content, @"func\s+(Test\w+)\s*\(t\s+\*testing\.T\)");
        foreach (Match m in testMatches)
        {
            var testName = m.Groups[1].Value;
            var body = ExtractFunctionBodySimple(content, m.Index);
            var subtests = Regex.Matches(body, @"t\.Run\([""']([^""']+)[""']")
                .Cast<Match>().Select(sm => sm.Groups[1].Value).ToList();
            tf.TestCases.Add(new TestCase
            {
                Name = testName,
                Description = DeriveDescription(testName),
                HasSubtests = subtests.Count > 0,
                Subtests = subtests
            });
        }

        // Benchmark functions
        foreach (Match m in Regex.Matches(content, @"func\s+(Benchmark\w+)\s*\(b\s+\*testing\.B\)"))
            tf.TestCases.Add(new TestCase { Name = m.Groups[1].Value, Description = "Benchmark" });

        return tf;
    }

    // ── Python (pytest / unittest) ─────────────────────────────────────────────

    private static TestFile ParsePythonTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        // Track parametrize-decorated functions to avoid double-counting below.
        var parametrized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // pytest.mark.parametrize — must run first to populate the exclusion set.
        foreach (Match pm in Regex.Matches(content,
            @"@pytest\.mark\.parametrize\([^)]+\)\s*(?:async\s+)?def\s+(test_\w+)"))
        {
            var name = pm.Groups[1].Value;
            parametrized.Add(name);
            tf.TestCases.Add(new TestCase
            {
                Name        = $"[parametrize] {name}",
                Description = "Parametrised test"
            });
        }

        // Module-level test functions: def test_xxx(...) — skip already-captured parametrize ones.
        foreach (Match m in Regex.Matches(content,
            @"^(?:async\s+)?def\s+(test_\w+)\s*\(", RegexOptions.Multiline))
        {
            if (parametrized.Contains(m.Groups[1].Value)) continue;
            tf.TestCases.Add(new TestCase
            {
                Name        = m.Groups[1].Value,
                Description = DeriveDescription(m.Groups[1].Value)
            });
        }

        // Class-based tests: class TestFoo: ... def test_xxx
        // Uses a regex with a 5-second timeout to prevent ReDoS on crafted inputs (CWE-400).
        foreach (Match cls in Regex.Matches(content,
            @"class\s+(Test\w+)(?:\([^)]*\))?\s*:([\s\S]*?)(?=^class\s|\Z)",
            RegexOptions.Multiline, TimeSpan.FromSeconds(5)))
        {
            var className = cls.Groups[1].Value;
            foreach (Match mth in Regex.Matches(cls.Groups[2].Value,
                @"def\s+(test_\w+)\s*\(", RegexOptions.Multiline))
                tf.TestCases.Add(new TestCase
                {
                    Name        = $"{className}.{mth.Groups[1].Value}",
                    Description = DeriveDescription(mth.Groups[1].Value)
                });
        }

        return tf;
    }

    // ── JavaScript / TypeScript (Jest / Mocha / Vitest) ───────────────────────

    private static TestFile ParseJsTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        // Nested describe/it/test blocks (depth 1 only — sufficient for most suites)
        foreach (Match desc in Regex.Matches(content,
            @"(?:describe|suite)\s*\(\s*['""`]([^'""` ]+)['""`]"))
        {
            var suiteName = desc.Groups[1].Value;
            // Collect subtests after this describe opening
            var afterIdx = desc.Index + desc.Length;
            var block = content.Length > afterIdx
                ? content[afterIdx..Math.Min(afterIdx + 4000, content.Length)]
                : "";

            var subtests = Regex.Matches(block,
                @"(?:it|test)\s*\(\s*['""`]([^'""` ]+)['""`]")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            if (subtests.Count > 0)
            {
                tf.TestCases.Add(new TestCase
                {
                    Name = suiteName,
                    Description = $"Test suite with {subtests.Count} case(s)",
                    HasSubtests = true,
                    Subtests = subtests
                });
            }
        }

        // Top-level it/test calls not inside describe
        foreach (Match m in Regex.Matches(content,
            @"^(?:it|test)\s*\(\s*['""`]([^'""` ]+)['""`]", RegexOptions.Multiline))
            tf.TestCases.Add(new TestCase
            {
                Name = m.Groups[1].Value,
                Description = DeriveDescription(m.Groups[1].Value)
            });

        return tf;
    }

    // ── Java / Kotlin (JUnit 4 + 5, TestNG) ──────────────────────────────────

    private static TestFile ParseJavaTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        // @Test / @ParameterizedTest / @RepeatedTest annotations
        foreach (Match m in Regex.Matches(content,
            @"@(?:Test|ParameterizedTest|RepeatedTest|TestFactory)[^\n]*\n\s*(?:public|protected)?\s*\S+\s+(\w+)\s*\("))
            tf.TestCases.Add(new TestCase
            {
                Name = m.Groups[1].Value,
                Description = DeriveDescription(m.Groups[1].Value)
            });

        // Kotlin: @Test fun testSomething()
        foreach (Match m in Regex.Matches(content,
            @"@Test\s+fun\s+(`[^`]+`|\w+)\s*\("))
            tf.TestCases.Add(new TestCase
            {
                Name = m.Groups[1].Value.Trim('`'),
                Description = DeriveDescription(m.Groups[1].Value.Trim('`'))
            });

        return tf;
    }

    // ── C# (xUnit / NUnit / MSTest) ──────────────────────────────────────────

    private static TestFile ParseCSharpTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        // (?:\s*\[[^\]]*\])* allows any number of stacked attribute lines between the
        // test attribute ([Fact]/[Theory]/etc.) and the method declaration, which is
        // required for [Theory] + [InlineData(...)] patterns (fix for code-review issue #2).
        const string pattern =
            @"\[(?:Fact|Theory|Test|TestMethod)\]" +
            @"(?:\s*\[[^\]]*\])*" +
            @"\s*(?:public|private|protected|internal)?\s*(?:async\s+)?\S+\s+(\w+)\s*\(";

        foreach (Match m in Regex.Matches(content, pattern, RegexOptions.Singleline))
            tf.TestCases.Add(new TestCase
            {
                Name        = m.Groups[1].Value,
                Description = DeriveDescription(m.Groups[1].Value)
            });

        return tf;
    }

    // ── Ruby (RSpec / Minitest) ────────────────────────────────────────────────

    private static TestFile ParseRubyTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        // RSpec: describe / context / it blocks
        foreach (Match desc in Regex.Matches(content,
            @"(?:describe|context)\s+['""]([^'""]+)['""]"))
        {
            var suiteName = desc.Groups[1].Value;
            var block = content.Length > desc.Index + 200
                ? content[(desc.Index + desc.Length)..Math.Min(desc.Index + desc.Length + 2000, content.Length)]
                : "";
            var subtests = Regex.Matches(block, @"it\s+['""]([^'""]+)['""]")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            tf.TestCases.Add(new TestCase
            {
                Name = suiteName, HasSubtests = subtests.Count > 0, Subtests = subtests,
                Description = $"{subtests.Count} example(s)"
            });
        }

        // Minitest: def test_xxx
        foreach (Match m in Regex.Matches(content, @"def\s+(test_\w+)"))
            tf.TestCases.Add(new TestCase { Name = m.Groups[1].Value, Description = DeriveDescription(m.Groups[1].Value) });

        return tf;
    }

    // ── PHP (PHPUnit) ─────────────────────────────────────────────────────────

    private static TestFile ParsePhpTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        // PHPUnit test methods: public function testXxx or /** @test */
        foreach (Match m in Regex.Matches(content,
            @"(?:public\s+function\s+(test\w+)|/\*\*[^*]*@test[^*]*\*/\s+public\s+function\s+(\w+))\s*\("))
            tf.TestCases.Add(new TestCase
            {
                Name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value,
                Description = DeriveDescription(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            });

        return tf;
    }

    // ── Rust (#[test]) ────────────────────────────────────────────────────────

    private static TestFile ParseRustTestFile(FileNode file, string content)
    {
        var tf = new TestFile { FilePath = file.RelativePath };

        foreach (Match m in Regex.Matches(content,
            @"#\[(?:test|tokio::test|async_std::test)\]\s+(?:async\s+)?fn\s+(\w+)\s*\("))
            tf.TestCases.Add(new TestCase
            {
                Name = m.Groups[1].Value,
                Description = DeriveDescription(m.Groups[1].Value)
            });

        return tf;
    }

    // ── Mock File Detection ────────────────────────────────────────────────────

    private static bool IsMockFile(FileNode file)
    {
        // Normalize to forward-slashes so detection works on Windows too (backslash paths).
        var lower = file.RelativePath.Replace('\\', '/').ToLowerInvariant();
        var name  = file.Name.ToLowerInvariant();
        return name.StartsWith("mock_") || name.StartsWith("fake_")
               || lower.Contains("/mock/") || lower.Contains("/mocks/")
               || lower.Contains("/stub/") || lower.Contains("/stubs/")
               || lower.Contains("/fake/") || lower.Contains("/fakes/");
    }

    private MockInfo ParseMockFile(FileNode file)
    {
        var content = file.SizeBytes < 500_000
            ? File.ReadAllText(file.AbsolutePath)
            : "";

        // Language-agnostic: grab first type/struct/class name that looks like a mock
        var structMatch = Regex.Match(content,
            @"(?:type\s+|class\s+|struct\s+)(Mock\w+|Fake\w+|Stub\w+)");

        // Collect method names from the file (language-agnostic heuristic)
        var methods = Regex.Matches(content,
                @"(?:func\s+\(\w+\s+\*\w+\)\s+(\w+)|def\s+(\w+)|public\s+\S+\s+(\w+)\s*\(|fn\s+(\w+)\s*\()")
            .Cast<Match>()
            .Select(m => m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "")
            .Where(n => !string.IsNullOrEmpty(n) && !IsKeyword(n))
            .Distinct()
            .ToList();

        return new MockInfo
        {
            Name = structMatch.Success
                ? structMatch.Groups[1].Value
                : Path.GetFileNameWithoutExtension(file.Name),
            FilePath = file.RelativePath,
            Methods = methods
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string ClassifyTest(string relativePath)
    {
        var lower = relativePath.ToLowerInvariant();
        if (lower.Contains("/integration/") || lower.Contains("integration_test")
            || lower.Contains("_integration_") || lower.Contains("/e2e/"))
            return "integration";
        if (lower.Contains("/acceptance") || lower.Contains("acceptance_test")
            || lower.Contains("/bdd/") || lower.Contains("/feature/"))
            return "acceptance";
        return "unit";
    }

    private static bool IsTestFile(FileNode file)
    {
        var name = file.Name;
        var ext  = file.Extension.ToLowerInvariant();
        return ext switch
        {
            ".go"           => name.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase),
            ".cs"           => name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("Spec.cs", StringComparison.OrdinalIgnoreCase),
            ".java"         => name.EndsWith("Test.java", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("Tests.java", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("Spec.java", StringComparison.OrdinalIgnoreCase),
            ".kt"           => name.EndsWith("Test.kt", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("Tests.kt", StringComparison.OrdinalIgnoreCase),
            ".py"           => name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase),
            ".ts" or ".tsx" => name.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".spec.tsx", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase),
            ".js" or ".jsx" => name.EndsWith(".spec.js", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase),
            ".rb"           => name.EndsWith("_spec.rb", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("_test.rb", StringComparison.OrdinalIgnoreCase),
            ".php"          => name.EndsWith("Test.php", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith("Tests.php", StringComparison.OrdinalIgnoreCase),
            ".rs"           => true, // Rust tests are inline in any .rs file; ParseRustTestFile filters by #[test]
            _               => false
        };
    }

    private static string ExtractGoPackage(string content)
    {
        var m = Regex.Match(content, @"^package\s+(\w+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ExtractFunctionBodySimple(string content, int startIdx)
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

    // Converts snake_case / CamelCase test names into readable descriptions.
    private static string DeriveDescription(string testName)
    {
        var noPrefix = testName.StartsWith("Test_") ? testName[5..]
                       : testName.StartsWith("Test")  ? testName[4..]
                       : testName.StartsWith("test_") ? testName[5..]
                       : testName;
        return Regex.Replace(noPrefix, @"([A-Z])", " $1").Replace("_", " ").Trim();
    }

    private static bool IsKeyword(string name) =>
        name is "if" or "for" or "return" or "switch" or "case" or "break"
                or "class" or "interface" or "struct" or "func" or "def"
                or "new" or "var" or "let" or "const" or "this" or "self";
}
