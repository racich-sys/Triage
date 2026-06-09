using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VestigantTriage;

public static class ParserRegistry
{
    public static IReadOnlyList<IArtifactParser> OrderedParsers { get; } = new IArtifactParser[]
    {
        new GoogleWorkspaceAuditParser(),
        new GoogleTakeoutParser(),
        new MboxParser(),
        new GeminiSessionParser(),
        new O365UalParser(),
        new RecycleBinParser(),
        new BrowserHistoryParser(),
        new PowerShellHistoryParser(),
        new PowerShellTranscriptParser(),
        new SetupApiDevLogParser(),
        new AmCacheParser(),
        new SrumParser(),
        new OfficeActivityParser(),
        new OneDriveParser(),
        new DropboxParser(),
        new GoogleDriveParser(),
        new PrintSpoolParser(),
        new EvtxParser(),
        new ShellBagsParser(),
        new RegistryParser(),
        new PrefetchParser(),
        new JumpListParser(),
        new LnkParser(),
        new UsnJournalParser()
    };

    public static IArtifactParser? SelectParser(string filePath)
    {
        foreach (var parser in OrderedParsers)
        {
            try
            {
                if (parser.CanParse(filePath))
                    return parser;
            }
            catch
            {
                // Parser selection must be non-fatal. A parser can fail later with a logged parse error.
            }
        }

        return null;
    }

    public static IReadOnlyList<IArtifactParser> SelectParsers(string filePath)
    {
        var matches = new List<IArtifactParser>();
        foreach (var parser in OrderedParsers)
        {
            try
            {
                if (parser.CanParse(filePath))
                    matches.Add(parser);
            }
            catch
            {
                // Parser selection must be non-fatal. A parser can fail later with a logged parse error.
            }
        }

        return matches;
    }


    public static IReadOnlyList<IArtifactParser> SelectParsersForEvidence(string localPath, string? originalPath)
    {
        var matches = new List<IArtifactParser>();
        foreach (var parser in OrderedParsers)
        {
            var matched = false;
            try { matched = parser.CanParse(localPath); } catch { }
            if (!matched && !string.IsNullOrWhiteSpace(originalPath))
            {
                try { matched = parser.CanParse(originalPath); } catch { }
            }
            if (matched) matches.Add(parser);
        }

        return matches;
    }

    public static bool IsTargetArtifactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('/', '\\');
        var name = Path.GetFileName(normalized) ?? string.Empty;
        var lowerName = name.ToLowerInvariant();
        var lowerPath = normalized.ToLowerInvariant();

        if (IsHighValueBrowserArtifact(lowerName, lowerPath)) return true;
        if (lowerName.StartsWith("~$", StringComparison.Ordinal) || lowerName.Contains("_~$", StringComparison.Ordinal) || lowerPath.Contains("\\~$", StringComparison.Ordinal) || lowerPath.Contains("[deleted mft owner]", StringComparison.Ordinal) || lowerName.StartsWith("ghostowner_", StringComparison.Ordinal)) return true;
        if (IsPrintSpoolArtifactPath(normalized, lowerName)) return true;
        if (lowerName.EndsWith(".evtx", StringComparison.Ordinal)) return true;
        if (lowerName.EndsWith(".pf", StringComparison.Ordinal)) return true;
        if (lowerName.EndsWith(".lnk", StringComparison.Ordinal)) return true;
        if (lowerName.EndsWith(".automaticdestinations-ms", StringComparison.Ordinal)) return true;
        if (lowerName.EndsWith(".customdestinations-ms", StringComparison.Ordinal)) return true;
        if (lowerName.StartsWith("$i", StringComparison.Ordinal)) return true;
        if (lowerName is "system" or "software" or "sam" or "security" or "ntuser.dat" or "usrclass.dat") return true;
        if (lowerName.EndsWith("_system", StringComparison.Ordinal) || lowerName.EndsWith("_ntuser.dat", StringComparison.Ordinal) || lowerName.EndsWith("_usrclass.dat", StringComparison.Ordinal)) return true;

