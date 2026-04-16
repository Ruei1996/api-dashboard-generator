// ============================================================
// ApiTraceAnalyzer.cs — Multi-language API execution path tracer
// ============================================================
// Architecture: stateful service class; all indexes are built eagerly at
//   construction time (one full source-file scan) and then used read-only
//   during analysis (fast dictionary lookups per endpoint).
//
// Supported languages & frameworks:
//   Go         — grpc-gateway HandlePath, gin, echo, chi
//   C#         — ASP.NET Core [HttpGet/Post/…] attribute routing
//   Python     — FastAPI / Flask / Django decorators
//   Java/Kotlin — Spring MVC @GetMapping / @PostMapping
//   TypeScript/JavaScript — NestJS decorators, Express router.get/post
//   PHP/Laravel — Route::get/post in routes/api.php
//   Ruby/Rails — routes.rb get/post + 'controller#action' syntax
//
// Analysis pipeline per endpoint:
//   1. Route resolution  — explicit route index (exact → normalised → fuzzy path match)
//                          OR name-derived candidates from operationId + path segments
//   2. Handler tracing   — locate handler function, extract body via brace counting
//   3. Service tracing   — follow service/manager/handler calls from handler body
//   4. Repository tracing — follow DB/store/repo calls from service body
//   5. SQL association   — match pre-loaded sqlc queries or inline SQL strings
//
// Performance decisions:
//   • BuildFunctionIndex is O(F × L) run once; per-endpoint analysis is O(C) lookups.
//   • MaxSourceFileSizeBytes (5 MB) prevents OOM on auto-generated bundles.
//   • Take(120) in AnalyzeTraces caps CPU time on very large generated APIs.
//   • _routeHandlerIndex avoids redundant file scans for registered Go routes.
//
// Usage:
//   var tracer = new ApiTraceAnalyzer(repoPath, allFiles);
//   List<ApiTrace> traces = tracer.AnalyzeTraces(apiEndpoints);
// ============================================================

using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Traces execution path for each API endpoint through source code layers.
/// Supports Go (grpc-gateway/gin/echo/chi), C#/.NET, Python (FastAPI/Flask/Django),
/// Java/Spring, TypeScript/Node.js (NestJS/Express), PHP/Laravel, Ruby/Rails.
/// </summary>
public class ApiTraceAnalyzer
{
    private readonly string _repoPath;
    private readonly List<FileNode> _allFiles;
    private readonly string _primaryLanguage;

    /// <summary>Maximum size for source files read into the function index or trace analysis.
    /// Files larger than this are skipped to prevent OOM on giant generated/bundled files.</summary>
    private const long MaxSourceFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    // funcName (lower) -> list of (file, lineNumber) where the function is defined
    private readonly Dictionary<string, List<(FileNode File, int Line)>> _funcIndex =
        new(StringComparer.OrdinalIgnoreCase);

    // SQL query cache: funcName (lower) -> SqlQuery
    private readonly Dictionary<string, SqlQuery> _sqlCache =
        new(StringComparer.OrdinalIgnoreCase);

    // (HTTP_METHOD_UPPER, normalized_path) -> handler function name
    // Populated by scanning grpc-gateway HandlePath / gin / echo / chi route registrations.
    private readonly Dictionary<(string Method, string Path), string> _routeHandlerIndex = new();

    // File content cache: AbsolutePath -> lines array.
    // Avoids re-reading the same source file 3+ times across the analysis pipeline.
    private readonly Dictionary<string, string[]> _fileLineCache =
        new(StringComparer.Ordinal);

