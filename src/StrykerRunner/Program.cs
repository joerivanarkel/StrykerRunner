using System.CommandLine;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

[assembly: InternalsVisibleTo("StrykerRunner.Tests")]

namespace StrykerRunner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var testProjectOption = new Option<FileInfo?>(
            name: "--test-project",
            description: "Path to the test project .csproj file. If not provided, auto-detects.",
            getDefaultValue: () => null);

        var solutionOption = new Option<FileInfo?>(
            name: "--solution",
            description: "Path to a .sln or .slnx solution file. If not provided, auto-detects.",
            getDefaultValue: () => null);

        var outputDirOption = new Option<DirectoryInfo?>(
            name: "--output",
            description: "Base output directory for Stryker reports.",
            getDefaultValue: () => new DirectoryInfo("./StrykerOutput"));

        var reportNameOption = new Option<string>(
            name: "--report-name",
            description: "Name of the unified HTML report file.",
            getDefaultValue: () => "UnifiedMutationReport.html");

        var excludePatternsOption = new Option<string[]>(
            name: "--exclude-patterns",
            description: "Regex patterns to exclude projects (in addition to test projects). Can be specified multiple times.",
            getDefaultValue: () => new[] { @"\.Init$", @"\.CommunicatieModels$", @"\.Reqnroll$" });
        excludePatternsOption.AllowMultipleArgumentsPerToken = true;

        var rootCommand = new RootCommand("StrykerRunner - Run Stryker mutation testing across multiple projects and generate unified reports")
        {
            testProjectOption,
            solutionOption,
            outputDirOption,
            reportNameOption,
            excludePatternsOption
        };

        rootCommand.SetHandler(async (testProject, solution, outputDir, reportName, excludePatterns) =>
        {
            await RunStrykerAsync(testProject, solution, outputDir!, reportName, excludePatterns);
        }, testProjectOption, solutionOption, outputDirOption, reportNameOption, excludePatternsOption);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunStrykerAsync(FileInfo? testProject, FileInfo? solution, DirectoryInfo outputDir, string reportName, string[] excludePatterns)
    {
        var runTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var runOutputDir = Path.Combine(outputDir.FullName, runTimestamp);
        Directory.CreateDirectory(runOutputDir);

        var allFiles = new Dictionary<string, JsonElement>();

        var currentDir = Directory.GetCurrentDirectory();

        if (solution == null)
        {
            // Exclude *.Stryker.sln — those are generated artifacts, not primary solutions
            var slnFiles = Directory.GetFiles(currentDir, "*.sln")
                .Where(f => !Path.GetFileName(f).EndsWith(".Stryker.sln", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var slnxFiles = Directory.GetFiles(currentDir, "*.slnx");

            if (slnFiles.Length > 0)
                solution = new FileInfo(slnFiles[0]);
            else if (slnxFiles.Length > 0)
                solution = new FileInfo(slnxFiles[0]);
        }

        var hasLocalCsproj = testProject?.Exists ?? Directory.GetFiles(currentDir, "*.csproj").Length > 0;

        if (solution != null && solution.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SOLUTION MODE] Detected solution: {solution.FullName}");
            Console.ResetColor();

            var allProjects = DiscoverAllProjectsFromSolution(solution);
            Log($"Discovered {allProjects.Count} total project(s) from solution");

            var testProjects = allProjects
                .Where(p => Regex.IsMatch(Path.GetFileNameWithoutExtension(p.FullName), @"\.Tests?$", RegexOptions.IgnoreCase))
                .ToList();
            var sourceProjects = allProjects
                .Where(p => !Regex.IsMatch(Path.GetFileNameWithoutExtension(p.FullName), @"\.Tests?$", RegexOptions.IgnoreCase))
                .Where(p => !excludePatterns.Any(pattern => Regex.IsMatch(p.Name, pattern, RegexOptions.IgnoreCase)))
                .ToList();

            Log($"Test projects ({testProjects.Count}):");
            foreach (var tp in testProjects)
                Log($"  {tp.FullName}");

            Log($"Source projects after exclusion ({sourceProjects.Count}):");
            foreach (var sp in sourceProjects)
                Log($"  {sp.FullName}");

            if (testProjects.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARNING] No test projects found in solution.");
                Console.ResetColor();
                return;
            }

            if (sourceProjects.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: No source projects found to mutate in solution.");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[FOUND] {testProjects.Count} test project(s), {sourceProjects.Count} source project(s)");
            Console.ResetColor();

            foreach (var tp in testProjects)
            {
                // If the test project has a stryker-config.json with a "project" filter, honour it.
                // We write a temporary config that strips the broken "solution" reference so Stryker
                // runs in test-project mode and discovers source projects from build references instead.
                var configProject = ReadStrykerConfigProject(tp);
                Log($"Test project {tp.Name}: stryker-config.json project = {configProject ?? "(none)"}");

                if (configProject != null)
                {
                    var outDir = Path.Combine(runOutputDir, Path.GetFileNameWithoutExtension(tp.FullName));
                    await RunStrykerViaConfigAsync(tp, outDir, allFiles);
                    continue;
                }

                // No config: discover source references and run one Stryker invocation per pair.
                var relevantSources = sourceProjects
                    .Where(sp => ProjectReferenceExists(tp, sp))
                    .ToList();

                Log($"Test project {tp.Name}: {relevantSources.Count} relevant source(s): {string.Join(", ", relevantSources.Select(s => s.Name))}");

                if (relevantSources.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SKIP] Test project {tp.Name} has no source project references.");
                    Console.ResetColor();
                    continue;
                }

                foreach (var sp in relevantSources)
                {
                    await RunStrykerForProjectAsync(sp, tp, Path.Combine(runOutputDir, sp.Name), allFiles);
                }
            }
        }
        else
        {
            // Test-project mode: discover via test project
            if (testProject == null)
            {
                var csprojFiles = Directory.GetFiles(currentDir, "*.csproj");
                if (csprojFiles.Length == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: No .csproj or .sln/.slnx found. Please run from a project or solution directory.");
                    Console.ResetColor();
                    return;
                }
                testProject = new FileInfo(csprojFiles[0]);
            }

            if (!testProject.Exists)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Test project not found: {testProject.FullName}");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[PROJECT MODE] Detected project: {testProject.Name}");
            Console.ResetColor();

            await RunStrykerViaTestProjectAsync(testProject, runOutputDir, excludePatterns, allFiles);
        }

        GenerateUnifiedReport(allFiles, runOutputDir, reportName);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[DONE] Combined report generated: {Path.Combine(runOutputDir, reportName)}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[PATH] Run folder: {runOutputDir}");
        Console.ResetColor();
    }

    // Reads the "project" value from stryker-config.json in the test project directory, or null if absent.
    internal static string? ReadStrykerConfigProject(FileInfo testProject)
    {
        var configPath = Path.Combine(testProject.DirectoryName!, "stryker-config.json");
        if (!File.Exists(configPath)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("stryker-config", out var cfg)) return null;
            if (!cfg.TryGetProperty("project", out var proj)) return null;
            return proj.GetString();
        }
        catch { return null; }
    }

    // Runs Stryker from a test project directory in test-project mode (no solution).
    // Writes a temporary override config that strips "solution" (to avoid trying to build
    // the full solution, which may include projects with compile errors) and "reporters"
    // (controlled via CLI). The --msbuild-path arg ensures Stryker finds the .NET 10+ SDK.
    static async Task RunStrykerViaConfigAsync(FileInfo testProject, string outputDir, Dictionary<string, JsonElement> allFiles)
    {
        var configProject = ReadStrykerConfigProject(testProject) ?? testProject.Name;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[MUTATING] {configProject} (via stryker-config.json)");
        Console.ResetColor();

        var tempConfig = Path.Combine(testProject.DirectoryName!, "stryker-runner-override.json");
        try
        {
            WriteStrykerConfigOverride(testProject, tempConfig);
            Log($"Wrote override config: {tempConfig}");
            Log($"Override config contents: {File.ReadAllText(tempConfig)}");

            var msBuildArg = s_msBuildPath.Value is { } p ? $" --msbuild-path \"{p}\"" : string.Empty;
            Log($"MSBuild path: {s_msBuildPath.Value ?? "(auto-detect)"}");
            var arguments = $"stryker --config-file \"{tempConfig}\" --reporter json --output \"{outputDir}\"{msBuildArg}";
            Log($"Working directory: {testProject.DirectoryName}");
            Log($"Command: dotnet {arguments}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = testProject.DirectoryName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            await RunStrykerProcessAsync(startInfo, outputDir, allFiles);
        }
        finally
        {
            try { File.Delete(tempConfig); } catch { }
        }
    }

    // Writes a stryker-config override, carrying forward keys from stryker-config.json except:
    // - "solution": stripped so Stryker runs in test-project mode (avoids full solution build)
    // - "reporters": controlled via CLI (--reporter json)
    static void WriteStrykerConfigOverride(FileInfo testProject, string outputPath)
    {
        var originalConfig = Path.Combine(testProject.DirectoryName!, "stryker-config.json");

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName("stryker-config");
        writer.WriteStartObject();

        if (File.Exists(originalConfig))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(originalConfig));
            if (doc.RootElement.TryGetProperty("stryker-config", out var cfg))
            {
                foreach (var prop in cfg.EnumerateObject())
                {
                    if (prop.Name is "solution" or "reporters") continue;
                    prop.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        File.WriteAllBytes(outputPath, stream.ToArray());
    }

    // Runs Stryker for a specific source project from a test project directory.
    // Used when no stryker-config.json is present; --project filters the source project.
    static async Task RunStrykerForProjectAsync(FileInfo sourceProject, FileInfo testProject, string outputDir, Dictionary<string, JsonElement> allFiles)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[MUTATING] {sourceProject.Name}");
        Console.ResetColor();

        var msBuildArg = s_msBuildPath.Value is { } p ? $" --msbuild-path \"{p}\"" : string.Empty;
        var arguments = $"stryker --project \"{sourceProject.Name}\" --reporter json --output \"{outputDir}\"{msBuildArg}";
        Log($"Working directory: {testProject.DirectoryName}");
        Log($"Command: dotnet {arguments}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = testProject.DirectoryName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await RunStrykerProcessAsync(startInfo, outputDir, allFiles);
    }

    static async Task RunStrykerProcessAsync(ProcessStartInfo startInfo, string outputDir, Dictionary<string, JsonElement> allFiles)
    {
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Failed to start Stryker process.");
            Console.ResetColor();
            return;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Log($"Stryker exit code: {process.ExitCode}");

        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(output);

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(error);
            Console.ResetColor();
        }

        var jsonReport = FindJsonReport(outputDir);
        Log($"JSON report: {jsonReport ?? "(not found)"}");

        if (jsonReport != null)
        {
            var reportData = await File.ReadAllTextAsync(jsonReport);
            using var doc = JsonDocument.Parse(reportData);
            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                var count = files.EnumerateObject().Count();
                Log($"Merged {count} file(s) from report into unified results");
                foreach (var file in files.EnumerateObject())
                {
                    allFiles[file.Name] = file.Value.Clone();
                }
            }
        }
    }

    static void Log(string message)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [DBG] {message}");
        Console.ResetColor();
    }

    // Locates MSBuild.dll in the active .NET SDK. Stryker 4.x cannot auto-detect .NET 10+ SDK
    // so we pass it explicitly via --msbuild-path to prevent "No project found" failures.
    static readonly Lazy<string?> s_msBuildPath = new(() =>
    {
        try
        {
            var si = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--info",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(si)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Base Path:", StringComparison.OrdinalIgnoreCase))
                {
                    var basePath = t["Base Path:".Length..].Trim().TrimEnd('\\', '/');
                    var candidate = Path.Combine(basePath, "MSBuild.dll");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch { }
        return null;
    });

    static async Task RunStrykerViaTestProjectAsync(FileInfo testProject, string runOutputDir, string[] excludePatterns, Dictionary<string, JsonElement> allFiles)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[DISCOVER] Discovering project references from: {testProject.Name}");
        Console.ResetColor();

        var targetProjects = DiscoverTargetProjects(testProject, excludePatterns);

        if (targetProjects.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: No project references found in {testProject.Name}.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[FOUND] Found {targetProjects.Count} project(s) to mutate");
        Console.ResetColor();

        foreach (var targetProj in targetProjects)
        {
            var sourceProj = FindProjectFile(targetProj.ReferencePath, testProject.Directory!);

            if (sourceProj == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Could not find project file for {targetProj.Name}. Skipping...");
                Console.ResetColor();
                continue;
            }

            var projectOutputDir = Path.Combine(runOutputDir, targetProj.Name);
            await RunStrykerForProjectAsync(sourceProj, testProject, projectOutputDir, allFiles);
        }
    }

    internal static List<FileInfo> DiscoverAllProjectsFromSolution(FileInfo solution)
    {
        var projects = new List<FileInfo>();
        var solutionDir = solution.DirectoryName ?? Directory.GetCurrentDirectory();

        try
        {
            if (solution.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var doc = XDocument.Load(solution.FullName);
                var paths = doc.Descendants("Project")
                    .Select(e => e.Attribute("Path")?.Value)
                    .Where(p => p != null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p!);

                foreach (var relativePath in paths)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
                    projects.Add(new FileInfo(fullPath));
                }
            }
            else
            {
                var slnText = File.ReadAllText(solution.FullName);
                var projectLineRegex = new Regex(
                    @"Project\(""\{[^}]+\}""\)\s*=\s*""(?<name>[^""]+)""\s*,\s*""(?<path>[^""]+\.csproj)""\s*,",
                    RegexOptions.IgnoreCase);

                foreach (Match match in projectLineRegex.Matches(slnText))
                {
                    var relativePath = match.Groups["path"].Value;
                    var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
                    projects.Add(new FileInfo(fullPath));
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error parsing solution file: {ex.Message}");
            Console.ResetColor();
        }

        return projects;
    }

    internal static bool ProjectReferenceExists(FileInfo testProject, FileInfo sourceProject)
    {
        try
        {
            var doc = XDocument.Load(testProject.FullName);
            var sourceProjectName = Path.GetFileNameWithoutExtension(sourceProject.FullName);
            var projectReferences = doc.Descendants("ProjectReference")
                .Where(pr => pr.Attribute("Include") != null)
                .Select(pr => pr.Attribute("Include")!.Value);

            foreach (var refPath in projectReferences)
            {
                var refProjectName = Path.GetFileNameWithoutExtension(refPath);
                if (refProjectName.Equals(sourceProjectName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    internal static List<FileInfo> DiscoverTestProjectsFromSolution(FileInfo solution)
    {
        var testProjects = new List<FileInfo>();
        var solutionDir = solution.DirectoryName ?? Directory.GetCurrentDirectory();

        try
        {
            if (solution.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var doc = XDocument.Load(solution.FullName);
                var paths = doc.Descendants("Project")
                    .Select(e => e.Attribute("Path")?.Value)
                    .Where(p => p != null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p!);

                foreach (var relativePath in paths)
                {
                    var projName = Path.GetFileNameWithoutExtension(relativePath);
                    if (Regex.IsMatch(projName, @"\.Tests?$", RegexOptions.IgnoreCase))
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
                        testProjects.Add(new FileInfo(fullPath));
                    }
                }
            }
            else
            {
                var slnText = File.ReadAllText(solution.FullName);
                var projectLineRegex = new Regex(
                    @"Project\(""\{[^}]+\}""\)\s*=\s*""(?<name>[^""]+)""\s*,\s*""(?<path>[^""]+\.csproj)""\s*,",
                    RegexOptions.IgnoreCase);

                foreach (Match match in projectLineRegex.Matches(slnText))
                {
                    var projName = match.Groups["name"].Value;
                    var relativePath = match.Groups["path"].Value;

                    if (Regex.IsMatch(projName, @"\.Tests?$", RegexOptions.IgnoreCase))
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
                        testProjects.Add(new FileInfo(fullPath));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error parsing solution file: {ex.Message}");
            Console.ResetColor();
        }

        return testProjects;
    }

    internal static List<(string Name, string ReferencePath)> DiscoverTargetProjects(FileInfo testProject, string[] excludePatterns)
    {
        var targetProjects = new List<(string, string)>();

        try
        {
            var doc = XDocument.Load(testProject.FullName);
            var projectReferences = doc.Descendants("ProjectReference")
                .Where(pr => pr.Attribute("Include") != null)
                .Select(pr => pr.Attribute("Include")!.Value);

            foreach (var refPath in projectReferences)
            {
                if (!refPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    continue;

                var projName = Path.GetFileNameWithoutExtension(refPath);

                if (Regex.IsMatch(projName, @"\.Tests?$", RegexOptions.IgnoreCase))
                    continue;

                bool excluded = excludePatterns.Any(pattern =>
                    Regex.IsMatch(projName, pattern, RegexOptions.IgnoreCase));

                if (!excluded)
                {
                    targetProjects.Add((projName, refPath));
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error parsing test project file: {ex.Message}");
            Console.ResetColor();
        }

        return targetProjects;
    }

    internal static FileInfo? FindProjectFile(string relativePath, DirectoryInfo testProjectDir)
    {
        var fullPath = Path.Combine(testProjectDir.FullName, relativePath);
        if (File.Exists(fullPath))
            return new FileInfo(fullPath);

        var projName = Path.GetFileName(relativePath);
        var searchDir = testProjectDir.Parent;
        int maxLevels = 3;
        int currentLevel = 0;

        while (searchDir != null && currentLevel < maxLevels)
        {
            try
            {
                var found = Directory.GetFiles(searchDir.FullName, projName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null)
                    return new FileInfo(found);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            searchDir = searchDir.Parent;
            currentLevel++;
        }

        return null;
    }

    internal static string? FindJsonReport(string outputDir)
    {
        if (!Directory.Exists(outputDir))
            return null;

        var reportDir = Path.Combine(outputDir, "reports");
        if (!Directory.Exists(reportDir))
            return null;

        return Directory.GetFiles(reportDir, "mutation-report.json", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    static void GenerateUnifiedReport(Dictionary<string, JsonElement> allFiles, string outputDir, string reportName)
    {
        var report = new
        {
            schemaVersion = "1",
            thresholds = new { high = 80, low = 60, @break = 0 },
            files = allFiles
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var finalJson = JsonSerializer.Serialize(report, options);

        var htmlContent = new StringBuilder();
        htmlContent.AppendLine("<!DOCTYPE html>");
        htmlContent.AppendLine("<html>");
        htmlContent.AppendLine("<head>");
        htmlContent.AppendLine("  <meta charset=\"utf-8\">");
        htmlContent.AppendLine("  <script src=\"https://www.unpkg.com/mutation-testing-elements\"></script>");
        htmlContent.AppendLine("</head>");
        htmlContent.AppendLine("<body>");
        htmlContent.AppendLine("  <mutation-test-report-app></mutation-test-report-app>");
        htmlContent.AppendLine("  <script>");
        htmlContent.AppendLine("    const app = document.querySelector('mutation-test-report-app');");
        htmlContent.AppendLine($"    app.report = {finalJson};");
        htmlContent.AppendLine("  </script>");
        htmlContent.AppendLine("</body>");
        htmlContent.AppendLine("</html>");

        File.WriteAllText(Path.Combine(outputDir, reportName), htmlContent.ToString(), Encoding.UTF8);
    }
}
