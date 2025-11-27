using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

public class ClangFormatTask : MSBuildTask
{
    // Directory to start scanning for source files (project dir by default)
    [Required]
    public string RootDirectory { get; set; }

    [Required]
    public string ClangFormatToolPath { get; set; }

    // Directory where stamp files are stored (intermediate dir)
    [Required]
    public string StampDirectory { get; set; }

    // Semicolon separated extensions, e.g. ".cpp;.c;.h;.hpp" is default.
    [Required]
    public string Extensions { get; set; } = ".cpp;.c;.h;.hpp";

    // ItemGroup of regex patterns to ignore (passed from MSBuild)
    public ITaskItem[] IgnorePatterns { get; set; }

    // Number or "auto"
    public string MaxProcesses { get; set; } = "1";

    // Optional global config file path (if empty, clang-format will search)
    public string ConfigFile { get; set; }

    private List<Regex> _ignoreRegexList;

    private static int _seenNodeId = 0;

    private int NodeId
    {
        get
        {
            var engine = BuildEngine as IBuildEngine6;
            var props = engine.GetGlobalProperties();
            return (engine != null && engine.GetGlobalProperties().TryGetValue("VSTEL_CurrentSolutionBuildID", out string val) && int.TryParse(val, out int res)) ? res : -1;
        }
    }

    private bool VersionAlreadyEmittedThisBuild
    {
        get
        {
            return _seenNodeId == NodeId;
        }
    }

    private void MarkVersionEmitted()
    {
        _seenNodeId = NodeId;
    }

