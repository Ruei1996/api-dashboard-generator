// ============================================================
// EnvFileAnalyzer.cs — .env file discovery and safe parsing
// ============================================================
// Architecture: stateless service class, instantiated by AnalysisOrchestrator.
// Depends on: RepoInsightDashboard.Models (EnvVariable, FileNode)
//
// Security note (CWE-312 — Cleartext Storage of Sensitive Information):
//   Sensitive values are replaced with "***masked***" at parse time — the earliest
//   possible moment — so that no plaintext secret ever reaches any downstream
//   consumer: HTML renderer, JSON serialiser, console logger, or AI prompt builder.
//   Masking at the output layer would be too late; a bug in any single consumer
//   could expose a live credential.
// ============================================================

using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Reads .env files discovered during the file scan and extracts their key/value pairs,
/// automatically masking values whose key names match known sensitive patterns
/// (password, token, secret, key, etc.) before returning them to callers.
/// </summary>
/// <remarks>
/// <para>
/// Masking happens at parse time — not at render time — so the masked value propagates
/// through every downstream layer (HTML, JSON, AI prompt) without further special-casing.
/// This is a defence-in-depth measure aligned with OWASP A3 (Sensitive Data Exposure)
/// and CWE-312 (Cleartext Storage of Sensitive Information).
/// </para>
/// <para>
/// Test-only .env files (test.env, *.test.env, files under /test/, /tests/, /spec/) are
/// excluded from the analysis because they typically contain fake credentials and would
/// produce misleading security warnings.
/// </para>
/// </remarks>
public class EnvFileAnalyzer
{
    // Key-name fragments that indicate the value is a secret credential.
    // Uses OrdinalIgnoreCase so "Password", "PASSWORD", and "password" all match.
    // Intentionally broad — false positives (masking a non-secret) are far safer
    // than false negatives (displaying a real credential in the dashboard).
    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "key", "token", "credential",
        "api_key", "apikey", "private", "auth", "cert", "pass"
    };

    /// <summary>
    /// Parses all .env files present in <paramref name="files"/>, skipping test fixtures,
    /// and returns the extracted variables with sensitive values already replaced by
    /// <c>"***masked***"</c>.
    /// </summary>
    /// <param name="files">
    /// Flat list of <see cref="FileNode"/> objects produced by <see cref="FileScanner"/>.
    /// Non-.env files and directory nodes are ignored.
    /// </param>
    /// <returns>
    /// List of <see cref="EnvVariable"/> objects — one per valid KEY=VALUE line across
    /// all discovered .env files.  The <see cref="EnvVariable.IsSensitive"/> flag is
    /// set to <c>true</c> when the key matched a sensitive keyword, and
    /// <see cref="EnvVariable.Value"/> is <c>"***masked***"</c> in that case.
    /// </returns>
    public List<EnvVariable> Analyze(List<FileNode> files)
    {
        var vars = new List<EnvVariable>();
        var envFiles = files.Where(f => !f.IsDirectory && IsEnvFile(f)).ToList();

        foreach (var file in envFiles)
        {
            try
            {
                foreach (var line in File.ReadAllLines(file.AbsolutePath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx < 0) continue;

                    var key = trimmed[..eqIdx].Trim();
                    var rawValue = trimmed[(eqIdx + 1)..].Trim().Trim('"', '\'');

                    // Check whether ANY sensitive keyword appears anywhere in the key name.
                    // Using Contains (substring match) rather than Equals so composite keys
                    // like "DB_PASSWORD" or "STRIPE_API_KEY" are also caught.
                    var isSensitive = SensitiveKeywords.Any(kw =>
                        key.Contains(kw, StringComparison.OrdinalIgnoreCase));

                    // Mask sensitive values at parse time — the earliest safe opportunity.
                    // Doing this here (rather than at the HTML/JSON output layer) ensures
                    // that the raw value never reaches AI prompts, log sinks, or any other
                    // consumer, even if a future code path forgets to mask it (CWE-312).
                    var value = isSensitive ? "***masked***" : rawValue;

                    vars.Add(new EnvVariable
                    {
                        Key = key,
                        Value = value,
                        IsSensitive = isSensitive,
                        SourceFile = file.RelativePath
                    });
                }
            }
            catch (IOException ex)
            {
                // Unreadable env files are non-fatal (e.g. permissions issue); log to stderr
                // so the problem is visible without aborting the full analysis. (CWE-390)
                Console.Error.WriteLine($"[EnvFileAnalyzer] 無法讀取 {file.RelativePath}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"[EnvFileAnalyzer] 存取被拒 {file.RelativePath}: {ex.Message}");
            }
        }
        return vars;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="file"/> is a .env file that should be analysed.
    /// Excludes test-related env files to avoid spurious security warnings from fake credentials.
    /// </summary>
    /// <param name="file">The file node to evaluate.</param>
    /// <returns>
    /// <c>true</c> for files whose name is <c>.env</c>, starts with <c>.env.</c>, or ends
    /// with <c>.env</c> — unless they are identified as test fixtures by name or path.
    /// </returns>
    private static bool IsEnvFile(FileNode file)
    {
        var name = file.Name.ToLowerInvariant();
        var path = file.RelativePath.ToLowerInvariant();

        // Exclude test env files: test.env, *.test.env, files in test/ directories.
        // Test files usually contain fake/placeholder values and would flood the
        // security report with meaningless warnings (e.g. PASSWORD=testpassword123).
        if (name == "test.env" || name.StartsWith("test.") || name.Contains(".test."))
            return false;
        if (path.Contains("/test/") || path.Contains("/tests/") || path.Contains("/spec/"))
            return false;

        return name == ".env" || name.StartsWith(".env.") || name.EndsWith(".env");
    }
}
