# Global Graffiti Wall - Roadmap & TODO

This document tracks planned development phases and architecture enhancements for the **Global Graffiti Wall** project.

---

## 1. High-Throughput & Performance Optimization
- [x] **Batched Pixel Persistence Queue**:
  - Replaced per-pixel synchronous SQL inserts in `CanvasHub.PlacePixel` with an in-memory `Channel<PixelPlacementItem>` (`PixelPlacementQueue`).
  - Implemented `PixelBatchWriterService` background worker to flush accumulated pixel placements to SQL Server every 250ms or 1,000 items via high-speed `SqlBulkCopy`, with graceful shutdown flushing.
- [x] **8-Bit Byte Storage & 256-Color Redis Engine**:
  - Upgraded global canvas memory layout from 4-bit nibbles (50 MB) to full 8-bit byte allocation (100 MB for $10,000 \times 10,000$ canvas: `canvas:global_state`), supporting up to 256 indexed colors.
  - Migrated atomic pixel mutations from nibble bitwise manipulation to native Redis `StringSetRangeAsync` (`SETRANGE`) operations, eliminating race conditions and CPU overhead.
  - Scaled binary tile chunk payloads from 32 KB to 64 KB ($256 \times 256$ pixels $\times$ 1 byte) via row-major Lua tile extraction (`GET /api/canvas/tile`).
  - Scaled minimap overview buffer from 12.8 KB to 25.6 KB ($160 \times 160$ bytes: `canvas:minimap_overview`) served via `GET /api/canvas/minimap`.
  - Rebuilt and rehydrated historical pixels cleanly on startup via `CanvasInitializerService` (8-bit binary rehydration).

---

## 2. Authentication, Accounts & Private Canvas Walls
- [x] **Optional User Authentication & Painter Profiles**:
  - **Frictionless Dual Mode (Guests & Authenticated Painters)**:
    - Anyone can immediately draw and reserve territory as an anonymous guest with a persistent guest UUID in `localStorage`.
    - Painters can optionally create a free account (`POST /api/auth/register`) or sign in (`POST /api/auth/login`) anytime to claim a permanent painter handle (`@Username`).
  - **Seamless Guest History Migration**:
    - When signing in or registering, users can check *"Link guest session graffiti & territories to this account"*, which automatically reassigns all past guest pixels and active territories to the new user in a single atomic SQL transaction.
  - **Cryptographic Security & JWT Architecture**:
    - Salted PBKDF2 SHA-256 password hashing with 100,000 iterations and constant-time equality checks (`CryptographicOperations.FixedTimeEquals`).
    - HMAC SHA-256 signed JWT tokens configured via `JwtSettings` in `appsettings.json`.
    - SignalR WebSocket connection authentication using `accessTokenFactory` and query string token interception (`OnMessageReceived`).
  - **Painter Profiles & Verified Handles**:
    - Lifetime stats tracking: Total non-banned pixels placed, active territories count, account role, member registration date.
    - Pixel Inspector displays verified author handles (`@Username ✓ Verified`) in the active coordinate card and historical overwrite list.
    - Territory reservation modal automatically populates territory owner handle for logged-in painters.
  - **Cyberpunk Account Modal & Navigation**:
    - Added top HUD Account pill and bottom-bar `[👤 Account]` button (hotkey `U`) toggling `#auth-modal`.
    - Tabbed interface for Sign In, Registration, and My Profile with live stat cards, instant feedback, and Logout.
