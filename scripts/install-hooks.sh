#!/bin/zsh
# Installs the pre-push git hook that blocks pushing main-appstore (by ref name or any commit
# reachable only from main-appstore), and sets branch.main-appstore.pushRemote=no_push when
# main-appstore exists locally. Works in both regular clones and git worktrees.
set -euo pipefail

ROOT=${0:A:h:h}
cd "$ROOT"

HOOK=$(git rev-parse --git-path hooks/pre-push)
mkdir -p "${HOOK:h}"

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
