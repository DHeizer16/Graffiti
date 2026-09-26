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
    /// Can optionally filter by sinceUtc (e.g. current season start).
    /// </summary>
    public async Task<IEnumerable<(int X, int Y, byte ColorId)>> GetAllPixelPlacementsAsync(Guid? wallId = null, DateTimeOffset? sinceUtc = null)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        string sinceFilter = sinceUtc.HasValue ? "AND placed_at >= @SinceUtc" : "";

        string sql = wallId == null
            ? $"""
              SELECT x AS X, y AS Y, color_id AS ColorId
              FROM pixel_placements WITH (NOLOCK)
              WHERE is_shadow_banned = 0 AND wall_id IS NULL {sinceFilter}
              ORDER BY placed_at ASC;
              """
            : $"""
              SELECT x AS X, y AS Y, color_id AS ColorId
              FROM pixel_placements WITH (NOLOCK)
              WHERE is_shadow_banned = 0 AND wall_id = @WallId {sinceFilter}
              ORDER BY placed_at ASC;
              """;

        return await db.QueryAsync<(int X, int Y, byte ColorId)>(sql, new { WallId = wallId, SinceUtc = sinceUtc });
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
    /// Ensures that the primary pixel_placements table and its indexes exist.
    /// </summary>
    public async Task EnsureBaseSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'pixel_placements')
            BEGIN
                CREATE TABLE pixel_placements (
                    placement_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                    placed_at DATETIMEOFFSET NOT NULL,
                    x INT NOT NULL,
                    y INT NOT NULL,
                    color_id TINYINT NOT NULL,
                    user_id UNIQUEIDENTIFIER NOT NULL,
                    ip_address VARCHAR(45) NULL,
                    is_shadow_banned BIT NOT NULL DEFAULT 0,
                    wall_id UNIQUEIDENTIFIER NULL
                );

                CREATE NONCLUSTERED INDEX IX_pixel_placements_time_spatial 
                ON pixel_placements (placed_at ASC, x ASC, y ASC) 
                INCLUDE (color_id, user_id, is_shadow_banned, wall_id);

                CREATE NONCLUSTERED INDEX IX_pixel_placements_user 
                ON pixel_placements (user_id, placed_at);

                CREATE NONCLUSTERED INDEX IX_pixel_placements_wall_id 
                ON pixel_placements (wall_id, is_shadow_banned, placed_at);
            END
            """;
        await db.ExecuteAsync(sql);
    }

    /// <summary>
    /// Ensures that the shadow_bans table and is_shadow_banned column exist.
    /// </summary>
    public async Task EnsureShadowBanSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_NAME = 'pixel_placements' AND COLUMN_NAME = 'is_shadow_banned'
            )
            BEGIN
                ALTER TABLE pixel_placements 
                ADD is_shadow_banned BIT NOT NULL CONSTRAINT DF_pixel_placements_is_shadow_banned DEFAULT 0;
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'shadow_bans')
            BEGIN
                CREATE TABLE shadow_bans (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    identifier NVARCHAR(100) NOT NULL,
                    ban_type NVARCHAR(20) NOT NULL,
                    reason NVARCHAR(255) NULL,
                    banned_by NVARCHAR(100) NULL,
                    banned_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    is_active BIT NOT NULL DEFAULT 1
                );

                CREATE INDEX IX_shadow_bans_identifier ON shadow_bans(identifier, is_active);
            END
            """;
        await db.ExecuteAsync(sql);
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
                    is_active BIT NOT NULL DEFAULT 1,
                    token_cost INT NOT NULL DEFAULT 0,
                    refunded_tokens INT NOT NULL DEFAULT 0
                );
                CREATE INDEX IX_canvas_reservations_active ON canvas_reservations(is_active, expires_at);
                CREATE INDEX IX_canvas_reservations_owner ON canvas_reservations(owner_id, is_active);
            END
            ELSE
            BEGIN
                IF COL_LENGTH('canvas_reservations', 'token_cost') IS NULL
                BEGIN
                    ALTER TABLE canvas_reservations ADD token_cost INT NOT NULL DEFAULT 0;
                END
                IF COL_LENGTH('canvas_reservations', 'refunded_tokens') IS NULL
                BEGIN
                    ALTER TABLE canvas_reservations ADD refunded_tokens INT NOT NULL DEFAULT 0;
                END
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
                token_cost AS TokenCost,
                refunded_tokens AS RefundedTokens,
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
                (reservation_id, owner_id, owner_name, secret_key, x1, y1, x2, y2, label, token_cost, refunded_tokens, created_at, expires_at, is_active)
            VALUES 
                (@ReservationId, @OwnerId, @OwnerName, @SecretKey, @X1, @Y1, @X2, @Y2, @Label, @TokenCost, @RefundedTokens, @CreatedAt, @ExpiresAt, @IsActive);
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
    /// Deactivates a reservation and records refunded tokens.
    /// </summary>
    public async Task DeactivateReservationWithRefundAsync(Guid reservationId, int refundedTokens)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            UPDATE canvas_reservations
            SET is_active = 0, refunded_tokens = @RefundedTokens
            WHERE reservation_id = @ReservationId;
            """;
        await db.ExecuteAsync(sql, new { ReservationId = reservationId, RefundedTokens = refundedTokens });
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
                    role NVARCHAR(20) NOT NULL DEFAULT 'User',
                    bonus_tokens INT NOT NULL DEFAULT 0,
                    last_daily_claim DATETIMEOFFSET NULL
                );

                CREATE UNIQUE INDEX IX_users_normalized_username ON users(normalized_username);
                CREATE INDEX IX_users_created_at ON users(created_at);
            END
            ELSE
            BEGIN
                IF COL_LENGTH('users', 'bonus_tokens') IS NULL
                BEGIN
                    ALTER TABLE users ADD bonus_tokens INT NOT NULL DEFAULT 0;
                END
                IF COL_LENGTH('users', 'last_daily_claim') IS NULL
                BEGIN
                    ALTER TABLE users ADD last_daily_claim DATETIMEOFFSET NULL;
                END
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
                (id, username, normalized_username, display_name, password_hash, password_salt, created_at, last_login_at, is_active, role, bonus_tokens, last_daily_claim)
            VALUES 
                (@Id, @Username, @NormalizedUsername, @DisplayName, @PasswordHash, @PasswordSalt, @CreatedAt, @LastLoginAt, @IsActive, @Role, @BonusTokens, @LastDailyClaim);
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
                role AS Role,
                bonus_tokens AS BonusTokens,
                last_daily_claim AS LastDailyClaim
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
                role AS Role,
                bonus_tokens AS BonusTokens,
                last_daily_claim AS LastDailyClaim
            FROM users WITH (NOLOCK)
            WHERE id = @Id;
            """;
        return await db.QuerySingleOrDefaultAsync<User>(sql, new { Id = id });
    }

    // =========================================================================
    // Unified Token Economy & Ledger
    // =========================================================================

    /// <summary>
    /// Ensures that token_transactions, promo_codes, and promo_code_redemptions tables exist.
    /// </summary>
    public async Task EnsureTokenSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'token_transactions')
            BEGIN
                CREATE TABLE token_transactions (
                    id BIGINT IDENTITY(1,1) PRIMARY KEY,
                    user_id UNIQUEIDENTIFIER NOT NULL,
                    amount INT NOT NULL,
                    transaction_type NVARCHAR(50) NOT NULL,
                    description NVARCHAR(255) NULL,
                    reference_id NVARCHAR(100) NULL,
                    created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
                );
                CREATE INDEX IX_token_transactions_user ON token_transactions(user_id, created_at DESC);
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'promo_codes')
            BEGIN
                CREATE TABLE promo_codes (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    code NVARCHAR(50) NOT NULL,
                    token_amount INT NOT NULL,
                    max_uses INT NOT NULL DEFAULT 1000,
                    current_uses INT NOT NULL DEFAULT 0,
                    expires_at DATETIMEOFFSET NULL,
                    is_active BIT NOT NULL DEFAULT 1,
                    created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
                );
                CREATE UNIQUE INDEX IX_promo_codes_code ON promo_codes(code);

                INSERT INTO promo_codes (code, token_amount, max_uses, current_uses, is_active)
                VALUES 
                    ('CYBERPUNK2026', 50, 10000, 0, 1),
                    ('ANTIGRAVITY', 100, 5000, 0, 1),
                    ('VIBEPAINT', 30, 10000, 0, 1);
            END

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'promo_code_redemptions')
            BEGIN
                CREATE TABLE promo_code_redemptions (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    promo_code_id INT NOT NULL,
                    user_id UNIQUEIDENTIFIER NOT NULL,
                    redeemed_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    CONSTRAINT FK_promo_redemptions_code FOREIGN KEY (promo_code_id) REFERENCES promo_codes(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IX_promo_redemptions_user ON promo_code_redemptions(promo_code_id, user_id);
            END
            """;
        await db.ExecuteAsync(sql);
    }

    /// <summary>
    /// Gets current SQL bonus token balance for a user.
    /// </summary>
    public async Task<int> GetBonusTokensAsync(Guid userId)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "SELECT bonus_tokens FROM users WITH (NOLOCK) WHERE id = @UserId;";
        return await db.ExecuteScalarAsync<int?>(sql, new { UserId = userId }) ?? 0;
    }

    /// <summary>
    /// Adds bonus tokens to a user and logs transaction in ledger.
    /// </summary>
    public async Task AddBonusTokensAsync(Guid userId, int amount, string transactionType, string? description = null, string? referenceId = null)
    {
        if (amount <= 0) return;
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            UPDATE users 
            SET bonus_tokens = bonus_tokens + @Amount 
            WHERE id = @UserId;

            INSERT INTO token_transactions (user_id, amount, transaction_type, description, reference_id)
            VALUES (@UserId, @Amount, @TransactionType, @Description, @ReferenceId);
            """;
        await db.ExecuteAsync(sql, new { UserId = userId, Amount = amount, TransactionType = transactionType, Description = description, ReferenceId = referenceId });
    }

    /// <summary>
    /// Deducts bonus tokens from a user and logs transaction in ledger.
    /// </summary>
    public async Task DeductBonusTokensAsync(Guid userId, int amount, string transactionType = "CONSUMED", string? description = null, string? referenceId = null)
    {
        if (amount <= 0) return;
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            UPDATE users 
            SET bonus_tokens = CASE WHEN bonus_tokens >= @Amount THEN bonus_tokens - @Amount ELSE 0 END 
            WHERE id = @UserId;

            INSERT INTO token_transactions (user_id, amount, transaction_type, description, reference_id)
            VALUES (@UserId, -@Amount, @TransactionType, @Description, @ReferenceId);
            """;
        await db.ExecuteAsync(sql, new { UserId = userId, Amount = amount, TransactionType = transactionType, Description = description, ReferenceId = referenceId });
    }

    /// <summary>
    /// Claims the daily supply drop reward if 24 hours have elapsed since last claim.
    /// </summary>
    public async Task<(bool Success, string Message, int ClaimedTokens, int NewBalance, DateTimeOffset NextClaimAt)> ClaimDailyRewardAsync(Guid userId, int rewardAmount = 30)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        var user = await GetUserByIdAsync(userId);
        if (user == null)
            return (false, "User not found.", 0, 0, DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;
        if (user.LastDailyClaim.HasValue)
        {
            var elapsed = now - user.LastDailyClaim.Value;
            if (elapsed < TimeSpan.FromHours(24))
            {
                var nextClaim = user.LastDailyClaim.Value.AddHours(24);
                var remaining = nextClaim - now;
                return (false, $"Daily supply drop already claimed! Next drop in {(int)remaining.TotalHours}h {remaining.Minutes}m.", 0, user.BonusTokens, nextClaim);
            }
        }

        const string sql = """
            UPDATE users 
            SET bonus_tokens = bonus_tokens + @RewardAmount,
                last_daily_claim = @Now
            WHERE id = @UserId;

            INSERT INTO token_transactions (user_id, amount, transaction_type, description)
            VALUES (@UserId, @RewardAmount, 'DAILY_CLAIM', 'Daily Painter Supply Drop');
            """;

        await db.ExecuteAsync(sql, new { UserId = userId, RewardAmount = rewardAmount, Now = now });
        int newBalance = user.BonusTokens + rewardAmount;
        return (true, $"Claimed +{rewardAmount} Bonus Tokens! ⚡", rewardAmount, newBalance, now.AddHours(24));
    }

    private sealed class PromoCodeRecord
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public int TokenAmount { get; set; }
        public int MaxUses { get; set; }
        public int CurrentUses { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Redeems an event or community promo code.
    /// </summary>
    public async Task<(bool Success, string Message, int GrantedTokens, int NewBalance)> RedeemPromoCodeAsync(Guid userId, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (false, "Promo code cannot be empty.", 0, 0);

        string cleanCode = code.Trim().ToUpperInvariant();
        using IDbConnection db = new SqlConnection(_connectionString);

        const string lookupSql = """
            SELECT id AS Id, code AS Code, token_amount AS TokenAmount, max_uses AS MaxUses, current_uses AS CurrentUses, expires_at AS ExpiresAt, is_active AS IsActive
            FROM promo_codes WITH (UPDLOCK)
            WHERE code = @Code;
            """;
        var promo = await db.QuerySingleOrDefaultAsync<PromoCodeRecord>(lookupSql, new { Code = cleanCode });
        if (promo == null || !promo.IsActive)
            return (false, "Invalid or inactive promo code.", 0, 0);

        if (promo.ExpiresAt != null && promo.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            return (false, "This promo code has expired.", 0, 0);

        if (promo.CurrentUses >= promo.MaxUses)
            return (false, "This promo code has reached its maximum redemptions.", 0, 0);

        const string checkRedeemedSql = """
            SELECT COUNT(*) FROM promo_code_redemptions WHERE promo_code_id = @PromoId AND user_id = @UserId;
            """;
        int alreadyRedeemed = await db.ExecuteScalarAsync<int>(checkRedeemedSql, new { PromoId = promo.Id, UserId = userId });
        if (alreadyRedeemed > 0)
            return (false, "You have already redeemed this promo code.", 0, 0);

        int amount = promo.TokenAmount;
        const string redeemSql = """
            INSERT INTO promo_code_redemptions (promo_code_id, user_id) VALUES (@PromoId, @UserId);
            UPDATE promo_codes SET current_uses = current_uses + 1 WHERE id = @PromoId;
            UPDATE users SET bonus_tokens = bonus_tokens + @Amount WHERE id = @UserId;
            INSERT INTO token_transactions (user_id, amount, transaction_type, description, reference_id)
            VALUES (@UserId, @Amount, 'PROMO_CODE', 'Redeemed code: ' + @Code, @Code);
            """;
        await db.ExecuteAsync(redeemSql, new { PromoId = promo.Id, UserId = userId, Amount = amount, Code = cleanCode });

        int updatedBalance = await GetBonusTokensAsync(userId);
        return (true, $"Promo code successfully redeemed! Granted +{amount} Bonus Tokens! ⚡", amount, updatedBalance);
    }

    /// <summary>
    /// Fetches recent token ledger transactions for a user.
    /// </summary>
    public async Task<IEnumerable<TokenTransactionDto>> GetTokenTransactionsAsync(Guid userId, int limit = 20)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT TOP (@Limit)
                id AS Id,
                amount AS Amount,
                transaction_type AS TransactionType,
                description AS Description,
                reference_id AS ReferenceId,
                created_at AS CreatedAt
            FROM token_transactions WITH (NOLOCK)
            WHERE user_id = @UserId
            ORDER BY created_at DESC;
            """;
        return await db.QueryAsync<TokenTransactionDto>(sql, new { UserId = userId, Limit = limit });
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

    // =========================================================================
    // Canvas Seasons & Scheduled Resets
    // =========================================================================

    /// <summary>
    /// Ensures that canvas_seasons, canvas_scheduled_resets tables exist, and seeds Season 1 if needed.
    /// </summary>
    public async Task EnsureSeasonSchemaAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "006_create_canvas_seasons.sql");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", "006_create_canvas_seasons.sql");
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
            const string createTablesSql = """
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_seasons')
                BEGIN
                    CREATE TABLE canvas_seasons (
                        season_id INT IDENTITY(1,1) PRIMARY KEY,
                        season_number INT NOT NULL UNIQUE,
                        name NVARCHAR(100) NOT NULL,
                        started_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                        ended_at DATETIMEOFFSET NULL,
                        reset_by NVARCHAR(100) NULL,
                        reset_reason NVARCHAR(255) NULL,
                        total_pixels_placed BIGINT NOT NULL DEFAULT 0,
                        archive_export_url NVARCHAR(500) NULL
                    );
                    CREATE INDEX IX_canvas_seasons_number ON canvas_seasons(season_number);
                    CREATE INDEX IX_canvas_seasons_active ON canvas_seasons(ended_at);
                    INSERT INTO canvas_seasons (season_number, name, started_at)
                    VALUES (1, 'Season 1: Genesis', '2000-01-01 00:00:00 +00:00');
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_scheduled_resets')
                BEGIN
                    CREATE TABLE canvas_scheduled_resets (
                        id INT IDENTITY(1,1) PRIMARY KEY,
                        scheduled_reset_utc DATETIMEOFFSET NOT NULL,
                        scheduled_by NVARCHAR(100) NOT NULL,
                        announcement_message NVARCHAR(500) NULL,
                        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                        is_cancelled BIT NOT NULL DEFAULT 0,
                        cancelled_at DATETIMEOFFSET NULL,
                        cancelled_by NVARCHAR(100) NULL,
                        is_executed BIT NOT NULL DEFAULT 0,
                        executed_at DATETIMEOFFSET NULL
                    );
                    CREATE INDEX IX_canvas_scheduled_resets_status 
                    ON canvas_scheduled_resets (is_cancelled, is_executed, scheduled_reset_utc);
                END

                IF EXISTS (SELECT * FROM sys.tables WHERE name = 'pixel_placements')
                BEGIN
                    IF COL_LENGTH('pixel_placements', 'season_id') IS NULL
                    BEGIN
                        ALTER TABLE pixel_placements ADD season_id INT NOT NULL DEFAULT 1;
                        CREATE NONCLUSTERED INDEX IX_pixel_placements_season 
                        ON pixel_placements (season_id, is_shadow_banned, placed_at);
                    END
                END
                """;
            await db.ExecuteAsync(createTablesSql);
        }
    }

    /// <summary>
    /// Retrieves the current active canvas season (ended_at IS NULL).
    /// </summary>
    public async Task<CanvasSeason> GetCurrentSeasonAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT TOP 1
                season_id AS SeasonId,
                season_number AS SeasonNumber,
                name AS Name,
                started_at AS StartedAt,
                ended_at AS EndedAt,
                reset_by AS ResetBy,
                reset_reason AS ResetReason,
                total_pixels_placed AS TotalPixelsPlaced,
                archive_export_url AS ArchiveExportUrl
            FROM canvas_seasons WITH (NOLOCK)
            WHERE ended_at IS NULL
            ORDER BY season_number DESC;
            """;
        var season = await db.QuerySingleOrDefaultAsync<CanvasSeason>(sql);
        if (season == null)
        {
            // Fallback: seed or return default Season 1
            season = new CanvasSeason
            {
                SeasonNumber = 1,
                Name = "Season 1: Genesis",
                StartedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
            };
        }
        return season;
    }

    /// <summary>
    /// Closes the current season and starts the next season atomically.
    /// </summary>
    public async Task<CanvasSeason> CreateNextSeasonAsync(string resetBy, string reason)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        var now = DateTimeOffset.UtcNow;

        const string sql = """
            DECLARE @CurrentSeasonNumber INT;
            SELECT TOP 1 @CurrentSeasonNumber = season_number
            FROM canvas_seasons WITH (UPDLOCK, HOLDLOCK)
            WHERE ended_at IS NULL
            ORDER BY season_number DESC;

            IF @CurrentSeasonNumber IS NULL
                SET @CurrentSeasonNumber = 1;

            -- Close current season
            UPDATE canvas_seasons
            SET ended_at = @Now,
                reset_by = @ResetBy,
                reset_reason = @Reason,
                total_pixels_placed = (
                    SELECT COUNT(*) 
                    FROM pixel_placements WITH (NOLOCK) 
                    WHERE is_shadow_banned = 0 AND wall_id IS NULL AND placed_at >= started_at AND placed_at <= @Now
                )
            WHERE ended_at IS NULL;

            -- Insert next season
            DECLARE @NextSeasonNumber INT = @CurrentSeasonNumber + 1;
            DECLARE @NextSeasonName NVARCHAR(100) = 'Season ' + CAST(@NextSeasonNumber AS NVARCHAR(10));

            INSERT INTO canvas_seasons (season_number, name, started_at)
            OUTPUT 
                INSERTED.season_id AS SeasonId,
                INSERTED.season_number AS SeasonNumber,
                INSERTED.name AS Name,
                INSERTED.started_at AS StartedAt,
                INSERTED.ended_at AS EndedAt,
                INSERTED.reset_by AS ResetBy,
                INSERTED.reset_reason AS ResetReason,
                INSERTED.total_pixels_placed AS TotalPixelsPlaced,
                INSERTED.archive_export_url AS ArchiveExportUrl
            VALUES (@NextSeasonNumber, @NextSeasonName, @Now);
            """;

        return await db.QuerySingleAsync<CanvasSeason>(sql, new { Now = now, ResetBy = resetBy, Reason = reason });
    }

    /// <summary>
    /// Schedules a future canvas reset, cancelling any existing unexecuted/uncancelled resets.
    /// </summary>
    public async Task<CanvasScheduledReset> SaveScheduledResetAsync(DateTimeOffset scheduledUtc, string scheduledBy, string? message)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        var now = DateTimeOffset.UtcNow;

        const string sql = """
            -- Cancel any existing pending resets
            UPDATE canvas_scheduled_resets
            SET is_cancelled = 1, cancelled_at = @Now, cancelled_by = @ScheduledBy
            WHERE is_cancelled = 0 AND is_executed = 0;

            -- Insert new scheduled reset
            INSERT INTO canvas_scheduled_resets (scheduled_reset_utc, scheduled_by, announcement_message, created_at, is_cancelled, is_executed)
            OUTPUT
                INSERTED.id AS Id,
                INSERTED.scheduled_reset_utc AS ScheduledResetUtc,
                INSERTED.scheduled_by AS ScheduledBy,
                INSERTED.announcement_message AS AnnouncementMessage,
                INSERTED.created_at AS CreatedAt,
                INSERTED.is_cancelled AS IsCancelled,
                INSERTED.is_executed AS IsExecuted
            VALUES (@ScheduledUtc, @ScheduledBy, @Message, @Now, 0, 0);
            """;

        return await db.QuerySingleAsync<CanvasScheduledReset>(sql, new { ScheduledUtc = scheduledUtc, ScheduledBy = scheduledBy, Message = message, Now = now });
    }

    /// <summary>
    /// Cancels active scheduled reset.
    /// </summary>
    public async Task<bool> CancelScheduledResetAsync(string cancelledBy)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        var now = DateTimeOffset.UtcNow;

        const string sql = """
            UPDATE canvas_scheduled_resets
            SET is_cancelled = 1, cancelled_at = @Now, cancelled_by = @CancelledBy
            WHERE is_cancelled = 0 AND is_executed = 0;
            """;
        int rows = await db.ExecuteAsync(sql, new { Now = now, CancelledBy = cancelledBy });
        return rows > 0;
    }

    /// <summary>
    /// Retrieves active pending scheduled reset if one exists.
    /// </summary>
    public async Task<CanvasScheduledReset?> GetActiveScheduledResetAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = """
            SELECT TOP 1
                id AS Id,
                scheduled_reset_utc AS ScheduledResetUtc,
                scheduled_by AS ScheduledBy,
                announcement_message AS AnnouncementMessage,
                created_at AS CreatedAt,
                is_cancelled AS IsCancelled,
                cancelled_at AS CancelledAt,
                cancelled_by AS CancelledBy,
                is_executed AS IsExecuted,
                executed_at AS ExecutedAt
            FROM canvas_scheduled_resets WITH (NOLOCK)
            WHERE is_cancelled = 0 AND is_executed = 0
            ORDER BY scheduled_reset_utc ASC;
            """;
        return await db.QuerySingleOrDefaultAsync<CanvasScheduledReset>(sql);
    }

    /// <summary>
    /// Marks a scheduled reset as executed.
    /// </summary>
    public async Task MarkScheduledResetExecutedAsync(int id)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "UPDATE canvas_scheduled_resets SET is_executed = 1, executed_at = SYSDATETIMEOFFSET() WHERE id = @Id;";
        await db.ExecuteAsync(sql, new { Id = id });
    }

    /// <summary>
    /// Deactivates all active territory reservations on the primary global wall (wall_id IS NULL).
    /// </summary>
    public async Task<int> DeactivateAllGlobalReservationsAsync()
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        const string sql = "UPDATE canvas_reservations SET is_active = 0 WHERE wall_id IS NULL AND is_active = 1;";
        return await db.ExecuteAsync(sql);
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