// ============================================================
// ApiTrace.cs — Execution path data models for a single API endpoint
// ============================================================
// Architecture: plain data classes (no logic); populated by ApiTraceAnalyzer
//   and serialised into window.__RID_DATA__.apiTraces by HtmlDashboardGenerator.
//
// Trace structure per endpoint:
//   ApiTrace
//     └─ List<TraceStep>   — ordered: Handler → Service → Repository → External
//     └─ List<SqlQuery>    — all SQL operations discovered in the execution path
//                            └─ List<SqlParameter> — named/positional SQL params
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Represents the full execution path traced for a single API endpoint,
/// from its handler function through service and repository layers down to
/// any raw SQL queries discovered in the source code.
/// </summary>
public class ApiTrace
{
    /// <summary>HTTP verb of the traced endpoint (e.g. "GET", "POST").</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>URL path template of the traced endpoint (e.g. "/v1/users/{id}").</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Repository-relative path of the file containing the handler function.</summary>
    public string HandlerFile { get; set; } = string.Empty;

    /// <summary>Name of the function that directly handles the HTTP request.</summary>
    public string HandlerFunction { get; set; } = string.Empty;

    /// <summary>
    /// Ordered execution steps from Handler through Service to Repository.
    /// The <see cref="TraceStep.Order"/> field indicates the call sequence.
    /// </summary>
    public List<TraceStep> Steps { get; set; } = [];

    /// <summary>
    /// All SQL queries associated with this endpoint's execution path,
    /// extracted from sqlc query files or inline SQL strings.
    /// </summary>
    public List<SqlQuery> SqlQueries { get; set; } = [];
}

/// <summary>
/// Represents one layer in an API endpoint's execution trace
/// (e.g. Handler, Service, Repository, or External).
/// </summary>
public class TraceStep
{
    /// <summary>1-based position in the call chain; 1 = the outermost Handler.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Architectural layer label.  One of: <c>"Handler"</c>, <c>"Controller"</c>,
    /// <c>"Service"</c>, <c>"Repository"</c>, <c>"External"</c>.
    /// Used to colour-code the trace diagram in the dashboard.
    /// </summary>
    public string Layer { get; set; } = string.Empty; // Handler, Service, Repository, External

    /// <summary>Repository-relative path of the source file where this step is implemented.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Name of the function implementing this step.</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this step does (Traditional Chinese).</summary>
    public string? Description { get; set; }

    /// <summary>1-based line number where the function body starts.</summary>
    public int StartLine { get; set; }

    /// <summary>1-based line number where the function body ends (closing brace).</summary>
    public int EndLine { get; set; }

    /// <summary>
    /// Names of other functions called from inside this step's body.
    /// Capped at 12 entries to keep the dashboard readable.
    /// </summary>
    public List<string> CalledFunctions { get; set; } = [];
}

/// <summary>
/// Represents a single SQL statement associated with an API endpoint,
/// either extracted from a sqlc query file or discovered as an inline string literal.
/// </summary>
public class SqlQuery
{
    /// <summary>Display name of the query (sqlc function name or a synthetic "inline_N" label).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SQL verb: <c>"SELECT"</c>, <c>"INSERT"</c>, <c>"UPDATE"</c>, or <c>"DELETE"</c>.
    /// Determines the colour of the operation badge in the dashboard.
    /// </summary>
    public string Operation { get; set; } = string.Empty; // SELECT, INSERT, UPDATE, DELETE

    /// <summary>The original SQL with sqlc-style parameter placeholders (e.g. <c>sqlc.arg(id)</c>).</summary>
    public string RawSql { get; set; } = string.Empty;

    /// <summary>
    /// SQL with validate-flag conditional blocks expanded, suitable for display and
    /// interactive editing in the dashboard's SQL parameter builder widget.
    /// </summary>
    public string ComposedSql { get; set; } = string.Empty;

    /// <summary>Repository-relative path of the file that contains this query.</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Name of the Go/C#/etc. function that wraps this query.</summary>
    public string FunctionName { get; set; } = string.Empty;

    /// <summary>
    /// Typed parameter list extracted from the query's parameter struct or
    /// sqlc.arg/narg annotations.
    /// </summary>
    public List<SqlParameter> Parameters { get; set; } = [];
}

/// <summary>
/// Describes a single named parameter within a <see cref="SqlQuery"/>.
/// </summary>
public class SqlParameter
{
    /// <summary>Parameter name as it appears in the query struct or function signature.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Go/C# type of the parameter (e.g. "string", "int64", "pgtype.Text").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>SQL placeholder token (e.g. <c>$1</c>, <c>@p1</c>, <c>?</c>).</summary>
    public string Placeholder { get; set; } = string.Empty;
}
