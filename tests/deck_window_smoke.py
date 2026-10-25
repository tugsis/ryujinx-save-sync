#!/usr/bin/env python3
"""Linux: real Chrome app window under Xvfb; no user browser profile or external site."""
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import urllib.request
from integration import Agent, DOTNET, DLL

with tempfile.TemporaryDirectory(prefix='ryusync-native-deck-') as tmp:
    root=Path(tmp); agent=Agent(root/'state'); process=None
    browser=root/'test-chrome'
    browser.write_text('''#!/usr/bin/env python3
import os,sys
from pathlib import Path
profile=next(a.split('=',1)[1] for a in sys.argv[1:] if a.startswith('--user-data-dir='))
Path(os.environ['DECK_TEST_PROFILE']).write_text(profile)
os.execv('/usr/bin/google-chrome',['google-chrome','--no-sandbox','--disable-gpu','--no-first-run','--remote-debugging-port=0','--password-store=basic',*sys.argv[1:]])
''')
    browser.chmod(0o700)
    try:
        with open(root/'browser.log','w') as log:
            process=subprocess.Popen(['xvfb-run','-a',DOTNET,str(DLL),'deck','--data-dir',str(root/'state'),'--browser',str(browser)],env={**os.environ,'DECK_TEST_PROFILE':str(root/'profile'),'DBUS_SESSION_BUS_ADDRESS':'unix:path='+str(root/'no-bus'),'NO_AT_BRIDGE':'1','GTK_USE_PORTAL':'0'},stdout=log,stderr=log,start_new_session=True)
            for _ in range(200):
                if (root/'profile').exists():
                    profile=Path((root/'profile').read_text())
                    if (profile/'DevToolsActivePort').exists(): break
                if process.poll() is not None: raise AssertionError('App window failed to start')
                time.sleep(.1)
            else: raise AssertionError('Browser debugger startup timed out')
            debug_port=(profile/'DevToolsActivePort').read_text().splitlines()[0]
            def request(path):
                with urllib.request.urlopen('http://127.0.0.1:'+debug_port+path,timeout=5) as response:return response.read()
            for _ in range(100):
                pages=json.loads(request('/json/list'))
                page=next((p for p in pages if p.get('type')=='page' and p.get('url','').startswith(agent.info['url'])),None)
                if page and '#token=' not in page['url']:break
                time.sleep(.1)
            else:raise AssertionError('App did not load and consume its ticket')
            assert process.poll() is None
            request('/json/close/'+page['id'])
            assert process.wait(timeout=20)==0
            assert agent.request('/status')
            print('PASS: real Chromium app window loads, launcher stays alive and exits when last window closes; backend remains alive')
    finally:
        if process and process.poll() is None:
            os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=10)
        agent.close()
