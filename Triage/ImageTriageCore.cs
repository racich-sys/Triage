using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Setup;
using DiscUtils.Streams;

namespace VestigantTriage;

public class ExtractedArtifact
{
    public string ExtractedPath { get; set; } = string.Empty;
    public string InternalPath { get; set; } = string.Empty;
    public DateTime? CreatedUtc { get; set; }
    public DateTime? AccessedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
}

public enum ImageArtifactScanMode
{
    Triage,
    Full
}

public static class ImageTriageCore
{
    private const long DefaultUsnJournalTailBytes = 64L * 1024L * 1024L;
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(5);

    static ImageTriageCore()
    {
        SetupHelper.RegisterAssembly(typeof(NtfsFileSystem).Assembly);
    }

    public static List<ExtractedArtifact> ExtractTargetedArtifacts(string[] imagePaths, string outputDir, Action<string> log)
    {
        return ExtractTargetedArtifacts(imagePaths, outputDir, log, ImageArtifactScanMode.Triage);
    }

    public static List<ExtractedArtifact> ExtractTargetedArtifacts(string[] imagePaths, string outputDir, Action<string> log, ImageArtifactScanMode scanMode)
    {
        var extractedFiles = new List<ExtractedArtifact>();
        log($"Starting Advanced Forensic Triage ({scanMode} mode)...");

        foreach (var imgPath in imagePaths)
        {
            try
            {
                log($"Mounting Image: {Path.GetFileName(imgPath)}");
                using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                using var disk = new DiscUtils.Raw.Disk(fs, DiscUtils.Streams.Ownership.None);
                var volumes = VolumeManager.GetPhysicalVolumes(disk);

                foreach (var vol in volumes)
                {
                    using var volStream = vol.Open();
                    if (NtfsFileSystem.Detect(volStream))
                    {
                        log("  - NTFS Partition detected. Commencing MFT and File System scan...");
                        using var ntfs = new NtfsFileSystem(volStream);

                        volStream.Position = 0;
                        byte[] bootSector = new byte[512];
                        volStream.Read(bootSector, 0, 512);
                        long clusterSize = BitConverter.ToUInt16(bootSector, 11) * bootSector[13];

                        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var liveProgress = new FileScanProgress(scanMode == ImageArtifactScanMode.Triage ? "Fast triage artifact discovery" : "Full live file system scan", log);

                        if (scanMode == ImageArtifactScanMode.Triage)
                        {
                            log("  -> [Diagnostic] Starting Fast Triage artifact discovery. Only high-value forensic artifact locations are scanned.");
                            log("  -> [Diagnostic] Fast Triage can be followed later by Full Discovery without intentionally duplicating deterministic staged artifacts.");
                            ExtractFastTriageArtifacts(ntfs, outputDir, extractedFiles, livePaths, log, liveProgress);
                        }
                        else
                        {
                            log("  -> [Diagnostic] Starting Full Live File System Scan. This recursively scans the full file system and may take a long time.");
                            ScanLiveFileSystem(ntfs, ntfs.Root, outputDir, extractedFiles, livePaths, log, liveProgress);
                        }

                        liveProgress.Finish($"  -> [Diagnostic] Live Scan Complete. Scanned {liveProgress.ItemsScanned:N0} files and found {extractedFiles.Count:N0} live artifacts.");

                        log("  -> [Diagnostic] Extracting targeted NTFS metadata streams...");
                        ExtractNtfsMetadataArtifacts(ntfs, outputDir, extractedFiles, log);

                        log("  -> [Diagnostic] Starting MFT owner/lock file scan (~$ Office files)...");
                        using (var ownerMftCarveStream = vol.Open())
                        {
                            ScanMftForOfficeOwnerLockFiles(ntfs, ownerMftCarveStream, clusterSize, outputDir, extractedFiles, livePaths, log);
                        }
                        log("  -> [Diagnostic] MFT owner/lock file scan complete.");

                        log("  -> [Diagnostic] Starting MFT Ethereal Scan (Resident & Non-Resident)...");
                        using var carveStream = vol.Open();
                        ScanMftForDeletedEtherealData(ntfs, carveStream, clusterSize, outputDir, extractedFiles, log);
                        log("  -> [Diagnostic] MFT Ethereal Scan Complete.");
                    }
                }
            }
            catch (Exception ex)
            {
                log($"  ! CRITICAL IMAGE ERROR: {ex.Message}");
            }
        }

        log($"Triage complete. Extracted {extractedFiles.Count} total artifacts.");
        return extractedFiles;
    }

