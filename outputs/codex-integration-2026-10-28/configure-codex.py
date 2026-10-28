"""Owner-authorized local rules/MCP setup. No vault or credential values are read."""
import pathlib,json,tomllib,hashlib,datetime,shutil,os,ctypes
TASK=pathlib.Path(__file__).parent
HOME=pathlib.Path('C:/Users/Главбух/.codex')
AGENTS=HOME/'AGENTS.md';FULL=HOME/'owner-rules-reference-2026-10-01.md';CONFIG=HOME/'config.toml'
SOURCE=pathlib.Path('D:/Projects/SecretShelf/docs/agents/secretshelf/SKILL.md')
DEST=HOME/'skills/secretshelf/SKILL.md'
STAMP=datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=3))).strftime('%Y-%m-%d-%H%M%S-msk')
section='''## Секреты на этой машине — через SecretShelf (28.10.2026)

- Локальные сессии Codex используют пароли, API-токены и SSH-ключи владельца
  только через MCP `secretshelf` — программу SecretShelf в `D:\\Projects\\SecretShelf`.
  Процедура: `C:\\Users\\Главбух\\.codex\\skills\\secretshelf\\SKILL.md`.
- Сначала `list_secrets`, затем ссылки «Запись / Поле»: `run_with_secrets`
  для окружения/stdin или `write_secret_file` для шаблона `{{Запись/Поле}}`.
  Не читать значения из `.env`, SSH-ключей и конфигов напрямую, в том числе
  отдельным обходным скриптом. Секреты не попадают в модель, вывод и аргументы.
  `get_secret_value` допустим только для заведомо несекретных полей «Открыто»
  (например, логин или адрес сервера); секретные значения не выводить.
- Новые ключи по разрешённой задаче сразу сохранять через `store_secret`
  из файла или вывода программы. Старые источники переносить через
  `scan_for_secrets` и `store_secret` только в согласованном объёме;
  сплошной поиск и удаление старых файлов этим правилом не разрешаются.
- Отказ, закрытое поле, недостаточный уровень или запрет программы не обходить.
  Объяснить, какое поле и какой уровень нужен: «⋯» → «Доступ для ИИ».
  Если шторка не запущена/заперта — попросить владельца открыть её;
  если MCP недоступен — сообщить об этом, не искать замену на диске.
- Учётные данные ИИ-клиентов (`auth.json`, `.credentials.json` и подобные)
  не читать и не переносить. Уровни доступа и подтверждения SecretShelf сохраняются.
- Это правило регулирует работу локальных ИИ с секретами. Оно само по себе
  не переносит и не меняет серверную базу NorthSafe, бота и его выдачу владельцу.
  Учёт уже согласованных получателей и защищённой передачи сохраняется.

'''
short_naming='''## Имена скиллов (28.10.2026)

- Имя папки и `name:` совпадают: латиница, нижний регистр, слова через дефис.
  Внутри остаётся стандартное имя `SKILL.md`.
- Имя называет предмет/задачу; без общих `skill`, `helper`, `tool`, `utils`,
  `misc`, `my-`, `new-` и произвольных номеров. Для проекта — префикс проекта.
- `description:` описывает действие и поводы срабатывания, включая слова
  владельца без названия скилла. Перед переименованием проверить ссылки
  и согласовать затронутые зависимости; сейчас существующие скиллы не переименовывать.

'''
full_naming='''### Имена скиллов (правило от 28.10.2026)

Скилл различается по имени каталога и полю `name:`. Сам файл внутри всегда
называется `SKILL.md`, переименовывать его нельзя.

- Имя называет предмет или задачу: `secretshelf`, `fpv-build-assist`,
  `godot-ui-control`. Имя и описание помогают модели выбрать скилл.
- Не использовать неразличающие `skill`, `helper`, `tool`, `utils`, `misc`,
  префиксы `my-`, `new-` и произвольные номера вроде `skill-1`.
- Скилл конкретного проекта начинается с имени проекта.
- Латиница, нижний регистр, слова через дефис; каталог совпадает с `name:`.
- `description:` включает назначение и поводы для срабатывания, в том числе
  слова владельца, когда он не называет скилл явно.
- Перед переименованием искать ссылки в сводах, инструкциях, других скиллах
  и скриптах и менять их согласованно. Само добавление этого правила
  не разрешает автоматически переименовывать существующие скиллы.

'''
originals={p:p.read_bytes() for p in [AGENTS,FULL,CONFIG]}
a=originals[AGENTS].decode('utf-8');f=originals[FULL].decode('utf-8');c=originals[CONFIG].decode('utf-8')
assert '## Секреты на этой машине — через SecretShelf (28.10.2026)' not in a+f,'rules_already_updated_audit_first'
assert a.count('## Правки, проверка и Git')==1 and f.count('## Сплошной обход каталогов')==1
updated_a=a.replace('## Правки, проверка и Git',section+'## Правки, проверка и Git',1)
assert updated_a.count('## Железо и схемы')==1
updated_a=updated_a.replace('## Железо и схемы',short_naming+'## Железо и схемы',1)
updated_f=f.replace('## Сплошной обход каталогов',section+'## Сплошной обход каталогов',1)
assert updated_f.count('## Общая папка share на Стоике (2026-10-11)')==1
updated_f=updated_f.replace('## Общая папка share на Стоике (2026-10-11)',full_naming+'## Общая папка share на Стоике (2026-10-11)',1)
before=tomllib.loads(c)
assert 'secretshelf' not in before.get('mcp_servers',{}),'already_registered_audit_first'
assert not DEST.exists(),'skill_already_present_audit_first'
addition='''

# Owner-approved local SecretShelf integration, 2026-10-28.
[mcp_servers.secretshelf]
command = "D:/Projects/SecretShelf/dist/mcp/SecretShelf.Mcp.exe"
cwd = "D:/Projects/SecretShelf"
enabled = true
startup_timeout_sec = 15
tool_timeout_sec = 90
'''
updated_c=c.rstrip()+addition
after=tomllib.loads(updated_c)
without=json.loads(json.dumps(after));without['mcp_servers'].pop('secretshelf')
assert without==before,'unrelated_config_change'
assert updated_a.replace(section,'',1).replace(short_naming,'',1)==a
assert updated_f.replace(section,'',1).replace(full_naming,'',1)==f
# DPAPI protects the complete configuration backup even if another MCP happens
# to carry sensitive configuration. The backup never enters this task's output.
class Blob(ctypes.Structure):_fields_=[('size',ctypes.c_uint32),('data',ctypes.POINTER(ctypes.c_ubyte))]
def protect(data,encrypt=True):
 buf=ctypes.create_string_buffer(data);src=Blob(len(data),ctypes.cast(buf,ctypes.POINTER(ctypes.c_ubyte)));dst=Blob()
 lib=ctypes.windll.crypt32
 if encrypt:ok=lib.CryptProtectData(ctypes.byref(src),None,None,None,None,1,ctypes.byref(dst))
 else:ok=lib.CryptUnprotectData(ctypes.byref(src),None,None,None,None,1,ctypes.byref(dst))
 if not ok:raise ctypes.WinError()
 try:return ctypes.string_at(dst.data,dst.size)
 finally:ctypes.windll.kernel32.LocalFree(ctypes.cast(dst.data,ctypes.c_void_p))