- [x] **Canvas Area & Territory Reservations (Timed Zone Protection)**:
  - Allowed users to reserve a rectangular bounding box (`[x1, y1]` to `[x2, y2]`) on the canvas for a specified duration (15m, 1h, 4h, 12h, 24h).
  - **Exclusive / Protected Drawing Rights**:
    - Only the reservation owner and authorized teammates holding the collaborator secret key can paint inside the reserved territory.
    - Unauthorized placements are intercepted and rejected prior to token deduction in `CanvasHub.PlacePixel` with friendly feedback (*"This area is part of '{Label}' reserved by {Owner}..."*).
  - **High-Performance Spatial Lookups**:
    - In-memory `ReservationService` maintaining active reservation bounds for sub-millisecond coordinate hit testing without SQL latency.
  - **Anti-Monopoly Quotas & Sizing Limits**:
    - Enforced $5 \times 5$ min and $128 \times 128$ max dimension boundaries with 1 active reservation concurrency cap per owner. Overlaps with existing active territories are strictly rejected.
  - **Frontend Selection & Territory Overlay**:
    - Added `[📐 Reserve]` HUD tool (shortcut `Shift + R`) enabling live click-and-drag box selection with pixel-dimension feedback.
    - Created Territory Reservation modal (`#reservation-modal`) with coordinate inspection, name/duration configuration, and collaborator secret key generation/copying.
    - Created Territories & Passes modal (`#zones-modal`, shortcut `Z` or `[🏰 Zones]`) displaying active wall territories with coordinate teleportation, early release actions, and collaborator key management.
    - Rendered cyberpunk glowing boundary brackets, shaded territory fill, territory labels, and Minimap Radar bounding box overlays.
  - **Persistence & Auto-Release**:
    - SQL Server `canvas_reservations` table (schema preserved in `Scripts/002_create_canvas_reservations.sql`) with indices on `(is_active, expires_at)`.
    - Implemented `ReservationExpirationService` background worker to auto-prune expired territories and broadcast `ZoneExpired` events via SignalR.
- [x] **Private Pixel Walls for Authenticated Users**:
  - **Custom Canvas Studios**:
    - Authenticated users can create, name, describe, configure dimensions, set privacy access controls, and manage their own private canvas studios with the full feature set (zoom/pan viewport, high-zoom grid, 16-color palette, SignalR real-time sync, time-lapse timeline scrubbing, and minimap radar).
    - Presets include $500 \times 500$ px (Studio), $1000 \times 1000$ px (Standard), $2048 \times 2048$ px (Arena), or custom dimensions ($100 \times 100$ to $4000 \times 4000$).
  - **Multi-Wall Backend & Storage Architecture**:
    - SQL Server schema migration (`Scripts/003_create_canvas_walls.sql`) adding `canvas_walls` and partitioning `pixel_placements.wall_id` and `canvas_reservations.wall_id`.
    - Redis isolated state keys (`canvas:wall:{id}:state`) and minimap keys (`canvas:wall:{id}:minimap`) dynamically allocated and rehydrated via `WallService`.
    - Full backward compatibility: The primary 10,000×10,000 Global Wall remains the default (`wallId == null` or empty).
    - Dynamic 2D tile extraction (`/api/canvas/tile`), minimap overview (`/api/canvas/minimap`), pixel inspection (`/api/canvas/pixel-info`), and time-lapse deltas (`/api/history/deltas`) adapt dynamically to wall dimensions and `wallId`.
  - **SignalR Group Partitioning & Owner Privileges**:
    - Pixel placements and updates are isolated to their own connection groups (`wall:{wallId}`) so clients only receive events for their active canvas.
    - Wall owners get unconstrained, infinite free painting on their own studio canvases without token-bucket rate limiting.
- [x] **Private Wall Marketplace, Access Permissions & Quotas**:
    - **Flexible Privacy & Access Modes**:
      - `Public`: Discoverable in directory; anyone can view and paint.
      - `Unlisted`: Accessible via direct share/invite link (`?wallId={id}`).
      - `Password`: Protected by custom passphrase required to paint.
      - `Private`: Studio owner-only painting.
    - **Quota Enforcement**:
      - Capped at 5 custom walls per authenticated user (Admins/Moderators have unlimited quota).
    - **Full Management & Sharing Lifecycle**:
      - Top HUD Wall Switcher pill (`🖼️ Global Wall ▾`), Bottom-bar `[🖼️ Walls]` tool button, and hotkey <kbd>Shift</kbd> + <kbd>W</kbd>.
      - 3-tab modal (`#walls-modal`): **Explore Walls** (searchable public community directory + global canvas), **My Studios** (personal walls with delete/deactivate action), and **+ Create Studio** (presets, custom dimensions, privacy controls).
      - One-click invite link copying (`?wallId={id}`) with deep-link auto-loading on page load.
- [x] **Configurable Token-Bucket Rates**:
  - Moved rate-limiting capacity and refill rate to `appsettings.json` (`CanvasSettings:MaxCapacity`, `CanvasSettings:RefillRatePerSecond`).
