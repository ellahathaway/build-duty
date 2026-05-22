#!/usr/bin/env bash
set -euo pipefail

PACKAGE_NAME="ellahathaway.buildduty.mcp"
NUGET_SOURCE_NAME="github-ellahathaway"
NUGET_SOURCE_URL="https://nuget.pkg.github.com/ellahathaway/index.json"
MIN_GH_VERSION="2.66.0"

log() {
  printf '[build-duty setup] %s\n' "$1"
}

fail() {
  printf '[build-duty setup] Error: %s\n' "$1" >&2
  exit 1
}

command_exists() {
  command -v "$1" >/dev/null 2>&1
}

normalize_version() {
  printf '%s' "$1" | sed 's/^v//'
}

version_ge() {
  local left right
  left="$(normalize_version "$1")"
  right="$(normalize_version "$2")"
  [ "$(printf '%s\n%s\n' "$right" "$left" | sort -V | tail -n1)" = "$left" ]
}

if command_exists BuildDuty.Mcp; then
  log "BuildDuty.Mcp already available on PATH. Skipping setup."
  exit 0
fi

command_exists gh || fail "GitHub CLI (gh) is required. Install gh 2.66.0+ and run setup again."
command_exists az || fail "Azure CLI (az) is required. Install Azure CLI and run setup again."
command_exists dotnet || fail "dotnet SDK is required. Install .NET SDK and run setup again."

GH_VERSION="$(gh --version | sed -n '1s/^gh version \([0-9][0-9.]*\).*/\1/p')"
[ -n "$GH_VERSION" ] || fail "Unable to determine gh version. Ensure gh is installed correctly."
if ! version_ge "$GH_VERSION" "$MIN_GH_VERSION"; then
  fail "gh CLI ${MIN_GH_VERSION}+ required, you have ${GH_VERSION}. Upgrade gh and retry."
fi

if ! gh auth status >/dev/null 2>&1; then
  fail "GitHub CLI is not authenticated. Run: gh auth login"
fi

if ! az account show >/dev/null 2>&1; then
  fail "Azure CLI is not authenticated. Run: az login"
fi

NUGET_SOURCES="$(dotnet nuget list source)"
if ! printf '%s\n' "$NUGET_SOURCES" | sed -n 's/^[[:space:]]*[0-9]\+\. \([^ ]\+\).*/\1/p' | grep -Fxq "$NUGET_SOURCE_NAME" \
  && ! printf '%s\n' "$NUGET_SOURCES" | grep -Fq "$NUGET_SOURCE_URL"; then
  GH_USER="$(gh api user -q .login 2>/dev/null || true)"
  [ -n "$GH_USER" ] || fail "Unable to determine GitHub username from gh auth."

  GH_TOKEN="$(gh auth token 2>/dev/null || true)"
  [ -n "$GH_TOKEN" ] || fail "Unable to get GitHub auth token. Run: gh auth refresh --scopes read:packages"

  log "Adding NuGet source ${NUGET_SOURCE_NAME}."
  dotnet nuget add source \
    --username "$GH_USER" \
    --password "$GH_TOKEN" \
    --store-password-in-clear-text \
    --name "$NUGET_SOURCE_NAME" \
    "$NUGET_SOURCE_URL" >/dev/null
fi

if dotnet tool list --global | awk '{print $1}' | grep -Fxq "$PACKAGE_NAME"; then
  log "Updating ${PACKAGE_NAME} global tool."
  dotnet tool update --global "$PACKAGE_NAME" >/dev/null
else
  log "Installing ${PACKAGE_NAME} global tool."
  dotnet tool install --global "$PACKAGE_NAME" >/dev/null
fi

if command_exists BuildDuty.Mcp; then
  log "BuildDuty.Mcp is ready."
  exit 0
fi

fail "BuildDuty.Mcp installed but not found on PATH. Add ~/.dotnet/tools to PATH and restart your shell."
