# Jellyfin.Plugin.AIRecommendations

> **Work in progress** — early preview, not production-ready. Expect breaking changes.

Jellyfin plugin that builds per-user movie and TV recommendation libraries from watch history. Uses an LLM (OpenAI, OpenRouter, or Ollama) for suggestions and TMDB for metadata. Recommendations appear as normal libraries on all clients — each user only sees their own.

Requires Jellyfin **10.11+** (use plugin **v1.0.1** if you are on **10.10.x**).

> If status shows **NotSupported**, your Jellyfin version does not match the plugin build. Check **Dashboard → Help** for your server version, then install the matching plugin version from the catalog.

## Plugin catalog

Add this repository in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/main/manifest.json
```

---

## How it works

### Taste profile

Before calling the LLM, the plugin builds a compact taste profile from each user's Jellyfin watch history:

| Field | What it captures |
|---|---|
| **Top genres** | Up to 6 genres ranked by play count |
| **Era preference** | "mostly modern", "mix of classic and modern", or "mostly classic (pre-2000s)" |
| **Movie/show ratio** | % of watch history that is movies vs series |
| **Sample titles** | 8 titles sampled evenly across watch history (not just the most recent) |
| **Favourite titles** | Up to 5 items the user has marked as a favourite in Jellyfin |

This profile — not a raw item list — is what gets sent to the LLM. It is small, cheap to transmit, and gives the model enough signal to make personalised picks.

### Exclusion sets

Before generating recommendations, the plugin assembles three exclusion sets so the LLM is never asked to suggest something the user already has or has dismissed:

- **Watched** — TMDB IDs of every played movie, plus every series with at least one played episode (including partial watches)
- **Owned** — TMDB IDs of every real (non-stub) item in the library, excluding the AI recommendation folders themselves so stubs don't self-block
- **Rejected / Requested** — IDs the user has permanently dismissed or already requested via Jellyseerr

### The recommendation loop

The plugin runs a **two-round LLM + TMDB verification loop** each sync:

**Round 1**
1. Builds the exclusion sets and taste profile described above.
2. Sends a compact prompt asking for `N` candidates (typically 25 total — movies + shows).
3. Resolves every suggestion against TMDB in parallel (up to 8 concurrent requests).
4. Confirmed titles (TMDB ID found) go into the result list; unresolved titles go into a "not found" list.

**Round 2** (only if the target count isn't met)
1. Sends a follow-up prompt that includes the failed titles so the LLM knows to avoid re-suggesting them.
2. Asks for exactly the remaining deficit.
3. Verified results are merged into the confirmed list.

The target is **10 movies + 10 shows** per user by default (configurable).

### What the LLM receives

```
Suggest exactly N movie/show recommendations for this user. Return ONLY valid JSON.

User taste profile:
  Total watched: 87
  Top genres: Action (34), Drama (28), Sci-Fi (19), Thriller (12), Comedy (8), Animation (5)
  Era preference: mostly modern (2000s–2020s)
  Mix: 60% movies, 40% shows
  Sample titles: Inception, Breaking Bad, The Witcher, Dune, Severance, Parasite, Oppenheimer, Succession
  Favourites: Interstellar, The Wire

Already watched (exclude): ...
Already owned (exclude): ...

Rules: real titles only (must exist on TMDB), not in the exclude lists, vary genres and eras.
JSON: {"recommendations":[{"title":"...","year":2020,"type":"movie","reason":"one line"}]}
```

### Virtual library stubs

Confirmed recommendations are written as `.strm` + `.nfo` stub folders:

```
{data}/virtual/{userId}/movies/{Title} ({Year}) [tmdbid-{id}]/
    {Title} ({Year}) [tmdbid-{id}].strm    ← JustWatch search URL
    {Title} ({Year}) [tmdbid-{id}].nfo     ← TMDB ID + AI reason + plot

{data}/virtual/{userId}/shows/{Title} [tmdbid-{id}]/
    tvshow.nfo
    Season 1/{Title} - S01E01 [tmdbid-{id}].strm
