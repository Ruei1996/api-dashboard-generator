namespace RepoInsightDashboard.Models;

public class TestSuiteInfo
{
    public List<TestFile> UnitTests { get; set; } = [];
    public List<TestFile> IntegrationTests { get; set; } = [];
    public List<TestFile> AcceptanceTests { get; set; } = [];
    public int TotalTestCount { get; set; }
    public List<MockInfo> Mocks { get; set; } = [];
}

public class TestFile
{
    public string FilePath { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public List<TestCase> TestCases { get; set; } = [];
    public string Category { get; set; } = string.Empty; // unit, integration, acceptance
}

public class TestCase
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool HasSubtests { get; set; }
    public List<string> Subtests { get; set; } = [];
}

public class MockInfo
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = [];
    public string InterfaceMocked { get; set; } = string.Empty;
}
