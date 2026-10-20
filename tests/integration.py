#!/usr/bin/env python3
"""Real HTTP + WebDAV + launcher smoke test, isolated synthetic saves only (Linux)."""
import base64
import contextlib
import http.server
import json
import os
from pathlib import Path
import shlex
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from xml.sax.saxutils import escape

REPO = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get('DOTNET', 'dotnet')
DLL = REPO / 'backend/RyuSync.Api/bin/Debug/net10.0/ryusync.dll'

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

class Dav(http.server.BaseHTTPRequestHandler):
    root = None
    fail_next_upload = False
    def log_message(self, *_): pass
    def do_GET(self): self.handle_request()
    def do_PUT(self): self.handle_request()
    def do_MKCOL(self): self.handle_request()
    def do_PROPFIND(self): self.handle_request()
    def do_DELETE(self): self.handle_request()
    def reply(self, status, body=b'', content='application/octet-stream'):
        self.send_response(status); self.send_header('Content-Type', content)
        self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
    def handle_request(self):
        if self.headers.get('Authorization') != 'Basic ' + base64.b64encode(b'test:password').decode():
            self.reply(401); return
        relative = urllib.parse.unquote(urllib.parse.urlparse(self.path).path).lstrip('/')
        path = (self.root / relative).resolve()
        if not path.is_relative_to(self.root): self.reply(403); return
        if self.command == 'MKCOL':
            if path.exists(): self.reply(405); return
            if not path.parent.is_dir(): self.reply(409); return
            path.mkdir(); self.reply(201)
        elif self.command == 'PUT':
            if type(self).fail_next_upload and '/objects/' in relative:
                type(self).fail_next_upload = False
                self.reply(503); return
            if path.exists() and self.headers.get('If-None-Match') == '*': self.reply(412); return
            if not path.parent.is_dir(): self.reply(409); return
            path.write_bytes(self.rfile.read(int(self.headers.get('Content-Length', 0)))); self.reply(201)
        elif self.command == 'GET':
            if not path.is_file(): self.reply(404); return
            self.reply(200, path.read_bytes())
        elif self.command == 'DELETE':
            if path.is_file(): path.unlink()
            self.reply(204)
        elif self.command == 'PROPFIND':
            if not path.is_dir(): self.reply(404); return
            paths = [path, *path.iterdir()]
            body = '<d:multistatus xmlns:d="DAV:">' + ''.join(
                '<d:response><d:href>' + escape('/' + urllib.parse.quote(str(p.relative_to(self.root)).replace(os.sep, '/')) + ('/' if p.is_dir() else '')) + '</d:href></d:response>' for p in paths) + '</d:multistatus>'
            self.reply(207, body.encode(), 'application/xml')

def synthetic(root, value):
    ix = root / 'bis/system/save/8000000000000000/0'; ix.mkdir(parents=True)
    key = struct.pack('<QQQQBBH', 0x0100F2C0115B6000, 42, 0, 0, 1, 0, 0) + bytes(28)
    val = struct.pack('<QqQBB', 3, 0, 0, 1, 0) + bytes(38)
    assert len(key) == len(val) == 64
    (ix / 'imkvdb.arc').write_bytes(b'IMKV' + struct.pack('<II', 0, 1) + b'IMEN' + struct.pack('<II', 64, 64) + key + val)
    game = root / 'games/0100f2c0115b6000/gui'; game.mkdir(parents=True)
    (game / 'metadata.json').write_text(json.dumps({'title': '통합 테스트 게임'}))
    save = root / 'bis/user/save/0000000000000003/0'; save.mkdir(parents=True)
    if value is not None: (save / 'save.dat').write_text(value)
    return save / 'save.dat'

