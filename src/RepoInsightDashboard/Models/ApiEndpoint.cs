// ============================================================
// ApiEndpoint.cs — Data models for a single discovered API endpoint
// ============================================================
// Architecture: plain data classes (no logic); populated by SwaggerAnalyzer
//   (OpenAPI / GraphQL / gRPC parsers) and consumed by HtmlDashboardGenerator,
//   ApiTraceAnalyzer, and CopilotSemanticAnalyzer.
//
// Design note: classes rather than records are used so that Newtonsoft.Json
//   can deserialise them without a parameterised constructor, and the JS client
//   receives camelCase property names via CamelCasePropertyNamesContractResolver.
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Represents a single API operation discovered from an OpenAPI/Swagger spec,
/// a GraphQL schema, or a gRPC Protocol Buffer definition.
/// </summary>
/// <remarks>
/// GraphQL and gRPC operations use synthetic HTTP method values
/// (QUERY, MUTATION, SUBSCRIPTION, RPC, STREAM) so all three formats
/// can be displayed uniformly in the dashboard without special-casing at the
/// rendering layer.
/// </remarks>
public class ApiEndpoint
{
    /// <summary>
    /// HTTP verb or synthetic operation type.
    /// Standard values: GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS.
    /// Synthetic values: QUERY, MUTATION, SUBSCRIPTION (GraphQL); RPC, STREAM (gRPC).
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// URL path template as declared in the spec (e.g. <c>/v1/users/{id}</c>).
    /// For GraphQL fields the path is <c>/graphql#OperationType.FieldName</c>.
    /// For gRPC methods the path is <c>/ServiceName/MethodName</c>.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>One-line human-readable description of what the operation does.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Extended Markdown-formatted description; <c>null</c> when the spec omits it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// OpenAPI tag group name (e.g. "users", "orders").
    /// Used by the dashboard to group and filter endpoints by functional area.
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Unique operation identifier from the spec.  Used by <see cref="Analyzers.ApiTraceAnalyzer"/>
    /// as the first candidate when searching for the handler function in source files.
    /// </summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Query, path, header, and cookie parameters declared for this operation.</summary>
    public List<ApiParameter> Parameters { get; set; } = [];

    /// <summary>HTTP response definitions, one per declared status code (e.g. "200", "404", "default").</summary>
    public List<ApiResponse> Responses { get; set; } = [];

    /// <summary>Request body schema; <c>null</c> for GET/DELETE or operations with no payload.</summary>
    public ApiRequestBody? RequestBody { get; set; }

    /// <summary>
    /// When <c>true</c>, the endpoint is marked deprecated in the spec.
    /// Deprecated endpoints are rendered with reduced opacity and a "棄用" badge in the dashboard.
    /// </summary>
    public bool IsDeprecated { get; set; }

    /// <summary>Relative path (from repository root) of the spec file that defined this endpoint.</summary>
    public string SourceFile { get; set; } = string.Empty;
}

/// <summary>
/// Describes a single parameter declared for an API operation.
/// Maps directly to an OpenAPI Parameter Object.
/// </summary>
public class ApiParameter
{
    /// <summary>Parameter name as declared in the spec (e.g. "userId", "page").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Where the parameter appears: <c>"query"</c>, <c>"path"</c>, <c>"header"</c>, or <c>"cookie"</c>.
    /// Populated from the OpenAPI <c>in</c> field; defaults to <c>"query"</c> when absent.
    /// </summary>
    public string Location { get; set; } = string.Empty; // query, path, header, cookie

    /// <summary>JSON Schema primitive type of the parameter value (e.g. "string", "integer", "boolean").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>When <c>true</c>, clients must supply this parameter; omitting it is an API contract violation.</summary>
    public bool Required { get; set; }

    /// <summary>Optional human-readable description of the parameter's purpose or accepted values.</summary>
    public string? Description { get; set; }

    /// <summary>Optional example value shown in the dashboard parameter detail panel.</summary>
    public string? Example { get; set; }
}

/// <summary>
/// Describes one of the possible HTTP responses for an API operation.
/// Maps directly to an OpenAPI Response Object.
/// </summary>
public class ApiResponse
{
    /// <summary>HTTP status code string as declared in the spec (e.g. "200", "404", "default").</summary>
    public string StatusCode { get; set; } = string.Empty;

    /// <summary>Human-readable description of when this response is returned.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>MIME type of the response body (e.g. "application/json"); <c>null</c> when the response has no body.</summary>
    public string? ContentType { get; set; }

    /// <summary>Top-level JSON Schema type of the response body: "object", "array", "string", etc.</summary>
    public string? Schema { get; set; }      // top-level type (object/array/string/…)

    /// <summary>Full response schema serialised as a JSON Schema string for display in the detail view.</summary>
    public string? SchemaJson { get; set; }  // full schema serialized as JSON (JSON Schema format)

    /// <summary>
    /// A generated example JSON instance produced by <see cref="Analyzers.SwaggerAnalyzer"/>
    /// for quick reference in the dashboard endpoint detail panel.
    /// </summary>
    public string? ExampleJson { get; set; } // generated example instance for display
}

/// <summary>
/// Describes the request body schema for an API operation that accepts a body payload
/// (typically POST, PUT, PATCH).
/// </summary>
public class ApiRequestBody
{
    /// <summary>Optional description of what the request body should contain.</summary>
    public string? Description { get; set; }

    /// <summary>When <c>true</c>, the request body must be provided by the client.</summary>
    public bool Required { get; set; }

    /// <summary>MIME type of the expected body (e.g. "application/json", "multipart/form-data").</summary>
    public string? ContentType { get; set; }

    /// <summary>JSON Schema type of the top-level body schema ("object", "array", etc.).</summary>
    public string? Schema { get; set; }
}
