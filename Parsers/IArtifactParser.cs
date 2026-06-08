using System;
using System.Collections.Generic;

namespace VestigantTriage;

public interface IArtifactParser
{
    string ParserName { get; }
    
    /// <summary>
    /// Checks if the provided file is compatible with this specific parser.
    /// </summary>
    bool CanParse(string filePath);

    /// <summary>
    /// Parses the file and yields normalized events for database insertion.
    /// </summary>
    IEnumerable<NormalizedEvent> Parse(string filePath, string tzName, Action<string> log);
}

/// <summary>
/// The standard data transfer object (DTO) used to move evidence from 
/// raw files into the SQLite behavior database.
/// </summary>
public class NormalizedEvent
{
    public DateTime TimestampUtc { get; set; }
    public string DataSource { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ObjectPath { get; set; } = "";
    public string ClientIp { get; set; } = "";
    // Timestamp provenance. A blank/MinValue timestamp is valid for metadata-only rows.
    public string EventTimeBasis { get; set; } = "";
    public string EventTimeConfidence { get; set; } = "";
    public bool? IsBehavioralTimestamp { get; set; }
    public string TimestampWarning { get; set; } = "";
    
    // Catch-all for artifact-specific metadata (e.g., Serial Numbers, App IDs)
    public Dictionary<string, string> AdditionalFields { get; } = new(StringComparer.OrdinalIgnoreCase);
}