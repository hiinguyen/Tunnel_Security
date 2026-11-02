#!/bin/sh
# PreToolUse (Edit|Write|MultiEdit): block hand edits to secrets and generated files.
file=$(jq -r '.tool_input.file_path // empty')
[ -z "$file" ] && exit 0

reason=
case "$file" in
  */dotenv\ .env|*/.env|*/.env.*) reason="an environment file that holds secrets" ;;
  */BackendV2/keys/*) reason="the JWT signing key" ;;
  */Migrations/*.Designer.cs|*/Migrations/*ModelSnapshot.cs) reason="a generated migration file, use the add-migration skill" ;;
  */bin/*|*/obj/*) reason="a build output directory" ;;
esac
case "$file" in
  */.env.example) reason= ;;
esac

if [ -n "$reason" ]; then
  echo "Blocked: $file is $reason." >&2
  exit 2
fi
