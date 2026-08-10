#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads and extracts the WebView2 Fixed Version runtime (x64) for bundling with Athlon Agent.

.DESCRIPTION
    Reads the pinned version from tools/webview2-runtime.version, resolves the Microsoft CDN URL
    (from tools/webview2-runtime.download-url or the official WebView2 download page), downloads
    the CAB, expands it, and copies binaries to src/Athlon.Agent.App/runtimes/webview2/x64/.
#>
[CmdletBinding()]
param(
    [string]$VersionFile = (Join-Path $PSScriptRoot 'webview2-runtime.version'),
    [string]$DownloadUrlFile = (Join-Path $PSScriptRoot 'webview2-runtime.download-url'),
    [string]$DestinationRoot = (Join-Path $PSScriptRoot '..\src\Athlon.Agent.App\runtimes\webview2'),
    [string]$Version,
    [string]$DownloadUrl,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PinnedVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Version file not found: $Path"
    }

    $value = (Get-Content -LiteralPath $Path -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Version file is empty: $Path"
    }

    return $value
}

function Normalize-EscapedWebContent {
    param([string]$Content)
    return $Content.Replace('\u002F', '/').Replace('\/', '/')
}

function Resolve-DownloadUrl {
    param(
        [string]$RuntimeVersion,
        [string]$UrlOverride,
        [string]$UrlFilePath
    )

    if (-not [string]::IsNullOrWhiteSpace($UrlOverride)) {
        return $UrlOverride.Trim()
    }

    if (Test-Path -LiteralPath $UrlFilePath) {
        $fromFile = (Get-Content -LiteralPath $UrlFilePath -Raw).Trim()
        if (-not [string]::IsNullOrWhiteSpace($fromFile)) {
            return $fromFile
        }
    }

    $cabName = "Microsoft.WebView2.FixedVersionRuntime.$RuntimeVersion.x64.cab"
    $pageUrl = 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/'
    Write-Host "Resolving download URL from $pageUrl ..."
    $html = Normalize-EscapedWebContent (Invoke-WebRequest -Uri $pageUrl -UseBasicParsing -TimeoutSec 120).Content
    $escapedCabName = [Regex]::Escape($cabName)
    $match = [Regex]::Match(
        $html,
        "https://msedge\.sf\.dl\.delivery\.mp\.microsoft\.com/filestreamingservice/files/[0-9a-f-]+/$escapedCabName",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        $available = [Regex]::Matches(
            $html,
            'Microsoft\.WebView2\.FixedVersionRuntime\.([0-9.]+)\.x64\.cab') |
            ForEach-Object { $_.Groups[1].Value } |
            Select-Object -Unique
        $availableText = if ($available.Count -gt 0) { ($available -join ', ') } else { '(none found on page)' }
        throw @"
Could not resolve CDN URL for $cabName.
Versions currently listed for x64 on the WebView2 download page: $availableText
Update tools/webview2-runtime.version, set tools/webview2-runtime.download-url, or pass -DownloadUrl.
"@
    }

    return $match.Value
}

function Find-MsEdgeWebView2Folder {
    param([string]$Root)
    $exe = Get-ChildItem -LiteralPath $Root -Recurse -Filter 'msedgewebview2.exe' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $exe) {
        throw "msedgewebview2.exe not found under $Root"
    }

    return $exe.Directory.FullName
}

function Copy-RuntimeTree {
    param(
        [string]$SourceFolder,
        [string]$TargetFolder
    )

    if (-not (Test-Path -LiteralPath $SourceFolder)) {
        throw "Source runtime folder not found: $SourceFolder"
    }

    $items = @(Get-ChildItem -LiteralPath $SourceFolder -Force)
    if ($items.Count -eq 0) {
        throw "Source runtime folder is empty: $SourceFolder"
    }

    if (Test-Path -LiteralPath $TargetFolder) {
        Remove-Item -LiteralPath $TargetFolder -Recurse -Force
    }

    New-Item -ItemType Directory -Path $TargetFolder -Force | Out-Null
    # -LiteralPath does not expand wildcards; use -Path for glob copy.
    Copy-Item -Path (Join-Path $SourceFolder '*') -Destination $TargetFolder -Recurse -Force
}

function Test-DownloadedCab {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $length = (Get-Item -LiteralPath $Path).Length
    # Fixed runtime CABs are typically well over 50 MB; reject truncated downloads.
    return $length -ge 50MB
}

