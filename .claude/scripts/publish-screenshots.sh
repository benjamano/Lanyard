#!/usr/bin/env bash
#
# Publish UI screenshots to the `screenshots` branch and surface them on a PR.
#
# Screenshots attached inline in a chat transcript do not survive — the transcript
# drops them and the PNGs are gone. This puts them somewhere permanent and links
# them from the pull request, which is where UI review happens anyway.
#
# Uses the GitHub REST API exclusively: no checkout, no index write, no stash.
# That matters because this repo is worked on through git worktrees that share a
# stash stack with the main checkout and with other concurrent sessions.
#
# Usage:
#   publish-screenshots.sh --manifest <file.json> --pr <number>
#   publish-screenshots.sh --manifest <file.json> --emit-markdown
#
# Manifest format:
#   [ { "file": "/tmp/shots/a.png", "viewport": "desktop", "caption": "Analytics, populated" } ]
#
# Optional per-entry "size" overrides the default viewport label (1440x900 / 390x844).

set -euo pipefail

REPO="benjamano/Lanyard"
SHOT_BRANCH="screenshots"
MARKER_START="<!-- claude-screenshots:start -->"
MARKER_END="<!-- claude-screenshots:end -->"

manifest=""
pr=""
emit_markdown=0

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() {
  sed -n '3,20p' "$0" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest)       manifest="${2:-}"; shift 2 ;;
    --pr)             pr="${2:-}"; shift 2 ;;
    --emit-markdown)  emit_markdown=1; shift ;;
    -h|--help)        usage; exit 0 ;;
    *)                die "unknown argument: $1 (try --help)" ;;
  esac
done

[[ -n "$manifest" ]] || die "--manifest is required"
[[ -f "$manifest" ]] || die "manifest not found: $manifest"
[[ -n "$pr" || $emit_markdown -eq 1 ]] || die "pass either --pr <number> or --emit-markdown"

command -v gh  >/dev/null || die "gh is not installed"
command -v jq  >/dev/null || die "jq is not installed"

jq -e 'type == "array" and length > 0' "$manifest" >/dev/null \
  || die "manifest must be a non-empty JSON array"

# Every entry needs a file that exists; fail before uploading anything rather than
# leaving a half-published set on the branch.
while IFS= read -r f; do
  [[ -f "$f" ]] || die "manifest references a missing file: $f"
done < <(jq -r '.[].file' "$manifest")

jq -e 'all(.[]; has("file") and has("viewport") and has("caption"))' "$manifest" >/dev/null \
  || die "every manifest entry needs \"file\", \"viewport\" and \"caption\""

# Storage is keyed on branch name, not PR number, so screenshots can be uploaded
# before the PR exists (that is what makes --emit-markdown possible).
src_branch="$(git rev-parse --abbrev-ref HEAD)"
[[ "$src_branch" != "HEAD" ]] || die "detached HEAD — cannot derive a screenshot path"
safe_branch="$(printf '%s' "$src_branch" | tr -c 'A-Za-z0-9._-' '-')"

printf 'Publishing %s screenshot(s) for branch %s\n' \
  "$(jq 'length' "$manifest")" "$src_branch" >&2

# --- 1. Locate the screenshots branch (absent on first run) -------------------
base_tree=""
parents="[]"
if ref_json="$(gh api "/repos/$REPO/git/ref/heads/$SHOT_BRANCH" 2>/dev/null)"; then
  head_sha="$(jq -r '.object.sha' <<<"$ref_json")"
  base_tree="$(gh api "/repos/$REPO/git/commits/$head_sha" --jq '.tree.sha')"
  parents="[\"$head_sha\"]"
  printf '  branch exists at %s\n' "${head_sha:0:7}" >&2
else
  # First run: no parents, so the branch starts as a true orphan and carries none
  # of main's history.
  printf '  branch does not exist yet — creating as an orphan\n' >&2
fi

# --- 2. Upload each PNG as a blob --------------------------------------------
tree_entries="$(mktemp)"
trap 'rm -f "$tree_entries"' EXIT

