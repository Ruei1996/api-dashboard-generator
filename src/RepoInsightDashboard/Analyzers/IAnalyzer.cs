namespace RepoInsightDashboard.Analyzers;

public interface IAnalyzer<TResult>
{
    Task<TResult> AnalyzeAsync(string repoPath, CancellationToken cancellationToken = default);
}
