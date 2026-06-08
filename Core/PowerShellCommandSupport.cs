using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VestigantTriage;

internal static class PowerShellCommandSupport
{
    private static readonly Regex UrlRegex = new("https?://[^\\s\\\"'<>]{4,1000}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EncodedCommandRegex = new(@"(?i)(?:-|/)(?:enc|encodedcommand|e)\s+([A-Za-z0-9+/=]{20,})", RegexOptions.Compiled);

    public static string ExtractFirstUrl(string command)
    {
        var m = UrlRegex.Match(command ?? string.Empty);
        return m.Success ? ForensicText.TrimBinaryPathTail(m.Value) : string.Empty;
    }

    public static string ExtractFirstPath(string command)
    {
        foreach (var path in ForensicText.ExtractPathCandidates(command ?? string.Empty)) return path;
        return string.Empty;
    }

    public static string TryDecodeEncodedCommand(string command)
    {
        var m = EncodedCommandRegex.Match(command ?? string.Empty);
        if (!m.Success) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(m.Groups[1].Value);
            var decoded = ParserSupport.Clean(Encoding.Unicode.GetString(bytes));
            return decoded.Length > 12000 ? decoded[..12000] : decoded;
        }
        catch { return string.Empty; }
    }

    public static string ClassifyCommand(string command)
    {
        var c = command ?? string.Empty;
        if (ContainsAny(c, "Invoke-WebRequest", "Invoke-RestMethod", "Start-BitsTransfer", "curl", "wget", "scp", "sftp", "rclone", "azcopy", "ftp", "iwr", "irm")) return "NetworkTransferCommand";
        if (ContainsAny(c, "Compress-Archive", "7z", "winrar", "tar ", "zip")) return "ArchiveOrStagingCommand";
        if (ContainsAny(c, "Remove-Item", "del ", "erase", "sdelete", "cipher /w", "Clear-RecycleBin")) return "DeletionOrWipeCommand";
        if (ContainsAny(c, "Copy-Item", "Move-Item", "robocopy", "xcopy", "Copy ")) return "FileCopyMoveCommand";
        if (ContainsAny(c, "Set-ExecutionPolicy", "-ExecutionPolicy Bypass", "DownloadString", "FromBase64String", "Add-MpPreference")) return "SuspiciousPowerShellCommand";
        return "PowerShellCommand";
    }

    public static void AddCommandFields(NormalizedEvent ev, string command)
    {
        var decoded = TryDecodeEncodedCommand(command);
        var analysisText = string.IsNullOrWhiteSpace(decoded) ? command : decoded;
        ev.AdditionalFields["CommandLine"] = command;
        if (!string.IsNullOrWhiteSpace(decoded)) ev.AdditionalFields["DecodedEncodedCommand"] = decoded;
        ev.AdditionalFields["CommandClassification"] = ClassifyCommand(analysisText);
        ev.AdditionalFields["ContainsUrl"] = (!string.IsNullOrWhiteSpace(ExtractFirstUrl(analysisText))).ToString();
        ev.AdditionalFields["ContainsPath"] = (!string.IsNullOrWhiteSpace(ExtractFirstPath(analysisText))).ToString();
        var url = ExtractFirstUrl(analysisText);
        if (!string.IsNullOrWhiteSpace(url))
        {
            ev.AdditionalFields["Url"] = url;
            ev.AdditionalFields["Domain"] = ParserSupport.InferCloudOrWebDomain(url);
        }
        var path = ExtractFirstPath(analysisText);
        if (!string.IsNullOrWhiteSpace(path)) ParserSupport.AddTargetFields(ev, path, "PowerShellCommandPath");
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));
}
