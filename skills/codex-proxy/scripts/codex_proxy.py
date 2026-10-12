#!/usr/bin/env python3
"""Subscription manager and process-scoped Codex launcher. Python stdlib only."""
import argparse
import contextlib
import getpass
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

AUTO = 'CODEX-AUTO'
SELECT = 'CODEX-SELECT'
PROVIDER = 'subscription'
TEST_URL = 'https://www.gstatic.com/generate_204'


class ProxyError(Exception):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


def save_json(path, data):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    if os.name != 'nt':
        temporary.chmod(0o600)
    temporary.replace(path)


def private_directory(path):
    path.mkdir(parents=True, exist_ok=True)
    if os.name == 'nt':
        user = subprocess.check_output(['whoami'], text=True).strip()
        result = subprocess.run(['icacls', str(path), '/inheritance:r', '/grant:r',
                                 f'{user}:(OI)(CI)F'], capture_output=True)
        if result.returncode:
            raise ProxyError('无法限制本地状态目录权限，请选择自己的用户目录。')
    else:
        path.chmod(0o700)


def free_ports():
    with contextlib.ExitStack() as stack:
        sockets = [stack.enter_context(socket.socket()) for _ in range(2)]
        for item in sockets:
            item.bind(('127.0.0.1', 0))
        return [item.getsockname()[1] for item in sockets]


def hidden():
    return subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0


def find_core(explicit=None):
    candidates = [explicit, shutil.which('mihomo'), shutil.which('verge-mihomo')]
    if os.name == 'nt':
        candidates += [r'D:\Clash Verge\verge-mihomo.exe',
                       r'D:\v2rayN-windows-64\bin\mihomo\mihomo.exe']
        for root in (os.environ.get('ProgramFiles'), os.environ.get('LOCALAPPDATA')):
            if root:
                candidates += [str(Path(root) / 'Clash Verge' / 'verge-mihomo.exe')]
    for item in candidates:
        if item and Path(item).is_file():
            result = subprocess.run([str(item), '-v'], capture_output=True, text=True,
                                    timeout=10, creationflags=hidden())
            if result.returncode == 0 and 'mihomo' in result.stdout.lower():
                return str(Path(item).resolve())
        if explicit:
            break
    raise ProxyError('未找到 Mihomo。用 setup --core <mihomo.exe绝对路径> 指定内核。')


def load_profile(directory):
    path = directory / 'profile.json'
    if not path.exists():
        raise ProxyError('尚未配置。请先运行 setup，或无参数启动终端菜单。')
    return json.loads(path.read_text(encoding='utf-8'))


def configuration(profile):
    digest = hashlib.sha256(profile['url'].encode()).hexdigest()[:16]
    return {
        'mixed-port': profile['port'], 'bind-address': '127.0.0.1',
        'allow-lan': False, 'mode': 'rule', 'log-level': 'warning',
        'authentication': ['codex:' + profile['proxy_password']],
        'skip-auth-prefixes': [], 'tun': {'enable': False},
        'external-controller': f"127.0.0.1:{profile['controller_port']}",
        'secret': profile['secret'], 'profile': {'store-selected': False},
        'proxy-providers': {PROVIDER: {
            'type': 'http', 'url': profile['url'],
            'path': f'./subscription-{digest}.yaml', 'interval': 86400,
            'proxy': 'DIRECT', 'size-limit': 10485760,
            'health-check': {'enable': True, 'url': profile['test_url'],
                             'expected-status': profile['expected'],
                             'interval': 300, 'timeout': 5000, 'lazy': False}}},
        'proxy-groups': [
            {'name': AUTO, 'type': 'url-test', 'use': [PROVIDER],
             'url': profile['test_url'], 'expected-status': profile['expected'],
             'interval': 300, 'tolerance': 50, 'lazy': False,
             'exclude-type': 'Direct|Compatible|Reject|RejectDrop|Pass', 'empty-fallback': 'REJECT'},
            {'name': SELECT, 'type': 'select', 'proxies': [AUTO], 'use': [PROVIDER],
             'exclude-type': 'Direct|Compatible|Reject|RejectDrop|Pass', 'empty-fallback': 'REJECT'}],
        'rules': [f'MATCH,{SELECT}'],
    }