- [ ] **Unified Token Economy & Vault (Pixel Overdrive & Zone Reservation Leases)**:
  - **Dual-Token Currency Model**:
    - **Regenerating Charges (0–16)**: Standard free bucket refilling at 1 charge every 5s for rapid casual painting.
    - **Banked Bonus Tokens**: Persistent token vault tied to authenticated user accounts (`users.bonus_tokens`) in SQL Server and cached in Redis (`user:{userId}:bonus_balance`).
  - **Pixel Placement Overdrive**:
    - When regular charges reach 0, painting does not halt or error; placements automatically consume banked bonus tokens (sub-millisecond Redis Lua deduction).
    - Prevents creative flow interruptions when drawing murals or defending artwork.
  - **Token-Funded Territory Reservation Leases (Spatial Footprint $\times$ Duration Formula)**:
    - Replace free, unconstrained territory locking with a fair, balanced token leasing model:
      - Formula: $\text{Token Cost} = \text{Base Fee} + \lceil \frac{\text{Width} \times \text{Height}}{\text{Area Scale}} \rceil \times \text{Duration Multiplier}$.
      - Micro stickers ($10 \times 10$): 2 ⚡ for 15m up to 25 ⚡ for 24h.
      - Standard murals ($32 \times 32$): 5 ⚡ for 15m up to 50 ⚡ for 24h.
      - Large collaborative art ($64 \times 64$): 10 ⚡ for 15m up to 90 ⚡ for 24h.
      - Guild/Clan megazones ($128 \times 128$): 20 ⚡ for 15m up to 180 ⚡ for 24h.
    - **Dual-Pool Funding**: Quick 15m sketch shields can be funded directly with normal regenerating charges (e.g. 5 charges), while long multi-hour/24h holds require banked bonus tokens.
    - **Early-Release Refund Incentive**: Releasing a territory early in `#zones-modal` returns a 50% prorated refund of unused time back to the owner's bonus token bank, preventing dead "ghost zones".
    - **Territory Extension / Renewal**: Allow owners to spend tokens in `#zones-modal` to extend active territory duration before expiry.
    - **Studio Owner Exemption**: Reservations on private studio walls remain 100% free (0 tokens) for wall owners.
  - **Token Inflow & Acquisition Mechanisms**:
    - **Daily Supply Drop (`POST /api/tokens/claim-daily`)**: Authenticated painters claim +25 to +50 bonus tokens once per 24 hours.
    - **Welcome Stash**: New user registrations automatically receive +50 bonus tokens.
    - **Promo / Voucher Codes (`POST /api/tokens/redeem`)**: Event and stream promo codes (e.g. `CYBERPUNK2026`).
    - **Audit Ledger Table (`token_transactions`)**: Full transactional ledger logging `DAILY_CLAIM`, `WELCOME_BONUS`, `ZONE_RESERVE_DEBIT`, `ZONE_RELEASE_REFUND`, `PIXEL_OVERDRIVE_DEBIT`, and `PROMO_CODE`.
  - **Frontend Cyberpunk HUD & Modal Integrations**:
    - **HUD Overdrive Visuals**: Top meter displays `16/16 ⚡ (+42 Bonus)`. When regular charges deplete, the meter glows amber/gold with *"⚡ Overdrive: Using Bonus Tokens"* status.
    - **Live Lease Cost Calculator in `#reservation-modal`**: Dynamic calculation of token cost as the user drags a selection box or changes duration, showing current balance and available tokens.
    - **Token Vault Card in `#auth-modal`**: Balance display, pulsating `[🎁 Claim Daily Drop (+25 ⚡)]` button with live countdown timer, and promo code redemption input.


---

## 3. Moderation & Anti-Abuse
- [x] **Shadow Banning Engine**:
  - Implemented `ModerationService.cs` with $O(1)$ Redis sets (`canvas:shadow_banned:ips`, `canvas:shadow_banned:users`) synchronized with SQL Server `shadow_bans` table (schema preserved in `Scripts/001_create_shadow_bans.sql` with index on identifier).
  - Containment in `CanvasHub.PlacePixel`: Banned users/IPs receive normal success responses and local connection echo (`Clients.Caller.SendAsync("PixelUpdated", ...)`), but shared Redis buffer mutation and public `Clients.All` broadcasts are bypassed.
  - Placements flagged with `is_shadow_banned = 1` and batched via `SqlBulkCopy` for administrative auditing.
  - Public pixel inspection (`CanvasRepository.GetPixelHistoryAsync`), boot rehydration (`CanvasInitializerService`), and time-lapse replay (`HistoryController`) strictly exclude shadow-banned graffiti (`WHERE is_shadow_banned = 0`).