    // Go struct body index: structName (case-insensitive) -> body text between { }.
    // Built once at construction from all .go files; FindGoStructDefinition is O(1).
    private readonly Dictionary<string, string> _structBodyIndex =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Compiled static regexes (instantiated once per AppDomain) ─────────────
    // Using RegexOptions.Compiled so the JIT emits native code for the pattern.
    private static readonly Regex RxGoFunc =
        new(@"^func\s+(?:\([^)]+\)\s+)?(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex RxCsMethod =
        new(@"(?:public|private|protected|internal)(?:\s+(?:override|virtual|static|async))*\s+\S+\s+(\w+)\s*\(",
            RegexOptions.Compiled);
    private static readonly Regex RxPyDef =
        new(@"^(?:[ \t]*)(?:async\s+)?def\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex RxJavaDef =
        new(@"(?:public|private|protected)(?:\s+(?:static|final|synchronized|async))*\s+\S+\s+(\w+)\s*\(",
            RegexOptions.Compiled);
    private static readonly Regex RxJsFunction =
        new(@"(?:async\s+)?function\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex RxJsArrow =
        new(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s*)?\(", RegexOptions.Compiled);
    private static readonly Regex RxJsClassMethod =
        new(@"^\s+(\w+)\s*\(.*\)\s*(?:\{|:)", RegexOptions.Compiled);
    private static readonly Regex RxPhpFunction =
        new(@"(?:public|private|protected)?\s*function\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex RxRubyDef =
        new(@"^\s*def\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex RxSqlcArg =
        new(@"sqlc\.arg\((\w+)\)", RegexOptions.Compiled);
    private static readonly Regex RxNarg =
        new(@"@(\w+)", RegexOptions.Compiled);
    private static readonly Regex RxStructField =
        new(@"(\w+)\s+([\w\.\*\[\]]+)", RegexOptions.Compiled);
    private static readonly Regex RxStructInit =
        new(@"(\w+)\s*:\s*([^,\n\r]+)", RegexOptions.Compiled);
    private static readonly Regex RxVersionSeg =
        new(@"^v\d+$", RegexOptions.Compiled);
    private static readonly Regex RxCamelToSnake1 =
        new(@"(?<=[a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex RxCamelToSnake2 =
        new(@"(?<=[A-Z]{1})([A-Z][a-z])", RegexOptions.Compiled);
    private static readonly Regex RxNumericLiteral =
        new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex RxGoStringLiteral =
        new(@"^""([^""\\]*)""$", RegexOptions.Compiled);
    private static readonly Regex RxGoRawLiteral =
        new(@"^`([^`]*)`$", RegexOptions.Compiled);

    // ── Additional compiled regexes for hot-path helpers ─────────────────────
    // These replace Regex.Xxx(string, string) static overloads that bypass the
    // 15-entry MRU cache and allocate a new Regex object on every invocation.

    // ParseSqlcFile — matches sqlc query blocks (-- name: FuncName :operation)
    private static readonly Regex RxSqlcBlock =
        new(@"--\s*name:\s*(\w+)\s*:(\w+)\s*\n([\s\S]+?)(?=--\s*name:|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Go struct definitions — populated once at construction into _structBodyIndex
    private static readonly Regex RxGoStructDef =
        new(@"type\s+(\w+)\s+struct\s*\{([^}]+)\}", RegexOptions.Compiled);

    // CollectStoreCallsRecursive — receiver method pattern (compiled once per call, not per line)
    // NOTE: not a static field because the pattern is interpolated with helperName.
    // Used as a local compiled Regex to avoid re-compiling inside the per-line loop.

    // TryParseGinEchoChiRoute
    private static readonly Regex RxGinRoute =
        new(@"(?:router|r|e|g|api|v\d+|group)\.(GET|POST|PUT|DELETE|PATCH|Get|Post|Put|Delete|Patch)\s*\(\s*""([^""]+)""\s*,\s*(?:\w+\.)?(\w+)\s*[,)]",
            RegexOptions.Compiled);

    // ExtractGoStoreCalls
    private static readonly Regex RxGoStoreCall =
        new(@"(?:\w+\.\w*store|\bstore\b|\bq\b|\bdb\b|\w+\.\w*db|\w+\.\w*repo|\w+\.\w*quer)\s*\.\s*([A-Z]\w+)\s*\(",
            RegexOptions.Compiled);

    // ExtractGoSqlcCalls
    private static readonly Regex RxGoSqlcCall =
        new(@"(?:\bq\b|\bqueries\b|\btx\b|\bqtx\b)\s*\.\s*([A-Z]\w+)\s*\(",
            RegexOptions.Compiled);

    // ExtractGoCalledFunctions
    private static readonly Regex RxGoExportedCall =
        new(@"(?:[a-zA-Z_]\w*)\.([A-Z]\w+)\s*\(", RegexOptions.Compiled);

    // FindCSharpActionMethod — HTTP attribute path and method signature
    private static readonly Regex RxCsAttrPath =
        new(@"\(""([^""]+)""\)", RegexOptions.Compiled);
    private static readonly Regex RxCsActionSig =
        new(@"(?:public|private|protected)(?:\s+(?:async|virtual|override))*\s+\S+\s+(\w+)\s*\(",
            RegexOptions.Compiled);

    // PathsMatch — normalise path parameters to wildcards
    private static readonly Regex RxPathParam =
        new(@"\{[^}]+\}", RegexOptions.Compiled);

    /// <summary>
    /// Constructs the analyser and eagerly builds three indexes by scanning all
    /// source files exactly once:
    /// <list type="bullet">
    ///   <item><see cref="_funcIndex"/> — maps function name → (file, lineNumber)</item>
    ///   <item><see cref="_sqlCache"/> — maps sqlc function name → SQL query</item>
    ///   <item><see cref="_routeHandlerIndex"/> — maps (method, path) → handler name</item>
    /// </list>
    /// </summary>
    /// <param name="repoPath">Absolute path to the repository root.</param>
    /// <param name="allFiles">
    /// Flat list of all file nodes produced by <see cref="FileScanner"/>.
    /// Test files (<c>_test.go</c>, <c>*_test.py</c>, etc.) and generated files
    /// (<c>*.pb.go</c>, <c>wire_gen.go</c>, etc.) are excluded from all indexes.
    /// </param>
    /// <remarks>
    /// Construction cost is O(F × L) where F = source files and L = average lines per file.
    /// The resulting indexes are read-only; concurrent calls to <see cref="AnalyzeTraces"/>
    /// are therefore safe without additional locking.
    /// </remarks>
    public ApiTraceAnalyzer(string repoPath, List<FileNode> allFiles)
    {
        _repoPath = repoPath;
        _allFiles = allFiles;
        _primaryLanguage = DetectPrimaryLanguage();
        BuildFunctionIndex();
        BuildStructIndex();
        PreloadSqlQueries();
        BuildRouteHandlerIndex();
    }

    // ═══ Public Entry Point ══════════════════════════════════════════════════

    /// <summary>
    /// Returns cached file content as a single string for <paramref name="absolutePath"/>.
    /// Re-uses the <see cref="_fileLineCache"/> by joining cached lines, avoiding a second
    /// <c>File.ReadAllText</c> call when lines are already cached.
    /// </summary>
    private string GetCachedContent(string absolutePath)
        => string.Join("\n", GetCachedLines(absolutePath));

    /// <summary>
    /// Returns cached lines for <paramref name="absolutePath"/>, reading from disk on first access.
    /// Silently returns an empty array if the file cannot be read (no exception propagates).
    /// </summary>
    private string[] GetCachedLines(string absolutePath)
    {
        if (_fileLineCache.TryGetValue(absolutePath, out var cached))
            return cached;
        try
        {
            var lines = File.ReadAllLines(absolutePath);
            _fileLineCache[absolutePath] = lines;
            return lines;
        }
        catch
        {
            _fileLineCache[absolutePath] = [];
            return [];
        }
    }


    /// <summary>
    /// Traces the execution path of each endpoint through the repository's source layers
    /// (Handler → Service → Repository → SQL) and returns the collected
    /// <see cref="ApiTrace"/> objects.
    /// </summary>
    /// <param name="endpoints">
    /// Endpoint list produced by <see cref="SwaggerAnalyzer"/>.
    /// At most 120 endpoints are traced to cap analysis time on very large generated APIs;
    /// endpoints are processed in declaration order so the most important routes are always covered.
    /// </param>
    /// <returns>
    /// List of <see cref="ApiTrace"/> objects.  Traces that resolved zero steps <em>and</em>
    /// zero SQL queries are discarded so the dashboard only shows actionable results.
    /// </returns>
    public List<ApiTrace> AnalyzeTraces(List<ApiEndpoint> endpoints)
    {
        var traces = new List<ApiTrace>();
        foreach (var ep in endpoints.Take(120))
        {
            try
            {
                var trace = _primaryLanguage switch
                {
                    "Go" => AnalyzeGoEndpoint(ep),
                    "C#" => AnalyzeCSharpEndpoint(ep),
                    "Python" => AnalyzePythonEndpoint(ep),
                    "Java" or "Kotlin" => AnalyzeJavaEndpoint(ep),
                    "TypeScript" or "JavaScript" => AnalyzeTypeScriptEndpoint(ep),
                    "PHP" => AnalyzePhpEndpoint(ep),
                    "Ruby" => AnalyzeRubyEndpoint(ep),
                    _ => AnalyzeGoEndpoint(ep)
                };
                if (trace.Steps.Count > 0 || trace.SqlQueries.Count > 0)
                    traces.Add(trace);
            }
            catch { /* skip on error */ }
        }
        return traces;
    }

    // ═══ Language Detection ══════════════════════════════════════════════════

    /// <summary>
    /// Identifies the repository's dominant programming language by counting non-test
    /// source files per language extension and returning the language with the most files.
    /// </summary>
    /// <returns>
    /// One of: "Go", "C#", "Python", "Java", "Kotlin", "TypeScript", "JavaScript",
    /// "PHP", "Ruby".  Defaults to "Go" when no recognised source files are found.
    /// </returns>
    /// <remarks>
    /// Test files are excluded so that a Go backend with a large Jest/TypeScript test suite
    /// does not incorrectly report "TypeScript" as the primary language.
    /// </remarks>
    private string DetectPrimaryLanguage()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var extToLang = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".go", "Go" }, { ".cs", "C#" }, { ".py", "Python" },
            { ".java", "Java" }, { ".kt", "Kotlin" },
            { ".ts", "TypeScript" }, { ".js", "JavaScript" },
            { ".php", "PHP" }, { ".rb", "Ruby" }
        };
        foreach (var f in _allFiles.Where(f => !f.IsDirectory && !IsTestFile(f)))
            if (extToLang.TryGetValue(f.Extension, out var lang))
                counts[lang] = counts.GetValueOrDefault(lang) + 1;

        return counts.Count == 0 ? "Go" : counts.OrderByDescending(x => x.Value).First().Key;
    }

    // ═══ Function Index ═══════════════════════════════════════════════════════

    /// <summary>
    /// Scans all non-test, non-generated source files and populates
    /// <see cref="_funcIndex"/> with every function definition found, grouped by name.
    /// </summary>
    /// <remarks>
    /// Files larger than <see cref="MaxSourceFileSizeBytes"/> (5 MB) are skipped to
    /// prevent OOM on auto-generated bundles (e.g. swagger client SDKs).
    /// <see cref="OutOfMemoryException"/> and <see cref="StackOverflowException"/> are
    /// explicitly re-thrown so fatal runtime conditions surface to the CLR rather than
    /// being silently swallowed by the catch-all handler.
    /// </remarks>
    private void BuildFunctionIndex()
    {
        var srcExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".go", ".cs", ".py", ".java", ".kt", ".ts", ".js", ".php", ".rb" };

        foreach (var file in _allFiles.Where(f =>
            !f.IsDirectory && srcExts.Contains(f.Extension) && !IsTestFile(f) && !IsGeneratedFile(f)
            && f.SizeBytes < MaxSourceFileSizeBytes))
        {
            try
            {
                var lines = GetCachedLines(file.AbsolutePath);
                foreach (var (name, lineNum) in ExtractFunctionDefinitions(file.Extension, lines))
                {
                    if (!_funcIndex.TryGetValue(name, out var list))
                        _funcIndex[name] = list = [];
                    list.Add((file, lineNum));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
        }
    }

    private static List<(string Name, int Line)> ExtractFunctionDefinitions(string ext, string[] lines)
    {
        var result = new List<(string, int)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            Match m;
            switch (ext.ToLowerInvariant())
            {
                case ".go":
                    m = RxGoFunc.Match(line);
                    if (m.Success) result.Add((m.Groups[1].Value, i + 1));
                    break;

                case ".cs":
                    m = RxCsMethod.Match(line);
                    if (m.Success && !IsKeyword(m.Groups[1].Value))
                        result.Add((m.Groups[1].Value, i + 1));
                    break;

                case ".py":
                    m = RxPyDef.Match(line);
                    if (m.Success) result.Add((m.Groups[1].Value, i + 1));
                    break;

                case ".java":
                case ".kt":
                    m = RxJavaDef.Match(line);
                    if (m.Success && !IsKeyword(m.Groups[1].Value))
                        result.Add((m.Groups[1].Value, i + 1));
                    break;

                case ".ts":
                case ".js":
                    m = RxJsFunction.Match(line);
                    if (m.Success) { result.Add((m.Groups[1].Value, i + 1)); break; }
                    m = RxJsArrow.Match(line);
                    if (m.Success) { result.Add((m.Groups[1].Value, i + 1)); break; }
                    m = RxJsClassMethod.Match(line);
                    if (m.Success && !IsKeyword(m.Groups[1].Value))
                        result.Add((m.Groups[1].Value, i + 1));
                    break;

                case ".php":
                    m = RxPhpFunction.Match(line);
                    if (m.Success) result.Add((m.Groups[1].Value, i + 1));
                    break;

                case ".rb":
                    m = RxRubyDef.Match(line);
                    if (m.Success) result.Add((m.Groups[1].Value, i + 1));
                    break;
            }
        }
        return result;
    }

    // ═══ SQL Preloading ═══════════════════════════════════════════════════════

    private void PreloadSqlQueries()
    {
        // 1. sqlc-format .sql files: -- name: FuncName :operation
        foreach (var file in _allFiles.Where(f => !f.IsDirectory && f.Extension == ".sql"))
        {
            try { ParseSqlcFile(file); }
            catch { }
        }

        // 2. Inline SQL strings inside source files
        var srcExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".go", ".cs", ".py", ".java", ".ts", ".js", ".php", ".rb" };
        foreach (var file in _allFiles.Where(f =>
            !f.IsDirectory && srcExts.Contains(f.Extension) && !IsTestFile(f) && !IsGeneratedFile(f)))
        {
            try { ExtractInlineSqlFromFile(file); }
            catch { }
        }
    }

    private void ParseSqlcFile(FileNode file)
    {
        // Uses GetCachedContent to avoid a second disk read when BuildFunctionIndex
        // has already populated _fileLineCache for this file (P1-A perf fix).
        var content = GetCachedContent(file.AbsolutePath);
        var matches = RxSqlcBlock.Matches(content);

        foreach (Match m in matches)
        {
            var funcName = m.Groups[1].Value.Trim();
            var operation = m.Groups[2].Value.Trim().ToUpper();
            var rawSql = m.Groups[3].Value.Trim();

            // Extract sqlc.arg(name) parameters and build composed SQL
            var (composed, paramList) = ComposeSqlcArgs(rawSql, funcName);

            _sqlCache[funcName] = new SqlQuery
            {
                Name = funcName,
                FunctionName = funcName,
                Operation = NormalizeSqlOperation(operation, rawSql),
                RawSql = rawSql,
                ComposedSql = composed,
                SourceFile = file.RelativePath,
                Parameters = paramList
            };
        }
    }

    private void ExtractInlineSqlFromFile(FileNode file)
    {
        var lines = GetCachedLines(file.AbsolutePath);
        string? currentFunc = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var funcDefs = ExtractFunctionDefinitions(file.Extension, [lines[i]]);
            if (funcDefs.Count > 0) currentFunc = funcDefs[0].Name;

            if (currentFunc == null || _sqlCache.ContainsKey(currentFunc)) continue;

            var sql = ExtractSqlFromLine(lines[i], file.Extension);
            if (sql != null)
            {
                _sqlCache[currentFunc] = new SqlQuery
                {
                    Name = currentFunc,
                    FunctionName = currentFunc,
                    Operation = NormalizeSqlOperation("", sql),
                    RawSql = sql,
                    ComposedSql = sql,
                    SourceFile = file.RelativePath
                };
            }
        }
    }

    private static string? ExtractSqlFromLine(string line, string ext)
    {
        var m = Regex.Match(line,
            @"[`""'@](SELECT|INSERT|UPDATE|DELETE|WITH)\s+[\s\S]{5,}",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Value.Trim('`', '"', '\'', '@').Trim();
    }

    // ═══ Route Handler Index (grpc-gateway / gin / echo / chi) ═══════════════

    /// <summary>
    /// Scans all Go source files for explicit route registrations:
    ///   grpc-gateway: mux.HandlePath(http.MethodXxx, "/path", server.HandlerFunc)
    ///   gin:          r.POST("/path", handler.Method) / router.POST("/path", fn)
    ///   echo:         e.POST("/path", handler.Method)
    ///   chi:          r.Post("/path", handler.Method)
    /// Populates _routeHandlerIndex so AnalyzeGoEndpoint can resolve routes
    /// before falling back to name-derived candidates.
    /// </summary>
    private void BuildRouteHandlerIndex()
    {
        // Method literal map: http.MethodPost → "POST", etc.
        var methodMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "MethodGet", "GET" }, { "MethodPost", "POST" }, { "MethodPut", "PUT" },
            { "MethodDelete", "DELETE" }, { "MethodPatch", "PATCH" },
            { "MethodOptions", "OPTIONS" }, { "MethodHead", "HEAD" }
        };

        foreach (var file in _allFiles.Where(f =>
            !f.IsDirectory && f.Extension == ".go" && !IsTestFile(f) && !IsGeneratedFile(f)))
        {
            try
            {
                var lines = GetCachedLines(file.AbsolutePath);
                foreach (var line in lines)
                {
                    TryParseHandlePathRoute(line, methodMap);
                    TryParseGinEchoChiRoute(line, methodMap);
                }
            }
            catch { }
        }
    }

    private void TryParseHandlePathRoute(string line, Dictionary<string, string> methodMap)
    {
        // Patterns: mux.HandlePath(http.MethodPost, "/v1/path", server.HandlerFunc)
        //           grpcMux.HandlePath("POST", "/v1/path", server.HandlerFunc)
        var m = Regex.Match(line,
            @"\.HandlePath\s*\(\s*(?:http\.(\w+)|""(\w+)"")\s*,\s*""([^""]+)""\s*,\s*\w+\.(\w+)\s*\)");
        if (!m.Success) return;

        var methodRaw = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
        var path = m.Groups[3].Value;
        var handlerFunc = m.Groups[4].Value;

        var method = methodMap.TryGetValue(methodRaw, out var mapped)
            ? mapped
            : methodRaw.ToUpper();

        _routeHandlerIndex[(method, path)] = handlerFunc;
    }

    private void TryParseGinEchoChiRoute(string line, Dictionary<string, string> methodMap)
    {
        // Patterns:
        //   r.POST("/v1/path", handler.Method)
        //   router.GET("/v1/path", handlerFunc)
        //   e.PUT("/v1/path", controller.Update)
        //   r.Post("/v1/path", handler.Method)   (chi uses uppercase first letter)
        var m = Regex.Match(line,
            @"(?:router|r|e|g|api|v\d+|group)\.(GET|POST|PUT|DELETE|PATCH|Get|Post|Put|Delete|Patch)\s*\(\s*""([^""]+)""\s*,\s*(?:\w+\.)?(\w+)\s*[,)]");
        if (!m.Success) return;

        var method = m.Groups[1].Value.ToUpper();
        var path = m.Groups[2].Value;
        var handlerFunc = m.Groups[3].Value;

        if (handlerFunc.Length > 1 && !IsKeyword(handlerFunc))
            _routeHandlerIndex[(method, path)] = handlerFunc;
    }



    /// <summary>
    /// Handles sqlc.arg(name) format — compose SQL by annotating each named argument.
    /// Also handles positional $1, $2 placeholders from Go function signatures.
    /// </summary>
    private (string ComposedSql, List<SqlParameter> Params) ComposeSqlcArgs(
        string rawSql, string funcName)
    {
        var paramList = new List<SqlParameter>();
        var composed = rawSql;

        // 1. Handle sqlc.arg(name) format (newer sqlc)
        var argMatches = RxSqlcArg.Matches(rawSql);
        int idx = 1;
        foreach (Match am in argMatches)
        {
            var pName = am.Groups[1].Value;
            paramList.Add(new SqlParameter
            {
                Name = pName,
                Type = "any",
                Placeholder = $"sqlc.arg({pName})"
            });
            // Replace sqlc.arg(name) with a readable placeholder value
            composed = composed.Replace(am.Value, $"'{pName}_value'");
            idx++;
        }

        // 2. Handle @param_name format (sqlc narg)
        if (paramList.Count == 0)
        {
            var nargMatches = RxNarg.Matches(rawSql);
            foreach (Match nm in nargMatches)
            {
                var pName = nm.Groups[1].Value;
                if (paramList.Any(p => p.Name == pName)) continue;
                paramList.Add(new SqlParameter
                {
                    Name = pName,
                    Type = "any",
                    Placeholder = $"@{pName}"
                });
            }
        }

        // 3. Handle positional $1, $2, ... (classic sqlc)
        if (paramList.Count == 0)
        {
            var (posComposed, posList) = ComposeSqlWithGoParams(rawSql, funcName);
            return (posList.Count > 0 ? posComposed : rawSql, posList);
        }

        return (composed, paramList);
    }

    private (string ComposedSql, List<SqlParameter> Params) ComposeSqlWithGoParams(
        string rawSql, string funcName)
    {
        var paramList = new List<SqlParameter>();
        var sig = FindGoFunctionSignature(funcName);
        if (sig == null) return (rawSql, paramList);

        // Extract parameters from Go function signature, skipping ctx
        var paramMatches = Regex.Matches(sig,
            @"(?:ctx\s+context\.Context[\s,]*)?(\w+)\s+([\w\.\*\[\]]+)\s*[,)]");
        int idx = 1;
        foreach (Match pm in paramMatches)
        {
            var pName = pm.Groups[1].Value;
            var pType = pm.Groups[2].Value;
            if (pName is "ctx" or "context" or "arg") continue;
            paramList.Add(new SqlParameter
            {
                Name = pName,
                Type = pType,
                Placeholder = $"${idx}"
            });
            idx++;
        }

        // Also handle sqlc arg structs: func (q *Queries) CreateUser(ctx context.Context, arg CreateUserParams) (User, error)
        if (paramList.Count == 0)
        {
            var argMatch = Regex.Match(sig, @"\barg\s+(\w+)");
            if (argMatch.Success)
            {
                // Look for struct fields matching $1, $2...
                var structName = argMatch.Groups[1].Value;
                var structDef = FindGoStructDefinition(structName);
                if (structDef != null)
                {
                    var fieldMatches = RxStructField.Matches(structDef);
                    idx = 1;
                    foreach (Match fm in fieldMatches)
                    {
                        paramList.Add(new SqlParameter
                        {
                            Name = fm.Groups[1].Value,
                            Type = fm.Groups[2].Value,
                            Placeholder = $"${idx++}"
                        });
                    }
                }
            }
        }

        var composed = rawSql;
        foreach (var p in paramList)
            composed = composed.Replace(p.Placeholder, $"'{p.Name}_value'");

        return (composed, paramList);
    }

    private string? FindGoFunctionSignature(string funcName)
    {
        if (!_funcIndex.TryGetValue(funcName, out var locations)) return null;
        foreach (var (file, lineNum) in locations)
        {
            if (file.Extension != ".go") continue;
            try
            {
                var lines = GetCachedLines(file.AbsolutePath);
                if (lineNum <= lines.Length) return lines[lineNum - 1];
            }
            catch { }
        }
        return null;
    }

    private string? FindGoStructDefinition(string structName)
        => _structBodyIndex.TryGetValue(structName, out var body) ? body : null;

    /// <summary>
    /// Populates <see cref="_structBodyIndex"/> by scanning all .go files once at
    /// construction time using the pre-compiled <see cref="RxGoStructDef"/> regex.
    /// Subsequent calls to <see cref="FindGoStructDefinition"/> are O(1) dictionary lookups
    /// instead of O(F × L) linear scans with per-file Regex allocations.
    /// </summary>
    private void BuildStructIndex()
    {
        foreach (var file in _allFiles.Where(f =>
            !f.IsDirectory && f.Extension == ".go" && !IsTestFile(f) && !IsGeneratedFile(f)))
        {
            var content = GetCachedContent(file.AbsolutePath);
            foreach (Match m in RxGoStructDef.Matches(content))
                _structBodyIndex.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }
    }

    private static string NormalizeSqlOperation(string hint, string sql)
    {
        if (hint is "one" or "many" or "exec" or "execrows" or "")
        {
            var kw = sql.TrimStart().Split(' ', '\n', '\t')[0].ToUpper();
            return kw is "SELECT" or "INSERT" or "UPDATE" or "DELETE" ? kw : "SELECT";
        }
        return hint.ToUpper() is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "ONE" or "MANY"
            ? sql.TrimStart().Split(' ')[0].ToUpper()
            : hint.ToUpper();
    }

    // ═══ Go Analyzer ═════════════════════════════════════════════════════════

    /// <summary>
    /// Traces a Go API endpoint through handler → service → repository layers.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>
    /// An <see cref="ApiTrace"/> containing resolved <see cref="TraceStep"/> objects and
    /// any associated <see cref="SqlQuery"/> records, or an empty trace when the handler
    /// function cannot be located in the indexed source files.
    /// </returns>
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item>Explicit route index (<see cref="FindGoRouteHandlerFromIndex"/>) — fastest, most accurate.</item>
    ///   <item>Name-derived candidates from <c>operationId</c> and path segments — heuristic fallback.</item>
    /// </list>
    /// Direct DB/store calls in the handler body are also collected when no separate
    /// Service layer is detected, covering the common Go pattern of embedding business
    /// logic directly on the gRPC server struct.
    /// </remarks>
    private ApiTrace AnalyzeGoEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };

        // 1. Check explicit route registrations first (HandlePath, gin/echo/chi)
        var handlerResult = FindGoRouteHandlerFromIndex(ep.Method, ep.Path);

        // 2. Fall back to name-derived candidates
        if (handlerResult == null)
        {
            var candidates = DeriveGoFunctionCandidates(ep.OperationId, ep.Path, ep.Method);
            handlerResult = FindHandlerInFiles(candidates, IsGoSourceFile);
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var handlerLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (handlerBody, hStart, hEnd) = ExtractFunctionBodyWithLines(
            handlerLines, handlerResult.Value.FuncName, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = ClassifyGoLayer(handlerResult.Value.File.RelativePath),
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = DescribeGoHandlerLayer(handlerResult.Value.File.RelativePath, handlerBody),
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractGoCalledFunctions(handlerBody)
        });

        // Find service calls from handler
        foreach (var (_, svcMethod) in ExtractGoServiceCalls(handlerBody))
        {
            var svcResult = FindHandlerInFiles([svcMethod],
                f => IsGoSourceFile(f) && IsGoServiceFile(f.RelativePath));

            if (svcResult == null) continue;

            var svcLines = GetCachedLines(svcResult.Value.File.AbsolutePath);
            var (svcBody, svcStart, svcEnd) = ExtractFunctionBodyWithLines(
                svcLines, svcMethod, svcResult.Value.Line);

            trace.Steps.Add(new TraceStep
            {
                Order = trace.Steps.Count + 1,
                Layer = "Service",
                File = svcResult.Value.File.RelativePath,
                Function = svcMethod,
                Description = $"業務邏輯層 — {Path.GetFileNameWithoutExtension(svcResult.Value.File.Name)}",
                StartLine = svcStart,
                EndLine = svcEnd,
                CalledFunctions = ExtractGoCalledFunctions(svcBody)
            });

            // Collect all store calls: direct + through private helper methods
            var allStoreCalls = CollectGoStoreCalls(svcBody, svcLines, svcResult.Value.File);
            foreach (var repoMethod in allStoreCalls)
            {
                var repoResult = FindHandlerInFiles([repoMethod],
                    f => IsGoSourceFile(f) && IsGoRepositoryFile(f.RelativePath));

                if (repoResult != null && !trace.Steps.Any(s => s.Function == repoMethod))
                {
                    var repoLines = GetCachedLines(repoResult.Value.File.AbsolutePath);
                    var (repoBody, repoStart, repoEnd) = ExtractFunctionBodyWithLines(
                        repoLines, repoMethod, repoResult.Value.Line);

                    trace.Steps.Add(new TraceStep
                    {
                        Order = trace.Steps.Count + 1,
                        Layer = "Repository",
                        File = repoResult.Value.File.RelativePath,
                        Function = repoMethod,
                        Description = "資料存取層（sqlc 生成）",
                        StartLine = repoStart,
                        EndLine = repoEnd
                    });

                    // Scan repository body for sqlc query calls (q.XxxFunc / db.XxxFunc)
                    // This handles transaction wrappers that delegate to sqlc-generated functions.
                    foreach (var sqlcMethod in ExtractGoSqlcCalls(repoBody))
                        if (_sqlCache.TryGetValue(sqlcMethod, out var sqlcSql))
                            trace.SqlQueries.Add(EnrichSqlQueryFromCallSite(sqlcSql, repoBody));
                }

                if (_sqlCache.TryGetValue(repoMethod, out var sql))
                    trace.SqlQueries.Add(EnrichSqlQueryFromCallSite(sql, svcBody));
            }
        }

        // Also check for direct DB/store calls in handler body (no service layer detected).
        // Use CollectGoStoreCalls (not just ExtractGoStoreCalls) so that private helper
        // methods on the same receiver (e.g. server.donateCodes_List → server.store.Xxx)
        // defined in the same file are followed recursively, matching Go projects that
        // embed business logic as unexported *Server methods instead of a separate Service struct.
        if (trace.Steps.Count == 1)
        {
            var allDbMethods = CollectGoStoreCalls(handlerBody, handlerLines, handlerResult.Value.File);
            foreach (var dbMethod in allDbMethods)
                if (_sqlCache.TryGetValue(dbMethod, out var sql))
                    trace.SqlQueries.Add(EnrichSqlQueryFromCallSite(sql, handlerBody));
        }

        return trace;
    }

