# Jellyfin → Letterboxd Sync — Working Homelab Setup

> This repo previously held a fork of the (now archived) `danielveigasilva/jellyfin-plugin-letterboxd-sync` plugin.
> It now documents a **complete, verified working setup** (June 2026) built on the actively maintained
> [builtbyproxy/jellyfin-plugin-letterboxd](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd) ("LetterboxdSync") plugin,
> plus a companion container that mirrors Letterboxd lists into Jellyfin collections.

## What this gives you

1. **Watch a movie in Jellyfin → it appears in your Letterboxd diary.** Once, no duplicates. Ratings sync too (and update in place).
2. **Your Letterboxd lists → Jellyfin collections**, refreshed daily, with auto-generated collage covers.

```
┌─────────────┐  watched/rated   ┌────────────────────┐
│  Jellyfin    │ ───────────────▶ │  Letterboxd diary  │   (LetterboxdSync plugin, real-time + daily catch-up)
│  (Docker)    │ ◀─────────────── │  Letterboxd lists  │   (jellyfin-auto-collections, daily 4 AM cron)
└─────────────┘   collections    └────────────────────┘
```

## Why this architecture

Letterboxd has **no public write API** (request-only beta; personal projects are explicitly rejected).
Every sync tool therefore uses either the unofficial mobile JSON API or CSV-import automation.
The LetterboxdSync plugin is the best current option: actively maintained, idempotent (local sync-history ledger),
supports rating updates, and runs *inside* your existing Jellyfin container — plugins live on the config volume,
so they survive image updates.

## Part 1 — Jellyfin (`jellyfin/`)

Standard linuxserver.io Jellyfin with NVIDIA transcoding. See `jellyfin/docker-compose.yml`.

**Critical: set `PUID`/`PGID` to match your media ownership** (e.g. `1000:1000`).

> ⚠️ **Gotcha (cost me a crash-loop):** if you change PUID on an *existing* linuxserver container, its startup
> only chowns the top level of `/config`. Subdirectories (including the SQLite DB at `/config/data/data/`)
> keep the old uid → Jellyfin dies with `SQLite Error 8: attempt to write a readonly database` and restart-loops
> *inside* the container (docker still shows "Up", RestartCount stays 0). Fix:
> ```bash
> docker exec -u 0 jellyfin chown -R 1000:1000 /config && docker restart jellyfin
> ```

**linuxserver plugin path quirk:** plugins live at `/config/data/plugins` (host: `<config>/data/plugins/`),
not `/config/plugins` like the official image.

## Part 2 — LetterboxdSync plugin

### Install
1. Dashboard → Plugins → Repositories, add **both**:
   - File Transformation (dependency): `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
   - LetterboxdSync: `https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
2. Catalog → install **File Transformation**, then **LetterboxdSync** → restart the container.
3. Plugin settings → add an account: map your Jellyfin user to your Letterboxd username/password.

### Recommended config (one-way, no duplicates, no history flood)

| Setting | Value | Why |
|---|---|---|
| `EnableDiaryImport` | **false** | Strictly one-way. Bidirectional sync has a known feedback-loop bug class (phantom rewatches). |
| `SkipPreviouslySynced` | **true** | Local dedupe ledger — every film logs exactly once, re-runs are no-ops. |
| `EnableDateFilter` + `DateFilterDays=1` | **true / 1** | Catch-up task ignores anything older than a day → your pre-existing watch history is never bulk-dumped into your diary. |
| `SyncFavorites` | true (optional) | Jellyfin ❤️ → Letterboxd like. |
| Watchlist / Jellyseerr features | off | Keep the surface minimal until you need them. |

Manual trigger: Dashboard → Scheduled Tasks → *Sync watched movies to Letterboxd*.

### Notes
- Jellyfin's web UI only has the ❤️ button — the 0–10 user-rating field exists in the API/data model but has no widget.
  Rate via API or a third-party client; the plugin maps 0–10 → 0.5–5 stars and **updates the existing diary entry** on change.
- The plugin authenticates with your Letterboxd password against the unofficial API (stored in plugin config on disk).
  If Cloudflare blocks login, paste browser cookies into the `RawCookies` + `UserAgent` fallback fields.
- Dedupe is local-first: the ledger lives in plugin config. Wiping plugin config risks re-logging
  (Letterboxd's own same-day guard is the backstop).

## Part 3 — Letterboxd lists → Jellyfin collections (`jellyfin-auto-collections/`)

Uses [ghomasHudson/jellyfin-auto-collections](https://github.com/ghomasHudson/jellyfin-auto-collections).
See `jellyfin-auto-collections/docker-compose.yml` + `config/config.yaml`. Runs an immediate full sync on
container start, then daily at the `CRONTAB` schedule. Idempotent — re-runs never duplicate collection entries.

**Hard-earned operational notes:**
- Keep **only the `letterboxd` plugin enabled**. As of June 2026 `imdb_chart`, `tspdt` and `bfi` are all
  bitrotted upstream (changed sites / whitespace-broken title parsing).
- The tool **crashes the whole run on any single list error** — even one 404'd list URL. Combined with
  `restart: unless-stopped` that means an infinite crash-loop that re-scrapes all your lists every restart
  (Cloudflare rate-limit risk). Verify every list URL with `curl -I` before adding it.
- `Item X not found in jellyfin` warnings are normal: the list film isn't in your library yet.
  It's added automatically on a later run once acquired (e.g. via Radarr).
- **Boxset art gotcha:** Jellyfin provider-matches collections *by name* against TMDb's collection DB.
  A generically named collection can adopt a wrong franchise's artwork, and the stored `ProviderIds`
  outlives any rename. Fix: clear `ProviderIds`, delete the images, set `LockData=true` on the boxset.
  (Boxset identity lives in its folder path, not the display name.)

## Verification checklist

```bash
# plugin loaded?
docker logs jellyfin 2>&1 | grep -i letterboxd
# end-to-end: mark a movie watched, run the sync task, then:
#   1) it appears in your Letterboxd diary (once)
#   2) re-running the task logs "already in local sync history"
#   3) changing the rating updates the same entry
# persistence: docker compose up -d --force-recreate  → plugin + config must survive
```

## Out of scope / future

- Importing Letterboxd collections *into* Jellyfin beyond list-collections
- Historic watch-history backfill (deliberately disabled — see DateFilter)
