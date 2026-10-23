#!/usr/bin/env python3
"""Real file credential API/UI with an unavailable OS keyring and isolated test WebDAV."""
import http.server, json, os, pathlib, select, subprocess, tempfile, threading, time
from integration import Agent, Dav
root_context = tempfile.TemporaryDirectory(prefix='ryusync-file-ui-')
root = pathlib.Path(root_context.name)
os.environ['DBUS_SESSION_BUS_ADDRESS'] = 'unix:path=' + str(root / 'no-bus')
os.environ.pop('RYUSYNC_WEBDAV_PASSWORD', None)
Dav.root = root / 'remote'; Dav.root.mkdir()
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Dav)
threading.Thread(target=server.serve_forever, daemon=True).start()
agent = Agent(root / 'state')
settings = agent.request('/settings')
settings.update(webDavUrl=f'http://127.0.0.1:{server.server_port}/', webDavUser='test', allowInsecureHttp=True)
agent.request('/settings', 'PUT', settings)
chrome = subprocess.Popen(['bash', '-c', 'exec /usr/bin/google-chrome --headless=new --no-sandbox --disable-gpu --no-first-run --disable-background-networking --remote-debugging-pipe --user-data-dir="$1" 3<&0 4>&1', 'browser', str(root / 'chrome')], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env={**os.environ, 'NO_AT_BRIDGE': '1', 'GTK_USE_PORTAL': '0'})
seq=0;buffer=b''
def call(method,params=None,session=None):
 global seq,buffer
 seq+=1;ident=seq;message=dict(id=ident,method=method,params=params or {})
 if session:message['sessionId']=session
 chrome.stdin.write(json.dumps(message).encode()+b'\0');chrome.stdin.flush();deadline=time.time()+15
 while time.time()<deadline:
  if b'\0' not in buffer:
   if not select.select([chrome.stdout],[],[],1)[0]:continue
   chunk=os.read(chrome.stdout.fileno(),1000000)
   if not chunk:raise RuntimeError('Chrome pipe closed')
   buffer+=chunk
  while b'\0' in buffer:
   raw,buffer=buffer.split(b'\0',1)
   if not raw:continue
   reply=json.loads(raw)
   if reply.get('id')==ident:
    assert 'error' not in reply,reply
    return reply.get('result',{})
 raise RuntimeError('CDP timeout: '+method)
try:
 target=call('Target.createTarget',{'url':'about:blank'});session=call('Target.attachToTarget',{'targetId':target['targetId'],'flatten':True})['sessionId']
 call('Emulation.setDeviceMetricsOverride',{'width':1280,'height':900,'deviceScaleFactor':1,'mobile':False},session)
 def evaluate(expression):
  result=call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True},session)
  assert 'exceptionDetails' not in result,result.get('exceptionDetails')
  return result['result'].get('value')
 def wait(expression):
  for _ in range(150):
   if evaluate(expression):return
   time.sleep(.1)
  raise AssertionError('UI timeout: '+expression+'\n'+str(evaluate('document.body.innerText')))
 def click(text):
  evaluate('Array.from(document.querySelectorAll("button")).find(e=>e.textContent.trim()==='+json.dumps(text)+').click()');time.sleep(.15)

 call('Page.navigate', {'url':agent.request('/ui-ticket','POST',{})['url']}, session)
 wait('document.querySelector(".credential-settings") && document.querySelector(".credential-settings").innerText.includes("이용 불가")')
 evaluate("(()=>{const e=document.querySelector('.credential-settings select');e.value='file';e.dispatchEvent(new Event('change',{bubbles:true}));})()")
 time.sleep(.15)
 evaluate("(()=>{const e=document.querySelector('.credential-settings input[type=password]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(e,'password');e.dispatchEvent(new Event('input',{bubbles:true}));})()")
 time.sleep(.15)
 evaluate('document.querySelector(".credential-settings input[type=checkbox]").click()')
 time.sleep(.15)
 click('연결 확인 후 적용')
 wait('document.querySelector(".credential-state").innerText.includes("파일에 저장된 비밀번호 사용")')
 assert evaluate('document.querySelector(".credential-settings input[type=password]").value') == ''
 files=list((root/'state'/'credentials-plaintext').glob('*.json'))
 assert len(files)==1 and json.loads(files[0].read_text())['Password']=='password'
 assert files[0].stat().st_mode & 0o777 == 0o600
 agent.close(); agent=Agent(root/'state')
 status=agent.request('/storage/credential')
 assert status['ready'] and status['source']=='file', status
 agent.request('/storage/test','POST',{})
 call('Page.navigate', {'url':agent.request('/ui-ticket','POST',{})['url']}, session)
 wait('document.querySelector(".credential-state") && document.querySelector(".credential-state").innerText.includes("파일에 저장된 비밀번호 사용")')
 click('현재 세션 잠금')
 wait('document.querySelector(".credential-state").innerText.includes("현재 세션 잠김")')
 click('저장된 비밀번호 사용')
 wait('document.querySelector(".credential-state").innerText.includes("파일에 저장된 비밀번호 사용")')
 evaluate('window.confirm=()=>true')
 click('저장된 비밀번호 삭제')
 wait('document.querySelector(".credential-state").innerText.includes("기기 저장값 없음")')
 assert not files[0].exists()
 agent.close(); agent=Agent(root/'state')
 assert not agent.request('/storage/credential')['ready']
 print('PASS: unavailable OS store, UI file opt-in, restart, WebDAV authentication, lock, reload, deletion and permissions')
finally:
 chrome.terminate()
 try: chrome.wait(timeout=5)
 except subprocess.TimeoutExpired: chrome.kill(); chrome.wait()
 agent.close(); server.shutdown(); server.server_close(); root_context.cleanup()
