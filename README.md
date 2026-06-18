# Jellyfin.Plugin.AIRecommendations

Jellyfin plugin that builds per-user movie and TV recommendation libraries from watch history. Uses an LLM (OpenAI, OpenRouter, or Ollama) for suggestions and TMDB for metadata. Recommendations appear as normal libraries on all clients.

Requires Jellyfin 10.9+ and .NET 8 SDK.

## Build

```powershell
.\build.ps1
```

Linux/macOS: `./build.sh`

Output: `bin/Release/net8.0/Jellyfin.Plugin.AIRecommendations.dll`

## Install locally

```powershell
.\deploy-local.ps1
```

Copies the DLL to `%AppData%\Jellyfin\Server\plugins\AI Recommendations_1.0.0.0\`. Restart Jellyfin, then open **Dashboard → Plugins → AI Recommendations**.

## Plugin catalog

Add this repository in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/main/manifest.json
```

## Configuration

1. Set LLM provider and API key (OpenAI, OpenRouter, or Ollama).
2. Set a [TMDB API key](https://www.themoviedb.org/settings/api).
3. Run **Dashboard → Scheduled Tasks → AI Recommendations Sync**.

Each user gets `{Username}'s AI Movie Picks` and `{Username}'s AI Show Picks` libraries.

Ollama Cloud: deployment = Cloud, base URL `https://ollama.com`, API key from ollama.com.

## Admin API

Admin only:

- `GET /AIRecommendations/Status`
- `POST /AIRecommendations/Sync`
- `POST /AIRecommendations/Sync/{userId}`

## Releases

Tag `v1.0.0` (etc.) to trigger the release workflow and publish a zip to GitHub Releases.

## Git hooks

Auto-bump patch version on push:

```powershell
.\scripts\setup-hooks.ps1
```

- **pre-commit** — blocks commit if `VERSION.txt`, `.csproj`, and `manifest.json` are out of sync
- **pre-push** — bumps patch version, commits, then pushes

Skip once: `$env:SKIP_VERSION_BUMP=1; git push`

Manual bump: `.\scripts\bump-version.ps1 -Part minor`
