#!/usr/bin/env python3
"""Headless Chrome test of Deck UI; API responses are mocked, no provider requests."""
import base64, json, os, pathlib, select, subprocess, tempfile, time
from integration import Agent

root_context = tempfile.TemporaryDirectory(prefix='ryusync-deck-ui-')
root = pathlib.Path(root_context.name)
agent = Agent(root / 'state')
settings = agent.request('/settings'); settings.update(deviceName='Steam Deck', webDavUrl='https://nas.example.com/webdav/', webDavUser='save-sync', workspace='my-games'); agent.request('/settings','PUT',settings)
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
 call('Emulation.setDeviceMetricsOverride',{'width':1280,'height':800,'deviceScaleFactor':1,'mobile':False},session)
 def evaluate(expression):
  result=call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True},session)
  assert 'exceptionDetails' not in result,result.get('exceptionDetails')
  return result['result'].get('value')
 def screenshot(name):
  destination=os.environ.get('RYUSYNC_UI_SCREENSHOTS')
  if destination:
   folder=pathlib.Path(destination);folder.mkdir(parents=True,exist_ok=True)
   (folder/name).write_bytes(base64.b64decode(call('Page.captureScreenshot',{'format':'png'},session)['data']))
 def wait(expression):
  for _ in range(150):
   if evaluate(expression):return
   time.sleep(.1)
  raise AssertionError('UI timeout: '+expression+'\n'+str(evaluate('document.body.innerText')))
 def click(text):
  evaluate('Array.from(document.querySelectorAll("button")).find(e=>e.textContent.trim()==='+json.dumps(text)+').click()');time.sleep(.15)

 call('Page.enable', {}, session)
 call('Page.addScriptToEvaluateOnNewDocument', {'source': r"""
 const original=window.fetch;
 window.testBindings=[{id:'one',titleId:'0100000000000001',title:'숲의 여행자',slot:'player-1',emulatorKind:'ryujinx',installationId:'legacy',enabled:true},{id:'two',titleId:'0100000000000002',title:'별빛 항해',slot:'player-2',emulatorKind:'yuzu',installationId:'deck-yuzu',enabled:true}];
 window.testJobs=[];window.requests=[];window.playing=false;
 window.fetch=async(input,options)=>{
 const url=String(input);
 if(url==='/api/status'){const r=await original(input,options);const s=await r.json();return Response.json({...s,credentialUnlocked:true,launch:window.playing?{id:'play',status:'playing',message:'게임 실행 중'}:null});}
 if(url==='/api/save-sets')return Response.json(window.testBindings);
 if(url==='/api/jobs')return Response.json(window.testJobs);
 if(url==='/api/sync/jobs' || url.startsWith('/api/conflicts/')){window.requests.push({url,body:JSON.parse(options.body)});return Response.json({id:'job'});}
 return original(input,options);
 };
 """}, session)
 url=agent.request('/ui-ticket','POST',{})['url'].replace('/#','/?ui=deck#')
 call('Page.navigate',{'url':url},session)
 wait('!!document.querySelector(".deck-app")')
 assert evaluate('document.body.innerText.includes("아직 확인하지 않았어요")')
 assert evaluate('document.documentElement.scrollWidth <= innerWidth')
 assert evaluate('document.querySelector(".deck-footer").getBoundingClientRect().bottom <= innerHeight')
 screenshot('implemented-sync.png')
 click('전체 동기화')
 wait('window.requests.length===1')
 assert evaluate('window.requests[0].url')=='/api/sync/jobs'
 click('설정')
 wait('!!document.querySelector(".deck-settings-nav")')
 screenshot('implemented-settings.png')
 evaluate("(()=>{const e=Array.from(document.querySelectorAll('.deck-app label')).find(e=>e.textContent.includes('기기 이름')).querySelector('input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(e,'Deck test');e.dispatchEvent(new Event('input',{bubbles:true}));})()")
 time.sleep(.15); click('설정 저장')
 wait('!document.body.innerText.includes("저장하지 않은 변경")')
 assert agent.request('/settings')['deviceName']=='Deck test'
 evaluate('Array.from(document.querySelectorAll(".deck-settings-nav button")).find(e=>e.textContent.includes("비밀번호")).click()')
 wait('!!document.querySelector(".credential-settings")')
 assert evaluate('Array.from(document.querySelectorAll(".credential-settings option")).some(e=>e.value==="file")')
 click('동기화')
 evaluate("window.testJobs=[{id:'conflict-job',status:'conflict',source:'web',message:'충돌',createdAt:new Date().toISOString(),results:[{bindingId:'one',status:'conflict',message:'서로 다른 진행',localHash:'hash',heads:['remote'],versions:[{id:'remote',deviceName:'Windows PC',createdAt:new Date().toISOString(),sourceEmulator:'ryujinx'}]}]}]")
 wait('Array.from(document.querySelectorAll("button")).some(e=>e.textContent==="버전 확인")')
 click('버전 확인')
 wait('!!document.querySelector("[role=dialog]")')
 screenshot('implemented-conflict.png')
 assert evaluate('document.querySelector(".deck-app").inert')
 assert evaluate('document.querySelector("[role=dialog]").contains(document.activeElement)')
 evaluate('Array.from(document.querySelectorAll(".version-button")).find(e=>e.textContent.includes("이 기기")).click()')
 time.sleep(.15)
 assert evaluate('window.requests.length')==1, 'selection applied without confirmation'
 click('선택한 버전 적용')
 wait('window.requests.length===2')
 assert evaluate('window.requests[1].body.expectedHeads[0]')=='remote'
 assert evaluate('window.requests[1].body.choice')=='local'
 assert evaluate('!document.querySelector("[role=dialog]")')
 evaluate('window.testJobs=[];window.playing=true')
 wait('Array.from(document.querySelectorAll("button")).some(e=>e.textContent==="게임 종료 후 전체 동기화 예약")')
 click('게임 종료 후 전체 동기화 예약')
 wait('window.requests.length===3')
 click('기존 화면')
 wait('!!document.querySelector(".sidebar")')
 assert evaluate('document.documentElement.dataset.ui')=='desktop'
 click('Deck 화면')
 wait('!!document.querySelector(".deck-app")')
 evaluate('window.playing=false;window.testBindings=[]')
 wait('document.body.innerText.includes("아직 등록된 게임이 없어요")')
 click('설치본 설정·게임 탐색')
 wait('document.body.innerText.includes("Ryujinx 최상위 데이터 폴더")')
 call('Emulation.setDeviceMetricsOverride',{'width':1280,'height':720,'deviceScaleFactor':1,'mobile':False},session)
 assert evaluate('document.documentElement.scrollWidth <= innerWidth')
 assert evaluate('document.querySelector(".deck-footer").getBoundingClientRect().bottom <= innerHeight')
 # Query-free navigation remembers the user's layout, without storing any auth token.
 assert evaluate('localStorage.getItem("ryusync-ui")')=='deck'
 assert evaluate('Object.values(localStorage).every(v=>!v.includes("token"))')
 print('PASS: Deck ticket, sync, settings persistence, credentials, conflict confirmation, queued sync, desktop toggle, empty state and 1280x720 layout')
finally:
 chrome.terminate()
 try:chrome.wait(timeout=5)
 except subprocess.TimeoutExpired:chrome.kill();chrome.wait()
 agent.close();root_context.cleanup()
