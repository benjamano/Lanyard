#!/usr/bin/env bash
#
# Publish UI screenshots and short demo videos to the `screenshots` branch and
# surface them on a PR.
#
# Screenshots/videos attached inline in a chat transcript do not survive — the
# transcript drops them and the files are gone. This puts them somewhere
# permanent and links them from the pull request, which is where UI review
# happens anyway.
#
# Media kind (image vs. video) is inferred from each file's extension; see
# IMAGE_EXTS / VIDEO_EXTS below.
#
# Uses the GitHub REST API exclusively: no checkout, no index write, no stash.
# That matters because this repo is worked on through git worktrees that share a
# stash stack with the main checkout and with other concurrent sessions.
#
# Run --help for usage.

set -euo pipefail

REPO="benjamano/Lanyard"
SHOT_BRANCH="screenshots"
MARKER_START="<!-- claude-screenshots:start -->"
MARKER_END="<!-- claude-screenshots:end -->"
MAX_REF_ATTEMPTS=5
IMAGE_EXTS="png jpg jpeg gif webp"
VIDEO_EXTS="mp4 webm mov"
VIDEO_WARN_BYTES=$((10 * 1024 * 1024))   # 10 MiB — fine, but a nudge to trim/compress
VIDEO_MAX_BYTES=$((60 * 1024 * 1024))    # 60 MiB — stay comfortably under GitHub's blob limits
PREVIEW_MAX_BYTES=$((9 * 1024 * 1024))   # silent GIF preview of each video; skipped if it can't get under this

# Prints "image" or "video" for a filename based on its extension, or dies for
# anything unrecognized so a typo'd manifest entry fails loudly instead of
# silently rendering as the wrong tag.
media_kind() {
  local ext
  ext="$(printf '%s' "${1##*.}" | tr '[:upper:]' '[:lower:]')"
  if [[ " $IMAGE_EXTS " == *" $ext "* ]]; then
    printf 'image\n'
  elif [[ " $VIDEO_EXTS " == *" $ext "* ]]; then
    printf 'video\n'
  else
    die "unrecognized file extension '.$ext' for $1 (images: $IMAGE_EXTS; videos: $VIDEO_EXTS)"
  fi
}

manifest=""
pr=""
emit_markdown=0

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Publish UI screenshots and short demo videos to the screenshots branch and
onto a PR.

Usage:
  publish-screenshots.sh --manifest <file.json> --pr <number>
  publish-screenshots.sh --manifest <file.json> --emit-markdown
  publish-screenshots.sh --manifest <file.json> --pr <number> --emit-markdown

Options:
  --manifest <file>   JSON array describing the screenshots/videos (required).
  --pr <number>       Rewrite this PR's description with the media.
  --emit-markdown     Print the markdown block to stdout. May be combined
                      with --pr, in which case both happen.

Manifest format — "file", "viewport" and "caption" are required, "size"
is optional and overrides the default viewport label. Images (png, jpg,
jpeg, gif, webp) render as an inline <img>. Videos (mp4, webm, mov) render
as a silent, looping GIF preview (made with ffmpeg) that links to the
github.com blob viewer, where the full video plays with sound. An inline
<video> player isn't possible: GitHub strips <video> tags whose source isn't
one of its own uploads, and the PR page's CSP blocks media from
raw.githubusercontent.com:

  [
    { "file": ".playwright-mcp/desktop-home.png", "viewport": "desktop",
      "caption": "Home, populated" },
    { "file": ".playwright-mcp/phone-home.png",   "viewport": "phone",
      "caption": "Home, populated" },
    { "file": ".playwright-mcp/desktop-flow.mp4", "viewport": "desktop",
      "caption": "Creating a scene end-to-end" }
  ]

Videos should be short (a few seconds to ~1 minute) and compressed — under
~10 MiB is comfortable, over ~60 MiB is rejected outright to stay well clear
of GitHub's blob-size limits.

Capture screenshots into .playwright-mcp/ — the Playwright MCP rejects paths
outside the repo, and a bare filename lands in the repo root where it is not
gitignored. Paths may be relative to the repo root or absolute.
USAGE
}

