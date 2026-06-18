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

### The recommendation loop

The plugin runs a **two-round LLM + TMDB feedback loop** each sync cycle:

**Round 1**
1. Collects each user's watch history (up to 30 items) and their top genres.
2. Sends a compact prompt to the LLM asking for ~15–20 candidates (movies and shows).
3. Verifies every candidate against TMDB in parallel (up to 8 concurrent lookups).
4. Confirmed titles (resolved TMDB ID) go into the confirmed list; unresolved titles go into the "not found" list.

**Round 2** (only if the target isn't met)
1. Sends a follow-up prompt including the list of titles that failed TMDB lookup so the LLM avoids re-suggesting them.
2. Asks for exactly the remaining deficit.
3. Verified results are added to the confirmed list.

The target is **10 movies + 10 shows** per user (configurable via `MaxRecommendationsPerType`).

### What the LLM receives

The prompt is kept deliberately short to minimize latency and cost:

```
Suggest exactly N movies/shows this user would enjoy. Return ONLY valid JSON.

Genres: Action, Drama, Sci-Fi
Watched: Inception (2010, movie), Breaking Bad (2008, series), ...
Skip (already owned): The Matrix, Interstellar, ...

Rules: real titles only (must exist on TMDB), mix movies+series, vary eras, be specific with year.

JSON format: {"recommendations":[{"title":"Name","year":2020,"type":"movie","reason":"one line why"}]}
```

- Watch history: up to 25 most recent items, comma-separated
- Exclude list: up to 100 owned titles, comma-separated
- No bullet points, no verbose instruction blocks

### TMDB verification

Every LLM suggestion is validated against The Movie Database API before it goes into a library. This prevents the LLM from hallucinating titles that don't exist. Titles that fail lookup are fed back into round 2 so the LLM knows not to re-suggest them.

### Virtual libraries

Confirmed recommendations are written as `.strm` stub files into per-user folders:

```
{data}/virtual/{userId}/movies/{Title} ({Year}) [tmdbid-{id}]/{Title} ({Year}) [tmdbid-{id}].strm
{data}/virtual/{userId}/shows/{Title} [tmdbid-{id}]/Season 1/{Title} - S01E01 [tmdbid-{id}].strm
```

Jellyfin's metadata scanner picks up the TMDB ID from the filename and pulls full artwork, descriptions, and ratings. The files aren't playable — they're metadata hooks only.

**Per-user visibility**: after provisioning, the plugin reconciles library access so each user only sees their own recommendation folders. Users who had "Enable all folders" are demoted to an explicit list that excludes other users' AI libraries.

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

- `GET /AIRecommendations/Status` — last sync time and message
- `POST /AIRecommendations/Sync` — trigger sync for all users
- `POST /AIRecommendations/Sync/{userId}` — sync one user
- `POST /AIRecommendations/TestProvider` — test LLM connection from config UI

## Releases

Tag `v1.0.x` to trigger the release workflow, which builds the ZIP, computes the MD5 checksum, creates a GitHub Release, and updates `manifest.json` on `main` automatically.

## Git hooks

Auto-bump patch version on push:

```powershell
.\scripts\setup-hooks.ps1
```

- **pre-push** — bumps patch version, builds, syncs checksum, commits, then pushes

Skip once: `$env:SKIP_VERSION_BUMP=1; git push`

Manual bump: `.\scripts\bump-version.ps1 -Part minor`
