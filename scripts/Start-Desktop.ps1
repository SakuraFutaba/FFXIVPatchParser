#requires -Version 5.1
# Keep build outputs, NuGet caches and temporary files in the project workspace.
& (Join-Path $PSScriptRoot 'Invoke-Dotnet.ps1') run --project src/FFXIVPatchParser.Desktop @args

exit $LASTEXITCODE
