using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Traces execution path for each API endpoint through source code layers.
/// Supports Go (api → service → repo → SQL) architecture.
/// </summary>
public class ApiTraceAnalyzer
{
    private readonly string _repoPath;
    private readonly List<FileNode> _allFiles;
    private readonly Dictionary<string, string> _sqlQueryCache = [];

    public ApiTraceAnalyzer(string repoPath, List<FileNode> allFiles)
    {
        _repoPath = repoPath;
        _allFiles = allFiles;
        PreloadSqlQueries();
    }

    public List<ApiTrace> AnalyzeTraces(List<ApiEndpoint> endpoints)
    {
        var traces = new List<ApiTrace>();
        foreach (var ep in endpoints.Take(100)) // limit for perf
        {
            try
            {
                var trace = AnalyzeEndpoint(ep);
                if (trace.Steps.Count > 0 || trace.SqlQueries.Count > 0)
                    traces.Add(trace);
            }
            catch { /* skip */ }
        }
        return traces;
    }

    private ApiTrace AnalyzeEndpoint(ApiEndpoint endpoint)
    {
        var trace = new ApiTrace
        {
            Method = endpoint.Method,
            Path = endpoint.Path
        };

        // 1. Find handler by operationId or derived function name
        var handlerFuncName = endpoint.OperationId;
        if (string.IsNullOrEmpty(handlerFuncName))
            handlerFuncName = DeriveGoFunctionName(endpoint.Path, endpoint.Method);

        var handlerFile = FindGoHandlerFile(handlerFuncName);
        if (handlerFile == null) return trace;

        trace.HandlerFile = handlerFile.RelativePath;
        trace.HandlerFunction = handlerFuncName;

        // 2. Parse handler body
        var handlerContent = File.ReadAllText(handlerFile.AbsolutePath);
        var handlerBody = ExtractFunctionBody(handlerContent, handlerFuncName);

        var step0 = new TraceStep
        {
            Order = 1,
            Layer = "Handler",
            File = handlerFile.RelativePath,
            Function = handlerFuncName,
            Description = $"gRPC/HTTP 入口處理函式",
            CalledFunctions = ExtractCalledFunctions(handlerBody)
        };
        trace.Steps.Add(step0);

        // 3. Find service calls: server.XxxService.Method(
        var serviceCalls = ExtractServiceCalls(handlerBody);
        foreach (var (svcField, svcMethod) in serviceCalls)
        {
            var serviceFile = FindGoServiceFile(svcMethod, svcField);
            if (serviceFile == null) continue;

            var svcContent = File.ReadAllText(serviceFile.AbsolutePath);
            var svcBody = ExtractFunctionBody(svcContent, svcMethod);

            var step1 = new TraceStep
            {
                Order = trace.Steps.Count + 1,
                Layer = "Service",
                File = serviceFile.RelativePath,
                Function = svcMethod,
                Description = $"業務邏輯層 ({DeriveServiceName(serviceFile.RelativePath)})",
                CalledFunctions = ExtractCalledFunctions(svcBody)
            };
            trace.Steps.Add(step1);

            // 4. Find repo/store calls: d.store.Method( or store.Method(
            var repoCalls = ExtractStoreCalls(svcBody);
            foreach (var repoMethod in repoCalls)
            {
                var repoFile = FindGoRepoFile(repoMethod);
                if (repoFile != null)
                {
                    var step2 = new TraceStep
                    {
                        Order = trace.Steps.Count + 1,
                        Layer = "Repository",
                        File = repoFile.RelativePath,
                        Function = repoMethod,
                        Description = "資料存取層 (sqlc generated)"
                    };
                    trace.Steps.Add(step2);
                }

                // 5. Map to SQL query
                var sql = FindSqlQuery(repoMethod);
                if (sql != null)
                    trace.SqlQueries.Add(sql);
            }
        }

        return trace;
    }

    private void PreloadSqlQueries()
    {
        var sqlFiles = _allFiles.Where(f => !f.IsDirectory && f.Extension == ".sql"
            && f.RelativePath.Contains("query")).ToList();

        foreach (var file in sqlFiles)
        {
            try
            {
                var content = File.ReadAllText(file.AbsolutePath);
                ParseSqlFile(content, file.RelativePath);
            }
            catch { }
        }
    }

    private void ParseSqlFile(string content, string filePath)
    {
        // Parse sqlc format: -- name: FunctionName :operation\nSQL...
        var pattern = @"--\s*name:\s*(\w+)\s*:(\w+)\s*\n([\s\S]+?)(?=--\s*name:|$)";
        var matches = Regex.Matches(content, pattern);
        foreach (Match m in matches)
        {
            var funcName = m.Groups[1].Value.Trim();
            var operation = m.Groups[2].Value.Trim();
            var sql = m.Groups[3].Value.Trim();
            _sqlQueryCache[funcName.ToLower()] = $"-- Operation: {operation}\n-- Function: {funcName}\n\n{sql}";
        }
    }

