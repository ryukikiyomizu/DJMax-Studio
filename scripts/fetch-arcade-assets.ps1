<#
.SYNOPSIS
    Stages the owner's arcade art for the studio's gameplay previews.

.DESCRIPTION
    Two source checkouts, two destinations under DJMaxEditor.Studio\LocalAssets:

      - Shino-Toku   (TECHNIKA: numbered sprite sets with cool/good/max judgment frames
                      and hold/long note parts)  ->  LocalAssets\Technika2\<set>\
      - Shino-Tokuu  (RESPECT V: base gear textures)  ->  LocalAssets\RespectV\Gear\

    Both destinations are gitignored, exactly like the Technika folder already is: this is the
    owner's own extracted art and nothing it holds is redistributed with the repo. When either
    folder is absent the renderers keep their procedural fallbacks, so the script is optional.

    The TECHNIKA copy grabs one numbered set (default "0"). The note-glyph loader keys its art
    off flat files and falls back to the packaged glyphs for what a set does not carry; the hit
    effect (CoolBomb cool_* frames) resolves from the set's own cool\ folder.

    The RESPECT V copy pins five base-skin gear textures by their Unity asset id in
    "Base Game Skins\Gears\Texture2D" (same-named variants coexist there; these five are the
    ones composed against RespectGameplayLayout's 502x1080 native playfield):

      Gear_bg @8145.png              (502x836   playfield glass)
      Gear_default_back @20756.png   (635x346   header plate)
      Gear_default_bottom @22770.png (480x275   bottom deck)
      Gear_frame_left @17180.png     (55x1080   left rail)
      Gear_frame_right @15674.png    (57x1045   right rail)

    If an id is not found (the dump was refreshed and ids renumbered) the script takes the
    first same-named texture instead and says so, since the base skin keeps its native sizes.

.PARAMETER TechnikaRoot
    Root of a Shino-Toku clone. Defaults to %DJMAX_SHINO_TOKU%.

.PARAMETER RespectRoot
    Root of a Shino-Tokuu clone. Defaults to %DJMAX_SHINO_TOKUU%.

.PARAMETER Set
    Which numbered TECHNIKA set to copy (0-5). Default 0.

.PARAMETER TargetRoot
    Where to stage. Defaults to <repo>\DJMaxEditor.Studio\LocalAssets.

.EXAMPLE
    powershell -File scripts\fetch-arcade-assets.ps1 `
        -TechnikaRoot D:\src\Shino-Toku -RespectRoot D:\src\Shino-Tokuu
#>
[CmdletBinding()]
param(
    [string] $TechnikaRoot = $(if ($env:DJMAX_SHINO_TOKU) { $env:DJMAX_SHINO_TOKU } else { $null }),
    [string] $RespectRoot  = $(if ($env:DJMAX_SHINO_TOKUU) { $env:DJMAX_SHINO_TOKUU } else { $null }),
    [ValidateRange(0, 5)] [int] $Set = 0,
    [string] $TargetRoot = (Join-Path $PSScriptRoot "..\DJMaxEditor.Studio\LocalAssets")
)

$ErrorActionPreference = "Stop"

function Copy-TechnikaSet {
    param([string] $Root, [int] $SetNumber, [string] $Target)
    if (-not $Root) { Write-Host "TECHNIKA: no -TechnikaRoot given, skipping."; return }
    $source = Join-Path $Root $SetNumber
    if (-not (Test-Path $source)) { Write-Warning "TECHNIKA: set folder not found: $source"; return }

    $dest = Join-Path $Target "Technika2\$SetNumber"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Write-Host "TECHNIKA: copying set $SetNumber -> $dest"
    robocopy $source $dest /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE) copying $source" }
    Write-Host "TECHNIKA: done. Set DJMAX_EDITOR_TECHNIKA_ASSETS to another set folder to switch skins."
}

$GearPins = @(
    @{ Pin = "Gear_bg @8145.png";              Match = "Gear_bg @*.png";              Out = "gear_bg.png" },
    @{ Pin = "Gear_default_back @20756.png";   Match = "Gear_default_back @*.png";   Out = "gear_back.png" },
    @{ Pin = "Gear_default_bottom @22770.png"; Match = "Gear_default_bottom @*.png"; Out = "gear_bottom.png" },
    @{ Pin = "Gear_frame_left @17180.png";     Match = "Gear_frame_left @*.png";     Out = "gear_frame_left.png" },
    @{ Pin = "Gear_frame_right @15674.png";    Match = "Gear_frame_right @*.png";    Out = "gear_frame_right.png" }
)

function Copy-RespectGear {
    param([string] $Root, [string] $Target)
    if (-not $Root) { Write-Host "RESPECT V: no -RespectRoot given, skipping."; return }
    $texDir = Join-Path $Root "Base Game Skins\Gears\Texture2D"
    if (-not (Test-Path $texDir)) { Write-Warning "RESPECT V: gear textures not found: $texDir"; return }

    $dest = Join-Path $Target "RespectV\Gear"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    foreach ($pin in $GearPins) {
        $source = Join-Path $texDir $pin.Pin
        if (-not (Test-Path $source)) {
            $fallback = Get-ChildItem -Path $texDir -Filter $pin.Match | Sort-Object Name | Select-Object -First 1
            if ($fallback) {
                Write-Warning "RESPECT V: $($pin.Pin) not found; using $($fallback.Name)"
                $source = $fallback.FullName
            } else {
                Write-Warning "RESPECT V: no texture matching $($pin.Match); $($pin.Out) skipped."
                continue
            }
        }
        Copy-Item $source (Join-Path $dest $pin.Out) -Force
        Write-Host "RESPECT V: $($pin.Out) <- $(Split-Path $source -Leaf)"
    }
    Write-Host "RESPECT V: done. The RespectPlayfieldView picks these up as 'SHINO-TOKUU GEAR'."
}

Copy-TechnikaSet -Root $TechnikaRoot -SetNumber $Set -Target $TargetRoot
Copy-RespectGear -Root $RespectRoot -Target $TargetRoot
Write-Host "Stage complete: $TargetRoot"
