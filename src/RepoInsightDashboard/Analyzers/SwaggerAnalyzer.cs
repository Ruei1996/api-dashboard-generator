using Microsoft.OpenApi.Readers;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

public class SwaggerAnalyzer
{
    public List<ApiEndpoint> Analyze(List<FileNode> files)
    {
        var endpoints = new List<ApiEndpoint>();

        var swaggerFiles = files.Where(f => !f.IsDirectory && IsSwaggerFile(f)).ToList();
        foreach (var file in swaggerFiles)
        {
            try
            {
                using var stream = File.OpenRead(file.AbsolutePath);
                var reader = new OpenApiStreamReader();
                var doc = reader.Read(stream, out var diagnostic);

                if (doc?.Paths == null) continue;

                foreach (var (path, pathItem) in doc.Paths)
                {
                    foreach (var (method, operation) in pathItem.Operations)
                    {
                        var endpoint = new ApiEndpoint
                        {
                            Method = method.ToString().ToUpperInvariant(),
                            Path = path,
                            Summary = operation.Summary ?? "",
                            Description = operation.Description,
                            OperationId = operation.OperationId ?? "",
                            IsDeprecated = operation.Deprecated,
                            Tag = operation.Tags?.FirstOrDefault()?.Name,
                            SourceFile = file.RelativePath
                        };

                        foreach (var param in operation.Parameters ?? [])
                        {
                            endpoint.Parameters.Add(new ApiParameter
                            {
                                Name = param.Name,
                                Location = param.In?.ToString()?.ToLowerInvariant() ?? "query",
                                Type = param.Schema?.Type ?? "string",
                                Required = param.Required,
                                Description = param.Description,
                                Example = param.Example?.ToString()
                            });
                        }

                        foreach (var (code, response) in operation.Responses ?? [])
                        {
                            endpoint.Responses.Add(new ApiResponse
                            {
                                StatusCode = code,
                                Description = response.Description ?? "",
                                Schema = response.Content?.FirstOrDefault().Value?.Schema?.Type
                            });
                        }

                        if (operation.RequestBody != null)
                        {
                            var firstContent = operation.RequestBody.Content?.FirstOrDefault();
                            endpoint.RequestBody = new ApiRequestBody
                            {
                                Description = operation.RequestBody.Description,
                                Required = operation.RequestBody.Required,
                                ContentType = firstContent?.Key,
                                Schema = firstContent?.Value?.Schema?.Type
                            };
                        }

                        endpoints.Add(endpoint);
                    }
                }
            }
            catch { /* skip malformed swagger files */ }
        }

        return endpoints;
    }

    private static bool IsSwaggerFile(FileNode file)
    {
        var name = file.Name.ToLowerInvariant();
        return name.Contains("swagger") || name.Contains("openapi")
            || name == "api.json" || name == "api.yaml" || name == "api.yml"
            || name == "service.swagger.json";
    }
}
