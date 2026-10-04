# ForestCraft - builds both halves and packs a ready-to-share release in dist\.
#   dist\ForestCraft\            (folder you can test from)
#   dist\ForestCraft-<ver>.zip   (upload this to a GitHub release)
# Needs: The Forest installed, the .NET SDK (any version >= 6). A JDK 25 is downloaded if missing.
. "$PSScriptRoot\common.ps1"
$repo = Split-Path $PSScriptRoot

Step 'Outils'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "le SDK .NET est introuvable. Installe-le (winget install Microsoft.DotNet.SDK.8) puis relance."
}
$forest = Find-Forest
if (-not $forest) { Fail "dossier de The Forest introuvable" }
Ok "The Forest : $forest"
# The plugin compiles against the game's own BepInEx (or .tools\bepinex if the game has none yet).
if (-not (Test-Path (Join-Path $forest 'BepInEx\core\BepInEx.dll')) -and -not (Test-Path (Join-Path $repo '.tools\bepinex\BepInEx\core\BepInEx.dll'))) {
    Install-BepInEx $forest
}
$jdk = Find-Jdk25 $repo
Ok "JDK : $jdk"

Step 'Build The Forest (BepInEx)'
& dotnet build (Join-Path $repo 'forest\ForestCraft.csproj') -c Release -nologo -v q "-p:ForestDir=$forest"
if ($LASTEXITCODE -ne 0) { Fail 'build du plugin The Forest' }
$dll = Join-Path $repo 'forest\bin\ForestCraft.dll'

Step 'Build Minecraft (Fabric)'
$env:JAVA_HOME = $jdk
Push-Location (Join-Path $repo 'fabric')
try {
    & cmd /c "gradlew.bat build --no-daemon -q"
    if ($LASTEXITCODE -ne 0) { Fail 'build du mod Fabric' }
} finally { Pop-Location }
$jar = Get-ChildItem (Join-Path $repo 'fabric\build\libs') -Filter 'forestcraft-*.jar' |
    Where-Object { $_.Name -notmatch '(sources|dev)\.jar$' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $jar) { Fail 'jar Fabric introuvable dans fabric\build\libs' }
$version = ($jar.BaseName -replace '^forestcraft-', '')

Step 'Paquet'
$dist = Join-Path $repo 'dist'
$pkg = Join-Path $dist 'ForestCraft'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $pkg 'mods'), (Join-Path $pkg 'scripts') | Out-Null
Copy-Item $dll (Join-Path $pkg 'mods\ForestCraft.dll')
Copy-Item $jar.FullName (Join-Path $pkg 'mods\forestcraft.jar')
foreach ($f in 'install.bat', 'reset.bat', 'README.md', 'LICENSE') {
    if (Test-Path (Join-Path $repo $f)) { Copy-Item (Join-Path $repo $f) $pkg }
}
foreach ($f in 'common.ps1', 'install.ps1', 'reset.ps1') { Copy-Item (Join-Path $PSScriptRoot $f) (Join-Path $pkg 'scripts') }
$zip = Join-Path $dist "ForestCraft-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath $zip
Ok "dist\ForestCraft-$version.zip"
