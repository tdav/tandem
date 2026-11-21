# Tandem, этап 3 (инструменты и сеть) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Добавить к двухпанельному менеджеру инструменты (групповое переименование с отменой, сравнение и синхронизация папок, сравнение файлов, контрольные суммы, размеры папок) и сеть (FTP, FTPS, SFTP как источник в панели, менеджер подключений с паролями в связке ключей, правка файла на сервере через временную копию, переподключение после обрыва).

**Architecture:** Сервер — такой же источник за `IFileSystemProvider`, как архив: один класс `RemoteProvider` в новом проекте `Tandem.Net` работает поверх узкого шва `IRemoteSession`, у которого две реализации — `FtpSession` (FluentFTP) и `SftpSession` (SSH.NET); панели, `FileCommands` и движок операций о серверах не знают и узнают о них только через возможности источника и типы исключений ядра. Инструменты — чистая логика в `Tandem.Core/Tools` плюс три новые операции движка (`BatchRenameOperation`, `SyncOperation`, операции контрольных сумм), поэтому они работают на любом источнике, включая сервер. Этап выполняется параллельно: короткая общая подготовка в `main`, затем три независимые рабочие копии (инструменты-А, инструменты-Б, сеть), слияние, общая проверка совместной работы и сборка 0.3.0.

**Tech Stack:** C#, .NET 11 (`net11.0`), Avalonia 12.1.6, CommunityToolkit.Mvvm 8.4.2, SharpCompress 1.0.0, Avalonia.AvaloniaEdit 12.0.0, **SSH.NET 2026.0.1**, **FluentFTP 55.1.0**, **System.IO.Hashing 11.0.0**, **DiffPlex 1.9.0**, xunit.v3 3.2.2, Avalonia.Headless.XUnit 12.1.6.

**Spec:** `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md` — исполнитель читает её вместе с планом. Этот план покрывает группы F и G (раздел 6), строку «Групповое переименование» раздела 7.2, боковую панель с сетевыми подключениями (7.1), хранение подключений и паролей (5.5) и критерий этапа 3 раздела 8. Что построено на деле: `docs/superpowers/2026-11-19-stage-1-decisions.md`, `docs/superpowers/2026-11-19-stage-2-decisions.md` и сам код (при расхождении с планами прежних этапов прав код).

**Модели:** план написан Fable 5.2. Реализацию и проверку каждой задачи выполняют подагенты на Opus 5.5 (`model: "opus"`), по одной задаче, после каждой — проверяющий.

## Global Constraints

Действуют из этапов 1 и 2 (сверено с кодом):

- `TargetFramework` = `net11.0`; сборка только `osx-arm64`; `LSMinimumSystemVersion` = `14.0`. `Nullable` включён, `TreatWarningsAsErrors` = `true`. Версии пакетов — только в `Directory.Packages.props`.
- `Tandem.Core` не ссылается на Avalonia и не вызывает системные библиотеки macOS. Платформа — через интерфейсы `Tandem.Core.Platform`, реализованные в `Tandem.Platform.Mac` прямыми вызовами системных библиотек (`LibraryImport`, как в `Interop/LibC.cs` и `Interop/ObjC.cs`); готовые библиотеки привязок к Objective-C не подключать.
- Все обращения локального источника к диску идут через `IFileAccessBroker`. Исключение — собственные папки приложения: `~/Library/Application Support/Tandem/`, `~/Library/Caches/Tandem/`, `~/Library/Logs/Tandem/`.
- Никакой молчаливой перезаписи. Файл пишется во временное имя `.{имя}.tandem-part` рядом с целью и встаёт на место через `SafeReplace`; заменяемый файл откладывается как `.{имя}.tandem-old` и удаляется после успеха. Прерванная операция не оставляет испорченного файла под настоящим именем.
- Перенос между томами и источниками: копирование → проверка → удаление источника только для успешно скопированных элементов.
- Файловые операции, чтение папок, сравнение, подсчёт сумм и размеров, любые сетевые вызовы не выполняются в потоке интерфейса. Модели представления создаются и вызываются в потоке интерфейса, тяжёлое уводят в `Task.Run`; их тесты — `[AvaloniaFact]`.
- Ядро и `Tandem.Net` не содержат текстов для пользователя: отдают коды и типы исключений, приложение переводит (`OperationTexts`, `Loc.Enum`). Строки интерфейса — только в `Resources/Strings.resx` (английский) и `Strings.ru.resx` (русский), с одинаковым набором ключей; не в коде и не в разметке.
- Цвета — только в `App.axaml` (`ThemeDictionaries`, светлый и тёмный вариант каждого ключа); представления ссылаются через `DynamicResource`. Никаких цветов в коде и разметке окон.
- Каждая новая команда: константа в `CommandIds`, подпись `Menu_<id с '_' вместо '.'>` в обоих файлах ресурсов, сочетание по умолчанию в `KeyMap.Default`. Запись сочетаний — как в существующей таблице (`Cmd+Shift+R`, `Ctrl+Cmd+1`).
- Название, иконки и оформление Total Commander не используются.
- Код, не являющийся разметкой, пишется через тест: сначала падающий тест, затем реализация.
- API Avalonia 12, SSH.NET 2026, FluentFTP 55, DiffPlex сверять с документацией (Context7, сайты проектов) и с самой сборкой, а не по памяти.
- Коммит после каждой задачи; сообщение на английском, заканчивается строкой `Co-Authored-By` той модели, которая сделала коммит (для Opus 5.5 — `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`). Не пушить.

Новые для этапа 3:

- Новые пакеты (и только они): `SSH.NET` 2026.0.1 и `FluentFTP` 55.1.0 — только в `Tandem.Net`; `System.IO.Hashing` 11.0.0 и `DiffPlex` 1.9.0 — только в `Tandem.Core` (оба чисто управляемые, без кода платформы). Библиотеки приходят только через `dotnet restore` объявленных пакетов (вместе с их зависимостями); ничего другого не скачивается — ни образов серверов, ни программ, ни тестовых данных.
- Новый проект `src/Tandem.Net` зависит только от `Tandem.Core`, SSH.NET и FluentFTP; `Tandem.App` ссылается на него. Новый тестовый проект `tests/Tandem.Net.Tests`.
- **Серверы — источники, как архивы.** В `PanelViewModel`, `SideViewModel`, `FileCommands`, `ShellCommands`, `Tandem.Core/Operations` нет проверок «это сервер», имён типов из `Tandem.Net` и строк `"net:"`: только `ProviderCapabilities`, `IHostedProvider`, `VfsConnectionLostException` и прочие типы ядра. Знать о подключениях могут только `NetCommands`, диалоги подключений и `Composition`.
- **Тесты не трогают систему.** Запрещено: менять системные настройки (в том числе включать «Удалённый вход»), использовать Docker, скачивать что-либо, обращаться к настоящей связке ключей, к настоящей домашней папке и `~/Library`, к адресам вне `127.0.0.1`. Сетевые тесты слушают только `127.0.0.1` на порту, выданном системой (порт 0). Всё, что тест пишет на диск, лежит во временной папке (`TempDir`). Проверки с настоящим сервером и настоящей связкой ключей включаются только переменными окружения (раздел «Что проверяется автоматически») и без них сообщают `Skipped` с причиной.
- Пароли и ключевые фразы: только в связке ключей (через `ISecretStore`) и в памяти на время работы. Никогда — в `connections.json`, `settings.json`, `state.json`, журнале, временной папке, тексте исключений, заголовках окон и строке пути (`sftp://user@host/…` без пароля).
- Все передачи по FTP — двоичные (`TYPE I`). Подключение FTPS никогда молча не понижается до открытого FTP. Ключ узла SFTP и сертификат FTPS, не прошедший системную проверку, подтверждает пользователь; подтверждённые отпечатки хранятся в `known-hosts.json` (не секрет).
- Каждый сетевой вызов имеет предел времени (подключение 15 с, чтение/запись 30 с без движения данных) и отменяется `CancellationToken`; ни один вызов не ждёт бесконечно.
- Новые файлы настроек — `connections.json` и `known-hosts.json` (версия 1) в `~/Library/Application Support/Tandem/`; `state.json` и `settings.json` не меняют формат и читаются сборкой 0.2.0.
- Журнал отмены переименования живёт в памяти до выхода из приложения.

## Review Focus

Места, где ошибка теряет данные пользователя или выдаёт его секреты; у каждого — тесты в названных задачах.

1. **Связь оборвалась посреди записи на сервер** (F5 на сервер, выгрузка после F4, синхронизация) — под настоящим именем на сервере остаётся прежний файл либо ничего, но не обрубок; «Переподключиться и повторить» дописывает файл целиком; после успеха нет `.tandem-part` и `.tandem-old`; ошибка сервера при закрытии потока записи (диск полон) не теряется (тесты в задачах 14, 16, 24).
2. **Сервер молча заменяет файл при переименовании** (FTP `RNTO` на многих серверах) или сервер простоял без дела и закрыл соединение — `MoveAsync` источника никогда не заменяет существующее имя (проверка перед переименованием, `VfsAlreadyExistsException`); `SafeReplace` при сбое между двумя переименованиями возвращает старый файл; первое действие после простоя переподключается само, без сообщения об ошибке (тесты в задачах 14, 16).
3. **Синхронизация после того, как файлы изменились** (между сравнением и запуском пользователь или другая программа поменяла, удалила или создала файл) — перезаписывается и удаляется только то, что осталось таким же, как при сравнении, остальное пропускается и попадает в сводку; по умолчанию ни один элемент не удаляется; подтверждение называет число удалений и говорит «в Корзину» или «безвозвратно»; нечитаемая подпапка не превращает её содержимое на другой стороне в «лишнее» (тесты в задачах 11, 12, 13).
4. **Групповое переименование с пересечением имён** (обмен `a↔b`, цепочка `a→b→c`, смена только регистра, имя занято посторонним файлом, отмена посреди работы) — ни один файл не затирается и не пропадает; прерванное переименование не оставляет файлов под служебными именами; отмена возвращает только те файлы, которые не изменились и чьё прежнее имя свободно (тесты в задачах 4, 5).
5. **Правка файла на сервере** — сохранение пишет только во временную копию; на сервер файл уходит лишь после ответа «да»; если файл на сервере изменился после скачивания — отдельный вопрос; при сбое выгрузки правки остаются в редакторе; окно и приложение не закрываются с невыгруженной копией без вопроса (тесты в задаче 19, сквозной — в задаче 24).
6. **Пароль утекает в файл или на экран** — после сохранения подключения с паролем текст пароля не встречается ни в одном файле папки настроек, ни в журнале, ни в сообщениях об ошибках подключения; FTPS без TLS на стороне сервера даёт ошибку, а не вход открытым текстом (команда `PASS` не отправляется) (тесты в задачах 16, 20, 21).
7. **Имена в разных формах Юникода** (macOS отдаёт «й» в разложенном виде, сервер и архив — в составном) — сравнение папок и синхронизация считают такие имена одним файлом, а не «уникальным слева» и «уникальным справа» (иначе синхронизация создаст дубликаты) (тесты в задачах 8, 11, 24).

## Параллельное выполнение

Заказчик выполняет этап несколькими параллельными подагентами. План разбит на четыре фазы.

| Фаза | Где | Задачи | Правило |
|---|---|---|---|
| A. Подготовка | ветка `main`, по порядку | 1 → 2 → 3 | Создаёт всё общее: договоры ядра, пакеты и проекты, идентификаторы команд, сочетания, меню, ключи ресурсов, заготовки классов. После неё общие файлы заморожены |
| B. Направления | три рабочие копии от `main` после задачи 3 | **tools-a:** 4 → 5, 6, 7 · **tools-b:** 8, 9 → 10, 11 → 12 → 13 · **net:** 14 → {15 → 16} ∥ {17} ∥ {18 → 19 → 20 → 21} → 22, 23 | Направления не зависят друг от друга и не трогают чужие файлы |
| C. Совместная работа | `main` после слияния трёх направлений | 24, 25 | Сквозные сценарии «архив → сервер», «синхронизация диск ↔ сервер», «F4 на сервере» |
| D. Завершение | `main` | 26 | Tandem.app 0.3.0, список приёмки, README |

Направление **net** внутри себя делится ещё на три ветви, которые после задачи 14 можно вести одновременно: **net-ftp** (15, 16), **net-sftp** (17), **net-app** (18, 19, 20, 21). Задачи 15, 18 и 19 от задачи 14 не зависят и могут начаться сразу после фазы A.

Зависимости каждой задачи указаны в её заголовке строкой **Направление / Зависит от**.

**Кто какие файлы трогает в фазе B** (всё, чего в таблице нет, принадлежит задаче, которая это создаёт):

| Файлы | tools-a | tools-b | net |
|---|---|---|---|
| `src/Tandem.Core/Tools/Rename/**`, `Tools/Checksums/**`, `Tools/FolderSizeCalculator.cs` | да | — | — |
| `src/Tandem.Core/Tools/Compare/**`, `Tools/Sync/**` | — | да | — |
| `src/Tandem.Net/**`, `tests/Tandem.Net.Tests/**`, `src/Tandem.Platform.Mac/**` | — | — | да |
| `ViewModels/Tools/ToolsCommands.Rename.cs`, `.Checksums.cs`, `.FolderSizes.cs`; `Services/ToolWindows.Rename.cs`, `.Checksums.cs` | да | — | — |
| `ViewModels/Tools/ToolsCommands.CompareDirs.cs`, `.CompareFiles.cs`, `.Sync.cs`; `Services/ToolWindows.Compare.cs`, `.Sync.cs` | — | да | — |
| `PanelViewModel*.cs`, `EntryRowViewModel.cs` | да (задача 7) | — | — |
| `Views/Dialogs/ConflictDialog.cs` (диалог конфликта; задача 3 выносит его в свой файл), `App.axaml` | — | да (задача 10) | — |
| `ViewModels/Net/**`, `Views/Net/**`, `Services/INetDialogs.cs`, `NetDialogs.cs`, `ShellCommands.cs`, `Editing/**`, `SidebarView.axaml*`, `Composition.cs` (в том числе запись `AppServices`), `IDialogService.cs`, `DialogService.cs`, `Views/Dialogs/ChoiceDialogs.cs`, `tests/…/Views/TestApp.cs`, `ScriptedDialogs.cs`, `Tandem.App.Tests.csproj` | — | — | да |
| `Resources/Strings.resx`, `Strings.ru.resx` | свой раздел | свой раздел | свой раздел |

**Общие файлы с правилом «только добавлять».** Единственные файлы, которые в фазе B правят все три направления, — два файла ресурсов. Задача 3 ставит в конце каждого из них три раздела, разделённые неизменяемыми строками-комментариями:

```xml
  <!-- stage3:tools-a begin (Rename_*, Checksum_*, FolderSize_*) -->
  <!-- stage3:tools-a end -->
  <!-- ======================================================== -->
  <!-- stage3:tools-b begin (CompareDirs_*, Compare_*, Sync_*) -->
  <!-- stage3:tools-b end -->
  <!-- ======================================================== -->
  <!-- stage3:net begin (Net_*, Connection_*, RemoteEdit_*) -->
  <!-- stage3:net end -->
```

Направление добавляет ключи только между своими `begin` и `end`, только со своими приставками, и не трогает ни одной строки вне своего раздела. Всё остальное общее (`CommandIds`, `KeyMap`, `MainMenu`, `MainViewModel`, `IToolWindows`, `ToolsCommands.cs`, `ToolWindows.cs`, `Directory.Packages.props`, `Tandem.slnx`, перечисления `OperationKind` и `SkipReason`, `OperationTexts`, тесты полного списка команд и сочетаний) после задачи 3 **в фазе B не меняется никем**. Если направлению не хватает чего-то в замороженном файле — остановиться и доложить, а не править.

**Слияние.** Направления сливаются в `main` по одному (tools-a, tools-b, net; порядок не важен); после каждого слияния — `dotnet build Tandem.slnx` без предупреждений и `dotnet test Tandem.slnx` полностью. Конфликт слияния в любом файле, кроме двух файлов ресурсов, означает нарушение таблицы выше: разобраться, какое направление вышло за свои файлы.

## Сочетания по умолчанию, закреплённые за направлениями

Сверено с разделом 7.2 спецификации, с текущим `KeyMap.Default` и со списком запрещённых системных сочетаний `KeyChordRules`: ни одно из сочетаний ниже не занято и не запрещено; сочетаний Ctrl+F-клавиша нет; у каждой команды с F-клавишей есть второе сочетание без неё. Все сочетания добавляет задача 3; направления их только используют.

| Направление | Id | Сочетания | Задача-владелец команды |
|---|---|---|---|
| tools-a | `tools.multiRename` | Cmd+M (спецификация, 7.2), Ctrl+M | 5 |
| tools-a | `tools.undoRename` | Ctrl+Shift+M | 5 |
| tools-a | `tools.checksumCreate` | — (только меню) | 6 |
| tools-a | `tools.checksumVerify` | — (только меню) | 6 |
| tools-a | `tools.folderSize` | Cmd+Alt+I | 7 |
| tools-a | `tools.folderSizeAll` | Cmd+Shift+I | 7 |
| tools-b | `tools.compareDirs` | Shift+F2, Cmd+Shift+C | 8 |
| tools-b | `tools.compareFiles` | Cmd+Shift+D | 10 |
| tools-b | `tools.syncDirs` | Cmd+Shift+S | 13 |
| net | `net.connect` | Cmd+K, Ctrl+F | 21 |
| net | `net.disconnect` | Cmd+Shift+K, Ctrl+Shift+F | 21 |

Cmd+M в macOS по обычаю сворачивает окно; спецификация отдаёт его групповому переименованию, и в `KeyChordRules` оно не запрещено, поэтому оставлено (свернуть окно можно жёлтой кнопкой). Пункт «проверить первым» в списке приёмки. Клавиши внутри новых окон (сравнение файлов, синхронизация) — местные, как у окна просмотра, и в раскладку не входят.

## Что проверяется автоматически, что вручную

| Что | Автоматически (всегда) | Только с переменной окружения | Вручную (список приёмки) |
|---|---|---|---|
| Инструменты (переименование, сравнение, синхронизация, суммы, размеры) | полностью: `InMemoryFileSystem`, временные папки; 1000 файлов с отменой — на настоящей временной папке | — | удобство окон, клавиши на настоящей клавиатуре |
| `RemoteProvider` (пул, переподключение, запрет замены при переименовании, рекурсивное удаление) | общий набор `ProviderContractTests` и тесты обрыва на поддельном сервере в памяти (`FakeRemoteServer`) | — | — |
| FTP | `FtpSession` против тестового FTP-сервера, написанного в тестах и работающего в том же процессе на `127.0.0.1` (общий набор договора, обрывы, ответ сервера без TLS на запрос FTPS) | `TANDEM_TEST_FTP_URL` (+ `TANDEM_TEST_FTP_PASSWORD`): тот же набор на настоящем сервере | настоящий FTP-сервер, старый сервер с именами в Windows-1251 |
| FTPS | выбор режима шифрования, отказ от понижения до открытого FTP, решение о доверии сертификату (на подставных данных) | `TANDEM_TEST_FTPS_URL` (+ `…_PASSWORD`): рукопожатие TLS и весь набор | настоящий FTPS-сервер, самоподписанный сертификат |
| SFTP | разбор ключей (образцы созданы `ssh-keygen` и лежат в проекте), сборка способов входа, перевод ошибок библиотеки, решение о ключе узла; вся остальная логика — в `RemoteProvider` на поддельном сервере | `TANDEM_TEST_SFTP_URL` (+ `…_PASSWORD` или `…_KEY`, `…_PASSPHRASE`): общий набор договора и «архив → сервер» на настоящем сервере. `TANDEM_TEST_LOCAL_SSHD=1`: тесты сами запускают системный `/usr/sbin/sshd` от имени текущего пользователя на `127.0.0.1` с ключами во временной папке (системные настройки не меняются, «Удалённый вход» не включается) | **работа с SFTP-сервером как с панелью, включая архив → сервер** (критерий этапа) |
| Связка ключей | `FakeSecretStore`; перевод кодов ошибок системы в исключения | `TANDEM_TEST_KEYCHAIN=1`: запись, чтение, замена и удаление в настоящей связке под служебным именем `Tandem.Tests.<guid>`, с уборкой | пароль сохраняется, виден в «Связке ключей» как «Tandem», переживает перезапуск, удаляется вместе с подключением |
| Менеджер подключений, F4 на сервере, переподключение | модели представления и окна без экрана на поддельном сервере | — | на настоящем сервере |

По умолчанию `dotnet test Tandem.slnx` не открывает ни одного соединения вне `127.0.0.1`, не запускает `sshd` и не обращается к связке ключей.

## Структура файлов

```
Directory.Packages.props                    + SSH.NET, FluentFTP, System.IO.Hashing, DiffPlex
Tandem.slnx                                 + Tandem.Net, Tandem.Net.Tests
src/Tandem.Core/
  Vfs/         ProviderCapabilities (+LocalCopyEdit), VfsConnectionLostException
  Platform/    ISecretStore
  Operations/  OperationKind (+Rename, Sync, ChecksumCreate, ChecksumVerify), SkipReason (+ChangedSinceCompared, NameTaken)
  Tools/Rename/     RenameRule, RenamePlanner, BatchRenameOperation
  Tools/Checksums/  Checksum, ChecksumFile, ChecksumCreateOperation, ChecksumVerifyOperation
  Tools/Compare/    DirectoryComparer, TextDiff, BinaryDiff, FileComparison
  Tools/Sync/       SyncPlanner, SyncOperation
  Tools/            FolderSizeCalculator
src/Tandem.Net/
  ConnectionProfile, NetExceptions, Trust (HostKeyInfo, CertificateInfo, ITrustDecisions, ICredentialSource)
  Remote/      IRemoteSession, RemoteEntry, RemoteFeatures, RemoteProvider, SessionPool, CredentialLoop
  Ftp/         FtpSession, FtpSessionFactory
  Sftp/        SftpSession, SftpSessionFactory, SftpAuth, SftpErrors
  Connections/ ConnectionStore, TrustStore, INetPrompts, NetConnections
src/Tandem.Platform.Mac/   MacKeychainSecretStore, Interop/Security
src/Tandem.App/
  ViewModels/Tools/  ToolsCommands (+ по файлу на инструмент), MultiRenameViewModel, ChecksumViewModels,
                     FileCompareViewModel, SyncViewModel
  ViewModels/Net/    NetCommands, ConnectionListViewModel, ConnectionEditViewModel, RemoteEdit
  Views/Tools/       MultiRenameWindow, ChecksumWindows, FileCompareWindow, SyncWindow
  Views/Net/         ConnectionDialogs
  Services/          IToolWindows, ToolWindows (+ по файлу на инструмент), INetDialogs, NetDialogs, DialogNetPrompts
tests/Tandem.Net.Tests/    Remote/FakeRemoteServer, Ftp/TestFtpServer, Fixtures/keys, Integration/ (по переменным окружения)
docs/checklists/stage-3.md
```

