// ============================================================
// CopilotSemanticAnalyzer.cs — AI-powered and local semantic analysis
// ============================================================
//
// What this class does:
//   Provides three kinds of semantic insight about a scanned repository:
//     1. A human-readable project summary (GenerateProjectSummaryAsync)
//     2. A list of detected architectural design patterns (DetectDesignPatternsAsync)
//     3. A prioritised security risk report (DetectSecurityRisksAsync)
//
// AI vs local fallback:
//   When a valid GITHUB_COPILOT_TOKEN is available, each analysis task sends
//   a structured prompt to api.githubcopilot.com/chat/completions and parses
//   the JSON response.  When no token is present (IsAvailable == false), every
//   method transparently falls back to a fast, fully-offline implementation:
//     - GenerateLocalSummary   → template string built from DashboardData
//     - DetectPatternsLocally  → heuristic package-name matching
//     - DetectSecurityRisksLocally → seven static regex patterns (OWASP Top 10)
//
// Security model:
//   • Consent warning: the first call that would upload code to Copilot prints a
//     5-second countdown to STDOUT so users can abort with Ctrl+C (CWE-359).
//   • No GITHUB_TOKEN: only the Copilot-specific token is accepted.  Using the
//     broad GITHUB_TOKEN (which carries repo write access) as a Copilot credential
//     would silently send source code with an overprivileged token (CWE-522).
//   • Model allowlist: only KnownModels values reach the API, preventing an
//     injected COPILOT_MODEL value from triggering unexpected API behaviour (CWE-20).
// ============================================================

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class CopilotSemanticAnalyzer
{
    // Shared HttpClient — creating one per request exhausts socket connections.
    // CLI tools are single-process and short-lived, so a static instance is safe.
    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly string _model;

    /// <summary>The Copilot chat-completions endpoint (same for all supported models).</summary>
    private const string CopilotEndpoint = "https://api.githubcopilot.com/chat/completions";

    /// <summary>
    /// Fallback model name used when <c>COPILOT_MODEL</c> environment variable is absent or whitespace.
    /// Defined as a constant so every reference stays in sync.
    /// </summary>
    private const string DefaultModel = "gpt-4o";

    // int (not bool) is required because Interlocked.Exchange only operates on int/long/object.
    // Using bool would force a lock, negating the point of an atomic flag entirely.
    // 0 = warning not yet shown; 1 = warning shown.
    private static int _uploadWarningShown;

    // ── Static readonly regexes — compiled ONCE per process lifetime ──────────
    // RegexOptions.Compiled emits CIL bytecode; paying that cost inside a hot loop
    // (once per source file × up to 200 files) is wasteful. Hoisting to static fields
    // amortises the compilation cost to a one-time startup expense. (P1 perf fix)

    /// <summary>Detects hardcoded passwords, API keys, and other secrets in source code.</summary>
    private static readonly Regex SecretPattern = new(
        @"(?i)(password|passwd|secret|api_key|apikey|private_key|access_token|auth_token|client_secret)\s*[=:]\s*[""'][^""'\s]{6,}[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Detects SQL queries built by string concatenation or format calls.</summary>
    private static readonly Regex SqlConcatPattern = new(
        @"(?i)(select|insert|update|delete|where|from)\s+.{0,60}(\+|string\.Format|String\.format|sprintf|fmt\.Sprintf)\s*",
        RegexOptions.Compiled);

    /// <summary>Detects disabled TLS/SSL certificate validation.</summary>
    private static readonly Regex InsecureTlsPattern = new(
        @"(?i)(ServerCertificateValidationCallback\s*=.*true|InsecureSkipVerify\s*:\s*true|verify\s*=\s*false|ssl_verify\s*=\s*false|CURLOPT_SSL_VERIFYPEER)",
        RegexOptions.Compiled);

    /// <summary>Detects use of deprecated weak cryptographic algorithms (MD5, SHA-1, DES, …).</summary>
    private static readonly Regex WeakCryptoPattern = new(
        @"(?i)(MD5\.Create|SHA1\.Create|new\s+MD5|new\s+SHA1|hashlib\.md5|hashlib\.sha1|DES\.|TripleDES\.|RC2\.|['""]md5['""]|['""]sha1['""])",
        RegexOptions.Compiled);

    /// <summary>Detects debug/actuator endpoints or developer-exception-page middleware.</summary>
    private static readonly Regex DebugEndpointPattern = new(
        @"(?i)(\/debug|\/actuator|app\.UseDeveloperExceptionPage|DEBUG\s*=\s*true|\.UseSwagger\(\))",
        RegexOptions.Compiled);

    /// <summary>Detects calls to shell/OS command execution functions.</summary>
    private static readonly Regex CommandInjectionPattern = new(
        @"(?i)(Process\.Start|os\.system\(|exec\(|shell_exec\(|popen\(|Runtime\.getRuntime\(\)\.exec|os\.exec\.Command)",
        RegexOptions.Compiled);

    /// <summary>Detects unsafe deserialisation patterns (TypeNameHandling, BinaryFormatter, pickle …).</summary>
    private static readonly Regex InsecureDeserPattern = new(
        @"(?i)(JsonConvert\.DeserializeObject.*TypeNameHandling|BinaryFormatter|ObjectInputStream|pickle\.loads|yaml\.load\((?!.*Loader))",
        RegexOptions.Compiled);

    public bool IsAvailable => !string.IsNullOrEmpty(_token);

    /// <summary>
    /// Initialises the analyser.
    /// </summary>
    /// <param name="copilotToken">
    /// Explicit token supplied via <c>--copilot-token</c> CLI flag.
    /// An empty string is treated the same as <c>null</c> so the env-var fallback fires.
    /// When absent, falls back to the <c>GITHUB_COPILOT_TOKEN</c> environment variable
    /// (which may have been loaded from a <c>.env</c> file by <see cref="Services.DotEnvLoader"/>).
    /// </param>
    public CopilotSemanticAnalyzer(string? copilotToken = null)
    {
        // Only accept the Copilot-specific token, never the broad GITHUB_TOKEN (CWE-522).
        // GITHUB_TOKEN in CI carries repo write access; using it as a Copilot credential
        // would silently send source code to an external API with an overprivileged token.
        //
        // IsNullOrEmpty (not ??) so that an empty-string CLI arg ("--copilot-token """)
        // falls through to the env-var lookup rather than storing "" and disabling AI.
        _token = string.IsNullOrEmpty(copilotToken)
            ? Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN")
            : copilotToken;

        // Read the model name from the environment so users can override via .env or OS export.
        // Example: COPILOT_MODEL=gpt-4o-mini rid analyze ./myrepo
        // IsNullOrWhiteSpace guard prevents a whitespace-only value from reaching the API
        // and causing a silent HTTP 400/422 that is swallowed by CallCopilotAsync.
        var rawModel = Environment.GetEnvironmentVariable("COPILOT_MODEL");
        _model = string.IsNullOrWhiteSpace(rawModel) ? DefaultModel : rawModel.Trim();

        _http = _sharedHttp;
    }

    public async Task<string?> GenerateProjectSummaryAsync(DashboardData data, CancellationToken ct = default)
    {
        if (!IsAvailable) return GenerateLocalSummary(data);

        var prompt = $"""
            分析以下程式碼庫，用 200 字以內的繁體中文生成專案摘要：
            專案名稱：{data.Project.Name}
            分支：{data.Project.Branch}
            主要語言：{string.Join("、", data.Project.Languages.Take(5).Select(l => $"{l.Name}({l.Percentage}%)"))}
            套件數量：{data.Packages.Count}
            API 端點：{data.ApiEndpoints.Count}
            容器服務：{data.Containers.Count}
            檔案數：{data.Project.TotalFiles}
            {(data.Project.CopilotInstructions != null ? $"Copilot 指示：{data.Project.CopilotInstructions[..Math.Min(200, data.Project.CopilotInstructions.Length)]}" : "")}
            """;
        return await CallCopilotAsync(prompt, 400, ct);
    }

    public async Task<List<string>> DetectDesignPatternsAsync(DashboardData data, CancellationToken ct = default)
    {
        if (!IsAvailable) return DetectPatternsLocally(data);

        var prompt = $"""
            分析以下程式碼庫資訊，識別使用的架構設計模式（用繁體中文）。
            語言：{string.Join(", ", data.Project.Languages.Take(5).Select(l => l.Name))}
            套件：{string.Join(", ", data.Packages.Take(20).Select(p => p.Name))}
            API 端點數：{data.ApiEndpoints.Count}
            容器服務數：{data.Containers.Count}

            請回傳 JSON 陣列，例如：["微服務架構", "RESTful API", "容器化部署", "CQRS"]
            """;

        var response = await CallCopilotAsync(prompt, 200, ct);
        if (response == null) return [];

        try
        {
            var start = response.IndexOf('[');
            var end = response.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                var json = response[start..(end + 1)];
                return JsonConvert.DeserializeObject<List<string>>(json) ?? [];
            }
        }
        catch { }
        return [];
    }

    /// <summary>
    /// Detects security risks in the repository using a two-stage pipeline:
    /// local static analysis (always) followed by optional AI deep scan (when token is available).
    /// </summary>
    /// <param name="data">
    /// The fully-populated <see cref="DashboardData"/> produced by the analysis orchestrator.
    /// Used by both the local and AI stages.
    /// </param>
    /// <param name="allFiles">
    /// Flat list of all scanned <see cref="FileNode"/> objects.  When non-null, enables
    /// source-code static scanning (regex patterns) and AI code review.
    /// </param>
    /// <param name="repoPath">
    /// Absolute path to the repository root.  Reserved for future path-relative reporting;
    /// currently unused but kept in the signature for forward compatibility.
    /// </param>
    /// <param name="ct">Cancellation token — propagated to all async Copilot API calls.</param>
    /// <returns>
    /// De-duplicated, priority-sorted list of <see cref="SecurityRisk"/> objects.
    /// Priority 1 = Critical (fix immediately), 4 = Info/Low (advisory).
    /// </returns>
    /// <remarks>
    /// De-duplication uses a <see cref="HashSet{T}"/> of <c>(Title, FilePath)</c> tuples,
    /// turning the naive O(n²) "does this already exist?" check into O(n) (one HashSet.Add per item).
    /// </remarks>
    public async Task<List<SecurityRisk>> DetectSecurityRisksAsync(
        DashboardData data,
        List<FileNode>? allFiles = null,
        string? repoPath = null,
        CancellationToken ct = default)
    {
        // Always run local static analysis first.
        var risks = DetectSecurityRisksLocally(data, allFiles, repoPath);

        // AI deep scan when token is available.
        if (IsAvailable && allFiles != null)
        {
            // Build an O(1) lookup from already-known local risks BEFORE the loop.
            // This turns the deduplication from O(n²) → O(n): no per-element linear scan.
            var existingKeys = risks
                .Select(r => (r.Title, r.FilePath))
                .ToHashSet();

            var aiRisks = await AnalyzeCodeSecurityWithAIAsync(data, allFiles, ct);
            foreach (var r in aiRisks)
            {
                // HashSet.Add returns false on duplicate — single O(1) check per item.
                if (existingKeys.Add((r.Title, r.FilePath)))
                    risks.Add(r);
            }
        }

        return risks.OrderBy(r => r.Priority).ThenBy(r => r.Title).ToList();
    }

    public async Task<string> TranslateApiDescriptionAsync(string description, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(description)) return description;
        if (!IsAvailable) return description;

        var prompt = $"將以下 API 說明翻譯為繁體中文，保持技術術語，直接回傳翻譯結果：\n{description}";
        return await CallCopilotAsync(prompt, 150, ct) ?? description;
    }

    // ─── AI Security Analysis ──────────────────────────────────────────────

    /// <summary>
    /// Sends representative source-code excerpts to the Copilot API and requests an
    /// OWASP Top 10–aligned security review, returning the parsed risk list.
    /// </summary>
    /// <param name="data">Project metadata used to build the prompt context.</param>
    /// <param name="allFiles">Full file list used to select which files to excerpt.</param>
    /// <param name="ct">Cancellation token propagated to the HTTP call.</param>
    /// <returns>
    /// List of <see cref="SecurityRisk"/> objects parsed from the AI JSON response,
    /// or an empty list if the API call fails or returns unparseable content.
    /// </returns>
    /// <remarks>
    /// File selection strategy: controllers, auth files, and middleware are ranked highest
    /// because they are the most likely attack surface.  At most 8 files are sent and each
    /// is truncated to 2 500 characters to keep the prompt within the model's context window.
    /// </remarks>
    private async Task<List<SecurityRisk>> AnalyzeCodeSecurityWithAIAsync(
        DashboardData data, List<FileNode> allFiles, CancellationToken ct)
    {
        // Select key source files for review
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".go", ".java", ".py", ".ts", ".js", ".php", ".rb" };
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "vendor", "node_modules", "bin", "obj", "dist", ".git", "migrations" };

        var keyFiles = allFiles
            .Where(f => !f.IsDirectory
                && extensions.Contains(f.Extension)
                && !skipDirs.Any(d => f.RelativePath.Contains(d, StringComparison.OrdinalIgnoreCase))
                && f.SizeBytes < 100_000)
            .OrderByDescending(f =>
            {
                // Prioritise controllers/handlers/services/repositories
                var p = f.Name.ToLower();
                if (p.Contains("controller") || p.Contains("handler")) return 4;
                if (p.Contains("service") || p.Contains("middleware")) return 3;
                if (p.Contains("repository") || p.Contains("dao")) return 2;
                if (p.Contains("auth") || p.Contains("jwt") || p.Contains("token")) return 5;
                return 1;
            })
            .Take(8)
            .ToList();

        if (keyFiles.Count == 0) return [];

        var codeSnippets = new StringBuilder();
        foreach (var f in keyFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(f.AbsolutePath, ct);
                var excerpt = content.Length > 2500 ? content[..2500] + "\n...(truncated)" : content;
                codeSnippets.AppendLine($"\n### File: {f.RelativePath}");
                codeSnippets.AppendLine(excerpt);
            }
            catch { }
        }

        var jsonTemplate = """
            [
              {
                "level": "critical|high|warning|info",
                "priority": 1,
                "category": "A1-注入攻擊",
                "title": "問題標題（繁體中文）",
                "description": "詳細說明（繁體中文）",
                "recommendation": "建議修復方式（繁體中文）",
                "filePath": "相對檔案路徑或null",
                "affectedFiles": ["file1", "file2"]
              }
            ]
            """;
        var prompt = $"""
            你是一位資深資訊安全專家，請對以下程式碼進行完整的 OWASP Top 10 安全審查，
            以及其他潛在安全風險（硬編碼憑證、SQL 注入、XSS、不安全的 API、弱密碼學等）。

            專案資訊：
            - 名稱：{data.Project.Name}
            - 語言：{string.Join(", ", data.Project.Languages.Take(5).Select(l => l.Name))}
            - 套件：{string.Join(", ", data.Packages.Take(15).Select(p => p.Name))}
            - API 端點數：{data.ApiEndpoints.Count}

            程式碼片段：
            {codeSnippets}

            請以 JSON 陣列格式回傳安全風險列表（只回傳 JSON，不要其他說明），格式如下：
            {jsonTemplate}

            Priority 規則：1=Critical（立即修復），2=High（高優先），3=Medium（中等），4=Info/Low（參考）
            請確保每個 JSON 欄位都是有效的字串，不要有 null 以外的缺失值。
            """;

        var response = await CallCopilotAsync(prompt, 2000, ct);
        if (response == null) return [];

        try
        {
            var start = response.IndexOf('[');
            var end = response.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                var json = response[start..(end + 1)];
                var raw = JsonConvert.DeserializeObject<List<JObject>>(json) ?? [];
                return raw.Select(o => new SecurityRisk
                {
                    Level       = o["level"]?.ToString() ?? "info",
                    Priority    = o["priority"]?.Value<int>() ?? 4,
                    Category    = o["category"]?.ToString() ?? "",
                    Title       = o["title"]?.ToString() ?? "",
                    Description = o["description"]?.ToString() ?? "",
                    Recommendation = o["recommendation"]?.ToString() ?? "",
                    FilePath    = o["filePath"]?.ToString(),
                    AffectedFiles = o["affectedFiles"]?.ToObject<List<string>>() ?? []
                }).Where(r => !string.IsNullOrWhiteSpace(r.Title)).ToList();
            }
        }
        catch { }
        return [];
    }

    // ─── Local Static Analysis ─────────────────────────────────────────────

    /// <summary>
    /// Performs offline static analysis against common OWASP Top 10 vulnerability patterns
    /// using pre-compiled regular expressions.  No network calls are made.
    /// </summary>
    /// <param name="data">
    /// Dashboard data providing env-variable metadata, deprecated API flags,
    /// and container port mappings for infrastructure-level risk checks.
    /// </param>
    /// <param name="allFiles">
    /// When non-null, source files are read from disk and matched against
    /// seven regex patterns covering OWASP A1–A8.  When null, only metadata-derived
    /// checks (env vars, deprecated endpoints, risky ports) are performed.
    /// </param>
    /// <param name="repoPath">Reserved for future use; currently unused.</param>
    /// <returns>
    /// Unordered list of detected <see cref="SecurityRisk"/> objects.
    /// The caller (<see cref="DetectSecurityRisksAsync"/>) is responsible for sorting.
    /// </returns>
    private List<SecurityRisk> DetectSecurityRisksLocally(
        DashboardData data, List<FileNode>? allFiles, string? repoPath)
    {
        var risks = new List<SecurityRisk>();

        // ── Env variable checks ──────────────────────────────────────────
        var sensitiveVars = data.EnvVariables.Where(e => e.IsSensitive).ToList();
        if (sensitiveVars.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "warning", Priority = 3,
                Category = "A3-敏感資料暴露",
                Title = $"發現 {sensitiveVars.Count} 個敏感環境變數",
                Description = "以下變數名稱可能包含機密憑證，請確認其值不包含真實密鑰：" +
                              string.Join(", ", sensitiveVars.Take(8).Select(v => v.Key)),
                Recommendation = "使用 Secret Manager（如 Vault、AWS Secrets Manager）管理密鑰，.env 檔加入 .gitignore，生產環境禁止明文儲存密鑰。",
                FilePath = ".env"
            });

        // ── Exposed secrets (env var with real values) ───────────────────
        var exposedSecrets = data.EnvVariables
            .Where(e => e.IsSensitive && !string.IsNullOrEmpty(e.Value) && e.Value != "***"
                        && e.Value.Length > 5)
            .Take(5).Select(e => e.Key).ToList();
        if (exposedSecrets.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "critical", Priority = 1,
                Category = "A3-敏感資料暴露",
                Title = "密鑰可能以明文儲存",
                Description = $"以下敏感變數可能包含明文密鑰：{string.Join(", ", exposedSecrets)}",
                Recommendation = "立即輪換這些密鑰並從版本庫歷史中清除（git filter-branch 或 BFG）。",
                FilePath = ".env"
            });

        // ── Deprecated APIs ──────────────────────────────────────────────
        var deprecatedApis = data.ApiEndpoints.Where(e => e.IsDeprecated).ToList();
        if (deprecatedApis.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "info", Priority = 4,
                Category = "A9-使用已知漏洞的組件",
                Title = $"{deprecatedApis.Count} 個已棄用 API 端點",
                Description = "棄用端點可能缺乏安全維護：" +
                              string.Join(", ", deprecatedApis.Take(3).Select(a => $"{a.Method} {a.Path}")),
                Recommendation = "移除或重導向棄用端點，確保其有適當的認證授權保護。"
            });

        if (allFiles == null) return risks;

        // ── Source code static scan ──────────────────────────────────────
        var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".go", ".java", ".py", ".ts", ".js", ".php", ".rb" };
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "vendor", "node_modules", "bin", "obj", "dist", ".git", "migrations" };

        var sourceFiles = allFiles
            .Where(f => !f.IsDirectory
                && sourceExtensions.Contains(f.Extension)
                && !skipDirs.Any(d => f.RelativePath.Contains(d, StringComparison.OrdinalIgnoreCase))
                && f.SizeBytes < 500_000)
            .ToList();

        var hardcodedSecretFiles  = new List<string>();
        var sqlInjectionFiles     = new List<string>();
        var insecureTlsFiles      = new List<string>();
        var weakCryptoFiles       = new List<string>();
        var debugEndpointFiles    = new List<string>();
        var commandInjectionFiles = new List<string>();
        var insecureDeserFiles    = new List<string>();

        // Use the class-level static readonly regex fields (defined at the top of this class).
        // They are compiled ONCE per process lifetime; instantiating inside the loop
        // would pay the expensive Regex compilation cost once per call (P1 perf fix).
        foreach (var file in sourceFiles.Take(200))
        {
            try
            {
                var content = File.ReadAllText(file.AbsolutePath);
                if (SecretPattern.IsMatch(content))          hardcodedSecretFiles.Add(file.RelativePath);
                if (SqlConcatPattern.IsMatch(content))       sqlInjectionFiles.Add(file.RelativePath);
                if (InsecureTlsPattern.IsMatch(content))     insecureTlsFiles.Add(file.RelativePath);
                if (WeakCryptoPattern.IsMatch(content))      weakCryptoFiles.Add(file.RelativePath);
                if (DebugEndpointPattern.IsMatch(content))   debugEndpointFiles.Add(file.RelativePath);
                if (CommandInjectionPattern.IsMatch(content)) commandInjectionFiles.Add(file.RelativePath);
                if (InsecureDeserPattern.IsMatch(content))   insecureDeserFiles.Add(file.RelativePath);
            }
            catch (OperationCanceledException) { throw; } // Always propagate cancellation.
            catch (Exception ex)
            {
                // Non-critical: a file that can't be read is skipped. Log for debuggability.
                Debug.WriteLine(
                    $"[CopilotSemanticAnalyzer] Static scan skipped '{file.RelativePath}': " +
                    $"{ex.GetType().Name} — {ex.Message}");
            }
        }

        if (hardcodedSecretFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "critical", Priority = 1,
                Category = "A3-敏感資料暴露",
                Title = "程式碼中疑似存在硬編碼憑證",
                Description = $"在 {hardcodedSecretFiles.Count} 個檔案中偵測到可能的硬編碼密碼/金鑰模式。",
                Recommendation = "將所有憑證移至環境變數或 Secret Manager，使用掃描工具（如 git-secrets, TruffleHog）防止提交。",
                AffectedFiles = hardcodedSecretFiles.Take(5).ToList()
            });

        if (sqlInjectionFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "high", Priority = 2,
                Category = "A1-注入攻擊",
                Title = "疑似 SQL 字串拼接（潛在 SQL 注入）",
                Description = $"在 {sqlInjectionFiles.Count} 個檔案中偵測到 SQL 查詢可能使用字串拼接或 Format。",
                Recommendation = "改用參數化查詢（Parameterized Query）或 ORM，嚴禁將用戶輸入直接拼入 SQL。",
                AffectedFiles = sqlInjectionFiles.Take(5).ToList()
            });

        if (insecureTlsFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "high", Priority = 2,
                Category = "A7-身份驗證與存取控制失效",
                Title = "TLS/SSL 憑證驗證已停用",
                Description = $"在 {insecureTlsFiles.Count} 個檔案中偵測到跳過 TLS 憑證驗證的設定，可能允許中間人攻擊。",
                Recommendation = "移除所有停用 TLS 驗證的程式碼，確保生產環境使用有效憑證。",
                AffectedFiles = insecureTlsFiles.Take(5).ToList()
            });

        if (weakCryptoFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "warning", Priority = 3,
                Category = "A2-加密失敗",
                Title = "使用已過時的弱密碼學演算法",
                Description = $"在 {weakCryptoFiles.Count} 個檔案中偵測到 MD5/SHA1/DES 等已知弱演算法。",
                Recommendation = "改用 SHA-256/SHA-3、AES-256-GCM 或 bcrypt/Argon2（用於密碼雜湊）。",
                AffectedFiles = weakCryptoFiles.Take(5).ToList()
            });

        if (debugEndpointFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "warning", Priority = 3,
                Category = "A5-安全設定錯誤",
                Title = "偵測到除錯端點或開發者模式設定",
                Description = $"在 {debugEndpointFiles.Count} 個檔案中找到 /debug、/actuator 或開發例外頁面設定，可能在生產環境洩漏敏感資訊。",
                Recommendation = "確保所有除錯端點在生產環境有適當保護或停用；開發例外頁面只在 Development 環境啟用。",
                AffectedFiles = debugEndpointFiles.Take(5).ToList()
            });

        if (commandInjectionFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "high", Priority = 2,
                Category = "A1-注入攻擊",
                Title = "疑似系統命令執行（潛在命令注入）",
                Description = $"在 {commandInjectionFiles.Count} 個檔案中偵測到系統命令執行函式，若帶入用戶輸入可能導致命令注入。",
                Recommendation = "避免直接執行系統命令；若必要，嚴格白名單過濾所有輸入，禁止 shell 特殊字元。",
                AffectedFiles = commandInjectionFiles.Take(5).ToList()
            });

        if (insecureDeserFiles.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "high", Priority = 2,
                Category = "A8-軟體與資料完整性失效",
                Title = "疑似不安全的反序列化",
                Description = $"在 {insecureDeserFiles.Count} 個檔案中偵測到可能不安全的反序列化用法（BinaryFormatter、TypeNameHandling 等）。",
                Recommendation = "避免使用 BinaryFormatter；JSON 反序列化禁用 TypeNameHandling.All；對輸入資料做嚴格型別驗證。",
                AffectedFiles = insecureDeserFiles.Take(5).ToList()
            });

        // Check for exposed Docker ports (high-risk services exposed to 0.0.0.0)
        var riskyPorts = data.Containers
            .SelectMany(c => c.Ports.Select(p => new { c.Name, p.HostPort }))
            .Where(p => p.HostPort is 3306 or 5432 or 6379 or 27017 or 9200 or 2181)
            .ToList();
        if (riskyPorts.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "warning", Priority = 3,
                Category = "A5-安全設定錯誤",
                Title = "高風險資料庫/服務 Port 可能對外暴露",
                Description = $"以下服務的管理 Port 可能映射到宿主機：" +
                              string.Join(", ", riskyPorts.Take(5).Select(p => $"{p.Name}:{p.HostPort}")),
                Recommendation = "生產環境中資料庫 Port 不應對外暴露，使用 Docker 網路隔離，僅允許同網路內部存取。"
            });

        return risks;
    }

    // ─── Copilot API ───────────────────────────────────────────────────────

    // Allowlist of model identifiers accepted by the Copilot chat-completions endpoint.
    // An unexpected COPILOT_MODEL value (typo, injection) is silently replaced with
    // DefaultModel rather than forwarded to the API, preventing silent HTTP 422 errors
    // and limiting the blast radius of a misconfigured environment (CWE-20).
    private static readonly HashSet<string> KnownModels = new(StringComparer.OrdinalIgnoreCase)
        { "gpt-4o", "gpt-4o-mini", "gpt-4.1" };

    /// <summary>
    /// Sends a single chat-completion request to the Copilot API and returns the text content
    /// of the first response choice, or <c>null</c> on any failure.
    /// </summary>
    /// <param name="userPrompt">The full user-turn prompt text to send.</param>
    /// <param name="maxTokens">
    /// Upper bound on response tokens.  Tune per call site to avoid truncation on long
    /// structured responses (e.g. JSON arrays) while keeping short summaries cheap.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.  <see cref="OperationCanceledException"/> is always re-thrown
    /// so the caller's <see cref="Task.WhenAll"/> propagates user Ctrl+C correctly.
    /// </param>
    /// <returns>
    /// The model's response string, or <c>null</c> if the API returns a non-2xx status
    /// or if any network/parsing error occurs.
    /// </returns>
    /// <remarks>
    /// The first invocation (across all concurrent callers in a <c>Task.WhenAll</c>) displays
    /// a 5-second consent warning.  Subsequent callers skip it atomically via
    /// <see cref="Interlocked.Exchange"/> on <see cref="_uploadWarningShown"/>.
    /// </remarks>
    private async Task<string?> CallCopilotAsync(string userPrompt, int maxTokens, CancellationToken ct)
    {
        // Interlocked.Exchange atomically writes 1 and returns the PREVIOUS value.
        // Only the very first caller sees 0 (old value) and enters the warning block.
        // All concurrent callers from Task.WhenAll already see 1 and skip the warning,
        // guaranteeing the countdown is shown exactly once even under parallelism.
        if (Interlocked.Exchange(ref _uploadWarningShown, 1) == 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠  [RID] Code snippets will be sent to api.githubcopilot.com for AI analysis.");
            Console.WriteLine("   Press Ctrl+C within 5 seconds to abort, or wait to continue...");
            Console.ResetColor();
            await Task.Delay(5000, ct);
        }

        // Validate _model against KnownModels before sending it to the API.
        // safeModel is always a literal string from KnownModels or DefaultModel, never
        // a raw user-supplied value — this prevents prompt/header injection (CWE-20).
        var safeModel = KnownModels.Contains(_model) ? _model : DefaultModel;

        var payload = new
        {
            // safeModel is always a member of KnownModels or DefaultModel.
            model = safeModel,
            messages = new[]
            {
                new { role = "system", content = "你是一位資深程式架構分析師與資訊安全專家，專精於繁體中文技術文件撰寫。" },
                new { role = "user", content = userPrompt }
            },
            max_tokens = maxTokens,
            temperature = 0.3
        };

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, CopilotEndpoint)
            {
                Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("Copilot-Integration-Id", "repo-insight-dashboard");
            request.Headers.Add("Editor-Version", "vscode/1.85.0");

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Log HTTP-level failures so auth/TLS errors are not silently swallowed (CWE-390).
                Debug.WriteLine(
                    $"[CopilotSemanticAnalyzer] Copilot API returned {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase} for model='{safeModel}'");
                return null;
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            return json["choices"]?[0]?["message"]?["content"]?.ToString();
        }
        catch (OperationCanceledException) { throw; } // Always propagate user Ctrl+C / timeout.
        catch (HttpRequestException ex)
        {
            Debug.WriteLine(
                $"[CopilotSemanticAnalyzer] HTTP error calling Copilot: " +
                $"StatusCode={ex.StatusCode}, Message={ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[CopilotSemanticAnalyzer] Unexpected error in CallCopilotAsync: " +
                $"{ex.GetType().Name} — {ex.Message}");
            return null;
        }
    }

    // ─── Local Fallbacks ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a brief project summary from <see cref="DashboardData"/> without any AI call.
    /// Used when <see cref="IsAvailable"/> is <c>false</c> (no Copilot token configured).
    /// </summary>
    /// <param name="data">Populated dashboard data to summarise.</param>
    /// <returns>A single Markdown-compatible sentence describing the project.</returns>
    private string GenerateLocalSummary(DashboardData data)
    {
        var langs = string.Join("、", data.Project.Languages.Take(3).Select(l => l.Name));
        var apiCount = data.ApiEndpoints.Count;
        var svcCount = data.Containers.Count;
        var pkgCount = data.Packages.Count;

        return $"本專案 **{data.Project.Name}** 使用 {langs} 開發，" +
               $"共包含 {data.Project.TotalFiles} 個檔案。" +
               (apiCount > 0 ? $"提供 {apiCount} 個 API 端點，" : "") +
               (svcCount > 0 ? $"由 {svcCount} 個容器服務組成，" : "") +
               (pkgCount > 0 ? $"依賴 {pkgCount} 個外部套件。" : "") +
               $"分支：{data.Project.Branch}。";
    }

    /// <summary>
    /// Infers architectural design patterns from package names and project structure
    /// without any AI call.  Used as a fast offline fallback when no Copilot token is set.
    /// </summary>
    /// <param name="data">Populated dashboard data used for heuristic matching.</param>
    /// <returns>
    /// List of detected pattern names (e.g. "微服務架構", "RESTful API", "gRPC 通訊").
    /// Returns an empty list if no patterns are matched.
    /// </returns>
    /// <remarks>
    /// Package scanning uses a single O(P) loop with early-exit once all three flags are found,
    /// rather than three separate <c>.Any()</c> traversals which would each scan up to P items —
    /// worst-case O(3P) vs O(P) for large dependency lists.
    /// </remarks>
    private List<string> DetectPatternsLocally(DashboardData data)
    {
        var patterns = new List<string>();
        if (data.Containers.Count > 1) patterns.Add("微服務架構");
        if (data.ApiEndpoints.Count > 0) patterns.Add("RESTful API");
        if (data.Containers.Count > 0) patterns.Add("容器化部署");

        // Single O(P) pass over packages instead of three separate .Any() traversals.
        // Short-circuits as soon as all three flags are found. (P3 perf fix)
        bool hasGrpc = false, hasMessageQueue = false, hasRedis = false;
        foreach (var pkg in data.Packages)
        {
            var name = pkg.Name;
            if (!hasGrpc         && name.Contains("grpc",      StringComparison.OrdinalIgnoreCase)) hasGrpc = true;
            if (!hasMessageQueue && (name.Contains("kafka",     StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("rabbitmq",  StringComparison.OrdinalIgnoreCase))) hasMessageQueue = true;
            if (!hasRedis        && name.Contains("redis",      StringComparison.OrdinalIgnoreCase)) hasRedis = true;
            // Early exit: once all three flags are set, further iterations cannot add new patterns.
            if (hasGrpc && hasMessageQueue && hasRedis) break;
        }
        if (hasGrpc)         patterns.Add("gRPC 通訊");
        if (hasMessageQueue) patterns.Add("訊息佇列");
        if (hasRedis)        patterns.Add("快取層 (Redis)");

        if (data.Project.Languages.Any(l => l.Name is "TypeScript" or "JavaScript") &&
            data.Project.Languages.Any(l => l.Name is "C#" or "Go" or "Java"))
            patterns.Add("前後端分離");
        return patterns;
    }
}
