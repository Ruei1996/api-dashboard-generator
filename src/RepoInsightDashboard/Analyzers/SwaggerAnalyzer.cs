using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Newtonsoft.Json.Linq;
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
                            var firstContent = response.Content?.FirstOrDefault();
                            var schema = firstContent?.Value?.Schema;
                            endpoint.Responses.Add(new ApiResponse
                            {
                                StatusCode   = code,
                                Description  = response.Description ?? "",
                                ContentType  = firstContent?.Key,
                                Schema       = schema?.Type,
                                SchemaJson   = SerializeSchema(schema)?.ToString(Newtonsoft.Json.Formatting.Indented)
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

    /// <summary>
    /// Recursively serialize an OpenAPI schema to a JObject for display.
    /// Depth-limited to 6 levels to avoid infinite recursion on circular refs.
    /// </summary>
    private static JObject? SerializeSchema(OpenApiSchema? schema, int depth = 0)
    {
        if (schema == null || depth > 6) return null;

        // $ref short-circuit
        if (schema.Reference != null)
            return new JObject { ["$ref"] = schema.Reference.Id };

        var obj = new JObject();

        if (!string.IsNullOrEmpty(schema.Type))     obj["type"]        = schema.Type;
        if (!string.IsNullOrEmpty(schema.Format))   obj["format"]      = schema.Format;
        if (!string.IsNullOrEmpty(schema.Description)) obj["description"] = schema.Description;
        if (schema.Nullable)                        obj["nullable"]    = true;
        if (schema.ReadOnly)                        obj["readOnly"]    = true;
        if (schema.WriteOnly)                       obj["writeOnly"]   = true;

        // Example value
        if (schema.Example != null)
        {
            try { obj["example"] = JToken.Parse(schema.Example.ToString() ?? "null"); }
            catch { obj["example"] = schema.Example.ToString(); }
        }

        // Enum values
        if (schema.Enum?.Count > 0)
            obj["enum"] = new JArray(schema.Enum.Select(e => e.ToString()));

        // Required fields
        if (schema.Required?.Count > 0)
            obj["required"] = new JArray(schema.Required);

        // Properties (object)
        if (schema.Properties?.Count > 0)
        {
            var props = new JObject();
            foreach (var (propName, propSchema) in schema.Properties)
            {
                var serialized = SerializeSchema(propSchema, depth + 1);
                if (serialized != null) props[propName] = serialized;
            }
            obj["properties"] = props;
        }

        // Items (array)
        if (schema.Items != null)
        {
            var items = SerializeSchema(schema.Items, depth + 1);
            if (items != null) obj["items"] = items;
        }

        // allOf / anyOf / oneOf
        if (schema.AllOf?.Count > 0)
            obj["allOf"] = new JArray(schema.AllOf
                .Select(s => (JToken?)SerializeSchema(s, depth + 1))
                .Where(s => s != null));
        if (schema.AnyOf?.Count > 0)
            obj["anyOf"] = new JArray(schema.AnyOf
                .Select(s => (JToken?)SerializeSchema(s, depth + 1))
                .Where(s => s != null));

        return obj.Count > 0 ? obj : null;
    }
}
