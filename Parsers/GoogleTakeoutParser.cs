using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VestigantTriage;

public class GoogleTakeoutParser : IArtifactParser
{
    private const long MaxNestedZipBytes = 300L * 1024L * 1024L;

    public string ParserName => "Google Takeout";

    public bool CanParse(string filePath)
    {
        var lower = filePath.Replace('\\', '/').ToLowerInvariant();
        if (LooksLikeWorkspaceAuditContainer(lower))
            return false;
        if (GoogleSourceSupport.IsZip(filePath))
            return lower.Contains("takeout") || GoogleSourceSupport.ZipContains(filePath, IsTakeoutEntry);
        if (GoogleSourceSupport.IsCsv(filePath) || lower.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || lower.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || lower.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            return IsTakeoutEntry(filePath);
        if (GoogleSourceSupport.IsJson(filePath))
            return IsTakeoutEntry(filePath);
        return false;
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        if (GoogleSourceSupport.IsZip(filePath))
        {
            using var archive = ZipFile.OpenRead(filePath);
            foreach (var ev in ParseArchive(archive, filePath, log, 0)) yield return ev;
            yield break;
        }

        if (GoogleSourceSupport.IsCsv(filePath))
        {
            int row = 0;
            foreach (var raw in CsvUtil.ReadRows(filePath))
            {
                row++;
                yield return BuildCsvEvent(raw, filePath, Path.GetFileName(filePath), row);
            }
            yield break;
        }

        if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(filePath);
            foreach (var ev in BuildJsonEvents(filePath, Path.GetFileName(filePath), json)) yield return ev;
            yield break;
        }

        if (filePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ev in BuildCalendarIcsEvents(filePath, Path.GetFileName(filePath), File.ReadAllText(filePath))) yield return ev;
            yield break;
        }