Из списков отложенного включено:

- действие «Сравнить» в диалоге конфликта (этап 1, решение 34; обещано этапу 3) — задача 10;
- панель кнопок недоступна на панелях с результатами поиска и в архиве (этап 2, раздел «Поиск») — задача 25: кнопка доступна, когда её шаблону хватает настоящих путей; с появлением серверов «не диск» становится обычным делом;
- «одно зависшее определение тома задерживает очередь» (этап 1, задача 10) — для серверов закрыто в задаче 14: ключ тома сервера вычисляется без обращения к сети.

Оставлено (причина): окно показывается только после загрузки всех вкладок — вкладка на сервере восстанавливается как папка диска (задача 14), запуск сеть не ждёт, поэтому этап 3 в этом не нуждается; зависший локальный том в очереди, ключ тома без разрешения ссылок — не связаны с сетью; замена `qlmanage` — этап 4; остальные пункты `2026-11-19-stage-2-backlog.md` не мешают работам этапа.

---

### Task 1: Договоры ядра: потеря связи, секреты, новые виды операций

**Направление:** подготовка (main). **Зависит от:** —.

**Files:**
- Create: `src/Tandem.Core/Platform/ISecretStore.cs`, `tests/Tandem.Core.Tests/Platform/FakeSecretStore.cs`, `tests/Tandem.Core.Tests/Vfs/DroppingProvider.cs` (связать в `Tandem.App.Tests.csproj`, как уже связаны другие общие подделки)
- Modify: `src/Tandem.Core/Vfs/IFileSystemProvider.cs` (`ProviderCapabilities`), `Vfs/VfsExceptions.cs`, `Operations/FileOperation.cs` (`OperationKind`), `Operations/OperationProgress.cs` (`SkipReason`), `src/Tandem.App/Services/OperationTexts.cs`, `IDialogService.cs`, `DialogService.cs`, `DialogOperationInteraction.cs`, `Views/Dialogs/ChoiceDialogs.cs` (диалог ошибки), `ViewModels/PanelViewModel.cs` (`PanelErrorKind`, перевод ошибки), `SideViewModel.cs`, `Views/PanelView.axaml`, оба файла ресурсов, `tests/Tandem.App.Tests/Views/ScriptedDialogs.cs`
- Test: `tests/Tandem.Core.Tests/Operations/ConnectionLostTests.cs`, `tests/Tandem.App.Tests/Services/OperationTextsTests.cs`, `ViewModels/DialogOperationInteractionTests.cs`, `ViewModels/PanelListingTests.cs`, `Views/DialogWindowTests.cs`, `LocalizationTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Vfs;
[Flags] public enum ProviderCapabilities { None = 0, Write = 1, Trash = 2, UnixPermissions = 4, Symlinks = 8, Watch = 16,
    /// Источник далеко: файл правят во временной копии на диске и возвращают в источник после вопроса.
    LocalCopyEdit = 32 }
/// Связь с источником потеряна или не установилась. Повтор того же вызова пробует подключиться заново.
public class VfsConnectionLostException : VfsException { (string message); (string message, Exception? inner); }

namespace Tandem.Core.Platform;
/// Хранилище секретов (на macOS — связка ключей). Ключ — произвольная непустая строка.
public interface ISecretStore {
    Task<string?> GetAsync(string key, CancellationToken ct);            // null — секрета нет
    Task SetAsync(string key, string secret, CancellationToken ct);      // создаёт или заменяет
    Task DeleteAsync(string key, CancellationToken ct);                  // отсутствие — не ошибка
}
/// Хранилище недоступно или пользователь отказал в доступе.
public class SecretStoreException : IOException { (string message, Exception? inner = null); }

namespace Tandem.Core.Operations;
public enum OperationKind { Copy, Move, Delete, Pack, Unpack, Rename, Sync, ChecksumCreate, ChecksumVerify }
public enum SkipReason { /* прежние шесть */ …,
    ChangedSinceCompared,   // элемент изменился после сравнения или после переименования: не тронут
    NameTaken }             // нужное имя уже занято другим элементом: не тронут

namespace Tandem.App.Services;
public interface IDialogService {   // изменён один метод
    Task<ErrorAction> AskErrorAsync(VfsPath item, string message, string? detail, bool connectionLost, CancellationToken ct);
}
namespace Tandem.App.ViewModels;
public enum PanelErrorKind { AccessDenied, NotFound, Other, ConnectionLost }
public sealed partial class SideViewModel { public bool IsConnectionLost { get; } public IRelayCommand ReconnectCommand { get; } }
```

Тестовые подделки: `FakeSecretStore` (словарь в памяти; `Calls` — журнал вызовов; `Fail` — исключение для следующих вызовов) и `DroppingProvider` — обёртка над `InMemoryFileSystem` с другим `Id`: при `Connected == false` каждый вызов бросает `VfsConnectionLostException`; `ReconnectOnNextCall` возвращает связь при следующем вызове; `DropAfterBytes` обрывает открытый поток записи или чтения после указанного числа байтов; `Capabilities` задаются (по умолчанию — как у обёрнутого источника).

Решения:
- `OperationTexts.KindOf`: `VfsConnectionLostException` → `"ConnectionLost"` (ветка стоит выше `IOException`). Тексты: `OperationError_ConnectionLost` («Связь потеряна при работе с {0}.» / «The connection was lost while working with {0}.»), `OperationFailed_ConnectionLost`.
- `DialogOperationInteraction` передаёт в диалог `connectionLost: error is VfsConnectionLostException`. Диалог ошибки тогда подписывает кнопку повтора `Button_ReconnectAndRetry` («Переподключиться и повторить» / «Reconnect and Retry»); остальные кнопки прежние. Повтор — это обычный `ErrorAction.Retry`: движок не меняется, заново подключается сам источник.
- Панель: `VfsConnectionLostException` при чтении папки → `PanelErrorKind.ConnectionLost`, текст `Panel_ConnectionLost`. Панель остаётся на своём пути (обновление при этой ошибке **не** поднимается к предкам, в отличие от `NotFound`). `SideViewModel.IsConnectionLost` показывает в `PanelView` кнопку `Panel_Reconnect` («Переподключиться»), которая вызывает `Panel.RefreshAsync()`; Cmd+R делает то же.
- Подписи новых значений перечислений (`OperationKind_Rename` «Переименование», `OperationKind_Sync` «Синхронизация», `OperationKind_ChecksumCreate` «Подсчёт контрольных сумм», `OperationKind_ChecksumVerify` «Проверка контрольных сумм», `SkipReason_ChangedSinceCompared` «изменён после сравнения», `SkipReason_NameTaken` «имя уже занято») — в обоих языках.

- [ ] **Step 1:** Run `dotnet test Tandem.slnx` на чистом `main` → все тесты PASS; записать их число в отчёт задачи (точка отсчёта этапа).
- [ ] **Step 2: Тесты**

```csharp
// OperationTextsTests
public void Connection_lost_error_has_its_own_words()                    // ForError и ForFailure, оба языка; не «ошибка ввода-вывода»
// DialogOperationInteractionTests
public async Task Connection_lost_error_is_asked_as_reconnect()          // ScriptedDialogs.ErrorRequests[0].ConnectionLost == true
public async Task Other_io_error_is_not_asked_as_reconnect()
// DialogWindowTests
public async Task Error_dialog_for_lost_connection_labels_retry_as_reconnect()
// PanelListingTests (DroppingProvider)
public async Task Lost_connection_shows_connection_error_and_keeps_the_path()   // ErrorKind == ConnectionLost, CurrentPath прежний
public async Task Refresh_after_lost_connection_does_not_climb_to_the_parent()
public async Task Reconnect_command_reloads_the_folder_when_the_provider_is_back()
// ConnectionLostTests (Core: CopyOperation в DroppingProvider, ScriptedInteraction отвечает Retry)
public async Task Copy_retried_after_lost_connection_completes_and_leaves_no_part_file()   // RF1 на уровне движка
public async Task Copy_aborted_after_lost_connection_keeps_the_old_destination_file()       // прежнее содержимое под настоящим именем
// LocalizationTests: новые значения OperationKind и SkipReason покрыты существующей теорией CoreEnums
```

- [ ] **Step 3:** Run `dotnet test Tandem.slnx --filter "FullyQualifiedName~ConnectionLost|FullyQualifiedName~OperationTexts|FullyQualifiedName~DialogOperationInteraction|FullyQualifiedName~PanelListing|FullyQualifiedName~Localization"` → FAIL (нет типов и ключей).
- [ ] **Step 4:** Реализовать. Все реализации `IDialogService` и вызовы `AskErrorAsync` обновить (`grep -rn "AskErrorAsync" src tests`).
- [ ] **Step 5:** Run `dotnet build Tandem.slnx && dotnet test Tandem.slnx` → 0 предупреждений; все прежние тесты и новые PASS.
- [ ] **Step 6:** Commit: `feat(core,app): connection-lost contract, secret store seam, stage 3 operation kinds`.

---

### Task 2: Пакеты, проект `Tandem.Net` и проверка библиотек

Цель — до начала работ закрепить тестами то поведение четырёх библиотек, на которое опирается план (как задача 6 этапа 2).

**Направление:** подготовка (main). **Зависит от:** 1.

**Files:**
- Create: `src/Tandem.Net/Tandem.Net.csproj` (ссылка на Core; `SSH.NET`, `FluentFTP`; `InternalsVisibleTo Tandem.Net.Tests`), `src/Tandem.Net/AssemblyMarker.cs`, `tests/Tandem.Net.Tests/Tandem.Net.Tests.csproj` (как `Tandem.Archives.Tests`; ссылки на Net и Core; связанные файлы `..\Tandem.Core.Tests\Vfs\ProviderContractTests.cs`, `Vfs\TempDir.cs`, `Operations\ScriptedInteraction.cs`, `Platform\FakeSecretStore.cs`; `Fixtures/**` копируются в вывод, кроме `*.sh` и `README.md`), `tests/Tandem.Net.Tests/Fixtures/keys/make-keys.sh`, `Fixtures/README.md`, файлы ключей, `tests/Tandem.Net.Tests/README.md` (переменные окружения из раздела «Что проверяется автоматически»)
- Modify: `Directory.Packages.props`, `Tandem.slnx`, `src/Tandem.Core/Tandem.Core.csproj` (`System.IO.Hashing`, `DiffPlex`), `src/Tandem.App/Tandem.App.csproj` (ссылка на `Tandem.Net`)
- Test: `tests/Tandem.Core.Tests/Tools/HashingProbeTests.cs`, `Tools/DiffPlexProbeTests.cs`, `tests/Tandem.Net.Tests/SshNetProbeTests.cs`, `FluentFtpProbeTests.cs`

**Interfaces:**
- Produces: проекты и пакеты; закреплённые в комментариях тестов факты о библиотеках, на которые ссылаются задачи 6, 9, 15, 16, 17, 26 (в отчёте задачи — список библиотек, которые SSH.NET и FluentFTP принесли с собой).

Образцы ключей. `make-keys.sh` создаёт системным `/usr/bin/ssh-keygen` (ничего не скачивается, системные настройки не меняются) в `Fixtures/keys`: `ed25519`, `rsa3072`, `ecdsa256` (без ключевой фразы, формат OpenSSH), `ed25519-pass` (ключевая фраза `secret`), `rsa-pem` (`-m PEM`), каждому — файл `.pub`. Образцы коммитятся; это тестовые ключи, нигде больше не используемые, — так и написать в `Fixtures/README.md`.

- [ ] **Step 1: Тесты-пробы** (только вызовы библиотек; ни одного соединения вне `127.0.0.1`)

```csharp
// HashingProbeTests (System.IO.Hashing)
public void Crc32_of_123456789_is_CBF43926()                      // GetCurrentHashAsUInt32() == 0xCBF43926; порядок байтов Hash() записать в комментарии
public void Crc32_appended_in_pieces_equals_one_shot()
// DiffPlexProbeTests
public void Identical_texts_give_only_unchanged_lines()
public void Inserted_deleted_and_modified_lines_are_aligned_side_by_side()   // SideBySideDiffBuilder: Imaginary напротив Inserted/Deleted, Modified парой
public void Modified_line_reports_its_changed_pieces()                        // SubPieces; записать, по словам или по символам
public void Line_endings_how_lines_are_split()                               // "a\r\nb" против "a\nb": закрепить, видит ли библиотека разницу
public void Empty_against_non_empty_text()
[Fact, Trait("Category","Timing")] public void Fifty_thousand_lines_with_one_percent_changed_take_under_two_seconds()
[Fact, Trait("Category","Timing")] public void Two_unrelated_texts_of_twenty_thousand_lines_finish_under_ten_seconds()
// SshNetProbeTests
[Theory] public void Private_key_file_loads(string fixture)                   // ed25519, rsa3072, ecdsa256, rsa-pem
public void Encrypted_key_loads_with_its_passphrase()
public void Encrypted_key_without_or_with_wrong_passphrase_fails_recognisably()   // типы исключений записать в комментарии
public async Task Connect_to_a_closed_local_port_fails_within_the_timeout()   // 127.0.0.1, порт свободный и закрытый; тип исключения записать
public async Task Connect_to_a_listener_that_hangs_up_fails_recognisably()    // TcpListener на 127.0.0.1 принимает и закрывает
public async Task Connect_honours_cancellation()
public void Api_shape_compiles()   // не выполняется против сервера: метод, который ссылается на SftpClient.ConnectAsync, ListDirectoryAsync,
                                   // OpenAsync, CreateDirectoryAsync, DeleteFileAsync, DeleteDirectoryAsync, RenameFileAsync, GetAttributesAsync,
                                   // SetAttributes, HostKeyReceived (FingerPrintSHA256, HostKeyName), KeepAliveInterval, OperationTimeout;
                                   // записать: чем получить сведения о самой ссылке (lstat), чем — о её цели, есть ли открытые чтение цели ссылки
                                   // и создание ссылки, умеет ли поток SftpFileStream CanSeek
// FluentFtpProbeTests
public async Task Connect_to_a_closed_local_port_fails_within_the_timeout()   // тип исключения записать
public void Api_shape_compiles()   // AsyncFtpClient: Connect, GetListing, GetObjectInfo, OpenRead, OpenWrite, GetReply, Rename, DeleteFile,
                                   // DeleteDirectory, CreateDirectory, SetModifiedTime, Chmod; FtpConfig: EncryptionMode, DataConnectionType,
                                   // ConnectTimeout, ReadTimeout, DataConnectionConnectTimeout, DataConnectionReadTimeout, ValidateCertificate
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Net.Tests` → FAIL (проекта нет).
- [ ] **Step 3:** Добавить пакеты и проекты, создать образцы ключей, написать пробы по документации версий из заголовка плана. **Если пакет не восстанавливается под `net11.0`, тянет код платформы, которого нет для `osx-arm64`, или проба показывает, что библиотека не умеет нужного (асинхронные вызовы с отменой, чтение ключа OpenSSH ed25519, выравнивание строк бок о бок), — остановиться и доложить, не подменяя библиотеку.** Исключения из правила: если SSH.NET не даёт открыто прочитать цель ссылки или создать ссылку — записать это в отчёт, задача 17 тогда объявляет SFTP без возможности `Symlinks`; если второй тест времени DiffPlex не укладывается — записать, задача 9 тогда ограничивает текстовое сравнение числом строк.
- [ ] **Step 4:** Run `dotnet build Tandem.slnx && dotnet test Tandem.slnx` → 0 предупреждений, всё PASS. Run `ls ~/.nuget/packages | grep -i -E "ssh.net|fluentftp|diffplex|system.io.hashing"` → четыре пакета; версии совпадают с планом.
- [ ] **Step 5:** Commit: `feat(net): Tandem.Net project, stage 3 packages and library probes`.

---

### Task 3: Заготовки приложения: команды, сочетания, меню, окна инструментов, разделы ресурсов

После этой задачи общие файлы заморожены до фазы C.

**Направление:** подготовка (main). **Зависит от:** 1, 2.

**Files:**
- Create: `src/Tandem.App/ViewModels/Tools/ToolsCommands.cs`, `ViewModels/Net/NetCommands.cs`, `Services/IToolWindows.cs`, `Services/ToolWindows.cs`, `Views/Dialogs/ConflictDialog.cs` (класс `ConflictDialog` переносится сюда из `ChoiceDialogs.cs` без изменений — чтобы направления tools-b и net не правили один файл), `tests/Tandem.App.Tests/Views/ScriptedToolWindows.cs`
- Modify: `Commands/CommandIds.cs`, `Commands/KeyMap.cs`, `Views/MainMenu.cs`, `ViewModels/MainViewModel.cs`, `ViewModels/PanelViewModel.Selection.cs`, `Composition.cs` (`AppServices.ToolWindows`), `Services/IDialogService.cs`, `DialogService.cs`, `Views/Dialogs/ChoiceDialogs.cs` (вынос `ConflictDialog`), `Views/MainWindow.axaml.cs` (закрытие окон инструментов при выходе), оба файла ресурсов, `tests/…/Views/TestApp.cs`, `ScriptedDialogs.cs`
- Test: `tests/Tandem.App.Tests/Commands/KeyMapTests.cs`, `CommandRegistryTests.cs`, `KeyChordRulesTests.cs`, `LocalizationTests.cs`, `ViewModels/PanelSelectionTests.cs`, `Views/MainWindowTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.App.Commands;
public static class CommandIds {   // добавить; все попадают в All
    public const string ToolsMultiRename = "tools.multiRename", ToolsUndoRename = "tools.undoRename",
        ToolsCompareDirs = "tools.compareDirs", ToolsSyncDirs = "tools.syncDirs", ToolsCompareFiles = "tools.compareFiles",
        ToolsChecksumCreate = "tools.checksumCreate", ToolsChecksumVerify = "tools.checksumVerify",
        ToolsFolderSize = "tools.folderSize", ToolsFolderSizeAll = "tools.folderSizeAll",
        NetConnect = "net.connect", NetDisconnect = "net.disconnect";
}

namespace Tandem.App.Services;
/// Модель диалога: просит закрыть окно и отдаёт ответ.
public interface IDialogModel<TResult> { event EventHandler<TResult?>? CloseRequested; }
/// Окна инструментов. Какое окно показывает какую модель, решает таблица в ToolWindows (по типу модели).
public interface IToolWindows {
    /// Немодальное окно для модели; уже открытое окно той же модели выходит вперёд. Закрытие окна освобождает модель
    /// (IDisposable / IAsyncDisposable).
    void Show(object viewModel);
    /// Модальное окно (к owner, иначе к главному); завершается при закрытии; закрытие крестиком и Esc — default.
    Task<TResult?> ShowDialogAsync<TResult>(IDialogModel<TResult> viewModel, Window? owner = null);
    bool HasOpenWindows { get; }
    Task<bool> CloseAllAsync();
}
public sealed partial class ToolWindows : IToolWindows {
    private void Register<TViewModel>(Func<TViewModel, Window> create);     // вызывают частичные методы
    partial void AddRename(); partial void AddChecksums(); partial void AddCompare(); partial void AddSync();   // вызываются из конструктора
}
public interface IDialogService {   // добавить
    /// Задано — диалог конфликта показывает кнопку «Сравнить…» и вызывает это с двумя файлами и своим окном.
    Func<FileEntry, FileEntry, Window, Task>? CompareFiles { get; set; }
}

namespace Tandem.App.ViewModels.Tools;
public sealed partial class ToolsCommands : IDisposable {
    public ToolsCommands(WorkspaceViewModel workspace, AppServices services, FileCommands files,
        IOperationInteraction interaction, CommandRegistry commands, Action<Exception> reportFailure);
    private void On(string id, Func<Task> run, Func<bool> canRun);   // регистрирует команду и включает её в пересчёт доступности
    private PanelViewModel? Active { get; } private PanelViewModel? Passive { get; }
    partial void RegisterRename(); partial void RegisterChecksums(); partial void RegisterFolderSizes();
    partial void RegisterCompareDirs(); partial void RegisterCompareFiles(); partial void RegisterSync();
    public Task WhenIdleAsync();
}
namespace Tandem.App.ViewModels.Net;
public sealed partial class NetCommands : IDisposable {
    public NetCommands(WorkspaceViewModel workspace, AppServices services, FileCommands files, CommandRegistry commands,
        Action<Exception> reportFailure);
}
namespace Tandem.App.ViewModels;
public sealed partial class PanelViewModel {
    /// Помечает строки, для которых predicate истинно, и снимает пометку с остальных («..» не помечается).
    public void SelectWhere(Func<FileEntry, bool> predicate);
}
public sealed partial class MainViewModel { public ToolsCommands Tools { get; } public NetCommands Net { get; } }
```

`AppServices` получает позиционный параметр `IToolWindows ToolWindows` после `Windows` (обновить все места создания: `grep -rn "new AppServices(" src tests`). `MainViewModel` создаёт `Tools` и `Net` после `Archives`, освобождает их в `Dispose`; `HasOpenWindows` и `ConfirmCloseAsync` учитывают `ToolWindows`. Пока направления не добавили обработчики, команды не зарегистрированы и их пункты меню серые (так `MainMenu` уже поступает с незарегистрированными командами).

Сочетания — таблица раздела «Сочетания по умолчанию» (в `KeyMap.Default` в этом порядке, после `ArchiveUnpack`). `tools.checksumCreate` и `tools.checksumVerify` — без сочетаний (добавить в список команд «только меню» в `KeyMapTests`).

Меню: два новых меню между «Переход» и «Вид».
`Menu_Tools` («Инструменты» / «Tools»): `tools.multiRename`, `tools.undoRename`, —, `tools.compareDirs`, `tools.syncDirs`, `tools.compareFiles`, —, `tools.checksumCreate`, `tools.checksumVerify`, —, `tools.folderSize`, `tools.folderSizeAll`.
`Menu_Net` («Сеть» / «Network»): `net.connect`, `net.disconnect`.

Подписи команд (русский / английский): «Групповое переименование…» / «Multi-Rename…»; «Отменить последнее переименование» / «Undo Last Rename»; «Сравнить папки» / «Compare Folders»; «Синхронизировать папки…» / «Synchronize Folders…»; «Сравнить файлы…» / «Compare Files…»; «Создать контрольные суммы…» / «Create Checksums…»; «Проверить контрольные суммы» / «Verify Checksums»; «Размер папки» / «Folder Size»; «Размеры всех папок» / «All Folder Sizes»; «Подключиться к серверу…» / «Connect to Server…»; «Отключиться» / «Disconnect».

