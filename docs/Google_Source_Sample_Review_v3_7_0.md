# Google Source Sample Review - v3.7.0

This note summarizes the uploaded Google/Takeout/Gemini sample archives reviewed before the v3.7.0 parser update.

## Uploaded samples reviewed

- `Google Audit and Investigation-20260608T002026Z-3-001.zip`
  - 19 Google Workspace Audit/Investigation CSV exports.
  - Large high-value families include Drive, Gmail, OAuth, Calendar, Access Evaluation, Gemini for Workspace, Meet, Chat, Device, User, Chrome, Chrome Sync, Takeout, Vault, Groups, Tasks, and Workspace Studio.
- `Takeout.zip`
  - 365 files across Takeout product folders.
  - Useful parsable content includes Access Log Activity CSV, Google Chat message JSON, Google Meet conference history CSV, Calendar ICS, My Activity HTML, NotebookLM HTML/PDF/source material, Contacts VCF, Gemini product HTML, and Google Wallet PDFs.
- `Part1_takeout-20260604T140952Z-17-001.zip`
  - Drive preferences, Trash XLSX, and nested Takeout ZIP archives under Drive/Takeout.
- `Part2_takeout-20260604T140952Z-15-001.zip`
  - Mail User Settings JSON: Filters, Blocked Addresses, and Vacation Responder.
- `Gemini AI session-20260608T002023Z-3-001.zip`
  - Gemini transcript RTF/PDF screenshots, code-extract `.py` files, screenshots, and output PDFs.

## New v3.7.0 parser coverage

- Promote Takeout My Activity HTML entries to events when `content-cell` activity blocks are present.
- Promote Google Chat `messages.json` into message-level events with creator, message ID, topic ID, text preview, and first URL.
- Promote Google Meet `conference_history_records.csv` into participation events with start/end/duration and meeting identifiers.
- Promote Calendar `.ics` VEVENT records into calendar event rows with title, UID, start/end, organizer, and location.
- Inventory and bounded-parse nested Takeout ZIPs up to a guarded size limit.
- Extract Gemini transcript/code text previews and risk-term summaries while keeping PDF/screenshots as metadata-only inventory.

## Validation expectations

- `RUN_GOOGLE_THIN_TEST_V3_7_0.ps1` should run with risk skipped by default.
- A successful v3.7.0 Google thin run should show events for:
  - Google Workspace Audit CSV rows.
  - Takeout Access Log Activity CSV rows.
  - Takeout My Activity HTML rows.
  - Google Chat message JSON rows.
  - Google Meet conference history rows.
  - Calendar ICS VEVENT rows.
  - Gemini transcript/code text preview rows.
- Full raw source columns should remain out of indexed `event_fields` and in `google_event_raw_fields`.
