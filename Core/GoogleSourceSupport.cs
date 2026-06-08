using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VestigantTriage;

internal sealed class GoogleAuditFamilyDefinition
{
    public string Family { get; set; } = "Unknown";
    public string CanonicalDataSource { get; set; } = "Google Workspace Audit - Unknown";
    public string[] FileNameTokens { get; set; } = Array.Empty<string>();
    public string[] TimestampFields { get; set; } = Array.Empty<string>();
    public string[] ActorFields { get; set; } = Array.Empty<string>();
    public string[] IpFields { get; set; } = Array.Empty<string>();
    public string[] TargetFields { get; set; } = Array.Empty<string>();
    public string[] OperationFields { get; set; } = Array.Empty<string>();
}

internal static class GoogleAuditSourceRegistry
{
    public static IReadOnlyList<string> DocumentedAuditFamilies { get; } = new[]
    {
        "Access Evaluation", "Access Transparency", "Admin", "Admin Data Action", "Assignments", "Calendar", "Chat", "Chrome Browsers", "Chrome", "Chrome Sync",
        "Classroom", "Cloud Search", "Contacts", "Context-Aware Access", "Data Migration", "Device", "Directory Sync", "Drive", "Gemini for Workspace", "Gmail",
        "Google Workspace Quota", "Graduation", "Groups", "Groups Enterprise", "Keep", "Looker Studio", "Meet Hardware", "Meet", "OAuth", "Policy Compliance",
        "Profile", "Rule", "SAML", "Search and Investigate User", "Secure LDAP", "Takeout", "Tasks", "User", "Vault", "Voice", "Workspace Studio"
    };

    private static readonly GoogleAuditFamilyDefinition Default = new()
    {
        Family = "Unknown",
        CanonicalDataSource = "Google Workspace Audit - Unknown",
        TimestampFields = new[] { "Date", "CreationDate", "Creation Time", "Activity Timestamp", "Time", "Timestamp" },
        ActorFields = new[] { "Actor", "User", "User email", "Owner", "Profile user", "Device user", "Gaia ID", "Takeout initiator", "Target User" },
        IpFields = new[] { "IP address", "IP Address", "Client IP", "ClientIP", "Remote IP", "Local IP" },
        TargetFields = new[] { "Document ID", "Title", "Target", "URL", "Resource Url", "Message ID", "Attachment URL", "Attachment name", "App name", "Device ID", "Calendar ID", "Event ID", "Task ID", "Group email", "OAuth Client ID" },
        OperationFields = new[] { "Event", "OAuth event", "Activity Type", "Action", "Description" }
    };

    public static IReadOnlyList<GoogleAuditFamilyDefinition> Families { get; } = BuildFamilies();

    public static bool LooksLikeGoogleAuditFileName(string name)
    {
        name = (name ?? string.Empty).Replace('\\', '/');
        var lower = name.ToLowerInvariant();
        return lower.Contains("audit and investigation") || lower.Contains("google audit") || lower.Contains("google workspace audit") ||
               Families.Any(f => f.FileNameTokens.Any(t => lower.Contains(t.ToLowerInvariant())));
    }

