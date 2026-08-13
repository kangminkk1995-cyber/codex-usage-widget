$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))
$staging = [System.IO.Path]::GetFullPath((Join-Path $artifacts 'installer-staging'))
$widgetPublish = [System.IO.Path]::GetFullPath((Join-Path $artifacts 'widget'))
$setupPublish = [System.IO.Path]::GetFullPath((Join-Path $artifacts 'setup'))
$bundle = Join-Path ([System.IO.Path]::GetTempPath()) 'CodexUsageWidget-IExpress-Bundle'
$payload = [System.IO.Path]::GetFullPath((Join-Path $artifacts 'payload.zip'))
$finalSetup = [System.IO.Path]::GetFullPath((Join-Path $artifacts 'CodexUsageWidgetSetup.exe'))
$temporarySetup = Join-Path ([System.IO.Path]::GetTempPath()) 'CodexUsageWidgetSetup.exe'
$sedPath = Join-Path ([System.IO.Path]::GetTempPath()) 'CodexUsageWidgetSetup.sed'

function Assert-WorkspacePath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $workspace.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the workspace: $full"
    }
}

function Reset-Directory([string]$Path) {
    Assert-WorkspacePath $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Reset-TemporaryDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a non-temporary path: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Path $full | Out-Null
}

Assert-WorkspacePath $artifacts
Reset-Directory $staging
Reset-Directory $widgetPublish
Reset-Directory $setupPublish
Reset-TemporaryDirectory $bundle
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
if (Test-Path -LiteralPath $finalSetup) { Remove-Item -LiteralPath $finalSetup -Force }
if (Test-Path -LiteralPath $temporarySetup) { Remove-Item -LiteralPath $temporarySetup -Force }
if (Test-Path -LiteralPath $sedPath) { Remove-Item -LiteralPath $sedPath -Force }

$dotnetCliHome = Join-Path $workspace '.dotnet-home'
$env:DOTNET_CLI_HOME = $dotnetCliHome
dotnet publish (Join-Path $workspace 'src\CodexUsageWidget\CodexUsageWidget.csproj') -c Release -r win-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -o $widgetPublish
if ($LASTEXITCODE -ne 0) { throw 'Widget publish failed.' }

Get-ChildItem -LiteralPath $widgetPublish -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $staging $_.Name) }
Copy-Item -LiteralPath (Join-Path $workspace 'THIRD_PARTY_NOTICES.txt') -Destination (Join-Path $staging 'OPENAI_CODEX_LICENSE.txt')
$payloadFiles = Get-ChildItem -LiteralPath $staging -File | ForEach-Object { $_.FullName }
Compress-Archive -LiteralPath $payloadFiles -DestinationPath $payload -CompressionLevel Optimal

dotnet publish (Join-Path $workspace 'src\CodexUsageWidget.Installer\CodexUsageWidget.Installer.csproj') -c Release -r win-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -p:PayloadZip=$payload -o $setupPublish
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }

Get-ChildItem -LiteralPath $setupPublish -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $bundle $_.Name) }
Copy-Item -LiteralPath (Join-Path $workspace 'installer\bootstrap.ps1') -Destination (Join-Path $bundle 'bootstrap.ps1')

$bundleFiles = Get-ChildItem -LiteralPath $bundle -File | Sort-Object Name
$sourceEntries = New-Object System.Collections.Generic.List[string]
$stringEntries = New-Object System.Collections.Generic.List[string]
for ($index = 0; $index -lt $bundleFiles.Count; $index++) {
    $sourceEntries.Add("%FILE$index%=")
    $stringEntries.Add("FILE$index=`"$($bundleFiles[$index].Name)`"")
}
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$temporarySetup
FriendlyName=Codex Usage Widget Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File bootstrap.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File bootstrap.ps1 -Silent
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$bundle\
[SourceFiles0]
$($sourceEntries -join "`r`n")
[Strings]
$($stringEntries -join "`r`n")
"@
[System.IO.File]::WriteAllText($sedPath, $sed, [System.Text.Encoding]::ASCII)
& "$env:WINDIR\System32\iexpress.exe" /N $sedPath | Out-Null
for ($attempt = 0; $attempt -lt 100; $attempt++) {
    if (Test-Path -LiteralPath $temporarySetup) {
        $size = (Get-Item -LiteralPath $temporarySetup).Length
        Start-Sleep -Milliseconds 100
        if ((Test-Path -LiteralPath $temporarySetup) -and (Get-Item -LiteralPath $temporarySetup).Length -eq $size) { break }
    }
    Start-Sleep -Milliseconds 100
}
if (-not (Test-Path -LiteralPath $temporarySetup)) { throw 'IExpress packaging failed.' }
Copy-Item -LiteralPath $temporarySetup -Destination $finalSetup
Get-Item -LiteralPath $finalSetup | Select-Object FullName, Length, LastWriteTime
