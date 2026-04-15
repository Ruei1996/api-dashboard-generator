// ============================================================
// DockerAnalyzer.cs — Dockerfile and Docker Compose parser
// ============================================================
// Architecture: stateless service class; instantiated once per analyze run.
// Supported inputs:
//   1. Dockerfile       — parses FROM, EXPOSE, ENV, ENTRYPOINT, CMD, WORKDIR
//   2. docker-compose.yml / .yaml — parses services, ports, environment,
//      depends_on, restart policy, and build context
//
// Security (OWASP A3:2021 — Sensitive Data Exposure):
//   ENV values whose keys contain sensitive keywords (_sensitiveEnvKeywords)
//   are masked to "***masked***" before being stored, preventing secrets
//   from leaking into the generated dashboard HTML output.
//
// Usage:
//   var analyzer = new DockerAnalyzer();
//   var containers = analyzer.Analyze(allFiles);           // docker-compose
//   var dockerfile = analyzer.AnalyzeDockerfile(allFiles); // Dockerfile
// ============================================================

using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Parses Dockerfile and docker-compose files in a repository, extracting
/// container services, port mappings, environment variables, and build contexts.
/// </summary>
/// <remarks>
/// <para>
/// When a docker-compose file is present, <see cref="Analyze"/> returns one
/// <see cref="ContainerInfo"/> per declared service.  When only a Dockerfile
/// is present, <see cref="SynthesizeContainersFromDockerfile"/> creates a
/// virtual service from the EXPOSE instructions.
/// </para>
/// <para>
/// ENV values whose keys match any entry in <see cref="_sensitiveEnvKeywords"/>
/// are masked to <c>***masked***</c> before storage — consistent with
/// <c>EnvFileAnalyzer</c>'s behaviour (OWASP A3:2021 Sensitive Data Exposure).
/// </para>
/// </remarks>
public class DockerAnalyzer
{
    // Keywords matching EnvFileAnalyzer.SensitiveKeywords — Dockerfile ENV values
    // whose keys contain these terms are masked to "***masked***" in output.
    private static readonly string[] _sensitiveEnvKeywords =
    [
        "password", "passwd", "secret", "key", "token", "credential",
        "api_key", "apikey", "private", "auth", "cert", "pass"
    ];

    /// <summary>
    /// Parses a Dockerfile line-by-line and extracts its base image, multi-stage build
    /// names, exposed ports, environment variables, entry point, CMD, and working directory.
    /// </summary>
    /// <param name="file">The <see cref="FileNode"/> representing the Dockerfile to parse.</param>
    /// <returns>
    /// A <see cref="DockerfileInfo"/> populated with the parsed values, or <c>null</c>
    /// if the file does not exist on disk at the time of analysis.
    /// </returns>
    /// <remarks>
    /// Only the first FROM instruction sets <see cref="DockerfileInfo.BaseImage"/>; subsequent
    /// FROM instructions add stage names for multi-stage builds.  Lines beginning with
    /// <c>#</c> (comments) and blank lines are skipped to avoid false positives.
    /// </remarks>
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
                        var envKey = envParts[0].Trim();
                        var rawVal = envParts.Length > 1 ? envParts[1].Trim() : "";
                        // Mask values for keys that look like credentials/secrets,
                        // consistent with EnvFileAnalyzer's masking behaviour.
                        var isSensitive = _sensitiveEnvKeywords.Any(kw =>
                            envKey.Contains(kw, StringComparison.OrdinalIgnoreCase));
                        info.EnvVars.Add(new EnvVariable
                        {
                            Key = envKey,
                            Value = isSensitive ? "***masked***" : rawVal,
                            IsSensitive = isSensitive,
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

    /// <summary>
    /// Parses a docker-compose YAML file and returns one <see cref="ContainerInfo"/>
    /// per service defined under the top-level <c>services</c> key.
    /// </summary>
    /// <param name="file">The <see cref="FileNode"/> representing the compose file.</param>
    /// <returns>
    /// List of <see cref="ContainerInfo"/> objects, one per service.
    /// Returns an empty list if the file is missing, empty, or structurally malformed.
    /// </returns>
    /// <remarks>
    /// Uses YamlDotNet with <c>IgnoreUnmatchedProperties</c> so that docker-compose v2/v3
    /// extension fields (e.g. <c>healthcheck</c>, <c>deploy</c>, <c>x-custom</c>) do not
    /// throw during deserialisation.  All exceptions are silently swallowed so that one
    /// malformed compose file does not abort the entire analysis run.
    /// </remarks>
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
                        // Port format: [hostPort:]containerPort[/protocol]
                        // Group 1 = optional host port, Group 2 = container port, Group 3 = optional protocol.
                        // Examples: "8080:80" → host=8080, container=80; "443/udp" → host=443, container=443, proto=udp.
                        // When no host port is specified (e.g. "80"), host port defaults to the container port.
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

    /// <summary>
    /// Scans <paramref name="files"/> for docker-compose YAML files and returns a
    /// consolidated list of <see cref="ContainerInfo"/> objects from all discovered files.
    /// </summary>
    /// <param name="files">Flat file list produced by <see cref="FileScanner"/>.</param>
    /// <returns>
    /// Combined list of <see cref="ContainerInfo"/> objects from all compose files found.
    /// Returns an empty list if no compose files are present.
    /// </returns>
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

    /// <summary>
    /// Finds the first file named <c>Dockerfile</c> (case-insensitive) in
    /// <paramref name="files"/> and returns its parsed representation.
    /// </summary>
    /// <param name="files">Flat file list produced by <see cref="FileScanner"/>.</param>
    /// <returns>
    /// A populated <see cref="DockerfileInfo"/>, or <c>null</c> if no Dockerfile is found.
    /// </returns>
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
