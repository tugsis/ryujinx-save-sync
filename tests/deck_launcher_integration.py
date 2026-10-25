#!/usr/bin/env python3
"""Deck CLI ownership + fresh single-use tickets, with a controllable browser substitute."""
import os
from pathlib import Path
import subprocess
import tempfile
import time
import json
from integration import Agent, DOTNET, DLL

with tempfile.TemporaryDirectory(prefix='ryusync-deck-launcher-') as tmp:
    root = Path(tmp); agent = Agent(root / 'state')
    browser = root / 'browser with spaces'
    browser.write_text('''#!/usr/bin/env python3
import sys, os, json, time, urllib.request, urllib.parse, http.cookiejar, urllib.error
from pathlib import Path
args=sys.argv[1:]
url=next(a.split('=',1)[1] for a in args if a.startswith('--app='))
parsed=urllib.parse.urlsplit(url)
assert parsed.query=='ui=deck'
origin=parsed.scheme+'://'+parsed.netloc
token=urllib.parse.parse_qs(parsed.fragment)['token'][0]
jar=http.cookiejar.CookieJar();client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
request=urllib.request.Request(origin+'/api/session',data=json.dumps({'token':token}).encode(),headers={'Content-Type':'application/json','Origin':origin})
assert client.open(request).status==200
try:client.open(request);raise AssertionError('ticket reused')
except urllib.error.HTTPError as e:assert e.code==401
assert client.open(origin+'/api/status').status==200
profile=Path(next(a.split('=',1)[1] for a in args if a.startswith('--user-data-dir=')))
assert profile.is_dir()
Path(os.environ['DECK_READY']).write_text(json.dumps({'profile':str(profile)}))
while not Path(os.environ['DECK_CLOSE']).exists():time.sleep(.05)
''')
    browser.chmod(0o700)
    process = None
    try:
        profiles = []
        for index, (command, expected_exit) in enumerate([(["deck"], 0), (["deck"], 0), (["launch", "--deck"], 1)]):
            ready = root / f'ready-{index}'; close = root / f'close-{index}'
            process = subprocess.Popen([DOTNET, str(DLL), *command, '--data-dir', str(root/'state'), '--browser', str(browser)], env={**os.environ,'DECK_READY':str(ready),'DECK_CLOSE':str(close)}, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            for _ in range(150):
                if ready.exists(): break
                if process.poll() is not None: raise AssertionError(process.communicate())
                time.sleep(.1)
            assert ready.exists(), 'window did not start'
            profile = Path(json.loads(ready.read_text())['profile']); profiles.append(profile)
            time.sleep(.2); assert process.poll() is None, 'launcher exited while window was open'
            close.touch(); assert process.wait(timeout=10)==expected_exit
            assert not profile.exists(), 'normal close left cookies behind'
            assert agent.request('/status'), 'closing UI killed shared backend'
        assert len(set(profiles)) == 3
        print('PASS: deck CLI stays alive, unique profiles, fresh single-use tickets on repeated launches, profile cleanup, backend preserved, blocked game opens a fresh Deck window')
    finally:
        if process and process.poll() is None:
            close.touch(); process.terminate(); process.wait(timeout=10)
        agent.close()