    private static List<string> DeriveGoFunctionCandidates(string operationId, string path, string method)
    {
        var cands = new List<string>();

        if (!string.IsNullOrEmpty(operationId))
        {
            cands.Add(operationId);

            // Remove service name prefix (part before first _)
            var idx = operationId.IndexOf('_');
            if (idx > 0 && idx < operationId.Length - 1)
            {
                var withoutPrefix = operationId[(idx + 1)..];
                cands.Add(withoutPrefix);

                // CamelCase version
                var camel = string.Concat(withoutPrefix.Split('_')
                    .Select(p => p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : ""));
                if (camel != withoutPrefix) cands.Add(camel);

                // Last segment only (e.g., "GetChurnDetail" → try "Detail")
                var lastSeg = withoutPrefix.Split('_').Last();
                if (lastSeg.Length > 2) cands.Add(lastSeg);
            }
        }

        // Path-derived: /v1/cms/dashboard/churn/detail → CmsDashboardChurnDetail
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !RxVersionSeg.IsMatch(s) && !s.StartsWith('{'))
            .ToArray();
        if (segs.Length > 0)
        {
            var pathFunc = string.Concat(segs.Select(s =>
                string.Concat(s.Split('_', '-')
                    .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : ""))));
            cands.Add(pathFunc);
        }

        return cands.Distinct().Where(c => c.Length > 1).ToList();
    }

    private static List<(string Service, string Method)> ExtractGoServiceCalls(string body)
    {
        var calls = new List<(string, string)>();

        // Pattern 1: any_receiver.SomeService.Method(  — handles server/s/d/h/svc/impl etc.
        var m1 = Regex.Matches(body,
            @"\w+\.\s*(\w*[Ss]ervice\w*|\w*[Ss]vc\w*|\w*[Mm]anager\w*|\w*[Hh]andler\w*)\.\s*(\w+)\s*\(");
        foreach (Match m in m1)
        {
            if (!IsKeyword(m.Groups[2].Value))
                calls.Add((m.Groups[1].Value, m.Groups[2].Value));
        }

        // Pattern 2: StandaloneServiceVar.Method(  e.g. subscriptionService.Import(
        var m2 = Regex.Matches(body,
            @"(?<!\.)(\w+[Ss]ervice\w*|\w+[Ss]vc\w*)\.(\w+)\s*\(");
        foreach (Match m in m2)
        {
            if (!IsKeyword(m.Groups[2].Value))
                calls.Add((m.Groups[1].Value, m.Groups[2].Value));
        }

        return calls.DistinctBy(c => c.Item2).Take(8).ToList();
    }

    private static List<string> ExtractGoStoreCalls(string body)
    {
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QueryContext", "ExecContext", "QueryRowContext", "BeginTx", "Begin",
            "Commit", "Rollback", "Prepare", "PrepareContext", "Context", "WithTx",
            "Close", "Ping", "Stats"
        };
        // Match receiver.storeField.Method( — covers naming variants: server.store, d.db, s.db_store, r.repo
        // \w*store  → matches store, db_store, my_store
        // \w*db     → matches db, my_db
        // \w*repo   → matches repo, my_repo
        // \w*quer   → matches query, queries
        var matches = Regex.Matches(body,
            @"(?:\w+\.\w*store|\bstore\b|\bq\b|\bdb\b|\w+\.\w*db|\w+\.\w*repo|\w+\.\w*quer)\s*\.\s*([A-Z]\w+)\s*\(");
        return matches.Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(n => !excludes.Contains(n))
            .Distinct()
            .Take(15)
            .ToList();
    }

    /// <summary>
    /// Extracts sqlc query function calls from a repository/transaction body.
    /// Matches patterns like q.Subscriptions_GetByUserID(...) or queries.Create(...)
    /// where the receiver is a sqlc Queries object (q / queries / db / tx).
    /// </summary>
    private static List<string> ExtractGoSqlcCalls(string body)
    {
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QueryContext", "ExecContext", "QueryRowContext", "BeginTx", "Begin",
            "Commit", "Rollback", "WithTx", "New", "Close"
        };
        // q.FunctionName(  |  queries.FunctionName(  |  tx.FunctionName(
        var matches = Regex.Matches(body,
            @"(?:\bq\b|\bqueries\b|\btx\b|\bqtx\b)\s*\.\s*([A-Z]\w+)\s*\(");
        return matches.Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(n => !excludes.Contains(n))
            .Distinct()
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// Enriches a SQL query's ComposedSql by substituting literal (hardcoded) values
    /// detected at the call site struct literal. For example, if the call site contains
    /// <c>EnableDeletedAt: false</c>, replaces <c>'enable_deleted_at_value'</c>
    /// with <c>false</c>, making the composed SQL accurate for this call.
    /// Runtime values (variables, function results) are left as symbolic placeholders.
    /// </summary>
    private static SqlQuery EnrichSqlQueryFromCallSite(SqlQuery query, string callSiteBody)
    {
        if (string.IsNullOrEmpty(callSiteBody) ||
            string.IsNullOrEmpty(query.ComposedSql) ||
            query.Parameters.Count == 0)
            return query;

        // Find the struct literal passed to this sqlc function call.
        // Matches: FuncName(ctx, PkgName.FuncNameParams{ field: value, ... })
        var structMatch = Regex.Match(callSiteBody,
            $@"\b{Regex.Escape(query.Name)}\s*\([^{{]*\{{([^}}]*)\}}",
            RegexOptions.Singleline);

        if (!structMatch.Success) return query;

        var structBody = structMatch.Groups[1].Value;
        var composed = query.ComposedSql;
        var enriched = false;

        foreach (Match fm in RxStructInit.Matches(structBody))
        {
            var goFieldValue = fm.Groups[2].Value.Trim().TrimEnd(',').Trim();
            var literalValue = ExtractGoLiteralValue(goFieldValue);
            if (literalValue == null) continue;

            var argName = CamelToSnake(fm.Groups[1].Value.Trim());
            // Match the plain placeholder pattern (no /* */ comment prefix)
            var placeholder = $"'{argName}_value'";
            if (!composed.Contains(placeholder)) continue;

            composed = composed.Replace(placeholder, literalValue);
            enriched = true;
        }

        if (!enriched) return query;

        return new SqlQuery
        {
            Name = query.Name,
            Operation = query.Operation,
            RawSql = query.RawSql,
            ComposedSql = composed,
            SourceFile = query.SourceFile,
            FunctionName = query.FunctionName,
            Parameters = query.Parameters
        };
    }

    /// <summary>
    /// Converts a Go struct field name (CamelCase) to the snake_case name used by sqlc.arg().
    /// Example: "DateStartedAtValidate" → "date_started_at_validate", "UserID" → "user_i_d" (no, handled below)
    /// </summary>
    private static string CamelToSnake(string camel)
    {
        var s = RxCamelToSnake1.Replace(camel, "_$1");
        s = RxCamelToSnake2.Replace(s, "_$1");
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Returns a SQL-literal string if the Go expression is a compile-time constant,
    /// or null if it is a runtime value (variable, function call, etc.).
    /// </summary>
    private static string? ExtractGoLiteralValue(string expr)
    {
        expr = expr.Trim();
        if (expr is "true" or "false") return expr;
        if (expr == "nil") return "NULL";
        if (RxNumericLiteral.IsMatch(expr)) return expr;
        var strMatch = RxGoStringLiteral.Match(expr);
        if (strMatch.Success) return $"'{strMatch.Groups[1].Value}'";
        var rawMatch = RxGoRawLiteral.Match(expr);
        if (rawMatch.Success) return $"'{rawMatch.Groups[1].Value}'";
        return null;
    }

    /// <summary>
    /// Collects all store/DB method calls from a service body, including calls
    /// through private helper methods. Recurses up to maxDepth levels to handle
    /// deep Go service patterns like GetX → getHelper → getInnerHelper → d.store.Xxx()
    /// </summary>
    private List<string> CollectGoStoreCalls(string svcBody, string[] svcAllLines, FileNode svcFile, int maxDepth = 4)
    {
        var allStoreCalls = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectStoreCallsRecursive(svcBody, svcAllLines, maxDepth, allStoreCalls, visited);
        return allStoreCalls.Distinct().Take(20).ToList();
    }

    private void CollectStoreCallsRecursive(
        string body, string[] allLines, int depth,
        List<string> collected, HashSet<string> visited)
    {
        // Direct store calls in this body
        collected.AddRange(ExtractGoStoreCalls(body));

        if (depth <= 0) return;

        // Find receiver method calls in the same file — both lowercase (unexported)
        // and uppercase (exported) names, since helper logic is sometimes in exported methods.
        // We validate the name actually exists as a method in allLines to avoid chasing
        // external package calls.
        var helperCalls = Regex.Matches(body, @"\w+\s*\.\s*([A-Za-z]\w+)\s*\(")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(n => n.Length > 2 && !IsKeyword(n))
            .Distinct();

        foreach (var helperName in helperCalls)
        {
            if (!visited.Add(helperName)) continue;

            // Find the helper method in the same file (receiver method pattern).
            // Compile the regex ONCE per helper (not once per line) to avoid 500+
            // Regex allocations on files with many helpers (P1-C perf fix).
            int helperLine = -1;
            var receiverRx = new Regex(
                $@"func\s+\([^)]+\)\s+{Regex.Escape(helperName)}\s*\(",
                RegexOptions.Compiled);
            for (int i = 0; i < allLines.Length; i++)
            {
                if (receiverRx.IsMatch(allLines[i]))
                {
                    helperLine = i + 1;
                    break;
                }
            }
            if (helperLine < 0)
            {
                // Cross-file lookup: helper is defined in a different source file.
                // Use _funcIndex (built from all project files) to locate it.
                if (!_funcIndex.TryGetValue(helperName, out var locations)) continue;
                foreach (var (xFile, xLine) in locations)
                {
                    if (xFile.Extension != ".go" || IsTestFile(xFile) || IsGeneratedFile(xFile)) continue;
                    try
                    {
                        var crossLines = GetCachedLines(xFile.AbsolutePath);
                        var (crossBody, _, _) = ExtractFunctionBodyWithLines(crossLines, helperName, xLine);
                        CollectStoreCallsRecursive(crossBody, crossLines, depth - 1, collected, visited);
                    }
                    catch { }
                    break; // use first match
                }
                continue;
            }

            var (helperBody, _, _) = ExtractFunctionBodyWithLines(allLines, helperName, helperLine);
            CollectStoreCallsRecursive(helperBody, allLines, depth - 1, collected, visited);
        }
    }

    private static List<string> ExtractGoCalledFunctions(string body)
    {
        var m = Regex.Matches(body, @"(?:[a-zA-Z_]\w*)\.([A-Z]\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(12).ToList();
    }

    private static string ClassifyGoLayer(string path)
    {
        if (IsGoServiceFile(path)) return "Service";
        if (IsGoRepositoryFile(path)) return "Repository";
        return "Handler";
    }

    private static string DescribeGoHandlerLayer(string path, string body = "")
    {
        if (path.Contains("/grpc/") || path.Contains("grpc_server")) return "gRPC 請求入口（HTTP → gRPC Gateway）";
        if (path.Contains("server.go") || path.Contains("_server.go")) return "gRPC 伺服器實作（請求入口）";
        if (path.Contains("handler") || path.Contains("controller")) return "HTTP 請求處理層";
        if (path.Contains("/api/") || path.StartsWith("api/"))
        {
            // Detect multipart / file upload patterns
            if (body.Contains("ParseMultipartForm") || body.Contains("FormFile") || body.Contains("csv.NewReader"))
                return "HTTP Handler（grpc-gateway）— 多部分表單 / CSV 檔案上傳";
            return "HTTP Handler（grpc-gateway）";
        }
        return "API 請求入口處理函式";
    }

    /// <summary>
    /// Resolves a route (method + path) to a handler function using the
    /// explicitly registered route index built from HandlePath / gin / echo / chi calls.
    /// Tries exact match first, then normalized path match.
    /// </summary>
    private (FileNode File, string FuncName, int Line)? FindGoRouteHandlerFromIndex(
        string httpMethod, string apiPath)
    {
        var method = httpMethod.ToUpper();

        // Try exact match first
        if (_routeHandlerIndex.TryGetValue((method, apiPath), out var handlerFunc))
            return FindHandlerInFiles([handlerFunc], IsGoSourceFile);

        // Strip generic /api prefix when present (keeps the leading slash for path matching)
        var normalizedPath = apiPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? apiPath[4..]  // e.g. "/api/v1/users" → "/v1/users"
            : apiPath;
        if (_routeHandlerIndex.TryGetValue((method, normalizedPath), out handlerFunc))
            return FindHandlerInFiles([handlerFunc], IsGoSourceFile);

        // Try fuzzy: path ends-with match (e.g. registered as /v1/path, called as /api/svc/v1/path)
        foreach (var kv in _routeHandlerIndex)
        {
            if (kv.Key.Method != method) continue;
            if (apiPath.EndsWith(kv.Key.Path, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Path.EndsWith(apiPath, StringComparison.OrdinalIgnoreCase))
            {
                var result = FindHandlerInFiles([kv.Value], IsGoSourceFile);
                if (result != null) return result;
            }
        }
        return null;
    }

    private static bool IsGoSourceFile(FileNode f)
        => f.Extension == ".go" && !f.Name.EndsWith("_test.go") && !f.IsDirectory
           && !IsGeneratedFile(f);

    private static bool IsGoServiceFile(string path) =>
        path.Contains("/service/") || path.StartsWith("service/") ||
        path.Contains("_service.go") || path.EndsWith("service.go");

    private static bool IsGoRepositoryFile(string path) =>
        path.Contains("/sqlc/") || path.Contains("/repo/") || path.Contains("/db/") ||
        path.Contains("_query.go") || path.Contains("query.sql.go") ||
        path.EndsWith("repository.go") || path.Contains("/store/") ||
        path.Contains("/query/");

    // ═══ C# Analyzer ═════════════════════════════════════════════════════════

    /// <summary>
    /// Traces an ASP.NET Core endpoint through Controller → Service → Repository layers.
    /// Finds the action method by matching <c>[HttpGet/Post/…]</c> attribute annotations,
    /// then follows injected service calls (<c>_xyzService.Method()</c>) into service files
    /// and repository calls (<c>_xyzRepo.Method()</c>) into data-access files.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>An <see cref="ApiTrace"/> with resolved steps and inline SQL queries, or an empty trace.</returns>
    private ApiTrace AnalyzeCSharpEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };
        var csFiles = _allFiles.Where(f => !f.IsDirectory && f.Extension == ".cs" && !IsTestFile(f)).ToList();

        (FileNode File, string FuncName, int Line)? handlerResult = null;

        foreach (var file in csFiles)
        {
            var lines = GetCachedLines(file.AbsolutePath);
            var r = FindCSharpActionMethod(lines, ep.Method, ep.Path, file);
            if (r.HasValue) { handlerResult = (file, r.Value.Name, r.Value.Line); break; }
        }

        if (handlerResult == null)
        {
            var cands = DeriveCSharpMethodCandidates(ep.OperationId, ep.Path, ep.Method);
            handlerResult = FindHandlerInFiles(cands, f => f.Extension == ".cs" && !IsTestFile(f));
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var handlerLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (handlerBody, hStart, hEnd) = ExtractFunctionBodyWithLines(
            handlerLines, handlerResult.Value.FuncName, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = "Controller",
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = $"ASP.NET Core Controller — {Path.GetFileNameWithoutExtension(handlerResult.Value.File.Name)}",
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractCSharpCalledMethods(handlerBody)
        });

        foreach (var svcMethod in ExtractCSharpServiceCalls(handlerBody))
        {
            var svcResult = FindHandlerInFiles([svcMethod],
                f => f.Extension == ".cs" && !IsTestFile(f) && IsCSharpServiceFile(f.RelativePath));
            if (svcResult == null) continue;

            var svcLines = GetCachedLines(svcResult.Value.File.AbsolutePath);
            var (svcBody, svcStart, svcEnd) = ExtractFunctionBodyWithLines(
                svcLines, svcMethod, svcResult.Value.Line);

            trace.Steps.Add(new TraceStep
            {
                Order = trace.Steps.Count + 1,
                Layer = "Service",
                File = svcResult.Value.File.RelativePath,
                Function = svcMethod,
                Description = $"業務邏輯層 — {Path.GetFileNameWithoutExtension(svcResult.Value.File.Name)}",
                StartLine = svcStart,
                EndLine = svcEnd,
                CalledFunctions = ExtractCSharpCalledMethods(svcBody)
            });

            foreach (var repoMethod in ExtractCSharpRepoCalls(svcBody))
            {
                var repoResult = FindHandlerInFiles([repoMethod],
                    f => f.Extension == ".cs" && !IsTestFile(f) && IsCSharpRepoFile(f.RelativePath));
                if (repoResult != null)
                {
                    var repoLines = GetCachedLines(repoResult.Value.File.AbsolutePath);
                    var (_, repoStart, repoEnd) = ExtractFunctionBodyWithLines(
                        repoLines, repoMethod, repoResult.Value.Line);
                    trace.Steps.Add(new TraceStep
                    {
                        Order = trace.Steps.Count + 1,
                        Layer = "Repository",
                        File = repoResult.Value.File.RelativePath,
                        Function = repoMethod,
                        Description = "資料存取層（Repository / DbContext）",
                        StartLine = repoStart,
                        EndLine = repoEnd
                    });
                }
                if (_sqlCache.TryGetValue(repoMethod, out var sql)) trace.SqlQueries.Add(sql);
            }

            foreach (var sq in ExtractCSharpInlineSql(svcBody, svcResult.Value.File.RelativePath))
                trace.SqlQueries.Add(sq);
        }

        return trace;
    }

    private static (string Name, int Line)? FindCSharpActionMethod(
        string[] lines, string httpMethod, string path, FileNode file)
    {
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!IsHttpAttribute(line, httpMethod)) continue;

            var attrPath = Regex.Match(line, @"\(""([^""]+)""\)").Groups[1].Value;
            if (!string.IsNullOrEmpty(attrPath) && !PathsMatch(path, attrPath)) continue;

            for (int j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
            {
                var m = Regex.Match(lines[j],
                    @"(?:public|private|protected)(?:\s+(?:async|virtual|override))*\s+\S+\s+(\w+)\s*\(");
                if (m.Success && !IsKeyword(m.Groups[1].Value))
                    return (m.Groups[1].Value, j + 1);
            }
        }
        return null;
    }

    private static bool IsHttpAttribute(string line, string method) =>
        line.StartsWith($"[Http{char.ToUpper(method[0])}{method[1..].ToLower()}", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[Route(", StringComparison.OrdinalIgnoreCase);

    private static bool PathsMatch(string apiPath, string attrPath)
    {
        var normalize = (string p) => Regex.Replace(p.Trim('/').ToLower(), @"\{[^}]+\}", "{x}");
        return normalize(apiPath).Contains(normalize(attrPath)) ||
               normalize(attrPath).Contains(normalize(apiPath));
    }

    private static List<string> DeriveCSharpMethodCandidates(string operationId, string path, string method)
    {
        var cands = new List<string>();
        if (!string.IsNullOrEmpty(operationId)) cands.Add(operationId);

        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !RxVersionSeg.IsMatch(s))
            .Select(s => s.StartsWith('{')
                ? "By" + char.ToUpper(s[1]) + s[2..].TrimEnd('}')
                : string.Concat(s.Split('_', '-').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : "")))
            .ToArray();

        var prefix = char.ToUpper(method[0]) + method[1..].ToLower();
        cands.Add(prefix + string.Concat(segs));
        if (segs.Length > 0) cands.Add(segs.Last());

        return cands.Distinct().ToList();
    }

    private static List<string> ExtractCSharpServiceCalls(string body)
    {
        var m = Regex.Matches(body,
            @"(?:await\s+)?(?:_\w+[Ss]ervice\w*|_service\w*|this\._\w+)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(8).ToList();
    }

    private static List<string> ExtractCSharpRepoCalls(string body)
    {
        var m = Regex.Matches(body,
            @"(?:await\s+)?(?:_\w+[Rr]epo\w*|_\w+[Rr]epository\w*|_context\w*|_db\w*)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(8).ToList();
    }

    private static List<string> ExtractCSharpCalledMethods(string body)
    {
        var m = Regex.Matches(body, @"(?:await\s+)?(?:\w+)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(12).ToList();
    }

    private static List<SqlQuery> ExtractCSharpInlineSql(string body, string filePath)
    {
        var result = new List<SqlQuery>();
        var matches = Regex.Matches(body,
            @"@?""(SELECT|INSERT|UPDATE|DELETE)[\s\S]*?""|""(SELECT|INSERT|UPDATE|DELETE)[^""]*""",
            RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var sql = (m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (sql.Length < 10) continue;
            result.Add(new SqlQuery
            {
                Name = $"inline_{result.Count + 1}",
                FunctionName = $"inline_{result.Count + 1}",
                Operation = NormalizeSqlOperation("", sql),
                RawSql = sql,
                ComposedSql = sql,
                SourceFile = filePath
            });
        }
        return result.Take(3).ToList();
    }

    private static bool IsCSharpServiceFile(string path) =>
        path.Contains("/Services/") || path.Contains("/Service/") ||
        path.EndsWith("Service.cs") || path.Contains("Service.");

    private static bool IsCSharpRepoFile(string path) =>
        path.Contains("/Repositories/") || path.Contains("/Repository/") ||
        path.EndsWith("Repository.cs") || path.Contains("/Data/") || path.EndsWith("Context.cs");

    // ═══ Python Analyzer ══════════════════════════════════════════════════════

    /// <summary>
    /// Traces a Python API endpoint through Handler → Service layers.
    /// Identifies the handler by matching FastAPI/Flask/Django route decorators
    /// (<c>@app.get</c>, <c>@router.post</c>, <c>methods=[…]</c>) against the
    /// endpoint's method and path, then follows <c>self.xyzService.method()</c> calls.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>An <see cref="ApiTrace"/> with resolved steps and extracted SQL queries, or an empty trace.</returns>
    private ApiTrace AnalyzePythonEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };
        var pyFiles = _allFiles.Where(f => !f.IsDirectory && f.Extension == ".py" && !IsTestFile(f)).ToList();
        (FileNode File, string FuncName, int Line)? handlerResult = null;

        foreach (var file in pyFiles)
        {
            var lines = GetCachedLines(file.AbsolutePath);
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (!IsPythonRouteDecorator(lines[i].Trim(), ep.Method, ep.Path)) continue;
                for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                {
                    var m = Regex.Match(lines[j], @"(?:async\s+)?def\s+(\w+)\s*\(");
                    if (m.Success) { handlerResult = (file, m.Groups[1].Value, j + 1); break; }
                }
                if (handlerResult != null) break;
            }
            if (handlerResult != null) break;
        }

        if (handlerResult == null)
        {
            var cands = DerivePythonCandidates(ep.OperationId, ep.Path, ep.Method);
            handlerResult = FindHandlerInFiles(cands, f => f.Extension == ".py" && !IsTestFile(f));
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var handlerLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (handlerBody, hStart, hEnd) = ExtractPythonFunctionBody(handlerLines, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = "Handler",
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = "FastAPI/Flask 路由處理函式",
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractPythonCalledFunctions(handlerBody)
        });

        foreach (var svcFunc in ExtractPythonServiceCalls(handlerBody))
        {
            var svcResult = FindHandlerInFiles([svcFunc],
                f => f.Extension == ".py" && !IsTestFile(f) && IsPythonServiceFile(f.RelativePath));
            if (svcResult == null) continue;

            var svcLines = GetCachedLines(svcResult.Value.File.AbsolutePath);
            var (svcBody, svcStart, svcEnd) = ExtractPythonFunctionBody(svcLines, svcResult.Value.Line);

            trace.Steps.Add(new TraceStep
            {
                Order = trace.Steps.Count + 1,
                Layer = "Service",
                File = svcResult.Value.File.RelativePath,
                Function = svcFunc,
                Description = $"業務邏輯層 — {Path.GetFileNameWithoutExtension(svcResult.Value.File.Name)}",
                StartLine = svcStart,
                EndLine = svcEnd,
                CalledFunctions = ExtractPythonCalledFunctions(svcBody)
            });

            foreach (var sq in ExtractPythonSql(svcBody, svcResult.Value.File.RelativePath))
                trace.SqlQueries.Add(sq);
        }

        return trace;
    }

    private static bool IsPythonRouteDecorator(string line, string method, string path)
    {
        if (!line.StartsWith('@')) return false;
        if (!Regex.IsMatch(line, $@"\.{method.ToLower()}\s*\(", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(line, $@"methods=\[.*{method}.*\]", RegexOptions.IgnoreCase)) return false;
        var attrPath = Regex.Match(line, @"\(['""](.*?)['""]").Groups[1].Value;
        return string.IsNullOrEmpty(attrPath) || PathsMatch(path, attrPath);
    }

    private static List<string> DerivePythonCandidates(string operationId, string path, string method)
    {
        var cands = new List<string>();
        if (!string.IsNullOrEmpty(operationId)) cands.Add(operationId);
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !RxVersionSeg.IsMatch(s) && !s.StartsWith('{'))
            .ToArray();
        if (segs.Length > 0)
        {
            cands.Add(string.Join("_", segs));
            cands.Add($"{method.ToLower()}_{string.Join("_", segs)}");
        }
        return cands.Distinct().ToList();
    }

    private static (string body, int start, int end) ExtractPythonFunctionBody(string[] lines, int funcLine)
    {
        var sb = new System.Text.StringBuilder();
        int start = funcLine;
        if (start > lines.Length) return ("", funcLine, funcLine);

        var defLine = lines[start - 1];
        var bodyIndent = (defLine.Length - defLine.TrimStart().Length) + 4;
        int end = start;

        for (int i = start; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim().Length > 0 && (l.Length - l.TrimStart().Length) < bodyIndent && !l.TrimStart().StartsWith('#'))
                break;
            sb.AppendLine(l);
            end = i + 1;
        }
        return (sb.ToString(), start, end);
    }

    private static List<string> ExtractPythonServiceCalls(string body)
    {
        var m = Regex.Matches(body,
            @"(?:self\.\w*service\w*|self\.\w*_service|service\w*)\s*\.\s*(\w+)\s*\(",
            RegexOptions.IgnoreCase);
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(8).ToList();
    }

    private static List<string> ExtractPythonCalledFunctions(string body)
    {
        var m = Regex.Matches(body, @"(?:await\s+)?(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value)
            .Where(n => n.Length > 2 && !IsKeyword(n)).Distinct().Take(12).ToList();
    }

    private static List<SqlQuery> ExtractPythonSql(string body, string filePath)
    {
        var result = new List<SqlQuery>();
        // Match Python triple-quoted or single-quoted SQL strings
        var m = Regex.Matches(body,
            @"(?:'{3}|""{3}|'|"")(SELECT|INSERT|UPDATE|DELETE)[\s\S]*?(?:'{3}|""{3}|'|"")",
            RegexOptions.IgnoreCase);
        foreach (Match match in m)
        {
            var sql = match.Value.Trim('"', '\'').Trim();
            if (sql.Length < 10) continue;
            result.Add(new SqlQuery
            {
                Name = $"sql_{result.Count + 1}",
                FunctionName = $"sql_{result.Count + 1}",
                Operation = NormalizeSqlOperation("", sql),
                RawSql = sql,
                ComposedSql = sql,
                SourceFile = filePath
            });
        }
        return result.Take(5).ToList();
    }

    private static bool IsPythonServiceFile(string path) =>
        path.Contains("/services/") || path.Contains("/service/") ||
        path.EndsWith("_service.py") || path.EndsWith("service.py");

    // ═══ Java / Spring Analyzer ══════════════════════════════════════════════

    /// <summary>
    /// Traces a Java/Kotlin Spring MVC endpoint through Controller → Service → Repository layers.
    /// Identifies the handler by matching <c>@GetMapping</c> / <c>@PostMapping</c> / etc.
    /// annotations, then follows <c>xyzService.method()</c> and
    /// <c>xyzRepository.method()</c> / <c>jdbcTemplate.method()</c> calls.
    /// Also extracts JPA <c>@Query</c> / MyBatis <c>@Select</c> annotations as SQL queries.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>An <see cref="ApiTrace"/> with resolved steps and annotation-derived SQL, or an empty trace.</returns>
    private ApiTrace AnalyzeJavaEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };
        var javaFiles = _allFiles.Where(f => !f.IsDirectory &&
            (f.Extension == ".java" || f.Extension == ".kt") && !IsTestFile(f)).ToList();

        (FileNode File, string FuncName, int Line)? handlerResult = null;

        foreach (var file in javaFiles)
        {
            var lines = GetCachedLines(file.AbsolutePath);
            var r = FindJavaControllerMethod(lines, ep.Method, ep.Path);
            if (r.HasValue) { handlerResult = (file, r.Value.Name, r.Value.Line); break; }
        }

        if (handlerResult == null)
        {
            var cands = DeriveCSharpMethodCandidates(ep.OperationId, ep.Path, ep.Method);
            handlerResult = FindHandlerInFiles(cands,
                f => (f.Extension == ".java" || f.Extension == ".kt") && !IsTestFile(f));
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var handlerLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (handlerBody, hStart, hEnd) = ExtractFunctionBodyWithLines(
            handlerLines, handlerResult.Value.FuncName, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = "Controller",
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = $"Spring Controller — {Path.GetFileNameWithoutExtension(handlerResult.Value.File.Name)}",
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractJavaCalledMethods(handlerBody)
        });

        foreach (var svcMethod in ExtractJavaServiceCalls(handlerBody))
        {
            var svcResult = FindHandlerInFiles([svcMethod],
                f => (f.Extension == ".java" || f.Extension == ".kt") &&
                     !IsTestFile(f) && IsJavaServiceFile(f.RelativePath));
            if (svcResult == null) continue;

            var svcLines = GetCachedLines(svcResult.Value.File.AbsolutePath);
            var (svcBody, svcStart, svcEnd) = ExtractFunctionBodyWithLines(
                svcLines, svcMethod, svcResult.Value.Line);

            trace.Steps.Add(new TraceStep
            {
                Order = trace.Steps.Count + 1,
                Layer = "Service",
                File = svcResult.Value.File.RelativePath,
                Function = svcMethod,
                Description = $"業務邏輯層（@Service）— {Path.GetFileNameWithoutExtension(svcResult.Value.File.Name)}",
                StartLine = svcStart,
                EndLine = svcEnd,
                CalledFunctions = ExtractJavaCalledMethods(svcBody)
            });

            foreach (var repoMethod in ExtractJavaRepoCalls(svcBody))
            {
                var repoResult = FindHandlerInFiles([repoMethod],
                    f => (f.Extension == ".java" || f.Extension == ".kt") &&
                         !IsTestFile(f) && IsJavaRepoFile(f.RelativePath));
                if (repoResult != null)
                {
                    var repoLines = GetCachedLines(repoResult.Value.File.AbsolutePath);
                    var (repoBody, repoStart, repoEnd) = ExtractFunctionBodyWithLines(
                        repoLines, repoMethod, repoResult.Value.Line);
                    trace.Steps.Add(new TraceStep
                    {
                        Order = trace.Steps.Count + 1,
                        Layer = "Repository",
                        File = repoResult.Value.File.RelativePath,
                        Function = repoMethod,
                        Description = "資料存取層（JPA Repository / MyBatis）",
                        StartLine = repoStart,
                        EndLine = repoEnd
                    });
                    foreach (var sq in ExtractJavaQueryAnnotation(repoLines, repoResult.Value.Line, repoResult.Value.File.RelativePath))
                        trace.SqlQueries.Add(sq);
                }
                if (_sqlCache.TryGetValue(repoMethod, out var sql)) trace.SqlQueries.Add(sql);
            }
        }

        return trace;
    }

    private static (string Name, int Line)? FindJavaControllerMethod(string[] lines, string httpMethod, string path)
    {
        var annotation = httpMethod switch
        {
            "GET" => "GetMapping",
            "POST" => "PostMapping",
            "PUT" => "PutMapping",
            "DELETE" => "DeleteMapping",
            "PATCH" => "PatchMapping",
            _ => "RequestMapping"
        };
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.Contains($"@{annotation}") && !line.Contains("@RequestMapping")) continue;
            var attrPath = Regex.Match(line, @"\(""([^""]+)""\)").Groups[1].Value;
            if (!string.IsNullOrEmpty(attrPath) && !PathsMatch(path, attrPath)) continue;
            for (int j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
            {
                var m = Regex.Match(lines[j],
                    @"(?:public|private|protected)(?:\s+(?:static|final))*\s+\S+\s+(\w+)\s*\(");
                if (m.Success && !IsKeyword(m.Groups[1].Value))
                    return (m.Groups[1].Value, j + 1);
            }
        }
        return null;
    }

    private static List<string> ExtractJavaServiceCalls(string body)
    {
        var m = Regex.Matches(body, @"(?:\w+[Ss]ervice)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(8).ToList();
    }

    private static List<string> ExtractJavaRepoCalls(string body)
    {
        var m = Regex.Matches(body,
            @"(?:\w+[Rr]epository|\w+[Dd]ao|\w+[Mm]apper|jdbcTemplate)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(8).ToList();
    }

    private static List<string> ExtractJavaCalledMethods(string body)
    {
        var m = Regex.Matches(body, @"(\w+)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[2].Value).Distinct().Take(12).ToList();
    }

    private static List<SqlQuery> ExtractJavaQueryAnnotation(string[] lines, int funcLine, string filePath)
    {
        var result = new List<SqlQuery>();
        for (int i = Math.Max(0, funcLine - 5); i < funcLine; i++)
        {
            var m = Regex.Match(lines[i], @"@(?:Query|Select|Insert|Update|Delete)\s*\(\s*""([^""]+)""");
            if (m.Success)
                result.Add(new SqlQuery
                {
                    Name = $"annotation_{i}",
                    FunctionName = $"annotation_{i}",
                    Operation = NormalizeSqlOperation("", m.Groups[1].Value),
                    RawSql = m.Groups[1].Value,
                    ComposedSql = m.Groups[1].Value,
                    SourceFile = filePath
                });
        }
        return result;
    }

    private static bool IsJavaServiceFile(string path) =>
        path.Contains("/service/") || path.Contains("/services/") ||
        path.EndsWith("Service.java") || path.EndsWith("ServiceImpl.java") ||
        path.EndsWith("Service.kt") || path.EndsWith("ServiceImpl.kt");

    private static bool IsJavaRepoFile(string path) =>
        path.Contains("/repository/") || path.Contains("/repositories/") ||
        path.Contains("/dao/") || path.Contains("/mapper/") ||
        path.EndsWith("Repository.java") || path.EndsWith("Dao.java") || path.EndsWith("Mapper.java");

    // ═══ TypeScript / Node.js Analyzer ═══════════════════════════════════════

    /// <summary>
    /// Traces a TypeScript/JavaScript API endpoint through Handler → Service layers.
    /// Identifies the handler by matching NestJS decorators (<c>@Get</c>, <c>@Post</c>, …)
    /// or Express router calls (<c>router.get/post(…)</c>), then follows
    /// <c>this.xyzService.method()</c> calls into service files.
    /// Also extracts raw SQL from template-literal query calls.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>An <see cref="ApiTrace"/> with resolved steps and extracted SQL, or an empty trace.</returns>
    private ApiTrace AnalyzeTypeScriptEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };
        var tsFiles = _allFiles.Where(f => !f.IsDirectory &&
            (f.Extension == ".ts" || f.Extension == ".js") && !IsTestFile(f)).ToList();

        (FileNode File, string FuncName, int Line)? handlerResult = null;

        foreach (var file in tsFiles)
        {
            var lines = GetCachedLines(file.AbsolutePath);
            var r = FindTsRouteHandler(lines, ep.Method, ep.Path);
            if (r.HasValue) { handlerResult = (file, r.Value.Name, r.Value.Line); break; }
        }

        if (handlerResult == null)
        {
            var cands = DeriveTypeScriptCandidates(ep.OperationId, ep.Path, ep.Method);
            handlerResult = FindHandlerInFiles(cands,
                f => (f.Extension == ".ts" || f.Extension == ".js") && !IsTestFile(f));
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var handlerLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (handlerBody, hStart, hEnd) = ExtractFunctionBodyWithLines(
            handlerLines, handlerResult.Value.FuncName, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = "Handler",
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = DetectTsFramework(handlerResult.Value.File.AbsolutePath) + " 路由處理函式",
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractTsCalledFunctions(handlerBody)
        });

        foreach (var svcMethod in ExtractTsServiceCalls(handlerBody))
        {
            var svcResult = FindHandlerInFiles([svcMethod],
                f => (f.Extension == ".ts" || f.Extension == ".js") &&
                     !IsTestFile(f) && IsTsServiceFile(f.RelativePath));
            if (svcResult == null) continue;

            var svcLines = GetCachedLines(svcResult.Value.File.AbsolutePath);
            var (svcBody, svcStart, svcEnd) = ExtractFunctionBodyWithLines(
                svcLines, svcMethod, svcResult.Value.Line);

            trace.Steps.Add(new TraceStep
            {
                Order = trace.Steps.Count + 1,
                Layer = "Service",
                File = svcResult.Value.File.RelativePath,
                Function = svcMethod,
                Description = $"業務邏輯層 — {Path.GetFileNameWithoutExtension(svcResult.Value.File.Name)}",
                StartLine = svcStart,
                EndLine = svcEnd
            });

            foreach (var sq in ExtractTsSql(svcBody, svcResult.Value.File.RelativePath))
                trace.SqlQueries.Add(sq);
        }

        return trace;
    }

    private static (string Name, int Line)? FindTsRouteHandler(string[] lines, string httpMethod, string path)
    {
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            bool isNest = Regex.IsMatch(line,
                $@"@{char.ToUpper(httpMethod[0])}{httpMethod[1..].ToLower()}\s*\(", RegexOptions.IgnoreCase);
            bool isExpress = Regex.IsMatch(line,
                $@"(?:router|app)\.{httpMethod.ToLower()}\s*\(", RegexOptions.IgnoreCase);
            if (!isNest && !isExpress) continue;

            var attrPath = Regex.Match(line, @"\(['""](.*?)['""]").Groups[1].Value;
            if (!string.IsNullOrEmpty(attrPath) && !PathsMatch(path, attrPath)) continue;

            for (int j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
            {
                var m = Regex.Match(lines[j],
                    @"(?:async\s+)?(?:function\s+)?(\w+)\s*\(|(?:public|private)?\s*(?:async\s+)?(\w+)\s*\(");
                var name = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                if (m.Success && name.Length > 0 && !IsKeyword(name))
                    return (name, j + 1);
            }
        }
        return null;
    }

    private static List<string> DeriveTypeScriptCandidates(string operationId, string path, string method)
    {
        var cands = new List<string>();
        if (!string.IsNullOrEmpty(operationId)) cands.Add(operationId);
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !RxVersionSeg.IsMatch(s) && !s.StartsWith('{'))
            .Select(s => string.Concat(s.Split('_', '-').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : "")))
            .ToArray();
        if (segs.Length > 0)
        {
            var p = char.ToLower(method[0]) + method[1..].ToLower();
            cands.Add(p + string.Concat(segs));
            cands.Add(string.Concat(segs));
        }
        return cands.Distinct().ToList();
    }

    // Instance method — uses GetCachedContent so the file is never re-read from disk
    // when it was already loaded for handler/service layer tracing.
    private string DetectTsFramework(string filePath)
    {
        var content = GetCachedContent(filePath);
        if (content.Contains("@nestjs")) return "NestJS";
        if (content.Contains("fastify")) return "Fastify";
        if (content.Contains("express")) return "Express";
        return "Node.js";
    }

    private static List<string> ExtractTsServiceCalls(string body)
    {
        var m = Regex.Matches(body, @"(?:this\.\w*[Ss]ervice\w*|\w+[Ss]ervice)\s*\.\s*(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(8).ToList();
    }

    private static List<string> ExtractTsCalledFunctions(string body)
    {
        var m = Regex.Matches(body, @"(?:await\s+)?(?:\w+)\.(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(12).ToList();
    }

    private static List<SqlQuery> ExtractTsSql(string body, string filePath)
    {
        var result = new List<SqlQuery>();
        var m = Regex.Matches(body,
            @"(?:query|execute|find)\s*\(\s*[`""'](SELECT|INSERT|UPDATE|DELETE)[\s\S]*?[`""']",
            RegexOptions.IgnoreCase);
        foreach (Match match in m)
        {
            var sql = Regex.Match(match.Value,
                @"(SELECT|INSERT|UPDATE|DELETE)[\s\S]+", RegexOptions.IgnoreCase).Value.Trim('`', '"', '\'').Trim();
            if (sql.Length < 10) continue;
            result.Add(new SqlQuery
            {
                Name = $"ts_sql_{result.Count + 1}",
                FunctionName = $"ts_sql_{result.Count + 1}",
                Operation = NormalizeSqlOperation("", sql),
                RawSql = sql,
                ComposedSql = sql,
                SourceFile = filePath
            });
        }
        return result.Take(5).ToList();
    }

    private static bool IsTsServiceFile(string path) =>
        path.Contains("/services/") || path.Contains("/service/") ||
        path.EndsWith(".service.ts") || path.EndsWith(".service.js");

    // ═══ PHP / Laravel Analyzer ═══════════════════════════════════════════════

    /// <summary>
    /// Traces a PHP/Laravel endpoint by first looking up the route definition in
    /// <c>routes/api.php</c> to find the <c>[ControllerClass::class, 'method']</c> tuple,
    /// then resolving the controller file and extracting its handler body and SQL calls.
    /// Falls back to name-derived candidates when the route file lookup fails.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>An <see cref="ApiTrace"/> with resolved steps and extracted SQL, or an empty trace.</returns>
    private ApiTrace AnalyzePhpEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };
        var phpFiles = _allFiles.Where(f => !f.IsDirectory && f.Extension == ".php" && !IsTestFile(f)).ToList();

        (FileNode File, string FuncName, int Line)? handlerResult = null;

        // Find route in routes/api.php
        foreach (var file in phpFiles.Where(f =>
            f.RelativePath.Contains("route", StringComparison.OrdinalIgnoreCase) ||
            f.Name == "api.php"))
        {
            var lines = GetCachedLines(file.AbsolutePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!Regex.IsMatch(line, $@"Route::{ep.Method.ToLower()}\s*\(", RegexOptions.IgnoreCase)) continue;
                var pathM = Regex.Match(line, @"'([^']+)'|""([^""]+)""");
                var routePath = pathM.Groups[1].Value.Length > 0 ? pathM.Groups[1].Value : pathM.Groups[2].Value;
                if (!string.IsNullOrEmpty(routePath) && !PathsMatch(ep.Path, routePath)) continue;

                var controllerM = Regex.Match(line, @"\[(\w+)::class,\s*'(\w+)'\]");
                if (controllerM.Success)
                {
                    var controllerFile = phpFiles.FirstOrDefault(f => f.Name == $"{controllerM.Groups[1].Value}.php");
                    if (controllerFile != null)
                        handlerResult = (controllerFile, controllerM.Groups[2].Value,
                            FindFunctionLineInFile(controllerFile, controllerM.Groups[2].Value));
                }
                break;
            }
            if (handlerResult != null) break;
        }

        if (handlerResult == null)
        {
            var cands = DeriveCSharpMethodCandidates(ep.OperationId, ep.Path, ep.Method);
            handlerResult = FindHandlerInFiles(cands, f => f.Extension == ".php" && !IsTestFile(f));
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var fileLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (handlerBody, hStart, hEnd) = ExtractFunctionBodyWithLines(
            fileLines, handlerResult.Value.FuncName, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = "Controller",
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = $"Laravel Controller — {Path.GetFileNameWithoutExtension(handlerResult.Value.File.Name)}",
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractPhpCalledMethods(handlerBody)
        });

        foreach (var sq in ExtractPhpSql(handlerBody, handlerResult.Value.File.RelativePath))
            trace.SqlQueries.Add(sq);

        return trace;
    }

    private static List<string> ExtractPhpCalledMethods(string body)
    {
        var m = Regex.Matches(body, @"(?:\$this->\w+|[A-Z]\w+)::(\w+)\s*\(");
        return m.Cast<Match>().Select(x => x.Groups[1].Value).Distinct().Take(12).ToList();
    }

    private static List<SqlQuery> ExtractPhpSql(string body, string filePath)
    {
        var result = new List<SqlQuery>();
        var m = Regex.Matches(body,
            @"DB::(?:select|statement|insert|update|delete)\s*\(\s*'([\s\S]*?)'",
            RegexOptions.IgnoreCase);
        foreach (Match match in m)
        {
            result.Add(new SqlQuery
            {
                Name = $"php_sql_{result.Count + 1}",
                FunctionName = $"php_sql_{result.Count + 1}",
                Operation = NormalizeSqlOperation("", match.Groups[1].Value),
                RawSql = match.Groups[1].Value,
                ComposedSql = match.Groups[1].Value,
                SourceFile = filePath
            });
        }
        return result.Take(5).ToList();
    }

    // ═══ Ruby / Rails Analyzer ════════════════════════════════════════════════

    /// <summary>
    /// Traces a Ruby/Rails endpoint by first parsing <c>config/routes.rb</c> for the
    /// <c>to: 'controller#action'</c> mapping, resolving the controller file, and
    /// extracting its action method body.  Falls back to last-segment path candidates
    /// when the routes file lookup fails.
    /// </summary>
    /// <param name="ep">The endpoint to trace.</param>
    /// <returns>An <see cref="ApiTrace"/> with resolved steps and extracted SQL, or an empty trace.</returns>
    private ApiTrace AnalyzeRubyEndpoint(ApiEndpoint ep)
    {
        var trace = new ApiTrace { Method = ep.Method, Path = ep.Path };
        var rbFiles = _allFiles.Where(f => !f.IsDirectory && f.Extension == ".rb" && !IsTestFile(f)).ToList();

        (FileNode File, string FuncName, int Line)? handlerResult = null;

        var routesFile = rbFiles.FirstOrDefault(f => f.Name == "routes.rb");
        if (routesFile != null)
        {
            var lines = GetCachedLines(routesFile.AbsolutePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!Regex.IsMatch(line, $@"^{ep.Method.ToLower()}\s+", RegexOptions.IgnoreCase)) continue;

                var pathM = Regex.Match(line, @"'([^']+)'|""([^""]+)""");
                var routePath = pathM.Groups[1].Value.Length > 0 ? pathM.Groups[1].Value : pathM.Groups[2].Value;
                if (!PathsMatch(ep.Path, routePath)) continue;

                var toM = Regex.Match(line, @"to:\s*'([^#']+)#(\w+)'");
                if (toM.Success)
                {
                    var controllerFile = rbFiles.FirstOrDefault(f =>
                        f.Name.Contains(toM.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    if (controllerFile != null)
                        handlerResult = (controllerFile, toM.Groups[2].Value,
                            FindFunctionLineInFile(controllerFile, toM.Groups[2].Value));
                }
                break;
            }
        }

        if (handlerResult == null)
        {
            var segs = ep.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !RxVersionSeg.IsMatch(s) && !s.StartsWith(':')).ToArray();
            var cands = segs.Length > 0
                ? new List<string> { segs.Last(), $"{ep.Method.ToLower()}_{segs.Last()}" }
                : [ep.Method.ToLower()];
            handlerResult = FindHandlerInFiles(cands, f => f.Extension == ".rb" && !IsTestFile(f));
        }

        if (handlerResult == null) return trace;

        trace.HandlerFile = handlerResult.Value.File.RelativePath;
        trace.HandlerFunction = handlerResult.Value.FuncName;

        var fileLines = GetCachedLines(handlerResult.Value.File.AbsolutePath);
        var (body, hStart, hEnd) = ExtractRubyFunctionBody(fileLines, handlerResult.Value.Line);

        trace.Steps.Add(new TraceStep
        {
            Order = 1,
            Layer = "Controller",
            File = handlerResult.Value.File.RelativePath,
            Function = handlerResult.Value.FuncName,
            Description = $"Rails Controller — {Path.GetFileNameWithoutExtension(handlerResult.Value.File.Name)}",
            StartLine = hStart,
            EndLine = hEnd,
            CalledFunctions = ExtractRubyCalledMethods(body)
        });

        foreach (var sq in ExtractRubySql(body, handlerResult.Value.File.RelativePath))
            trace.SqlQueries.Add(sq);

        return trace;
    }

    private static (string body, int start, int end) ExtractRubyFunctionBody(string[] lines, int funcLine)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        int end = funcLine;
        for (int i = funcLine - 1; i < lines.Length; i++)
        {
            var l = lines[i];
            sb.AppendLine(l);
            if (Regex.IsMatch(l, @"\b(def|do|if|unless|begin|class|module|case)\b")) depth++;
            if (Regex.IsMatch(l, @"^\s*end\b")) depth--;
            if (depth <= 0 && i >= funcLine) { end = i + 1; break; }
        }
        return (sb.ToString(), funcLine, end);
    }

    private static List<string> ExtractRubyCalledMethods(string body)
    {
        var m = Regex.Matches(body, @"(\w+)\s*\.");
        return m.Cast<Match>().Select(x => x.Groups[1].Value)
            .Where(n => !IsKeyword(n) && n.Length > 2).Distinct().Take(12).ToList();
    }

    private static List<SqlQuery> ExtractRubySql(string body, string filePath)
    {
        var result = new List<SqlQuery>();
        var m = Regex.Matches(body,
            @"(?:find_by_sql|execute|exec_query)\s*\([""']([\s\S]*?)[""']",
            RegexOptions.IgnoreCase);
        foreach (Match match in m)
        {
            result.Add(new SqlQuery
            {
                Name = $"ruby_sql_{result.Count + 1}",
                FunctionName = $"ruby_sql_{result.Count + 1}",
                Operation = NormalizeSqlOperation("", match.Groups[1].Value),
                RawSql = match.Groups[1].Value,
                ComposedSql = match.Groups[1].Value,
                SourceFile = filePath
            });
        }
        return result.Take(5).ToList();
    }

    // ═══ Shared Utilities ════════════════════════════════════════════════════

    /// <summary>
    /// Looks up each candidate function name in <see cref="_funcIndex"/> (O(1) per lookup)
    /// and returns the first file and line number that satisfies the
    /// <paramref name="fileFilter"/> predicate.
    /// </summary>
    /// <param name="candidates">
    /// Ordered list of function name candidates to try.  Earlier candidates are preferred;
    /// the list is typically derived from <c>operationId</c> variants and path segments.
    /// </param>
    /// <param name="fileFilter">
    /// Predicate restricting which files are eligible (e.g. only Go source files,
    /// only service-layer files).
    /// </param>
    /// <returns>
    /// A tuple of (file node, matched function name, 1-based line number),
    /// or <c>null</c> if no candidate resolves to a file that satisfies the filter.
    /// </returns>
    private (FileNode File, string FuncName, int Line)? FindHandlerInFiles(
        List<string> candidates, Func<FileNode, bool> fileFilter)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length <= 1) continue;
            if (!_funcIndex.TryGetValue(candidate, out var locations)) continue;
            var match = locations.FirstOrDefault(l => fileFilter(l.File));
            if (match.File != null) return (match.File, candidate, match.Line);
        }
        return null;
    }

    /// <summary>
    /// Extracts the textual body of a named function starting near
    /// <paramref name="funcLine"/> by counting matching braces (for C-style languages)
    /// or by tracking indentation depth (for Python).
    /// </summary>
    /// <param name="lines">All lines of the source file as a string array.</param>
    /// <param name="funcName">The function name used to scan for the exact signature line.</param>
    /// <param name="funcLine">
    /// 1-based hint line number from the function index.  The method scans up to
    /// 6 lines around this position to locate the exact signature, accommodating
    /// annotations and multi-line signatures.
    /// </param>
    /// <returns>
    /// A tuple of (body text, 1-based start line, 1-based end line).
    /// Returns an empty body tuple when <paramref name="funcLine"/> is out of range.
    /// </returns>
    private static (string body, int startLine, int endLine) ExtractFunctionBodyWithLines(
        string[] lines, string funcName, int funcLine)
    {
        // Scan near funcLine for the actual function signature.
        // string.Contains pre-filter avoids constructing and executing the dynamic Regex
        // on lines that clearly cannot match (hot path: 600+ calls per analysis run).
        int funcStart = funcLine - 1;
        int scanFrom = Math.Max(0, funcLine - 2);
        for (int i = scanFrom; i < Math.Min(scanFrom + 6, lines.Length); i++)
        {
            if (!lines[i].Contains(funcName)) continue;
            if (Regex.IsMatch(lines[i],
                $@"(?:func|def|function|fun)\s+(?:\([^)]*\)\s+)?{Regex.Escape(funcName)}\s*[\(<]|" +
                $@"(?:public|private|protected|internal).*\b{Regex.Escape(funcName)}\s*\(|" +
                $@"\b{Regex.Escape(funcName)}\s*(?:=\s*(?:async\s*)?\(|\([^)]*\)\s*\{{)"))
            {
                funcStart = i;
                break;
            }
        }

        // Find opening { or : (Python).
        // Use a window of 15 lines to handle functions with long parameter lists
        // (e.g. 5+ named parameters, each on its own line) without missing the opening brace.
        int braceStart = funcStart;
        for (int i = funcStart; i < Math.Min(funcStart + 15, lines.Length); i++)
        {
            if (lines[i].Contains('{') || lines[i].TrimEnd().EndsWith(':'))
            { braceStart = i; break; }
        }

        // Walk forward tracking brace depth
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        int endLine = braceStart + 1;
        for (int i = braceStart; i < lines.Length; i++)
        {
            sb.AppendLine(lines[i]);
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            endLine = i + 1;
            if (depth <= 0 && i > braceStart) break;
        }

        return (sb.ToString(), funcStart + 1, endLine);
    }

    // Instance method — uses GetCachedLines so the file is not re-read from disk
    // when already loaded during handler tracing for PHP/Ruby routes.
    private int FindFunctionLineInFile(FileNode file, string funcName)
    {
        var lines = GetCachedLines(file.AbsolutePath);
        for (int i = 0; i < lines.Length; i++)
            if (Regex.IsMatch(lines[i], $@"\b{Regex.Escape(funcName)}\b"))
                return i + 1;
        return 1;
    }

    private static bool IsTestFile(FileNode f)
    {
        var name = f.Name.ToLowerInvariant();
        var path = f.RelativePath.ToLowerInvariant();
        return name.EndsWith("_test.go") || name.EndsWith(".test.ts") || name.EndsWith(".spec.ts") ||
               name.EndsWith("_test.py") || name.StartsWith("test_") || name.Contains(".test.") ||
               path.Contains("/test/") || path.Contains("/tests/") || path.Contains("/spec/") || path.Contains("/__tests__/");
    }

    /// <summary>
    /// Returns true for auto-generated source files that should NOT be treated as
    /// handler/service/repository entry points (they contain no real business logic).
    /// Examples: *_grpc.pb.go, *.pb.go, *.pb.gw.go, *_gen.go, *.gen.go
    /// Also detects files that begin with the canonical Go "Code generated" comment.
    /// </summary>
    private static bool IsGeneratedFile(FileNode f)
    {
        var name = f.Name.ToLowerInvariant();

        // Go protobuf / grpc-gateway generated files
        if (name.EndsWith(".pb.go") || name.EndsWith(".pb.gw.go")) return true;

        // Convention: *_gen.go, *.gen.go, generated_*.go
        if (name.EndsWith("_gen.go") || name.Contains(".gen.go") || name.StartsWith("generated_")) return true;

        // C# / Java generated files (designers, scaffolds)
        if (name.EndsWith(".designer.cs") || name.EndsWith(".generated.cs")) return true;
        if (name.EndsWith(".generated.java") || name.EndsWith("generated.ts")) return true;

        // Peek at first few bytes to detect "// Code generated" or "// DO NOT EDIT"
        try
        {
            using var reader = new StreamReader(f.AbsolutePath);
            for (int i = 0; i < 5; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                if (line.Contains("Code generated") || line.Contains("DO NOT EDIT") ||
                    line.Contains("@generated") || line.Contains("auto-generated"))
                    return true;
            }
        }
        catch { }

        return false;
    }

    // Pre-allocated keyword set — avoids re-allocating a new HashSet on every IsKeyword() call.
    private static readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "switch", "return", "new", "class", "interface",
        "var", "let", "const", "void", "null", "true", "false", "async", "await",
        "static", "public", "private", "protected", "override", "abstract",
        "string", "int", "long", "bool", "float", "double", "object", "type",
        "func", "def", "end", "do", "then", "else", "elif", "try", "catch", "finally"
    };

    private static bool IsKeyword(string name) => _keywords.Contains(name);
}
