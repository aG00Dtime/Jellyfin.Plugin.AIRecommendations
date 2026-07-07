# CLAUDE.md — AI assistant instructions for this repo

This file tells Claude Code and other AI coding assistants how to work effectively in this codebase.

---

## What this project is

A Jellyfin plugin (C#, .NET 9, `IHostedService` pattern) that acts as an AI media agent: it maintains per-user recommendation libraries populated by an LLM, and lets users chat with that agent over Telegram or Discord DMs to get personalised recommendations and request downloads.

See `AGENT.md` for the full architecture reference.

---

## Build

```powershell
# Windows
.\build.ps1

# Linux / macOS
./build.sh

# Quick check (no zip)
dotnet build Jellyfin.Plugin.AIRecommendations.csproj -c Release
```

Output lands in `bin/Release/net9.0/`. The plugin DLL is `Jellyfin.Plugin.AIRecommendations.dll`.

---

## Git & release workflow

**Every `git push` triggers the pre-push hook** which:
1. Bumps the patch version in `VERSION.txt` and `.csproj`
2. Commits the version bump
3. Pushes a `v1.0.x` tag → GitHub Actions builds the release ZIP and updates `manifest.json`

**To push WITHOUT triggering the hook** (e.g. docs-only or when CI has a race condition):

```powershell
$env:SKIP_VERSION_BUMP=1; git push
```

**Race condition warning:** If GitHub Actions is simultaneously updating `manifest.json` when you push, your push will be rejected. Fix: `git merge origin/main`, take CI's version of `manifest.json` (`git checkout --theirs manifest.json`), then `SKIP_VERSION_BUMP=1 git push`.

**Install hooks** (required once per clone):

```powershell
.\scripts\setup-hooks.ps1
```

---

## Key rules when modifying this project

### Adding a new service
1. Register it in `PluginServiceRegistrator.cs` (as `AddSingleton` or `AddHostedService`)
2. If it needs an HTTP client, add a named `AddHttpClient(...)` entry with an appropriate timeout
3. Inject it into consumers via constructor DI — never use `Plugin.Instance` to resolve services

### Adding a new config property
1. Add the property to `Configuration/PluginConfiguration.cs`
2. Add a UI control to `Configuration/configPage.html` (see existing sections for pattern)
3. Save: `document.getElementById('...').value` → add to the `config.X = ...` save block
4. Load: add `document.getElementById('...').value = config.X || ''` in the load block
5. The config is XML-serialised automatically by Jellyfin — no migration needed for new optional properties (they default to their C# default)

### Adding a new agent tool
1. Add the tool definition JSON inside `TelegramAgentLoop.cs` (`_toolDefinitions` list)
2. Add a `case "tool_name":` branch in `ExecuteToolAsync`
3. The tool result must be a `string` (JSON-serialise complex objects)
4. Keep tool names snake_case — the LLM is case-sensitive about them

### Adding a new bot command (Telegram or Discord)
- Telegram: handle in `TelegramBotService.HandleUpdateAsync`, before the agent dispatch block
- Discord: handle in `DiscordBotService.HandleMessageAsync`, before the agent dispatch block
- Commands that don't need the agent (e.g. `/reset`, `/profile`) return early before calling `TelegramAgentLoop.RunAsync`

### Adding a new API endpoint
- Add to `Api/RecommendationsController.cs`
- All endpoints require `[Authorize(Policy = "RequiresElevation")]` (admin-only)
- The controller has `[ApiExplorerSettings(IgnoreApi = true)]` — Swagger won't show it

### Modifying the Discord bot
- `DiscordBotService.cs` implements the Gateway protocol from scratch using `ClientWebSocket`
- Gateway intents: only `DIRECT_MESSAGES` (4096) — no privileged intents
- Token format: `"Bot {token}"` header on all REST calls
- See `AGENT.md § Discord Gateway` for the full protocol state machine

---

## Common pitfalls

| Pitfall | Fix |
|---|---|
| `Plugin.Instance` is null | Only access it after DI is fully built; don't call it in constructors |
| Config not saved | Always call `Plugin.Instance!.SaveConfiguration()` after mutating `config` |
| Library scan on every change | Batch writes, then do one `ValidateMediaLibrary` call at the end |
| Discord IDs in JSON | Return `ulong` Discord IDs as `string` to avoid JS precision loss on snowflakes |
| Agent loop not terminating | `MaxToolRounds = 5` is the hard limit — ensure tools always return a string result |
| HTTP timeout too short | Agent LLM calls can take 30–60 s; the `TelegramAgent` client has 80 s, turn has 90 s |

---

## Testing locally

There is no test project. The fastest feedback loop is:
1. `dotnet build` — catches compile errors
2. `.\deploy-local.ps1` — builds and copies DLL to a local Jellyfin plugins folder (edit the path in the script)
3. Restart Jellyfin, check logs

The **Test Agent** panel in the plugin config page lets you chat with the agent from the browser without Telegram or Discord setup.
