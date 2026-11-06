#!/usr/bin/env bash
# Prepare a separate Steam application installation; never starts the game/server.
set -euo pipefail

PROFILE="${PROFILE:-client-headless}"
APP_ID="${APP_ID:-}"
BRANCH_EXPLICIT=0
[[ -z "${BRANCH:-}" ]] || BRANCH_EXPLICIT=1
BRANCH="${BRANCH-}"
RETIRED_LAUNCH_ARGS="${LAUNCH_ARGS:-}"
INSTALL_DIR="${INSTALL_DIR:-/srv/turbotick/game}"
STEAMCMD="${STEAMCMD:-steamcmd}"
STEAM_USER="${STEAM_USER:-}"
STEAM_PASS="${STEAM_PASS:-}"
STEAM_GUARD="${STEAM_GUARD:-}"
PROXY="${PROXY:-}"
MOD_SOURCE="${MOD_SOURCE:-}"
MODS_CONFIG="${MODS_CONFIG:-}"
MOD_NAME=''
EXECUTABLE=''
VERSION_FILE=''
EXPECTED_VERSION=''
REQUIRED_PATHS=()
LAUNCH_ARGS=()
ARGS_EXPLICIT=0
DRY_RUN=0
CHECK_ONLY=0
SKIP_DOWNLOAD=0
LOGIN_ONLY=0
NO_VALIDATE=0

