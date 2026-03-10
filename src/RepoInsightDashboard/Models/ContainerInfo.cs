namespace RepoInsightDashboard.Models;

public class ContainerInfo
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string? BuildContext { get; set; }
    public string? DockerfilePath { get; set; }
    public List<PortMapping> Ports { get; set; } = [];
    public List<EnvVariable> EnvVariables { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public List<string> Volumes { get; set; } = [];
    public List<string> Networks { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = [];
    public string? Command { get; set; }
    public string? HealthCheck { get; set; }
    public string? RestartPolicy { get; set; }
}

public class PortMapping
{
    public int HostPort { get; set; }
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string? Description { get; set; }
}

public class DockerfileInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string BaseImage { get; set; } = string.Empty;
    public List<int> ExposedPorts { get; set; } = [];
    public List<EnvVariable> EnvVars { get; set; } = [];
    public string? EntryPoint { get; set; }
    public string? Cmd { get; set; }
    public string? WorkDir { get; set; }
    public List<string> Stages { get; set; } = []; // multi-stage
}
