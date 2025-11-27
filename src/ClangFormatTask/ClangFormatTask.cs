using Microsoft.Build.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace ClangFormatTask
{
    public class ClangFormatTask : MSBuildTask
    {
        static readonly string stamp_extension = ".stamp";
        static readonly string extensions = ".cpp|.c|.h|.hpp|.inl";
        static readonly string processors = "1";

        // Directory to start scanning for source files (project dir by default)
        [Required]
        public string RootDirectory { get; set; }
        [Required]
        public string ClangFormatToolPath { get; set; }
        // Directory where stamp files are stored (intermediate dir)
        [Required]
        public string StampDirectory { get; set; }
        // Pipe separated extensions, e.g. ".cpp|.c|.h|.hpp" is default.
        public string Extensions { get; set; } = extensions;
        // Pipe separated regex patterns to ignore
        public string IgnorePatterns { get; set; }
        // Number or "auto"
        public string MaxProcesses { get; set; } = processors;
        // Optional global config file path (if empty, clang-format will search)
        public string ConfigFile { get; set; }

        private List<Regex> _ignoreRegexList;

        public override bool Execute()
        {
            var res = ExecuteImpl();

            // Cleanup
            RootDirectory = null;
            ClangFormatToolPath = null;
            StampDirectory = null;
            Extensions = extensions;
            IgnorePatterns = null;
            MaxProcesses = processors;
            ConfigFile = null;
            _ignoreRegexList.Clear();

            return res;
        }
        public bool ExecuteImpl()
        {
            try
            {
                var verbosityLevel = GetVerbosityLevel();

                // Normalize root
                if (string.IsNullOrWhiteSpace(RootDirectory))
                {
                    Log.LogError("RootDirectory must be set.");
                    return false;
                }
                var root = RootDirectory;
                if (!Path.IsPathRooted(root))
                {
                    root = Path.GetFullPath(root);
                }

                if (!Directory.Exists(root))
                {
                    Log.LogError($"RootDirectory does not exist: {root}");
                    return false;
                }

                // Build ignore regex list
                _ignoreRegexList = [];
                if (IgnorePatterns != null)
                {
                    var patterns = TaskUtils.ParsePipeSeparateList(IgnorePatterns);

                    foreach (var item in patterns)
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            try
                            {
                                _ignoreRegexList.Add(new Regex(item, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                            }
                            catch (Exception ex)
                            {
                                Log.LogWarning($"Invalid ignore regex '{item}': {ex.Message}");
                            }
                        }
                    }
                }

                // Resolve clang-format executable
                string exeToRun = TaskUtils.ResolveExecutable(ClangFormatToolPath);
                if (exeToRun == null)
                {
                    Log.LogError($"Could not find clang-format executable: {ClangFormatToolPath}");
                    return false;
                }
                else
                {
                    Log.LogCommandLine(exeToRun);
                }

                IEnumerable<string> collected = null;
                {
                    // Parse extensions
                    var exts = TaskUtils.ParseExtensions(Extensions);
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
                    if (verbosityLevel >= MessageImportance.Low)
                    {
                        Log.LogMessage(MessageImportance.Low, "No input files available for ClangFormat.");
                    }
                    return true;
                }

                // Emit clang-format version
                EmitVersion(exeToRun);

                // Ensure stamp directory exists
                Directory.CreateDirectory(StampDirectory);

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
                        if (verbosityLevel >= MessageImportance.Low)
                        {
                            Log.LogMessage(MessageImportance.Low, $"Skipping {file}, unchanged.");
                        }
                        continue;
                    }

                    filesToFormat.Add(file);
                }

                int reformattedCount = 0;
                var success = true;

                if (filesToFormat.Count > 0)
                {
                    var logQueue = new ConcurrentQueue<Tuple<MessageImportance, string>>();

                    Parallel.ForEach(filesToFormat, new ParallelOptions { MaxDegreeOfParallelism = maxProcesses }, file =>
                    {
                        if (verbosityLevel >= MessageImportance.Normal)
                        {
                            logQueue.Enqueue(new Tuple<MessageImportance, string>(MessageImportance.Normal, $"Formatting {file}..."));
                        }

                        bool ok = TaskUtils.RunClangFormatCapture(exeToRun, file, ConfigFile,
                            out string stdout, out string stderr, out string fullCommand, out int exitCode);

                        if (!ok)
                        {
                            if (!string.IsNullOrEmpty(fullCommand))
                            {
                                logQueue.Enqueue(new Tuple<MessageImportance, string>(MessageImportance.Low, $"Command executed: {fullCommand}"));
                            }
                            if (!string.IsNullOrWhiteSpace(stdout))
                            {
                                logQueue.Enqueue(new Tuple<MessageImportance, string>(MessageImportance.High, stdout));
                            }
                            if (!string.IsNullOrWhiteSpace(stderr))
                            {
                                logQueue.Enqueue(new Tuple<MessageImportance, string>(MessageImportance.High, stderr));
                            }

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
                    {
                        Log.LogMessage(m.Item1, m.Item2);
                    }
                }

                Log.LogMessage(MessageImportance.High, $"{reformattedCount} of {filesToFormat.Count} files have been reformatted ({ignoredFiles.Count()} ignored).");

                if (verbosityLevel >= MessageImportance.Low && ignoredFiles.Count > 0)
                {
                    foreach (var f in ignoredFiles)
                    {
                        Log.LogMessage(MessageImportance.Low, $"Ignored {f}");
                    }
                }

                return success && !Log.HasLoggedErrors;
            }
            catch (Exception ex)
            {
                Log.LogError($"ClangFormatTask failed: {ex.Message}");
                return false;
            }
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
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch (PathTooLongException)
                    {
                        continue;
                    }

                    // Try reading files
                    try
                    {
                        files = Directory.EnumerateFiles(dir);
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (PathTooLongException)
                    {
                    }

                    // Add matched files
                    foreach (var f in files)
                    {
                        string ext = Path.GetExtension(f);
                        if (extSet.Contains(ext))
                        {
                            results.Add(f);
                        }
                    }

                    // Push subdirectories for DFS
                    foreach (string s in subdirs)
                    {
                        dirs.Push(s);
                    }
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
        private string MakeStampFileName(string rootDir, string filePath)
        {
            try
            {
                string relative = TaskUtils.GetRelativePath(rootDir, filePath);

                // Compute hash of relative path
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(relative));
                string hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                // Sanitize original file name: remove invalid chars
                string fileName = Path.GetFileName(filePath);
                string sanitized = string.Concat(fileName.Select(c =>
                    char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));

                return $"{sanitized}_{hashString}{stamp_extension}";
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to generate stamp filename for {filePath}: {ex.Message}");
                string fallback = filePath.GetHashCode().ToString("x");
                string fileName = Path.GetFileName(filePath);
                string sanitized = string.Concat(fileName.Select(c =>
                    char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
                return $"{sanitized}_{fallback}{stamp_extension}";
            }
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
                using var stream = File.OpenRead(file);
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] hash = sha.ComputeHash(stream);

                // Compatible hex conversion
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    // lowercase hex
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
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
                return true;
            }
            if (!File.Exists(stampFile))
            {
                return true;
            }

            string oldHash = File.ReadAllText(stampFile).Trim();
            return !string.Equals(currentHash, oldHash, StringComparison.OrdinalIgnoreCase);
        }
        private bool IsIgnored(string file)
        {
            if (_ignoreRegexList == null || _ignoreRegexList.Count == 0)
            {
                return false;
            }

            foreach (var r in _ignoreRegexList)
            {
                try
                {
                    if (r.IsMatch(file))
                    {
                        return true;
                    }
                }
                catch
                {
                    // ignore regex runtime errors here
                }
            }
            return false;
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
                {
                    Log.LogMessage(MessageImportance.High, $"Using {versionLine}");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to detect clang-format version: {ex.Message}");
            }
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
        private MessageImportance? GetVerbosityLevel()
        {
            if (Log.LogsMessagesOfImportance(MessageImportance.Low))
            {
                return MessageImportance.Low;
            }
            if (Log.LogsMessagesOfImportance(MessageImportance.Normal))
            {
                return MessageImportance.Normal;
            }
            if (Log.LogsMessagesOfImportance(MessageImportance.High))
            {
                return MessageImportance.High;
            }
            return null;
        }
    }
}
