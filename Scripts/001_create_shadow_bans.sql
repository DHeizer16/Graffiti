-- ==============================================================================
-- Migration: 001_create_shadow_bans.sql
-- Description: Adds is_shadow_banned column to pixel_placements and creates the
--              shadow_bans table with indexing for fast identifier lookups.
-- ==============================================================================

-- 1. Add is_shadow_banned column to pixel_placements if missing
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME = 'pixel_placements' AND COLUMN_NAME = 'is_shadow_banned'
)
BEGIN
    ALTER TABLE pixel_placements 
    ADD is_shadow_banned BIT NOT NULL CONSTRAINT DF_pixel_placements_is_shadow_banned DEFAULT 0;
END
GO

-- 2. Create shadow_bans audit table if missing
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
GO
