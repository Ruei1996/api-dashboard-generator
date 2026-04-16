// ============================================================
// GitignoreParser.cs — .gitignore rule engine
// ============================================================
// Architecture: stateful value object; built once per analyze run from the repo's
//   .gitignore file plus a baked-in DefaultIgnores list.  Rules are evaluated in
//   declaration order (later rules override earlier ones), matching the official
//   gitignore specification.
//
// Supported syntax:
//   *.ext          — extension glob
//   dir/           — directory match (trailing slash)
//   **/glob        — double-star path prefix
//   !pattern       — negation (un-ignores previously matched paths)
//   # comment      — ignored
//
// Usage:
//   var parser = new GitignoreParser("/path/to/repo");
//   bool skip = parser.IsIgnored("vendor/foo/bar.go"); // → true
// ============================================================

using System.Text.RegularExpressions;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Applies .gitignore filtering to repository-relative paths.
/// Combines a hardcoded <c>DefaultIgnores</c> list (covering common build artefacts,
/// language-specific caches, and editor files) with patterns loaded from the
/// repository's own <c>.gitignore</c> file.
/// </summary>
/// <remarks>
/// Rules are accumulated in order; a later rule always wins over an earlier one.
/// Negation patterns (<c>!pattern</c>) are supported and will un-ignore a previously
/// matched path.  This matches the semantics of <c>git check-ignore</c>.
/// </remarks>
public class GitignoreParser
{
    private readonly List<GitignoreRule> _rules = [];

    // Hardcoded baseline patterns that are almost universally ignored across all project types.
    // Loaded BEFORE any repo-specific .gitignore entries so that project-specific !negation
    // rules can override them if needed.
    private static readonly string[] DefaultIgnores =
    [
        // Version-control internals
        ".git/",
        // JavaScript / Node.js
        "node_modules/", "dist/", ".next/", ".nuxt/", ".parcel-cache/", ".cache/",
        ".turbo/", ".svelte-kit/", ".remix/",
        // .NET / MSBuild
        "bin/", "obj/", ".vs/", "packages/",
        // Java / Kotlin / Scala + Rust (both use target/ for build output)
        "target/", "*.class", "*.jar", "*.war", "*.ear", ".gradle/", ".m2/",
        // Python
        "__pycache__/", "*.pyc", "*.pyo", ".pytest_cache/", "*.egg-info/", "*.egg",
        "venv/", ".venv/", "env/", ".env/", "site-packages/",
        // Ruby
        ".bundle/", "vendor/bundle/",
        // iOS / macOS
        "Pods/", "*.xcworkspace/",
        // Elixir / Erlang
        "_build/", ".mix/", "deps/",
        // Go
        "vendor/",
        // Infrastructure / CI
        ".terraform/", "*.tfstate", "*.tfstate.backup",
        // Editor / OS artefacts
        ".idea/", ".vscode/", "*.user", "*.suo", ".DS_Store", "Thumbs.db",
        // Test coverage
        "coverage/", ".nyc_output/",
        // Misc build artefacts
        "build/", "out/", "__mocks__/",
    ];

    /// <summary>
    /// Constructs a <see cref="GitignoreParser"/> for the repository at <paramref name="repoPath"/>.
    /// Loads the built-in <c>DefaultIgnores</c> first, then appends patterns from the repo's
    /// own <c>.gitignore</c> file (if present).
    /// </summary>
    /// <param name="repoPath">
    /// Absolute path to the repository root.  The parser looks for a <c>.gitignore</c>
    /// file directly in this directory; nested <c>.gitignore</c> files in sub-directories
    /// are not currently processed (future enhancement).
    /// </param>
    public GitignoreParser(string repoPath)
    {
        // Register all default ignore patterns before the repo-specific ones.
        // This ensures the common build artefacts are always filtered out,
        // even if the repo has no .gitignore file at all.
        foreach (var pattern in DefaultIgnores)
            _rules.Add(new GitignoreRule(pattern, true));

        var gitignorePath = Path.Combine(repoPath, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            foreach (var line in File.ReadAllLines(gitignorePath))
                AddRule(line);
        }
    }

