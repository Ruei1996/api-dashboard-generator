namespace RepoInsightDashboard.Models;

public class ApiTrace
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string HandlerFile { get; set; } = string.Empty;
    public string HandlerFunction { get; set; } = string.Empty;
    public List<TraceStep> Steps { get; set; } = [];
    public List<SqlQuery> SqlQueries { get; set; } = [];
}

public class TraceStep
{
    public int Order { get; set; }
    public string Layer { get; set; } = string.Empty; // Handler, Service, Repository, External
    public string File { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<string> CalledFunctions { get; set; } = [];
}

public class SqlQuery
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty; // SELECT, INSERT, UPDATE, DELETE
    public string RawSql { get; set; } = string.Empty;
    public string ComposedSql { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string FunctionName { get; set; } = string.Empty;
    public List<SqlParameter> Parameters { get; set; } = [];
}

public class SqlParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
}
