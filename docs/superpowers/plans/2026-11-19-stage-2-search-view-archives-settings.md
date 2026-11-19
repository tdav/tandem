# Tandem, этап 2 (поиск, просмотр и правка, архивы, настройка) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Добавить к двухпанельному менеджеру поиск файлов с выводом в панель, встроенные просмотр (включая файлы на несколько ГБ) и редактор, архивы как папки (чтение zip/tar/7z/rar, создание zip/tar/tar.gz/7z, правка zip) и окно настроек (клавиши, темы, цвета, столбцы, панель кнопок).

**Architecture:** Всё новое подключается к тому, что построено в этапе 1, через `IFileSystemProvider`: результаты поиска и архив — источники, панели и движок операций о них не знают. Для этого в Core добавляются три необязательные общие возможности источника — «пакет изменений» (`IBatchProvider`), «вложенный источник» (`IHostedProvider`) и «файл открывается как папка» (`IFileMounter`); ими пользуются панели и операции, архивы лишь реализуют их. Архивы живут в новом проекте `Tandem.Archives` (SharpCompress), просмотр больших файлов — постраничное чтение по смещению в Core, настройки — отдельный файл `settings.json` рядом с неизменным `state.json`.

**Tech Stack:** C#, .NET 11 (`net11.0`), Avalonia 12.1.6, CommunityToolkit.Mvvm 8.4.2, SharpCompress 1.0.0, Avalonia.AvaloniaEdit 12.0.0, xunit.v3 3.2.2, Avalonia.Headless.XUnit 12.1.6.

**Spec:** `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md` — исполнитель читает её вместе с планом. Этот план покрывает группы C, D, E, H (раздел 6), строки этапа 2 раздела 7.2 и критерий этапа 2 раздела 8. Что этап 1 построил на деле: `docs/superpowers/2026-11-19-stage-1-decisions.md` и сам код (при расхождении с планом этапа 1 прав код).

**Модели:** план написан Fable 5.2. Реализацию и проверку каждой задачи выполняют подагенты на Opus 5.5 (`model: "opus"`), по одной задаче, после каждой — проверяющий.

## Global Constraints

Действуют из этапа 1 (сверено с кодом):

- `TargetFramework` = `net11.0`; сборка только `osx-arm64`; `LSMinimumSystemVersion` = `14.0`. `Nullable` включён, `TreatWarningsAsErrors` = `true`. Версии пакетов — только в `Directory.Packages.props`.
- `Tandem.Core` не ссылается на Avalonia и не вызывает системные библиотеки macOS (разрешён только POSIX-вызов `lstat`, уже есть). Платформа — через интерфейсы `Tandem.Core.Platform`.
- Все обращения локального источника к диску идут через `IFileAccessBroker`. Исключение — собственные папки приложения: `~/Library/Application Support/Tandem/` (настройки), `~/Library/Caches/Tandem/` (временные файлы), `~/Library/Logs/Tandem/`.
- Никакой молчаливой перезаписи. Файл пишется во временное имя `.{имя}.tandem-part` рядом с целью и переименовывается по завершении; заменяемый файл откладывается как `.{имя}.tandem-old` и удаляется после успеха. Прерванная операция не оставляет испорченного файла под настоящим именем.
- Перенос между томами и источниками: копирование → проверка → удаление источника только для успешно скопированных элементов.
- Файловые операции, чтение папок, поиск, чтение архивов и файлов для просмотра не выполняются в потоке интерфейса. Модели представления создаются и вызываются в потоке интерфейса, тяжёлое уводят в `Task.Run`; их тесты — `[AvaloniaFact]`.
- Ядро не содержит текстов для пользователя: отдаёт коды и типы исключений, приложение переводит (`OperationTexts`, `Loc.Enum`). Строки интерфейса — только в `Resources/Strings.resx` (английский) и `Strings.ru.resx` (русский), с одинаковым набором ключей; не в коде и не в разметке.
- Название, иконки и оформление Total Commander не используются.
- Код, не являющийся разметкой, пишется через тест: сначала падающий тест, затем реализация.
- API Avalonia 12 сверять с документацией (Context7 / docs.avaloniaui.net), а не по памяти.
- Коммит после каждой задачи; сообщение на английском, заканчивается строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

Новые для этапа 2:

- Новые пакеты: `SharpCompress` 1.0.0 (только в `Tandem.Archives`), `Avalonia.AvaloniaEdit` 12.0.0 (только в `Tandem.App`). Других пакетов не добавлять. **API SharpCompress 1.0 отличается от 0.x** (`ArchiveFactory.OpenArchive`, `ReaderFactory.OpenReader`, `WriterFactory.OpenWriter`, `ZipArchive.CreateArchive`, `ReaderOptions.Password`, `ArchiveDetector`) — сверять по `docs/API.md`, `docs/FORMATS.md` репозитория adamhathcock/sharpcompress и по самой сборке, не по памяти. То же для AvaloniaEdit 12.
- Новый проект `src/Tandem.Archives` зависит только от `Tandem.Core` и SharpCompress; `Tandem.App` ссылается на него. Новый тестовый проект `tests/Tandem.Archives.Tests`. `Tandem.Core` остаётся без пакетов.
- Архивы и результаты поиска — источники за `IFileSystemProvider`. В панелях, `FileCommands` и движке операций нет проверок «это архив» / «это поиск»: только общие возможности (`ProviderCapabilities`, `IBatchProvider`, `IHostedProvider`, `IFileMounter`).
- Правка архива: новый архив пишется во временный файл `.{имя}.tandem-part` рядом с исходным и заменяет его только при успехе; при любой ошибке исходный архив остаётся нетронутым.
- Пароли архивов живут только в памяти до закрытия приложения. Никогда не пишутся в файлы настроек, журнал и временную папку.
- Временные файлы — только через `TempFileStore` (папка `~/Library/Caches/Tandem/tmp`, очищается при запуске и при выходе).
- `state.json` остаётся версии 1 и читается сборкой этапа 1; настройки группы H — в новом `settings.json` (версия 1) в той же папке.
- Цвета — только в `App.axaml` (`ThemeDictionaries`, светлый и тёмный вариант каждого ключа); представления ссылаются через `DynamicResource`. Цвета по типам файлов — именованная палитра из тех же ресурсов.
- Каждая новая команда: константа в `CommandIds`, подпись `Menu_<id с '_' вместо '.'>` в обоих файлах ресурсов, сочетание по умолчанию в `KeyMap.Default` там, где его называет план. Запись сочетаний: порядок модификаторов Ctrl, Alt, Shift, Cmd.
- Поддельные реализации для тестов лежат только в тестовых проектах (кроме уже существующего `InMemoryFileSystem`). Тесты не трогают настоящую домашнюю папку, корзину, связку ключей и `~/Library`: только `InMemoryFileSystem`, временные папки (`TempDir`) и тестовые службы `TestApp`.

## Review Focus

1. **Перезапись zip прервана или не удалась** (диск заполнился посреди записи, сбой при подмене файла) — исходный архив цел и читается, `.tandem-part` и `.tandem-old` не остаются; при переносе файлов в zip источники удаляются только после того, как новый архив встал на место (тесты в задачах 3 и 24).
2. **Архив с опасными именами записей** (`../../x`, абсолютный путь, обратные косые черты, запись «внутри» символической ссылки) — при просмотре и распаковке ничего не появляется вне выбранной папки (тесты в задаче 22).
3. **Файл на несколько ГБ, в том числе из одной строки без переводов строк, в просмотре** — открывается сразу, с любого места читается только страница, память не растёт с размером файла (тесты в задачах 8 и 10).
4. **Неверно угаданная кодировка** — русский текст в Windows-1251, UTF-8, оборванный на границе пробы, UTF-16 без метки с кириллицей показываются буквами, а не мусором; ручной выбор кодировки всегда доступен (тесты в задачах 7, 10 и 18).
5. **Поиск по огромному дереву, по петле символических ссылок, по папке без доступа** — первые результаты видны до конца обхода, «Стоп» останавливает сразу, петля не зацикливает, нечитаемая папка пропускается и считается (тесты в задаче 17).

## Структура файлов

```
Directory.Packages.props                    + SharpCompress, Avalonia.AvaloniaEdit
Tandem.slnx                                 + Tandem.Archives, Tandem.Archives.Tests
src/Tandem.Core/
  Operations/  CopyPlanner (новый, из CopyEngine), SafeReplace (новый, открытый), ItemErrors (открытый),
               CopyEngine/CopyOperation/MoveOperation/DeleteOperation (пакеты изменений)
  Platform/    IExclusiveRename
  Vfs/         IBatchProvider, BatchScope, IHostedProvider, IFileMounter, TempFileStore, ProviderRegistry (+)
  Viewing/     TextEncodings, TextEncodingDetector, PagedTextReader, HexDump
  Search/      SearchQuery, FileSearch, ContentMatcher, SearchResultsProvider
  Settings/    AppSettings, JsonSettingsStore, AppState (ViewState.ButtonBar)
src/Tandem.Archives/
  ArchiveFormat(s), ArchiveIndex, ArchiveProvider (+ ArchiveProvider.Zip.cs — правка zip),
  ArchiveMounts, IArchivePasswordPrompt, ArchiveExceptions, PackOperation
src/Tandem.Platform.Mac/  MacExclusiveRename, Interop/LibC (+ renamex_np)
src/Tandem.App/
  Services/    IWindowService, WindowService, SettingsService, KeyChordRules, ArgumentTemplate,
               ProgramLauncher, FileColors, DialogArchivePasswordPrompt
  ViewModels/  Viewing/ViewerViewModel, Viewing/QuickViewViewModel, Editing/EditorViewModel,
               Search/SearchViewModel, Settings/SettingsViewModel, ListLayoutViewModel,
               ButtonBarViewModel, ArchiveCommands
  Views/       Viewing/ViewerView, Viewing/ViewerWindow, Editing/EditorWindow, Search/SearchWindow,
               Settings/SettingsWindow, Settings/ShortcutRecorder, ButtonBar, Dialogs/ArchiveDialogs
tests/Tandem.Archives.Tests/   Fixtures/ (готовые архивы + make-fixtures.sh + README.md)
docs/checklists/stage-2.md
```

Включённые пункты из списка отложенного этапа 1 (по одному на строку):

- разделение `CopyEngine` перед архивами (итоговая проверка, вопрос 7) — задача 1;
- неатомарное переименование и отказ в переименовании, отличающемся только нормализацией Юникода — задача 2;
- Enter на пакете `.app` запускает программу, а не входит в папку — задача 5;
- клавиша Insert не привязана к «выделить и вниз» — задача 13;
- меню приложения «Tandem» не своё и не переведено — задача 16 (пункт «Настройки…»).

Оставлено (причина): окно показывается только после загрузки всех вкладок — этап 3, вместе с сетью; ключ тома без разрешения ссылок, FIFO-очередь при зависшем томе — этап 3; замена `qlmanage` на `QLPreviewPanel` — этап 4; действие «Сравнить» в конфликте — этап 3 (решение 34); Esc в командной строке, мёртвые ключи ресурсов, тройное дублирование блока доступности команд и разбора `~`, мелочи прогресса и паузы, чувствительные ко времени тесты, блокированный файл при переносе между дисками, 10 секунд тишины при выходе — не мешают работам этапа 2, остаются в списке.

Сочетания по умолчанию, добавляемые этапом 2 (раздел 7.2 спецификации плюс решения этого плана):

| Id | Сочетания | Задача |
|---|---|---|
| `nav.enter` | Cmd+Down | 5 |
| `view.quickView` | Ctrl+Q | 11 |
| `select.toggleDown` | + Insert | 13 |
| `app.settings` | Cmd+OemComma | 16 |
| `search.files` | Alt+F7, Cmd+F | 21 |
| `archive.pack` | Alt+F5, Cmd+Alt+5 | 26 |
| `archive.unpack` | Alt+F9, Cmd+Alt+9 | 26 |

`file.view` (F3, Cmd+3) с этапа 2 открывает встроенный просмотр, `file.edit` (F4, Cmd+4) — встроенный редактор; Quick Look остаётся командой `file.quickLook` на Cmd+Y и в меню «Файл».

---

### Task 1: Разделение `CopyEngine` и открытая безопасная замена файла

**Files:**
- Create: `src/Tandem.Core/Operations/CopyPlanner.cs`, `src/Tandem.Core/Operations/SafeReplace.cs`
- Modify: `src/Tandem.Core/Operations/CopyEngine.cs`, `ItemErrors.cs`, `CopyOperation.cs`, `MoveOperation.cs`
- Test: `tests/Tandem.Core.Tests/Operations/SafeReplaceTests.cs`

**Interfaces:**
- Consumes: существующие `CopyEngine` (все его открытые члены), `ItemErrors`, `IFileSystemProvider`.
- Produces:

```csharp
namespace Tandem.Core.Operations;
public static class SafeReplace {
    public const string PartSuffix = ".tandem-part";
    public const string BackupSuffix = ".tandem-old";
    public static VfsPath PartPathFor(VfsPath destination);            // .{имя}.tandem-part рядом
    /// write получает путь временного файла; затем он встаёт на место destination.
    /// replace: существующий destination откладывается как .tandem-old, удаляется после успеха, возвращается при сбое.
    public static Task WriteAsync(IFileSystemProvider target, VfsPath destination, bool replace, Func<VfsPath, Task> write);
    public static Task MoveIntoPlaceAsync(IFileSystemProvider target, VfsPath from, VfsPath destination, bool replace);
}
public sealed class ItemErrors {                                       // был internal; члены те же
    public ItemErrors(FileOperation operation, IOperationInteraction interaction);
    public static bool IsItemError(Exception e);
    public Task<bool> ShouldRetryAsync(VfsPath item, Exception error, CancellationToken ct);
    public Task<bool> RunAsync(VfsPath item, Func<Task> attempt, CancellationToken ct);
}
internal sealed class CopyPlanner(ProviderRegistry providers, FileOperation operation, ItemErrors errors, bool verifyContent) {
    // сюда переезжают без изменения поведения: ResolveTargets, OntoItself (static),
    // IsSameEntryAsync, RejectTargetsOntoThemselvesAsync, ResolveDestinationDirectoryAsync, ScanAsync
}
```

`CopyEngine` оставляет себе копирование одного элемента, конфликты, `RenameAsync`, метаданные и проверку; тела `ReplaceWithPartAsync`/`MoveIntoPlaceAsync`/`SiblingPath`/тихие удаление и возврат переезжают в `SafeReplace`. Это чистая перестановка: поведение, сообщения исключений и имена временных файлов не меняются.

- [ ] **Step 1: Тесты** (`InMemoryFileSystem`)

```csharp
public async Task Write_puts_content_under_real_name_and_leaves_no_part()
public async Task Write_failure_removes_part_and_keeps_missing_destination_missing()
public async Task Replace_swaps_content_and_removes_backup()          // старый "old" → новый "new", .tandem-old нет
public async Task Replace_keeps_old_file_when_write_fails()            // FailWriteAfter на part → destination == "old"
public async Task Failed_move_into_place_restores_backup()             // FailOn("Move", part→dest) → destination == "old"
public async Task Stale_backup_from_earlier_run_is_cleared()
[Fact] public void Part_path_is_hidden_sibling()                        // /a/b.txt → /a/.b.txt.tandem-part
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Core.Tests --filter "FullyQualifiedName~SafeReplace"` → FAIL (типа нет).
- [ ] **Step 3:** Вынести код, сделать `ItemErrors` открытым, поправить вызовы.
- [ ] **Step 4:** Run `dotnet build Tandem.slnx && dotnet test Tandem.slnx` → 0 предупреждений; все прежние 1197 тестов и новые PASS, ни один прежний тест не изменён.
- [ ] **Step 5:** Commit: `refactor(core): split CopyEngine, public SafeReplace`.

---

### Task 2: Переименование без перезаписи одним системным вызовом

**Files:**
- Create: `src/Tandem.Core/Platform/IExclusiveRename.cs`, `src/Tandem.Platform.Mac/MacExclusiveRename.cs`
- Modify: `src/Tandem.Core/Vfs/Local/LocalFileSystemProvider.cs` (конструктор, `MoveAsync`), `src/Tandem.Platform.Mac/Interop/LibC.cs`, `src/Tandem.App/Composition.cs`
- Test: `tests/Tandem.Core.Tests/Vfs/LocalProviderTests.cs`, `tests/Tandem.Platform.Mac.Tests/MacExclusiveRenameTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Platform;
public interface IExclusiveRename {
    /// Переименовывает файл, папку или ссылку (саму ссылку). Цель существует → VfsAlreadyExistsException,
    /// ничего не заменено. Прочие отказы системы — IOException / UnauthorizedAccessException.
    void Rename(string sourcePath, string destinationPath);
}
public sealed class LocalFileSystemProvider(IFileAccessBroker broker, IExclusiveRename? exclusiveRename = null)
public sealed class MacExclusiveRename : IExclusiveRename      // renamex_np(src, dst, RENAME_EXCL = 0x4); ошибки — через SystemErrors.FromErrno, EEXIST → VfsAlreadyExistsException
```

