#!/usr/bin/env bash
# Regenerates one grammar for the oracle harness.
#
# Usage: regen-grammar.sh <Name> <repo-url> <commit> [<subdir>]
#
#   <subdir>: the directory of the grammar inside the repo, for repos with more than
#   one grammar (typescript and tsx of tree-sitter-typescript). Default: the root.
#
#   1. clones the grammar repo at <commit> (shallow)
#   2. downloads the tree-sitter CLI v0.27.2 release asset
#      (tree-sitter-cli-<os>-<arch>.zip) into .cache/cli/ (not committed)
#   3. runs `tree-sitter generate`
#   4. verifies #define LANGUAGE_VERSION 15 in src/parser.c (ABI 15)
#   5. copies grammar.js, src/parser.c, src/scanner.c with the other src/*.c and
#      src/*.h it includes (if any), src/tree_sitter/*.h, src/node-types.json,
#      queries/, test/corpus/ (of <subdir>, else of the root), LICENSE into
#      grammars/<Name>/upstream/ unchanged; with <subdir>, the headers of the
#      repo's common/ directory go to grammars/<Name>/common/, where the scanner's
#      #include "../../common/..." finds them
#   6. appends or updates the grammar row in UPSTREAM.md (repo, commit, license)
set -euo pipefail

detect_license() {
  local license_file="$1"
  local text
  if [ ! -f "$license_file" ]; then
    echo "Unknown"
    return
  fi
  text="$(tr '[:upper:]' '[:lower:]' < "$license_file")"
  # The MIT text sometimes comes without its title (tree-sitter-yaml).
  if grep -q "mit license" <<< "$text" ||
     grep -q "permission is hereby granted, free of charge" <<< "$text"; then
    echo "MIT"
  elif grep -q "apache license" <<< "$text"; then
    echo "Apache-2.0"
  elif grep -q "isc license" <<< "$text"; then
    echo "ISC"
  else
    echo "Unknown"
  fi
}

update_upstream_md() {
  # Replaces the row of this grammar (matched by name) or inserts a new
  # row right after the last table row. Other lines are kept as they are.
  local name="$1" repo="$2" commit="$3" license="$4"
  local row="| $name | $repo | - | $commit | $license |"
  local tmp
  tmp="$(mktemp)"
  awk -v row="$row" -v key="| $name |" '
    { lines[NR] = $0 }
    index($0, "|") == 1 { last = NR; if (index($0, key) == 1) found = NR }
    END {
      for (i = 1; i <= NR; i++) {
        if (i == found) { print row; continue }
        print lines[i]
        if (!found && i == last) print row
      }
    }' "$UPSTREAM_MD" > "$tmp"
  mv "$tmp" "$UPSTREAM_MD"
}

if [ "$#" -lt 3 ] || [ "$#" -gt 4 ]; then
  echo "usage: $0 <Name> <repo-url> <commit> [<subdir>]" >&2
  exit 2
fi

NAME="$1"
REPO_URL="$2"
COMMIT="$3"
SUBDIR="${4:-.}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GRAMMAR_DIR="$ROOT/grammars/$NAME"
UPSTREAM_DIR="$GRAMMAR_DIR/upstream"
UPSTREAM_MD="$ROOT/UPSTREAM.md"
CACHE_DIR="$ROOT/.cache/cli"

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
case "$OS" in
  linux*) OS=linux ;;
  darwin*) OS=macos ;;
  msys*|mingw*|cygwin*) OS=windows ;;
esac
ARCH="$(uname -m)"
case "$ARCH" in
  x86_64|AMD64|x64) ARCH=x64 ;;
  aarch64|arm64) ARCH=arm64 ;;
esac

CLI_ZIP="tree-sitter-cli-$OS-$ARCH.zip"
CLI_URL="https://github.com/tree-sitter/tree-sitter/releases/download/v0.27.2/$CLI_ZIP"
CLI_DIR="$CACHE_DIR/cli"

mkdir -p "$CACHE_DIR"
if [ ! -f "$CACHE_DIR/$CLI_ZIP" ]; then
  echo "==> downloading tree-sitter CLI v0.27.2 ($CLI_ZIP) from $CLI_URL"
  if command -v curl >/dev/null 2>&1; then
    if curl -fsSL --retry 3 --connect-timeout 30 -o "$CACHE_DIR/$CLI_ZIP.part" "$CLI_URL"; then
      mv "$CACHE_DIR/$CLI_ZIP.part" "$CACHE_DIR/$CLI_ZIP"
    else
      echo "curl download failed, trying wget" >&2
      if command -v wget >/dev/null 2>&1; then
        wget -q -O "$CACHE_DIR/$CLI_ZIP.part" "$CLI_URL" && mv "$CACHE_DIR/$CLI_ZIP.part" "$CACHE_DIR/$CLI_ZIP"
      else
        echo "error: neither curl nor wget could download $CLI_URL" >&2
        exit 1
      fi
    fi
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$CACHE_DIR/$CLI_ZIP.part" "$CLI_URL" && mv "$CACHE_DIR/$CLI_ZIP.part" "$CACHE_DIR/$CLI_ZIP"
  else
    echo "error: no curl or wget available to download $CLI_URL" >&2
    exit 1
  fi
fi

rm -rf "$CLI_DIR"
mkdir -p "$CLI_DIR"
if command -v unzip >/dev/null 2>&1; then
  unzip -o -q "$CACHE_DIR/$CLI_ZIP" -d "$CLI_DIR"
