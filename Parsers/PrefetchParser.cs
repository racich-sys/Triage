using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VestigantTriage;

public class PrefetchParser : IArtifactParser
{
    public string ParserName => "Windows Prefetch (.pf)";

    public bool CanParse(string filePath) => filePath.EndsWith(".pf", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log)
    {
        var fallbackName = ForensicText.PrefetchNameFromPath(filePath);
        var ev = new NormalizedEvent
        {
            DataSource = "Prefetch",
            Operation = "Application_Executed",
            ObjectPath = fallbackName,
            UserId = "LocalSystem",
            TimestampUtc = DateTime.MinValue
        };

        ev.AdditionalFields["EventCategory"] = "Execution";
        ev.AdditionalFields["ArtifactType"] = "Prefetch";
        ev.AdditionalFields["PrefetchFile"] = Path.GetFileName(filePath);
        ev.AdditionalFields["PrefetchLocalEvidencePath"] = filePath;
        ev.AdditionalFields["PrefetchLastWriteUtc"] = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).ToString("O") : string.Empty;
        ev.AdditionalFields["ExecutableName"] = fallbackName;
        ev.AdditionalFields["FileName"] = fallbackName;
        ev.AdditionalFields["DisplayTarget"] = fallbackName;
        ParserSupport.AddParseQuality(ev, ParserName, "Low", "Filename fallback before payload validation.");
        ParserSupport.SetEventTime(ev, null, "PrefetchRunTimeNotDecoded", "Unknown", false, "Prefetch source/staged file timestamps are not used as execution time.");

