#!/bin/sh
# PostToolUse (Edit|Write|MultiEdit): build C# and run quick Python and WireProtocol tests after each edit.
# Errors go to stderr with exit 2 so Claude sees and fixes them.
file=$(jq -r '.tool_input.file_path // empty')
[ -z "$file" ] && exit 0
root=$(cd "$(dirname "$0")/../.." && pwd)
env_sh="$HOME/.cache/tunnel-security-dotnet/env.sh"
[ -f "$env_sh" ] && . "$env_sh"

fail() { echo "$1" >&2; exit 2; }

case "$file" in
  "$root"/Station/*) exit 0 ;; # WinUI 3, builds only on Windows
  *.cs)
    command -v dotnet >/dev/null 2>&1 || exit 0
    dir=$(dirname "$file")
    while [ "$dir" != "/" ] && [ -z "$(ls "$dir"/*.csproj 2>/dev/null)" ]; do dir=$(dirname "$dir"); done
    proj=$(ls "$dir"/*.csproj 2>/dev/null | head -n1)
    [ -z "$proj" ] && exit 0
    out=$(dotnet build "$proj" --nologo -v q -clp:ErrorsOnly 2>&1) || fail "dotnet build $proj failed:
$out"
    case "$file" in
      "$root"/WireProtocol/*)
        out=$(dotnet test "$root/tests/WireProtocol.Tests" --nologo -v q 2>&1) || fail "WireProtocol.Tests failed:
$out" ;;
    esac
    ;;
  *.py)
    if command -v ruff >/dev/null 2>&1; then
      ruff format -q "$file"
      out=$(ruff check -q "$file" 2>&1) || fail "ruff check failed:
$out"
    fi
    case "$file" in
      "$root"/NodeAgent/*)
        out=$(cd "$root/NodeAgent" && python3 -m pytest -q -x 2>&1) || fail "pytest NodeAgent failed:
$out" ;;
    esac
    ;;
esac
exit 0
