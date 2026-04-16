// ============================================================
// JsonMetadataGenerator.cs — Machine-readable JSON export of analysis results
// ============================================================
// Architecture: stateless generator; takes a fully-populated DashboardData and
//   produces a compact JSON file suitable for CI badge generators, custom tooling,
//   or archival.
//
// Security:
//   Sensitive environment-variable values are projected to "***" before
//   serialisation — the raw masked value "***masked***" from EnvFileAnalyzer is
//   further shortened for compactness.  The full host path (Meta.RepoPath) is
//   replaced with its basename (directory name only) to avoid leaking machine
//   filesystem layout in shared reports.
//
// Usage:
//   var gen  = new JsonMetadataGenerator();
//   var json = gen.Generate(dashboardData);
//   File.WriteAllText("metadata.json", json, Encoding.UTF8);
// ============================================================

using Newtonsoft.Json;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Generators;

/// <summary>
/// Serialises a <see cref="DashboardData"/> snapshot into a compact, indented JSON file
/// that third-party tools (CI badges, custom dashboards, scripts) can consume directly.
/// </summary>
/// <remarks>
/// <para>
/// The output schema is intentionally a subset of <see cref="DashboardData"/>: only the
/// fields that are stable and machine-useful are included.  Private internal fields
/// (e.g. <c>AbsolutePath</c>, raw file trees) are omitted to keep the file small and
/// avoid leaking host filesystem information.
/// </para>
/// <para>
/// Sensitive environment-variable values are masked to <c>"***"</c> (three asterisks)
/// before serialisation, matching the masking already applied by
/// <see cref="Analyzers.EnvFileAnalyzer"/>.
/// </para>
/// </remarks>
public class JsonMetadataGenerator
{
    /// <summary>
    /// Generates a JSON string from the supplied <see cref="DashboardData"/> snapshot.
    /// </summary>
    /// <param name="data">
    /// Fully-populated dashboard data produced by <see cref="Services.AnalysisOrchestrator"/>.
    /// The object is read-only from the generator's perspective; no mutations are made.
    /// </param>
    /// <returns>
    /// A pretty-printed (indented) JSON string with <c>null</c> values omitted.
    /// The top-level keys are: <c>meta</c>, <c>languages</c>, <c>dependencies</c>,
    /// <c>apiEndpoints</c>, <c>apiTraces</c>, <c>containers</c>, <c>envVariables</c>,
    /// <c>copilotSummary</c>, <c>designPatterns</c>, <c>securityRisks</c>,
    /// <c>startupSequence</c>, and <c>dockerfile</c>.
    /// </returns>
    public string Generate(DashboardData data)
    {
        var metadata = new
        {
            meta = new
            {
                generatedAt = data.Meta.GeneratedAt.ToString("O"),
                toolVersion = data.Meta.ToolVersion,
                projectName = data.Meta.ProjectName,
                branch = data.Meta.Branch,
                // Only the directory basename is included — not the full host path —
                // to avoid leaking machine filesystem layout in shared reports.
                repoPath = Path.GetFileName(data.Meta.RepoPath)
            },
            languages = data.Project.Languages.Select(l => new
            {
                name = l.Name,
                fileCount = l.FileCount,
                percentage = l.Percentage,
                color = l.Color
            }),
            dependencies = data.Packages.Select(p => new
            {
                name = p.Name,
                version = p.Version,
                type = p.Type,
                ecosystem = p.Ecosystem,
                sourceFile = p.SourceFile
            }),
            apiEndpoints = data.ApiEndpoints.Select(e => new
            {
                method = e.Method,
                path = e.Path,
                summary = e.Summary,
                description = e.Description,
                tag = e.Tag,
                operationId = e.OperationId,
                isDeprecated = e.IsDeprecated,
                parameters = e.Parameters.Select(p => new
                {
                    name = p.Name,
                    location = p.Location,
                    type = p.Type,
                    required = p.Required,
                    description = p.Description
                }),
                responses = e.Responses.Select(r => new
                {
                    statusCode = r.StatusCode,
                    description = r.Description
                })
            }),
            apiTraces = data.ApiTraces.Select(t => new
            {
                method = t.Method,
                path = t.Path,
                handlerFile = t.HandlerFile,
                handlerFunction = t.HandlerFunction,
                steps = t.Steps.Select(s => new
                {
                    order = s.Order,
                    layer = s.Layer,
                    file = s.File,
                    function = s.Function,
                    description = s.Description,
                    startLine = s.StartLine,
                    endLine = s.EndLine,
                    calledFunctions = s.CalledFunctions
                }),
                sqlQueries = t.SqlQueries.Select(q => new
                {
                    name = q.Name,
                    operation = q.Operation,
                    rawSql = q.RawSql,
                    composedSql = q.ComposedSql,
                    sourceFile = q.SourceFile,
                    functionName = q.FunctionName,
                    parameters = q.Parameters.Select(p => new
                    {
                        name = p.Name,
                        type = p.Type,
                        placeholder = p.Placeholder
                    })
                })
            }),
            containers = data.Containers.Select(c => new
            {
                name = c.Name,
                image = c.Image,
                buildContext = c.BuildContext,
                ports = c.Ports.Select(p => new
                {
                    hostPort = p.HostPort,
                    containerPort = p.ContainerPort,
                    protocol = p.Protocol
                }),
                envVars = c.EnvVariables.Select(e => new
                {
                    key = e.Key,
                    value = e.IsSensitive ? "***" : e.Value,
                    masked = e.IsSensitive
                }),
                dependsOn = c.DependsOn
            }),
            envVariables = data.EnvVariables.Select(e => new
            {
                key = e.Key,
                value = e.IsSensitive ? "***" : e.Value,
                masked = e.IsSensitive,
                sourceFile = e.SourceFile
            }),
            copilotSummary = data.CopilotSummary,
            designPatterns = data.DesignPatterns,
            securityRisks = data.SecurityRisks.Select(r => new
            {
                level = r.Level,
                title = r.Title,
                description = r.Description,
                filePath = r.FilePath
            }),
            startupSequence = data.StartupSequence,
            dockerfile = data.Dockerfile == null ? null : new
            {
                baseImage = data.Dockerfile.BaseImage,
                exposedPorts = data.Dockerfile.ExposedPorts,
                stages = data.Dockerfile.Stages,
                workDir = data.Dockerfile.WorkDir
            }
        };

        return JsonConvert.SerializeObject(metadata, Formatting.Indented,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    }
}
