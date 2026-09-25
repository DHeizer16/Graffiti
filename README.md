# Graffiti

> A Multi-User Pixel art app Vibe coded with AntiGravity

A high-performance, real-time massive multiplayer pixel canvas ($10,000 \times 10,000$ pixels, 100 million total pixels) built with **ASP.NET Core 9**, **Redis**, **SQL Server**, and **SignalR**.

---

## Architecture Overview

```
                      +-----------------------------+
                      |       Web Frontend          |
                      |  (HTML5 Canvas + SignalR)   |
                      +--------------+--------------+
                                     |
                       HTTP / WebSockets (SignalR)
                                     |
                                     v
                      +-----------------------------+
                      |   ASP.NET Core 9 Web API    |
                      |    (Kestrel + Token Bucket) |
                      +-------+--------------+------+
                              |              |
                      Atomic Byte Read/Write | Async Batch Queue (Channel)
                              |              | (250ms / 1000 items)
                              v              v
                  +-------------------+  +-------------------+
                  |       Redis       |  |    SQL Server     |
                  | (100MB 8-bit RAM) |  |   (SqlBulkCopy)   |
                  +-------------------+  +-------------------+
```

- **In-Memory Redis Buffer**: The $10,000 \times 10,000$ canvas state is kept in Redis as a continuous 100 MB 8-bit byte array (`canvas:global_state`), providing sub-millisecond coordinate reads and writes.
- **Batched Persistence Queue**: In-memory `Channel<PixelPlacementItem>` backed by `PixelBatchWriterService`, flushing accumulated pixel placements to SQL Server every 250ms or 1,000 items via `SqlBulkCopy`.
- **Dynamic 2D Viewport Tiling**: Clients stream on-demand $256 \times 256$ chunks (64 KB binary payloads) using an atomic Redis Lua script via `GET /api/canvas/tile?tx={tx}&ty={ty}`.
- **Minimap Overview**: Fast 25.6 KB ($160 \times 160$ bytes) minimap overview buffer (`canvas:minimap_overview`) served via `GET /api/canvas/minimap` for global radar awareness.

---

## 256-Color Palette System & Dynamic 32-Color Selection

The canvas engine uses an 8-bit byte per pixel, supporting a full 256-color palette. To maintain visual clarity while offering rich creative flexibility, the active palette is curated to **32 active colors** arranged in two 16-color rows.

### Database Configuration (`canvas_palette`)

All 256 colors are defined in the `canvas_palette` table (`Scripts/004_create_canvas_palette.sql`) with RGB hex codes, descriptive names, sorting orders, and an `is_active` flag.

#### Changing Active Colors Dynamically:
Any color can be enabled or disabled directly via SQL Server without restarting the application:

```sql
-- Activate a color (e.g., Color ID 200 - Cyberpunk Neon Pink)
UPDATE canvas_palette 
SET is_active = 1 
WHERE id = 200;

-- Deactivate a color (e.g., Color ID 4 - Light Pink)
UPDATE canvas_palette 
SET is_active = 0 
WHERE id = 4;
```

The in-memory `PaletteService` caches and periodically refreshes the active palette. `CanvasHub.PlacePixel` strictly validates placements against active colors, and the frontend updates via `GET /api/canvas/palette`.

### Active 32-Color Palette & Hotkey Mapping

| Row | Range | Hotkeys | Description |
| :--- | :--- | :--- | :--- |
| **Row 1** | ID 0 – 15 | `1`, `2`, `3`, `4`, `5`, `6`, `7`, `8`, `9`, `0`, `Q`, `W`, `E`, `R`, `T`, `Y` | **Classic 16 Colors**: Pure White, Light Gray, Medium Gray, Dark Charcoal, Light Pink, Crimson Red, Vivid Orange, Earth Brown, Sunflower Yellow, Lime Green, Vibrant Green, Aqua Cyan, Ocean Blue, Royal Blue, Soft Magenta, Deep Purple |
| **Row 2** | ID 16 – 31 | `A`, `S`, `D`, `F`, `G`, `H`, `J`, `K`, `O`, `X`, `C`, `V`, `N`, `;`, `-`, `=` | **New Curated 16 Colors**: Pure Black, Slate Gray, Deep Maroon, Coral Red, Blush Pink, Peach Skin, Warm Cream, Amber Gold, Olive Drab, Forest Green, Mint Green, Teal, Seafoam Cyan, Indigo, Amethyst Purple, Coffee Brown |

### UI & Layout Features
- **Row-Major Grid Flow**: 16 columns $\times$ 2 rows (`repeat(16, 26px)`), eliminating horizontal scrollbars and container clipping.
- **Edge Browser & Compact Mode**: Automatically applies compact `repeat(16, 21px)` swatches to prevent overlap with the Radar HUD and tools.
- **Mobile Responsive**: Adapts to centered 18px swatches at `@media (max-width: 650px)`.
- **Cache-Control & Cache Busting**: Configured in ASP.NET Core Kestrel (`StaticFileOptions`) with `no-cache, no-store, must-revalidate, max-age=0` and HTML `<meta>` tags to eliminate stale browser caching.

