#!/usr/bin/env bash
# Fails unless <version-file> holds a MAJOR.MINOR.PATCH version strictly greater than the
# same file on <base-ref>. With [changed-path-regex], the bump is only required when a file
# matching the regex was added or modified (incl. renamed/copied) relative to <base-ref>;
# deletions alone don't require a bump.
#
# Usage: check-version-bump.sh <version-file> <base-ref> [changed-path-regex]
# e.g.   check-version-bump.sh version origin/main
#        check-version-bump.sh EstateHubProtos/version origin/main '\.proto$'
set -euo pipefail

version_file="$1"
base_ref="$2"
changed_path_regex="${3:-}"
semver='^[0-9]+\.[0-9]+\.[0-9]+$'

if [ -n "$changed_path_regex" ] &&
   ! git diff --name-only --diff-filter=ACMR "${base_ref}...HEAD" | grep -Eq "$changed_path_regex"; then
  echo "No files matching '${changed_path_regex}' added or modified; ${version_file} bump not required."
  exit 0
fi

if [ ! -f "$version_file" ]; then
  echo "::error file=${version_file}::${version_file} is missing."
  exit 1
fi

current=$(tr -d '[:space:]' < "$version_file")
if ! [[ "$current" =~ $semver ]]; then
  echo "::error file=${version_file}::'${current}' is not a MAJOR.MINOR.PATCH version."
  exit 1
fi

base=$(git show "${base_ref}:${version_file}" 2>/dev/null | tr -d '[:space:]' || true)
if [ -z "$base" ]; then
  echo "${version_file} does not exist on ${base_ref}; accepting ${current} as the first version."
  exit 0
fi

highest=$(printf '%s\n%s\n' "$base" "$current" | sort -V | tail -n 1)
if [ "$current" = "$base" ] || [ "$highest" != "$current" ]; then
  echo "::error file=${version_file}::${version_file} must be bumped above ${base} (found ${current})."
  exit 1
fi

echo "${version_file}: ${base} -> ${current}"