        if (filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ev in BuildHtmlEvents(filePath, Path.GetFileName(filePath), File.ReadAllText(filePath))) yield return ev;
            yield break;
        }
    }

    private IEnumerable<NormalizedEvent> ParseArchive(ZipArchive archive, string container, Action<string> log, int depth)
    {
        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name) && IsTakeoutEntry(e.FullName)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var entryName = entry.FullName;
            if (entryName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                int row = 0;
                using var stream = entry.Open();
                foreach (var raw in GoogleSourceSupport.ReadCsvRows(stream))
                {
                    row++;
                    if (row % 10000 == 0) log($"  … parsed {row:N0} Google Takeout rows from {entryName}");
                    yield return BuildCsvEvent(raw, container, entryName, row);
                }
                continue;
            }

            if (entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                foreach (var ev in BuildJsonEvents(container, entryName, reader.ReadToEnd())) yield return ev;
                continue;
            }

            if (entryName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                foreach (var ev in BuildCalendarIcsEvents(container, entryName, reader.ReadToEnd())) yield return ev;
                continue;
            }

            if (entryName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || entryName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                foreach (var ev in BuildHtmlEvents(container, entryName, reader.ReadToEnd())) yield return ev;
                continue;
            }

            if (entryName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && depth < 2 && entry.Length > 0 && entry.Length <= MaxNestedZipBytes)
            {
                yield return BuildEntryObservedEvent(container, entryName, entry.Length, entry.LastWriteTime.UtcDateTime, "GoogleTakeout_NestedArchiveObserved");
                using var source = entry.Open();
                using var ms = new MemoryStream();
                source.CopyTo(ms);
                ms.Position = 0;
                using var nested = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
                foreach (var ev in ParseArchive(nested, container + "!" + entryName, log, depth + 1)) yield return ev;
                continue;
            }

            yield return BuildEntryObservedEvent(container, entryName, entry.Length, entry.LastWriteTime.UtcDateTime);
        }
    }

    private static bool IsTakeoutEntry(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (LooksLikeWorkspaceAuditContainer(p)) return false;
        return p.Contains("takeout/") ||
               p.Contains("google takeout") ||
               p.Contains("activities - a list of google services") ||
               p.Contains("devices - a list of devices") ||
               p.Contains("mail/user settings/") ||
               p.Contains("youtube and youtube music") ||
               p.Contains("my activity/") ||
               p.Contains("myactivity.html") ||
               p.Contains("google chat/") ||
               p.EndsWith("/messages.json", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("google meet/conferencehistory/") ||
               p.Contains("conference_history_records.csv") ||
               p.Contains("calendar/") ||
               p.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("notebooklm/") ||
               p.Contains("gemini/") ||
               p.Contains("blocked addresses.json") ||
               p.Contains("vacation responder.json") ||
               p.Contains("filters.json");
    }

    private static bool LooksLikeWorkspaceAuditContainer(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        return p.Contains("audit and investigation") || p.Contains("google audit");
    }

    private static NormalizedEvent BuildCsvEvent(Dictionary<string, string> row, string container, string sourceEntry, int rowNumber)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        if (family == "Meet Conference History") return BuildMeetConferenceCsvEvent(row, container, sourceEntry, rowNumber);

        var timestampText = GoogleSourceSupport.Get(row, "Activity Timestamp", "Date", "Timestamp", "Time", "Last status report time", "Start Time");
        var timestamp = ParseGoogleTime(timestampText);
        var product = GoogleSourceSupport.Get(row, "Product Name", "Sub-Product Name", "Device Type", "Marketing Name", "Activity Type", "OS", "Brand Name", "Item Type");

        var target = product;
        var op = "GoogleTakeout_ProductRowObserved";
        var category = "CloudAccountExport";

        if (family == "Activities")
        {
            var activityDetail = GoogleSourceSupport.FirstNonBlank(
                GoogleSourceSupport.Get(row, "Title"),
                GoogleSourceSupport.Get(row, "Title Url", "Title URL"),
                GoogleSourceSupport.Get(row, "URL", "Url"),
                GoogleSourceSupport.Get(row, "Details"),
                GoogleSourceSupport.Get(row, "Query", "Search query"));

            target = !string.IsNullOrWhiteSpace(activityDetail) ? activityDetail : product;

            if (product.Contains("Search", StringComparison.OrdinalIgnoreCase))
            {
                op = "Google_Search_Query";
                category = "WebHistory";
            }
            else if (product.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            {
                op = "Chrome_Sync_Web_Visit";
                category = "WebHistory";
            }
            else if (product.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            {
                op = "GoogleYouTube_ActivityObserved";
                category = "MediaHistory";
            }
            else if (product.Contains("Gmail", StringComparison.OrdinalIgnoreCase))
            {
                op = "GoogleTakeout_GmailAccessObserved";
                category = "CloudAccountAccess";
            }
            else
            {
                op = "GoogleTakeout_ActivityObserved";
            }
        }
        else if (family == "Devices")
        {
            target = GoogleSourceSupport.FirstNonBlank(GoogleSourceSupport.Get(row, "User Given Name"), GoogleSourceSupport.Get(row, "Marketing Name"), GoogleSourceSupport.Get(row, "Device Model"), product);
            op = "GoogleTakeout_DeviceObserved";
            category = "DeviceLogon";
        }

        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - " + family,
            UserId = GoogleSourceSupport.FirstNonBlank(GoogleSourceSupport.Get(row, "Gaia ID", "User", "User email", "Email"), "Unknown"),
            Operation = op,
            ObjectPath = target,
            ClientIp = GoogleSourceSupport.Get(row, "IP Address", "Client IP", "Remote IP"),
            TimestampUtc = timestamp ?? DateTime.MinValue,
            EventTimeBasis = family == "Activities" ? "GoogleTakeoutActivityTimestamp" : "GoogleTakeoutInventoryMetadata",
            EventTimeConfidence = timestamp.HasValue && family == "Activities" ? "High" : "MetadataOnly",
            IsBehavioralTimestamp = timestamp.HasValue && family == "Activities",
            TimestampWarning = timestamp.HasValue && family == "Activities" ? string.Empty : "Google Takeout inventory/config row is not treated as endpoint behavior without correlation."
        };

        AddTakeoutBase(ev, "Google Takeout", family, category, container, sourceEntry, rowNumber, timestampText, target, row);
        ev.AdditionalFields["GoogleTakeoutProduct"] = product;
        GoogleSourceSupport.AddGoogleField(ev, "GoogleTakeoutActivityType", GoogleSourceSupport.Get(row, "Activity Type"));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleTakeoutSubProduct", GoogleSourceSupport.Get(row, "Sub-Product Name"));
        if (!string.IsNullOrWhiteSpace(target) && (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || ForensicText.IsLikelyPath(target)))
            GoogleSourceSupport.AddGoogleTargetFields(ev, target, "GoogleTakeoutActivity");

        GoogleSourceSupport.AddGoogleRiskFields(ev, "Takeout", row);
        GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleTakeoutRaw", row);
        return ev;
    }

    private static NormalizedEvent BuildMeetConferenceCsvEvent(Dictionary<string, string> row, string container, string sourceEntry, int rowNumber)
    {
        var timestampText = GoogleSourceSupport.Get(row, "Start Time");
        var timestamp = ParseGoogleTime(timestampText);
        var meetingCode = GoogleSourceSupport.Get(row, "Meeting Code");
        var conferenceId = GoogleSourceSupport.Get(row, "Conference Id", "Conference ID");
        var target = GoogleSourceSupport.FirstNonBlank(meetingCode, conferenceId, GoogleSourceSupport.Get(row, "Event Id"));

        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - Meet Conference History",
            UserId = GoogleSourceSupport.FirstNonBlank(GoogleSourceSupport.Get(row, "Owner Gaia Id"), "Unknown"),
            Operation = "GoogleTakeout_MeetConferenceParticipated",
            ObjectPath = target,
            TimestampUtc = timestamp ?? DateTime.MinValue,
            EventTimeBasis = "GoogleMeetConferenceStartTime",
            EventTimeConfidence = timestamp.HasValue ? "High" : "MetadataOnly",
            IsBehavioralTimestamp = timestamp.HasValue,
            TimestampWarning = timestamp.HasValue ? string.Empty : "Google Meet conference history row has no parsed start time."
        };

        AddTakeoutBase(ev, "Google Meet", "Meet Conference History", "CloudCommunication", container, sourceEntry, rowNumber, timestampText, target, row);
        GoogleSourceSupport.AddGoogleField(ev, "GoogleMeetMeetingCode", meetingCode);
        GoogleSourceSupport.AddGoogleField(ev, "GoogleMeetConferenceId", conferenceId);
        GoogleSourceSupport.AddGoogleField(ev, "GoogleMeetStartTime", timestampText);
        GoogleSourceSupport.AddGoogleField(ev, "GoogleMeetEndTime", GoogleSourceSupport.Get(row, "End Time"));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleMeetDuration", GoogleSourceSupport.Get(row, "Duration"));
        GoogleSourceSupport.AddGoogleField(ev, "GoogleMeetParticipationState", GoogleSourceSupport.Get(row, "Participation State"));
        GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleTakeoutRaw", row);
        GoogleSourceSupport.AddGoogleRiskFields(ev, "Meet", row);
        return ev;
    }

    private static IEnumerable<NormalizedEvent> BuildJsonEvents(string container, string sourceEntry, string rawJson)
    {
        if (sourceEntry.Replace('\\', '/').EndsWith("/messages.json", StringComparison.OrdinalIgnoreCase) || sourceEntry.Contains("Google Chat", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ev in BuildChatMessageEvents(container, sourceEntry, rawJson)) yield return ev;
            yield break;
        }

        yield return BuildJsonObservedEvent(container, sourceEntry, rawJson);
    }

    private static IEnumerable<NormalizedEvent> BuildChatMessageEvents(string container, string sourceEntry, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            yield return BuildJsonObservedEvent(container, sourceEntry, rawJson);
            yield break;
        }

        var row = 0;
        foreach (var msg in messages.EnumerateArray())
        {
            row++;
            var createdText = GetJsonString(msg, "created_date");
            var timestamp = ParseGoogleTime(createdText);
            var text = GetJsonString(msg, "text");
            var messageId = GetJsonString(msg, "message_id");
            var topicId = GetJsonString(msg, "topic_id");
            var creatorName = "";
            var creatorType = "";
            if (msg.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object)
            {
                creatorName = GetJsonString(creator, "name");
                creatorType = GetJsonString(creator, "user_type");
            }

            var target = GoogleSourceSupport.FirstNonBlank(FirstUrlFromChatMessage(msg), Truncate(text, 180), messageId, sourceEntry);
            var ev = new NormalizedEvent
            {
                DataSource = "Google Takeout - Google Chat",
                UserId = GoogleSourceSupport.FirstNonBlank(creatorName, "Unknown"),
                Operation = "GoogleTakeout_ChatMessageObserved",
                ObjectPath = target,
                TimestampUtc = timestamp ?? DateTime.MinValue,
                EventTimeBasis = "GoogleChatMessageCreatedDate",
                EventTimeConfidence = timestamp.HasValue ? "High" : "MetadataOnly",
                IsBehavioralTimestamp = timestamp.HasValue,
                TimestampWarning = timestamp.HasValue ? string.Empty : "Google Chat message row did not contain a parseable created_date."
            };

            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["created_date"] = createdText,
                ["text"] = text,
                ["message_id"] = messageId,
                ["topic_id"] = topicId,
                ["creator_name"] = creatorName,
                ["creator_user_type"] = creatorType
            };

            AddTakeoutBase(ev, "Google Chat", "Google Chat", "CloudCommunication", container, sourceEntry, row, createdText, target, raw);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleChatMessageId", messageId);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleChatTopicId", topicId);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleChatCreatorName", creatorName);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleChatCreatorType", creatorType);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleChatMessageText", Truncate(text, 2000));
            GoogleSourceSupport.AddGoogleField(ev, "GoogleChatFirstUrl", FirstUrlFromChatMessage(msg));
            GoogleSourceSupport.AddGoogleRiskFields(ev, "Chat", raw);
            GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleTakeoutRaw", raw);
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> BuildHtmlEvents(string container, string sourceEntry, string html)
    {
        if (sourceEntry.Contains("MyActivity", StringComparison.OrdinalIgnoreCase) || sourceEntry.Contains("My Activity", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ev in BuildMyActivityHtmlEvents(container, sourceEntry, html)) yield return ev;
            yield break;
        }

        yield return BuildHtmlObservedEvent(container, sourceEntry, html);
    }

    private static IEnumerable<NormalizedEvent> BuildMyActivityHtmlEvents(string container, string sourceEntry, string html)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        var matches = Regex.Matches(html ?? string.Empty, @"<div[^>]*class=""[^""]*content-cell[^""]*""[^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (matches.Count == 0)
        {
            yield return BuildHtmlObservedEvent(container, sourceEntry, html);
            yield break;
        }

        var row = 0;
        foreach (Match match in matches)
        {
            var text = CleanHtml(match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;
            row++;
            var timestampText = ExtractActivityTimestampText(text);
            var timestamp = ParseGoogleTime(timestampText);
            var actionText = timestampText.Length > 0 ? text.Replace(timestampText, "", StringComparison.OrdinalIgnoreCase).Trim() : text;
            var target = Truncate(actionText, 240);
            var op = family.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_GeminiActivityObserved" :
                     family.Contains("Drive", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_DriveActivityObserved" :
                     family.Contains("Takeout", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_TakeoutActivityObserved" :
                     "GoogleTakeout_MyActivityObserved";
            var category = family.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "AIActivity" : "CloudAccountActivity";

            var ev = new NormalizedEvent
            {
                DataSource = "Google Takeout - My Activity - " + family,
                UserId = "Unknown",
                Operation = op,
                ObjectPath = target,
                TimestampUtc = timestamp ?? DateTime.MinValue,
                EventTimeBasis = "GoogleTakeoutMyActivityHtmlTimestamp",
                EventTimeConfidence = timestamp.HasValue ? "High" : "MetadataOnly",
                IsBehavioralTimestamp = timestamp.HasValue,
                TimestampWarning = timestamp.HasValue ? string.Empty : "Google My Activity HTML entry did not contain a parseable timestamp."
            };

            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ActivityText"] = actionText,
                ["TimestampText"] = timestampText,
                ["SourceEntry"] = sourceEntry
            };

            AddTakeoutBase(ev, "Google My Activity", family, category, container, sourceEntry, row, timestampText, target, raw);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleMyActivityText", Truncate(actionText, 4000));
            GoogleSourceSupport.AddGoogleField(ev, "GoogleMyActivityTimestampText", timestampText);
            GoogleSourceSupport.AddGoogleRiskFields(ev, family.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "Gemini for Workspace" : "Takeout", raw);
            GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleTakeoutRaw", raw);
            yield return ev;
        }
    }

    private static IEnumerable<NormalizedEvent> BuildCalendarIcsEvents(string container, string sourceEntry, string ics)
    {
        var lines = UnfoldIcsLines(ics).ToList();
        var row = 0;
        foreach (var block in ExtractIcsBlocks(lines, "VEVENT"))
        {
            row++;
            var summary = IcsValue(block, "SUMMARY");
            var uid = IcsValue(block, "UID");
            var startText = IcsValue(block, "DTSTART");
            var endText = IcsValue(block, "DTEND");
            var createdText = IcsValue(block, "CREATED");
            var modifiedText = IcsValue(block, "LAST-MODIFIED");
            var timestampText = GoogleSourceSupport.FirstNonBlank(startText, createdText, modifiedText);
            var timestamp = ParseIcsTime(timestampText);
            var target = GoogleSourceSupport.FirstNonBlank(summary, uid, sourceEntry);
            var ev = new NormalizedEvent
            {
                DataSource = "Google Takeout - Calendar",
                UserId = GoogleSourceSupport.FirstNonBlank(IcsValue(block, "ORGANIZER"), "Unknown"),
                Operation = "GoogleTakeout_CalendarEventObserved",
                ObjectPath = target,
                TimestampUtc = timestamp ?? DateTime.MinValue,
                EventTimeBasis = "GoogleCalendarIcsDtStart",
                EventTimeConfidence = timestamp.HasValue ? "High" : "MetadataOnly",
                IsBehavioralTimestamp = timestamp.HasValue,
                TimestampWarning = timestamp.HasValue ? string.Empty : "Google Calendar VEVENT did not contain a parseable DTSTART/CREATED/LAST-MODIFIED value."
            };

            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SUMMARY"] = summary,
                ["UID"] = uid,
                ["DTSTART"] = startText,
                ["DTEND"] = endText,
                ["CREATED"] = createdText,
                ["LAST-MODIFIED"] = modifiedText,
                ["ORGANIZER"] = IcsValue(block, "ORGANIZER"),
                ["LOCATION"] = IcsValue(block, "LOCATION")
            };

            AddTakeoutBase(ev, "Google Calendar", "Calendar", "CloudCalendar", container, sourceEntry, row, timestampText, target, raw);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleCalendarEventTitle", summary);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleCalendarEventUid", uid);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleCalendarStart", startText);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleCalendarEnd", endText);
            GoogleSourceSupport.AddGoogleField(ev, "GoogleCalendarLocation", IcsValue(block, "LOCATION"));
            GoogleSourceSupport.AddGoogleRiskFields(ev, "Calendar", raw);
            GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleTakeoutRaw", raw);
            yield return ev;
        }
    }

    private static NormalizedEvent BuildJsonObservedEvent(string container, string sourceEntry, string rawJson)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        var flat = JsonRepair.TryJsonLoads(rawJson ?? string.Empty);
        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - " + family,
            UserId = "Unknown",
            Operation = sourceEntry.Contains("Filters.json", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_MailFilterObserved" : sourceEntry.Contains("Blocked Addresses.json", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_BlockedAddressObserved" : sourceEntry.Contains("Vacation Responder.json", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_VacationResponderObserved" : "GoogleTakeout_ConfigObserved",
            ObjectPath = sourceEntry,
            TimestampUtc = DateTime.MinValue,
            EventTimeBasis = "GoogleTakeoutConfigMetadata",
            EventTimeConfidence = "MetadataOnly",
            IsBehavioralTimestamp = false,
            TimestampWarning = "Google Takeout config file observation is metadata-only unless correlated with other activity."
        };
        ev.AdditionalFields["ParserName"] = "Google Takeout";
        ev.AdditionalFields["ArtifactType"] = "Google Takeout Config";
        GoogleSourceSupport.AddGoogleCoreFields(ev, "GoogleTakeout", family, ev.UserId, string.Empty, string.Empty, ev.Operation, ev.Operation, sourceEntry, sourceEntry, string.Empty, sourceEntry, container, 0);
        ev.AdditionalFields["GoogleWorkload"] = "Google Takeout";
        ev.AdditionalFields["GoogleCategory"] = "CloudAccountExport";
        ev.AdditionalFields["GoogleTakeoutProductFamily"] = family;
        ev.AdditionalFields["GoogleTakeoutSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleTakeoutContainer"] = container;
        ev.AdditionalFields["GoogleRawSerializedRow"] = rawJson ?? string.Empty;
        foreach (var kvp in flat.Take(250))
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [kvp.Key] = kvp.Value };
            GoogleSourceSupport.AddPrefixedRawFields(ev, "GoogleTakeoutRaw", raw);
        }
        return ev;
    }

    private static NormalizedEvent BuildHtmlObservedEvent(string container, string sourceEntry, string html)
    {
        return BuildEntryObservedEvent(container, sourceEntry, Encoding.UTF8.GetByteCount(html ?? string.Empty), DateTime.MinValue, sourceEntry.Contains("NotebookLM", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_NotebookLmSourceObserved" : "GoogleTakeout_HtmlArtifactObserved");
    }

    private static NormalizedEvent BuildEntryObservedEvent(string container, string sourceEntry, long bytes, DateTime lastWriteUtc, string? operation = null)
    {
        var family = GoogleSourceSupport.FamilyFromTakeoutPath(sourceEntry);
        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - " + family,
            UserId = "Unknown",
            Operation = operation ?? (family.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ? "GoogleTakeout_YouTubeArtifactObserved" : "GoogleTakeout_ProductArtifactObserved"),
            ObjectPath = sourceEntry,
            TimestampUtc = lastWriteUtc,
            EventTimeBasis = "GoogleTakeoutArchiveEntryLastWrite",
            EventTimeConfidence = "MetadataOnly",
            IsBehavioralTimestamp = false,
            TimestampWarning = "Archive entry timestamp is not treated as behavioral account activity without correlation."
        };
        ev.AdditionalFields["ParserName"] = "Google Takeout";
        ev.AdditionalFields["ArtifactType"] = "Google Takeout Archive Entry";
        GoogleSourceSupport.AddGoogleCoreFields(ev, "GoogleTakeout", family, ev.UserId, string.Empty, string.Empty, ev.Operation, ev.Operation, sourceEntry, sourceEntry, string.Empty, sourceEntry, container, 0);
        ev.AdditionalFields["GoogleTakeoutProductFamily"] = family;
        ev.AdditionalFields["GoogleTakeoutSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleTakeoutContainer"] = container;
        ev.AdditionalFields["GoogleFileSizeBytes"] = bytes.ToString(CultureInfo.InvariantCulture);
        ev.AdditionalFields["GoogleFileName"] = Path.GetFileName(sourceEntry);
        return ev;
    }

    private static void AddTakeoutBase(NormalizedEvent ev, string workload, string family, string category, string container, string sourceEntry, int rowNumber, string timestampText, string target, IDictionary<string, string> raw)
    {
        ev.AdditionalFields["ParserName"] = "Google Takeout";
        ev.AdditionalFields["ArtifactType"] = "Google Takeout Export";
        GoogleSourceSupport.AddGoogleCoreFields(ev, "GoogleTakeout", family, ev.UserId, ev.ClientIp, GoogleSourceSupport.Get(raw, "User Agent String", "User Agent"), ev.Operation, ev.Operation, target, GoogleSourceSupport.FirstNonBlank(GoogleSourceSupport.Get(raw, "Message ID", "UID", "Item Id", "Event Id"), target), timestampText, sourceEntry, container, rowNumber);
        ev.AdditionalFields["GoogleWorkload"] = workload;
        ev.AdditionalFields["GoogleCategory"] = category;
        ev.AdditionalFields["GoogleEventCategory"] = category;
        ev.AdditionalFields["GoogleTakeoutProductFamily"] = family;
        ev.AdditionalFields["GoogleTakeoutSourceEntry"] = sourceEntry;
        ev.AdditionalFields["GoogleTakeoutContainer"] = container;
        ev.AdditionalFields["GoogleTakeoutRowNumber"] = rowNumber.ToString(CultureInfo.InvariantCulture);
        ev.AdditionalFields["GoogleRawSerializedRow"] = SafeSerialize(raw);
    }

    private static string SafeSerialize(IDictionary<string, string> row)
    {
        try { return JsonSerializer.Serialize(row); }
        catch { return string.Empty; }
    }

    private static string GetJsonString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? GoogleSourceSupport.Clean(value.GetString()) : GoogleSourceSupport.Clean(value.ToString());
    }

    private static string FirstUrlFromChatMessage(JsonElement msg)
    {
        if (!msg.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var ann in annotations.EnumerateArray())
        {
            if (!ann.TryGetProperty("url_metadata", out var meta) || meta.ValueKind != JsonValueKind.Object) continue;
            if (!meta.TryGetProperty("url", out var urlObj) || urlObj.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in urlObj.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return value;
            }
        }
        return string.Empty;
    }

    private static string CleanHtml(string html)
    {
        var text = Regex.Replace(html ?? string.Empty, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00A0', ' ').Replace('\u202F', ' ');
        return Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
    }

    private static string ExtractActivityTimestampText(string text)
    {
        var normalized = (text ?? string.Empty).Replace('\u00A0', ' ').Replace('\u202F', ' ');
        var match = Regex.Match(normalized, @"[A-Z][a-z]{2,9}\s+\d{1,2},\s+\d{4},\s+\d{1,2}:\d{2}:\d{2}\s*(?:AM|PM)\s*(?:UTC|GMT|IDT|EST|EDT|CST|CDT|MST|MDT|PST|PDT)?", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Trim() : string.Empty;
    }

    private static DateTime? ParseGoogleTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Replace('\u00A0', ' ').Replace('\u202F', ' ');
        text = Regex.Replace(text, @"\s+", " ");
        var mapped = text;
        mapped = Regex.Replace(mapped, @"\bUTC\b", "+00:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bGMT\b", "+00:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bIDT\b", "+03:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bEDT\b", "-04:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bEST\b", "-05:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bCDT\b", "-05:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bCST\b", "-06:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bMDT\b", "-06:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bMST\b", "-07:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bPDT\b", "-07:00", RegexOptions.IgnoreCase);
        mapped = Regex.Replace(mapped, @"\bPST\b", "-08:00", RegexOptions.IgnoreCase);

        var parsed = TimeUtil.ParseUtc(mapped) ?? TimeUtil.ParseUtc(text);
        if (parsed.HasValue) return parsed.Value;

        if (DateTimeOffset.TryParse(mapped, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.UtcDateTime;
        return null;
    }

    private static IEnumerable<string> UnfoldIcsLines(string ics)
    {
        var current = new StringBuilder();
        using var reader = new StringReader(ics ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if ((line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)) && current.Length > 0)
            {
                current.Append(line[1..]);
                continue;
            }
            if (current.Length > 0) yield return current.ToString();
            current.Clear();
            current.Append(line);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static IEnumerable<Dictionary<string, string>> ExtractIcsBlocks(IReadOnlyList<string> lines, string component)
    {
        Dictionary<string, string>? current = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:" + component, StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("END:" + component, StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) yield return current;
                current = null;
                continue;
            }
            if (current == null) continue;
            var sep = line.IndexOf(':');
            if (sep <= 0) continue;
            var key = line[..sep];
            var semi = key.IndexOf(';');
            if (semi >= 0) key = key[..semi];
            var value = line[(sep + 1)..].Replace("\\n", "\n").Replace("\\,", ",").Replace("\\;", ";");
            if (!current.ContainsKey(key)) current[key] = value;
        }
    }

    private static string IcsValue(IDictionary<string, string> block, string key)
        => block.TryGetValue(key, out var value) ? GoogleSourceSupport.Clean(value) : string.Empty;

    private static DateTime? ParseIcsTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var z))
            return DateTime.SpecifyKind(z, DateTimeKind.Utc);
        if (DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return local.ToUniversalTime();
        if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
            return date.ToUniversalTime();
        return ParseGoogleTime(text);
    }

    private static string Truncate(string value, int max)
    {
        value = GoogleSourceSupport.Clean(value);
        if (value.Length <= max) return value;
        return value[..max];
    }
}
