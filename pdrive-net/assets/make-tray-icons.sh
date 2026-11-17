#!/usr/bin/env bash
# Makes the tray state icons from a base icon (PNG, square): the base plus a status dot in the
# bottom-right corner. Needs ImageMagick (magick or convert).
#
#   make-tray-icons.sh BASE.png OUTDIR
#
# Writes OUTDIR/tray-{idle,syncing,paused,error,attention,stopped}.png (128×128):
#   idle = synced (green ✓), syncing (blue ⟳, two circular arrows), paused (grey ‖), error (red !),
#   attention = sign in / confirm deletions (amber !), stopped = daemon not running (dark grey –, faded base)
set -euo pipefail
base="$1"
out="$2"
im=$(command -v magick || command -v convert) || { echo "ImageMagick not found" >&2; exit 1; }
mkdir -p "$out"

# Dot: centre (97,97), radius 29, with a white ring so it reads on any background.
dot() { echo "fill $1 stroke white stroke-width 6 circle 97,97 97,126"; }

declare -A glyph=(
  [idle]="fill none stroke white stroke-width 7 stroke-linecap round stroke-linejoin round polyline 84,98 93,107 110,88"
  [syncing]="fill none stroke white stroke-width 6 stroke-linecap round arc 83,83 111,111 200,325 arc 83,83 111,111 20,145 fill white stroke none polygon 115,82 116,98 101,93 polygon 79,112 78,96 93,101"
  [paused]="fill white stroke none rectangle 85,84 93,110 rectangle 101,84 109,110"
  [error]="fill white stroke none roundrectangle 93,80 101,102 3,3 circle 97,110 97,114"
  [attention]="fill white stroke none roundrectangle 93,80 101,102 3,3 circle 97,110 97,114"
  [stopped]="fill white stroke none roundrectangle 83,93 111,101 3,3"
)
declare -A colour=(
  [idle]="#2fa84f" [syncing]="#3b7ddd" [paused]="#8a8f98" [error]="#d64545" [attention]="#e3a21a" [stopped]="#5f6368"
)

for state in idle syncing paused error attention stopped; do
  args=("$base" -background none -resize 128x128 -gravity center -extent 128x128)
  if [ "$state" = stopped ]; then
    args+=(-modulate 100,0 -channel A -evaluate multiply 0.55 +channel) # faded grey: not running
  fi
  "$im" "${args[@]}" -draw "$(dot "${colour[$state]}")" -draw "${glyph[$state]}" "$out/tray-$state.png"
done