Решения: `MoveAsync` сначала сверяет личность (`lstat`, как `IsSameEntryAsync`): если цель существует и это тот же объект, что источник (другой регистр или другая форма нормализации имени) — обычное переименование; иначе `exclusiveRename.Rename`, а без него — прежняя проверка и `Directory.Move`. Прежний `IsCaseOnlyRename` по тексту убрать. `Composition.CreatePlatformServices` передаёт `new MacExclusiveRename()`.

- [ ] **Step 1: Тесты**

```csharp
// Core (поддельный IExclusiveRename-счётчик, настоящая временная папка)
public async Task Move_goes_through_exclusive_rename_when_given()
public async Task Case_only_rename_does_not_use_exclusive_rename_and_succeeds()
public async Task Normalization_only_rename_succeeds()                  // "й" NFC → NFD, один файл с новым именем
// Platform.Mac
public void Rename_onto_existing_file_throws_AlreadyExists_and_keeps_both()   // содержимое обоих прежнее
public void Renames_file_directory_and_symlink_itself()
public void Rename_across_volumes_throws_IOException()                   // TinyVolume
public async Task Local_provider_with_mac_rename_passes_move_contract()   // Move_onto_existing_throws_AlreadyExists
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~LocalProvider|FullyQualifiedName~ExclusiveRename"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `fix(core,mac): exclusive rename for local moves`.

---

### Task 3: Пакеты изменений в операциях

Источник, для которого каждая мелкая правка дорога (архив: каждая правка — перезапись файла), собирает правки операции и применяет их разом.

**Files:**
- Create: `src/Tandem.Core/Vfs/IBatchProvider.cs`, `src/Tandem.Core/Vfs/BatchScope.cs`
- Modify: `src/Tandem.Core/Operations/CopyOperation.cs`, `MoveOperation.cs`, `DeleteOperation.cs`
- Test: `tests/Tandem.Core.Tests/Operations/BatchOperationTests.cs`, `tests/Tandem.Core.Tests/Vfs/BatchingProvider.cs` (вспомогательный)

**Interfaces:**
- Consumes: `CopyPlanner.ScanAsync`, `CopyEngine.CopyAllAsync`, `ItemErrors` (задача 1).
- Produces:

```csharp
namespace Tandem.Core.Vfs;
public interface IBatchProvider {
    /// Пока пакет не принят, правки копятся; чтения источника видят накопленное. Второй пакет того же
    /// источника ждёт конца первого.
    Task<IProviderBatch> BeginBatchAsync(CancellationToken ct);
}
public interface IProviderBatch : IAsyncDisposable {                    // DisposeAsync без CommitAsync отбрасывает накопленное
    /// Подсказка: эти файлы будут прочитаны в пакете в таком порядке.
    Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}
public sealed class BatchScope : IAsyncDisposable {
    /// По одному пакету на каждый IBatchProvider среди источников путей; открываются в порядке Id (Ordinal).
    public static Task<BatchScope> BeginAsync(ProviderRegistry providers, IEnumerable<VfsPath> paths, CancellationToken ct);
    public bool Covers(VfsPath path);                                   // источник пути участвует в пакете
    public Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct);   // раздаёт по источникам
    public Task CommitAsync(CancellationToken ct);
}
```

Правила для операций:
- `CopyOperation`, `MoveOperation`, `DeleteOperation` открывают `BatchScope` на `Sources` и `Destination` в начале `ExecuteAsync` и принимают его в `finally` с `CancellationToken.None` — и после отмены, и после ошибки: уже обработанные элементы сохраняются, как на обычном диске.
- После сканирования копирование и перенос вызывают `PrepareReadAsync` со списком файлов плана (только `EntryKind.File`) в порядке плана.
- Ошибка `CommitAsync` при успешном или отменённом теле — исход операции `Failed` с этим исключением. Если тело уже упало, остаётся его исключение.
- Перенос, когда `Covers(назначение)`: удаление источников (файлов, затем опустевших папок) откладывается до успешного `CommitAsync`; при его ошибке не удаляется ничего. Переименования внутри одного источника не меняются.
- Источники без `IBatchProvider` работают как раньше (пустой `BatchScope`).

`BatchingProvider` (тестовый): обёртка над `InMemoryFileSystem` с другим `Id`, пропускает вызовы сразу, пишет общий журнал событий (`Begin`, `PrepareRead`, `Commit`, `Dispose`, `Delete`, `Write`) и умеет `FailCommit = () => new IOException(...)`.

- [ ] **Step 1: Тесты**

```csharp
public async Task Copy_into_batching_provider_commits_once_after_all_items()        // журнал: 1 Begin, 1 Commit, после последнего Write
public async Task Copy_announces_files_to_read_in_plan_order()                       // PrepareRead источника == файлы плана
public async Task Cancelled_copy_still_commits_completed_items()
public async Task Move_into_batching_provider_deletes_sources_only_after_commit()    // в журнале Commit раньше любого Delete источника
public async Task Move_keeps_every_source_when_commit_fails()                        // RF1: Failed с IOException; все источники на месте
public async Task Failed_commit_fails_a_copy_that_otherwise_succeeded()
public async Task Delete_in_batching_provider_commits_once()
public async Task Two_batching_providers_begin_in_id_order()
public async Task Second_batch_waits_for_the_first()                                  // вторая операция стартует после Dispose первой
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~BatchOperation"` → FAIL; реализовать; → PASS; `dotnet test tests/Tandem.Core.Tests` → PASS.
- [ ] **Step 5:** Commit: `feat(core): provider batches in copy, move and delete`.

---

### Task 4: Вложенные источники, «файл как папка», временные файлы

**Files:**
- Create: `src/Tandem.Core/Vfs/IHostedProvider.cs`, `IFileMounter.cs`, `TempFileStore.cs`
- Modify: `src/Tandem.Core/Vfs/ProviderRegistry.cs`
- Test: `tests/Tandem.Core.Tests/Vfs/ProviderRegistryTests.cs`, `TempFileStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Vfs;
public readonly record struct HostLocation(VfsPath Directory, string? FocusName);
public interface IHostedProvider {
    /// Для корня источника — куда ведёт «вверх» (папка и имя, на которое поставить курсор). Для прочих путей null.
    HostLocation? GetHostLocation(VfsPath path);
    /// Путь, как его читает пользователь, например "/a/b.zip/docs".
    string GetDisplayPath(VfsPath path);
}
public interface IFileMounter {
    /// Путь папки, которой открывается файл, или null, если этот файл не открывается.
    /// byContent: false — решать только по имени (дёшево, Enter); true — заглянуть в содержимое.
    Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct);
}
public sealed class ProviderRegistry {                                   // добавить к существующему
    public bool TryGet(string id, out IFileSystemProvider? provider);
    public void AddMounter(IFileMounter mounter);
    public Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct);   // первый не-null
    public HostLocation? HostLocationOf(VfsPath path);                   // null для обычных источников
    public string DisplayPath(VfsPath path);                             // path.Path для обычных источников
    public VfsPath PersistentLocationOf(VfsPath path);                   // сам путь; для вложенного — папка хозяина его корня, рекурсивно
}
public sealed class TempFileStore : IDisposable {
    public TempFileStore(string directory);                              // создаёт папку; Clear() при создании
    public string Directory { get; }
    public string NewFile(string name);                                  // {dir}/{guid}/{name}; файл не создаётся
    /// Копирует файл источника во временный файл с тем же именем; при отмене или ошибке недописанное удаляется.
    public Task<string> MaterializeAsync(IFileSystemProvider provider, VfsPath file, IProgress<long>? bytes, CancellationToken ct);
    public void Release(string path);                                    // удаляет файл и его папку; чужой путь — ничего
    public void Clear();
    public void Dispose();                                               // Clear
}
```

- [ ] **Step 1: Тесты**

```csharp
public void Plain_provider_displays_its_own_path_and_has_no_host()
public void Persistent_location_of_hosted_path_is_host_directory_recursively()   // вложенный во вложенный → папка самого внешнего
public async Task First_mounter_that_answers_wins_and_none_gives_null()
public void TryGet_reports_missing_provider()
public async Task Materialize_copies_bytes_and_keeps_the_name()
public async Task Same_name_twice_gives_two_files()
public async Task Cancelled_materialize_leaves_nothing()
public void Clear_removes_everything_and_Release_removes_one()
public void New_store_clears_leftovers_of_previous_run()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ProviderRegistry|FullyQualifiedName~TempFileStore"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): hosted providers, file mounters, temp file store`.

---

### Task 5: Панель и вложенные источники; пакеты `.app`

