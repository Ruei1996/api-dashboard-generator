using Newtonsoft.Json;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Generators;

public class JsonMetadataGenerator
{
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
                repoPath = data.Meta.RepoPath
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