- [x] **Role-Based Moderation Lockdown & RBAC**:
  - Locked down all moderation endpoints in `ModerationController.cs` under `[Authorize(Roles = "Admin,Moderator")]`, returning `401 Unauthorized` for anonymous requests and `403 Forbidden` for standard users.
  - Added `POST /api/moderation/set-role` protected with `[Authorize(Roles = "Admin")]` to allow administrators to promote or demote users between `User`, `Moderator`, and `Admin`.
  - Automatic Admin Seeding: Green-field registrations auto-promote the 1st registered user (`userCount == 0`) to `Admin`. Existing databases auto-promote the earliest registered user in `EnsureUserSchemaAsync()`.
  - Authenticated Caller Identity: The `bannedBy` audit trail in `ShadowBanAsync` automatically resolves from caller JWT claims (`User.FindFirstValue(ClaimTypes.Name)`).
  - Frontend UI Lockdown: The `[🛡️ Mod]` HUD button and Inspector `[🚨 Shadow Ban]` button are completely hidden by default and only rendered for authenticated users with `Admin` or `Moderator` roles. Unauthorized hotkeys (`Shift + M`) are rejected with an access-denied alert. All moderation fetches attach JWT bearer tokens via `getAuthHeaders()`.


---

## 4. Pixel Inspection & Audit History
- [x] **Pixel Inspector API**:
  - Implemented `GET /api/canvas/pixel-info?x={x}&y={y}` in `CanvasController.cs` and `CanvasRepository.cs` returning active color, latest placement user/time, total overwrite count, and audit history.
- [x] **Frontend Inspector UI**:
  - Added toggleable "Paint / Inspect" mode buttons and right-click context inspection on the canvas.
  - Floating inspector card showing color preview, placer user ID, placement timestamp (with relative time), contestation count, and scrollable overwrite history.
  - Interactive "Pick This Color" eyedropper button and neon visual reticle on inspected coordinates.

---

## 5. Frontend & Viewport Optimization
- [x] **Dynamic Tiling / Viewport Chunking**:
  - Replaced the monolithic 50 MB full-canvas startup download with an on-demand $256 \times 256$ chunk streaming engine (`GET /api/canvas/tile?tx={tx}&ty={ty}`).
  - **Atomic 2D Tile Extraction Lua Script**: Server-side Lua script slices 256 rows directly from Redis RAM in row-major order with boundary padding, returning 32 KB binary payloads for populated tiles and `204 No Content` (0 bytes) for unpainted tiles.
  - **Lightweight 12.8 KB Minimap Overview**: Server provisions and dynamically updates a $160 \times 160$ overview buffer (`canvas:minimap_overview`) served via `GET /api/canvas/minimap`, delivering instantaneous global map awareness on page boot in < 5ms.
  - **Hardware-Accelerated Client Tile Cache**: Client manages an LRU tile cache (`CanvasTile`) rendered with zero-latency GPU `ctx.drawImage` calls, automatically fetching entering tiles and evicting offscreen tiles during panning/zooming.
  - **Real-Time Synchronized Tile Painting**: SignalR `PixelUpdated` deltas and local optimistic placements update local tile canvases and minimap buffers simultaneously.
- [x] **Efficient Delta Replay & Scrubbing**:
  - Debounced timeline scrubbing, cached deltas in memory, and implemented incremental frame rendering.
- [x] **Time-Lapse Replay Controls**:
  - Dual date pickers with adjacent Load Canvas buttons positioned directly above the playback scrubber bar.
  - Minute-per-frame replay engine supporting 1 min/s, 5 min/s, 15 min/s, 1 hr/s, 5 hr/s, 12 hr/s, and 1 day/s.
  - Collapsible/shrinkable Time-Lapse HUD panel with `−` / `+` minimize toggle and animated chevron indicator.
