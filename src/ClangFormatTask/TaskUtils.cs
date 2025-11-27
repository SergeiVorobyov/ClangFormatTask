using System;
using System.Collections.Generic;
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
        public static string GetRelativePath(string basePath, string fullPath)
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
        public static string AppendDirectorySeparator(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
                return path + Path.DirectorySeparatorChar;
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
    }
}
