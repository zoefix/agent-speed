<#
.SYNOPSIS
  Builds self-contained single-file Agent Speed for Windows (x64 and ARM64).
.DESCRIPTION
  Output: dist\AgentSpeed-Windows-x64.zip and dist\AgentSpeed-Windows-arm64.zip, each containing AgentSpeed.exe, which
  embeds both remote relays agent-speed-relay-linux-x86_64 and -aarch64 (spec/REMOTE.md 3.1).
  The traffic helper (spec/WINDOWS-SERVICE.md) is published first with NativeAOT for each architecture and embedded in
  AgentSpeed.exe; so is the Claude Code status line helper agent-speed-statusline.exe (spec/QUOTAS.md 5). This needs the
  Visual Studio C++ build tools including the ARM64 tools. AGENT_SPEED_NO_HELPER=1 builds without both (per-process
  traffic and the Claude Code quota bridge are then unavailable in that build).
.PARAMETER Version
  Version to stamp (defaults to the repository VERSION file, then 1.0.0).
.PARAMETER RelayDir
  Folder with agent-speed-relay-linux-x86_64 and agent-speed-relay-linux-aarch64 of that version (defaults to
  RELAY_DIR, then linux\relay\dist, built by linux/relay/build-static.sh).
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $RelayDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $root 'windows\src\AgentSpeed\AgentSpeed.csproj'
$helperProject = Join-Path $root 'windows\src\AgentSpeed.TrafficHelper\AgentSpeed.TrafficHelper.csproj'
$statuslineProject = Join-Path $root 'windows\src\AgentSpeed.StatuslineHelper\AgentSpeed.StatuslineHelper.csproj'
$dist = Join-Path $root 'dist'

if (-not $Version) { $Version = $env:AGENT_SPEED_VERSION }
if (-not $Version) {
    $versionFile = Join-Path $root 'VERSION'
    if (Test-Path $versionFile) { $Version = (Get-Content -Raw $versionFile).Trim() }
}
if (-not $Version) { $Version = '1.0.0' }
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$') { throw "Invalid version '$Version'" }
$fileVersion = ($Version -split '[-+]')[0]

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK (dotnet) is required.' }

if (-not $RelayDir) { $RelayDir = $env:RELAY_DIR }
if (-not $RelayDir) { $RelayDir = Join-Path $root 'linux\relay\dist' }
$latin1 = [Text.Encoding]::GetEncoding(28591)
$versionPattern = '(?<![\x20-\x7e])' + [regex]::Escape($Version) + '(?![\x20-\x7e])'
foreach ($arch in @('x86_64', 'aarch64')) {
    $relay = Join-Path $RelayDir "agent-speed-relay-linux-$arch"
    if (-not (Test-Path -LiteralPath $relay -PathType Leaf)) {
        throw "Missing remote relay $relay. Build it with linux/relay/build-static.sh $arch, or pass -RelayDir."
    }
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $relay).Path)
    if ($bytes.Length -lt 4 -or $bytes[0] -ne 0x7f -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4c -or $bytes[3] -ne 0x46) {
        throw "$relay is not a Linux executable."
    }
    if ($latin1.GetString($bytes) -notmatch $versionPattern) {
        throw "$relay was not built from version $Version. Rebuild it with linux/relay/build-static.sh $arch."
    }
}
$RelayDir = (Resolve-Path -LiteralPath $RelayDir).Path
Write-Host "==> Embedding the remote relays $Version from $RelayDir"

