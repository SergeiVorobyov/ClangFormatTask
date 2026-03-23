using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClangFormatTask
{
    internal class ChangeDetectionUtils
    {
        public static readonly string DateTimeFormat = "o";

        static readonly string StampFileExtension = ".stamp";
        public static string MakeStampFileName(string rootDir, string filePath, Action<string> log)
        {
            try
            {
                string relative = TaskUtils.GetRelativePath(rootDir, filePath);

                // Sanitize original file name: remove invalid chars
                string fileName = Path.GetFileName(filePath);
                string prefix = string.Concat(relative.Select(c =>
                    char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
                string sanitized = string.Concat(fileName.Select(c =>
                    char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));

                return $"{prefix}{sanitized}{StampFileExtension}";
            }
            catch (Exception ex)
            {
                log($"Failed to generate stamp filename for {filePath}: {ex.Message}");
                string fallback = filePath.GetHashCode().ToString("x");
                string fileName = Path.GetFileName(filePath);
                string sanitized = string.Concat(fileName.Select(c =>
                    char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
                return $"{sanitized}_{fallback}{StampFileExtension}";
            }
        }
        public static string ComputeFileHash(string file, Action<string> log)
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
                log(@$"Failed to compute hash for ""{file}"": {ex.Message}");
                return null;
            }
        }
        public static DateTime? ComputeTimestamp(string file, Action<string> log)
        {
            try
            {
                return File.GetLastWriteTimeUtc(file);
            }
            catch (Exception ex)
            {
                log(@$"Failed to compute timestamp for ""{file}"": {ex.Message}.");
                return null;
            }
        }
        public static bool HasFileChanged(string file, string stampFile, bool preciseChangeDetectionStrategy, Action<string> log)
        {
            if (!File.Exists(stampFile))
            {
                return true;
            }

            string oldHash = File.ReadAllText(stampFile).Trim();

            if (preciseChangeDetectionStrategy)
            {
                var currentHash = ComputeFileHash(file, log);
                if (currentHash == null)
                {
                    return true;
                }
                return !string.Equals(currentHash, oldHash, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                DateTime oldTimeStamp;
                if (!DateTime.TryParse(oldHash, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out oldTimeStamp ))
                {
                    return true;
                }

                var currentHash = ComputeTimestamp(file, log);
                return currentHash.HasValue ? DateTime.Compare(oldTimeStamp, currentHash.Value) < 0 : true;
            }
        }
        public static void UpdateStamp(string stampFile, string hash, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
                File.WriteAllText(stampFile, hash);
            }
            catch (Exception ex)
            {
                log($"Failed to update stamp {stampFile}: {ex.Message}");
            }
        }
    }
}
