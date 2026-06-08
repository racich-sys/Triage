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
        var text = FirstNonBlank(Get(row, "Event"), Get(row, "OAuth event"), Get(row, "Action"), Get(row, "Activity Type"), operation, Get(row, "Description"));
        var key = OperationKey(text);

        if (family.Equals("Drive", StringComparison.OrdinalIgnoreCase))
        {
            return key switch
            {
                "download" => "FileDownloaded",
                "export" => "GoogleDrive_FileExported",
                "change document visibility" => "GoogleDrive_VisibilityChanged",
                "user sharing permissions change" => "GoogleDrive_UserSharingPermissionsChanged",
                "change user access" => "GoogleDrive_UserAccessChanged",
                "change access scope" => "GoogleDrive_AccessScopeChanged",
                "item content synced" => "GoogleDrive_ItemContentSynced",
                "item content accessed" => "GoogleDrive_FileContentAccessed",
                "item content prefetched" => "GoogleDrive_FileContentPrefetched",
                "item search performed" => "GoogleDrive_SearchPerformed",
                "delete" => "FileDeleted",
                "trash" => "FileTrashed",
                "create" => "FileCreated",
                "upload" => "FileUploaded",
                "edit" => "FileModified",
                "view" => "GoogleDrive_FileAccessed",
                "rename" => "FileRenamed",
                "move" => "GoogleDrive_FileMoved",
                "copy" => "GoogleDrive_FileCopied",
                "source copy" => "GoogleDrive_SourceCopied",
                "comment created" => "GoogleDrive_CommentCreated",
                "comment edited" => "GoogleDrive_CommentEdited",
                "comment deleted" => "GoogleDrive_CommentDeleted",
                "comment resolved" => "GoogleDrive_CommentResolved",
                "suggestion created" => "GoogleDrive_SuggestionCreated",
                "suggestion accepted" => "GoogleDrive_SuggestionAccepted",
                "suggestion rejected" => "GoogleDrive_SuggestionRejected",
                "suggestion deleted" => "GoogleDrive_SuggestionDeleted",
                "access requested" => "GoogleDrive_AccessRequested",
                "access request denied" => "GoogleDrive_AccessRequestDenied",
                "access request expired" => "GoogleDrive_AccessRequestExpired",
                _ => "GoogleDrive_" + SafeOperationToken(text)
            };
        }

        if (family.Equals("Gmail", StringComparison.OrdinalIgnoreCase))
        {
            return key switch
            {
                "send" => "GoogleGmail_MessageSent",
                "receive" => "GoogleGmail_MessageReceived",
                "view" => "MailItemsAccessed",
                "open" => "GoogleGmail_MessageOpened",
                "message content accessed" => "GoogleGmail_MessageContentAccessed",
                "draft" => "GoogleGmail_DraftObserved",
                "delete" => "GoogleGmail_MessageDeleted",
                "move to trash" => "GoogleGmail_MessageMovedToTrash",
                "move out of trash" => "GoogleGmail_MessageMovedOutOfTrash",
                "move to inbox" => "GoogleGmail_MessageMovedToInbox",
                "archive" => "GoogleGmail_MessageArchived",
                "reply" => "GoogleGmail_MessageReplied",
                "mark unread" => "GoogleGmail_MessageMarkedUnread",
                "attachment preview" => "GoogleGmail_AttachmentPreviewed",
                "attachment download" => "GoogleGmail_AttachmentDownloaded",
                "attachment link click" => "GoogleGmail_AttachmentLinkClicked",
                "attachment save to drive" => "GoogleGmail_AttachmentSavedToDrive",
                "link click" => "GoogleGmail_LinkClicked",
                "delegate grant" => "GoogleGmail_DelegationChanged",
                "fetch" => "GoogleGmail_MessagesFetched",
                "forward" => "GoogleGmail_ForwardingRuleTriggered",
                _ => "GoogleGmail_" + SafeOperationToken(text)
            };
        }

        if (family.Equals("OAuth", StringComparison.OrdinalIgnoreCase))
        {
            return key switch
            {
                "api call" => "GoogleOAuth_API_call",
                "authorize" => "GoogleOAuth_Authorize",
                "revoke" => "GoogleOAuth_Revoke",
                _ => "GoogleOAuth_" + SafeOperationToken(text)
            };
        }

        if (family.Equals("Takeout", StringComparison.OrdinalIgnoreCase))
        {
            return key switch
            {
                "user initiated a takeout" => "GoogleTakeout_UserInitiated",
                "user completed a takeout" => "GoogleTakeout_UserCompleted",
                "user downloaded a takeout" => "GoogleTakeout_UserDownloaded",
                _ => "GoogleTakeout_" + SafeOperationToken(text)
            };
        }

        if (family.Equals("Calendar", StringComparison.OrdinalIgnoreCase)) return "GoogleCalendar_" + SafeOperationToken(text);
        if (family.Equals("Chat", StringComparison.OrdinalIgnoreCase)) return "GoogleChat_" + SafeOperationToken(text);
        if (family.Equals("Meet", StringComparison.OrdinalIgnoreCase)) return "GoogleMeet_" + SafeOperationToken(text);
        if (family.Equals("Chrome", StringComparison.OrdinalIgnoreCase)) return "GoogleChrome_" + SafeOperationToken(text);
        if (family.Equals("Chrome Sync", StringComparison.OrdinalIgnoreCase)) return "GoogleChromeSync_" + SafeOperationToken(text);
        if (family.Equals("Access Evaluation", StringComparison.OrdinalIgnoreCase)) return "GoogleAccessEvaluation_" + SafeOperationToken(text);
        if (family.Equals("Gemini for Workspace", StringComparison.OrdinalIgnoreCase)) return "GoogleGemini_" + SafeOperationToken(text);
        if (family.Equals("User", StringComparison.OrdinalIgnoreCase)) return "GoogleUser_" + SafeOperationToken(text);
        if (family.Equals("Device", StringComparison.OrdinalIgnoreCase)) return "GoogleDevice_" + SafeOperationToken(text);
        if (family.Equals("Vault", StringComparison.OrdinalIgnoreCase)) return "GoogleVault_" + SafeOperationToken(text);
        if (family.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "GoogleAdmin_" + SafeOperationToken(text);
        if (family.Equals("Groups", StringComparison.OrdinalIgnoreCase)) return "GoogleGroups_" + SafeOperationToken(text);
        if (family.Equals("Groups Enterprise", StringComparison.OrdinalIgnoreCase)) return "GoogleGroupsEnterprise_" + SafeOperationToken(text);
        if (family.Equals("Tasks", StringComparison.OrdinalIgnoreCase)) return "GoogleTasks_" + SafeOperationToken(text);
        if (family.Equals("Workspace Studio", StringComparison.OrdinalIgnoreCase)) return "GoogleWorkspaceStudio_" + SafeOperationToken(text);

        return string.IsNullOrWhiteSpace(text) ? "GoogleAudit_Event" : "GoogleAudit_" + SafeOperationToken(text);
    }

    public static string OperationKey(string value)
    {
        var cleaned = Clean(value).Replace('_', ' ').Replace('-', ' ');
        return Regex.Replace(cleaned.ToLowerInvariant(), @"\s+", " ").Trim();
    }

    public static string SafeOperationToken(string value)
    {
        value = Clean(value);
        if (string.IsNullOrWhiteSpace(value)) return "Event";
        var token = Regex.Replace(value, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (token.Length > 80) token = token.Substring(0, 80);
        return string.IsNullOrWhiteSpace(token) ? "Event" : token;
    }

    public static string BuildReadableTarget(string family, IDictionary<string, string> row, IEnumerable<string>? familyFields = null)
    {
        var readable = FirstNonBlank(
            Get(row, "Title"),
            Get(row, "Subject"),
            Get(row, "Event title"),
            Get(row, "New task title"),
            Get(row, "Task title"),
            Get(row, "Room name"),
            Get(row, "Attachment name"),
            Get(row, "Target attachment name"),
            Get(row, "Filename"),
            Get(row, "FileName"),
            Get(row, "Content name"),
            Get(row, "Resource Name"),
            Get(row, "App name"),
            Get(row, "Application Name"),
            Get(row, "Device name"),
            Get(row, "Group email"),
            Get(row, "Flow name"));
        if (!string.IsNullOrWhiteSpace(readable)) return readable;

        if (familyFields != null)
        {
            var fromFamily = Get(row, familyFields.ToArray());
            if (!string.IsNullOrWhiteSpace(fromFamily)) return fromFamily;
        }

        return FirstNonBlank(
            Get(row, "URL"),
            Get(row, "Resource Url"),
            Get(row, "Attachment URL"),
            Get(row, "Target link url"),
            Get(row, "Target"),
            Get(row, "Document ID"),
            Get(row, "Message ID"),
            Get(row, "Event ID"),
            Get(row, "Device ID"),
            Get(row, "OAuth Client ID"),
            Get(row, "Scope"));
    }

    public static string StableObjectId(IDictionary<string, string> row)
    {
        return FirstNonBlank(
            Get(row, "Document ID"),
            Get(row, "Message ID"),
            Get(row, "Event ID"),
            Get(row, "Task ID"),
            Get(row, "Device ID"),
            Get(row, "Browser ID"),
            Get(row, "Takeout job ID"),
            Get(row, "App ID"),
            Get(row, "OAuth Client ID"),
            Get(row, "Conference ID"),
            Get(row, "Meeting code"),
            Get(row, "Room ID"),
            Get(row, "Matter ID"),
            Get(row, "Flow ID"),
            Get(row, "Run ID"),
            Get(row, "Target"),
            Get(row, "URL"));
    }

    public static void PromoteWorkspaceAuditFields(NormalizedEvent ev, string family, IDictionary<string, string> row, string operationRaw, string normalizedOperation, string displayTarget, string stableObjectId)
    {
        AddGoogleField(ev, "GoogleDisplayTarget", displayTarget);
        AddGoogleField(ev, "GoogleTarget", displayTarget);
        AddGoogleField(ev, "GoogleStableObjectId", stableObjectId);
        AddGoogleField(ev, "GoogleOperationExact", FirstNonBlank(Get(row, "Event"), Get(row, "OAuth event"), Get(row, "Action"), operationRaw));
        AddGoogleField(ev, "GoogleOperationNormalized", normalizedOperation);
        AddGoogleField(ev, "GoogleActivityParameters", FirstNonBlank(Get(row, "Event"), Get(row, "OAuth event"), Get(row, "Action"), operationRaw));

        var oldVis = Get(row, "Prior visibility", "Old publish visibility value", "Old value");
        var newVis = Get(row, "Visibility", "New publish visibility value", "New value");
        if (!string.IsNullOrWhiteSpace(oldVis) || !string.IsNullOrWhiteSpace(newVis))
            ev.AdditionalFields["GoogleVisibilityChange"] = oldVis + " -> " + newVis;

        AddIfPresent(ev, row, "GoogleDocumentType", "Document type");
        AddIfPresent(ev, row, "GoogleDrive_DocumentId", "Document ID");
        AddIfPresent(ev, row, "GoogleDrive_Title", "Title");
        AddIfPresent(ev, row, "GoogleDrive_Owner", "Owner");
        AddIfPresent(ev, row, "GoogleDrive_Recipients", "Recipients");
        AddIfPresent(ev, row, "GoogleGmail_MessageId", "Message ID");
        AddIfPresent(ev, row, "GoogleGmail_Subject", "Subject");
        AddIfPresent(ev, row, "GoogleGmail_From", "From (Header address)", "From (Envelope)");
        AddIfPresent(ev, row, "GoogleGmail_To", "To (Envelope)");
        AddIfPresent(ev, row, "GoogleGmail_AttachmentName", "Attachment name", "Target attachment name");
        AddIfPresent(ev, row, "GoogleOAuth_AppName", "App name");
        AddIfPresent(ev, row, "GoogleOAuth_AppId", "App ID", "OAuth Client ID");
        AddIfPresent(ev, row, "GoogleOAuth_Scope", "Scope");
        AddIfPresent(ev, row, "GoogleOAuth_ApiName", "API name", "Product");
        AddIfPresent(ev, row, "GoogleTakeout_ProductsRequested", "Products requested");
        AddIfPresent(ev, row, "GoogleTakeout_Destination", "Takeout destination");
        AddIfPresent(ev, row, "GoogleTakeout_Status", "Takeout status");
        AddIfPresent(ev, row, "GoogleGemini_AppName", "App name");
        AddIfPresent(ev, row, "GoogleGemini_FeatureSource", "Feature source");
        AddIfPresent(ev, row, "GoogleGemini_Action", "Action");
        AddIfPresent(ev, row, "GoogleDevice_Type", "Device type");
        AddIfPresent(ev, row, "GoogleDevice_Model", "Device model");
        AddIfPresent(ev, row, "GoogleUserAgent", "User agent", "User Agent", "User Agent String");
        AddIfPresent(ev, row, "GoogleNetworkInfo", "Network info");
        AddIfPresent(ev, row, "GoogleResultStatus", "Event status", "Event result", "Status", "Takeout status");

        if (!string.IsNullOrWhiteSpace(displayTarget) && (displayTarget.StartsWith("http", StringComparison.OrdinalIgnoreCase) || ForensicText.IsLikelyPath(displayTarget)))
            AddGoogleTargetFields(ev, displayTarget, "GoogleWorkspaceAuditTarget");
    }

    public static void AddIfPresent(NormalizedEvent ev, IDictionary<string, string> row, string promotedName, params string[] sourceNames)
    {
        var value = Get(row, sourceNames);
        if (!string.IsNullOrWhiteSpace(value)) ev.AdditionalFields[promotedName] = value;
    }


    public static void AddGoogleCoreFields(
        NormalizedEvent ev,
        string recordType,
        string family,
        string actor,
        string clientIp,
        string userAgent,
        string operationRaw,
        string normalizedOperation,
        string target,
        string stableObjectId,
        string timestampText,
        string sourceEntry,
        string sourceContainer,
        int rowNumber)
    {
        AddGoogleField(ev, "GoogleRecordType", recordType);
        AddGoogleField(ev, "GoogleSourceFamily", family);
        AddGoogleField(ev, "GoogleActor", actor);
        AddGoogleField(ev, "GoogleUserId", actor);
        AddGoogleField(ev, "GoogleClientIp", clientIp);
        AddGoogleField(ev, "GoogleUserAgent", userAgent);
        AddGoogleField(ev, "GoogleOperationRaw", operationRaw);
        AddGoogleField(ev, "GoogleOperationNormalized", normalizedOperation);
        AddGoogleField(ev, "GoogleTarget", target);
        AddGoogleField(ev, "GoogleDisplayTarget", target);
        AddGoogleField(ev, "GoogleStableObjectId", stableObjectId);
        AddGoogleField(ev, "GoogleCreationDateText", timestampText);
        AddGoogleField(ev, "GoogleSourceEntry", sourceEntry);
        AddGoogleField(ev, "GoogleSourceContainer", sourceContainer);
        if (rowNumber > 0)
            AddGoogleField(ev, "GoogleSourceRowNumber", rowNumber.ToString(CultureInfo.InvariantCulture));
        ev.AdditionalFields["GoogleMasterExportSchema"] = "GoogleCloudSeparatedV1";
    }

    public static void AddGoogleField(NormalizedEvent ev, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            ev.AdditionalFields[key] = Clean(value);
    }

    public static void AddGoogleTargetFields(NormalizedEvent ev, string? target, string source)
    {
        target = ForensicText.TrimBinaryPathTail(Clean(target));
        if (string.IsNullOrWhiteSpace(target))
            return;

        ev.ObjectPath = target;
        ev.AdditionalFields["GoogleTarget"] = target;
        ev.AdditionalFields["GoogleDisplayTarget"] = target;
        ev.AdditionalFields["GoogleTargetSource"] = source;
        ev.AdditionalFields["GoogleTargetFileName"] = ParserSupport.SafeFileName(target);
        ev.AdditionalFields["GoogleTargetFileExtension"] = ParserSupport.SafeExtension(target);
        ev.AdditionalFields["GoogleTargetType"] = ParserSupport.ClassifyPathOrUrl(target);
        var driveType = ParserSupport.DetermineDriveType(target);
        if (!string.IsNullOrWhiteSpace(driveType))
            ev.AdditionalFields["GoogleTargetDriveType"] = driveType;
    }

    public static void AddPrefixedRawFields(NormalizedEvent ev, string prefix, IDictionary<string, string> row)
    {
        foreach (var kvp in row)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                continue;

            var rawKey = prefix + "_" + SafeRawFieldToken(kvp.Key);
            var candidate = rawKey;
            var suffix = 2;
            while (ev.AdditionalFields.ContainsKey(candidate))
            {
                candidate = rawKey + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
            ev.AdditionalFields[candidate] = kvp.Value ?? string.Empty;
        }
    }

    public static string SafeRawFieldToken(string value)
    {
        value = Clean(value);
        if (string.IsNullOrWhiteSpace(value)) return "Field";
        var token = Regex.Replace(value, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (token.Length > 120) token = token.Substring(0, 120);
        return string.IsNullOrWhiteSpace(token) ? "Field" : token;
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
        if (p.Contains("google chat/") || p.EndsWith("/messages.json", StringComparison.OrdinalIgnoreCase)) return "Google Chat";
        if (p.Contains("google meet/conferencehistory") || p.Contains("conference_history_records.csv")) return "Meet Conference History";
        if (p.Contains("calendar/") || p.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) return "Calendar";
        if (p.Contains("notebooklm/")) return "NotebookLM";
        if (p.Contains("my activity/gemini") || p.Contains("gemini/") || p.Contains("gemini apps")) return "Gemini";
        if (p.Contains("my activity/drive")) return "Drive My Activity";
        if (p.Contains("my activity/takeout")) return "Takeout My Activity";
        if (p.Contains("my activity/")) return "My Activity";
        if (p.Contains("youtube") || p.Contains("youtube music")) return "YouTube and YouTube Music";
        if (p.Contains("devices - a list of devices")) return "Devices";
        if (p.Contains("activities - a list of google services")) return "Activities";
        if (p.Contains("location history")) return "Location History";
        if (p.Contains("chrome")) return "Chrome";
        if (p.Contains("drive")) return "Drive";
        return "Generic Product";
    }
}
