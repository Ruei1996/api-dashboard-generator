using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class CopilotSemanticAnalyzer
{
    private readonly HttpClient _http;
    private readonly string? _token;
    private const string CopilotEndpoint = "https://api.githubcopilot.com/chat/completions";

    public bool IsAvailable => !string.IsNullOrEmpty(_token);

    public CopilotSemanticAnalyzer(string? copilotToken = null)
    {
        _token = copilotToken ?? Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN")
                              ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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

    public async Task<List<SecurityRisk>> DetectSecurityRisksAsync(
        DashboardData data,
        List<FileNode>? allFiles = null,
        string? repoPath = null,
        CancellationToken ct = default)
    {
        // Always run local static analysis
        var risks = DetectSecurityRisksLocally(data, allFiles, repoPath);

        // AI deep scan when token is available
        if (IsAvailable && allFiles != null)
        {
            var aiRisks = await AnalyzeCodeSecurityWithAIAsync(data, allFiles, ct);
            foreach (var r in aiRisks)
            {
                // Deduplicate by title + file
                if (!risks.Any(x => x.Title == r.Title && x.FilePath == r.FilePath))
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

        var hardcodedSecretFiles = new List<string>();
        var sqlInjectionFiles = new List<string>();
        var insecureTlsFiles = new List<string>();
        var weakCryptoFiles = new List<string>();
        var debugEndpointFiles = new List<string>();
        var commandInjectionFiles = new List<string>();
        var insecureDeserFiles = new List<string>();

        // Regex patterns for common security issues
        var secretPattern = new Regex(
            @"(?i)(password|passwd|secret|api_key|apikey|private_key|access_token|auth_token|client_secret)\s*[=:]\s*[""'][^""'\s]{6,}[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var sqlConcatPattern = new Regex(
            @"(?i)(select|insert|update|delete|where|from)\s+.{0,60}(\+|string\.Format|String\.format|sprintf|fmt\.Sprintf)\s*",
            RegexOptions.Compiled);
        var insecureTlsPattern = new Regex(
            @"(?i)(ServerCertificateValidationCallback\s*=.*true|InsecureSkipVerify\s*:\s*true|verify\s*=\s*false|ssl_verify\s*=\s*false|CURLOPT_SSL_VERIFYPEER)",
            RegexOptions.Compiled);
        var weakCryptoPattern = new Regex(
            @"(?i)(MD5\.Create|SHA1\.Create|new\s+MD5|new\s+SHA1|hashlib\.md5|hashlib\.sha1|DES\.|TripleDES\.|RC2\.|['""]md5['""]|['""]sha1['""])",
            RegexOptions.Compiled);
        var debugEndpointPattern = new Regex(
            @"(?i)(\/debug|\/actuator|app\.UseDeveloperExceptionPage|DEBUG\s*=\s*true|\.UseSwagger\(\))",
            RegexOptions.Compiled);
        var commandInjectionPattern = new Regex(
            @"(?i)(Process\.Start|os\.system\(|exec\(|shell_exec\(|popen\(|Runtime\.getRuntime\(\)\.exec|os\.exec\.Command)",
            RegexOptions.Compiled);
        var insecureDeserPattern = new Regex(
            @"(?i)(JsonConvert\.DeserializeObject.*TypeNameHandling|BinaryFormatter|ObjectInputStream|pickle\.loads|yaml\.load\((?!.*Loader))",
            RegexOptions.Compiled);

        foreach (var file in sourceFiles.Take(200))
        {
            try
            {
                var content = File.ReadAllText(file.AbsolutePath);
                if (secretPattern.IsMatch(content)) hardcodedSecretFiles.Add(file.RelativePath);
                if (sqlConcatPattern.IsMatch(content)) sqlInjectionFiles.Add(file.RelativePath);
                if (insecureTlsPattern.IsMatch(content)) insecureTlsFiles.Add(file.RelativePath);
                if (weakCryptoPattern.IsMatch(content)) weakCryptoFiles.Add(file.RelativePath);
                if (debugEndpointPattern.IsMatch(content)) debugEndpointFiles.Add(file.RelativePath);
                if (commandInjectionPattern.IsMatch(content)) commandInjectionFiles.Add(file.RelativePath);
                if (insecureDeserPattern.IsMatch(content)) insecureDeserFiles.Add(file.RelativePath);
            }
            catch { }
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

    private async Task<string?> CallCopilotAsync(string userPrompt, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            model = "gpt-4o",
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
            if (!response.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            return json["choices"]?[0]?["message"]?["content"]?.ToString();
        }
        catch { return null; }
    }

    // ─── Local Fallbacks ───────────────────────────────────────────────────

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

    private List<string> DetectPatternsLocally(DashboardData data)
    {
        var patterns = new List<string>();
        if (data.Containers.Count > 1) patterns.Add("微服務架構");
        if (data.ApiEndpoints.Count > 0) patterns.Add("RESTful API");
        if (data.Containers.Count > 0) patterns.Add("容器化部署");
        if (data.Packages.Any(p => p.Name.Contains("grpc", StringComparison.OrdinalIgnoreCase)))
            patterns.Add("gRPC 通訊");
        if (data.Packages.Any(p => p.Name.Contains("kafka", StringComparison.OrdinalIgnoreCase) ||
                                   p.Name.Contains("rabbitmq", StringComparison.OrdinalIgnoreCase)))
            patterns.Add("訊息佇列");
        if (data.Packages.Any(p => p.Name.Contains("redis", StringComparison.OrdinalIgnoreCase)))
            patterns.Add("快取層 (Redis)");
        if (data.Project.Languages.Any(l => l.Name is "TypeScript" or "JavaScript") &&
            data.Project.Languages.Any(l => l.Name is "C#" or "Go" or "Java"))
            patterns.Add("前後端分離");
        return patterns;
    }
}