    public override bool Execute()
    {
        Debugger.Launch();

        try
        {
            // Normalize root
            if (string.IsNullOrWhiteSpace(RootDirectory))
            {
                Log.LogError("RootDirectory must be set.");
                return false;
            }
            var root = RootDirectory;
            if (!Path.IsPathRooted(root))
                root = Path.GetFullPath(root);

            if (!Directory.Exists(root))
            {
                Log.LogError($"RootDirectory does not exist: {root}");
                return false;
            }

            // Build ignore regex list
            _ignoreRegexList = new List<Regex>();
            if (IgnorePatterns != null)
            {
                foreach (var item in IgnorePatterns)
                {
                    var pattern = item.ItemSpec;
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        try
                        {
                            _ignoreRegexList.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning($"Invalid ignore regex '{pattern}': {ex.Message}");
                        }
                    }
                }
            }

            // Resolve clang-format executable
            string exeToRun = ResolveExecutable(ClangFormatToolPath);
            if (exeToRun == null)
            {
                Log.LogError($"Could not find clang-format executable: {ClangFormatToolPath}");
                return false;
            }

            IEnumerable<string> collected = null;
            {
                // Parse extensions
                var exts = ParseExtensions(Extensions);
                if (exts.Length == 0)
                {
                    Log.LogWarning($"No extensions provided in Extensions property ('{Extensions}').");
                    return false;
                }

                // Collect files recursively (robust against access errors)
                collected = CollectSourceFiles(root, exts);
            }
            if (collected == null || !collected.Any())
            {
                Log.LogMessage("No input files available for ClangFormat.");
                return true;
            }

            // Emit clang-format version once
            if (!VersionAlreadyEmittedThisBuild)
            {
                EmitVersion(exeToRun);
                MarkVersionEmitted();
            }

            // Ensure stamp directory exists
            Directory.CreateDirectory(StampDirectory);

            int effectiveVerbosity = GetEffectiveVerbosity();
            int maxProcesses = ResolveMaxProcesses();

            var filesToFormat = new List<string>();
            var ignoredFiles = new List<string>();

            foreach (var file in collected)
            {
                if (IsIgnored(file))
                {
                    ignoredFiles.Add(file);
                    continue;
                }

                string stampFileName = MakeStampFileName(root, file);
                string stampPath = Path.Combine(StampDirectory, stampFileName);

                if (!HasFileChanged(file, stampPath))
                {
                    if (effectiveVerbosity >= 1)
                        Log.LogMessage(MessageImportance.Low, $"Skipping {file}, unchanged.");
                    continue;
                }

                filesToFormat.Add(file);
            }

            int reformattedCount = 0;
            var success = true;

            if (filesToFormat.Count > 0)
            {
                var logQueue = new ConcurrentQueue<string>();

                Parallel.ForEach(filesToFormat, new ParallelOptions { MaxDegreeOfParallelism = maxProcesses }, file =>
                {
                    if (effectiveVerbosity >= 1)
                        logQueue.Enqueue($"Formatting {file}...");

                    bool ok = RunClangFormatCapture(exeToRun, file, ConfigFile,
                        out string stdout, out string stderr, out string fullCommand, out int exitCode);

                    if (!ok)
                    {
                        if (!string.IsNullOrEmpty(fullCommand))
                            logQueue.Enqueue($"Command executed: {fullCommand}");
                        if (!string.IsNullOrWhiteSpace(stdout))
                            logQueue.Enqueue(stdout);
                        if (!string.IsNullOrWhiteSpace(stderr))
                            logQueue.Enqueue(stderr);

                        success = false;
                    }
                    else
                    {
                        // Compute hash in parallel
                        string hash = ComputeFileHash(file);
                        if (hash != null)
                        {
                            string stampFileName = MakeStampFileName(root, file);
                            string stampPath = Path.Combine(StampDirectory, stampFileName);
                            UpdateStamp(stampPath, hash);
                        }
                        Interlocked.Increment(ref reformattedCount);
                    }
                });

                // Emit queued logs
                while (logQueue.TryDequeue(out var m))
                    Log.LogMessage(MessageImportance.High, m);
            }

            Log.LogMessage(MessageImportance.High, $"{reformattedCount} of files have been reformatted ({ignoredFiles.Count()} ignored).");

            if (effectiveVerbosity >= 2 && ignoredFiles.Count > 0)
            {
                foreach (var f in ignoredFiles)
                    Log.LogMessage(MessageImportance.Low, $"Ignored {f}");
            }

            return success && !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogError($"ClangFormatTask failed: {ex.Message}");
            return false;
        }
    }

    private string[] ParseExtensions(string extensions)
    {
        if (string.IsNullOrWhiteSpace(extensions))
            return Array.Empty<string>();

        return extensions
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().StartsWith(".") ? s.Trim() : "." + s.Trim())
            .ToArray();
    }

    private IEnumerable<string> CollectSourceFiles(string rootDir, string[] exts)
    {
        // Normalize extensions for fast lookup
        var extSet = new HashSet<string>(exts.Select(e => e.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<string>();

        try
        {
            var dirs = new Stack<string>();
            dirs.Push(rootDir);

            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();

                IEnumerable<string> subdirs = Enumerable.Empty<string>();
                IEnumerable<string> files = Enumerable.Empty<string>();

                // Try reading subdirectories
                try
                {
                    subdirs = Directory.EnumerateDirectories(dir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (PathTooLongException) { continue; }

                // Try reading files
                try
                {
                    files = Directory.EnumerateFiles(dir);
                }
                catch (UnauthorizedAccessException) { }
                catch (PathTooLongException) { }

                // Add matched files
                foreach (var f in files)
                {
                    string ext = Path.GetExtension(f);
                    if (extSet.Contains(ext))
                        results.Add(f);
                }

                // Push subdirectories for DFS
                foreach (string s in subdirs)
                    dirs.Push(s);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Error while collecting source files: {ex.Message}");
        }

        // De-duplicate and sort
        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private string GetRelativePath(string basePath, string fullPath)
    {
        try
        {
            var baseUri = new Uri(AppendDirectorySeparator(basePath));
            var fullUri = new Uri(fullPath);
            var relUri = baseUri.MakeRelativeUri(fullUri);
            return Uri.UnescapeDataString(relUri.ToString().Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            // fallback: just use filename
            return Path.GetFileName(fullPath);
        }
    }

    private string AppendDirectorySeparator(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            return path + Path.DirectorySeparatorChar;
        return path;
    }

    private string MakeStampFileName(string rootDir, string filePath)
    {
        // create a stamp filename that encodes relative path to avoid clashes across projects
        string relative = GetRelativePath(rootDir, filePath);
        // replace directory separators with '_' to create a safe filename
        var safe = relative.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
        return safe + ".format.stamp";
    }

    private void UpdateStamp(string stampFile, string hash)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
            File.WriteAllText(stampFile, hash);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to update stamp {stampFile}: {ex.Message}");
        }
    }

    private string ComputeFileHash(string file)
    {
        try
        {
            using (var stream = File.OpenRead(file))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to compute hash for {file}: {ex.Message}");
            return null;
        }
    }
    private bool HasFileChanged(string file, string stampFile)
    {
        string currentHash = ComputeFileHash(file);

        if (currentHash == null)
        {
            // treat unreadable file as changed
            return true;
        }

        try
        {
            if (!File.Exists(stampFile))
                return true;

            string oldHash = File.ReadAllText(stampFile).Trim();

            // If hash matches that means the file is unchanged
            return !string.Equals(currentHash, oldHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // On any issue treat as changed
            return true;
        }
    }

    private bool IsIgnored(string file)
    {
        if (_ignoreRegexList == null || _ignoreRegexList.Count == 0)
            return false;

        foreach (var r in _ignoreRegexList)
        {
            try
            {
                if (r.IsMatch(file))
                    return true;
            }
            catch { /* ignore regex runtime errors here */ }
        }
        return false;
    }

    private string Quoted(string configFile)
    {
        return configFile.Contains(" ") ? $@"""{configFile}""" : configFile;
    }

    private bool RunClangFormatCapture(string exe, string file, string configFile,
        out string stdout, out string stderr, out string fullCommand, out int exitCode)
    {
        stdout = string.Empty;
        stderr = string.Empty;
        fullCommand = null;
        exitCode = -1;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string args = "-i";
            if (!string.IsNullOrEmpty(configFile))
                args = $"-style=file:{Quoted(configFile)} {args}";

            args += $" \"{file}\"";
            psi.Arguments = args;

            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }

            fullCommand = $"{psi.FileName} {psi.Arguments}";

            if (exitCode != 0)
            {
                // Failure: return false (caller will log)
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            fullCommand = $"{exe} (failed to start)";
            return false;
        }
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
            string versionLine = proc.StandardOutput.ReadLine();
            proc.WaitForExit();

            if (!string.IsNullOrEmpty(versionLine))
                Log.LogMessage(MessageImportance.High, $"Using {versionLine}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to detect clang-format version: {ex.Message}");
        }
    }

    private string ResolveExecutable(string exe)
    {
        try
        {
            if (File.Exists(exe))
                return Path.GetFullPath(exe);

            // If simple name, rely on PATH resolution at runtime
            if (!exe.Contains(Path.DirectorySeparatorChar.ToString()) &&
                !exe.Contains(Path.AltDirectorySeparatorChar.ToString()))
            {
                return exe;
            }
        }
        catch { }

        return null;
    }

    private int ResolveMaxProcesses()
    {
        if (string.Equals(MaxProcesses, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var engine = BuildEngine as IBuildEngine9;
            return Math.Max(1, engine != null ? engine.RequestCores(Environment.ProcessorCount) : Environment.ProcessorCount);
        }

        if (int.TryParse(MaxProcesses, out int n) && n > 0)
        {
            return Math.Max(1, n);
        }

        return 1;
    }

    private int GetEffectiveVerbosity()
    {
        // 0 = summary only, 1 = summary + reformatted, 2 = summary + reformatted + ignored
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
