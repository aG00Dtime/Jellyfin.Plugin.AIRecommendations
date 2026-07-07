# AGENT.md — Architecture reference for AI agents

This document is the authoritative guide for understanding and modifying this codebase. Read it before making any changes. See `CLAUDE.md` for build commands and workflow instructions.

---

## What this project is

**Jellyfin.Plugin.AIRecommendations** is a Jellyfin plugin that acts as a personal AI media agent. It has two main surfaces:

1. **Chat bots** — Telegram and Discord bots that let linked users chat with an LLM-powered agent to get personalised recommendations, search their library, and request downloads from Jellyseerr / Radarr / Sonarr.
2. **Recommendation libraries** — each user gets two private Jellyfin libraries ("AI Movie Picks" and "AI Show Picks") populated automatically with stub files the LLM chose for them.

Both surfaces share the same underlying agent loop, taste profiles, and LLM provider configuration.

---

## Directory structure

```
Jellyfin.Plugin.AIRecommendations/
│
├── Plugin.cs                        ← Entry point; registers plugin metadata
├── PluginServiceRegistrator.cs      ← DI container setup (all services + HTTP clients)
├── PluginStartupService.cs          ← Hosted service; delays first library provision 30 s
│
├── Api/
│   └── RecommendationsController.cs ← 18 admin REST endpoints (sync, links, taste profiles)
│
├── Configuration/
│   ├── PluginConfiguration.cs       ← All persisted settings; XML-serialised by Jellyfin
│   └── configPage.html              ← Admin UI (vanilla JS, fetch API, Jellyfin ApiClient)
│
├── Models/
│   └── Models.cs                    ← Shared DTOs (ResolvedRecommendation, TmdbCandidate, etc.)
│
├── Providers/
│   ├── ILlmProvider.cs              ← LLM provider interface
│   ├── OpenAiProvider.cs            ← OpenAI + OpenRouter (tool-calling + RAG)
│   └── OllamaProvider.cs            ← Ollama local/cloud
│
├── Services/
│   ├── RecommendationEngine.cs      ← Orchestrates TMDB Discover → LLM → resolve cycle
│   ├── RecommendationSyncService.cs ← Full sync: taste profiles, gen, stubs, cleanup
│   ├── TasteProfileService.cs       ← Per-user LLM narrative from watch history
│   ├── WatchHistoryService.cs       ← Reads Jellyfin watch data; builds taste/exclusion sets
│   ├── LibraryFilterService.cs      ← Owned/watched TMDB IDs; library search
│   ├── LibraryPermissionManager.cs  ← Per-user folder permissions on AI libraries
│   ├── VirtualLibraryManager.cs     ← Creates/manages per-user virtual libraries in Jellyfin
│   ├── VirtualItemWriter.cs         ← Writes NFO + STRM stub files; manages 50-item cap
│   ├── JellyseerrService.cs         ← Submit/check download requests via Jellyseerr API
│   ├── ArrRequestService.cs         ← Direct Radarr v3 / Sonarr v3 HTTP wrapper
│   ├── FavouriteWatcher.cs          ← Watches heart toggles; immediately submits requests
│   └── NtfyNotifierSuppressor.cs    ← Silences NtfyNotifier during library scans
│
├── Tasks/
│   └── RecommendationSyncTask.cs    ← Jellyfin scheduled task interface
│
├── Telegram/
│   ├── TelegramBotService.cs        ← Long-polling hosted service; session/link management
│   ├── TelegramAgentLoop.cs         ← Shared agentic tool-calling loop (used by both bots)
│   ├── TelegramModels.cs            ← TelegramUserLink, ConversationSession, etc.
│   └── DownloadStatusPoller.cs      ← Background poll; notifies Telegram when downloads land
│
└── Discord/
    ├── DiscordBotService.cs          ← WebSocket Gateway hosted service; DM handling
    ├── DiscordDownloadStatusPoller.cs← Background poll; notifies Discord when downloads land
    └── DiscordModels.cs              ← DiscordUserLink, PendingDiscordLinkCode
```

---

## Configuration model

All settings live in `Configuration/PluginConfiguration.cs` and are XML-serialised automatically by Jellyfin to:

