using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class EnvFileAnalyzer
{
    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "key", "token", "credential",
        "api_key", "apikey", "private", "auth", "cert", "pass"
    };

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
                    var value = trimmed[(eqIdx + 1)..].Trim().Trim('"', '\'');
                    var isSensitive = SensitiveKeywords.Any(kw =>
                        key.Contains(kw, StringComparison.OrdinalIgnoreCase));

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
        return name == ".env" || name.StartsWith(".env.") || name.EndsWith(".env");
    }
}
