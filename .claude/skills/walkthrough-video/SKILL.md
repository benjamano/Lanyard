---
name: walkthrough-video
description: Record a narrated walkthrough video of a Lanyard feature — a real browser session with a voice-over, burned-in subtitles, a visible cursor and highlighted controls, explaining what the feature is and taking the viewer through the steps to use it. Required on every PR that adds or changes UI (alongside the screenshots), and whenever the user asks for a walkthrough, demo, tutorial, explainer or "presentation" video.
---

# Narrated walkthrough videos

`.claude/scripts/record-video/walkthrough.mjs` turns a short script of narrated steps into an `.mp4`: Piper (offline text-to-speech, British voice) reads each line, Playwright drives the app while the line plays, and ffmpeg muxes the voice-over, a subtitle track and the recording together. Captions, a cursor, a highlight box round the control being used and an opening title card are drawn into the page, so they appear in the recording itself.

## When

- **Every PR that adds or changes UI** gets one walkthrough of the feature, published to the PR next to the screenshots (see CLAUDE.md "UI screenshots go on the PR"). Desktop (1440x900) is enough by default; add a phone one when the feature is mainly used on a phone (My Shifts, chat, clocking in).
- **On request**, for any existing feature ("make a walkthrough of rota publishing").
- Not for internal refactors, test-only or dev-tooling changes, or pure bug fixes whose only visible effect is a before/after screenshot.

## One-time setup

```bash
.claude/scripts/walkthrough-setup.sh
```

Installs Piper and the `en_GB-jenny_dioco-medium` voice under `~/.local/share/piper`, ffmpeg if missing, the recorder's npm deps and Playwright's Chromium. Safe to re-run. Pass another voice name to add it (for example `en_GB-alan-medium`), then use it with `PIPER_VOICE=~/.local/share/piper/voices/en_GB-alan-medium.onnx`.

## Writing the script

Put it in `.playwright-mcp/` (gitignored), such as `.playwright-mcp/walkthrough-rota.mjs`:

```js
export const meta = {
  title: 'Publishing the rota',        // title card, also read aloud
  subtitle: 'For managers',            // optional, defaults to "Lanyard walkthrough"
  start: '/rota',                      // opened before step 1 (not narrated)
};

export default [
  { say: 'The rota shows every shift for the week, grouped by site.' },
  { say: 'Drafts only you can see are shown faded. When the week is ready, press Publish.',
    do: async (page, h) => {
      await h.click(page.getByRole('button', { name: 'Publish' }));
    } },
  { say: 'Lanyard asks you to confirm, then sends everyone on the rota a notification.',
    do: async (page, h) => {
      await h.highlight(page.locator('fluent-dialog'));
    } },
];
```

**Narration.** Write for someone who has never seen the feature:
- Open with what the feature is for and who uses it, then go through the steps in the order a user would actually do them, and finish with the result ("…and the shift now shows on their phone").
- Use one or two short sentences per step, in plain words, and say what's on screen as it happens. Use the button and page names exactly as the UI shows them.
- Avoid code names, class names and "we added". Describe the feature, not the PR.
- Aim for 5–12 steps, under about 90 seconds in total.

**Actions.** `do` runs while its line is spoken, and the step lasts as long as whichever takes longer, so keep each action short. Use the helpers rather than raw `page.click`, because they move the cursor visibly and highlight the target:

| helper | does |
|---|---|
| `h.click(locator)` | highlight, glide the cursor over, click (the highlight clears after the click) |
| `h.fill(locator, text)` | click, then type visibly |
| `h.highlight(locator)` | box round it for the rest of the step |
| `h.point(locator)` | glide the cursor there, no click |
| `h.scroll(locator)` | smooth-scroll it to the middle |
| `h.goto(path)` | navigate (relative to `--base-url`) |
| `h.wait(ms)` | pause |

Raw `page` calls still work for anything else. The Fluent selector gotchas in the `verify` skill apply here too: use `getByRole('textbox', …)` for inputs, scope `fluent-option:visible`, and locate dialogs via `fluent-dialog`.

**Data.** Record against realistic data, not an empty page. Seed what the walkthrough needs first (postgres MCP or the UI), and check that the page looks right with a screenshot before recording.

## Recording

With the app running (see `verify` → Launch):

```bash
cd .claude/scripts/record-video
node walkthrough.mjs --script ../../../.playwright-mcp/walkthrough-rota.mjs \
  --out ../../../.playwright-mcp/desktop-walkthrough-rota.mp4 --viewport 1440x900
```

- Sign-in happens before recording, so the video opens already signed in, as `admin` in the first company. Use `LANYARD_USER`, `LANYARD_PASSWORD` or `LANYARD_COMPANY` to change that (a staff-eye view, for example), and `--no-login` for public pages.
- `--base-url http://localhost:5197` if your server isn't on 5096.
- A run takes roughly the video's length plus about 30s. It prints the duration and size, and warns over 10 MiB.
- On failure it saves `<out>.failed.png`, showing the page at the moment the failing step gave up. `Read` it, fix the locator and re-run.

**Check it before publishing.** Pull a few frames and `Read` them, making sure the captions are readable and the highlight is on the right control:

```bash
for t in 2 10 20 30; do ffmpeg -v error -y -ss $t -i .playwright-mcp/desktop-walkthrough-rota.mp4 -frames:v 1 .playwright-mcp/frame-$t.png; done
```

## Publishing

Add it to the same manifest as the screenshots and run `publish-screenshots.sh` as usual (see `verify` → Publishing to the PR):

```json
{ "file": ".playwright-mcp/desktop-walkthrough-rota.mp4", "viewport": "desktop", "caption": "Walkthrough: publishing the rota (with narration)" }
```

The publish script also makes a silent, looping GIF preview of it, which plays inline in the PR description (subtitles are drawn into the picture, so it makes sense without sound). The preview links to GitHub's file viewer, where the full video plays with sound. A real inline `<video>` player isn't possible, because GitHub strips the tag for anything it didn't host itself. Mention the walkthrough in the final chat message too. Clean up afterwards with `rm -f .playwright-mcp/*.mp4 .playwright-mcp/frame-*.png`.

## Gotchas

- **The shared `lanyarddb` can be behind your branch's migrations** (errors like `column s.CompanyId does not exist`, and the login page stuck on "Loading companies…"). Other sessions use that database, so don't migrate it without asking. Copy it and point your server at the copy instead:
  ```bash
  docker exec lanyard-postgres psql -U lanyard_dev -d postgres -c "CREATE DATABASE lanyard_walkthrough"
  docker exec lanyard-postgres sh -c "pg_dump -U lanyard_dev lanyarddb | psql -q -U lanyard_dev lanyard_walkthrough"
  export ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=lanyard_walkthrough;Username=lanyard_dev;Password=lanyard_dev_password"
  dotnet ef database update --project src/Lanyard.Infrastructure --startup-project src/Lanyard.Server/LanyardApp --no-build
  ```
  Then launch the server from the same shell. Drop the copy when finished.
- The overlays are injected on every page load, so full navigations are fine. The caption survives them via `sessionStorage`.
- Pronunciation: Piper can mangle acronyms and odd names. Write the `say` text as it should sound ("D M X" rather than "DMX"). Subtitles show that text too, so keep it readable.
