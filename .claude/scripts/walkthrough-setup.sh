#!/usr/bin/env bash
# One-time setup for record-video/walkthrough.mjs: installs Piper (offline
# text-to-speech) and a British English voice under ~/.local/share/piper.
# Safe to re-run; skips anything already present.
set -euo pipefail
P="$HOME/.local/share/piper"
VOICE="${1:-en_GB-jenny_dioco-medium}"
mkdir -p "$P/voices"

if ! python3 -m venv --help >/dev/null 2>&1 || ! python3 -c 'import ensurepip' 2>/dev/null; then
  echo "installing python3-venv (needs sudo)"
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -q "python3.$(python3 -c 'import sys; print(sys.version_info.minor)')-venv"
fi
[ -x "$P/venv/bin/piper" ] || { python3 -m venv "$P/venv"; "$P/venv/bin/pip" install -q piper-tts; }

# Voice names are <lang>_<REGION>-<name>-<quality>, laid out the same way on Hugging Face.
lang=${VOICE%%-*}; rest=${VOICE#*-}; name=${rest%-*}; quality=${rest##*-}
for ext in onnx onnx.json; do
  f="$P/voices/$VOICE.$ext"
  [ -s "$f" ] || curl -sSfL -o "$f" "https://huggingface.co/rhasspy/piper-voices/resolve/main/${lang%%_*}/$lang/$name/$quality/$VOICE.$ext"
done

command -v ffmpeg >/dev/null || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -q ffmpeg
cd "$(dirname "$0")/record-video"
[ -d node_modules ] || npm install --silent
npx playwright install chromium >/dev/null
echo "ready: $P/venv/bin/piper with voice $VOICE"
