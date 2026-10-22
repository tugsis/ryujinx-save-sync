#!/usr/bin/env python3
"""Headless Chrome test of bulk metadata UI; API responses are mocked, no provider requests."""
import json, os, pathlib, select, subprocess, tempfile, time
from integration import Agent

root_context = tempfile.TemporaryDirectory(prefix='ryusync-bulk-ui-')
root = pathlib.Path(root_context.name)
agent = Agent(root / 'state')
chrome = subprocess.Popen(['bash', '-c', 'exec /usr/bin/google-chrome --headless=new --no-sandbox --disable-gpu --no-first-run --disable-background-networking --remote-debugging-pipe --user-data-dir="$1" 3<&0 4>&1', 'browser', str(root / 'chrome')], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env={**os.environ, 'DBUS_SESSION_BUS_ADDRESS': 'unix:path=' + str(root / 'no-browser-bus'), 'NO_AT_BRIDGE': '1', 'GTK_USE_PORTAL': '0'})
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

 call('Page.enable', {}, session)
 call('Page.addScriptToEvaluateOnNewDocument', {'source': r"""
 const originalFetch = window.fetch;
 window.metadataCalls = [];
 window.mode = 'mixed';
 const games = ['0100000000000001', '0100000000000002', '0100000000000003', '0100000000000001'].map((titleId, i) => ({id: 'binding-' + i, titleId, title: 'Game ' + i, slot: 'player', emulatorKind: 'yuzu'}));
 window.fetch = async (input, options) => {
   const url = String(input);
   if (url === '/api/save-sets') return Response.json(games);
   if (url.startsWith('/api/game-titles/') && url.endsWith('/web')) {
     const id = url.split('/')[3];
     window.metadataCalls.push({id, body: JSON.parse(options.body)});
     await new Promise(resolve => setTimeout(resolve, window.mode === 'slow' ? 800 : 100));
     if (window.mode === 'mixed' && id.endsWith('2')) return Response.json({error:'Test provider unavailable'}, {status:409});
     const result = {titleId:id, title:id.endsWith('1') ? 'Manual name kept' : 'Web title', source:id.endsWith('1') ? 'manual' : 'web'};
     games.filter(g => g.titleId === id).forEach(g => Object.assign(g, {title:result.title,titleSource:result.source}));
     return Response.json(result);
   }
   return originalFetch(input, options);
 };
 """}, session)
 call('Page.navigate', {'url': agent.request('/ui-ticket', 'POST', {})['url']}, session)
 wait('Array.from(document.querySelectorAll("button")).some(e=>e.textContent.includes("내 게임"))')
 evaluate('Array.from(document.querySelectorAll("button")).find(e=>e.textContent.includes("내 게임")).click()')
 wait('!!document.querySelector(".bulk-game-metadata")')
 click('전체 이름·썸네일 가져오기 (3)')
 wait('document.querySelector(".bulk-game-metadata").innerText.includes("조회 완료: 3/3")')
 assert evaluate('window.metadataCalls.length') == 3
 assert evaluate('new Set(window.metadataCalls.map(c=>c.id)).size') == 3
 assert evaluate('window.metadataCalls.every(c=>c.body.language === "ko")')
 assert evaluate('document.querySelector(".bulk-game-metadata").innerText.includes("성공 2 · 실패 1")')
 assert evaluate('document.querySelector(".bulk-game-metadata details").textContent.includes("Test provider unavailable")')
 assert evaluate('document.body.innerText.includes("Manual name kept")')
 evaluate("window.mode='slow';window.metadataCalls=[]")
 evaluate("(()=>{const e=document.querySelector('.bulk-game-metadata select');e.value='en';e.dispatchEvent(new Event('change',{bubbles:true}));})()")
 click('전체 이름·썸네일 가져오기 (3)')
 wait('window.metadataCalls.length === 1')
 click('현재 조회 후 중지')
 wait('document.querySelector(".bulk-game-metadata").innerText.includes("중지됨: 1/3")')
 assert evaluate('window.metadataCalls.length') == 1
 assert evaluate('window.metadataCalls[0].body.language') == 'en'
 assert evaluate('document.querySelector(".bulk-game-metadata").innerText.includes("미처리 2개")')
 evaluate("window.mode='all';window.metadataCalls=[]")
 click('전체 이름·썸네일 가져오기 (3)')
 wait('document.querySelector(".bulk-game-metadata").innerText.includes("조회 완료: 3/3")')
 assert evaluate('document.querySelector(".bulk-game-metadata").innerText.includes("성공 3 · 실패 0")')
 call('Emulation.setDeviceMetricsOverride', {'width':390,'height':844,'deviceScaleFactor':1,'mobile':False}, session)
 assert evaluate('document.documentElement.scrollWidth <= window.innerWidth')
 print('PASS: deduplication, partial failure, continued queue, title updates, language, stop, restart and mobile layout')
finally:
 chrome.terminate()
 try: chrome.wait(timeout=5)
 except subprocess.TimeoutExpired: chrome.kill(); chrome.wait()
 agent.close()
 root_context.cleanup()