function Invoke-CurlDownload {
    param(
        [string]$Uri,
        [string]$DestinationPath
    )

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -eq $curl) {
        throw 'curl.exe is not available.'
    }

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    & curl.exe `
        --fail `
        --location `
        --retry 5 `
        --retry-all-errors `
        --retry-delay 5 `
        --connect-timeout 30 `
        --max-time 7200 `
        --output $DestinationPath `
        $Uri
    if ($LASTEXITCODE -ne 0) {
        throw "curl.exe failed with exit code $LASTEXITCODE"
    }
}

function Invoke-BitsDownload {
    param(
        [string]$Uri,
        [string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    Start-BitsTransfer `
        -Source $Uri `
        -Destination $DestinationPath `
        -TransferType Download `
        -Priority Foreground `
        -ErrorAction Stop
}

function Invoke-HttpClientDownload {
    param(
        [string]$Uri,
        [string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(120)
    try {
        $response = $client.GetAsync(
            $Uri,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode()
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $fileStream = [System.IO.File]::Open(
            $DestinationPath,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $stream.CopyTo($fileStream)
        }
        finally {
            $fileStream.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Download-FileRobust {
    param(
        [string]$Uri,
        [string]$DestinationPath,
        [int]$MaxAttempts = 5
    )

    $parent = Split-Path -Parent $DestinationPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $strategies = @(
        @{ Name = 'curl'; Action = { Invoke-CurlDownload -Uri $Uri -DestinationPath $DestinationPath } },
        @{ Name = 'bits'; Action = { Invoke-BitsDownload -Uri $Uri -DestinationPath $DestinationPath } },
        @{ Name = 'httpclient'; Action = { Invoke-HttpClientDownload -Uri $Uri -DestinationPath $DestinationPath } }
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        foreach ($strategy in $strategies) {
            try {
                Write-Host "Downloading via $($strategy.Name) (attempt $attempt/$MaxAttempts) ..."
                & $strategy.Action
                if (Test-DownloadedCab -Path $DestinationPath) {
                    return
                }

                throw "Downloaded CAB is missing or too small."
            }
            catch {
                $lastError = $_
                Write-Warning "$($strategy.Name) download failed: $($_.Exception.Message)"
                if (Test-Path -LiteralPath $DestinationPath) {
                    Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
                }
            }
        }

        if ($attempt -lt $MaxAttempts) {
            $delay = [Math]::Min(60, [Math]::Pow(2, $attempt))
            Write-Host "Waiting ${delay}s before retry ..."
            Start-Sleep -Seconds $delay
        }
    }

    throw "Failed to download WebView2 CAB after $MaxAttempts attempts. Last error: $lastError"
}

$runtimeVersion = if ([string]::IsNullOrWhiteSpace($Version)) { Get-PinnedVersion -Path $VersionFile } else { $Version.Trim() }
$targetFolder = Join-Path $DestinationRoot 'x64'
$versionMarker = Join-Path $DestinationRoot 'VERSION'
$expectedExe = Join-Path $targetFolder 'msedgewebview2.exe'

if (-not $Force -and (Test-Path -LiteralPath $expectedExe) -and (Test-Path -LiteralPath $versionMarker)) {
    $installedVersion = (Get-Content -LiteralPath $versionMarker -Raw).Trim()
    if ($installedVersion -eq $runtimeVersion) {
        Write-Host "WebView2 Fixed Runtime $runtimeVersion already present at $targetFolder"
        exit 0
    }
}

$resolvedUrl = Resolve-DownloadUrl -RuntimeVersion $runtimeVersion -UrlOverride $DownloadUrl -UrlFilePath $DownloadUrlFile
Write-Host "Downloading WebView2 Fixed Runtime $runtimeVersion ..."
Write-Host "URL: $resolvedUrl"

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("athlon-webview2-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$cabPath = Join-Path $tempRoot "Microsoft.WebView2.FixedVersionRuntime.$runtimeVersion.x64.cab"
$extractRoot = Join-Path $tempRoot 'extracted'

try {
    Download-FileRobust -Uri $resolvedUrl -DestinationPath $cabPath
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

    $expand = Join-Path $env:SystemRoot 'System32\expand.exe'
    if (-not (Test-Path -LiteralPath $expand)) {
        throw "expand.exe not found. This script must run on Windows."
    }

    Write-Host "Expanding CAB to $extractRoot ..."
    & $expand -F:* $cabPath $extractRoot | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "expand.exe failed with exit code $LASTEXITCODE"
    }

    $runtimeFolder = Find-MsEdgeWebView2Folder -Root $extractRoot
    Write-Host "Copying runtime from $runtimeFolder to $targetFolder ..."
    Copy-RuntimeTree -SourceFolder $runtimeFolder -TargetFolder $targetFolder

    if (-not (Test-Path -LiteralPath $expectedExe)) {
        throw "Expected executable missing after copy: $expectedExe"
    }

    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    Set-Content -LiteralPath $versionMarker -Value $runtimeVersion -NoNewline
    Write-Host "WebView2 Fixed Runtime $runtimeVersion installed to $targetFolder"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
