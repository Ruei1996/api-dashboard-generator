using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoInsightDashboard.Analyzers;

public class DockerAnalyzer
{
    public DockerfileInfo? ParseDockerfile(FileNode file)
    {
        if (!File.Exists(file.AbsolutePath)) return null;
        var lines = File.ReadAllLines(file.AbsolutePath);
        var info = new DockerfileInfo { FilePath = file.RelativePath };

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || string.IsNullOrEmpty(trimmed)) continue;

            var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) continue;

            switch (parts[0].ToUpperInvariant())
            {
                case "FROM":
                    var fromParts = parts.Length > 1 ? parts[1].Split(' ') : [];
                    if (fromParts.Length > 0)
                    {
                        if (string.IsNullOrEmpty(info.BaseImage))
                            info.BaseImage = fromParts[0];
                        var asIdx = Array.FindIndex(fromParts, p => p.Equals("AS", StringComparison.OrdinalIgnoreCase));
                        if (asIdx >= 0 && asIdx + 1 < fromParts.Length)
                            info.Stages.Add(fromParts[asIdx + 1]);
                    }
                    break;
                case "EXPOSE":
                    if (parts.Length > 1)
                    {
                        foreach (var portStr in parts[1].Split(' '))
                        {
                            var portNum = portStr.Split('/')[0];
                            if (int.TryParse(portNum, out var port))
                                info.ExposedPorts.Add(port);
                        }
                    }
                    break;
                case "ENV":
                    if (parts.Length > 1)
                    {
                        var envParts = parts[1].Split('=', 2);
                        info.EnvVars.Add(new EnvVariable
                        {
                            Key = envParts[0].Trim(),
                            Value = envParts.Length > 1 ? envParts[1].Trim() : "",
                            SourceFile = file.RelativePath
                        });
                    }
                    break;
                case "ENTRYPOINT":
                    info.EntryPoint = parts.Length > 1 ? parts[1] : null;
                    break;
                case "CMD":
                    info.Cmd = parts.Length > 1 ? parts[1] : null;
                    break;
                case "WORKDIR":
                    info.WorkDir = parts.Length > 1 ? parts[1] : null;
                    break;
            }
        }
        return info;
    }

    public List<ContainerInfo> ParseDockerCompose(FileNode file)
    {
        var containers = new List<ContainerInfo>();
        if (!File.Exists(file.AbsolutePath)) return containers;

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var yaml = File.ReadAllText(file.AbsolutePath);
            var compose = deserializer.Deserialize<Dictionary<string, object>>(yaml);

            if (!compose.TryGetValue("services", out var servicesObj)) return containers;

            var services = servicesObj as Dictionary<object, object> ?? [];

            foreach (var (serviceName, serviceConfig) in services)
            {
                var svc = serviceConfig as Dictionary<object, object> ?? [];
                var container = new ContainerInfo { Name = serviceName.ToString()! };

                if (svc.TryGetValue("image", out var image))
                    container.Image = image?.ToString() ?? "";

                if (svc.TryGetValue("build", out var build))
                {
                    if (build is string buildStr)
                        container.BuildContext = buildStr;
                    else if (build is Dictionary<object, object> buildDict)
                    {
                        if (buildDict.TryGetValue("context", out var ctx))
                            container.BuildContext = ctx?.ToString();
                        if (buildDict.TryGetValue("dockerfile", out var df))
                            container.DockerfilePath = df?.ToString();
                    }
                    if (string.IsNullOrEmpty(container.Image))
                        container.Image = $"build:{container.BuildContext ?? "."}";
                }

                if (svc.TryGetValue("ports", out var portsObj) && portsObj is List<object> portsList)
                {
                    foreach (var portEntry in portsList)
                    {
                        var portStr = portEntry?.ToString() ?? "";
                        var match = Regex.Match(portStr, @"^(?:(\d+):)?(\d+)(?:/(\w+))?$");
                        if (match.Success)
                            container.Ports.Add(new PortMapping
                            {
                                HostPort = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : int.Parse(match.Groups[2].Value),
                                ContainerPort = int.Parse(match.Groups[2].Value),
                                Protocol = match.Groups[3].Success ? match.Groups[3].Value : "tcp"
                            });
                    }
                }

                if (svc.TryGetValue("environment", out var envObj))
                {
                    if (envObj is List<object> envList)
                    {
                        foreach (var e in envList)
                        {
                            var parts = e?.ToString()?.Split('=', 2) ?? [];
                            if (parts.Length >= 1)
                                container.EnvVariables.Add(new EnvVariable
                                {
                                    Key = parts[0],
                                    Value = parts.Length > 1 ? parts[1] : "",
                                    SourceFile = file.RelativePath
                                });
                        }
                    }
                    else if (envObj is Dictionary<object, object> envDict)
                    {
                        foreach (var (k, v) in envDict)
                            container.EnvVariables.Add(new EnvVariable
                            {
                                Key = k.ToString()!,
                                Value = v?.ToString() ?? "",
                                SourceFile = file.RelativePath
                            });
                    }
                }

                if (svc.TryGetValue("depends_on", out var depsObj))
                {
                    if (depsObj is List<object> depsList)
                        container.DependsOn = depsList.Select(d => d.ToString()!).ToList();
                    else if (depsObj is Dictionary<object, object> depsDict)
                        container.DependsOn = depsDict.Keys.Select(k => k.ToString()!).ToList();
                }

                if (svc.TryGetValue("restart", out var restart))
                    container.RestartPolicy = restart?.ToString();

                containers.Add(container);
            }
        }
        catch { /* skip malformed compose files */ }
        return containers;
    }

    public List<ContainerInfo> Analyze(List<FileNode> files)
    {
        var containers = new List<ContainerInfo>();
        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            var name = file.Name.ToLowerInvariant();
            if (name.StartsWith("docker-compose") && (file.Extension == ".yml" || file.Extension == ".yaml"))
                containers.AddRange(ParseDockerCompose(file));
        }
        return containers;
    }

    public DockerfileInfo? AnalyzeDockerfile(List<FileNode> files)
    {
        var dockerfile = files.FirstOrDefault(f =>
            !f.IsDirectory && f.Name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase));
        return dockerfile != null ? ParseDockerfile(dockerfile) : null;
    }

    /// <summary>
    /// When no docker-compose exists, synthesize ContainerInfo from Dockerfile EXPOSE ports.
    /// </summary>
    public List<ContainerInfo> SynthesizeContainersFromDockerfile(DockerfileInfo info, string projectName)
    {
        if (info.ExposedPorts.Count == 0) return [];

        var container = new ContainerInfo
        {
            Name = projectName,
            Image = $"build:{projectName}",
            DockerfilePath = info.FilePath,
            BuildContext = "."
        };

        foreach (var port in info.ExposedPorts)
        {
            // Common convention: guess protocol based on port range
            var desc = port switch
            {
                >= 50000 => "gRPC",
                >= 10000 and < 20000 => "HTTP/gRPC-Gateway",
                >= 20000 and < 30000 => "gRPC",
                443 or 8443 => "HTTPS",
                80 or 8080 or 3000 or 4000 => "HTTP",
                5432 => "PostgreSQL",
                6379 => "Redis",
                _ => "Service"
            };
            container.Ports.Add(new PortMapping
            {
                HostPort = port,
                ContainerPort = port,
                Protocol = desc.Contains("gRPC") ? "grpc" : "tcp",
                Description = desc
            });
        }

        foreach (var env in info.EnvVars)
            container.EnvVariables.Add(env);

        return [container];
    }
}
