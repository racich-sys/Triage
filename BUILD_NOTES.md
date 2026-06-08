# Build Notes - Vestigant Triage v3.4.3

v3.4.3 fixes the v3.4.0 compile failure in `Parsers\ParserRegistry.cs` caused by an invalid C# backslash character literal. It also introduces the root `ai_context.md` living project context file.

## Commands

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
