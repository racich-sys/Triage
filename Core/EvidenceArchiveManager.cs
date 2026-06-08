using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VestigantTriage;

internal static class EvidenceArchiveManager
{
    public static EvidenceArchiveResult CreateWorkingEvidenceArchive(string sourceDir, string targetDir, Action<string> log)
    {
        var startedUtc = DateTime.UtcNow;
        var stamp = startedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var result = new EvidenceArchiveResult
        {
            StartedUtc = startedUtc,
            SourceDirectory = sourceDir,
            TargetDirectory = targetDir,
            Status = "NotStarted"
        };

        try
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            {
                result.Status = "Skipped";
                result.Message = "WorkingEvidence folder not found.";
                log("  ! WorkingEvidence folder not found. Static evidence archive skipped.");
                return result;
            }

            Directory.CreateDirectory(targetDir);
            var archiveLogDir = Path.Combine(targetDir, "ArchiveLogs");
            Directory.CreateDirectory(archiveLogDir);

            var files = EnumerateFilesSafe(sourceDir, log).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            result.FileCount = files.Count;
            if (files.Count == 0)
            {
                result.Status = "Skipped";
                result.Message = "No files found in WorkingEvidence.";
                log("  ! WorkingEvidence folder contained no files. Static evidence archive skipped.");
                return result;
            }

            var archivePath = Path.Combine(targetDir, $"Pristine_Evidence_{stamp}.7z");
            var listPath = Path.Combine(archiveLogDir, $"EvidenceArchive_{stamp}_filelist.txt");
            var manifestPath = Path.Combine(archiveLogDir, $"EvidenceArchive_{stamp}_manifest.csv");
            var createLogPath = Path.Combine(archiveLogDir, $"EvidenceArchive_{stamp}_7zip_create.log");
            var verifyLogPath = Path.Combine(archiveLogDir, $"EvidenceArchive_{stamp}_7zip_verify.log");

            result.ArchivePath = archivePath;
            result.FileListPath = listPath;
            result.ManifestPath = manifestPath;
            result.CreateLogPath = createLogPath;
            result.VerifyLogPath = verifyLogPath;

            WriteFileList(sourceDir, files, listPath);
            WriteManifest(sourceDir, files, manifestPath, log);

            var sevenZipPath = FindSevenZipExecutable();
            result.SevenZipPath = sevenZipPath;
            if (string.IsNullOrWhiteSpace(sevenZipPath) || !File.Exists(sevenZipPath))
            {
                result.Status = "Failed";
                result.Message = "7-Zip executable not found.";
                log("  ! 7-Zip not found. Static evidence archive failed. Manifest and file list were still written.");
                return result;
            }

            log($"  - Creating static evidence archive using 7-Zip listfile: {Path.GetFileName(archivePath)}");
            var create = RunSevenZip(sevenZipPath, sourceDir, createLogPath,
                "a", "-t7z", "-mx=5", "-mmt=on", "-mtc=on", "-mta=on", "-mtt=on", "-bb1", "-scsUTF-8", archivePath, "@" + listPath);
            result.CreateExitCode = create.ExitCode;
            result.CreateElapsed = create.Elapsed;

            if (create.ExitCode > 1)
            {
                result.Status = "Failed";
                result.Message = $"7-Zip create returned fatal exit code {create.ExitCode}.";
                log($"  ! 7-Zip create failed with exit code {create.ExitCode}. See {Path.GetFileName(createLogPath)}.");
                return result;
            }

            if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
            {
                result.Status = "Failed";
                result.Message = "Archive was not created or is zero bytes.";
                log($"  ! Static evidence archive failed: archive was not created or is empty. See {Path.GetFileName(createLogPath)}.");
                return result;
            }

            log($"  - Verifying static evidence archive: {Path.GetFileName(archivePath)}");
            var verify = RunSevenZip(sevenZipPath, targetDir, verifyLogPath, "t", "-bb1", archivePath);
            result.VerifyExitCode = verify.ExitCode;
            result.VerifyElapsed = verify.Elapsed;
            result.ArchiveSizeBytes = new FileInfo(archivePath).Length;

            if (verify.ExitCode == 0 && create.ExitCode == 0)
            {
                result.Status = "CreatedAndVerified";
                result.Message = "Archive created and verified successfully.";
                log($"  ✓ Static evidence archive created and verified: {Path.GetFileName(archivePath)} ({result.ArchiveSizeBytes:N0} bytes, {files.Count:N0} files).");
            }
            else if (verify.ExitCode <= 1 && create.ExitCode <= 1)
            {
                result.Status = "CreatedWithWarnings";
                result.Message = $"Archive created with warnings. create={create.ExitCode}, verify={verify.ExitCode}.";
                log($"  ! Static evidence archive created with warnings. create={create.ExitCode}, verify={verify.ExitCode}. Review ArchiveLogs.");
            }
            else
            {
                result.Status = "FailedVerification";
                result.Message = $"Archive creation completed but verification failed with exit code {verify.ExitCode}.";
                log($"  ! Static evidence archive verification failed with exit code {verify.ExitCode}. Review ArchiveLogs.");
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Status = "Exception";
            result.Message = ex.Message;
            log($"  ! Static evidence archive exception: {ex.Message}");
            return result;
        }
        finally
        {
            result.FinishedUtc = DateTime.UtcNow;
        }
    }

