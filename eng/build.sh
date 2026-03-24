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
    echo "==> Install (global tool)"
    dotnet tool uninstall -g buildduty 2>/dev/null || true
    dotnet tool install --global --add-source "$ARTIFACTS/packages" buildduty
fi

echo "==> Done"
