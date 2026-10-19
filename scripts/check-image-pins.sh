#!/usr/bin/env bash
# Fails if a container image is used without being pinned by digest (name:tag@sha256:...). A tag can be moved to different contents; a digest cannot.
# Checks every FROM in every Dockerfile (except "scratch" and a name an earlier stage gave), and every image a workflow names
# (an "image:" setting, or an environment value called ...IMAGE).
set -euo pipefail
cd "$(dirname "$0")/.."

failed=0

while IFS= read -r -d '' file; do
  stages=" "
  while IFS= read -r line; do
    image="$(awk '{ for (i = 2; i <= NF; i++) if ($i !~ /^--/) { print $i; exit } }' <<<"$line")"
    stage="$(awk 'tolower($(NF-1)) == "as" { print $NF }' <<<"$line")"
    if [[ "$image" != "scratch" && "$stages" != *" $image "* && "$image" != *@sha256:* ]]; then
      echo "::error file=$file::'$line' is not pinned by digest (add @sha256:...)"
      failed=1
    fi
    if [[ -n "$stage" ]]; then stages="$stages$stage "; fi
  done < <(grep -iE '^FROM ' "$file" || true)
done < <(find . -name 'Dockerfile*' -not -path '*/node_modules/*' -not -path './.git/*' -print0)

while IFS= read -r match; do
  file="${match%%:*}"
  value="$(sed -E 's/^[^:]+:[0-9]+:\s*[A-Za-z_]*[Ii][Mm][Aa][Gg][Ee]:\s*//; s/\s+#.*$//' <<<"$match" | tr -d '"')"
  if [[ "$value" != *@sha256:* ]]; then
    echo "::error file=$file::image '$value' is not pinned by digest (add @sha256:...)"
    failed=1
  fi
done < <(grep -rnE '^\s*([A-Za-z_]*_IMAGE|image):\s*\S' .github/workflows || true)

if [[ "$failed" -ne 0 ]]; then exit 1; fi
echo "Every container image is pinned by digest."
