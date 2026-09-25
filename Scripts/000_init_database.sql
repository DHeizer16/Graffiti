-- ==============================================================================
-- Migration: 000_init_database.sql
-- Description: Creates the Graffiti database and the primary pixel_placements
--              table with spatial and historical indexing.
-- ==============================================================================

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'Graffiti')
BEGIN
    CREATE DATABASE Graffiti;
END
GO

USE Graffiti;
GO

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
GO
