using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Maps file extensions and special filenames to their display language name and GitHub Linguist hex colour.
/// Used by <see cref="FileScanner"/> to tag each <see cref="FileNode"/> and by
/// <see cref="HtmlDashboardGenerator"/> to render the language breakdown chart.
/// </summary>
public static class LanguageDetector
{
    // Maps file extensions (case-insensitive) to their language display name and GitHub Linguist colour.
    private static readonly Dictionary<string, (string Name, string Color)> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Compiled / Systems ──────────────────────────────────────────────
        { ".cs",      ("C#",           "#178600") },
        { ".csx",     ("C#",           "#178600") },
        { ".go",      ("Go",           "#00ADD8") },
        { ".java",    ("Java",         "#b07219") },
        { ".kt",      ("Kotlin",       "#A97BFF") },
        { ".kts",     ("Kotlin",       "#A97BFF") },
        { ".rs",      ("Rust",         "#dea584") },
        { ".swift",   ("Swift",        "#F05138") },
        { ".cpp",     ("C++",          "#f34b7d") },
        { ".cc",      ("C++",          "#f34b7d") },
        { ".cxx",     ("C++",          "#f34b7d") },
        { ".c",       ("C",            "#555555") },
        { ".h",       ("C/C++",        "#555555") },
        { ".hpp",     ("C++",          "#f34b7d") },
        { ".hxx",     ("C++",          "#f34b7d") },
        { ".scala",   ("Scala",        "#c22d40") },
        { ".sc",      ("Scala",        "#c22d40") },
        { ".zig",     ("Zig",          "#ec915c") },
        { ".nim",     ("Nim",          "#ffc200") },
        { ".nims",    ("Nim",          "#ffc200") },
        { ".cr",      ("Crystal",      "#000100") },
        { ".d",       ("D",            "#ba595e") },
        { ".v",       ("V",            "#4f87c4") },

        // ── JVM / Managed runtimes ───────────────────────────────────────────
        { ".groovy",  ("Groovy",       "#e69f56") },
        { ".gvy",     ("Groovy",       "#e69f56") },
        { ".clj",     ("Clojure",      "#db5855") },
        { ".cljs",    ("ClojureScript","#db5855") },
        { ".cljc",    ("Clojure",      "#db5855") },

        // ── .NET ─────────────────────────────────────────────────────────────
        { ".vb",      ("Visual Basic", "#945db7") },
        { ".fs",      ("F#",           "#b845fc") },
        { ".fsx",     ("F#",           "#b845fc") },
        { ".fsi",     ("F#",           "#b845fc") },

        // ── Scripting / Dynamic ──────────────────────────────────────────────
        { ".py",      ("Python",       "#3572A5") },
        { ".pyw",     ("Python",       "#3572A5") },
        { ".rb",      ("Ruby",         "#701516") },
        { ".php",     ("PHP",          "#4F5D95") },
        { ".php8",    ("PHP",          "#4F5D95") },
        { ".php7",    ("PHP",          "#4F5D95") },
        { ".lua",     ("Lua",          "#000080") },
        { ".pl",      ("Perl",         "#0298c3") },
        { ".pm",      ("Perl",         "#0298c3") },
        { ".tcl",     ("Tcl",          "#e4cc98") },
        { ".r",       ("R",            "#198CE7") },
        { ".jl",      ("Julia",        "#a270ba") },
        // NOTE: .m is ambiguous — both MATLAB and Objective-C use it.
        // We default to MATLAB here; repos with Xcode projects (.xcodeproj/Podfile)
        // are likely iOS and should treat .m as Objective-C — a future heuristic could
        // check for @implementation/@interface to disambiguate (code-review issue #5).
        { ".m",       ("MATLAB",       "#e16737") },

        // ── Functional ───────────────────────────────────────────────────────
        { ".hs",      ("Haskell",      "#5e5086") },
        { ".lhs",     ("Haskell",      "#5e5086") },
        { ".elm",     ("Elm",          "#60B5CC") },
        { ".ml",      ("OCaml",        "#3be133") },
        { ".mli",     ("OCaml",        "#3be133") },
        { ".ex",      ("Elixir",       "#6e4a7e") },
        { ".exs",     ("Elixir",       "#6e4a7e") },
        { ".erl",     ("Erlang",       "#B83998") },
        { ".hrl",     ("Erlang",       "#B83998") },

        // ── TypeScript / JavaScript ──────────────────────────────────────────
        { ".ts",      ("TypeScript",   "#3178c6") },
        { ".tsx",     ("TypeScript",   "#3178c6") },
        { ".mts",     ("TypeScript",   "#3178c6") },
        { ".cts",     ("TypeScript",   "#3178c6") },
        { ".js",      ("JavaScript",   "#f1e05a") },
        { ".jsx",     ("JavaScript",   "#f1e05a") },
        { ".mjs",     ("JavaScript",   "#f1e05a") },
        { ".cjs",     ("JavaScript",   "#f1e05a") },
        { ".coffee",  ("CoffeeScript", "#244776") },

