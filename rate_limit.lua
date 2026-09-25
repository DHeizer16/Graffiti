-- KEYS[1]: User Token Bucket key (e.g., "user:{userId}:tokens")
-- KEYS[2]: User Last Update Timestamp key (e.g., "user:{userId}:last_update")
-- KEYS[3]: User Bonus Balance key (e.g., "user:{userId}:bonus_balance")
-- ARGV[1]: Max regular token capacity (e.g., 16)
-- ARGV[2]: Token refill rate per second (e.g., 0.2 = 1 token per 5 seconds)
-- ARGV[3]: Current Unix timestamp in seconds
-- ARGV[4]: Tokens requested (typically 1 for a single pixel)

local max_capacity = tonumber(ARGV[1]) or 16
local refill_rate = tonumber(ARGV[2]) or 0.1
local now = tonumber(ARGV[3])
local requested = tonumber(ARGV[4]) or 1

-- 1. Fetch current stored values
local current_tokens = tonumber(redis.call('GET', KEYS[1])) or max_capacity
local last_update = tonumber(redis.call('GET', KEYS[2])) or now
local bonus_balance = tonumber(redis.call('GET', KEYS[3])) or 0

-- 2. Calculate elapsed time and regenerated tokens
local elapsed = math.max(0, now - last_update)
local regenerated = elapsed * refill_rate
local new_tokens = math.min(max_capacity, current_tokens + regenerated)

-- 3. Evaluate token deduction priority (Regular Tokens -> Bonus Balance)
if new_tokens >= requested then
    -- Deduct from regular bucket
    new_tokens = new_tokens - requested
    redis.call('SET', KEYS[1], string.format("%.6f", new_tokens))
    redis.call('SET', KEYS[2], tostring(math.floor(now)))
    return {1, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), 0}
elseif bonus_balance >= requested then
    -- Regular tokens empty, deduct from paid bonus balance
    bonus_balance = bonus_balance - requested
    redis.call('SET', KEYS[1], string.format("%.6f", new_tokens))
    redis.call('SET', KEYS[2], tostring(math.floor(now)))
    redis.call('SET', KEYS[3], tostring(math.floor(bonus_balance)))
    return {1, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), 0}
else
    -- Reject: Calculate wait time until next regular token is ready
    local missing = requested - new_tokens
    local wait_seconds = math.ceil(missing / refill_rate)
    return {0, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), wait_seconds}
end