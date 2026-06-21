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

The Telegram agent uses the same provider as the main plugin (including Ollama). No separate configuration needed.

---

## Download services

Any combination of the three services can be active at once. On a favourite, all configured services receive the request.

### Jellyseerr
Handles both movies and shows. Paste your Jellyseerr base URL and the API key from your profile page (top-right avatar → API Key tab).

### Radarr (movies)
Direct Radarr integration. Fill in:
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

### What the agent can do
- Recommend movies or TV shows based on your watch history
- Search TMDB for a specific title
- Request a download via Jellyseerr / Radarr / Sonarr
- Check if something is already downloading or available
- Refresh the AI recommendation stubs in your Jellyfin library (on request)

### Test without Telegram
The **Test Agent** panel in the plugin config lets you chat with the agent directly — no Telegram setup needed. Select a user, type a message, and press Enter or Send.

### Download status notifications
The bot can notify you in Telegram when a requested item becomes available. Configure the poll interval in the **Telegram** section (default: 15 minutes).

---

## User actions on stubs

| Action | What happens |
|---|---|
| **Heart / Favourite** | Submits a download request to all configured services. An on-screen toast confirms which services received it. The stub stays visible until the content arrives. |
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

---

## How it works

### Recommendation loop

```
Scheduled task / manual sync
  │
  ├─ Skip LLM if stubs exist and interval not elapsed (maintenance only)
  │
  ├─ Read watch history → build taste profile (genres, era, sample titles, favourites)
  │
  ├─ Build exclusion sets (watched, owned, rejected, already requested)
  │
  ├─ TMDB Discover → fetch real candidates per top genre (movies + shows in parallel)
  │
  ├─ LLM picks N items from the candidate list by TMDB ID
  │
  ├─ Write stubs to disk (.strm + .nfo per item)
  │
  ├─ Jellyfin library scan
  │
  └─ Recommendations visible on all clients
```

### Taste profile

| Field | Description |
|---|---|
| Top genres | Up to 6 genres ranked by play count |
| Era preference | "mostly modern", "mix", or "mostly classic (pre-2000s)" |
| Movie/show ratio | % of watch history that is movies vs series |
| Sample titles | 8 titles sampled evenly across watch history |
| Favourites | Up to 5 hearted items |

### Telegram agent tools

| Tool | What it does |
|---|---|
| `discover_content` | TMDB Discover by genre — fast, no extra AI call, used for "recommend me" requests |
| `search_content` | TMDB search by title — always called before `request_media` to get a verified ID |
| `request_media` | Submits to Jellyseerr / Radarr / Sonarr |
| `check_status` | Queries all configured services for download status |
| `sync_to_jellyfin` | Refreshes recommendation stubs in the Jellyfin library (on explicit request) |

### Stubs

Each recommendation is a folder containing a `.strm` file and a `.nfo` file. Jellyfin reads the TMDB ID from the folder name and fetches full artwork, cast, ratings, and descriptions automatically. The `.strm` files point to a JustWatch search URL and are not playable.

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
| `POST` | `/AIRecommendations/Telegram/Link` | Link a Telegram chat to a Jellyfin user |
| `GET` | `/AIRecommendations/Telegram/Links` | List linked Telegram accounts |
| `DELETE` | `/AIRecommendations/Telegram/Links/{chatId}` | Unlink a Telegram account |
| `POST` | `/AIRecommendations/Telegram/TestAgent` | Send a test message to the agent |
| `DELETE` | `/AIRecommendations/Telegram/TestAgent/{userId}` | Clear agent test session |

All endpoints require admin authentication.

---

For build instructions and development notes, see [DEVELOPMENT.md](DEVELOPMENT.md).
