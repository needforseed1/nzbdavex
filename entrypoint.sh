#!/bin/sh

wait_either() {
    local pid1=$1
    local pid2=$2

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return $?
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return $?
        fi

        sleep 0.5
    done
}

# Signal handling for graceful shutdown
terminate() {
    echo "Caught termination signal. Shutting down..."
    if [ -n "$BACKEND_PID" ] && kill -0 "$BACKEND_PID" 2>/dev/null; then
        kill "$BACKEND_PID"
    fi
    if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
        kill "$FRONTEND_PID"
    fi
    # Wait for children to exit
    wait
    exit 0
}
trap terminate TERM INT

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Create or reuse group based on PGID
if getent group "$PGID" >/dev/null; then
    EXISTING_GROUP=$(getent group "$PGID" | cut -d: -f1)
    echo "GID $PGID already exists, using group $EXISTING_GROUP"
    GROUP_NAME="$EXISTING_GROUP"
else
    addgroup -g "$PGID" appgroup
    GROUP_NAME=appgroup
fi

# Create or reuse user based on PUID
if getent passwd "$PUID" >/dev/null; then
    EXISTING_USER=$(getent passwd "$PUID" | cut -d: -f1)
    echo "UID $PUID already exists, using user $EXISTING_USER"
    USER_NAME="$EXISTING_USER"
else
    if ! id appuser >/dev/null 2>&1; then
        adduser -D -H -u "$PUID" -G "$GROUP_NAME" appuser
    fi
    USER_NAME=appuser
fi

# Fall back to /app/version.txt when no build-arg was provided so the
# UI footer shows the right version under deploy systems (Coolify, etc.)
# that don't pass NZBDAV_VERSION at docker-build time.
if [ -z "${NZBDAV_VERSION}" ] && [ -f /app/version.txt ]; then
    export NZBDAV_VERSION="$(tr -d '[:space:]' < /app/version.txt)"
fi

# Set and validate environment variables
if [ -z "${CONFIG_PATH}" ]; then
    export CONFIG_PATH="/config"
