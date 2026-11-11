#requires -Version 7.0
[CmdletBinding()]
param([switch]$DownloadLatest, [switch]$SkipProbe)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Join-Path (Split-Path -Parent $PSScriptRoot) 'tmp/patches'
$meta=Join-Path $root 'metadata'
New-Item -ItemType Directory -Force $meta | Out-Null
$client=[Net.Http.HttpClient]::new()
$client.Timeout=[TimeSpan]::FromSeconds(60)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('FF14v3autopatch')
# Public launcher CDN signing constant; signed links expire and are generated per request.
function Sign-Url([uri]$Uri) {
    $stamp=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString('x')
    $key='EKUWRI5KXXAIDlQ0mBNLa7XkjU1JNFuL'
    $hash=[Convert]::ToHexString([Security.Cryptography.MD5]::HashData([Text.Encoding]::UTF8.GetBytes($key+$Uri.AbsolutePath+$stamp))).ToLowerInvariant()
    return [uri]($Uri.Scheme+'://'+$Uri.Authority+'/'+$hash+'/'+$stamp+$Uri.AbsolutePath)
}
function Resolve-DownloadUrl([string]$BaseUrl,[string]$Relative) {
    $absolute=$null
    if ([uri]::TryCreate($Relative,[UriKind]::Absolute,[ref]$absolute) -and $absolute.Scheme -in @('http','https')) { return $absolute }
    return [uri]($BaseUrl.TrimEnd('/')+'/'+$Relative.TrimStart('/'))
}
function Get-Text([uri]$Uri) { return $client.GetStringAsync($Uri).GetAwaiter().GetResult() }
$packages=[Collections.Generic.List[object]]::new()
try {
    $versionUrl='https://v3launcher.jijiagames.com/v3launcher/build/ver2data/100001900/8847/-1/ver2.dat'
    $versionText=Get-Text ([uri]$versionUrl)
    [IO.File]::WriteAllText((Join-Path $meta 'ver2.dat'),$versionText)
    $v=$versionText | ConvertFrom-Json
    $area=@($v.areas | Where-Object id -EQ '0')[0]
    foreach ($p in $v.packages) {
        $package=[ordered]@{name=$p.name;from=$p.from;to=$p.to;versionView=$p.versionView;isLatest=($p.to -eq $area.must);manifestAvailable=$false;error=$null;files=@()}
        try {
            if ($p.name -notmatch '^patch-[0-9.]+-[0-9.]+$') { throw "Invalid package name: $($p.name)" }
            $listText=Get-Text (Sign-Url (Resolve-DownloadUrl $v.baseUrl $p.fileListUrl))
            [IO.File]::WriteAllText((Join-Path $meta ($p.name+'.json')),$listText)
            $list=$listText | ConvertFrom-Json
            $package.manifestAvailable=$true
            foreach ($f in $list.fileList) {
                $base=if ($list.baseUrl) { $list.baseUrl } else { $v.baseUrl }
                $url=Resolve-DownloadUrl $base $f.url
                $name=[IO.Path]::GetFileName($f.path.Replace('\','/'))
                if ($name -in @('.','..') -or !$name -or $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid package filename' }
                if ([long]$f.size -lt 0 -or $f.md5 -notmatch '^[0-9a-fA-F]{32}$') { throw 'Invalid expected size or MD5' }
                $file=[ordered]@{name=$name;url=$url.AbsoluteUri;size=[long]$f.size;md5=$f.md5;probeStatus=$null;available=$null;localPath=$null;verified=$false;error=$null}
                if (!$SkipProbe) {
                    # A one-byte Range request also works when the CDN does not implement HEAD.
                    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,(Sign-Url $url))
                    $request.Headers.Range=[Net.Http.Headers.RangeHeaderValue]::new(0,0)
                    try {
                        $response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                        try {
                            $file.probeStatus=[int]$response.StatusCode
                            $file.available=$response.IsSuccessStatusCode
                            if ($response.StatusCode -eq [Net.HttpStatusCode]::PartialContent -and $null -ne $response.Content.Headers.ContentRange) {
                                $file.available=$file.available -and ($response.Content.Headers.ContentRange.Length -eq $file.size)
                            }
                        } finally { $response.Dispose() }
                    } catch { $file.error=$_.Exception.Message } finally { $request.Dispose() }
                }
                if ($DownloadLatest -and $package.isLatest) {
                    $folder=Join-Path $root $p.name
                    New-Item -ItemType Directory -Force $folder | Out-Null
                    $target=Join-Path $folder $name
                    $file.localPath=[IO.Path]::GetRelativePath((Split-Path -Parent $PSScriptRoot), $target)
                    if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $file.size) {
                        $file.verified=(Get-FileHash -LiteralPath $target -Algorithm MD5).Hash -eq $file.md5
                    }
                    if (!$file.verified) {
                        $partial=$target+'.partial'
                        $response=$client.GetAsync((Sign-Url $url),[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                        try {
                            [void]$response.EnsureSuccessStatusCode()
                            $inputStream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                            $outputStream=[IO.File]::Open($partial,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
                            try { $inputStream.CopyTo($outputStream,131072) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
                        } finally { $response.Dispose() }
                        if ((Get-Item -LiteralPath $partial).Length -ne $file.size -or (Get-FileHash -LiteralPath $partial -Algorithm MD5).Hash -ne $file.md5) { throw "Size/MD5 mismatch: $name" }
                        Move-Item -LiteralPath $partial -Destination $target -Force
                        $file.verified=$true
                    }
                    Write-Host "Verified latest ZIP: $target"
                }
                $package.files+=,$file
            }
        } catch { $package.error=$_.Exception.Message }
        $packages.Add($package)
        Write-Host ("Catalogued "+$p.name)
    }
    $report=[ordered]@{checkedAt=[DateTimeOffset]::Now.ToString('o');versionUrl=$versionUrl;target=$area.must;minimumSupported=$area.min;probe='GET Range bytes=0-0; HTTP reachability, not full payload integrity';packages=$packages.ToArray()}
    $output=Join-Path $root 'catalog.json'
    [IO.File]::WriteAllText($output,($report | ConvertTo-Json -Depth 12))
    $packages | ForEach-Object { [pscustomobject]@{From=$_.from;To=$_.to;Version=$_.versionView;Files=$_.files.Count;MiB=[Math]::Round((@($_.files | ForEach-Object { $_.size }) | Measure-Object -Sum).Sum/1MB,2);Available=@($_.files | Where-Object available).Count;Error=$_.error} } | Format-Table
    Write-Host "Catalog: $output"
    if ($DownloadLatest -and @($packages | Where-Object isLatest).Count -eq 0) { throw 'No package targets area.must' }
    if ($DownloadLatest -and @($packages | Where-Object { $_.isLatest -and ($_.error -or $_.files.Count -eq 0 -or @($_.files | Where-Object { !$_.verified }).Count -gt 0) }).Count -gt 0) { throw 'Latest package download incomplete; see catalog.json' }
} finally { $client.Dispose() }
