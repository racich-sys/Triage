using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VestigantTriage;

public class UsnJournalParser : IArtifactParser
{
    public string ParserName => "NTFS USN Journal ($J)";

    public bool CanParse(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.Equals("$J", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_$J", StringComparison.OrdinalIgnoreCase) || filePath.Contains("UsnJrnl", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var events = new List<NormalizedEvent>();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            while (fs.Position + 8 <= fs.Length)
            {
                long startPos = fs.Position;
                uint recordLength = br.ReadUInt32();
                if (recordLength == 0)
                {
                    fs.Position = Math.Min(fs.Length, startPos + 8);
                    continue;
                }
                if (recordLength < 60 || startPos + recordLength > fs.Length)
                {
                    fs.Position = startPos + 1;
                    continue;
                }
                ushort majorVersion = br.ReadUInt16();
                ushort minorVersion = br.ReadUInt16();
                try { ParseRecord(br, fs, startPos, recordLength, majorVersion, minorVersion, events); }
                catch (Exception ex) { log($"USN record parse warning at offset {startPos}: {ex.Message}"); }
                fs.Position = startPos + recordLength;
            }
        }
        catch (Exception ex)
        {
            log($"Failed to parse USN Journal {Path.GetFileName(filePath)}: {ex.Message}");
            events.Add(new NormalizedEvent
            {
                DataSource = "USN_Journal",
                Operation = "USN_ParseError",
                ObjectPath = filePath,
                TimestampUtc = DateTime.MinValue,
                AdditionalFields = { ["ParseError"] = ex.Message, ["EventCategory"] = "ParserError" }
            });
        }
        return events;
    }

    private static void ParseRecord(BinaryReader br, FileStream fs, long startPos, uint recordLength, ushort majorVersion, ushort minorVersion, List<NormalizedEvent> events)
    {
        ulong fileReference;
        ulong parentReference;
        long usn;
        long timestamp;
        uint reason;
        uint fileAttributes;
        ushort nameLen;
        ushort nameOff;
        uint sourceInfo = 0;
        uint securityId = 0;

        if (majorVersion == 2)
        {
            fs.Position = startPos + 8;
            fileReference = br.ReadUInt64();
            parentReference = br.ReadUInt64();
            usn = br.ReadInt64();
            timestamp = br.ReadInt64();
            reason = br.ReadUInt32();
            sourceInfo = br.ReadUInt32();
            securityId = br.ReadUInt32();
            fileAttributes = br.ReadUInt32();
            nameLen = br.ReadUInt16();
            nameOff = br.ReadUInt16();
        }
        else if (majorVersion == 3)
        {
            fs.Position = startPos + 8;
            var fileRefBytes = br.ReadBytes(16);
            var parentRefBytes = br.ReadBytes(16);
            fileReference = BitConverter.ToUInt64(fileRefBytes, 0);
            parentReference = BitConverter.ToUInt64(parentRefBytes, 0);
            usn = br.ReadInt64();
            timestamp = br.ReadInt64();
            reason = br.ReadUInt32();
            sourceInfo = br.ReadUInt32();
            securityId = br.ReadUInt32();
            fileAttributes = br.ReadUInt32();
            nameLen = br.ReadUInt16();
            nameOff = br.ReadUInt16();
        }
        else return;

        if (nameLen == 0 || nameOff == 0 || startPos + nameOff + nameLen > startPos + recordLength) return;
        fs.Position = startPos + nameOff;
        var fileName = ParserSupport.Clean(Encoding.Unicode.GetString(br.ReadBytes(nameLen)).TrimEnd('\0'));
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var eventTime = ParserSupport.FromFileTime(timestamp) ?? DateTime.MinValue;
        var flags = DecodeReasonFlags(reason).ToList();
        var operation = DecodePrimaryReason(flags);
        var ev = new NormalizedEvent
        {
            DataSource = "USN_Journal",
            Operation = operation,
            ObjectPath = fileName,
            TimestampUtc = eventTime,
            UserId = "LocalSystem"
        };

        ev.AdditionalFields["EventCategory"] = operation.Contains("Delete", StringComparison.OrdinalIgnoreCase) ? "Deletion" : operation.Contains("Rename", StringComparison.OrdinalIgnoreCase) ? "Rename" : "FileSystem";
        ev.AdditionalFields["ArtifactType"] = "USNJournalRecord";
        ev.AdditionalFields["FileName"] = fileName;
        ev.AdditionalFields["FileExtension"] = ParserSupport.SafeExtension(fileName);
        ev.AdditionalFields["USN"] = usn.ToString();
        ev.AdditionalFields["USNRecordMajorVersion"] = majorVersion.ToString();
        ev.AdditionalFields["USNRecordMinorVersion"] = minorVersion.ToString();
        ev.AdditionalFields["FileReferenceNumber"] = fileReference.ToString();
        ev.AdditionalFields["FileReferenceMftEntry"] = (fileReference & 0x0000FFFFFFFFFFFFUL).ToString();
        ev.AdditionalFields["FileReferenceSequence"] = (fileReference >> 48).ToString();
        ev.AdditionalFields["ParentFileReferenceNumber"] = parentReference.ToString();
        ev.AdditionalFields["ParentReferenceMftEntry"] = (parentReference & 0x0000FFFFFFFFFFFFUL).ToString();
        ev.AdditionalFields["ParentReferenceSequence"] = (parentReference >> 48).ToString();
        ev.AdditionalFields["ReasonHex"] = reason.ToString("X8");
        ev.AdditionalFields["ReasonFlags"] = string.Join(",", flags);
        ev.AdditionalFields["SourceInfoHex"] = sourceInfo.ToString("X8");
        ev.AdditionalFields["SecurityId"] = securityId.ToString();
        ev.AdditionalFields["FileAttributesHex"] = fileAttributes.ToString("X8");
        ev.AdditionalFields["FileAttributes"] = ParserSupport.DecodeFileAttributes(fileAttributes);
        ParserSupport.AddTargetFields(ev, fileName, "USNFileNameOnly");
        ParserSupport.AddParseQuality(ev, "NTFS USN Journal ($J)", "Medium", "USN record parsed; full path requires parent FRN/MFT correlation.");
        events.Add(ev);
    }

