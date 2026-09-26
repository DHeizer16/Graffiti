-- ==============================================================================
-- Migration: 006_create_canvas_seasons.sql
-- Description: Creates canvas_seasons, canvas_scheduled_resets tables, and adds
--              season_id partitioning to pixel_placements for scheduled wipes.
-- ==============================================================================

USE Graffiti;
GO

-- 1. Create canvas_seasons table to track canvas eras/seasons
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

    -- Seed Season 1: Genesis starting from year 2000 so all prior placements belong to Season 1
    INSERT INTO canvas_seasons (season_number, name, started_at)
    VALUES (1, 'Season 1: Genesis', '2000-01-01 00:00:00 +00:00');
END
GO

-- 2. Create canvas_scheduled_resets table for persistent countdown scheduling
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
GO

-- 3. Add season_id column to pixel_placements table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'pixel_placements')
BEGIN
    IF COL_LENGTH('pixel_placements', 'season_id') IS NULL
    BEGIN
        ALTER TABLE pixel_placements ADD season_id INT NOT NULL DEFAULT 1;
        CREATE NONCLUSTERED INDEX IX_pixel_placements_season 
        ON pixel_placements (season_id, is_shadow_banned, placed_at);
    END
END
GO
