using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

public class ClangFormatTask : MSBuildTask
{
    [Required]
    public ITaskItem[] InputFiles { get; set; }

    [Required]
    public string ClangFormatExe { get; set; }

    public ITaskItem[] IgnorePatterns { get; set; }

    /// <summary>
    /// Maximum number of concurrent clang-format processes.
    /// Can be a number (e.g., "4") or "auto" to use all logical processors.
    /// </summary>
    public string MaxProcesses { get; set; } = "1";

    /// <summary>
    /// Optional path to a global .clang-format configuration file.
    /// If empty, clang-format will search in the source tree.
    /// </summary>
    public string ConfigFile { get; set; }

    private List<Regex> _ignoreRegexList;
    private static bool _versionEmitted = false;

    public override bool Execute()
    {
        _ignoreRegexList = new List<Regex>();
        if (IgnorePatterns != null)
        {
            foreach (var item in IgnorePatterns)
            {
                string pattern = item.ItemSpec;
                if (!string.IsNullOrWhiteSpace(pattern))
                    _ignoreRegexList.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
        }

        string exeToRun = ResolveExecutable(ClangFormatExe);
        if (exeToRun == null)
        {
            Log.LogError($"Could not find clang-format executable: {ClangFormatExe}");
            return false;
        }

        if (!_versionEmitted)
        {
            EmitVersion(exeToRun);
            _versionEmitted = true;
        }

        int effectiveVerbosity = GetEffectiveVerbosity();
        int maxProcesses = Math.Max(1, ResolveMaxProcesses());

        var filesToFormat = new List<string>();
        var ignoredFiles = new List<string>();

        foreach (var item in InputFiles)
        {
            string file = item.ItemSpec;
            if (IsIgnored(file))
            {
                ignoredFiles.Add(file);
                continue;
            }

            string stamp = file + ".format.stamp";
            if (!HasFileChanged(file, stamp))
            {
                if (effectiveVerbosity >= 1)
                    Log.LogMessage(MessageImportance.Low, $"Skipping {file}, unchanged.");
                continue;
            }

            filesToFormat.Add(file);
        }

        if (filesToFormat.Count == 0)
        {
            Log.LogMessage(MessageImportance.High, "0 files have been reformatted.");
            if (effectiveVerbosity >= 2 && ignoredFiles.Count > 0)
            {
                foreach (var f in ignoredFiles)
                    Log.LogMessage(MessageImportance.Low, $"Ignored {f}");
            }
            return true;
        }

        int reformattedCount = 0;
        var success = true;
        var logQueue = new ConcurrentQueue<string>();

        Parallel.ForEach(filesToFormat, new ParallelOptions { MaxDegreeOfParallelism = maxProcesses }, file =>
        {
            if (effectiveVerbosity >= 1)
                logQueue.Enqueue($"Formatting {file}...");

            if (!RunClangFormat(exeToRun, file))
            {
                success = false;
            }
            else
            {
                UpdateStamp(file, file + ".format.stamp");
                Interlocked.Increment(ref reformattedCount);
            }
        });

        while (logQueue.TryDequeue(out var msg))
            Log.LogMessage(MessageImportance.High, msg);

        Log.LogMessage(MessageImportance.High, $"{reformattedCount} files have been reformatted.");

        if (effectiveVerbosity >= 2 && ignoredFiles.Count > 0)
        {
            foreach (var f in ignoredFiles)
                Log.LogMessage(MessageImportance.Low, $"Ignored {f}");
        }

        return success && !Log.HasLoggedErrors;
    }

    private void EmitVersion(string exe)
    {
        try
        {
            var proc = new Process();
            proc.StartInfo.FileName = exe;
            proc.StartInfo.Arguments = "--version";
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;

            proc.Start();
            string version = proc.StandardOutput.ReadLine();
            proc.WaitForExit();

            if (!string.IsNullOrEmpty(version))
                Log.LogMessage(MessageImportance.High, $"Using {version}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to detect clang-format version: {ex.Message}");
        }
    }

    private bool RunClangFormat(string exe, string file)
    {
        try
        {
            var proc = new Process();
            proc.StartInfo.FileName = exe;

            string args = "-i";
            if (!string.IsNullOrEmpty(ConfigFile))
                args += $" -style=file:\"{ConfigFile}\"";

            args += $" \"{file}\"";
            proc.StartInfo.Arguments = args;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;

            proc.Start();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                Log.LogError($"clang-format failed for {file} (exit code {proc.ExitCode})");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Exception running clang-format on {file}: {ex.Message}");
            return false;
        }
    }

    private bool IsIgnored(string file)
    {
        foreach (var regex in _ignoreRegexList)
        {
            if (regex.IsMatch(file))
                return true;
        }
        return false;
    }

    private bool HasFileChanged(string file, string stamp)
    {
        DateTime fileTime = File.GetLastWriteTimeUtc(file);
        DateTime stampTime = File.Exists(stamp) ? File.GetLastWriteTimeUtc(stamp) : DateTime.MinValue;
        return fileTime > stampTime;
    }

    private void UpdateStamp(string file, string stamp)
    {
        File.WriteAllText(stamp, string.Empty);
        File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow);
    }

    private string ResolveExecutable(string exe)
    {
        if (File.Exists(exe))
            return Path.GetFullPath(exe);

        if (!exe.Contains(Path.DirectorySeparatorChar.ToString()) &&
            !exe.Contains(Path.AltDirectorySeparatorChar.ToString()))
        {
            return exe;
        }

        return null;
    }

    private int ResolveMaxProcesses()
    {
        if (string.Equals(MaxProcesses, "auto", StringComparison.OrdinalIgnoreCase))
            return Environment.ProcessorCount;

        if (int.TryParse(MaxProcesses, out int n) && n > 0)
            return n;

        return 1;
    }

    private int GetEffectiveVerbosity()
    {
        int verbosity = 0;
        try
        {
            var verbosityProp = BuildEngine.GetType().GetProperty("Verbosity");
            if (verbosityProp != null)
            {
                var currentVerbosity = verbosityProp.GetValue(BuildEngine);
                int v = (int)currentVerbosity;
                if (v >= 3) // Detailed / Diagnostic
                    verbosity = 2;
                else if (v >= 2) // Normal
                    verbosity = 1;
            }
        }
        catch { }
        return verbosity;
    }
}
