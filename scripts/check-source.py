#!/usr/bin/env python3
# [AI修改] 全生产目录边界与复用审计；报告声明静态限制，不能替代编译或真机验收。
from pathlib import Path
import argparse
import plistlib
import re
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')
ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--report', type=Path)
args = parser.parse_args()
failures = []
def require(condition, message):
    if not condition:
        failures.append(message)
def read(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')

production = [p for directory in ['shared/src', 'macos/Sources', 'windows/src']
              for p in (ROOT / directory).rglob('*') if p.suffix in ['.rs', '.swift', '.cs', '.h', '.c']
              and not set(p.parts).intersection({'bin', 'obj'})]
inventory = []
for path in production:
    source = path.read_text(encoding='utf-8-sig')
    require(re.search(r'codexu|shanggqm|LeadershipModel|PaletteCatalog', source, re.I) is None,
            '生产源码残留旧实现：' + path.relative_to(ROOT).as_posix())
    require('[AI修改]' in source, '缺少修改意图：' + path.relative_to(ROOT).as_posix())
    risks = [label for label, pattern in [('进程/管道', r'Process|Pipe|poll'), ('定时/订阅', r'Timer|Observer|Subscribe'),
             ('缓存/集合', r'cache|Cache|Dictionary|Vec<'), ('文件/目录', r'FileStream|ReadLine|EnumerateFiles|Data\(contentsOf:')]
             if re.search(pattern, source)]
    inventory.append(f"| {path.relative_to(ROOT).as_posix()} | {', '.join(risks) or '纯模型/适配'} |")
rpc = read('windows/src/CodeXPedal.Windows.Core/Services/CodexAppServerClient.cs')
require('ReadBoundedLineAsync' in rpc and '32768' in rpc and '1_048_576' in rpc, 'Windows stdout边界缺失')
require('ReadLineAsync(' not in rpc and 'BeginErrorReadLine' not in rpc, 'Windows仍含无界管道长行读取')
require('_latestNotification' not in rpc and 'DrainErrorAsync' in rpc, 'Windows保留旧缓存成功旁路或stderr未排空')
require('MaximumBytes' in read('windows/src/CodeXPedal.Windows.Core/Services/BoundedLocalText.cs'), '可选本地统计缺少读取上限')
session = read('windows/src/CodeXPedal.Windows.Core/Services/CodexSessionUsageReader.cs')
require('_cache.Count >= 64' in session and 'events.Count >= 4096' in session, '本地会话缓存/事件缺少上限')
require('CancelAfter' in rpc and 'WaitForExit(1000)' in rpc, 'Windows等待/退出缺少上限')
mac_rpc = read('macos/Sources/CodeXPedalCore/CodexRPCClient.swift')
for marker in ['1_048_576', '131_072', 'Darwin.poll', 'Darwin.read', 'SIGKILL', 'deadline', 'FileHandle.nullDevice']:
    require(marker in mac_rpc, 'Mac进程边界缺少：' + marker)
for marker in ['readDataToEndOfFile', 'readabilityHandler', 'availableData', 'waitUntilExit', 'auth.json', 'thread/start', 'turn/start']:
    require(marker not in mac_rpc, 'Mac含禁用入口：' + marker)
store = read('macos/Sources/CodeXPedal/QuotaStore.swift')
for marker in ['!isRefreshing', 'requestGeneration == self.generation', 'timer?.invalidate()', 'client?.cancel()', '[weak self]']:
    require(marker in store, 'Mac刷新边界缺少：' + marker)
for file in ['macos/Sources/CodeXPedalCore/RateLimitParser.swift', 'windows/src/CodeXPedal.Windows.Core/Services/RateLimitParser.cs']:
    source = read(file)
    require('SharedQuotaCore' in source and 'TryParseWindow' not in source and 'Decodable' not in source, '宿主仍有重复解析：' + file)
header = read('macos/Sources/CSharedCore/include/CSharedCore.h')
require('shared/include/codexpedal_core.h' in header, 'Swift没有引用唯一ABI头')
require('pedal_apply' in read('macos/Sources/CodeXPedalCore/SharedQuotaCore.swift') and 'pedal_apply' in read('windows/src/CodeXPedal.Windows.Core/Services/SharedQuotaCore.cs'), '两端没有使用共享状态出口')
plist = plistlib.loads((ROOT / 'Resources/Info.plist').read_bytes())
require(plist.get('LSUIElement') is True and plist.get('CFBundleIdentifier') == 'com.codexpedal.app', 'Mac应用身份不一致')
require(plist.get('LSMinimumSystemVersion') == '13.0', 'Mac最低版本不一致')
require(plist.get('CFBundleShortVersionString') == read('VERSION').strip(), '版本元数据不一致')
swift_tests = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'macos/Tests').rglob('*.swift'))
test_count = len(re.findall(r'func test\w+\s*\(', swift_tests))
require(test_count >= 27, 'Swift回归声明不完整')
require('--draft --prerelease' in read('.github/workflows/release.yml'), '发布没有草稿门禁')
require(not (ROOT/'Resources/Palettes').exists() and not (ROOT/'Resources/LeadershipBadges').exists(), '废弃资源未移除')
report = '# 全生产目录静态风险清单\n\n' + ('失败\n' if failures else '静态边界检查通过\n')
report += '\n这是文件契约检查，不等同完整安全审计、编译、内存、能耗或GUI验收。以下所有生产文件均列入清单，相关生命周期需结合测试和真机复核。\n\n| 文件 | 复核类别 |\n|---|---|\n' + '\n'.join(inventory) + '\n'
if failures:
    report += '\n' + '\n'.join('- ' + item for item in failures) + '\n'
if args.report:
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_bytes(report.replace('\n', '\r\n').encode('utf-8'))
for failure in failures:
    print('失败：' + failure)
print(f'生产文件{len(production)}个；Swift测试声明{test_count}个；静态失败{len(failures)}项。')
sys.exit(1 if failures else 0)