usage() {
  cat <<'EOF'
Usage: install-server.sh [options]
  --profile NAME       client-headless (default) or official-server.
  --app-id ID          client-headless: 294100; official-server: required.
  --branch NAME        Client default: unstable. Use public for the default branch.
  --dir PATH           Separate installation (default /srv/turbotick/game).
  --steamcmd PATH      SteamCMD executable or wrapper (default steamcmd in PATH).
  --user NAME          Existing SteamCMD account; anonymous only where app permits.
  --proxy URL          Process proxy environment, e.g. http://PROXY_HOST:PROXY_PORT.
  --executable PATH    Relative executable. Required for official-server.
  --required-path PATH Relative required file/directory; repeat for official-server.
  --launch-arg ARG     One literal argv item; repeat. No shell evaluation.
  --no-launch-args     Explicitly select an empty argv for official-server.
  --version-file PATH  Relative version file (client default Version.txt).
  --expected-version V Require this version prefix (client default 1.7).
  --mod-source PATH    Packaged mod root containing About/About.xml (client only).
  --mod-name NAME      Destination folder name (default source directory name).
  --mods-config PATH   Initial ModsConfig.xml (client only; existing one retained).
  --no-validate        Omit SteamCMD validate; local layout checks still run.
  --login-only         Authenticate, verify SteamCMD output, exit without installing.
  --dry-run            Alias --preflight: show redacted plan; no SteamCMD or writes.
  --preflight          Check options/local prerequisites without SteamCMD or writes.
  --check-only         Verify existing layout/version; no SteamCMD or writes.
  --skip-download      Prepare existing verified files without invoking SteamCMD.
  -h, --help           Show help.

Environment: PROFILE, APP_ID, BRANCH, INSTALL_DIR, STEAMCMD, STEAM_USER,
STEAM_PASS, STEAM_GUARD, PROXY, MOD_SOURCE, MODS_CONFIG.
Prefer the established SteamCMD cache with STEAM_PASS/STEAM_GUARD unset.
Otherwise SteamCMD handles password, phone confirmation, email and code prompts.
Environment secrets become native process arguments; dry-run always redacts them.
LAUNCH_ARGS strings are retired: pass each argument with --launch-arg.
EOF
}
log() { printf '[install] %s\n' "$*"; }
die() { printf '[install] error: %s\n' "$*" >&2; exit 1; }
value() { [[ $# -ge 2 && -n "$2" ]] || die "option $1 requires a value"; }
relative_path() {
  [[ -n "$1" && "$1" != /* && "$1" != *\\* && "$1" != *:* && "$1" != *$'\n'* && "$1" != *$'\r'* ]] || die "expected relative path using /: $1"
  case "/$1/" in */../*|*/./*) die "path must stay under installation: $1" ;; esac
}
absolute_path() {
  # realpath -m is not available on every target; parent need not exist yet.
  case "$1" in /*) printf '%s\n' "$1" ;; *) printf '%s/%s\n' "$PWD" "$1" ;; esac
}
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) value "$@"; PROFILE="$2"; shift 2 ;;
    --app-id) value "$@"; APP_ID="$2"; shift 2 ;;
    --branch) value "$@"; BRANCH="$2"; BRANCH_EXPLICIT=1; shift 2 ;;
    --dir) value "$@"; INSTALL_DIR="$2"; shift 2 ;;
    --steamcmd) value "$@"; STEAMCMD="$2"; shift 2 ;;
    --user) value "$@"; STEAM_USER="$2"; shift 2 ;;
    --proxy) value "$@"; PROXY="$2"; shift 2 ;;
    --executable) value "$@"; EXECUTABLE="$2"; shift 2 ;;
    --required-path) value "$@"; REQUIRED_PATHS+=("$2"); shift 2 ;;
    --launch-arg|--launch-args) value "$@"; [[ "$ARGS_EXPLICIT" -ne 2 ]] || die "choose --launch-arg or --no-launch-args"; LAUNCH_ARGS+=("$2"); ARGS_EXPLICIT=1; shift 2 ;;
    --no-launch-args) [[ "$ARGS_EXPLICIT" -eq 0 ]] || die "choose --launch-arg or --no-launch-args"; ARGS_EXPLICIT=2; shift ;;
    --version-file) value "$@"; VERSION_FILE="$2"; shift 2 ;;
    --expected-version) value "$@"; EXPECTED_VERSION="$2"; shift 2 ;;
    --mod-source) value "$@"; MOD_SOURCE="$2"; shift 2 ;;
    --mod-name) value "$@"; MOD_NAME="$2"; shift 2 ;;
    --mods-config) value "$@"; MODS_CONFIG="$2"; shift 2 ;;
    --no-validate) NO_VALIDATE=1; shift ;;
    --login-only) LOGIN_ONLY=1; shift ;;
    --dry-run|--preflight) DRY_RUN=1; shift ;;
    --check-only) CHECK_ONLY=1; shift ;;
    --skip-download) SKIP_DOWNLOAD=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done
[[ -z "$RETIRED_LAUNCH_ARGS" ]] || die "LAUNCH_ARGS environment is retired; use --launch-arg"
[[ "$LOGIN_ONLY" -eq 0 || ( "$CHECK_ONLY" -eq 0 && "$SKIP_DOWNLOAD" -eq 0 ) ]] || die "--login-only conflicts with local-file modes"
[[ "$CHECK_ONLY" -eq 0 || "$SKIP_DOWNLOAD" -eq 0 ]] || die "choose --check-only or --skip-download"
[[ "$ARGS_EXPLICIT" -ne 2 || ${#LAUNCH_ARGS[@]} -eq 0 ]] || die "choose --launch-arg or --no-launch-args"

case "$PROFILE" in
  client-headless)
    APP_ID="${APP_ID:-294100}"
    [[ "$APP_ID" == 294100 ]] || die "a different AppId needs --profile official-server and an explicit layout/argv"
    [[ "$BRANCH_EXPLICIT" -eq 1 ]] || BRANCH=unstable
    REQUIRED_PATHS=(Version.txt Assembly-CSharp.dll RimWorld.dll Waylith.dll UnityStubs.dll Assets.zip Data/Core "${REQUIRED_PATHS[@]}")
    VERSION_FILE="${VERSION_FILE:-Version.txt}"
    EXPECTED_VERSION="${EXPECTED_VERSION:-1.7}"
    [[ "$ARGS_EXPLICIT" -ne 0 ]] || LAUNCH_ARGS=(-headless)
    ;;
  official-server)
    if [[ "$LOGIN_ONLY" -eq 0 ]]; then
      [[ -n "$APP_ID" && -n "$EXECUTABLE" && ${#REQUIRED_PATHS[@]} -gt 0 && "$ARGS_EXPLICIT" -ne 0 ]] || die "official-server needs --app-id, --executable, --required-path and --launch-arg (or --no-launch-args)"
    fi
    [[ -z "$MOD_SOURCE$MODS_CONFIG$MOD_NAME" ]] || die "official-server does not assume a RimWorld Mods/Saved layout; configure that application's paths explicitly in its argv"
    ;;
  *) die "unknown profile: $PROFILE" ;;
esac
[[ "$LOGIN_ONLY" -eq 1 || "$APP_ID" =~ ^[1-9][0-9]*$ ]] || die "AppId must be a positive integer"
[[ -z "$BRANCH" || "$BRANCH" =~ ^[a-zA-Z0-9_][a-zA-Z0-9_.\/-]*$ ]] || die "invalid branch name"
for item in "${REQUIRED_PATHS[@]}"; do relative_path "$item"; done
[[ -z "$EXECUTABLE" ]] || relative_path "$EXECUTABLE"
[[ -z "$VERSION_FILE" ]] || relative_path "$VERSION_FILE"
[[ -z "$EXPECTED_VERSION" || -n "$VERSION_FILE" ]] || die "--expected-version requires --version-file"
[[ "$INSTALL_DIR" != *$'\n'* && "$INSTALL_DIR" != *$'\r'* ]] || die "installation path contains a newline"
INSTALL_DIR="$(absolute_path "$INSTALL_DIR")"
if [[ -n "$MOD_SOURCE" ]]; then
  [[ -f "$MOD_SOURCE/About/About.xml" ]] || die "mod source must contain About/About.xml"
  MOD_SOURCE="$(cd "$MOD_SOURCE" && pwd -P)"
  MOD_NAME="${MOD_NAME:-${MOD_SOURCE##*/}}"
  [[ "$MOD_NAME" =~ ^[a-zA-Z0-9_][a-zA-Z0-9_.\ -]*$ && "$MOD_NAME" != . && "$MOD_NAME" != .. ]] || die "invalid mod directory name"
  case "$INSTALL_DIR/" in "$MOD_SOURCE/"*) die "installation must be outside mod source to avoid recursive copying" ;; esac
fi
[[ -z "$MODS_CONFIG" || -f "$MODS_CONFIG" ]] || die "mods config file not found"
[[ -z "$MODS_CONFIG" ]] || MODS_CONFIG="$(absolute_path "$MODS_CONFIG")"

CMD=()
DISPLAY=()
if [[ "$CHECK_ONLY" -eq 0 && "$SKIP_DOWNLOAD" -eq 0 ]]; then
  [[ "$STEAM_USER" =~ ^[a-zA-Z0-9_@.-]+$ ]] || die "supply a Steam account using --user / STEAM_USER"
  STEAMCMD="$(command -v "$STEAMCMD")" || die "SteamCMD executable not found; pass --steamcmd PATH"
  STEAMCMD="$(absolute_path "$STEAMCMD")"
  [[ -x "$STEAMCMD" && -f "$STEAMCMD" ]] || die "SteamCMD is not executable"
  [[ "$LOGIN_ONLY" -eq 1 ]] || CMD+=(+force_install_dir "$INSTALL_DIR")
  [[ -z "$STEAM_GUARD" ]] || CMD+=(+set_steam_guard_code "$STEAM_GUARD")
  CMD+=(+login "$STEAM_USER")
  [[ -z "$STEAM_PASS" ]] || CMD+=("$STEAM_PASS")
  if [[ "$LOGIN_ONLY" -eq 0 ]]; then
    CMD+=(+app_update "$APP_ID")
    [[ -z "$BRANCH" || "$BRANCH" == public ]] || CMD+=(-beta "$BRANCH")
    [[ "$NO_VALIDATE" -eq 1 ]] || CMD+=(validate)
  fi
  CMD+=(+quit)
  # Build the display independently: secrets never enter printable arguments.
  [[ "$LOGIN_ONLY" -eq 1 ]] || DISPLAY+=(+force_install_dir "$INSTALL_DIR")
  [[ -z "$STEAM_GUARD" ]] || DISPLAY+=(+set_steam_guard_code '<redacted>')
  DISPLAY+=(+login "$STEAM_USER")
  [[ -z "$STEAM_PASS" ]] || DISPLAY+=('<redacted>')
  if [[ "$LOGIN_ONLY" -eq 0 ]]; then
    DISPLAY+=(+app_update "$APP_ID")
    [[ -z "$BRANCH" || "$BRANCH" == public ]] || DISPLAY+=(-beta "$BRANCH")
    [[ "$NO_VALIDATE" -eq 1 ]] || DISPLAY+=(validate)
  fi
  DISPLAY+=(+quit)
fi
if [[ "$DRY_RUN" -eq 1 ]]; then
  log "preflight only: profile=$PROFILE app=${APP_ID:-not-used} branch=${BRANCH:-public} directory=$INSTALL_DIR"
  if [[ ${#DISPLAY[@]} -gt 0 ]]; then printf '[install] command: '; printf '%q ' "$STEAMCMD" "${DISPLAY[@]}"; printf '\n'; fi
  [[ "$LOGIN_ONLY" -eq 1 ]] || { printf '[install] required: '; printf '%q ' "${REQUIRED_PATHS[@]}"; printf '\n'; log "executable: ${EXECUTABLE:-detect client executable after install}"; }
  log 'no authentication, download or filesystem changes performed'
  exit 0
fi

if [[ "$CHECK_ONLY" -eq 0 && "$SKIP_DOWNLOAD" -eq 0 ]]; then
  [[ -z "$PROXY" ]] || { export HTTP_PROXY="$PROXY" HTTPS_PROXY="$PROXY" ALL_PROXY="$PROXY" http_proxy="$PROXY" https_proxy="$PROXY" all_proxy="$PROXY"; log 'proxy environment configured for SteamCMD'; }
  transcript="$(mktemp)"
  trap 'rm -f -- "$transcript"' EXIT
  log 'SteamCMD uses its own cache and native password / phone / email / code interaction'
  # Preserve stdin for native Steam Guard interaction. Do not script prompt responses.
  set +e
  (cd "$(dirname "$STEAMCMD")" && "$STEAMCMD" "${CMD[@]}") 2>&1 | tee "$transcript"
  results=("${PIPESTATUS[@]}")
  set -e
  [[ "${results[0]}" -eq 0 ]] || { log "SteamCMD exited with ${results[0]}"; exit "${results[0]}"; }
  [[ "${results[1]}" -eq 0 ]] || die 'SteamCMD output capture failed'
  if grep -Eiq 'ERROR!|FAILED \(|Login Failure|Invalid Password|Account Logon Denied' "$transcript"; then die 'SteamCMD reported an authentication/update error'; fi
  if [[ "$LOGIN_ONLY" -eq 1 ]]; then
    grep -Eiq 'Logged in OK|Logging in user .*\.\.\.OK|Waiting for user info\.\.\.OK' "$transcript" || die 'SteamCMD exited without a recognized login-success marker; cache status unconfirmed'
    log 'authentication confirmed; SteamCMD controls cache location (reuse this executable, OS account and HOME)'
    exit 0
  fi
  grep -Eiq "Success! App '$APP_ID' (fully installed|already up to date)" "$transcript" || die 'SteamCMD exited without the requested AppId success marker'
fi

log "verifying $PROFILE layout in $INSTALL_DIR"
for item in "${REQUIRED_PATHS[@]}"; do [[ -e "$INSTALL_DIR/$item" ]] || die "missing required path: $item"; done
if [[ "$PROFILE" == client-headless ]]; then
  for item in Version.txt Assembly-CSharp.dll RimWorld.dll Waylith.dll UnityStubs.dll Assets.zip; do
    [[ -f "$INSTALL_DIR/$item" ]] || die "required file is not a regular file: $item"
  done
  [[ -d "$INSTALL_DIR/Data/Core" ]] || die 'Data/Core must be a directory'
fi
if [[ -z "$EXECUTABLE" ]]; then
  for candidate in RimWorld RimWorld.x86_64 RimWorld.x86_64.bin; do
    if [[ -f "$INSTALL_DIR/$candidate" && -x "$INSTALL_DIR/$candidate" ]]; then EXECUTABLE="$candidate"; break; fi
  done
fi
[[ -n "$EXECUTABLE" && -f "$INSTALL_DIR/$EXECUTABLE" && -x "$INSTALL_DIR/$EXECUTABLE" ]] || die 'required executable missing or not executable'
VERSION='unspecified'
if [[ -n "$VERSION_FILE" ]]; then
  [[ -f "$INSTALL_DIR/$VERSION_FILE" ]] || die "version file missing: $VERSION_FILE"
  VERSION=''
  IFS= read -r VERSION < "$INSTALL_DIR/$VERSION_FILE" || true
  VERSION="${VERSION%$'\r'}"
  [[ -n "${VERSION//[[:space:]]/}" ]] || die 'version file is empty'
  if [[ -n "$EXPECTED_VERSION" ]]; then
    [[ "$VERSION" == "$EXPECTED_VERSION" || "$VERSION" == "$EXPECTED_VERSION".* || "$VERSION" == "$EXPECTED_VERSION"\ * ]] || die "version '$VERSION' does not match $EXPECTED_VERSION"
  fi
fi
log "verified: app=$APP_ID branch=${BRANCH:-public} version=$VERSION executable=$EXECUTABLE directory=$INSTALL_DIR"
[[ "$CHECK_ONLY" -eq 0 ]] || { log 'check-only complete; no files changed'; exit 0; }

if [[ -n "$MOD_SOURCE" ]]; then
  destination="$INSTALL_DIR/Mods/$MOD_NAME"
  mkdir -p "$destination"
  cp -a "$MOD_SOURCE/." "$destination/"
  [[ -f "$destination/About/About.xml" ]] || die 'copied mod is missing About/About.xml'
  log "mod installed under Mods/$MOD_NAME"
fi
if [[ "$PROFILE" == client-headless ]]; then
  config_dir="$INSTALL_DIR/Saved/Config"
  mkdir -p "$config_dir"
  if [[ -e "$config_dir/ModsConfig.xml" ]]; then
    log 'preserving existing Saved/Config/ModsConfig.xml (including when --mods-config is supplied)'
  elif [[ -n "$MODS_CONFIG" ]]; then
    cp "$MODS_CONFIG" "$config_dir/ModsConfig.xml"
  else
    # Use only the numeric build token in XML; Version.txt may contain a revision label.
    xml_version="${VERSION%% *}"
    [[ "$xml_version" =~ ^[0-9]+(\.[0-9]+)*$ ]] || die 'version token is not suitable for default ModsConfig.xml; provide --mods-config'
    printf '<?xml version="1.0" encoding="utf-8"?>\n<ModsConfigData>\n  <version>%s</version>\n  <activeMods><li>ludeon.rimworld</li></activeMods>\n  <knownExpansions />\n</ModsConfigData>\n' "$xml_version" > "$config_dir/ModsConfig.xml"
    log 'created initial Core-only ModsConfig.xml; enable Harmony/TurboTick and matching DLC/mods separately'
  fi
fi
start="$INSTALL_DIR/start-server.sh"
{
  printf '#!/usr/bin/env bash\nset -euo pipefail\nroot="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"\ncd -- "$root"\n'
  printf 'executable=%q\n' "$EXECUTABLE"
  printf 'launch_args=('
  if [[ ${#LAUNCH_ARGS[@]} -gt 0 ]]; then printf '%q ' "${LAUNCH_ARGS[@]}"; fi
  printf ')\n'
  if [[ "$PROFILE" == client-headless ]]; then
    printf 'exec "$root/$executable" "${launch_args[@]}" "-savedatafolder=$root/Saved" "$@"\n'
  else
    printf 'exec "$root/$executable" "${launch_args[@]}" "$@"\n'
  fi
} > "$start"
chmod +x "$start"
printf 'profile=%s\nappId=%s\nbranch=%s\nversion=%s\nexecutable=%s\ninstallDirectory=%s\n' "$PROFILE" "$APP_ID" "${BRANCH:-public}" "$VERSION" "$EXECUTABLE" "$INSTALL_DIR" > "$INSTALL_DIR/install-info.txt"
log "prepared launcher: $start"
log 'installation preparation complete; no server process started'
