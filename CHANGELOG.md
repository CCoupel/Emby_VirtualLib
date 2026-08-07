# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Live counter aggregation** (commit 7b923fc) : 3-value counter extended to connector and content-type levels (in addition to existing library-level counters) during sync operations

### Changed

### Fixed
- **Configuration checkbox visibility regression** (commits 1467772 / 2eb924b / 8eefc8e / 6e6412e)
  - **Bug**: `cacheEnabled`, `connectorFilterIgnoreForcedSubtitles`, and `connectorCacheEnabled` checkboxes rendered invisible in configuration page after rapid deployment cycles
  - **Root cause identified**: Emby's `emby-checkbox.css` module applies `appearance: none` to elements with `class="emby-checkbox"` but requires a sibling with exact class `checkboxLabel` for rendering the check mark — our HTML markup lacked this sibling, causing invisible checkboxes when the CSS module was loaded in the browser session (non-deterministic trigger dependent on Emby pages visited in session)
  - **Resolution attempts** (3 unsuccessful):
    1. Cache-busting `config.html` (invalidated QUALIF) — incorrect hypothesis
    2. Eager-load `viewshow` module (invalidated QUALIF) — incorrect hypothesis
    3. Synthetic `dispatchEvent('change')` to trigger CSS (invalidated QUALIF) — CSS issue not event-driven
  - **Final fix** (commit 6e6412e, confirmed QUALIF): Remove incompatible `class="emby-checkbox"` from all three checkboxes; accept loss of Emby's custom visual styling in exchange for reliable native checkbox rendering
  - **Trade-off**: Configuration checkboxes now render with browser default styling instead of Emby's custom appearance, but are visually reliable across all browser sessions

### Security

### Deprecated

### Removed

---

## [1.11.0.0] - 2026-08-06