        // ── Frontend Frameworks ──────────────────────────────────────────────
        { ".svelte",  ("Svelte",       "#ff3e00") },
        { ".vue",     ("Vue",          "#41b883") },
        { ".astro",   ("Astro",        "#ff5a03") },
        { ".dart",    ("Dart",         "#00B4AB") },
        { ".hx",      ("Haxe",         "#df7900") },

        // ── Shell / Scripting ─────────────────────────────────────────────────
        { ".sh",      ("Shell",        "#89e051") },
        { ".bash",    ("Shell",        "#89e051") },
        { ".zsh",     ("Shell",        "#89e051") },
        { ".fish",    ("Shell",        "#89e051") },
        { ".ps1",     ("PowerShell",   "#012456") },
        { ".psm1",    ("PowerShell",   "#012456") },
        { ".bat",     ("Batch",        "#C1F12E") },
        { ".cmd",     ("Batch",        "#C1F12E") },
        { ".hack",    ("Hack",         "#878787") },
        { ".hh",      ("Hack",         "#878787") },

        // ── Markup / Data Formats ─────────────────────────────────────────────
        { ".html",    ("HTML",         "#e34c26") },
        { ".htm",     ("HTML",         "#e34c26") },
        { ".xml",     ("XML",          "#0060ac") },
        { ".xsl",     ("XML",          "#0060ac") },
        { ".xslt",    ("XML",          "#0060ac") },
        { ".json",    ("JSON",         "#292929") },
        { ".jsonc",   ("JSON",         "#292929") },
        { ".yaml",    ("YAML",         "#cb171e") },
        { ".yml",     ("YAML",         "#cb171e") },
        { ".toml",    ("TOML",         "#9c4221") },
        { ".ini",     ("INI",          "#d1dbe0") },
        { ".md",      ("Markdown",     "#083fa1") },
        { ".mdx",     ("Markdown",     "#083fa1") },
        { ".rst",     ("reStructuredText", "#141414") },
        { ".tex",     ("TeX",          "#3D6117") },
        { ".sql",     ("SQL",          "#e38c00") },

        // ── Styling ───────────────────────────────────────────────────────────
        { ".css",     ("CSS",          "#563d7c") },
        { ".scss",    ("SCSS",         "#c6538c") },
        { ".sass",    ("Sass",         "#a53b70") },
        { ".less",    ("Less",         "#1d365d") },
        { ".styl",    ("Stylus",       "#ff6347") },

        // ── API / Schema / Config ─────────────────────────────────────────────
        { ".proto",   ("Protobuf",     "#4a4a4a") },
        { ".graphql", ("GraphQL",      "#e10098") },
        { ".gql",     ("GraphQL",      "#e10098") },
        { ".tf",      ("Terraform",    "#7B42BC") },
        { ".tfvars",  ("Terraform",    "#7B42BC") },

        // ── WebAssembly ────────────────────────────────────────────────────────
        { ".wasm",    ("WebAssembly",  "#654ff0") },
        { ".wat",     ("WebAssembly",  "#654ff0") },

        // ── Infrastructure / Container ─────────────────────────────────────────
        { ".dockerfile", ("Dockerfile","#384d54") },
    };

    /// <summary>Returns the language display name for a file extension, or <c>null</c> if unknown.</summary>
    public static string? GetLanguage(string extension)
        => ExtensionMap.TryGetValue(extension, out var info) ? info.Name : null;

    /// <summary>
    /// Returns the language name for a file, falling back to a special-filename lookup when the
    /// extension lookup fails. Handles convention-based files like Dockerfile, Makefile, Gemfile, etc.
    /// </summary>
    public static string? GetLanguageForFile(string extension, string fileName)
    {
        if (ExtensionMap.TryGetValue(extension, out var info)) return info.Name;
        // Filenames without a meaningful extension — match by lowercased base name.
        return fileName.ToLowerInvariant() switch
        {
            "dockerfile"  => "Dockerfile",
            "makefile"    => "Makefile",
            "gemfile"     => "Ruby",
            "rakefile"    => "Ruby",
            "podfile"     => "Ruby",
            "vagrantfile" => "Ruby",
            "pipfile"     => "Python",
            "brewfile"    => "Ruby",
            _             => null
        };
    }

    /// <summary>Returns the GitHub Linguist hex colour for an extension, defaulting to grey.</summary>
    public static string GetColor(string extension)
        => ExtensionMap.TryGetValue(extension, out var info) ? info.Color : "#8b949e";

    /// <summary>
    /// Aggregates language statistics across all scanned files.
    /// Returns languages ordered by file count, with percentage of total file count.
    /// </summary>
    public static List<LanguageInfo> Detect(List<Models.FileNode> files)
    {
        var groups = files
            .Where(f => !f.IsDirectory && f.Language != null)
            .GroupBy(f => f.Language!)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Size = g.Sum(f => f.SizeBytes),
                Color = GetColor(g.First().Extension)
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        var total = groups.Sum(g => g.Count);
        if (total == 0) return [];

        return groups.Select(g => new LanguageInfo
        {
            Name = g.Name,
            FileCount = g.Count,
            SizeBytes = g.Size,
            Percentage = Math.Round((double)g.Count / total * 100, 1),
            Color = g.Color
        }).ToList();
    }
}