    private static string DecodePrimaryReason(List<string> flags)
    {
        if (flags.Contains("FILE_DELETE")) return "File_Delete";
        if (flags.Contains("FILE_CREATE")) return "File_Create";
        if (flags.Contains("RENAME_NEW_NAME")) return "File_Rename_New_Name";
        if (flags.Contains("RENAME_OLD_NAME")) return "File_Rename_Old_Name";
        if (flags.Contains("DATA_EXTEND")) return "File_Data_Extend";
        if (flags.Contains("DATA_OVERWRITE")) return "File_Data_Overwrite";
        if (flags.Contains("DATA_TRUNCATION")) return "File_Data_Truncation";
        if (flags.Contains("CLOSE")) return "File_Close";
        return "File_Modified";
    }

    private static IEnumerable<string> DecodeReasonFlags(uint reason)
    {
        void Maybe(uint mask, string name, List<string> result) { if ((reason & mask) != 0) result.Add(name); }
        var flags = new List<string>();
        Maybe(0x00000001, "DATA_OVERWRITE", flags);
        Maybe(0x00000002, "DATA_EXTEND", flags);
        Maybe(0x00000004, "DATA_TRUNCATION", flags);
        Maybe(0x00000100, "FILE_CREATE", flags);
        Maybe(0x00000200, "FILE_DELETE", flags);
        Maybe(0x00000400, "EA_CHANGE", flags);
        Maybe(0x00000800, "SECURITY_CHANGE", flags);
        Maybe(0x00001000, "RENAME_OLD_NAME", flags);
        Maybe(0x00002000, "RENAME_NEW_NAME", flags);
        Maybe(0x00004000, "INDEXABLE_CHANGE", flags);
        Maybe(0x00008000, "BASIC_INFO_CHANGE", flags);
        Maybe(0x00010000, "HARD_LINK_CHANGE", flags);
        Maybe(0x00020000, "COMPRESSION_CHANGE", flags);
        Maybe(0x00040000, "ENCRYPTION_CHANGE", flags);
        Maybe(0x00080000, "OBJECT_ID_CHANGE", flags);
        Maybe(0x00100000, "REPARSE_POINT_CHANGE", flags);
        Maybe(0x00200000, "STREAM_CHANGE", flags);
        Maybe(0x80000000, "CLOSE", flags);
        return flags.Count == 0 ? new[] { "0" } : flags;
    }
}