backups=[]
for p,data in originals.items():
 assert p.read_bytes()==data,'concurrent_edit_detected'
 target=p.with_name(p.name+'.bak-'+STAMP+('.dpapi' if p==CONFIG else ''))
 assert not target.exists()
 if p==CONFIG:
  sealed=protect(data);assert protect(sealed,False)==data
  with target.open('xb') as out:out.write(sealed)
 else:shutil.copy2(p,target)
 backups.append(str(target))
for p,text in [(AGENTS,updated_a),(FULL,updated_f),(CONFIG,updated_c)]:
 assert p.read_bytes()==originals[p],'concurrent_edit_detected'
 with p.open('wb') as out:out.write(text.encode('utf-8'))
 assert p.read_text(encoding='utf-8')==text
DEST.parent.mkdir(parents=True,exist_ok=False);shutil.copy2(SOURCE,DEST)
assert DEST.read_bytes()==SOURCE.read_bytes()
assert tomllib.loads(CONFIG.read_text(encoding='utf-8'))==after
receipt={'rules_updated':[str(AGENTS),str(FULL)],'backups':backups,'config_backup':'DPAPI CurrentUser; round-trip verified','mcp':'secretshelf','command':after['mcp_servers']['secretshelf']['command'],'other_settings_preserved':True,'skill':str(DEST),'skill_sha256':hashlib.sha256(DEST.read_bytes()).hexdigest()}
(TASK/'configuration-result.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(receipt,ensure_ascii=False))
