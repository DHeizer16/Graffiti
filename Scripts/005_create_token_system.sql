-- ==============================================================================
-- Migration: 005_create_token_system.sql
-- Description: Adds persistent bonus tokens, daily claim tracking, territory
--              reservation token costs, and token transaction ledger.
-- ==============================================================================

-- 1. Add bonus_tokens and last_daily_claim to users table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'users')
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
GO

-- 2. Add token_cost and refunded_tokens to canvas_reservations table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_reservations')
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
GO

-- 3. Create token_transactions ledger
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'token_transactions')
BEGIN
    CREATE TABLE token_transactions (
        id BIGINT IDENTITY(1,1) PRIMARY KEY,
        user_id UNIQUEIDENTIFIER NOT NULL,
        amount INT NOT NULL,
        transaction_type NVARCHAR(50) NOT NULL, -- 'WELCOME_BONUS', 'DAILY_CLAIM', 'ZONE_RESERVE_DEBIT', 'ZONE_RELEASE_REFUND', 'PIXEL_OVERDRIVE_DEBIT', 'PROMO_CODE', 'ADMIN_GRANT'
        description NVARCHAR(255) NULL,
        reference_id NVARCHAR(100) NULL,
        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE INDEX IX_token_transactions_user ON token_transactions(user_id, created_at DESC);
END
GO

-- 4. Create promo_codes and promo_code_redemptions tables
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

    -- Seed initial launch promo codes
    INSERT INTO promo_codes (code, token_amount, max_uses, current_uses, is_active)
    VALUES 
        ('CYBERPUNK2026', 50, 10000, 0, 1),
        ('ANTIGRAVITY', 100, 5000, 0, 1),
        ('VIBEPAINT', 30, 10000, 0, 1);
END
GO

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
GO
