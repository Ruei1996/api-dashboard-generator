// ============================================================
// MakefileAnalyzer.cs — Makefile discovery and auto-generation
// ============================================================
// Architecture: stateless service class; instantiated once per analyze run.
//
// Behaviour:
//   1. If a Makefile (or GNUmakefile) exists in the repository root, it is
//      read verbatim and its target names are extracted for quick navigation.
//   2. If no Makefile is found, one is auto-generated based on the tools,
//      languages, and auto-generated artifacts detected in the project.
//      Detection is purely file-presence-based — no network calls are made.
//
// Supported auto-generation triggers:
//   Go      → go build / go test / go vet / go mod tidy
//   Node.js → npm ci / npm run build / npm test
//   Python  → pip install / pytest / ruff / mypy
//   Rust    → cargo build / cargo test / cargo clippy
//   Java    → mvn package / mvn test
//   Ruby    → bundle install / rspec
//   Proto   → buf generate (preferred) or protoc fallback
//   sqlc    → sqlc generate
//   mockery → mockery --all
//   swag    → swag init (gin-swagger)
//   wire    → wire (Google Wire DI)
//   Docker  → docker compose up / docker compose build
// ============================================================

using System.Text;
using System.Text.RegularExpressions;
using RepoInsightDashboard.Models;

namespace RepoInsightDashboard.Analyzers;

/// <summary>
/// Discovers or auto-generates a Makefile for the repository and returns
/// a <see cref="MakefileInfo"/> containing its raw content and extracted targets.
/// </summary>
public class MakefileAnalyzer
{
    /// <summary>
    /// Returns a <see cref="MakefileInfo"/> for the repository at <paramref name="repoPath"/>.
    /// If a Makefile (or GNUmakefile) exists it is read verbatim; otherwise a suitable
    /// Makefile is synthesised from detected tools and languages.
    /// </summary>
    public MakefileInfo Analyze(string repoPath, List<FileNode> allFiles)
    {
        // Search for an existing Makefile in the repo root (also catches gitignored ones
        // because FileScanner.ForceInclude now lists "Makefile" and "GNUmakefile").
        var makefileNode = allFiles.FirstOrDefault(f =>
            !f.IsDirectory &&
            (f.Name.Equals("Makefile",    StringComparison.OrdinalIgnoreCase) ||
             f.Name.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase) ||
             f.Name.Equals("makefile",    StringComparison.OrdinalIgnoreCase)));

        // Also do a direct filesystem check in case the file was gitignored and missed
        if (makefileNode == null)
        {
            foreach (var candidate in new[] { "Makefile", "GNUmakefile", "makefile" })
            {
                var fullPath = Path.Combine(repoPath, candidate);
                if (File.Exists(fullPath))
                {
                    var content = SafeReadFile(fullPath);
                    return new MakefileInfo
                    {
                        Exists   = true,
                        Content  = content,
                        FilePath = candidate,
                        Targets  = ExtractTargets(content)
                    };
                }
            }
        }

        if (makefileNode != null)
        {
            var content = SafeReadFile(makefileNode.AbsolutePath);
            return new MakefileInfo
            {
                Exists   = true,
                Content  = content,
                FilePath = makefileNode.RelativePath,
                Targets  = ExtractTargets(content)
            };
        }