В конец обоих файлов ресурсов — три раздела-заготовки из раздела «Параллельное выполнение», слово в слово.

`ScriptedToolWindows` (тесты): `Shown` — список показанных моделей; `OnDialog` — `Func<object, object?>`, отвечающая на модальные окна (без неё — `default`); окон не создаёт. `TestApp` получает параметр выбора: настоящий `ToolWindows` (по умолчанию, окна без экрана) или `ScriptedToolWindows`.

- [ ] **Step 1: Тесты**

```csharp
// KeyMapTests: дополнить теорию Default_binding и Default_map_has_the_whole_table 13 новыми парами; затем
public void Stage_3_chords_do_not_collide_with_earlier_ones()            // Default_map_has_no_duplicate_chords остаётся зелёным
public void Every_stage_3_chord_is_assignable()                           // KeyChordRules.IsAssignable для каждой из 13
// CommandRegistryTests / MainWindowTests
public void Tools_and_network_menus_list_every_new_command_in_order()
public void A_command_nobody_registered_yet_shows_a_disabled_menu_item()  // до фазы B — все одиннадцать
// PanelSelectionTests
public void SelectWhere_marks_matching_rows_and_unmarks_the_rest()
public void SelectWhere_never_marks_the_parent_link_and_updates_the_status()
// ToolWindows (Views)
[AvaloniaFact] public async Task Dialog_model_closes_its_window_with_the_result()
[AvaloniaFact] public void Showing_the_same_model_twice_keeps_one_window()
[AvaloniaFact] public async Task Closing_the_app_closes_tool_windows()
// LocalizationTests: Both_languages_have_identical_key_sets остаётся зелёным; добавить
public void Stage_3_section_markers_are_present_in_both_resource_files()  // шесть строк begin/end в каждом файле
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.App.Tests --filter "FullyQualifiedName~KeyMap|FullyQualifiedName~CommandRegistry|FullyQualifiedName~PanelSelection|FullyQualifiedName~Localization|FullyQualifiedName~MainWindow"` → FAIL.
- [ ] **Step 3:** Реализовать. Тела частичных методов не писать — их дают направления.
- [ ] **Step 4:** Run `dotnet build Tandem.slnx && dotnet test Tandem.slnx` → 0 предупреждений, всё PASS.
- [ ] **Step 5:** Commit: `feat(app): stage 3 commands, chords, menus and tool window scaffolding`. Создать три рабочие копии от этого коммита: `tools-a`, `tools-b`, `net` (навык `superpowers:using-git-worktrees`).

---
## Направление tools-a (задачи 4–7)

### Task 4: Правила группового переименования и план имён

**Направление:** tools-a. **Зависит от:** 3.

**Files:**
- Create: `src/Tandem.Core/Tools/Rename/RenameRule.cs`, `RenameTemplate.cs`, `RenamePlanner.cs`
- Test: `tests/Tandem.Core.Tests/Tools/Rename/RenameTemplateTests.cs`, `RenamePlannerTests.cs`

**Interfaces:**
- Consumes: `FileEntry`, `VfsPath`.
- Produces:

```csharp
namespace Tandem.Core.Tools.Rename;
public enum CaseChange { None, Lower, Upper, FirstUpper, EachWordUpper }
/// Правило: шаблон имени и шаблон расширения, затем поиск и замена, затем регистр.
public sealed record RenameRule(string NameTemplate = "[N]", string ExtensionTemplate = "[E]",
    string Search = "", string Replace = "", bool UseRegex = false, bool MatchCase = false,
    CaseChange Case = CaseChange.None, int CounterStart = 1, int CounterStep = 1, int CounterDigits = 1);
/// Шаблон или выражение записаны неверно; Position — место ошибки в шаблоне (-1 для выражения).
public sealed class RenameRuleException(string message, int position) : FormatException(message) { public int Position { get; } }
public enum RenameStatus { Unchanged, Ok, InvalidName, Duplicate, NameTaken }
public sealed record RenameItem(FileEntry Entry, string NewName, RenameStatus Status);
public sealed record RenamePlan(IReadOnlyList<RenameItem> Items) {
    public int ChangedCount { get; }      // сколько Ok
    public bool CanRun { get; }           // есть хотя бы один Ok и нет InvalidName, Duplicate, NameTaken
}
public static class RenamePlanner {
    /// namesByFolder: все имена, которые сейчас есть в каждой папке, где лежат entries. Порядок entries задаёт счётчик.
    /// <exception cref="RenameRuleException">Шаблон или регулярное выражение неверны.</exception>
    public static RenamePlan Build(IReadOnlyList<FileEntry> entries, RenameRule rule,
        IReadOnlyDictionary<VfsPath, IReadOnlySet<string>> namesByFolder, TimeZoneInfo zone);
}
```

Шаблон (собственная запись в квадратных скобках): `[N]` — имя без расширения; `[N2-5]` — символы со 2-го по 5-й (с единицы, включительно, по текстовым элементам, а не по единицам UTF-16), `[N3-]` — с 3-го до конца; `[E]` — расширение без точки; `[C]` — счётчик (`CounterStart`, `CounterStep`, дополнение нулями до `CounterDigits`); `[Y]` — год в 4 цифры, `[M]`, `[D]`, `[h]`, `[m]`, `[s]` — по 2 цифры из времени изменения файла в поясе `zone`; `[[]` — знак «[». Всё прочее — буквально. Неизвестное или незакрытое `[…]` — `RenameRuleException` с позицией. У папки расширения нет: всё имя — `[N]`, `[E]` пусто. Итоговое имя — `имя` или `имя.расширение` (точка не ставится при пустом расширении).

Порядок: шаблоны → поиск и замена по всему получившемуся имени (обычный текст или `Regex` с пределом времени 1 с, без учёта регистра, пока не задано `MatchCase`; пустой `Search` — ничего) → регистр (только часть до последней точки).

Статусы: `InvalidName` — пусто, «.», «..», содержит `/` или `\0`, длиннее 255 байтов UTF-8; `Duplicate` — два элемента одной папки получают одно имя; `NameTaken` — имя занято элементом папки, который сам в этом плане не переименовывается в другое имя. «То же имя» сравнивается без учёта регистра и формы Юникода (после `Normalize(FormC)`, `OrdinalIgnoreCase`) — как на диске macOS. Смена только регистра или формы собственного имени — `Ok`. Обмен `a↔b` и цепочка `a→b, b→c` — `Ok` у всех участников.

- [ ] **Step 1: Тесты**

```csharp
// RenameTemplateTests
[Theory] public void Name_tokens(string template, string fileName, string expected)
    // "[N]_old","report.pdf"→"report_old.pdf"; "[N2-4]","abcdef.txt"→"bcd.txt"; "[N3-]","abcdef.txt"→"cdef.txt";
    // "[N9-]","abc.txt" → InvalidName (пустое имя); "[[]x]","a.txt"→"[x].txt"
public void Counter_uses_start_step_and_digits()                      // start 5, step 5, digits 3 → 005, 010, 015 в порядке entries
public void Date_tokens_come_from_the_modified_time_in_the_given_zone()   // 2026-03-01T23:30Z в UTC+3 → [Y]-[M]-[D] = 2026-03-02
public void Extension_template_can_replace_or_drop_the_extension()    // "txt" → a.txt; "" → "a" без точки
public void Folder_has_no_extension()                                  // папка "v1.2": [N] == "v1.2"
public void Surrogate_pairs_and_combining_marks_are_not_split()        // [N1-1] для "й" (и + бреве) и для эмодзи
[Theory] public void Bad_template_reports_the_position(string template, int position)   // "[X]",0; "ab[N",2; "[N5-2]",0
// RenamePlannerTests
public void Search_and_replace_is_plain_text_and_ignores_case_by_default()
public void Regex_replace_supports_groups()                            // "(\\d+)-(\\d+)" → "$2-$1"
public void Bad_regex_is_a_rule_error()
public void Case_change_leaves_the_extension_alone()
public void Unchanged_names_are_reported_unchanged_and_do_not_block_the_plan()
public void Two_items_mapping_to_one_name_are_duplicates()             // RF4
public void Name_of_a_bystander_file_is_taken()                         // RF4: в папке лежит target.txt, которого нет в плане
public void Name_differing_only_in_case_or_normalization_counts_as_the_same()   // "Й.txt" NFC против NFD; "A.txt" против "a.txt"
public void Swap_and_chain_inside_the_plan_are_ok()                     // RF4
public void Case_only_change_of_its_own_name_is_ok()
public void Items_of_different_folders_do_not_collide()                 // панель с результатами поиска
[Fact, Trait("Category","Timing")] public void Ten_thousand_items_plan_in_under_a_second()
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.Core.Tests --filter "FullyQualifiedName~Tools.Rename"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): batch rename templates and name planning`.

---

### Task 5: Операция переименования, отмена, окно (Cmd+M)

**Направление:** tools-a. **Зависит от:** 4.

**Files:**
- Create: `src/Tandem.Core/Tools/Rename/BatchRenameOperation.cs`, `src/Tandem.App/ViewModels/Tools/MultiRenameViewModel.cs`, `ViewModels/Tools/ToolsCommands.Rename.cs`, `Views/Tools/MultiRenameWindow.axaml(.cs)`, `Services/ToolWindows.Rename.cs`
- Modify: оба файла ресурсов (раздел tools-a)
- Test: `tests/Tandem.Core.Tests/Tools/Rename/BatchRenameOperationTests.cs`, `BatchRenameLocalTests.cs`, `tests/Tandem.App.Tests/ViewModels/MultiRenameTests.cs`, `Views/MultiRenameWindowTests.cs`

**Interfaces:**
- Consumes: `RenamePlanner`, `RenamePlan` (задача 4); `ItemErrors`, `FileOperation` (ядро); `ToolsCommands.On`, `IToolWindows`, `FileCommands.StartOperation` (задача 3 и этап 2).
- Produces:

```csharp
namespace Tandem.Core.Tools.Rename;
public sealed record RenameStep(VfsPath From, VfsPath To);                 // в одной папке одного источника
/// Сделанное переименование и вид файла сразу после него — чтобы отмена узнала «тот же ли это файл».
public sealed record RenamedItem(VfsPath From, VfsPath To, EntryKind Kind, long Size, DateTimeOffset Modified);
public sealed class BatchRenameOperation : FileOperation {                 // Kind == OperationKind.Rename
    public const string TempSuffix = ".tandem-rename";                     // служебное имя: .{guid}.tandem-rename
    public BatchRenameOperation(ProviderRegistry providers, IReadOnlyList<RenameStep> steps, IOperationInteraction interaction,
        IReadOnlyList<RenamedItem>? undoOf = null);
    /// Что действительно переименовано (также после отмены и ошибки); читать после завершения.
    public IReadOnlyList<RenamedItem> Completed { get; }
}
namespace Tandem.App.ViewModels.Tools;
public sealed partial class MultiRenameViewModel : ObservableObject, IDialogModel<RenamePlan> {
    public MultiRenameViewModel(IReadOnlyList<FileEntry> targets, IReadOnlyDictionary<VfsPath, IReadOnlySet<string>> namesByFolder);
    // свойства правила (NameTemplate, ExtensionTemplate, Search, Replace, UseRegex, MatchCase, Case, CounterStart, CounterStep, CounterDigits),
    // Rows (старое имя, новое имя, статус), Summary, RuleError, CanAccept; команды Accept и Cancel
}
public sealed partial class ToolsCommands { public bool CanUndoRename { get; } }
```

Правила операции:
- Шаги одной операции лежат в одном или нескольких каталогах; источники цели и начала совпадают. Отказ до начала (`OperationRejectedException`, `InvalidName`) — если `To` не имя в той же папке.
- Каждое переименование — `MoveAsync` источника, который не заменяет существующее имя; `VfsAlreadyExistsException` → элемент пропущен с `SkipReason.NameTaken`. Прочие ошибки ввода-вывода — через `ItemErrors` (повторить, пропустить, прервать).
- Пересечения: шаг, чьё новое имя сейчас занято началом другого, ещё не выполненного шага, идёт в два приёма — сначала все такие файлы переименовываются в служебные имена `.{guid}.tandem-rename` в той же папке, затем каждый — в своё новое имя. Шаги без пересечений выполняются прямо, в порядке списка.
- Отмена, «Прервать» и ошибка посреди работы: файлы, оставшиеся под служебными именами, возвращаются под прежние имена (с `CancellationToken.None`); `Completed` содержит только доведённые до нового имени. Если вернуть не удалось, файл остаётся под служебным именем и попадает в сводку пропущенного.
- С `undoOf` (операция отмены) перед каждым шагом сверяется, что элемент под `From` шага — тот же, что был записан (`Kind`, `Size`, `Modified`); иначе пропуск с `ChangedSinceCompared`. Занятое прежнее имя — `NameTaken`.
- Ход: по одному «файлу» на шаг; байтов нет.

Приложение:
- `tools.multiRename` доступна, когда есть цели (`GetOperationTargets`) и источник каждой может писать. Команда читает имена папок целей вне потока интерфейса, показывает `MultiRenameViewModel` через `IToolWindows.ShowDialogAsync`; при принятии строит шаги из элементов `Ok` и запускает операцию через `FileCommands.StartOperation` (очередь, окно прогресса, обновление панелей, сводка — как у копирования).
- Предварительный просмотр пересчитывается при каждом изменении правила: до 2000 элементов — сразу, больше — вне потока интерфейса с задержкой 150 мс, последний результат вытесняет прежние. Ошибка правила показывается текстом под полем и запрещает «Переименовать»; строки со статусами `InvalidName`, `Duplicate`, `NameTaken` выделены существующим цветом ошибки темы и названы в `Summary` («Будет переименовано 998 из 1000; мешают 2»).
- После завершения операции её `Completed` (если не пуст) становится журналом отмены, вытесняя прежний. `tools.undoRename` доступна при непустом журнале: спрашивает подтверждение («Вернуть прежние имена: N?»), запускает `BatchRenameOperation` с обратными шагами и `undoOf`; после неё журнал пуст (повторить отменённое нельзя).

- [ ] **Step 1: Тесты**

```csharp
// BatchRenameOperationTests (InMemoryFileSystem)
public async Task Renames_every_step_and_reports_them_completed()
public async Task Swap_of_two_names_keeps_both_contents()                         // RF4: a↔b
public async Task Chain_and_cycle_of_three_keep_every_content()                    // RF4: a→b→c→a
public async Task Case_only_rename_works()
public async Task Name_taken_by_a_bystander_skips_that_item_and_overwrites_nothing()   // RF4: содержимое постороннего файла прежнее
public async Task Cancel_between_the_two_passes_leaves_no_temp_names()             // RF4: все файлы под прежним или новым именем, ни одного *.tandem-rename
public async Task Failure_in_the_second_pass_restores_the_others()                 // FailOn("Move") на одном шаге, ответ Abort
public async Task Error_can_be_retried_or_skipped()
public async Task Step_into_another_folder_is_rejected_before_anything_changes()
public async Task Undo_restores_the_old_names()
public async Task Undo_skips_a_file_changed_since_the_rename()                     // RF4: ChangedSinceCompared, файл не тронут
public async Task Undo_skips_when_the_old_name_is_taken_again()                    // RF4: NameTaken, оба файла целы
public async Task Undo_after_a_partial_rename_reverts_only_what_was_done()
// BatchRenameLocalTests (настоящая временная папка, LocalFileSystemProvider + MacExclusiveRename) — критерий этапа
public async Task Thousand_files_renamed_by_template_and_undone()   // 1000 файлов "f0001.txt"…; правило "[N]_[C]" (digits 4) → все имена по плану;
                                                                     // отмена → исходные имена и содержимое байт в байт; в папке нет лишних файлов
// MultiRenameTests ([AvaloniaFact], TestApp + ScriptedToolWindows)
public async Task Command_is_off_without_targets_and_in_a_read_only_source()
public async Task Preview_follows_the_rule_and_blocks_accept_on_conflicts()
public async Task Bad_template_shows_an_error_and_blocks_accept()
public async Task Accept_renames_through_the_queue_and_refreshes_the_panel()
public async Task Undo_is_available_after_a_rename_and_restores_names()
public async Task Undo_is_off_again_after_it_ran()
public async Task Rename_in_a_search_result_panel_renames_each_file_in_its_own_folder()
// MultiRenameWindowTests
[AvaloniaFact] public async Task Window_shows_old_and_new_names_and_closes_on_Escape()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~BatchRename|FullyQualifiedName~MultiRename"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(tools): batch rename with preview and undo`.

---

### Task 6: Контрольные суммы: создание и проверка

**Направление:** tools-a. **Зависит от:** 3.

**Files:**
- Create: `src/Tandem.Core/Tools/Checksums/Checksum.cs`, `ChecksumFile.cs`, `ChecksumCreateOperation.cs`, `ChecksumVerifyOperation.cs`, `src/Tandem.App/ViewModels/Tools/ChecksumViewModels.cs`, `ToolsCommands.Checksums.cs`, `Views/Tools/ChecksumWindows.cs`, `Services/ToolWindows.Checksums.cs`
- Modify: оба файла ресурсов (раздел tools-a)
- Test: `tests/Tandem.Core.Tests/Tools/Checksums/ChecksumTests.cs`, `ChecksumFileTests.cs`, `ChecksumOperationTests.cs`, `tests/Tandem.App.Tests/ViewModels/ChecksumCommandsTests.cs`

**Interfaces:**
- Consumes: `System.IO.Hashing.Crc32` (факты из `HashingProbeTests`), `System.Security.Cryptography` (MD5, SHA1, SHA256 — `IncrementalHash`), `SafeReplace`, `ItemErrors`, `TextEncodingDetector`.
- Produces:

```csharp
namespace Tandem.Core.Tools.Checksums;
public enum ChecksumAlgorithm { Crc32, Md5, Sha1, Sha256 }
public static class Checksum {
    public static string Extension(ChecksumAlgorithm algorithm);            // "sfv", "md5", "sha1", "sha256"
    public static ChecksumAlgorithm? FromFileName(string name);             // по расширению, без учёта регистра
    /// Шестнадцатеричная запись строчными буквами (CRC32 — 8 знаков). Читает блоками по 1 МиБ; между блоками — pause и ct.
    public static Task<string> ComputeAsync(Stream stream, ChecksumAlgorithm algorithm, IProgress<long>? bytes,
        Func<CancellationToken, ValueTask>? pause, CancellationToken ct);
}
public sealed record ChecksumLine(string RelativePath, string Hash);        // путь через '/'
public sealed record ChecksumFileContent(IReadOnlyList<ChecksumLine> Lines, IReadOnlyList<int> UnreadableLineNumbers);
public static class ChecksumFile {
    public const long MaxBytes = 16 * 1024 * 1024;
    public static string Format(ChecksumAlgorithm algorithm, IReadOnlyList<ChecksumLine> lines);
    public static ChecksumFileContent Parse(ChecksumAlgorithm algorithm, string text);
}
public sealed class ChecksumCreateOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources, VfsPath checksumFile,
    ChecksumAlgorithm algorithm, IOperationInteraction interaction) : FileOperation;      // Kind == ChecksumCreate; Destination == папка файла сумм
public enum VerifyStatus { Ok, Mismatch, Missing, Unreadable, OutsideFolder }
public sealed record VerifyResult(string RelativePath, VerifyStatus Status, string Expected, string? Actual);
public sealed class ChecksumVerifyOperation(ProviderRegistry providers, VfsPath checksumFile, IOperationInteraction interaction)
    : FileOperation {                                                                    // Kind == ChecksumVerify
    public IReadOnlyList<VerifyResult> Results { get; }
    public IReadOnlyList<int> UnreadableLineNumbers { get; }
}
```

Форматы. Запись: `.sfv` — `путь ХЕШ`; остальные — `хеш *путь` (как пишут `md5sum`, `shasum` в двоичном режиме); строки через `\n`, UTF-8 без метки. Чтение принимает: `хеш *путь`, `хеш  путь`, `АЛГОРИТМ (путь) = хеш`, для `.sfv` — `путь хеш`; строки-комментарии с `;` и `#`; `\r\n`; обратную косую черту в пути как `/`; хеш в любом регистре. Текст файла сумм читается как UTF-8, а при неверном UTF-8 — как Windows-1251 (`TextEncodingDetector`). Строка, которую не удалось разобрать, — в `UnreadableLineNumbers`.

Создание: источники — файлы и папки (папки обходятся рекурсивно; ссылки пропускаются и попадают в сводку как `SymlinksNotSupported`); пути в файле — относительно папки, где создаётся файл сумм; источник вне этой папки — отказ до начала. Существующий файл сумм — отказ `DestinationExists`. Файл сумм пишется через `SafeReplace.WriteAsync` в самом конце; отмена и «Прервать» не оставляют ничего. Сам создаваемый файл в список не попадает.

Проверка: путь из файла сумм разрешается от папки файла сумм; абсолютный путь и путь с `..`, выходящим из этой папки, не читаются — `OutsideFolder`. Отсутствующий файл — `Missing`; ошибка чтения, на которую ответили «Пропустить», — `Unreadable`. Ход — по байтам проверяемых файлов.

Приложение: `tools.checksumCreate` — есть цели и папка активной панели пишется; диалог (алгоритм — по умолчанию SHA-256; имя файла — имя единственной цели или имя папки панели плюс расширение алгоритма, меняется вместе с алгоритмом) → операция через `FileCommands.StartOperation`, курсор встаёт на созданный файл. `tools.checksumVerify` — курсор на файле, для которого `Checksum.FromFileName` не null → операция; по завершении окно результатов: «Совпало N, не совпало M, нет файла K», затем список, несовпавшие и отсутствующие первыми; нераспознанные строки названы номерами.

- [ ] **Step 1: Тесты**

```csharp
// ChecksumTests
[Theory] public async Task Known_vectors(ChecksumAlgorithm a, string text, string hex)
    // Crc32 "123456789" → "cbf43926"; Md5 "" → "d41d8cd98f00b204e9800998ecf8427e"; Sha1 "abc" → "a9993e364706816aba3e25717850c26c9cd0d89d";
    // Sha256 "abc" → "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
public async Task Large_stream_is_hashed_in_blocks_and_reports_progress()      // SparseStream на 3 ГиБ не нужен: 5 МиБ, ≥5 отчётов
public async Task Cancel_between_blocks_stops_the_hashing()
// ChecksumFileTests
[Theory] public void Format_then_parse_roundtrips(ChecksumAlgorithm a)          // имена с пробелами, кириллицей, подпапками
[Theory] public void Parses_foreign_spellings(string line, string path, string hash)   // "…  a b.txt"; "SHA256 (a.txt) = …"; "dir\\a.txt ABCD1234" для sfv
public void Comments_blank_lines_and_crlf_are_accepted()
public void Garbage_lines_are_reported_by_number_and_do_not_stop_parsing()
// ChecksumOperationTests (InMemoryFileSystem)
public async Task Create_writes_one_line_per_file_with_paths_relative_to_the_checksum_file()
public async Task Create_refuses_an_existing_checksum_file_and_changes_nothing()
public async Task Cancelled_create_leaves_no_file_and_no_part()
public async Task Verify_reports_ok_mismatch_and_missing()
public async Task Verify_never_reads_outside_the_checksum_files_folder()        // "../secret.txt" и "/etc/passwd" → OutsideFolder; журнал чтений источника их не содержит
public async Task Verify_reads_a_windows_1251_checksum_file_with_russian_names()
// ChecksumCommandsTests ([AvaloniaFact])
public async Task Create_is_off_in_a_read_only_source_and_verify_is_off_on_other_files()
public async Task Create_then_verify_reports_everything_ok()
public async Task Verify_shows_mismatches_first()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Checksum"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(tools): create and verify CRC32, MD5, SHA-1 and SHA-256 checksums`.

---

### Task 7: Размеры папок

**Направление:** tools-a. **Зависит от:** 3.

**Files:**
- Create: `src/Tandem.Core/Tools/FolderSizeCalculator.cs`, `src/Tandem.App/ViewModels/Tools/ToolsCommands.FolderSizes.cs`
- Modify: `src/Tandem.App/ViewModels/EntryRowViewModel.cs`, `PanelViewModel.cs`, `PanelViewModel.Selection.cs`, оба файла ресурсов (раздел tools-a)
- Test: `tests/Tandem.Core.Tests/Tools/FolderSizeCalculatorTests.cs`, `tests/Tandem.App.Tests/ViewModels/FolderSizeTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Tools;
public sealed record FolderSize(long Bytes, long Files, long Folders, int Unreadable);
public static class FolderSizeCalculator {
    /// Сумма размеров всех файлов внутри folder. По ссылкам не идёт (ссылка считается своим размером). Подпапка, которую
    /// не удалось прочитать, считается в Unreadable. Обход без рекурсии стека. progress — не чаще 10 раз в секунду.
    public static Task<FolderSize> ComputeAsync(IFileSystemProvider provider, VfsPath folder, IProgress<FolderSize>? progress,
        CancellationToken ct);
}
namespace Tandem.App.ViewModels;
public sealed partial class EntryRowViewModel { public FolderSize? FolderSize { get; } public bool IsSizing { get; } }   // SizeText следует за ними
public sealed partial class PanelViewModel {
    public Task ComputeFolderSizeAsync(int rowIndex);        // папка в этой строке; для файла и «..» — ничего
    public Task ComputeAllFolderSizesAsync();                // все папки списка, по одной
    public bool IsSizing { get; }
}
```

Решения: пока идёт подсчёт, в столбце размера папки — «…»; затем размер обычным форматом, с приставкой «≥» при `Unreadable > 0`. Подсчитанные размеры входят в «выбрано … байт» строки состояния. Размеры живут, пока панель показывает ту же папку (обновление списка их сохраняет для папок, которые остались); переход в другую папку и закрытие вкладки отменяют идущие подсчёты и забывают размеры. Повторный вызов для уже подсчитанной папки считает заново. Сортировка по размеру подсчитанные размеры не учитывает (папки по-прежнему идут первыми по имени). Подсчёт идёт мимо очереди операций и без окна прогресса; для «всех папок» — последовательно, по одной (щадит серверы). Ссылки на папки не считаются. `tools.folderSize` доступна, когда курсор на папке; `tools.folderSizeAll` — когда в списке есть папки.

- [ ] **Step 1: Тесты**

```csharp
// FolderSizeCalculatorTests
public async Task Sums_files_of_the_whole_tree()
public async Task Does_not_follow_links()                              // ссылка на большую папку и петля ссылок: конечное время, размер без цели
public async Task Unreadable_subfolder_is_counted_and_the_rest_is_summed()
public async Task Deep_tree_of_ten_thousand_levels_does_not_overflow()
public async Task Cancel_stops_the_walk()
// FolderSizeTests ([AvaloniaFact])
public async Task Size_of_the_folder_under_the_cursor_appears_in_its_row()
public async Task All_folder_sizes_fill_every_folder_row_and_leave_files_alone()
public async Task Selected_bytes_include_computed_folder_sizes()
public async Task Navigating_away_cancels_and_forgets_sizes()
public async Task Refresh_keeps_sizes_of_folders_that_are_still_there()
public async Task Unreadable_part_is_shown_as_at_least()
public async Task Commands_follow_the_cursor_and_the_listing()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~FolderSize"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS (тесты панели этапов 1–2 не изменены).
- [ ] **Step 5:** Commit: `feat(tools): folder sizes for the cursor folder and for all`.

---

## Направление tools-b (задачи 8–13)

### Task 8: Сравнение папок

**Направление:** tools-b. **Зависит от:** 3.

**Files:**
- Create: `src/Tandem.Core/Tools/Compare/DirectoryComparer.cs`, `src/Tandem.App/ViewModels/Tools/ToolsCommands.CompareDirs.cs`
- Modify: оба файла ресурсов (раздел tools-b)
- Test: `tests/Tandem.Core.Tests/Tools/Compare/DirectoryComparerTests.cs`, `tests/Tandem.App.Tests/ViewModels/CompareDirsTests.cs`

**Interfaces:**
- Consumes: `PanelViewModel.SelectWhere` (задача 3).
- Produces:

```csharp
namespace Tandem.Core.Tools.Compare;
public static class NameKey { public static string Of(string name); }     // Normalize(FormC); сравнение ключей — Ordinal
public sealed record DirectoryDifference(IReadOnlySet<string> MarkLeft, IReadOnlySet<string> MarkRight,
    int UniqueLeft, int UniqueRight, int Differing);                        // в наборах — имена, как они записаны на своей стороне
public static class DirectoryComparer {
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);
    public static DirectoryDifference Compare(IReadOnlyList<FileEntry> left, IReadOnlyList<FileEntry> right, TimeSpan tolerance);
}
```

Правила: пары составляются по `NameKey` (форма Юникода не важна, регистр важен). Имя есть только на одной стороне — помечается там (файл или папка). Файл и файл: отличаются, если разный размер или время изменения расходится больше чем на `tolerance`, — помечаются **на обеих** сторонах. Папка и папка — не помечаются (внутрь не заходим). Файл против папки и ссылки с разной целью — помечаются на обеих. Сравниваются строки, которые панели сейчас показывают (скрытые — если показаны). Команда `tools.compareDirs` доступна, когда обе панели показывают папку без ошибки и ни одна не содержит «чужих» записей (результаты поиска); после сравнения — `SelectWhere` на обеих панелях и сообщение в строке состояния активной панели: «Помечено: слева N, справа M» либо «Папки не отличаются».

- [ ] **Step 1: Тесты**

```csharp
// DirectoryComparerTests
public void Unique_names_are_marked_on_their_side()
public void Files_differing_in_size_or_time_are_marked_on_both_sides()
public void Times_within_two_seconds_are_equal()
public void Same_named_folders_are_not_marked_but_unique_folders_are()
public void File_against_folder_is_marked_on_both_sides()
public void Composed_and_decomposed_spellings_are_one_name()            // RF7: "й" NFC слева, NFD справа, одинаковые размер и время → ничего не помечено
public void Letter_case_makes_different_names()
// CompareDirsTests ([AvaloniaFact])
public async Task Command_marks_both_panels_and_reports_counts()
public async Task Equal_folders_clear_the_marks_and_say_so()
public async Task Command_is_off_when_a_panel_shows_search_results_or_an_error()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~DirectoryComparer|FullyQualifiedName~CompareDirs"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(tools): compare folders, marking different and unique items`.

