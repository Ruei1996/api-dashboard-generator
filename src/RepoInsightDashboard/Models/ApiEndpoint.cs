namespace RepoInsightDashboard.Models;

public class ApiEndpoint
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Tag { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public List<ApiParameter> Parameters { get; set; } = [];
    public List<ApiResponse> Responses { get; set; } = [];
    public ApiRequestBody? RequestBody { get; set; }
    public bool IsDeprecated { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}

public class ApiParameter
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty; // query, path, header, cookie
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Description { get; set; }
    public string? Example { get; set; }
}

public class ApiResponse
{
    public string StatusCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Schema { get; set; }
}

public class ApiRequestBody
{
    public string? Description { get; set; }
    public bool Required { get; set; }
    public string? ContentType { get; set; }
    public string? Schema { get; set; }
}