elif [ "$OS" = windows ] && command -v pwsh >/dev/null 2>&1 && command -v cygpath >/dev/null 2>&1; then
  pwsh -NoProfile -Command "Expand-Archive -Path \"$(cygpath -w "$CACHE_DIR/$CLI_ZIP")\" -DestinationPath \"$(cygpath -w "$CLI_DIR")\" -Force" >/dev/null
else
  echo "error: cannot extract $CLI_ZIP (no unzip or pwsh+cygpath)" >&2
  exit 1
fi
CLI_BIN=""
for candidate in "$CLI_DIR/tree-sitter" "$CLI_DIR/tree-sitter.exe" "$CLI_DIR"/*/tree-sitter "$CLI_DIR"/*/tree-sitter.exe; do
  if [ -f "$candidate" ]; then
    CLI_BIN="$candidate"
    break
  fi
done
if [ -z "$CLI_BIN" ]; then
  echo "error: tree-sitter CLI binary not found in $CLI_ZIP" >&2
  exit 1
fi
chmod +x "$CLI_BIN"
echo "==> CLI: $CLI_BIN"

WORK_DIR="$(mktemp -d)"
CLONE_DIR="$WORK_DIR/grammar"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "==> cloning grammar $REPO_URL @ $COMMIT"
git clone --filter=blob:none --no-checkout "$REPO_URL" "$CLONE_DIR"
git -C "$CLONE_DIR" fetch --depth 1 origin "$COMMIT"
git -C "$CLONE_DIR" checkout -q "$COMMIT"

ACTUAL_COMMIT="$(git -C "$CLONE_DIR" rev-parse HEAD)"
if [ "$ACTUAL_COMMIT" != "$COMMIT" ]; then
  echo "error: resolved commit $ACTUAL_COMMIT != requested $COMMIT" >&2
  exit 1
fi

# Grammars that extend another grammar (C++ uses tree-sitter-c, TypeScript uses
# tree-sitter-javascript) load it with require() from node_modules: install the
# dependencies of package.json first, without running their install scripts.
if grep -q "require(" "$CLONE_DIR/grammar.js" "$CLONE_DIR"/*/grammar.js 2>/dev/null && [ -f "$CLONE_DIR/package.json" ]; then
  echo "==> installing the grammar dependencies (npm)"
  (cd "$CLONE_DIR" && npm install --ignore-scripts --no-audit --no-fund >/dev/null)
fi

GEN_DIR="$CLONE_DIR/$SUBDIR"
echo "==> running tree-sitter generate in $SUBDIR (CLI: $CLI_BIN)"
(cd "$GEN_DIR" && "$CLI_BIN" generate)

PARSER_C="$GEN_DIR/src/parser.c"
if [ ! -f "$PARSER_C" ]; then
  echo "error: src/parser.c was not generated" >&2
  exit 1
fi
if ! grep -q '^#define LANGUAGE_VERSION 15' "$PARSER_C"; then
  echo "error: grammar $NAME does not declare LANGUAGE_VERSION 15 (ABI 15)" >&2
  exit 1
fi

echo "==> copying upstream sources to $UPSTREAM_DIR"
rm -rf "$UPSTREAM_DIR"
mkdir -p "$UPSTREAM_DIR/src"

cp "$GEN_DIR/grammar.js" "$UPSTREAM_DIR/grammar.js"
cp "$GEN_DIR/src/parser.c" "$UPSTREAM_DIR/src/parser.c"
cp "$GEN_DIR/src/node-types.json" "$UPSTREAM_DIR/src/node-types.json"
if [ -f "$GEN_DIR/src/scanner.c" ]; then
  cp "$GEN_DIR/src/scanner.c" "$UPSTREAM_DIR/src/scanner.c"
  # Headers and sources the scanner includes (tag.h of tree-sitter-html,
  # schema.*.c of tree-sitter-yaml). Only parser.c and scanner.c are compiled.
  for extra in "$GEN_DIR"/src/*.h "$GEN_DIR"/src/*.c; do
    [ -f "$extra" ] && cp "$extra" "$UPSTREAM_DIR/src/"
  done
fi
if [ "$SUBDIR" != . ] && [ -d "$CLONE_DIR/common" ]; then
  rm -rf "$GRAMMAR_DIR/common"
  for header in "$CLONE_DIR"/common/*.h; do
    [ -f "$header" ] || continue
    mkdir -p "$GRAMMAR_DIR/common"
    cp "$header" "$GRAMMAR_DIR/common/"
  done
fi
if [ -d "$GEN_DIR/src/tree_sitter" ]; then
  mkdir -p "$UPSTREAM_DIR/src/tree_sitter"
  cp "$GEN_DIR/src/tree_sitter"/*.h "$UPSTREAM_DIR/src/tree_sitter/"
fi
# queries/ and test/corpus/ of the grammar directory, else of the repo root.
for part in queries test/corpus; do
  for base in "$GEN_DIR" "$CLONE_DIR"; do
    if [ -d "$base/$part" ]; then
      mkdir -p "$(dirname "$UPSTREAM_DIR/$part")"
      cp -r "$base/$part" "$UPSTREAM_DIR/$part"
      break
    fi
  done
done
if [ -f "$CLONE_DIR/LICENSE" ]; then
  cp "$CLONE_DIR/LICENSE" "$UPSTREAM_DIR/LICENSE"
fi

LICENSE="$(detect_license "$UPSTREAM_DIR/LICENSE")"
update_upstream_md "$NAME" "$REPO_URL" "$COMMIT" "$LICENSE"

echo "==> grammar $NAME regenerated at commit $ACTUAL_COMMIT, license: $LICENSE"
