#!/usr/bin/env python3
"""Credential API + real Secret Service, isolated D-Bus and keyring data only (Linux)."""
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import http.server
import urllib.error
from integration import Agent, Dav, synthetic


def rejected(agent, path, method='POST', body=None, code=409):
    try:
        agent.request(path, method, body)
    except urllib.error.HTTPError as error:
        assert error.code == code, (error.code, error.read())
    else:
        raise AssertionError('Request unexpectedly succeeded: ' + path)


def exercise(root):
    # This child runs under a NEW session bus with a NEW XDG_DATA_HOME. It never
    # contacts the desktop's existing keyring or uses the user's NAS credentials.
    assert Path(os.environ['XDG_DATA_HOME']).is_relative_to(root)
    assert os.environ.get('DBUS_SESSION_BUS_ADDRESS')
    def start_keyring():
        process = subprocess.Popen(['gnome-keyring-daemon', '--foreground', '--unlock', '--components=secrets',
                               '--control-directory', str(root / 'control')],
                              stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        process.stdin.write(b'ryusync-isolated-keyring-test\n'); process.stdin.close()
        return process
    daemon = start_keyring()
    Dav.root = root / 'remote'; Dav.root.mkdir()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Dav)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    agent = None
    try:
        save = synthetic(root / 'emulator', 'test save')
        agent = Agent(root / 'state')
        settings = agent.request('/settings')
        settings.update(dataRoot=str(root / 'emulator'), webDavUrl=f'http://127.0.0.1:{server.server_port}/',
                        webDavUser='test', workspace='credentials', allowInsecureHttp=True)
        agent.request('/settings', 'PUT', settings)
        credential = agent.request('/storage/credential')
        assert not credential['saved'] and not credential['ready']
        probe = agent.request('/storage/credential/probe', 'POST', {})
        assert probe['storeState'] == 'available', probe
        connection = credential['connectionId']
        apply = dict(password='password', persist=True, expectedConnectionId=connection)
        credential = agent.request('/storage/credential', 'PUT', apply)
        assert credential['saved'] and credential['ready'] and credential['verification'] == 'verified', credential
        assert credential['source'] == 'os-store'
        discovered = agent.request('/ryujinx/scan', 'POST', {})['saves'][0]
        binding = agent.request('/save-sets', 'POST', {'discoveryId': discovered['id'], 'slot': 'main'})
        assert agent.sync(binding['id'])['status'] == 'completed'
        # Invalid candidate does not overwrite the working secret.
        rejected(agent, '/storage/credential', 'PUT', {**apply, 'password': 'incorrect'})
        agent.request('/storage/test', 'POST', {})
        agent.close(); agent = Agent(root / 'state')
        status = agent.request('/storage/credential')
        assert status['ready'] and status['saved'] and status['source'] == 'os-store', status
        assert status['verification'] == 'unknown'
        assert agent.sync(binding['id'])['status'] == 'completed'
        # Lock the PRIVATE OS collection; startup must report locked without a prompt.
        command = ['gdbus', 'call', '--session', '--dest', 'org.freedesktop.secrets', '--object-path', '/org/freedesktop/secrets', '--method']
        alias = subprocess.check_output([*command, 'org.freedesktop.Secret.Service.ReadAlias', 'default'], text=True)
        collection = re.search(r"'([^']+)'", alias).group(1)
        assert collection.startswith('/org/freedesktop/secrets/collection/')
        subprocess.run([*command, 'org.freedesktop.Secret.Service.Lock', "['" + collection + "']"], check=True, stdout=subprocess.DEVNULL)
        agent.close(); agent = Agent(root / 'state')
        locked = agent.request('/storage/credential')
        assert locked['storeState'] == 'locked' and not locked['ready'], locked
        agent.close(); agent = None
        daemon.terminate(); daemon.wait(timeout=5); daemon = start_keyring()
        agent = Agent(root / 'state')
        assert agent.request('/storage/credential')['ready'], agent.request('/storage/credential')
        # Explicit lock is not undone by polling, probing, sync, or startup reuse within this process.
        agent.request('/storage/credential/lock', 'POST', {})
        assert not agent.request('/storage/credential')['ready']
        agent.request('/storage/credential/probe', 'POST', {})
        rejected(agent, '/storage/test', 'POST', {})
        agent.request('/storage/credential/unlock', 'POST', {'expectedConnectionId': connection})
        agent.request('/storage/test', 'POST', {})
        # Switching accounts never sends the previous secret to the new connection.
        changed = {**settings, 'webDavUser': 'other'}
        agent.request('/settings', 'PUT', changed)
        assert not agent.request('/storage/credential')['ready']
        assert len(agent.request('/storage/credential/retired')) == 1
        rejected(agent, '/storage/credential', 'PUT', apply)
        agent.request('/settings', 'PUT', settings)
        agent.request('/storage/credential/unlock', 'POST', {'expectedConnectionId': connection})
        agent.request('/storage/credential?connectionId=' + connection, 'DELETE')
        assert not agent.request('/storage/credential')['saved']
        agent.close(); agent = Agent(root / 'state')
        assert not agent.request('/storage/credential')['ready']
        assert not agent.request('/storage/credential')['saved']
        # Session-only input remains available without recreating an OS entry.
        agent.request('/storage/credential', 'PUT', {**apply, 'persist': False})
        assert agent.request('/storage/credential')['source'] == 'session'
        assert not agent.request('/storage/credential')['saved']
        assert save.read_text() == 'test save'
        print('PASS: real private Secret Service save/read/restart/delete, candidate rejection, connection isolation, session lock and WebDAV sync.')
    except urllib.error.HTTPError as error:
        print('Credential API error:', error.read().decode())
        if agent: print('Credential status:', agent.request('/storage/credential'))
        raise
    finally:
        if agent: agent.close()
        server.shutdown(); server.server_close()
        daemon.terminate()
        try: daemon.wait(timeout=5)
        except subprocess.TimeoutExpired: daemon.kill(); daemon.wait()


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--isolated-child':
        exercise(Path(sys.argv[2]))
    else:
        for program in ('dbus-run-session', 'gnome-keyring-daemon', 'gdbus'):
            if not shutil.which(program): raise SystemExit(f'Test dependency missing: {program}')
        with tempfile.TemporaryDirectory(prefix='ryusync-credential-integration-') as folder:
            root = Path(folder)
            for name in ('data', 'runtime', 'config', 'control'): (root / name).mkdir(mode=0o700)
            env = {**os.environ, 'XDG_DATA_HOME': str(root / 'data'), 'XDG_RUNTIME_DIR': str(root / 'runtime'),
                   'XDG_CONFIG_HOME': str(root / 'config')}
            for key in ('DBUS_SESSION_BUS_ADDRESS', 'GNOME_KEYRING_CONTROL', 'RYUSYNC_WEBDAV_PASSWORD'):
                env.pop(key, None)
            subprocess.run(['dbus-run-session', '--', sys.executable, __file__, '--isolated-child', str(root)],
                           env=env, check=True)
