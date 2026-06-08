#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOLUTION="$REPO_ROOT/BuildDuty.slnx"
ARTIFACTS="$REPO_ROOT/artifacts"
PACK=false
INSTALL=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --pack)
            PACK=true
            shift
            ;;
        --install)
            INSTALL=true
            PACK=true
            shift
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

echo "==> Clean"
find "$REPO_ROOT/src" -type d \( -name bin -o -name obj \) -exec rm -rf {} +

echo "==> Restore"
dotnet restore "$SOLUTION"

echo "==> Build"
dotnet build "$SOLUTION" --no-restore -c Release

echo "==> Test"
dotnet test "$SOLUTION" --no-build -c Release

if [ "$PACK" = true ]; then
    echo "==> Pack"
    dotnet pack "$SOLUTION" --no-build -c Release -o "$ARTIFACTS/packages"
    echo "Package(s) written to $ARTIFACTS/packages"
fi

if [ "$INSTALL" = true ]; then
    echo "==> Install (MCP server global tool)"
    dotnet tool uninstall -g ellahathaway.buildduty.mcp 2>/dev/null || true
    LOCAL_CONFIG="$ARTIFACTS/nuget.local.config"
    cleanup_local_config() {
        rm -f "$LOCAL_CONFIG"
    }
    trap cleanup_local_config EXIT
    cat > "$LOCAL_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-artifacts" value="$ARTIFACTS/packages" />
  </packageSources>
</configuration>
EOF
    dotnet tool install --global --configfile "$LOCAL_CONFIG" ellahathaway.buildduty.mcp --prerelease
    trap - EXIT
    cleanup_local_config
fi

echo "==> Done"