### Added
- **Additive media filtering** (Issue #44) : per-connector stream filtering by minimum resolution, audio languages, and subtitle languages for Movie/Episode scope
- **Complete stream capture** : Emby streams (language, subtitle info) captured at no cost; Plex streams fetched via batch requests (50 items per request) with full metadata
- **Shared orphan cleanup brick** (shared with Issue #12) : implemented but disabled by default with forced dry-run mode (no actual deletion possible in this version)
- **Complete track injection** : audio and subtitle tracks now injected into Emby MediaStreams and NFO files (bonus improvement)

### Fixed
- `CacheEnabled` configuration not persisting across restarts
- `toggleLibrary` action accidentally erasing `LocalUserId` on checkbox click (breaking multi-user isolation)
- Orphan cleanup protection against symbolic links in cleanup path traversal
- Connector name uniqueness enforcement with concurrent access mutex (Issue #44)
- Sync logs now visible in production (resolves practical aspect of Issue #26)

---

## [1.9.4] - 2026-04-12

### Added
- **Parallel sync page fetch** : PlexConnector and EmbyConnector now fetch paginated results in parallel (page 0 first to determine total count, then remaining pages concurrently)
- **Parallel per-item metadata** : `SyncLibraryItemsAsync` now fetches GetMetadata + DownloadArtwork for each item concurrently (max 8 concurrent, configurable)
- **Thread-safe deduplication** : show/season/book deduplication via `ConcurrentDictionary.TryAdd`

### Fixed
- Issue #43 — optimize sync performance for large libraries with parallel operations
- Plex Part stream URL now includes `&download=1` parameter to bypass mtime validation
- Proxy robustness : reject non-media upstream responses, fix cache TotalSize from 206 partial-content responses
- Unique plugin GUID + `IHasThumbImage` interface for proper plugin marketplace integration

### Technical
- Thumb image (`thumb.png`) added for plugin presentation in hub

---

## [1.9.0] - 2026-04-08

### Added
- **Segment-based local cache** : cache proxified streams locally on Emby host disk using segment-based architecture
  - Chunk-aligned flush with configurable interval (default 2 MB)
  - Automatic merge of adjacent segments for sequential reads
  - Pending segments to avoid duplicate source fetches for concurrent clients
  - Auto-completion threshold (default 90%) — continues download even after client stop
- **Cache configuration** : `CacheEnabled`, `CacheChunkSizeMb`, `CacheMaxSizeGb`, `CacheRootPath` in PluginConfiguration
- **Per-connector cache override** : `ConnectorConfig.CacheEnabled` to enable/disable per remote server
- **Cache manifest** : `ChunkManifest` model with segments, TotalSize, TotalChunks, CachedPercent tracking
- **ProxyController cache integration** : cache hits return directly, cache misses proxy with write-through caching

### Documentation
- New `docs/core/CACHE.md` : complete cache architecture guide with segment alignment, merge strategy, and manifest structure

---

## [1.8.2] - 2026-04-05

### Fixed
- **StrmGenerator idempotent** : `.strm` file only written if content differs (URL or file missing), preventing unnecessary Emby scan triggers that caused MediaStream info loss

---

## [1.8.1] - 2026-04-03

### Added
- **Multi-user playback isolation** (Issue #41, #42)
  - `LocalUserId` field in `ConnectorConfig` to bind connector to specific local user
  - Session key now includes `userId` : `{connectorId}:{remoteItemId}:{localUserId}` 
  - `playSessionId` used as `DeviceId` for isolated sessions on remote server
  - Device name format `user@client` (e.g., `cyril@Emby Web`) propagated in all playback calls
  - Progress isolation : only linked user sends updates to remote, others maintain locally
  - Position restoration at Stop for non-linked user (resolves linked user's position from active session)
  - `SyncUserFlags` now targets only linked user instead of all local users

---

## [1.8.0] - 2026-04-01

### Added
- **LibraryOrganization modes** (Issue #36, #38, #39)
  - `Isolated` (default) : dedicated Emby library per connector-library pair
  - `SharedByType` : shared Emby library by content type (Movies, TvShows, etc.)
  - `SharedLibraryPrefix`/`SharedLibrarySuffix` parameters for customization (e.g., `[VL] Movies`)
- **Incremental path addition** : each library adds its path individually to shared library via `AddMediaPaths`
- **Selective removal** : in SharedByType mode, library removal only removes its path via `RemoveMediaPath(long itemId, string path)`
- **Cancel Sync button** : UI button to cancel ongoing synchronization

### Changed
- Physical file path consistency : `.strm`/`.nfo` files always placed in `virtualLibRoot/ConnectorName/LibraryName/` regardless of organization mode (organization only affects Emby library name, not disk structure)

### Fixed
- `ApplyLibraryOptions` now preserves `PathInfos` when updating (Emby resets path list if not re-injected explicitly)
- `RemoveMediaPath(long, string)` implementation aligned with actual Emby API signature

---

## [1.7.1] - 2026-03-28

### Changed
- Version bump for release tracking

---

## [1.7.0] - 2026-03-25

### Added
- **Playback event forwarding** (Issue #34)
  - `PlaybackEventForwarder` (`IServerEntryPoint`) subscribes to `ISessionManager` events (Start/Progress/Stop)
  - Proxied playback URL parsing : extract connector and remote item from `/virtuallib/proxy/{connectorId}/{libraryId}/{remoteItemId}`
  - 30-second heartbeat to keep remote session alive during pauses
  - 8-second debounce on Stop event (Emby fires Stop when HTTP connection closes due to full buffer)
  - Transparent session reopening : Progress for absent session resends Start + Progress automatically
  - Final position sent with Stopped event for "Continue playback" support

### Fixed
- `PostWithRetryAsync` in `EmbyConnector` now uses `StringContent` with explicit `Content-Length` instead of `PostAsJsonAsync` (chunked encoding ignored by ServiceStack/Emby, causing null fields including `PlaySessionId`)
- `PlaySessionId` GUID generation and propagation through Progress and Stopped events
- Plex playback reporting via `GET /:/timeline?state=playing|paused|stopped&time={ms}` and progress persistence via `/:/progress?key={ratingKey}&time={ms}`

---

## [1.6.1] - 2026-03-20

### Added
- **100% parallel sync** (Issues #30, #35)
  - All libraries from all connectors synced simultaneously (`Task.WhenAll`)
  - Each library chains Phase 1 → Phase 2 independently without waiting for others
  - `MaxParallelLibraries` per-connector limit (default: 4) via `SemaphoreSlim` on Phase 1 only

### Changed
- **SyncState redesign** : `ConcurrentDictionary<string, LibrarySyncEntry>` with `Volatile.Read/Write` for thread-safety, statuses `Pending / RunningPhase1 / RunningPhase2 / Done / Failed`
- **Real-time progress bars** : dual-phase progress (Phase 1 blue / Phase 2 green) on each library line via polling `/virtuallib/sync/status` every 2s
- **Progress bar layout** : full-width bars (`flex:1`), variable height by level (connector 9px / type 6px / library 4px), bicolor tracks (light background = total, dark fill = progress)

---

## [1.6.0] - 2026-03-15

### Added
- **User state synchronization**
  - `IsPlayed`, `IsFavorite`, `PlayCount`, `LastPlayedDate`, `PlaybackPositionTicks` fields on `MediaItem`
  - `UserData` field in `EmbyConnector` requests and mapping to `MediaItem`
  - `viewCount` (play count), `viewOffset` (position in ms → ticks), `lastViewedAt` parsing in `PlexConnector`
  - `SyncUserFlags` phase after metadata injection, applies played/favorite/position states for all local users via `IUserDataManager.SaveUserData(..., UserDataSaveReason.Import)`

### Changed
- User data merge strategy : local states never reduced, only increased (playCount, position, favorite flag propagated)

---

## [1.5.0] - 2026-03-10

### Added
- **Robust audiobook metadata**
  - `AudioBookNfoProvider` and `AudioBookFolderNfoProvider` (`ILocalMetadataProvider`) to inject Album, AlbumArtists, ProductionYear from `album.nfo`
  - `BookNfoProvider` for ebook NFO injection
  - `RuntimeTicks` (100-ns ticks) propagation from source server
  - AlbumArtists field for author information
  - **Polling loop post-scan** : background loop (2s poll, 5min timeout) awaits Emby scan completion, then injects metadata — reduces to single sync (vs. prior 2 syncs)

### Changed
- Artwork decoupled from NFO condition : images downloaded even when `album.nfo` exists
- Fallback artwork for AudioBook containers without images (uses chapter artwork)
- Extended ArtworkType support : Banner, Disc, Art (ClearArt) in addition to Poster/Backdrop/Thumb/Logo
- Per-chapter Primary image downloaded as `{chapter}.jpg` alongside `.strm`

### Fixed
- `AudioBookNfoProvider` now injects `Album` (book title) correctly instead of `Name`

---

## [1.4.0] - 2026-03-05

### Added
- **AudioBook, eBook, Photo support**
  - `MediaType.AudioBook` and `MediaType.Book` added
  - `StrmGenerator` : arborescence `{book}/{chapter}.strm` for audiobooks
  - `EpubStubGenerator` : download actual epub/pdf/mobi files from source
  - `NfoGenerator.GenerateAudioBookNfo()` : album.nfo format for Music/AudioBook in Emby
  - `LibraryProvisioner` : automatic virtual folder creation for `audiobooks`, `books`, `photos`
  - Photo/HomeVideo support in Emby connector

---

## [1.3.0] - 2026-02-28

### Added
- **Plex Connector**
  - `PlexConnector` : auth via API key, API XML `/library/sections`, items with metadata (movies + series)
  - `PlexTvConnector` : plex.tv authentication, server selection by machineIdentifier
  - Automatic best-URL resolution (local → plex.direct → relay), LAN IP exclusion from Kubernetes
  - 120s timeout on relay connections
  - Per-server access tokens from `/api/v2/resources`
  - 2FA support (TOTP code at server load time)
  - Plex item counter : `X-Plex-Container-Start=0` parameter for accurate `totalSize`
- **UI enhancements** for Plex
  - Collapsible library type tree (Movies, TvShows…), A→Z sorted, collapsed by default
  - Summary counters on each connector line and type group (`X/Y libs · A/B items`)
  - Auto-discovery of new libraries during sync (merge into `KnownLibraries`)
  - Automatic UI refresh after sync (no page reload)
  - Password prefill in connector edit (avoid re-entry for Test Connection)
  - Item counter updated for all libraries (checked and unchecked) during sync

---

## [1.2.0] - 2026-02-20

### Added
- **Proxy security hardening** (Issue #22)
  - Redesigned proxy URL : `/virtuallib/proxy/{connectorId}/{libraryId}/{itemId}` (added `libraryId`)
  - Active connector + enabled library validation before proxification
  - Token-based access control : user rights verification on virtual Emby library
  - Browser request blocking via User-Agent filter (block `Mozilla/*`, allow internal `Lavf/*` and absent)
  - Automatic `.strm`/`.nfo` cleanup when library is unchecked or connector removed

### Fixed
- Dependency injection : `ILogger<ProxyController>` removed from constructor (not registered in Emby SimpleInjector)

### Known Limitations
- Emby doesn't forward user token in internal ffprobe calls → proxy access control impossible server-side for ffprobe; delegated to Emby `PlaybackInfo` which is the true control point

---

## [1.1.0] - 2026-02-15

### Added
- **Metadata synchronization workflow**
  - `SyncService` : orchestrates sync per connector + library
  - `.strm` + `.nfo` + artwork downloads (poster, fanart, landscape, logo)
  - Complete metadata : cast, directors, writers, tagline, trailer URL
  - Intelligent skip : always regenerate `.strm`, skip `.nfo` only in `RemoteSync` mode
  - `MetadataMode` per connector : `RemoteSync` (incremental) / `RemoteSyncFull` (force) / `LocalScraping`
  - Emby `LibraryOptions` applied per mode (TMDB/TVDB/FanArt fetchers, cache, chapters)
  - `LibrarySyncJob` : scheduled task (`IScheduledTask`) with configurable interval
  - Dynamic trigger update without restart
  - `QueueLibraryScan()` triggered when items created
  - Remote item counters per library (endpoint `/item-counts`)
  - Per-library sync progress in UI (client-side iteration)

### Fixed
- `/Users/Me` endpoint 500 error : `GetUserIdAsync` no longer calls `/Users/Me`

### Backlog
- [ ] Delta detection : local JSON index `{connectorId}.json` (Issue #12)
- [ ] Deletion handling : cleanup removed items from source
- [ ] Sync job integration tests (Issue #14)

---

## [1.0.0] - 2026-02-01

### Added
- **Core plugin architecture**
  - `IMediaServerConnector` interface with normalized models
  - `EmbyConnector` : auth, library listing, item metadata
  - `StrmGenerator` : `.strm` file generation
  - `NfoGenerator` : `.nfo` file generation (movie + episode)
  - `PluginConfiguration` + `ConnectorConfig`
  - `Plugin.cs` entry point (verified on test server)
  - 18 unit tests (xUnit + Moq)

- **UI and manual sync** (Phase 1.5)
  - Configuration page in Emby dashboard
  - Remote server add/edit/delete form (URL + API key)
  - "Test Connection" button → `TestConnectionAsync`
  - Available libraries listing → `ListLibrariesAsync`
  - Library selection (checkboxes → `ConnectorConfig.LibraryIds`)
  - "Sync Now" button → `.strm` + `.nfo` + Emby scan
  - `ProxyBaseUrl` parameter (override for `.strm` URLs)

- **Basic HTTP proxy**
  - `ProxyController` endpoint `/virtuallib/proxy/{connectorId}/{itemId}`
  - Range header support (seek/scrubbing)
  - Content-Range + Accept-Ranges forwarding
  - `[Unauthenticated]` DTO for ffprobe calls
  - Clean client disconnect handling
  - End-to-end playback validation (web + app, direct play + transcode)
