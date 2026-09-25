-- ==============================================================================
-- Migration: 002_create_canvas_reservations.sql
-- Description: Creates the canvas_reservations table to support timed territory
--              reservations with owner keys, coordinates, and expiration indices.
-- ==============================================================================

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
GO
