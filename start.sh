#!/usr/bin/env bash
set -eu
(
  flock -x 9
  fingerprint=$({ sha256sum package.json package-lock.json; node --version; npm --version; uname -sm; } | sha256sum | cut -d ' ' -f 1)
  installed=$(cat node_modules/.workshop-arena-dependencies 2>/dev/null || true)
  if [ "$installed" != "$fingerprint" ] || [ ! -x node_modules/.bin/vite ]; then
    seed=/opt/workshop-arena-dependencies
    seeded=$(cat "$seed/node_modules/.workshop-arena-dependencies" 2>/dev/null || true)
    if [ "$seeded" = "$fingerprint" ] && [ ! -e node_modules ]; then
      cp -R "$seed/node_modules" ./node_modules
      chmod -R u+w node_modules
    else
      npm ci --ignore-scripts --no-audit --no-fund
    fi
    printf '%s\n' "$fingerprint" > node_modules/.workshop-arena-dependencies
  fi
) 9>.workshop-arena-install.lock
if [ "${WORKSHOP_ARENA_PREPARE_ONLY:-}" = "1" ]; then exit 0; fi
APP_PORT="${APP_PORT:?APP_PORT is required}"
export BACKEND_PORT=$((APP_PORT + 100))
if [ -f /usr/local/lib/workshop-devguard.sh ]; then
  source /usr/local/lib/workshop-devguard.sh
  devguard_acquire "$APP_PORT" "$BACKEND_PORT"
fi
(cd backend && uv sync --quiet && exec uv run uvicorn habita_api.main:app --host 127.0.0.1 --port "$BACKEND_PORT") &
BACKEND_PID=$!
trap 'kill "$BACKEND_PID" 2>/dev/null || true' EXIT INT TERM
npm run dev -- --host 0.0.0.0 --port "$APP_PORT" --strictPort
