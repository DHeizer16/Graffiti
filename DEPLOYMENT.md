# Global Graffiti Wall — Production Deployment Runbook

Complete, step-by-step production hosting runbook for taking **Global Graffiti Wall** live on the public internet with automated SSL/TLS (Let's Encrypt), WebSocket streaming, DDoS protection, database backups, and high-concurrency performance tuning.

---

## 1. Architecture Overview

```
                                  [ Public Internet ]
                                          │
                                          ▼
                      ┌───────────────────────────────────────┐
                      │        Cloudflare Edge Network        │
                      │  - Free Global CDN & DDoS Mitigation   │
                      │  - WebSocket Proxy & SSL Termination  │
                      └───────────────────┬───────────────────┘
                                          │  HTTPS (Port 443) / HTTP (Port 80)
                                          ▼
                      ┌───────────────────────────────────────┐
                      │          Host Server (Linux VPS)      │
                      │  - UFW Firewall: Only 22, 80, 443 open │
                      │  - TCP & Socket Kernel Optimizations  │
                      └───────────────────┬───────────────────┘
                                          │
 ┌────────────────────────────────────────┴────────────────────────────────────────┐
 │ Docker Network: 'graffiti-net' (Internal Bridge)                                │
 │                                                                                 │
 │   ┌──────────────────────┐        HTTP / WSS       ┌────────────────────────┐   │
 │   │  Caddy Reverse Proxy │ ──────────────────────> │  ASP.NET Core 9 API    │   │
 │   │  (Auto Let's Encrypt)│                         │  (Kestrel :8080)       │   │
 │   └──────────────────────┘                         └───────────┬────────────┘   │
 │                                                                │                │
 │                         ┌──────────────────────────────────────┴──┐             │
 │                         ▼                                         ▼             │
 │             ┌─────────────────────────┐               ┌─────────────────────┐   │
 │             │  Redis 7 In-Memory      │               │  SQL Server 2022    │   │
 │             │  - 100 MB Canvas State  │               │  - Persistent Audit │   │
 │             │  - SignalR Pub/Sub      │               │  - User Accounts    │   │
 │             │  (Port 6379 - Private!) │               │  (Port 1433-Private)│   │
 │             └─────────────────────────┘               └─────────────────────┘   │
 └─────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Infrastructure Sizing & Cloud VPS Recommendations

Global Graffiti Wall is engineered for extreme efficiency. Because the 100,000,000-pixel canvas is stored as an 8-bit memory-mapped buffer in Redis (100 MB) with batched asynchronous SQL flushes, a modest server can comfortably host thousands of concurrent painters.

### Recommended Providers (Option A: Single-Node VPS)
| Provider | Tier / Plan | Specs | Approx. Cost | Best For |
| :--- | :--- | :--- | :--- | :--- |
| **Hetzner Cloud** *(Top Pick)* | CPX21 / CAX21 | 3–4 vCPU, 4–8 GB RAM, 80 GB NVMe | ~$7–$12/month | Best cost-to-performance ratio in US & EU |
| **DigitalOcean** | Basic Droplet | 2 vCPU, 4 GB RAM, 80 GB SSD | ~$24/month | Simple UX, fast setup |
| **Linode / Akamai** | Dedicated 4GB | 2 vCPU, 4 GB RAM, 80 GB SSD | ~$20/month | High network throughput |
| **AWS EC2** | `t4g.medium` (ARM64) | 2 vCPU, 4 GB RAM, EBS | ~$25/month | AWS ecosystem integration |

> [!TIP]
> **Minimum Server Specs**: 2 vCPUs, 3 GB RAM (SQL Server 2022 requires at least 2 GB RAM). For budget setups, adding a 2 GB Linux swap file is recommended to prevent OOM errors during container startup.

---

## 3. Server Preparation & Hardening

SSH into your freshly provisioned Linux server (Ubuntu 22.04 or 24.04 LTS recommended):

```bash
ssh root@your_server_ip
```

### 3.1 Update OS & Install Docker Engine
```bash
# Update system packages
apt update && apt upgrade -y

# Install prerequisite utilities
apt install -y curl git ufw jq unzip htop ca-certificates gnupg lsb-release

# Install Official Docker & Docker Compose Plugin
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
chmod a+r /etc/apt/keyrings/docker.gpg

echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null

apt update
apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Verify Docker installation
docker --version
docker compose version
```

### 3.2 Configure UFW Firewall (Zero Exposed Database Ports)
Lock down all ports except SSH, HTTP, and HTTPS:

```bash
# Set default policies
ufw default deny incoming
ufw default allow outgoing

# Open required ingress ports
ufw allow 22/tcp    # SSH
ufw allow 80/tcp    # HTTP (Let's Encrypt validation & redirect)
ufw allow 443/tcp   # HTTPS (Production Web & SignalR traffic)
ufw allow 443/udp   # HTTP/3 QUIC (Accelerated delivery via Caddy)

# Enable firewall
ufw --force enable
ufw status verbose
```

> [!IMPORTANT]
> Neither Redis (`6379`) nor SQL Server (`1433`) are exposed via UFW. In `docker-compose.prod.yml`, their ports are kept strictly internal to the Docker bridge network `graffiti-net`.

### 3.3 Linux Kernel Tuning for High Concurrency (5,000+ Painters)
To prevent WebSocket socket exhaustion and handle thousands of simultaneous SignalR streams:

```bash
# Create kernel optimization profile
cat << 'EOF' > /etc/sysctl.d/99-graffiti.conf
# Increase max open socket connections
net.core.somaxconn = 65535
net.ipv4.tcp_max_syn_backlog = 65535

# Fast socket recycling
net.ipv4.tcp_fin_timeout = 15
net.ipv4.tcp_tw_reuse = 1

# Buffer memory scaling
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216

# Increase max open file descriptors
fs.file-max = 2097152
EOF

# Apply kernel settings immediately
sysctl --system

# Increase user file descriptor limits
cat << 'EOF' >> /etc/security/limits.conf
* soft nofile 65535
* hard nofile 65535
root soft nofile 65535
root hard nofile 65535
EOF
```

---

## 4. Domain & Cloudflare Setup

1. **DNS Records**:
   - In your DNS provider (e.g. Cloudflare), create an **A record**:
     - **Name**: `graffiti` (or `@` for root domain)
     - **IPv4 Address**: `YOUR_SERVER_PUBLIC_IP`
     - **Proxy status**: Proxied (Orange Cloud) for DDoS mitigation, or DNS Only during initial certificate issuance.
2. **Cloudflare SSL/TLS Settings**:
   - Set encryption mode to **Full (Strict)**.
   - Enable **Always Use HTTPS**.
3. **WebSockets Toggle**:
   - Navigate to **Network** in Cloudflare dashboard $\rightarrow$ ensure **WebSockets** is toggled **ON**.
4. **Real Client IP Passing**:
   - Cloudflare automatically passes the visitor's real IP address in the `CF-Connecting-IP` and `X-Forwarded-For` HTTP headers.
   - Caddy and ASP.NET Core (`UseForwardedHeaders`) automatically ingest this header, ensuring shadow bans and rate limits apply to actual bad actors instead of Cloudflare proxy IPs.

---

## 5. Clone & Configure Global Graffiti Wall

```bash
# Navigate to deployment directory
cd /opt

# Clone repository
git clone https://github.com/DHeizer16/Graffiti.git graffiti
cd graffiti

# Copy production environment configuration template
cp .env.production.example .env.production
```

### 5.1 Generate Cryptographically Secure Secrets
Run the following commands to generate random keys for your production environment:

```bash
# Generate SQL Server SA Password (alphanumeric + symbol)
openssl rand -base64 24

# Generate 512-bit JWT Signing Secret Key
openssl rand -base64 64
```

### 5.2 Edit `.env.production`
Open `.env.production` in your preferred editor (`nano .env.production` or `vim .env.production`) and set:

```ini
# Domain & Auto-SSL
DOMAIN_NAME=graffiti.yourdomain.com
ACME_EMAIL=admin@yourdomain.com

# Production Database Password
MSSQL_SA_PASSWORD=YourGeneratedSqlPasswordHere!#123

# JWT Secret Key
JWT_SECRET=YourGenerated512BitBase64JwtSecretKeyHere==

# CORS Lockdown (Restricted to your domain)
CORS_ALLOWED_ORIGINS=https://graffiti.yourdomain.com

# High-concurrency Redis Backplane
ENABLE_REDIS_BACKPLANE=true
CANVAS_MAX_CAPACITY=16
CANVAS_REFILL_RATE=0.2
```

---

## 6. One-Command Launch

Start the production stack using Docker Compose:

```bash
# Build images and start all containers in detached mode
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
```

### 6.1 Verify Container Health
Check container status:

```bash
docker compose -f docker-compose.prod.yml ps
```

You should see all 4 services running healthy:
```
NAME                   IMAGE                         STATUS                    PORTS
graffiti-proxy         caddy:2-alpine                Up (healthy)              0.0.0.0:80->80/tcp, 0.0.0.0:443->443/tcp, 0.0.0.0:443->443/udp
graffiti-api           graffiti-api:latest           Up (healthy)              8080/tcp
graffiti-redis         redis:7-alpine                Up (healthy)              6379/tcp
graffiti-sqlserver     mssql/server:2022-latest      Up (healthy)              1433/tcp
graffiti-migrations    mssql/server:2022-latest      Exited (0) (completed)
```

### 6.2 Verify SSL & Health Endpoints
```bash
# Verify API Health Probe through Caddy
curl -i https://graffiti.yourdomain.com/health

# Verify Readiness Probe (SQL Server & Redis connectivity)
curl -i https://graffiti.yourdomain.com/health/ready

# Check Real-Time Telemetry
curl -i https://graffiti.yourdomain.com/api/canvas/telemetry
```

Visit `https://graffiti.yourdomain.com` in your browser. You will see the live canvas, the retro cyberpunk HUD, and a padlock icon with a valid Let's Encrypt certificate!

---

## 7. Operational Management & Runbooks

### 7.1 Promoting the First Administrator
The very first registered user is automatically seeded as an Admin. If you need to manually promote or change roles:

```bash
# Connect to SQL Server container via sqlcmd
docker exec -it graffiti-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d Graffiti \
  -Q "UPDATE users SET role = 'Admin' WHERE username = 'YourUsername';"
```

### 7.2 Zero-Downtime Application Updates
When new features or bug fixes are committed to the repository:

```bash
cd /opt/graffiti

# Pull latest code
git pull origin main

# Rebuild API container and roll restart with zero downtime
docker compose -f docker-compose.prod.yml --env-file .env.production build api
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --no-deps api
```

### 7.3 Automated Daily Database Backups
Set up an automated cron job to backup SQL Server and Redis state every night at 3:00 AM:

```bash
# Create backup directory
mkdir -p /opt/backups/graffiti

# Create backup script
cat << 'EOF' > /opt/graffiti/backup.sh
#!/bin/bash
set -e
TIMESTAMP=$(date +"%Y%m%d_%H%M%S")
BACKUP_DIR="/opt/backups/graffiti"
source /opt/graffiti/.env.production

# 1. Backup SQL Server Database to .bak file
docker exec graffiti-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "BACKUP DATABASE [Graffiti] TO DISK = N'/var/opt/mssql/Graffiti_$TIMESTAMP.bak' WITH NOFORMAT, NOINIT, SKIP, NOREWIND, NOUNLOAD, STATS = 10;"

# Move .bak to host backup storage
docker cp graffiti-sqlserver:/var/opt/mssql/Graffiti_$TIMESTAMP.bak $BACKUP_DIR/
docker exec graffiti-sqlserver rm -f /var/opt/mssql/Graffiti_$TIMESTAMP.bak

# 2. Trigger Redis Background Save and copy dump.rdb
docker exec graffiti-redis redis-cli BGSAVE
sleep 3
docker cp graffiti-redis:/data/dump.rdb $BACKUP_DIR/redis_$TIMESTAMP.rdb

# 3. Prune backups older than 14 days
find $BACKUP_DIR -type f -mtime +14 -delete

echo "[$TIMESTAMP] Backup completed successfully."
EOF

chmod +x /opt/graffiti/backup.sh

# Add to crontab
(crontab -l 2>/dev/null; echo "0 3 * * * /opt/graffiti/backup.sh >> /var/log/graffiti_backup.log 2>&1") | crontab -
```

---

## 8. Horizontal Multi-Instance Scaling (Surge Traffic)

When painting events draw tens of thousands of visitors, you can scale the API container horizontally with a single command thanks to the **SignalR Redis Backplane**:

```bash
# Scale the API service to 3 parallel instances
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --scale api=3 --no-recreate
```

Caddy automatically load-balances incoming HTTP and WebSocket connections across all active API instances, while Redis synchronizes all SignalR `PixelUpdated` broadcasts and token deduction locks across nodes.

---

## 9. Troubleshooting & Diagnostic Commands

| Symptom | Diagnostic Command | Common Cause & Solution |
| :--- | :--- | :--- |
| **502 Bad Gateway from Caddy** | `docker compose -f docker-compose.prod.yml logs api` | API container is still starting or failing database migrations. Check `graffiti-migrations` logs. |
| **WebSocket Connection Fails** | `curl -i -N -H "Connection: Upgrade" -H "Upgrade: websocket" https://graffiti.yourdomain.com/hubs/canvas` | Cloudflare WebSockets toggle is OFF, or firewall is blocking long-lived TCP connections. |
| **SSL Certificate Error** | `docker compose -f docker-compose.prod.yml logs caddy` | Domain DNS does not point to server IP, or port 80 is blocked by firewall preventing ACME HTTP challenge. |
| **SQL Server OOM / Crash** | `dmesg -T \| grep -i oom` | Server has $< 2.5\text{ GB}$ available RAM. Add a swap file: `fallocate -l 2G /swapfile && mkswap /swapfile && swapon /swapfile`. |
| **Rate Limit Triggered for All Users** | Inspect client IP in `api` logs | Proxy is not forwarding `X-Forwarded-For`. Ensure `UseForwardedHeaders` is active and Caddy sets `header_up X-Forwarded-For {remote_host}`. |
