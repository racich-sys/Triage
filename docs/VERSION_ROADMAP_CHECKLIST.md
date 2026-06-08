# Vestigant Triage Version Roadmap Checklist

## v3.6.3.1 - Google framework build fix and ai_context baseline

- [x] Build from v3.4.0 baseline.
- [x] Fix invalid C# backslash character literal in `Parsers\ParserRegistry.cs`.
- [x] Add root `ai_context.md`.
- [x] Include `ai_context.md` in upload/project documentation packaging.
- [x] Add build-validation assertions for `ai_context.md` presence and packaging.
- [ ] Build on Windows workstation.
- [ ] Run real fixed-image headless case.
- [ ] Upload v3.6.3.1 validation bundle for review.

## v3.6.3.1 or v3.6.3.1 - Google validation hardening

- [ ] Review Google Workspace Audit ZIP ingestion results.
- [ ] Review Google Takeout ingestion results.
- [ ] Review Gemini session archive ingestion results.
- [ ] Improve Google schema mapping and risk correlation based on validation output.
- [ ] Add Google Cloud Logging JSON/NDJSON support when sample data is available.
