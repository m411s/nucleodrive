#!/usr/bin/env bash
# Makes the tray state icons from a base icon (PNG, square), in the style of Proton VPN's tray icon:
# the base slightly smaller in the bottom-left, a status dot in the top-right corner, and a
# transparent gap around the dot instead of a ring. Needs ImageMagick (magick or convert).
#
#   make-tray-icons.sh BASE.png OUTDIR [GLYPH_COLOUR]
#
# Writes OUTDIR/tray-{idle,syncing,paused,error,attention,stopped}.png (128×128):
#   idle = synced (✓), syncing (⟳ two circular arrows), paused (‖), error (!),
#   attention = sign in / confirm deletions (!), stopped = daemon not running (–, faded base)
# GLYPH_COLOUR defaults to a near-black, like Proton VPN's check mark.
set -euo pipefail
base="$1"
out="$2"
ink="${3:-#1d1d1f}"
im=$(command -v magick || command -v convert) || { echo "ImageMagick not found" >&2; exit 1; }
mkdir -p "$out"

# Geometry (128×128 canvas): base 104×104 at the bottom-left; dot centred at (95,33), radius 29;
# the base is cut away within radius 36 of the dot's centre, leaving a 7 px transparent gap.
CX=95 CY=33 R=29 GAP=36

# Glyphs are drawn around (0,0) and translated to the dot's centre.
declare -A glyph=(
  [idle]="fill none stroke $ink stroke-width 7 stroke-linecap round stroke-linejoin round polyline -13,1 -4,10 13,-9"
  [syncing]="fill none stroke $ink stroke-width 6 stroke-linecap round arc -14,-14 14,14 200,325 arc -14,-14 14,14 20,145 fill $ink stroke none polygon 18,-15 19,1 4,-4 polygon -18,15 -19,-1 -4,4"
  [paused]="fill $ink stroke none roundrectangle -12,-13 -4,13 2,2 roundrectangle 4,-13 12,13 2,2"
  [error]="fill $ink stroke none roundrectangle -4,-17 4,5 3,3 circle 0,13 0,17"
  [attention]="fill $ink stroke none roundrectangle -4,-17 4,5 3,3 circle 0,13 0,17"
  [stopped]="fill $ink stroke none roundrectangle -14,-4 14,4 3,3"
)
declare -A colour=(
  [idle]="#1fb58e" [syncing]="#4c8dff" [paused]="#9aa0a6" [error]="#ff5c5c" [attention]="#ffb020" [stopped]="#9aa0a6"
)

for state in idle syncing paused error attention stopped; do
  fade=()
  [ "$state" = stopped ] && fade=(-modulate 100,0 -channel A -evaluate multiply 0.5 +channel)
  "$im" \( "$base" -background none -resize 104x104 -gravity center -extent 104x104 "${fade[@]}" \) \
        -background none -gravity southwest -extent 128x128 \
        \( -size 128x128 xc:none -fill black -draw "circle $CX,$CY $CX,$((CY - GAP))" \) -compose DstOut -composite \
        -compose Over -draw "fill ${colour[$state]} stroke none circle $CX,$CY $CX,$((CY - R))" \
        -draw "translate $CX,$CY ${glyph[$state]}" \
        "$out/tray-$state.png"
done
