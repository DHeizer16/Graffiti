-- ==============================================================================
-- Migration: 003_create_users_table.sql
-- Description: Creates the users table to support optional user accounts,
--              password hashing, display handles, and profile metadata.
-- ==============================================================================

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
GO
