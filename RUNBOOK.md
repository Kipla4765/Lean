# LEAN Engine — Runbook

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- Python 3.11 (optional, for Python algorithms)
- Docker (optional, for containerized runs)

## Build

```bash
# Debug build
dotnet build QuantConnect.Lean.sln

# Release build
dotnet build /p:Configuration=Release QuantConnect.Lean.sln
```

## Run (Backtest)

```bash
dotnet run --project Launcher/QuantConnect.Lean.Launcher.csproj
```

Or build first, then run:

```bash
dotnet build Launcher/QuantConnect.Lean.Launcher.csproj -c Release
dotnet Launcher/bin/Release/net10.0/QuantConnect.Lean.Launcher.dll
```

Override algorithm or config:

```bash
dotnet Launcher/bin/Release/net10.0/QuantConnect.Lean.Launcher.dll \
  --data-folder ../../Data \
  --algorithm-type-name BasicTemplateFrameworkAlgorithm \
  --algorithm-language CSharp \
  --algorithm-location QuantConnect.Algorithm.CSharp.dll
```

Configuration is read from `config.json`. See the `environments` section for live-trading profiles.

## Test

```bash
# Run all tests
dotnet test Tests/QuantConnect.Tests.csproj -c Release

# Run only regression tests
dotnet test Tests/QuantConnect.Tests.csproj -c Release --filter TestCategory=RegressionTests

# Exclude slow/Travis-heavy tests
dotnet test Tests/QuantConnect.Tests.csproj -c Release \
  --filter "TestCategory!=TravisExclude&TestCategory!=ResearchRegressionTests" \
  -- TestRunParameters.Parameter(name="log-handler", value="ConsoleErrorLogHandler")
```

## Lint & Typecheck (Python only)

```bash
pip install mypy quantconnect-stubs   # one-time
python run_syntax_check.py
```

## Benchmarks

```bash
python run_benchmarks.py [data-path]
python compare_benchmarks.py <reference.json> <new.json>
```

## Docker

```bash
# Build the foundation image (Python + .NET + ML libs)
docker build -f DockerfileLeanFoundation -t quantconnect/lean:foundation .

# Build the main LEAN image
docker build -t quantconnect/lean .

# Build the Jupyter research image
docker build -f DockerfileJupyter -t quantconnect/lean:jupyter .

# Run LEAN in a container
docker run quantconnect/lean
```

## Other Scripts

| Script | Purpose |
|---|---|
| `run_syntax_check.py` | Run mypy on Python algorithms |
| `run_benchmarks.py` | Run performance benchmarks |
| `compare_benchmarks.py` | Compare benchmark results |
| `ci_build_stubs.sh` | Generate & publish Python type stubs |
| `rebase_organization_branches.sh` | Rebase `org-*` branches onto master |

## IDE

- **VS Code**: `.vscode/` and `.devcontainer/` are pre-configured.
- **Visual Studio / Rider**: Open `QuantConnect.Lean.sln`.
