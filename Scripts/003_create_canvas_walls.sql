-- ==============================================================================
-- Migration: 003_create_canvas_walls.sql
-- Description: Creates the canvas_walls table for private / custom walls,
--              and adds wall_id column to pixel_placements and canvas_reservations.
-- ==============================================================================

-- 1. Create canvas_walls table if missing
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_walls')
BEGIN
    CREATE TABLE canvas_walls (
        id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        owner_id UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES users(id),
        name NVARCHAR(100) NOT NULL,
        description NVARCHAR(500) NULL,
        width INT NOT NULL DEFAULT 1000,
        height INT NOT NULL DEFAULT 1000,
        access_type NVARCHAR(20) NOT NULL DEFAULT 'Public', -- Public, Unlisted, Password, Private
        access_key NVARCHAR(100) NULL,
        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        is_active BIT NOT NULL DEFAULT 1
    );

    CREATE INDEX IX_canvas_walls_owner ON canvas_walls(owner_id, is_active);
    CREATE INDEX IX_canvas_walls_created ON canvas_walls(created_at, is_active);
END
GO

-- 2. Add wall_id column to pixel_placements if missing
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME = 'pixel_placements' AND COLUMN_NAME = 'wall_id'
)
BEGIN
    ALTER TABLE pixel_placements 
    ADD wall_id UNIQUEIDENTIFIER NULL;

    CREATE INDEX IX_pixel_placements_wall_id ON pixel_placements(wall_id, is_shadow_banned, placed_at);
END
GO

-- 3. Add wall_id column to canvas_reservations if missing
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_NAME = 'canvas_reservations' AND COLUMN_NAME = 'wall_id'
)
BEGIN
    ALTER TABLE canvas_reservations 
    ADD wall_id UNIQUEIDENTIFIER NULL;

    CREATE INDEX IX_canvas_reservations_wall_id ON canvas_reservations(wall_id, is_active, expires_at);
END
GO