- [x] **Interactive HUD & Cooldown Visuals**:
  - Implemented 16-pip segmented charge meter with real-time token bucket fractional regeneration, countdown timer (`+1 in X.Xs`), depletion alert animations, and SignalR synchronization (`GetChargeStatus` & `PlacementResult`).
  - Added interactive Minimap Radar widget in the bottom-right corner featuring real-time viewport bounding box reflection, live/historical pixel rendering, hover coordinates, and click/drag navigation teleportation.
- [x] **256-Color Palette System & Dynamic 32-Color Active Selection**:
  - **SQL Database Palette Engine**:
    - Created `canvas_palette` table (`Scripts/004_create_canvas_palette.sql`) pre-populated with all 256 curated hexadecimal colors, descriptive names, `sort_order`, and `is_active` bit flag.
    - Updated foreign key constraint `FK_pixel_placements_palette` on `pixel_placements.color_id` referencing `canvas_palette(id)`.
    - Synchronized legacy `color_palette` table with all 256 colors for historical consistency.
  - **Dynamic Runtime Color Activation**:
    - Implemented `PaletteService` with in-memory caching and periodic refresh querying active colors (`is_active = 1`).
    - Added `GET /api/canvas/palette` (returns all 256 colors) and `GET /api/canvas/palette?activeOnly=true` (returns 32 active colors).
    - SignalR validation in `CanvasHub.PlacePixel`: Validates each placement against `PaletteService.IsColorActiveAsync(colorId)`, rejecting placements with inactive color IDs. Changing `is_active` in SQL Server immediately enables/disables colors without code changes or restarts.
  - **2-Row $\times$ 16-Column UI Layout & Hotkeys**:
    - Implemented a clean, row-major 16-column $\times$ 2-row grid (`repeat(16, 26px)`) in `#bottom-bar`:
      - **Row 1 (Classic 16 Colors)**: Pure White, Light Gray, Medium Gray, Dark Charcoal, Light Pink, Crimson Red, Vivid Orange, Earth Brown, Sunflower Yellow, Lime Green, Vibrant Green, Aqua Cyan, Ocean Blue, Royal Blue, Soft Magenta, Deep Purple (Hotkeys `1..9, 0, Q..Y`).
      - **Row 2 (New Curated 16 Colors)**: Pure Black, Slate Gray, Deep Maroon, Coral Red, Blush Pink, Peach Skin, Warm Cream, Amber Gold, Olive Drab, Forest Green, Mint Green, Teal, Seafoam Cyan, Indigo, Amethyst Purple, Coffee Brown (Hotkeys `A..N, ;, -, =`).
    - Eliminated horizontal scrollbars and container overflow clipping.
    - Added high-contrast hotkey badges on all 32 swatches with keyboard navigation listeners (`1-9, 0, Q-Y` and `A-N, ;, -, =`).
    - Added mode shortcuts (`P`/`B` for Paint, `I` for Inspect, `M` to toggle Minimap, `L` to toggle Time-Lapse, `Esc` to close Inspector).
    - Added canvas hover target reticle indicating the active 1x1 pixel footprint and high-zoom pixel grid lines (`zoom >= 10`) for pixel-perfect placement.
  - **Edge & Compact Mode Responsiveness**:
    - Scaled palette swatches to `repeat(16, 21px)` under `.edge-browser` and `.compact-ui`, preventing any overlap with the Radar HUD and tools.
    - Responsive mobile breakpoint (`@media (max-width: 650px)`) rendering compact 18px swatches in a centered 16-column grid.
  - **Cache-Control & Cache Busting**:
    - Added explicit Kestrel static file middleware headers (`Cache-Control: no-cache, no-store, must-revalidate, max-age=0`, `Pragma: no-cache`, `Expires: 0`) in `Program.cs`.
    - Injected `<meta http-equiv="Cache-Control">`, `<meta http-equiv="Pragma">`, and `<meta http-equiv="Expires">` tags in `index.html` to eliminate stale browser disk cache and ensure immediate delivery of updated assets.