---

### Task 9: Сравнение файлов: текст и двоичные данные (ядро)

**Направление:** tools-b. **Зависит от:** 3 (и фактов `DiffPlexProbeTests` задачи 2).

**Files:**
- Create: `src/Tandem.Core/Tools/Compare/TextDiff.cs`, `BinaryDiff.cs`, `FileComparison.cs`
- Test: `tests/Tandem.Core.Tests/Tools/Compare/TextDiffTests.cs`, `BinaryDiffTests.cs`, `FileComparisonTests.cs`

**Interfaces:**
- Consumes: DiffPlex (`SideBySideDiffBuilder`), `TextEncodingDetector`, `TextEncodings`.
- Produces:

```csharp
namespace Tandem.Core.Tools.Compare;
public enum DiffKind { Same, Changed, LeftOnly, RightOnly }
public readonly record struct DiffSpan(int Start, int Length);                 // в символах строки
/// Одна строка окна «бок о бок»: номера с единицы; на стороне, где строки нет, номер и текст — null.
public sealed record DiffRow(DiffKind Kind, int? LeftNumber, string? LeftText, int? RightNumber, string? RightText,
    IReadOnlyList<DiffSpan> LeftChanged, IReadOnlyList<DiffSpan> RightChanged);
public sealed record TextDiffResult(IReadOnlyList<DiffRow> Rows, IReadOnlyList<int> DifferenceStarts, bool LineEndingsDiffer);
public static class TextDiff {
    public const int MaxInlineLength = 10_000;      // более длинная изменённая строка подсвечивается целиком
    public static TextDiffResult Compare(string left, string right, bool ignoreWhitespace = false, bool ignoreCase = false);
}
public readonly record struct ByteRange(long Offset, long Length);
public sealed record BinaryDiffResult(long LeftSize, long RightSize, IReadOnlyList<ByteRange> Ranges, bool Truncated, long DifferingBytes) {
    public bool Identical { get; }                  // размеры равны и DifferingBytes == 0
}
public static class BinaryDiff {
    public const int MaxRanges = 1000;
    public static Task<BinaryDiffResult> CompareAsync(Stream left, Stream right, IProgress<long>? bytes, CancellationToken ct);
    /// Быстрое «равны ли»: останавливается на первом отличии. pause — как в Checksum.ComputeAsync.
    public static Task<bool> AreEqualAsync(Stream left, Stream right, IProgress<long>? bytes,
        Func<CancellationToken, ValueTask>? pause, CancellationToken ct);
}
public enum CompareMode { Auto, Text, Binary }
public sealed record FileComparisonResult(TextDiffResult? Text, BinaryDiffResult? Binary,
    TextEncodingInfo? LeftEncoding, TextEncodingInfo? RightEncoding);           // заполнено ровно одно из Text и Binary
public static class FileComparison {
    public const long MaxTextBytes = 16 * 1024 * 1024;
    public static Task<FileComparisonResult> CompareAsync(ProviderRegistry providers, VfsPath left, VfsPath right, CompareMode mode,
        TextEncodingInfo? leftEncoding, TextEncodingInfo? rightEncoding, IProgress<long>? bytes, CancellationToken ct);
}
```

Правила: строки делятся по `\n`, `\r\n` и `\r` и сравниваются без окончаний; если тексты отличаются только окончаниями строк — строк-различий нет, `LineEndingsDiffer == true`. `DifferenceStarts` — индексы первых строк каждого сплошного блока различий (для «следующее/предыдущее отличие»). `Auto`: текст, если оба файла не больше `MaxTextBytes` и оба не выглядят двоичными (`TextEncodingDetector`), иначе двоичное; `Text` для слишком большого файла — `IOException` с понятным типом (приложение переводит). Кодировка каждой стороны — заданная или угаданная; метка BOM в текст не входит. Двоичное сравнение читает оба потока блоками, не загружая файлы в память; `Ranges` — первые `MaxRanges` сплошных отрезков различий в общей части, `DifferingBytes` считается по всей общей части. Файлы читаются через их источники (архив, сервер — без особых случаев).

- [ ] **Step 1: Тесты**

```csharp
// TextDiffTests
public void Equal_texts_have_only_same_rows_and_no_differences()
public void Inserted_line_is_right_only_with_a_gap_on_the_left()         // LeftNumber == null; номера справа идут подряд
public void Deleted_line_is_left_only()
public void Changed_line_pairs_both_sides_and_marks_the_changed_spans()   // "color = red" против "color = blue": span покрывает "red" и "blue"
public void Difference_starts_point_at_each_block_once()
public void Texts_differing_only_in_line_endings_have_no_difference_rows_but_say_so()
public void Ignore_whitespace_and_ignore_case_hide_such_changes()
public void Very_long_changed_line_is_marked_whole()
public void Empty_against_text_and_text_without_final_newline()
// BinaryDiffTests
public async Task Identical_streams_are_identical()
public async Task Reports_ranges_and_differing_byte_count()
public async Task Different_lengths_are_never_identical_and_compare_the_common_part()
public async Task More_than_a_thousand_ranges_are_truncated_but_counted()
public async Task AreEqual_stops_at_the_first_difference()               // читающий поток-счётчик: прочитано не больше двух блоков
public async Task Works_on_non_seekable_streams_and_honours_cancel()
// FileComparisonTests (InMemoryFileSystem)
public async Task Auto_picks_text_for_text_and_binary_for_binary()
public async Task Windows_1251_against_utf8_with_the_same_words_is_equal_text()
public async Task Too_large_for_text_falls_back_to_binary_in_auto()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~TextDiff|FullyQualifiedName~BinaryDiff|FullyQualifiedName~FileComparison"` → FAIL; реализовать; → PASS. Если задача 2 записала, что DiffPlex не укладывается во время на непохожих текстах: при более чем 100 000 строк в сумме `Auto` выбирает двоичное сравнение — добавить тест на это.
- [ ] **Step 5:** Commit: `feat(core): side-by-side text diff and binary compare`.

---

### Task 10: Окно сравнения файлов, команда и «Сравнить» в диалоге конфликта

**Направление:** tools-b. **Зависит от:** 9.

**Files:**
- Create: `src/Tandem.App/ViewModels/Tools/FileCompareViewModel.cs`, `ToolsCommands.CompareFiles.cs`, `Views/Tools/FileCompareWindow.axaml(.cs)`, `Services/ToolWindows.Compare.cs`
- Modify: `src/Tandem.App/App.axaml` (цвета различий), `Views/Dialogs/ConflictDialog.cs`, оба файла ресурсов (раздел tools-b)
- Test: `tests/Tandem.App.Tests/ViewModels/FileCompareTests.cs`, `Views/FileCompareWindowTests.cs`, `Views/ConflictCompareTests.cs` (новый файл)

**Interfaces:**
- Consumes: `FileComparison`, `TextDiffResult`, `BinaryDiffResult` (задача 9); `HexDump` (этап 2); `IToolWindows`, `IDialogService.CompareFiles` (задача 3).
- Produces:

```csharp
namespace Tandem.App.ViewModels.Tools;
public sealed partial class FileCompareViewModel : ObservableObject, IDialogModel<bool>, IAsyncDisposable {
    public static Task<FileCompareViewModel> OpenAsync(ProviderRegistry providers, VfsPath left, VfsPath right, CancellationToken ct);
    public string LeftTitle { get; } public string RightTitle { get; }        // ProviderRegistry.DisplayPath
    public CompareMode Mode { get; set; }                                     // смена перечитывает вне потока интерфейса
    public TextEncodingInfo? LeftEncoding { get; set; } public TextEncodingInfo? RightEncoding { get; set; }
    public IReadOnlyList<DiffRow> Rows { get; }                               // текстовый режим
    public IReadOnlyList<BinaryRangeRow> Ranges { get; }                      // двоичный: смещение, длина, по 16 байтов каждой стороны (HexDump)
    public string Summary { get; }                                            // «Отличий: N» / «Файлы одинаковы» / «Размеры разные: …»
    public int CurrentRow { get; set; }
    public void NextDifference(); public void PreviousDifference();
    public bool IsBusy { get; } public string? Error { get; }
}
```

Решения:
- Окно: один виртуализированный список строк, две колонки моноширинным шрифтом с номерами строк; фон строки и подсветка изменённых отрезков — из новых ресурсов темы `Tandem.DiffChangedBackground`, `Tandem.DiffAddedBackground`, `Tandem.DiffRemovedBackground`, `Tandem.DiffInlineBackground` (светлый и тёмный вариант каждого). Клавиши окна: Alt+↓ / Alt+↑ — следующее и предыдущее отличие, Cmd+1 / Cmd+2 — текст и двоичный режим, Esc — закрыть. Окно только показывает; правки нет.
- Какие файлы сравнивает `tools.compareFiles`: если в активной панели помечено ровно два файла — их; иначе файл под курсором активной панели и файл с тем же именем в папке пассивной панели, а если такого нет — файл под курсором пассивной панели. Иначе команда недоступна. Окно немодальное (`IToolWindows.Show`); чтение идёт вне потока интерфейса, окно открывается сразу с признаком `IsBusy`.
- Диалог конфликта: когда задано `IDialogService.CompareFiles` и оба элемента — файлы, диалог показывает кнопку `Conflict_Compare` («Сравнить…»). Кнопка открывает окно сравнения модально к диалогу (`ShowDialogAsync`); диалог конфликта остаётся открытым, операция ждёт; после закрытия окна сравнения пользователь выбирает действие как раньше. `ToolsCommands` при создании присваивает `services.Dialogs.CompareFiles`, при освобождении — обнуляет.

- [ ] **Step 1: Тесты**

```csharp
// FileCompareTests ([AvaloniaFact])
public async Task Text_files_show_rows_and_a_difference_count()
public async Task Next_and_previous_difference_move_between_blocks_and_stop_at_the_ends()
public async Task Binary_files_show_ranges_with_hex_of_both_sides()
public async Task Switching_mode_or_encoding_recompares()
public async Task Missing_or_unreadable_file_is_told_in_words_and_does_not_throw()
public async Task Command_picks_two_marked_files_else_same_name_in_the_other_panel_else_both_cursors()
public async Task Command_is_off_on_folders_and_when_there_is_no_pair()
public async Task Compares_a_file_in_an_archive_with_a_file_on_disk()        // источники без особых случаев
// FileCompareWindowTests
[AvaloniaFact] public async Task Window_lists_rows_in_two_columns_and_closes_on_Escape()
[AvaloniaFact] public void Diff_colours_exist_in_light_and_dark_themes()
// ConflictCompareTests
[AvaloniaFact] public async Task Conflict_dialog_offers_compare_for_two_files_and_stays_open_after_it()
[AvaloniaFact] public async Task Conflict_dialog_has_no_compare_without_the_hook_or_for_a_link()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~FileCompare|FullyQualifiedName~ConflictCompare|FullyQualifiedName~DialogWindow"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(tools): file compare window and Compare in the conflict dialog`.

---

### Task 11: Синхронизация папок: сравнение и план

**Направление:** tools-b. **Зависит от:** 9 (`BinaryDiff.AreEqualAsync`), 8 (`NameKey`).

**Files:**
- Create: `src/Tandem.Core/Tools/Sync/SyncModel.cs`, `SyncPlanner.cs`
- Test: `tests/Tandem.Core.Tests/Tools/Sync/SyncPlannerTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Tools.Sync;
public enum SyncCompareMode { SizeAndDate, Content }
public sealed record SyncOptions(SyncCompareMode Mode = SyncCompareMode.SizeAndDate, bool Subfolders = true,
    string Mask = "*", bool IncludeHidden = true) { public TimeSpan Tolerance { get; init; } = TimeSpan.FromSeconds(2); }
public enum SyncState { Equal, LeftNewer, RightNewer, Different, LeftOnly, RightOnly, KindMismatch }
public enum SyncAction { None, CopyToRight, CopyToLeft, DeleteLeft, DeleteRight }
/// Файл, ссылка или пустая папка, существующая только на одной стороне. RelativePath — через '/', в написании стороны, где элемент есть
/// (левой, если есть на обеих).
public sealed record SyncItem(string RelativePath, FileEntry? Left, FileEntry? Right, SyncState State) {
    public SyncAction DefaultAction { get; }
    public IReadOnlyList<SyncAction> AllowedActions { get; }
}
public sealed record SyncComparison(VfsPath LeftRoot, VfsPath RightRoot, SyncOptions Options, IReadOnlyList<SyncItem> Items,
    IReadOnlyList<string> UnreadableFolders);
public readonly record struct SyncProgress(int FoldersScanned, int FilesCompared, long BytesCompared);
public static class SyncPlanner {
    /// <exception cref="ArgumentException">Корни совпадают или один лежит в другом (в том числе как один и тот же объект под другим путём).</exception>
    public static Task<SyncComparison> CompareAsync(ProviderRegistry providers, VfsPath leftRoot, VfsPath rightRoot, SyncOptions options,
        IProgress<SyncProgress>? progress, CancellationToken ct);
}
```

Правила:
- Пары — по `NameKey` на каждом уровне. Папка с папкой — обход внутрь (при `Subfolders`); элементом они не становятся. Папка только на одной стороне — её файлы становятся элементами `LeftOnly`/`RightOnly`; пустая — один элемент-папка. Файл против папки — `KindMismatch`.
- `SizeAndDate`: размеры равны и времена в пределах `Tolerance` → `Equal`; иначе более позднее время → `LeftNewer`/`RightNewer`; время в пределах допуска при разном размере → `Different`.
- `Content`: при равных размерах файлы читаются и сравниваются (`BinaryDiff.AreEqualAsync`): равны → `Equal` при любом времени; не равны или размеры разные → по времени, как выше (`Different`, когда время в пределах допуска).
- Ссылки сравниваются по тексту цели; внутрь ссылок на папки обход не идёт.
- Действие по умолчанию: `LeftNewer`, `LeftOnly` → `CopyToRight`; `RightNewer`, `RightOnly` → `CopyToLeft`; остальное → `None`. **Удаление никогда не бывает действием по умолчанию.** Разрешено: для элемента на обеих сторонах — `None`, `CopyToRight`, `CopyToLeft`; `LeftOnly` — `None`, `CopyToRight`, `DeleteLeft`; `RightOnly` — `None`, `CopyToLeft`, `DeleteRight`; `KindMismatch` — только `None`. Копирование в сторону, чей источник не пишет или (для ссылки) не хранит ссылки, из разрешённых исключается.
- Папка, которую не удалось прочитать на одной из сторон: её путь — в `UnreadableFolders`, и **всё поддерево исключается на обеих сторонах** (ни одного элемента из него).
- Маска применяется к именам файлов (папки обходятся все); скрытые — по `IncludeHidden`.
- Служебные имена `.*.tandem-part`, `.*.tandem-old`, `.*.tandem-rename` в сравнение не попадают.

- [ ] **Step 1: Тесты**

