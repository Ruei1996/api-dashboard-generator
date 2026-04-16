// ============================================================
// TestInfo.cs — Test discovery data models
// ============================================================
// Architecture: plain data classes (no logic); populated by TestAnalyzer
//   and serialised by HtmlDashboardGenerator for the Tests panel.
//
// Hierarchy:
//   TestSuiteInfo
//     ├─ UnitTests        → List<TestFile>  (default category)
//     ├─ IntegrationTests → List<TestFile>  (paths containing /integration/, /e2e/)
//     ├─ AcceptanceTests  → List<TestFile>  (paths containing /acceptance/, /bdd/)
//     └─ Mocks            → List<MockInfo>  (mock/stub/spy files)
//          TestFile
//            └─ TestCases → List<TestCase>
//                 TestCase
//                   └─ Subtests → List<string>  (table-driven subtests in Go, etc.)
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Top-level container for all test metadata discovered in a repository,
/// partitioned into unit, integration, and acceptance categories.
/// </summary>
public class TestSuiteInfo
{
    /// <summary>Test files classified as unit tests (no integration/e2e path segment).</summary>
    public List<TestFile> UnitTests { get; set; } = [];

    /// <summary>Test files found under paths containing <c>/integration/</c>, <c>/e2e/</c>, or <c>/functional/</c>.</summary>
    public List<TestFile> IntegrationTests { get; set; } = [];

    /// <summary>Test files found under paths containing <c>/acceptance/</c> or <c>/bdd/</c>.</summary>
    public List<TestFile> AcceptanceTests { get; set; } = [];

    /// <summary>
    /// Total number of individual test cases across all categories and all files.
    /// Pre-summed by <see cref="Analyzers.TestAnalyzer"/> to avoid re-scanning at render time.
    /// </summary>
    public int TotalTestCount { get; set; }

    /// <summary>Mock / stub / spy files detected by name or content pattern.</summary>
    public List<MockInfo> Mocks { get; set; } = [];
}

/// <summary>
/// Represents a single test source file and the test cases it contains.
/// </summary>
public class TestFile
{
    /// <summary>Repository-relative path to the test file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Package or namespace containing the tests (language-specific interpretation).</summary>
    public string Package { get; set; } = string.Empty;

    /// <summary>All test cases parsed from this file.</summary>
    public List<TestCase> TestCases { get; set; } = [];

    /// <summary>
    /// Category label assigned by <see cref="Analyzers.TestAnalyzer"/>:
    /// <c>"unit"</c>, <c>"integration"</c>, or <c>"acceptance"</c>.
    /// </summary>
    public string Category { get; set; } = string.Empty; // unit, integration, acceptance
}

/// <summary>
/// Represents a single test function or test method within a <see cref="TestFile"/>.
/// </summary>
public class TestCase
{
    /// <summary>Test function or method name (e.g. <c>"TestCreateUser"</c>, <c>"it creates a user"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional human-readable description extracted from a docstring or test framework annotation.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// <c>true</c> when the test function drives table-driven subtests (Go <c>t.Run</c>)
    /// or nested <c>describe</c> blocks (Jest/Mocha).
    /// </summary>
    public bool HasSubtests { get; set; }

    /// <summary>Names of subtests, collected from <c>t.Run("name", …)</c> calls or nested describe labels.</summary>
    public List<string> Subtests { get; set; } = [];
}

/// <summary>
/// Represents a mock, stub, or spy file detected in the repository.
/// </summary>
public class MockInfo
{
    /// <summary>Simple name of the mock type or file (without extension).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Repository-relative path to the mock file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Names of methods declared on the mock type, up to 20 entries for display purposes.</summary>
    public List<string> Methods { get; set; } = [];

    /// <summary>Name of the interface or class being mocked (empty when it cannot be detected).</summary>
    public string InterfaceMocked { get; set; } = string.Empty;
}