```
/media/configs/jellyfin/plugins/configurations/Jellyfin.Plugin.AIRecommendations.xml
```

**Key sections:**

| Section | Key fields |
|---|---|
| LLM provider | `ActiveProvider`, `OpenAiApiKey`, `OpenAiModel`, `OpenRouterApiKey`, `OllamaBaseUrl`, `OllamaModel` |
| TMDB | `TmdbApiKey`, `DiscoverLanguage`, `IncludeAdult` |
| Sync behaviour | `SyncIntervalHours`, `MaxRecommendationsPerType`, `AlwaysRefreshRecommendations`, `LimitShowsToSeasonOne` |
| Taste profile | `TasteProfileRegenerationIntervalDays` |
| Download services | `JellyseerrBaseUrl/ApiKey`, `RadarrBaseUrl/ApiKey/QualityProfileId/RootFolderPath`, same for Sonarr |
| Telegram | `TelegramBotToken`, `TelegramDownloadPollIntervalMinutes`, `TelegramUserLinks` |
| Discord | `DiscordBotToken`, `DiscordUserLinks` |
| Per-user state | `UserLibraries` (List<UserLibraryRegistration>) — paths, library IDs, TMDB tracking lists |
| Sync status | `LastSyncUtc`, `LastSyncMessage` |

**`UserLibraryRegistration`** — one per Jellyfin user:

| Field | Purpose |
|---|---|
| `UserId` | Jellyfin user GUID as string |
| `MovieLibraryId`, `ShowLibraryId` | Jellyfin virtual library GUIDs |
| `MoviePath`, `ShowPath` | Disk paths for stub folders |
| `RejectedTmdbIds` | Permanent reject list (marked-watched stubs) |
| `RequestedTmdbIds` | Items sent to download services |
| `PlacedTmdbIds` | Currently on-disk stubs (used to detect user-deleted stubs) |
| `TasteProfileText` | LLM-generated narrative |
| `TasteProfileGeneratedAt` | Timestamp for auto-refresh interval |

**Rule:** always call `Plugin.Instance!.SaveConfiguration()` after mutating the config object. The XML is only written on explicit save calls.

---

## Data flow: recommendation sync

```
RecommendationSyncTask.ExecuteAsync()
  └─ RecommendationSyncService.SyncAllUsersAsync()
       │
       ├─ Guard: skip if interval not elapsed (unless force=true)
       │
       ├─ VirtualLibraryManager.ProvisionAllUsersAsync()
       │    └─ Creates missing movie/show libraries for each user
       │
       └─ for each user → SyncUserAsync()
            │
            ├─ ClearStubShowEpisodePlayedStates()      ← reset inherited played state
            ├─ [if AlwaysRefreshRecommendations] PlacedTmdbIds.Clear()
            ├─ DetectAndRejectDeletedStubs()            ← stub missing from disk → reject
            ├─ ProcessUserFeedbackAsync()               ← ❤️ → request, 👎 → reject
            ├─ TasteProfileService.RefreshIfNeededAsync()
            │
            ├─ Build extraExcludeIds:
            │    RejectedTmdbIds + RequestedTmdbIds
            │    + on-disk TMDB IDs (accumulate mode only)
            │
            ├─ [if shouldGenerate] RecommendationEngine.GenerateForUserAsync()
            │    ├─ WatchHistoryService.BuildTasteProfile()  → owned/watched/excluded IDs
            │    ├─ TMDB Discover per top genre (RAG)
            │    └─ LLM picks from candidate list
            │
            ├─ VirtualItemWriter.SyncRecommendations()
            │    ├─ RemoveStaleStubs()   ← delete rejected/requested/owned
            │    ├─ EnsureShowEpisodeStubs()  ← migration / upgrade
            │    └─ WriteMovie() / WriteShow() up to 50-item cap per type
            │
            └─ registration.PlacedTmdbIds = placedIds

  └─ LibraryManager.ValidateMediaLibrary()   ← Jellyfin scans new stubs
  └─ Post-scan: ClearStubShowEpisodePlayedStates() for all users
```

**`shouldGenerate` logic (accumulate mode):**
```
shouldGenerate = force
    OR AlwaysRefreshRecommendations
    OR no stubs exist on disk
    OR syncAgeHours >= SyncIntervalHours
```

