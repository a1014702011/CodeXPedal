# [AI修改] 默认入口完全切换到独立 macOS 客户端，旧发布入口在真机验收前失败关闭。
SHELL := /bin/bash
APP_NAME := CodeXPedal
DEPLOYMENT_TARGET ?= 13.0
HOST_ARCH := $(shell uname -m)
TARGET_TRIPLE ?= $(HOST_ARCH)-apple-macos$(DEPLOYMENT_TARGET)
SIGN_IDENTITY ?= -
APP_DIR := $(CURDIR)/build/CodeXPedal.app
RUST_TARGET := $(if $(filter arm64,$(HOST_ARCH)),aarch64-apple-darwin,x86_64-apple-darwin)
CARGO_TARGET_DIR ?= $(CURDIR)/build/rust
export CARGO_TARGET_DIR
export CODEXPEDAL_CORE_LIB_DIR := $(CARGO_TARGET_DIR)/$(RUST_TARGET)/release

.PHONY: build run probe test verify source-check memory-risk-check release release-all release-arm64 release-intel release-package release-check dmg dmg-arm64 dmg-intel notarize

build:
	@sed 's/\r$$//' macos/scripts/build-app.sh | bash -s -- "$(CURDIR)" "$(TARGET_TRIPLE)" "$(SIGN_IDENTITY)"

core:
	@sed 's/\r$$//' shared/scripts/build-macos.sh | bash -s -- "$(CURDIR)" "$(RUST_TARGET)"

test: core
	@mkdir -p build
	@set -o pipefail; swift test --package-path macos --parallel --enable-code-coverage --xunit-output "$(CURDIR)/build/macos-tests.xml" 2>&1 | tee build/macos-tests.log

run: build
	open "$(APP_DIR)"

probe: build
	"$(APP_DIR)/Contents/MacOS/$(APP_NAME)" --dump-json

verify: build
	codesign --verify --deep --strict "$(APP_DIR)"
	file "$(APP_DIR)/Contents/MacOS/$(APP_NAME)"

source-check:
	python3 scripts/check-source.py

memory-risk-check: source-check
	python3 scripts/check-source.py --report build/memory-risk/report.md

# [AI修改] 自动构建仅生成候选包；公开发布需另过真机验收门禁。
package: memory-risk-check test build verify
	@sed 's/\r$$//' macos/scripts/package-app.sh | bash -s -- "$(CURDIR)"

# [AI修改] 新实现尚未完成 Mac 编译/真机验收，不允许旧发布脚本误发历史安装包。
release release-all release-arm64 release-intel release-package release-check dmg dmg-arm64 dmg-intel notarize:
	@echo 'macOS 重写尚待构建与真机验收，发布入口暂不可用。' >&2
	@exit 2