    private static void ExtractFastTriageArtifacts(NtfsFileSystem ntfs, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, Action<string> log, FileScanProgress progress)
    {
        log("  -> [Diagnostic] Fast Triage avoids broad Program Files/Windows resource crawling. Use Full Discovery for exhaustive scanning.");

        ExtractExactPathIfTarget(ntfs, @"Windows\System32\config\SYSTEM", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\System32\config\SOFTWARE", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\System32\config\SAM", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\System32\config\SECURITY", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\AppCompat\Programs\Amcache.hve", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\inf\setupapi.dev.log", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\inf\setupapi.dev.log.old", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\inf\setupapi.app.log", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\inf\setupapi.app.log.old", outputDir, extracted, livePaths, progress);
        ExtractExactPathIfTarget(ntfs, @"Windows\System32\sru\SRUDB.dat", outputDir, extracted, livePaths, progress);

        ExtractDirectoryMatching(ntfs, @"Windows\Prefetch", outputDir, extracted, livePaths, progress, false, f => EndsWithIgnoreCase(f.Name, ".pf"));
        ExtractDirectoryMatching(ntfs, @"Windows\System32\winevt\Logs", outputDir, extracted, livePaths, progress, false, f => EndsWithIgnoreCase(f.Name, ".evtx"));
        ExtractPrintSpoolFastTriageArtifacts(ntfs, outputDir, extracted, livePaths, progress);
        ExtractDirectoryMatching(ntfs, @"$Recycle.Bin", outputDir, extracted, livePaths, progress, true, f => Path.GetFileName(f.Name).StartsWith("$I", StringComparison.OrdinalIgnoreCase));

        foreach (var userDir in EnumerateDirectories(ntfs, @"Users"))
        {
            var userRoot = NormalizeInternalPath(userDir.FullName);
            var userName = userDir.Name;
            if (IsNonUserProfileDirectory(userName))
                continue;

            ExtractExactPathIfTarget(ntfs, CombineInternalPath(userRoot, "NTUSER.DAT"), outputDir, extracted, livePaths, progress);
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(userRoot, @"AppData\Local\Microsoft\Windows\UsrClass.dat"), outputDir, extracted, livePaths, progress);

            ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"AppData\Roaming\Microsoft\Windows\Recent"), outputDir, extracted, livePaths, progress, true,
                f => EndsWithIgnoreCase(f.Name, ".lnk") || EndsWithIgnoreCase(f.Name, ".automaticDestinations-ms") || EndsWithIgnoreCase(f.Name, ".customDestinations-ms"));

            ExtractChromiumHistoryRoots(ntfs, CombineInternalPath(userRoot, @"AppData\Local\Google\Chrome\User Data"), outputDir, extracted, livePaths, progress);
            ExtractChromiumHistoryRoots(ntfs, CombineInternalPath(userRoot, @"AppData\Local\Microsoft\Edge\User Data"), outputDir, extracted, livePaths, progress);
            ExtractChromiumHistoryRoots(ntfs, CombineInternalPath(userRoot, @"AppData\Local\BraveSoftware\Brave-Browser\User Data"), outputDir, extracted, livePaths, progress);
            ExtractFirefoxProfileArtifacts(ntfs, CombineInternalPath(userRoot, @"AppData\Roaming\Mozilla\Firefox\Profiles"), outputDir, extracted, livePaths, progress);

            ExtractExactPathIfTarget(ntfs, CombineInternalPath(userRoot, @"AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt"), outputDir, extracted, livePaths, progress);
            ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"Documents\PowerShell\Transcripts"), outputDir, extracted, livePaths, progress, true, f => ParserRegistry.IsTargetArtifactPath(f.FullName));

            ExtractOfficeFastTriageArtifacts(ntfs, userRoot, outputDir, extracted, livePaths, progress);
            // Owner/lock files are collected from the MFT filename index after live triage.
            ExtractOneDriveFastTriageArtifacts(ntfs, userRoot, outputDir, extracted, livePaths, progress);
            ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"AppData\Roaming\Dropbox"), outputDir, extracted, livePaths, progress, true, f => ParserRegistry.IsTargetArtifactPath(f.FullName));
            ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"AppData\Local\Dropbox"), outputDir, extracted, livePaths, progress, true, f => ParserRegistry.IsTargetArtifactPath(f.FullName));
            ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"AppData\Local\Google\DriveFS"), outputDir, extracted, livePaths, progress, true, f => ParserRegistry.IsTargetArtifactPath(f.FullName));
        }
    }


    private static void ExtractOfficeFastTriageArtifacts(NtfsFileSystem ntfs, string userRoot, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress)
    {
        // Avoid recursively walking OfficeFileCache and WebView caches in fast triage. Those caches can contain
        // tens of thousands of opaque cache fragments and are better handled by Full Discovery when exhaustive review is needed.
        ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"AppData\Roaming\Microsoft\Office\Recent"), outputDir, extracted, livePaths, progress, true,
            f => EndsWithIgnoreCase(f.Name, ".lnk") || EndsWithIgnoreCase(f.Name, ".automaticDestinations-ms") || EndsWithIgnoreCase(f.Name, ".customDestinations-ms"));

        ExtractDirectoryMatching(ntfs, CombineInternalPath(userRoot, @"AppData\Roaming\Microsoft\Office"), outputDir, extracted, livePaths, progress, false,
            f => Path.GetFileName(f.Name).StartsWith("~$", StringComparison.OrdinalIgnoreCase) || EndsWithIgnoreCase(f.Name, ".lnk"));
    }

    private static bool IsOfficeOwnerLockFile(string name)
    {
        var lowerName = (name ?? string.Empty).ToLowerInvariant();
        if (!lowerName.StartsWith("~$", StringComparison.Ordinal)) return false;
        return lowerName.EndsWith(".doc", StringComparison.Ordinal) ||
               lowerName.EndsWith(".docx", StringComparison.Ordinal) ||
               lowerName.EndsWith(".docm", StringComparison.Ordinal) ||
               lowerName.EndsWith(".dot", StringComparison.Ordinal) ||
               lowerName.EndsWith(".dotx", StringComparison.Ordinal) ||
               lowerName.EndsWith(".dotm", StringComparison.Ordinal) ||
               lowerName.EndsWith(".xls", StringComparison.Ordinal) ||
               lowerName.EndsWith(".xlsx", StringComparison.Ordinal) ||
               lowerName.EndsWith(".xlsm", StringComparison.Ordinal) ||
               lowerName.EndsWith(".xlsb", StringComparison.Ordinal) ||
               lowerName.EndsWith(".ppt", StringComparison.Ordinal) ||
               lowerName.EndsWith(".pptx", StringComparison.Ordinal) ||
               lowerName.EndsWith(".pptm", StringComparison.Ordinal);
    }

    private static void ExtractPrintSpoolFastTriageArtifacts(NtfsFileSystem ntfs, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress)
    {
        // Default triage collects Windows print queue artifacts directly instead of scanning user document trees.
        // Queue coverage includes SHD/SPL, rendered EMF/XPS/OXPS, raw printer-language payloads, and spool temp files.
        // Deleted .SPL/.SHD/.EMF/.PCL/.PJL/.PRN/.RAW candidates are also eligible through the MFT deleted-artifact pass.
        ExtractDirectoryMatching(ntfs, @"Windows\System32\spool\PRINTERS", outputDir, extracted, livePaths, progress, false,
            f => IsPrintSpoolCandidate(f.FullName));
        ExtractDirectoryMatching(ntfs, @"Windows\System32\spool\SERVERS", outputDir, extracted, livePaths, progress, false,
            f => IsPrintSpoolCandidate(f.FullName));

        // Driver/configuration artifacts are metadata-only but can identify installed print capabilities and print processors.
        // Extraction is extension-gated by ParserRegistry so fast triage avoids copying broad driver binaries.
        ExtractDirectoryMatching(ntfs, @"Windows\System32\spool\drivers", outputDir, extracted, livePaths, progress, true,
            f => IsPrintSpoolCandidate(f.FullName));
        ExtractDirectoryMatching(ntfs, @"Windows\System32\spool\PRTPROCS", outputDir, extracted, livePaths, progress, true,
            f => IsPrintSpoolCandidate(f.FullName));
    }

    private static bool IsPrintSpoolCandidate(string path)
    {
        var normalized = (path ?? string.Empty).Replace('/', '\\');
        var name = Path.GetFileName(normalized) ?? string.Empty;
        var lowerName = name.ToLowerInvariant();
        return ParserRegistry.IsPrintSpoolArtifactPath(normalized, lowerName);
    }

    private static void ExtractOneDriveFastTriageArtifacts(NtfsFileSystem ntfs, string userRoot, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress)
    {
        var oneDriveRoot = CombineInternalPath(userRoot, @"AppData\Local\Microsoft\OneDrive");

        // Fast triage now prioritizes durable OneDrive state databases/settings. Rotated logs (.odl/.odlgz/.loggz/.etlgz)
        // are intentionally excluded from default triage because they are numerous and usually low-yield for source parity.
        ExtractDirectoryMatching(ntfs, CombineInternalPath(oneDriveRoot, "settings"), outputDir, extracted, livePaths, progress, true,
            f => IsOneDriveSettingsOrDatabase(f.Name));
    }

    private static bool IsOneDriveSettingsOrDatabase(string name)
    {
        var lowerName = (name ?? string.Empty).ToLowerInvariant();
        return lowerName.EndsWith(".json", StringComparison.Ordinal) ||
               lowerName.EndsWith(".ini", StringComparison.Ordinal) ||
               lowerName.EndsWith(".db", StringComparison.Ordinal) ||
               lowerName is "syncenginedatabase.db";
    }


    private static void ExtractChromiumHistoryRoots(NtfsFileSystem ntfs, string userDataRoot, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress)
    {
        foreach (var profileDir in EnumerateDirectories(ntfs, userDataRoot))
        {
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(profileDir.FullName, "History"), outputDir, extracted, livePaths, progress);
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(profileDir.FullName, "History-journal"), outputDir, extracted, livePaths, progress);
        }
    }

    private static void ExtractFirefoxProfileArtifacts(NtfsFileSystem ntfs, string profilesRoot, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress)
    {
        foreach (var profileDir in EnumerateDirectories(ntfs, profilesRoot))
        {
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(profileDir.FullName, "places.sqlite"), outputDir, extracted, livePaths, progress);
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(profileDir.FullName, "places.sqlite-wal"), outputDir, extracted, livePaths, progress);
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(profileDir.FullName, "places.sqlite-shm"), outputDir, extracted, livePaths, progress);
            ExtractExactPathIfTarget(ntfs, CombineInternalPath(profileDir.FullName, "downloads.sqlite"), outputDir, extracted, livePaths, progress);
        }
    }

    private static void ExtractExactPathIfTarget(NtfsFileSystem ntfs, string internalPath, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress)
    {
        var normalized = NormalizeInternalPath(internalPath);
        try
        {
            if (!ntfs.FileExists(normalized) || !ParserRegistry.IsTargetArtifactPath(normalized))
                return;

            var file = ntfs.GetFileInfo(normalized);
            progress.MarkScanned(file.FullName);
            ExtractLiveArtifact(file, outputDir, extracted, livePaths, progress, "Live");
        }
        catch
        {
            // Missing target paths are normal across Windows builds and profiles.
        }
    }

    private static void ExtractDirectoryMatching(NtfsFileSystem ntfs, string internalDir, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress, bool recursive, Func<DiscFileInfo, bool> shouldExtract)
    {
        foreach (var file in EnumerateFiles(ntfs, internalDir))
        {
            progress.MarkScanned(file.FullName);
            bool match = false;
            try { match = shouldExtract(file); } catch { }
            if (match)
                ExtractLiveArtifact(file, outputDir, extracted, livePaths, progress, "Live");
        }

        if (!recursive)
            return;

        foreach (var subDir in EnumerateDirectories(ntfs, internalDir))
            ExtractDirectoryMatching(ntfs, subDir.FullName, outputDir, extracted, livePaths, progress, true, shouldExtract);
    }

    private static void ExtractDirectoryMatchingRecentPerDirectory(NtfsFileSystem ntfs, string internalDir, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress, bool recursive, int maxMatchesPerDirectory, Func<DiscFileInfo, bool> shouldExtract)
    {
        var matches = new List<DiscFileInfo>();
        foreach (var file in EnumerateFiles(ntfs, internalDir))
        {
            progress.MarkScanned(file.FullName);
            bool match = false;
            try { match = shouldExtract(file); } catch { }
            if (match)
                matches.Add(file);
        }

        foreach (var file in matches
                     .OrderByDescending(SafeLastWriteUtc)
                     .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(0, maxMatchesPerDirectory)))
        {
            ExtractLiveArtifact(file, outputDir, extracted, livePaths, progress, "Live");
        }

        if (!recursive)
            return;

        foreach (var subDir in EnumerateDirectories(ntfs, internalDir))
            ExtractDirectoryMatchingRecentPerDirectory(ntfs, subDir.FullName, outputDir, extracted, livePaths, progress, true, maxMatchesPerDirectory, shouldExtract);
    }

    private static DateTime SafeLastWriteUtc(DiscFileInfo file)
    {
        try { return file.LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static DateTime? SafeCreationUtc(DiscFileInfo file)
    {
        try { return file.CreationTimeUtc; }
        catch { return null; }
    }

    private static DateTime? SafeLastAccessUtc(DiscFileInfo file)
    {
        try { return file.LastAccessTimeUtc; }
        catch { return null; }
    }

    private static IEnumerable<DiscFileInfo> EnumerateFiles(NtfsFileSystem ntfs, string internalDir)
    {
        try
        {
            var dir = ntfs.GetDirectoryInfo(NormalizeInternalPath(internalDir));
            return dir.Exists ? dir.GetFiles() : Array.Empty<DiscFileInfo>();
        }
        catch
        {
            return Array.Empty<DiscFileInfo>();
        }
    }

    private static IEnumerable<DiscDirectoryInfo> EnumerateDirectories(NtfsFileSystem ntfs, string internalDir)
    {
        try
        {
            var dir = ntfs.GetDirectoryInfo(NormalizeInternalPath(internalDir));
            return dir.Exists ? dir.GetDirectories() : Array.Empty<DiscDirectoryInfo>();
        }
        catch
        {
            return Array.Empty<DiscDirectoryInfo>();
        }
    }

    private static void ExtractLiveArtifact(DiscFileInfo file, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, FileScanProgress progress, string prefix)
    {
        var internalPath = NormalizeInternalPath(file.FullName);
        if (!livePaths.Add(internalPath))
            return;

        string safeName = SafeDeterministicArtifactName(prefix, internalPath, ParserRegistry.SafeArtifactFileName(internalPath, file.Name));
        string destPath = Path.Combine(outputDir, safeName);

        if (!File.Exists(destPath))
        {
            using var src = file.Open(FileMode.Open, FileAccess.Read);
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
        }

        extracted.Add(new ExtractedArtifact
        {
            ExtractedPath = destPath,
            InternalPath = internalPath,
            CreatedUtc = SafeCreationUtc(file),
            AccessedUtc = SafeLastAccessUtc(file),
            ModifiedUtc = SafeLastWriteUtc(file)
        });
        progress.MarkMatched(internalPath, extracted.Count);
    }

    private static string SafeDeterministicArtifactName(string prefix, string internalPath, string fallbackName)
    {
        var safe = ParserRegistry.SafeArtifactFileName(internalPath, fallbackName);
        foreach (var ch in Path.GetInvalidFileNameChars())
            safe = safe.Replace(ch, '_');

        using var sha = SHA256.Create();
        string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(NormalizeInternalPath(internalPath).ToUpperInvariant()))).Substring(0, 16).ToLowerInvariant();
        return $"{prefix}_{hash}_{safe}";
    }

    private static string NormalizeInternalPath(string path)
    {
        return (path ?? string.Empty).Replace('/', '\\').TrimStart('\\');
    }

    private static string CombineInternalPath(string left, string right)
    {
        return NormalizeInternalPath(left).TrimEnd('\\') + "\\" + NormalizeInternalPath(right);
    }

    private static bool EndsWithIgnoreCase(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonUserProfileDirectory(string name)
    {
        return name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("All Users", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanLiveFileSystem(NtfsFileSystem ntfs, DiscDirectoryInfo dir, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, Action<string> log, FileScanProgress progress)
    {
        IEnumerable<DiscFileInfo> files;
        try { files = dir.GetFiles(); }
        catch (Exception ex)
        {
            log($"  ! Live scan could not enumerate files in {dir.FullName}: {ex.Message}");
            files = Array.Empty<DiscFileInfo>();
        }

        foreach (var file in files)
        {
            try
            {
                progress.MarkScanned(file.FullName);

                if (!IsTargetArtifact(file.FullName))
                    continue;

                ExtractLiveArtifact(file, outputDir, extracted, livePaths, progress, "Live");
            }
            catch (Exception ex)
            {
                log($"  ! Failed to extract live artifact {file.FullName}: {ex.Message}");
            }
        }

        IEnumerable<DiscDirectoryInfo> subDirs;
        try { subDirs = dir.GetDirectories(); }
        catch (Exception ex)
        {
            log($"  ! Live scan could not enumerate subdirectories in {dir.FullName}: {ex.Message}");
            return;
        }

        foreach (var subDir in subDirs)
        {
            try
            {
                ScanLiveFileSystem(ntfs, subDir, outputDir, extracted, livePaths, log, progress);
            }
            catch (Exception ex)
            {
                log($"  ! Skipped directory {subDir.FullName}: {ex.Message}");
            }
        }
    }

    private static void ExtractNtfsMetadataArtifacts(NtfsFileSystem ntfs, string outputDir, List<ExtractedArtifact> extracted, Action<string> log)
    {
        foreach (var internalPath in new[]
        {
            @"$Extend\$UsnJrnl:$J",
            @"\$Extend\$UsnJrnl:$J"
        })
        {
            try
            {
                using var src = ntfs.OpenFile(internalPath, FileMode.Open, FileAccess.Read);
                long streamLength = TryGetStreamLength(src);
                bool partialTail = streamLength > DefaultUsnJournalTailBytes;
                long bytesToCopy = partialTail ? DefaultUsnJournalTailBytes : streamLength;

                if (streamLength <= 0)
                {
                    bytesToCopy = DefaultUsnJournalTailBytes;
                    log($"  - NTFS USN Journal stream found at {internalPath}, but stream length is unknown. Copying at most {FormatBytes(bytesToCopy)}.");
                }
                else if (partialTail)
                {
                    log($"  - NTFS USN Journal stream found at {internalPath}: {FormatBytes(streamLength)}. Extracting recent tail only ({FormatBytes(bytesToCopy)}) to avoid long flat-image stalls.");
                    if (src.CanSeek)
                        src.Position = Math.Max(0, streamLength - bytesToCopy);
                    else
                    {
                        log("  ! NTFS USN Journal stream is larger than the safety limit but is not seekable. Skipping $J extraction for this partition.");
                        return;
                    }
                }
                else
                {
                    log($"  - NTFS USN Journal stream found at {internalPath}: {FormatBytes(streamLength)}. Extracting full stream.");
                }

                string safeName = partialTail
                    ? SafeDeterministicArtifactName("Live", internalPath + "::Tail64MB", "USNJournal_Tail64MB_$J")
                    : SafeDeterministicArtifactName("Live", internalPath, ParserRegistry.SafeArtifactFileName(internalPath, "$J"));
                if (!safeName.EndsWith("_$J", StringComparison.OrdinalIgnoreCase) && !safeName.EndsWith("$J", StringComparison.OrdinalIgnoreCase))
                    safeName += "_$J";

                string destPath = Path.Combine(outputDir, safeName);
                if (!File.Exists(destPath))
                {
                    using var dst = File.Create(destPath);
                    CopyBoundedWithProgress(src, dst, bytesToCopy, log, partialTail ? "USN Journal tail extraction" : "USN Journal extraction");
                }
                else
                {
                    log($"  - NTFS USN Journal staged artifact already exists; reusing {Path.GetFileName(destPath)}.");
                }

                string recordedInternalPath = partialTail
                    ? $"{internalPath} [partial tail {FormatBytes(bytesToCopy)} of {FormatBytes(streamLength)}]"
                    : internalPath;
                extracted.Add(new ExtractedArtifact { ExtractedPath = destPath, InternalPath = recordedInternalPath });
                log($"  -> Extracted NTFS metadata stream: {recordedInternalPath}");
                return;
            }
            catch (Exception ex)
            {
                log($"  - NTFS metadata stream path {internalPath} unavailable or not extracted: {ex.Message}");
            }
        }

        log("  - NTFS USN Journal stream ($Extend\\$UsnJrnl:$J) not extracted from this partition.");
    }

    private static long TryGetStreamLength(Stream stream)
    {
        try { return stream.Length; }
        catch { return -1; }
    }

    private static void CopyBoundedWithProgress(Stream src, Stream dst, long maxBytes, Action<string> log, string label)
    {
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        DateTime started = DateTime.UtcNow;
        DateTime lastLog = started;

        while (copied < maxBytes)
        {
            int toRead = (int)Math.Min(buffer.Length, maxBytes - copied);
            int read = src.Read(buffer, 0, toRead);
            if (read <= 0) break;
            dst.Write(buffer, 0, read);
            copied += read;

            var now = DateTime.UtcNow;
            if (now - lastLog >= ProgressLogInterval)
            {
                double elapsed = Math.Max(0.001, (now - started).TotalSeconds);
                double rate = copied / elapsed;
                log($"  -> [Progress] {label}: copied {FormatBytes(copied)} / {FormatBytes(maxBytes)} at {FormatBytes((long)rate)}/s, elapsed {FormatElapsed(now - started)}.");
                lastLog = now;
            }
        }

        log($"  -> [Progress] {label}: copied {FormatBytes(copied)} total.");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "unknown";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    private sealed class FileScanProgress
    {
        private readonly string _operationName;
        private readonly Action<string> _log;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private DateTime _lastLogUtc = DateTime.UtcNow;
        private long _lastLoggedScanned;

        public FileScanProgress(string operationName, Action<string> log)
        {
            _operationName = operationName;
            _log = log;
        }

        public long ItemsScanned { get; private set; }
        public long ItemsMatched { get; private set; }

        public void MarkScanned(string currentPath)
        {
            ItemsScanned++;
            MaybeLog(currentPath, force: false);
        }

        public void MarkMatched(string currentPath, int totalExtracted)
        {
            ItemsMatched++;
            MaybeLog(currentPath, force: ItemsMatched % 100 == 0 || totalExtracted % 250 == 0);
        }

        public void Finish(string message)
        {
            _log(message);
        }

        private void MaybeLog(string currentPath, bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastLogUtc < ProgressLogInterval && ItemsScanned - _lastLoggedScanned < 5000)
                return;

            double elapsedSeconds = Math.Max(0.001, (now - _startedUtc).TotalSeconds);
            double rate = ItemsScanned / elapsedSeconds;
            _log($"  -> [Progress] {_operationName}: scanned {ItemsScanned:N0} files, matched {ItemsMatched:N0} artifacts, {rate:0.0} files/sec, current: {TruncateForLog(currentPath, 160)}");
            _lastLogUtc = now;
            _lastLoggedScanned = ItemsScanned;
        }
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return "..." + value.Substring(value.Length - maxLength + 3);
    }

    private sealed class MftNameRecord
    {
        public long RecordNumber { get; init; }
        public bool InUse { get; set; }
        public bool IsDirectory { get; set; }
        public string Name { get; set; } = string.Empty;
        public int NameNamespace { get; set; } = 255;
        public long ParentRecordNumber { get; set; } = -1;
        public DateTime? CreatedUtc { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public DateTime? AccessedUtc { get; set; }
        public byte[] ResidentData { get; set; } = Array.Empty<byte>();
        public bool IsNonResident { get; set; }
        public int RunlistOffset { get; set; }
        public long RealSize { get; set; }
        public byte[] RawRecord { get; set; } = Array.Empty<byte>();
    }

    private static void ScanMftForOfficeOwnerLockFiles(NtfsFileSystem ntfs, Stream carveStream, long clusterSize, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths, Action<string> log)
    {
        var records = new Dictionary<long, MftNameRecord>();
        int totalRecords = 0;
        int decodedNames = 0;
        int candidateOwnerNames = 0;
        int extractedOwnerFiles = 0;
        int liveOwnerFiles = 0;
        int deletedOwnerFiles = 0;

        try
        {
            using var mftStream = ntfs.OpenFile(@"$MFT", FileMode.Open, FileAccess.Read);
            byte[] record = new byte[1024];
            long recordNumber = 0;

            while (mftStream.Read(record, 0, 1024) == 1024)
            {
                totalRecords++;
                var parsed = ParseMftNameRecord(record, recordNumber);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Name))
                {
                    decodedNames++;
                    records[recordNumber] = parsed;
                    if (IsOfficeOwnerLockFile(parsed.Name))
                        candidateOwnerNames++;
                }
                recordNumber++;
            }

            foreach (var rec in records.Values.Where(r => IsOfficeOwnerLockFile(r.Name)).OrderBy(r => r.InUse ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var reconstructedPath = ReconstructMftPath(records, rec);
                var displayPath = string.IsNullOrWhiteSpace(reconstructedPath) ? rec.Name : reconstructedPath;

                if (rec.InUse)
                {
                    if (TryExtractLiveMftOwnerFile(ntfs, rec, displayPath, outputDir, extracted, livePaths))
                    {
                        extractedOwnerFiles++;
                        liveOwnerFiles++;
                    }
                    continue;
                }

                if (TryExtractDeletedMftOwnerFile(rec, displayPath, carveStream, clusterSize, outputDir, extracted))
                {
                    extractedOwnerFiles++;
                    deletedOwnerFiles++;
                }
            }

            log($"  -> [Diagnostic] MFT owner/lock stats: scanned {totalRecords:N0} records, decoded {decodedNames:N0} names, found {candidateOwnerNames:N0} owner/lock filename candidates.");
            if (extractedOwnerFiles > 0)
                log($"  ✓ Extracted {extractedOwnerFiles:N0} Office owner/lock file artifacts from MFT ({liveOwnerFiles:N0} live, {deletedOwnerFiles:N0} deleted/recovered). These are document-open proximity indicators.");
            else
                log("  - No extractable Office owner/lock files found from MFT filename records.");
        }
        catch (Exception ex)
        {
            log($"  ! MFT owner/lock file scan encountered an issue: {ex.Message}");
        }
    }

    private static MftNameRecord? ParseMftNameRecord(byte[] record, long recordNumber)
    {
        try
        {
            if (record.Length < 1024 || record[0] != 0x46 || record[1] != 0x49 || record[2] != 0x4C || record[3] != 0x45)
                return null;

            var info = new MftNameRecord
            {
                RecordNumber = recordNumber,
                InUse = (record[0x16] & 0x01) != 0,
                IsDirectory = (record[0x16] & 0x02) != 0,
                RawRecord = (byte[])record.Clone()
            };

            long baseRecord = ReadFileReference(record, 0x20);
            if (baseRecord > 0)
                return null;

            int attrOffset = BitConverter.ToUInt16(record, 0x14);
            while (attrOffset > 0 && attrOffset < 1024 - 8)
            {
                uint attrType = BitConverter.ToUInt32(record, attrOffset);
                if (attrType == 0xFFFFFFFF) break;

                uint attrLen = BitConverter.ToUInt32(record, attrOffset + 4);
                if (attrLen == 0 || attrOffset + attrLen > 1024) break;

                byte nonResident = record[attrOffset + 8];
                byte attrNameLength = record[attrOffset + 9];

                if (attrType == 0x30 && nonResident == 0)
                {
                    int dataLength = (int)BitConverter.ToUInt32(record, attrOffset + 0x10);
                    int dataOffset = BitConverter.ToUInt16(record, attrOffset + 0x14);
                    int dataStart = attrOffset + dataOffset;
                    if (dataLength >= 0x42 && dataStart + dataLength <= 1024)
                    {
                        int nameLength = record[dataStart + 0x40];
                        int nameNamespace = record[dataStart + 0x41];
                        int nameOffset = dataStart + 0x42;
                        if (nameLength > 0 && nameOffset + (nameLength * 2) <= 1024)
                        {
                            string name = Encoding.Unicode.GetString(record, nameOffset, nameLength * 2);
                            if (ShouldPreferMftName(info.Name, info.NameNamespace, name, nameNamespace))
                            {
                                info.Name = name;
                                info.NameNamespace = nameNamespace;
                                info.ParentRecordNumber = ReadFileReference(record, dataStart);
                                info.CreatedUtc = SafeFileTimeUtc(BitConverter.ToInt64(record, dataStart + 0x08));
                                info.ModifiedUtc = SafeFileTimeUtc(BitConverter.ToInt64(record, dataStart + 0x10));
                                info.AccessedUtc = SafeFileTimeUtc(BitConverter.ToInt64(record, dataStart + 0x20));
                            }
                        }
                    }
                }
                else if (attrType == 0x80 && attrNameLength == 0)
                {
                    if (nonResident == 0)
                    {
                        int dataLen = (int)BitConverter.ToUInt32(record, attrOffset + 0x10);
                        int dataOffset = BitConverter.ToUInt16(record, attrOffset + 0x14);
                        if (dataLen > 0 && attrOffset + dataOffset + dataLen <= 1024)
                        {
                            info.ResidentData = new byte[dataLen];
                            Array.Copy(record, attrOffset + dataOffset, info.ResidentData, 0, dataLen);
                        }
                    }
                    else
                    {
                        int runOffset = BitConverter.ToUInt16(record, attrOffset + 0x20);
                        info.RealSize = BitConverter.ToInt64(record, attrOffset + 0x30);
                        info.RunlistOffset = attrOffset + runOffset;
                        info.IsNonResident = true;
                    }
                }

                attrOffset += (int)attrLen;
            }

            return string.IsNullOrWhiteSpace(info.Name) ? null : info;
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldPreferMftName(string currentName, int currentNamespace, string candidateName, int candidateNamespace)
    {
        if (string.IsNullOrWhiteSpace(candidateName)) return false;
        if (string.IsNullOrWhiteSpace(currentName)) return true;
        if (currentNamespace == 2 && candidateNamespace != 2) return true;
        if (candidateNamespace != 2 && candidateName.Length > currentName.Length) return true;
        return false;
    }

    private static long ReadFileReference(byte[] record, int offset)
    {
        if (offset < 0 || offset + 8 > record.Length) return -1;
        ulong raw = BitConverter.ToUInt64(record, offset);
        return (long)(raw & 0x0000FFFFFFFFFFFFUL);
    }

    private static DateTime? SafeFileTimeUtc(long value)
    {
        try
        {
            if (value <= 0) return null;
            return DateTime.FromFileTimeUtc(value);
        }
        catch
        {
            return null;
        }
    }

    private static string ReconstructMftPath(Dictionary<long, MftNameRecord> records, MftNameRecord record)
    {
        var parts = new List<string>();
        var seen = new HashSet<long>();
        var cur = record;

        while (cur != null && seen.Add(cur.RecordNumber))
        {
            if (!string.IsNullOrWhiteSpace(cur.Name) && cur.Name != ".")
                parts.Add(cur.Name);

            if (cur.ParentRecordNumber < 0 || cur.ParentRecordNumber == cur.RecordNumber || cur.ParentRecordNumber == 5)
                break;

            if (!records.TryGetValue(cur.ParentRecordNumber, out var parent))
                break;
            cur = parent;
        }

        parts.Reverse();
        return NormalizeInternalPath(string.Join("\\", parts));
    }

    private static bool TryExtractLiveMftOwnerFile(NtfsFileSystem ntfs, MftNameRecord rec, string internalPath, string outputDir, List<ExtractedArtifact> extracted, HashSet<string> livePaths)
    {
        try
        {
            var normalized = NormalizeInternalPath(internalPath);
            if (string.IsNullOrWhiteSpace(normalized) || !livePaths.Add(normalized))
                return false;

            var file = ntfs.GetFileInfo(normalized);
            if (file == null || !file.Exists)
            {
                livePaths.Remove(normalized);
                return false;
            }

            string safeName = SafeDeterministicArtifactName("Live", normalized, ParserRegistry.SafeArtifactFileName(normalized, rec.Name));
            string destPath = Path.Combine(outputDir, safeName);
            if (!File.Exists(destPath))
            {
                using var src = file.Open(FileMode.Open, FileAccess.Read);
                using var dst = File.Create(destPath);
                src.CopyTo(dst);
            }

            extracted.Add(new ExtractedArtifact
            {
                ExtractedPath = destPath,
                InternalPath = normalized,
                CreatedUtc = rec.CreatedUtc ?? SafeCreationUtc(file),
                AccessedUtc = rec.AccessedUtc ?? SafeLastAccessUtc(file),
                ModifiedUtc = rec.ModifiedUtc ?? SafeLastWriteUtc(file)
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractDeletedMftOwnerFile(MftNameRecord rec, string displayPath, Stream carveStream, long clusterSize, string outputDir, List<ExtractedArtifact> extracted)
    {
        try
        {
            string internalPath = "[DELETED MFT OWNER] " + (string.IsNullOrWhiteSpace(displayPath) ? rec.Name : displayPath);
            string safeName = SafeDeterministicArtifactName("GhostOwner", internalPath, ParserRegistry.SafeArtifactFileName(internalPath, rec.Name));
            string destPath = Path.Combine(outputDir, safeName);
            bool success = false;

            if (File.Exists(destPath))
            {
                success = true;
            }
            else if (rec.ResidentData.Length > 0)
            {
                File.WriteAllBytes(destPath, rec.ResidentData);
                success = true;
            }
            else if (rec.IsNonResident && rec.RunlistOffset > 0 && rec.RealSize > 0 && rec.RawRecord.Length == 1024)
            {
                success = CarveDataRuns(rec.RawRecord, rec.RunlistOffset, rec.RealSize, clusterSize, carveStream, destPath);
            }

            if (!success) return false;

            extracted.Add(new ExtractedArtifact
            {
                ExtractedPath = destPath,
                InternalPath = internalPath,
                CreatedUtc = rec.CreatedUtc,
                AccessedUtc = rec.AccessedUtc,
                ModifiedUtc = rec.ModifiedUtc
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ScanMftForDeletedEtherealData(NtfsFileSystem ntfs, Stream carveStream, long clusterSize, string outputDir, List<ExtractedArtifact> extracted, Action<string> log)
    {
        int recoveredCount = 0;
        int totalDeletedRecords = 0;
        int successfullyParsedNames = 0;

        log("  - Engaging RAW binary MFT carving for resident ethereal artifacts...");

        try
        {
            using var mftStream = ntfs.OpenFile(@"$MFT", FileMode.Open, FileAccess.Read);
            byte[] record = new byte[1024];

            while (mftStream.Read(record, 0, 1024) == 1024)
            {
                if (record[0] != 0x46 || record[1] != 0x49 || record[2] != 0x4C || record[3] != 0x45) continue;

                bool inUse = (record[0x16] & 0x01) != 0;
                bool isDir = (record[0x16] & 0x02) != 0;

                if (inUse || isDir) continue;

                totalDeletedRecords++;

                int attrOffset = BitConverter.ToUInt16(record, 0x14);
                string fileName = string.Empty;

                byte[] residentData = Array.Empty<byte>();
                bool isNonResident = false;
                int runlistOffset = 0;
                long realSize = 0;

                try
                {
                    while (attrOffset > 0 && attrOffset < 1024 - 8)
                    {
                        uint attrType = BitConverter.ToUInt32(record, attrOffset);
                        if (attrType == 0xFFFFFFFF) break;

                        uint attrLen = BitConverter.ToUInt32(record, attrOffset + 4);
                        if (attrLen == 0 || attrOffset + attrLen > 1024) break;

                        byte nonResident = record[attrOffset + 8];

                        if (attrType == 0x30 && nonResident == 0)
                        {
                            int dataOffset = BitConverter.ToUInt16(record, attrOffset + 0x14);
                            int nameLength = record[attrOffset + dataOffset + 0x40];
                            int nameOffset = attrOffset + dataOffset + 0x42;

                            if (nameOffset + (nameLength * 2) <= 1024 && string.IsNullOrEmpty(fileName))
                                fileName = System.Text.Encoding.Unicode.GetString(record, nameOffset, nameLength * 2);
                        }
                        else if (attrType == 0x80)
                        {
                            if (nonResident == 0)
                            {
                                int dataLen = (int)BitConverter.ToUInt32(record, attrOffset + 0x10);
                                int dataOffset = BitConverter.ToUInt16(record, attrOffset + 0x14);
                                if (attrOffset + dataOffset + dataLen <= 1024)
                                {
                                    residentData = new byte[dataLen];
                                    Array.Copy(record, attrOffset + dataOffset, residentData, 0, dataLen);
                                }
                            }
                            else
                            {
                                int runOffset = BitConverter.ToUInt16(record, attrOffset + 0x20);
                                realSize = BitConverter.ToInt64(record, attrOffset + 0x30);
                                runlistOffset = attrOffset + runOffset;
                                isNonResident = true;
                            }
                        }
                        attrOffset += (int)attrLen;
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(fileName))
                {
                    successfullyParsedNames++;

                    if (IsTargetArtifact(fileName) && !IsOfficeOwnerLockFile(fileName))
                    {
                        string safeName = SafeDeterministicArtifactName("Ghost", "[DELETED] " + fileName, ParserRegistry.SafeArtifactFileName(fileName, fileName));
                        string destPath = Path.Combine(outputDir, safeName);
                        bool success = false;

                        if (File.Exists(destPath))
                        {
                            success = true;
                        }
                        else if (!isNonResident && residentData.Length > 0)
                        {
                            File.WriteAllBytes(destPath, residentData);
                            success = true;
                        }
                        else if (isNonResident && runlistOffset > 0 && realSize > 0)
                        {
                            success = CarveDataRuns(record, runlistOffset, realSize, clusterSize, carveStream, destPath);
                        }

                        if (success)
                        {
                            extracted.Add(new ExtractedArtifact { ExtractedPath = destPath, InternalPath = $"[DELETED] {fileName}" });
                            recoveredCount++;
                        }
                    }
                }
            }

            log($"  -> [Diagnostic] MFT Stats: Examined {totalDeletedRecords} deleted records. Decoded {successfullyParsedNames} names.");

            if (recoveredCount > 0) log($"  ✓ Extracted {recoveredCount} deleted ethereal artifacts (Resident & Non-Resident).");
            else log("  - No recoverable deleted ethereal artifacts found.");
        }
        catch (Exception ex)
        {
            log($"  ! RAW MFT parsing encountered an issue: {ex.Message}");
        }
    }

    private static bool CarveDataRuns(byte[] record, int runPos, long realSize, long clusterSize, Stream carveStream, string destPath)
    {
        try
        {
            long currentLcn = 0;
            long bytesWritten = 0;
            byte[] buffer = new byte[(int)Math.Min(clusterSize, 1024 * 1024)];

            using var dst = File.Create(destPath);

            while (runPos < 1024 && record[runPos] != 0x00 && bytesWritten < realSize)
            {
                byte header = record[runPos++];
                int lenCount = header & 0x0F;
                int lenOffset = (header >> 4) & 0x0F;

                if (runPos + lenCount + lenOffset > 1024) break;

                long count = ReadVarInt(record, runPos, lenCount, false);
                runPos += lenCount;

                long lcnOffset = ReadVarInt(record, runPos, lenOffset, true);
                runPos += lenOffset;

                currentLcn += lcnOffset;

                long bytesToRead = count * clusterSize;
                long offsetInVolume = currentLcn * clusterSize;

                carveStream.Position = offsetInVolume;

                long remainingInRun = bytesToRead;
                while (remainingInRun > 0 && bytesWritten < realSize)
                {
                    int toRead = (int)Math.Min(buffer.Length, remainingInRun);
                    toRead = (int)Math.Min(toRead, realSize - bytesWritten);

                    int read = carveStream.Read(buffer, 0, toRead);
                    if (read == 0) break;

                    dst.Write(buffer, 0, read);
                    bytesWritten += read;
                    remainingInRun -= read;
                }
            }
            return bytesWritten > 0;
        }
        catch { return false; }
    }

    private static long ReadVarInt(byte[] data, int offset, int length, bool isSigned)
    {
        if (length == 0) return 0;
        long result = 0;
        for (int i = 0; i < length; i++) result |= (long)data[offset + i] << (i * 8);

        if (isSigned && (data[offset + length - 1] & 0x80) != 0)
        {
            for (int i = length; i < 8; i++) result |= 0xFFL << (i * 8);
        }
        return result;
    }

    private static bool IsTargetArtifact(string fileName)
    {
        return ParserRegistry.IsTargetArtifactPath(fileName);
    }
}