- [x] **Edge Browser Compact Menus & Collision Prevention**:
  - Automatic Microsoft Edge detection via `navigator.userAgent` (`Edg/`) and Client Hints API (`navigator.userAgentData`), applying `.edge-browser` styling immediately before DOM paint.
  - Compact Radar widget: Scaled minimap canvas display footprint down from $160 \times 160$ to $110 \times 110$ px with dynamically calculated `rect.width` / `rect.height` coordinate mapping for click/drag teleportation.
  - Streamlined Bottom Bar: Reduced color swatches from $32\text{px}$ to $21\text{px}$ and tightened button padding, shrinking bottom bar width from 1085px to 739px (a 346px reduction).
  - Zero-Overlap Responsive Layout: Added breakpoint `@media (max-width: 1050px)` automatically stacking the tool selector vertically above the palette, narrowing horizontal width to ~413px and completely preventing collisions even when Edge is snapped to half-screen or running with the Copilot sidebar.
  - Manual Compact Toggle: Exposed `Shift + C` hotkey and `?compact=true` / `?edge=true` URL parameters with local storage persistence.


---

## 6. DevOps, Resilience & Testing
- [x] **Containerization & Docker Compose (1-Command Startup)**:
  - Multi-stage `Dockerfile` targeting .NET 9 SDK (build) and ASP.NET Core 9 (runtime) with layer caching and lean deployment footprint.
  - Complete `docker-compose.yml` orchestrating Redis 7, SQL Server 2022, automated database migration runner, and API services with health checks and bridge networking (`graffiti-net`).
  - Automated SQL migration runner (`entrypoint-migrations.sh`) applying `000_init_database.sql` through `004_create_canvas_palette.sql` in strict dependency order before API launch.
  - Self-healing startup schema checks in `CanvasInitializerService` (`EnsureBaseSchemaAsync`, `EnsureShadowBanSchemaAsync`, `EnsureReservationSchemaAsync`, etc.).
  - Configurable `.env.example` and `.dockerignore` for environment port and secret customization.
- [x] **Automated Test Suite (xUnit & FluentAssertions)**:
  - Created `GlobalGraffitiWall.Tests` test project structured in `GlobalGraffitiWall.sln`.
  - Comprehensive unit test coverage across 4 core domains (62 passed tests in 212ms):
    - **Coordinate Math & Viewport**: 8-bit row-major indexing (`(y * Width) + x`), boundary condition testing (`(0,0)`, `(9999,9999)`), $160 \times 160$ minimap coordinate mapping, and $256 \times 256$ 2D tile chunk coverage (40 tiles/axis = 1,600 total chunks).
    - **Rate Limiting & Token Bucket Math**: Fractional refill rates (1 charge / 5s), capacity capping at 16 tokens, burst deduction, cooldown wait calculation, and bonus balance fallback logic.
    - **Spatial Collisions & Territory Protection**: Comprehensive rectangle intersection logic (`Overlaps`), interior point containment (`Contains`), inclusive coordinate dimension boundaries (`x2 - x1 + 1`), and expiration evaluation.
    - **Security & Cryptography**: PBKDF2 SHA-256 password hashing with 100,000 iterations, unique 128-bit salt generation, case sensitivity, constant-time comparison, and malformed input resilience.
    - **Color Palette & Active Flags**: Hex format validation, $O(1)$ constant-time active bit flag indexing, and sort order preservation.
- [x] **Health Checks & Telemetry**:
  - Implemented ASP.NET Core Health Checks for Redis (`RedisHealthCheck`) and SQL Server (`SqlServerHealthCheck`) with round-trip query latency and error capture.
  - Formatted developer-friendly JSON output (`HealthCheckResponseWriter`) served at `/health` and `/health/ready`.
  - Added fast liveness probe at `/health/live` for process responsiveness.
  - Added automated Docker container healthcheck probe in `docker-compose.yml` (`curl -f http://localhost:8080/health || exit 1`) with `curl` baked into runtime container.
  - Implemented OpenTelemetry-compatible `CanvasMetrics` tracking `canvas.pixels.placed`, `canvas.batches.flushed`, `canvas.signalr.active_connections`, and `canvas.batch_writer.duration_ms`.
  - Exposed live system metrics endpoint (`GET /api/canvas/telemetry`) reporting active connections, lifetime session placements, and server uptime.
- [ ] **GitHub Actions CI/CD Pipeline (`.github/workflows/ci.yml`)**:
  - Continuous integration workflow that automatically triggers on every push and pull request to `main`.
  - Automatically executes `dotnet restore`, `dotnet build`, `dotnet test GlobalGraffitiWall.sln`, and verifies `docker build`.
  - Displays build and test pass status badges on the GitHub repository.

