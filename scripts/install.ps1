# ForestCraft - one-click installer.
# From a release zip: installs the prebuilt files in mods\.
# From the source tree: builds first (scripts\build.ps1), then installs dist\ForestCraft\mods.
param([switch]$NoBuild)
. "$PSScriptRoot\common.ps1"
$root = Split-Path $PSScriptRoot

Write-Host ''
Write-Host '  ForestCraft - installation' -ForegroundColor Yellow
Write-Host '  The Forest + Minecraft 26.2 (Fabric), via BepInEx et Prism Launcher'

if (Get-Process -Name 'TheForest' -ErrorAction SilentlyContinue) { Fail 'The Forest tourne encore. Ferme-le puis relance.' }

$mods = Join-Path $root 'mods'
if (Test-Path (Join-Path $root 'forest\ForestCraft.csproj')) {
    if (-not $NoBuild) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1')
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }
    $mods = Join-Path $root 'dist\ForestCraft\mods'
}
$dll = Join-Path $mods 'ForestCraft.dll'
$jar = Join-Path $mods 'forestcraft.jar'
if (-not (Test-Path $dll) -or -not (Test-Path $jar)) {
    Fail "fichiers du mod introuvables dans $mods. Telecharge le zip depuis la page Releases du projet GitHub."
}

# ------------------------------------------------------------ The Forest + BepInEx
Step 'The Forest'
$forest = Find-Forest
if (-not $forest) { Fail 'dossier de The Forest introuvable' }
Ok $forest
New-Item -ItemType Directory -Force -Path $FC.LinkDir | Out-Null
Set-Content -Path (Join-Path $FC.LinkDir 'forest.txt') -Value $forest -Encoding ASCII
Install-BepInEx $forest
$plugins = Join-Path $forest 'BepInEx\plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item $dll (Join-Path $plugins 'ForestCraft.dll') -Force
Ok 'plugin ForestCraft.dll installe'

# ------------------------------------------------------------ Prism Launcher
Step 'Prism Launcher'
$prism = Find-PrismExe
$freshPrism = $false
if (-not $prism) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Info 'Prism Launcher absent : installation avec winget...'
        & winget install --id PrismLauncher.PrismLauncher -e --silent --accept-package-agreements --accept-source-agreements
        $prism = Find-PrismExe
    }
    if (-not $prism) {
        Info 'Installe Prism Launcher depuis la page qui vient de s''ouvrir, puis reviens ici.'
        Start-Process 'https://prismlauncher.org/download/windows/'
        Read-Host '      Appuie sur Entree quand Prism est installe'
        $prism = Find-PrismExe
    }
    if (-not $prism) { Fail 'Prism Launcher introuvable' }
    $freshPrism = $true
}
Ok $prism
$data = Get-PrismDataDir $prism
$instances = Get-PrismInstancesDir $data
$inst = Join-Path $instances $FC.InstanceId
if (-not (Test-Path (Join-Path $inst 'instance.cfg'))) {
    New-Item -ItemType Directory -Force -Path $inst | Out-Null
    $cfg = @(
        '[General]', 'ConfigVersion=1.3', 'InstanceType=OneSix', 'iconKey=default', "name=$($FC.InstanceId)",
        'AutomaticJava=true', 'OverrideMemory=true', 'MinMemAlloc=512', 'MaxMemAlloc=3072'
    )
    Set-Content -Path (Join-Path $inst 'instance.cfg') -Value $cfg -Encoding ASCII
    $pack = @"
{
    "components": [
        { "important": true, "uid": "net.minecraft", "version": "$($FC.McVersion)" },
        { "uid": "net.fabricmc.fabric-loader", "version": "$($FC.LoaderVersion)" }
    ],
    "formatVersion": 1
}
"@
    Set-Content -Path (Join-Path $inst 'mmc-pack.json') -Value $pack -Encoding ASCII
    Ok "instance Prism '$($FC.InstanceId)' creee (Minecraft $($FC.McVersion) + Fabric $($FC.LoaderVersion))"
} else {
    Ok "instance Prism '$($FC.InstanceId)' deja presente"
}
$modsDir = Join-Path (Get-InstanceGameDir $inst) 'mods'
New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
Get-ChildItem $modsDir -Filter 'forestcraft*.jar' | Remove-Item -Force
Copy-Item $jar (Join-Path $modsDir 'forestcraft.jar') -Force
Ok 'mod forestcraft.jar installe'
# FerriteCore: less memory for Minecraft, nothing else changes (The Forest runs at the same time).
Install-ModrinthMod 'ferrite-core' 'ferritecore' $modsDir
Save-LauncherInfo $prism $FC.InstanceId

# ------------------------------------------------------------ done
Write-Host ''
Write-Host '  Installation terminee.' -ForegroundColor Green
Write-Host '  Lance The Forest depuis Steam : Minecraft demarre tout seul en arriere-plan.'
Write-Host ''
Write-Host '  Premiere fois seulement : dans Prism, ajoute ton compte Microsoft'
Write-Host '  (en haut a droite > Gerer les comptes) et lance une fois l''instance ForestCraft'
Write-Host '  pour que Prism telecharge Minecraft et Java 25 (elle peut se refermer seule, c''est normal).'
if ($freshPrism) { Start-Process $prism }
