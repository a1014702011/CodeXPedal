#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# [AI修改] Claude 剩余额度探针：验证安装路径发现、登录状态和两种额度来源，供 CodeXPedal 接入前实测。
# 只用 Python 3.8+ 标准库，Windows / macOS / Linux 通用。
#
# 用法：
#   python claude-quota-probe.py                 只读探测：安装路径、版本、登录状态、状态栏缓存（不读凭据、不联网）
#   python claude-quota-probe.py --oauth         额外调用官方额度接口（会读取本机 Claude Code 登录令牌，只发往 api.anthropic.com，不打印令牌）
#   python claude-quota-probe.py --oauth --raw   同上，并打印接口原始 JSON（只含百分比与重置时间，不含令牌）
#   python claude-quota-probe.py --statusline-hook   作为 Claude Code statusLine 命令运行：从 stdin 取 rate_limits 写入缓存
#
# 退出码：0=拿到至少一个额度窗口；1=未拿到；2=参数错误。
import argparse
import datetime as dt
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

USAGE_URL = "https://api.anthropic.com/api/oauth/usage"
OAUTH_BETA = "oauth-2025-04-20"
KEYCHAIN_SERVICE = "Claude Code-credentials"
CACHE_NAME = "codexpedal-rate-limits.json"
MAX_BYTES = 1_048_576  # [AI修改] 所有外部输入都有上限，与共享核心 MAX_INPUT 一致
IS_WIN = os.name == "nt"
IS_MAC = sys.platform == "darwin"


def log(msg=""):
    print(msg, flush=True)


def config_dir() -> Path:
    # [AI修改] 与 Claude Code 一致：CLAUDE_CONFIG_DIR 优先，否则 ~/.claude
    env = os.environ.get("CLAUDE_CONFIG_DIR", "").strip()
    return Path(env).expanduser() if env else Path.home() / ".claude"


# ---------------------------------------------------------------- 安装路径发现
def candidate_paths():
    home = Path.home()
    names = ["claude.exe", "claude.cmd", "claude.bat"] if IS_WIN else ["claude"]
    fixed = []
    env_path = os.environ.get("CODEXPEDAL_CLAUDE_PATH", "").strip()
    if env_path:
        fixed.append(("环境变量 CODEXPEDAL_CLAUDE_PATH", Path(env_path).expanduser()))
    if IS_WIN:
        appdata = Path(os.environ.get("APPDATA", home / "AppData" / "Roaming"))
        local = Path(os.environ.get("LOCALAPPDATA", home / "AppData" / "Local"))
        fixed += [
            ("官方原生安装", home / ".local" / "bin" / "claude.exe"),
            ("npm 全局", appdata / "npm" / "claude.cmd"),
            ("WinGet 链接", local / "Microsoft" / "WinGet" / "Links" / "claude.exe"),
            ("旧版本地安装", home / ".claude" / "local" / "claude.exe"),
        ]
        managed_roots = [appdata / "Claude" / "claude-code", local / "AnthropicClaude"]
    else:
        fixed += [
            ("官方原生安装", home / ".local" / "bin" / "claude"),
            ("旧版本地安装", home / ".claude" / "local" / "claude"),
            ("Homebrew (Apple Silicon)", Path("/opt/homebrew/bin/claude")),
            ("Homebrew (Intel) / 系统", Path("/usr/local/bin/claude")),
            ("npm 全局", home / ".npm-global" / "bin" / "claude"),
        ]
        managed_roots = [home / "Library" / "Application Support" / "Claude" / "claude-code"] if IS_MAC else []
    path_entries = []
    for directory in os.environ.get("PATH", "").split(os.pathsep)[:256]:
        directory = directory.strip().strip('"')
        if directory:
            for n in names:
                path_entries.append(("PATH", Path(directory) / n))
    # [AI修改] Claude 桌面版托管的 Claude Code 位于版本号子目录，按修改时间取最新，深度有界
    managed = []
    for root in managed_roots:
        try:
            if root.is_dir():
                for sub in list(root.iterdir())[:64]:
                    for n in names:
                        for p in (sub / n, sub / "bin" / n):
                            if p.is_file():
                                managed.append(("Claude 桌面版托管", p))
        except OSError:
            pass
    managed.sort(key=lambda item: item[1].stat().st_mtime, reverse=True)
    # [AI修改] 顺序：显式配置 > 已知安装位置 > 桌面版托管 > PATH
    return fixed + managed + path_entries