        // No Makefile found — synthesise one
        var generated = GenerateMakefile(repoPath, allFiles);
        return new MakefileInfo
        {
            Exists   = false,
            Content  = generated,
            FilePath = "(auto-generated)",
            Targets  = ExtractTargets(generated)
        };
    }

    // ── Target Extractor ──────────────────────────────────────────────────────

    /// <summary>
    /// Extracts target names from Makefile content (lines matching "target:").
    /// Phony declarations and lines starting with a tab are excluded.
    /// </summary>
    private static List<string> ExtractTargets(string content)
    {
        var targets = new List<string>();
        // Split on both \r\n (Windows) and \n (Unix) to handle cross-platform Makefiles
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.StartsWith('\t') || trimmed.StartsWith('#') || trimmed.StartsWith('.'))
                continue;
            var m = Regex.Match(trimmed, @"^([\w\-\.]+)\s*:");
            if (m.Success && m.Groups[1].Value != "PHONY")
                targets.Add(m.Groups[1].Value);
        }
        return targets.Distinct().Take(30).ToList();
    }

    // ── Makefile Auto-Generator ───────────────────────────────────────────────

    private static string GenerateMakefile(string repoPath, List<FileNode> files)
    {
        var sb = new StringBuilder();

        // Detect project characteristics
        bool hasGo         = files.Any(f => f.Extension == ".go" && !f.IsDirectory);
        bool hasGoMod      = files.Any(f => f.Name == "go.mod");
        bool hasNode       = files.Any(f => f.Name == "package.json");
        bool hasPython     = files.Any(f => f.Name is "requirements.txt" or "setup.py" or "pyproject.toml");
        bool hasRust       = files.Any(f => f.Name == "Cargo.toml");
        bool hasJava       = files.Any(f => f.Name == "pom.xml");
        bool hasRuby       = files.Any(f => f.Name == "Gemfile");
        bool hasDocker     = files.Any(f => f.Name is "docker-compose.yml" or "docker-compose.yaml");
        bool hasProto      = files.Any(f => f.Extension == ".proto" && !f.IsDirectory);
        bool hasBuf        = files.Any(f => f.Name == "buf.yaml" || f.Name == "buf.gen.yaml");
        bool hasSqlc       = files.Any(f => f.Name is "sqlc.yaml" or ".sqlc.yaml" or "sqlc.yml");
        bool hasMockery    = files.Any(f => f.Name is ".mockery.yaml" or "mockery.yaml");
        bool hasSwag = files.Any(f =>
            f.Name == "swag" ||
            f.RelativePath.EndsWith("docs/swagger.json",  StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.EndsWith("docs/swagger.yaml",  StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.EndsWith("docs/swagger.yml",   StringComparison.OrdinalIgnoreCase));
        bool hasWire       = files.Any(f => f.Name == "wire.go" || f.RelativePath.EndsWith("wire_gen.go"));
        bool hasAirConfig  = files.Any(f => f.Name is ".air.toml" or "air.toml");
        bool hasDotEnv     = files.Any(f => f.Name == ".env.example" || f.Name == ".env.sample");

        sb.AppendLine("# =============================================================");
        sb.AppendLine("# Makefile — Auto-generated by Repo Insight Dashboard");
        sb.AppendLine("# This file was synthesised because no Makefile was found.");
        sb.AppendLine("# Review and adjust the commands to match your environment.");
        sb.AppendLine("# =============================================================");
        sb.AppendLine();

        // Collect all phony targets
        var phony = new List<string> { "help", "all", "clean" };

        // ── Go section ───────────────────────────────────────────────────────
        if (hasGo && hasGoMod)
        {
            phony.AddRange(["build", "test", "lint", "vet", "tidy", "run"]);
            if (hasAirConfig) phony.Add("dev");
        }

        // ── Code-gen targets ─────────────────────────────────────────────────
        if (hasProto)    phony.Add(hasBuf ? "generate-buf" : "generate-proto");
        if (hasSqlc)     phony.Add("generate-sqlc");
        if (hasMockery)  phony.Add("generate-mocks");
        if (hasSwag)     phony.Add("generate-docs");
        if (hasWire)     phony.Add("generate-wire");
        if (hasBuf)      phony.Add("buf-lint");

        // ── Docker ───────────────────────────────────────────────────────────
        if (hasDocker) phony.AddRange(["docker-up", "docker-down", "docker-build"]);

        // ── Node.js ──────────────────────────────────────────────────────────
        if (hasNode) phony.AddRange(["npm-install", "npm-build", "npm-test", "npm-lint"]);

        // ── Python ───────────────────────────────────────────────────────────
        if (hasPython) phony.AddRange(["py-install", "py-test", "py-lint"]);

        // ── Rust ─────────────────────────────────────────────────────────────
        if (hasRust) phony.AddRange(["cargo-build", "cargo-test", "cargo-clippy"]);

        // ── Java ─────────────────────────────────────────────────────────────
        if (hasJava) phony.AddRange(["mvn-build", "mvn-test"]);

        // ── Ruby ─────────────────────────────────────────────────────────────
        if (hasRuby) phony.AddRange(["bundle-install", "rspec"]);

        sb.AppendLine($".PHONY: {string.Join(" ", phony.Distinct())}");
        sb.AppendLine();

        // ── help ─────────────────────────────────────────────────────────────
        sb.AppendLine("# Display all available targets with descriptions");
        sb.AppendLine("help:");
        sb.AppendLine("\t@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \\");
        sb.AppendLine("\t  | awk 'BEGIN {FS = \":.*?## \"}; {printf \"\\033[36m%-20s\\033[0m %s\\n\", $$1, $$2}'");
        sb.AppendLine();

        // ── Go targets ───────────────────────────────────────────────────────
        if (hasGo && hasGoMod)
        {
            sb.AppendLine("# ── Go ────────────────────────────────────────────────────────────");
            sb.AppendLine();

            // Detect binary output path from main package
            var outputBin = "./bin/app";
            sb.AppendLine("build: ## Compile the Go binary");
            sb.AppendLine($"\tgo build -v -o {outputBin} ./...");
            sb.AppendLine();

            if (hasAirConfig)
            {
                sb.AppendLine("dev: ## Run the server in hot-reload mode (requires: air)");
                sb.AppendLine("\tair");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("run: ## Run the application");
                sb.AppendLine("\tgo run ./...");
                sb.AppendLine();
            }

            sb.AppendLine("test: ## Run all unit and integration tests");
            sb.AppendLine("\tgo test -race -coverprofile=coverage.out ./...");
            sb.AppendLine();

            sb.AppendLine("vet: ## Run go vet static analysis");
            sb.AppendLine("\tgo vet ./...");
            sb.AppendLine();

            sb.AppendLine("lint: ## Run golangci-lint (requires: golangci-lint)");
            sb.AppendLine("\tgolangci-lint run ./...");
            sb.AppendLine();

            sb.AppendLine("tidy: ## Tidy go.mod and go.sum");
            sb.AppendLine("\tgo mod tidy");
            sb.AppendLine();
        }

        // ── Code generation ──────────────────────────────────────────────────
        bool hasAnyGen = hasProto || hasSqlc || hasMockery || hasSwag || hasWire;
        if (hasAnyGen)
        {
            sb.AppendLine("# ── Code Generation ───────────────────────────────────────────────");
            sb.AppendLine();

            if (hasProto)
            {
                if (hasBuf)
                {
                    sb.AppendLine("generate-buf: ## Generate protobuf files using buf (requires: buf)");
                    sb.AppendLine("\tbuf generate");
                    sb.AppendLine();
                    sb.AppendLine("buf-lint: ## Lint proto files with buf");
                    sb.AppendLine("\tbuf lint");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("generate-proto: ## Generate Go code from proto files (requires: protoc, protoc-gen-go, protoc-gen-go-grpc)");
                    sb.AppendLine("\tprotoc --go_out=. --go_opt=paths=source_relative \\");
                    sb.AppendLine("\t       --go-grpc_out=. --go-grpc_opt=paths=source_relative \\");
                    sb.AppendLine("\t       $(shell find . -name '*.proto' -not -path './vendor/*')");
                    sb.AppendLine();
                }
            }

            if (hasSqlc)
            {
                sb.AppendLine("generate-sqlc: ## Generate Go database code from SQL queries (requires: sqlc)");
                sb.AppendLine("\tsqlc generate");
                sb.AppendLine();
            }

            if (hasMockery)
            {
                sb.AppendLine("generate-mocks: ## Generate mock implementations (requires: mockery)");
                sb.AppendLine("\tmockery --all --keeptree");
                sb.AppendLine();
            }

            if (hasSwag)
            {
                sb.AppendLine("generate-docs: ## Re-generate Swagger docs from annotations (requires: swag)");
                sb.AppendLine("\tswag init -g cmd/main.go --output docs");
                sb.AppendLine();
            }

            if (hasWire)
            {
                sb.AppendLine("generate-wire: ## Re-generate dependency injection code (requires: wire)");
                sb.AppendLine("\twire ./...");
                sb.AppendLine();
            }
        }

        // ── Docker ───────────────────────────────────────────────────────────
        if (hasDocker)
        {
            sb.AppendLine("# ── Docker ────────────────────────────────────────────────────────");
            sb.AppendLine();

            // Detect docker-compose file name
            var dcFile = files.Any(f => f.Name == "docker-compose.yml") ? "docker-compose.yml" : "docker-compose.yaml";
            sb.AppendLine("docker-up: ## Start all services with Docker Compose");
            sb.AppendLine($"\tdocker compose -f {dcFile} up -d");
            sb.AppendLine();

            sb.AppendLine("docker-down: ## Stop all Docker Compose services");
            sb.AppendLine($"\tdocker compose -f {dcFile} down");
            sb.AppendLine();

            sb.AppendLine("docker-build: ## Rebuild all Docker Compose images");
            sb.AppendLine($"\tdocker compose -f {dcFile} build --no-cache");
            sb.AppendLine();
        }

        // ── Node.js ──────────────────────────────────────────────────────────
        if (hasNode)
        {
            sb.AppendLine("# ── Node.js ───────────────────────────────────────────────────────");
            sb.AppendLine();

            // Detect lock file to pick the right package manager
            var hasYarnLock = files.Any(f => f.Name == "yarn.lock");
            var hasPnpmLock = files.Any(f => f.Name == "pnpm-lock.yaml");
            var pm = hasPnpmLock ? "pnpm" : hasYarnLock ? "yarn" : "npm";

            sb.AppendLine($"npm-install: ## Install Node.js dependencies ({pm})");
            sb.AppendLine($"\t{pm} {(pm == "npm" ? "ci" : "install")}");
            sb.AppendLine();

            sb.AppendLine($"npm-build: ## Build the Node.js project");
            sb.AppendLine($"\t{pm} run build");
            sb.AppendLine();

            sb.AppendLine($"npm-test: ## Run Node.js tests");
            sb.AppendLine($"\t{pm} test");
            sb.AppendLine();

            sb.AppendLine($"npm-lint: ## Lint the Node.js project");
            sb.AppendLine($"\t{pm} run lint");
            sb.AppendLine();
        }

        // ── Python ───────────────────────────────────────────────────────────
        if (hasPython)
        {
            sb.AppendLine("# ── Python ────────────────────────────────────────────────────────");
            sb.AppendLine();

            var hasPyproject = files.Any(f => f.Name == "pyproject.toml");
            sb.AppendLine("py-install: ## Install Python dependencies");
            sb.AppendLine(hasPyproject ? "\tpip install -e '.[dev]'" : "\tpip install -r requirements.txt");
            sb.AppendLine();

            sb.AppendLine("py-test: ## Run Python tests with pytest");
            sb.AppendLine("\tpytest -v --tb=short");
            sb.AppendLine();

            sb.AppendLine("py-lint: ## Lint Python code with ruff and mypy");
            sb.AppendLine("\truff check .");
            sb.AppendLine("\tmypy .");
            sb.AppendLine();
        }

        // ── Rust ─────────────────────────────────────────────────────────────
        if (hasRust)
        {
            sb.AppendLine("# ── Rust ──────────────────────────────────────────────────────────");
            sb.AppendLine();

            sb.AppendLine("cargo-build: ## Build Rust binaries");
            sb.AppendLine("\tcargo build --release");
            sb.AppendLine();

            sb.AppendLine("cargo-test: ## Run Rust tests");
            sb.AppendLine("\tcargo test");
            sb.AppendLine();

            sb.AppendLine("cargo-clippy: ## Run Clippy linter");
            sb.AppendLine("\tcargo clippy -- -D warnings");
            sb.AppendLine();
        }

        // ── Java ─────────────────────────────────────────────────────────────
        if (hasJava)
        {
            sb.AppendLine("# ── Java / Maven ──────────────────────────────────────────────────");
            sb.AppendLine();

            sb.AppendLine("mvn-build: ## Package the Maven project (skip tests)");
            sb.AppendLine("\tmvn package -DskipTests");
            sb.AppendLine();

            sb.AppendLine("mvn-test: ## Run Maven tests");
            sb.AppendLine("\tmvn test");
            sb.AppendLine();
        }

        // ── Ruby ─────────────────────────────────────────────────────────────
        if (hasRuby)
        {
            sb.AppendLine("# ── Ruby ──────────────────────────────────────────────────────────");
            sb.AppendLine();

            sb.AppendLine("bundle-install: ## Install Ruby gems");
            sb.AppendLine("\tbundle install");
            sb.AppendLine();

            sb.AppendLine("rspec: ## Run RSpec tests");
            sb.AppendLine("\tbundle exec rspec");
            sb.AppendLine();
        }

        // ── Env setup ────────────────────────────────────────────────────────
        if (hasDotEnv)
        {
            sb.AppendLine("# ── Environment ──────────────────────────────────────────────────");
            sb.AppendLine();

            var exampleFile = files.Any(f => f.Name == ".env.example") ? ".env.example" : ".env.sample";
            sb.AppendLine("setup-env: ## Copy example env file to .env");
            sb.AppendLine($"\tcp {exampleFile} .env");
            sb.AppendLine("\t@echo 'Edit .env with your local configuration'");
            sb.AppendLine();
        }

        // ── clean ─────────────────────────────────────────────────────────────
        sb.AppendLine("# ── Utilities ─────────────────────────────────────────────────────────");
        sb.AppendLine();

        sb.AppendLine("all: ## Run all checks (vet + test + build)");
        var allDeps = new List<string>();
        if (hasGo && hasGoMod) allDeps.AddRange(["vet", "test", "build"]);
        if (hasNode)           allDeps.Add("npm-test");
        if (hasPython)         allDeps.Add("py-test");
        if (hasRust)           allDeps.Add("cargo-test");
        if (hasJava)           allDeps.Add("mvn-test");
        sb.AppendLine(allDeps.Count > 0 ? $"all: {string.Join(" ", allDeps)}" : "all:");
        sb.AppendLine();

        sb.AppendLine("clean: ## Remove build artefacts");
        sb.AppendLine("\trm -rf bin/ dist/ coverage.out *.out");
        if (hasNode)  sb.AppendLine("\trm -rf node_modules/");
        if (hasPython) sb.AppendLine("\tfind . -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null; true");
        if (hasRust)  sb.AppendLine("\tcargo clean");
        if (hasJava)  sb.AppendLine("\tmvn clean");
        sb.AppendLine();

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads all text from <paramref name="path"/>, returning an empty string on any I/O error.
    /// Prevents <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> from
    /// aborting the analysis when a Makefile exists but is momentarily unreadable (e.g.
    /// file locked by another process, permission denied in CI environments).
    /// </summary>
    private static string SafeReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }
}