while IFS=$'\t' read -r file; do
  name="$(basename "$file")"
  blob_sha="$(
    jq -n --arg c "$(base64 -w0 "$file")" '{content:$c, encoding:"base64"}' \
      | gh api -X POST "/repos/$REPO/git/blobs" --input - --jq '.sha'
  )"
  printf '  uploaded %s (%s)\n' "$name" "${blob_sha:0:7}" >&2
  jq -nc --arg p "$safe_branch/$name" --arg s "$blob_sha" \
    '{path:$p, mode:"100644", type:"blob", sha:$s}' >> "$tree_entries"
done < <(jq -r '.[].file' "$manifest")

# --- 3. Build a tree ----------------------------------------------------------
# base_tree is essential on updates: without it the new tree replaces the whole
# branch and every other PR's screenshots are destroyed.
tree_payload="$(jq -sc '{tree: .}' "$tree_entries")"
if [[ -n "$base_tree" ]]; then
  tree_payload="$(jq -c --arg b "$base_tree" '. + {base_tree: $b}' <<<"$tree_payload")"
fi
tree_sha="$(gh api -X POST "/repos/$REPO/git/trees" --input - --jq '.sha' <<<"$tree_payload")"

# --- 4. Commit and move the ref ----------------------------------------------
commit_sha="$(
  jq -nc \
    --arg m "Screenshots for $src_branch" \
    --arg t "$tree_sha" \
    --argjson p "$parents" \
    '{message:$m, tree:$t, parents:$p}' \
  | gh api -X POST "/repos/$REPO/git/commits" --input - --jq '.sha'
)"

if [[ -n "$base_tree" ]]; then
  jq -nc --arg s "$commit_sha" '{sha:$s, force:true}' \
    | gh api -X PATCH "/repos/$REPO/git/refs/heads/$SHOT_BRANCH" --input - >/dev/null
else
  jq -nc --arg r "refs/heads/$SHOT_BRANCH" --arg s "$commit_sha" '{ref:$r, sha:$s}' \
    | gh api -X POST "/repos/$REPO/git/refs" --input - >/dev/null
fi
printf '  committed %s to %s\n' "${commit_sha:0:7}" "$SHOT_BRANCH" >&2

# --- 5. Build the markdown block ---------------------------------------------
# ?v=<sha> busts GitHub's camo image cache. Without it, re-running on the same
# branch overwrites the PNG but the PR keeps rendering the previous image.
raw_base="https://raw.githubusercontent.com/$REPO/$SHOT_BRANCH"
cache_bust="${commit_sha:0:7}"

block="$(
  {
    printf '%s\n' "$MARKER_START"
    printf '## Screenshots\n'
    # Known viewports first in a stable order, then anything else the manifest used.
    ordered="$(jq -r '[.[].viewport] | unique | (map(select(. == "desktop")) + map(select(. == "phone")) + map(select(. != "desktop" and . != "phone"))) | .[]' "$manifest")"
    while IFS= read -r vp; do
      [[ -n "$vp" ]] || continue
      case "$vp" in
        desktop) heading="Desktop"; default_size="1440x900"; width="" ;;
        phone)   heading="Phone";   default_size="390x844";  width="380" ;;
        *)       heading="$(tr '[:lower:]' '[:upper:]' <<<"${vp:0:1}")${vp:1}"; default_size=""; width="" ;;
      esac
      size="$(jq -r --arg v "$vp" 'map(select(.viewport == $v)) | .[0].size // ""' "$manifest")"
      [[ -n "$size" ]] || size="$default_size"
      printf '\n### %s%s\n' "$heading" "${size:+ — $size}"
      while IFS=$'\t' read -r cap fname; do
        printf '\n**%s**\n\n' "$cap"
        url="$raw_base/$safe_branch/$fname?v=$cache_bust"
        if [[ -n "$width" ]]; then
          printf '<img width="%s" alt="%s" src="%s">\n' "$width" "$cap" "$url"
        else
          printf '![%s](%s)\n' "$cap" "$url"
        fi
      done < <(jq -r --arg v "$vp" '.[] | select(.viewport == $v) | [.caption, (.file | split("/") | last)] | @tsv' "$manifest")
    done <<<"$ordered"
    printf '\n%s\n' "$MARKER_END"
  }
)"

if [[ $emit_markdown -eq 1 && -z "$pr" ]]; then
  printf '%s\n' "$block"
  exit 0
fi

# --- 6. Rewrite the managed section of the PR description --------------------
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

printf 'Updated PR #%s description with %s screenshot(s).\n' \
  "$pr" "$(jq 'length' "$manifest")" >&2