```csharp
public async Task Equal_trees_have_only_equal_items()
public async Task Newer_side_wins_by_default_and_unique_files_are_copied_across()
public async Task Same_time_other_size_is_different_with_no_default_action()
public async Task Time_within_tolerance_is_equal()
public async Task Content_mode_calls_same_size_files_equal_whatever_their_dates()
public async Task Content_mode_finds_a_difference_hidden_behind_equal_size_and_date()
public async Task Unique_folder_gives_its_files_and_an_empty_one_gives_itself()
public async Task File_against_folder_is_a_mismatch_with_no_action_allowed()
public async Task No_default_action_is_ever_a_delete()                              // RF3: по всем состояниям
public async Task Unreadable_folder_excludes_its_subtree_on_both_sides()            // RF3: содержимое другой стороны не «уникально»
public async Task Composed_and_decomposed_names_pair_up()                           // RF7
public async Task Without_subfolders_only_the_top_level_is_compared()
public async Task Mask_filters_files_and_hidden_files_follow_the_option()
public async Task Read_only_side_allows_no_copy_towards_it()
public async Task Same_or_nested_roots_are_refused()                                // и /a против /A на томе без учёта регистра (IsSameEntryAsync)
public async Task Leftover_part_and_backup_files_are_ignored()
public async Task Cancel_stops_the_comparison()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~SyncPlanner"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): folder synchronization comparison and plan`.

---

### Task 12: Операция синхронизации

**Направление:** tools-b. **Зависит от:** 11.

**Files:**
- Create: `src/Tandem.Core/Tools/Sync/SyncOperation.cs`
- Test: `tests/Tandem.Core.Tests/Tools/Sync/SyncOperationTests.cs`

**Interfaces:**
- Consumes: `SyncComparison`, `SyncItem`, `SyncAction` (задача 11); `CopyPlanner`, `CopyEngine`, `ItemErrors`, `FileOperation.RunInBatchAsync` (ядро; внутренние типы доступны, операция живёт в `Tandem.Core`); `ITrash`, `INativeCopy`.
- Produces:

```csharp
namespace Tandem.Core.Tools.Sync;
public sealed record SyncDecision(SyncItem Item, SyncAction Action);
public sealed class SyncOperation : FileOperation {                          // Kind == Sync; Sources == [LeftRoot]; Destination == RightRoot
    /// <exception cref="ArgumentException">Действие не входит в AllowedActions своего элемента.</exception>
    public SyncOperation(ProviderRegistry providers, SyncComparison comparison, IReadOnlyList<SyncDecision> decisions,
        IOperationInteraction interaction, ITrash? trash = null, INativeCopy? nativeCopy = null);
}
```

Правила:
- Решения с `None` не делают ничего. Порядок: копирование вправо, копирование влево, удаления.
- **Перед каждым действием обе стороны элемента перечитываются.** Сторона «та же», если вид, размер и время изменения совпадают с записанными при сравнении (отсутствовала — и сейчас отсутствует). Копирование выполняется, только если источник тот же и то, что будет заменено, то же; удаление — только если удаляемое то же. Иначе элемент пропускается с `SkipReason.ChangedSinceCompared` и остаётся нетронутым.
- Копирование идёт обычным движком (`CopyEngine`): временное имя, `SafeReplace`, время изменения и права как при F5; недостающие папки создаются. Вопрос о конфликте пользователю не задаётся: замена решена в плане; конфликт, которого в плане не было (цель появилась после сравнения), — пропуск `ChangedSinceCompared`.
- Удаление: в Корзину, когда источник объявляет `Trash` и служба корзины дана; иначе безвозвратно. Удаляется сам элемент (файл, ссылка, пустая папка); папки, опустевшие после удаления файлов, остаются.
- Ошибки ввода-вывода на элементе — через `ItemErrors` (повторить — в том числе после потери связи, пропустить, прервать).
- Всё идёт внутри `RunInBatchAsync` по обоим корням (сторона-архив zip переписывается один раз в конце).
- Ключи томов — обоих корней. Итоги хода: файлы и байты копируемого; удаления — по одному «файлу».

- [ ] **Step 1: Тесты** (`InMemoryFileSystem`, `BatchingProvider`, `ScriptedInteraction`)

```csharp
public async Task Copies_in_both_directions_and_then_trees_compare_equal()        // повторный SyncPlanner.CompareAsync: все Equal
public async Task Overwrites_the_older_file_without_asking()
public async Task Preserves_modified_time_so_the_next_comparison_is_equal()
public async Task Creates_missing_folders_on_the_way()
public async Task None_touches_nothing()
public async Task Delete_goes_to_trash_when_the_side_has_one_and_is_permanent_otherwise()
public async Task Source_changed_since_comparison_is_skipped_untouched()           // RF3
public async Task Destination_changed_since_comparison_is_not_overwritten()        // RF3: содержимое цели прежнее, пропуск ChangedSinceCompared
public async Task File_that_appeared_at_the_destination_is_not_overwritten()       // RF3: LeftOnly → справа появился файл
public async Task File_to_delete_that_changed_is_not_deleted()                     // RF3
public async Task Disallowed_action_is_refused_before_anything_changes()
public async Task Cancel_keeps_finished_items_and_leaves_no_part_files()
public async Task Interrupted_overwrite_keeps_the_old_file()                        // FailWriteAfter на part → цель прежняя, нет .tandem-part и .tandem-old
public async Task Error_on_an_item_can_be_skipped_and_the_rest_goes_on()
public async Task Batching_side_commits_once()
public async Task Symlink_is_copied_as_a_link()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~SyncOperation"` → FAIL; реализовать; → PASS; `dotnet test tests/Tandem.Core.Tests` → PASS.
- [ ] **Step 5:** Commit: `feat(core): folder synchronization through the operations engine`.

---

### Task 13: Окно синхронизации

**Направление:** tools-b. **Зависит от:** 12.

**Files:**
- Create: `src/Tandem.App/ViewModels/Tools/SyncViewModel.cs`, `ToolsCommands.Sync.cs`, `Views/Tools/SyncWindow.axaml(.cs)`, `Services/ToolWindows.Sync.cs`
- Modify: оба файла ресурсов (раздел tools-b)
- Test: `tests/Tandem.App.Tests/ViewModels/SyncViewModelTests.cs`, `Views/SyncWindowTests.cs`

**Interfaces:**
- Consumes: `SyncPlanner`, `SyncOperation`, `SyncDecision` (задачи 11–12); `FileCommands.StartOperation`; `IToolWindows`.
- Produces:

```csharp
namespace Tandem.App.ViewModels.Tools;
public sealed partial class SyncRowViewModel : ObservableObject {
    public SyncItem Item { get; } public SyncAction Action { get; set; }      // присваивание неразрешённого действия не меняет ничего
    public string LeftText { get; } public string RightText { get; }          // размер и дата стороны или пусто
}
public sealed record SyncTotals(int ToRight, long ToRightBytes, int ToLeft, long ToLeftBytes, int DeleteLeft, int DeleteRight);
public sealed record SyncConfirmation(SyncTotals Totals, bool LeftToTrash, bool RightToTrash);
public sealed partial class SyncViewModel : ObservableObject, IDisposable {
    public SyncViewModel(ProviderRegistry providers, VfsPath leftRoot, VfsPath rightRoot,
        Func<SyncOperation, Task<OperationResult?>> run, Func<SyncConfirmation, Task<bool>> confirm, ITrash? trash, INativeCopy? nativeCopy,
        IOperationInteraction interaction);
    // параметры: ByContent, Subfolders, Mask, IncludeHidden; фильтры показа: ShowEqual (false), ShowToRight, ShowToLeft, ShowDifferent
    public IReadOnlyList<SyncRowViewModel> Rows { get; } public SyncTotals Totals { get; }
    public bool IsComparing { get; } public string Status { get; } public bool CanSynchronize { get; }
    public Task CompareAsync(); public void StopComparing(); public Task SynchronizeAsync();
}
```

Решения:
- `tools.syncDirs` доступна, когда обе панели показывают папку без ошибки, ни одна не содержит «чужих» записей и папки не совпадают; открывает немодальное окно (одно; повторный вызов выводит его вперёд) для папки левой и правой панели. Сравнение запускается кнопкой «Сравнить», идёт вне потока интерфейса с показом хода («папок N, файлов M») и кнопкой «Стоп».
- Список виртуализирован. Строка: путь, сведения слева, значок действия (→, ←, =, ✕ слева, ✕ справа), сведения справа. Клавиши окна: → и ← — направление копирования, Пробел — следующее из разрешённых действий без удаления, Fn+Delete (Delete) — удаление для элемента одной стороны, Cmd+A — выделить все; действие применяется к выделенным строкам. Равные элементы скрыты, пока не включён `ShowEqual`.
- Итоги под списком: «→ N файлов, X МБ · ← M файлов, Y МБ · удалить: слева A, справа B».
- «Синхронизировать» доступна, когда есть хоть одно действие, отличное от `None`. Сначала подтверждение (`confirm`): те же итоги; при удалениях — отдельная строка «Будет удалено: слева A (в Корзину / безвозвратно), справа B (…)» и кнопка по умолчанию — «Отменить». Затем `SyncOperation` через `FileCommands.StartOperation` (очередь, окно прогресса, сводка пропущенного), после её завершения — автоматическое повторное сравнение.
- Нечитаемые папки названы строкой над списком («Не прочитано папок: K — их содержимое не сравнивалось»).

- [ ] **Step 1: Тесты**

```csharp
// SyncViewModelTests ([AvaloniaFact], TestApp)
public async Task Compare_lists_differences_with_default_directions_and_hides_equal_items()
public async Task Direction_can_be_changed_per_row_only_to_allowed_actions()
public async Task Totals_follow_the_chosen_actions()
public async Task Synchronize_asks_first_and_does_nothing_when_declined()
public async Task Confirmation_names_deletions_and_whether_they_go_to_the_trash()      // RF3
public async Task Synchronize_runs_through_the_queue_and_recompares_afterwards()
public async Task Unreadable_folders_are_named_above_the_list()
public async Task Stop_ends_a_running_comparison()
public async Task Command_is_off_for_the_same_folder_and_for_search_results()
public async Task Sync_between_a_folder_and_a_zip_archive_works()                       // источник-архив, запись одним пакетом
// SyncWindowTests
[AvaloniaFact] public async Task Arrow_keys_and_space_change_the_action_of_the_selected_rows()
[AvaloniaFact] public async Task Confirmation_with_deletions_has_cancel_as_the_default_button()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~SyncViewModel|FullyQualifiedName~SyncWindow"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(tools): folder synchronization window`.

---

## Направление net (задачи 14–23)

После задачи 14 направление ведётся тремя ветвями, каждая в своей рабочей копии от коммита задачи 14 (ветви 15, 18 и 19 можно начать раньше — от коммита задачи 3): **net-ftp** — 15 → 16 (`src/Tandem.Net/Ftp/**`, `tests/Tandem.Net.Tests/Ftp/**`); **net-sftp** — 17 (`src/Tandem.Net/Sftp/**`, `tests/Tandem.Net.Tests/Sftp/**`); **net-app** — 18 → 19 → 20 → 21 (`Tandem.Platform.Mac`, `src/Tandem.Net/Connections/**`, файлы приложения из таблицы). Ветви сливаются в `net`, затем идут задачи 22 и 23.

### Task 14: Источник «сервер»: `RemoteProvider` поверх `IRemoteSession`

Вся логика, общая для FTP и SFTP (пул соединений, переподключение, запрет замены при переименовании, рекурсивное удаление, договор источника), живёт здесь и проверяется на поддельном сервере в памяти. Протоколы потом лишь реализуют узкий `IRemoteSession`.

**Направление:** net. **Зависит от:** 2, 3.

**Files:**
- Create: `src/Tandem.Net/ConnectionProfile.cs`, `NetExceptions.cs`, `Trust.cs`, `Remote/IRemoteSession.cs`, `Remote/RemoteProvider.cs`, `Remote/SessionPool.cs`, `Remote/CredentialLoop.cs`, `tests/Tandem.Net.Tests/Remote/FakeRemoteServer.cs`
- Delete: `src/Tandem.Net/AssemblyMarker.cs`
- Test: `tests/Tandem.Net.Tests/Remote/RemoteProviderContractTests.cs`, `RemoteProviderTests.cs`, `RemoteOperationTests.cs`, `CredentialLoopTests.cs`, `ConnectionProfileTests.cs`

**Interfaces:**
- Consumes: `IFileSystemProvider`, `IHostedProvider`, `HostLocation`, `VfsConnectionLostException`, `ProviderCapabilities.LocalCopyEdit` (задача 1); `ProviderContractTests` (общий набор).
- Produces:

```csharp
namespace Tandem.Net;
public enum NetProtocol { Ftp, FtpsExplicit, FtpsImplicit, Sftp }
public enum NetAuth { Password, PrivateKey, Anonymous }
public sealed record ConnectionProfile(Guid Id, string Name, NetProtocol Protocol, string Host, int Port, string User, NetAuth Auth,
    string? PrivateKeyPath = null, string InitialPath = "/", bool PassiveMode = true, string? NameEncoding = null) {
    public static int DefaultPort(NetProtocol protocol);        // Ftp 21, FtpsExplicit 21, FtpsImplicit 990, Sftp 22
    public string Address { get; }                              // "sftp://user@host" или "ftp://user@host:2121" (порт — только не по умолчанию); пароля нет никогда
    public string VolumeKey { get; }                            // Address в нижнем регистре; без обращения к сети
    public ConnectionProfile Normalized();                      // обрезка пробелов; порт вне 1…65535 → по умолчанию; InitialPath — абсолютный; Anonymous → User "anonymous"
}
public sealed record NetCredentials(string? Password, byte[]? PrivateKey, string? Passphrase);
public sealed record HostKeyInfo(string Algorithm, string Sha256);            // отпечаток как "SHA256:…"
public sealed record CertificateInfo(string Subject, string Issuer, string Sha256, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Problems);
public interface ITrustDecisions {
    Task<bool> TrustHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, CancellationToken ct);
    Task<bool> TrustCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct);
}
public interface ICredentialSource {
    /// null — пользователь отказался. previousWasWrong: предыдущие данные сервер не принял.
    Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct);
    void Accepted(ConnectionProfile profile, NetCredentials credentials);      // сервер принял: можно запомнить
}
/// Сервер не принял имя, пароль или ключ. Declined: пользователь сам отказался их вводить (сообщение не нужно).
public sealed class NetAuthenticationException : VfsAccessDeniedException { public bool Declined { get; } }
/// Ключ узла или сертификат не подтверждён.
public sealed class NetTrustException : VfsAccessDeniedException;

namespace Tandem.Net.Remote;
[Flags] public enum RemoteFeatures { None = 0, Symlinks = 1, Modes = 2, SetModified = 4, Multiplexed = 8 }
public sealed record RemoteEntry(string Name, EntryKind Kind, long Size, DateTimeOffset Modified, UnixFileMode? Mode,
    string? LinkTarget = null, bool LinkPointsToDirectory = false);
/// Одно соединение с сервером. Пути — абсолютные, через '/'. Ошибки — исключения ядра: VfsNotFoundException,
/// VfsAccessDeniedException, VfsAlreadyExistsException, VfsConnectionLostException (связь), NetAuthenticationException, NetTrustException.
/// Без Multiplexed соединение выполняет один вызов за раз, а открытый поток занимает его до закрытия.
public interface IRemoteSession : IAsyncDisposable {
    RemoteFeatures Features { get; }                              // после ConnectAsync
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    IAsyncEnumerable<RemoteEntry> ListAsync(string directory, CancellationToken ct);   // нет папки → VfsNotFoundException; без "." и ".."
    Task<RemoteEntry?> StatAsync(string path, CancellationToken ct);                    // сама ссылка, не её цель; нет → null
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct);                     // создаёт или обрезает; закрытие потока завершает запись и бросает её ошибку
    Task CreateDirectoryAsync(string path, CancellationToken ct);                       // один уровень
    Task DeleteFileAsync(string path, CancellationToken ct);                            // файл или ссылка
    Task DeleteDirectoryAsync(string path, CancellationToken ct);                       // только пустая
    Task RenameAsync(string from, string to, CancellationToken ct);                     // может заменить существующее: вызывающий проверяет сам
    Task CreateSymlinkAsync(string path, string target, CancellationToken ct);
    Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken ct);  // без SetModified — ничего не делает
    Task SetModeAsync(string path, UnixFileMode mode, CancellationToken ct);
}
public interface IRemoteSessionFactory {
    IRemoteSession Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust);
}
public static class CredentialLoop {
    public const int MaxAttempts = 3;
    /// Берёт данные у source и вызывает attempt; NetAuthenticationException от attempt → снова, с previousWasWrong. Успех → source.Accepted.
    /// Отказ пользователя → NetAuthenticationException с Declined; три неудачи → NetAuthenticationException.
    public static Task RunAsync(ConnectionProfile profile, ICredentialSource source, Func<NetCredentials, Task> attempt, CancellationToken ct);
}
public sealed class RemoteProvider : IFileSystemProvider, IHostedProvider, IAsyncDisposable {
    public const int MaxSessions = 4;
    public RemoteProvider(string id, ConnectionProfile profile, Func<IRemoteSession> newSession, HostLocation origin);
    public ConnectionProfile Profile { get; }
    public VfsPath Root { get; }                                  // new VfsPath(id, "/")
    public Task ConnectAsync(CancellationToken ct);               // открывает первое соединение: вопросы о пароле и ключе приходят здесь
    public Task CloseConnectionsAsync();                          // свободные закрываются сразу, занятые — когда освободятся
    public bool HasOpenConnections { get; }
}
```

Правила `RemoteProvider`:
- `Capabilities` = `Write | LocalCopyEdit`, плюс `Symlinks` и `UnixPermissions`, когда соединение объявило `Symlinks` и `Modes`. Корзины и слежения нет (`Watch` возвращает null).
- `IHostedProvider`: «вверх» из корня сервера ведёт в `origin` (папку, из которой подключались), `FocusName` — null; `GetDisplayPath` = `Profile.Address` + путь. Поэтому сохранённая вкладка на сервере после перезапуска открывается как `origin` — подключение при запуске не выполняется.
- `GetVolumeKeyAsync` возвращает `Profile.VolumeKey` без обращения к сети: операции на одном сервере встают в очередь друг за другом, а недоступный сервер не задерживает очередь.
- Пул: до `MaxSessions` соединений; вызов берёт свободное или открывает новое, при исчерпании ждёт (с отменой). Поток чтения или записи держит своё соединение до закрытия. С `Multiplexed` — одно соединение на всё.
- **Переподключение.** Соединение, оказавшееся мёртвым в начале вызова (сервер закрыл его за простой), выбрасывается, открывается новое, вызов повторяется один раз — пользователь ничего не видит. Если и новое не открылось либо связь оборвалась посреди потока — `VfsConnectionLostException`. Следующий вызов снова пробует подключиться: «Переподключиться и повторить» в диалоге ошибки и кнопка панели не требуют ничего, кроме повтора вызова.
- `MoveAsync`: если цель существует (`StatAsync`) — `VfsAlreadyExistsException`, кроме случая, когда оба пути равны; затем `RenameAsync`. Между проверкой и переименованием на сервере есть зазор, закрыть который протокол не даёт, — отметить в комментарии.
- `DeleteAsync` с `recursive`: обход дерева с удалением снизу вверх; ссылки удаляются как файлы, внутрь не заходим. Без `recursive` непустая папка — `IOException`.
- `CreateDirectoryAsync` создаёт недостающих предков; существующая папка — не ошибка, существующий файл — `VfsAlreadyExistsException`. `OpenWriteAsync` при отсутствующей папке — `VfsNotFoundException`.
- `IsSameEntryAsync`: оба существуют и пути равны. `IsHidden` — имя с точки. `Created` = `Modified`.
- Отмена любого вызова — `OperationCanceledException`.

`FakeRemoteServer` (тесты): дерево в памяти, общее для всех выданных им соединений (`NewSession()`), и ручки: `Features`, `Offline` (подключение не удаётся), `DropNow()` (все соединения мертвы; новые открываются), `DropAfterBytes` (обрыв потока после N байтов), `RenameOverwrites`, `FailCloseWrite` (исключение при закрытии потока записи), `FailOn(имя вызова, путь)`, `ConnectCount`, `MaxOpenAtOnce`, `Log`. Одно соединение без `Multiplexed` бросает `InvalidOperationException` при втором одновременном вызове — так тесты ловят ошибки пула.

- [ ] **Step 1: Тесты**

```csharp
// RemoteProviderContractTests : ProviderContractTests — два класса: сервер со всеми возможностями и сервер без Symlinks/Modes/Multiplexed
// RemoteProviderTests
public async Task Idle_connection_closed_by_the_server_is_replaced_silently()            // RF2: DropNow(), затем листинг — успех, ConnectCount == 2
public async Task Offline_server_is_connection_lost_and_works_again_when_it_returns()
public async Task Drop_in_the_middle_of_a_read_or_write_is_connection_lost()
public async Task Error_at_closing_a_written_stream_is_not_swallowed()                    // RF1
public async Task Move_onto_an_existing_name_throws_even_if_the_server_would_replace_it()  // RF2: содержимое цели прежнее
public async Task Recursive_delete_removes_the_tree_and_does_not_enter_links()
public async Task CreateDirectory_makes_missing_parents()
public async Task Two_open_streams_use_two_connections()                                   // FakeRemoteServer не видел двух вызовов на одном соединении
public async Task A_fifth_stream_waits_for_a_free_connection_and_can_be_cancelled()
public async Task Multiplexed_server_gets_one_connection()
public async Task Volume_key_opens_no_connection()                                         // ConnectCount == 0
public void Up_from_the_root_leads_to_the_origin_and_persistent_location_is_the_origin()  // через ProviderRegistry
public void Display_path_is_address_plus_path_without_a_password()
public async Task Capabilities_follow_the_server_features()
public async Task CloseConnections_then_a_call_reconnects()
// CredentialLoopTests
public async Task Wrong_credentials_are_asked_again_at_most_three_times()
public async Task Declined_credentials_end_as_declined()
public async Task Accepted_credentials_are_reported_to_the_source()
// ConnectionProfileTests
[Theory] public void Address_shows_the_port_only_when_it_is_not_the_default(...)
public void Normalized_fixes_port_path_and_anonymous_user()
// RemoteOperationTests (движок ядра поверх RemoteProvider)
public async Task Upload_cut_off_and_retried_completes_and_leaves_no_part_or_backup()      // RF1: CopyOperation, DropAfterBytes, Retry
public async Task Upload_cut_off_and_skipped_keeps_the_previous_file_under_its_name()      // RF1
public async Task Overwrite_whose_final_rename_fails_puts_the_old_file_back()               // RF2: FailOn("Rename", part→цель)
public async Task Move_to_the_server_deletes_local_sources_only_after_the_upload_is_checked()
public async Task Download_cut_off_leaves_no_part_file_on_disk()
public async Task Operations_on_one_server_queue_up_and_on_two_servers_run_side_by_side()   // OperationQueue по VolumeKey
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Net.Tests --filter "FullyQualifiedName~Remote|FullyQualifiedName~CredentialLoop|FullyQualifiedName~ConnectionProfile"` → FAIL.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Run `dotnet build Tandem.slnx && dotnet test tests/Tandem.Net.Tests` → 0 предупреждений, PASS (пробы задачи 2 остаются зелёными).
- [ ] **Step 5:** Commit: `feat(net): remote provider with connection pool and reconnect`.