class Core:
    def __init__(self, directory, profile):
        self.directory, self.profile = directory, profile
        self.process = None
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def api(self, path, method='GET', data=None, timeout=10):
        url = f"http://127.0.0.1:{self.profile['controller_port']}{path}"
        request = urllib.request.Request(url, method=method,
            data=None if data is None else json.dumps(data).encode(),
            headers={'Authorization': 'Bearer ' + self.profile['secret'],
                     'Content-Type': 'application/json'})
        try:
            with self.opener.open(request, timeout=timeout) as response:
                body = response.read()
                return json.loads(body) if body else None
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            # Core errors can contain the full subscription URL. Never echo the body.
            code = getattr(exc, 'code', None)
            if isinstance(exc, urllib.error.HTTPError):
                exc.close()
            raise ProxyError(f'本地代理请求失败（{code or "连接失败或超时"}）。', code) from None

    def running(self):
        try:
            return self.api('/configs', timeout=1)['mixed-port'] == self.profile['port']
        except ProxyError:
            return False

    def provider_name(self):
        providers = self.api('/providers/proxies').get('providers', {})
        name = PROVIDER + '-' + hashlib.sha256(self.profile['url'].encode()).hexdigest()[:16]
        return name if name in providers else PROVIDER

    def nodes(self):
        entries = self.api(f'/providers/proxies/{self.provider_name()}').get('proxies', [])
        return [item['name'] for item in entries
                if item['type'].lower() not in ('direct', 'reject', 'rejectdrop', 'compatible', 'pass')]

    def __enter__(self):
        if self.running():
            return self
        for port in (self.profile['port'], self.profile['controller_port']):
            with socket.socket() as check:
                try:
                    check.bind(('127.0.0.1', port))
                except OSError:
                    raise ProxyError('专用端口被其他程序占用；退出专用启动器后重新 setup。') from None
        save_json(self.directory / 'config.json', configuration(self.profile))
        log = self.directory / 'core.log'
        try:
            with log.open('w', encoding='utf-8') as output:
                self.process = subprocess.Popen(
                    [self.profile['core'], '-d', str(self.directory),
                     '-f', str(self.directory / 'config.json')],
                    stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                    creationflags=hidden())
            deadline = time.monotonic() + 35
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise ProxyError('Mihomo 启动失败；请本地检查 core.log，不要分享含凭据的日志。')
                try:
                    if self.nodes():
                        selected = self.profile['selected']
                        if selected != AUTO and selected not in self.nodes():
                            print('原手选节点已不在订阅中；请重新 select 或 auto。')
                        else:
                            self.api(f'/proxies/{SELECT}', 'PUT', {'name': selected})
                        return self
                except ProxyError:
                    pass
                time.sleep(0.2)
            raise ProxyError('35 秒内未加载到节点；检查订阅是否可直连获取、格式是否受内核支持。')
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *unused):
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()

    def probe(self):
        query = urllib.parse.urlencode({'url': self.profile['test_url'],
                                        'timeout': 5000, 'expected': self.profile['expected']})
        try:
            result = self.api(f'/group/{AUTO}/delay?{query}', timeout=30)
        except ProxyError as exc:
            if exc.status == 504:
                return {}
            raise
        entries = self.api(f'/providers/proxies/{self.provider_name()}').get('proxies', [])
        # URLTest can return a delay even when the HTTP status did not match.
        return {item['name']: result[item['name']] for item in entries
                if item.get('extra', {}).get(self.profile['test_url'], {}).get('alive', False)
                and isinstance(result.get(item['name']), int) and result[item['name']] > 0}

    def choose(self, name):
        if name != AUTO and name not in self.nodes():
            raise ProxyError('节点不存在；用 list 查看完整节点名称。')
        available = self.probe()
        if not available or (name != AUTO and name not in available):
            raise ProxyError('所选节点或所有节点检测失败，保留原选择。')
        self.api(f'/proxies/{SELECT}', 'PUT', {'name': name})
        self.profile['selected'] = name
        save_json(self.directory / 'profile.json', self.profile)
        print('已启用持续自动选择（300 秒检测一次）。' if name == AUTO else f'已固定节点：{name}')


