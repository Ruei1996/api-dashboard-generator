using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class CopilotSemanticAnalyzer
{
    private readonly HttpClient _http;
    private readonly string? _token;
    private const string CopilotEndpoint = "https://api.githubcopilot.com/chat/completions";
    private const string FallbackEndpoint = "https://api.openai.com/v1/chat/completions";

    public bool IsAvailable => !string.IsNullOrEmpty(_token);

    public CopilotSemanticAnalyzer(string? copilotToken = null)
    {
        _token = copilotToken ?? Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN")
                              ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

    public async Task<List<SecurityRisk>> DetectSecurityRisksAsync(DashboardData data, CancellationToken ct = default)
    {
        var risks = DetectSecurityRisksLocally(data);

        if (!IsAvailable || data.EnvVariables.Count == 0) return risks;

        var exposedSecrets = data.EnvVariables
            .Where(e => e.IsSensitive && !string.IsNullOrEmpty(e.Value) && e.Value != "***")
            .Take(10)
            .Select(e => e.Key)
            .ToList();

        if (exposedSecrets.Count > 0)
        {
            risks.Add(new SecurityRisk
            {
                Level = "critical",
                Title = "敏感環境變數暴露",
                Description = $"以下敏感變數可能包含明文密鑰：{string.Join(", ", exposedSecrets)}",
                FilePath = ".env"
            });
        }

        return risks;
    }

    public async Task<string> TranslateApiDescriptionAsync(string description, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(description)) return description;
        if (!IsAvailable) return description;

        var prompt = $"將以下 API 說明翻譯為繁體中文，保持技術術語，直接回傳翻譯結果：\n{description}";
        return await CallCopilotAsync(prompt, 150, ct) ?? description;
    }

    private async Task<string?> CallCopilotAsync(string userPrompt, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = "你是一位資深程式架構分析師，專精於繁體中文技術文件撰寫。" },
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

    private List<SecurityRisk> DetectSecurityRisksLocally(DashboardData data)
    {
        var risks = new List<SecurityRisk>();

        // Check for sensitive info in .env
        var sensitiveVars = data.EnvVariables.Where(e => e.IsSensitive).ToList();
        if (sensitiveVars.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "warning",
                Title = $"發現 {sensitiveVars.Count} 個敏感環境變數",
                Description = "請確認這些變數的值不包含真實密鑰：" + string.Join(", ", sensitiveVars.Take(5).Select(v => v.Key)),
                FilePath = ".env"
            });

        // Check for deprecated APIs
        var deprecatedApis = data.ApiEndpoints.Where(e => e.IsDeprecated).ToList();
        if (deprecatedApis.Count > 0)
            risks.Add(new SecurityRisk
            {
                Level = "info",
                Title = $"{deprecatedApis.Count} 個已棄用的 API 端點",
                Description = "建議移除或更新這些端點：" + string.Join(", ", deprecatedApis.Take(3).Select(a => $"{a.Method} {a.Path}"))
            });

        return risks;
    }
}
