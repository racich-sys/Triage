# Parser Usage and Forensic Actionability Review

## Scope

This review focused on whether every parser in the project is actually reachable from evidence ingestion and whether parser output is normalized into timeline/risk fields that an investigator can use.

The project was reviewed statically in this environment. The .NET SDK is not installed in the container, so compile validation must be performed on Windows with the commands below.

## What was changed

### 1. Central parser registry

Added `Parsers/ParserRegistry.cs`.

All artifact parsers are now registered in one ordered list:

| Parser | Target artifacts | Primary investigative value |
|---|---|---|
| `O365UalParser` | UAL CSV | Cloud access, downloads, sharing, email, mailbox access, audit operations |
| `RecycleBinParser` | `$I*` files | Deleted original path, deletion time, file size, probable drive type |
| `BrowserHistoryParser` | Chromium `History` databases | Cloud storage visits, web activity, typed/visited URL context |
| `EvtxParser` | `.evtx` logs | Windows event log activity, print events, provider/event ID context |
| `ShellBagsParser` | `USRCLASS.DAT` | Folder navigation, network/removable folder traces |
| `RegistryParser` | `SYSTEM`, `NTUSER.DAT` | USBSTOR devices and UserAssist execution traces |
| `PrefetchParser` | `.pf` files | Program execution, run count, plausible last run time |
| `JumpListParser` | `.automaticDestinations-ms`, `.customDestinations-ms` | Recently/frequently accessed files by application |
| `LnkParser` | `.lnk` files | File access traces, target path, volume serial, drive type |
| `UsnJournalParser` | `$J`, `$UsnJrnl` | NTFS file create/delete/rename/modify traces |

### 2. Evidence extraction now targets parser-compatible artifacts

`ImageTriageCore` and `TskTriageCore` now use `ParserRegistry.IsTargetArtifactPath(...)` instead of independent filename tests. This keeps image triage aligned with the actual parser inventory.

The target list now includes:

- Office owner lock files beginning `~$`
- EVTX logs
- Prefetch files
- LNK shortcuts
- Jump Lists
- Recycle Bin `$I` files
- Registry hives: `SYSTEM`, `SOFTWARE`, `SAM`, `SECURITY`, `NTUSER.DAT`, `USRCLASS.DAT`
- Chromium `History` databases
- NTFS `$J` / `$UsnJrnl`

### 3. E01/EWF routing was added

`TskTriageCore` existed but was not called from the UI. Disk image selection now separates:

- EWF/E01-style images -> `TskTriageCore.ExtractFromEwf(...)`
- raw/dd/img-style images -> `ImageTriageCore.ExtractTargetedArtifacts(...)`

The TSK path requires `fls.exe`, `icat.exe`, and `mmls.exe` in the application folder. If they are missing, the UI logs the error instead of silently treating E01 as a raw image.

### 4. Endpoint artifacts are now first-class timeline/risk records

`IngestEngine` was rewritten so non-UAL parser output is inserted into the main `events` table, not just loose metadata. The following fields are populated where possible:

- `data_source`
- `user_id`
- `operation`
- `object_id`
- `creation_date_utc`
- `creation_date_local`
- `forensic_status`
- `source_file`
- `workload`
- `category`
- `source_relative_url`
- `file_name`
- `file_size_bytes`
- `result_status`
- `raw_json`

Artifact-specific details are still preserved in `event_fields`.

### 5. Parser auditability improved

For every parsed event, ingestion now adds:

- `ParserName`
- `OriginalSourcePath`
- `LocalEvidencePath`
- `SourceHashSHA256`
- `ForensicStatus`

If a parser matches but emits zero events, the source is still recorded as a metadata fallback row so the evidence is auditable.

### 6. Risk engine now uses more endpoint artifacts

The risk engine now includes endpoint evidence in the following rule areas:

| Rule | Domain | Endpoint evidence used |
|---|---|---|
| `EXF-060` | Exfiltration | LNK, Jump List, Recycle Bin, ShellBags pointing to removable/secondary drive |
| `EXF-061` | Exfiltration | LNK, Jump List, Recycle Bin, ShellBags pointing to network path |
| `EXF-070` | Exfiltration | USB device connection from registry/system traces |
| `EXF-080` | Exfiltration | Browser visits to personal cloud storage providers |
| `CON-062` | Concealment | Recycle Bin deletion evidence |
| `CON-063` | Concealment | USN Journal file delete evidence |
| `CON-070` | Concealment | Prefetch/UserAssist execution of anti-forensic tools |
| `SEQ-004` | Exfiltration sequence | File access/download followed by cloud-storage browser visit |

### 7. UAL parsing corrected for usable ingest

The UAL parser now uses quoted CSV parsing and JSON flattening for `AuditData`, instead of comma splitting. This makes embedded JSON with commas usable.

## Build/test commands

Run on Windows from the project root:

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false
```

Or use:

```powershell
.\Build-Workbench.ps1 -Publish
```

## Current limitations to address next

1. The parsers are functional triage parsers, not substitutes for mature forensic tools. LNK, Jump List, Prefetch, ShellBags, and USN parsing should eventually be expanded with formal structure validation and parser confidence levels.
2. E01/EWF support depends on Sleuth Kit binaries being present. The UI should eventually include a dependency check/status panel.
3. Registry parsing currently extracts USBSTOR and UserAssist only. Next useful additions are MountedDevices, MountPoints2, RecentDocs, Office MRUs, RunMRU, TypedPaths, BAM/DAM, ShimCache/AmCache where available.
4. Browser parsing currently targets Chromium history. Firefox places.sqlite and Edge/Chrome downloads should be separate parser targets.
5. EVTX parsing is generic. Add event-specific interpretations for logon, RDP, USBPnP, print, service installation, scheduled tasks, PowerShell, Defender, and Security auditing.
6. Risk scoring is still rule-based. Add a second layer for sequence and behavior pattern summaries per user/day/device.
7. Add parser test fixtures and a command-line test harness before relying on this across case data.
