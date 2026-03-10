namespace RepoInsightDashboard.Models;

public class DependencyGraph
{
    public List<DependencyNode> Nodes { get; set; } = [];
    public List<DependencyEdge> Edges { get; set; } = [];
}

public class DependencyNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Type { get; set; } = "library"; // library, service, internal
    public string? Description { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}

public class DependencyEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public class PackageDependency
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Type { get; set; } = "production"; // production, dev, peer
    public string SourceFile { get; set; } = string.Empty;
    public string Ecosystem { get; set; } = string.Empty; // npm, nuget, go, maven, pip, cargo
}

public class CallGraph
{
    public List<CallNode> Nodes { get; set; } = [];
    public List<CallEdge> Edges { get; set; } = [];
}

public class CallNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Type { get; set; } = "function"; // function, method, class
    public string? Namespace { get; set; }
}

public class CallEdge
{
    public string Caller { get; set; } = string.Empty;
    public string Callee { get; set; } = string.Empty;
    public int LineNumber { get; set; }
}
