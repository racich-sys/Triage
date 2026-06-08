using System;
using System.Collections.Generic;

namespace VestigantTriage;

internal sealed class RiskHit
{
    public long EventId { get; set; }
    public string RuleCode { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string RiskDomain { get; set; } = "";
    public int RiskScore { get; set; }
    public string RiskLevel { get; set; } = "Low";
    public string Reason { get; set; } = "";
    public string SupportingValue { get; set; } = "";
}

internal sealed class EventRecord
{
    public long EventId { get; set; }
    public string RecordId { get; set; } = "";
    public string CreationDateUtc { get; set; } = "";
    public string CreationDateLocal { get; set; } = "";
    public string EventTimeBasis { get; set; } = "";
    public string EventTimeConfidence { get; set; } = "";
    public bool IsBehavioralTimestamp { get; set; } = true;
    public string TimestampWarning { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Workload { get; set; } = "";
    public string Category { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string ClientIpAlt { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string SourceRelativeUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public long? FileSizeBytes { get; set; }
    public string Recipients { get; set; } = "";
    public string AttachmentDetails { get; set; } = "";
    public string ResultStatus { get; set; } = "";
    public string RawJson { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public int SourceRowNumber { get; set; }
    public string ZipFileName { get; set; } = "";
    public string EmailFrom { get; set; } = "";
    public string EmailTo { get; set; } = "";
    public string EmailSubject { get; set; } = "";
    public string PathHint { get; set; } = "";
    public string AttachmentsExpanded { get; set; } = "";
    public string DataSource { get; set; } = "";
    public string DriveType { get; set; } = "";
}

internal sealed class IngestResult
{
    public int RowsImported { get; set; }
    public string DatabasePath { get; set; } = "";
}

internal sealed class RiskRunResult
{
    public int TotalHits { get; set; }
    public int CriticalEvents { get; set; }
    public int HighEvents { get; set; }
    public int MediumEvents { get; set; }
}

internal sealed class RiskEngineConfig
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Default Risk Profile";
    public RiskScoreThresholds ScoreThresholds { get; set; } = new();
    public RiskBusinessHours BusinessHours { get; set; } = new();
    public RiskThresholds Thresholds { get; set; } = new();
    public List<string> PersonalDomains { get; set; } = new();
    public List<string> SensitiveKeywords { get; set; } = new();
    public List<string> AntiForensicTools { get; set; } = new();
    public List<string> TransferTools { get; set; } = new();
    public List<string> CloudStorageDomains { get; set; } = new();
    
    public Dictionary<string, RiskRuleDefinition> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class RiskScoreThresholds
{
    public int CriticalMin { get; set; } = 90;
    public int HighMin { get; set; } = 70;
    public int MediumMin { get; set; } = 40;
}

internal sealed class RiskBusinessHours
{
    public int StartHourLocal { get; set; } = 7;
    public int EndHourLocal { get; set; } = 19;
}

internal sealed class RiskThresholds
{
    public int DownloadBurst30Min { get; set; } = 10;
    public int MassDownloadBurst30Min { get; set; } = 25;
    public int MailboxBurst30Min { get; set; } = 10;
    public int DeletionBurst30Min { get; set; } = 10;
    public int AfterHoursDownloadBurst30Min { get; set; } = 10;
    public int SequenceWindowMinutes { get; set; } = 60;
    public int UserIpBaselineTopCount { get; set; } = 3;
}

internal sealed class RiskRuleDefinition
{
    public bool Enabled { get; set; } = true;
    public string RiskDomain { get; set; } = "";
    public int Score { get; set; }
    public string Description { get; set; } = "";
}