**Files:**
- Modify: `src/Tandem.App/ViewModels/PanelViewModel.cs`, `TabViewModel.cs`, `PanelTabsViewModel.cs`, `SideViewModel.cs`, `MainViewModel.cs`, `ShellCommands.cs`, `src/Tandem.App/Composition.cs` (`AppServices` + `TempFileStore Temp`), `Commands/CommandIds.cs`, `Commands/KeyMap.cs`, `Views/MainMenu.cs`, `ViewModels/ContextMenuModel.cs`, ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/HostedPanelTests.cs`, `tests/Tandem.App.Tests/ViewModels/FakeHosted.cs` (вспомогательный), `tests/Tandem.App.Tests/Views/TestApp.cs`

**Interfaces:**
- Consumes: `ProviderRegistry.TryMountAsync/HostLocationOf/DisplayPath/PersistentLocationOf`, `TempFileStore` (задача 4).
- Produces:

```csharp
public sealed record AppServices(..., IDialogService Dialogs, TempFileStore Temp)   // новый последний член; Dispose освобождает Temp
public static class Composition { public static string DefaultTempDirectory(string homeDirectory); }  // ~/Library/Caches/Tandem/tmp
public sealed partial class PanelViewModel {
    public string DisplayPath { get; }                 // ProviderRegistry.DisplayPath(CurrentPath); меняется вместе с CurrentPath
    public Task EnterAsync();                          // nav.enter
    public event EventHandler<FileEntry>? NotAFolder;  // EnterAsync на файле, который ничем не открывается
    public event EventHandler<Exception>? OpenFailed;  // файл не удалось открыть как папку (IOException)
}
CommandIds.NavEnter = "nav.enter"                       // Cmd+Down; меню «Переход»; в контекстном меню пакета — «Показать содержимое пакета»
```

Решения:
- `GoUpAsync` в корне источника с `HostLocationOf(CurrentPath)` переходит в `Directory` с курсором на `FocusName`. Строка «..» показывается, если путь не корень **или** у корня есть место хозяина. При обновлении исчезнувшей папки подъём к ближайшему существующему предку продолжается через место хозяина.
- `ActivateAsync` на файле: `TryMountAsync(entry, byContent: false)`; не null — переход туда; null — `FileActivated`. Вызов — вне потока интерфейса (`Task.Run`). Отмена пользователем внутри монтирования (`OperationCanceledException`) — ничего не происходит; `IOException` — панель остаётся на месте и поднимает `OpenFailed` (в `Error` панели не пишется); `ShellCommands`, уже следящий за обеими панелями, показывает его через `ShowMessageAsync` с текстом `OperationTexts.ForFailure`.
- Пакет: папка на локальном источнике с именем на `.app` (без учёта регистра). `ActivateAsync` на пакете поднимает `FileActivated` (программа запускается через `OpenDefault`), не входит. `EnterAsync`: папка, пакет или ссылка на папку — войти; файл — `TryMountAsync(entry, byContent: true)`, иначе `NotAFolder` → `ShellCommands` показывает сообщение `Panel_NotAFolder`.
- Строка пути, заголовок вкладки (`TabViewModel.Title` — последний сегмент `DisplayPath`), история показывают `DisplayPath`. Ввод пути (Cmd+Shift+G) во вложенном источнике начинается с `DisplayPath` и всегда разбирается как путь локального диска.
- `PanelTabsViewModel.ToState` сохраняет `PersistentLocationOf(path)`.
- Enter на файле не локального источника: `ShellCommands` копирует его в `TempFileStore.MaterializeAsync` (вне потока интерфейса) и открывает копию `OpenDefault`; правки копии назад не попадают.
- `TestApp.Services` получает `TempFileStore` во временной папке теста.

`FakeHosted` (тестовый): `InMemoryFileSystem`-обёртка с `Id = "hosted"`, `IHostedProvider` (корень → заданное место, `DisplayPath` = префикс + путь) и `IFileMounter`, открывающий файлы `*.pack` по имени и любые по содержимому `PK`.

- [ ] **Step 1: Тесты**

```csharp
public async Task Enter_on_mountable_file_navigates_into_it()
public async Task Up_from_hosted_root_returns_to_host_folder_with_cursor_on_the_file()
public async Task Hosted_root_shows_parent_link()
public async Task Path_bar_and_tab_title_use_display_path()
public async Task Tab_inside_hosted_provider_is_saved_as_host_folder()         // ToState → local, папка файла-хозяина
public async Task Refresh_after_host_vanished_climbs_out_to_host_folder()
public async Task Mount_failure_shows_message_and_panel_stays()
public async Task Cancelled_mount_does_nothing()
public async Task Enter_on_app_bundle_opens_it_instead_of_navigating()         // Shell.Calls == [("open", "/home/Apps/Calc.app")]
public async Task Nav_enter_goes_inside_app_bundle()
public async Task Nav_enter_mounts_by_content_and_reports_plain_file()         // .docx с "PK" входит; txt → NotAFolder
public async Task Enter_on_file_of_non_local_provider_opens_temp_copy()        // путь в Shell.Calls лежит в Temp.Directory
public async Task Typed_path_in_hosted_panel_addresses_local_disk()
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.App.Tests --filter "FullyQualifiedName~HostedPanel"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): panels over hosted providers, app bundles open on Enter`.

---

### Task 6: Проект `Tandem.Archives` и проверка SharpCompress 1.0

Цель — до начала работ по архивам зафиксировать тестами то поведение библиотеки, на которое опирается план.

**Files:**
- Create: `src/Tandem.Archives/Tandem.Archives.csproj` (ссылка на Core, `SharpCompress`; `InternalsVisibleTo Tandem.Archives.Tests`), `src/Tandem.Archives/ArchiveFormat.cs`, `tests/Tandem.Archives.Tests/Tandem.Archives.Tests.csproj` (как `Tandem.Core.Tests`; ссылки на Archives и Core; связанный файл `..\Tandem.Core.Tests\Operations\ScriptedInteraction.cs`; `Fixtures/**` копируются в вывод), `tests/Tandem.Archives.Tests/Fixtures/make-fixtures.sh`, `Fixtures/README.md`, файлы архивов, `Fixture.cs` (вспомогательный)
- Modify: `Directory.Packages.props`, `Tandem.slnx`
- Test: `tests/Tandem.Archives.Tests/SharpCompressProbeTests.cs`, `ArchiveFormatsTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Archives;
public enum ArchiveFormat { Zip, Tar, TarGz, TarBz2, TarXz, SevenZip, Rar }
public static class ArchiveFormats {
    public static ArchiveFormat? FromName(string fileName);    // .zip .tar .tar.gz .tgz .tar.bz2 .tbz2 .tar.xz .txz .7z .rar, без учёта регистра
    public static Task<ArchiveFormat?> DetectAsync(Stream seekable, CancellationToken ct);   // по сигнатуре; поток возвращается на начало
    public static string DefaultExtension(ArchiveFormat format);   // "zip","tar","tar.gz","tar.bz2","tar.xz","7z","rar"
    public static IReadOnlyList<ArchiveFormat> Creatable { get; }   // Zip, Tar, TarGz, SevenZip (SevenZip — только если прошёл тест создания)
    public static bool CanEdit(ArchiveFormat format);              // только Zip
}
internal static class Fixture { public static byte[] Bytes(string name); public static string Path(string name); }
```

Образцы. Общее дерево: `a.txt` («a»), `docs/readme.txt` («hello\n»), `docs/sub/data.bin` (байты 0…255), `привет.txt` («привет\n» в UTF-8), пустая папка `empty/`. `make-fixtures.sh` строит системными средствами (`zip`, `bsdtar`, `python3`): `sample.zip`, `sample.tar`, `sample.tar.gz`, `sample.tar.bz2`, `sample.tar.xz`, `sample.7z`, `encrypted.zip` (`zip -e`, пароль `secret`), `cp866.zip` (имя `привет.txt` в CP866 без признака UTF-8), `slip.zip` (записи `../../evil.txt`, `/abs.txt`, `a/../../b.txt`, `dir\win.txt`), `trap.tar` (ссылка `link` → `/tmp`, затем запись `link/file.txt`), `broken.zip` (первая половина `sample.zip`). RAR, сплошные (solid) 7z и RAR и зашифрованный 7z системными средствами не создать: взять подходящие файлы из `tests/TestArchives/Archives` репозитория SharpCompress (лицензия MIT) под именами `sample.rar`, `solid.rar`, `solid.7z`, `encrypted.7z`, записать в `Fixtures/README.md` исходные имена, коммит, пароль и ожидаемые имена записей. Все образцы коммитятся; скрипт нужен только для воспроизведения. **Если скачать файлы нельзя — остановиться и доложить, не подменяя формат.**

- [ ] **Step 1: Тесты**

```csharp
// ArchiveFormatsTests
[Theory] FromName: "a.ZIP"→Zip, "a.tar.gz"→TarGz, "a.tgz"→TarGz, "a.txz"→TarXz, "a.7z"→SevenZip, "a.rar"→Rar, "a.docx"→null, "a.gz"→null
[Theory] public async Task Detects_by_signature(string fixture, ArchiveFormat expected)     // все образцы; "a.txt" → null
// SharpCompressProbeTests — только вызовы библиотеки
[Theory] public void Lists_and_reads_every_readable_format(string fixture)   // есть запись docs/readme.txt (для чужих образцов — из README), содержимое совпадает
public void Solid_archives_extract_all_entries_in_one_pass()
[Theory] public void Creates_and_reads_back(ArchiveFormat format)            // Zip, Tar, TarGz, SevenZip
public void Zip_writer_works_on_a_non_seekable_stream()
public void Writer_stores_an_empty_directory_and_modified_time()
public void Entry_stream_may_be_abandoned_half_read()
public void Encrypted_zip_lists_without_password_and_marks_entries_encrypted()
public void Encrypted_entries_read_with_the_right_password()                  // zip и 7z
public void Wrong_or_missing_password_fails_with_a_recognisable_exception()   // тип исключения записать в комментарии теста
public void Tar_symlink_entry_exposes_its_target()
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Archives.Tests` → FAIL (проекта и типов нет).
- [ ] **Step 3:** Создать проекты, образцы, `ArchiveFormats`. Тесты-пробы писать по документации 1.0. **Если какая-то проба показывает, что библиотека этого не умеет (кроме создания 7z — тогда убрать `SevenZip` из `Creatable` и записать в отчёт) — остановиться и доложить.**
- [ ] **Step 4:** Run `dotnet build Tandem.slnx && dotnet test tests/Tandem.Archives.Tests` → PASS, 0 предупреждений.
- [ ] **Step 5:** Commit: `feat(archives): project, fixtures and SharpCompress probes`.

---

### Task 7: Кодировки текста и их определение

**Files:**
- Create: `src/Tandem.Core/Viewing/TextEncodings.cs`, `TextEncodingDetector.cs`
- Test: `tests/Tandem.Core.Tests/Viewing/TextEncodingDetectorTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Viewing;
public sealed record TextEncodingInfo(string Id, Encoding Encoding);     // Encoding не бросает на неверных байтах (замена U+FFFD)
public static class TextEncodings {                                       // регистрирует CodePagesEncodingProvider
    public static TextEncodingInfo Utf8, Utf16LE, Utf16BE, Windows1251 { get; }
    /// Порядок списка для ручного выбора: utf-8, utf-16le, utf-16be, windows-1251, windows-1252, koi8-r, cp866, x-mac-cyrillic
    public static IReadOnlyList<TextEncodingInfo> All { get; }
    public static TextEncodingInfo? Find(string id);
}
public sealed record EncodingGuess(TextEncodingInfo Encoding, int BomLength, bool LooksBinary);
public static class TextEncodingDetector {
    public const int SampleSize = 64 * 1024;
    public static EncodingGuess Detect(ReadOnlySpan<byte> sample, bool sampleIsWholeFile);
}
```

Алгоритм (по порядку, первое сработавшее):
1. Метка: `EF BB BF` → utf-8 (3); `FF FE` → utf-16le (2); `FE FF` → utf-16be (2).
2. UTF-16 без метки (длина пробы ≥ 16): доля байтов `0x00` или `0x04` на нечётных позициях ≥ 0,9 и на чётных < 0,5 → utf-16le; зеркально → utf-16be.
3. В пробе есть `0x00`, либо доля управляющих байтов (< `0x20`, кроме 09, 0A, 0C, 0D, 1B) > 0,1 → `LooksBinary = true`, кодировка windows-1251.
4. Проба — правильный UTF-8 (если `sampleIsWholeFile == false`, незавершённая последовательность в последних 3 байтах допустима) → utf-8. Чистый ASCII и пустая проба — тоже utf-8.
5. Иначе windows-1251.

- [ ] **Step 1: Тесты**

```csharp
public void Bom_decides_utf8_utf16le_utf16be()                               // BomLength 3, 2, 2
public void Ascii_and_empty_are_utf8_and_not_binary()
public void Russian_utf8_is_utf8()
public void Russian_windows1251_is_windows1251()                             // RF4: "Привет, мир! Это текст в кодировке Windows-1251."
public void Single_1251_letter_among_ascii_is_windows1251()                  // "Price: 5 \xF0."
public void Utf8_cut_inside_a_character_at_sample_end_is_still_utf8()        // RF4: sampleIsWholeFile: false, обрезаны 1 и 2 байта
public void Truncated_sequence_in_a_whole_file_is_windows1251()
public void Utf16le_without_bom_latin_and_cyrillic()                         // RF4: "Hello, world!!!!" и "Привет, мир!!!!!" → utf-16le
public void Utf16be_without_bom()
public void Png_header_and_zero_filled_data_look_binary()
public void Every_listed_encoding_roundtrips_russian_text_except_windows1252()
public void Find_is_case_insensitive_and_null_for_unknown()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~TextEncoding"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): text encodings and detection`.

---

### Task 8: Постраничное чтение текста и hex для файлов любого размера

Просмотр не строит индекс строк и не читает файл целиком: положение — смещение в байтах, страница читается с этого места.

**Files:**
- Create: `src/Tandem.Core/Viewing/PagedTextReader.cs`, `HexDump.cs`
- Test: `tests/Tandem.Core.Tests/Viewing/PagedTextReaderTests.cs`, `HexDumpTests.cs`, `SparseStream.cs` (вспомогательный)

**Interfaces:**
- Consumes: `TextEncodingInfo` (задача 7).
- Produces:

```csharp
namespace Tandem.Core.Viewing;
public sealed record TextLine(long Offset, int ByteLength, string Text, bool EndsLine);   // EndsLine == false — кусок длинной строки
public sealed record TextPage(long StartOffset, long EndOffset, IReadOnlyList<TextLine> Lines);
public sealed class PagedTextReader {
    public const int MaxLineBytes = 4096;
    public const int BackScanLimit = 64 * 1024;
    public PagedTextReader(Stream seekable, TextEncodingInfo encoding, int bomLength);   // поток не закрывает
    public long Length { get; }
    public Task<long> LineStartAsync(long offset, CancellationToken ct);
    public Task<TextPage> ReadPageAsync(long offset, int lineCount, CancellationToken ct);   // с LineStartAsync(offset)
    public Task<long> StepBackAsync(long offset, int lineCount, CancellationToken ct);       // начало строки на lineCount выше; не меньше bomLength
}
public static class HexDump {
    public const int BytesPerRow = 16;
    public static string FormatRow(long offset, ReadOnlySpan<byte> bytes);
    public static Task<IReadOnlyList<string>> ReadRowsAsync(Stream seekable, long offset, int rowCount, CancellationToken ct);  // offset округляется вниз до 16
}
```

Правила чтения:
- Перевод строки — `\n` в единицах кодировки (в UTF-16 — двухбайтовый, выравнивание от `bomLength`); `\r` перед ним в `Text` не попадает. Табуляция и прочие символы — как есть.
- `LineStartAsync(offset)`: искать назад не дальше `BackScanLimit` байт ближайший перевод строки; найден — строка начинается сразу за ним; не найден — с `max(bomLength, offset − BackScanLimit)`, сдвинутого вперёд до границы символа. От этого начала идти вперёд кусками до куска, содержащего `offset`: кусок кончается переводом строки либо через `MaxLineBytes` байт, отступив назад до границы символа (UTF-8 — не внутри последовательности; UTF-16 — чётное число байт и не между суррогатами).
- За одно обращение читается не больше `BackScanLimit + lineCount × MaxLineBytes` байт.
- Строка hex: смещение `X8` (шире, если не помещается), два пробела, 16 байтов `XX ` с дополнительным пробелом после восьмого, пробел, `|…|` — печатные ASCII как есть, остальное `.`. Пример полной строки: `00000010  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 0A 00 FF  |Hello, world!...|`; неполная строка дополняется пробелами до той же ширины перед `|`.

`SparseStream` (тестовый): поток только для чтения с заданной `Length`, содержимое вычисляется по позиции (`(pos) => byte`), считает `BytesRead` и наибольший запрошенный `count`.

- [ ] **Step 1: Тесты**

```csharp
public async Task Reads_lines_with_offsets_and_strips_cr()                   // "a\nbb\r\nccc" → (0,"a"),(2,"bb"),(6,"ccc"); EndsLine true,true,false
public async Task Offset_inside_a_line_aligns_to_its_start()
public async Task Page_end_offset_is_the_start_of_the_next_page()            // страницы подряд дают весь текст без повторов и пропусков
public async Task Utf8_characters_are_never_split()                           // одна строка из 5000 «я»: в кусках нет U+FFFD, склейка == исходной
public async Task Utf16_uses_two_byte_newlines_and_keeps_surrogate_pairs()
public async Task Long_line_is_cut_into_pieces_of_at_most_MaxLineBytes()
public async Task StepBack_walks_lines_and_stops_at_the_start()
public async Task Bom_is_skipped()
public async Task Page_from_the_middle_of_8_GiB_reads_under_one_mebibyte()    // RF3: SparseStream 8 ГиБ строк "line NNNNNNNNNN\n"; BytesRead < 1 МиБ
public async Task File_of_one_8_GiB_line_pages_without_scanning()             // RF3: без '\n'; страница с 4 ГиБ, StepBack, конец файла — каждое < 1 МиБ
public async Task Reading_past_the_end_gives_an_empty_page()
[Theory] public void Formats_full_and_short_rows(...)                         // строки из примера выше, смещение 0x1_0000_0000 даёт 9 цифр
public async Task Hex_rows_from_8_GiB_offset_read_only_those_rows()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~PagedTextReader|FullyQualifiedName~HexDump"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): paged text reader and hex dump`.

---

### Task 9: Встроенный редактор (F4) на AvaloniaEdit

**Files:**
- Create: `src/Tandem.App/ViewModels/Editing/EditorViewModel.cs`, `Views/Editing/EditorWindow.axaml(.cs)`, `Services/IWindowService.cs`, `Services/WindowService.cs`
- Modify: `Directory.Packages.props`, `src/Tandem.App/Tandem.App.csproj`, `App.axaml` (стили AvaloniaEdit), `Services/IDialogService.cs`, `DialogService.cs`, `Views/Dialogs/ChoiceDialogs.cs`, `ViewModels/ShellCommands.cs`, `MainViewModel.cs`, `Composition.cs` (`AppServices` + `IWindowService Windows`), `Views/MainWindow.axaml.cs`, ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/EditorViewModelTests.cs`, `Views/EditorWindowTests.cs`, `Views/ScriptedDialogs.cs`, `Views/TestApp.cs`

**Interfaces:**
- Consumes: `SafeReplace.WriteAsync` (1), `BatchScope` (3), `TextEncodingDetector`, `TextEncodings` (7), `ProviderRegistry.DisplayPath` (4).
- Produces:

```csharp
public enum SaveChangesAnswer { Save, Discard, Cancel }
public interface IDialogService { ... Task<SaveChangesAnswer> AskSaveChangesAsync(string fileName); }   // отменённый диалог = Cancel
public interface IWindowService {
    void ShowEditor(EditorViewModel editor);
    /// Закрывает все окна просмотра и правки; изменённый редактор спрашивает. false — пользователь отменил.
    Task<bool> CloseAllAsync();
}
public sealed class WindowService : IWindowService { public Window? Owner { get; set; } public IReadOnlyList<Window> Open { get; } }
public sealed class EditorRefusedException(EditorRefusal reason) : Exception;   // enum EditorRefusal { TooLarge, Binary }
public sealed partial class EditorViewModel : ObservableObject {
    public const long MaxFileBytes = 32 * 1024 * 1024;
    public static Task<EditorViewModel> OpenAsync(ProviderRegistry providers, VfsPath file, CancellationToken ct);
    public VfsPath File { get; }  public string Title { get; }             // DisplayPath; со звёздочкой, пока IsModified
    public AvaloniaEdit.Document.TextDocument Document { get; }
    public TextEncodingInfo Encoding { get; }  public bool HasBom { get; }
    public bool IsReadOnly { get; }                                         // источник без ProviderCapabilities.Write
    public bool IsModified { get; }  public string? Error { get; }
    public Task<bool> SaveAsync();                                          // false и Error при неудаче
}
```

Решения:
- `file.edit` (F4, Cmd+4): файл под курсором активной панели любого источника; папка — команда недоступна. `OpenAsync` вне потока интерфейса; `EditorRefusedException` → сообщение (`Editor_TooLarge` называет предел 32 МБ и советует F3; `Editor_Binary` советует F3). Повторный F4 на уже открытом файле выводит его окно вперёд.
- Кодировка — `TextEncodingDetector` по первым 64 КиБ; `LooksBinary` → отказ. Сохранение — в той же кодировке, с той же меткой, окончания строк как в документе.
- `SaveAsync`: `BatchScope` на файле; `SafeReplace.WriteAsync(provider, File, replace: true, …)`; если у исходного файла был `Mode` и источник умеет права — `SetModeAsync` на временный файл до подмены. При ошибке исходный файл цел, `IsModified` остаётся.
- Окно: `AvaloniaEdit.TextEditor`, моноширинный шрифт, номера строк, `SearchPanel.Install` (Cmd+F внутри окна). Cmd+S — сохранить; Esc и Cmd+W — закрыть; закрытие изменённого — `AskSaveChangesAsync` (Save при ошибке сохранения окно не закрывает). Окно не модальное.
- Выход из приложения: `MainWindow.OnClosing` задерживает закрытие не только при идущих операциях (`HasOperations`), но и пока открыто хотя бы одно окно `WindowService`; `MainViewModel.ConfirmCloseAsync` сначала вызывает `Windows.CloseAllAsync()` (`false` отменяет выход), затем, как раньше, спрашивает про операции, если они есть.
- `TestApp` по умолчанию использует настоящий `WindowService` (окна без экрана) и отдаёт его как `app.Windows`.

- [ ] **Step 1: Тесты**

```csharp
// EditorViewModelTests
public async Task Opens_utf8_and_windows1251_text_with_detected_encoding()
public async Task Save_keeps_encoding_and_bom()                              // 1251 остаётся 1251; UTF-8 с меткой — с меткой, без — без
public async Task Save_keeps_crlf_line_endings()
public async Task Save_keeps_unix_mode()                                     // 0o755 до и после
public async Task Failed_save_keeps_original_and_reports_error()             // FailWriteAfter на part: содержимое прежнее, part нет, IsModified
public async Task File_over_32_MiB_is_refused()                              // EditorRefusal.TooLarge
public async Task Binary_file_is_refused()
public async Task Provider_without_write_opens_read_only()
public async Task Save_inside_batching_provider_commits_once()               // BatchingProvider-подобная обёртка
// EditorWindowTests
public async Task F4_opens_an_editor_window_with_the_file_text()             // окно содержит AvaloniaEdit.TextEditor, Document.Text == содержимому
public async Task Typing_marks_modified_and_Cmd_S_saves()
public async Task Closing_modified_editor_asks_and_Cancel_keeps_it_open()
public async Task Second_F4_on_same_file_reuses_the_window()
public async Task Quit_with_modified_editor_can_be_cancelled()
public async Task F4_on_too_large_file_shows_message()
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~Editor"` → FAIL.
- [ ] **Step 3:** Реализовать. Подключение стилей AvaloniaEdit — по его документации для Avalonia 12. **Если `Avalonia.AvaloniaEdit` 12.0.0 не собирается или не работает с Avalonia 12.1.6 — остановиться и доложить, не меняя версию Avalonia и не подменяя редактор.**
- [ ] **Step 4:** Run → PASS; `dotnet test Tandem.slnx` → PASS. Затем вручную `dotnet run --project src/Tandem.App`: F4 на текстовом файле, правка, Cmd+S, содержимое на диске изменилось.
- [ ] **Step 5:** Commit: `feat(app): built-in text editor on AvaloniaEdit`.

---

### Task 10: Встроенный просмотр (F3): текст, hex, изображения

**Files:**
- Create: `src/Tandem.App/ViewModels/Viewing/ViewerViewModel.cs`, `Views/Viewing/ViewerView.axaml(.cs)`, `Views/Viewing/ViewerWindow.axaml(.cs)`
- Modify: `Services/IWindowService.cs`, `WindowService.cs`, `ViewModels/ShellCommands.cs`, `App.axaml` (кисти просмотра), ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/ViewerViewModelTests.cs`, `Views/ViewerWindowTests.cs`, `ViewModels/StreamProvider.cs` (вспомогательный: источник, отдающий заданный поток, в том числе без перемотки)

**Interfaces:**
- Consumes: `PagedTextReader`, `HexDump` (8), `TextEncodingDetector`, `TextEncodings` (7), `TempFileStore` (4), `IWindowService` (9).
- Produces:

```csharp
public enum ViewerMode { Text, Hex, Image }
public interface IWindowService { ... void ShowViewer(ViewerViewModel viewer); }
public sealed partial class ViewerViewModel : ObservableObject, IAsyncDisposable {
    public const long MaxImageBytes = 512L * 1024 * 1024;
    public const int MaxImageWidth = 4096;
    public static Task<ViewerViewModel> OpenAsync(ProviderRegistry providers, TempFileStore temp, VfsPath file, CancellationToken ct);
    public string Title { get; }  public long Length { get; }
    public ViewerMode Mode { get; set; }  public IReadOnlyList<ViewerMode> Modes { get; }      // Image — только когда картинка разобралась
    public TextEncodingInfo Encoding { get; set; }  public IReadOnlyList<TextEncodingInfo> Encodings { get; }   // TextEncodings.All
    public bool WrapLines { get; set; }
    public IReadOnlyList<string> Lines { get; }        // текущая страница: строки текста или строки hex
    public long Offset { get; }                         // смещение первой показанной строки
    public int PageLines { get; set; }                  // задаёт представление по своей высоте; по умолчанию 40
    public Task ScrollLinesAsync(int delta);  public Task ScrollToOffsetAsync(long offset);
    public Task GoToStartAsync();  public Task GoToEndAsync();
    public Avalonia.Media.Imaging.Bitmap? Image { get; }
    public string StatusText { get; }                   // процент, размер, кодировка
}
```

Решения:
- `file.view` (F3, Cmd+3): файл под курсором любого источника; на папке и «..» ничего. `file.quickLook` (Cmd+Y) по-прежнему открывает Quick Look.
- Открытие: поток источника; если он без перемотки (`CanSeek == false`) — сначала `temp.MaterializeAsync`, копия освобождается при закрытии. Поток держится открытым, пока открыт просмотр.
- Начальный режим: расширение из `png jpg jpeg gif bmp webp ico`, размер ≤ `MaxImageBytes` и картинка разобралась (`Bitmap.DecodeToWidth(stream, MaxImageWidth)`, вне потока интерфейса) → `Image`; иначе проба 64 КиБ: `LooksBinary` → `Hex`, иначе `Text` в угаданной кодировке. Неразобравшаяся картинка — `Hex`.
- Смена кодировки перечитывает страницу с того же смещения (выровненного по новой кодировке). Смена режима Text ↔ Hex сохраняет смещение.
- Прокрутка — по строкам через `StepBackAsync`/`ReadPageAsync`; полоса прокрутки отображает смещение `0…Length`. Вся страница — текст, который можно выделить и скопировать (Cmd+C).
- Клавиши окна (не команды, не переназначаются): Esc, F3, Cmd+W — закрыть; ↑/↓ — строка; PageUp/PageDown и Пробел — страница; Home/End — начало/конец; Cmd+1/2/3 — текст/hex/картинка; Cmd+E — следующая кодировка.
- `ViewerView` — `UserControl` с `DataContext` = `ViewerViewModel`; его же использует панель быстрого просмотра (задача 11). Моноширинный шрифт; цвета — новые ключи `Tandem.Viewer*` в обеих темах.

- [ ] **Step 1: Тесты**

```csharp
// ViewerViewModelTests
public async Task Text_file_opens_as_text_in_detected_encoding()              // RF4: 1251 → Lines[0] == "Привет"
public async Task Changing_encoding_redecodes_the_same_place()                // RF4: открыт как utf-8 с мусором → Encoding = cp866 → правильный текст
public async Task Binary_file_opens_in_hex_with_offset_bytes_and_ascii()
public async Task Scroll_moves_by_lines_and_clamps_at_both_ends()
public async Task End_shows_the_last_page_and_Start_the_first()
public async Task Png_opens_as_image_and_can_switch_to_hex()                  // PNG 1×1 массивом байтов в тесте
public async Task Broken_image_falls_back_to_hex()
public async Task Non_seekable_source_is_copied_to_temp_once_and_released_on_dispose()
public async Task Eight_GiB_file_opens_and_jumps_to_end_reading_under_two_mebibytes()   // RF3: StreamProvider + SparseStream
public async Task Dispose_closes_the_stream()
// ViewerWindowTests
public async Task F3_opens_viewer_window_for_cursor_file()
public async Task F3_on_folder_does_nothing()
public async Task Esc_closes_the_viewer()
public async Task Cmd_Y_still_toggles_quick_look()
public async Task PageDown_in_viewer_scrolls_a_page()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Viewer"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS. Вручную: `mkfile -n 5g /tmp/big.bin` и текстовый файл на 3 ГБ (`yes "строка текста" | head -c 3221225472 > /tmp/big.txt`), F3 на каждом — окно открывается сразу, End и перетаскивание полосы прокрутки без задержки, память процесса не растёт.
- [ ] **Step 5:** Commit: `feat(app): built-in viewer for text, hex and images`.

---

### Task 11: Панель быстрого просмотра (Ctrl+Q)

**Files:**
- Create: `src/Tandem.App/ViewModels/Viewing/QuickViewViewModel.cs`
- Modify: `ViewModels/MainViewModel.cs`, `SideViewModel.cs`, `Views/PanelView.axaml`, `Commands/CommandIds.cs`, `KeyMap.cs`, `Views/MainMenu.cs` (меню «Вид»), `Composition.cs` (задержка для тестов), ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/QuickViewTests.cs`, `Views/QuickViewWindowTests.cs`

**Interfaces:**
- Consumes: `ViewerViewModel.OpenAsync`, `ViewerView` (10).
- Produces:

```csharp
CommandIds.ViewQuickView = "view.quickView"                                   // Ctrl+Q
public sealed partial class QuickViewViewModel : ObservableObject, IDisposable {
    public static readonly TimeSpan FollowDelay = TimeSpan.FromMilliseconds(150);
    public QuickViewViewModel(WorkspaceViewModel workspace, ProviderRegistry providers, TempFileStore temp,
        Func<TimeSpan, CancellationToken, Task>? delay = null);
    public bool IsOpen { get; }
    public PanelSide? Side { get; }                    // сторона, чья панель заменена: пассивная в момент открытия
    public ViewerViewModel? Viewer { get; }            // null для папки, «..», пустой панели, ошибки
    public string? Message { get; }                    // имя папки и «Папка», либо текст ошибки
    public void Toggle();  public void Close();
}
public sealed partial class MainViewModel { public QuickViewViewModel QuickView { get; } }
public sealed partial class SideViewModel { public bool ShowsQuickView { get; } }   // QuickView.IsOpen && QuickView.Side == Side
```

Решения: при открытии показывается файл под курсором активной панели; при движении курсора и смене папки — следующий файл после `FollowDelay` покоя, предыдущий `ViewerViewModel` освобождается. Смена активной панели (Tab, щелчок по другой панели) закрывает быстрый просмотр. Список файлов заменённой стороны скрыт, строка пути и вкладки остаются. `Composition.Build/StartAsync` используют уже существующий параметр `quickLookDelay` и для этой задержки.

- [ ] **Step 1: Тесты**

```csharp
public async Task Ctrl_Q_shows_cursor_file_in_the_passive_side()
public async Task Follows_cursor_after_the_delay()
public async Task Fast_cursor_moves_open_only_the_last_file()                 // задержка-ворота: OpenAsync вызван 1 раз
public async Task Previous_viewer_is_disposed_when_following()
public async Task Folder_under_cursor_shows_a_message_not_a_viewer()
public async Task Switching_panel_closes_quick_view()
public async Task Second_Ctrl_Q_closes_and_the_list_returns()                 // окно: ListBox видим, ViewerView нет
public async Task Unreadable_file_shows_error_message_and_keeps_following()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~QuickView"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): quick view panel`.

---

### Task 12: Файл настроек

**Files:**
- Create: `src/Tandem.Core/Settings/AppSettings.cs`, `JsonSettingsStore.cs`
- Modify: `src/Tandem.Core/Settings/AppState.cs` (`ViewState`), `JsonStateStore.cs` (общий внутренний помощник атомарной записи — без изменения поведения)
- Test: `tests/Tandem.Core.Tests/Settings/JsonSettingsStoreTests.cs`, `JsonStateStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Tandem.Core.Settings;
public sealed record ViewState(bool Sidebar = false, bool VolumeBars = true, bool CommandLine = true, bool FKeyBar = true,
    bool ButtonBar = true);                                                  // новый последний параметр
public enum ThemeChoice { System, Light, Dark }
public enum ColumnId { Extension, Size, Modified, Mode }                     // «Имя» есть всегда, первым
public sealed record ColumnSetting(ColumnId Column, bool Visible, double Width);
public sealed record KeyBindingOverride(string CommandId, IReadOnlyList<string> Chords);   // полная замена сочетаний команды; пусто — без сочетаний
public sealed record FileColorRule(string Mask, string Color);              // маска FileMask; Color — имя из ColorNames
public sealed record UserButton(string Title, string Program, string Arguments);
public sealed record AppSettings(int Version, ThemeChoice Theme, double FontSize, double RowHeight,
    IReadOnlyList<ColumnSetting> LeftColumns, IReadOnlyList<ColumnSetting> RightColumns,
    IReadOnlyList<FileColorRule> FileColors, IReadOnlyList<KeyBindingOverride> KeyBindings,
    IReadOnlyList<UserButton> Buttons, string? ExternalEditor) {
    public const int CurrentVersion = 1;
    public const double DefaultFontSize = 14, MinFontSize = 10, MaxFontSize = 24;
    public const double DefaultRowHeight = 22, MinRowHeight = 16, MaxRowHeight = 48;
    public const double MinColumnWidth = 30, MaxColumnWidth = 400;
    public static IReadOnlyList<string> ColorNames { get; }                  // Red, Orange, Yellow, Green, Teal, Blue, Purple, Gray
    public static IReadOnlyList<ColumnSetting> DefaultColumns { get; }       // Extension 56, Size 84, Modified 124, Mode 84 — все видимы
    public static AppSettings Default { get; }                               // System, 14, 22, DefaultColumns ×2, остальное пусто, ExternalEditor null
    public AppSettings Normalized();
}
public sealed class JsonSettingsStore(string directory) {                    // settings.json, settings.json.bad
    public AppSettings Load();                                               // никогда не бросает
    public void Save(AppSettings settings);                                  // IOException при неудаче, прежний файл цел
}
```

Решения: правила хранилища — те же, что у `JsonStateStore`: нет файла или он не читается → умолчания; не JSON, `Version` вне `1…CurrentVersion`, неизвестное значение перечисления → файл откладывается как `settings.json.bad`, умолчания; запись атомарная. Остальное лечит `Normalized()` (вызывается при `Load`): числа зажимаются в пределы, `RowHeight` не меньше `FontSize + 6`; `null`-списки становятся пустыми; в столбцах убираются повторы и дописываются недостающие из умолчаний, ширина зажимается; правила цвета с пустой маской или неизвестным цветом и кнопки с пустым `Title` или `Program` отбрасываются; пустой `ExternalEditor` → null. `state.json`: `AppState.CurrentVersion` остаётся 1; старый файл без `ButtonBar` читается с `ButtonBar = true`.

- [ ] **Step 1: Тесты**

```csharp
// JsonSettingsStoreTests
public void Missing_file_gives_defaults()
public void Roundtrip_keeps_every_field()
public void Damaged_file_is_set_aside_as_bad_and_defaults_returned()
public void Future_version_is_set_aside()
public void Unknown_theme_text_sets_the_file_aside()
public void Out_of_range_numbers_are_clamped()                              // FontSize 99 → 24; RowHeight 5 → 30 (24 + 6)
public void Missing_and_duplicate_columns_are_repaired()
public void Bad_colour_rules_and_buttons_are_dropped()
public void Save_does_not_touch_state_json()
public void Failed_save_keeps_previous_file()
// JsonStateStoreTests
public void Stage1_state_file_loads_with_button_bar_shown()                 // дословный JSON этапа 1 без ButtonBar
public void State_version_is_still_1_and_saved_file_is_accepted_by_stage1_rules()   // Version == 1 в записанном файле
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Settings"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): settings file`.

---

### Task 13: Служба настроек и переназначение клавиш

**Files:**
- Create: `src/Tandem.App/Services/SettingsService.cs`, `Services/KeyChordRules.cs`
- Modify: `Commands/KeyMap.cs`, `ViewModels/MainViewModel.cs`, `Composition.cs`, `Views/MainWindow.axaml.cs` (пересборка меню), ресурсы (подписи `Menu_<id>` для команд, у которых их нет)
- Test: `tests/Tandem.App.Tests/Commands/KeyMapTests.cs`, `Commands/KeyChordRulesTests.cs`, `Services/SettingsServiceTests.cs`, `ViewModels/MainViewModelTests.cs`, `Views/MainWindowTests.cs`, `LocalizationTests.cs`, `Views/TestApp.cs`

**Interfaces:**
- Consumes: `AppSettings`, `JsonSettingsStore`, `KeyBindingOverride` (12).
- Produces:

```csharp
public sealed class SettingsService {
    public SettingsService(JsonSettingsStore store, Action<Exception>? saveFailed = null);
    public AppSettings Current { get; }                                      // до Load — AppSettings.Default
    public event EventHandler? Changed;                                      // в потоке вызвавшего Update (поток интерфейса)
    public void Load();
    public void Update(AppSettings settings);                                // Normalized → Current → Changed → Save; ошибка записи — в saveFailed, Current остаётся новым
}
public sealed class KeyMap {
    public KeyMap WithOverrides(IReadOnlyList<KeyBindingOverride> overrides);
    public IReadOnlyList<KeyBindingOverride> OverridesAgainst(KeyMap defaults);   // только команды, чьи сочетания отличаются
}
public static class KeyChordRules { public static bool IsAssignable(KeyChord chord); }
public sealed partial class MainViewModel {
    public MainViewModel(WorkspaceViewModel workspace, AppServices services, SettingsService settings,
        Func<TimeSpan, CancellationToken, Task>? progressWindowDelay = null, Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null);
    public SettingsService Settings { get; }
    public KeyMap KeyMap { get; }                                             // KeyMap.Default.WithOverrides(Settings.Current.KeyBindings); PropertyChanged при смене
}
public static class Composition {
    public static MainViewModel Build(AppServices services, JsonStateStore store, SettingsService settings, string homeDirectory, ...);
    // StartAsync создаёт JsonSettingsStore(stateDirectory) и SettingsService, вызывает Load() вместе с чтением state.json
}
```

Решения:
- `WithOverrides`: для каждой записи с `CommandId` из `CommandIds.All` сочетания этой команды заменяются разобранными (`KeyChord.Parse`) и допустимыми (`IsAssignable`); сочетание, отданное команде, снимается со всех остальных; при споре двух записей побеждает более поздняя; неизвестные команды и неразборчивые сочетания пропускаются. Никогда не бросает.
- `IsAssignable`: да — F1…F24 с любыми модификаторами; любая клавиша с Cmd или Ctrl; Tab, Space, Back, Delete, Insert без модификаторов или с Shift/Alt. Нет — Enter, Escape; стрелки, Home, End, PageUp, PageDown без Cmd/Ctrl (ими управляет панель, они не команды); буквы, цифры и знаки без Cmd/Ctrl (быстрый поиск); системные Cmd+Q, Cmd+H, Cmd+Alt+H, Cmd+Tab, Cmd+Space, Cmd+Alt+Escape.
- `KeyMap.Default` получает `(CommandIds.SelectToggleDown, "Insert")`.
- При `Settings.Changed` `MainViewModel` меняет `KeyMap`; `MainWindow` заново строит `NativeMenu`; контекстные меню читают `main.KeyMap` при открытии.
- `TestApp.StartAsync` получает параметр `AppSettings? settings = null` (пишет `settings.json` во временную папку состояния).

- [ ] **Step 1: Тесты**

```csharp
public void Override_replaces_chords_of_that_command_only()
public void Override_takes_a_chord_away_from_its_default_owner()            // Ctrl+Cmd+P → file.copy; у tab.togglePin его больше нет
public void Empty_chord_list_unbinds_the_command()
public void Unknown_command_and_bad_chord_are_ignored()
public void Later_override_wins_a_disputed_chord()
public void OverridesAgainst_roundtrips_through_WithOverrides()
[Theory] IsAssignable: "F5"✓ "Alt+F7"✓ "Cmd+K"✓ "Ctrl+Q"✓ "Tab"✓ "Insert"✓ "K"✗ "Shift+K"✗ "Enter"✗ "Escape"✗ "Down"✗ "Cmd+Down"✓ "Cmd+Q"✗ "Cmd+Tab"✗
public void Default_map_binds_Insert_to_select_toggleDown()
public void Every_default_chord_is_assignable()
public void Update_normalizes_saves_and_raises_Changed_once()
public void Failed_save_is_reported_and_new_settings_stay_current()
public async Task Changed_binding_works_without_restart()                    // file.copy на Ctrl+Cmd+K: HandleKeyAsync выполняет; F5 больше не копирует
public async Task Main_menu_shows_the_new_shortcut()
public void Every_command_id_has_a_menu_label_in_both_languages()            // Menu_<id> для всех CommandIds.All
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~KeyMap|FullyQualifiedName~KeyChordRules|FullyQualifiedName~SettingsService|FullyQualifiedName~MainViewModel|FullyQualifiedName~MainWindow|FullyQualifiedName~Localization"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): settings service and key binding overrides`.

---

### Task 14: Внешний вид: тема, шрифт и высота строк, столбцы по панелям, цвета по типам

**Files:**
- Create: `src/Tandem.App/ViewModels/ListLayoutViewModel.cs`, `Services/FileColors.cs`
- Modify: `App.axaml` (палитра `Tandem.FileColor.*`), `App.axaml.cs`, `ViewModels/SideViewModel.cs`, `MainViewModel.cs`, `Views/FileListView.axaml(.cs)`, `Views/PanelView.axaml`, `Views/MainMenu.cs` (подменю «Тема»), ресурсы
- Delete: `src/Tandem.App/Views/FileListColumns.cs`
- Test: `tests/Tandem.App.Tests/ViewModels/ListLayoutTests.cs`, `Services/FileColorsTests.cs`, `Views/AppearanceWindowTests.cs`

**Interfaces:**
- Consumes: `SettingsService` (13), `AppSettings`, `ColumnSetting`, `FileColorRule`, `ThemeChoice` (12), `FileMask`.
- Produces:

```csharp
public sealed partial class ListLayoutViewModel : ObservableObject {
    public void Apply(IReadOnlyList<ColumnSetting> columns, double fontSize, double rowHeight);
    public double FontSize { get; }  public double RowHeight { get; }
    public IReadOnlyList<ColumnSetting> Columns { get; }                     // порядок показа после «Имени»
    // сетка строки и заголовка: ячейка 0 — «Имя» (*), ячейки 1…4 — остальные в порядке Columns; скрытый столбец — ширина 0
    public int ExtensionCell { get; }  public int SizeCell { get; }  public int ModifiedCell { get; }  public int ModeCell { get; }
    public GridLength Cell1Width { get; }  public GridLength Cell2Width { get; }  public GridLength Cell3Width { get; }  public GridLength Cell4Width { get; }
}
public sealed partial class SideViewModel { public ListLayoutViewModel Layout { get; } }   // LeftColumns / RightColumns по стороне
public sealed class FileColors {
    public FileColors(IReadOnlyList<FileColorRule> rules);
    public string? ColorNameFor(FileEntry entry);                            // первое подходящее правило; для папок и ссылок на папки — null
    public static string ResourceKey(string colorName);                      // "Tandem.FileColor." + имя
}
public sealed partial class MainViewModel { public FileColors FileColors { get; } public void SetTheme(ThemeChoice theme); }  // FileColors заменяется при смене правил
```

Решения:
- Тема: `ThemeChoice.System/Light/Dark` → `Application.RequestedThemeVariant` = `Default/Light/Dark`, применяется при запуске и при `Settings.Changed` без перезапуска. Меню «Вид» → «Тема»: три переключателя, вызывают `SetTheme` (→ `Settings.Update`).
- Палитра в `App.axaml` (светлая / тёмная): Red `#FFC62828`/`#FFFF8A80`, Orange `#FFC2570C`/`#FFFFB74D`, Yellow `#FF8A6D00`/`#FFFFE082`, Green `#FF2E7D32`/`#FF81C784`, Teal `#FF00796B`/`#FF4DB6AC`, Blue `#FF1565C0`/`#FF64B5F6`, Purple `#FF6A1B9A`/`#FFCE93D8`, Gray `#FF6B6B73`/`#FFA1A1AA`.
- Цвет строки берётся из ресурса темы динамически: смена темы и смена правил перекрашивают показанные строки без перечитывания папки. Помеченная (выделенная) строка всегда `Tandem.MarkedForeground`.
- Высота строки остаётся фиксированной (виртуализация списка сохраняется); `PageSize` считается от `Layout.RowHeight`. Шрифт и высота — общие для обеих панелей, столбцы — свои у каждой.

- [ ] **Step 1: Тесты**

```csharp
public void Layout_maps_columns_to_cells_in_order_and_hides_with_zero_width()   // [Size, Mode(hidden), Extension, Modified] → SizeCell 1, ModeCell 2, Cell2Width 0
public void First_matching_colour_rule_wins_and_folders_have_none()
public void Mask_matching_ignores_case_and_normalization()
public async Task Left_and_right_panels_use_their_own_columns()               // окно: ширины заголовков разные
public async Task Hidden_column_is_absent_from_header_and_rows()
public async Task Row_height_and_font_follow_settings_without_restart()
public async Task Row_matching_a_rule_gets_the_palette_brush_and_marked_row_keeps_marked_colour()
public async Task Changing_rules_recolours_rows_without_reading_the_folder_again()   // ObservedProvider: число EnumerateAsync не выросло
public async Task Theme_choice_sets_requested_theme_variant_and_survives_restart()
public async Task Hundred_thousand_rows_still_virtualized_with_custom_row_height()   // реализованных контейнеров < 200
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ListLayout|FullyQualifiedName~FileColors|FullyQualifiedName~Appearance"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): theme choice, list layout and file colours from settings`.

---

### Task 15: Панель кнопок с пользовательскими командами; внешний редактор

**Files:**
- Create: `src/Tandem.App/Services/ArgumentTemplate.cs`, `Services/ProgramLauncher.cs`, `ViewModels/ButtonBarViewModel.cs`, `Views/ButtonBar.axaml(.cs)`
- Modify: `ViewModels/MainViewModel.cs`, `ShellCommands.cs`, `Views/MainWindow.axaml`, `Views/MainMenu.cs` (флажок «Панель кнопок»), ресурсы
- Test: `tests/Tandem.App.Tests/Services/ArgumentTemplateTests.cs`, `Services/ProgramLauncherTests.cs`, `ViewModels/ButtonBarTests.cs`, `ViewModels/ShellCommandsTests.cs`

**Interfaces:**
- Consumes: `SettingsService` (13), `UserButton`, `ViewState.ButtonBar` (12), `IProcessRunner`, `ProcessSpec`, `EditorViewModel` (9).
- Produces:

```csharp
public static class ArgumentTemplate {
    public sealed record Context(string CurrentPath, string OtherPath, string? CursorPath, IReadOnlyList<string> Targets);
    public static IReadOnlyList<string> Expand(string template, Context context);
}
public static class ProgramLauncher {
    /// Программа с именем на ".app" → /usr/bin/open -a <программа> <аргументы…>; иначе сама программа с аргументами.
    public static ProcessSpec Build(string program, IReadOnlyList<string> arguments, string workingDirectory, string homeDirectory);
}
public sealed class ButtonBarViewModel {                                     // MainViewModel.ButtonBar
    public IReadOnlyList<UserButton> Buttons { get; }                        // из настроек; PropertyChanged при их смене
    public bool CanRun { get; }                                              // активная панель на локальном источнике
    public Task RunAsync(UserButton button);
}
public sealed partial class MainViewModel { public void ToggleButtonBar(); }
```

Решения:
- Шаблон делится на аргументы по пробелам; двойные кавычки объединяют, `\"` и `\\` внутри кавычек — сами символы. Подстановки: `%p` — папка активной панели, `%t` — папка другой панели, `%f` — полный путь элемента под курсором, `%n` — его имя, `%s` — все цели (`GetOperationTargets()`), `%%` — знак процента. Аргумент, равный `%s`, раскрывается в отдельный аргумент на каждую цель; `%s` внутри большего аргумента — пути через пробел. Аргумент с `%f`, `%n` или `%s`, для которого подставить нечего, выбрасывается целиком. Программа запускается списком аргументов, без оболочки; `~` в начале `Program` — домашняя папка.
- Запуск: `Processes.Start(spec)`, вывод читается и отбрасывается, процесс не убивается и освобождается после завершения. Ошибка запуска → `ShowMessageAsync(UserCommand_StartFailed, подробность)`. Код возврата не показывается.
- Панель кнопок — верхняя строка окна (раздел 7.1), видимость `Workspace.View.ButtonBar`. Без кнопок показывает одну кнопку «Настроить…» (команда `app.settings`, до задачи 16 недоступна).
- F4: если `Settings.Current.ExternalEditor` задан и файл на локальном источнике — `ProgramLauncher.Build(editor, [путь], папка файла, home)`; иначе встроенный редактор.

- [ ] **Step 1: Тесты**

```csharp
[Theory] Expand:
//  "-a %p"                → ["-a", "/cur"]
//  "%s"                   → ["/cur/a", "/cur/b"]
//  "--files=%s"           → ["--files=/cur/a /cur/b"]
//  "\"my file\" %f"       → ["my file", "/cur/a"]
//  "--out=%t/x %n"        → ["--out=/other/x", "a"]
//  "100%% %f" без курсора → ["100%"]
//  "\"a \\\"b\\\"\""      → ["a \"b\""]
public void App_bundle_is_started_through_open()                             // FileName "/usr/bin/open", Arguments ["-a", "/Applications/X.app", "/cur/a"]
public void Plain_program_gets_arguments_and_working_directory()
public void Tilde_expands_to_home()
public async Task Button_runs_program_with_expanded_arguments_in_active_folder()   // FakeProcessRunner.Started[0]
public async Task Start_failure_shows_message()
public async Task Buttons_cannot_run_on_a_non_local_panel()
public async Task Button_bar_follows_settings_and_view_toggle()
public async Task F4_uses_external_editor_for_local_file_when_configured()
public async Task F4_uses_built_in_editor_for_non_local_file_even_when_external_is_configured()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ArgumentTemplate|FullyQualifiedName~ProgramLauncher|FullyQualifiedName~ButtonBar|FullyQualifiedName~ShellCommands"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): button bar with user commands, external editor`.

---

### Task 16: Окно настроек и редактор сочетаний

**Files:**
- Create: `src/Tandem.App/ViewModels/Settings/SettingsViewModel.cs`, `Views/Settings/SettingsWindow.axaml(.cs)`, `Views/Settings/ShortcutRecorder.cs`
- Modify: `Services/IWindowService.cs`, `WindowService.cs`, `ViewModels/MainViewModel.cs`, `Commands/CommandIds.cs`, `KeyMap.cs`, `Views/MainMenu.cs`, `App.axaml(.cs)` (меню приложения), ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/SettingsViewModelTests.cs`, `Views/SettingsWindowTests.cs`

**Interfaces:**
- Consumes: `SettingsService`, `KeyMap.WithOverrides/OverridesAgainst`, `KeyChordRules` (13), `AppSettings` (12), `MainMenu.LabelKey`, `KeyRouting.ChordFor`.
- Produces:

```csharp
CommandIds.AppSettings = "app.settings"                                      // Cmd+OemComma
public interface IWindowService { ... Task<bool> ShowSettingsAsync(SettingsViewModel settings); }   // модально; true — применено
public enum ChordAssignment { Assigned, NotAssignable, TakenByOther }
public sealed partial class SettingsViewModel : ObservableObject {
    public SettingsViewModel(SettingsService settings);
    // Вид
    public ThemeChoice Theme { get; set; }  public double FontSize { get; set; }  public double RowHeight { get; set; }
    public ObservableCollection<FileColorRuleRow> FileColors { get; }        // Mask, Color; AddFileColor(), Remove(row), MoveUp/MoveDown(row)
    // Столбцы
    public ObservableCollection<ColumnRow> LeftColumns { get; }  public ObservableCollection<ColumnRow> RightColumns { get; }   // Column, Visible, Width; MoveUp/MoveDown
    // Клавиши
    public IReadOnlyList<ShortcutRow> Shortcuts { get; }                     // по строке на CommandIds.All: CommandId, Label, Chords, IsChanged
    public string ShortcutFilter { get; set; }                               // отбор по подписи и по тексту сочетания
    public ChordAssignment Assign(ShortcutRow row, KeyChord chord, bool takeFromOther = false);
    public ShortcutRow? OwnerOf(KeyChord chord);
    public void RemoveChord(ShortcutRow row, KeyChord chord);
    public void ResetShortcut(ShortcutRow row);  public void ResetAllShortcuts();
    // Кнопки и правка
    public ObservableCollection<UserButtonRow> Buttons { get; }              // Title, Program, Arguments; Add/Remove/MoveUp/MoveDown
    public string ExternalEditor { get; set; }
    public string? Validate();                                               // первая ошибка (локализованная) или null
    public bool Apply();                                                     // false, когда Validate вернул ошибку; иначе settings.Update(...)
}
```

Решения:
- Окно модальное, вкладки: «Клавиши», «Вид» (тема, шрифт, высота строк, цвета по типам), «Столбцы» (левая и правая панель), «Панель кнопок» (с подсказкой о подстановках `%p %t %f %n %s %%`), «Правка» (внешний редактор: путь к программе или `.app`; пусто — встроенный). Кнопки: OK (применить и закрыть), «Применить», «Отмена».
- Запись сочетания: поле `ShortcutRecorder` в фокусе превращает следующее нажатие в `KeyChord` (через `KeyRouting.ChordFor`), Esc — отмена записи. `NotAssignable` — подпись с причиной; `TakenByOther` — вопрос «Сочетание занято командой „X“. Переназначить?», согласие повторяет `Assign(..., takeFromOther: true)`.
- В настройки сохраняются только отличия от `KeyMap.Default` (`OverridesAgainst`); «Сбросить всё» оставляет пустой список.
- `Validate`: маска правила цвета не пуста; у кнопки заполнены название и программа; числа в пределах `AppSettings`.
- Пункт «Настройки…» — в меню приложения «Tandem» (`NativeMenu` на `Application`, по документации Avalonia 12) и в конце меню «Вид».

- [ ] **Step 1: Тесты**

```csharp
public void Lists_every_command_with_label_and_current_chords()
public void Assign_adds_a_chord_and_marks_the_row_changed()
public void Unassignable_chord_is_refused()                                  // "K" → NotAssignable, строка не изменилась
public void Taken_chord_reports_owner_and_take_moves_it()                    // F5 для file.move: TakenByOther, OwnerOf == file.copy; с takeFromOther у file.copy F5 исчез
public void Reset_one_restores_that_commands_defaults()
public void Reset_all_then_Apply_saves_no_overrides()
public void Apply_saves_only_differences_from_defaults()
public void Filter_matches_label_and_chord_text()
public void Empty_mask_or_button_without_program_blocks_Apply()
public void Column_order_and_visibility_roundtrip_per_panel()
public void Discarded_view_model_changes_nothing()
public async Task Cmd_comma_opens_settings_window()
public async Task Recorder_turns_a_key_press_into_a_chord()
public async Task Ok_applies_theme_and_new_shortcut_immediately()            // RequestedThemeVariant и HandleKeyAsync новым сочетанием
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~SettingsViewModel|FullyQualifiedName~SettingsWindow"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): settings window with shortcut editor`.

---

### Task 17: Поиск файлов: обход, имя, фильтры

**Files:**
- Create: `src/Tandem.Core/Search/SearchQuery.cs`, `FileSearch.cs`
- Test: `tests/Tandem.Core.Tests/Search/FileSearchTests.cs`, `tests/Tandem.Core.Tests/Vfs/CountingProvider.cs` (вспомогательный: обёртка, считающая вызовы по методам и путям)

**Interfaces:**
- Consumes: `FileMask`, `IFileSystemProvider`, `InMemoryFileSystem.FailOn`.
- Produces:

```csharp
namespace Tandem.Core.Search;
public sealed record ContentQuery(string Text, bool IsRegex = false, bool CaseSensitive = false, string? EncodingId = null);  // null — определять по файлу
public sealed record SearchQuery(VfsPath Root, string NameMasks = "", bool NameIsRegex = false, ContentQuery? Content = null,
    long? MinSize = null, long? MaxSize = null, DateTimeOffset? ModifiedFrom = null, DateTimeOffset? ModifiedTo = null,
    int? MaxDepth = null, string ExcludedFolders = "", bool IncludeHidden = false);
public sealed record SearchProgress(long FoldersScanned, long FilesChecked, long Found, long FoldersSkipped, long FilesSkipped, string? CurrentFolder);
public enum SearchField { Name, Content, Encoding }
public sealed class SearchQueryException(SearchField field, string message) : Exception(message) { public SearchField Field { get; } }
public sealed class FileSearch(ProviderRegistry providers) {
    public static void Validate(SearchQuery query);                          // SearchQueryException
    public IAsyncEnumerable<FileEntry> RunAsync(SearchQuery query, IProgress<SearchProgress>? progress, CancellationToken ct);
}
```

Правила:
- Обход в глубину в порядке перечисления; результат отдаётся сразу, как найден. Проверка отмены — на каждой записи.
- Символические ссылки не раскрываются: ссылка на папку может быть результатом, но внутрь поиск не заходит (петли невозможны); содержимое ссылок не читается.
- `MaxDepth`: 0 — только сама папка `Root`; n — не глубже n уровней под ней; null — без предела.
- `ExcludedFolders` — маска `FileMask` по имени папки; исключённая папка не результат и не обходится. Скрытые (`IsHidden`) записи пропускаются целиком, если `IncludeHidden == false`.
- Имя: `NameMasks` — `FileMask` (несколько через `;`, пусто — все); при `NameIsRegex` — `Regex` с `IgnoreCase | CultureInvariant`, имя в форме C, предел 1 с на имя (превышение — не совпало).
- Папки — результаты по имени, только когда нет `Content` и фильтров размера. Размер — по файлам, границы включительно; дата — по `Modified`, границы включительно.
- Папка, чтение которой дало `IOException`/`UnauthorizedAccessException`, пропускается (`FoldersSkipped`); ошибка чтения самой `Root` пробрасывается.
- `progress` — не чаще 10 раз в секунду и один раз в конце.
- В этой задаче запрос с `Content != null` отвергается `Validate` с `SearchField.Content` (содержимое — задача 18).

- [ ] **Step 1: Тесты**

```csharp
public async Task Finds_by_several_masks()                                   // "*.txt;*.md"
public async Task Regex_name_matches_and_invalid_regex_is_rejected()         // SearchQueryException, Field == Name
public async Task Size_range_is_inclusive_and_leaves_folders_out()
public async Task Date_range_is_inclusive()
public async Task MaxDepth_zero_stays_in_the_root()
public async Task Excluded_folder_is_neither_listed_nor_entered()            // CountingProvider: Enumerate для неё не вызывался
public async Task Hidden_entries_are_skipped_unless_asked()
public async Task Folders_match_by_name_when_no_content_and_no_size()
public async Task Nfd_name_matches_nfc_mask()
public async Task Symlink_loop_ends_and_each_file_is_found_once()            // RF5: /r/a/loop → /r; /r/a/x.txt найден ровно 1 раз
public async Task Unreadable_folder_is_skipped_and_counted()                 // RF5: FailOn("Enumerate", /r/secret) → FoldersSkipped == 1, остальное найдено
public async Task Unreadable_root_throws()
public async Task First_result_arrives_before_the_walk_ends_and_cancel_stops_it()   // RF5: 1000 папок; после первого результата отмена → вызовов Enumerate < 1000
public async Task Hundred_thousand_files_are_searched()                      // RF5: 100 000 файлов в 100 папках, "*.log" → точное число, без предела по времени
public async Task Progress_reports_final_counts()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~FileSearch"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): file search by name and filters`.

---

### Task 18: Поиск по содержимому

**Files:**
- Create: `src/Tandem.Core/Search/ContentMatcher.cs`
- Modify: `src/Tandem.Core/Search/FileSearch.cs`
- Test: `tests/Tandem.Core.Tests/Search/ContentMatcherTests.cs`, `FileSearchTests.cs`

**Interfaces:**
- Consumes: `ContentQuery`, `SearchQueryException`, `FileSearch` (17), `TextEncodings`, `TextEncodingDetector` (7).
- Produces:

```csharp
public sealed class ContentMatcher {
    public const int BlockSize = 64 * 1024;
    public const int MaxLineChars = 1024 * 1024;
    public ContentMatcher(ContentQuery query);                               // SearchQueryException: пустой текст, неверное выражение, неизвестная кодировка
    public Task<bool> IsMatchAsync(Stream content, CancellationToken ct);    // читает блоками, останавливается на первом совпадении
}
```

Правила:
- Кодировка: заданная `EncodingId`; иначе `TextEncodingDetector` по первому блоку, а для `LooksBinary` — windows-1251 (строки ASCII и кириллицы 1251 в двоичных файлах находятся).
- Декодирование — одним `Decoder` на файл: символ, разрезанный границей блока, читается правильно; метка пропускается.
- Обычный текст: окно из хвоста предыдущего блока длиной `Text.Length − 1` символов плюс новый блок; сравнение `Ordinal` либо `OrdinalIgnoreCase`.
- Выражение: по строкам (`\n`, `\r` отбрасывается); строка длиннее `MaxLineChars` проверяется кусками этой длины; `Regex` с `CultureInvariant`, плюс `IgnoreCase` при `CaseSensitive == false`; предел 1 с на строку.
- `FileSearch`: содержимое проверяется только у файлов (`EntryKind.File`), прошедших остальные условия; файл, который не открылся или не дочитался (`IOException`/`UnauthorizedAccessException`), пропускается (`FilesSkipped`). Отмена проверяется на каждом блоке.

- [ ] **Step 1: Тесты**

```csharp
public async Task Finds_text_that_straddles_a_block_boundary()
public async Task Case_insensitive_search_finds_cyrillic()                   // "ПРИВЕТ" в файле UTF-8 "привет"
public async Task Case_sensitive_search_does_not()
public async Task Auto_encoding_finds_russian_text_in_windows1251_file()     // RF4
public async Task Auto_encoding_finds_text_in_utf16_file()
public async Task Explicit_encoding_overrides_detection()                    // текст в cp866 находится только с EncodingId "cp866"
public async Task Multibyte_character_split_between_blocks_is_decoded()
public async Task Ascii_string_inside_a_binary_file_is_found()
public async Task Regex_matches_within_a_line_and_not_across_lines()         // "^ERROR \\d+$"
public async Task Stops_reading_after_the_first_match()                      // SparseStream 1 ГиБ, совпадение в первом блоке: BytesRead ≤ 2 × BlockSize
public void Empty_text_bad_regex_and_unknown_encoding_are_rejected()         // Field: Content, Content, Encoding
public async Task Search_with_content_returns_only_matching_files()          // FileSearch
public async Task File_that_cannot_be_read_is_skipped_and_counted()
public async Task Cancel_inside_a_large_file_stops_the_search()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ContentMatcher|FullyQualifiedName~FileSearch"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): content search`.

---

### Task 19: Источник «результаты поиска»

**Files:**
- Create: `src/Tandem.Core/Search/SearchResultsProvider.cs`
- Test: `tests/Tandem.Core.Tests/Search/SearchResultsProviderTests.cs`

**Interfaces:**
- Consumes: `IHostedProvider`, `HostLocation` (4), `CopyOperation`, `DeleteOperation`.
- Produces:

```csharp
public sealed class SearchResultsProvider(ProviderRegistry providers) : IFileSystemProvider, IHostedProvider {
    public const string ProviderId = "search";
    public const int MaxResultSets = 20;
    /// Новая виртуальная папка "/{n}" (n = 1, 2, …) со списком найденных путей; title — готовый текст для строки пути.
    public VfsPath Add(VfsPath origin, string title, IReadOnlyList<VfsPath> results);
}
```

Решения:
- `EnumerateAsync("/n")` отдаёт **настоящие** записи найденных путей (`FileEntry.Path` — путь в исходном источнике), перечитывая каждую через её источник; путь, которого больше нет или который не читается, из набора выбывает. `EnumerateAsync("/")` — папки наборов. `GetEntryAsync("/")` и `("/n")` — папка; прочее — null.
- `Capabilities = None`. Все изменяющие методы и `OpenReadAsync` бросают `VfsAccessDeniedException`. `IsSameEntryAsync` — равенство путей; `GetVolumeKeyAsync` — `"search"`; `Watch` — null.
- `GetHostLocation("/n")` = `HostLocation(origin, null)`; для `/` — null. `GetDisplayPath("/n")` = `title`, для `/` — `"/"`.
- Наборов не больше `MaxResultSets`: самый старый удаляется. Потокобезопасен.

- [ ] **Step 1: Тесты**

```csharp
public async Task Lists_real_entries_with_their_own_paths()                  // Path.ProviderId == "mem", не "search"
public async Task Vanished_file_drops_out_of_the_next_listing()
public async Task Root_lists_result_sets_and_unknown_set_is_not_found()
public async Task Every_write_is_refused_with_access_denied()
public void Host_location_is_the_searched_folder_and_display_path_is_the_title()
public void Oldest_set_is_dropped_after_twenty()
public async Task Copy_of_listed_entries_copies_the_real_files()             // CopyOperation с путями из перечисления → файлы в /dst
public async Task Delete_of_listed_entries_deletes_the_real_files_and_listing_shrinks()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~SearchResultsProvider"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): search results provider`.

---

### Task 20: Панель с «чужими» записями: операции по источнику цели

Папка может показывать записи другого источника (результаты поиска). Команды должны судить по самим записям, а не по папке панели.

**Files:**
- Modify: `src/Tandem.App/ViewModels/EntryRowViewModel.cs`, `PanelViewModel.cs`, `PanelViewModel.Selection.cs`, `FileCommands.cs`, `ShellCommands.cs`, `DropHandler.cs`, `ContextMenuModel.cs`, `Views/FileListView.axaml`, `Composition.cs`, `MainViewModel.cs`
- Test: `tests/Tandem.App.Tests/ViewModels/ForeignEntriesTests.cs`

**Interfaces:**
- Consumes: `SearchResultsProvider` (19), поведение вложенных источников в панели (5).
- Produces:

```csharp
public sealed partial class EntryRowViewModel {
    public string LocationText { get; }                // "" для обычной записи; для записи из другой папки — DisplayPath её родителя
}
public sealed partial class MainViewModel { public SearchResultsProvider SearchResults { get; } }
// Composition.Build регистрирует SearchResultsProvider в services.Providers, если источника с Id "search" там ещё нет.
```

Решения:
- Запись «чужая», если `entry.Path.Parent != CurrentPath`. В ячейке имени после имени показывается `LocationText` вторичным цветом.
- Курсор и пометки при обновлении списка восстанавливаются по `Entry.Path`, а не по имени (в результатах имена повторяются).
- `FileCommands`: доступность F6, F8, Shift+F8, переименования — по `ProviderCapabilities.Write` источников целей (все цели должны быть записываемыми); корзина — по источнику целей; основа для относительного назначения — папка первой цели. F5 требует только записываемой пассивной панели. F7 по-прежнему судит по папке панели.
- После любой операции дополнительно обновляются панели, в которых есть чужие записи.
- `ShellCommands`: «локальность» для открытия, «Показать в Finder», «Открыть в…», Quick Look, свойств — по источнику записи под курсором; «Открыть терминал» — по папке панели. Перетаскивание наружу — по источнику записей.

- [ ] **Step 1: Тесты** (`TestApp`, набор результатов создаётся через `Main.SearchResults.Add`)

```csharp
public async Task Results_panel_shows_each_entrys_folder_next_to_its_name()
public async Task F5_from_results_copies_real_files_to_the_other_panel()
public async Task F8_from_results_moves_real_files_to_trash_and_the_list_shrinks()   // trashCapable; ConfirmDelete permanent: false
public async Task F6_and_rename_are_available_by_the_entries_provider()
public async Task F7_and_F5_into_results_are_unavailable()
public async Task Enter_on_a_folder_result_goes_to_the_real_folder()
public async Task Up_from_results_returns_to_the_searched_folder()
public async Task Cursor_and_marks_survive_refresh_when_names_repeat()       // два a.txt из разных папок
public async Task Reveal_and_open_work_on_a_result()
public async Task Results_tab_is_saved_as_the_searched_folder()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ForeignEntries"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): panels and commands over entries of another provider`.

---

### Task 21: Окно поиска

**Files:**
- Create: `src/Tandem.App/ViewModels/Search/SearchViewModel.cs`, `Views/Search/SearchWindow.axaml(.cs)`
- Modify: `Services/IWindowService.cs`, `WindowService.cs`, `ViewModels/MainViewModel.cs`, `Commands/CommandIds.cs`, `KeyMap.cs`, `Views/MainMenu.cs` (меню «Файл»), ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/SearchViewModelTests.cs`, `Views/SearchWindowTests.cs`

**Interfaces:**
- Consumes: `FileSearch`, `SearchQuery`, `ContentQuery`, `SearchProgress`, `SearchQueryException` (17, 18), `SearchResultsProvider.Add` (19), `TextEncodings.All` (7), `PanelViewModel.NavigateToAsync`.
- Produces:

```csharp
CommandIds.SearchFiles = "search.files"                                      // Alt+F7, Cmd+F
public interface IWindowService { ... void ShowSearch(SearchViewModel search); }
public sealed record SearchResultRow(FileEntry Entry, string Name, string Folder, string SizeText, string DateText);
public enum SizeUnit { Bytes, Kilobytes, Megabytes, Gigabytes }               // × 1, 1024, 1024², 1024³
public sealed partial class SearchViewModel : ObservableObject, IDisposable {
    public const int MaxShownResults = 100_000;
    public SearchViewModel(ProviderRegistry providers, VfsPath root, Func<FileEntry, Task> goTo,
        Func<IReadOnlyList<VfsPath>, string, Task> showInPanel, Func<TimeSpan, CancellationToken, Task>? flushDelay = null);
    public string RootText { get; }
    public string NameMasks { get; set; }  public bool NameIsRegex { get; set; }
    public bool SearchContent { get; set; }  public string ContentText { get; set; }
    public bool ContentIsRegex { get; set; }  public bool ContentCaseSensitive { get; set; }
    public TextEncodingInfo? ContentEncoding { get; set; }                   // null — «Авто»
    public string MinSizeText { get; set; }  public string MaxSizeText { get; set; }  public SizeUnit SizeUnit { get; set; }
    public DateTime? ModifiedFrom { get; set; }  public DateTime? ModifiedTo { get; set; }   // дни; «по» включает весь день
    public string MaxDepthText { get; set; }  public string ExcludedFolders { get; set; }  public bool IncludeHidden { get; set; }
    public ObservableCollection<SearchResultRow> Results { get; }
    public bool IsRunning { get; }  public string StatusText { get; }  public string? Error { get; }
    public Task StartAsync();  public void Stop();
    public Task GoToAsync(SearchResultRow row);  public Task ShowInPanelAsync();
    public event EventHandler? CloseRequested;
}
```

Решения:
- `search.files` доступна, когда активная панель на локальном источнике (поиск внутри архивов и на серверах — вне объёма спецификации); корень — папка активной панели. Окно не модальное, одно: повторная команда выводит его вперёд с новым корнем.
- `StartAsync`: разобрать поля (нечисловой размер или глубина, «от» больше «до», ошибка `FileSearch.Validate` → `Error`, поиск не начинается), очистить `Results`, обходить в фоне; найденное дописывается пачками не чаще раза в 100 мс; на `MaxShownResults` поиск останавливается и статус говорит об этом. `StatusText` — найдено, просмотрено папок, пропущено папок и файлов; после конца — «готово» либо «остановлено».
- «Перейти к файлу» (Enter или двойной щелчок по строке): активная панель открывает папку записи с курсором на ней, окно закрывается. «Показать в панели»: `showInPanel(все найденные пути, заголовок)` — `MainViewModel` вызывает `SearchResults.Add(root, заголовок, пути)` и открывает полученную папку в активной панели; окно закрывается. Заголовок — локализованная строка `Search_ResultsTitle` с масками и корнем.
- Клавиши окна: Enter в полях — начать; Esc — остановить, а если поиск не идёт — закрыть.

- [ ] **Step 1: Тесты**

```csharp
public async Task Start_streams_results_and_ends_with_done_status()
public async Task Stop_cancels_and_keeps_what_was_found()
public async Task Invalid_regex_shows_error_and_does_not_start()
public async Task Size_unit_and_date_fields_build_the_query()                // "2" + Megabytes → 2 097 152; «по 10.11» включает 10.11 23:59:59
public async Task Bad_number_in_size_or_depth_shows_error()
public async Task Second_start_replaces_previous_results()
public async Task GoTo_opens_the_folder_with_cursor_on_the_file_and_closes()
public async Task ShowInPanel_opens_a_virtual_folder_with_all_results()      // панель: Rows == найденное, DisplayPath == заголовок
public async Task Content_search_with_auto_encoding_finds_1251_file()
public async Task Status_counts_skipped_folders()
public async Task Cmd_F_opens_search_for_the_active_folder()
public async Task Search_is_unavailable_on_a_non_local_panel()
public async Task Esc_stops_running_search_then_closes()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~SearchViewModel|FullyQualifiedName~SearchWindow"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): file search window`.

---

### Task 22: Архив как источник только для чтения

**Files:**
- Create: `src/Tandem.Archives/ArchiveIndex.cs`, `ArchiveProvider.cs`, `ArchiveExceptions.cs`
- Test: `tests/Tandem.Archives.Tests/ArchiveIndexTests.cs`, `ArchiveProviderTests.cs`, `ArchiveProviderLocalTests.cs`, `CountingProvider.cs` (связанный файл из `Tandem.Core.Tests/Vfs`), `TempDir.cs` (связанный файл)

**Interfaces:**
- Consumes: `ArchiveFormat`, `ArchiveFormats.DetectAsync/FromName`, `Fixture` (6), `IHostedProvider`, `TempFileStore`, `ProviderRegistry.DisplayPath` (4), `IBatchProvider`, `IProviderBatch` (3).
- Produces:

```csharp
namespace Tandem.Archives;
public class ArchiveDamagedException(string message, Exception? inner = null) : VfsException(message, inner);
public sealed class ArchivePasswordRequiredException(VfsPath archive, bool wrongPassword) : VfsException(...) {
    public VfsPath Archive { get; }  public bool WrongPassword { get; }
}
internal sealed record RawEntry(string Key, bool IsDirectory, long Size, DateTimeOffset? Modified, string? LinkTarget, bool IsEncrypted);
internal sealed class ArchiveIndex {
    public static ArchiveIndex Build(IEnumerable<RawEntry> entries, DateTimeOffset archiveTime);
    public int RenamedOrDroppedCount { get; }
    // узлы: путь внутри архива ("/docs/readme.txt") → вид, размер, время, ключ записи библиотеки, цель ссылки
}
public sealed class ArchiveProvider : IFileSystemProvider, IHostedProvider, IBatchProvider {
    /// Определяет формат по содержимому (при неудаче — по имени), читает оглавление. Не архив или повреждён → ArchiveDamagedException;
    /// оглавление зашифровано, а пароль не подошёл или не дан → ArchivePasswordRequiredException.
    public static Task<ArchiveProvider> OpenAsync(string id, ProviderRegistry providers, VfsPath host, TempFileStore temp,
        string? password, CancellationToken ct);
    public VfsPath Host { get; }  public ArchiveFormat Format { get; }
    public bool HasEncryptedEntries { get; }
    public string? Password { get; set; }                                    // только в памяти
}
```

Решения:
- Оглавление (`ArchiveIndex.Build`), по порядку записей: `\` → `/`; сегменты пустые, `.` и `..` выбрасываются (запись `../../x` оказывается `/x`, абсолютный путь — от корня архива); символ NUL в имени → `_`; запись, оказавшаяся без имени, отбрасывается; запись, чей предок по пути — файл или ссылка, отбрасывается; повтор имени получает вид `имя (2).расш`; недостающие папки-предки создаются сами со временем архива. Имена zip без признака UTF-8: правильный UTF-8 читается как UTF-8, иначе CP866.
- `FileEntry`: `Mode = null`, `IsHidden` — имя начинается с точки, `Created = Modified`; ссылки tar — `EntryKind.Symlink` с `LinkTarget`.
- Чтение: форматы с произвольным доступом (zip, tar, не сплошные 7z/rar) открывают запись напрямую; последовательные (tar.gz, tar.bz2, tar.xz, сплошные 7z/rar) — один проход до нужной записи. В пакете (`BeginBatchAsync` + `PrepareReadAsync(files)`) последовательный архив за **один** проход распаковывает названные файлы в `TempFileStore`, чтения идут оттуда, конец пакета их освобождает. Поток хозяина без перемотки (архив внутри архива) один раз копируется в `TempFileStore` на каждую версию хозяина.
- Перед каждым обращением сверяются размер и время файла-хозяина: изменился — оглавление перечитывается; исчез — `VfsNotFoundException`.
- В этой задаче `Capabilities = None`; изменяющие методы бросают `VfsAccessDeniedException`; `CommitAsync` ничего не делает. `IsSameEntryAsync` — равенство путей; `GetVolumeKeyAsync` — `"archive"`; `Watch` — null.
- `GetHostLocation("/")` = `HostLocation(Host.Parent, Host.Name)`; `GetDisplayPath(p)` = `providers.DisplayPath(Host)` + (`p.IsRoot ? "" : p.Path`).
- Исключения SharpCompress наружу не выходят: оборачиваются в `VfsException`; неверный или отсутствующий пароль при чтении записи — `ArchivePasswordRequiredException`. Методы потокобезопасны.

- [ ] **Step 1: Тесты** (хозяин — `InMemoryFileSystem` с байтами образца; класс `…LocalTests` — настоящий диск во временной папке)

```csharp
// ArchiveIndexTests
public void Slip_keys_are_confined_to_the_root()                             // RF2: ["../../evil.txt","/abs.txt","a/../../b.txt","dir\\win.txt"] → /evil.txt, /abs.txt, /a/b.txt, /dir/win.txt
public void Entry_below_a_file_or_link_is_dropped()                          // RF2: link (ссылка), затем link/file.txt
public void Missing_parent_folders_are_created()
public void Duplicate_names_are_numbered()
public void Nul_in_a_name_is_replaced()
// ArchiveProviderTests
[Theory] public async Task Lists_folders_and_reads_files(string fixture)      // все читаемые образцы с общим деревом
public async Task Empty_folder_and_unicode_name_are_listed()
public async Task Cp866_names_without_utf8_flag_are_decoded()                 // "привет.txt"
public async Task Copying_slip_archive_out_writes_only_under_the_destination()   // RF2: CopyOperation корня slip.zip в /dst/out; всё новое в AllPaths начинается с /dst/out/
public async Task Copying_trap_tar_out_never_writes_through_the_link()        // RF2: в /dst/out есть ссылка link и нет link/file.txt нигде
public async Task Sequential_archive_copy_reads_the_host_in_one_pass()        // sample.tar.gz, 3 файла: OpenRead(host) ≤ 2 (оглавление + проход)
public async Task Solid_archive_copy_reads_the_host_in_one_pass()
public async Task Changed_host_reloads_the_listing_and_missing_host_is_not_found()
public async Task Truncated_zip_is_reported_as_damaged()                      // broken.zip → ArchiveDamagedException
public async Task Plain_text_file_is_reported_as_damaged()
public async Task Every_write_is_refused()
public void Up_from_root_is_the_archive_file_and_display_path_joins_host_and_inner()
public async Task Twenty_thousand_entries_list_and_enumerate_a_folder()       // zip строится в тесте
// ArchiveProviderLocalTests
public async Task Zip_and_7z_on_real_disk_copy_out_with_content_and_times()   // CopyOperation на настоящий диск
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.Archives.Tests --filter "FullyQualifiedName~ArchiveIndex|FullyQualifiedName~ArchiveProvider"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(archives): read-only archive provider`.

---

### Task 23: Подключение архивов, вложенность, пароли

**Files:**
- Create: `src/Tandem.Archives/ArchiveMounts.cs`, `IArchivePasswordPrompt.cs`
- Modify: `src/Tandem.Archives/ArchiveProvider.cs`
- Test: `tests/Tandem.Archives.Tests/ArchiveMountsTests.cs`, `ScriptedPasswordPrompt.cs` (вспомогательный)

**Interfaces:**
- Consumes: `ArchiveProvider.OpenAsync`, `ArchivePasswordRequiredException`, `ArchiveDamagedException` (22), `IFileMounter`, `ProviderRegistry.Register/TryGet` (4).
- Produces:

```csharp
public interface IArchivePasswordPrompt {
    /// Пароль для архива или null, если пользователь отказался. Может вызываться из фонового потока.
    Task<string?> AskAsync(VfsPath archive, bool previousWasWrong, CancellationToken ct);
}
public sealed class ArchiveMounts : IFileMounter {
    public const int MaxPasswordAttempts = 3;
    public ArchiveMounts(ProviderRegistry providers, TempFileStore temp, IArchivePasswordPrompt prompt);
    public Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct);
    /// Корень архива. Не архив → ArchiveDamagedException; отказ от пароля → OperationCanceledException.
    public Task<VfsPath> MountAsync(VfsPath archive, CancellationToken ct);
    public bool IsArchiveName(string fileName);                              // ArchiveFormats.FromName != null
}
```

Решения:
- Один источник на один путь архива (`Id` = `"archive:" + n`, регистрируется в `ProviderRegistry`, живёт до выхода из приложения); повторное подключение того же пути возвращает тот же корень.
- `TryMountAsync(byContent: false)`: только файлы (`EntryKind.File`) с именем архива; `byContent: true` — любой файл, в котором `DetectAsync` узнал архив (например `.docx`, `.jar`); не узнал — null.
- Пароль при подключении (оглавление зашифровано): спросить, при неверном — снова с `previousWasWrong: true`, не больше `MaxPasswordAttempts`; затем `ArchivePasswordRequiredException`. Отказ — `OperationCanceledException`.
- Пароль при чтении записи (zip с открытым оглавлением): `ArchiveProvider.OpenReadAsync` сам спрашивает через тот же `prompt` по тем же правилам; отказ здесь — `ArchivePasswordRequiredException` (в операции это ошибка элемента: повторить/пропустить/прервать). Верный пароль запоминается в источнике до выхода.
- Вложенность получается сама: хозяин — путь в другом `ArchiveProvider`.

- [ ] **Step 1: Тесты**

```csharp
public async Task Zip_by_name_mounts_and_lists_its_root()
public async Task Same_archive_is_mounted_once()
public async Task Non_archive_name_is_not_mounted_by_name()                   // a.txt, a.docx → null
public async Task Zip_named_docx_mounts_by_content_and_text_file_does_not()
public async Task Damaged_archive_by_name_throws_damaged()
public async Task Encrypted_7z_asks_for_password_at_mount()                   // encrypted.7z, пароль из README
public async Task Encrypted_zip_lists_freely_and_asks_at_first_read()
public async Task Wrong_password_is_asked_again_and_fails_after_three()       // previousWasWrong: false, true, true
public async Task Declined_prompt_cancels_mount_and_fails_a_read_as_item_error()
public async Task Right_password_is_asked_once_per_session()
public async Task Archive_inside_archive_lists_and_reads()                    // zip с sample.zip внутри строится в тесте
public async Task Copy_out_of_encrypted_zip_through_the_engine()              // CopyOperation, один вопрос о пароле
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~ArchiveMounts"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(archives): mounting, nesting and passwords`.

---

### Task 24: Правка zip-архивов

**Files:**
- Create: `src/Tandem.Archives/ArchiveProvider.Zip.cs` (часть `partial`-класса: накопление правок и перезапись)
- Modify: `src/Tandem.Archives/ArchiveProvider.cs`, `tests/Tandem.Core.Tests/Vfs/ProviderContractTests.cs` (пропуск фактов о ссылках и правах, когда источник их не умеет)
- Test: `tests/Tandem.Archives.Tests/ZipEditingTests.cs`, `ZipContractTests.cs` (наследник `ProviderContractTests`, подключённого в `Tandem.Archives.Tests.csproj` связанным файлом), `ZipEditingLocalTests.cs`

**Interfaces:**
- Consumes: `ArchiveProvider`, `ArchiveIndex` (22), `ArchiveMounts` (23), `SafeReplace.PartPathFor/MoveIntoPlaceAsync` (1), `BatchScope`, `IBatchProvider` (3), `TempFileStore` (4).
- Produces: `ArchiveProvider.Capabilities == ProviderCapabilities.Write` для zip без зашифрованных записей, чей хозяин лежит в записываемом источнике; работающие `OpenWriteAsync`, `CreateDirectoryAsync`, `DeleteAsync`, `MoveAsync`, `SetModifiedAsync`, `BeginBatchAsync`/`CommitAsync`.

Решения:
- Правки копятся: новое содержимое файла пишется во временный файл `TempFileStore`; удаления, переименования (файла и папки с поддеревом), создание папок, время — в памяти. Чтения (`GetEntryAsync`, `EnumerateAsync`, `OpenReadAsync`) видят накопленное. Вне пакета каждая правка сразу принимается; в пакете — при `CommitAsync`. Второй пакет ждёт первого.
- Контракт источника соблюдается: `MoveAsync` на существующее имя — `VfsAlreadyExistsException`; удаление непустой папки без `recursive` — `IOException`; запись в отсутствующую папку — `VfsNotFoundException`; `CreateSymlinkAsync` и `SetModeAsync` — `VfsException` («не поддерживается»).
- Принятие (`CommitAsync`), если есть что принимать:
  1. новый архив пишется через источник хозяина в `SafeReplace.PartPathFor(Host)`: неизменённые записи с прежним содержимым и временем, затем накопленные файлы, папки — отдельными записями;
  2. записанный файл открывается заново, и его оглавление сверяется с ожидаемым набором путей; расхождение — `IOException`;
  3. `SafeReplace.MoveIntoPlaceAsync(hostProvider, part, Host, replace: true)`;
  4. временные файлы освобождаются, оглавление перечитывается.
  Сбой на шагах 1–3: временный файл архива удаляется, исходный архив не тронут, накопленное отбрасывается, исключение уходит вызвавшему. Шаги 1–3 выполняются внутри `BatchScope` источника хозяина (zip внутри zip перезаписывает внешний один раз).
- Сжатие — Deflate; записи ≥ 4 ГиБ и архивы с более чем 65 535 записями — в формате Zip64.

- [ ] **Step 1: Тесты**

```csharp
// ZipEditingTests (хозяин — InMemoryFileSystem; CountingProvider считает подмены)
public async Task Added_file_appears_and_untouched_entries_keep_content_and_time()
public async Task Delete_file_and_recursive_delete_of_a_folder()
public async Task Rename_file_and_rename_folder_with_its_subtree()
public async Task Created_folder_survives_reopening_the_archive()
public async Task Overwrite_answer_replaces_an_entry_through_the_engine()      // CopyOperation + Overwrite
public async Task Copying_100_files_in_rewrites_the_archive_once()             // ровно одна подмена хозяина
public async Task Reads_inside_a_batch_see_pending_changes()
public async Task Move_out_of_zip_copies_then_removes_entries()                // MoveOperation zip → mem
public async Task Move_into_zip_keeps_sources_when_the_rewrite_fails()         // RF1: FailWriteAfter(part архива): Failed; все источники на месте; байты архива прежние; нет .tandem-part и .tandem-old
public async Task Failed_swap_leaves_the_original_archive_intact_and_readable()   // RF1: FailOn("Move", part→host): оглавление и содержимое прежние
public async Task Rewrite_that_lost_an_entry_is_rejected_before_the_swap()     // RF1: подмена записанного part усечённым → IOException, оригинал цел
public async Task Cancelled_copy_into_zip_keeps_the_files_already_copied()
public async Task Encrypted_zip_and_other_formats_stay_read_only()
public async Task Zip_inside_zip_edit_rewrites_the_outer_archive_once()
public async Task Editor_style_save_inside_one_batch_rewrites_once()           // BatchScope + SafeReplace.WriteAsync(replace: true)
// ZipContractTests : ProviderContractTests — общий контракт источника
// ZipEditingLocalTests (настоящий диск)
public async Task System_unzip_accepts_the_archive_after_edits()               // /usr/bin/unzip -t → код 0
public async Task Edit_on_real_disk_leaves_no_temp_files_next_to_the_archive()
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Zip"` → FAIL; реализовать; → PASS; `dotnet test tests/Tandem.Core.Tests` → PASS (контракт для прежних источников не ослаблен: пропуск только по отсутствующей возможности).
- [ ] **Step 5:** Commit: `feat(archives): zip editing with rewrite-and-replace`.

---

### Task 25: Упаковка и вид операции «распаковка»

**Files:**
- Create: `src/Tandem.Archives/PackOperation.cs`
- Modify: `src/Tandem.Core/Operations/FileOperation.cs` (`OperationKind`), `CopyOperation.cs`, `OperationRejectedException.cs` (`RejectionReason`), `src/Tandem.App/Resources/Strings.resx`, `Strings.ru.resx` (`OperationKind_Pack`, `OperationKind_Unpack`, `RejectionReason_DestinationExists`, `RejectionReason_FormatNotWritable` — существующий тест `Every_core_enum_member_has_text_in_both_languages` требует текст для каждого значения)
- Test: `tests/Tandem.Archives.Tests/PackOperationTests.cs`, `PackOperationLocalTests.cs`, `tests/Tandem.Core.Tests/Operations/CopyOperationTests.cs`

**Interfaces:**
- Consumes: `ItemErrors`, `SafeReplace.WriteAsync` (1), `ArchiveFormats.Creatable` (6), `ArchiveProvider.OpenAsync` (22, для проверки в тестах), `TempFileStore` (4), защищённые члены `FileOperation` (`SetState`, `SetTotals`, `BeginItem`, `AddBytesDone`, `CompleteFile`, `AddSkipped`, `WaitWhilePausedAsync`).
- Produces:

```csharp
public enum OperationKind { Copy, Move, Delete, Pack, Unpack }
public enum RejectionReason { ..., DestinationExists, FormatNotWritable }     // добавить в конец
public sealed class CopyOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources, VfsPath destinationDirectory,
    CopyOptions options, IOperationInteraction interaction, INativeCopy? nativeCopy = null,
    OperationKind kind = OperationKind.Copy);                                 // допустимы Copy и Unpack, иначе ArgumentException
namespace Tandem.Archives;
public sealed class PackOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources, VfsPath archiveFile,
    ArchiveFormat format, IOperationInteraction interaction, TempFileStore temp) : FileOperation
// Kind = Pack; Destination = archiveFile.Parent; GetVolumeKeysAsync — как у CopyOperation
```

Решения:
- Отказы до начала (`OperationRejectedException`): `FormatNotWritable` — формата нет в `Creatable`; `DestinationExists` — файл архива уже есть; `DestinationInsideSource` — файл архива лежит внутри источника.
- Сканирование — как у копирования: папки рекурсивно, ссылки не раскрываются; `Scanning`, затем `Running` с итогами по файлам и байтам.
- Имена записей — от папки источников: `имя`, `имя/вложенное/файл`; папки (и пустые) — отдельными записями; время изменения сохраняется. Символические ссылки не упаковываются: `SkipReason.SymlinksNotSupported` в сводке.
- Архив пишется через `SafeReplace.WriteAsync(target, archiveFile, replace: false, …)`: до конца — только `.tandem-part`. 7z требует перемотки: собирается во временном файле `TempFileStore`, затем копируется во временное имя у цели.
- Чтение блоками по 1 МиБ с проверкой паузы и отмены между блоками. Файл, который не удалось открыть, — вопрос об ошибке (повторить/пропустить/пропустить все/прервать). Ошибка чтения посреди уже начатой записи прерывает операцию (`Failed`), временный файл удаляется.
- Отмена и сбой не оставляют ни архива под настоящим именем, ни временных файлов.

- [ ] **Step 1: Тесты**

```csharp
[Theory] public async Task Packed_tree_reads_back_through_the_archive_provider(ArchiveFormat format)   // все Creatable: дерево, содержимое, времена
public async Task Empty_folder_is_stored()
public async Task Symlink_is_skipped_and_listed_in_the_summary()
public async Task Existing_archive_file_is_rejected()                         // DestinationExists, ничего не записано
public async Task Archive_inside_its_own_source_is_rejected()
public async Task Format_that_cannot_be_created_is_rejected()                 // Rar
public async Task Cancel_leaves_no_archive_and_no_part_file()
public async Task Failed_write_leaves_no_archive_and_no_part_file()           // FailWriteAfter
public async Task File_that_cannot_be_opened_can_be_skipped()                 // FailOn("OpenRead") + Skip → в архиве остальное
public async Task Totals_equal_scanned_files_and_bytes()
public void Copy_operation_accepts_Unpack_kind_and_rejects_others()
// PackOperationLocalTests
public async Task System_tools_accept_packed_zip_and_tar_gz()                 // unzip -t; tar -tzf → код 0
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.Archives.Tests --filter "FullyQualifiedName~PackOperation"` и `dotnet test tests/Tandem.Core.Tests --filter "FullyQualifiedName~CopyOperation"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(archives): pack operation`.

---

### Task 26: Архивы в приложении

**Files:**
- Create: `src/Tandem.App/ViewModels/ArchiveCommands.cs`, `Services/DialogArchivePasswordPrompt.cs`, `ViewModels/Dialogs/ArchiveDialogViewModels.cs`, `Views/Dialogs/ArchiveDialogs.cs`
- Modify: `src/Tandem.App/Tandem.App.csproj` (ссылка на `Tandem.Archives`), `Composition.cs`, `ViewModels/MainViewModel.cs`, `FileCommands.cs`, `ContextMenuModel.cs`, `Services/IDialogService.cs`, `DialogService.cs`, `OperationTexts.cs`, `Commands/CommandIds.cs`, `KeyMap.cs`, `Views/MainMenu.cs`, ресурсы
- Test: `tests/Tandem.App.Tests/ViewModels/ArchiveCommandsTests.cs`, `Views/ArchiveWindowTests.cs`, `Services/OperationTextsTests.cs`, `Views/ScriptedDialogs.cs`, `tests/Tandem.App.Tests/Tandem.App.Tests.csproj` (образцы архивов — связанные файлы из `Tandem.Archives.Tests/Fixtures`)

**Interfaces:**
- Consumes: `ArchiveMounts`, `IArchivePasswordPrompt` (23), `PackOperation`, `OperationKind.Pack/Unpack`, `RejectionReason.DestinationExists/FormatNotWritable` (25), `ArchiveFormats` (6), `ArchivePasswordRequiredException`, `ArchiveDamagedException` (22), `DestinationParser.ParseAsync`, `ProviderRegistry.AddMounter` (4).
- Produces:

```csharp
CommandIds.ArchivePack = "archive.pack"                                       // Alt+F5, Cmd+Alt+5
CommandIds.ArchiveUnpack = "archive.unpack"                                   // Alt+F9, Cmd+Alt+9
public sealed record PackRequest(int ItemCount, string? SingleName, string Destination, IReadOnlyList<ArchiveFormat> Formats, TextValidator? Validate = null);
public sealed record PackAnswer(string Destination, ArchiveFormat Format, bool RunImmediately);
public sealed record UnpackRequest(int ArchiveCount, string? SingleName, string Destination, bool SeparateFolders, TextValidator? Validate = null);
public sealed record UnpackAnswer(string Destination, bool SeparateFolders, bool RunImmediately);
public interface IDialogService { ...
    Task<PackAnswer?> AskPackAsync(PackRequest request);
    Task<UnpackAnswer?> AskUnpackAsync(UnpackRequest request);
    Task<string?> AskPasswordAsync(string archiveName, bool previousWasWrong, CancellationToken ct);
}
public sealed class FileCommands { public void StartOperation(FileOperation operation, bool runImmediately, IReadOnlyList<VfsPath>? alsoChanged = null); }  // прежний закрытый Start с тем же завершением
public sealed class ArchiveCommands : IDisposable {                            // MainViewModel.Archives
    public ArchiveCommands(WorkspaceViewModel workspace, AppServices services, ArchiveMounts mounts, FileCommands files,
        IOperationInteraction interaction, CommandRegistry commands, Action<Exception> reportFailure);
}
public sealed partial class MainViewModel { public ArchiveMounts ArchiveMounts { get; } }
// Composition.Build создаёт ArchiveMounts(services.Providers, services.Temp, new DialogArchivePasswordPrompt(services.Dialogs)) и вызывает AddMounter
```

Решения:
- Enter на файле с именем архива входит в него (задача 5 + `AddMounter`); `nav.enter` — в любой файл, который по содержимому архив. Backspace в корне архива возвращает к его файлу.
- Внутри архива всё делает существующий код: F5 наружу и в zip, F6, F8 (корзины нет — диалог предупреждает о безвозвратном удалении), F7, переименование, F3, F4 с сохранением в zip. В архивах только для чтения F6, F7, F8, переименование и F5 внутрь недоступны по `ProviderCapabilities.Write`.
- `archive.pack`: цели активной панели; предложение — папка пассивной панели + (имя единственной цели без расширения | имя текущей папки) + `.zip`; смена формата в диалоге меняет расширение (`ArchiveFormats.DefaultExtension`); форматы — `ArchiveFormats.Creatable`; текст назначения разбирается как у копирования (относительный — от папки активной панели); существующий файл — ошибка в диалоге. Источник назначения должен уметь запись. Запуск — `PackOperation` через `files.StartOperation`.
- `archive.unpack`: архивы среди целей активной панели (по имени; единственный файл под курсором — и по содержимому). Назначение — папка пассивной панели; «каждый архив в свою папку» включено по умолчанию, когда архивов больше одного. Для каждого архива: `mounts.MountAsync`, затем `CopyOperation(..., kind: OperationKind.Unpack)` всех записей корня в назначение или в `назначение/<имя архива без расширения>` (папка создаётся). Недоступна, когда среди целей нет архива.
- `DialogArchivePasswordPrompt` переводит вопрос в поток интерфейса; поле пароля скрывает ввод; после неверного пароля диалог говорит об этом.
- `OperationTexts`: `ArchivePasswordRequiredException` → вид `PasswordRequired`, `ArchiveDamagedException` → `ArchiveDamaged` (ключи `OperationError_*` и `OperationFailed_*`).
- Меню «Файл» и контекстное меню строки: «Упаковать…», «Распаковать…».

- [ ] **Step 1: Тесты** (`TestApp`: «локальный диск» в памяти с образцами архивов; подключение архивов настоящее)

```csharp
public async Task Enter_on_zip_opens_it_and_Backspace_returns_to_the_file()
public async Task F5_copies_a_file_out_of_7z()
public async Task F5_into_zip_adds_files_and_they_are_listed()
public async Task F8_in_zip_warns_permanent_and_deletes_the_entry()            // ConfirmDelete permanent: true
public async Task F7_and_rename_work_inside_zip()
public async Task Write_commands_are_unavailable_inside_7z()
public async Task F3_shows_a_file_from_inside_an_archive()
public async Task F4_inside_zip_saves_the_change_into_the_archive()
public async Task Alt_F5_packs_marked_items_into_the_passive_folder()          // sample.zip создан, подключается, содержит цели
public async Task Pack_dialog_offers_only_creatable_formats_and_rejects_an_existing_name()
public async Task Alt_F9_unpacks_the_archive_under_cursor()
public async Task Two_archives_unpack_into_separate_folders()
public async Task Encrypted_archive_asks_for_password_and_declining_leaves_the_panel_in_place()
public async Task Wrong_password_dialog_says_so_on_the_second_ask()
public async Task Damaged_archive_shows_a_message()
public async Task Archive_tab_is_restored_as_the_archives_folder()
public void Archive_errors_have_their_own_words()                              // ForError/ForFailure для ArchivePasswordRequiredException и ArchiveDamagedException
```

- [ ] **Step 2–4:** Run `--filter "FullyQualifiedName~Archive|FullyQualifiedName~OperationTexts"` → FAIL; реализовать; → PASS; `dotnet test Tandem.slnx` → PASS.
- [ ] **Step 5:** Commit: `feat(app): archives as folders, pack and unpack`.

---

### Task 27: Сборка, приёмка этапа, README

**Files:**
- Create: `docs/checklists/stage-2.md`
- Modify: `build/Info.plist` (`CFBundleShortVersionString` = `0.2.0`), `build/verify-app.sh` (наличие `Tandem.Archives.dll`, `SharpCompress.dll`, `AvaloniaEdit.dll` в `Contents/MacOS`), `README.md`

**Interfaces:**
- Consumes: всё построенное в задачах 1–26.
- Produces: `dist/Tandem.app` версии 0.2.0; список ручной приёмки.

- [ ] **Step 1:** Дополнить `build/verify-app.sh` проверками трёх библиотек. Run `build/verify-app.sh` на старом пакете → FAIL (библиотек нет).
- [ ] **Step 2:** Поправить `Info.plist`. Run `build/make-app.sh && build/verify-app.sh` → `RESULT: PASS`. Run `open dist/Tandem.app` → окно с двумя панелями и панелью кнопок.
- [ ] **Step 3:** Написать `docs/checklists/stage-2.md` на русском, в той же форме, что `docs/checklists/stage-1.md` (действие → ожидаемый результат, флажки). Разделы:
  - «Как запустить» — сборка, проверка, где лежат `state.json` и новый `settings.json`, как сбросить настройки, что настройки этапа 1 сохраняются.
  - «Поиск» — по нескольким маскам; по выражению; по содержимому (UTF-8, Windows-1251, выбор кодировки, регистр, выражение); размер, дата, глубина, исключённые папки; результаты появляются по ходу; «Стоп»; переход к файлу; «Показать в панели» и F3/F5/F6/F8 оттуда; поиск по домашней папке не подвешивает окно.
  - «Просмотр (F3)» — текст в трёх кодировках и ручной выбор; hex; изображения; **файл на несколько ГБ** (команды `mkfile` и `yes … | head -c` из задачи 10) — открывается сразу, конец файла, память в «Мониторинге системы» не растёт; файл из одной строки; Cmd+Y по-прежнему Quick Look.
  - «Быстрый просмотр (Ctrl+Q)» и «Правка (F4)» — правка и сохранение, сохранение кодировки, отказ на большом и двоичном файле, вопрос при закрытии, внешний редактор из настроек.
  - «Архивы» — **вход в zip и 7z и копирование из них**; tar, tar.gz, tar.bz2, tar.xz, rar; **создание zip**, tar, tar.gz, 7z (Alt+F5); распаковка (Alt+F9); F5 в zip, удаление, переименование, новая папка, перенос из zip, F4 внутри zip; архив с паролем (zip и 7z); архив внутри архива; zip с русскими именами, созданный в Windows; архив открывается после правки в «Утилите архивирования»; прервать добавление большого файла в zip — исходный архив цел.
  - «Настройки» — **переназначение клавиш** (назначить, занятое сочетание, сброс одной и всех, работает без перезапуска и после него); **темы** (светлая, тёмная, системная); цвета по типам; столбцы отдельно для левой и правой панели; шрифт и высота строк; панель кнопок с подстановками; переключатели меню «Вид».
  - «Клавиатура» — «Проверить первым»: Alt+F5, Alt+F7, Alt+F9, Ctrl+Q, Cmd+F, Cmd+, и Cmd+Down доходят до приложения на настоящей клавиатуре (в том числе на русской раскладке) и не перехвачены системой.
  - «Оба языка» и «Перезапуск» (вкладка внутри архива и результаты поиска восстанавливаются как обычная папка).
  - «Известные ограничения этапа 2»: редактор — файлы до 32 МБ; изменять можно только zip без пароля; при добавлении в zip нужно свободное место под добавляемые файлы во временной папке и под новый архив; созданные архивы не хранят права доступа и символические ссылки; сохранение в редакторе не переносит теги Finder и расширенные атрибуты файла; поиск не заходит в архивы и по ссылкам на папки; переименованный или перенесённый файл выбывает из результатов поиска; клавиши окна просмотра и стрелки в панели не переназначаются; цвета по типам — из восьми готовых; ширина столбцов задаётся в настройках, не мышью; путь внутрь архива нельзя ввести в строке пути; перетаскивание наружу из архива недоступно; смена языка — после перезапуска; всё оставшееся из ограничений этапа 1, что ещё верно.
- [ ] **Step 4:** Обновить `README.md`: состояние (этап 2 на приёмке), возможности этапа 2, `src/Tandem.Archives` в структуре, ссылки на этот план и `docs/checklists/stage-2.md`, строка про `settings.json`; английский раздел — то же кратко.
- [ ] **Step 5:** Run `dotnet build Tandem.slnx` → 0 предупреждений; `dotnet test Tandem.slnx` → все тесты PASS. Commit: `build: stage 2 bundle, checklist and README`.

---

## Самопроверка плана

- **Покрытие спецификации.** Группа C — задачи 17 (имя, фильтры), 18 (содержимое), 19–20 (виртуальная папка и операции над ней), 21 (окно, переход, «в панель»). Группа D — 7–8 (кодировки, любой размер), 10 (F3: текст, hex, изображения), 11 (быстрый просмотр), 9 и 15 (F4: встроенный и внешний редактор). Группа E — 6 (форматы), 22 (чтение, вход как в папку), 23 (пароли, вложенность), 24 (правка zip), 25 (упаковка), 26 (Alt+F5, Alt+F9, F5 из архива и в zip, сохранение после F4). Группа H — 12–13 (хранение, клавиши), 14 (темы, цвета, шрифт, столбцы), 15 (панель кнопок), 16 (окно, редактор сочетаний, сброс); переключатели вида сохранены и дополнены. Раздел 5.2 (вложенные источники, быстрые пути) — 3–5, 22–23; 5.3 (упаковка и распаковка в движке) — 25; 5.5 — 12; 7.1 (панель кнопок сверху) — 15; 7.2 — таблица сочетаний выше; раздел 8, этап 2: поиск с выводом в панель — 21, файл на несколько ГБ — 8, 10 и приёмка, вход в zip/7z и копирование — 22, 26, создание zip — 25, 26, переназначение клавиш и темы — 13, 14, 16; раздел 9 — тесты в каждой задаче и ручной список в 27.
- **Review Focus.** 1 — задачи 3 и 24; 2 — задача 22; 3 — задачи 8 и 10; 4 — задачи 7, 10, 18; 5 — задача 17.
- **Согласованность имён.** `SafeReplace`, `ItemErrors` (1) → 9, 24, 25; `BatchScope` (3) → 9, 24; `IHostedProvider`, `IFileMounter`, `TempFileStore` (4) → 5, 10, 19, 22, 23; `TextEncodings`/`TextEncodingDetector` (7) → 8–10, 18, 21; `IWindowService` (9) пополняется в 10, 16, 21; `SettingsService` (13) → 14–16; `AppServices` получает `Temp` (5) и `Windows` (9); конструктор `MainViewModel` меняется один раз (13).
- **Осознанно вне объёма:** поиск внутри архивов и на серверах, создание RAR (не-цели спецификации); поиск текста в окне просмотра, подсветка синтаксиса в редакторе, изменение ширины столбцов мышью, произвольные цвета, правка архивов с паролем — спецификация их не требует.
