# StrykerRunner

A .NET global tool to run Stryker mutation testing across multiple projects and generate unified reports.

## What it does

StrykerRunner automates the process of running [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) mutation testing across multiple projects and combines the results into a single unified HTML report. It:

1. Resolves test project(s) using the following priority order:
   - `--solution` flag (explicit `.sln` / `.slnx` path) → discovers all `*.Tests` / `*.Test` projects from the solution
   - Auto-detects a `.sln` file in the current directory
   - Auto-detects a `.slnx` file in the current directory
   - `--test-project` flag (explicit `.csproj` path)
   - Falls back to the first `.csproj` found in the current directory
2. Discovers project references from each test project
3. Filters out test projects and other excluded patterns (Init, CommunicatieModels, Reqnroll, etc.)
4. Runs Stryker mutation testing on each discovered project
5. Aggregates the JSON reports from all runs
6. Generates a unified HTML report using [mutation-testing-elements](https://github.com/stryker-mutator/mutation-testing-elements)

## Installation

Install as a global .NET tool:

```bash
dotnet tool install --global StrykerRunner
```

Or install from source:

```bash
dotnet pack
dotnet tool install --global --add-source ./bin/Debug StrykerRunner
```

## Usage

Navigate to your test project directory and run:

```bash
stryker-runner
```

### Options

- `--solution <path>` - Path to a `.sln` or `.slnx` solution file. If not provided, auto-detects a solution in the current directory. Takes precedence over `--test-project`.
- `--test-project <path>` - Path to the test project .csproj file. If not provided, searches for a .csproj in the current directory.
- `--output <directory>` - Base output directory for Stryker reports. Default: `./StrykerOutput`
- `--report-name <name>` - Name of the unified HTML report file. Default: `UnifiedMutationReport.html`
- `--exclude-patterns <pattern>` - Regex patterns to exclude projects (in addition to test projects). Can be specified multiple times. Default patterns:
  - `\.Init$` - Excludes initialization/migration projects
  - `\.CommunicatieModels$` - Excludes CommunicatieModels projects
  - `\.Reqnroll$` - Excludes Reqnroll projects

### Examples

Basic usage from a solution or project directory (auto-detects `.sln` / `.slnx` / `.csproj`):
```bash
stryker-runner
```

Manually specify a solution file:
```bash
stryker-runner --solution ./MySolution.sln
```

Manually specify a `.slnx` solution file:
```bash
stryker-runner --solution ./MySolution.slnx
```

Specify a test project directly:
```bash
stryker-runner --test-project ./MyProject.Tests/MyProject.Tests.csproj
```

Custom output directory:
```bash
stryker-runner --output ./MutationReports
```

Add custom exclusion patterns:
```bash
stryker-runner --exclude-patterns "\.Init$" "\.Models$" "\.Contracts$"
```

## Output

The tool generates a timestamped folder structure:

```
StrykerOutput/
└── 2024-01-21_14-30-00/
    ├── ProjectA/
    │   └── reports/
    │       └── mutation-report.json
    ├── ProjectB/
    │   └── reports/
    │       └── mutation-report.json
    └── UnifiedMutationReport.html
```

Open `UnifiedMutationReport.html` in your browser to view the combined mutation testing results for all projects.

## Requirements

- .NET 8.0 SDK or later
- [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) must be installed globally or locally

## Uninstall

```bash
dotnet tool uninstall --global StrykerRunner
```

## License

MIT
