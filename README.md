# StrykerRunner

A .NET global tool to run Stryker mutation testing across multiple projects and generate unified reports.

## What it does

StrykerRunner automates the process of running [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) mutation testing across multiple projects and combines the results into a single unified HTML report. It:

1. Discovers project references from your test project
2. Filters out test projects and other excluded patterns (Init, CommunicatieModels, Reqnroll, etc.)
3. Runs Stryker mutation testing on each discovered project
4. Aggregates the JSON reports from all runs
5. Generates a unified HTML report using [mutation-testing-elements](https://github.com/stryker-mutator/mutation-testing-elements)

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

- `--test-project <path>` - Path to the test project .csproj file. If not provided, searches for a .csproj in the current directory.
- `--output <directory>` - Base output directory for Stryker reports. Default: `./StrykerOutput`
- `--report-name <name>` - Name of the unified HTML report file. Default: `UnifiedMutationReport.html`
- `--exclude-patterns <pattern>` - Regex patterns to exclude projects (in addition to test projects). Can be specified multiple times. Default patterns:
  - `\.Init$` - Excludes initialization/migration projects
  - `\.CommunicatieModels$` - Excludes CommunicatieModels projects
  - `\.Reqnroll$` - Excludes Reqnroll projects

### Examples

Basic usage from test project directory:
```bash
stryker-runner
```

Specify a test project:
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
