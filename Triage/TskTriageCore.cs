using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VestigantTriage;

public static class TskTriageCore
{
    public static List<(string ExtractedPath, string InternalPath)> ExtractFromEwf(string[] imagePaths, string outputDir, Action<string> log)
    {
        var extractedFiles = new List<(string, string)>();
        var firstImage = imagePaths[0];

        if (!File.Exists("fls.exe") || !File.Exists("icat.exe") || !File.Exists("mmls.exe"))
        {
            throw new Exception("Missing Sleuth Kit binaries in the application folder.");
        }

        log($"DEBUG: Starting Vestigant TSK scan on: {firstImage}");

        // Step 1: Find Partitions
        var offsets = new List<string>();
        var mmls = RunProcess("mmls.exe", $"\"{firstImage}\"", log);

        foreach (var line in mmls)
        {
            if (line.Contains("NTFS", StringComparison.OrdinalIgnoreCase) || line.Contains("FAT", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && long.TryParse(parts[2], out _))
                {
                    offsets.Add(parts[2]);
                    log($"DEBUG: Found valid partition at offset {parts[2]}");
                }
            }
        }

        if (offsets.Count == 0)
        {
            log("DEBUG: No partitions found via mmls. Attempting to scan as a flat logical image...");
            offsets.Add("");
        }

        // Step 2: Scan Partitions
        foreach (var offset in offsets)
        {
            var offsetArg = string.IsNullOrWhiteSpace(offset) ? "" : $"-o {offset} ";
            log($"DEBUG: Executing FLS scan at offset {offset}...");

            var flsOutput = RunProcess("fls.exe", $"-r -p {offsetArg}\"{firstImage}\"", log);

            foreach (var line in flsOutput)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var meta = parts[0].Trim();
                var rawPath = parts[1].Trim();
                var normPath = rawPath.Replace("/", "\\");
                if (!normPath.StartsWith("\\")) normPath = "\\" + normPath;
                var fileName = Path.GetFileName(normPath);

                bool isTarget = ParserRegistry.IsTargetArtifactPath(normPath);

                if (isTarget)
                {
                    try
                    {
                        var inodeBlock = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd(':');
                        
                        // FORENSIC NAMING ENGINE: Extract Username or SID from the TSK path output
                        string ownerPrefix = "Sys";
                        var pathSegments = normPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < pathSegments.Length - 1; i++)
                        {
                            if (pathSegments[i].Equals("Users", StringComparison.OrdinalIgnoreCase))
                            {
                                ownerPrefix = pathSegments[i + 1];
                                break;
                            }
                            if (pathSegments[i].Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                            {
                                ownerPrefix = pathSegments[i + 1];
                                break;
                            }
                        }

                        // Consistent Naming: e.g., jdoe_NTUSER.DAT or Sys_SOFTWARE
                        var destName = $"Ewf_{Guid.NewGuid():N}_{ParserRegistry.SafeArtifactFileName(normPath, fileName)}";
                        var destPath = Path.Combine(outputDir, destName);

                        ExtractWithIcat(firstImage, offsetArg, inodeBlock, destPath);
                        extractedFiles.Add((destPath, $"[E01]{normPath}"));
                        log($"  -> Extracted: {normPath}");
                    }
                    catch (Exception ex)
                    {
                        log($"ERROR: Failed to extract {fileName}: {ex.Message}");
                    }
                }
            }
        }

        log($"DEBUG: TSK Scan complete. Found {extractedFiles.Count} files.");
        return extractedFiles;
    }

    private static List<string> RunProcess(string fileName, string arguments, Action<string> log)
    {
        var lines = new List<string>();
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // Capture errors
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            }
        };

        proc.Start();

        // Read output
        while (!proc.StandardOutput.EndOfStream)
        {
            lines.Add(proc.StandardOutput.ReadLine() ?? "");
        }

        // Read errors
        string errors = proc.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(errors))
        {
            log($"TSK {fileName} Warning/Error: {errors.Trim()}");
        }

        proc.WaitForExit();
        return lines;
    }

    private static void ExtractWithIcat(string image, string offsetArg, string inode, string destPath)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "icat.exe",
                Arguments = $"{offsetArg}\"{image}\" {inode}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
        {
            proc.StandardOutput.BaseStream.CopyTo(fs);
        }
        proc.WaitForExit();
    }
}