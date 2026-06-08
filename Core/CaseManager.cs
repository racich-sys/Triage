using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VestigantTriage;

public static class CaseManager
{
    private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public static void Save(string path, CaseFile model)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Case file path is blank.", nameof(path));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        model.AuditTrail.Add(new CaseAuditEntry
        {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Message = "Case file saved."
        });

        var json = JsonSerializer.Serialize(model, _options);
        File.WriteAllText(path, json);
    }

    public static CaseFile Load(string path)
    {
        if (!File.Exists(path))
            return new CaseFile();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CaseFile>(json, _options) ?? new CaseFile();
    }

    public static SourceFileRecord CreateSourceRecord(string caseFolder, string filePath, string type)
    {
        var info = new FileInfo(filePath);
        return new SourceFileRecord
        {
            FileName = info.Name,
            LocalPath = StorePath(caseFolder, filePath),
            OriginalSourcePath = filePath,
            SourceType = type,
            FileSizeBytes = info.Exists ? info.Length : 0,
            HashAlgorithm = "SHA256",
            HashValue = ComputeHash(filePath)
        };
    }

    public static string StorePath(string baseFolder, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(baseFolder))
            return fullPath;

        try
        {
            return Path.GetRelativePath(baseFolder, fullPath);
        }
        catch
        {
            return fullPath;
        }
    }

    private static string ComputeHash(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }
}