---

### Task 15: Тестовый FTP-сервер в процессе тестов и пробы FluentFTP

**Направление:** net / net-ftp. **Зависит от:** 2.

**Files:**
- Create: `tests/Tandem.Net.Tests/Ftp/TestFtpServer.cs`
- Test: `tests/Tandem.Net.Tests/Ftp/TestFtpServerTests.cs`, `Ftp/FluentFtpServerProbeTests.cs`

**Interfaces:**
- Produces (только для тестов):

```csharp
internal sealed class TestFtpOptions {
    public bool Mlsd = true, Mfmt = true, Utf8 = true, RenameOverwrites = true;
    public long? DropDataAfterBytes;          // оборвать канал данных после N байтов передачи
    public int? StoreCompletionCode;          // ответ после приёма файла вместо 226 (например 552)
    public bool HangAfterLogin;               // перестать отвечать (проверка пределов времени)
    public Encoding NameEncoding = Encoding.UTF8;
}
internal sealed class TestFtpServer : IAsyncDisposable {
    public static Task<TestFtpServer> StartAsync(string rootDirectory, TestFtpOptions? options = null);   // слушает 127.0.0.1, порт 0
    public int Port { get; } public string User => "user"; public string Password => "pass";
    public TestFtpOptions Options { get; }
    public IReadOnlyList<string> CommandLog { get; }      // команды в порядке прихода; аргумент PASS не записывается
    public int OpenControlConnections { get; }
    public void DropAllConnections();
}
```

Сервер — около 500 строк на `TcpListener`: команды `USER`, `PASS`, `SYST`, `FEAT`, `OPTS UTF8 ON`, `PWD`, `CWD`, `CDUP`, `TYPE` (только `I`; на `A` — 504), `PASV`, `EPSV`, `LIST` (вид Unix `ls -l`), `MLSD`, `MLST`, `SIZE`, `MDTM`, `MFMT`, `RETR`, `STOR`, `DELE`, `MKD`, `RMD`, `RNFR`/`RNTO`, `REST 0`, `NOOP`, `QUIT`; прочее — 502. `AUTH TLS` — 502 (TLS сервер не умеет). Только пассивный режим, каналы данных — на `127.0.0.1`. Хранилище — настоящие файлы в `rootDirectory` (временная папка теста); путь клиента не может выйти из неё. Несколько клиентов одновременно.

- [ ] **Step 1: Тесты**

```csharp
// TestFtpServerTests — сервер проверяется «сырым» TcpClient, без FluentFTP
public async Task Greets_logs_in_and_refuses_a_wrong_password()
public async Task Stores_and_retrieves_bytes_over_a_passive_data_connection()
public async Task Never_serves_a_path_outside_its_root()
public async Task Listens_on_loopback_only()                                   // LocalEndPoint.Address == 127.0.0.1
// FluentFtpServerProbeTests — закрепляют поведение FluentFTP 55.1.0 (факты записать в комментариях тестов)
public async Task Connects_and_logs_in_and_reports_a_wrong_password_recognisably()      // тип исключения
public async Task Lists_with_MLSD_and_with_plain_LIST()                                   // виды, размеры, время (UTC), имена с пробелом и кириллицей
public async Task Listing_a_missing_folder()                                              // исключение или пустой список — закрепить
public async Task Stream_download_and_upload_roundtrip_all_byte_values()                  // OpenRead/OpenWrite; что нужно вызвать после закрытия потока
public async Task Upload_refused_at_the_end_is_reported()                                 // StoreCompletionCode = 552: где и каким исключением
public async Task Rename_onto_an_existing_file_with_and_without_server_overwrite()
public async Task Modified_time_with_and_without_MFMT()
public async Task Data_connection_cut_in_the_middle_of_a_transfer()                       // типы исключений при чтении и при записи
public async Task Control_connection_closed_while_idle_then_a_command()                   // тип исключения; IsConnected после этого
public async Task Server_that_stops_answering_times_out()                                 // ReadTimeout = 2 с
public async Task Cancellation_stops_a_transfer()
public async Task Requesting_explicit_TLS_from_a_server_without_it_fails_before_PASS()    // CommandLog без USER и PASS
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Net.Tests --filter "FullyQualifiedName~Ftp"` → FAIL.
- [ ] **Step 3:** Написать сервер, затем пробы. **Если FluentFTP не удаётся заставить работать с этим сервером за разумное число правок сервера (клиент требует команду, которой нет в списке) — добавить команду в сервер; если клиент не умеет потоков чтения и записи с отменой или не сообщает об отказе в конце записи — остановиться и доложить.**
- [ ] **Step 4:** Run `dotnet test tests/Tandem.Net.Tests --filter "FullyQualifiedName~Ftp"` → PASS; запустить трижды подряд — все три PASS (сетевые тесты не должны быть случайными).
- [ ] **Step 5:** Commit: `test(net): in-process FTP test server and FluentFTP probes`.

---

### Task 16: `FtpSession` — FTP и FTPS

**Направление:** net / net-ftp. **Зависит от:** 14, 15.

**Files:**
- Create: `src/Tandem.Net/Ftp/FtpSession.cs`, `Ftp/FtpSessionFactory.cs`, `Ftp/FtpSettings.cs`
- Test: `tests/Tandem.Net.Tests/Ftp/FtpContractTests.cs`, `FtpSessionTests.cs`, `FtpSettingsTests.cs`, `FtpOperationTests.cs`

**Interfaces:**
- Consumes: `IRemoteSession`, `IRemoteSessionFactory`, `RemoteProvider`, `CredentialLoop`, `ITrustDecisions`, исключения (задача 14); `TestFtpServer` и закреплённые факты (задача 15).
- Produces:

```csharp
namespace Tandem.Net.Ftp;
public sealed class FtpSessionFactory : IRemoteSessionFactory {
    public FtpSessionFactory(TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null);     // 15 с и 30 с
}
internal sealed class FtpSession : IRemoteSession { /* по соединению FluentFTP на экземпляр; Features: SetModified при MFMT, Modes — никогда, Symlinks — никогда */ }
internal static class FtpSettings {
    public static FtpConfig ToConfig(ConnectionProfile profile, TimeSpan connectTimeout, TimeSpan readTimeout);
    public static Encoding EncodingOf(ConnectionProfile profile);            // NameEncoding или UTF-8
    public static CertificateInfo Describe(X509Certificate certificate, SslPolicyErrors errors);
}
```

Правила:
- Режим шифрования: `Ftp` — нет; `FtpsExplicit` — явный (`AUTH TLS`), канал данных защищён; `FtpsImplicit` — неявный. **Понижения нет:** если сервер не принял TLS, подключение завершается ошибкой, имя и пароль не отправляются.
- Сертификат: прошёл системную проверку — принят без вопросов; не прошёл — `ITrustDecisions.TrustCertificateAsync`; отказ — `NetTrustException`.
- Вход — через `CredentialLoop` (отказ сервера во входе → `NetAuthenticationException`). `Anonymous` — имя `anonymous`, пароль пустой, без вопросов.
- Передачи — только двоичные. Пассивный режим при `PassiveMode`, иначе активный.
- Листинг несуществующей папки — `VfsNotFoundException` (если сервер отвечает пустым списком — проверить папку отдельным запросом). Ссылки в листинге (`LIST`) отдаются как `Symlink` с целью, если сервер её показал; создавать ссылки FTP не умеет (`NotSupportedException` не нужен: возможности `Symlinks` нет, движок не вызовет).
- `SetModifiedAsync` без `MFMT` ничего не делает. `SetModeAsync` — ничего (возможности `Modes` нет).
- Ошибки: разрыв соединения, истёкшее время, закрытый сервером канал — `VfsConnectionLostException`; ответы 550 «нет такого файла» — `VfsNotFoundException`, «нет доступа» (530, 550 с отказом в правах, 553) — `VfsAccessDeniedException`; отказ в конце записи (452, 552) — `IOException` с текстом сервера, бросается при закрытии потока.
- Исключения и журнал никогда не содержат пароль.

- [ ] **Step 1: Тесты** (все — против `TestFtpServer`, через `RemoteProvider`, где не сказано иное)

```csharp
// FtpContractTests : ProviderContractTests            — TimeResolution = 1 с; факты о ссылках и правах пропускаются по возможностям
// FtpSessionTests
public async Task Lists_with_MLSD_and_with_plain_LIST()
public async Task Missing_folder_is_not_found_not_an_empty_listing()
public async Task All_byte_values_and_line_endings_survive_a_round_trip()                 // и в CommandLog нет "TYPE A"
public async Task Wrong_password_asks_again_and_then_fails_as_authentication()
public async Task Anonymous_logs_in_without_asking()
public async Task Explicit_tls_against_a_plain_server_fails_and_never_sends_the_password()   // RF6: в CommandLog нет USER и PASS
public async Task Rename_never_replaces_an_existing_file_even_when_the_server_would()     // RF2
public async Task Modified_time_is_set_with_MFMT_and_left_alone_without_it()
public async Task Connection_closed_while_idle_is_replaced_silently()                     // RF2: DropAllConnections(), затем листинг — успех
public async Task Data_connection_cut_mid_upload_is_connection_lost()
public async Task Upload_refused_by_the_server_at_the_end_fails_the_write()               // RF1: 552 → исключение при закрытии потока, не успех
public async Task Server_that_stops_answering_fails_within_the_timeout()                  // readTimeout 2 с → VfsConnectionLostException
public async Task Names_in_windows_1251_are_read_and_written_with_that_encoding()
public async Task Failure_messages_never_contain_the_password()                           // RF6: ToString() исключений входа и обрыва
// FtpSettingsTests
[Theory] public void Protocol_maps_to_encryption_mode_and_port(NetProtocol protocol, ...)
public void Passive_flag_and_timeouts_map_to_the_config()
public async Task Valid_certificate_is_accepted_without_asking_and_invalid_one_is_asked_with_its_fingerprint()   // на подставных X509 и SslPolicyErrors
public async Task Refused_certificate_is_a_trust_error()
// FtpOperationTests (CopyOperation / MoveOperation ядра на настоящем FtpSession)
public async Task Upload_cut_off_and_retried_completes_with_no_leftovers_on_the_server()  // RF1: в папке сервера нет *.tandem-part, *.tandem-old
public async Task Upload_cut_off_and_aborted_keeps_the_previous_file()                    // RF1
public async Task Overwrite_on_the_server_goes_through_backup_and_leaves_only_the_new_file()
public async Task Folder_tree_copied_to_the_server_and_back_is_identical()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Ftp"` → FAIL; реализовать; → PASS трижды подряд; `dotnet test tests/Tandem.Net.Tests` → PASS.
- [ ] **Step 5:** Commit: `feat(net): FTP and FTPS sessions over FluentFTP`.

---

### Task 17: `SftpSession` — SFTP с паролем и ключом

Настоящего SSH-сервера в тестах по умолчанию нет, поэтому всё, что можно решить без сети, вынесено в чистые функции и проверяется автоматически; сам обмен с сервером — тонкая оболочка над SSH.NET, которую проверяют задача 23 (по переменным окружения) и ручная приёмка.

**Направление:** net / net-sftp. **Зависит от:** 14 (и фактов `SshNetProbeTests` задачи 2).

**Files:**
- Create: `src/Tandem.Net/Sftp/SftpSession.cs`, `Sftp/SftpSessionFactory.cs`, `Sftp/SftpAuth.cs`, `Sftp/SftpErrors.cs`, `Sftp/SftpEntries.cs`
- Test: `tests/Tandem.Net.Tests/Sftp/SftpAuthTests.cs`, `SftpErrorsTests.cs`, `SftpEntriesTests.cs`, `SftpSessionOfflineTests.cs`

**Interfaces:**
- Consumes: `IRemoteSession`, `IRemoteSessionFactory`, `CredentialLoop`, `ITrustDecisions`, `HostKeyInfo` (задача 14); образцы ключей (задача 2).
- Produces:

```csharp
namespace Tandem.Net.Sftp;
public sealed class SftpSessionFactory : IRemoteSessionFactory {
    /// readKeyFile читает файл закрытого ключа по пути из профиля (приложение читает через локальный источник и посредник доступа).
    public SftpSessionFactory(Func<string, CancellationToken, Task<byte[]>> readKeyFile, TimeSpan? connectTimeout = null, TimeSpan? operationTimeout = null);
}
internal sealed class SftpSession : IRemoteSession { /* один SftpClient; Features: Multiplexed | Modes | SetModified (+ Symlinks по итогам пробы задачи 2) */ }
internal static class SftpAuth {
    /// Способы входа для этих данных: Password → пароль и «клавиатурный» вход, отвечающий тем же паролем; PrivateKey → ключ.
    /// <exception cref="NetAuthenticationException">Ключ не читается, ключевая фраза не подошла или не дана.</exception>
    public static ConnectionInfo Build(ConnectionProfile profile, NetCredentials credentials, TimeSpan connectTimeout);
    public static bool NeedsPassphrase(byte[] privateKey);
    public static HostKeyInfo Describe(string hostKeyName, string fingerprintSha256);
}
internal static class SftpErrors { public static Exception Map(Exception error, string path); }     // см. таблицу ниже
internal static class SftpEntries { public static RemoteEntry? ToEntry(ISftpFile file); }           // null для "." и ".."
```

Правила:
- Ключ узла: в обработчике `HostKeyReceived` — `ITrustDecisions.TrustHostKeyAsync` с алгоритмом и отпечатком SHA-256 (ожидание ответа блокирует поток подключения, не поток интерфейса); отказ — `NetTrustException`.
- Ключ пользователя: файл читает `readKeyFile`; зашифрованный ключ просит ключевую фразу через `ICredentialSource` (`NetCredentials.Passphrase`), повтор — через `CredentialLoop`. Агент ключей (`ssh-agent`) не используется.
- Переименование — обычная команда переименования (не «posix-rename», которая заменяет цель); проверку существования всё равно делает `RemoteProvider`.
- `KeepAliveInterval` = 30 с; предел времени операции — 30 с.
- Перевод ошибок (`SftpErrors.Map`): «нет такого пути» → `VfsNotFoundException`; «нет доступа» → `VfsAccessDeniedException`; отказ во входе → `NetAuthenticationException`; обрыв соединения, истёкшее время, `SocketException`, «клиент не подключён» → `VfsConnectionLostException`; `OperationCanceledException` — как есть; прочие ошибки библиотеки → `VfsException` с её текстом. Точные типы исключений SSH.NET взять из комментариев `SshNetProbeTests`.

- [ ] **Step 1: Тесты** (ни одного соединения, кроме закрытого порта на `127.0.0.1`)

```csharp
// SftpAuthTests
public void Password_profile_offers_password_and_keyboard_interactive()
[Theory] public void Key_profile_loads_each_key_format(string fixture)                   // ed25519, rsa3072, ecdsa256, rsa-pem
public void Encrypted_key_needs_a_passphrase_and_loads_with_it()
public void Wrong_passphrase_is_an_authentication_error_that_can_be_asked_again()
public void Garbage_instead_of_a_key_is_an_authentication_error_naming_the_key_file_not_its_content()
public void Describe_formats_the_fingerprint_as_SHA256_colon_base64()
// SftpErrorsTests
[Theory] public void Library_errors_map_to_core_errors(Exception library, Type expected)
public void Cancellation_passes_through_unchanged()
public void Mapped_errors_keep_the_original_as_inner_and_never_contain_a_password()
// SftpEntriesTests (поддельный ISftpFile)
public void Maps_files_folders_and_links_with_size_time_and_mode()
public void Dot_and_dot_dot_are_dropped()
// SftpSessionOfflineTests
public async Task Connect_to_a_closed_port_is_connection_lost_within_the_timeout()
public async Task Connect_to_something_that_is_not_ssh_is_connection_lost()             // TcpListener на 127.0.0.1 отвечает мусором
public async Task Connect_honours_cancellation()
public async Task Missing_key_file_is_an_authentication_error()                          // readKeyFile бросает VfsNotFoundException
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Sftp"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(net): SFTP sessions over SSH.NET with password and key`.

---

### Task 18: Связка ключей macOS

**Направление:** net / net-app. **Зависит от:** 1.

**Files:**
- Create: `src/Tandem.Platform.Mac/MacKeychainSecretStore.cs`, `Interop/Security.cs`
- Modify: `src/Tandem.Platform.Mac/Interop/ObjC.cs` (только если не хватает преобразований `NSData` и словаря)
- Test: `tests/Tandem.Platform.Mac.Tests/MacKeychainSecretStoreTests.cs`

**Interfaces:**
- Consumes: `ISecretStore`, `SecretStoreException` (задача 1).
- Produces:

```csharp
namespace Tandem.Platform.Mac;
/// Записи «обычный пароль» связки ключей входа: служба — service, учётная запись — ключ секрета. Вызовы Security.framework
/// (SecItemAdd, SecItemCopyMatching, SecItemUpdate, SecItemDelete) — прямые, как остальной слой macOS.
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore(string service = "Tandem") : ISecretStore {
    internal static Exception? ErrorFor(int osStatus);     // 0 и «не найдено» → null; прочее → SecretStoreException с кодом и системным текстом
}
```

Решения: вызовы выполняются в `Task.Run` (система может показать окно доступа и ждать ответа). «Не найдено» при чтении — null, при удалении — не ошибка; «уже есть» при записи — замена (`SecItemUpdate`). Отказ пользователя и недоступная связка — `SecretStoreException`. Используется обычная связка ключей входа, без групп доступа: сборка этапов 1–3 не имеет нужных прав подписи (на этапе 4 пересмотреть). Секрет хранится как UTF-8. Текст секрета не попадает в исключения.

- [ ] **Step 1: Тесты**

```csharp
// выполняются всегда
[Theory] public void Status_codes_map_to_errors(int osStatus, bool isError)        // 0, errSecItemNotFound (-25300) → нет; errSecUserCanceled (-128),
                                                                                    // errSecAuthFailed (-25293), errSecInteractionNotAllowed (-25308) → SecretStoreException
public void Error_text_carries_the_code_and_not_the_secret()
public async Task Empty_key_is_refused()
// только с TANDEM_TEST_KEYCHAIN=1 (иначе Assert.Skip с объяснением); служба "Tandem.Tests.<guid>", уборка в finally
public async Task Set_get_replace_and_delete_round_trip_in_the_real_keychain()
public async Task Missing_secret_reads_as_null_and_deletes_quietly()
public async Task Unicode_secret_survives()
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.Platform.Mac.Tests --filter "FullyQualifiedName~Keychain"` → FAIL; реализовать; → PASS (три теста `Skipped` с причиной). Run `dotnet test Tandem.slnx` и убедиться командой `security find-generic-password -s Tandem 2>&1 | head -1`, что тесты не создали записи службы `Tandem`.
- [ ] **Step 5:** Commit: `feat(mac): keychain secret store`.

---

### Task 19: F4 на сервере: правка через временную копию

Работает для любого источника с возможностью `LocalCopyEdit` — в коде нет слова «сервер». Тесты используют `DroppingProvider` с этой возможностью.

**Направление:** net / net-app. **Зависит от:** 3.

**Files:**
- Create: `src/Tandem.App/ViewModels/Net/RemoteEdit.cs`
- Modify: `src/Tandem.App/ViewModels/Editing/EditorViewModel.cs`, `ViewModels/ShellCommands.cs` (`EditAsync`), `Views/Editing/EditorWindow.axaml.cs`, `Services/WindowService.cs`, `Services/IDialogService.cs`, `DialogService.cs`, `Views/Dialogs/ChoiceDialogs.cs` (`EditorSaveDialog` и новый диалог «выгрузить перед закрытием»), `tests/…/Views/ScriptedDialogs.cs`, оба файла ресурсов (раздел net), `tests/Tandem.App.Tests/ViewModels/StreamProvider.cs` (только если подделке не хватает журнала чтений)
- Test: `tests/Tandem.App.Tests/ViewModels/RemoteEditTests.cs`, `Views/EditorWindowTests.cs`

**Interfaces:**
- Consumes: `ProviderCapabilities.LocalCopyEdit`, `TempFileStore`, `SafeReplace`, `BatchScope`, существующий `EditorViewModel`.
- Produces:

```csharp
namespace Tandem.App.ViewModels.Net;
public enum UploadOutcome { Uploaded, ChangedAtSource, Failed }
/// Временная копия файла далёкого источника и её возвращение в источник.
public sealed class RemoteEdit {
    public static Task<RemoteEdit> DownloadAsync(ProviderRegistry providers, TempFileStore temp, VfsPath file, long maxBytes, CancellationToken ct);
    public VfsPath Source { get; } public VfsPath LocalCopy { get; }          // LocalCopy — путь локального источника во временной папке
    /// Пишет копию в источник через SafeReplace (в пакете источника). overwriteChanged == false и файл в источнике уже не тот,
    /// что скачивали (размер, время, либо исчез) → ChangedAtSource, ничего не записано.
    public Task<(UploadOutcome Outcome, Exception? Error)> UploadAsync(bool overwriteChanged, CancellationToken ct);
    public void Release();                                                     // удаляет копию
}
namespace Tandem.App.ViewModels.Editing;
public sealed partial class EditorViewModel {
    public static Task<EditorViewModel> OpenRemoteAsync(ProviderRegistry providers, TempFileStore temp, VfsPath file, CancellationToken ct);
    public bool IsRemoteCopy { get; }
    public bool HasPendingUpload { get; }        // копия сохранена, но в источнике ещё старое
    public Task<bool> UploadAsync();             // true — источник получил копию
}
namespace Tandem.App.Services;
public enum EditorSaveQuestion { /* прежние */ …, UploadToSource, OverwriteChangedAtSource }
public interface IDialogService {   // добавить
    /// Редактор временной копии закрывается, а сохранённое не выгружено: выгрузить, не выгружать, остаться.
    Task<SaveChangesAnswer> AskUploadBeforeCloseAsync(string fileName, Window? owner = null);
}
```