    private SqlQuery? FindSqlQuery(string methodName)
    {
        var key = methodName.ToLower();
        if (_sqlQueryCache.TryGetValue(key, out var sql))
        {
            var op = "SELECT";
            if (sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase)) op = "INSERT";
            else if (sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)) op = "UPDATE";
            else if (sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase)) op = "DELETE";

            // Find the query SQL file path
            var sqlFile = _allFiles.FirstOrDefault(f => !f.IsDirectory && f.Extension == ".sql"
                && f.RelativePath.Contains("query")
                && File.ReadAllText(f.AbsolutePath).Contains(methodName));

            return new SqlQuery
            {
                Name = methodName,
                Operation = op,
                RawSql = sql,
                SourceFile = sqlFile?.RelativePath ?? "repo/db/query",
                GoFunctionName = methodName
            };
        }
        return null;
    }

    private FileNode? FindGoHandlerFile(string funcName)
    {
        if (string.IsNullOrEmpty(funcName)) return null;

        return _allFiles.FirstOrDefault(f =>
            !f.IsDirectory && f.Extension == ".go"
            && !f.Name.EndsWith("_test.go")
            && f.RelativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            && TryFindFunctionInFile(f.AbsolutePath, funcName));
    }

    private FileNode? FindGoServiceFile(string funcName, string? serviceHint = null)
    {
        return _allFiles.FirstOrDefault(f =>
            !f.IsDirectory && f.Extension == ".go"
            && !f.Name.EndsWith("_test.go")
            && (f.RelativePath.Contains("/service/") || f.RelativePath.StartsWith("service/"))
            && TryFindFunctionInFile(f.AbsolutePath, funcName));
    }

    private FileNode? FindGoRepoFile(string funcName)
    {
        return _allFiles.FirstOrDefault(f =>
            !f.IsDirectory && f.Extension == ".go"
            && !f.Name.EndsWith("_test.go")
            && (f.RelativePath.Contains("/sqlc/") || f.RelativePath.Contains("/repo/"))
            && TryFindFunctionInFile(f.AbsolutePath, funcName));
    }

    private bool TryFindFunctionInFile(string path, string funcName)
    {
        try
        {
            var content = File.ReadAllText(path);
            return Regex.IsMatch(content, $@"func\s+\([^)]+\)\s+{Regex.Escape(funcName)}\s*\(")
                || Regex.IsMatch(content, $@"func\s+{Regex.Escape(funcName)}\s*\(");
        }
        catch { return false; }
    }

    private string ExtractFunctionBody(string content, string funcName)
    {
        var pattern = $@"func\s+(?:\([^)]+\)\s+)?{Regex.Escape(funcName)}\s*\([^{{]*\{{";
        var match = Regex.Match(content, pattern);
        if (!match.Success) return string.Empty;

        var start = match.Index + match.Length;
        var depth = 1;
        var i = start;
        while (i < content.Length && depth > 0)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}') depth--;
            i++;
        }
        return i <= content.Length ? content[start..(i - 1)] : string.Empty;
    }

    private List<(string Service, string Method)> ExtractServiceCalls(string body)
    {
        var calls = new List<(string, string)>();
        // Pattern: server.DashboardService.GetUserSourceCount( or s.AccountService.Create(
        var matches = Regex.Matches(body,
            @"(?:server|s)\.\s*(\w*Service)\.\s*(\w+)\s*\(");
        foreach (Match m in matches)
            calls.Add((m.Groups[1].Value, m.Groups[2].Value));

        // Also match direct service calls: DashboardService.Method(
        var matches2 = Regex.Matches(body, @"(\w+Service)\.(\w+)\s*\(");
        foreach (Match m in matches2)
            calls.Add((m.Groups[1].Value, m.Groups[2].Value));

        return calls.DistinctBy(c => c.Item2).Take(5).ToList();
    }

    private List<string> ExtractStoreCalls(string body)
    {
        // Pattern: d.store.UserSources_ListBySearch( or store.Users_Create(
        var matches = Regex.Matches(body,
            @"(?:d\.store|store|q\.db|db)\.\s*(\w+)\s*\(");
        return matches.Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(n => !new[] { "QueryContext", "ExecContext", "QueryRowContext", "BeginTx", "Begin", "Commit", "Rollback" }.Contains(n))
            .Distinct()
            .Take(10)
            .ToList();
    }

    private List<string> ExtractCalledFunctions(string body)
    {
        var calls = new List<string>();
        var matches = Regex.Matches(body, @"(?:server|s|d)\.\s*(\w+)\s*\(");
        foreach (Match m in matches)
            calls.Add(m.Groups[1].Value);
        return calls.Distinct().Take(10).ToList();
    }

    private string DeriveGoFunctionName(string path, string method)
    {
        // /v1/cms/dashboard/user_source → Cms_Dashboard_UserSource (GET)
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(s => s == "v1" || s.StartsWith("v") && s.Length <= 3)
            .ToArray();

        return string.Join("_", segments.Select(s =>
            string.Concat(s.Split('_', '-').Select(w => char.ToUpper(w[0]) + w[1..]))));
    }

    private string DeriveServiceName(string filePath)
    {
        var parts = filePath.Split('/');
        var serviceIdx = Array.FindIndex(parts, p => p == "service");
        if (serviceIdx >= 0 && serviceIdx + 1 < parts.Length)
            return parts[serviceIdx + 1];
        return Path.GetFileNameWithoutExtension(filePath);
    }
}