def is_runnable(p: Path) -> bool:
    try:
        if not p.is_file():
            return False
        if IS_WIN:
            # [AI修改] 商店包 WindowsApps 受 ACL 保护，外部进程无法调用
            pf = os.environ.get("ProgramFiles", r"C:\Program Files")
            return not str(p.resolve()).lower().startswith(str(Path(pf) / "WindowsApps").lower())
        return os.access(p, os.X_OK)
    except OSError:
        return False


def resolve_claude():
    seen, found = set(), []
    for source, p in candidate_paths():
        key = str(p).lower() if IS_WIN else str(p)
        if key in seen:
            continue
        seen.add(key)
        if is_runnable(p):
            found.append((source, p))
    return found


def run_cli(exe: Path, args, timeout=20):
    cmd = [str(exe), *args]
    if IS_WIN and exe.suffix.lower() in (".cmd", ".bat"):
        cmd = [os.environ.get("COMSPEC", "cmd.exe"), "/d", "/s", "/c", subprocess.list2cmdline(cmd)]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=timeout, stdin=subprocess.DEVNULL,
                           creationflags=0x08000000 if IS_WIN else 0)  # CREATE_NO_WINDOW
        return r.returncode, r.stdout[:MAX_BYTES].decode("utf-8", "replace").strip()
    except subprocess.TimeoutExpired:
        return None, "超时"
    except OSError as e:
        return None, f"无法启动：{e}"


# ---------------------------------------------------------------- 结果归一化
def to_epoch(value):
    if value is None:
        return None
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        v = float(value)
        return v / 1000 if v > 1e11 else v  # 兼容毫秒
    if isinstance(value, str) and value.strip():
        try:
            return dt.datetime.fromisoformat(value.strip().replace("Z", "+00:00")).timestamp()
        except ValueError:
            return None
    return None


def normalize(five, seven, used_key):
    """把 Claude 的 five_hour/seven_day 转成 CodeXPedal 共享核心的本地桶格式（用量百分比 0–100）。"""
    windows = {}
    for minutes, raw, slot in ((300, five, "primary"), (10080, seven, "secondary")):
        if not isinstance(raw, dict):
            continue
        used = raw.get(used_key)
        if isinstance(used, bool) or not isinstance(used, (int, float)):
            continue
        used = float(used)
        # [AI修改] 官方接口可能返回 >100（超额）；共享核心只接受 0–100，这里截断并标注
        windows[slot] = {"window_minutes": minutes, "used_percent": max(0.0, min(100.0, used)),
                         "resets_at": to_epoch(raw.get("resets_at")), "raw_used": used}
    return {"limit_id": "claude", **windows}


def pct(used):
    # [AI修改] 与客户端一致：剩余百分比四舍五入远离零（76.5→77）
    return int(100 - used + 0.5)


def show(bucket, observed_at, source):
    log(f"  来源：{source}")
    log(f"  采样时间：{dt.datetime.fromtimestamp(observed_at).strftime('%Y-%m-%d %H:%M:%S')}")
    got = False
    for slot, label in (("primary", "5小时"), ("secondary", "7天")):
        w = bucket.get(slot)
        if not w:
            log(f"  {label}：--（未返回）")
            continue
        got = True
        reset = w["resets_at"]
        reset_text = dt.datetime.fromtimestamp(reset).strftime("%m-%d %H:%M") if reset else "--"
        over = "（原始值超过100%，已截断）" if w["raw_used"] > 100 else ""
        log(f"  {label}：剩余 {pct(w['used_percent'])}%  已用 {w['raw_used']:.1f}%  重置 {reset_text}{over}")
    log("  共享核心输入：" + json.dumps({k: ({kk: vv for kk, vv in v.items() if kk != "raw_used"} if isinstance(v, dict) else v)
                                       for k, v in bucket.items()}, ensure_ascii=False))
    return got


