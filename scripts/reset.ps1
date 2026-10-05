# ForestCraft - wipes the Minecraft ForestCraft world and the dug terrain of The Forest.
# The Forest's own saves are not touched.
. "$PSScriptRoot\common.ps1"

Write-Host ''
Write-Host '  Remise a zero de ForestCraft :' -ForegroundColor Yellow
Write-Host '   - le monde Minecraft ForestCraft (blocs poses, inventaire, trous)'
Write-Host '   - le terrain creuse de The Forest (dig.txt)'
Write-Host '  Les sauvegardes de The Forest ne sont PAS touchees.'
Write-Host ''
if (Get-Process -Name 'TheForest' -ErrorAction SilentlyContinue) { Fail 'The Forest tourne encore. Ferme-le (Minecraft se ferme avec) puis relance.' }
$answer = Read-Host '  Tout effacer ? (o/n)'
if ($answer -notmatch '^[oOyY]') { Write-Host '  Annule.'; exit 0 }

$inst = Find-ForestCraftInstance
if ($inst) {
    # Every ForestCraft world: the one being played and the one kept with each save slot.
    $savesDir = Join-Path (Get-InstanceGameDir $inst) 'saves'
    $worlds = @(Get-ChildItem $savesDir -Directory -Filter 'ForestCraft*' -ErrorAction SilentlyContinue)
    if ($worlds.Count -gt 0) {
        foreach ($w in $worlds) {
            try { Remove-Item $w.FullName -Recurse -Force } catch { Fail 'impossible d''effacer les mondes : Minecraft est sans doute encore ouvert.' }
        }
        Ok "$($worlds.Count) monde(s) Minecraft efface(s)"
    } else { Ok 'pas de monde Minecraft a effacer' }
} else { Info 'Prism introuvable : monde Minecraft non touche' }

$dig = Join-Path $FC.LinkDir 'dig.txt'
if (Test-Path $dig) { Remove-Item $dig -Force; Ok 'terrain de The Forest remis d''origine' }
else { Ok 'pas de terrain creuse a remettre' }
Write-Host ''
Write-Host '  Au prochain lancement : monde Minecraft neuf, ile intacte, barre de depart redonnee.'
