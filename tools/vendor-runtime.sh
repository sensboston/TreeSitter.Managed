#!/usr/bin/env bash
# Vendors the pinned tree-sitter C runtime into tests/Oracle/tree-sitter/lib/
# for the oracle test harness (tests only, never shipped).
#
#   1. clones https://github.com/tree-sitter/tree-sitter at tag v0.27.2
#   2. verifies the resolved commit is 3fb7be49145acfb07e33b61ed5689e2b6771b2e9
#   3. copies lib/include and lib/src into tests/Oracle/tree-sitter/lib/ unchanged
#
set -euo pipefail

REPO_URL="https://github.com/tree-sitter/tree-sitter"
TAG="v0.27.2"
EXPECTED_COMMIT="3fb7be49145acfb07e33b61ed5689e2b6771b2e9"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENDOR_DIR="$ROOT/tests/Oracle/tree-sitter/lib"
WORK_DIR="$(mktemp -d)"
CLONE_DIR="$WORK_DIR/tree-sitter"

trap 'rm -rf "$WORK_DIR"' EXIT

echo "==> cloning $REPO_URL @ $TAG"
git clone --depth 1 --branch "$TAG" "$REPO_URL" "$CLONE_DIR"

ACTUAL_COMMIT="$(git -C "$CLONE_DIR" rev-parse HEAD)"
if [ "$ACTUAL_COMMIT" != "$EXPECTED_COMMIT" ]; then
  echo "error: $TAG resolved to commit $ACTUAL_COMMIT, expected $EXPECTED_COMMIT" >&2
  exit 1
fi

echo "==> commit verified: $ACTUAL_COMMIT"

rm -rf "$VENDOR_DIR"
mkdir -p "$VENDOR_DIR"
cp -r "$CLONE_DIR/lib/include" "$VENDOR_DIR/include"
cp -r "$CLONE_DIR/lib/src" "$VENDOR_DIR/src"

echo "==> vendored tree-sitter $TAG ($ACTUAL_COMMIT) into $VENDOR_DIR"