# Uncompressed single-file starts ~600 ms faster and uses about half the private memory (no in-memory
# decompression); the zip is ~60 MB either way. AGENT_SPEED_COMPRESS=1 shrinks the exe to ~65 MB instead of ~150 MB.
$compress = if ($env:AGENT_SPEED_COMPRESS -eq '1') { 'true' } else { 'false' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$work = Join-Path ([IO.Path]::GetTempPath()) ('agentspeed-publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null

try {
    $helperArgs = @()
    if ($env:AGENT_SPEED_NO_HELPER -eq '1') {
        Write-Warning 'AGENT_SPEED_NO_HELPER=1: building without the traffic and status line helpers (no per-process traffic, no Claude Code quota bridge).'
    }
    else {
        # WINDOWS-SERVICE 2: the helper keeps its own version (HelperProtocol.BundledVersion), so no app version is stamped.
        $helperRoot = Join-Path $work 'helper'
        foreach ($arch in @('x64', 'arm64')) {
            $rid = "win-$arch"
            $helperOut = Join-Path $helperRoot $rid
            Write-Host "==> Publishing the traffic helper (NativeAOT) for $rid"
            & dotnet publish $helperProject `
                -c Release `
                -r $rid `
                --artifacts-path (Join-Path $work 'helper-artifacts') `
                -o $helperOut `
                -p:DebugType=none `
                -p:DebugSymbols=false `
                -p:ContinuousIntegrationBuild=true `
                -nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the traffic helper failed for $rid" }

            $helperExe = Join-Path $helperOut 'AgentSpeedTrafficHelper.exe'
            if (-not (Test-Path $helperExe)) { throw "$helperExe was not produced" }
            $helperHash = (Get-FileHash -Path $helperExe -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-Host ("    AgentSpeedTrafficHelper.exe: {0:N0} KiB, SHA-256 {1}" -f ((Get-Item $helperExe).Length / 1KB), $helperHash)
        }
        # QUOTAS 5.1: the status line helper, NativeAOT as well, embedded the same way.
        $statuslineRoot = Join-Path $work 'statusline'
        foreach ($arch in @('x64', 'arm64')) {
            $rid = "win-$arch"
            $statuslineOut = Join-Path $statuslineRoot $rid
            Write-Host "==> Publishing the status line helper (NativeAOT) for $rid"
            & dotnet publish $statuslineProject `
                -c Release `
                -r $rid `
                --artifacts-path (Join-Path $work 'statusline-artifacts') `
                -o $statuslineOut `
                -p:DebugType=none `
                -p:DebugSymbols=false `
                -p:ContinuousIntegrationBuild=true `
                -nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the status line helper failed for $rid" }

            $statuslineExe = Join-Path $statuslineOut 'agent-speed-statusline.exe'
            if (-not (Test-Path $statuslineExe)) { throw "$statuslineExe was not produced" }
            Write-Host ("    agent-speed-statusline.exe: {0:N0} KiB" -f ((Get-Item $statuslineExe).Length / 1KB))
        }
        $helperArgs = @("-p:TrafficHelperDir=$helperRoot", "-p:StatuslineHelperDir=$statuslineRoot")
    }

    foreach ($arch in @('x64', 'arm64')) {
        $rid = "win-$arch"
        $out = Join-Path $work $rid
        Write-Host "==> Publishing Agent Speed $Version for $rid"
        & dotnet publish $project `
            -c Release `
            -r $rid `
            --self-contained true `
            --artifacts-path (Join-Path $work 'artifacts') `
            -o $out `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            "-p:EnableCompressionInSingleFile=$compress" `
            -p:PublishTrimmed=false `
            -p:PublishReadyToRun=true `
            -p:DebugType=none `
            -p:DebugSymbols=false `
            -p:GenerateDocumentationFile=false `
            "-p:Version=$fileVersion" `
            "-p:InformationalVersion=$Version" `
            -p:ContinuousIntegrationBuild=true `
            "-p:RelayDir=$RelayDir" `
            @helperArgs `
            -nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

        $exe = Join-Path $out 'AgentSpeed.exe'
        if (-not (Test-Path $exe)) { throw "$exe was not produced" }

        $zip = Join-Path $dist "AgentSpeed-Windows-$arch.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path $exe -DestinationPath $zip -CompressionLevel Optimal
        $size = (Get-Item $exe).Length
        Write-Host ("    AgentSpeed.exe: {0:N1} MiB -> {1}" -f ($size / 1MB), $zip)
    }
    Write-Host '==> Done'
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
