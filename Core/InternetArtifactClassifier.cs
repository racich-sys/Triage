using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VestigantTriage;

internal sealed class InternetArtifactClassification
{
    public string Url { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Category { get; set; } = "OtherWeb";
    public bool IsCloudStorageUrl { get; set; }
    public bool IsPersonalEmailUrl { get; set; }
    public bool IsLocalHostUrl { get; set; }
    public bool IsLocalFileUrl { get; set; }
    public bool IsFileExplorerLocalAccess { get; set; }
    public string MatchedIndicator { get; set; } = string.Empty;
    public string LocalFilePath { get; set; } = string.Empty;
}

internal static class InternetArtifactClassifier
{
    private static readonly string[] CloudStorageIndicators =
    {
        "drive.google.com", "docs.google.com", "sheets.google.com", "slides.google.com",
        "dropbox.com", "dropboxusercontent.com", "box.com", "app.box.com",
        "onedrive.live.com", "1drv.ms", "sharepoint.com", "my.sharepoint.com",
        "mega.nz", "mega.co.nz", "wetransfer.com", "transfernow.net", "fromsmash.com",
        "icloud.com/iclouddrive", "icloud.com/drive", "pcloud.com", "mediafire.com",
        "sync.com", "sugarsync.com", "nextcloud", "owncloud", "egnyte.com"
    };

    private static readonly string[] PersonalEmailIndicators =
    {
        "mail.google.com", "gmail.com", "accounts.google.com/servicelogin",
        "outlook.live.com", "login.live.com", "hotmail.com", "live.com/mail",
        "mail.yahoo.com", "ymail.com", "rocketmail.com",
        "proton.me", "protonmail.com", "mail.proton.me",
        "icloud.com/mail", "mail.icloud.com", "aol.com", "mail.aol.com",
        "mail.com", "gmx.com", "gmx.net", "zoho.com/mail", "mail.zoho.com",
        "tutanota.com", "tuta.com", "fastmail.com"
    };

    public static InternetArtifactClassification Classify(string? value)
    {
        value = ParserSupport.Clean(value);
        var result = new InternetArtifactClassification { Url = value ?? string.Empty };
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Category = "Blank";
            return result;
        }

        if (LooksLikeWindowsPath(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            result.Category = "LocalOrNetworkPath";
            result.IsLocalFileUrl = true;
            result.IsFileExplorerLocalAccess = true;
            result.LocalFilePath = value;
            result.MatchedIndicator = "WindowsPath";
            return result;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            result.Scheme = uri.Scheme.ToLowerInvariant();
            result.Host = (uri.Host ?? string.Empty).ToLowerInvariant();

            if (uri.IsFile || result.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                result.Category = "LocalFileUrl";
                result.IsLocalFileUrl = true;
                result.IsFileExplorerLocalAccess = true;
                try { result.LocalFilePath = Uri.UnescapeDataString(uri.LocalPath ?? string.Empty); } catch { result.LocalFilePath = uri.LocalPath ?? string.Empty; }
                result.MatchedIndicator = "file://";
                return result;
            }

            if (IsLocalHost(result.Host))
            {
                result.Category = "LocalHostUrl";
                result.IsLocalHostUrl = true;
                result.IsFileExplorerLocalAccess = true;
                result.MatchedIndicator = result.Host;
                return result;
            }

            var normalized = NormalizeForMatch(value, result.Host);
            var emailMatch = PersonalEmailIndicators.FirstOrDefault(i => normalized.Contains(i, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(emailMatch))
            {
                result.Category = "PersonalEmailWebmail";
                result.IsPersonalEmailUrl = true;
                result.MatchedIndicator = emailMatch;
                return result;
            }

            var cloudMatch = CloudStorageIndicators.FirstOrDefault(i => normalized.Contains(i, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(cloudMatch))
            {
                result.Category = "CloudStorage";
                result.IsCloudStorageUrl = true;
                result.MatchedIndicator = cloudMatch;
                return result;
            }

            result.Category = string.IsNullOrWhiteSpace(result.Host) ? "OtherUrl" : "OtherWeb";
            return result;
        }

        if (value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            result.Category = "LocalHostUrl";
            result.Host = "localhost";
            result.IsLocalHostUrl = true;
            result.IsFileExplorerLocalAccess = true;
            result.MatchedIndicator = "localhost";
            return result;
        }

        result.Category = "UnparsedText";
        return result;
    }

    public static void AddFields(NormalizedEvent ev, string? urlOrPath, string sourceFieldName)
    {
        var c = Classify(urlOrPath);
        ev.AdditionalFields["UrlSourceField"] = sourceFieldName ?? string.Empty;
        ev.AdditionalFields["UrlScheme"] = c.Scheme;
        ev.AdditionalFields["UrlHost"] = c.Host;
        ev.AdditionalFields["UrlCategory"] = c.Category;
        ev.AdditionalFields["UrlMatchedIndicator"] = c.MatchedIndicator;
        ev.AdditionalFields["IsCloudStorageUrl"] = c.IsCloudStorageUrl ? "Yes" : "No";
        ev.AdditionalFields["IsPersonalEmailUrl"] = c.IsPersonalEmailUrl ? "Yes" : "No";
        ev.AdditionalFields["IsLocalHostUrl"] = c.IsLocalHostUrl ? "Yes" : "No";
        ev.AdditionalFields["IsLocalFileUrl"] = c.IsLocalFileUrl ? "Yes" : "No";
        ev.AdditionalFields["IsFileExplorerLocalAccess"] = c.IsFileExplorerLocalAccess ? "Yes" : "No";
        if (!string.IsNullOrWhiteSpace(c.LocalFilePath))
            ev.AdditionalFields["LocalFilePath"] = c.LocalFilePath;
    }

    private static string NormalizeForMatch(string value, string host)
    {
        value = (value ?? string.Empty).ToLowerInvariant();
        host = (host ?? string.Empty).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(host) ? value : host + " " + value;
    }

    private static bool LooksLikeWindowsPath(string value)
    {
        return Regex.IsMatch(value ?? string.Empty, @"(?i)^[a-z]:[\\/]");
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim('[', ']').ToLowerInvariant();
        return host == "localhost" || host == "::1" || host == "0:0:0:0:0:0:0:1" || host.StartsWith("127.", StringComparison.Ordinal);
    }
}
