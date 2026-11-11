#requires -Version 7.0
<#
.SYNOPSIS
Read-only inventory of Chinese FFXIV V3 ZIP patches.
.DESCRIPTION
Reads ZIP directories and patch_delta_direct.dat; never applies a patch.
Only Excel (0a) delta payloads are hashed. JSON output must remain under project tmp.
#>
[CmdletBinding()]
param(
    [string]$PatchRoot = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'XIVLauncherCN/patches/v3'),
    [string]$GameRoot = $env:FFXIV_GAME_PATH,
    [string]$OutputPath = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$GameRoot) { throw 'Specify -GameRoot or set FFXIV_GAME_PATH.' }
Add-Type -AssemblyName System.IO.Compression
$projectRoot = Split-Path -Parent $PSScriptRoot
$tmpRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'tmp'))
if (!$OutputPath) { $OutputPath = Join-Path $tmpRoot 'research/local-patches.json' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (!$OutputPath.StartsWith($tmpRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be inside this project tmp directory.'
}
$resolvedGameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\', '/')
function Normalize-Key([string]$Key) { return $Key.Replace('\', '/').TrimStart('/') }
function Read-Map($Document, [string]$ElementName) {
    $map = @{}
    foreach ($node in $Document.SelectNodes("//$ElementName")) {
        $key = Normalize-Key $node.GetAttribute('Key')
        if ($map.ContainsKey($key)) { throw "Duplicate $ElementName key: $key" }
        $map[$key] = $node.GetAttribute('Value')
    }
    return $map
}
$categoryNames = @{
    '00'='common'; '01'='bgcommon'; '02'='bg'; '03'='cut'; '04'='chara'
    '05'='shader'; '06'='ui'; '07'='sound'; '08'='vfx'; '09'='ui_script'
    '0a'='exd'; '0b'='game_script'; '0c'='music'; '12'='sqpack_test'; '13'='debug'
}
$currentFiles = @{}
$packages = [Collections.Generic.List[object]]::new()
$archives = @(Get-ChildItem -LiteralPath $PatchRoot -Recurse -File -Filter '*.zip' | Sort-Object FullName)
if ($archives.Count -eq 0) { throw "No ZIP patches found in $PatchRoot" }
foreach ($file in $archives) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            $name = Normalize-Key $entry.FullName
            if ($entries.ContainsKey($name)) { throw "Duplicate ZIP entry: $name" }
            $entries[$name] = $entry
        }
        $manifest = $entries['patch_delta_direct.dat']
        if (!$manifest) { throw "Missing patch_delta_direct.dat: $($file.FullName)" }
        if ($manifest.Length -gt 8MB) { throw 'Manifest exceeds 8 MiB inspection limit.' }
        $xmlSettings = [Xml.XmlReaderSettings]::new()
        $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlSettings.XmlResolver = $null
        $xmlSettings.MaxCharactersInDocument = 8MB
        $stream = $manifest.Open()
        $reader = [Xml.XmlReader]::Create($stream, $xmlSettings)
        try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader) }
        finally { $reader.Dispose(); $stream.Dispose() }
        $paths = Read-Map $document 'DeltaPathSubItem'
        $origins = Read-Map $document 'OriginMD5SubItem'
        $results = Read-Map $document 'ResultMD5SubItem'
        $deltas = Read-Map $document 'DeltaMD5SubItem'
        $items = [Collections.Generic.List[object]]::new()
        foreach ($target in ($paths.Keys | Sort-Object)) {
            $entryName = Normalize-Key $paths[$target]
            $entry = $entries[$entryName]
            if (!$entry) { throw "Missing mapped delta: $entryName" }
            $isExcel = $target -match '^game/sqpack/[^/]+/0a[0-9a-f]{4}\.win32\.(dat[0-9]+|index2?)$'
            $magic = $null
            $deltaHash = $null
            $matchesOrigin = $null
            $matchesResult = $null
            if ($isExcel) {
                $stream = $entry.Open()
                try {
                    $bytes = [byte[]]::new(8)
                    $count = 0
                    while ($count -lt $bytes.Length) {
                        $read = $stream.Read($bytes, $count, $bytes.Length - $count)
                        if ($read -eq 0) { break }
                        $count += $read
                    }
                    $magic = [Convert]::ToHexString($bytes, 0, $count)
                } finally { $stream.Dispose() }
                $stream = $entry.Open()
                $hash = [Security.Cryptography.MD5]::Create()
                try { $deltaHash = [Convert]::ToHexString($hash.ComputeHash($stream)) }
                finally { $hash.Dispose(); $stream.Dispose() }
                if (!$currentFiles.ContainsKey($target)) {
                    $localPath = [IO.Path]::GetFullPath((Join-Path $resolvedGameRoot $target))
                    if (!$localPath.StartsWith($resolvedGameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Target escapes game root: $target"
                    }
                    if (Test-Path -LiteralPath $localPath -PathType Leaf) {
                        $local = Get-Item -LiteralPath $localPath
                        $currentFiles[$target] = [ordered]@{
                            path = $target; bytes = $local.Length
                            md5 = (Get-FileHash -LiteralPath $localPath -Algorithm MD5).Hash
                        }
                    } else { $currentFiles[$target] = $null }
                }
                if ($null -ne $currentFiles[$target]) {
                    if ($origins.ContainsKey($target)) { $matchesOrigin = $currentFiles[$target].md5 -eq $origins[$target] }
                    if ($results.ContainsKey($target)) { $matchesResult = $currentFiles[$target].md5 -eq $results[$target] }
                }
            }
            $categoryId = $null; $category = 'other'; $repository = $null
            if ($target -match '^game/sqpack/(?<repository>[^/]+)/(?<category>[0-9a-f]{2})[0-9a-f]{4}\.win32\.') {
                $categoryId = $Matches['category'].ToLowerInvariant()
                $repository = $Matches['repository']
                $category = if ($categoryNames.ContainsKey($categoryId)) { $categoryNames[$categoryId] } else { "unknown-$categoryId" }
            }
            $expectedDeltaHash = $deltas[$target + '.delta']
            $items.Add([pscustomobject][ordered]@{
                target = $target; entry = $entryName; isExcel = $isExcel; repository = $repository; categoryId = $categoryId; category = $category
                deltaBytes = $entry.Length; compressedBytes = $entry.CompressedLength
                originMd5 = $origins[$target]; resultMd5 = $results[$target]
                expectedDeltaMd5 = $expectedDeltaHash
                actualDeltaMd5 = $deltaHash
                deltaMd5Matches = $(if ($null -ne $deltaHash -and $expectedDeltaHash) { $deltaHash -eq $expectedDeltaHash } else { $null })
                magic = $magic
                currentMatchesOrigin = $matchesOrigin; currentMatchesResult = $matchesResult
            })
        }
        $from = $null; $to = $null
        if ($file.BaseName -match '_(?<from>\d+(?:\.\d+){3})-(?<to>\d+(?:\.\d+){3})$') {
            $from = $Matches['from']; $to = $Matches['to']
        }
        $packages.Add([ordered]@{
            path = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($PatchRoot), $file.FullName); bytes = $file.Length
            from = $from; to = $to; zipEntries = $zip.Entries.Count
            mappedFiles = $paths.Count; excelFiles = @($items | Where-Object isExcel).Count
            categories = @($items | Group-Object category | Sort-Object Name | ForEach-Object {
                [ordered]@{ category = $_.Name; targetFiles = $_.Count; deltaBytes = ($_.Group | Measure-Object deltaBytes -Sum).Sum }
            })
            files = $items.ToArray()
        })
    } finally { $zip.Dispose() }
}
$versionFile = Join-Path $resolvedGameRoot 'game/ffxivgame.ver'
$report = [ordered]@{
    inspectedAt = [DateTimeOffset]::Now.ToString('o')
    gameVersion = $(if (Test-Path -LiteralPath $versionFile) { (Get-Content -Raw -LiteralPath $versionFile).Trim() } else { $null })
    sourceType = 'LocalCnV3Patches'
    verification = 'XML mappings; MD5 of Excel deltas and current Excel files. Whole ZIP hash and non-Excel payloads not checked.'
    currentExcelFiles = $currentFiles
    packages = $packages.ToArray()
}
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
[IO.File]::WriteAllText($OutputPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
$packages | ForEach-Object {
    [pscustomobject]@{ From = $_.from; To = $_.to; MiB = [Math]::Round($_.bytes / 1MB, 2); MappedFiles = $_.mappedFiles; ExcelFiles = $_.excelFiles }
} | Format-Table
Write-Output "Report: $OutputPath"
