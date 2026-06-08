using System;
using System.Collections.Generic;

namespace VestigantTriage;

public class CaseFile
{
    public string CaseName { get; set; } = "New Investigation";
    public string CaseNumber { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Investigator { get; set; } = "";
    public string Description { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public List<SourceFileRecord> Sources { get; set; } = new();
    public List<CaseAuditEntry> AuditTrail { get; set; } = new();
    public List<TagDef> Tags { get; set; } = new();
}

public class TagDef { public string Name { get; set; } = ""; }

public class CaseAuditEntry
{
    public string TimestampUtc { get; set; } = "";
    public string Message { get; set; } = "";
}

public class SourceFileRecord
{
    public string FileName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string OriginalSourcePath { get; set; } = "";
    public string SourceType { get; set; } = "Unknown";
    public string HashAlgorithm { get; set; } = "SHA256";
    public string HashValue { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string ParserName { get; set; } = "";
    public int EventsImported { get; set; } = 0;
    public string LastIngestUtc { get; set; } = "";
    public bool ImportedToDb { get; set; } = false;
    public string Status { get; set; } = "Found";
    public string OriginalCreatedUtc { get; set; } = "";
    public string OriginalAccessedUtc { get; set; } = "";
    public string OriginalModifiedUtc { get; set; } = "";
}