        if (lowerName == "history" || lowerName.EndsWith("_history", StringComparison.Ordinal))
            return true;

        if (lowerName == "amcache.hve" || lowerName.EndsWith("_amcache.hve", StringComparison.Ordinal) || lowerName == "amcache" || lowerName.EndsWith("_amcache", StringComparison.Ordinal))
            return true;

        if (lowerName == "srudb.dat" || lowerName.EndsWith("_srudb.dat", StringComparison.Ordinal))
            return true;

        if (lowerName == "consolehost_history.txt" || lowerName.EndsWith("_consolehost_history.txt", StringComparison.Ordinal))
            return true;

        if (lowerName.EndsWith("setupapi.dev.log", StringComparison.Ordinal) || lowerName.EndsWith("setupapi.dev.log.old", StringComparison.Ordinal) || lowerName.Contains("_setupapi.dev.log", StringComparison.Ordinal))
            return true;

        if (IsGoogleCloudArtifactPath(normalized, lowerName))
            return true;

        if (lowerName.StartsWith("powershell_transcript", StringComparison.Ordinal) || lowerName.Contains("powershell_transcript", StringComparison.Ordinal) || lowerPath.Contains(@"\powershell\transcripts", StringComparison.Ordinal))
            return true;

        if ((lowerPath.Contains(@"\microsoft\office\", StringComparison.Ordinal) || lowerPath.Contains(@"\office\", StringComparison.Ordinal)) &&
            (lowerName.EndsWith(".lnk", StringComparison.Ordinal) || lowerName.EndsWith(".dat", StringComparison.Ordinal) || lowerName.EndsWith(".tmp", StringComparison.Ordinal) || lowerName.StartsWith("~$", StringComparison.Ordinal)))
            return true;

        if (IsOneDriveApplicationArtifact(normalized, lowerName))
            return true;

        if (IsDropboxApplicationArtifact(normalized, lowerName))
            return true;

        if (IsGoogleDriveApplicationArtifact(normalized, lowerName))
            return true;

        if (lowerName is "places.sqlite" || lowerName.EndsWith("_places.sqlite", StringComparison.Ordinal))
            return true;

        if (lowerName == "$j" || lowerPath.Contains("\\$extend\\$usnjrnl", StringComparison.Ordinal))
            return true;

        return false;
    }


    internal static bool IsPrintSpoolArtifactPath(string normalizedPath, string lowerName)
    {
        var p = (normalizedPath ?? string.Empty).Replace('/', '\\');
        lowerName ??= string.Empty;

        var inQueuePath = p.Contains(@"\Windows\System32\spool\PRINTERS\", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains(@"\System32\spool\PRINTERS\", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains(@"\spool\PRINTERS\", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains(@"\Windows\System32\spool\SERVERS\", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains(@"\System32\spool\SERVERS\", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains(@"\spool\SERVERS\", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains("[DELETED PRINT SPOOL]", StringComparison.OrdinalIgnoreCase) ||
                          p.Contains("[DELETED]", StringComparison.OrdinalIgnoreCase) ||
                          lowerName.StartsWith("ghostprint", StringComparison.Ordinal);

        var inPrintConfigPath = p.Contains(@"\Windows\System32\spool\drivers\", StringComparison.OrdinalIgnoreCase) ||
                                p.Contains(@"\System32\spool\drivers\", StringComparison.OrdinalIgnoreCase) ||
                                p.Contains(@"\spool\drivers\", StringComparison.OrdinalIgnoreCase) ||
                                p.Contains(@"\Windows\System32\spool\PRTPROCS\", StringComparison.OrdinalIgnoreCase) ||
                                p.Contains(@"\System32\spool\PRTPROCS\", StringComparison.OrdinalIgnoreCase) ||
                                p.Contains(@"\spool\PRTPROCS\", StringComparison.OrdinalIgnoreCase);

        if (HasAnyExtension(lowerName, ".spl", ".shd")) return true;

        if (HasAnyExtension(lowerName, ".emf", ".xps", ".oxps", ".prn", ".pcl", ".pjl", ".ps", ".eps", ".raw"))
            return inQueuePath || LooksLikePrintArtifactName(lowerName);

        if (HasAnyExtension(lowerName, ".tmp", ".bud", ".gpd", ".ppd", ".ntf", ".dat"))
            return inQueuePath || inPrintConfigPath || lowerName.StartsWith("ghostprint", StringComparison.Ordinal);

        return false;
    }

    private static bool HasAnyExtension(string lowerName, params string[] extensions)
    {
        foreach (var ext in extensions)
        {
            if (lowerName.EndsWith(ext, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool LooksLikePrintArtifactName(string lowerName)
    {
        return lowerName.StartsWith("fp", StringComparison.Ordinal) ||
               lowerName.StartsWith("spl", StringComparison.Ordinal) ||
               lowerName.Contains("spool", StringComparison.Ordinal) ||
               lowerName.Contains("print", StringComparison.Ordinal) ||
               lowerName.Contains("printer", StringComparison.Ordinal) ||
               lowerName.StartsWith("ghostprint", StringComparison.Ordinal);
    }

    private static bool IsGoogleCloudArtifactPath(string normalizedPath, string lowerName)
    {
        var p = (normalizedPath ?? string.Empty).Replace('/', '\\');
        if (lowerName.EndsWith(".csv", StringComparison.Ordinal) && (p.Contains("Audit and Investigation", StringComparison.OrdinalIgnoreCase) || lowerName.Contains("google", StringComparison.Ordinal) || lowerName.Contains("takeout", StringComparison.Ordinal))) return true;
        if (lowerName.EndsWith(".zip", StringComparison.Ordinal) && (p.Contains("Google Audit", StringComparison.OrdinalIgnoreCase) || p.Contains("Audit and Investigation", StringComparison.OrdinalIgnoreCase) || p.Contains("Takeout", StringComparison.OrdinalIgnoreCase) || p.Contains("Gemini", StringComparison.OrdinalIgnoreCase))) return true;
        if ((lowerName.EndsWith(".json", StringComparison.Ordinal) || lowerName.EndsWith(".html", StringComparison.Ordinal) || lowerName.EndsWith(".htm", StringComparison.Ordinal)) && (p.Contains("Takeout", StringComparison.OrdinalIgnoreCase) || p.Contains("Google", StringComparison.OrdinalIgnoreCase))) return true;
        if (p.Contains("Gemini", StringComparison.OrdinalIgnoreCase) && (lowerName.EndsWith(".rtf", StringComparison.Ordinal) || lowerName.EndsWith(".pdf", StringComparison.Ordinal) || lowerName.EndsWith(".png", StringComparison.Ordinal) || lowerName.EndsWith(".jpg", StringComparison.Ordinal) || lowerName.EndsWith(".jpeg", StringComparison.Ordinal) || lowerName.EndsWith(".py", StringComparison.Ordinal))) return true;
        return false;
    }

    private static bool IsOneDriveApplicationArtifact(string normalizedPath, string lowerName)
    {
        var inOneDriveAppTree = ParserSupport.HasPathSequence(normalizedPath, "Microsoft", "OneDrive") || ParserSupport.HasPathSegment(normalizedPath, "OneDrive");
        if (!inOneDriveAppTree)
            return false;

        if (lowerName.EndsWith(".pf", StringComparison.Ordinal) || lowerName.EndsWith(".exe", StringComparison.Ordinal))
            return false;

        var inSettings = ParserSupport.HasPathSegment(normalizedPath, "settings");
        if (inSettings && (lowerName.EndsWith(".json", StringComparison.Ordinal) || lowerName.EndsWith(".ini", StringComparison.Ordinal) || lowerName.EndsWith(".db", StringComparison.Ordinal)))
            return true;
        return lowerName is "syncenginedatabase.db" or "settingsdatabase.db" or "safedelete.db" or "global.ini" or "clientpolicy.ini" or "business1.ini" or "personal.ini";
    }

    private static bool IsDropboxApplicationArtifact(string normalizedPath, string lowerName)
    {
        if (!ParserSupport.HasPathSegment(normalizedPath, "Dropbox"))
            return false;

        if (lowerName.EndsWith(".pf", StringComparison.Ordinal) || lowerName.EndsWith(".exe", StringComparison.Ordinal))
            return false;

        return lowerName is "info.json" or "host.db" or "config.db" or "filecache.db" or "deleted.db" ||
               lowerName.EndsWith(".db", StringComparison.Ordinal) || lowerName.EndsWith(".json", StringComparison.Ordinal) || lowerName.EndsWith(".log", StringComparison.Ordinal);
    }

    private static bool IsGoogleDriveApplicationArtifact(string normalizedPath, string lowerName)
    {
        var inDriveFs = ParserSupport.HasPathSequence(normalizedPath, "Google", "DriveFS") || ParserSupport.HasPathSegment(normalizedPath, "Google Drive");
        if (!inDriveFs)
            return false;

        if (lowerName.EndsWith(".pf", StringComparison.Ordinal) || lowerName.EndsWith(".exe", StringComparison.Ordinal))
            return false;

        return lowerName is "metadata_sqlite_db" or "mirror_sqlite.db" or "snapshot.db" or "sync_config.db" ||
               lowerName.EndsWith(".db", StringComparison.Ordinal) || lowerName.EndsWith(".log", StringComparison.Ordinal) || lowerName.EndsWith(".json", StringComparison.Ordinal);
    }

    private static bool IsHighValueBrowserArtifact(string lowerName, string lowerPath)
    {
        if (lowerName == "history" || lowerName.EndsWith("_history", StringComparison.Ordinal))
            return true;

        if (lowerName == "history-journal" || lowerName.EndsWith("_history-journal", StringComparison.Ordinal))
            return true;

        if (lowerName == "places.sqlite" || lowerName.EndsWith("_places.sqlite", StringComparison.Ordinal))
            return true;

        if (lowerName == "places.sqlite-wal" || lowerName == "places.sqlite-shm" ||
            lowerName.EndsWith("_places.sqlite-wal", StringComparison.Ordinal) ||
            lowerName.EndsWith("_places.sqlite-shm", StringComparison.Ordinal))
            return true;

        if (lowerName == "downloads.sqlite" || lowerName.EndsWith("_downloads.sqlite", StringComparison.Ordinal))
            return true;

        if (lowerName == "webcachev01.dat" || lowerName.EndsWith("_webcachev01.dat", StringComparison.Ordinal))
            return true;

        if ((lowerPath.Contains(@"\google\chrome\user data\", StringComparison.Ordinal) ||
             lowerPath.Contains(@"\microsoft\edge\user data\", StringComparison.Ordinal) ||
             lowerPath.Contains(@"\brave-browser\user data\", StringComparison.Ordinal) ||
             lowerPath.Contains(@"\mozilla\firefox\profiles\", StringComparison.Ordinal)) &&
            (lowerName == "history" || lowerName == "places.sqlite"))
            return true;

        return false;
    }

    public static string SafeArtifactFileName(string internalPath, string fallbackName)
    {
        var userOrSid = ExtractOwnerHint(internalPath);
        var artifactName = Path.GetFileName(internalPath.Replace('/', '\\'));
        if (string.IsNullOrWhiteSpace(artifactName))
            artifactName = fallbackName;

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            userOrSid = userOrSid.Replace(ch, '_');
            artifactName = artifactName.Replace(ch, '_');
        }

        return string.IsNullOrWhiteSpace(userOrSid) ? artifactName : $"{userOrSid}_{artifactName}";
    }

    private static string ExtractOwnerHint(string path)
    {
        var segments = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("Users", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];

            if (segments[i].Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }

        if (segments.Any(s => s.Equals("Windows", StringComparison.OrdinalIgnoreCase)))
            return "System";

        return "Artifact";
    }
}