def setup(directory, args):
    if (directory / 'profile.json').exists():
        previous = load_profile(directory)
        if Core(directory, previous).running():
            raise ProxyError('专用内核正在使用中，请退出专用启动器后再修改订阅。')
    url = (Path(args.url_file).read_text(encoding='utf-8-sig').strip()
           if args.url_file else getpass.getpass('粘贴订阅链接（隐藏输入，回车确认）：').strip())
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in ('http', 'https') or not parsed.hostname:
        raise ProxyError('订阅必须是 HTTP(S) 链接。')
    test = urllib.parse.urlsplit(args.test_url)
    if test.scheme not in ('http', 'https') or not test.hostname:
        raise ProxyError('检测地址必须是 HTTP(S) 链接。')
    port, controller = free_ports()
    profile = {'url': url, 'core': find_core(args.core), 'port': port,
               'controller_port': controller, 'secret': secrets.token_hex(24),
               'proxy_password': secrets.token_hex(24), 'selected': AUTO,
               'test_url': args.test_url, 'expected': args.expected_status}
    private_directory(directory)
    with Core(directory, profile) as core:
        count = len(core.nodes())
    save_json(directory / 'profile.json', profile)
    print(f'订阅已保存，导入 {count} 个节点。可运行 test、select、auto 或 run。')


def child_environment(profile, parent=None):
    environment = dict(os.environ if parent is None else parent)
    for key in list(environment):
        if key.lower() in ('http_proxy', 'https_proxy', 'all_proxy', 'no_proxy'):
            del environment[key]
    proxy = f"http://codex:{profile['proxy_password']}@127.0.0.1:{profile['port']}"
    values = {'HTTP_PROXY': proxy, 'HTTPS_PROXY': proxy, 'ALL_PROXY': proxy,
              'NO_PROXY': 'localhost,127.0.0.1,::1'}
    environment.update(values)
    if os.name != 'nt':
        environment.update({key.lower(): value for key, value in values.items()})
    return environment


def run_codex(core, arguments):
    executable = shutil.which('codex')
    if not executable:
        raise ProxyError('PATH 中没有 codex，请先安装 Codex CLI。')
    command = [executable]
    if Path(executable).suffix.lower() in ('.cmd', '.bat'):
        entry = Path(executable).parent / 'node_modules' / '@openai' / 'codex' / 'bin' / 'codex.js'
        node = shutil.which('node')
        if not node or not entry.is_file():
            raise ProxyError('无法直接调用 Codex 批处理启动器；请检查官方 npm 安装或使用 codex.exe。')
        command = [node, str(entry)]
    selected = core.profile['selected']
    if selected != AUTO and selected not in core.nodes():
        raise ProxyError('原手选节点已消失；请先运行 select 或 auto。')
    available = core.probe()
    if not available or (selected != AUTO and selected not in available):
        raise ProxyError('所选节点或所有节点检测失败，未启动 Codex。')
    core.api(f'/proxies/{SELECT}', 'PUT', {'name': selected})
    print('正在通过专用代理启动 Codex；代理环境仅传给该进程及其子进程。', flush=True)
    if arguments[:1] == ['--']:
        arguments = arguments[1:]
    return subprocess.call([*command, '-c', 'features.respect_system_proxy=false', *arguments],
                           env=child_environment(core.profile))