---

## Data flow: agent conversation

```
User DM (Telegram or Discord)
  │
  ▼
TelegramBotService / DiscordBotService
  │
  ├─ /link, /unlink, /reset, /profile → handle directly, return
  ├─ not linked → "Send /link to get started"
  └─ linked → TelegramAgentLoop.RunAsync(jellyfinUserId, message, onStatus, ct)
       │
       ├─ Build system prompt (taste profile + services + rules)
       ├─ Append user message to ConversationSession.History
       │
       └─ for round in 0..4 (MaxToolRounds=5):
            POST /chat/completions
              ├─ tool_calls → ExecuteToolAsync() → append result → next round
              └─ content → sanitize/convert → return to bot → send to user

ConversationSession (in-memory, per user per platform):
  - History: capped at 20 messages, trimmed from oldest
  - Expires: 60 min idle
  - Lost on server restart (by design)
```

**Agent tools:**

| Tool | What it calls |
|---|---|
| `search_library` | `LibraryFilterService.SearchByTitle` |
| `discover_content` | `TmdbMetadataService.DiscoverAsync` + owned-ID post-filter |
| `search_content` | `TmdbMetadataService.SearchMultiAsync` |
| `request_media` | `JellyseerrService` (primary) → `ArrRequestService` (fallback) |
| `check_status` | Jellyfin + Jellyseerr + Radarr + Sonarr |
| `sync_to_jellyfin` | `RecommendationSyncService.SyncUserAsync` |
| `get_ai_recommendations` | `LibraryFilterService.GetAiRecommendations` |
| `browse_tmdb` | `TmdbMetadataService.BrowseTmdbAsync` |
| `get_recently_added` | `LibraryFilterService.GetRecentlyAdded` |
| `refresh_taste_profile` | `TasteProfileService.ForceRefreshAsync` |

---

## Discord Gateway implementation

`DiscordBotService` implements the Discord Gateway protocol from scratch using `System.Net.WebSockets.ClientWebSocket`. No external Discord library is used.

**Intent:** `DIRECT_MESSAGES = 1 << 12 = 4096`. Not privileged — no toggle needed in the Developer Portal.

**Gateway state machine:**
```
GatewayLoopAsync (outer reconnect loop)
  └─ RunGatewaySessionAsync (one WebSocket session)
       ├─ Op 10 HELLO        → start HeartbeatLoopAsync, send IDENTIFY
       ├─ Op 0 DISPATCH
       │    ├─ READY          → save sessionId, resumeGatewayUrl
       │    └─ MESSAGE_CREATE → HandleMessageAsync
       ├─ Op 7 RECONNECT     → close + reconnect (RESUME)
       ├─ Op 9 INVALID_SESSION → close + reconnect (fresh IDENTIFY)
       └─ Op 11 HEARTBEAT_ACK → acknowledged
```

**Resume vs IDENTIFY:** On reconnect, if `_sessionId` is set, send RESUME (preserves missed events). Otherwise send fresh IDENTIFY.

**REST calls:** All use `Authorization: Bot {token}` header. Named HTTP client: `"DiscordBot"` (30 s timeout).

**Message formatting:** The agent produces HTML. Discord receives Markdown. `HtmlToMarkdown()` converts `<b>→**`, `<i>→*`, `<code>→backtick`, strips rest.

---

## Telegram implementation

`TelegramBotService` uses HTTP long-polling (`getUpdates` with 25 s timeout). Stateless — no persistent connection or session ID.

**Message formatting:** The agent produces HTML. Telegram receives Telegram HTML. `SanitizeTelegramHtml()` normalises tags, strips attributes, closes dangling tags.

---

## Download status notifications

Two separate pollers run on the same interval (`TelegramDownloadPollIntervalMinutes`, default 15 min):
- `DownloadStatusPoller` → Telegram
- `DiscordDownloadStatusPoller` → Discord

**Poll logic per linked user:**
1. For each `tmdbId` in `reg.RequestedTmdbIds`
2. Skip if `link.NotifiedAvailableTmdbIds` already contains it
3. Check if `LibraryFilterService.GetOwnedTmdbIds()` contains it (ground truth)
4. If yes, send notification + add to `NotifiedAvailableTmdbIds` + `SaveConfiguration()`

