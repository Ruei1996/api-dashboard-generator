// ============================================================
// SwaggerAnalyzer.cs — Multi-format API specification parser
// ============================================================
// Architecture: stateless service class; instantiated once per analyze run.
// Supported formats:
//   1. OpenAPI / Swagger (JSON + YAML) — parsed via Microsoft.OpenApi.Readers
//   2. GraphQL schemas (.graphql / .gql) — regex-based field extraction
//   3. gRPC Protocol Buffers (.proto) — regex-based rpc method extraction
//
// Usage:
//   var analyzer = new SwaggerAnalyzer();
//   List<ApiEndpoint> endpoints = analyzer.Analyze(allFiles);
// ============================================================

using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Newtonsoft.Json.Linq;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Discovers and parses API specification files in three formats:
/// <list type="bullet">
///   <item>OpenAPI / Swagger (JSON + YAML) — uses <see cref="Microsoft.OpenApi.Readers"/></item>
///   <item>GraphQL schemas (.graphql / .gql) — extracts Query/Mutation/Subscription fields</item>
///   <item>gRPC Protocol Buffers (.proto) — extracts service RPC methods</item>
/// </list>
/// Files larger than 10 MB are skipped to bound memory use.
/// </summary>
public class SwaggerAnalyzer
{
    // Maximum file size (bytes) for OpenAPI / GraphQL / proto parsing — 10 MB guard.
    // Extremely large spec files are almost always auto-generated bundles (swagger-ui dist)
    // rather than hand-authored API definitions; parsing them wastes memory and CPU.
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Scans <paramref name="files"/> for OpenAPI, GraphQL, and gRPC spec files and returns
    /// a unified list of <see cref="ApiEndpoint"/> objects discovered across all formats.
    /// </summary>
    /// <param name="files">
    /// Flat list of <see cref="FileNode"/> objects produced by <see cref="FileScanner"/>.
    /// Directory nodes and files over 10 MB are silently skipped.
    /// </param>
    /// <returns>
    /// Combined list of <see cref="ApiEndpoint"/> objects from all three parsers.
    /// GraphQL fields and gRPC RPC methods are surfaced as pseudo-endpoints with
    /// synthetic HTTP methods (QUERY, MUTATION, SUBSCRIPTION, RPC, STREAM) to allow
    /// uniform display in the dashboard.
    /// </returns>
    /// <remarks>
    /// Parse failures for individual files are silently swallowed so that one malformed
    /// spec does not abort the entire analysis.  Errors are not logged because doing so
    /// would pollute the progress output for repositories with many non-spec JSON files.
    /// </remarks>
    public List<ApiEndpoint> Analyze(List<FileNode> files)
    {
        var endpoints = new List<ApiEndpoint>();

        // 1. OpenAPI / Swagger documents — uses the official Microsoft.OpenApi parser which
        //    handles both JSON and YAML and resolves $ref pointers within the same document.
        foreach (var file in files.Where(f => !f.IsDirectory && IsSwaggerFile(f) && f.SizeBytes < MaxFileSizeBytes))
        {
            try
            {
                using var stream = File.OpenRead(file.AbsolutePath);
                var reader = new OpenApiStreamReader();
                var doc = reader.Read(stream, out _);

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
                                Required    = operation.RequestBody.Required,
                                ContentType = firstContent?.Key,
                                Schema      = firstContent?.Value?.Schema?.Type
                            };
                        }

                        endpoints.Add(endpoint);
                    }
                }
            }
            catch { /* skip malformed files */ }
        }

        // 2. GraphQL schema files — synthesise pseudo-endpoints for each Query/Mutation/Subscription field.
        //    GraphQL does not have HTTP verbs, so we map operation types to synthetic method names
        //    (QUERY, MUTATION, SUBSCRIPTION) that the dashboard renders with appropriate icons.
        foreach (var file in files.Where(f => !f.IsDirectory && IsGraphQlFile(f) && f.SizeBytes < MaxFileSizeBytes))
        {
            try { endpoints.AddRange(ParseGraphQlSchema(file)); }
            catch { }
        }

        // 3. gRPC proto files — synthesise pseudo-endpoints for each rpc method.
        //    RPC and streaming methods are surfaced as "RPC" and "STREAM" method types respectively.
        foreach (var file in files.Where(f => !f.IsDirectory && f.Extension == ".proto" && f.SizeBytes < MaxFileSizeBytes))
        {
            try { endpoints.AddRange(ParseProtoFile(file)); }
            catch { }
        }

        return endpoints;
    }

    // ── File-type Detectors ───────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if <paramref name="file"/> is likely an OpenAPI/Swagger spec file.
    /// </summary>
    /// <remarks>
    /// The extension whitelist (.json, .yaml, .yml) is intentional: it prevents the analyser
    /// from wasting CPU trying to parse swagger-ui bundle scripts (.js), README files (.md),
    /// or generator config files (.sh) as API specs.  Name-based matching then narrows to
    /// files that are semantically API descriptions.
    /// </remarks>
    private static bool IsSwaggerFile(FileNode file)
    {
        // Restrict to structured data formats only — prevents wasting CPU trying to parse
        // swagger-ui.bundle.js or openapi-generator-config.sh as API specs (code-review #9).
        var ext  = file.Extension.ToLowerInvariant();
        if (ext is not (".json" or ".yaml" or ".yml")) return false;
        var name = file.Name.ToLowerInvariant();
        return name.Contains("swagger") || name.Contains("openapi")
            || name is "api.json" or "api.yaml" or "api.yml" or "service.swagger.json";
    }

    private static bool IsGraphQlFile(FileNode file) =>
        file.Extension is ".graphql" or ".gql";

    // ── GraphQL Parser ────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts Query, Mutation, and Subscription fields from a GraphQL schema file
    /// and returns them as synthetic <see cref="ApiEndpoint"/> objects.
    /// </summary>
    /// <param name="file">The .graphql or .gql file to parse.</param>
    /// <returns>One <see cref="ApiEndpoint"/> per discovered field.</returns>
    /// <remarks>
    /// Uses regex rather than a full GraphQL parser to avoid the dependency on an
    /// external GraphQL library.  The regex handles <c>type</c> and <c>extend type</c>
    /// declarations and captures the field name and return type from each operation block.
    /// Inline argument definitions — <c>fieldName(arg: Type): ReturnType</c> — are skipped
    /// by the non-capturing group <c>(?:\([^)]*\))?</c>.
    /// </remarks>
    private static List<ApiEndpoint> ParseGraphQlSchema(FileNode file)
    {
        var endpoints = new List<ApiEndpoint>();
        var content = File.ReadAllText(file.AbsolutePath);

        // Outer regex: captures the operation type (Query/Mutation/Subscription) and
        // the entire body of the type block.  RegexOptions.Singleline is required so
        // '.' matches newlines inside the block body.
        foreach (Match block in Regex.Matches(content,
            @"(?:type|extend\s+type)\s+(Query|Mutation|Subscription)\s*(?:implements[^{]*)?\{([^}]+)\}",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var operationType = block.Groups[1].Value; // Query / Mutation / Subscription
            var httpMethod = operationType switch
            {
                "Query"        => "QUERY",
                "Mutation"     => "MUTATION",
                "Subscription" => "SUBSCRIPTION",
                _              => "QUERY"
            };

            // Inner regex: each field inside the block — fieldName(args): ReturnType.
            // Group 1 = field name, Group 2 = return type (may include ! and []).
            foreach (Match field in Regex.Matches(block.Groups[2].Value,
                @"(\w+)\s*(?:\([^)]*\))?\s*:\s*([\w!\[\]]+)"))
            {
                endpoints.Add(new ApiEndpoint
                {
                    Method = httpMethod,
                    Path = $"/graphql#{operationType}.{field.Groups[1].Value}",
                    Summary = $"GraphQL {operationType}: {field.Groups[1].Value}",
                    Description = $"Returns {field.Groups[2].Value}",
                    OperationId = field.Groups[1].Value,
                    Tag = operationType,
                    SourceFile = file.RelativePath
                });
            }
        }

        return endpoints;
    }

    // ── gRPC Proto Parser ─────────────────────────────────────────────────────

    /// <summary>
    /// Extracts RPC method definitions from a Protocol Buffer (.proto) file and returns
    /// them as synthetic <see cref="ApiEndpoint"/> objects.
    /// </summary>
    /// <param name="file">The .proto file to parse.</param>
    /// <returns>One <see cref="ApiEndpoint"/> per discovered RPC method.</returns>
    /// <remarks>
    /// The service-block regex allows one level of nested braces so that rpc option blocks
    /// such as <c>option (google.api.http) = { get: "/v1/..." };</c> do not prematurely
    /// close the service capture group (code-review issue #4).  Streaming RPCs are
    /// identified by the presence of the <c>stream</c> keyword in request or response types.
    /// </remarks>
    private static List<ApiEndpoint> ParseProtoFile(FileNode file)
    {
        var endpoints = new List<ApiEndpoint>();
        var content = File.ReadAllText(file.AbsolutePath);

        // service\s+Name\s+{ … } — the inner group ((?:[^{}]|\{[^{}]*\})*) matches:
        //   [^{}]          plain content (no braces)
        //   \{[^{}]*\}     a single level of nested braces (e.g. option { ... })
        // This prevents option blocks with their own braces from breaking the outer match.
        foreach (Match svc in Regex.Matches(content,
            @"service\s+(\w+)\s*\{((?:[^{}]|\{[^{}]*\})*)\}", RegexOptions.Singleline))
        {
            var serviceName = svc.Groups[1].Value;
            foreach (Match rpc in Regex.Matches(svc.Groups[2].Value,
                @"rpc\s+(\w+)\s*\(([^)]+)\)\s+returns\s*\(([^)]+)\)"))
            {
                var methodName = rpc.Groups[1].Value;
                var requestType = rpc.Groups[2].Value.Trim();
                var responseType = rpc.Groups[3].Value.Trim();
                // Detect server/client/bidirectional streaming by the "stream" keyword prefix.
                var isStreaming = requestType.StartsWith("stream ") || responseType.StartsWith("stream ");
                endpoints.Add(new ApiEndpoint
                {
                    Method = isStreaming ? "STREAM" : "RPC",
                    Path = $"/{serviceName}/{methodName}",
                    Summary = $"gRPC {serviceName}.{methodName}",
                    Description = $"{requestType} → {responseType}",
                    OperationId = $"{serviceName}_{methodName}",
                    Tag = serviceName,
                    SourceFile = file.RelativePath
                });
            }
        }

        return endpoints;
    }

    // ── OpenAPI Schema Serializer ─────────────────────────────────────────────

    /// <summary>
    /// Recursively serialises an OpenAPI schema to a JObject for display.
    /// Depth-limited to 6 levels to avoid infinite recursion on circular refs.
    /// </summary>
    private static JObject? SerializeSchema(OpenApiSchema? schema, int depth = 0)
    {
        if (schema == null || depth > 6) return null;

        if (schema.Reference != null)
            return new JObject { ["$ref"] = schema.Reference.Id };

        var obj = new JObject();

        if (!string.IsNullOrEmpty(schema.Type))        obj["type"]        = schema.Type;
        if (!string.IsNullOrEmpty(schema.Format))      obj["format"]      = schema.Format;
        if (!string.IsNullOrEmpty(schema.Description)) obj["description"] = schema.Description;
        if (schema.Nullable)                           obj["nullable"]    = true;
        if (schema.ReadOnly)                           obj["readOnly"]    = true;
        if (schema.WriteOnly)                          obj["writeOnly"]   = true;

        if (schema.Example != null)
        {
            try { obj["example"] = JToken.Parse(schema.Example.ToString() ?? "null"); }
            catch { obj["example"] = schema.Example.ToString(); }
        }

        if (schema.Enum?.Count > 0)
            obj["enum"] = new JArray(schema.Enum.Select(e => e.ToString()));

        if (schema.Required?.Count > 0)
            obj["required"] = new JArray(schema.Required);

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

        if (schema.Items != null)
        {
            var items = SerializeSchema(schema.Items, depth + 1);
            if (items != null) obj["items"] = items;
        }

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