Правила:
- `ShellCommands.EditAsync`: если источник файла объявляет `LocalCopyEdit` — всегда встроенный редактор через `OpenRemoteAsync` (внешний редактор для таких файлов не запускается: приложение не узнает, когда он сохранил). Файл больше `EditorViewModel.MaxFileBytes` отклоняется **до** скачивания; двоичный — после (копия удаляется). `EditorViewModel.File`, `Target` и заголовок — путь в источнике (повторное F4 выводит вперёд открытое окно).
- Cmd+S сохраняет во временную копию прежними правилами редактора. После каждого удачного сохранения — вопрос `UploadToSource` («Загрузить «{имя}» на сервер?»). «Да» → `UploadAsync`. «Нет» → `HasPendingUpload == true`, в заголовке пометка `RemoteEdit_NotUploaded`.
- Выгрузка: источник изменился после скачивания или прошлой выгрузки → вопрос `OverwriteChangedAtSource` (по умолчанию «Отменить»). Запись — `SafeReplace` (обрыв не портит файл в источнике); права файла в источнике сохраняются, если источник их поддерживает. Сбой → `Error` редактора словами (`OperationTexts.ForFailure`), `HasPendingUpload` остаётся, текст и копия целы; повторить — снова Cmd+S или закрытие окна.
- Закрытие окна: сначала прежний вопрос о несохранённом тексте; затем, если `HasPendingUpload`, — `AskUploadBeforeCloseAsync`: выгрузить (при сбое окно остаётся), не выгружать (окно закрывается), остаться. Выход из приложения идёт тем же путём (`WindowService.CloseAllAsync`). Закрытое окно удаляет копию.
- Источники без `LocalCopyEdit` (диск, zip) правятся по-прежнему, напрямую.

- [ ] **Step 1: Тесты** (`[AvaloniaFact]`; `DroppingProvider` с `Write | LocalCopyEdit`)

```csharp
// RemoteEditTests
public async Task Editing_opens_a_temporary_copy_and_leaves_the_source_untouched_by_typing()
public async Task Save_writes_only_the_copy_until_upload_is_confirmed()                    // RF5
public async Task Confirmed_upload_replaces_the_source_through_a_part_file()               // RF5: нет .tandem-part, .tandem-old
public async Task Declined_upload_marks_the_editor_as_not_uploaded()
public async Task Source_changed_since_download_asks_before_overwriting_and_keeps_it_on_no()   // RF5
public async Task Failed_upload_keeps_the_text_the_copy_and_the_old_source_file()          // RF5: DropAfterBytes
public async Task Upload_can_be_retried_after_the_connection_returns()
public async Task Closing_with_a_pending_upload_asks_and_stays_open_on_cancel_or_failure() // RF5
public async Task Closing_removes_the_temporary_copy()
public async Task Too_large_file_is_refused_before_any_download()                          // журнал чтений источника пуст
public async Task Binary_file_is_refused_and_its_copy_removed()
public async Task External_editor_setting_is_ignored_for_such_sources()
public async Task Second_F4_on_the_same_file_brings_the_open_editor_forward()
public async Task Zip_and_disk_files_are_still_edited_in_place()                           // тесты редактора этапа 2 не изменены и зелёные
public async Task Quitting_the_app_with_a_pending_upload_asks_first()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~RemoteEdit|FullyQualifiedName~Editor"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): edit files of remote sources through a temporary copy with upload on save`.

---

### Task 20: Сохранённые подключения, доверенные ключи, служба подключений

**Направление:** net / net-app. **Зависит от:** 14 (секреты — через `FakeSecretStore` задачи 1).

**Files:**
- Create: `src/Tandem.Net/Connections/ConnectionStore.cs`, `TrustStore.cs`, `INetPrompts.cs`, `NetConnections.cs`
- Test: `tests/Tandem.Net.Tests/Connections/ConnectionStoreTests.cs`, `TrustStoreTests.cs`, `NetConnectionsTests.cs`, `ScriptedNetPrompts.cs` (вспомогательный)

**Interfaces:**
- Consumes: `ConnectionProfile`, `IRemoteSessionFactory`, `RemoteProvider`, `ICredentialSource`, `ITrustDecisions` (задача 14); `ISecretStore` (задача 1); `FakeRemoteServer`.
- Produces:

```csharp
namespace Tandem.Net.Connections;
/// connections.json (версия 1): только поля ConnectionProfile. Правила надёжности — как у JsonSettingsStore: запись во временный файл
/// и переименование; нечитаемый файл откладывается как connections.json.bad, список пуст.
public sealed class ConnectionStore(string directory) {
    public const string FileName = "connections.json";
    public IReadOnlyList<ConnectionProfile> Load(); public void Save(IReadOnlyList<ConnectionProfile> profiles);
}
/// known-hosts.json (версия 1): подтверждённые ключи узлов (по "host:port" и алгоритму) и сертификаты (по "host:port").
public sealed class TrustStore(string directory) {
    public const string FileName = "known-hosts.json";
    public HostKeyInfo? HostKeyOf(string hostAndPort, string algorithm); public void TrustHostKey(string hostAndPort, HostKeyInfo key);
    public string? CertificateOf(string hostAndPort); public void TrustCertificate(string hostAndPort, string sha256);
}
public enum SecretKind { Password, Passphrase }
public sealed record SecretAnswer(string Secret, bool Remember);
/// Вопросы пользователю; могут прийти из фонового потока.
public interface INetPrompts {
    Task<SecretAnswer?> AskSecretAsync(ConnectionProfile profile, SecretKind kind, bool previousWasWrong, CancellationToken ct);
    /// previous != null — ключ узла изменился.
    Task<bool> ConfirmHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, HostKeyInfo? previous, CancellationToken ct);
    Task<bool> ConfirmCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct);
}
public sealed class NetConnections : IAsyncDisposable {
    public const string ProviderIdPrefix = "net:";
    public NetConnections(ProviderRegistry providers, ConnectionStore store, TrustStore trust, ISecretStore secrets, INetPrompts prompts,
        IReadOnlyDictionary<NetProtocol, IRemoteSessionFactory> factories, Func<string, CancellationToken, Task<byte[]>> readKeyFile);
    public static string SecretKey(Guid profileId, SecretKind kind);          // "connection:{id:N}:password" / ":passphrase"
    public IReadOnlyList<ConnectionProfile> Profiles { get; }                 // по имени
    public event EventHandler? Changed;
    public void Load();
    /// Создаёт или заменяет подключение. password: null — не трогать сохранённый, "" — удалить из связки, иначе — записать в связку.
    public Task SaveAsync(ConnectionProfile profile, string? password, CancellationToken ct);
    public Task DeleteAsync(Guid profileId, CancellationToken ct);            // и его секреты
    public Task<bool> HasSavedPasswordAsync(Guid profileId, CancellationToken ct);
    /// Подключается (или берёт уже подключённый сервер этого подключения) и возвращает его начальную папку. origin — куда ведёт «вверх» из корня.
    public Task<VfsPath> ConnectAsync(Guid profileId, VfsPath origin, CancellationToken ct);
    public ConnectionProfile? ProfileOf(string providerId);                   // null — источник не из подключений
    public Task DisconnectAsync(string providerId);
}
```

Правила:
- На подключение — один `RemoteProvider` с идентификатором `"net:" + n`, зарегистрированный **после** первого удачного входа и живущий до выхода; повторный `ConnectAsync` и подключение после `DisconnectAsync` используют его же (тот же `origin`, что при первом подключении).
- Данные для входа (`ICredentialSource` службы): сначала те, что уже приняты в этом сеансе (в памяти) → затем из связки ключей → затем вопрос `INetPrompts`. `previousWasWrong` пропускает первые два шага. Принятые сервером данные запоминаются в памяти до выхода; в связку пишутся, только если пользователь отметил «запомнить» (и заменяют прежние). Ошибка чтения связки — как будто секрета нет (спросить); ошибка записи — подключение продолжается, ошибка возвращается вызывающему отдельным событием `SecretSaveFailed`.
- Ключ узла (`ITrustDecisions` службы): совпал с сохранённым — да; неизвестный — вопрос, при «да» сохранить; **изменившийся — вопрос с прежним отпечатком, при «нет» отказ, прежний ключ остаётся**. Сертификат — так же по отпечатку.
- Нет фабрики для протокола — `NotSupportedException`.
- `DeleteAsync` подключённого сервера: соединения закрываются, запись и секреты удаляются, источник остаётся зарегистрированным (вкладки уводит приложение).
- Ни пароль, ни ключевая фраза, ни содержимое ключа не попадают в `connections.json`, `known-hosts.json`, тексты исключений.

- [ ] **Step 1: Тесты**

```csharp
// ConnectionStoreTests
public void Saves_and_loads_profiles()
public void Corrupt_file_is_set_aside_as_bad_and_gives_an_empty_list()
public void Loaded_profiles_are_normalized_and_duplicates_by_id_dropped()
public void File_has_no_field_that_could_hold_a_secret()                              // RF6: имена свойств JSON — ровно поля ConnectionProfile
// TrustStoreTests
public void Remembers_host_keys_per_host_port_and_algorithm_and_survives_reload()
public void Corrupt_file_trusts_nothing()
// NetConnectionsTests (FakeRemoteServer, FakeSecretStore, ScriptedNetPrompts, TempDir)
public async Task Connect_registers_a_provider_and_returns_the_initial_folder()
public async Task Connecting_twice_gives_the_same_provider()
public async Task Failed_connect_registers_nothing()
public async Task Saved_password_is_used_without_asking()
public async Task Missing_password_is_asked_and_stored_only_when_remember_is_ticked()
public async Task Wrong_saved_password_asks_and_replaces_the_saved_one_when_remembered()
public async Task Declined_password_ends_as_declined_authentication()
public async Task Second_connection_of_the_pool_and_a_reconnect_do_not_ask_again()
public async Task Unknown_host_key_is_asked_once_and_remembered()
public async Task Changed_host_key_is_refused_unless_confirmed_and_the_old_one_stays()
public async Task Unreadable_keychain_falls_back_to_asking()
public async Task Keychain_write_failure_does_not_fail_the_connection()
public async Task Private_key_is_read_through_the_given_reader()
public async Task Delete_removes_the_profile_and_both_secrets()
public async Task Password_is_stored_only_in_the_secret_store()                        // RF6: текст пароля не найден ни в одном файле папки настроек
public async Task Failure_texts_never_contain_the_password()                           // RF6
public async Task Disconnect_closes_connections_and_a_later_connect_reuses_the_provider()
public async Task Protocol_without_a_factory_is_not_supported()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Connections"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(net): saved connections, trusted host keys and the connection service`.

---

### Task 21: Менеджер подключений, вопросы о пароле и ключе, боковая панель

**Направление:** net / net-app. **Зависит от:** 18, 19, 20.

**Files:**
- Create: `src/Tandem.App/Services/INetDialogs.cs`, `NetDialogs.cs`, `DialogNetPrompts.cs`, `ViewModels/Net/ConnectionListViewModel.cs`, `ConnectionEditViewModel.cs`, `Views/Net/ConnectionDialogs.cs`, `tests/Tandem.App.Tests/Views/ScriptedNetDialogs.cs`
- Modify: `src/Tandem.App/ViewModels/Net/NetCommands.cs`, `Composition.cs` (`AppServices`: `ISecretStore Secrets`, `NetConnections Net`, `INetDialogs NetDialogs`; создание в `CreatePlatformServices`), `Views/SidebarView.axaml(.cs)`, оба файла ресурсов (раздел net), `tests/…/Views/TestApp.cs`, `Tandem.App.Tests.csproj` (связать `FakeRemoteServer.cs`, `FakeSecretStore.cs`)
- Test: `tests/Tandem.App.Tests/ViewModels/ConnectionListTests.cs`, `ConnectionEditTests.cs`, `NetCommandsTests.cs`, `Views/ConnectionDialogTests.cs`, `Views/SidebarConnectionsTests.cs` (новый файл)

**Interfaces:**
- Consumes: `NetConnections`, `INetPrompts`, `SecretAnswer` (задача 20); `MacKeychainSecretStore` (задача 18); `NetCommands` (заготовка задачи 3).
- Produces:

```csharp
namespace Tandem.App.Services;
public sealed record ConnectionEdit(ConnectionProfile Profile, string? Password);     // Password — как в NetConnections.SaveAsync
public interface INetDialogs {
    /// Список подключений: Guid выбранного для подключения или null. Создание, правка и удаление выполняются внутри диалога.
    Task<Guid?> ShowConnectionsAsync(ConnectionListViewModel list);
    Task<ConnectionEdit?> EditConnectionAsync(ConnectionEditViewModel edit, Window? owner = null);
    Task<SecretAnswer?> AskSecretAsync(string connectionName, string address, SecretKind kind, bool previousWasWrong, CancellationToken ct);
    Task<bool> ConfirmHostKeyAsync(string address, HostKeyInfo key, HostKeyInfo? previous, CancellationToken ct);
    Task<bool> ConfirmCertificateAsync(string address, CertificateInfo certificate, CancellationToken ct);
    Task<bool> ConfirmDeleteConnectionAsync(string connectionName, Window? owner = null);
}
public sealed class DialogNetPrompts(INetDialogs dialogs) : INetPrompts;              // переносит вопросы в поток интерфейса, как DialogArchivePasswordPrompt
namespace Tandem.App.ViewModels.Net;
public sealed partial class NetCommands {
    public IReadOnlyList<ConnectionProfile> Connections { get; }                      // для боковой панели; следует за NetConnections.Changed
    public Task ConnectAsync(Guid profileId);                                          // в активной панели
    public bool IsConnecting { get; } public void CancelConnecting();
}
```

Решения:
- `net.connect` (Cmd+K) доступна всегда после загрузки: открывает список подключений (имя, адрес; кнопки «Подключиться», «Создать…», «Изменить…», «Удалить», «Закрыть»; Enter — подключиться). Удаление спрашивает подтверждение.
- Форма подключения: имя; протокол (FTP, FTPS явный, FTPS неявный, SFTP); сервер; порт (подставляется по протоколу, пока пользователь не ввёл свой); пользователь; способ входа (пароль; файл ключа — только SFTP; анонимно — только FTP и FTPS); путь к файлу ключа с кнопкой выбора файла; пароль или ключевая фраза в поле со скрытым вводом и флажок «Хранить в связке ключей»; начальная папка; для FTP — пассивный режим и кодировка имён (список `TextEncodings`, по умолчанию UTF-8). Для протокола FTP под полем пароля — предупреждение `Connection_PlainFtpWarning` («Пароль и файлы передаются без шифрования»). Проверки: сервер обязателен, порт 1…65535, для входа по ключу — путь к ключу. При правке поле пароля пустое: пустое и флажок стоит — сохранённый пароль не трогается; флажок снят — сохранённый пароль удаляется.
- Подключение: `NetConnections.ConnectAsync` вне потока интерфейса; пока оно идёт, в строке состояния активной панели — «Подключение к {адрес}…», Esc отменяет. Успех → `NavigateToAsync(начальная папка)` активной панели. `origin` — `ProviderRegistry.PersistentLocationOf(текущая папка)`. Ошибка — сообщение словами (`OperationTexts.ForFailure`) и текст сервера деталью; отказ пользователя ввести пароль (`Declined`) и отмена — без сообщения. `SecretSaveFailed` — сообщение «Не удалось сохранить пароль в связке ключей».
- Вопрос о пароле: имя подключения и адрес, поле со скрытым вводом, флажок «Запомнить в связке ключей», при `previousWasWrong` — строка «Сервер не принял пароль». Вопрос о ключе узла: адрес, алгоритм, отпечаток; для изменившегося ключа — оба отпечатка, предупреждение и кнопка по умолчанию «Отменить».
- `net.disconnect` (Cmd+Shift+K) доступна, когда `NetConnections.ProfileOf(источник активной панели)` не null. Если в очереди есть операции, затрагивающие этот сервер, — сообщение и отказ. Иначе каждая вкладка обеих панелей, показывающая этот источник, уходит «вверх» из его корня (в `origin`), затем `DisconnectAsync`.
- Боковая панель: раздел `Sidebar_Connections` («Сетевые подключения») со списком сохранённых подключений; щелчок подключается в активной панели.
- `Composition.CreatePlatformServices`: `MacKeychainSecretStore`, `ConnectionStore` и `TrustStore` в папке настроек, `NetConnections` с **пустой** таблицей фабрик (протоколы подключает задача 22). `Composition.StartAsync` вызывает `Net.Load()` вместе с чтением настроек. Выход из приложения закрывает соединения (`AppServices.Dispose`).
- `TestApp`: `FakeSecretStore`, `ScriptedNetDialogs`, `NetConnections` во временной папке с фабриками `FakeRemoteServer` для всех четырёх протоколов.

- [ ] **Step 1: Тесты** (`[AvaloniaFact]`)

```csharp
// ConnectionEditTests
public void Port_follows_the_protocol_until_the_user_types_one()                 // 21, 21, 990, 22
public void Auth_choices_follow_the_protocol()
public void Host_port_and_key_path_are_validated()
public void Plain_ftp_shows_the_unencrypted_warning_and_ftps_and_sftp_do_not()
public void Editing_leaves_the_saved_password_alone_unless_a_new_one_is_typed_or_the_box_is_cleared()
public void The_typed_password_is_not_part_of_the_profile()                      // RF6
// ConnectionListTests
public async Task New_edit_and_delete_change_the_saved_list()
public async Task Delete_asks_first()
// NetCommandsTests (TestApp на FakeRemoteServer)
public async Task Connect_shows_the_server_folder_in_the_active_panel()
public async Task Path_line_and_tab_title_show_the_address_without_a_password()   // RF6
public async Task Up_from_the_server_root_returns_to_the_folder_connected_from()
public async Task Connection_failure_is_told_in_words_and_declining_the_password_is_silent()
public async Task Escape_cancels_a_connection_in_progress()
public async Task Password_and_host_key_questions_arrive_on_the_ui_thread()
public async Task Disconnect_is_available_only_on_a_server_panel_and_returns_both_panels_tabs()
public async Task Disconnect_is_refused_while_an_operation_uses_the_server()
public async Task Saved_tab_on_a_server_reopens_as_the_local_folder_after_restart()   // без подключения: FakeRemoteServer.ConnectCount == 0
public async Task Lost_connection_in_a_panel_offers_reconnect_and_reconnect_reloads()  // кнопка задачи 1 на настоящем RemoteProvider
// ConnectionDialogTests / SidebarConnectionsTests
public async Task Secret_dialog_hides_the_text_and_says_when_the_previous_one_was_wrong()
public async Task Changed_host_key_dialog_shows_both_fingerprints_and_defaults_to_cancel()
public async Task Sidebar_lists_saved_connections_and_a_click_connects()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Connection|FullyQualifiedName~NetCommands"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): connection manager, credential prompts and sidebar connections`.

---

### Task 22: Сборка сети в приложении

**Направление:** net (после слияния ветвей net-ftp, net-sftp, net-app). **Зависит от:** 16, 17, 21.

**Files:**
- Modify: `src/Tandem.App/Composition.cs`
- Test: `tests/Tandem.App.Tests/CompositionNetTests.cs`

**Interfaces:**
- Consumes: `FtpSessionFactory` (16), `SftpSessionFactory` (17), `NetConnections` (20), `MacKeychainSecretStore` (18).
- Produces: `Composition.CreateSessionFactories(ProviderRegistry providers) : IReadOnlyDictionary<NetProtocol, IRemoteSessionFactory>` — `Ftp`, `FtpsExplicit`, `FtpsImplicit` → одна `FtpSessionFactory`; `Sftp` → `SftpSessionFactory`, чей `readKeyFile` читает файл через локальный источник `providers` (то есть через `IFileAccessBroker`), раскрывая `~` в домашнюю папку.

- [ ] **Step 1: Тесты**

```csharp
public void Every_protocol_has_a_factory()
public async Task Key_file_is_read_through_the_local_provider_and_tilde_is_expanded()   // CountingBroker видел путь
public async Task Building_the_services_opens_no_connection_and_does_not_touch_the_keychain()   // FakeSecretStore.Calls пуст; ни одного сокета
public async Task Connect_to_a_closed_local_port_through_the_real_ftp_and_sftp_factories_is_told_as_connection_lost()   // 127.0.0.1
public async Task Ftp_end_to_end_through_the_app_services()    // TestFtpServer + настоящая FtpSessionFactory + NetConnections: подключиться, листинг, копия файла
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~CompositionNet"` → FAIL; реализовать; → PASS; `dotnet build Tandem.slnx && dotnet test Tandem.slnx` → 0 предупреждений, PASS.
- [ ] **Step 5:** Commit: `feat(app): wire FTP, FTPS and SFTP into the app`.

---

### Task 23: Проверки на настоящем сервере (по переменным окружения)

**Направление:** net. **Зависит от:** 16, 17.

**Files:**
- Create: `tests/Tandem.Net.Tests/Integration/LiveServer.cs`, `LocalSshd.cs`, `SftpLiveContractTests.cs`, `FtpLiveContractTests.cs`, `LiveScenarioTests.cs`
- Modify: `tests/Tandem.Net.Tests/README.md`

**Interfaces:**
- Produces: `LiveServer.Sftp`, `LiveServer.Ftp`, `LiveServer.Ftps` — описание сервера из переменных окружения или null; каждый тест начинается с `Assert.SkipWhen(server is null, <какие переменные задать>)`.

Переменные: `TANDEM_TEST_SFTP_URL` (`sftp://user@host:port/папка/для/тестов`) и `TANDEM_TEST_SFTP_PASSWORD` либо `TANDEM_TEST_SFTP_KEY` (путь к ключу) с необязательной `TANDEM_TEST_SFTP_PASSPHRASE`; `TANDEM_TEST_FTP_URL` + `TANDEM_TEST_FTP_PASSWORD`; `TANDEM_TEST_FTPS_URL` + `TANDEM_TEST_FTPS_PASSWORD` (+ `TANDEM_TEST_FTPS_TRUST=1`, чтобы принять самоподписанный сертификат). Каждый тест работает только внутри новой подпапки `tandem-test-<guid>` указанной папки и удаляет её за собой.

`TANDEM_TEST_LOCAL_SSHD=1` (и не задан `TANDEM_TEST_SFTP_URL`): `LocalSshd` создаёт во временной папке ключ узла и ключ пользователя (`/usr/bin/ssh-keygen`), пишет туда `sshd_config` (`ListenAddress 127.0.0.1`, свободный порт, `HostKey`, `AuthorizedKeysFile`, `PidFile` — во временной папке; `UsePAM no`, `PasswordAuthentication no`, `StrictModes no`, `Subsystem sftp internal-sftp`) и запускает `/usr/sbin/sshd -D -e -f <файл>` от имени текущего пользователя; по окончании тестов завершает процесс. Системные настройки не меняются, «Удалённый вход» не включается, ничего не устанавливается. Если `sshd` не запустился — тесты `Skipped` с его сообщением, а не `Failed`.

- [ ] **Step 1: Тесты**