```

Jellyfin's scanner picks up the TMDB ID from the filename and pulls full artwork, cast, ratings, and descriptions. The `.strm` files are not playable — they're metadata hooks only.

Stubs accumulate up to **50 per type** (movies / shows). On each sync only rejected and requested stubs are removed; the rest persist until the user acts on them.

**Per-user visibility**: the plugin reconciles library permissions after provisioning so each user only sees their own AI libraries. Users with "enable all folders" are demoted to an explicit allowlist that excludes other users' AI folders.

### User feedback loop

Once stubs appear in Jellyfin, the user has three actions:

| Action | What happens |
|---|---|
| ❤️ **Favourite** the item | Jellyseerr request is submitted **immediately** (no waiting for the next sync). TMDB ID is recorded as "requested" and the stub is removed on the next sync. |
| 🗑️ **Delete** the item in Jellyfin | Detected on the next sync by comparing what was placed vs what is on disk. TMDB ID is permanently rejected and will never be suggested again. |

The `FavouriteWatcher` service subscribes to Jellyfin's `UserDataSaved` event and fires the Jellyseerr request in under a second of the heart being tapped, without waiting for the scheduled sync.

---

## Configuration

1. Open **Dashboard → Plugins → AI Recommendations**.
2. Choose an LLM provider and enter your API key.
3. Enter a [TMDB API key](https://www.themoviedb.org/settings/api).
4. Save, then run **Dashboard → Scheduled Tasks → AI Recommendations Sync**.

Each user gets two libraries: `{Username}'s AI Movie Picks` and `{Username}'s AI Show Picks`.

### Providers

| Provider | Notes |
|---|---|
| **OpenAI** | Default model: `gpt-4o-mini`. Fast, reliable, costs ~$0.001/sync/user. |
| **OpenRouter** | Default model: `openai/gpt-4o-mini`. Access many models with one key. |
| **Ollama (local)** | Runs locally. Default base URL: `http://localhost:11434`. |
| **Ollama (cloud)** | Set Deployment = Cloud, base URL `https://ollama.com`, API key from ollama.com. Default model: `gemma3:27b`. |

### Ollama Cloud model names

Use the exact model tag from `https://ollama.com/models`. Free-tier models that work well:
- `gemma3:27b` — good quality, free tier
- `gemma3:4b` — faster, free tier
- `gpt-oss:20b` — OpenAI-compatible, free tier

The `:cloud` suffix is for local Ollama offloading — do not use it here.

### Key settings

| Setting | Default | Description |
|---|---|---|
| `MaxRecommendationsPerType` | 10 | Movies per user + shows per user |
| `MaxWatchedItems` | 30 | Watch history items sent to LLM |
| `SyncIntervalHours` | 24 | How often the scheduled task runs |
| `LimitShowsToSeasonOne` | true | Only write Season 1 stub (faster scans) |

---

## Build

```powershell
.\build.ps1
```

Linux/macOS: `./build.sh`

## Install

### From Jellyfin catalog

Use the repository URL above, then install **AI Recommendations (WIP)** from the catalog.

### From GitHub Release (manual)

Download `Jellyfin.Plugin.AIRecommendations.zip` from [Releases](https://github.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/releases) and extract into your Jellyfin plugins folder.

## Admin API

Admin only:

| Method | Path | Description |
|---|---|---|
| `GET` | `/AIRecommendations/Status` | Last sync time and message |
| `POST` | `/AIRecommendations/Sync` | Trigger sync for all users |
| `POST` | `/AIRecommendations/Sync/{userId}` | Sync one user |
| `GET` | `/AIRecommendations/Users` | List registered users and stub counts |
| `GET` | `/AIRecommendations/Recommendations/{userId}` | List current stubs on disk for a user |
| `POST` | `/AIRecommendations/Dismiss/{userId}/{tmdbId}` | Permanently reject a TMDB ID and delete its stub |
| `POST` | `/AIRecommendations/Clear` | Delete all stubs and reset state for all users |
| `POST` | `/AIRecommendations/Clear/{userId}` | Delete all stubs and reset state for one user |
| `POST` | `/AIRecommendations/TestProvider` | Test LLM connection from config UI |

## Releases

Every `git push` to `main` triggers the pre-push hook, which bumps the patch version, commits the change, and pushes a `v1.0.x` tag. That tag triggers the GitHub Actions release workflow, which:

1. Builds the release ZIP on a clean Ubuntu runner
2. Computes the MD5 checksum
3. Creates a GitHub Release with the ZIP attached
4. Updates `manifest.json` on `main` with the real checksum

## Git hooks

Install hooks:

```powershell
.\scripts\setup-hooks.ps1
```

- **pre-push** — bumps patch version in `VERSION.txt` + `.csproj`, adds a manifest entry with the release URL, commits, and pushes the `v{version}` tag. CI does the actual build, checksum, and release.
- **pre-commit** — verifies that `VERSION.txt`, `AssemblyVersion`, and `manifest.json` all agree on the same version number.

Skip version bump once: `$env:SKIP_VERSION_BUMP=1; git push`

Manual bump: `.\scripts\bump-version.ps1 -Part minor`
