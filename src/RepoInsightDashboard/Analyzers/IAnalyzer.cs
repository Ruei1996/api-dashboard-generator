// ============================================================
// IAnalyzer.cs — Core analyzer contract
// ============================================================
// Architecture: generic interface; each analyzer implements this to expose
//   a single async entry point that accepts a repository path and returns
//   a strongly-typed result object.
//
// Usage:
//   public class FooAnalyzer : IAnalyzer<FooResult>
//   {
//       public async Task<FooResult> AnalyzeAsync(string repoPath, CancellationToken ct = default) { … }
//   }
// ============================================================

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Defines the contract for all analyzer implementations in the rid pipeline.
/// Each analyzer is responsible for a single concern (e.g. dependencies, Docker, tests)
/// and returns a strongly-typed result that is aggregated by
/// <see cref="Services.AnalysisOrchestrator"/>.
/// </summary>
/// <typeparam name="TResult">
/// The type of result produced by the analysis, e.g. <c>List&lt;PackageDependency&gt;</c>
/// or <c>TestSuiteInfo</c>.
/// </typeparam>
public interface IAnalyzer<TResult>
{
    /// <summary>
    /// Performs analysis on the repository located at <paramref name="repoPath"/>
    /// and returns the typed result.
    /// </summary>
    /// <param name="repoPath">Absolute path to the repository root directory.</param>
    /// <param name="cancellationToken">
    /// Token used to cancel the operation.  Implementations should pass this to all
    /// async I/O calls so that a 5-minute global timeout in <see cref="Services.AnalysisOrchestrator"/>
    /// propagates correctly.
    /// </param>
    /// <returns>
    /// A <typeparamref name="TResult"/> populated with analysis results.
    /// Implementations should never return <c>null</c> — use an empty collection or
    /// a default-constructed result object instead.
    /// </returns>
    Task<TResult> AnalyzeAsync(string repoPath, CancellationToken cancellationToken = default);
}