# Escapes text that gets interpolated into HTML (captions are free-form and
# routinely contain quotes, ampersands and angle brackets).
html_escape() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest)
      [[ $# -ge 2 ]] || die "--manifest requires a value"
      manifest="$2"; shift 2 ;;
    --pr)
      [[ $# -ge 2 ]] || die "--pr requires a value"
      pr="$2"; shift 2 ;;
    --emit-markdown)  emit_markdown=1; shift ;;
    -h|--help)        usage; exit 0 ;;
    *)                die "unknown argument: $1 (try --help)" ;;
  esac
done

[[ -n "$manifest" ]] || die "--manifest is required"
[[ -f "$manifest" ]] || die "manifest not found: $manifest"
[[ -n "$pr" || $emit_markdown -eq 1 ]] || die "pass --pr <number>, --emit-markdown, or both"
[[ -z "$pr" || "$pr" =~ ^[0-9]+$ ]] || die "--pr must be a number, got: $pr"

command -v gh >/dev/null || die "gh is not installed"
command -v jq >/dev/null || die "jq is not installed"

# Validate the schema before touching the filesystem, so a malformed entry
# reports the actual problem rather than "missing file: null".
jq -e 'type == "array" and length > 0' "$manifest" >/dev/null \
  || die "manifest must be a non-empty JSON array"
jq -e 'all(.[]; (.file? | type == "string" and length > 0)
               and (.viewport? | type == "string" and length > 0)
               and (.caption? | type == "string"))' "$manifest" >/dev/null \
  || die 'every manifest entry needs non-empty string "file" and "viewport", and a "caption"'

while IFS= read -r f; do
  [[ -f "$f" ]] || die "manifest references a missing file: $f"
  media_kind "$f" >/dev/null  # validates the extension, dies on anything unrecognized
  size_bytes="$(wc -c < "$f")"
  if [[ "$size_bytes" -gt "$VIDEO_MAX_BYTES" ]]; then
    die "$f is $(du -h "$f" | cut -f1), over the $((VIDEO_MAX_BYTES / 1024 / 1024)) MiB cap — trim or compress it first"
  elif [[ "$size_bytes" -gt "$VIDEO_WARN_BYTES" ]]; then
    printf 'warning: %s is %s — consider trimming/compressing for a shorter, lighter clip\n' \
      "$f" "$(du -h "$f" | cut -f1)" >&2
  fi
done < <(jq -r '.[].file' "$manifest")

# Storage is keyed on branch name, not PR number, so screenshots can be uploaded
# before the PR exists (that is what makes --emit-markdown useful).
src_branch="$(git rev-parse --abbrev-ref HEAD)"
[[ "$src_branch" != "HEAD" ]] || die "detached HEAD — cannot derive a screenshot path"
safe_branch="$(printf '%s' "$src_branch" | tr -c 'A-Za-z0-9._-' '-')"

# Remote paths are namespaced by viewport so a desktop and a phone shot that
# happen to share a filename cannot overwrite each other. Collisions *within* a
# viewport would still silently drop an image, so reject them outright. Each
# video also gets a "<name>.preview.gif" beside it, so those paths count too.
dupes="$(jq -r --arg v "$VIDEO_EXTS" '
  ($v | split(" ")) as $vexts
  | .[] | (.file | split("/") | last) as $n
  | "\(.viewport)/\($n)",
    (select(($n | split(".") | last | ascii_downcase) as $e | $vexts | index($e))
     | "\(.viewport)/\($n).preview.gif")' "$manifest" | sort | uniq -d)"
[[ -z "$dupes" ]] || die "two entries map to the same remote path (rename one): $dupes"

printf 'Publishing %s media file(s) for branch %s\n' \
  "$(jq 'length' "$manifest")" "$src_branch" >&2

# --- 1. Upload each PNG as a blob --------------------------------------------
# Blobs are content-addressed and independent of the branch state, so this is
# done once up front and survives any ref-update retry below.
tree_entries="$(mktemp)"
b64="$(mktemp)"
blob_payload="$(mktemp)"
preview_dir="$(mktemp -d)"
trap 'rm -rf "$tree_entries" "$b64" "$blob_payload" "$preview_dir"' EXIT

