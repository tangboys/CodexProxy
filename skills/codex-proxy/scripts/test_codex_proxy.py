"""Run unittest; set MIHOMO_CORE to enable tests against an installed core."""
import argparse
import base64
import contextlib
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch
import urllib.error
import urllib.request

import codex_proxy as proxy


class EnvironmentTests(unittest.TestCase):
    def test_child_environment_does_not_mutate_parent_or_keep_bypass(self):
        parent = {'HTTP_PROXY': 'http://old:1', 'https_proxy': 'http://old:2',
                  'ALL_PROXY': 'socks5://old:3', 'no_proxy': '*', 'PATH': 'unchanged'}
        original = parent.copy()
        child = proxy.child_environment({'port': 32100, 'proxy_password': 'test'}, parent)
        self.assertEqual(parent, original)
        self.assertEqual(child['PATH'], original['PATH'])
        self.assertEqual(child['HTTPS_PROXY'], 'http://codex:test@127.0.0.1:32100')
        self.assertEqual(child['NO_PROXY'], 'localhost,127.0.0.1,::1')
        for key, value in child.items():
            if key.lower() in ('http_proxy', 'https_proxy', 'all_proxy'):
                self.assertEqual(value, child['HTTP_PROXY'])

    def test_all_failed_does_not_launch_codex(self):
        class Offline:
            profile = {'selected': proxy.AUTO}
            def probe(self):
                return {}
        with patch.object(proxy.shutil, 'which', return_value='codex.exe'), \
             patch.object(proxy.subprocess, 'call') as call:
            with self.assertRaises(proxy.ProxyError):
                proxy.run_codex(Offline(), [])
            call.assert_not_called()

    def test_codex_receives_proxy_and_literal_arguments(self):
        class Online:
            profile = {'selected': proxy.AUTO, 'port': 32100, 'proxy_password': 'test'}
            def probe(self):
                return {'good': 50}
            def api(self, *args):
                return None
        before = dict(os.environ)
        with patch.object(proxy.shutil, 'which', return_value='codex.exe'), \
             patch.object(proxy.subprocess, 'call', return_value=7) as call:
            self.assertEqual(proxy.run_codex(Online(), ['--', 'exec', 'a literal $value']), 7)
        self.assertEqual(dict(os.environ), before)
        self.assertEqual(call.call_args.args[0],
            ['codex.exe', '-c', 'features.respect_system_proxy=false', 'exec', 'a literal $value'])
        self.assertIn('127.0.0.1:32100', call.call_args.kwargs['env']['HTTPS_PROXY'])

    def test_npm_shim_is_bypassed_to_preserve_literal_arguments(self):
        class Online:
            profile = {'selected': proxy.AUTO, 'port': 32100, 'proxy_password': 'test'}
            def probe(self):
                return {'good': 50}
            def api(self, *args):
                return None
        with tempfile.TemporaryDirectory() as temporary:
            shim = Path(temporary) / 'codex.cmd'
            entry = Path(temporary) / 'node_modules' / '@openai' / 'codex' / 'bin' / 'codex.js'
            entry.parent.mkdir(parents=True)
            entry.touch()
            with patch.object(proxy.shutil, 'which', side_effect=lambda name: str(shim) if name == 'codex' else 'node.exe'), \
                 patch.object(proxy.subprocess, 'call', return_value=0) as call:
                proxy.run_codex(Online(), ['exec', 'literal & text'])
            self.assertEqual(call.call_args.args[0][:2], ['node.exe', str(entry)])
            self.assertEqual(call.call_args.args[0][-1], 'literal & text')


class FixtureHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', 0)))
        self.server.requests.append((self.command, self.path))
        item = {'id': 'msg_test', 'type': 'message', 'role': 'assistant', 'status': 'completed',
                'content': [{'type': 'output_text', 'text': 'proxy-test-ok', 'annotations': []}]}
        events = [
            {'type': 'response.created', 'response': {'id': 'resp_test', 'status': 'in_progress', 'output': []}},
            {'type': 'response.output_item.added', 'output_index': 0,
             'item': dict(item, status='in_progress', content=[])},
            {'type': 'response.output_text.delta', 'item_id': 'msg_test',
             'output_index': 0, 'content_index': 0, 'delta': 'proxy-test-ok'},
            {'type': 'response.output_item.done', 'output_index': 0, 'item': item},
            {'type': 'response.completed', 'response': {'id': 'resp_test', 'status': 'completed',
             'output': [item], 'usage': {'input_tokens': 1, 'output_tokens': 1, 'total_tokens': 2}}}]
        body = ''.join('event: ' + event['type'] + '\ndata: ' + json.dumps(event) + '\n\n'
                       for event in events).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_CONNECT(self):
        self.server.requests.append((self.command, self.path))
        self.send_response(200, 'Connection established')
        self.end_headers()
        self.close_connection = False
        self.handle_one_request()
        self.close_connection = True

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        self.server.requests.append((self.command, self.path))
        time.sleep(self.server.delay)
        self.send_response(self.server.status)
        self.end_headers()
        if self.server.status == 200:
            self.wfile.write(self.server.payload)

    def log_message(self, *args):
        pass


