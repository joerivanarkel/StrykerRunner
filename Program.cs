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

        var solutionOption = new Option<FileInfo?>(
            name: "--solution",
            description: "Path to a .sln or .slnx solution file. If not provided, auto-detects a solution in the current directory.",
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
        // Create timestamped output directory
        var runTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var runOutputDir = Path.Combine(outputDir.FullName, runTimestamp);
        Directory.CreateDirectory(runOutputDir);

        var allFiles = new Dictionary<string, JsonElement>();

        // Resolve solution: explicit flag → auto-detect .sln → auto-detect .slnx
        if (solution == null && testProject == null)
        {
            var currentDir = Directory.GetCurrentDirectory();
            var slnFiles = Directory.GetFiles(currentDir, "*.sln");
            var slnxFiles = Directory.GetFiles(currentDir, "*.slnx");

            if (slnFiles.Length > 0)
                solution = new FileInfo(slnFiles[0]);
            else if (slnxFiles.Length > 0)
                solution = new FileInfo(slnxFiles[0]);
        }

        if (solution != null)
        {
            // Solution flow
            if (!solution.Exists)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Solution file not found: {solution.FullName}");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SOLUTION] Using solution: {solution.FullName}");
            Console.ResetColor();

            var testProjects = DiscoverTestProjectsFromSolution(solution);

            if (testProjects.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: No test projects found in the solution. Test projects must match the pattern '*.Tests' or '*.Test'.");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[FOUND] Found {testProjects.Count} test project(s) in solution:");
            Console.ResetColor();
            foreach (var tp in testProjects)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"   - {tp.Name}");
                Console.ResetColor();
            }

            foreach (var tp in testProjects)
            {
                await RunStrykerForTestProjectAsync(tp, runOutputDir, excludePatterns, allFiles);
            }
        }
        else
        {
            // Single test-project flow
            if (testProject == null)
            {
                var csprojFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
                if (csprojFiles.Length == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: No .csproj found in the current directory. Please specify a test project with --test-project, a solution with --solution, or run from a project/solution directory.");
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

            await RunStrykerForTestProjectAsync(testProject, runOutputDir, excludePatterns, allFiles);
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

    static async Task RunStrykerForTestProjectAsync(FileInfo testProject, string runOutputDir, string[] excludePatterns, Dictionary<string, JsonElement> allFiles)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[DISCOVER] Discovering project references from test project: {testProject.Name}");
        Console.ResetColor();

        var targetProjects = DiscoverTargetProjects(testProject, excludePatterns);

        if (targetProjects.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: No project references found to mutate for {testProject.Name}. Skipping...");
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

        foreach (var targetProj in targetProjects)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[TARGET] Targeting: {targetProj.Name}");
            Console.ResetColor();

            var sourceProj = FindProjectFile(targetProj.ReferencePath, testProject.Directory!);

            if (sourceProj == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Could not find project file for {targetProj.Name}. Skipping...");
                Console.ResetColor();
                continue;
            }

            var projectOutputDir = Path.Combine(runOutputDir, targetProj.Name);

            await RunStrykerForProjectAsync(sourceProj, testProject, projectOutputDir);

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
    }

    static List<FileInfo> DiscoverTestProjectsFromSolution(FileInfo solution)
    {
        var testProjects = new List<FileInfo>();
        var solutionDir = solution.DirectoryName ?? Directory.GetCurrentDirectory();

        try
        {
            if (solution.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                // .slnx is XML: <Solution><Project Path="relative/path.csproj" /></Solution>
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
                // .sln text format: Project(...) = "Name", "path\file.csproj", "{GUID}"
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
                if (Regex.IsMatch(projName, @"\.Tests?$", RegexOptions.IgnoreCase))
                    continue;

                // Skip excluded patterns
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

    static FileInfo? FindProjectFile(string relativePath, DirectoryInfo testProjectDir)
    {
        // First try relative to test project
        var fullPath = Path.Combine(testProjectDir.FullName, relativePath);
        if (File.Exists(fullPath))
        {
            return new FileInfo(fullPath);
        }

        // Search in parent directories (limited scope to avoid permission issues)
        var projName = Path.GetFileName(relativePath);
        var searchDir = testProjectDir.Parent;
        int maxLevels = 3; // Limit search depth to avoid scanning entire filesystem
        int currentLevel = 0;
        
        while (searchDir != null && currentLevel < maxLevels)
        {
            try
            {
                var found = Directory.GetFiles(searchDir.FullName, projName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null)
                {
                    return new FileInfo(found);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we don't have permission to access
            }
            catch (IOException)
            {
                // Skip IO errors and continue searching
            }
            
            searchDir = searchDir.Parent;
            currentLevel++;
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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var finalJson = JsonSerializer.Serialize(report, options);

        // Write HTML with embedded JSON report
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
