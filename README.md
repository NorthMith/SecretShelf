# SecretShelf

Шторка у правого края экрана Windows для логинов, паролей, API-ключей, SSH-ключей —
любых записей «название + N полей». Хранилище — файл KDBX 4 (формат KeePass):
открывается также в KeePass / KeePassXC, это запасной выход.

## Запуск

```
dotnet build src/SecretShelf.App -c Release
src\SecretShelf.App\bin\Release\net8.0-windows\SecretShelf.exe
```

Нужен .NET 8 SDK. Хранилище по умолчанию: `%APPDATA%\SecretShelf\vault.kdbx`,
резервные копии — `%APPDATA%\SecretShelf\backups\` (перед перезаписью, не чаще раза в 10 мин, последние 30).

## Устройство

| Каталог | Что |
|---|---|
| `vendor/KeePassLib` | Ядро KeePass 2.61.1 (GPLv2), собранное под .NET 8. Правки — ниже |
| `src/SecretShelf.Core` | `Vault`: записи, поля, порядок, сохранение, копии |
| `src/SecretShelf.App` | WPF: шторка, язычок, буфер обмена |
| `tests/SecretShelf.Core.Tests` | xUnit: круговое сохранение, порядок, пароль, копии, замер KDF |

### Раскладка в KDBX
- запись = `PwEntry` в корневой группе; порядок записей = порядок в группе;
- значения полей — строки записи; секретные — `ProtectedString` (зашифрованы в памяти);
- первое открытое поле ложится в `UserName`, первое секретное — в `Password`
  (видно в стандартных колонках KeePass), остальные — под своими названиями;
- порядок полей, подписи и уровень доступа для ИИ — JSON в `CustomData["SecretShelf.Fields"]`.

### Шифрование
KDBX 4: AES-256, ключ из пароля — Argon2id (64 МиБ, 2 потока), итерации подбираются
при создании под ~0,4 с на этой машине. KeePassLib при каждом сохранении заново считает
Argon2 с новой солью, поэтому сохранение идёт в фоне через 0,7 с после последней правки.

### Правки в vendor/KeePassLib
- `Net8Compat/ProtectedMemoryShim.cs` — замена `ProtectedMemory` (нет в .NET 8)
  на тот же `CryptProtectMemory`, что был под ним в .NET Framework;
- `Serialization/FileTransactionEx.cs` — две строки: `File.Get/SetAccessControl` →
  `FileInfo.Get/SetAccessControl` (помечены `// SecretShelf`).

Исходный архив: `KeePass-2.61.1-Source.zip`, SHA-256
`711BD9EC707666167846 9E51BAE876DEBAC07A08C32F1B1C6B3B8FE5E761C13A` сверен с keepass.info.

## Лицензия
Из-за KeePassLib приложение — производная GPLv2: для себя без ограничений,
при распространении нужно открывать исходники.

## Этапы
1. ✅ Шторка, список с перетаскиванием, записи с N полями, пароль, копирование с очисткой буфера.
2. Отпечаток / ПИН / без пароля поверх пароля, автоблокировка, горячая клавиша, трей, импорт CSV, настройки.
3. MCP-сервер для ИИ-агентов: уровни доступа полей, подстановка без раскрытия, окно подтверждения.