@unittest.skipUnless(os.environ.get('MIHOMO_CORE'), 'MIHOMO_CORE not specified')
class RealCoreTests(unittest.TestCase):
    def setUp(self):
        self.resources = contextlib.ExitStack()
        self.addCleanup(self.resources.close)
        self.directory = Path(self.resources.enter_context(tempfile.TemporaryDirectory(prefix='codex-proxy-test-')))
        self.good = self.server(204, delay=0.04)
        self.slow = self.server(204, delay=0.20)
        self.subscription = self.server(200)
        port, controller = proxy.free_ports()
        self.profile = {'url': f'http://127.0.0.1:{self.subscription.server_port}/sub?token=private-test-token',
            'core': os.environ['MIHOMO_CORE'], 'port': port, 'controller_port': controller,
            'secret': 'test-controller-secret', 'proxy_password': 'test-password',
            'selected': proxy.AUTO, 'test_url': 'http://check.invalid/ping', 'expected': '204'}
        self.good_name = '可用 / HK #1'
        self.payload = {'proxies': [
            {'name': self.good_name, 'type': 'http', 'server': '127.0.0.1', 'port': self.good.server_port},
            {'name': 'slower', 'type': 'http', 'server': '127.0.0.1', 'port': self.slow.server_port},
            {'name': 'dead', 'type': 'http', 'server': '127.0.0.1', 'port': 1}]}
        self.subscription.payload = json.dumps(self.payload, ensure_ascii=False).encode()
        proxy.private_directory(self.directory)
        proxy.save_json(self.directory / 'profile.json', self.profile)

    def server(self, status, delay=0):
        server = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        server.status, server.delay, server.payload = status, delay, b''
        server.requests = []
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.resources.callback(server.server_close)
        self.resources.callback(server.shutdown)
        return server

    def test_yaml_selection_failover_auth_and_cleanup(self):
        before = dict(os.environ)
        with proxy.Core(self.directory, self.profile) as core:
            owner = core.process
            self.assertEqual(len(core.nodes()), 3)
            with proxy.Core(self.directory, proxy.load_profile(self.directory)) as borrowed:
                self.assertIsNone(borrowed.process)
            self.assertIsNone(owner.poll())
            delays = core.probe()
            self.assertIn(self.good_name, delays)
            self.assertNotIn('dead', delays)
            core.choose(self.good_name)
            self.assertEqual(core.api('/proxies/' + proxy.SELECT)['now'], self.good_name)
            self.assertEqual(proxy.load_profile(self.directory)['selected'], self.good_name)
            core.choose(proxy.AUTO)
            self.assertEqual(core.api('/proxies/' + proxy.SELECT)['now'], proxy.AUTO)
            self.assertEqual(core.api('/proxies/' + proxy.AUTO)['now'], self.good_name)
            self.good.status = 503
            remaining = core.probe()
            self.assertNotIn(self.good_name, remaining)
            self.assertEqual(core.api('/proxies/' + proxy.AUTO)['now'], 'slower')
            endpoint = 'http://check.invalid/ping'
            no_auth = urllib.request.build_opener(urllib.request.ProxyHandler(
                {'http': f"http://127.0.0.1:{self.profile['port']}"}))
            with self.assertRaises(urllib.error.HTTPError) as failure:
                no_auth.open(endpoint, timeout=5)
            self.assertEqual(failure.exception.code, 407)
            failure.exception.close()
            password = proxy.child_environment(self.profile)['HTTP_PROXY']
            authenticated = urllib.request.build_opener(urllib.request.ProxyHandler({'http': password}))
            with authenticated.open(endpoint, timeout=5) as response:
                self.assertEqual(response.status, 204)
            self.slow.status = 503
            self.assertFalse(core.probe())
            with self.assertRaises(proxy.ProxyError):
                core.choose(proxy.AUTO)
        self.assertIsNotNone(owner.poll())
        self.assertEqual(dict(os.environ), before)
        with socket.socket() as check:
            self.assertNotEqual(check.connect_ex(('127.0.0.1', self.profile['port'])), 0)

    def test_uri_and_base64_import(self):
        vmess = base64.b64encode(json.dumps({'v': '2', 'ps': 'vmess-local', 'add': '127.0.0.1',
            'port': '1', 'id': '11111111-1111-4111-8111-111111111111', 'aid': '0',
            'net': 'tcp', 'type': 'none', 'tls': '', 'scy': 'auto'}).encode()).decode()
        ss = base64.b64encode(b'aes-128-gcm:testpass').decode()
        payload = (f'ss://{ss}@127.0.0.1:1#ss-local\nvmess://{vmess}\n'
                   'vless://11111111-1111-4111-8111-111111111111@127.0.0.1:1?encryption=none&type=tcp#vless-local\n'
                   'trojan://testpass@127.0.0.1:1?sni=test.invalid#trojan-local\n').encode()
        for format_name, content in [('uri', payload), ('base64', base64.b64encode(payload))]:
            with self.subTest(format=format_name):
                self.subscription.payload = content
                self.profile['url'] += '&format=' + format_name
                with proxy.Core(self.directory, self.profile) as core:
                    self.assertEqual(set(core.nodes()), {'ss-local', 'vmess-local', 'vless-local', 'trojan-local'})

    def test_refresh_failure_keeps_cache_and_does_not_echo_token(self):
        with proxy.Core(self.directory, self.profile) as core:
            before = set(core.nodes())
            self.subscription.status = 500
            with self.assertRaises(proxy.ProxyError) as failure:
                proxy.perform(core, 'refresh')
            self.assertNotIn('private-test-token', str(failure.exception))
            self.assertEqual(set(core.nodes()), before)

    def test_setup_then_real_codex_version_launch(self):
        url_file = self.directory / 'input.txt'
        url_file.write_text(self.profile['url'], encoding='utf-8')
        args = argparse.Namespace(url_file=str(url_file), core=self.profile['core'],
            test_url=self.profile['test_url'], expected_status='204')
        with contextlib.redirect_stdout(io.StringIO()):
            proxy.setup(self.directory, args)
        profile = proxy.load_profile(self.directory)
        with proxy.Core(self.directory, profile) as core:
            completed = subprocess.run([os.environ['TEST_PYTHON'], str(Path(proxy.__file__)),
                '--state-dir', str(self.directory), 'run', '--', '--version'],
                capture_output=True, text=True, encoding='utf-8', timeout=40)
            self.assertEqual(completed.returncode, 0, completed.stderr)
            self.assertIn('codex-cli', completed.stdout)
            self.assertNotIn(profile['proxy_password'], completed.stdout + completed.stderr)
            self.assertTrue(core.running())
            cli_home = self.directory / 'isolated-cli-home'
            cli_home.mkdir()
            environment = dict(os.environ, CODEX_HOME=str(cli_home), CODEX_PROXY_TEST_KEY='fixture-only')
            completed = subprocess.run([os.environ['TEST_PYTHON'], str(Path(proxy.__file__)),
                '--state-dir', str(self.directory), 'run', '--', 'exec', '--ignore-user-config',
                '--ephemeral', '--skip-git-repo-check', '--json', '-C', str(self.directory),
                '-m', 'proxy-test', '-c', 'model_provider=proxy_test', '-c',
                'model_providers.proxy_test={name="Fixture",base_url="http://api.invalid/v1",'
                'env_key="CODEX_PROXY_TEST_KEY",wire_api="responses",supports_websockets=false}',
                'Reply proxy-test-ok.'], env=environment, capture_output=True,
                text=True, encoding='utf-8', timeout=45)
            self.assertEqual(completed.returncode, 0, completed.stderr + completed.stdout)
            self.assertIn('proxy-test-ok', completed.stdout)
            self.assertTrue(any(method == 'POST' for method, path in self.good.requests))


if __name__ == '__main__':
    unittest.main(verbosity=2)
