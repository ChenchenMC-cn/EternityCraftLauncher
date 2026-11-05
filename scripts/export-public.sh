#!/usr/bin/env bash
# ---------------------------------------------------------------------------
#  从本仓库导出「可公开」的源码子集到 ../ECL-public
#
#  为什么不直接推本仓库：
#    MPL 2.0 是文件级 copyleft，但 git 没法只公开一部分历史。所以公开仓库
#    由这个脚本从工作区生成 —— 一个受控的、可重复的导出，而不是手工 copy。
#
#  用法：
#      bash scripts/export-public.sh          # 生成/更新 ../ECL-public
#      bash scripts/export-public.sh --push    # 生成后提交并推送（需先配好 remote）
#
#  排除规则在 scripts/public-exclude.txt。
# ---------------------------------------------------------------------------
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="${PUBLIC_DIR:-$ROOT/../ECL-public}"
EXCLUDE="$ROOT/scripts/public-exclude.txt"

cd "$ROOT"

[ -f "$EXCLUDE" ] || { echo "缺少 $EXCLUDE"; exit 1; }

# --- 1. 收集文件：只用 git 跟踪的，避免把 build/ 或临时文件带出去 -------------
mapfile -t ALL < <(git ls-files)

KEEP=()
for f in "${ALL[@]}"; do
  skip=0
  while IFS= read -r pat; do
    [ -z "$pat" ] && continue
    case "$pat" in \#*) continue ;; esac
    # 目录规则（以 / 结尾）按前缀匹配，其余按 gitignore 风格的精确/通配匹配
    case "$pat" in
      */) [ "${f#"$pat"}" != "$f" ] && skip=1 ;;
      *)  case "$f" in $pat) skip=1 ;; esac ;;
    esac
    [ "$skip" = "1" ] && break
  done < "$EXCLUDE"
  [ "$skip" = "0" ] && KEEP+=("$f")
done

# --- 2. 清空目标（只删它能管理的内容，别动别的） -----------------------------
mkdir -p "$DEST"
find "$DEST" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +

# --- 3. 复制 -----------------------------------------------------------------
for f in "${KEEP[@]}"; do
  mkdir -p "$DEST/$(dirname "$f")"
  cp "$f" "$DEST/$f"
done

echo "导出 $(( ${#KEEP[@]} )) 个文件 -> $DEST"

# --- 4. 泄漏检查：宁可失败，也别把不该公开的推上去 ---------------------------
fail=0
check_absent() {
  local pattern="$1" why="$2"
  if grep -rl --exclude-dir=.git --exclude=export-public.sh --exclude=export-public.sh -- "$pattern" "$DEST" 2>/dev/null | head -3 | grep -q .; then
    echo "[泄漏] $why"
    grep -rl --exclude-dir=.git --exclude=export-public.sh --exclude=export-public.sh -- "$pattern" "$DEST" 2>/dev/null | head -3 | sed 's/^/         /'
    fail=1
  fi
}

# The id is split so this file itself never contains it in one piece -- otherwise the
# checker would publish the very string it is looking for.
LEGACY_ID="00000000402b""5328"
check_absent "$LEGACY_ID" "微软官方 launcher client id 出现在公开树里"
check_absent "ghp_"               "疑似 GitHub token"
check_absent "BEGIN RSA PRIVATE KEY" "私钥"

for f in src/ECL.Launcher/Services/MicrosoftAuthService.cs \
         src/ECL.Launcher/Services/Dpapi.cs \
         src/ECL.Launcher/Models/Account.cs \
         src/ECL.Native/src/http.cpp; do
  [ -e "$DEST/$f" ] && { echo "[泄漏] 私有文件被导出：$f"; fail=1; }
done

[ "$fail" = "1" ] && { echo; echo "导出中止：先修掉上面的问题。"; exit 1; }

# --- 5. 可选：提交并推送 -----------------------------------------------------
if [ "${1:-}" = "--push" ]; then
  cd "$DEST"
  [ -d .git ] || git init -q -b main
  git add -A
  if git rev-parse --verify HEAD >/dev/null 2>&1; then
    git commit -q -m "sync: $(date +%Y-%m-%d) 公开子集"
  else
    git commit -q -m "ECL 公开源码（部分模块闭源）"
  fi
  git push -q && echo "已推送："$(git remote get-url origin)
fi

echo "完成。审阅一遍再推：$DEST"
