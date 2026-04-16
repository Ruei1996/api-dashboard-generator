// ============================================================
// DependencyGraph.cs — Dependency graph node and edge models
// ============================================================
// Architecture: plain data classes (no logic); populated by DependencyAnalyzer.BuildGraph
//   and serialised into window.__RID_DATA__.dependencyGraph for the RidGraph SVG renderer.
//
// Graph structure:
//   DependencyGraph
//     ├─ Nodes   → List<DependencyNode>   (packages, services, internal modules)
//     └─ Edges   → List<DependencyEdge>   (directed: From → To)
//
// Graph cap: BuildGraph limits Nodes to MaxGraphNodes (80) for UI readability.
//   The most important packages (by direct-dependency count) are selected first.
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Directed dependency graph rendered by the RidGraph SVG engine in the dashboard.
/// Capped at 80 nodes to keep the visualisation readable.
/// </summary>
public class DependencyGraph
{
    /// <summary>All package and module nodes in the graph.</summary>
    public List<DependencyNode> Nodes { get; set; } = [];

    /// <summary>Directed edges representing "A depends on B" relationships.</summary>
    public List<DependencyEdge> Edges { get; set; } = [];
}

/// <summary>
/// A single package, service, or internal module node in the dependency graph.
/// </summary>
public class DependencyNode
{
    /// <summary>Unique identifier within the graph (typically the package name + version).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display label for the node.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Semantic version string (e.g. <c>"13.0.3"</c>); empty when unavailable.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Node type used to select the visual style in RidGraph: <c>"library"</c> (default),
    /// <c>"service"</c>, or <c>"internal"</c>.
    /// </summary>
    public string Type { get; set; } = "library"; // library, service, internal

    /// <summary>Optional short description from the package manifest; <c>null</c> when absent.</summary>
    public string? Description { get; set; }

    /// <summary>Repository-relative path of the manifest file that declared this dependency.</summary>
    public string SourceFile { get; set; } = string.Empty;
}

/// <summary>
/// A directed edge in the dependency graph indicating that one node depends on another.
/// </summary>
public class DependencyEdge
{
    /// <summary>ID of the dependent node (the one that has the dependency).</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>ID of the dependency node (the one being depended upon).</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Optional edge label (e.g. <c>"production"</c>, <c>"dev"</c>); empty when unlabelled.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// A single package dependency discovered in a project manifest file
/// (package.json, go.mod, requirements.txt, pom.xml, etc.).
/// </summary>
public class PackageDependency
{
    /// <summary>Package name as declared in the manifest (e.g. <c>"express"</c>, <c>"Newtonsoft.Json"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Declared version constraint or resolved version (e.g. <c>"^4.18.0"</c>, <c>"13.0.3"</c>).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Dependency type: <c>"production"</c>, <c>"dev"</c>, or <c>"peer"</c>.
    /// Inferred from the manifest section where the package is declared.
    /// </summary>
    public string Type { get; set; } = "production"; // production, dev, peer

    /// <summary>Repository-relative path of the manifest file (e.g. <c>"package.json"</c>).</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Package ecosystem identifier used for rendering ecosystem badges:
    /// <c>"npm"</c>, <c>"nuget"</c>, <c>"go"</c>, <c>"maven"</c>, <c>"pip"</c>, <c>"cargo"</c>, etc.
    /// </summary>
    public string Ecosystem { get; set; } = string.Empty; // npm, nuget, go, maven, pip, cargo
}
