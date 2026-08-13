param([switch]$Silent)

$ErrorActionPreference = 'Stop'
$extractRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\CodexUsageWidget'
$localDotnet = Join-Path $installRoot 'runtime\dotnet.exe'
$setupDll = Join-Path $extractRoot 'CodexUsageWidgetSetup.dll'

function Test-WindowsDesktopRuntime([string]$DotnetPath) {
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { return $false }
    try {
        $runtimes = & $DotnetPath --list-runtimes 2>$null
        return [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 8\.' } | Select-Object -First 1)
    } catch { return $false }
}

function Show-InstallError([string]$Message) {
    if ($Silent) { return }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($Message, 'Codex Usage Widget', 'OK', 'Error') | Out-Null
}

try {
    if (-not $Silent) {
        Add-Type -AssemblyName System.Windows.Forms
        $answer = [System.Windows.Forms.MessageBox]::Show(
            "Install Codex Usage Widget for the current Windows user?`r`n`r`nThe installer may download Microsoft .NET 8 Desktop Runtime when it is not already available. No administrator permission is required.",
            'Codex Usage Widget',
            'YesNo',
            'Information')
        if ($answer -ne 'Yes') { exit 2 }
    }

    $runner = $null
    $dotnetCandidates = @()
    $systemDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($systemDotnet) { $dotnetCandidates += $systemDotnet.Source }
    if ($env:ProgramFiles) { $dotnetCandidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if (${env:ProgramFiles(x86)}) { $dotnetCandidates += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    $systemRunner = $dotnetCandidates | Where-Object { Test-WindowsDesktopRuntime $_ } | Select-Object -First 1
    if ($systemRunner) {
        $runner = $systemRunner
    } elseif (Test-WindowsDesktopRuntime $localDotnet) {
        $runner = $localDotnet
    } else {
        $runtimeRoot = Split-Path -Parent $localDotnet
        New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
        $downloadRoot = Join-Path $env:TEMP ('CodexUsageWidget-Runtime-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $downloadRoot | Out-Null
        $installScript = Join-Path $downloadRoot 'dotnet-install.ps1'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -Channel 8.0 -Runtime windowsdesktop -Architecture x64 -InstallDir $runtimeRoot -NoPath
        if ($LASTEXITCODE -ne 0 -or -not (Test-WindowsDesktopRuntime $localDotnet)) {
            throw 'Microsoft .NET 8 Desktop Runtime installation failed.'
        }
        $runner = $localDotnet
        try { [System.IO.Directory]::Delete($downloadRoot, $true) } catch { }
    }

    $arguments = @($setupDll, '--silent')
    $process = Start-Process -FilePath $runner -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw 'The installer did not finish within 30 seconds.' }
    exit $process.ExitCode
} catch {
    Show-InstallError ("Installation failed.`r`n`r`n" + $_.Exception.Message)
    exit 1
}
