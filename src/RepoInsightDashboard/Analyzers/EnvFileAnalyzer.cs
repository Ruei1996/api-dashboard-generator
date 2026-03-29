using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Reads .env files and extracts key/value pairs, flagging entries whose key names
/// match known sensitive-value patterns (password, token, secret, etc.).
/// Sensitive values are replaced with <c>"***masked***"</c> at parse time so that
/// no plaintext secrets ever reach any output format (HTML, JSON, logs).
/// </summary>
public class EnvFileAnalyzer
{
    // Key-name fragments that indicate the value is a secret credential.
    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "key", "token", "credential",
        "api_key", "apikey", "private", "auth", "cert", "pass"
    };

    /// <summary>
    /// Parses all .env files found in <paramref name="files"/>, skipping test env files,
    /// and returns the variable list with sensitive values already masked.
    /// </summary>
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
                    var isSensitive = SensitiveKeywords.Any(kw =>
                        key.Contains(kw, StringComparison.OrdinalIgnoreCase));

                    // Mask sensitive values at the source — never allow secrets to propagate
                    // into any output format (HTML data-value attributes, JSON, logs, etc.).
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
            catch { /* skip */ }
        }
        return vars;
    }

    private static bool IsEnvFile(FileNode file)
    {
        var name = file.Name.ToLowerInvariant();
        var path = file.RelativePath.ToLowerInvariant();

        // Exclude test env files: test.env, *.test.env, files in test/ directories
        if (name == "test.env" || name.StartsWith("test.") || name.Contains(".test."))
            return false;
        if (path.Contains("/test/") || path.Contains("/tests/") || path.Contains("/spec/"))
            return false;

        return name == ".env" || name.StartsWith(".env.") || name.EndsWith(".env");
    }
}