fi
case "$CONFIG_PATH" in
    /) echo "CONFIG_PATH cannot be the filesystem root directory."; exit 1 ;;
    /*) ;;
    *) echo "CONFIG_PATH must be an absolute path. Got: $CONFIG_PATH"; exit 1 ;;
esac
if ! mkdir -p "$CONFIG_PATH"; then
    echo "Could not create CONFIG_PATH: $CONFIG_PATH"
    exit 1
fi

if [ -z "${BACKEND_URL}" ]; then
    BACKEND_URL="http://localhost:8080"
fi
export BACKEND_URL
NORMALIZED_BACKEND_URL=$(node -e '
    try {
        const url = new URL(process.env.BACKEND_URL.trim());
        const valid = ["http:", "https:"].includes(url.protocol)
            && url.host
            && (url.pathname === "/" || url.pathname === "")
            && !url.search
            && !url.hash
            && !url.username
            && !url.password;
        if (!valid) process.exit(1);
        process.stdout.write(url.origin);
    } catch { process.exit(1); }
')
if [ $? -ne 0 ] || [ -z "$NORMALIZED_BACKEND_URL" ]; then
    echo "BACKEND_URL must be an HTTP(S) origin without credentials, a path, query, or fragment."
    exit 1
fi
export BACKEND_URL="$NORMALIZED_BACKEND_URL"

if [ -z "${FRONTEND_BACKEND_API_KEY}" ]; then
    export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
else
    case "$FRONTEND_BACKEND_API_KEY" in
        *[![:space:]]*) ;;
        *) echo "FRONTEND_BACKEND_API_KEY cannot be blank or whitespace."; exit 1 ;;
    esac
fi

# Recursively update permissions to all $CONFIG_PATH files if needed
chown "$PUID:$PGID" "$CONFIG_PATH"
if [ -f "$CONFIG_PATH/db.sqlite" ]; then
    DB_UID=$(stat -c '%u' "$CONFIG_PATH/db.sqlite")
    DB_GID=$(stat -c '%g' "$CONFIG_PATH/db.sqlite")

    if [ "$DB_UID" -ne "$PUID" ] || [ "$DB_GID" -ne "$PGID" ]; then
        echo "$CONFIG_PATH/db.sqlite ownership mismatch: (uid:$DB_UID gid:$DB_GID) vs expected (uid:$PUID gid:$PGID)"
        echo "Updating ownership of $CONFIG_PATH/* to (uid:$PUID gid:$PGID)"
        chown -R "$PUID:$PGID" "$CONFIG_PATH"
    fi
fi

# Keep frontend login cookies valid across container restarts. Explicit
# SESSION_KEY still takes precedence; otherwise store a generated key beside
# the other persistent application state.
if [ -n "${SESSION_KEY}" ]; then
    case "$SESSION_KEY" in
        *[![:space:]]*) ;;
        *) echo "SESSION_KEY cannot be blank or whitespace."; exit 1 ;;
    esac
else
    SESSION_KEY_FILE="$CONFIG_PATH/session.key"
    if [ -L "$SESSION_KEY_FILE" ]; then
        echo "Refusing to use a symbolic link as the SESSION_KEY file: $SESSION_KEY_FILE"
        exit 1
    fi
    if [ ! -s "$SESSION_KEY_FILE" ]; then
        if ! (umask 077; head -c 64 /dev/urandom | hexdump -ve '1/1 "%.2x"' > "$SESSION_KEY_FILE"); then
            echo "Could not create the persistent SESSION_KEY file: $SESSION_KEY_FILE"
            exit 1
        fi
    fi
    chmod 600 "$SESSION_KEY_FILE"
    chown "$PUID:$PGID" "$SESSION_KEY_FILE"
    SESSION_KEY=$(tr -d '[:space:]' < "$SESSION_KEY_FILE")
    if [ -z "$SESSION_KEY" ]; then
        echo "The persistent SESSION_KEY file is empty: $SESSION_KEY_FILE"
        exit 1
    fi
    export SESSION_KEY
fi

MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-30}
MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
case "$MAX_BACKEND_HEALTH_RETRIES" in
    ''|*[!0-9]*) echo "MAX_BACKEND_HEALTH_RETRIES must be a positive integer."; exit 1 ;;
esac
case "$MAX_BACKEND_HEALTH_RETRY_DELAY" in
    ''|*[!0-9]*) echo "MAX_BACKEND_HEALTH_RETRY_DELAY must be a positive integer."; exit 1 ;;
esac
if [ "$MAX_BACKEND_HEALTH_RETRIES" -lt 1 ] || [ "$MAX_BACKEND_HEALTH_RETRY_DELAY" -lt 1 ]; then
    echo "Backend health retry count and delay must both be at least 1."
    exit 1
fi

# Run backend database migration
cd /app/backend
echo "Running database maintenance."
su-exec "$USER_NAME" ./NzbWebDAV --db-migration
MIGRATION_STATUS=$?
if [ "$MIGRATION_STATUS" -ne 0 ]; then
    echo "Database migration failed. Exiting with error code $MIGRATION_STATUS."
    exit "$MIGRATION_STATUS"
fi
echo "Done with database maintenance."

# Run backend as "$USER_NAME" in background
su-exec "$USER_NAME" ./NzbWebDAV &
BACKEND_PID=$!

# Wait for backend health check
echo "Waiting for backend to start."
i=0
while true; do
    echo "Checking backend health: $BACKEND_URL/health ..."
    if curl -s -o /dev/null -w "%{http_code}" "$BACKEND_URL/health" | grep -q "^200$"; then
        echo "Backend is healthy."
        break
    fi

    i=$((i+1))
    if [ "$i" -ge "$MAX_BACKEND_HEALTH_RETRIES" ]; then
        echo "Backend failed health check after $MAX_BACKEND_HEALTH_RETRIES retries. Exiting."
        kill $BACKEND_PID
        wait $BACKEND_PID
        exit 1
    fi

    sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
done

# Run frontend as "$USER_NAME" in background
cd /app/frontend
su-exec "$USER_NAME" npm run start &
FRONTEND_PID=$!

# Wait for either to exit
wait_either $BACKEND_PID $FRONTEND_PID
EXIT_CODE=$?

# Determine which process exited
if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
    echo "The web-frontend has exited. Shutting down the web-backend..."
else
    echo "The web-backend has exited. Shutting down the web-frontend..."
fi

# Kill the remaining process
kill $REMAINING_PID

# Exit with the code of the process that died first
exit $EXIT_CODE
