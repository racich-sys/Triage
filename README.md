# Vestigant Triage v3.4.3

v3.4.3 is a narrow build hotfix for the v3.4.0 Google source framework. It fixes the invalid C# backslash character literal in `Parsers\ParserRegistry.cs`, adds the root `ai_context.md` living project context file, and preserves all v3.4.0 Google source functionality.

## Build and fixed-image validation

```powershell
Set-Location D:\Downloads
Get-FileHash .\VestigantTriage_v3_4_3_google_framework_build_fix.zip -Algorithm SHA256
Expand-Archive -LiteralPath .\VestigantTriage_v3_4_3_google_framework_build_fix.zip -DestinationPath T:\ -Force
& "T:\VestigantTriage_v3_4_3\RUN_DEFAULT_IMAGE_TRIAGE.ps1"
```

Expected upload:

```text
D:\Downloads\Upload_VestigantTriage_v3_4_3_*.zip
```

## Google-source validation after build

1. Open the GUI.
2. Add the Google Audit ZIP using **Google Workspace Audit / Investigation CSV or ZIP**.
3. Add the Takeout archive or folder using **Google Takeout Archive / Export Files**.
4. Add the Gemini session ZIP using **Gemini Session Archive**.
5. Run ingest, risk analysis, then export the validation bundle.
6. Review the `vestigant_google_*.csv` validation files and the Master metadata export.
