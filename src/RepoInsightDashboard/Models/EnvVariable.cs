// ============================================================
// EnvVariable.cs — Environment variable data model
// ============================================================
// Architecture: plain data class; populated by EnvFileAnalyzer and DockerAnalyzer,
//   then referenced by HtmlDashboardGenerator for the Env Variables panel and by
//   CopilotSemanticAnalyzer for sensitive-data-exposure checks.
//
// Security (CWE-312 — Cleartext Storage of Sensitive Information):
//   EnvVariable.Value is set to "***masked***" at parse time for any key that
//   matches a known sensitive-keyword pattern (password, token, secret, etc.).
//   The IsSensitive flag lets the dashboard render a reveal-toggle UI element
//   instead of the masked placeholder.
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Represents a single key-value pair discovered in a <c>.env</c> file or
/// a Dockerfile <c>ENV</c> instruction.
/// </summary>
/// <remarks>
/// Values that match sensitive-keyword patterns are replaced with
/// <c>"***masked***"</c> before this object is created, so the raw secret
/// never reaches the HTML renderer, JSON serialiser, or AI prompt builder.
/// </remarks>
public class EnvVariable
{
    /// <summary>Variable name as declared in the source file (e.g. <c>DB_PASSWORD</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Variable value.  Set to <c>"***masked***"</c> when <see cref="IsSensitive"/> is <c>true</c>;
    /// otherwise the literal value from the file.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> when the key name matches a sensitive-keyword pattern
    /// (password, token, secret, key, etc.) and the value has been masked.
    /// The dashboard uses this flag to show a clickable reveal-toggle instead
    /// of the masked placeholder.
    /// </summary>
    public bool IsSensitive { get; set; }

    /// <summary>
    /// Repository-relative path of the file where this variable was found
    /// (e.g. <c>".env"</c>, <c>"docker-compose.yml"</c>).
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Inline comment that follows the value on the same line (e.g. <c># used by auth service</c>).
    /// <c>null</c> when no comment is present.  Not currently parsed — reserved for future use.
    /// </summary>
    public string? Comment { get; set; }
}
