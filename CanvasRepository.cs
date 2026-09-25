using System.Data;
using Microsoft.Data.SqlClient;
using Dapper;

namespace GlobalGraffitiWall.API;

public class CanvasRepository
{
    private readonly string _connectionString;

    public CanvasRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServerConnection")!;
    }



    /// <summary>
    /// High-performance bulk insertion of queued pixel placements using SqlBulkCopy.
    /// </summary>
    public async Task BulkInsertPixelPlacementsAsync(IReadOnlyList<PixelPlacementItem> placements)
    {
        if (placements == null || placements.Count == 0) return;

        using var table = new DataTable();
        table.Columns.Add("x", typeof(int));
        table.Columns.Add("y", typeof(int));
        table.Columns.Add("color_id", typeof(byte));
        table.Columns.Add("user_id", typeof(Guid));
        table.Columns.Add("ip_address", typeof(string));
        table.Columns.Add("placed_at", typeof(DateTimeOffset));
        table.Columns.Add("is_shadow_banned", typeof(bool));
        table.Columns.Add("wall_id", typeof(Guid));

        foreach (var p in placements)
        {
            table.Rows.Add(p.X, p.Y, p.ColorId, p.UserId, (object?)p.IpAddress ?? DBNull.Value, p.PlacedAt, p.IsShadowBanned, (object?)p.WallId ?? DBNull.Value);
        }

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null)
        {
            DestinationTableName = "pixel_placements",
            BatchSize = placements.Count,
            BulkCopyTimeout = 30
        };

        bulkCopy.ColumnMappings.Add("x", "x");
        bulkCopy.ColumnMappings.Add("y", "y");
        bulkCopy.ColumnMappings.Add("color_id", "color_id");
        bulkCopy.ColumnMappings.Add("user_id", "user_id");
        bulkCopy.ColumnMappings.Add("ip_address", "ip_address");
        bulkCopy.ColumnMappings.Add("placed_at", "placed_at");
        bulkCopy.ColumnMappings.Add("is_shadow_banned", "is_shadow_banned");
        bulkCopy.ColumnMappings.Add("wall_id", "wall_id");

        await bulkCopy.WriteToServerAsync(table);
    }

    /// <summary>
    /// Fetches all non-banned historical pixel placements from SQL Server to sync Redis state on boot.
    /// </summary>
    public async Task<IEnumerable<(int X, int Y, byte ColorId)>> GetAllPixelPlacementsAsync(Guid? wallId = null)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        string sql = wallId == null
            ? """
              SELECT x AS X, y AS Y, color_id AS ColorId
              FROM pixel_placements WITH (NOLOCK)
              WHERE is_shadow_banned = 0 AND wall_id IS NULL
              ORDER BY placed_at ASC;
              """
            : """
              SELECT x AS X, y AS Y, color_id AS ColorId
              FROM pixel_placements WITH (NOLOCK)
              WHERE is_shadow_banned = 0 AND wall_id = @WallId
              ORDER BY placed_at ASC;
              """;

        return await db.QueryAsync<(int X, int Y, byte ColorId)>(sql, new { WallId = wallId });
    }

    /// <summary>
    /// Fetches the recent placement history and total placement count for a specific pixel coordinate.
    /// Excludes shadow-banned placements from public auditing. Includes author username if registered.
    /// </summary>
    public async Task<(List<PixelPlacementHistoryDto> Placements, int TotalCount)> GetPixelHistoryAsync(int x, int y, int limit = 10, Guid? wallId = null)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        string wallFilter = wallId == null ? "AND p.wall_id IS NULL" : "AND p.wall_id = @WallId";
        string wallFilterCount = wallId == null ? "AND wall_id IS NULL" : "AND wall_id = @WallId";

        string sql = $"""
            SELECT TOP (@Limit)
                p.color_id AS ColorId,
                p.user_id AS UserId,
                p.placed_at AS PlacedAt,
                u.username AS Username,
                u.display_name AS DisplayName
            FROM pixel_placements p WITH (NOLOCK)
            LEFT JOIN users u WITH (NOLOCK) ON u.id = p.user_id
            WHERE p.x = @X AND p.y = @Y AND p.is_shadow_banned = 0 {wallFilter}
            ORDER BY p.placed_at DESC;

            SELECT COUNT(*) 
            FROM pixel_placements WITH (NOLOCK)
            WHERE x = @X AND y = @Y AND is_shadow_banned = 0 {wallFilterCount};
            """;

        using var multi = await db.QueryMultipleAsync(sql, new { X = x, Y = y, Limit = limit, WallId = wallId });
        var placements = (await multi.ReadAsync<PixelPlacementHistoryDto>()).AsList();
        int totalCount = await multi.ReadSingleAsync<int>();

        return (placements, totalCount);
    }

    /// <summary>
    /// Retrieves all active shadow bans from SQL Server.
    /// </summary>
    public async Task<IEnumerable<ShadowBanRecord>> GetActiveShadowBansAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        const string sql = """
            SELECT 
                id AS Id,
                identifier AS Identifier,
                ban_type AS BanType,
                reason AS Reason,
                banned_by AS BannedBy,
                banned_at AS BannedAt,
                is_active AS IsActive
            FROM shadow_bans WITH (NOLOCK)
            WHERE is_active = 1
            ORDER BY banned_at DESC;
            """;

        return await db.QueryAsync<ShadowBanRecord>(sql);
    }

    /// <summary>
    /// Adds or updates an active shadow ban in SQL Server.
    /// </summary>
    public async Task AddOrUpdateShadowBanAsync(string identifier, string banType, string? reason, string? bannedBy)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        const string sql = """
            IF EXISTS (SELECT 1 FROM shadow_bans WHERE identifier = @Identifier)
            BEGIN
                UPDATE shadow_bans
                SET is_active = 1,
                    ban_type = @BanType,
                    reason = @Reason,
                    banned_by = @BannedBy,
                    banned_at = SYSDATETIMEOFFSET()
                WHERE identifier = @Identifier;
            END
            ELSE
            BEGIN
                INSERT INTO shadow_bans (identifier, ban_type, reason, banned_by, banned_at, is_active)
                VALUES (@Identifier, @BanType, @Reason, @BannedBy, SYSDATETIMEOFFSET(), 1);
            END
            """;

        await db.ExecuteAsync(sql, new
        {
            Identifier = identifier,
            BanType = banType,
            Reason = reason,
            BannedBy = bannedBy ?? "Admin"
        });
    }

    /// <summary>
    /// Deactivates a shadow ban in SQL Server.
    /// </summary>
    public async Task DeactivateShadowBanAsync(string identifier)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        const string sql = """
            UPDATE shadow_bans
            SET is_active = 0
            WHERE identifier = @Identifier;
            """;

        await db.ExecuteAsync(sql, new { Identifier = identifier });
    }

    /// <summary>
    /// Returns recent pixel placements intercepted by the shadow-ban engine for moderator inspection.
    /// </summary>
    public async Task<IEnumerable<ShadowPlacementAuditDto>> GetShadowBannedPlacementsAsync(int limit = 50)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        const string sql = """
            SELECT TOP (@Limit)
                x AS X,
                y AS Y,
                color_id AS ColorId,
                user_id AS UserId,
                ip_address AS IpAddress,
                placed_at AS PlacedAt
            FROM pixel_placements WITH (NOLOCK)
            WHERE is_shadow_banned = 1
            ORDER BY placed_at DESC;
            """;

        return await db.QueryAsync<ShadowPlacementAuditDto>(sql, new { Limit = Math.Clamp(limit, 1, 500) });
    }

    /// <summary>
    /// Ensures the canvas_reservations table exists.
    /// </summary>
    public async Task EnsureReservationSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_reservations')
            BEGIN
                CREATE TABLE canvas_reservations (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    reservation_id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                    owner_id NVARCHAR(100) NOT NULL,
                    owner_name NVARCHAR(100) NOT NULL,
                    secret_key NVARCHAR(100) NOT NULL,
                    x1 INT NOT NULL,
                    y1 INT NOT NULL,
                    x2 INT NOT NULL,
                    y2 INT NOT NULL,
                    label NVARCHAR(100) NULL,
                    created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    expires_at DATETIMEOFFSET NOT NULL,
                    is_active BIT NOT NULL DEFAULT 1
                );
                CREATE INDEX IX_canvas_reservations_active ON canvas_reservations(is_active, expires_at);
                CREATE INDEX IX_canvas_reservations_owner ON canvas_reservations(owner_id, is_active);
            END
            """;
        await db.ExecuteAsync(sql);
    }

    /// <summary>
    /// Fetches all active, unexpired canvas reservations.
    /// </summary>
    public async Task<IEnumerable<CanvasReservation>> GetActiveReservationsAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                id AS Id,
                reservation_id AS ReservationId,
                owner_id AS OwnerId,
                owner_name AS OwnerName,
                secret_key AS SecretKey,
                x1 AS X1,
                y1 AS Y1,
                x2 AS X2,
                y2 AS Y2,
                label AS Label,
                created_at AS CreatedAt,
                expires_at AS ExpiresAt,
                is_active AS IsActive
            FROM canvas_reservations WITH (NOLOCK)
            WHERE is_active = 1 AND expires_at > SYSDATETIMEOFFSET()
            ORDER BY created_at ASC;
            """;
        return await db.QueryAsync<CanvasReservation>(sql);
    }

    /// <summary>
    /// Persists a new canvas reservation.
    /// </summary>
    public async Task InsertReservationAsync(CanvasReservation reservation)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            INSERT INTO canvas_reservations 
                (reservation_id, owner_id, owner_name, secret_key, x1, y1, x2, y2, label, created_at, expires_at, is_active)
            VALUES 
                (@ReservationId, @OwnerId, @OwnerName, @SecretKey, @X1, @Y1, @X2, @Y2, @Label, @CreatedAt, @ExpiresAt, @IsActive);
            """;
        await db.ExecuteAsync(sql, reservation);
    }

    /// <summary>
    /// Deactivates a reservation by its GUID identifier.
    /// </summary>
    public async Task DeactivateReservationAsync(Guid reservationId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            UPDATE canvas_reservations
            SET is_active = 0
            WHERE reservation_id = @ReservationId;
            """;
        await db.ExecuteAsync(sql, new { ReservationId = reservationId });
    }

    /// <summary>
    /// Automatically deactivates all reservations that have expired before the specified timestamp.
    /// </summary>
    public async Task<int> ExpireOldReservationsAsync(DateTimeOffset cutoff)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            UPDATE canvas_reservations
            SET is_active = 0
            WHERE is_active = 1 AND expires_at <= @Cutoff;
            """;
        return await db.ExecuteAsync(sql, new { Cutoff = cutoff });
    }

    /// <summary>
    /// Returns the number of currently active reservations owned by a specific owner ID.
    /// </summary>
    public async Task<int> GetActiveReservationCountForOwnerAsync(string ownerId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT COUNT(*)
            FROM canvas_reservations WITH (NOLOCK)
            WHERE owner_id = @OwnerId AND is_active = 1 AND expires_at > SYSDATETIMEOFFSET();
            """;
        return await db.ExecuteScalarAsync<int>(sql, new { OwnerId = ownerId });
    }

    /// <summary>
    /// Retrieves a reservation by its GUID identifier regardless of active state.
    /// </summary>
    public async Task<CanvasReservation?> GetReservationByIdAsync(Guid reservationId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                id AS Id,
                reservation_id AS ReservationId,
                owner_id AS OwnerId,
                owner_name AS OwnerName,
                secret_key AS SecretKey,
                x1 AS X1,
                y1 AS Y1,
                x2 AS X2,
                y2 AS Y2,
                label AS Label,
                created_at AS CreatedAt,
                expires_at AS ExpiresAt,
                is_active AS IsActive
            FROM canvas_reservations WITH (NOLOCK)
            WHERE reservation_id = @ReservationId;
            """;
        return await db.QuerySingleOrDefaultAsync<CanvasReservation>(sql, new { ReservationId = reservationId });
    }

    /// <summary>
    /// Retrieves an active, non-expired reservation by matching its secret collaborator key.
    /// </summary>
    public async Task<CanvasReservation?> GetActiveReservationBySecretKeyAsync(string secretKey)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                id AS Id,
                reservation_id AS ReservationId,
                owner_id AS OwnerId,
                owner_name AS OwnerName,
                secret_key AS SecretKey,
                x1 AS X1,
                y1 AS Y1,
                x2 AS X2,
                y2 AS Y2,
                label AS Label,
                created_at AS CreatedAt,
                expires_at AS ExpiresAt,
                is_active AS IsActive
            FROM canvas_reservations WITH (NOLOCK)
            WHERE UPPER(LTRIM(RTRIM(secret_key))) = UPPER(LTRIM(RTRIM(@SecretKey)))
              AND is_active = 1
              AND expires_at > SYSDATETIMEOFFSET();
            """;
        return await db.QueryFirstOrDefaultAsync<CanvasReservation>(sql, new { SecretKey = secretKey });
    }

    // =========================================================================
    // User Accounts, Profiles & History Linking
    // =========================================================================

    /// <summary>
    /// Ensures that the users table and its indices exist in SQL Server.
    /// </summary>
    public async Task EnsureUserSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'users')
            BEGIN
                CREATE TABLE users (
                    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                    username NVARCHAR(50) NOT NULL,
                    normalized_username NVARCHAR(50) NOT NULL,
                    display_name NVARCHAR(100) NOT NULL,
                    password_hash NVARCHAR(256) NOT NULL,
                    password_salt NVARCHAR(128) NOT NULL,
                    created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    last_login_at DATETIMEOFFSET NULL,
                    is_active BIT NOT NULL DEFAULT 1,
                    role NVARCHAR(20) NOT NULL DEFAULT 'User'
                );

                CREATE UNIQUE INDEX IX_users_normalized_username ON users(normalized_username);
                CREATE INDEX IX_users_created_at ON users(created_at);
            END

            -- Ensure at least one Admin exists if users are registered
            IF NOT EXISTS (SELECT 1 FROM users WHERE role = 'Admin')
            BEGIN
                UPDATE users SET role = 'Admin'
                WHERE id = (SELECT TOP 1 id FROM users ORDER BY created_at ASC);
            END
            """;
        await db.ExecuteAsync(sql);
    }

    /// <summary>
    /// Creates a new user in the database.
    /// </summary>
    public async Task CreateUserAsync(User user)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            INSERT INTO users 
                (id, username, normalized_username, display_name, password_hash, password_salt, created_at, last_login_at, is_active, role)
            VALUES 
                (@Id, @Username, @NormalizedUsername, @DisplayName, @PasswordHash, @PasswordSalt, @CreatedAt, @LastLoginAt, @IsActive, @Role);
            """;
        await db.ExecuteAsync(sql, user);
    }

    /// <summary>
    /// Retrieves a user by their case-insensitive username.
    /// </summary>
    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                id AS Id,
                username AS Username,
                normalized_username AS NormalizedUsername,
                display_name AS DisplayName,
                password_hash AS PasswordHash,
                password_salt AS PasswordSalt,
                created_at AS CreatedAt,
                last_login_at AS LastLoginAt,
                is_active AS IsActive,
                role AS Role
            FROM users WITH (NOLOCK)
            WHERE normalized_username = @NormalizedUsername;
            """;
        return await db.QuerySingleOrDefaultAsync<User>(sql, new { NormalizedUsername = username.Trim().ToUpperInvariant() });
    }

    /// <summary>
    /// Retrieves a user by their GUID identifier.
    /// </summary>
    public async Task<User?> GetUserByIdAsync(Guid id)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                id AS Id,
                username AS Username,
                normalized_username AS NormalizedUsername,
                display_name AS DisplayName,
                password_hash AS PasswordHash,
                password_salt AS PasswordSalt,
                created_at AS CreatedAt,
                last_login_at AS LastLoginAt,
                is_active AS IsActive,
                role AS Role
            FROM users WITH (NOLOCK)
            WHERE id = @Id;
            """;
        return await db.QuerySingleOrDefaultAsync<User>(sql, new { Id = id });
    }

    /// <summary>
    /// Updates the last login timestamp for a user.
    /// </summary>
    public async Task UpdateUserLastLoginAsync(Guid id, DateTimeOffset lastLogin)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "UPDATE users SET last_login_at = @LastLogin WHERE id = @Id;";
        await db.ExecuteAsync(sql, new { Id = id, LastLogin = lastLogin });
    }

    /// <summary>
    /// Retrieves the total number of non-banned pixels placed by a user.
    /// </summary>
    public async Task<int> GetUserPlacementCountAsync(Guid userId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "SELECT COUNT(*) FROM pixel_placements WITH (NOLOCK) WHERE user_id = @UserId AND is_shadow_banned = 0;";
        return await db.ExecuteScalarAsync<int>(sql, new { UserId = userId });
    }

    /// <summary>
    /// Links/migrates historical guest pixel placements and territory reservations to an authenticated user account.
    /// </summary>
    public async Task<LinkGuestHistoryResult> LinkGuestHistoryToUserAsync(Guid guestUserId, Guid authenticatedUserId, string displayName)
    {
        if (guestUserId == authenticatedUserId || guestUserId == Guid.Empty)
            return new LinkGuestHistoryResult();

        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            UPDATE pixel_placements
            SET user_id = @AuthUserId
            WHERE user_id = @GuestUserId;
            DECLARE @UpdatedPixels INT = @@ROWCOUNT;

            UPDATE canvas_reservations
            SET owner_id = @AuthUserIdString,
                owner_name = @DisplayName
            WHERE owner_id = @GuestUserIdString;
            DECLARE @UpdatedReservations INT = @@ROWCOUNT;

            SELECT @UpdatedPixels AS LinkedPixels, @UpdatedReservations AS LinkedReservations;
            """;

        return await db.QuerySingleAsync<LinkGuestHistoryResult>(sql, new
        {
            AuthUserId = authenticatedUserId,
            GuestUserId = guestUserId,
            AuthUserIdString = authenticatedUserId.ToString(),
            GuestUserIdString = guestUserId.ToString(),
            DisplayName = displayName
        });
    }

    /// <summary>
    /// Returns the total number of registered users.
    /// </summary>
    public async Task<int> GetUserCountAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "SELECT COUNT(*) FROM users WITH (NOLOCK);";
        return await db.ExecuteScalarAsync<int>(sql);
    }

    /// <summary>
    /// Updates the role of a user by username.
    /// </summary>
    public async Task<bool> SetUserRoleAsync(string username, string role)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "UPDATE users SET role = @Role WHERE normalized_username = @NormalizedUsername;";
        var rows = await db.ExecuteAsync(sql, new { Role = role, NormalizedUsername = username.Trim().ToUpperInvariant() });
        return rows > 0;
    }

    // =========================================================================
    // Private Pixel Walls Schema & Data Access
    // =========================================================================

    /// <summary>
    /// Ensures that the canvas_walls table exists and updates pixel_placements and canvas_reservations with wall_id.
    /// </summary>
    public async Task EnsureWallSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_walls')
            BEGIN
                CREATE TABLE canvas_walls (
                    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                    owner_id UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES users(id),
                    name NVARCHAR(100) NOT NULL,
                    description NVARCHAR(500) NULL,
                    width INT NOT NULL DEFAULT 1000,
                    height INT NOT NULL DEFAULT 1000,
                    access_type NVARCHAR(20) NOT NULL DEFAULT 'Public',
                    access_key NVARCHAR(100) NULL,
                    created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    is_active BIT NOT NULL DEFAULT 1
                );

                CREATE INDEX IX_canvas_walls_owner ON canvas_walls(owner_id, is_active);
                CREATE INDEX IX_canvas_walls_created ON canvas_walls(created_at, is_active);
            END

            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_NAME = 'pixel_placements' AND COLUMN_NAME = 'wall_id'
            )
            BEGIN
                ALTER TABLE pixel_placements ADD wall_id UNIQUEIDENTIFIER NULL;
                CREATE INDEX IX_pixel_placements_wall_id ON pixel_placements(wall_id, is_shadow_banned, placed_at);
            END

            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_NAME = 'canvas_reservations' AND COLUMN_NAME = 'wall_id'
            )
            BEGIN
                ALTER TABLE canvas_reservations ADD wall_id UNIQUEIDENTIFIER NULL;
                CREATE INDEX IX_canvas_reservations_wall_id ON canvas_reservations(wall_id, is_active, expires_at);
            END
            """;
        await db.ExecuteAsync(sql);
    }

    /// <summary>
    /// Creates a new private wall in the database.
    /// </summary>
    public async Task CreateWallAsync(CanvasWall wall)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            INSERT INTO canvas_walls
                (id, owner_id, name, description, width, height, access_type, access_key, created_at, is_active)
            VALUES
                (@Id, @OwnerId, @Name, @Description, @Width, @Height, @AccessType, @AccessKey, @CreatedAt, @IsActive);
            """;
        await db.ExecuteAsync(sql, wall);
    }

    /// <summary>
    /// Retrieves a wall by ID including owner username and pixel count.
    /// </summary>
    public async Task<CanvasWall?> GetWallByIdAsync(Guid id)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                w.id AS Id,
                w.owner_id AS OwnerId,
                ISNULL(u.username, 'Unknown') AS OwnerUsername,
                w.name AS Name,
                w.description AS Description,
                w.width AS Width,
                w.height AS Height,
                w.access_type AS AccessType,
                w.access_key AS AccessKey,
                w.created_at AS CreatedAt,
                w.is_active AS IsActive,
                ISNULL((SELECT COUNT(*) FROM pixel_placements p WITH (NOLOCK) WHERE p.wall_id = w.id AND p.is_shadow_banned = 0), 0) AS PixelCount
            FROM canvas_walls w WITH (NOLOCK)
            LEFT JOIN users u WITH (NOLOCK) ON u.id = w.owner_id
            WHERE w.id = @Id AND w.is_active = 1;
            """;
        return await db.QuerySingleOrDefaultAsync<CanvasWall>(sql, new { Id = id });
    }

    /// <summary>
    /// Retrieves active public / unlisted walls for discovery.
    /// </summary>
    public async Task<IEnumerable<CanvasWall>> GetPublicWallsAsync(string? search = null, int limit = 50)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        string searchFilter = string.IsNullOrWhiteSpace(search) ? "" : "AND (w.name LIKE @Search OR w.description LIKE @Search)";

        string sql = $"""
            SELECT TOP (@Limit)
                w.id AS Id,
                w.owner_id AS OwnerId,
                ISNULL(u.username, 'Unknown') AS OwnerUsername,
                w.name AS Name,
                w.description AS Description,
                w.width AS Width,
                w.height AS Height,
                w.access_type AS AccessType,
                w.access_key AS AccessKey,
                w.created_at AS CreatedAt,
                w.is_active AS IsActive,
                ISNULL((SELECT COUNT(*) FROM pixel_placements p WITH (NOLOCK) WHERE p.wall_id = w.id AND p.is_shadow_banned = 0), 0) AS PixelCount
            FROM canvas_walls w WITH (NOLOCK)
            LEFT JOIN users u WITH (NOLOCK) ON u.id = w.owner_id
            WHERE w.is_active = 1 AND w.access_type != 'Private' {searchFilter}
            ORDER BY w.created_at DESC;
            """;
        return await db.QueryAsync<CanvasWall>(sql, new { Search = $"%{search}%", Limit = limit });
    }

    /// <summary>
    /// Retrieves all walls created by a specific user.
    /// </summary>
    public async Task<IEnumerable<CanvasWall>> GetUserWallsAsync(Guid ownerId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                w.id AS Id,
                w.owner_id AS OwnerId,
                ISNULL(u.username, 'Unknown') AS OwnerUsername,
                w.name AS Name,
                w.description AS Description,
                w.width AS Width,
                w.height AS Height,
                w.access_type AS AccessType,
                w.access_key AS AccessKey,
                w.created_at AS CreatedAt,
                w.is_active AS IsActive,
                ISNULL((SELECT COUNT(*) FROM pixel_placements p WITH (NOLOCK) WHERE p.wall_id = w.id AND p.is_shadow_banned = 0), 0) AS PixelCount
            FROM canvas_walls w WITH (NOLOCK)
            LEFT JOIN users u WITH (NOLOCK) ON u.id = w.owner_id
            WHERE w.owner_id = @OwnerId AND w.is_active = 1
            ORDER BY w.created_at DESC;
            """;
        return await db.QueryAsync<CanvasWall>(sql, new { OwnerId = ownerId });
    }

    /// <summary>
    /// Retrieves total count of active walls owned by a user.
    /// </summary>
    public async Task<int> GetUserWallCountAsync(Guid ownerId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "SELECT COUNT(*) FROM canvas_walls WITH (NOLOCK) WHERE owner_id = @OwnerId AND is_active = 1;";
        return await db.ExecuteScalarAsync<int>(sql, new { OwnerId = ownerId });
    }

    /// <summary>
    /// Deletes (deactivates) a wall.
    /// </summary>
    public async Task<bool> DeleteWallAsync(Guid id, Guid ownerId, bool isAdmin = false)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        string sql = isAdmin
            ? "UPDATE canvas_walls SET is_active = 0 WHERE id = @Id;"
            : "UPDATE canvas_walls SET is_active = 0 WHERE id = @Id AND owner_id = @OwnerId;";
        int rows = await db.ExecuteAsync(sql, new { Id = id, OwnerId = ownerId });
        return rows > 0;
    }

    // =========================================================================
    // 8-Bit Canvas Palette
    // =========================================================================

    /// <summary>
    /// Ensures that the canvas_palette table exists and seeds all 256 colors if empty or missing.
    /// </summary>
    public async Task EnsurePaletteSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "004_create_canvas_palette.sql");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", "004_create_canvas_palette.sql");
        }

        if (File.Exists(scriptPath))
        {
            string rawSql = await File.ReadAllTextAsync(scriptPath);
            var batches = rawSql.Split(new[] { "\nGO\r", "\nGO\n", "\r\nGO\r\n", "\nGO" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var batch in batches)
            {
                var trimmed = batch.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    await db.ExecuteAsync(trimmed);
                }
            }
        }
        else
        {
            const string createTableSql = """
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_palette')
                BEGIN
                    CREATE TABLE canvas_palette (
                        id TINYINT PRIMARY KEY,
                        hex_code VARCHAR(7) NOT NULL,
                        name NVARCHAR(50) NOT NULL,
                        is_active BIT NOT NULL DEFAULT 0,
                        sort_order INT NOT NULL DEFAULT 0,
                        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
                    );
                    CREATE INDEX IX_canvas_palette_is_active ON canvas_palette(is_active, sort_order);
                END
                """;
            await db.ExecuteAsync(createTableSql);
        }
    }

    /// <summary>
    /// Retrieves all palette colors from SQL Server ordered by sort_order.
    /// </summary>
    public async Task<IEnumerable<PaletteColor>> GetPaletteColorsAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT 
                id AS Id,
                hex_code AS HexCode,
                name AS Name,
                is_active AS IsActive,
                sort_order AS SortOrder
            FROM canvas_palette WITH (NOLOCK)
            ORDER BY sort_order ASC, id ASC;
            """;
        return await db.QueryAsync<PaletteColor>(sql);
    }
}

public class PixelPlacementHistoryDto
{
    public byte ColorId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
}