# Remote paths ("<viewport>/<name>") of videos that got a GIF preview.
declare -A has_preview=()

# Writes a silent looping GIF of video $1 to $2 for the inline preview. Tries a
# smaller/slower encoding if the first is too big. Returns 1 if neither fits,
# 2 if ffmpeg itself failed (its error is left in $preview_dir/ffmpeg.log).
make_preview() {
  local src="$1" out="$2" width="$3" fps
  for fps in 8 5; do
    ffmpeg -nostdin -v error -y -i "$src" -vf "fps=$fps,scale='min($width,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" \
      -loop 0 "$out" 2> "$preview_dir/ffmpeg.log" || return 2
    [[ "$(wc -c < "$out")" -le "$PREVIEW_MAX_BYTES" ]] && return 0
    width=$((width * 3 / 4))
  done
  return 1
}

upload_blob() {
  base64 -w0 "$1" > "$b64"
  jq -n --rawfile c "$b64" \
    '{content: ($c | rtrimstr("\n")), encoding: "base64"}' > "$blob_payload"
  gh api -X POST "/repos/$REPO/git/blobs" --input "$blob_payload" --jq '.sha'
}

while IFS=$'\t' read -r file viewport; do
  name="$(basename "$file")"
  safe_vp="$(printf '%s' "$viewport" | tr -c 'A-Za-z0-9._-' '-')"

  if [[ "$(media_kind "$file")" == "video" ]]; then
    preview="$preview_dir/$name.preview.gif"
    if ! command -v ffmpeg >/dev/null; then
      printf '  warning: ffmpeg not installed, %s gets a plain link instead of a preview\n' "$name" >&2
    elif make_preview "$file" "$preview" "$([[ "$viewport" == phone ]] && echo 390 || echo 720)"; rc=$?; [[ $rc -eq 0 ]]; then
      preview_sha="$(upload_blob "$preview")"
      printf '  uploaded %s (%s, %s)\n' "$(basename "$preview")" "$(du -h "$preview" | cut -f1)" "${preview_sha:0:7}" >&2
      jq -nc --arg p "$safe_branch/$safe_vp/$(basename "$preview")" --arg s "$preview_sha" \
        '{path:$p, mode:"100644", type:"blob", sha:$s}' >> "$tree_entries"
      has_preview["$safe_vp/$name"]=1
    elif [[ $rc -eq 2 ]]; then
      printf '  warning: ffmpeg could not make a preview for %s, using a plain link:\n%s\n' \
        "$name" "$(tail -5 "$preview_dir/ffmpeg.log" | sed 's/^/    /')" >&2
    else
      printf '  warning: preview GIF for %s is over %s MiB even when shrunk, using a plain link\n' \
        "$name" "$((PREVIEW_MAX_BYTES / 1024 / 1024))" >&2
    fi
  fi

  # The payload must reach jq via a file. Passing base64 through --arg puts it in
  # argv, which Linux caps at 128 KiB per argument (MAX_ARG_STRLEN) — any
  # screenshot over ~96 KiB would abort with "Argument list too long", i.e.
  # essentially every real screenshot.
  blob_sha="$(upload_blob "$file")"

  printf '  uploaded %s (%s, %s)\n' "$name" "$(du -h "$file" | cut -f1)" "${blob_sha:0:7}" >&2
  jq -nc --arg p "$safe_branch/$safe_vp/$name" --arg s "$blob_sha" \
    '{path:$p, mode:"100644", type:"blob", sha:$s}' >> "$tree_entries"
done < <(jq -r '.[] | [.file, .viewport] | @tsv' "$manifest")