---

## 7. Community Sharing & Real-Time Presence
- [x] **Canvas Region Snapshot & PNG Export (`GET /api/canvas/export`)**:
  - Implemented high-performance, zero-dependency `PngEncoder.cs` producing 8-bit indexed PNG images with embedded 256-color `PLTE` chunk and `ZLibStream` DEFLATE compression.
  - Implemented atomic server-side Redis Lua script (`ExportRegionLuaScript`) to slice custom rectangular coordinate bounds in row-major order directly from Redis memory in < 5ms.
  - Exposed `GET /api/canvas/export` supporting `wallId`, coordinate bounding box (`x`, `y`, `width`, `height`), and crisp pixel art integer scaling (`1x`, `2x`, `4x`, `8x`).
  - Added unit test suite in `PngEncoderTests.cs` (15 tests) validating PNG magic signature, `IHDR`, `PLTE` color mapping, `IDAT` decompression, scanlines, and scaling.
  - Built Cyberpunk Export modal (`#export-modal`, hotkey `Shift + E` or `[📷 Export]` HUD tool) supporting Full Canvas, Current Viewport, and Custom Region presets with live dimension calculation and one-click download.
- [ ] **Live Multi-User Painter Presence & Cursors**:
  - Stream real-time remote painter cursor coordinates and active color reticles over SignalR to other users viewing the same canvas region.
  - Enhances multiplayer vibe coding feel with visual indicator tags of active painters.

---

## 8. Live Public Deployment & Production Hosting Guide (Going Live Online)
- [ ] **Public Production Hosting & Deployment Runbook (`DEPLOYMENT.md`)**:
  - **Cloud Hosting & Infrastructure Strategy**:
    - **Option A (Recommended & Cost-Effective: Single-Node VPS)**: Deploy via Docker Compose on a $5–$15/mo Linux VPS (Hetzner, DigitalOcean Droplet, Linode, AWS EC2 t4g, or Azure B2s) with 2–4 GB RAM.
    - **Option B (Fully Managed Cloud / Serverless)**: Azure Container Apps or AWS ECS with Azure SQL Database / Amazon RDS and Azure Cache for Redis.
  - **Automated SSL/TLS & Reverse Proxy Configuration**:
    - Add a lightweight reverse proxy (Caddy or Nginx) to `docker-compose.prod.yml` with automatic HTTPS via Let's Encrypt.
    - Configure WebSocket proxy headers (`Connection: Upgrade`, `Upgrade: websocket`) with infinite keep-alive timeouts for persistent SignalR streaming.
  - **Production Hardening & Network Security**:
    - Lock down Docker network: Bind Redis (`6379`) and SQL Server (`1433`) strictly to internal bridge network (`graffiti-net`); only expose ports 80/443 to the public internet.
    - Production environment configuration (`.env.production`): Generate cryptographically secure `SA_PASSWORD`, 512-bit `JwtSettings:SecretKey`, and disable development swagger in production.
    - CORS Policy Hardening: Restrict allowed origins in `Program.cs` from open `AllowAnyOrigin` to the verified production domain and subdomains.
    - Configure UFW / cloud security groups allowing only SSH (port 22), HTTP (port 80), and HTTPS (port 443).
  - **Cloudflare CDN, DDoS Protection & WebSocket Proxy**:
    - Set up free Cloudflare proxy for DNS, Web Application Firewall (WAF), global edge caching for static assets (`/index.html`, `/css`, `/js`), and L3/L4/L7 DDoS mitigation.
    - Enable Cloudflare WebSockets toggle and configure `X-Forwarded-For` / `CF-Connecting-IP` real IP forwarding for rate limiting and shadow banning.
  - **High-Concurrency Live Audience Tuning**:
    - Configure SignalR Redis Backplane (`AddStackExchangeRedis`) to allow horizontal multi-instance scaling when user traffic surges.
    - Linux kernel TCP & socket tuning (`sysctl net.core.somaxconn=4096`, `nofile=65536`) to handle 5,000+ simultaneous painters without WebSocket connection drops.
    - Provide a complete, copy-pasteable step-by-step checklist from clean server to live public URL in `DEPLOYMENT.md`.



