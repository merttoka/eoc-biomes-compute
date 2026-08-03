#!/usr/bin/env bash
# Encode the Scene_DAC master and both Urban Digital Canvas Shanghai deliverables.
#
#   ./tools/encode_dac.sh <frame-dir> [out-dir]
#
# <frame-dir> holds f00000.png .. f05399.png at 9472x900, one PNG per rendered frame,
# written by the batchmode render pass (Assets/Editor/RenderMaster.cs in the verification
# clone). 5400 frames at 60 fps = exactly 90 s.
#
# Both screens are PURE CROPS of the one master — no rescale, which is the whole reason the
# master is 9472x900 (10.524:1):
#   screen 2.6  Xinda Plaza                9472x800  crop y+50   (no sound, no cutout)
#   screen 1.2  Jingyao Hongqiao           9000x900  crop x+236  (sound, centre cutout)
#
# WIDTH WARNING. 9472 px is 592 macroblocks wide. That is legal H.264 — Level 6.2 allows
# 139264 MBs and a max width of ~1056 MBs, and libx264 encodes it happily at Level 6.0 — but
# many HARDWARE decoders cap at 4096 or 8192 px and will refuse it. Confirm the venue's
# player before treating the H.264 as final; the ProRes output is the safe fallback and is
# also the better master to hand over for re-encoding.

set -euo pipefail

FRAMES="${1:?usage: encode_dac.sh <frame-dir> [out-dir]}"
OUT="${2:-$(dirname "$FRAMES")/deliverables}"
FPS=60
CRF=16          # visually lossless-ish for this material; raise to trade size for quality

mkdir -p "$OUT"
count=$(find "$FRAMES" -name 'f*.png' | wc -l | tr -d ' ')
echo "frames: $count in $FRAMES  ->  $OUT"
[ "$count" -gt 0 ] || { echo "no frames found"; exit 1; }

common=(-y -framerate "$FPS" -start_number 0 -i "$FRAMES/f%05d.png")

echo
echo "== master 9472x900 (ProRes 422 HQ — the handover master, no width limits) =="
ffmpeg -hide_banner -loglevel warning "${common[@]}" \
  -c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le \
  "$OUT/DAC_master_9472x900_prores.mov"

echo
echo "== master 9472x900 (H.264 High, for preview/submission) =="
ffmpeg -hide_banner -loglevel warning "${common[@]}" \
  -c:v libx264 -profile:v high -level:v 6.2 -pix_fmt yuv420p \
  -crf "$CRF" -preset slow -movflags +faststart \
  "$OUT/DAC_master_9472x900.mp4"

echo
echo "== screen 2.6 — Xinda Plaza 9472x800 (crop, no rescale) =="
ffmpeg -hide_banner -loglevel warning "${common[@]}" \
  -vf "crop=9472:800:0:50" \
  -c:v libx264 -profile:v high -level:v 6.2 -pix_fmt yuv420p \
  -crf "$CRF" -preset slow -movflags +faststart \
  "$OUT/DAC_screen2.6_9472x800.mp4"

echo
echo "== screen 1.2 — Jingyao Hongqiao 9000x900 (crop, no rescale) =="
ffmpeg -hide_banner -loglevel warning "${common[@]}" \
  -vf "crop=9000:900:236:0" \
  -c:v libx264 -profile:v high -level:v 6.2 -pix_fmt yuv420p \
  -crf "$CRF" -preset slow -movflags +faststart \
  "$OUT/DAC_screen1.2_9000x900.mp4"

echo
echo "== result =="
for f in "$OUT"/*.mp4 "$OUT"/*.mov; do
  [ -f "$f" ] || continue
  printf '  %-42s %8s  ' "$(basename "$f")" "$(du -h "$f" | cut -f1)"
  ffprobe -v error -select_streams v:0 \
    -show_entries stream=width,height,r_frame_rate,nb_read_packets \
    -count_packets -of csv=p=0 "$f"
done
