#!/usr/bin/env bash
# [AI修改] Swift静态链接同一核心，软件包不携带Rust开发工具。
set -euo pipefail
ROOT_DIR="${1:?工程根目录}"
RUST_TARGET="${2:?Rust目标}"
case "$RUST_TARGET" in aarch64-apple-darwin|x86_64-apple-darwin) ;; *) exit 2 ;; esac
export MACOSX_DEPLOYMENT_TARGET=13.0
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$ROOT_DIR/build/rust}"
cargo build --manifest-path "$ROOT_DIR/shared/Cargo.toml" --locked --release --target "$RUST_TARGET"