def perform(core, action, name=None, arguments=None):
    if action in ('list', 'test'):
        delays = core.probe() if action == 'test' else {}
        for index, node in enumerate(core.nodes(), 1):
            detail = f'{delays[node]} ms' if node in delays else ('不可达' if action == 'test' else '未检测')
            print(f'{index:3}. {node}  [{detail}]')
        if action == 'test' and not delays:
            raise ProxyError('所有节点检测失败。')
    elif action in ('select', 'auto'):
        if action == 'select' and name is None:
            nodes = core.nodes()
            for index, node in enumerate(nodes, 1):
                print(f'{index:3}. {node}')
            answer = input('输入节点编号：').strip()
            if not answer.isdigit() or not 1 <= int(answer) <= len(nodes):
                raise ProxyError('无效节点编号。')
            name = nodes[int(answer) - 1]
        core.choose(AUTO if action == 'auto' else name)
    elif action == 'refresh':
        core.api(f'/providers/proxies/{core.provider_name()}', 'PUT', timeout=45)
        print(f'订阅已刷新，共 {len(core.nodes())} 个节点。')
    elif action == 'run':
        return run_codex(core, arguments or [])
    return 0


def menu(directory, args):
    if not (directory / 'profile.json').exists():
        setup(directory, args)
    with Core(directory, load_profile(directory)) as core:
        actions = {'1': 'test', '2': 'select', '3': 'auto', '4': 'run', '5': 'refresh'}
        while True:
            print('\n1 检测节点  2 手动选择  3 自动选择  4 启动 Codex  5 刷新订阅  0 退出')
            choice = input('请选择：').strip()
            if choice == '0':
                return 0
            try:
                if choice not in actions:
                    raise ProxyError('请输入菜单中的编号。')
                core.profile = load_profile(directory)
                perform(core, actions[choice])
            except ProxyError as exc:
                print(str(exc), file=sys.stderr)


def main(argv=None):
    default_directory = Path(os.environ.get('LOCALAPPDATA', str(Path.home() / '.local' / 'share'))) / 'codex-proxy'
    parser = argparse.ArgumentParser(description='Codex CLI 专用订阅代理；无参数进入菜单。')
    parser.add_argument('--state-dir', type=Path, default=default_directory)
    commands = parser.add_subparsers(dest='action')
    initialize = commands.add_parser('setup', help='隐藏输入订阅链接并验证导入')
    initialize.add_argument('--url-file', help='读取只含订阅链接的 UTF-8 文本文件')
    initialize.add_argument('--core', help='Mihomo 内核绝对路径')
    initialize.add_argument('--test-url', default=TEST_URL)
    initialize.add_argument('--expected-status', default='204')
    for action in ('list', 'test', 'auto', 'refresh', 'status', 'menu'):
        commands.add_parser(action)
    commands.add_parser('select').add_argument('name', nargs='?')
    commands.add_parser('run').add_argument('arguments', nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    for key, value in {'core': None, 'url_file': None, 'test_url': TEST_URL,
                       'expected_status': '204'}.items():
        if not hasattr(args, key):
            setattr(args, key, value)
    directory = args.state_dir.expanduser().resolve()
    if args.action == 'setup':
        setup(directory, args)
    elif args.action == 'status':
        if not (directory / 'profile.json').exists():
            print('尚未配置订阅；请运行 setup。')
        else:
            profile = load_profile(directory)
            core = Core(directory, profile)
            print('内核：' + ('运行中' if core.running() else '未运行，执行命令时临时启动'))
            print('选择：' + ('自动' if profile['selected'] == AUTO else profile['selected']))
            print(f"代理监听：127.0.0.1:{profile['port']}（需要身份验证）")
    elif args.action in (None, 'menu'):
        return menu(directory, args)
    else:
        if args.action == 'run' and not (directory / 'profile.json').exists():
            setup(directory, args)
        with Core(directory, load_profile(directory)) as core:
            return perform(core, args.action, getattr(args, 'name', None),
                           getattr(args, 'arguments', None))
    return 0


if __name__ == '__main__':
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    try:
        sys.exit(main())
    except ProxyError as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
    except (OSError, ValueError, KeyError) as exc:
        print(f'本地配置或程序错误（{type(exc).__name__}），请重新 setup 或检查内核路径。', file=sys.stderr)
        sys.exit(1)
    except (KeyboardInterrupt, EOFError):
        print('\n已退出；本启动器拥有的内核已清理。', file=sys.stderr)
        sys.exit(130)
