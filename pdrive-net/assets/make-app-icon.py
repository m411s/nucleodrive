#!/usr/bin/env python3
# Generates the app icon: assets/pdrive.svg (with the sync loop) and, with any second argument, the
# small-size variant without it (16/24 px and the tray base, where the loop would be a smudge).
#   python3 assets/make-app-icon.py assets/pdrive.svg
#   python3 assets/make-app-icon.py assets/pdrive-small.svg small
# Then render the PNGs in assets/icons with librsvg (like render_svg in install.sh).
import math, sys
out = sys.argv[1]
small = len(sys.argv) > 2
C = (128, 146); r = 27; w = 12.5

def pt(a, rad=r):
    t = math.radians(a); return (C[0] + rad*math.cos(t), C[1] + rad*math.sin(t))

def arc(a0, a1):
    x0, y0 = pt(a0); x1, y1 = pt(a1)
    return f"M{x0:.2f} {y0:.2f} A{r} {r} 0 0 1 {x1:.2f} {y1:.2f}"

def head(a, size=14.5):
    # filled arrowhead at angle a, pointing clockwise along the circle
    t = math.radians(a)
    tx, ty = -math.sin(t), math.cos(t)          # tangent (clockwise)
    nx, ny = math.cos(t), math.sin(t)           # outward normal
    bx, by = pt(a)
    tip = (bx + tx*size*0.9, by + ty*size*0.9)
    l = (bx + nx*size*0.75 - tx*size*0.2, by + ny*size*0.75 - ty*size*0.2)
    rr = (bx - nx*size*0.75 - tx*size*0.2, by - ny*size*0.75 - ty*size*0.2)
    return f"M{tip[0]:.2f} {tip[1]:.2f} L{l[0]:.2f} {l[1]:.2f} L{rr[0]:.2f} {rr[1]:.2f} Z"

# cloud: one smooth outline (flat bottom, three bumps)
cloud = ("M78 188 "
         "C55 188 40 172 40 152 C40 133 54 118 73 116 "
         "C78 89 101 70 129 70 C157 70 179 89 184 115 "
         "C203 117 216 132 216 151 C216 172 200 188 179 188 Z")

svg = f'''<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <!-- pdrive app icon{' (small sizes: no sync loop)' if small else ''}: a cloud with a two-way sync loop on a purple tile in the app's accent
       colours. Original artwork for this unofficial app; not Proton's logo (no "P", no Drive folder shape). -->
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#A08BFF"/>
      <stop offset="0.5" stop-color="#6D4AFF"/>
      <stop offset="1" stop-color="#4A2BD9"/>
    </linearGradient>
    <linearGradient id="shine" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#ffffff" stop-opacity="0.22"/>
      <stop offset="0.5" stop-color="#ffffff" stop-opacity="0"/>
    </linearGradient>
    <mask id="loop" maskUnits="userSpaceOnUse" x="0" y="0" width="256" height="256">
      <rect width="256" height="256" fill="#fff"/>
      <g fill="none" stroke="#000" stroke-width="{w}" stroke-linecap="round">
        <path d="{arc(195, 318)}"/>
        <path d="{arc(15, 138)}"/>
      </g>
      <g fill="#000">
        <path d="{head(318)}"/>
        <path d="{head(138)}"/>
      </g>
    </mask>
  </defs>
  <rect x="12" y="12" width="232" height="232" rx="58" fill="url(#tile)"/>
  <rect x="12" y="12" width="232" height="232" rx="58" fill="url(#shine)"/>
  <g transform="translate(128 132) scale(0.88) translate(-128 -132)">
    <path d="{cloud}" fill="#ffffff"{'' if small else ' mask="url(#loop)"'}/>
  </g>
</svg>
'''
open(out, "w").write(svg)
