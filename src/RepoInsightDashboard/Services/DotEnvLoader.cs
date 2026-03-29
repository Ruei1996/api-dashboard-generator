using System.Diagnostics;

namespace RepoInsightDashboard.Services;

/// <summary>
/// Lightweight .env file loader for the <c>rid</c> CLI tool.
/// </summary>
/// <remarks>
/// <para>
/// Searches for a <c>.env</c> file in the following order and loads the <b>first</b> one found:
/// <list type="number">
///   <item>Current working directory</item>
///   <item><c>~/.config/rid/.env</c> (user-level config, XDG Base Dir spec)</item>
/// </list>
/// </para>
/// <para>
/// <b>Priority rule</b>: variables already present in the process environment are <em>never</em>
/// overwritten — even if they are set to an empty string.  This means a shell export such as
/// <c>export GITHUB_COPILOT_TOKEN=</c> (intentional suppression) will not be silently restored
/// from the .env file.  Real OS environment variables and CI secrets always win.
/// </para>
/// <para>
/// <b>Idempotent</b>: calling <see cref="Load"/> more than once returns the cached result
/// without re-reading disk, which is safe for test harnesses.
/// </para>
/// <para>
/// <b>Syntax supported</b>:
/// <list type="bullet">
///   <item><c>KEY=value</c></item>
///   <item><c>KEY = value</c> — spaces around <c>=</c> are stripped</item>
///   <item><c>KEY="value with spaces"</c> — double-quoted values (quotes are stripped)</item>
///   <item><c>KEY='value'</c> — single-quoted values (quotes are stripped)</item>
///   <item><c># comment lines</c> — ignored</item>
///   <item>Blank lines — ignored</item>
/// </list>
/// Inline comments (<c>KEY=value # comment</c>) are <em>not</em> stripped to preserve values
/// that legitimately contain a <c>#</c> character (e.g. hex colours, GitHub tokens).
/// </para>
/// </remarks>
public static class DotEnvLoader
{
    // Allowlist of keys this tool is permitted to read from a .env file.
    // Any key not in this set is silently skipped, even if present in the file.
    // This closes the CWD-based proxy-injection attack vector (CWE-426):
    // a malicious .env planted in a shared/writable directory can only influence
    // the two variables the tool actually uses, not arbitrary process env vars
    // (e.g., PATH, DYLD_LIBRARY_PATH, JAVA_HOME …).
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "GITHUB_COPILOT_TOKEN",
        "COPILOT_MODEL"
    };
    // Written atomically via Interlocked so parallel test runners stay race-free.
    private static string? _loadedPath;

    // Sentinel value stored in _loadedPath when Load() ran but found no file.
    // This distinguishes "not yet run" (null) from "ran, found nothing" (Sentinel).
    private const string Sentinel = "";

    // Candidate locations searched in order.
    private static IEnumerable<string> CandidatePaths()
    {
        // 1. CWD — most common for local development workflows.
        yield return Path.Combine(Directory.GetCurrentDirectory(), ".env");

        // 2. ~/.config/rid/.env — XDG-style user-level config (survives directory changes).
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                        ?? Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".config");
        yield return Path.Combine(xdgConfig, "rid", ".env");
    }

    /// <summary>
    /// Attempts to locate and load a <c>.env</c> file.
    /// Subsequent calls return the cached result without re-reading disk.
    /// </summary>
    /// <param name="explicitPath">
    /// When non-null, skip the candidate search and load exactly this path instead.
    /// Ignored if a file was already loaded in this process.
    /// </param>
    /// <returns>
    /// The absolute path of the file that was successfully loaded,
    /// or <c>null</c> if no file was found.
    /// </returns>
    public static string? Load(string? explicitPath = null)
    {
        // Idempotency check: if Load() already ran in this process, return the cached result.
        // Volatile.Read prevents the JIT from caching the field value in a register.
        var cached = Volatile.Read(ref _loadedPath);
        if (cached != null)
            return cached == Sentinel ? null : cached;

        var candidates = explicitPath != null
            ? (IEnumerable<string>)[explicitPath]
            : CandidatePaths();

        foreach (var path in candidates)
        {
            try
            {
                // No File.Exists check here — that would be a TOCTOU race: the file could
                // disappear between Exists() and ReadAllLines(). We let FileNotFoundException
                // surface naturally and handle it in the catch below.
                LoadFile(path);

                // Publish the loaded path atomically; a concurrent caller may have beaten us
                // but that's fine — both loaded the same file and the "no overwrite" rule
                // inside LoadFile is idempotent.
                Interlocked.CompareExchange(ref _loadedPath, path, null);
                return path;
            }
            catch (FileNotFoundException)
            {
                // Expected: candidate does not exist — try next.
                Debug.WriteLine($"[DotEnvLoader] Candidate not found, skipping: '{path}'");
            }
            catch (Exception ex)
            {
                // Non-fatal: log what failed and try next candidate.
                // A malformed or unreadable .env must never crash the tool.
                Debug.WriteLine(
                    $"[DotEnvLoader] Failed to read '{path}': {ex.GetType().Name} — {ex.Message}");
            }
        }

        // Record that we ran but found nothing so the next call skips the search.
        Interlocked.CompareExchange(ref _loadedPath, Sentinel, null);
        return null;
    }

    // ─── For unit tests only ──────────────────────────────────────────────────

    /// <summary>
    /// Resets the idempotency guard so a subsequent <see cref="Load"/> call will re-search disk.
    /// <para><b>Never call this in production code.</b> It exists solely to allow test isolation.</para>
    /// </summary>
    internal static void ResetForTesting() =>
        Interlocked.Exchange(ref _loadedPath, null);

    // ─── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="path"/> atomically (all-at-once via <see cref="File.ReadAllLines"/>),
    /// then parses and injects each key-value pair.
    /// </summary>
    /// <remarks>
    /// Using <see cref="File.ReadAllLines"/> instead of <see cref="File.ReadLines"/> ensures that
    /// any <see cref="IOException"/> fires <em>before</em> any <see cref="Environment.SetEnvironmentVariable"/>
    /// call, preventing a partial-state environment if the file becomes unavailable mid-read.
    /// </remarks>
    private static void LoadFile(string path)
    {
        // ReadAllLines materialises the entire file in one syscall.
        // If this throws, no env vars have been modified yet (atomic intent).
        var lines = File.ReadAllLines(path);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip blank lines and full-line comments.
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0) continue; // No '=' found, or key portion is empty — malformed, skip.

            var key   = line[..eqIdx].Trim();
            var value = line[(eqIdx + 1)..].Trim(); // Trim strips accidental whitespace (e.g. KEY = value)

            // Strip optional surrounding quotes (single or double).
            if (value.Length >= 2
                && ((value[0] == '"'  && value[^1] == '"')
                 || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Security: only inject variables from the explicit allowlist (CWE-426 / CWE-20).
            // A .env planted in a shared/writable directory cannot influence PATH, LD_PRELOAD,
            // JAVA_HOME, or any other variable the tool does not need.
            if (!AllowedKeys.Contains(key)) continue;

            // Priority rule: only inject the value if the variable is ABSENT from the environment.
            // GetEnvironmentVariable returns null for absent vars and "" for present-but-empty.
            // Checking `is null` (not IsNullOrEmpty) correctly honours an intentional empty export:
            //   export GITHUB_COPILOT_TOKEN=   → env var is "", NOT null → we skip it.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
