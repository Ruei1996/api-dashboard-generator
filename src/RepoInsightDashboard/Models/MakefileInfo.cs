// ============================================================
// MakefileInfo.cs — Makefile content model
// ============================================================
// Architecture: plain data class; populated by MakefileAnalyzer.
//   Either holds the verbatim content of an existing Makefile, or the
//   synthesised content when no Makefile was found in the repository.
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Holds Makefile content to display in the dashboard's Makefile section.
/// Content is either read directly from an existing Makefile or auto-generated
/// based on the tools and languages detected in the repository.
/// </summary>
public class MakefileInfo
{
    /// <summary>True when a Makefile was found in the repository; false when auto-generated.</summary>
    public bool Exists { get; set; }

    /// <summary>Full raw text of the Makefile (real or auto-generated).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Relative path to the Makefile, or "(auto-generated)" when synthesised.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>List of make target names extracted from the content for quick navigation.</summary>
    public List<string> Targets { get; set; } = [];
}
