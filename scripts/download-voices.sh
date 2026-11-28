#!/usr/bin/env bash
# Downloads the Piper voices Lanyard speaks announcements with (SpeechVoiceCatalogue) from the
# sherpa-onnx model release, checks each against its SHA-256, and unpacks them into one folder
# with a single shared espeak-ng-data.
#
#   scripts/download-voices.sh                 # into ~/.config/Lanyard/Voices (%AppData% on Linux)
#   scripts/download-voices.sh /app/voices     # what the Dockerfile does
#
# Each voice's licence is in its MODEL_CARD. Adding a voice: add a line below and to
# SpeechVoiceCatalogue.Voices.
set -euo pipefail

DEST="${1:-${XDG_CONFIG_HOME:-$HOME/.config}/Lanyard/Voices}"
BASE="https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models"

VOICES=(
  "vits-piper-en_GB-cori-medium-int8 169ca8aff3adb271f009a4924c99928a811dbf2b52eaca2dbb460e8c34478c93"
  "vits-piper-en_GB-alba-medium-int8 f7581d123ae977f64f3032bb247d4deeac8440e881d918d36fdd36d8f1030fb7"
  "vits-piper-en_GB-northern_english_male-medium-int8 9b2afdc8f35426f6b2739f255e67841679a52b56d19ece874a1eae208cb88ad1"
  "vits-piper-en_GB-vctk-medium-int8 a506a3798c69bd9116f07d6ee6d4a502dd9e35742349fd56bafa192acba93f95"
)

mkdir -p "$DEST"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK:?}"' EXIT

for entry in "${VOICES[@]}"; do
  read -r name sha <<< "$entry"

  if [ -d "$DEST/$name" ]; then
    echo "$name: already there"
    continue
  fi

  echo "$name: downloading"
  curl -fsSL --retry 4 -o "$WORK/$name.tar.bz2" "$BASE/$name.tar.bz2"
  echo "$sha  $WORK/$name.tar.bz2" | sha256sum -c --quiet -

  tar -xjf "$WORK/$name.tar.bz2" -C "$WORK"

  # Every voice ships the same espeak-ng-data; keep one copy beside them all.
  if [ ! -d "$DEST/espeak-ng-data" ]; then
    mv "$WORK/$name/espeak-ng-data" "$DEST/espeak-ng-data"
  else
    rm -rf "${WORK:?}/${name:?}/espeak-ng-data"
  fi

  mv "$WORK/$name" "$DEST/$name"
done

echo "Voices are in $DEST"
