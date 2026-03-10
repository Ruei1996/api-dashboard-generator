using System.Text.RegularExpressions;

namespace RepoInsightDashboard.Analyzers;

public class GitignoreParser
{
    private readonly List<GitignoreRule> _rules = [];
    private static readonly string[] DefaultIgnores =
    [
        ".git/", "node_modules/", "bin/", "obj/", ".vs/", ".idea/",
        "*.user", "*.suo", ".DS_Store", "Thumbs.db", "__pycache__/",
        "*.pyc", "*.pyo", ".pytest_cache/", "dist/", "build/",
        "coverage/", ".nyc_output/", "vendor/", "packages/"
    ];

    public GitignoreParser(string repoPath)
    {
        foreach (var pattern in DefaultIgnores)
            _rules.Add(new GitignoreRule(pattern, true));

        var gitignorePath = Path.Combine(repoPath, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            foreach (var line in File.ReadAllLines(gitignorePath))
                AddRule(line);
        }
    }

    private void AddRule(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) return;

        var negated = trimmed.StartsWith('!');
        if (negated) trimmed = trimmed[1..];

        _rules.Add(new GitignoreRule(trimmed, !negated));
    }

    public bool IsIgnored(string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        bool ignored = false;

        foreach (var rule in _rules)
        {
            if (rule.Matches(relativePath))
                ignored = rule.Ignore;
        }

        return ignored;
    }

    private class GitignoreRule
    {
        private readonly Regex _regex;
        public bool Ignore { get; }

        public GitignoreRule(string pattern, bool ignore)
        {
            Ignore = ignore;
            _regex = PatternToRegex(pattern);
        }

        public bool Matches(string path) => _regex.IsMatch(path);

        private static Regex PatternToRegex(string pattern)
        {
            var isDirectory = pattern.EndsWith('/');
            if (isDirectory) pattern = pattern.TrimEnd('/');

            var escaped = Regex.Escape(pattern)
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]");

            var regexPattern = isDirectory
                ? $"(^|/){escaped}(/|$)"
                : $"(^|/){escaped}$";

            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}
