# Vestigant Triage Risk Engine (Endpoint Enabled v2)

The engine reads `risk_rules.json` from either the same folder as `case.db` or the same folder as the executable. If no file is present, it uses built-in defaults.

## Score levels
- Critical: score >= 90
- High: score >= 70
- Medium: score >= 40
- Low: below 40

## Thresholds
- Download burst, 30 min: 10
- Mass download burst, 30 min: 25
- Mailbox burst, 30 min: 10
- Deletion burst, 30 min: 10
- After-hours download burst, 30 min: 10
- Sequence window: 60 minutes
- User IP baseline size: top 3 IPs per user

## Rules

| Code | Domain | Score | Criteria |
|---|---|---:|---|
| EXF-001 | EXFILTRATION | 90 | Personal email recipient present |
| EXF-002 | EXFILTRATION | 95 | Personal email recipient plus attachment indicators |
| EXF-010 | EXFILTRATION | 20 | FileDownloaded event |
| EXF-011 | EXFILTRATION | 60 | >= 10 downloads in 30 min |
| EXF-012 | EXFILTRATION | 80 | >= 25 downloads in 30 min |
| EXF-013 | EXFILTRATION | 85 | ZipFileName on download/export event |
| EXF-014 | EXFILTRATION | 75 | >= 10 after-hours downloads in 30 min |
| EXF-020 | EXFILTRATION | 20 | MailItemsAccessed event |
| EXF-021 | EXFILTRATION | 65 | >= 10 MailItemsAccessed in 30 min |
| EXF-022 | EXFILTRATION | 80 | Mailbox access from non-baseline IP |
| EXF-030 | EXFILTRATION | 80 | External or anonymous sharing operation |
| EXF-040 | EXFILTRATION | 35 | Print-like operation |
| EXF-052 | EXFILTRATION | 75 | Sensitive keyword on access/movement/sharing event |
| EXF-060 | EXFILTRATION | 85 | LNK/JumpList/RecycleBin points to Removable Media |
| EXF-061 | EXFILTRATION | 45 | LNK/JumpList/RecycleBin points to Network Drive |
| EXF-070 | EXFILTRATION | 75 | USB Device Connected (Registry SYSTEM) |
| EXF-080 | EXFILTRATION | 70 | Browser visit to personal cloud storage |
| ACC-010 | UNAUTHORIZED_ACCESS | 80 | User accesses another user's personal OneDrive / SharePoint path |
| CON-060 | CONCEALMENT | 95 | HardDelete operation |
| CON-061 | CONCEALMENT | 70 | >= 10 delete-type operations in 30 min |
| CON-062 | CONCEALMENT | 15 | Recycle Bin file deleted |
| CON-070 | CONCEALMENT | 100 | Anti-forensic or wiping tool executed (Prefetch/UserAssist) |
| SEQ-001 | EXFILTRATION | 95 | Mailbox access followed by personal-email send within 60 min |
| SEQ-002 | EXFILTRATION | 100 | Download followed by external sharing within 60 min |
| SEQ-003 | CONCEALMENT | 95 | Download followed by deletion within 60 min |
| SEQ-004 | EXFILTRATION | 100 | File access/download followed by cloud storage web visit |

## Editable fields in `risk_rules.json`

You can edit:
- `scoreThresholds`
- `businessHours`
- `thresholds`
- `personalDomains`
- `sensitiveKeywords`
- `antiForensicTools`
- `cloudStorageDomains`
- every rule's `enabled`, `riskDomain`, and `score`