**At link creation:** `NotifiedAvailableTmdbIds` is pre-seeded with the intersection of `RequestedTmdbIds` and currently-owned IDs. This prevents flooding a newly-linked user with notifications for content already in the library.

---

## Stubs: how virtual libraries work

Each recommendation is a folder on disk:

**Movie:**
```
MoviePath/
  Title (Year) [tmdbid-12345]/
    Title (Year) [tmdbid-12345].strm    ← JustWatch search URL (not playable)
    Title (Year) [tmdbid-12345].nfo     ← TMDB ID, title, plot, tagline
```

**Show:**
```
ShowPath/
  Title [tmdbid-12345]/
    tvshow.nfo                          ← TMDB ID, title, plot
    Season 01/
      Title - S01E01.strm
      Title - S01E01.nfo                ← lockdata=true, no TMDB ID (prevents played-state inheritance)
```

**Cap:** 50 per type per user. `VirtualItemWriter.SyncRecommendations` only adds new stubs when slots are available.

**Dismissal:** marking a stub series as watched → `FavouriteWatcher` or next sync's `ProcessUserFeedbackAsync` detects it → adds to `RejectedTmdbIds` → next sync removes the folder.

---

## LLM providers

All providers implement `ILlmProvider` and use OpenAI-compatible chat completions (`/chat/completions`).

**RAG mode (default):** TMDB Discover fetches real candidates per genre → LLM picks from the list by returning `tmdbId` values. Prevents hallucination.

**Fallback mode:** If Discover yields < 5 candidates, falls back to free-form generation with a 2-round TMDB verification loop.

**Tool-calling requirement:** The agent loop requires tool-calling support (`tool_calls` in responses). Ollama works only with models that support function calling (e.g. `llama3.1`, `mistral-nemo`). Models without it loop until `MaxToolRounds` is exhausted.

---

## How to add a new feature

### New agent tool
1. Add tool JSON definition in `TelegramAgentLoop.cs` (`_toolDefinitions` static list)
2. Add `case "tool_name":` in `ExecuteToolAsync`
3. Return a plain `string` (JSON-serialise complex objects)
4. Update system prompt rules if the tool needs usage guidance

### New config setting
1. Add property to `PluginConfiguration.cs`
2. Add `<input>` / `<select>` in `configPage.html` save and load blocks
3. No migration needed — Jellyfin defaults new XML elements to C# defaults

### New bot command (Telegram or Discord)
1. Handle in the respective `Handle*Async` method, before the agent dispatch
2. Return early (don't call `TelegramAgentLoop.RunAsync`) for pure command responses

### New download service integration
1. Add HTTP client in `PluginServiceRegistrator.cs`
2. Add URL + API key fields to `PluginConfiguration.cs`
3. Add check logic in `ArrRequestService.cs`
4. Add `CheckAvailableAsync` calls in both pollers
5. Add UI in `configPage.html`

### New REST endpoint
1. Add method to `RecommendationsController.cs` with `[Authorize(Policy = "RequiresElevation")]`
2. All endpoints are admin-only; there is no user-facing API

---

## Key invariants — do not break

| Invariant | Why |
|---|---|
| AI stub paths excluded from `GetOwnedTmdbIds()` | Stubs would block the recommendations they're meant to represent |
| `PlacedTmdbIds` must reflect what's actually on disk | `DetectAndRejectDeletedStubs` uses this to detect user-initiated deletions |
| `NotifiedAvailableTmdbIds` pre-seeded at link creation | Prevents notification flood on first poll for newly-linked users |
| Episode NFO has no TMDB ID | Prevents played-state inheritance across stub instances |
| `MaxToolRounds = 5` | Hard cap; every tool case must return a string (never throw silently) |
| `SaveConfiguration()` after every mutation | Config is only persisted on explicit save; lost on restart otherwise |
| Discord IDs as strings in API responses | JavaScript loses precision on ulong snowflake IDs > 2^53 |

---

## Assets

- `agent.png` — mascot image for the agent (not currently used in plugin UI)
- `manifest.json` — Jellyfin plugin catalog manifest; updated automatically by CI after each release
- `VERSION.txt` — single source of truth for the version number; synced to `.csproj` by the pre-push hook