# --- 2. Commit and move the ref, retrying on concurrent updates --------------
# Several worktree sessions can publish at once. force:true would let them
# clobber each other, leaving an earlier PR pointing at deleted images, so the
# update is fast-forward-only and the tree is rebuilt against the new head on
# each retry.
commit_sha=""
for attempt in $(seq 1 "$MAX_REF_ATTEMPTS"); do
  base_tree=""
  parents="[]"
  branch_exists=0
  if ref_json="$(gh api "/repos/$REPO/git/ref/heads/$SHOT_BRANCH" 2>/dev/null)"; then
    branch_exists=1
    head_sha="$(jq -r '.object.sha' <<<"$ref_json")"
    base_tree="$(gh api "/repos/$REPO/git/commits/$head_sha" --jq '.tree.sha')"
    parents="[\"$head_sha\"]"
  fi

  # base_tree is essential on updates: without it the new tree replaces the
  # whole branch and every other PR's screenshots are destroyed.
  tree_payload="$(jq -sc '{tree: .}' "$tree_entries")"
  [[ -z "$base_tree" ]] || tree_payload="$(jq -c --arg b "$base_tree" '. + {base_tree: $b}' <<<"$tree_payload")"
  tree_sha="$(gh api -X POST "/repos/$REPO/git/trees" --input - --jq '.sha' <<<"$tree_payload")"

  commit_sha="$(
    jq -nc --arg m "Screenshots for $src_branch" --arg t "$tree_sha" --argjson p "$parents" \
      '{message:$m, tree:$t, parents:$p}' \
    | gh api -X POST "/repos/$REPO/git/commits" --input - --jq '.sha'
  )"

  if [[ $branch_exists -eq 1 ]]; then
    # force:false => rejected if someone else moved the ref since we read it.
    if jq -nc --arg s "$commit_sha" '{sha:$s, force:false}' \
       | gh api -X PATCH "/repos/$REPO/git/refs/heads/$SHOT_BRANCH" --input - >/dev/null 2>&1; then
      break
    fi
  else
    # parents:[] => a true orphan, carrying none of main's history.
    if jq -nc --arg r "refs/heads/$SHOT_BRANCH" --arg s "$commit_sha" '{ref:$r, sha:$s}' \
       | gh api -X POST "/repos/$REPO/git/refs" --input - >/dev/null 2>&1; then
      break
    fi
  fi

  [[ $attempt -lt $MAX_REF_ATTEMPTS ]] \
    || die "could not update $SHOT_BRANCH after $MAX_REF_ATTEMPTS attempts (concurrent updates?)"
  printf '  ref moved underneath us, retrying (%s/%s)\n' "$attempt" "$MAX_REF_ATTEMPTS" >&2
  sleep "$attempt"
done
printf '  committed %s to %s\n' "${commit_sha:0:7}" "$SHOT_BRANCH" >&2

# --- 3. Build the markdown block ---------------------------------------------
# ?v=<sha> busts GitHub's cache for images. Without it, re-running on the same
# branch overwrites the file but the PR keeps rendering the previous one.
#
# Videos show their silent GIF preview inline, linked to the github.com *blob
# viewer*, which plays the real file with sound. A <video src=raw...> can't be
# used: GitHub's markdown sanitizer drops <video> unless the source is one of
# its own uploads (checked with the /markdown API: it renders as an empty <p>),
# and the PR page's CSP media-src doesn't include raw.githubusercontent.com.
raw_base="https://raw.githubusercontent.com/$REPO/$SHOT_BRANCH"
blob_base="https://github.com/$REPO/blob/$SHOT_BRANCH"
cache_bust="${commit_sha:0:7}"