    public static GoogleAuditFamilyDefinition Identify(string fileName, IEnumerable<string>? headers = null)
    {
        var normalized = (fileName ?? string.Empty).Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();
        foreach (var family in Families)
        {
            if (family.FileNameTokens.Any(t => lower.Contains(t.ToLowerInvariant())))
                return family;
        }

        var headerSet = (headers ?? Enumerable.Empty<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (headerSet.Contains("OAuth event") || headerSet.Contains("Scope") || headerSet.Contains("API name")) return Families.First(f => f.Family == "OAuth");
        if (headerSet.Contains("Document ID") || headerSet.Contains("Prior visibility") || headerSet.Contains("Visibility")) return Families.First(f => f.Family == "Drive");
        if (headerSet.Contains("Message ID") && (headerSet.Contains("Subject") || headerSet.Contains("From (Header address)"))) return Families.First(f => f.Family == "Gmail");
        if (headerSet.Contains("Takeout job ID") || headerSet.Contains("Products requested")) return Families.First(f => f.Family == "Takeout");
        if (headerSet.Contains("App name") && headerSet.Contains("Feature source")) return Families.First(f => f.Family == "Gemini for Workspace");
        if (headerSet.Contains("Device ID") && headerSet.Contains("Device type")) return Families.First(f => f.Family == "Device");
        if (headerSet.Contains("Meeting code") || headerSet.Contains("Conference ID")) return Families.First(f => f.Family == "Meet");
        if (headerSet.Contains("Room ID") || headerSet.Contains("Attachment URL")) return Families.First(f => f.Family == "Chat");
        if (headerSet.Contains("Calendar ID") || headerSet.Contains("Event title")) return Families.First(f => f.Family == "Calendar");
        if (headerSet.Contains("Matter ID")) return Families.First(f => f.Family == "Vault");
        if (headerSet.Contains("Chrome version") || headerSet.Contains("Browser ID")) return Families.First(f => f.Family == "Chrome Browsers");

        return Default;
    }

    private static IReadOnlyList<GoogleAuditFamilyDefinition> BuildFamilies()
    {
        GoogleAuditFamilyDefinition Def(string family, string[] tokens, string[] targetFields, string[]? actorFields = null, string[]? operationFields = null, string[]? ipFields = null, string[]? timestampFields = null) => new()
        {
            Family = family,
            CanonicalDataSource = "Google Workspace Audit - " + family,
            FileNameTokens = tokens,
            TimestampFields = timestampFields ?? Default.TimestampFields,
            ActorFields = actorFields ?? Default.ActorFields,
            IpFields = ipFields ?? Default.IpFields,
            TargetFields = targetFields,
            OperationFields = operationFields ?? Default.OperationFields
        };

        return new[]
        {
            Def("Access Evaluation", new[] { "access evaluation" }, new[] { "Application Name", "OAuth Client ID", "Scope", "Service Account" }),
            Def("Access Transparency", new[] { "access transparency" }, new[] { "Resource", "Resource name", "Justification" }),
            Def("Admin", new[] { "admin log", "admin audit" }, new[] { "Target", "Resources", "Setting", "New value", "Old value" }),
            Def("Admin Data Action", new[] { "admin data action" }, new[] { "Target", "Resources", "Resource" }),
            Def("Assignments", new[] { "assignments" }, new[] { "Course ID", "Course title", "Assignment ID", "Assignment title" }),
            Def("Calendar", new[] { "calendar log" }, new[] { "Event title", "Calendar ID", "Event ID", "Target", "Organizer calendar ID", "Subscriber calendar ID" }),
            Def("Chat", new[] { "chat log" }, new[] { "Room name", "Room ID", "Message ID", "Attachment URL", "Attachment name", "Filename", "Recipients" }),
            Def("Chrome Browsers", new[] { "chrome browsers" }, new[] { "Device name", "Device ID", "Browser ID", "Chrome signed in users" }, actorFields: new[] { "Chrome signed in users", "Machine user" }, timestampFields: new[] { "Last status report time", "Last policy fetch time", "Registration time" }),
            Def("Chrome", new[] { "chrome log" }, new[] { "URL", "Content name", "Content hash", "Application name", "Application ID", "Device name" }, actorFields: new[] { "Profile user", "Device user", "Actor", "User" }, ipFields: new[] { "Local IP", "Remote IP", "IP address" }),
            Def("Chrome Sync", new[] { "chrome sync" }, new[] { "Entity", "Resources" }),
            Def("Classroom", new[] { "classroom" }, new[] { "Course ID", "Course title", "Actor", "Target" }),
            Def("Cloud Search", new[] { "cloud search" }, new[] { "Query", "Resource", "Target" }),
            Def("Contacts", new[] { "contacts" }, new[] { "Contact", "Contact ID", "Target" }),
            Def("Context-Aware Access", new[] { "context-aware", "context aware" }, new[] { "Application Name", "Configuration Source", "Service Account" }),
            Def("Data Migration", new[] { "data migration" }, new[] { "Migration ID", "Target", "Resources" }),
            Def("Device", new[] { "device log" }, new[] { "Device ID", "Device model", "Serial number", "Resource ID", "Application ID", "Policy name" }, actorFields: new[] { "User email", "Actor", "User" }),
            Def("Directory Sync", new[] { "directory sync" }, new[] { "Resource", "Target", "Directory" }),
            Def("Drive", new[] { "drive log" }, new[] { "Document ID", "Title", "Document type", "Target", "Recipient doc", "Revision ID", "Visibility", "Prior visibility" }),
            Def("Gemini for Workspace", new[] { "gemini for workspace" }, new[] { "App name", "Feature source", "Action", "Event category" }),
            Def("Gmail", new[] { "gmail log" }, new[] { "Message ID", "Subject", "From (Header address)", "From (Envelope)", "To (Envelope)", "Attachment name", "Link domain", "Resources" }, actorFields: new[] { "Owner", "Delegate", "Actor", "User" }),
            Def("Google Workspace Quota", new[] { "workspace quota", "quota" }, new[] { "Resource", "Quota", "Target" }),
            Def("Graduation", new[] { "graduation" }, new[] { "Target", "Resources" }),
            Def("Groups", new[] { "groups log" }, new[] { "Group email", "Target", "Message ID", "Resources" }),
            Def("Groups Enterprise", new[] { "groups enterprise" }, new[] { "Group identifier", "Member identifier", "Dynamic group query", "Resources" }),
            Def("Keep", new[] { "keep" }, new[] { "Note ID", "Title", "Target" }),
            Def("Looker Studio", new[] { "looker studio" }, new[] { "Report ID", "Report Name", "Data Source" }),
            Def("Meet Hardware", new[] { "meet hardware" }, new[] { "Device ID", "Meeting code", "Conference ID" }),
            Def("Meet", new[] { "meet log" }, new[] { "Meeting code", "Conference ID", "Calendar event ID", "Organizer email", "Acceptor email", "Endpoint ID" }),
            Def("OAuth", new[] { "oauth log" }, new[] { "App ID", "App name", "Scope", "API name", "Method", "Product" }, actorFields: new[] { "User", "Actor" }, operationFields: new[] { "OAuth event", "Event", "Method", "Description" }),
            Def("Policy Compliance", new[] { "policy compliance" }, new[] { "Policy", "Rule", "Target" }),
            Def("Profile", new[] { "profile" }, new[] { "Profile", "Target", "Resources" }),
            Def("Rule", new[] { "rule log" }, new[] { "Rule name", "Rule ID", "Target", "Resources" }),
            Def("SAML", new[] { "saml" }, new[] { "Application", "Service Provider", "Target" }),
            Def("Search and Investigate User", new[] { "search and investigate", "investigate user" }, new[] { "Query", "Target", "Resources" }),
            Def("Secure LDAP", new[] { "secure ldap" }, new[] { "Client", "Target", "Resources" }),
            Def("Takeout", new[] { "takeout log" }, new[] { "Takeout job ID", "Products requested", "Takeout destination", "Target", "Takeout status" }, actorFields: new[] { "Actor", "Takeout initiator", "User" }),
            Def("Tasks", new[] { "tasks log" }, new[] { "Task list ID", "Task title", "New task title", "Task ID", "Task list title", "Email of assignee" }),
            Def("User", new[] { "user log" }, new[] { "User", "Affected user", "Email forwarding address", "Sensitive action name", "Guest user email" }, actorFields: new[] { "User", "Affected user", "Guest user email" }),
            Def("Vault", new[] { "vault log" }, new[] { "Matter ID", "Resource Url", "Resource Name", "Target User", "Query" }),
            Def("Voice", new[] { "voice" }, new[] { "Phone number", "Call ID", "Target" }),
            Def("Workspace Studio", new[] { "workspace studio" }, new[] { "Flow ID", "Flow name", "Run ID", "Step name", "Step app", "Step app provider" })
        };
    }
}

internal static class GoogleSourceSupport
{
    public static bool IsCsv(string path) => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    public static bool IsJson(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    public static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public static bool IsTarGz(string path) => path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    public static bool ZipContains(string filePath, Func<string, bool> predicate)
    {
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            return archive.Entries.Any(e => !string.IsNullOrWhiteSpace(e.Name) && predicate(e.FullName));
        }
        catch { return false; }
    }

