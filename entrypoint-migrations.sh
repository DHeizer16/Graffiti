#!/bin/bash
set -e

# Detect sqlcmd path
if [ -f "/opt/mssql-tools18/bin/sqlcmd" ]; then
    SQLCMD="/opt/mssql-tools18/bin/sqlcmd"
elif [ -f "/opt/mssql-tools/bin/sqlcmd" ]; then
    SQLCMD="/opt/mssql-tools/bin/sqlcmd"
else
    SQLCMD="sqlcmd"
fi

SQL_HOST="${SQL_HOST:-sqlserver}"
SQL_USER="${SQL_USER:-sa}"
SQL_PASSWORD="${MSSQL_SA_PASSWORD:-YourStrong@Passw0rd}"

echo "=========================================================="
echo " Graffiti SQL Server Database Migration Runner"
echo " Target: $SQL_HOST as $SQL_USER"
echo "=========================================================="

echo "Waiting for SQL Server to become ready..."
for i in {1..60}; do
    if $SQLCMD -S "$SQL_HOST" -U "$SQL_USER" -P "$SQL_PASSWORD" -C -Q "SELECT 1" > /dev/null 2>&1; then
        echo "SQL Server is responsive and accepting connections!"
        break
    fi
    echo "Waiting for SQL Server engine ($i/60)..."
    sleep 2
done

# Step 1: Ensure Graffiti database and base tables exist
echo "Applying 000_init_database.sql..."
$SQLCMD -S "$SQL_HOST" -U "$SQL_USER" -P "$SQL_PASSWORD" -C -d master -i /scripts/000_init_database.sql

# Step 2: Execute migration scripts in strict dependency order against Graffiti database
SCRIPTS=(
    "/scripts/001_create_shadow_bans.sql"
    "/scripts/002_create_canvas_reservations.sql"
    "/scripts/003_create_users_table.sql"
    "/scripts/003_create_canvas_walls.sql"
    "/scripts/004_create_canvas_palette.sql"
    "/scripts/005_create_token_system.sql"
    "/scripts/006_create_canvas_seasons.sql"
)

for script in "${SCRIPTS[@]}"; do
    if [ -f "$script" ]; then
        echo "Applying $(basename "$script")..."
        $SQLCMD -S "$SQL_HOST" -U "$SQL_USER" -P "$SQL_PASSWORD" -C -d Graffiti -i "$script"
    else
        echo "Warning: Script $script not found, skipping."
    fi
done

echo "=========================================================="
echo " All Graffiti migrations applied successfully!"
echo "=========================================================="
