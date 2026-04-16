// ============================================================
// ContainerInfo.cs — Docker container and Dockerfile data models
// ============================================================
// Architecture: plain data classes (no logic); populated by DockerAnalyzer
//   and serialised into window.__RID_DATA__.containers by HtmlDashboardGenerator.
//
// ContainerInfo    — one docker-compose service (or synthesised from a Dockerfile)
// PortMapping      — host:container port pair with protocol and optional description
// DockerfileInfo   — parsed Dockerfile: base image, stages, ports, ENV vars, entry point
// ============================================================

namespace RepoInsightDashboard.Models;

/// <summary>
/// Represents a single container service discovered from a docker-compose file,
/// or synthesised from a Dockerfile when no compose file is present.
/// </summary>
/// <remarks>
/// ENV values whose keys contain sensitive keywords are masked to <c>"***masked***"</c>
/// by <see cref="Analyzers.DockerAnalyzer"/> before populating this object
/// (OWASP A3:2021 — Sensitive Data Exposure).
/// </remarks>
public class ContainerInfo
{
    /// <summary>Service name as declared under the top-level <c>services</c> key in docker-compose.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Docker image reference (e.g. <c>"postgres:15"</c>, <c>"build:myapp"</c>).</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Build context path when the service uses a local Dockerfile instead of a published image.</summary>
    public string? BuildContext { get; set; }

    /// <summary>Path to the Dockerfile, relative to <see cref="BuildContext"/>.  <c>null</c> when using the default location.</summary>
    public string? DockerfilePath { get; set; }

    /// <summary>All host→container port mappings declared under <c>ports:</c>.</summary>
    public List<PortMapping> Ports { get; set; } = [];

    /// <summary>Environment variables declared under <c>environment:</c> with sensitive values already masked.</summary>
    public List<EnvVariable> EnvVariables { get; set; } = [];

    /// <summary>Service names listed under <c>depends_on:</c>; used by <see cref="Services.AnalysisOrchestrator.BuildStartupSequence"/>.</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>Volume mount declarations from the compose file (stored as raw strings for display purposes).</summary>
    public List<string> Volumes { get; set; } = [];

    /// <summary>Named networks this service is attached to.</summary>
    public List<string> Networks { get; set; } = [];

    /// <summary>Arbitrary label key-value pairs from the <c>labels:</c> block.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>Override command for the container entrypoint; <c>null</c> when the image default is used.</summary>
    public string? Command { get; set; }

    /// <summary>Healthcheck command string; <c>null</c> when no healthcheck is declared.</summary>
    public string? HealthCheck { get; set; }

    /// <summary>Restart policy (e.g. <c>"always"</c>, <c>"on-failure"</c>, <c>"unless-stopped"</c>).</summary>
    public string? RestartPolicy { get; set; }
}

/// <summary>
/// Describes a single port mapping between the host and the container.
/// </summary>
public class PortMapping
{
    /// <summary>Port on the host machine that is bound to the container port.</summary>
    public int HostPort { get; set; }

    /// <summary>Port inside the container that the service listens on.</summary>
    public int ContainerPort { get; set; }

    /// <summary>Network protocol: <c>"tcp"</c> (default), <c>"udp"</c>, or <c>"grpc"</c> (synthesised).</summary>
    public string Protocol { get; set; } = "tcp";

    /// <summary>Human-readable description synthesised from the port number (e.g. <c>"PostgreSQL"</c>, <c>"HTTPS"</c>).</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Holds metadata parsed from a <c>Dockerfile</c> in the repository.
/// Used both for direct rendering and as input to
/// <see cref="Analyzers.DockerAnalyzer.SynthesizeContainersFromDockerfile"/> when no
/// docker-compose file is present.
/// </summary>
public class DockerfileInfo
{
    /// <summary>Repository-relative path to the Dockerfile (e.g. <c>"Dockerfile"</c>).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Image name from the first <c>FROM</c> instruction (e.g. <c>"golang:1.22-alpine"</c>).</summary>
    public string BaseImage { get; set; } = string.Empty;

    /// <summary>Port numbers listed in <c>EXPOSE</c> instructions.</summary>
    public List<int> ExposedPorts { get; set; } = [];

    /// <summary>Environment variables declared via <c>ENV</c> instructions; sensitive values are masked.</summary>
    public List<EnvVariable> EnvVars { get; set; } = [];

    /// <summary>Entry point command from the <c>ENTRYPOINT</c> instruction; <c>null</c> when absent.</summary>
    public string? EntryPoint { get; set; }

    /// <summary>Default command from the <c>CMD</c> instruction; <c>null</c> when absent.</summary>
    public string? Cmd { get; set; }

    /// <summary>Working directory set via <c>WORKDIR</c>; <c>null</c> when absent.</summary>
    public string? WorkDir { get; set; }

    /// <summary>Named stages from multi-stage builds (the <c>AS name</c> suffix of each <c>FROM</c> instruction).</summary>
    public List<string> Stages { get; set; } = []; // multi-stage
}
