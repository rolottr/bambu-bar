#!/bin/zsh
# Installs the pre-push git hook that blocks pushing main-appstore (by ref name or any commit
# reachable only from main-appstore), and sets branch.main-appstore.pushRemote=no_push when
# main-appstore exists locally. Works in regular clones, git worktrees, and custom core.hooksPath.
set -euo pipefail

ROOT=${0:A:h:h}
cd "$ROOT"

HOOKS_DIR=$(git config --get core.hooksPath || true)
if [[ -n "$HOOKS_DIR" ]]; then
  [[ "$HOOKS_DIR" = /* ]] || HOOKS_DIR="$ROOT/$HOOKS_DIR"
  HOOK="$HOOKS_DIR/pre-push"
else
  HOOK=$(git rev-parse --git-path hooks/pre-push)
fi
mkdir -p "${HOOK:h}"

if [[ -f "$HOOK" ]] && ! grep -qF "Refuse to push main-appstore" "$HOOK"; then
  BACKUP="${HOOK}.backup-$(date +%Y%m%d-%H%M%S)"
  cp -p "$HOOK" "$BACKUP"
  echo "==> Backed up existing pre-push hook to $BACKUP"
fi

cat > "$HOOK" <<'EOF'
#!/bin/sh
# Refuse to push main-appstore, or any commit that is only on main-appstore (D-34)
while read local_ref local_sha remote_ref remote_sha; do
  case "$local_ref $remote_ref" in *main-appstore*) echo "pre-push: main-appstore is never pushed" >&2; exit 1;; esac
  [ "$local_sha" = 0000000000000000000000000000000000000000 ] && continue
  if git rev-parse -q --verify refs/heads/main-appstore >/dev/null; then
    only=$(git rev-list "$local_sha" --not main $(git for-each-ref --format='%(refname)' refs/remotes) | while read c; do
      git merge-base --is-ancestor "$c" main-appstore && echo "$c"; done | head -1)
    [ -n "$only" ] && { echo "pre-push: $only exists only on main-appstore" >&2; exit 1; }
  fi
done
exit 0
EOF

chmod +x "$HOOK"
echo "==> Installed pre-push hook at $HOOK"

if git rev-parse -q --verify refs/heads/main-appstore >/dev/null 2>&1; then
  git config --local branch.main-appstore.pushRemote no_push
  echo "==> Configured branch.main-appstore.pushRemote=no_push"
fi