class Agent:
    def __init__(self, root):
        self.root = root; self.port = port()
        env = {**os.environ, 'DOTNET_CLI_HOME': '/tmp/ryusync-dotnet-home', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
        self.process = subprocess.Popen([DOTNET, str(DLL), 'serve', '--data-dir', str(root), '--port', str(self.port)], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, env=env)
        for _ in range(100):
            try:
                self.info = json.loads((root / 'instance.json').read_text()); self.request('/status'); break
            except (FileNotFoundError, json.JSONDecodeError, OSError): time.sleep(.1)
        else: raise RuntimeError('agent failed to start: ' + self.process.stderr.read().decode())
    def request(self, path, method='GET', body=None, headers=None):
        request = urllib.request.Request(self.info['url'] + '/api' + path, method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={'X-RyuSync-Agent': self.info['token'], 'Content-Type': 'application/json', **(headers or {})})
        with urllib.request.urlopen(request, timeout=10) as response:
            data = response.read(); return json.loads(data) if data else None
    def sync(self, binding=None):
        job = self.request('/sync/jobs', 'POST', {'bindingId': binding})
        for _ in range(200):
            job = self.request('/jobs/' + job['id'])
            if job['status'] not in ('queued', 'running', 'waiting'): return job
            time.sleep(.1)
        raise RuntimeError('job timeout')
    def close(self):
        self.process.terminate()
        try: self.process.wait(timeout=8)
        except subprocess.TimeoutExpired: self.process.kill(); self.process.wait()


def main():
    with tempfile.TemporaryDirectory(prefix='ryusync-integration-') as tmp:
        tmp = Path(tmp); Dav.root = tmp / 'remote'; Dav.root.mkdir()
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Dav)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        a = b = None
        try:
            file_a = synthetic(tmp / 'pc', 'initial'); file_b = synthetic(tmp / 'deck', None)
            a = Agent(tmp / 'pc-state'); b = Agent(tmp / 'deck-state')
            ids = []
            for agent, name in ((a, 'pc'), (b, 'deck')):
                settings = agent.request('/settings'); settings.update(deviceName=name, dataRoot=str(tmp / name), webDavUrl=f'http://127.0.0.1:{server.server_port}', webDavUser='test', workspace='integration', allowInsecureHttp=True)
                agent.request('/settings', 'PUT', settings); agent.request('/storage/unlock', 'POST', {'password': 'password'})
                agent.request('/storage/test', 'POST', {})
                scan = agent.request('/ryujinx/scan', 'POST', {}); assert len(scan['saves']) == 1, scan
                save_id = scan['saves'][0]['id']; ids.append(save_id)
                agent.request('/save-sets', 'POST', {'discoveryId': save_id, 'slot': 'main'})
            assert a.sync()['status'] == 'completed'
            assert b.sync()['status'] == 'completed'
            assert file_b.read_text() == 'initial'
            file_a.write_text('pc progress'); file_b.write_text('deck progress')
            assert a.sync()['status'] == 'completed'
            conflict = b.sync(); assert conflict['status'] == 'conflict', conflict
            outcome = conflict['results'][0]
            job = b.request('/conflicts/' + ids[1] + '/resolve', 'POST', {'choice': outcome['heads'][0], 'expectedLocalHash': outcome['localHash'], 'expectedHeads': outcome['heads']})
            for _ in range(150):
                result = b.request('/jobs/' + job['id'])
                if result['status'] not in ('running', 'queued'): break
                time.sleep(.1)
            assert result['status'] == 'completed', result
            assert file_b.read_text() == 'pc progress'
            assert b.request('/backups')
            # Browser-origin checks and unauthenticated API denial.
            for origin in ('https://evil.example', 'null'):
                try: a.request('/settings', headers={'Origin': origin}); raise AssertionError('bad origin allowed')
                except urllib.error.HTTPError as e: assert e.code == 403
            try: urllib.request.urlopen(a.info['url'] + '/api/settings'); raise AssertionError('unauthenticated API allowed')
            except urllib.error.HTTPError as e: assert e.code == 401
            # A real child process writes a new synthetic save; post-sync must publish it.
            stub = tmp / 'fake-emulator'; sentinel = tmp / 'launched'
            stub.write_text('#!/bin/sh\nsleep 2\nprintf played > ' + shlex.quote(str(file_a)) + '\ntouch ' + shlex.quote(str(sentinel)) + '\n'); stub.chmod(0o700)
            settings = a.request('/settings'); settings['executable'] = str(stub); a.request('/settings', 'PUT', settings)
            session = a.request('/launch-sessions', 'POST', {})
            queued = False
            for _ in range(200):
                session = a.request('/launch-sessions/' + session['id'])
                if session['status'] == 'playing' and not queued:
                    waiting = a.request('/sync/jobs', 'POST', {'bindingId': ids[0]}); assert waiting['status'] == 'waiting'; queued = True
                if session['status'] in ('completed', 'failed', 'blocked', 'needs-attention'): break
                time.sleep(.1)
            assert session['status'] == 'completed', session
            assert queued and sentinel.exists()
            assert b.sync()['status'] == 'completed'
            assert file_b.read_text() == 'played'
            # Conflict must block process creation.
            sentinel.unlink(); file_a.write_text('new pc'); file_b.write_text('new deck'); b.sync()
            blocked = a.request('/launch-sessions', 'POST', {})
            for _ in range(100):
                blocked = a.request('/launch-sessions/' + blocked['id'])
                if blocked['status'] == 'blocked': break
                time.sleep(.1)
            assert blocked['status'] == 'blocked', blocked
            assert not sentinel.exists()
            # A failed upload retains a durable snapshot, then resumes after an agent restart.
            fresh_root = tmp / 'retry-device'
            synthetic(fresh_root, 'retry progress')
            retry = Agent(tmp / 'retry-state')
            try:
                settings = retry.request('/settings')
                settings.update(dataRoot=str(fresh_root), webDavUrl=f'http://127.0.0.1:{server.server_port}', webDavUser='test', workspace='retry', allowInsecureHttp=True)
                retry.request('/settings', 'PUT', settings)
                retry.request('/storage/unlock', 'POST', {'password': 'password'})
                save = retry.request('/ryujinx/scan', 'POST', {})['saves'][0]
                retry.request('/save-sets', 'POST', {'discoveryId': save['id'], 'slot': 'main'})
                Dav.fail_next_upload = True
                assert retry.sync()['status'] == 'failed'
                assert retry.request('/status')['pendingUploads'] == 1
                retry.close()
                retry = Agent(tmp / 'retry-state')
                assert not retry.request('/status')['credentialUnlocked']
                retry.request('/storage/unlock', 'POST', {'password': 'password'})
                assert retry.sync()['status'] == 'completed'
                assert retry.request('/status')['pendingUploads'] == 0
            finally:
                retry.close()
            print('PASS: real HTTP WebDAV round-trip, conflict resolution, API authentication/Origin, pre/post launch sync, queued web sync, conflict launch block, durable failed-upload recovery')
            print('Synthetic fixtures only; no real emulator data accessed.')
        finally:
            if a: a.close()
            if b: b.close()
            server.shutdown(); server.server_close()

if __name__ == '__main__': main()
