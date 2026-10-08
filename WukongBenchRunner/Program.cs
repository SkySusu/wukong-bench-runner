using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = CliOptions.Parse(args);

try
{
    var runner = new BenchmarkRunner(options);
    var report = await runner.RunAsync();
    ReportWriter.Write(report, options.OutputPath);
    Console.WriteLine($"Done. Report saved to: {Path.GetFullPath(options.OutputPath)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    Environment.ExitCode = 1;
}

sealed record CliOptions(
    string? GameDir,
    string? ExePath,
    string? ConfigPath,
    string OutputPath,
    int TimeoutMinutes,
    bool DryRun,
    string? LaunchArgs,
    bool UseSteam)
{
    public static CliOptions Parse(string[] args)
    {
        string? gameDir = null;
        string? exePath = null;
        string? configPath = null;
        string outputPath = "wukong-benchmark-report.md";
        int timeoutMinutes = 20;
        bool dryRun = false;
        string? launchArgs = null;
        bool useSteam = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {arg}");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "--game-dir":
                    gameDir = Next();
                    break;
                case "--exe":
                    exePath = Next();
                    break;
                case "--config":
                    configPath = Next();
                    break;
                case "--out":
                    outputPath = Next();
                    break;
                case "--timeout-minutes":
                    timeoutMinutes = int.Parse(Next());
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--launch-args":
                    launchArgs = Next();
                    break;
                case "--use-steam":
                    useSteam = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new CliOptions(gameDir, exePath, configPath, outputPath, timeoutMinutes, dryRun, launchArgs, useSteam);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        WukongBenchRunner

        Usage:
          dotnet run --project WukongBenchRunner -- [options]

        Options:
          --game-dir <path>          Black Myth Wukong Benchmark Tool directory.
          --exe <path>               Benchmark executable. If omitted, it is searched inside --game-dir.
          --config <path>            GameUserSettings.ini path.
          --out <path>               Markdown report path. Default: wukong-benchmark-report.md
          --timeout-minutes <n>      Max wait time for each run. Default: 20
          --launch-args <args>       Extra command-line arguments for the benchmark executable.
          --use-steam                Launch through steam://rungameid/3132990 instead of direct exe.
          --dry-run                  Apply settings and print actions without launching the benchmark.
        """);
    }
}

sealed class BenchmarkRunner
{
    private const int SteamAppId = 3132990;
    private readonly CliOptions _options;

    public BenchmarkRunner(CliOptions options)
    {
        _options = options;
    }

    public async Task<BenchmarkReport> RunAsync()
    {
        var gameDir = ResolveGameDirectory();
        var configPath = ResolveConfigPath(gameDir);
        var exePath = _options.UseSteam ? $"steam://rungameid/{SteamAppId}" : ResolveExePath(gameDir);
        var systemInfo = SystemInfoCollector.Collect();
        var runs = new List<BenchmarkRunResult>();

        foreach (var profile in BenchmarkProfiles.All)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {profile.Name} ===");
            Console.WriteLine("Applying benchmark settings...");

            IniEditor.ApplyProfile(configPath, profile);

            if (_options.DryRun)
            {
                runs.Add(new BenchmarkRunResult(
                    profile.Name,
                    profile.Description,
                    profile.Settings,
                    StartedAt: DateTimeOffset.Now,
                    FinishedAt: DateTimeOffset.Now,
                    ExitCode: null,
                    ParsedMetrics: new Dictionary<string, string>(),
                    SourceFiles: Array.Empty<string>(),
                    Note: "Dry run: settings were written, benchmark was not launched."));
                continue;
            }

            var beforeRun = DateTimeOffset.Now;
            var knownFiles = ResultScanner.Snapshot(gameDir);
            var exitCode = _options.UseSteam
                ? await LaunchViaSteamAndWaitAsync(gameDir)
                : await LaunchAndWaitAsync(exePath, gameDir);
            var afterRun = DateTimeOffset.Now;
            var freshFiles = ResultScanner.FindFreshFiles(gameDir, knownFiles, beforeRun);
            var metrics = ResultScanner.ParseMetrics(freshFiles);

            runs.Add(new BenchmarkRunResult(
                profile.Name,
                profile.Description,
                profile.Settings,
                beforeRun,
                afterRun,
                exitCode,
                metrics,
                freshFiles.Select(Path.GetFullPath).ToArray(),
                metrics.Count == 0
                    ? "No FPS metrics were found automatically. Check the listed fresh files or attach a screenshot from the benchmark result screen."
                    : null));
        }

        return new BenchmarkReport(
            DateTimeOffset.Now,
            gameDir,
            exePath,
            configPath,
            systemInfo,
            runs);
    }

    private string ResolveGameDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.GameDir))
        {
            return ExistingDirectory(_options.GameDir, "--game-dir");
        }

        var candidates = SteamLocator.FindLibraryFolders()
            .Select(path => Path.Combine(path, "steamapps", "common", "Black Myth Wukong Benchmark Tool"))
            .ToList();

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam",
            "steamapps",
            "common",
            "Black Myth Wukong Benchmark Tool"));

        var found = candidates.FirstOrDefault(Directory.Exists);
        if (found is not null)
        {
            return found;
        }

        throw new DirectoryNotFoundException(
            "Could not find Black Myth Wukong Benchmark Tool. Pass its folder with --game-dir.");
    }

    private string ResolveConfigPath(string gameDir)
    {
        if (!string.IsNullOrWhiteSpace(_options.ConfigPath))
        {
            return ExistingFile(_options.ConfigPath, "--config");
        }

        var candidates = new[]
        {
            Path.Combine(gameDir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "b1",
                "Saved",
                "Config",
                "Windows",
                "GameUserSettings.ini")
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(candidates[0])!);
        File.WriteAllText(candidates[0], string.Empty, Encoding.UTF8);
        return candidates[0];
    }

    private string ResolveExePath(string gameDir)
    {
        if (!string.IsNullOrWhiteSpace(_options.ExePath))
        {
            return ExistingFile(_options.ExePath, "--exe");
        }

        var exeFiles = Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.AllDirectories)
            .Where(path => !path.Contains("CrashReportClient", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.FullName.Contains(Path.Combine("Binaries", "Win64"), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.Length)
            .ToList();

        var found = exeFiles.FirstOrDefault();
        if (found is not null)
        {
            return found.FullName;
        }

        throw new FileNotFoundException(
            $"Could not find a benchmark executable in {gameDir}. You can also launch through Steam app id {SteamAppId}, but direct exe mode is preferred.");
    }

    private async Task<int?> LaunchAndWaitAsync(string exePath, string gameDir)
    {
        Console.WriteLine($"Launching: {exePath}");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = _options.LaunchArgs ?? string.Empty,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? gameDir,
            UseShellExecute = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("Process.Start returned null.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_options.TimeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Timeout reached; closing benchmark process.");
            TryClose(process);
            return null;
        }
    }

    private async Task<int?> LaunchViaSteamAndWaitAsync(string gameDir)
    {
        Console.WriteLine($"Launching through Steam app id {SteamAppId}...");

        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://rungameid/{SteamAppId}",
            UseShellExecute = true
        });

        var deadline = DateTimeOffset.Now.AddMinutes(_options.TimeoutMinutes);
        var processName = Path.GetFileNameWithoutExtension(ResolveExePath(gameDir));
        Process? benchmarkProcess = null;

        while (DateTimeOffset.Now < deadline)
        {
            benchmarkProcess = Process.GetProcesses()
                .FirstOrDefault(process =>
                    string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

            if (benchmarkProcess is not null)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        if (benchmarkProcess is null)
        {
            Console.WriteLine("Benchmark process was not detected after Steam launch.");
            return null;
        }

        var remaining = deadline - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            TryClose(benchmarkProcess);
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(remaining);
        try
        {
            await benchmarkProcess.WaitForExitAsync(timeoutCts.Token);
            return benchmarkProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Timeout reached; closing benchmark process.");
            TryClose(benchmarkProcess);
            return null;
        }
    }

    private static void TryClose(Process process)
    {
        try
        {
            if (!process.HasExited && process.CloseMainWindow())
            {
                process.WaitForExit(TimeSpan.FromSeconds(15));
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already be gone; the report will still include the timeout note.
        }
    }

    private static string ExistingDirectory(string path, string argName)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{argName} does not exist: {path}");
        }

        return Path.GetFullPath(path);
    }

    private static string ExistingFile(string path, string argName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{argName} does not exist: {path}");
        }

        return Path.GetFullPath(path);
    }
}

sealed record BenchmarkProfile(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Settings);

static class BenchmarkProfiles
{
    public static readonly BenchmarkProfile Cpu = new(
        "CPU test",
        "Low resolution and low graphics quality reduce GPU pressure, so scene preparation, draw calls and frame submission are limited more by CPU work.",
        new Dictionary<string, string>
        {
            ["ResolutionSizeX"] = "1280",
            ["ResolutionSizeY"] = "720",
            ["LastUserConfirmedResolutionSizeX"] = "1280",
            ["LastUserConfirmedResolutionSizeY"] = "720",
            ["FullscreenMode"] = "1",
            ["PreferredFullscreenMode"] = "1",
            ["bUseVSync"] = "False",
            ["FrameRateLimit"] = "0.000000",
            ["sg.ResolutionQuality"] = "50.000000",
            ["sg.ViewDistanceQuality"] = "0",
            ["sg.AntiAliasingQuality"] = "0",
            ["sg.ShadowQuality"] = "0",
            ["sg.GlobalIlluminationQuality"] = "0",
            ["sg.ReflectionQuality"] = "0",
            ["sg.PostProcessQuality"] = "0",
            ["sg.TextureQuality"] = "0",
            ["sg.EffectsQuality"] = "0",
            ["sg.FoliageQuality"] = "0",
            ["sg.ShadingQuality"] = "0",
            ["bMotionBlur"] = "False",
            ["bUseRayTracing"] = "False",
            ["bEnableRayTracing"] = "False",
            ["RayTracingQuality"] = "0"
        });

    public static readonly BenchmarkProfile Gpu = new(
        "GPU test",
        "High resolution scale and cinematic graphics quality maximize pixel, lighting, shadow, texture and post-processing work on the video card.",
        new Dictionary<string, string>
        {
            ["ResolutionSizeX"] = "1920",
            ["ResolutionSizeY"] = "1080",
            ["LastUserConfirmedResolutionSizeX"] = "1920",
            ["LastUserConfirmedResolutionSizeY"] = "1080",
            ["FullscreenMode"] = "1",
            ["PreferredFullscreenMode"] = "1",
            ["bUseVSync"] = "False",
            ["FrameRateLimit"] = "0.000000",
            ["sg.ResolutionQuality"] = "100.000000",
            ["sg.ViewDistanceQuality"] = "3",
            ["sg.AntiAliasingQuality"] = "3",
            ["sg.ShadowQuality"] = "3",
            ["sg.GlobalIlluminationQuality"] = "3",
            ["sg.ReflectionQuality"] = "3",
            ["sg.PostProcessQuality"] = "3",
            ["sg.TextureQuality"] = "3",
            ["sg.EffectsQuality"] = "3",
            ["sg.FoliageQuality"] = "3",
            ["sg.ShadingQuality"] = "3",
            ["bMotionBlur"] = "False",
            ["bUseRayTracing"] = "True",
            ["bEnableRayTracing"] = "True",
            ["RayTracingQuality"] = "3"
        });

    public static readonly IReadOnlyList<BenchmarkProfile> All = new[] { Cpu, Gpu };
}

static class IniEditor
{
    private const string SettingsSection = "/Script/Engine.GameUserSettings";

    public static void ApplyProfile(string path, BenchmarkProfile profile)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        EnsureSection(lines, SettingsSection);
        foreach (var pair in profile.Settings)
        {
            SetValue(lines, SettingsSection, pair.Key, pair.Value);
        }

        var backup = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        if (File.Exists(path))
        {
            File.Copy(path, backup, overwrite: false);
            Console.WriteLine($"Config backup: {backup}");
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void EnsureSection(List<string> lines, string section)
    {
        if (!lines.Any(line => IsSection(line, section)))
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
        }
    }

    private static void SetValue(List<string> lines, string section, string key, string value)
    {
        var sectionStart = lines.FindIndex(line => IsSection(line, section));
        var sectionEnd = lines.FindIndex(sectionStart + 1, IsAnySection);
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        var keyIndex = -1;
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                keyIndex = i;
                break;
            }
        }

        if (keyIndex >= 0)
        {
            lines[keyIndex] = $"{key}={value}";
        }
        else
        {
            lines.Insert(sectionEnd, $"{key}={value}");
        }
    }

    private static bool IsSection(string line, string section) =>
        string.Equals(line.Trim(), $"[{section}]", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnySection(string line) =>
        line.TrimStart().StartsWith('[') && line.TrimEnd().EndsWith(']');
}

static class SteamLocator
{
    public static IEnumerable<string> FindLibraryFolders()
    {
        var steamRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam");
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(vdfPath))
        {
            yield break;
        }

        yield return steamRoot;

        foreach (var line in File.ReadLines(vdfPath))
        {
            var match = Regex.Match(line, "\"path\"\\s+\"(?<path>.+?)\"");
            if (match.Success)
            {
                yield return match.Groups["path"].Value.Replace(@"\\", @"\");
            }
        }
    }
}

static class ResultScanner
{
    private static readonly string[] Extensions =
    {
        ".log", ".txt", ".csv", ".json", ".xml", ".ini"
    };

    public static HashSet<string> Snapshot(string gameDir)
    {
        return SafeEnumerate(gameDir)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> FindFreshFiles(
        string gameDir,
        HashSet<string> knownFiles,
        DateTimeOffset runStartedAt)
    {
        var localB1 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "b1");

        var roots = new[] { gameDir, localB1 }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return roots
            .SelectMany(SafeEnumerate)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => !knownFiles.Contains(file.FullName)
                || file.LastWriteTimeUtc >= runStartedAt.UtcDateTime.AddSeconds(-5))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(20)
            .Select(file => file.FullName)
            .ToArray();
    }

    public static Dictionary<string, string> ParseMetrics(IReadOnlyList<string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            AddFirst(result, "Average FPS", Match(text, @"(?:Average|Avg)\s*(?:Frame\s*Rate|FPS)?\s*[:=]\s*(\d+(?:[.,]\d+)?)"));
            AddFirst(result, "Minimum FPS", Match(text, @"(?:Minimum|Min)\s*(?:Frame\s*Rate|FPS)?\s*[:=]\s*(\d+(?:[.,]\d+)?)"));
            AddFirst(result, "Maximum FPS", Match(text, @"(?:Maximum|Max)\s*(?:Frame\s*Rate|FPS)?\s*[:=]\s*(\d+(?:[.,]\d+)?)"));
            AddFirst(result, "1% Low FPS", Match(text, @"1%\s*(?:Low|Lows?)\s*(?:FPS)?\s*[:=]\s*(\d+(?:[.,]\d+)?)"));
            AddFirst(result, "Frame time", Match(text, @"(?:Frame\s*Time|Frametime)\s*[:=]\s*(\d+(?:[.,]\d+)?)\s*ms"));
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Replace(',', '.') : null;
    }

    private static void AddFirst(Dictionary<string, string> result, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !result.ContainsKey(key))
        {
            result[key] = value;
        }
    }
}

static class SystemInfoCollector
{
    public static SystemInfo Collect()
    {
        return new SystemInfo(
            Cpu: QueryPowerShell("(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)") ?? RuntimeInformation.ProcessArchitecture.ToString(),
            Gpu: QueryPowerShell("(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join '; '") ?? "Unknown",
            Ram: QueryPowerShell("\"{0:N1} GB\" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)") ?? "Unknown",
            Os: RuntimeInformation.OSDescription,
            MachineName: Environment.MachineName);
    }

    private static string? QueryPowerShell(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

static class ReportWriter
{
    public static void Write(BenchmarkReport report, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Black Myth: Wukong Benchmark Tool report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("## Computer");
        sb.AppendLine();
        sb.AppendLine($"- CPU: {report.System.Cpu}");
        sb.AppendLine($"- GPU: {report.System.Gpu}");
        sb.AppendLine($"- RAM: {report.System.Ram}");
        sb.AppendLine($"- OS: {report.System.Os}");
        sb.AppendLine($"- Machine: {report.System.MachineName}");
        sb.AppendLine();
        sb.AppendLine("## Benchmark paths");
        sb.AppendLine();
        sb.AppendLine($"- Game directory: `{report.GameDir}`");
        sb.AppendLine($"- Executable: `{report.ExePath}`");
        sb.AppendLine($"- Config: `{report.ConfigPath}`");
        sb.AppendLine();

        foreach (var run in report.Runs)
        {
            sb.AppendLine($"## {run.Name}");
            sb.AppendLine();
            sb.AppendLine(run.Description);
            sb.AppendLine();
            sb.AppendLine($"- Started: {run.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"- Finished: {run.FinishedAt:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"- Exit code: {(run.ExitCode.HasValue ? run.ExitCode.Value.ToString() : "timeout/not available")}");
            sb.AppendLine();
            sb.AppendLine("### Result");
            sb.AppendLine();

            if (run.ParsedMetrics.Count == 0)
            {
                sb.AppendLine("- Automatic parser did not find FPS metrics.");
            }
            else
            {
                foreach (var metric in run.ParsedMetrics)
                {
                    sb.AppendLine($"- {metric.Key}: {metric.Value}");
                }
            }

            if (!string.IsNullOrWhiteSpace(run.Note))
            {
                sb.AppendLine($"- Note: {run.Note}");
            }

            sb.AppendLine();
            sb.AppendLine("### Settings");
            sb.AppendLine();
            foreach (var setting in run.Settings)
            {
                sb.AppendLine($"- `{setting.Key}` = `{setting.Value}`");
            }

            if (run.SourceFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Parsed/source files");
                sb.AppendLine();
                foreach (var file in run.SourceFiles)
                {
                    sb.AppendLine($"- `{file}`");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("## JSON copy");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}

sealed record SystemInfo(string Cpu, string Gpu, string Ram, string Os, string MachineName);

sealed record BenchmarkRunResult(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Settings,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int? ExitCode,
    IReadOnlyDictionary<string, string> ParsedMetrics,
    IReadOnlyList<string> SourceFiles,
    string? Note);

sealed record BenchmarkReport(
    DateTimeOffset GeneratedAt,
    string GameDir,
    string ExePath,
    string ConfigPath,
    SystemInfo System,
    IReadOnlyList<BenchmarkRunResult> Runs);
