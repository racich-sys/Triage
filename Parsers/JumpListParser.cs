using System;
using System.Collections.Generic;
using System.IO;

namespace VestigantTriage;

public class JumpListParser : IArtifactParser
{
    public string ParserName => "Windows Jump Lists";

    private static readonly Dictionary<string, string> KnownAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "1b4dd67f29cb1962", "Windows Explorer" },
        { "f01b4d95cf55d32a", "Windows Explorer" },
        { "918e0ecb43d17e23", "Notepad" },
        { "7b01f375fa228f24", "Paint" },
        { "a7bd71699cd38d1c", "WordPad" },
        { "d6d1ae988eb7e39a", "Command Prompt" },
        { "b82bc44393ce098d", "Remote Desktop" },
        { "5b04b775362b535", "Internet Explorer" },
        { "28c8b86deab549a1", "Microsoft Edge" },
        { "5d696d521ea23821", "Google Chrome" },
        { "f38bf4041d56956b", "Firefox" },
        { "bc1f4db3ed4e138a", "Microsoft Word" },
        { "fb3b0dbfee58fac8", "Microsoft Excel" },
        { "d119e7a9986b2eb3", "Microsoft PowerPoint" },
        { "9839aec31243a928", "Microsoft Excel" },
        { "adecfb853d77462a", "Microsoft Word" }
    };

    public bool CanParse(string filePath) => filePath.EndsWith(".automaticDestinations-ms", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".customDestinations-ms", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var bytes = File.ReadAllBytes(filePath);
        var lnkMagic = new byte[] { 0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00 };
        int foundCount = 0;

        var baseName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        var appId = baseName.Length >= 16 ? baseName[..16] : baseName;
        var appName = KnownAppIds.TryGetValue(appId, out var known) ? known : "Unknown App";
        var jumpListType = filePath.Contains("custom", StringComparison.OrdinalIgnoreCase) ? "Custom" : "Automatic";

        for (int i = 0; i <= bytes.Length - lnkMagic.Length; i++)
        {
            if (!IsMatch(bytes, i, lnkMagic))
                continue;

            foundCount++;
            var ev = new NormalizedEvent
            {
                DataSource = "JumpList",
                Operation = "JumpList_Entry_Parsed",
                ObjectPath = string.Empty,
                TimestampUtc = DateTime.MinValue,
                UserId = ParserSupport.ExtractUserFromPath(filePath)
            };

            ev.AdditionalFields["EventCategory"] = "FileAccess";
            ev.AdditionalFields["ArtifactType"] = "JumpListEmbeddedLnk";
            ev.AdditionalFields["AppID"] = appId;
            ev.AdditionalFields["PotentialAppName"] = appName;
            ev.AdditionalFields["JumpListType"] = jumpListType;
            ev.AdditionalFields["JumpListLocalEvidencePath"] = filePath;
            ev.AdditionalFields["JumpListEntryIndex"] = foundCount.ToString();
            ev.AdditionalFields["JumpListLastWriteUtc"] = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).ToString("O") : string.Empty;
            ParserSupport.AddParseQuality(ev, ParserName, "Medium", "Embedded LNK header found in JumpList; target confidence depends on LinkInfo path extraction.");
            ParserSupport.SetEventTime(ev, null, "JumpListTargetTimeNotDecoded", "Unknown", false, "JumpList source/staged file timestamps are not used as behavioral event time.");

            try
            {
                using var ms = new MemoryStream(bytes, i, bytes.Length - i);
                using var br = new BinaryReader(ms);
                ms.Seek(0x14, SeekOrigin.Begin);
                uint linkFlags = br.ReadUInt32();
                uint fileAttributes = br.ReadUInt32();
                long cTime = br.ReadInt64();
                long aTime = br.ReadInt64();
                long wTime = br.ReadInt64();
                uint fileSize = br.ReadUInt32();
                br.ReadInt32();
                uint showCommand = br.ReadUInt32();

                ev.AdditionalFields["LinkFlagsHex"] = linkFlags.ToString("X8");
                ev.AdditionalFields["TargetAttributes"] = ParserSupport.DecodeFileAttributes(fileAttributes);
                ev.AdditionalFields["TargetFileSizeBytes"] = fileSize.ToString();
                ev.AdditionalFields["ShowCommand"] = ParserSupport.DecodeShowCommand(showCommand);
                ev.AdditionalFields["TargetCreatedUtc"] = ParserSupport.FileTimeToString(cTime);
                ev.AdditionalFields["TargetAccessedUtc"] = ParserSupport.FileTimeToString(aTime);
                ev.AdditionalFields["TargetModifiedUtc"] = ParserSupport.FileTimeToString(wTime);
                var accessed = ParserSupport.FromFileTime(aTime);
                if (accessed.HasValue) ParserSupport.SetEventTime(ev, accessed.Value, "JumpListTargetAccessTime", "Medium", true);

                var targetPath = ShellLinkTargetExtractor.TryExtractTargetPath(bytes, i);
                if (string.IsNullOrWhiteSpace(targetPath)) targetPath = FirstUsefulPathCandidate(bytes, i);
                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    ParserSupport.AddTargetFields(ev, targetPath, "EmbeddedLnk");
                    ParserSupport.AddParseQuality(ev, ParserName, "High", "Embedded JumpList LNK target path decoded.");
                }
                else
                {
                    ev.AdditionalFields["ParseWarning"] = "JumpList embedded LNK found but target path was not decoded.";
                    ParserSupport.AddParseQuality(ev, ParserName, "Low", "Embedded LNK parsed without target path.");
                }
            }
            catch (Exception ex)
            {
                ev.AdditionalFields["ParseError"] = ex.Message;
                ParserSupport.AddParseQuality(ev, ParserName, "Low", "Parser exception for embedded LNK entry.");
            }

            yield return ev;
        }

        if (foundCount == 0)
            log($"Warning: No embedded LNK entries found in Jump List {Path.GetFileName(filePath)}");
    }

    private static string FirstUsefulPathCandidate(byte[] bytes, int offset)
    {
        var max = Math.Min(8192, bytes.Length - offset);
        var block = new byte[max];
        Array.Copy(bytes, offset, block, 0, max);
        foreach (var candidate in ParserSupport.ExtractUsefulPathCandidates(block, 20))
        {
            if (!ForensicText.IsLocalWorkingEvidencePath(candidate))
                return candidate;
        }
        return string.Empty;
    }

    private static bool IsMatch(byte[] data, int index, byte[] pattern)
    {
        if (index < 0 || index + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (data[index + i] != pattern[i]) return false;
        }
        return true;
    }
}