---

## Core Features

### 1. Multi-Wall Canvas Studios
- **Global Wall**: 10,000 $\times$ 10,000 public community canvas.
- **Private Studios**: Authenticated users can create custom walls ($500 \times 500$, $1000 \times 1000$, $2048 \times 2048$, or custom dimensions up to $4000 \times 4000$) with Public, Unlisted, Password-Protected, or Private access permissions.
- **SignalR Group Isolation**: Placements and updates are partitioned by `wallId` (`wall:{wallId}`). Studio owners enjoy unlimited paint placement.

### 2. Territory Reservations (Zone Protection)
- Reserve rectangular bounding boxes ($5 \times 5$ to $128 \times 128$) for 15m, 1h, 4h, 12h, or 24h.
- Unauthorized placements are intercepted and rejected prior to charge deduction.
- Collaborator secret keys permit team collaboration inside reserved zones.
- Automatic expiration worker (`ReservationExpirationService`) frees expired claims.

### 3. Moderation & Anti-Abuse
- **Shadow Banning Engine**: Placements by banned IPs/users are silently isolated into SQL with `is_shadow_banned = 1`, bypassing Redis mutation and public broadcasts.
- **Role-Based Access Control (RBAC)**: `User`, `Moderator`, and `Admin` roles with administrative role management endpoints (`POST /api/moderation/set-role`).

### 4. Time-Lapse & Replay
- High-speed delta timeline scrubber (`/api/history/deltas`) with variable playback speeds (1 min/s to 1 day/s) and dual start/end time pickers.

---

## API Endpoints

### Canvas & Tiles
- `GET /api/canvas/tile?tx={tx}&ty={ty}&wallId={wallId}`: Stream $256 \times 256$ binary chunk (64 KB).
- `GET /api/canvas/minimap?wallId={wallId}`: Fetch $160 \times 160$ minimap buffer (25.6 KB).
- `GET /api/canvas/pixel-info?x={x}&y={y}&wallId={wallId}`: Detailed coordinate history, author, and timestamp.
- `GET /api/canvas/palette`: Returns all 256 palette colors.
- `GET /api/canvas/palette?activeOnly=true`: Returns the 32 currently active colors.

### Authentication & Profiles
- `POST /api/auth/register`: Register new painter account.
- `POST /api/auth/login`: Authenticate and receive JWT bearer token.
- `GET /api/auth/me`: Fetch current painter profile, stats, and role.

### Wall Studios
- `GET /api/walls`: List public walls.
- `GET /api/walls/my`: List studios created by the authenticated user.
- `POST /api/walls`: Create a new custom canvas studio.
- `DELETE /api/walls/{id}`: Deactivate/delete a canvas studio.

### Territory Reservations
- `GET /api/reservations/active?wallId={wallId}`: Get active territory zones.
- `POST /api/reservations`: Reserve a rectangular territory.
- `DELETE /api/reservations/{id}`: Release a reserved zone early.

### Health Checks & Telemetry
- `GET /health`: Comprehensive JSON health report for Redis and SQL Server with round-trip query latency.
- `GET /health/ready`: Readiness probe for container orchestration.
- `GET /health/live`: Lightweight liveness probe for process responsiveness.
- `GET /api/canvas/telemetry`: Real-time session telemetry (active SignalR connections, placements, uptime).

---

## Getting Started

### Option 1: 1-Command Startup with Docker Compose (Recommended)

Requires [Docker Desktop](https://www.docker.com/products/docker-desktop/).

```bash
# Clone the repository
git clone https://github.com/DHeizer16/Graffiti.git
cd Graffiti

# Start all services (Redis, SQL Server with automated migrations, and API)
docker compose up -d --build
```

Navigate to `http://localhost:5217` in your browser.

- **Redis**: Automatically provisioned with persistence on port `6379`.
- **SQL Server**: Automatically initialized with database `Graffiti` and all 5 migration scripts applied in dependency order.
- **API**: ASP.NET Core 9 container running on port `5217`.

To stop services:
```bash
docker compose down
```

---

### Option 2: Local Development Setup (.NET 9 SDK)

#### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Redis](https://redis.io/) running on `localhost:6379`
- [Microsoft SQL Server](https://www.microsoft.com/sql-server) running on `localhost`

#### Database Setup
Execute the SQL migration scripts located in `Scripts/` in order:
1. `Scripts/000_init_database.sql`
2. `Scripts/001_create_shadow_bans.sql`
3. `Scripts/002_create_canvas_reservations.sql`
4. `Scripts/003_create_users_table.sql`
5. `Scripts/003_create_canvas_walls.sql`
6. `Scripts/004_create_canvas_palette.sql`

#### Build & Run
```bash
# Build solution
dotnet build

# Run API & web server
dotnet run
```

Navigate to `http://localhost:5217` in your browser.