        try
        {
            var rawBytes = File.ReadAllBytes(filePath);
            byte[] data = TryDecompressMam(rawBytes, ev);
            if (!LooksLikePrefetchPayload(data))
            {
                ev.AdditionalFields["ParseWarning"] = "Prefetch payload signature was not valid after decompression attempt; using evidence filename fallback.";
                return new[] { ev };
            }

            uint version = BitConverter.ToUInt32(data, 0);
            ev.AdditionalFields["PrefetchVersion"] = version.ToString();
            ev.AdditionalFields["PrefetchSignature"] = Encoding.ASCII.GetString(data, 4, 4);

            string executableName = ParserSupport.Clean(ParserSupport.ReadUnicodeNullTerminated(data, 0x10, 60));
            if (ForensicText.LooksLikeWindowsExecutableName(executableName) && !ForensicText.IsLikelyGarbledExecutableText(executableName))
            {
                executableName = executableName.ToUpperInvariant();
                ev.ObjectPath = executableName;
                ev.AdditionalFields["DisplayTarget"] = executableName;
                ev.AdditionalFields["ExecutableName"] = executableName;
                ev.AdditionalFields["FileName"] = executableName;
                ParserSupport.AddParseQuality(ev, ParserName, "Medium", "Prefetch header and executable name decoded.");
            }
            else if (!string.IsNullOrWhiteSpace(executableName))
            {
                ev.AdditionalFields["RejectedExecutableName"] = executableName;
                ev.AdditionalFields["ParseWarning"] = "Executable name field looked invalid/garbled; retained filename-derived executable fallback.";
            }

            var runCount = ExtractRunCount(data, version);
            if (runCount.HasValue) ev.AdditionalFields["RunCount"] = runCount.Value.ToString();

            var runTimes = ExtractRunTimes(data, version).ToList();
            if (runTimes.Count > 0)
            {
                ParserSupport.SetEventTime(ev, runTimes[0], "PrefetchLastRun", "High", true);
                ev.AdditionalFields["LastRunUtc"] = runTimes[0].ToString("O");
                ev.AdditionalFields["AllRunTimesUtc"] = string.Join("; ", runTimes.Select(t => t.ToString("O")));
                ev.AdditionalFields["RunTimeCountDecoded"] = runTimes.Count.ToString();
                ParserSupport.AddParseQuality(ev, ParserName, "High", "Prefetch header, executable name, and run timestamps decoded.");
            }

            var referenced = ParserSupport.ExtractUsefulPathCandidates(data, 200).Where(p => !ForensicText.IsLocalWorkingEvidencePath(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (referenced.Count > 0)
            {
                ev.AdditionalFields["ReferencedPathCount"] = referenced.Count.ToString();
                ev.AdditionalFields["ReferencedPaths"] = string.Join("; ", referenced.Take(75));
                foreach (var p in referenced.Take(25).Select((path, idx) => new { path, idx }))
                    ev.AdditionalFields[$"ReferencedPath_{p.idx + 1:D2}"] = p.path;
            }
        }
        catch (Exception ex)
        {
            ev.AdditionalFields["ParseError"] = ex.Message;
            ParserSupport.AddParseQuality(ev, ParserName, "Low", "Parser exception; filename fallback only.");
        }

        return new[] { ev };
    }

    private static bool LooksLikePrefetchPayload(byte[] data)
    {
        if (data.Length < 0x54) return false;
        var version = BitConverter.ToUInt32(data, 0);
        return (version is 17 or 23 or 26 or 30 or 31) && data[4] == 'S' && data[5] == 'C' && data[6] == 'C' && data[7] == 'A';
    }

    private static uint? ExtractRunCount(byte[] data, uint version)
    {
        int[] offsets = version switch
        {
            17 => new[] { 0x90 },
            23 => new[] { 0x98 },
            26 or 30 or 31 => new[] { 0xD0, 0xD8 },
            _ => new[] { 0xD0, 0x98, 0x90 }
        };

        foreach (var offset in offsets)
        {
            if (offset + 4 > data.Length) continue;
            var value = BitConverter.ToUInt32(data, offset);
            if (value > 0 && value < 1000000) return value;
        }
        return null;
    }

    private static IEnumerable<DateTime> ExtractRunTimes(byte[] data, uint version)
    {
        var offsets = new List<int>();
        if (version == 17) offsets.Add(0x78);
        else if (version == 23) offsets.Add(0x80);
        else if (version is 26 or 30 or 31)
        {
            for (int off = 0x80; off <= 0xB8; off += 8) offsets.Add(off);
            for (int off = 0x78; off <= 0xB8; off += 8) offsets.Add(off);
        }
        else
        {
            for (int off = 0x78; off <= 0xB8; off += 8) offsets.Add(off);
        }

        var seen = new HashSet<long>();
        foreach (var offset in offsets.Distinct())
        {
            if (offset + 8 > data.Length) continue;
            long ft = BitConverter.ToInt64(data, offset);
            if (!seen.Add(ft)) continue;
            var dt = ParserSupport.FromFileTime(ft);
            if (dt.HasValue) yield return dt.Value;
        }
    }

    private static byte[] TryDecompressMam(byte[] rawBytes, NormalizedEvent ev)
    {
        if (rawBytes.Length <= 8 || Encoding.ASCII.GetString(rawBytes, 0, Math.Min(3, rawBytes.Length)) != "MAM")
            return rawBytes;

        ev.AdditionalFields["PrefetchCompressed"] = "true";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                ev.AdditionalFields["PrefetchDecompressError"] = "MAM decompression requires Windows ntdll; compressed payload cannot be decoded here.";
                return rawBytes;
            }

            uint decompressedSize = BitConverter.ToUInt32(rawBytes, 4);
            ev.AdditionalFields["PrefetchDeclaredDecompressedBytes"] = decompressedSize.ToString();
            if (decompressedSize == 0 || decompressedSize > 256 * 1024 * 1024) return rawBytes;

            var decompressed = new byte[decompressedSize];
            int workspaceRc = RtlGetCompressionWorkSpaceSize(0x0102, out uint workspaceSize, out _);
            if (workspaceRc != 0 || workspaceSize == 0)
            {
                ev.AdditionalFields["PrefetchDecompressReturnCode"] = workspaceRc.ToString();
                return rawBytes;
            }

            var workspace = new byte[workspaceSize];
            var payload = new byte[rawBytes.Length - 8];
            Array.Copy(rawBytes, 8, payload, 0, payload.Length);
            int rc = RtlDecompressBufferEx(0x0002 | 0x0100, decompressed, decompressedSize, payload, (uint)payload.Length, out uint finalSize, workspace);
            ev.AdditionalFields["PrefetchDecompressReturnCode"] = rc.ToString();
            ev.AdditionalFields["PrefetchDecompressedBytes"] = finalSize.ToString();
            if (rc == 0 && finalSize > 0 && finalSize <= decompressed.Length)
            {
                if (finalSize == decompressed.Length) return decompressed;
                var exact = new byte[finalSize];
                Array.Copy(decompressed, exact, finalSize);
                return exact;
            }
        }
        catch (Exception ex)
        {
            ev.AdditionalFields["PrefetchDecompressError"] = ex.Message;
        }
        return rawBytes;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint work, out uint frag);

    [DllImport("ntdll.dll")]
    private static extern int RtlDecompressBufferEx(ushort format, byte[] uncompressed, uint uncompressedSize, byte[] compressed, uint compressedSize, out uint final, byte[] work);
}