build_block() {
  printf '%s\n' "$MARKER_START"
  local heading_kinds
  heading_kinds="$(jq -r '[.[].file | split(".") | last | ascii_downcase] | unique | .[]' "$manifest" \
    | while read -r ext; do
        if [[ " $VIDEO_EXTS " == *" $ext "* ]]; then printf 'video\n'; else printf 'image\n'; fi
      done | sort -u)"
  case "$heading_kinds" in
    image) printf '## Screenshots\n' ;;
    video) printf '## Videos\n' ;;
    *)     printf '## Screenshots & Videos\n' ;;
  esac

  # Known viewports first in a stable order, then anything else the manifest used.
  local ordered vp heading default_size width sizes size shot_suffix cap fname safe_vp esc_cap
  ordered="$(jq -r '[.[].viewport] | unique
                    | (map(select(. == "desktop")) + map(select(. == "phone"))
                       + map(select(. != "desktop" and . != "phone"))) | .[]' "$manifest")"

  while IFS= read -r vp; do
    [[ -n "$vp" ]] || continue
    case "$vp" in
      desktop) heading="Desktop"; default_size="1440x900"; width="" ;;
      phone)   heading="Phone";   default_size="390x844";  width="380" ;;
      *)       heading="$(html_escape "$vp")"; default_size=""; width="" ;;
    esac
    safe_vp="$(printf '%s' "$vp" | tr -c 'A-Za-z0-9._-' '-')"

    # Only label the section with a size if every shot in it agrees; otherwise the
    # heading would mislabel the rest, so fall back and label each shot instead.
    sizes="$(jq -r --arg v "$vp" '[.[] | select(.viewport == $v) | .size // ""] | unique | .[]' "$manifest")"
    if [[ "$(wc -l <<<"$sizes")" -eq 1 && -n "${sizes//[[:space:]]/}" ]]; then
      size="$sizes"; shot_suffix=0
    elif [[ "$(wc -l <<<"$sizes")" -eq 1 ]]; then
      size="$default_size"; shot_suffix=0
    else
      size=""; shot_suffix=1
    fi

    printf '\n### %s%s\n' "$heading" "${size:+ — $size}"

    while IFS=$'\t' read -r cap fname entry_size; do
      esc_cap="$(html_escape "$cap")"
      if [[ $shot_suffix -eq 1 && -n "$entry_size" ]]; then
        esc_cap="$esc_cap ($(html_escape "$entry_size"))"
      fi
      # Captions are emitted as HTML rather than markdown so that characters
      # like * _ [ ] " & cannot break the rendering.
      printf '\n<p><strong>%s</strong></p>\n\n' "$esc_cap"
      if [[ "$(media_kind "$fname")" == "video" ]]; then
        if [[ -n "${has_preview["$safe_vp/$fname"]:-}" ]]; then
          printf '<a href="%s"><img%s alt="%s (silent preview)" src="%s"></a>\n\n' \
            "$blob_base/$safe_branch/$safe_vp/$fname" "${width:+ width=\"$width\"}" "$esc_cap" \
            "$raw_base/$safe_branch/$safe_vp/$fname.preview.gif?v=$cache_bust"
          printf '<p>&#9654; <a href="%s">Watch with sound</a> (the preview above is silent)</p>\n' \
            "$blob_base/$safe_branch/$safe_vp/$fname"
        else
          printf '<p>&#9654; <a href="%s">Watch video</a></p>\n' \
            "$blob_base/$safe_branch/$safe_vp/$fname"
        fi
      else
        printf '<img%s alt="%s" src="%s">\n' \
          "${width:+ width=\"$width\"}" "$esc_cap" \
          "$raw_base/$safe_branch/$safe_vp/$fname?v=$cache_bust"
      fi
    done < <(jq -r --arg v "$vp" \
      '.[] | select(.viewport == $v)
           | [.caption, (.file | split("/") | last), (.size // "")] | @tsv' "$manifest")
  done <<<"$ordered"

  printf '\n%s\n' "$MARKER_END"
}

block="$(build_block)"

[[ $emit_markdown -eq 0 ]] || printf '%s\n' "$block"
[[ -n "$pr" ]] || exit 0

# --- 4. Rewrite the managed section of the PR description --------------------
current_body="$(gh pr view "$pr" --repo "$REPO" --json body --jq '.body')"

# Drop any previous block so re-runs replace rather than duplicate the section.
stripped="$(
  awk -v s="$MARKER_START" -v e="$MARKER_END" '
    index($0, s) { skip = 1 }
    !skip        { print }
    index($0, e) { skip = 0 }
  ' <<<"$current_body"
)"
# Trim trailing blank lines left behind by the removal.
stripped="$(printf '%s\n' "$stripped" | sed -e :a -e '/^\s*$/{$d;N;ba' -e '}')"

printf '%s\n\n%s\n' "$stripped" "$block" \
  | gh pr edit "$pr" --repo "$REPO" --body-file - >/dev/null

printf 'Updated PR #%s description with %s media file(s).\n' \
  "$pr" "$(jq 'length' "$manifest")" >&2
