using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VestigantTriage;

public class RecycleBinParser : IArtifactParser
{
    public string ParserName => "Windows Recycle Bin ($I Files)";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName((filePath ?? string.Empty).Replace('/', '\\')) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Match actual Recycle Bin $I metadata files only. Earlier versions used
        // broad substring matching, which incorrectly matched Office owner files such
        // as "~$ilding Blocks.dotx" and caused source-coverage/parser-coverage
        // conflicts. Staged deleted artifacts may have prefixes before the
        // original deleted name, so also permit delimiter-prefixed $I names.
        if (name.StartsWith("$I", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("[DELETED] $I", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("_[DELETED] $I", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("_Artifact_[DELETED] $I", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("_$I", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var sid = ParserSupport.ExtractSid(filePath);
        var ev = new NormalizedEvent
        {
            DataSource = "RecycleBin",
            Operation = "FileDeleted",
            ObjectPath = string.Empty,
            UserId = string.IsNullOrWhiteSpace(sid) ? ParserSupport.ExtractUserFromPath(filePath, "UnknownUser") : sid,
            TimestampUtc = DateTime.MinValue
        };

        ev.AdditionalFields["EventCategory"] = "Deletion";
        ev.AdditionalFields["ArtifactType"] = "RecycleBinIFile";
        ev.AdditionalFields["RecycleIFile"] = Path.GetFileName(filePath);
        ev.AdditionalFields["RecycleIFilePath"] = filePath;
        ev.AdditionalFields["RecycleSid"] = sid;
        ev.AdditionalFields["RecycleRFileCandidate"] = DeriveRFileCandidate(filePath);
        ParserSupport.AddParseQuality(ev, ParserName, "Low", "Recycle Bin metadata header pending validation.");
        ParserSupport.SetEventTime(ev, null, "RecycleBinDeletionTimeNotDecoded", "Unknown", false, "Recycle Bin source/staged file timestamps are not used as deletion time.");

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 24)
            {
                ev.AdditionalFields["ParseError"] = "Recycle Bin $I file too small.";
                return new[] { ev };
            }

            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            long version = br.ReadInt64();
            long fileSize = br.ReadInt64();
            long delTime = br.ReadInt64();
            if (version != 1 && version != 2)
            {
                ev.AdditionalFields["ParseError"] = $"Unsupported Recycle Bin $I version: {version}";
                return new[] { ev };
            }

            ev.AdditionalFields["RecycleBinVersion"] = version.ToString();
            ev.AdditionalFields["FileSizeBytes"] = fileSize.ToString();
            var delDate = ParserSupport.FromFileTime(delTime);
            if (delDate.HasValue)
            {
                ParserSupport.SetEventTime(ev, delDate.Value, "RecycleBinDeletionTime", "High", true);
                ev.AdditionalFields["DeletedUtc"] = delDate.Value.ToString("O");
            }

            string originalPath;
            if (version == 1)
            {
                originalPath = Encoding.Unicode.GetString(br.ReadBytes(Math.Min(520, (int)(ms.Length - ms.Position)))).TrimEnd('\0');
            }
            else
            {
                if (ms.Position + 4 > ms.Length)
                    originalPath = string.Empty;
                else
                {
                    int charCount = br.ReadInt32();
                    int byteCount = Math.Max(0, Math.Min(charCount * 2, (int)(ms.Length - ms.Position)));
                    originalPath = Encoding.Unicode.GetString(br.ReadBytes(byteCount)).TrimEnd('\0');
                }
            }

            originalPath = ForensicText.TrimBinaryPathTail(originalPath);
            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                ParserSupport.AddTargetFields(ev, originalPath, "RecycleBinOriginalPath");
                ev.AdditionalFields["OriginalPath"] = originalPath;
                ev.AdditionalFields["OriginalFileName"] = ParserSupport.SafeFileName(originalPath);
                ParserSupport.AddParseQuality(ev, ParserName, "High", "$I header, delete timestamp, file size, and original path decoded.");
            }
            else
            {
                ev.AdditionalFields["ParseWarning"] = "Original deleted path was not decoded from $I record.";
                ParserSupport.AddParseQuality(ev, ParserName, "Medium", "$I header decoded but original path missing.");
            }
        }
        catch (Exception ex)
        {
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Parser exception; partial metadata only.");
        }

        return new[] { ev };
    }

    private static string DeriveRFileCandidate(string iFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(iFilePath) ?? string.Empty;
            var name = Path.GetFileName(iFilePath);
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var rName = name.Replace("$I", "$R", StringComparison.OrdinalIgnoreCase);
            return Path.Combine(dir, rName);
        }
        catch { return string.Empty; }
    }
}
