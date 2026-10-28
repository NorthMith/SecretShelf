"""Bounded stdio MCP check. Never fetches values or writes the real vault."""
import json,subprocess,threading,queue,time,pathlib,sys
ROOT=pathlib.Path(__file__).parent
EXE='D:/Projects/SecretShelf/dist/mcp/SecretShelf.Mcp.exe'
p=subprocess.Popen([EXE],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue()
def reader():
 for line in p.stdout:
  try:messages.put(json.loads(line))
  except ValueError:messages.put({'invalid_json':True})
threading.Thread(target=reader,daemon=True).start()
def send(data):p.stdin.write(json.dumps(data)+'\n');p.stdin.flush()
def request(i,method,params):
 send({'jsonrpc':'2.0','id':i,'method':method,'params':params});end=time.monotonic()+15
 while time.monotonic()<end:
  item=messages.get(timeout=max(.1,end-time.monotonic()))
  if item.get('id')==i:
   assert 'error' not in item,'mcp_error';return item['result']
 raise TimeoutError('mcp_timeout')
try:
 init=request(1,'initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'Codex-SecretShelf-setup-check','version':'1.0'}})
 send({'jsonrpc':'2.0','method':'notifications/initialized'})
 listing=request(2,'tools/list',{})
 tools=sorted(x['name'] for x in listing['tools'])
 expected=sorted(['list_secrets','get_secret_value','run_with_secrets','write_secret_file','store_secret','delete_secret','scan_for_secrets'])
 assert tools==expected,'unexpected_tool_catalog'
 result=request(3,'tools/call',{'name':'list_secrets','arguments':{}})
 text='\n'.join(x.get('text','') for x in result.get('content',[]) if x.get('type')=='text')
 # List only returns entry/field names and access, never values. Keep even those
 # names out of tool output during setup; report availability and counts only.
 denied=text.startswith('Отказ SecretShelf:')
 status='available'
 if denied:
  if 'заблокировано' in text:status='vault_locked'
  elif 'не запущен' in text:status='app_not_running'
  elif 'выключен' in text:status='agent_access_disabled'
  else:status='denied'
 receipt={'initialize':True,'protocol':init.get('protocolVersion'),'server':init.get('serverInfo',{}),'tool_count':len(tools),'tools':tools,'list_secrets_status':status,'returned_values':False,'vault_modified':False}
 (ROOT/'mcp-probe-result.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps(receipt,ensure_ascii=False))
finally:
 p.stdin.close()
 try:p.wait(timeout=5)
 except subprocess.TimeoutExpired:
  p.terminate()
  try:p.wait(timeout=3)
  except subprocess.TimeoutExpired:p.kill();p.wait()