    private static List<string> EnumerateFilesSafe(string root, Action<string> log)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch (Exception ex)
            {
                log($"  ! Archive enumeration skipped files in {dir}: {ex.Message}");
                files = Enumerable.Empty<string>();
            }

            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file)) results.Add(file);
                }
                catch { }
            }

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir); }
            catch (Exception ex)
            {
                log($"  ! Archive enumeration skipped subdirectories in {dir}: {ex.Message}");
                dirs = Enumerable.Empty<string>();
            }

            foreach (var child in dirs) stack.Push(child);
        }

        return results;
    }

    private static void WriteFileList(string sourceDir, IEnumerable<string> files, string listPath)
    {
        using var writer = new StreamWriter(listPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(sourceDir, file).Replace('/', '\\');
            writer.WriteLine(@".\" + rel);
        }
    }

    private static void WriteManifest(string sourceDir, IEnumerable<string> files, string manifestPath, Action<string> log)
    {
        using var writer = new StreamWriter(manifestPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("RelativePath,FullPath,SizeBytes,ModifiedUtc,SHA256,ManifestStatus,Error");
        foreach (var file in files)
        {
            string relative = string.Empty;
            string size = string.Empty;
            string modified = string.Empty;
            string hash = string.Empty;
            string status = "Included";
            string error = string.Empty;

            try
            {
                relative = Path.GetRelativePath(sourceDir, file).Replace('/', '\\');
                var info = new FileInfo(file);
                size = info.Length.ToString(CultureInfo.InvariantCulture);
                modified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
                hash = ComputeSha256(file);
            }
            catch (Exception ex)
            {
                status = "ManifestError";
                error = ex.Message;
                log($"  ! Archive manifest warning for {file}: {ex.Message}");
            }

            writer.WriteLine(string.Join(',', new[] { Csv(relative), Csv(file), Csv(size), Csv(modified), Csv(hash), Csv(status), Csv(error) }));
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string FindSevenZipExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "7z.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(dir, "7z.exe")); }
            catch { }
        }

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static ProcessRunResult RunSevenZip(string exePath, string workingDirectory, string logPath, params string[] args)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        sw.Stop();

        using var writer = new StreamWriter(logPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine($"Executable: {exePath}");
        writer.WriteLine($"WorkingDirectory: {workingDirectory}");
        writer.WriteLine($"Arguments: {string.Join(' ', args.Select(QuoteForLog))}");
        writer.WriteLine($"ExitCode: {proc.ExitCode}");
        writer.WriteLine($"ElapsedSeconds: {sw.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)}");
        writer.WriteLine();
        writer.WriteLine("--- STDOUT ---");
        writer.Write(stdout.ToString());
        writer.WriteLine();
        writer.WriteLine("--- STDERR ---");
        writer.Write(stderr.ToString());

        return new ProcessRunResult(proc.ExitCode, sw.Elapsed);
    }

    private static string QuoteForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        return value.Any(char.IsWhiteSpace) ? '"' + value.Replace("\"", "\\\"") + '"' : value;
    }

    private sealed record ProcessRunResult(int ExitCode, TimeSpan Elapsed);
}

internal sealed class EvidenceArchiveResult
{
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string SourceDirectory { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public string FileListPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string CreateLogPath { get; set; } = string.Empty;
    public string VerifyLogPath { get; set; } = string.Empty;
    public string SevenZipPath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long ArchiveSizeBytes { get; set; }
    public int CreateExitCode { get; set; } = -1;
    public int VerifyExitCode { get; set; } = -1;
    public TimeSpan CreateElapsed { get; set; }
    public TimeSpan VerifyElapsed { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
}
