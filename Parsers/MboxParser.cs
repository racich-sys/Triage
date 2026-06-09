using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VestigantTriage;

public class MboxParser : IArtifactParser
{
    public string ParserName => "MBOX Email Archive";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath ?? string.Empty);
        return name.EndsWith(".mbox", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("All mail Including Spam and Trash", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        using var reader = new StreamReader(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastHeader = null;
        var inHeaders = false;
        var messageCount = 0;

        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("From ", StringComparison.Ordinal))
            {
                if (headers.Count > 0)
                {
                    messageCount++;
                    yield return BuildMessageEvent(headers, filePath, messageCount);
                    headers.Clear();
                }
                inHeaders = true;
                lastHeader = null;
                continue;
            }

            if (!inHeaders)
                continue;

            if (string.IsNullOrWhiteSpace(line))
            {
                inHeaders = false;
                lastHeader = null;
                continue;
            }

            if ((line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)) && !string.IsNullOrWhiteSpace(lastHeader) && headers.TryGetValue(lastHeader, out var existing))
            {
                headers[lastHeader] = existing + " " + line.Trim();
                continue;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            if (IsHeaderOfInterest(key))
            {
                headers[key] = value;
                lastHeader = key;
            }
            else
            {
                lastHeader = null;
            }
        }

        if (headers.Count > 0)
        {
            messageCount++;
            yield return BuildMessageEvent(headers, filePath, messageCount);
        }

        log($"  ✓ Parsed {messageCount:N0} email message headers from {Path.GetFileName(filePath)}");
    }

    private static bool IsHeaderOfInterest(string key)
        => key.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("From", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("To", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("Cc", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("Bcc", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("Subject", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("Message-ID", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("Message-Id", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("X-Gmail-Labels", StringComparison.OrdinalIgnoreCase);

    private static NormalizedEvent BuildMessageEvent(Dictionary<string, string> headers, string filePath, int msgIndex)
    {
        var dateText = Get(headers, "Date");
        var timestamp = TimeUtil.ParseUtc(dateText);
        var subject = FirstNonBlank(Get(headers, "Subject"), "No Subject");
        var from = FirstNonBlank(Get(headers, "From"), "Unknown");
        var to = Get(headers, "To");
        var cc = Get(headers, "Cc");
        var bcc = Get(headers, "Bcc");
        var msgId = FirstNonBlank(Get(headers, "Message-ID"), Get(headers, "Message-Id"));
        var labels = Get(headers, "X-Gmail-Labels");

        var ev = new NormalizedEvent
        {
            DataSource = "Google Takeout - Mail",
            UserId = from,
            Operation = "GoogleTakeout_EmailMessageObserved",
            ObjectPath = FirstNonBlank(msgId, subject),
            TimestampUtc = timestamp ?? DateTime.MinValue,
            EventTimeBasis = "MboxDateHeader",
            EventTimeConfidence = timestamp.HasValue ? "High" : "MetadataOnly",
            IsBehavioralTimestamp = timestamp.HasValue,
            TimestampWarning = timestamp.HasValue ? string.Empty : "MBOX message header did not contain a parseable Date value."
        };

        ev.AdditionalFields["ParserName"] = "MBOX Email Archive";
        ev.AdditionalFields["ArtifactType"] = "MBOX Message Header";
        ev.AdditionalFields["EventCategory"] = "Email";
        ev.AdditionalFields["SourceFile"] = Path.GetFileName(filePath);
        ev.AdditionalFields["MessageIndex"] = msgIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ev.AdditionalFields["Recipients"] = string.Join("; ", new[] { to, cc, bcc }.Where(v => !string.IsNullOrWhiteSpace(v)));
        ev.AdditionalFields["EmailInfo_From"] = from;
        ev.AdditionalFields["EmailInfo_To_0"] = to;
        ev.AdditionalFields["EmailInfo_Cc_0"] = cc;
        ev.AdditionalFields["EmailInfo_Bcc_0"] = bcc;
        ev.AdditionalFields["EmailInfo_Subject"] = subject;
        ev.AdditionalFields["GoogleWorkload"] = "Google Mail";
        ev.AdditionalFields["GoogleCategory"] = "CloudCommunication";
        ev.AdditionalFields["GoogleGmail_Subject"] = subject;
        ev.AdditionalFields["GoogleGmail_From"] = from;
        ev.AdditionalFields["GoogleGmail_To"] = to;
        ev.AdditionalFields["GoogleGmail_Cc"] = cc;
        ev.AdditionalFields["GoogleGmail_Bcc"] = bcc;
        ev.AdditionalFields["GoogleGmail_MessageId"] = msgId;
        ev.AdditionalFields["GoogleGmail_Labels"] = labels;
        ev.AdditionalFields["GoogleRiskTransferPotential"] = "Yes";
        ev.AdditionalFields["GoogleRiskReason"] = "Gmail Takeout MBOX message header observed; review recipients, labels, and subject for transfer context.";
        return ev;
    }

    private static string Get(Dictionary<string, string> headers, string key) => headers.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
