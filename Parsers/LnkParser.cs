using System;
using System.Collections.Generic;
using System.IO;

namespace VestigantTriage;

public class LnkParser : IArtifactParser
{
    public string ParserName => "Windows Shortcut (LNK)";

    public bool CanParse(string filePath) => filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var ev = new NormalizedEvent
        {
            DataSource = "LNK_File",
            Operation = "Shortcut_Created_Or_Accessed",
            ObjectPath = string.Empty,
            UserId = ParserSupport.ExtractUserFromPath(filePath),
            TimestampUtc = DateTime.MinValue
        };

        ev.AdditionalFields["EventCategory"] = "FileAccess";
        ev.AdditionalFields["ArtifactType"] = "WindowsShortcut";
        ev.AdditionalFields["ShortcutFileName"] = Path.GetFileName(filePath);
        ev.AdditionalFields["ShortcutLocalEvidencePath"] = filePath;
        ev.AdditionalFields["ShortcutLastWriteUtc"] = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).ToString("O") : string.Empty;
        ParserSupport.AddParseQuality(ev, ParserName, "Medium", "Header parsed; target confidence depends on LinkInfo/StringData availability.");
        ParserSupport.SetEventTime(ev, null, "LnkTargetTimeNotDecoded", "Unknown", false, "LNK source/staged file timestamps are not used as behavioral event time.");

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 0x4C || BitConverter.ToUInt32(bytes, 0) != 0x4C)
            {
                ev.AdditionalFields["ParseError"] = "Invalid LNK header.";
                ParserSupport.AddParseQuality(ev, ParserName, "Low", "Invalid LNK header; evidence metadata only.");
                return new[] { ev };
            }

            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            ms.Seek(0x14, SeekOrigin.Begin);
            uint linkFlags = br.ReadUInt32();
            uint fileAttributes = br.ReadUInt32();
            long cTime = br.ReadInt64();
            long aTime = br.ReadInt64();
            long wTime = br.ReadInt64();
            uint fileSize = br.ReadUInt32();
            int iconIndex = br.ReadInt32();
            uint showCommand = br.ReadUInt32();

            ev.AdditionalFields["LinkFlagsHex"] = linkFlags.ToString("X8");
            ev.AdditionalFields["LinkFlagsDecoded"] = DecodeLinkFlags(linkFlags);
            ev.AdditionalFields["TargetAttributes"] = ParserSupport.DecodeFileAttributes(fileAttributes);
            ev.AdditionalFields["TargetFileSizeBytes"] = fileSize.ToString();
            ev.AdditionalFields["IconIndex"] = iconIndex.ToString();
            ev.AdditionalFields["ShowCommand"] = ParserSupport.DecodeShowCommand(showCommand);
            ev.AdditionalFields["TargetCreatedUtc"] = ParserSupport.FileTimeToString(cTime);
            ev.AdditionalFields["TargetAccessedUtc"] = ParserSupport.FileTimeToString(aTime);
            ev.AdditionalFields["TargetModifiedUtc"] = ParserSupport.FileTimeToString(wTime);

            var accessed = ParserSupport.FromFileTime(aTime);
            if (accessed.HasValue)
                ParserSupport.SetEventTime(ev, accessed.Value, "LnkTargetAccessTime", "Medium", true);

            var stringData = ExtractStringData(bytes, linkFlags);
            foreach (var kv in stringData)
                ev.AdditionalFields[kv.Key] = kv.Value;

            ExtractVolumeInformation(bytes, ev);
            ExtractTrackerData(bytes, ev);

            var targetPath = ShellLinkTargetExtractor.TryExtractTargetPath(bytes);
            if (string.IsNullOrWhiteSpace(targetPath)) targetPath = FirstUsefulPathCandidate(bytes);
            if (string.IsNullOrWhiteSpace(targetPath)) targetPath = FirstLikelyStringDataPath(stringData);

            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                ParserSupport.AddTargetFields(ev, targetPath, "LinkInfo/StringData");
                ParserSupport.AddParseQuality(ev, ParserName, "High", "LNK target path decoded from LinkInfo/StringData/path candidates.");
            }
            else
            {
                ev.AdditionalFields["ParseWarning"] = "LNK target path was not decoded; ingest will display the original forensic artifact path instead of WorkingEvidence path.";
                ParserSupport.AddParseQuality(ev, ParserName, "Low", "LNK parsed but no target path decoded.");
            }
        }
        catch (Exception ex)
        {
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Parser exception; partial metadata only.");
        }

        return new[] { ev };
    }

    private static Dictionary<string, string> ExtractStringData(byte[] bytes, uint linkFlags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            ms.Seek(0x4C, SeekOrigin.Begin);

            if ((linkFlags & 0x01) != 0)
            {
                if (ms.Position + 2 > ms.Length) return result;
                ushort idListSize = br.ReadUInt16();
                if (ms.Position + idListSize > ms.Length) return result;
                ms.Seek(idListSize, SeekOrigin.Current);
            }

            if ((linkFlags & 0x02) != 0)
            {
                if (ms.Position + 4 > ms.Length) return result;
                uint linkInfoSize = br.ReadUInt32();
                if (linkInfoSize >= 0x1C && ms.Position - 4 + linkInfoSize <= ms.Length)
                    ms.Seek(linkInfoSize - 4, SeekOrigin.Current);
            }

            bool unicode = (linkFlags & 0x80) != 0;
            ReadOptionalString(result, "Description", br, unicode, (linkFlags & 0x04) != 0);
            ReadOptionalString(result, "RelativePath", br, unicode, (linkFlags & 0x08) != 0);
            ReadOptionalString(result, "WorkingDirectory", br, unicode, (linkFlags & 0x10) != 0);
            ReadOptionalString(result, "CommandLineArguments", br, unicode, (linkFlags & 0x20) != 0);
            ReadOptionalString(result, "IconLocation", br, unicode, (linkFlags & 0x40) != 0);
        }
        catch { }
        return result;
    }

    private static void ReadOptionalString(Dictionary<string, string> result, string name, BinaryReader br, bool unicode, bool present)
    {
        if (!present) return;
        var stream = br.BaseStream;
        if (stream.Position + 2 > stream.Length) return;
        ushort charCount = br.ReadUInt16();
        if (charCount == 0) return;
        int byteCount = unicode ? charCount * 2 : charCount;
        if (stream.Position + byteCount > stream.Length) return;
        var raw = br.ReadBytes(byteCount);
        var text = unicode ? System.Text.Encoding.Unicode.GetString(raw) : System.Text.Encoding.Default.GetString(raw);
        text = ParserSupport.Clean(text.TrimEnd('\0'));
        if (!string.IsNullOrWhiteSpace(text)) result[name] = text;
    }

    private static string FirstLikelyStringDataPath(Dictionary<string, string> stringData)
    {
        foreach (var key in new[] { "RelativePath", "WorkingDirectory", "IconLocation", "CommandLineArguments" })
        {
            if (stringData.TryGetValue(key, out var value))
            {
                foreach (var c in ForensicText.ExtractPathCandidates(value))
                    return c;
            }
        }
        return string.Empty;
    }

    private static string FirstUsefulPathCandidate(byte[] bytes)
    {
        foreach (var candidate in ParserSupport.ExtractUsefulPathCandidates(bytes, 20))
        {
            if (!ForensicText.IsLocalWorkingEvidencePath(candidate))
                return candidate;
        }
        return string.Empty;
    }

    private static void ExtractVolumeInformation(byte[] bytes, NormalizedEvent ev)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            ms.Seek(0x14, SeekOrigin.Begin);
            uint linkFlags = br.ReadUInt32();
            bool hasTargetIdList = (linkFlags & 0x01) != 0;
            bool hasLinkInfo = (linkFlags & 0x02) != 0;
            ms.Seek(0x4C, SeekOrigin.Begin);
            if (hasTargetIdList)
            {
                ushort idListSize = br.ReadUInt16();
                ms.Seek(idListSize, SeekOrigin.Current);
            }
            if (!hasLinkInfo || ms.Position + 0x1C > ms.Length) return;

            long linkInfoStart = ms.Position;
            uint linkInfoSize = br.ReadUInt32();
            if (linkInfoSize < 0x1C || linkInfoStart + linkInfoSize > ms.Length) return;
            br.ReadUInt32();
            uint linkInfoFlags = br.ReadUInt32();
            uint volumeIdOffset = br.ReadUInt32();
            if ((linkInfoFlags & 0x01) == 0 || volumeIdOffset == 0) return;

            long volStart = linkInfoStart + volumeIdOffset;
            if (volStart + 16 > ms.Length) return;
            ms.Seek(volStart, SeekOrigin.Begin);
            uint volIdSize = br.ReadUInt32();
            if (volIdSize < 16 || volStart + volIdSize > ms.Length) return;
            uint driveTypeRaw = br.ReadUInt32();
            uint serial = br.ReadUInt32();
            uint labelOffset = br.ReadUInt32();
            string serialHex = serial.ToString("X8");
            ev.AdditionalFields["VolumeSerialNumber"] = serialHex[..4] + "-" + serialHex[4..];
            ev.AdditionalFields["VolumeDriveTypeRaw"] = driveTypeRaw.ToString();
            ev.AdditionalFields["VolumeDriveType"] = DecodeVolumeDriveType(driveTypeRaw);

            if (labelOffset > 0 && volStart + labelOffset < ms.Length)
            {
                var label = ParserSupport.ReadAnsiNullTerminated(bytes, (int)(volStart + labelOffset), (int)(volIdSize - labelOffset));
                if (!string.IsNullOrWhiteSpace(label)) ev.AdditionalFields["VolumeLabel"] = label;
            }
        }
        catch { }
    }

    private static void ExtractTrackerData(byte[] bytes, NormalizedEvent ev)
    {
        try
        {
            var ascii = System.Text.Encoding.ASCII.GetString(bytes);
            int idx = ascii.IndexOf("Droid", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                ev.AdditionalFields["TrackerDataPresent"] = "true";
                var tail = ascii.Substring(idx, Math.Min(256, ascii.Length - idx));
                var clean = ParserSupport.Clean(tail);
                if (!string.IsNullOrWhiteSpace(clean)) ev.AdditionalFields["TrackerDataSnippet"] = clean;
            }
        }
        catch { }
    }

    private static string DecodeVolumeDriveType(uint driveType) => driveType switch
    {
        0 => "Unknown",
        1 => "NoRootDirectory",
        2 => "Removable",
        3 => "Fixed",
        4 => "Remote/Network",
        5 => "CD-ROM",
        6 => "RAMDisk",
        _ => $"Unknown({driveType})"
    };

    private static string DecodeLinkFlags(uint flags)
    {
        var names = new List<string>();
        void Add(uint mask, string name) { if ((flags & mask) != 0) names.Add(name); }
        Add(0x00000001, "HasLinkTargetIDList");
        Add(0x00000002, "HasLinkInfo");
        Add(0x00000004, "HasName");
        Add(0x00000008, "HasRelativePath");
        Add(0x00000010, "HasWorkingDir");
        Add(0x00000020, "HasArguments");
        Add(0x00000040, "HasIconLocation");
        Add(0x00000080, "IsUnicode");
        return names.Count == 0 ? "None" : string.Join(", ", names);
    }
}