    public static IEnumerable<Dictionary<string, string>> ReadCsvRows(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var headers = ReadCsvRecord(reader);
        if (headers == null || headers.Count == 0)
            yield break;
        for (int i = 0; i < headers.Count; i++)
            headers[i] = (headers[i] ?? string.Empty).Trim().TrimStart('\uFEFF');
        while (true)
        {
            var fields = ReadCsvRecord(reader);
            if (fields == null) yield break;
            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++) row[headers[i]] = i < fields.Count ? fields[i] ?? string.Empty : string.Empty;
            yield return row;
        }
    }

    public static IReadOnlyList<string> ReadCsvHeaders(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            return ReadCsvHeaders(fs);
        }
        catch { return Array.Empty<string>(); }
    }

    public static IReadOnlyList<string> ReadCsvHeaders(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return (ReadCsvRecord(reader) ?? new List<string>()).Select(h => (h ?? string.Empty).Trim().TrimStart('\uFEFF')).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static List<string>? ReadCsvRecord(StreamReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool sawAnyCharacter = false;
        while (true)
        {
            int raw = reader.Read();
            if (raw < 0)
            {
                if (!sawAnyCharacter && field.Length == 0 && fields.Count == 0) return null;
                fields.Add(field.ToString());
                return fields;
            }
            sawAnyCharacter = true;
            char ch = (char)raw;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"') { inQuotes = true; continue; }
            if (ch == ',') { fields.Add(field.ToString()); field.Clear(); continue; }
            if (ch == '\r') { if (reader.Peek() == '\n') reader.Read(); fields.Add(field.ToString()); return fields; }
            if (ch == '\n') { fields.Add(field.ToString()); return fields; }
            field.Append(ch);
        }
    }

    public static string Get(IDictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
            if (!string.IsNullOrWhiteSpace(key) && row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return Clean(value);
        return string.Empty;
    }

    public static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return Clean(value);
        return string.Empty;
    }

    public static string Clean(string? value) => ForensicText.CleanDisplayValue(value);

    public static string NormalizeOperation(string family, string operation, IDictionary<string, string> row)
    {
        var text = FirstNonBlank(operation, Get(row, "Description"), Get(row, "Action"));
        var lower = text.ToLowerInvariant();
        if (family.Equals("Drive", StringComparison.OrdinalIgnoreCase))
        {
            if (lower.Contains("download") || lower.Contains("export")) return "FileDownloaded";
            if (lower.Contains("share") || lower.Contains("visibility") || lower.Contains("permission") || lower.Contains("link")) return "GoogleDrive_SharingOrVisibilityChanged";
            if (lower.Contains("delete") || lower.Contains("trash")) return "FileDeleted";
            if (lower.Contains("copy")) return "GoogleDrive_FileCopied";
            if (lower.Contains("move")) return "GoogleDrive_FileMoved";
            if (lower.Contains("preview") || lower.Contains("view") || lower.Contains("open")) return "GoogleDrive_FileAccessed";
        }
        if (family.Equals("Gmail", StringComparison.OrdinalIgnoreCase))
        {
            if (lower.Contains("download") && lower.Contains("attachment")) return "GoogleGmail_AttachmentDownloaded";
            if (lower.Contains("forward")) return "GoogleGmail_ForwardingObserved";
            if (lower.Contains("delegate")) return "GoogleGmail_DelegationObserved";
            if (lower.Contains("send") || lower.Contains("sent")) return "GoogleGmail_MessageSent";
            if (lower.Contains("access") || lower.Contains("read") || lower.Contains("view")) return "MailItemsAccessed";
        }
        if (family.Equals("OAuth", StringComparison.OrdinalIgnoreCase)) return "GoogleOAuth_" + SafeOperationToken(text);
        if (family.Equals("Takeout", StringComparison.OrdinalIgnoreCase)) return "GoogleTakeout_" + SafeOperationToken(text);
        if (family.Equals("Gemini for Workspace", StringComparison.OrdinalIgnoreCase)) return "GoogleGemini_" + SafeOperationToken(text);
        if (family.Equals("User", StringComparison.OrdinalIgnoreCase)) return "GoogleUser_" + SafeOperationToken(text);
        if (family.Equals("Device", StringComparison.OrdinalIgnoreCase)) return "GoogleDevice_" + SafeOperationToken(text);
        if (family.Equals("Vault", StringComparison.OrdinalIgnoreCase)) return "GoogleVault_" + SafeOperationToken(text);
        return string.IsNullOrWhiteSpace(text) ? "GoogleAudit_Event" : text;
    }

    public static string SafeOperationToken(string value)
    {
        value = Clean(value);
        if (string.IsNullOrWhiteSpace(value)) return "Event";
        var token = Regex.Replace(value, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (token.Length > 80) token = token.Substring(0, 80);
        return string.IsNullOrWhiteSpace(token) ? "Event" : token;
    }

    public static void AddGoogleRiskFields(NormalizedEvent ev, string family, IDictionary<string, string> row)
    {
        var joined = string.Join(" ", new[] { ev.Operation, ev.ObjectPath, Get(row, "Description"), Get(row, "Event"), Get(row, "OAuth event"), Get(row, "Action"), Get(row, "Scope"), Get(row, "Products requested"), Get(row, "App name"), Get(row, "Subject"), Get(row, "Title"), Get(row, "Attachment name"), Get(row, "Takeout destination"), Get(row, "Visibility"), Get(row, "Prior visibility"), Get(row, "New value"), Get(row, "Old value") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var lower = joined.ToLowerInvariant();
        bool transfer = family is "Drive" or "Gmail" or "OAuth" or "Chat" or "Takeout" || ContainsAny(lower, "download", "export", "share", "external", "attachment", "scope", "drive", "gmail", "takeout", "copy");
        bool destruction = family is "Vault" or "Admin" or "Device" || ContainsAny(lower, "delete", "remove", "wipe", "reset", "suspend", "purge", "vault", "hold", "retention");
        bool ai = family.Equals("Gemini for Workspace", StringComparison.OrdinalIgnoreCase) || ContainsAny(lower, "gemini", "ai", "code", "prompt", "generate", "summarize");
        bool admin = family is "Admin" or "Admin Data Action" or "Vault" or "Rule" || ContainsAny(lower, "admin", "setting", "policy", "rule", "retention", "audit", "forwarding");
        ev.AdditionalFields["GoogleRiskTransferPotential"] = transfer ? "Yes" : "No";
        ev.AdditionalFields["GoogleRiskDestructionPotential"] = destruction ? "Yes" : "No";
        ev.AdditionalFields["GoogleRiskAiPotential"] = ai ? "Yes" : "No";
        ev.AdditionalFields["GoogleRiskAdminPotential"] = admin ? "Yes" : "No";
        ev.AdditionalFields["GoogleRiskReason"] = BuildRiskReason(transfer, destruction, ai, admin, family);
    }

    private static string BuildRiskReason(bool transfer, bool destruction, bool ai, bool admin, string family)
    {
        var parts = new List<string>();
        if (transfer) parts.Add("transfer-capable Google event family/text");
        if (destruction) parts.Add("destruction/concealment/admin-relevant Google event family/text");
        if (ai) parts.Add("AI/Gemini-relevant Google event family/text");
        if (admin) parts.Add("administrative-control Google event family/text");
        return parts.Count == 0 ? $"Google {family} event retained for context" : string.Join("; ", parts);
    }

    public static bool ContainsAny(string text, params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    public static string InferTargetFileName(string target)
    {
        target = Clean(target);
        if (string.IsNullOrWhiteSpace(target)) return string.Empty;
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.LocalPath)) return Path.GetFileName(uri.LocalPath.TrimEnd('/', '\\'));
            return Path.GetFileName(target.TrimEnd('/', '\\'));
        }
        catch { return string.Empty; }
    }

    public static string FamilyFromTakeoutPath(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("mail/user settings")) return "Mail User Settings";
        if (p.Contains("youtube") || p.Contains("youtube music")) return "YouTube and YouTube Music";
        if (p.Contains("devices - a list of devices")) return "Devices";
        if (p.Contains("activities - a list of google services")) return "Activities";
        if (p.Contains("location history")) return "Location History";
        if (p.Contains("chrome")) return "Chrome";
        if (p.Contains("drive")) return "Drive";
        return "Generic Product";
    }
}
