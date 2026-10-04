# ForestCraft - shared helpers for install / build / reset (Windows PowerShell 5.1+).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is 10x slower with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$FC = @{
    McVersion      = '26.2'
    LoaderVersion  = '0.19.3'
    BepInExVersion = '5.4.23.2'
    InstanceId     = 'ForestCraft'
    LinkDir        = Join-Path $env:LOCALAPPDATA 'ForestCraft'
}
$FC.BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$($FC.BepInExVersion)/BepInEx_win_x64_$($FC.BepInExVersion).zip"
$FC.JdkUrl     = 'https://api.adoptium.net/v3/binary/latest/25/ga/windows/x64/jdk/hotspot/normal/eclipse'

function Step($text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "  OK  $text" -ForegroundColor Green }
function Info($text) { Write-Host "      $text" }
function Fail($text) { Write-Host ''; Write-Host "ECHEC : $text" -ForegroundColor Red; exit 1 }

function Save-Download($url, $file) {
    Info "Telechargement de $url"
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
}

# ---------------------------------------------------------------- The Forest
function Get-SteamLibraries {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($n in 'SteamPath', 'InstallPath') { if ($p.$n) { $roots += ($p.$n -replace '/', '\') } }
        } catch { }
    }
    if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} 'Steam') }
    $libs = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path $r)) { continue }
        $libs += $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    return $libs | Select-Object -Unique
}

function Test-ForestDir($dir) { return $dir -and (Test-Path (Join-Path $dir 'TheForest.exe')) }

function Find-Forest {
    if (Test-ForestDir $env:FORESTCRAFT_FOREST) { return $env:FORESTCRAFT_FOREST }
    $saved = Join-Path $FC.LinkDir 'forest.txt'
    if (Test-Path $saved) {
        $d = (Get-Content $saved | Select-Object -First 1)
        if (Test-ForestDir $d) { return $d }
    }
    foreach ($lib in Get-SteamLibraries) {
        $d = Join-Path $lib 'steamapps\common\The Forest'
        if (Test-ForestDir $d) { return $d }
    }
    Info "The Forest introuvable dans Steam. Choisis son dossier (celui qui contient TheForest.exe)."
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Dossier de The Forest (celui qui contient TheForest.exe)'
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-ForestDir $dlg.SelectedPath)) {
        return $dlg.SelectedPath
    }
    return $null
}

function Install-BepInEx($forest) {
    if (Test-Path (Join-Path $forest 'BepInEx\core\BepInEx.dll')) { Ok "BepInEx deja present"; return }
    $zip = Join-Path $env:TEMP "BepInEx_$($FC.BepInExVersion).zip"
    Save-Download $FC.BepInExUrl $zip
    Expand-Archive -Path $zip -DestinationPath $forest -Force
    Remove-Item $zip -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $forest 'BepInEx\core\BepInEx.dll'))) { Fail "BepInEx n'a pas pu etre installe dans $forest" }
    Ok "BepInEx $($FC.BepInExVersion) installe"
}

# ---------------------------------------------------------------- Prism Launcher
function Find-PrismExe {
    $c = @()
    $cfg = Join-Path $FC.LinkDir 'launcher.txt'
    if (Test-Path $cfg) { $c += (Get-Content $cfg | Select-Object -First 1) }
    $c += (Join-Path $env:LOCALAPPDATA 'Programs\PrismLauncher\prismlauncher.exe')
    if ($env:ProgramFiles) { $c += (Join-Path $env:ProgramFiles 'PrismLauncher\prismlauncher.exe') }
    $c += (Join-Path $env:USERPROFILE 'scoop\apps\prismlauncher\current\prismlauncher.exe')
    $cmd = Get-Command 'prismlauncher.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $c += $cmd.Source }
    foreach ($p in $c) { if ($p -and (Test-Path $p)) { return (Resolve-Path $p).Path } }
    return $null
}

function Get-PrismDataDir($exe) {
    $dir = Split-Path $exe
    if (Test-Path (Join-Path $dir 'portable.txt')) { return $dir }
    return (Join-Path $env:APPDATA 'PrismLauncher')
}

function Get-PrismInstancesDir($data) {
    $dir = 'instances'
    $cfg = Join-Path $data 'prismlauncher.cfg'
    if (Test-Path $cfg) {
        $line = Get-Content $cfg | Where-Object { $_ -match '^InstanceDir=' } | Select-Object -First 1
        if ($line) { $dir = $line.Substring('InstanceDir='.Length).Trim() }
    }
    if ([IO.Path]::IsPathRooted($dir)) { return $dir }
    return (Join-Path $data $dir)
}

# Prism uses ".minecraft" when it exists, otherwise "minecraft".
function Get-InstanceGameDir($inst) {
    $dot = Join-Path $inst '.minecraft'
    if (Test-Path $dot) { return $dot }
    return (Join-Path $inst 'minecraft')
}

# Where Forest's watchdog finds Prism (line 1 = exe, line 2 = instance id).
function Save-LauncherInfo($exe, $instanceId) {
    New-Item -ItemType Directory -Force -Path $FC.LinkDir | Out-Null
    Set-Content -Path (Join-Path $FC.LinkDir 'launcher.txt') -Value @($exe, $instanceId) -Encoding ASCII
}

function Find-ForestCraftInstance {
    $exe = Find-PrismExe
    if (-not $exe) { return $null }
    $id = $FC.InstanceId
    $cfg = Join-Path $FC.LinkDir 'launcher.txt'
    if (Test-Path $cfg) {
        $lines = @(Get-Content $cfg)
        if ($lines.Count -gt 1 -and $lines[1].Trim()) { $id = $lines[1].Trim() }
    }
    return (Join-Path (Get-PrismInstancesDir (Get-PrismDataDir $exe)) $id)
}

# ---------------------------------------------------------------- JDK 25 (build only)
function Get-JavaMajor($jdkHome) {
    $java = Join-Path $jdkHome 'bin\java.exe'
    if (-not (Test-Path (Join-Path $jdkHome 'bin\javac.exe'))) { return 0 }
    $out = cmd /c "`"$java`" -version 2>&1" | Out-String
    if ($out -match 'version "(\d+)') { return [int]$Matches[1] }
    return 0
}

function Find-Jdk25($repo) {
    $c = @()
    if ($env:JAVA_HOME) { $c += $env:JAVA_HOME }
    $tools = Join-Path $repo '.tools\jdk-25'
    $c += $tools
    foreach ($base in @("$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Java", "$env:ProgramFiles\Microsoft", "$env:ProgramFiles\Zulu")) {
        if (Test-Path $base) { $c += (Get-ChildItem $base -Directory | ForEach-Object { $_.FullName }) }
    }
    foreach ($h in $c) { if ($h -and (Test-Path $h) -and (Get-JavaMajor $h) -ge 25) { return $h } }

    Info "Aucun JDK 25 trouve : telechargement de Temurin 25 dans .tools\jdk-25"
    $zip = Join-Path $env:TEMP 'forestcraft-jdk25.zip'
    $tmp = Join-Path $env:TEMP 'forestcraft-jdk25'
    Save-Download $FC.JdkUrl $zip
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $tools) { Remove-Item $tools -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $tools) | Out-Null
    Move-Item $inner.FullName $tools
    Remove-Item $zip, $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ((Get-JavaMajor $tools) -lt 25) { Fail "le JDK telecharge ne fonctionne pas" }
    return $tools
}
