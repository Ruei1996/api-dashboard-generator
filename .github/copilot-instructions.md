# Copilot Instructions

## Project Overview
**Repo Insight Dashboard (`rid`)** — a .NET 10 C# CLI tool that performs static and AI-assisted semantic analysis on code repositories and generates interactive, offline-capable HTML dashboards.

## Tech Stack
- **Language:** C# 12 / .NET 10 (nullable enabled, implicit usings)
- **Type:** Global dotnet tool (`dotnet tool install`), CLI command: `rid`
- **CLI parsing:** `System.CommandLine` (beta4)
- **Key NuGet deps:** `LibGit2Sharp`, `Microsoft.OpenApi.Readers`, `Newtonsoft.Json`, `YamlDotNet`
- **Testing:** xUnit 2.9, `coverlet.collector` for coverage
- **Frontend (embedded in output):** Vanilla JS + CSS variables + Mermaid.js (inlined into single HTML file)

## Build & Test Commands
```bash
dotnet build src/RepoInsightDashboard.slnx
dotnet test src/RepoInsightDashboard.slnx
dotnet pack src/RepoInsightDashboard/RepoInsightDashboard.csproj
dotnet run --project src/RepoInsightDashboard -- analyze /path/to/repo
```

## Project Structure
```
src/
├── RepoInsightDashboard/           # Main CLI application
│   ├── Program.cs                  # Entry point
│   ├── Commands/AnalyzeCommand.cs  # CLI command handler
│   ├── Analyzers/                  # Static analysis modules (one per concern)
│   ├── Services/AnalysisOrchestrator.cs  # Coordinates analyzer pipeline
│   ├── Generators/                 # HtmlDashboardGenerator, JsonMetadataGenerator
│   └── Models/                     # Data models (DashboardData is the root aggregate)
├── RepoInsightDashboard.Tests/     # xUnit tests for analyzers
│   └── AnalyzerTests.cs
└── RepoInsightDashboard.slnx       # Modern VS solution file
docs/SPEC.md                        # Feature specification
```

## Architecture & Conventions
- **Analyzer pattern:** Each concern has its own `*Analyzer.cs` implementing `IAnalyzer`. Add new analyzers by implementing the interface and registering in `AnalysisOrchestrator`.
- **Pipeline flow:** `AnalyzeCommand` → `AnalysisOrchestrator` → all Analyzers → `Models/DashboardData` → Generators → HTML/JSON output
- **Generators** consume `DashboardData` and produce output; keep generation logic out of analyzers.
- **Models** are plain C# records/classes with no behavior; data flows one-way from analyzers to generators.
- HTML output must remain fully self-contained (inline all CSS/JS/assets — no external CDN calls in final output).
- Use `Newtonsoft.Json` (not `System.Text.Json`) for serialization consistency.
- Sensitive values (env vars, secrets) must be masked in `EnvFileAnalyzer` before reaching any output.

## Supported Analysis Targets
Languages: C#, Go, Java, TypeScript, JavaScript, Python, Rust, Ruby  
Package managers: NuGet, npm, Go Modules, Maven, pip, Cargo  
Specs: OpenAPI/Swagger, Dockerfile, docker-compose  

## CLI Usage
```bash
rid analyze /path/to/repo [--copilot-token TOKEN] [--output PATH] [--verbose] [--theme light|dark]
```
`--copilot-token` enables optional GitHub Copilot API semantic analysis via `CopilotSemanticAnalyzer`.

## Code Style

- All `public` types and members require XML doc comments (`<summary>`, `<param>`, `<returns>`).
- Namespaces mirror folder paths: `RepoInsightDashboard.Analyzers`, `.Services`, `.Models`, `.Generators`.
- One concern per file; file name matches the primary type it defines.
- Use C# 12 features: top-level statements, records for immutable models, nullable reference types.

## Locale & Language

- CLI help text and user-facing strings: **Traditional Chinese (繁體中文)**.
- Source code comments and XML docs: **English**.
- README: Chinese; technical specs (`docs/SPEC.md`): English.

## Output & Error Handling

- Analysis results → `Console.Out` (stdout).
- Diagnostics, progress, and errors → `Console.Error` (stderr). Never mix them (CWE-200).
- Use `Console.ForegroundColor` / `Console.ResetColor` for colored terminal output; never ANSI escape codes directly.

## Concurrency Model

`AnalysisOrchestrator` runs in three phases:
1. **Serial** — file scan, language detection, `.env` loading.
2. **Concurrent** (`Task.WhenAll`) — `DependencyAnalyzer`, `DockerAnalyzer`, `SwaggerAnalyzer`, `EnvFileAnalyzer`, `TestAnalyzer`; then serial `ApiTraceAnalyzer` (depends on Swagger output).
3. **Concurrent** — three parallel GitHub Copilot API calls (summary, design patterns, security risks).

New analyzers that have no cross-dependencies should be added to Phase 2.

## GitHub Copilot API Integration

- Token sourced from `--copilot-token` CLI flag or `COPILOT_TOKEN` env var (`.env` file).
- Allowed model names: `gpt-4o` (default), `gpt-4o-mini`, `gpt-4.1` — validated via `FromAmong`.

## Testing

- Tests live in `RepoInsightDashboard.Tests`; the project references the main project directly (no mocking layer by default).
- Coverage collected via `coverlet.collector` — run `dotnet test` to generate reports.