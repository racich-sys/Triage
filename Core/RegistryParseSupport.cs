using System;
using System.Collections;
using System.Text;

namespace VestigantTriage;

internal static class RegistryParseSupport
{
    public static string SafeKeyName(object key)
    {
        try { return ParserSupport.Clean(GetProperty(key, "KeyName")?.ToString()); }
        catch { return string.Empty; }
    }

    public static string SafeKeyPath(object key)
    {
        try { return ParserSupport.Clean(GetProperty(key, "KeyPath")?.ToString()); }
        catch { return string.Empty; }
    }

    public static string SafeValueName(object val)
    {
        try { return ParserSupport.Clean(GetProperty(val, "ValueName")?.ToString()); }
        catch { return string.Empty; }
    }

    public static DateTime LastWriteUtc(object key)
    {
        try
        {
            var v = GetProperty(key, "LastWriteTime");
            if (v is DateTimeOffset dto) return dto.UtcDateTime;
            if (v is DateTime dt) return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        }
        catch { }
        return DateTime.MinValue;
    }

    public static string GetValueString(object key, string valueName)
    {
        try
        {
            var valuesObject = GetProperty(key, "Values");
            if (valuesObject is not IEnumerable values) return string.Empty;
            foreach (var item in values)
            {
                if (item == null) continue;
                if (string.Equals(SafeValueName(item), valueName, StringComparison.OrdinalIgnoreCase))
                    return GetValueDisplay(item);
            }
        }
        catch { }
        return string.Empty;
    }

    public static string GetValueDisplay(object val)
    {
        try
        {
            var rawObject = GetProperty(val, "ValueData");
            if (rawObject != null) return ParserSupport.Clean(rawObject.ToString());
            return DecodeBestEffort(SafeValueRaw(val));
        }
        catch { return string.Empty; }
    }

    public static byte[] SafeValueRaw(object val)
    {
        try
        {
            var raw = GetProperty(val, "ValueDataRaw");
            if (raw is byte[] bytes) return bytes;
        }
        catch { }
        return Array.Empty<byte>();
    }

    public static object? GetProperty(object obj, string name) => obj.GetType().GetProperty(name)?.GetValue(obj);

    public static string DecodeBestEffort(byte[] raw)
    {
        if (raw == null || raw.Length == 0) return string.Empty;
        string unicode = ParserSupport.Clean(Encoding.Unicode.GetString(raw).TrimEnd('\0'));
        string ascii = ParserSupport.Clean(Encoding.Default.GetString(raw).TrimEnd('\0'));
        return CountAscii(unicode) >= CountAscii(ascii) ? unicode : ascii;
    }

    private static int CountAscii(string value)
    {
        int count = 0;
        foreach (var c in value)
            if (c >= 0x20 && c <= 0x7e) count++;
        return count;
    }
}
