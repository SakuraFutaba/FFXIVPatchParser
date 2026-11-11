#requires -Version 5.1
# Publish a self-contained Windows folder; all downloads and outputs remain under tmp.
& (Join-Path $PSScriptRoot 'Invoke-Dotnet.ps1') publish src/FFXIVPatchParser.Desktop -c Release -r win-x64 --self-contained true -p:PublishProfile=Portable -p:DebugType=none -p:DebugSymbols=false @args

exit $LASTEXITCODE
