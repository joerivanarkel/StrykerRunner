using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StrykerRunner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var testProjectOption = new Option<FileInfo?>(
            name: "--test-project",
            description: "Path to the test project .csproj file. If not provided, searches for a .csproj in the current directory.",
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
            outputDirOption,
            reportNameOption,
            excludePatternsOption
        };

        rootCommand.SetHandler(async (testProject, outputDir, reportName, excludePatterns) =>
        {
            await RunStrykerAsync(testProject, outputDir!, reportName, excludePatterns);
        }, testProjectOption, outputDirOption, reportNameOption, excludePatternsOption);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunStrykerAsync(FileInfo? testProject, DirectoryInfo outputDir, string reportName, string[] excludePatterns)
    {
        // Find test project
        if (testProject == null)
        {
            var csprojFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
            if (csprojFiles.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: No .csproj found in the current directory. Please specify a test project with --test-project or run from a project directory.");
                Console.ResetColor();
                return;
            }
            testProject = new FileInfo(csprojFiles[0]);
        }

        if (!testProject.Exists)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Test project file not found: {testProject.FullName}");
            Console.ResetColor();
            return;
        }

        // Create timestamped output directory
        var runTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var runOutputDir = Path.Combine(outputDir.FullName, runTimestamp);
        Directory.CreateDirectory(runOutputDir);

        // Discover target projects
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[DISCOVER] Discovering project references from test project...");
        Console.ResetColor();

        var targetProjects = DiscoverTargetProjects(testProject, excludePatterns);

        if (targetProjects.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: No project references found to mutate. Make sure your test project references the projects you want to test.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[FOUND] Found {targetProjects.Count} project(s) to mutate:");
        Console.ResetColor();
        foreach (var proj in targetProjects)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"   - {proj.Name}");
            Console.ResetColor();
        }

        // Run Stryker for each target project
        var allFiles = new Dictionary<string, JsonElement>();

        foreach (var targetProj in targetProjects)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[TARGET] Targeting: {targetProj.Name}");
            Console.ResetColor();

            // Locate the source project
            var sourceProj = FindProjectFile(targetProj.ReferencePath, testProject.Directory!);

            if (sourceProj == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Could not find project file for {targetProj.Name}. Skipping...");
                Console.ResetColor();
                continue;
            }

            var projectOutputDir = Path.Combine(runOutputDir, targetProj.Name);

            // Run Stryker
            await RunStrykerForProjectAsync(sourceProj, testProject, projectOutputDir);

            // Collect JSON report
            var jsonReport = FindJsonReport(projectOutputDir);
            if (jsonReport != null)
            {
                var reportData = await File.ReadAllTextAsync(jsonReport);
                using var doc = JsonDocument.Parse(reportData);
                if (doc.RootElement.TryGetProperty("files", out var files))
                {
                    foreach (var file in files.EnumerateObject())
                    {
                        allFiles[file.Name] = file.Value.Clone();
                    }
                }
            }
        }

        // Generate unified report
        GenerateUnifiedReport(allFiles, runOutputDir, reportName);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[DONE] Combined report generated: {Path.Combine(runOutputDir, reportName)}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[PATH] Run folder: {runOutputDir}");
        Console.ResetColor();
    }

    static List<(string Name, string ReferencePath)> DiscoverTargetProjects(FileInfo testProject, string[] excludePatterns)
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

                // Skip test projects
                if (Regex.IsMatch(projName, @"\.Test$", RegexOptions.IgnoreCase))
                    continue;

                // Skip excluded patterns
                bool excluded = false;
                foreach (var pattern in excludePatterns)
                {
                    if (Regex.IsMatch(projName, pattern, RegexOptions.IgnoreCase))
                    {
                        excluded = true;
                        break;
                    }
                }

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

    static FileInfo? FindProjectFile(string relativePath, DirectoryInfo testProjectDir)
    {
        // First try relative to test project
        var fullPath = Path.Combine(testProjectDir.FullName, relativePath);
        if (File.Exists(fullPath))
        {
            return new FileInfo(fullPath);
        }

        // Search in parent directories
        var projName = Path.GetFileName(relativePath);
        var searchDir = testProjectDir.Parent;
        
        while (searchDir != null)
        {
            var found = Directory.GetFiles(searchDir.FullName, projName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found != null)
            {
                return new FileInfo(found);
            }
            searchDir = searchDir.Parent;
        }

        return null;
    }

    static async Task RunStrykerForProjectAsync(FileInfo sourceProject, FileInfo testProject, string outputDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"stryker --project \"{sourceProject.FullName}\" --test-project \"{testProject.FullName}\" --reporter json --output \"{outputDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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

        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(error);
            Console.ResetColor();
        }
    }

    static string? FindJsonReport(string outputDir)
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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var finalJson = JsonSerializer.Serialize(report, options);

        var htmlTemplate = $@"<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <script src=""https://www.unpkg.com/mutation-testing-elements""></script>
</head>
<body>
  <mutation-test-report-app></mutation-test-report-app>
  <script>
    const app = document.querySelector('mutation-test-report-app');
    app.report = {finalJson};
  </script>
</body>
</html>";

        File.WriteAllText(Path.Combine(outputDir, reportName), htmlTemplate, Encoding.UTF8);
    }
}