# ---------------------------------------------------------------- 方式A：statusLine 缓存
def statusline_hook():
    # [AI修改] 作为 statusLine 命令运行：stdin 是 Claude Code 官方会话 JSON，只摘取 rate_limits 写缓存
    data = sys.stdin.buffer.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        print("CodeXPedal: 输入过大")
        return 0
    try:
        root = json.loads(data.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        print("CodeXPedal: 输入无效")
        return 0
    rl = root.get("rate_limits") if isinstance(root, dict) else None
    if isinstance(rl, dict) and (rl.get("five_hour") or rl.get("seven_day")):
        payload = {"observed_at": dt.datetime.now().timestamp(), "version": root.get("version"),
                   "five_hour": rl.get("five_hour"), "seven_day": rl.get("seven_day")}
        target = config_dir() / CACHE_NAME
        try:
            fd, tmp = tempfile.mkstemp(dir=str(target.parent), prefix=".codexpedal-", suffix=".tmp")
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(payload, f)
            os.replace(tmp, target)  # 原子替换，读取端不会读到半个文件
        except OSError:
            pass
        b = normalize(rl.get("five_hour"), rl.get("seven_day"), "used_percentage")
        parts = [f"{lbl} {pct(b[s]['used_percent'])}%" for s, lbl in (("primary", "5h"), ("secondary", "7d")) if s in b]
        print("剩余 " + " · ".join(parts))
    else:
        print("剩余 --（首次请求后显示）")
    return 0


def read_statusline_cache():
    target = config_dir() / CACHE_NAME
    log(f"\n[方式A] Claude Code 状态栏 rate_limits 缓存（官方文档字段，不读凭据）")
    if not target.is_file():
        log(f"  未找到 {target}")
        log("  如需测试：在 Claude Code 的 settings.json 中把 statusLine 设为")
        exe = sys.executable.replace("\\", "/")
        me = str(Path(__file__).resolve()).replace("\\", "/")
        log(f'    "statusLine": {{"type": "command", "command": "\\"{exe}\\" \\"{me}\\" --statusline-hook"}}')
        log("  然后在 Claude Code 中发一条消息，再运行本脚本。（Pro/Max 订阅，且首次 API 响应后才有该字段）")
        return False
    try:
        if target.stat().st_size > MAX_BYTES:
            log("  缓存文件过大，忽略")
            return False
        payload = json.loads(target.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        log(f"  读取失败：{e}")
        return False
    observed = float(payload.get("observed_at") or 0)
    age = dt.datetime.now().timestamp() - observed
    log(f"  缓存年龄：{age:.0f} 秒（只在 Claude Code 交互会话发请求时更新；空闲时会变旧，界面应显示 ≈）")
    return show(normalize(payload.get("five_hour"), payload.get("seven_day"), "used_percentage"), observed,
                f"statusLine 缓存 {target}")


# ---------------------------------------------------------------- 方式B：官方 OAuth 额度接口
def load_token():
    """返回 (token, 说明, 过期时间epoch或None)。令牌只在内存中使用。"""
    env = os.environ.get("CLAUDE_CODE_OAUTH_TOKEN", "").strip()
    if env:
        return env, "环境变量 CLAUDE_CODE_OAUTH_TOKEN", None
    raw = None
    where = ""
    if IS_MAC:
        where = f"macOS 钥匙串「{KEYCHAIN_SERVICE}」（系统可能弹窗请求授权）"
        try:
            r = subprocess.run(["security", "find-generic-password", "-s", KEYCHAIN_SERVICE, "-w"],
                               capture_output=True, timeout=30)
            if r.returncode == 0:
                raw = r.stdout[:65536].decode("utf-8", "replace").strip()
        except (OSError, subprocess.TimeoutExpired):
            pass
    if raw is None:
        f = config_dir() / ".credentials.json"
        where = str(f)
        try:
            if f.is_file() and f.stat().st_size <= 65536:
                raw = f.read_text(encoding="utf-8")
        except OSError:
            pass
    if not raw:
        return None, where, None
    try:
        oauth = json.loads(raw).get("claudeAiOauth") or {}
    except (ValueError, AttributeError):
        return None, where + "（格式无法识别）", None
    return oauth.get("accessToken"), where, to_epoch(oauth.get("expiresAt"))


def read_oauth(raw_dump: bool):
    log("\n[方式B] 官方 OAuth 额度接口 " + USAGE_URL)
    log("  ※ 未公开文档的接口，Claude 自带 /usage 与第三方监控工具使用；字段可能随版本变化")
    token, where, expires = load_token()
    if not token:
        log(f"  未取得登录令牌（查找位置：{where}）。请先用 Claude Code 登录 Pro/Max 账号。")
        return False
    log(f"  令牌来源：{where}（长度 {len(token)}，不打印内容）")
    now = dt.datetime.now().timestamp()
    if expires and expires <= now:
        log("  令牌已过期。本工具不会代为刷新（避免与 Claude Code 争用刷新令牌），请打开一次 Claude Code 后重试。")
        return False
    req = urllib.request.Request(USAGE_URL, headers={
        "Authorization": f"Bearer {token}", "anthropic-beta": OAUTH_BETA,
        "Accept": "application/json", "User-Agent": "CodeXPedal-probe/0.1"})
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            body = resp.read(MAX_BYTES + 1)
    except urllib.error.HTTPError as e:
        log(f"  HTTP {e.code}：{'令牌无效或已过期，请打开 Claude Code 重新登录' if e.code in (401, 403) else '接口拒绝'}")
        return False
    except (urllib.error.URLError, OSError) as e:
        log(f"  网络失败：{e}")
        return False
    finally:
        token = None  # noqa: F841  尽早丢弃
    if len(body) > MAX_BYTES:
        log("  响应超过安全上限")
        return False
    try:
        root = json.loads(body.decode("utf-8"))
    except ValueError:
        log("  响应不是 JSON")
        return False
    log("  返回字段：" + ", ".join(sorted(root.keys())) if isinstance(root, dict) else "  返回非对象")
    if raw_dump:
        log("  原始响应：" + json.dumps(root, ensure_ascii=False))
    if not isinstance(root, dict):
        return False
    return show(normalize(root.get("five_hour"), root.get("seven_day"), "utilization"), now, "官方 OAuth 额度接口（实时）")


# ---------------------------------------------------------------- 主流程
def main():
    ap = argparse.ArgumentParser(description="CodeXPedal · Claude 剩余额度探针")
    ap.add_argument("--oauth", action="store_true", help="调用官方 OAuth 额度接口（读取本机登录令牌）")
    ap.add_argument("--raw", action="store_true", help="打印接口原始 JSON")
    ap.add_argument("--statusline-hook", action="store_true", help="作为 statusLine 命令运行")
    args = ap.parse_args()
    if args.statusline_hook:
        return statusline_hook()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    log(f"CodeXPedal Claude 额度探针 · {platform.system()} {platform.machine()} · Python {platform.python_version()}")
    log(f"配置目录：{config_dir()}  存在={config_dir().is_dir()}")
    log("\n[1] 安装路径发现")
    found = resolve_claude()
    if not found:
        log("  未找到 Claude Code CLI。可设置 CODEXPEDAL_CLAUDE_PATH 指向 claude 可执行文件。")
    for i, (source, p) in enumerate(found[:8]):
        log(f"  {'→' if i == 0 else ' '} [{source}] {p}")
    installed = bool(found) or config_dir().is_dir()

    if found:
        exe = found[0][1]
        log("\n[2] 版本与登录状态（由 CLI 自己判断，不读凭据）")
        code, out = run_cli(exe, ["--version"])
        log(f"  claude --version → {out or code}")
        code, out = run_cli(exe, ["auth", "status"])
        try:
            st = json.loads(out)
            keep = {k: st.get(k) for k in ("loggedIn", "authMethod", "subscriptionType", "apiProvider", "configDirectory") if k in st}
            log(f"  claude auth status → 退出码 {code}，{json.dumps(keep, ensure_ascii=False)}")
            log(f"  （全部字段名：{', '.join(sorted(st.keys()))}）")
        except (ValueError, AttributeError):
            log(f"  claude auth status → 退出码 {code}，非 JSON 输出（版本可能较旧）")

    ok = read_statusline_cache()
    if args.oauth:
        ok = read_oauth(args.raw) or ok
    else:
        log("\n[方式B] 跳过。加 --oauth 测试官方实时额度接口。")

    log("\n结论：" + ("已拿到 Claude 额度窗口" if ok else "本次未拿到 Claude 额度") +
        f"；界面判定 Claude 已安装={installed}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
