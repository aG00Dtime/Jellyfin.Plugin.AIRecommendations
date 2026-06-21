# Jellyfin.Plugin.AIRecommendations

> [!WARNING]
> **WORK IN PROGRESS — BREAKING CHANGES EXPECTED**
>
> This plugin is in active development. Configuration format, API endpoints, file layouts, and behaviour **will change without notice** between versions. Upgrading may require clearing all plugin state and starting fresh. Do not use in production environments where stability matters.

Jellyfin plugin that gives each user a private AI-powered recommendation library, a Telegram bot assistant, and one-tap download requests via Jellyseerr, Radarr, and Sonarr.

Requires Jellyfin **10.11.9**.

---

## Install

Add this repository URL in **Dashboard > Plugins > Repositories**:

```
https://raw.githubusercontent.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/main/manifest.json
```

Install **AI Recommendations (WIP)** from the catalog and restart Jellyfin.

**Manual install:** Download the `.zip` from [Releases](https://github.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/releases), extract into your Jellyfin plugins folder, restart.

---

## What it does

- **Private recommendation libraries** — each user gets two Jellyfin libraries ("AI Movie Picks" and "AI Show Picks") populated with personalised stubs. Other users cannot see them.
- **One-tap downloads** — heart a stub to immediately request it through Jellyseerr, Radarr, or Sonarr. An on-screen toast confirms the request.
- **Mark as watched to dismiss** — marking a stub as played permanently rejects it and deletes it from disk.
- **Telegram bot** — chat with an AI assistant via Telegram to get recommendations, search titles, and request downloads without opening Jellyfin.
- **Smart sync** — stubs are not regenerated on every restart; the LLM is only called when the sync interval has elapsed or when a manual sync is triggered.

---

## Setup

1. Open **Dashboard > Plugins > AI Recommendations**.
2. Select an AI provider and paste in your API key.
3. Paste in a [TMDB API key](https://www.themoviedb.org/settings/api) (free account required).
4. Configure at least one download service (Jellyseerr, Radarr, or Sonarr).
5. Click **Save**.
6. Go to **Dashboard > Scheduled Tasks** and run **AI Recommendations Sync** to generate the first batch.

Each user gets two libraries created automatically on the first sync.

---

## AI model providers

| Provider | Notes |
|---|---|
| **OpenAI** | Default model: `gpt-4o-mini`. ~$0.001 per sync per user. |
| **OpenRouter** | Default model: `openai/gpt-4o-mini`. One key for many models. |
| **Ollama (local)** | Free, runs on your hardware. Default URL: `http://localhost:11434`. |
| **Ollama (cloud)** | Set Deployment to Cloud, base URL `https://ollama.com`. |

Use the **Test Connection** button to verify credentials before saving.

**Ollama model suggestions:**
- `gemma3:27b` — good quality, free tier
- `gemma3:4b` — faster, free tier

The Telegram agent uses the same provider as the main plugin. No separate configuration needed.

> **Note:** The Telegram agent requires an OpenAI-compatible tool-calling API. OpenAI and OpenRouter both work. Ollama works with models that support function calling (e.g. `llama3.1`, `mistral-nemo`). Models without function-calling support will result in the agent looping without producing results.

---

## Download services

Jellyseerr is the primary request method and routes internally to Radarr/Sonarr. Direct Radarr/Sonarr integration is only used if Jellyseerr is not configured.

### Jellyseerr
Handles both movies and shows. Paste your Jellyseerr base URL and the API key from your profile page (top-right avatar → API Key tab).

### Radarr (movies)
Direct Radarr integration — only used when Jellyseerr is absent. Fill in:
- Base URL and API key
- Quality profile — click **Fetch Profiles** to load options from your Radarr instance
- Root folder — click **Fetch Folders** to auto-fill

### Sonarr (shows)
Same structure as Radarr. Click **Fetch Profiles** and **Fetch Folders** after saving credentials.

---

## Telegram bot

### Setup
1. Create a bot via [@BotFather](https://t.me/botfather) and copy the token.
2. Paste it into the **Telegram** section of the plugin config and click **Test**.
3. Click **Save** — the bot starts immediately without a restart.

### Linking accounts
1. Send `/link` to your bot in Telegram — it replies with a 6-digit code (expires in 10 minutes).
2. In the plugin config, enter the code and select the Jellyfin user, then click **Link**.
3. The bot confirms in Telegram. You can now chat with it.

### Bot commands
| Command | Description |
|---|---|
| `/link` or `/start` | Generate a link code |
| `/unlink` | Remove the link between this Telegram chat and Jellyfin |
| `/reset` | Clear the current conversation and start fresh |
| `/profile` | Show your AI-generated taste profile narrative |

### What the agent can do
- Recommend movies or TV shows based on your watch history (library-aware — never suggests titles you already have)
- Search your Jellyfin library to check if a specific title is available
- Search TMDB for any title to get a verified ID
- Request a download via Jellyseerr / Radarr / Sonarr
- Check if something is already downloading or available across all configured services
- Refresh the AI recommendation stubs in your Jellyfin library (on request)
- Include wildcard picks from genres outside your usual taste to help you discover new things

### Test without Telegram
The **Test Agent** panel in the plugin config lets you chat with the agent directly — no Telegram setup needed. Select a user, type a message, and press Enter or Send.

### Download status notifications
The bot notifies you in Telegram when a requested item becomes available in your Jellyfin library. Configure the poll interval in the **Telegram** section (default: 15 minutes). On startup, items already in the library are silently seeded so you don't get duplicate "now available" notifications for things that arrived while the server was down.

---

## Taste profiles

### What a taste profile is
A taste profile is a 2–3 paragraph LLM-generated narrative about a user's media preferences, written by the AI after analysing their watch history, genres, era preference, and favourites. It reads like a personalised brief — something the LLM can absorb as context rather than a raw table of numbers.

Example excerpt:
> "David leans heavily toward tense, plot-driven content — his most-played genres are Crime, Thriller, and Drama, with a strong preference for films from the 2000s onward. Titles like *The Wire*, *Sicario*, and *No Country for Old Men* reflect a taste for morally complex storytelling and understated performances..."

### How it's generated
On each sync, if a user has no taste profile stored (or the profile is older than the configured regeneration interval), `TasteProfileService` reads their watch history via `WatchHistoryService`, sends it to the active LLM provider with a generation prompt, and stores the resulting narrative in `PluginConfiguration` (XML-persisted). This only happens once per interval — not on every sync.

### Where it's used
- **Telegram agent system prompt** — every conversation turn includes the full narrative so the agent understands the user before they say a word
- **Auto-sync recommendations** — the narrative is passed to the LLM when generating stub recommendations, replacing the raw stats summary for richer, more personalised picks
- **`/profile` command** — users can read their own taste profile in Telegram at any time

### Regeneration
| Trigger | Behaviour |
|---|---|
| Automatic (sync) | Generates only if profile is missing or older than the interval |
| Manual (API) | `POST /AIRecommendations/TasteProfile/{userId}/Refresh` forces regeneration |
| Bulk (API) | `POST /AIRecommendations/TasteProfile/Refresh` regenerates all users |
| Config interval | `TasteProfileRegenerationIntervalDays` — set to 0 to disable auto-regeneration |

If the profile hasn't been generated yet, the agent falls back to a live summary of watch stats (genres, era, sample titles) until the profile is created on the next sync.

---

## User actions on stubs

| Action | What happens |
|---|---|
| **Heart / Favourite** | Submits a download request to the configured services. An on-screen toast confirms which services received it. The stub stays visible until the content arrives. |
| **Mark as watched** | Permanently dismisses the item. The stub is deleted right away and the title will never be suggested again. |

Both actions take effect immediately — no sync required.

---

## Settings

| Setting | Default | Description |
|---|---|---|
| Content language | `en` | ISO 639-1 code to filter recommendations. Leave blank for all languages. |
| Max recommendations per type | 10 | How many movie stubs and show stubs to maintain per user. |
| Sync interval (hours) | 24 | How often recommendations refresh. The LLM is not called if stubs already exist and this interval hasn't elapsed. |
| Limit shows to Season 1 | on | Only writes a Season 1 stub per show. Speeds up library scans. |
| Always refresh recommendations | off | Replace all stubs on every sync instead of accumulating them. |
| Taste profile interval (days) | 7 | How often to regenerate the taste profile narrative. Set to 0 to disable. |

---

## How it works

### Recommendation loop

```
Scheduled task / manual sync
  │
  ├─ Skip LLM if stubs exist and interval not elapsed (maintenance only)
  │
  ├─ Regenerate taste profile if missing or stale (calls LLM once, stores narrative)
  │
  ├─ Read watch history → build exclusion sets (watched, owned, rejected, requested)
  │
  ├─ TMDB Discover → fetch candidates per top genre (movies + shows)
  │
  ├─ LLM picks N items from the candidate list using the taste profile narrative
  │
  ├─ Write stubs to disk (.strm + .nfo per item)
  │
  ├─ Jellyfin library scan
  │
  └─ Recommendations visible on all clients
```

### Exclusion sets

Items are excluded from recommendations at multiple layers:

| Exclusion | Source | Scope |
|---|---|---|
| Already in library | `LibraryFilterService.GetOwnedTmdbIds()` | Entire real library (AI stub paths excluded) |
| Already watched | `WatchHistoryService.GetWatchedTmdbIds()` | Fully-played movies and series; any series with a played episode |
| Rejected | `UserLibraryRegistration.RejectedTmdbIds` | Titles the user marked as watched on stubs |
| Requested | `UserLibraryRegistration.RequestedTmdbIds` | Titles already in the download queue |

AI stub paths are explicitly excluded from the owned and watched checks so virtual stub items don't block real recommendations.

### TMDB Discover (RAG mode)

The sync and agent both use TMDB's Discover API to fetch real, highly-rated candidates before calling the LLM. This is "retrieval-augmented generation" — the LLM picks from a verified list rather than hallucinating titles.

Key details:
- One request per top genre (TMDB Discover uses AND for multi-genre, so separate requests give better coverage)
- Sorted by `popularity.desc` with `vote_count.gte=50` to avoid obscure or low-quality results
- Rejected and requested IDs are excluded at fetch time; owned IDs are post-filtered to avoid starving the result set from TMDB's first page

### Stubs

Each recommendation is a folder containing a `.strm` file and a `.nfo` file. Jellyfin reads the TMDB ID from the folder name and fetches full artwork, cast, ratings, and descriptions automatically. The `.strm` files point to a JustWatch search URL and are not playable.

---

## Telegram agent architecture

### Overview

The Telegram agent is a tool-calling loop implemented as a lightweight harness around any OpenAI-compatible chat completions endpoint. It does not use the Anthropic API or any agent SDK — it's a plain while-loop that dispatches tool calls to existing plugin services.

```
User message
  │
  ▼
TelegramBotService (polling loop)
  │  looks up linked Jellyfin user
  │  manages session history
  │  sends "typing..." indicator every 4s
  │
  ▼
TelegramAgentLoop.RunAsync()
  │
  ├─ Build system prompt (taste profile + service list + rules)
  ├─ Append user message to history
  │
  └─ while (round < 5):
       POST /chat/completions with system + history + tool defs
       │
       ├─ tool_calls → execute tools → append results → continue loop
       │
       └─ content → sanitize HTML → return to user
```

### Session management

- Each linked Telegram chat has an in-memory `ConversationSession` holding a message `History` list
- History is capped at **20 messages** (trimmed from the oldest end) to keep LLM context costs bounded
- Sessions expire after **60 minutes of inactivity** — evicted on each incoming message
- Sessions are lost on server restart (by design — they are never persisted to disk)
- The test agent in the config UI maintains separate per-user test sessions independent of Telegram

### Tool-calling loop

The agent runs a maximum of **5 rounds** (`MaxToolRounds`) per user turn. Each round:

1. Sends the full system prompt + conversation history + tool definitions to the LLM
2. If the response contains `tool_calls`, each tool is dispatched to the appropriate service and the results are appended to history before the next round
3. Before each tool call, an `onStatus` callback fires with a brief human-readable message (e.g. `"🔍 Searching for Punisher..."`) which is sent to Telegram immediately, so the user sees progress during long operations
4. If the response contains only `content` (no tool calls), the text is sanitized and returned as the final reply
5. If all 5 rounds are exhausted without a plain-text reply, a fallback error message is returned

The loop is non-recursive — each round is a sequential `await` inside a `for` loop, not a stack of callbacks.

### Tools

| Tool | Args | What it does |
|---|---|---|
| `search_library` | `query` | Searches the user's real Jellyfin library by title (case-insensitive, partial match). Returns matches with year, type, and TMDB ID. |
| `discover_content` | `type`, `genres?`, `count?` | TMDB Discover by genre — fetches popular titles not already in the user's library. Used for all "recommend me" requests. Count is clamped to 5–10. |
| `search_content` | `query`, `type?`, `year?` | TMDB multi-search (movies + TV simultaneously). Returns up to 5 matches with an `in_library` flag. Must be called before `request_media` to get a verified TMDB ID. |
| `request_media` | `tmdb_id`, `title`, `type` | Submits a download request. Routes to Jellyseerr if configured; otherwise falls through to Radarr (movies) or Sonarr (TV). Records the TMDB ID in `RequestedTmdbIds` to suppress future recommendations. |
| `check_status` | `tmdb_id`, `type` | Queries Jellyfin library, Jellyseerr, Radarr, and Sonarr for the current status of a title. Returns a per-service status list. |
| `sync_to_jellyfin` | *(none)* | Triggers a full `RecommendationSyncService.SyncUserAsync` for the current user. Only called if the user explicitly requests it. |

### discover_content — library exclusion

`discover_content` fetches **4× the requested count** (minimum 20) from TMDB per genre to absorb the expected hit rate of items already in the library. Owned IDs are filtered **after** collection, not passed to TMDB as exclusions. This prevents TMDB's first-page results (sorted by popularity) from being exhausted by the exclusion set before enough candidates are collected.

Rejected and requested IDs are excluded at fetch time because they are typically small sets and represent content the user has actively acted on.

### search_content — multi-search

`search_content` uses TMDB's `search/multi` endpoint, which searches movies and TV shows simultaneously in a single request. If a `type` argument is provided, results of the other type are still fetched but sorted to the end. Results more than one year off from the requested `year` (if any) are discarded. Up to 5 matches are returned.

This makes the agent resilient to title-type ambiguity — searching "Spider-Man: Noir" without specifying `type=tv` still finds the show.

### request_media — routing

Jellyseerr is the primary request method. Direct arr integration is only used as a fallback:

```
Jellyseerr configured?
  YES → send to Jellyseerr only (it routes internally to Radarr/Sonarr)
  NO  → movie + Radarr configured → send to Radarr
        TV show + Sonarr configured → send to Sonarr
        nothing configured → return error
```

### System prompt

The system prompt is rebuilt on every turn from live state (current service configuration, current taste profile). It is never stored in conversation history. Key elements:

- **Taste profile** — full LLM-generated narrative if available; falls back to live genre/era/sample-title stats
- **Download services** — which services are currently active (Jellyseerr, Radarr, Sonarr, or "none configured")
- **Tool descriptions** — concise per-tool guidance that supplements the JSON schema
- **Rules** — explicit instructions: always call `discover_content` for recommendations, never filter or reorder tool results, include at least one wildcard pick, always call `search_content` before `request_media`, confirm before requesting

### Wildcard recommendations

The system prompt instructs the agent to include at least one "wildcard" recommendation per set — a pick from a genre clearly outside the user's usual taste. The agent is instructed to call `discover_content` a second time with a different genre to source this pick, rather than selecting from the primary results. Wildcards are labelled lightly in the reply so the user understands it's a stretch pick.

### HTML sanitization

Telegram's Bot API only accepts a restricted subset of HTML: `<b>`, `<i>`, `<u>`, `<s>`, `<code>`, `<pre>`, `<a href="...">`. Attributes on any other tag cause a 400 Bad Request. The LLM frequently outputs `<br>`, markdown formatting, or unclosed tags, all of which break the API.

`TelegramBotService.SanitizeTelegramHtml` handles this before every send:
1. Converts semantic tags (`<strong>` → `<b>`, `<em>` → `<i>`)
2. Converts block-level tags (`<br>`, `<p>`, `<li>`, etc.) to newlines
3. Strips attributes from the allowed inline tags (e.g. `<b class="...">` → `<b>`)
4. Strips all disallowed tags entirely (retaining their text content)
5. Calls `CloseDanglingTags` — a stack-based scanner that appends missing closing tags in reverse open order

### Download status notifications

`DownloadStatusPoller` is a background `IHostedService` that ticks on the configured interval. On each tick it checks every linked user's `RequestedTmdbIds` against Jellyfin's library. If an ID is now owned and hasn't been notified before, the bot sends an availability message and the ID is added to `NotifiedAvailableTmdbIds` (persisted in config) so it's never re-notified.

On startup, items already in the library are silently added to `NotifiedAvailableTmdbIds` without sending any message. This prevents "now available" spam for titles that arrived before the notification system existed or while the server was offline.

---

## Admin API

| Method | Path | Description |
|---|---|---|
| `GET` | `/AIRecommendations/Status` | Last sync time and message |
| `POST` | `/AIRecommendations/Sync` | Force sync for all users (always runs LLM) |
| `POST` | `/AIRecommendations/Sync/{userId}` | Force sync for one user |
| `GET` | `/AIRecommendations/Users` | List users and stub counts |
| `GET` | `/AIRecommendations/Recommendations/{userId}` | List stubs on disk for a user |
| `POST` | `/AIRecommendations/Dismiss/{userId}/{tmdbId}` | Permanently reject a TMDB ID |
| `POST` | `/AIRecommendations/Clear` | Delete all stubs and reset state for all users |
| `POST` | `/AIRecommendations/Clear/{userId}` | Delete all stubs and reset state for one user |
| `POST` | `/AIRecommendations/TestProvider` | Test AI provider connection |
| `GET` | `/AIRecommendations/ArrProfiles?service=Radarr\|Sonarr` | Fetch quality profiles |
| `GET` | `/AIRecommendations/ArrRootFolders?service=Radarr\|Sonarr` | Fetch root folders |
| `POST` | `/AIRecommendations/TasteProfile/Refresh` | Force regenerate taste profiles for all users |
| `POST` | `/AIRecommendations/TasteProfile/{userId}/Refresh` | Force regenerate taste profile for one user |
| `POST` | `/AIRecommendations/Telegram/Link` | Link a Telegram chat to a Jellyfin user |
| `GET` | `/AIRecommendations/Telegram/Links` | List linked Telegram accounts |
| `DELETE` | `/AIRecommendations/Telegram/Links/{chatId}` | Unlink a Telegram account |
| `POST` | `/AIRecommendations/Telegram/TestAgent` | Send a test message to the agent |
| `DELETE` | `/AIRecommendations/Telegram/TestAgent/{userId}` | Clear agent test session |

All endpoints require admin authentication.

---

For build instructions and development notes, see [DEVELOPMENT.md](DEVELOPMENT.md).