```csharp
// SftpLiveContractTests / FtpLiveContractTests : ProviderContractTests       — общий набор договора на настоящем сервере (FTP — и для FTPS)
// LiveScenarioTests (для каждого заданного сервера)
public async Task Copy_from_a_zip_archive_to_the_server_and_back_is_identical()          // критерий этапа: архив → сервер
public async Task Folder_synchronization_between_disk_and_server_ends_equal()
public async Task Overwrite_on_the_server_leaves_no_part_or_backup_files()
public async Task Rename_onto_an_existing_file_is_refused()                              // RF2 на настоящем сервере
public async Task Wrong_password_is_reported_as_authentication()
public async Task Unknown_then_changed_host_key_is_asked_about()                          // только SFTP: подставной TrustStore
public async Task Link_is_listed_as_a_link_and_its_target_is_read_if_the_server_allows()  // только SFTP
public async Task Two_gigabyte_sparse_file_uploads_with_progress_and_can_be_cancelled()   // только при TANDEM_TEST_LARGE=1
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Net.Tests --filter "FullyQualifiedName~Integration"` без переменных → все `Skipped`, в причине названы переменные; ни одного `Failed`.
- [ ] **Step 3:** Реализовать `LiveServer` и `LocalSshd`. Исполнитель **не** задаёт переменные сам и не запускает `sshd` без прямого согласия заказчика; если согласие есть — Run `TANDEM_TEST_LOCAL_SSHD=1 dotnet test tests/Tandem.Net.Tests --filter "FullyQualifiedName~Integration"` и приложить итог к отчёту.
- [ ] **Step 4:** Run `dotnet test Tandem.slnx` → PASS, тесты `Integration` — `Skipped`.
- [ ] **Step 5:** Commit: `test(net): opt-in live server tests for SFTP, FTP and FTPS`. Слить `net` в `main`.

---

## Фаза C: совместная работа (main, после слияния трёх направлений)

### Task 24: Сквозные сценарии: архив → сервер, синхронизация диск ↔ сервер, инструменты на сервере

**Направление:** совместная работа (main). **Зависит от:** 5, 6, 7, 8, 10, 13, 19, 22.

**Files:**
- Create: `tests/Tandem.App.Tests/Integration/ServerScenarioTests.cs`, `ServerToolsTests.cs`, `ServerAvailabilityTests.cs`
- Modify: только то, что тесты покажут сломанным (каждую правку — с её тестом и отдельной строкой в отчёте)

**Interfaces:**
- Consumes: всё построенное в задачах 1–23; `TestApp` с `FakeRemoteServer`.

Задача не добавляет возможностей: она доказывает, что направления работают вместе без особых случаев, и чинит найденное.

- [ ] **Step 1:** Run `dotnet build Tandem.slnx && dotnet test Tandem.slnx` на `main` после трёх слияний → 0 предупреждений, PASS. Run `grep -rnE "Tandem\.Net|\"net:\"|RemoteProvider" src/Tandem.Core src/Tandem.Archives src/Tandem.App --include=*.cs | grep -vE "ViewModels/Net/|Views/Net/|Services/(INetDialogs|NetDialogs|DialogNetPrompts)\.cs|Composition\.cs|SidebarView"` → пусто (панели, команды файлов и движок о серверах не знают).
- [ ] **Step 2: Тесты** (`[AvaloniaFact]`, `TestApp`; сервер — `FakeRemoteServer`)

```csharp
// ServerScenarioTests
public async Task F5_from_a_zip_archive_to_a_server_panel_copies_the_files()                 // критерий этапа; байт в байт
public async Task Copy_to_the_server_cut_off_asks_to_reconnect_and_retry_and_then_completes()  // RF1: диалог с connectionLost, нет остатков
public async Task F6_from_the_server_to_disk_removes_server_files_only_after_the_copy()
public async Task F8_on_the_server_offers_only_permanent_deletion_with_the_warning()
public async Task Zip_lying_on_the_server_opens_as_a_folder_and_its_file_can_be_viewed()
public async Task F5_into_a_zip_lying_on_the_server_rewrites_it_safely()                     // старый архив цел при сбое записи
public async Task F3_and_quick_view_show_a_server_file()
public async Task F4_on_a_server_file_saves_asks_and_uploads()                               // RF5 сквозной
public async Task Enter_on_a_server_file_opens_a_temporary_copy()
public async Task Quitting_with_an_open_connection_and_a_running_upload_asks_and_closes_cleanly()
// ServerToolsTests
public async Task Synchronize_a_disk_folder_with_a_server_folder_both_ways()                 // затем повторное сравнение: всё равно
public async Task Synchronize_confirmation_says_server_deletions_are_permanent()             // RF3
public async Task Compare_folders_between_disk_and_server_pairs_decomposed_and_composed_names()   // RF7
public async Task Batch_rename_and_its_undo_work_on_a_server_folder()
public async Task Checksums_are_created_and_verified_on_a_server_folder()
public async Task Folder_sizes_are_computed_on_the_server_one_folder_at_a_time()            // MaxOpenAtOnce ≤ 1 на время подсчёта
public async Task Compare_files_between_an_archive_and_the_server()
public async Task Conflict_dialog_compare_works_while_copying_to_the_server()
// ServerAvailabilityTests
public async Task On_a_server_panel_reveal_terminal_open_with_and_search_are_off()
public async Task Tools_and_network_commands_have_the_expected_availability_on_disk_archive_search_and_server_panels()   // таблица 11 команд × 4 вида панели
```

- [ ] **Step 3:** Run `dotnet test tests/Tandem.App.Tests --filter "FullyQualifiedName~Integration"` → падающие тесты показывают, что чинить. Чинить в общем коде (возможности источника, типы исключений), не проверкой «это сервер». **Если сценарий «F5 в zip на сервере» требует переделки `ArchiveProvider` больше чем на один файл — не переделывать: оставить чтение, запись в такой архив запретить понятным сообщением и записать в известные ограничения.**
- [ ] **Step 4:** Run `dotnet build Tandem.slnx && dotnet test Tandem.slnx` → 0 предупреждений, PASS; повторить команду `grep` из шага 1 → пусто.
- [ ] **Step 5:** Commit: `test(app): cross-track scenarios for servers, archives and tools` (правки — отдельными коммитами `fix(...)` перед ним).

---

### Task 25: Панель кнопок на панелях без настоящих путей

Отложенное этапа 2: сейчас вся панель кнопок выключена, когда активная панель показывает архив или результаты поиска; с серверами таких панелей становится больше.

**Направление:** совместная работа (main); не зависит от задачи 24 и может идти одновременно с ней. **Зависит от:** 3.

**Files:**
- Modify: `src/Tandem.App/Services/ArgumentTemplate.cs`, `ViewModels/ButtonBarViewModel.cs`, `Views/ButtonBar.axaml`
- Test: `tests/Tandem.App.Tests/Services/ArgumentTemplateTests.cs`, `ViewModels/ButtonBarTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class ArgumentTemplate { public static IReadOnlySet<char> PlaceholdersOf(string template); }   // из 'p','t','f','n','s'
public sealed partial class ButtonBarViewModel { public bool CanRunButton(UserButton button); }             // заменяет общее CanRun
```

Правило: кнопка доступна, когда каждая подстановка её шаблона имеет значение — `%p` нужен путь активной панели на диске, `%t` — пассивной, `%f` и `%s` — файлы на диске, `%n` — любой файл под курсором. Кнопка без подстановок путей доступна всегда. Правило безопасности этапа 2 (кнопка не запускается с пропущенным аргументом) остаётся.

- [ ] **Step 1: Тесты**

```csharp
[Theory] public void Placeholders_of_a_template(string template, string expected)     // "%p %%t" → "p"; "" → ""; "%s %t" → "st"
public async Task Button_without_path_placeholders_runs_on_archive_search_and_remote_panels()
public async Task Button_needing_the_other_panels_path_is_off_when_that_panel_is_not_on_disk()
public async Task Button_availability_follows_the_cursor_and_the_panels()
public async Task A_disabled_button_never_starts_a_program_with_a_missing_argument()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ArgumentTemplate|FullyQualifiedName~ButtonBar"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): button bar buttons are enabled one by one`.

---

## Фаза D: завершение

### Task 26: Сборка 0.3.0, приёмка этапа, README

**Направление:** завершение (main). **Зависит от:** 24, 25.

**Files:**
- Create: `docs/checklists/stage-3.md`
- Modify: `build/Info.plist` (`CFBundleShortVersionString` = `0.3.0`, `CFBundleVersion` = `3`, `NSLocalNetworkUsageDescription`), `build/verify-app.sh`, `README.md`

**Interfaces:**
- Consumes: всё построенное в задачах 1–25.
- Produces: `dist/Tandem.app` версии 0.3.0; список ручной приёмки.

- [ ] **Step 1:** Дополнить `build/verify-app.sh`: версия 0.3.0 и наличие в `Contents/MacOS` файлов `Tandem.Net.dll`, `Renci.SshNet.dll`, `FluentFTP.dll`, `DiffPlex.dll`, `System.IO.Hashing.dll`, а также зависимостей SSH.NET, которые задача 2 назвала в отчёте. Run `build/verify-app.sh` на старом пакете → FAIL.
- [ ] **Step 2:** Поправить `Info.plist`. `NSLocalNetworkUsageDescription` — текст, который macOS показывает при первом подключении к серверу в локальной сети: «Tandem подключается к серверам FTP и SFTP, которые вы указали. / Tandem connects to the FTP and SFTP servers you set up.» Run `build/make-app.sh && build/verify-app.sh` → `RESULT: PASS`. Run `open dist/Tandem.app` → окно с двумя панелями, меню «Инструменты» и «Сеть».
- [ ] **Step 3:** Написать `docs/checklists/stage-3.md` на русском, в той же форме, что `docs/checklists/stage-2.md` (действие → ожидаемый результат, флажки; главные критерии жирным). Разделы:
  - «Как запустить» — сборка, проверка, новые файлы `connections.json` и `known-hosts.json`, где лежат пароли (программа «Связка ключей», записи службы «Tandem») и как их удалить; настройки этапов 1–2 сохраняются; сборку 0.2.0 можно запустить на тех же настройках.
  - «Проверить первым» — на настоящей клавиатуре (в том числе в русской раскладке) до приложения доходят Cmd+M (и окно не сворачивается), Ctrl+M, Ctrl+Shift+M, Shift+F2, Cmd+Shift+C, Cmd+Shift+D, Cmd+Shift+S, Cmd+Alt+I, Cmd+Shift+I, Cmd+K, Cmd+Shift+K, Ctrl+F, Ctrl+Shift+F.
  - «Групповое переименование» — **1000 файлов по шаблону и отмена** (команда для создания: `mkdir t && cd t && for i in $(seq -w 1 1000); do echo $i > f$i.txt; done`); счётчик, дата, диапазон символов; поиск и замена, регулярное выражение; регистр; предварительный просмотр показывает конфликты; обмен имён; имя занято посторонним файлом; отмена после того, как один файл изменили.
  - «Сравнение папок» и «Сравнение файлов» — отличающиеся и уникальные помечены в обеих панелях; текст бок о бок с подсветкой в светлой и тёмной теме, переход по отличиям; двоичные файлы; файл из архива против файла на диске; кнопка «Сравнить…» в диалоге конфликта при копировании.
  - «Синхронизация» — **синхронизация двух папок**: по размеру и дате, по содержимому; направление по каждому элементу; удаление только по явному выбору, подтверждение с числами; изменить файл между сравнением и запуском → он пропущен и назван в сводке; отмена посреди; папка против zip.
  - «Контрольные суммы» и «Размер папок» — все четыре алгоритма; файл сумм, созданный `shasum -a 256` и `md5`, проверяется; испорченный и отсутствующий файл; размер папки под курсором и всех папок, большая папка не подвешивает окно.
  - «Сеть: SFTP» — **подключение с паролем и с ключом; сервер как панель: листинг, F3, F5 в обе стороны, F6, F7, F8, переименование; копирование из zip-архива на сервер (F5 из открытого архива)**; первый вход спрашивает про ключ узла, повторный — нет; неверный пароль; ключ с ключевой фразой. Здесь же — как при желании запустить автоматические проверки на своём сервере (переменные из `tests/Tandem.Net.Tests/README.md`).
  - «Сеть: FTP и FTPS» — то же на FTP-сервере; предупреждение об открытой передаче; FTPS с настоящим и с самоподписанным сертификатом; сервер с русскими именами в Windows-1251.
  - «Менеджер подключений и связка ключей» — создать, изменить, удалить; пароль виден в «Связке ключей» и не встречается в `connections.json` (открыть файл и посмотреть); после перезапуска пароль не спрашивается; после удаления подключения запись из связки исчезает; первое подключение к серверу в локальной сети — системный вопрос macOS о локальной сети.
  - «Обрыв связи» — выключить Wi-Fi посреди копирования на сервер → сообщение с «Переподключиться и повторить»; включить Wi-Fi, повторить → файл целый; на сервере нет файлов `.tandem-part`; панель после простоя в 15 минут читает папку без сообщения об ошибке; кнопка «Переподключиться» в панели.
  - «F4 на сервере» — правка, сохранение, вопрос о загрузке; отказ и закрытие окна; файл изменён на сервере другим клиентом; обрыв при загрузке.
  - «Оба языка» и «Перезапуск» (вкладка на сервере открывается как папка диска).
  - «Известные ограничения этапа 3»: докачки нет (прерванная передача начинается заново — так в спецификации); поиск на серверах недоступен; `ssh-agent` и вход по сертификату не поддерживаются; путь `sftp://…` нельзя ввести в строке пути — только через подключения; FTP-сервер без команды `MFMT` не хранит время файла, поэтому после загрузки на него синхронизация «по дате» считает файл более новым на сервере (использовать «по содержимому»); на сервере между проверкой имени и переименованием есть зазор (другой клиент может создать файл в этот миг); после обрыва, на который ответили «Пропустить» или «Прервать», на сервере может остаться файл `.имя.tandem-part`; внешний редактор для файлов на сервере не используется; просмотр файла с FTP-сервера сначала скачивает его целиком; журнал отмены переименования теряется при выходе; сортировка по размеру не учитывает подсчитанные размеры папок; при сборке без сертификата разработчика macOS после каждой пересборки заново спрашивает доступ к паролю в связке ключей; всё оставшееся из ограничений этапов 1–2, что ещё верно.
- [ ] **Step 4:** Обновить `README.md`: состояние (этап 3 на приёмке, версия 0.3.0), возможности этапа 3, `src/Tandem.Net` в структуре, файлы `connections.json` и `known-hosts.json`, где хранятся пароли, ссылки на этот план и `docs/checklists/stage-3.md`, абзац «Проверки на настоящем сервере» со ссылкой на `tests/Tandem.Net.Tests/README.md`; английский раздел — то же кратко.
- [ ] **Step 5:** Run `dotnet build Tandem.slnx` → 0 предупреждений; `dotnet test Tandem.slnx` → все тесты PASS (тесты по переменным окружения — `Skipped`). Commit: `build: stage 3 bundle, checklist and README`.

---

## Решения плана за заказчика

Каждое можно пересмотреть; указана цена ошибки.

1. **Cmd+M остаётся за групповым переименованием**, как в спецификации, хотя в macOS это «свернуть окно»; второе сочетание — Ctrl+M. Цена: одна строка в `KeyMap` и переназначение в настройках.
2. **«Отличающиеся» файлы при сравнении папок помечаются на обеих сторонах** (спецификация говорит «в обеих панелях»), а не только более новый. Цена: правка одного правила и двух тестов.
3. **Синхронизация умеет удалять, но только по явному выбору для каждого элемента**; по умолчанию не удаляется ничего, режима «зеркало» нет. Цена: если заказчик ждал «зеркало» — отдельная небольшая задача поверх готового плана действий.
4. **Отмена переименования — только последнего и только до выхода из приложения**; повторить отменённое нельзя. Цена: журнал на диске — отдельная задача.
5. **Имена при сравнении и синхронизации считаются одинаковыми независимо от формы Юникода, но с учётом регистра.** Цена: на томах без учёта регистра `A.txt` и `a.txt` двух сторон покажутся разными файлами.
6. **В `Tandem.Core` появляются два пакета** (System.IO.Hashing, DiffPlex), хотя этап 2 держал ядро без пакетов. Оба чисто управляемые. Цена: написать CRC32 и сравнение строк самим — около дня работы.
7. **Ключ узла SFTP и сертификат FTPS подтверждает пользователь, отпечатки хранятся в `known-hosts.json`**; изменившийся ключ по умолчанию отвергается. Спецификация об этом молчит; без этого вход по паролю отдавал бы пароль любому, кто подменил сервер. Цена: один лишний вопрос при первом подключении.
8. **FTPS никогда не понижается до открытого FTP; открытый FTP разрешён, но с предупреждением в форме подключения.** Цена: нет.
9. **Вкладка на сервере после перезапуска открывается как папка диска, из которой подключались**; подключений при запуске нет. Цена: один Cmd+K после запуска.
10. **Файлы на сервере всегда правятся встроенным редактором** (внешний не сообщает, когда сохранил). Цена: если нужен внешний — слежение за временной копией, отдельная задача.
11. **Пароли лежат в обычной связке ключей входа**, без групп доступа; при сборке без постоянного сертификата macOS переспрашивает доступ после пересборки. Цена: пересмотр на этапе 4 вместе с песочницей.
12. **`ssh-agent`, вход по сертификату, прокси и активный режим FTP вне проверок** (активный режим есть флажком, но проверяется только вручную). Цена: добавляются поверх `SftpAuth` и `FtpSettings`.
13. **Автоматических проверок с настоящим SFTP-сервером по умолчанию нет** (нельзя включать «Удалённый вход», Docker и загрузки): логика сервера проверена на поддельном сервере, обмен по SFTP — по переменной окружения и вручную. Цена: ошибка в тонкой оболочке над SSH.NET найдётся только при `TANDEM_TEST_LOCAL_SSHD=1`, на сервере заказчика или в ручной приёмке.
14. **Запуск собственного `sshd` в тестах предусмотрен, но выключен** и включается только переменной `TANDEM_TEST_LOCAL_SSHD=1` по решению заказчика. Цена: нет, пока не включено.
15. **Из отложенного:** «Сравнить» в диалоге конфликта и кнопки панели кнопок по отдельности — включены; показ окна до загрузки вкладок и зависший локальный том в очереди — не включены (серверы их не усугубляют). Цена: остаются в списке отложенного.
16. **Размеры папок считаются мимо очереди операций** и не участвуют в сортировке. Цена: сортировка по подсчитанному размеру — правка `EntrySorter`.
17. **Шаблон переименования — собственная запись** (`[N]`, `[E]`, `[C]`, `[Y][M][D][h][m][s]`, диапазоны), описанная в окне подсказкой. Цена: другой синтаксис — замена одного разборщика.

## Самопроверка плана

- **Покрытие спецификации.** Группа F: групповое переименование (шаблон, счётчик, дата, поиск и замена с выражением, регистр, просмотр, отмена) — задачи 4–5; сравнение каталогов — 8; синхронизация (размер и дата или содержимое, направление по элементу, через движок) — 11–13; сравнение файлов (текст бок о бок с подсветкой, двоичное) — 9–10; контрольные суммы CRC32, MD5, SHA-1, SHA-256 (создание и проверка) — 6; размер папок (под курсором и всех) — 7. Группа G: FTP, FTPS — 15–16; SFTP с паролем и ключом — 17; сервер как источник в панели — 14, 21; менеджер подключений (сохранить, изменить, удалить), пароли в связке ключей — 18, 20, 21; F4 на сервере через временную копию с предложением загрузить — 19; операции и просмотр через общий движок — 14, 24; обрыв связи с «переподключиться и повторить» — 1, 14, 21, 24. Раздел 5.1 (`Tandem.Net`, `ISecretStore`) — 1, 2, 18; 5.2 (источники «сервер», вложение архива на сервере) — 14, 24; 5.3 (операция «синхронизация», действие «сравнить» в конфликте) — 10, 12; 5.4 (ключ читается через посредника доступа) — 22; 5.5 (подключения в JSON, пароли только в связке) — 18, 20; 5.6 (оба языка) — каждая задача с интерфейсом; 7.1 (боковая панель: сетевые подключения) — 21; 7.2 (Cmd+M) — 3, 5; раздел 8, этап 3: 1000 файлов с отменой — 5 (`Thousand_files_renamed_by_template_and_undone`) и приёмка; синхронизация двух папок — 12, 13, 24; SFTP как панель и архив → сервер — 14, 17, 21–24 и приёмка; раздел 9 (тесты ядра и `Net`, локальный тестовый сервер для FTP, ручной список) — в каждой задаче, 15, 26.
- **Review Focus.** 1 — задачи 1, 14, 16, 24; 2 — 14, 16, 23; 3 — 11, 12, 13, 24; 4 — 4, 5; 5 — 19, 24; 6 — 16, 20, 21; 7 — 8, 11, 24.
- **Согласованность имён.** `VfsConnectionLostException`, `LocalCopyEdit`, `ISecretStore`, `OperationKind`, `SkipReason` (1) → 5, 6, 12, 14, 18–20; `IToolWindows`, `IDialogModel<T>`, `ToolsCommands.On`, `SelectWhere`, `IDialogService.CompareFiles` (3) → 5–8, 10, 13; `RenamePlan`, `RenameItem` (4) → 5; `NameKey` (8) → 11; `BinaryDiff.AreEqualAsync` (9) → 11; `SyncComparison`, `SyncItem`, `SyncAction` (11) → 12, 13; `ConnectionProfile`, `IRemoteSession`, `IRemoteSessionFactory`, `CredentialLoop`, `ITrustDecisions`, `ICredentialSource`, `RemoteProvider`, `FakeRemoteServer` (14) → 16, 17, 20–24; `TestFtpServer` (15) → 16, 22; `NetConnections`, `INetPrompts`, `SecretAnswer` (20) → 21, 22; `AppServices` меняется в задаче 3 (`ToolWindows`) и в задаче 21 (`Secrets`, `Net`, `NetDialogs`) — в разных фазах и разных направлениях.
- **Параллельность.** После задачи 3 направления не делят ни одного файла, кроме двух файлов ресурсов с отдельными разделами: диалог конфликта (задача 10) вынесен в свой файл, остальные диалоги `ChoiceDialogs.cs` правит только направление net (задача 19); свои тесты каждое направление пишет в новых файлах, существующие тестовые файлы в фазе B не дополняются.
- **Осознанно вне объёма:** докачка передач и поиск на серверах (не-цели спецификации); режим «зеркало», слияние и правка в окне сравнения, трёхстороннее сравнение, расписание синхронизации; `ssh-agent`, прокси, SCP, WebDAV, облака; ввод адреса сервера в строке пути; журнал отмены на диске.