    /// <summary>
    /// Parses a single raw line from a .gitignore file and appends it to the rule list.
    /// Blank lines and comment lines (starting with <c>#</c>) are silently discarded.
    /// Negation patterns (starting with <c>!</c>) produce rules with <c>Ignore = false</c>.
    /// </summary>
    /// <param name="line">One raw line as read from the .gitignore file.</param>
    private void AddRule(string line)
    {
        var trimmed = line.Trim();

        // Skip blank lines and comment lines — they carry no filtering information.
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) return;

        // A leading '!' negates the pattern: any path previously ignored by an earlier rule
        // is un-ignored when this rule matches.
        var negated = trimmed.StartsWith('!');
        if (negated) trimmed = trimmed[1..];

        // ignore=false means "do NOT ignore" (negation rule), ignore=true means "ignore".
        _rules.Add(new GitignoreRule(trimmed, !negated));
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="relativePath"/> should be excluded from analysis.
    /// Rules are evaluated in declaration order; the last matching rule wins (gitignore semantics).
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the repository root, using either forward or back slashes.
    /// A leading slash is stripped before matching so callers do not need to normalise.
    /// </param>
    /// <returns>
    /// <c>true</c> when the path matches any active ignore rule and is not un-ignored
    /// by a later negation rule; <c>false</c> otherwise.
    /// </returns>
    public bool IsIgnored(string relativePath)
    {
        // Normalise to forward-slash, no leading slash — the regex patterns assume this form.
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        bool ignored = false;

        // Walk every rule in order — later rules override earlier ones (gitignore spec).
        foreach (var rule in _rules)
        {
            if (rule.Matches(relativePath))
                ignored = rule.Ignore;
        }

        return ignored;
    }

    /// <summary>
    /// Encapsulates a single compiled .gitignore pattern with its ignore/include flag.
    /// </summary>
    private class GitignoreRule
    {
        private readonly Regex _regex;

        /// <summary>
        /// <c>true</c> → matching paths should be ignored;
        /// <c>false</c> → matching paths should be un-ignored (negation rule).
        /// </summary>
        public bool Ignore { get; }

        /// <summary>
        /// Compiles <paramref name="pattern"/> into a <see cref="Regex"/> and records
        /// whether it is an ignore or un-ignore rule.
        /// </summary>
        /// <param name="pattern">The normalised pattern string (without leading <c>!</c>).</param>
        /// <param name="ignore">
        /// <c>true</c> if matching paths should be ignored; <c>false</c> for negation rules.
        /// </param>
        public GitignoreRule(string pattern, bool ignore)
        {
            Ignore = ignore;
            _regex = PatternToRegex(pattern);
        }

        /// <summary>Returns <c>true</c> if <paramref name="path"/> matches this rule's pattern.</summary>
        public bool Matches(string path) => _regex.IsMatch(path);

        /// <summary>
        /// Converts a .gitignore glob pattern to a compiled <see cref="Regex"/>.
        /// </summary>
        /// <remarks>
        /// Conversion rules:
        /// <list type="bullet">
        ///   <item><c>**</c> → <c>.*</c> (matches any number of path segments)</item>
        ///   <item><c>*</c>  → <c>[^/]*</c> (matches within a single segment)</item>
        ///   <item><c>?</c>  → <c>[^/]</c>  (matches a single non-separator character)</item>
        ///   <item>Trailing <c>/</c> → anchors the pattern so it only matches directories.</item>
        /// </list>
        /// </remarks>
        private static Regex PatternToRegex(string pattern)
        {
            // A trailing slash means "only match directories" — strip it and encode that
            // intent in the regex anchor pattern below.
            var isDirectory = pattern.EndsWith('/');
            if (isDirectory) pattern = pattern.TrimEnd('/');

            // Escape the pattern then expand gitignore wildcards back to regex equivalents.
            var escaped = Regex.Escape(pattern)
                .Replace(@"\*\*", ".*")     // ** → any depth
                .Replace(@"\*", "[^/]*")    // *  → within one segment
                .Replace(@"\?", "[^/]");    // ?  → single non-separator char

            // Directory patterns must match at a segment boundary (not mid-name).
            var regexPattern = isDirectory
                ? $"(^|/){escaped}(/|$)"
                : $"(^|/){escaped}$";

            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}
