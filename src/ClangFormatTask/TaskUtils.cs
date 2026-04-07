using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClangFormatTask
{
    internal class TaskUtils
    {
        public static IEnumerable<string> ParsePipeSeparateList(string extensions)
        {
            return string.IsNullOrWhiteSpace(extensions) ? [] : extensions.Split(['|'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        public static string[] ParseExtensions(string extensions)
        {
            return ParsePipeSeparateList(extensions)
                .Select(s => s.Trim().StartsWith(".") ? s.Trim() : "." + s.Trim())
                .ToArray();
        }
        // Returns the relative path from basePath to fullPath, or just filename if any error occurs
        public static string GetRelativePath(string basePath, string fullPath)
        {
            try
            {
                var baseUri = new Uri(AppendDirectorySeparator(basePath));
                var fullUri = new Uri(fullPath);
                var relUri = baseUri.MakeRelativeUri(fullUri);
                var relativePath = Uri.UnescapeDataString(relUri.ToString().Replace('/', Path.DirectorySeparatorChar));
                relativePath = relativePath.Remove(relativePath.Length - Path.GetFileName(fullPath).Length);
                return relativePath.Length > 0 ? relativePath : new string(Path.DirectorySeparatorChar, 1);
            }
            catch
            {
                // fallback: just use filename
                return Path.GetFileName(fullPath);
            }
        }
        public static string AppendDirectorySeparator(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                return path + Path.DirectorySeparatorChar;
            }
            return path;
        }
        public static string Quoted(string configFile)
        {
            return configFile.Contains(" ") ? $@"""{configFile}""" : configFile;
        }
        public static bool RunClangFormatCapture(string exe, string file, string configFile,
            out string stdout, out string stderr, out string fullCommand, out int exitCode)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            fullCommand = null;
            exitCode = -1;

            string args = "-i";
            try
            {
                // Build arguments, including config file if specified. Note that config file is optional
                // and clang-format will search for it in parent directories if not provided.
                if (!string.IsNullOrEmpty(configFile))
                {
                    args = $"-style=file:{Quoted(configFile)} {args}";
                }

                args += $" \"{file}\"";

                // First attempt: use the executable path as it is (e.g. if it's a simple name relying on PATH,
                // or fully specified path to clang-format tool). In case of file not found, and if it is a simple
                // or relative name, we will attempt to resolve it in standard location (C:\Program Files\LLVM\bin)
                // and retry.
                int maxRetries = Path.GetFileName(exe) != exe ? 1 : 2;
                for (int i = 0; i < maxRetries; ++i)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exe,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            Arguments = args
                        };

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
                            if (stderr.Length == 0)
                            {
                                 stderr = $"ClangFormat exited with code {exitCode} but did not provide any error message.";
                            }
                            // Failure: return false (caller will log)
                            return false;
                        }
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 2 && i < maxRetries - 1) // Catch only ERROR_FILE_NOT_FOUND and if we can retry
                    {
                        // Attempt to resolve it in standard location (C:\Program Files\LLVM\bin) and retry.
                        exe = Environment.GetEnvironmentVariable("ProgramFiles") + @$"\LLVM\bin\{Path.GetFileName(exe)}";
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                fullCommand = $"{exe} {args}";
                return false;
            }
        }
        public static string ResolveExecutable(string exe)
        {
            try
            {
                if (File.Exists(exe))
                {
                    return Path.GetFullPath(exe);
                }

                // If simple name, rely on PATH resolution at runtime
                if (!exe.Contains(Path.DirectorySeparatorChar.ToString()) &&
                    !exe.Contains(Path.AltDirectorySeparatorChar.ToString()))
                {
                    return exe;
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
