# Tandem, этап 1 (ядро, навигация, интеграция с macOS) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Собрать двухпанельный файловый менеджер для macOS (arm64), которым можно день работать вместо Finder: навигация, копирование, перенос, удаление в корзину, вкладки, перетаскивание, Quick Look.

**Architecture:** Панели и движок операций работают только с интерфейсом `IFileSystemProvider`; локальный диск — одна из реализаций. Движок операций один, живёт в `Tandem.Core` без интерфейса и без вызовов macOS. Всё системное (корзина, Finder, тома, теги) — в `Tandem.Platform.Mac` за интерфейсами из Core. Интерфейс — Avalonia, MVVM.

**Tech Stack:** C#, .NET 11 (`net11.0`), Avalonia 12.1.x, CommunityToolkit.Mvvm 8.4.x, xUnit, Avalonia.Headless.XUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md` — исполнитель читает её вместе с планом. Этот план покрывает группы A, B, I и разделы 5.1–5.6, 7.

**Модели:** план написан Fable 5.2. Реализацию и проверку каждой задачи выполняют подагенты на Opus 5.5 (`model: "opus"`).

## Global Constraints

- `TargetFramework` = `net11.0`; сборка только `osx-arm64`; `LSMinimumSystemVersion` = `14.0`.
- Avalonia: последняя стабильная 12.1.x каждого пакета. Версии пакетов — в `Directory.Packages.props` (central package management).
- `Nullable` включён, `TreatWarningsAsErrors` = `true`.
- `Tandem.Core` не ссылается на Avalonia и не вызывает API macOS. Платформа — только через интерфейсы Core.
- Все обращения локального источника к диску идут через `IFileAccessBroker`.
- Никакой молчаливой перезаписи. Запись файла — во временное имя `.{имя}.tandem-part` рядом с целью, переименование по завершении. Прерванная операция не оставляет файла под настоящим именем.
- Перенос между томами: копирование → проверка (нет ошибки и размер цели = размеру источника) → удаление источника только для успешно скопированных элементов.
- Файловые операции и чтение папок не выполняются в потоке интерфейса.
- Строки интерфейса — только в ресурсах (`Strings.resx` английский, `Strings.ru.resx` русский), не в коде и не в разметке.
- Название, иконки и оформление Total Commander не используются. Рабочее название — Tandem.
- Настройки — JSON в `~/Library/Application Support/Tandem/`. Паролей в этапе 1 нет.
- Код, не являющийся разметкой, пишется через тест: сначала падающий тест, затем реализация.
- API Avalonia 12 сверять с документацией (Context7 / docs.avaloniaui.net), а не по памяти: в 12-й версии менялись в том числе перетаскивание и буфер обмена.
- Коммит после каждой задачи; сообщение заканчивается строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Копирование/перенос папки в саму себя или в свою подпапку** — отказ до первой записи, с понятным сообщением (тест в задаче 6).
2. **Цель исчезла или диск заполнился посреди файла** (выдернули флешку) — ошибка показана, временный файл удалён, источник цел, при переносе источник не удалён (тест в задаче 8).
3. **Имена с Unicode**: разложенная форма (NFD, так хранит macOS) против составной (NFC, так печатает клавиатура), эмодзи, имена с переводом строки — список, сортировка и быстрый поиск не падают; набранное «й» находит файл, записанный в NFD (тест в задаче 17).
4. **Показанная папка удалена или том отключён извне** — панель переходит к ближайшему существующему предку, а не зацикливается на ошибке; то же при восстановлении вкладки с несуществующим путём (тесты в задачах 16 и 18).
5. **Повреждённый или несовместимый файл настроек** — запуск с умолчаниями, испорченный файл сохранён как `.bad`, падения нет (тест в задаче 14).

## Структура файлов

```
Tandem.slnx
Directory.Build.props            общие свойства сборки
Directory.Packages.props         версии пакетов
.gitignore
build/make-app.sh                сборка Tandem.app
build/create-dev-cert.sh         разовое создание локального сертификата
build/Info.plist
src/Tandem.Core/
  Vfs/        VfsPath, FileEntry, IFileSystemProvider, ProviderRegistry, VfsExceptions,
              Local/LocalFileSystemProvider, Local/IFileAccessBroker, InMemory/InMemoryFileSystem
  Listing/    EntrySorter, SortSpec, FileMask
  Operations/ FileOperation, CopyOperation, MoveOperation, DeleteOperation, OperationQueue,
              IOperationInteraction, OperationProgress, CopyEngine (внутренний)
  Platform/   ITrash, INativeCopy, IShellIntegration, IQuickLook, IVolumeService,
              IFileMetadataService, IProcessRunner
  Settings/   AppState, JsonStateStore
src/Tandem.Platform.Mac/
  Interop/ObjC.cs, Interop/LibC.cs
  MacTrash, MacNativeCopy, MacShellIntegration, MacQuickLook, MacVolumeService,
  MacFileMetadataService, ProcessRunner
src/Tandem.App/
  Program.cs, App.axaml(.cs), Composition.cs      (сборка зависимостей)
  Resources/Strings.resx, Strings.ru.resx, Loc.cs
  ViewModels/ PanelViewModel, EntryRowViewModel, PanelTabsViewModel, NavigationHistory,
              FavoritesViewModel, WorkspaceViewModel, CommandLineViewModel,
              OperationsViewModel, PropertiesViewModel
  Commands/   CommandIds, KeyChord, KeyMap, CommandRegistry
  Services/   IDialogService, DialogService, DialogOperationInteraction, SizeFormatter
  Views/      MainWindow, PanelView, FileListView, SidebarView, FKeyBar, CommandLineView,
              Dialogs/*  (CopyMove, NewFolder, ConfirmDelete, Conflict, Error, Progress,
              Summary, Mask, Properties, OutputPane)
tests/Tandem.Core.Tests/
tests/Tandem.Platform.Mac.Tests/     (интеграционные, только macOS)
tests/Tandem.App.Tests/              (Avalonia.Headless.XUnit)
docs/checklists/stage-1.md
```

`Tandem.Archives` и `Tandem.Net` из спецификации создаются в этапах 2 и 3, не сейчас.

Соглашение для моделей представления: создаются и вызываются в потоке интерфейса; тяжёлую работу сами уводят в фон (`Task.Run`), продолжения возвращаются в захваченный контекст. Тесты моделей — `[AvaloniaFact]`.

---

### Task 1: Каркас решения и проверка совместимости

**Files:**
- Create: `Tandem.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, пять проектов из структуры выше с минимальным содержимым
- Test: `tests/Tandem.App.Tests/SmokeTests.cs`, `tests/Tandem.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: проекты и ссылки: `App → Core, Platform.Mac`; `Platform.Mac → Core`; `Core.Tests → Core`; `Platform.Mac.Tests → Platform.Mac`; `App.Tests → App`. В `Tandem.App` — пустое `MainWindow` с заголовком `Tandem`.

- [ ] **Step 1:** Создать решение и проекты (`dotnet new sln`, `classlib`, `xunit`; для App — шаблон `avalonia.app` из `Avalonia.Templates`, установить `dotnet new install Avalonia.Templates`). `.gitignore`: `bin/`, `obj/`, `publish/`, `dist/`, `.DS_Store`, `*.user`, `.superpowers/`.
- [ ] **Step 2:** Написать тесты:

```csharp
// Core.Tests
[Fact] public void Core_targets_net11() =>
    Assert.StartsWith(".NETCoreApp,Version=v11", typeof(Tandem.Core.AssemblyMarker).Assembly
        .GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
// App.Tests
[AvaloniaFact] public void MainWindow_opens() {
    var w = new MainWindow(); w.Show();
    Assert.Equal("Tandem", w.Title);
}
```

- [ ] **Step 3:** Run: `dotnet build && dotnet test`. Expected: сборка без предупреждений, 2 теста PASS.
- [ ] **Step 4:** Run: `dotnet run --project src/Tandem.App`. Expected: открывается пустое окно. **Если Avalonia 12.1.x не собирается или не запускается под `net11.0` — остановиться и доложить, не понижая версию .NET и не меняя фреймворк интерфейса.**
- [ ] **Step 5:** Commit: `chore: scaffold solution`.

---

### Task 2: Путь и запись о файле

**Files:**
- Create: `src/Tandem.Core/Vfs/VfsPath.cs`, `src/Tandem.Core/Vfs/FileEntry.cs`
- Test: `tests/Tandem.Core.Tests/Vfs/VfsPathTests.cs`, `FileEntryTests.cs`

**Interfaces:**
- Produces:

```csharp
public readonly record struct VfsPath(string ProviderId, string Path) {
    public static VfsPath Local(string path);      // ProviderId = "local"
    public string Name { get; }                     // "" для корня
    public bool IsRoot { get; }
    public VfsPath? Parent { get; }                 // null для корня
    public VfsPath Combine(string name);
    public bool IsSameOrDescendantOf(VfsPath other);
}
public enum EntryKind { File, Directory, Symlink }
public sealed record FileEntry(VfsPath Path, EntryKind Kind, long Size,
    DateTimeOffset Modified, DateTimeOffset Created, UnixFileMode? Mode,
    bool IsHidden, string? LinkTarget = null, bool LinkPointsToDirectory = false) {
    public string Name { get; }          // Path.Name
    public string Extension { get; }     // без точки; "" для папок и имён вида ".gitignore"
    public bool IsDirectoryLike { get; } // Directory или Symlink на папку
}
```

Путь нормализуется в конструкторе: разделитель `/`, без завершающего `/` (кроме корня `/`), повторные `/` схлопываются.

- [ ] **Step 1: Тесты**

```csharp
[Theory]
[InlineData("/Users/me/", "/Users/me")] [InlineData("//a///b", "/a/b")] [InlineData("/", "/")]
public void Normalizes(string input, string expected) => Assert.Equal(expected, VfsPath.Local(input).Path);
[Fact] public void Parent_and_name() {
    var p = VfsPath.Local("/a/b.txt");
    Assert.Equal("b.txt", p.Name); Assert.Equal(VfsPath.Local("/a"), p.Parent);
    Assert.Null(VfsPath.Local("/").Parent); Assert.True(VfsPath.Local("/").IsRoot);
}
[Fact] public void Descendant_check_respects_segment_boundaries() {
    Assert.True(VfsPath.Local("/a/b/c").IsSameOrDescendantOf(VfsPath.Local("/a/b")));
    Assert.False(VfsPath.Local("/a/bc").IsSameOrDescendantOf(VfsPath.Local("/a/b")));
    Assert.False(new VfsPath("mem", "/a/b").IsSameOrDescendantOf(VfsPath.Local("/a")));
}
[Theory]
[InlineData("report.pdf", "pdf")] [InlineData(".gitignore", "")] [InlineData("a.tar.gz", "gz")] [InlineData("noext", "")]
public void Extension(string name, string ext) => Assert.Equal(ext, File(name).Extension);
```

- [ ] **Step 2:** Run `dotnet test tests/Tandem.Core.Tests --filter Vfs` → FAIL (типов нет).
- [ ] **Step 3:** Реализовать по сигнатурам выше.
- [ ] **Step 4:** Run тот же фильтр → PASS.
- [ ] **Step 5:** Commit: `feat(core): VfsPath and FileEntry`.

---

### Task 3: Интерфейс источника и источник «в памяти»

**Files:**
- Create: `src/Tandem.Core/Vfs/IFileSystemProvider.cs`, `ProviderRegistry.cs`, `VfsExceptions.cs`, `InMemory/InMemoryFileSystem.cs`
- Test: `tests/Tandem.Core.Tests/Vfs/ProviderContractTests.cs` (абстрактный), `InMemoryContractTests.cs`

**Interfaces:**
- Consumes: `VfsPath`, `FileEntry`.
- Produces:

```csharp
[Flags] public enum ProviderCapabilities { None = 0, Write = 1, Trash = 2, UnixPermissions = 4, Symlinks = 8, Watch = 16 }
public interface IFileSystemProvider {
    string Id { get; }
    ProviderCapabilities Capabilities { get; }
    IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct); // не заходит в ссылки
    Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct);                  // null, если нет
    Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct);
    Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct);                      // создаёт или обрезает
    Task CreateDirectoryAsync(VfsPath path, CancellationToken ct);                        // существующая папка — не ошибка
    Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct);
    Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct);            // цель существует → VfsAlreadyExistsException
    Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct);
    Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct);
    Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct);
    Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct);                   // одинаков для путей одного тома
    IDisposable? Watch(VfsPath directory, Action changed);                                // null, если не поддерживается
}
public class VfsException : IOException;  // + VfsNotFoundException, VfsAccessDeniedException, VfsAlreadyExistsException
public sealed class ProviderRegistry { void Register(IFileSystemProvider p); IFileSystemProvider Get(string id); IFileSystemProvider For(VfsPath path); }

public sealed class InMemoryFileSystem : IFileSystemProvider {   // Id по умолчанию "mem", все возможности кроме Trash
    public InMemoryFileSystem(string id = "mem");
    public void AddFile(string path, string content = "", DateTimeOffset? modified = null); // создаёт родителей
    public void AddFile(string path, byte[] content, DateTimeOffset? modified = null);
    public void AddDirectory(string path);
    public bool Exists(string path); public string ReadAllText(string path); public IReadOnlyList<string> AllPaths { get; }
    public void SetVolumeKey(string pathPrefix, string key);                      // по умолчанию один том "mem"
    public void FailOn(Func<string, VfsPath, bool> when, Func<Exception> error);  // имя операции = имя метода без Async
    public void FailWriteAfter(string path, long bytes, Func<Exception> error);   // запись в path падает после bytes байт
    public void RaiseChanged(string directory);
}
```

- [ ] **Step 1: Контрактные тесты** — абстрактный класс с `protected abstract Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()`, наследник для `InMemoryFileSystem`:

```csharp
[Fact] public async Task Enumerate_lists_direct_children_only()      // файл, папка, файл в подпапке → 2 записи
[Fact] public async Task Write_then_read_roundtrips_bytes()
[Fact] public async Task GetEntry_returns_null_for_missing()
[Fact] public async Task Enumerate_missing_throws_NotFound()
[Fact] public async Task Move_onto_existing_throws_AlreadyExists()
[Fact] public async Task Delete_nonempty_without_recursive_throws()   // IOException
[Fact] public async Task Delete_recursive_removes_tree()
[Fact] public async Task CreateDirectory_twice_is_ok()
[Fact] public async Task Symlink_is_reported_as_symlink_and_not_traversed()  // Kind == Symlink, LinkTarget задан
[Fact] public async Task SetModified_is_visible_in_GetEntry()
```

И отдельно для памяти: `FailWriteAfter_throws_after_given_bytes`, `FailOn_throws_for_matching_operation`.

- [ ] **Step 2:** Run `--filter InMemoryContractTests` → FAIL.
- [ ] **Step 3:** Реализовать интерфейс, исключения, реестр, `InMemoryFileSystem`.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit: `feat(core): provider interface and in-memory provider`.

---

### Task 4: Локальный источник и посредник доступа

**Files:**
- Create: `src/Tandem.Core/Vfs/Local/LocalFileSystemProvider.cs`, `Local/IFileAccessBroker.cs`, `Local/UnrestrictedAccessBroker.cs`
- Test: `tests/Tandem.Core.Tests/Vfs/LocalContractTests.cs`, `LocalProviderTests.cs`, `TempDir.cs` (вспомогательный)

**Interfaces:**
- Consumes: `IFileSystemProvider`, `ProviderContractTests`.
- Produces:

```csharp
public interface IFileAccessBroker {
    IDisposable Access(string path);                                  // нет доступа → VfsAccessDeniedException
    Task<bool> RequestAccessAsync(string path, CancellationToken ct); // в обычной сборке всегда true
}
public sealed class UnrestrictedAccessBroker : IFileAccessBroker;
public sealed class LocalFileSystemProvider(IFileAccessBroker broker) : IFileSystemProvider; // Id = "local"
```

Решения: `Capabilities` = `Write | Trash | UnixPermissions | Symlinks | Watch`. `IsHidden` = `FileAttributes.Hidden`. `UnauthorizedAccessException` → `VfsAccessDeniedException`, `FileNotFoundException`/`DirectoryNotFoundException` → `VfsNotFoundException`. `GetVolumeKeyAsync` — точка монтирования самого длинного совпадающего `DriveInfo`. `Watch` — `FileSystemWatcher` на одну папку без подпапок, вызовы `changed` склеиваются с задержкой 300 мс. Каждое обращение к диску обёрнуто в `using broker.Access(path)`.

- [ ] **Step 1: Тесты** — `LocalContractTests : ProviderContractTests` на временной папке, плюс:

```csharp
[Fact] public async Task Dotfile_is_hidden()
[Fact] public async Task Mode_roundtrips() // SetModeAsync(0o640) → GetEntry.Mode
[Fact] public async Task Unreadable_directory_throws_AccessDenied() // chmod 000, затем вернуть права в finally
[Fact] public async Task Watch_fires_once_for_burst() // создать 20 файлов подряд → changed вызван ≥1 и ≤3 раз за 1,5 с
[Fact] public async Task Every_call_goes_through_broker() // брокер-счётчик: Enumerate, OpenRead, Delete увеличивают счёт
[Fact] public async Task Broker_denial_surfaces_as_AccessDenied()
```

- [ ] **Step 2:** Run `--filter Local` → FAIL.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit: `feat(core): local provider behind access broker`.

---

### Task 5: Сортировка и маски

**Files:**
- Create: `src/Tandem.Core/Listing/SortSpec.cs`, `EntrySorter.cs`, `FileMask.cs`
- Test: `tests/Tandem.Core.Tests/Listing/EntrySorterTests.cs`, `FileMaskTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum SortColumn { Name, Extension, Size, Modified }
public readonly record struct SortSpec(SortColumn Column, bool Descending = false);
public static class EntrySorter { public static IComparer<FileEntry> Create(SortSpec spec); }
public sealed class FileMask { public FileMask(string pattern); public bool IsMatch(string name); }
```

Решения: папки (и ссылки на папки) всегда выше файлов при любом направлении. Имена сравниваются без учёта регистра, с числовым порядком (`file2` < `file10`), после нормализации в форму C. При равенстве ключа — по имени по возрастанию. Маска: части через `;`, `*` и `?`, без учёта регистра, сравнение после нормализации в форму C; пустая строка = `*`.

- [ ] **Step 1: Тесты**

```csharp
[Fact] public void Directories_first_even_when_descending()
[Fact] public void Natural_numeric_order() // file1, file2, file10
[Fact] public void Case_insensitive() // "b", "A", "c" → A, b, c
[Fact] public void Size_ties_break_by_name()
[Fact] public void Nfd_and_nfc_names_sort_together() // "й" в NFD и NFC считаются равными
[Theory]
[InlineData("*.txt", "a.TXT", true)] [InlineData("*.txt;*.md", "b.md", true)]
[InlineData("a?c", "abc", true)] [InlineData("a?c", "abbc", false)]
[InlineData("", "anything", true)] [InlineData("*.txt", "a.txt.bak", false)]
public void Mask(string pattern, string name, bool expected)
```

- [ ] **Step 2–4:** Run `--filter Listing` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): sorting and file masks`.

---

### Task 6: Копирование — основной путь

**Files:**
- Create: `src/Tandem.Core/Operations/FileOperation.cs`, `OperationProgress.cs`, `IOperationInteraction.cs`, `CopyOperation.cs`, `CopyEngine.cs`, `OperationRejectedException.cs`, `src/Tandem.Core/Platform/INativeCopy.cs`
- Test: `tests/Tandem.Core.Tests/Operations/CopyOperationTests.cs`, `ScriptedInteraction.cs` (вспомогательный)

**Interfaces:**
- Consumes: `ProviderRegistry`, `IFileSystemProvider`, `InMemoryFileSystem`.
- Produces:

```csharp
public enum OperationState { Queued, Scanning, Running, Paused, Completed, Cancelled, Failed }
public sealed record OperationProgress(string? CurrentItem, long FilesDone, long FilesTotal,
    long BytesDone, long BytesTotal, double BytesPerSecond, TimeSpan? Remaining);
public sealed record SkippedItem(VfsPath Path, string Reason);
public sealed record OperationResult(OperationState State, IReadOnlyList<SkippedItem> Skipped);
public enum ConflictAction { Overwrite, OverwriteOlder, Skip, Rename, Cancel }
public readonly record struct ConflictDecision(ConflictAction Action, bool ApplyToAll);
public enum ErrorAction { Retry, Skip, SkipAll, Abort }
public interface IOperationInteraction {
    Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct);
    Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct);
}
public abstract class FileOperation {
    public Guid Id { get; } public abstract string Title { get; }
    public OperationState State { get; } public OperationProgress Progress { get; }
    public event EventHandler? Changed;              // не чаще 10 раз в секунду, плюс при смене State
    public Task<OperationResult> RunAsync();         // однократно; повторный вызов → InvalidOperationException
    public Task<OperationResult> Completion { get; }
    public void Pause(); public void Resume(); public void Cancel();
    public abstract Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct);
}
public sealed record CopyOptions(bool VerifyContent = false, string? NewName = null);
public sealed class CopyOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    VfsPath destinationDirectory, CopyOptions options, IOperationInteraction interaction,
    INativeCopy? nativeCopy = null) : FileOperation;
public interface INativeCopy { // только local → local
    Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied, CancellationToken ct);
}
public sealed class OperationRejectedException(string reason) : Exception(reason);
```

Решения: сначала обход источников (`Scanning`) для подсчёта итогов, затем копирование. Файл пишется в `.{имя}.tandem-part`, затем `MoveAsync` в настоящее имя. Время изменения цели = времени источника; права копируются, если оба источника их поддерживают. Символическая ссылка воссоздаётся как ссылка (если цель поддерживает `Symlinks`, иначе пропуск с причиной). `NewName` допустим только при одном источнике. Если оба пути локальные и задан `nativeCopy` — файл копируется им (во временное имя), иначе потоками с буфером 1 МБ. Отказ до начала (`OperationRejectedException` из `RunAsync`): цель внутри источника или совпадает с ним; источник и итоговый путь совпадают; `NewName` при нескольких источниках. `CopyEngine` — внутренний класс с логикой одного элемента, его же использует перенос. `GetVolumeKeysAsync` возвращает ключи томов всех источников и папки назначения (у удаления — только источников).

- [ ] **Step 1: Тесты** (`mem`-источник; `ScriptedInteraction` бросает исключение, если его спросили без сценария)

```csharp
[Fact] public async Task Copies_file_with_content_and_mtime()
[Fact] public async Task Copies_tree_recursively()          // /src/a/b/c.txt → /dst/a/b/c.txt
[Fact] public async Task Copies_between_providers()          // mem1 → mem2
[Fact] public async Task NewName_renames_single_source()     // /src/a.txt, NewName "b.txt" → /dst/b.txt
[Fact] public async Task Symlink_copied_as_link()
[Fact] public async Task Progress_totals_match()             // 3 файла по 10 байт → FilesTotal 3, BytesTotal 30, в конце Done == Total
[Fact] public async Task No_part_files_left_after_success()  // ни один путь не оканчивается на ".tandem-part"
[Fact] public async Task Rejects_copy_into_itself()          // /a → /a/b : OperationRejectedException, записей в dst нет
[Fact] public async Task Rejects_copy_onto_itself()          // /a/x.txt → /a без NewName
[Fact] public async Task Uses_native_copy_for_local_to_local() // поддельный INativeCopy вызван 1 раз (временная папка)
[Fact] public async Task Empty_directory_is_copied()
```

- [ ] **Step 2–4:** Run `--filter CopyOperationTests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): copy operation`.

---

### Task 7: Конфликты имён и ошибки

**Files:**
- Modify: `src/Tandem.Core/Operations/CopyEngine.cs`
- Test: `tests/Tandem.Core.Tests/Operations/CopyConflictTests.cs`, `CopyErrorTests.cs`

**Interfaces:**
- Consumes: `IOperationInteraction`, `CopyOperation`.
- Produces: поведение (сигнатуры не меняются).

Решения: папка на существующую папку — слияние без вопроса. Файл на файл — вопрос. `OverwriteOlder`: перезаписать, если источник новее цели, иначе пропуск. `Rename`: `report.pdf` → `report (2).pdf`, затем `(3)` и т. д. до свободного имени. `Cancel` → состояние `Cancelled`. `ApplyToAll` запоминается до конца операции. Файл против папки (несовпадение типов) — это ошибка, идёт в `ResolveErrorAsync`. Перезапись: сначала полностью записан `.tandem-part`, затем старый файл удаляется и временный переименовывается — старый файл цел, пока новый не готов. Ошибка: `Retry` повторяет элемент; `Skip` добавляет в `Skipped`; `SkipAll` — пропускать все последующие без вопросов; `Abort` → `Failed`. Отказ в доступе на папке-источнике пропускает всё её поддерево одной записью.

- [ ] **Step 1: Тесты**

```csharp
[Fact] public async Task Conflict_skip_keeps_existing()
[Fact] public async Task Conflict_overwrite_replaces()
[Fact] public async Task OverwriteOlder_replaces_only_when_source_newer() // два файла: один новее, один старее
[Fact] public async Task Rename_picks_first_free_suffix()   // существуют "a.txt" и "a (2).txt" → "a (3).txt"
[Fact] public async Task ApplyToAll_asks_once()              // 3 конфликта → 1 вопрос
[Fact] public async Task Conflict_cancel_stops_operation()   // State == Cancelled, оставшиеся файлы не скопированы
[Fact] public async Task Directories_merge_without_prompt()
[Fact] public async Task Type_mismatch_goes_to_error_prompt()
[Fact] public async Task Failed_overwrite_keeps_old_file()   // FailWriteAfter на .tandem-part, ответ Skip → старое содержимое цело
[Fact] public async Task Error_retry_then_succeeds()         // FailOn один раз
[Fact] public async Task Error_skip_is_reported()            // Skipped содержит путь и текст ошибки
[Fact] public async Task SkipAll_asks_once()
[Fact] public async Task Abort_ends_as_failed()
[Fact] public async Task Unreadable_directory_skips_subtree_as_one_item()
```

- [ ] **Step 2–4:** Run `--filter "CopyConflict|CopyError"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): conflict and error handling`.

---

### Task 8: Пауза, отмена, проверка содержимого

**Files:**
- Modify: `src/Tandem.Core/Operations/FileOperation.cs`, `CopyEngine.cs`
- Test: `tests/Tandem.Core.Tests/Operations/CopyControlTests.cs`

Решения: отмена проверяется между блоками записи; при отмене или ошибке текущий `.tandem-part` удаляется. Пауза останавливает работу на границе блока, `State == Paused`. `VerifyContent`: после записи источник и `.tandem-part` перечитываются, сравниваются SHA-256; несовпадение — ошибка элемента (идёт в `ResolveErrorAsync`), временный файл удаляется. При `VerifyContent` байты проверки входят в `BytesTotal` (итог удваивается).

- [ ] **Step 1: Тесты**

```csharp
[Fact] public async Task Cancel_mid_file_removes_part_and_leaves_no_target() // большой файл, отмена из обработчика Changed
[Fact] public async Task Cancel_keeps_already_copied_files()
[Fact] public async Task Pause_stops_progress_until_resume()   // BytesDone не растёт 200 мс в Paused, затем Completed
[Fact] public async Task Target_failure_mid_file_removes_part_and_keeps_source() // Review Focus 2: FailWriteAfter(…, 5 байт, IOException("No space")), ответ Abort
[Fact] public async Task Verify_passes_for_identical_content()
[Fact] public async Task Verify_mismatch_is_item_error()        // источник, меняющий содержимое при втором чтении (FailOn-хук)
```

- [ ] **Step 2–4:** Run `--filter CopyControlTests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): pause, cancel, content verification`.

---

### Task 9: Перенос и удаление

**Files:**
- Create: `src/Tandem.Core/Operations/MoveOperation.cs`, `DeleteOperation.cs`, `src/Tandem.Core/Platform/ITrash.cs`
- Test: `tests/Tandem.Core.Tests/Operations/MoveOperationTests.cs`, `DeleteOperationTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class MoveOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    VfsPath destinationDirectory, CopyOptions options, IOperationInteraction interaction,
    INativeCopy? nativeCopy = null) : FileOperation;
public interface ITrash { Task MoveToTrashAsync(string localPath, CancellationToken ct); }
public sealed class DeleteOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    bool toTrash, ITrash? trash, IOperationInteraction interaction) : FileOperation;
```

Решения переноса: один источник и одинаковый `GetVolumeKeyAsync` → `MoveAsync` (переименование), при конфликте имён — те же правила, что в задаче 7. Иначе копирование через `CopyEngine`; источник удаляется поэлементно сразу после успешной записи (и проверки размера) этого элемента; папка-источник удаляется, только если в ней ничего не осталось. Пропущенные и ошибочные элементы остаются на месте. Переименование на месте = перенос в ту же папку с `NewName`; совпадение имён, отличающихся только регистром, выполняется.
Решения удаления: `toTrash` требует `trash != null` и локального источника, иначе `OperationRejectedException`; в корзину уходит выбранный элемент целиком, без обхода. Безвозвратное удаление — обход в глубину, ошибки через `ResolveErrorAsync`.

- [ ] **Step 1: Тесты**

```csharp
// Move
[Fact] public async Task Same_volume_uses_rename()                 // FailOn("OpenWrite") не срабатывает
[Fact] public async Task Cross_volume_copies_then_deletes_source() // SetVolumeKey для двух префиксов
[Fact] public async Task Cross_provider_move_works()
[Fact] public async Task Skipped_item_stays_in_source_and_parent_kept() // 1 из 3 файлов в папке пропущен
[Fact] public async Task Failed_copy_never_deletes_source()        // FailWriteAfter, ответ Skip
[Fact] public async Task Size_mismatch_keeps_source()              // цель короче источника → ошибка элемента
[Fact] public async Task Rename_in_place()                          // NewName, та же папка
[Fact] public async Task Case_only_rename_works()                   // "a.txt" → "A.txt"
[Fact] public async Task Rejects_move_into_itself()
// Delete
[Fact] public async Task Permanent_delete_removes_tree()
[Fact] public async Task Trash_calls_ITrash_once_per_selected_item() // папка с 100 файлами → 1 вызов
[Fact] public async Task Trash_on_provider_without_trash_is_rejected()
[Fact] public async Task Delete_error_skip_continues_with_rest()
```

- [ ] **Step 2–4:** Run `--filter "MoveOperation|DeleteOperation"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): move and delete operations`.

---

### Task 10: Очередь операций

**Files:**
- Create: `src/Tandem.Core/Operations/OperationQueue.cs`
- Test: `tests/Tandem.Core.Tests/Operations/OperationQueueTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class OperationQueue {
    public IReadOnlyList<FileOperation> Operations { get; }  // активные и ожидающие, в порядке добавления
    public event EventHandler? Changed;
    public Task<OperationResult> Enqueue(FileOperation op, bool runImmediately = false);
}
```

Решения: операция стартует сразу, если `runImmediately` или ни одна выполняющаяся операция не затрагивает те же тома (`GetVolumeKeysAsync`); иначе ждёт в состоянии `Queued` и стартует, когда мешающие завершатся, в порядке добавления. Отменённая в очереди операция не запускается. Завершённые удаляются из `Operations`.

- [ ] **Step 1: Тесты**

```csharp
[Fact] public async Task Same_volume_runs_sequentially()      // вторая Queued, пока первая Running
[Fact] public async Task Different_volumes_run_in_parallel()
[Fact] public async Task RunImmediately_bypasses_queue()
[Fact] public async Task Cancelled_queued_operation_never_runs()
[Fact] public async Task Failure_of_one_does_not_block_next()
```

- [ ] **Step 2–4:** Run `--filter OperationQueueTests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): operation queue`.

---

### Task 11: macOS — корзина и системное копирование

**Files:**
- Create: `src/Tandem.Platform.Mac/Interop/ObjC.cs`, `Interop/LibC.cs`, `MacTrash.cs`, `MacNativeCopy.cs`
- Test: `tests/Tandem.Platform.Mac.Tests/MacTrashTests.cs`, `MacNativeCopyTests.cs`

**Interfaces:**
- Consumes: `ITrash`, `INativeCopy`.
- Produces: `public sealed class MacTrash : ITrash` (доп. метод `Task<string> MoveToTrashAndGetLocationAsync(string path, CancellationToken ct)`), `public sealed class MacNativeCopy : INativeCopy`, внутренний `ObjC` (обёртки `objc_getClass`, `sel_registerName`, `objc_msgSend`, `NSString`/`NSURL`/`NSArray` ↔ .NET, чтение `NSError`).

Решения: корзина — `-[NSFileManager trashItemAtURL:resultingItemURL:error:]`; `NSError` → `IOException` с его описанием. Копирование — `copyfile(3)` с флагами `COPYFILE_ALL | COPYFILE_CLONE | COPYFILE_NOFOLLOW`, прогресс и отмена через `copyfile_state` (`COPYFILE_STATE_STATUS_CB`, чтение `COPYFILE_STATE_COPIED`, возврат `COPYFILE_QUIT` при отмене → `OperationCanceledException`). Ошибка → `IOException` с текстом `strerror(errno)`. Вызов `copyfile` — в `Task.Run`.

- [ ] **Step 1: Тесты** (все во временной папке)

```csharp
[Fact] public async Task Trash_removes_from_original_and_places_in_trash() // затем удалить файл из корзины по возвращённому пути
[Fact] public async Task Trash_missing_file_throws_IOException()
[Fact] public async Task Copy_preserves_content_mtime_mode_and_xattr()      // xattr "user.tandem.test" задать через setxattr
[Fact] public async Task Copy_reports_final_byte_count()                    // последний отчёт == размеру файла (файл 8 МБ)
[Fact] public async Task Copy_cancel_throws_OperationCanceled()             // токен отменён заранее
[Fact] public async Task Copy_to_missing_directory_throws_IOException()
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.Platform.Mac.Tests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(mac): trash and native copy`.

---

### Task 12: macOS — Finder, программы, терминал, Quick Look

**Files:**
- Create: `src/Tandem.Core/Platform/IProcessRunner.cs`, `IShellIntegration.cs`, `IQuickLook.cs`; `src/Tandem.Platform.Mac/ProcessRunner.cs`, `MacShellIntegration.cs`, `MacQuickLook.cs`
- Test: `tests/Tandem.Platform.Mac.Tests/MacShellIntegrationTests.cs`, `MacQuickLookTests.cs`, `ProcessRunnerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);
public interface IRunningProcess : IAsyncDisposable {
    IAsyncEnumerable<string> OutputLines { get; }   // stdout и stderr вперемешку
    Task<int> ExitCode { get; } void Kill();
}
public interface IProcessRunner { IRunningProcess Start(ProcessSpec spec); }
public sealed record AppInfo(string Name, string BundlePath);
public interface IShellIntegration {
    void OpenDefault(string path); void OpenInTextEditor(string path);
    IReadOnlyList<AppInfo> GetAppsFor(string path); void OpenWith(string path, AppInfo app);
    void RevealInFinder(string path); void OpenTerminal(string directory); void OpenFullDiskAccessSettings();
}
public interface IQuickLook { bool IsOpen { get; } void Toggle(IReadOnlyList<string> paths); void Close(); }
```

Решения: всё через `/usr/bin/open` и `IProcessRunner`, кроме списка программ: `OpenDefault` → `open <path>`; `OpenInTextEditor` → `open -t <path>`; `OpenWith` → `open -a <BundlePath> <path>`; `RevealInFinder` → `open -R <path>`; `OpenTerminal` → `open -a Terminal <dir>`; настройки → `open "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles"`. `GetAppsFor` — `-[NSWorkspace URLsForApplicationsToOpenURL:]`, имя программы — имя пакета без `.app`. Quick Look в этапе 1 — процесс `/usr/bin/qlmanage -p <paths…>`; `Toggle` при открытом окне убивает процесс; `IsOpen` сбрасывается, когда процесс завершился сам. Аргументы всегда передаются списком, без оболочки. (В песочнице `qlmanage` недоступен — замена на этапе 4, интерфейс остаётся.)

- [ ] **Step 1: Тесты** (с записывающим `IProcessRunner`)

```csharp
[Fact] public void Reveal_runs_open_R()               // FileName "/usr/bin/open", Arguments ["-R", path]
[Fact] public void OpenWith_passes_bundle_path()
[Fact] public void Path_with_spaces_and_quotes_is_single_argument() // "/tmp/a b \"c\".txt"
[Fact] public void GetAppsFor_txt_returns_at_least_TextEdit()        // настоящий NSWorkspace
[Fact] public void QuickLook_toggle_starts_then_kills()
[Fact] public void QuickLook_resets_when_process_exits()
[Fact] public async Task ProcessRunner_streams_output_and_exit_code() // /bin/echo hello → "hello", 0
```

- [ ] **Step 2–4:** Run → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(mac): shell integration and quick look`.

---

### Task 13: macOS — тома, теги, владелец

**Files:**
- Create: `src/Tandem.Core/Platform/IVolumeService.cs`, `IFileMetadataService.cs`; `src/Tandem.Platform.Mac/MacVolumeService.cs`, `MacFileMetadataService.cs`
- Test: `tests/Tandem.Platform.Mac.Tests/MacVolumeServiceTests.cs`, `MacFileMetadataServiceTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record VolumeInfo(string Name, string MountPoint, long TotalBytes, long FreeBytes, bool IsRemovable);
public interface IVolumeService { IReadOnlyList<VolumeInfo> GetVolumes(); event EventHandler? Changed; }
public sealed record OwnerInfo(string User, string Group);
public interface IFileMetadataService {
    IReadOnlyList<string> GetTags(string path); void SetTags(string path, IReadOnlyList<string> tags);
    OwnerInfo GetOwner(string path);
}
```

Решения: тома — `-[NSFileManager mountedVolumeURLsIncludingResourceValuesForKeys:options:]` с пропуском скрытых томов; имя — `NSURLVolumeLocalizedNameKey`, съёмность — `NSURLVolumeIsRemovableKey` или `NSURLVolumeIsEjectableKey`; место — `DriveInfo`. `Changed` — наблюдение за папкой `/Volumes` (`FileSystemWatcher`), склейка 500 мс. Теги — `NSURLTagNamesKey` через `getResourceValue`/`setResourceValue`. Владелец — `attributesOfItemAtPath` (`NSFileOwnerAccountName`, `NSFileGroupOwnerAccountName`).

- [ ] **Step 1: Тесты**

```csharp
[Fact] public void Volumes_include_root()               // есть том с MountPoint "/", TotalBytes > 0
[Fact] public void Tags_roundtrip()                      // SetTags(["Red","Работа"]) → GetTags возвращает оба
[Fact] public void SetTags_empty_clears()
[Fact] public void Owner_of_temp_file_is_current_user() // Environment.UserName
[Fact] public void Tags_on_missing_file_throw_IOException()
```

- [ ] **Step 2–4:** Run → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(mac): volumes, tags, owner`.

---

### Task 14: Состояние и настройки

**Files:**
- Create: `src/Tandem.Core/Settings/AppState.cs`, `JsonStateStore.cs`
- Test: `tests/Tandem.Core.Tests/Settings/JsonStateStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record TabState(string ProviderId, string Path, SortColumn SortColumn, bool SortDescending, bool Pinned);
public sealed record PanelState(IReadOnlyList<TabState> Tabs, int ActiveTab);
public sealed record ViewState(bool Sidebar = false, bool VolumeBars = true, bool CommandLine = true, bool FKeyBar = true);
public sealed record WindowState(double X, double Y, double Width, double Height, bool Maximized, double SplitRatio);
public sealed record AppState(int Version, PanelState Left, PanelState Right, bool LeftIsActive,
    ViewState View, WindowState? Window, IReadOnlyList<string> Favorites, bool ShowHidden, string? Language) {
    public const int CurrentVersion = 1;
    public static AppState Default(string homeDirectory);  // по одной вкладке на домашней папке, сортировка по имени
}
public sealed class JsonStateStore(string directory) {    // файл state.json
    public AppState Load(string homeDirectory);
    public void Save(AppState state);
}
```

Решения: запись — во временный файл и переименование. `Load`: файла нет → `Default`; JSON не разбирается, `Version` больше текущей или обязательные части отсутствуют → файл переименовывается в `state.json.bad` (с перезаписью старого `.bad`) и возвращается `Default`. Неизвестные поля игнорируются. Папка создаётся при первой записи.

- [ ] **Step 1: Тесты**

```csharp
[Fact] public void Missing_file_gives_default()
[Fact] public void Save_then_load_roundtrips()
[Fact] public void Corrupt_json_gives_default_and_keeps_bad_copy()   // Review Focus 5
[Fact] public void Newer_version_gives_default_and_keeps_bad_copy()
[Fact] public void Empty_tabs_list_is_treated_as_corrupt()
[Fact] public void Unknown_fields_are_ignored()
[Fact] public void Save_creates_directory()
```

- [ ] **Step 2–4:** Run `--filter JsonStateStoreTests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(core): persistent app state`.

---

### Task 15: Локализация и форматирование

**Files:**
- Create: `src/Tandem.App/Resources/Strings.resx`, `Strings.ru.resx`, `Resources/Loc.cs`, `Services/SizeFormatter.cs`
- Test: `tests/Tandem.App.Tests/LocalizationTests.cs`, `SizeFormatterTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class Loc {
    public static string Get(string key);                        // нет ключа → сам ключ
    public static string Format(string key, params object[] args);
    public static void SetLanguage(string? code);                // "ru", "en", null = язык системы; до создания окна
}
// разметка: {loc:Tr Key=Menu_View}
public static class SizeFormatter { public static string Format(long bytes); }
```

Решения: язык применяется при запуске (смена требует перезапуска). Размеры — двоичные единицы, одна цифра после запятой от КБ и выше, без неё для байтов; подписи из ресурсов (`Unit_B`, `Unit_KB`, `Unit_MB`, `Unit_GB`, `Unit_TB`): `0 Б`, `1023 Б`, `1,0 КБ`, `2,4 МБ` (ru) / `2.4 MB` (en). Ключи именуются `Область_Смысл`. Тексты следуют правилам: без «пожалуйста», без восклицательных знаков, кнопки — глагол.

- [ ] **Step 1: Тесты**

```csharp
[Fact] public void Both_languages_have_identical_key_sets()
[Fact] public void No_empty_values_in_either_language()
[Fact] public void Missing_key_returns_key()
[Fact] public void Russian_is_selected_by_code() // SetLanguage("ru"); Get("Unit_MB") == "МБ"
[Theory]
[InlineData(0, "0 B")] [InlineData(1023, "1023 B")] [InlineData(1024, "1.0 KB")]
[InlineData(2516582, "2.4 MB")] [InlineData(1099511627776, "1.0 TB")]
public void Formats_sizes_en(long bytes, string expected)
```

- [ ] **Step 2–4:** Run `dotnet test tests/Tandem.App.Tests --filter "Localization|SizeFormatter"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): localization and size formatting`.

---

### Task 16: Модель панели — чтение, сортировка, переходы

**Files:**
- Create: `src/Tandem.App/ViewModels/PanelViewModel.cs`, `EntryRowViewModel.cs`
- Test: `tests/Tandem.App.Tests/ViewModels/PanelListingTests.cs`

**Interfaces:**
- Consumes: `ProviderRegistry`, `EntrySorter`, `SizeFormatter`, `Loc`.
- Produces:

```csharp
public sealed partial class EntryRowViewModel : ObservableObject {
    public FileEntry? Entry { get; }            // null для строки ".."
    public bool IsParentLink { get; }
    public string Name { get; } public string Extension { get; }
    public string SizeText { get; }             // для папок — Loc "Panel_Folder"
    public string DateText { get; } public string ModeText { get; }  // "rwxr-xr-x"
    public bool IsSelected { get; set; }
}
public sealed partial class PanelViewModel : ObservableObject {
    public PanelViewModel(ProviderRegistry providers);
    public VfsPath CurrentPath { get; }
    public IReadOnlyList<EntryRowViewModel> Rows { get; }  // реализует INotifyCollectionChanged
    public int CursorIndex { get; set; }
    public EntryRowViewModel? CursorRow { get; }
    public SortSpec Sort { get; set; }                      // смена пересортировывает, курсор остаётся на том же имени
    public bool ShowHidden { get; set; }
    public bool IsLoading { get; } public string? Error { get; }
    public event EventHandler<VfsPath>? Navigated;          // после успешной смены папки
    public event EventHandler<FileEntry>? FileActivated;    // Enter на файле
    public Task NavigateToAsync(VfsPath path, string? focusName = null);
    public Task ActivateAsync();   // Enter: папка/".." → переход; файл → FileActivated
    public Task GoUpAsync();       // курсор встаёт на папку, из которой вышли
    public Task RefreshAsync();    // сохраняет курсор и выделение по именам
}
```

Решения: чтение в фоне; первые 500 записей показываются сразу несортированными, окончательный отсортированный список заменяет их одной операцией (`Reset`), без поэлементных уведомлений. Строка `..` первая, кроме корня. Неудачный переход (нет доступа, нет папки) оставляет прежнюю папку и содержимое, текст в `Error`; следующий удачный переход очищает `Error`. Новый переход отменяет незавершённый. Подписка `Watch` на текущую папку → `RefreshAsync`. Если при обновлении папка исчезла — переход к ближайшему существующему предку.

- [ ] **Step 1: Тесты** (`[AvaloniaFact]`, источник `mem`)

```csharp
public async Task Lists_sorted_with_parent_link_first()
public async Task Root_has_no_parent_link()
public async Task Hidden_files_filtered_until_ShowHidden()
public async Task Enter_on_directory_navigates()
public async Task GoUp_places_cursor_on_previous_folder()
public async Task Access_denied_keeps_previous_listing_and_sets_error()  // FailOn("Enumerate")
public async Task Sort_change_keeps_cursor_on_same_name()
public async Task Refresh_keeps_cursor_and_selection_by_name()
public async Task Watch_change_triggers_refresh()                         // RaiseChanged → новая строка появилась
public async Task Vanished_directory_falls_back_to_nearest_ancestor()     // Review Focus 4
public async Task Newer_navigation_cancels_older()                        // медленная папка, затем быстрая → итог быстрая
public async Task Loads_100k_entries_fast()  // первые строки < 1 с, полный отсортированный список < 3 с, уведомлений Reset ≤ 3
public async Task Enter_on_file_raises_FileActivated()
```

- [ ] **Step 2–4:** Run `--filter PanelListingTests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): panel listing view model`.

---

### Task 17: Модель панели — выделение и быстрый поиск

**Files:**
- Modify: `src/Tandem.App/ViewModels/PanelViewModel.cs` (вынести в `PanelViewModel.Selection.cs`, partial)
- Test: `tests/Tandem.App.Tests/ViewModels/PanelSelectionTests.cs`, `PanelQuickSearchTests.cs`

**Interfaces:**
- Produces (члены `PanelViewModel`):

```csharp
public void ToggleSelectAndMoveDown();
public void MoveCursor(int delta, bool extendSelection);   // Shift+стрелки выделяют пройденные строки
public void SelectByMask(string mask); public void UnselectByMask(string mask);
public void SelectAll(); public void InvertSelection(); public void ClearSelection();
public int SelectedCount { get; } public long SelectedBytes { get; }
public int TotalCount { get; } public long TotalBytes { get; }      // без ".."
public string StatusText { get; }   // Loc "Panel_Status": «Выбрано {0} из {1}, {2}»
public IReadOnlyList<FileEntry> GetOperationTargets();              // выделенные, иначе элемент под курсором; ".." никогда
public string QuickSearchText { get; }
public bool QuickSearchInput(string text);   // добавляет символы; false, если совпадения нет (символ не принят)
public void QuickSearchBackspace(); public void QuickSearchCancel();
```

Решения: `..` не выделяется и не входит в счётчики. Маска применяется и к папкам. Размер папок в `SelectedBytes` считается как 0. Быстрый поиск: первая строка, чьё имя начинается с набранного (без учёта регистра, обе стороны в форме C); поиск от начала списка; сброс при переходе в другую папку, движении курсора, Esc.

- [ ] **Step 1: Тесты**

```csharp
public async Task Toggle_selects_and_moves_down()
public async Task Parent_link_is_never_selected()            // Toggle на "..", SelectAll, Invert
public async Task Shift_move_selects_range()
public async Task Select_by_mask_and_unselect()              // "*.txt" выделяет 2 из 5; затем "a*" снимает 1
public async Task Invert_selection()
public async Task Status_counts_bytes_of_selected_files()
public async Task Targets_are_selection_or_cursor_item()
public async Task Targets_empty_when_cursor_on_parent_link()
public async Task QuickSearch_moves_to_first_prefix_match()  // "re" → "README.md", "rep" → "report.pdf"
public async Task QuickSearch_rejects_char_without_match()   // возвращает false, текст не изменился
public async Task QuickSearch_matches_nfd_name_with_nfc_input()  // Review Focus 3: файл "й.txt" в NFD, ввод "й" в NFC
public async Task Odd_names_do_not_break_listing()           // эмодзи, перевод строки в имени, имя из 255 символов
public async Task QuickSearch_resets_on_navigation()
```

- [ ] **Step 2–4:** Run `--filter "PanelSelection|PanelQuickSearch"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): selection and quick search`.

---

### Task 18: Вкладки, история, избранное, рабочее пространство

**Files:**
- Create: `src/Tandem.App/ViewModels/NavigationHistory.cs`, `PanelTabsViewModel.cs`, `FavoritesViewModel.cs`, `WorkspaceViewModel.cs`
- Test: `tests/Tandem.App.Tests/ViewModels/NavigationHistoryTests.cs`, `PanelTabsTests.cs`, `WorkspaceTests.cs`

**Interfaces:**
- Consumes: `PanelViewModel`, `AppState`, `JsonStateStore`.
- Produces:

```csharp
public sealed class NavigationHistory {            // не более 50 записей
    public void Visit(VfsPath path); public bool CanGoBack { get; } public bool CanGoForward { get; }
    public VfsPath? Back(); public VfsPath? Forward(); public IReadOnlyList<VfsPath> Recent { get; }
}
public sealed partial class TabViewModel : ObservableObject {
    public PanelViewModel Panel { get; } public NavigationHistory History { get; }
    public bool IsPinned { get; set; } public string Title { get; }   // имя текущей папки, "/" для корня
}
public sealed partial class PanelTabsViewModel : ObservableObject {
    public IReadOnlyList<TabViewModel> Tabs { get; } public TabViewModel Active { get; }
    public Task NewTabAsync();        // копия текущей папки, становится активной
    public bool CloseActive();        // false для последней и для закреплённой
    public void Next(); public void Previous(); public void Activate(TabViewModel tab);
    public Task GoBackAsync(); public Task GoForwardAsync();
    public PanelState ToState(); public Task RestoreAsync(PanelState state, VfsPath fallback);
}
public sealed partial class FavoritesViewModel : ObservableObject {
    public IReadOnlyList<string> Items { get; } public void Add(string path); public void Remove(string path);
}
public enum PanelSide { Left, Right }
public sealed partial class WorkspaceViewModel : ObservableObject {
    public PanelTabsViewModel Left { get; } public PanelTabsViewModel Right { get; }
    public PanelSide ActiveSide { get; set; }
    public PanelViewModel ActivePanel { get; } public PanelViewModel PassivePanel { get; }
    public void SwitchPanel(); public Task SwapPanelsAsync(); public Task MirrorToOtherAsync();
    public FavoritesViewModel Favorites { get; } public ViewState View { get; set; } public bool ShowHidden { get; set; }
    public Task LoadAsync(); public void Save();     // через JsonStateStore
}
```

Решения: переходы «назад/вперёд» не добавляют записей в историю; обычный переход обрезает «вперёд». Повтор одной и той же папки подряд не дублируется. Восстановление вкладки с несуществующим путём — ближайший существующий предок, иначе `fallback` (домашняя папка). `ShowHidden` — общий для всех панелей. `Save` вызывается при закрытии окна и не чаще раза в 2 секунды после изменений.

- [ ] **Step 1: Тесты**

```csharp
// History
public void Back_and_forward()            public void New_visit_truncates_forward()
public void Consecutive_duplicate_ignored() public void Caps_at_50()
// Tabs
public async Task New_tab_copies_current_path_and_activates()
public async Task Cannot_close_last_or_pinned_tab()
public async Task Close_activates_neighbour()
public async Task Back_does_not_add_history_entry()
public async Task Restore_missing_path_uses_nearest_ancestor()   // Review Focus 4: "/a/b/gone" → "/a/b"
public async Task Restore_unknown_provider_uses_fallback()
public async Task State_roundtrip_preserves_sort_and_pin()
// Workspace
public async Task Switch_changes_active_side()
public async Task Mirror_opens_active_path_in_passive_panel()
public async Task Swap_exchanges_tab_sets()
public async Task Save_then_load_restores_tabs_favorites_and_view()
```

- [ ] **Step 2–4:** Run `--filter "NavigationHistory|PanelTabs|Workspace"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): tabs, history, favorites, workspace`.

---

### Task 19: Команды и раскладка клавиш

**Files:**
- Create: `src/Tandem.App/Commands/CommandIds.cs`, `KeyChord.cs`, `KeyMap.cs`, `CommandRegistry.cs`
- Test: `tests/Tandem.App.Tests/Commands/KeyChordTests.cs`, `KeyMapTests.cs`, `CommandRegistryTests.cs`

**Interfaces:**
- Produces:

```csharp
public readonly record struct KeyChord(Key Key, KeyModifiers Modifiers) {
    public static KeyChord Parse(string text);   // "Cmd+Shift+G", "F5", "Alt+F7"; модификаторы: Cmd, Ctrl, Alt, Shift
    public override string ToString();           // тот же формат, порядок Ctrl, Alt, Shift, Cmd
}
public sealed class KeyMap {
    public static KeyMap Default { get; }
    public string? Find(KeyChord chord);                       // id команды
    public IReadOnlyList<KeyChord> ChordsFor(string commandId);
}
public sealed class CommandRegistry {
    public void Register(string id, Func<Task> execute, Func<bool>? canExecute = null);
    public Task<bool> ExecuteAsync(string id);                 // false: нет команды или canExecute == false
    public bool CanExecute(string id);
}
```

Раскладка по умолчанию (раздел 7.2 спецификации плюс решения этого плана; `Cmd` = `KeyModifiers.Meta`):

| Id | Сочетания |
|---|---|
| `file.view` | F3, Cmd+3 |
| `file.edit` | F4, Cmd+4 |
| `file.copy` | F5, Cmd+5 |
| `file.move` | F6, Cmd+6 |
| `file.mkdir` | F7, Cmd+7 |
| `file.delete` | F8, Cmd+Back |
| `file.deletePermanent` | Shift+F8, Cmd+Alt+Back |
| `file.rename` | Shift+F6, F2 |
| `file.quickLook` | Cmd+Y |
| `file.properties` | Cmd+I |
| `panel.switch` | Tab |
| `panel.refresh` | Cmd+R |
| `panel.swap` | Cmd+U |
| `panel.mirror` | Cmd+Shift+U |
| `nav.up` | Back, Cmd+Up |
| `nav.back` / `nav.forward` | Cmd+OemOpenBrackets / Cmd+OemCloseBrackets |
| `path.edit` | Cmd+Shift+G |
| `cmdline.focus` | Cmd+L |
| `select.toggleDown` | Space |
| `select.all` | Cmd+A |
| `select.byMask` / `unselect.byMask` | Cmd+OemPlus / Cmd+OemMinus |
| `select.invert` | Cmd+Shift+A |
| `tab.new` / `tab.close` | Cmd+T / Cmd+W |
| `tab.next` / `tab.prev` | Ctrl+Tab / Ctrl+Shift+Tab |
| `favorites.show` | Cmd+D |
| `view.toggleHidden` | Cmd+Shift+OemPeriod |

В этапе 1 `file.view` открывает Quick Look, `file.edit` — системный текстовый редактор; встроенные просмотр и редактор заменят их в этапе 2. Раскладка хранится как данные (список пар «id — сочетание»), чтобы этап 2 добавил редактор.

- [ ] **Step 1: Тесты**

```csharp
[Theory] [InlineData("F5")] [InlineData("Cmd+Shift+G")] [InlineData("Ctrl+Shift+Tab")]
public void Chord_roundtrips(string text) => Assert.Equal(text, KeyChord.Parse(text).ToString());
[Fact] public void Parse_rejects_unknown_key()                 // FormatException
[Fact] public void Default_map_has_no_duplicate_chords()
[Theory] [InlineData("F5", "file.copy")] [InlineData("Cmd+5", "file.copy")] [InlineData("Cmd+Back", "file.delete")]
[InlineData("Shift+F8", "file.deletePermanent")] [InlineData("Tab", "panel.switch")] [InlineData("Cmd+Y", "file.quickLook")]
[InlineData("Cmd+L", "cmdline.focus")] [InlineData("Space", "select.toggleDown")]
public void Default_binding(string chord, string id)
[Fact] public void Every_default_binding_refers_to_a_CommandIds_constant() // отражением по CommandIds; команды только из меню (shell.*) сочетаний не имеют
[Fact] public async Task Execute_respects_canExecute()
[Fact] public async Task Unknown_command_returns_false()
```

- [ ] **Step 2–4:** Run `--filter Commands` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): commands and default key map`.

---

### Task 20: Главное окно

**Files:**
- Create: `src/Tandem.App/Views/MainWindow.axaml(.cs)` (заменяет заглушку), `PanelView.axaml(.cs)`, `FileListView.axaml(.cs)`, `SidebarView.axaml(.cs)`, `FKeyBar.axaml(.cs)`, `src/Tandem.App/Composition.cs`, `ViewModels/MainViewModel.cs`
- Modify: `src/Tandem.App/App.axaml(.cs)`, `Program.cs`
- Test: `tests/Tandem.App.Tests/Views/MainWindowTests.cs`

**Interfaces:**
- Consumes: `WorkspaceViewModel`, `CommandRegistry`, `KeyMap`, `IVolumeService`, все службы платформы.
- Produces:

```csharp
public sealed partial class MainViewModel : ObservableObject {
    public WorkspaceViewModel Workspace { get; } public CommandRegistry Commands { get; }
    public IReadOnlyList<VolumeInfo> Volumes { get; }   // обновляется по IVolumeService.Changed
    public Task<bool> HandleKeyAsync(KeyChord chord);   // true, если команда найдена и выполнена
    public bool HandleTextInput(string text);           // быстрый поиск активной панели
}
public static class Composition { public static MainViewModel Build(string stateDirectory, string homeDirectory); }
```

Компоновка — раздел 7.1 спецификации: сверху вниз строка томов со свободным местом, вкладки, путь, заголовки столбцов, список, строка состояния — в каждой панели; под панелями командная строка (пока поле без поведения — задача 24) и панель F-клавиш (F3–F8, по щелчку выполняют команду). Боковая панель слева: «Избранное», «Тома». Панель кнопок в этапе 1 отсутствует (этап 2).

Решения:
- Список файлов — `ListBox` с виртуализацией и строкой фиксированной высоты; столбцы имя, расширение, размер, дата, права — общие ширины для заголовка и строк; щелчок по заголовку меняет `Sort` (повторный — направление), стрелка показывает текущую сортировку.
- Активная панель: строка пути с акцентным фоном; курсор — рамка/фон строки; выделенные строки — цвет текста. Цвета — из ресурсов темы, свои для светлой и тёмной; тема следует системе (`RequestedThemeVariant = Default`).
- Клавиши: `KeyDown` окна → `HandleKeyAsync`; если не обработано и фокус в списке, `TextInput` → `HandleTextInput`. Стрелки, Home/End, PageUp/PageDown двигают курсор (с Shift — выделяют), Enter — `ActivateAsync`, Esc — сброс быстрого поиска. Когда фокус в текстовом поле (путь, командная строка, диалог), команды панели не срабатывают.
- Меню приложения — системное (`NativeMenu`): «Файл», «Выделение», «Переход», «Вид» (флажки: боковая панель, строки томов, командная строка, панель F-клавиш, скрытые файлы; подменю «Язык»: как в системе / Русский / English с пометкой о перезапуске), «Окно». Пункты показывают сочетания из `KeyMap`.
- `path.edit` превращает строку пути в поле ввода; Enter — переход, Esc — отмена; несуществующий путь — сообщение в строке состояния панели, поле остаётся открытым.
- `favorites.show` — всплывающее меню: избранные папки, «Добавить текущую», «Удалить текущую». История — меню по долгому нажатию/правому щелчку на кнопках «назад/вперёд» в строке пути.
- Положение и размер окна, доля разделителя панелей восстанавливаются из `AppState.Window`; окно, оказавшееся вне экранов, возвращается на основной экран.
- Необработанное исключение записывается в `~/Library/Logs/Tandem/tandem.log` и показывается диалогом; приложение не закрывается молча.

- [ ] **Step 1: Тесты** (`[AvaloniaFact]`, `Composition` с источником `mem` и поддельными службами платформы)

```csharp
public async Task Shows_two_panels_with_rows()             // в каждом ListBox строк > 0
public async Task Tab_key_switches_active_panel()
public async Task Typing_letters_runs_quick_search()         // ввод "re" → курсор на README.md
public async Task Space_selects_and_moves_down()
public async Task Header_click_changes_sort_and_toggles_direction()
public async Task View_toggles_hide_and_show_parts()         // FKeyBar, CommandLine, VolumeBars, Sidebar: IsVisible следует ViewState
public async Task Sidebar_hidden_by_default()
public async Task Keys_ignored_while_path_editor_focused()   // Space в поле пути не выделяет
public async Task Path_edit_navigates_on_enter()
public async Task Volume_list_updates_on_change_event()
public async Task FKey_bar_click_executes_command()
```

- [ ] **Step 2:** Run `--filter MainWindowTests` → FAIL.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Run → PASS. Затем вручную: `dotnet run --project src/Tandem.App`, открыть папку на 100 000 файлов (создать скриптом во временной папке), прокрутка без рывков. Если рывки есть — доложить с замерами, не меняя элемент управления самовольно.
- [ ] **Step 5:** Commit: `feat(app): main window`.

---

### Task 21: Диалоги и файловые команды

**Files:**
- Create: `src/Tandem.App/Services/IDialogService.cs`, `DialogService.cs`, `DialogOperationInteraction.cs`, `ViewModels/OperationsViewModel.cs`, `ViewModels/FileCommands.cs`, `Views/Dialogs/` (CopyMove, NewFolder, ConfirmDelete, Conflict, Error, Progress, Summary, Mask)
- Test: `tests/Tandem.App.Tests/ViewModels/FileCommandsTests.cs`, `OperationsViewModelTests.cs`, `DialogOperationInteractionTests.cs`

**Interfaces:**
- Consumes: `CopyOperation`, `MoveOperation`, `DeleteOperation`, `OperationQueue`, `WorkspaceViewModel`, `ITrash`, `INativeCopy`.
- Produces:

```csharp
public sealed record CopyMoveRequest(bool IsMove, int ItemCount, string? SingleName, string Destination);
public sealed record CopyMoveAnswer(string Destination, bool VerifyContent, bool RunImmediately);
public interface IDialogService {
    Task<CopyMoveAnswer?> AskCopyMoveAsync(CopyMoveRequest request);
    Task<string?> AskNameAsync(string titleKey, string initial);              // новая папка, переименование
    Task<bool> ConfirmDeleteAsync(int itemCount, string? singleName, bool permanent);
    Task<string?> AskMaskAsync(bool select);
    Task<ConflictDecision> AskConflictAsync(FileEntry source, FileEntry existing);
    Task<ErrorAction> AskErrorAsync(VfsPath item, string message);
    Task ShowSummaryAsync(IReadOnlyList<SkippedItem> skipped);
    Task ShowMessageAsync(string text);
}
public sealed class DialogOperationInteraction(IDialogService dialogs) : IOperationInteraction;
public sealed partial class OperationsViewModel : ObservableObject {     // окно прогресса
    public IReadOnlyList<OperationItemViewModel> Items { get; }          // Title, Progress, State, Pause/Resume/Cancel
    public bool IsWindowVisible { get; set; }                            // «В фон» скрывает; индикатор в строке состояния возвращает
}
public sealed class FileCommands {   // регистрирует file.* и select.byMask/unselect.byMask в CommandRegistry
    public FileCommands(WorkspaceViewModel workspace, ProviderRegistry providers, OperationQueue queue,
        IDialogService dialogs, ITrash? trash, INativeCopy? nativeCopy, CommandRegistry commands);
}
```

Решения:
- F5/F6: цели — `GetOperationTargets()`; пусто → ничего. Диалог предлагает путь пассивной панели; при одном элементе — путь с именем (можно изменить имя). Ввод, оканчивающийся на `/`, или существующая папка → папка назначения; иначе последний сегмент — новое имя (только для одного элемента; для нескольких — сообщение об ошибке, диалог остаётся). Относительный путь считается от папки активной панели. Назначение в другом источнике, чем введённый текст, не поддерживается: текст всегда относится к источнику пассивной панели.
- F7: имя; вложенный путь `a/b/c` создаёт все уровни; после создания курсор на новой папке. Пустое имя и имя с `/` в начале отклоняются.
- F8: подтверждение всегда; в корзину, если источник поддерживает `Trash` и `trash != null`, иначе диалог сразу предупреждает о безвозвратном удалении. Shift+F8 — всегда безвозвратно, кнопка по умолчанию — «Отмена».
- Shift+F6/F2: имя редактируется в строке списка, выделена часть без расширения; Enter — `MoveOperation` с `NewName`, Esc — отмена.
- Окно прогресса появляется, если операция длится дольше 500 мс. По завершении с пропусками — сводка. Обе панели обновляются после завершения; выделение с перенесённых/удалённых элементов снимается.
- `OperationRejectedException` → `ShowMessageAsync` с её текстом (тексты причин — из ресурсов).
- Вопросы движка приходят из фонового потока: `DialogOperationInteraction` переводит их в поток интерфейса; одновременно показывается один вопрос.
- Диалоги: Enter — основная кнопка, Esc — отмена; в диалоге конфликта показаны размер и дата обоих файлов и флажок «Применить ко всем».

- [ ] **Step 1: Тесты** (поддельный `IDialogService` со сценарием ответов)

```csharp
public async Task F5_copies_selection_to_passive_panel_path()
public async Task F5_with_nothing_under_cursor_does_nothing()       // курсор на "..", диалог не показан
public async Task F5_single_item_with_new_name_renames()            // ввод "/dst/b.txt"
public async Task F5_trailing_slash_means_directory()               // "/dst/new/" → /dst/new/a.txt
public async Task F5_new_name_with_many_items_shows_error_and_reasks()
public async Task F5_relative_destination_resolves_from_active_panel()
public async Task F6_moves_and_both_panels_refresh()
public async Task F7_creates_nested_folders_and_focuses_first()
public async Task F7_rejects_empty_name()
public async Task F8_uses_trash_when_available()
public async Task F8_without_trash_support_warns_permanent()        // ConfirmDeleteAsync вызван с permanent: true
public async Task Shift_F8_is_permanent()
public async Task Cancelled_dialog_starts_no_operation()
public async Task Rejected_operation_shows_message()                // копирование папки в себя
public async Task Skipped_items_show_summary()
public async Task Interaction_serializes_concurrent_questions()     // два вопроса из двух потоков → второй ждёт первого
public async Task Operations_view_lists_running_and_queued()
```

- [ ] **Step 2:** Run `--filter "FileCommands|OperationsViewModel|DialogOperationInteraction"` → FAIL.
- [ ] **Step 3:** Реализовать модели, затем окна диалогов, подключить в `Composition`.
- [ ] **Step 4:** Run → PASS; `dotnet test` целиком → PASS.
- [ ] **Step 5:** Commit: `feat(app): file commands and dialogs`.

---

### Task 22: Интеграция с macOS в интерфейсе

**Files:**
- Create: `src/Tandem.App/ViewModels/PropertiesViewModel.cs`, `ViewModels/ShellCommands.cs`, `Views/Dialogs/PropertiesDialog.axaml(.cs)`
- Modify: `src/Tandem.App/Views/FileListView.axaml` (контекстное меню), `Views/PanelView.axaml` (сообщение об отказе в доступе), `Composition.cs`
- Test: `tests/Tandem.App.Tests/ViewModels/ShellCommandsTests.cs`, `PropertiesViewModelTests.cs`

**Interfaces:**
- Consumes: `IShellIntegration`, `IQuickLook`, `IFileMetadataService`, `IFileSystemProvider.SetModeAsync`.
- Produces:

```csharp
public sealed class ShellCommands {   // регистрирует file.view, file.edit, file.quickLook, file.properties, а также
                                      // shell.reveal, shell.openTerminal, shell.openWith (без сочетаний, из меню)
    public ShellCommands(WorkspaceViewModel workspace, IShellIntegration shell, IQuickLook quickLook,
        IDialogService dialogs, CommandRegistry commands);
    public IReadOnlyList<AppInfo> GetOpenWithApps();     // для элемента под курсором
    public void OpenWith(AppInfo app);
}
public sealed partial class PropertiesViewModel : ObservableObject {
    public PropertiesViewModel(FileEntry entry, IFileSystemProvider provider, IFileMetadataService? metadata);
    public string Name { get; } public string SizeText { get; } public string ModifiedText { get; }
    public string OwnerText { get; }                      // "user:group", только чтение
    public bool OwnerRead { get; set; } /* …OwnerWrite, OwnerExecute, Group*, Other* — 9 флажков */
    public string Octal { get; set; }                     // "755"; двусторонне связано с флажками
    public string TagsText { get; set; }                  // через запятую
    public bool CanEditMode { get; } public bool CanEditTags { get; }
    public Task<bool> ApplyAsync();                       // false и Error при неудаче
    public string? Error { get; }
}
```

Решения: все команды оболочки доступны только для локального источника (`canExecute`). `FileActivated` (Enter на файле) → `OpenDefault`. Quick Look получает цели `GetOperationTargets()`; при движении курсора с открытым окном — перезапуск для нового элемента. Контекстное меню строки: Открыть, Открыть в ▸ (список программ), Показать в Finder, Быстрый просмотр, Копировать, Перенести, Переименовать, Удалить, Свойства; на пустом месте: Новая папка, Открыть терминал здесь, Обновить. Если `Error` панели вызван `VfsAccessDeniedException`, под сообщением — кнопка «Открыть настройки доступа» (`OpenFullDiskAccessSettings`). Неверное восьмеричное значение не применяется, флажки не меняются.

- [ ] **Step 1: Тесты**

```csharp
public async Task Enter_on_file_opens_default_app()
public async Task Shell_commands_disabled_for_non_local_provider()
public async Task QuickLook_toggles_for_cursor_item()
public async Task QuickLook_follows_cursor_while_open()
public async Task OpenWith_lists_apps_for_cursor_item()
public void Octal_and_checkboxes_stay_in_sync()        // Octal "640" → OwnerRead, OwnerWrite, GroupRead; снять GroupRead → "600"
public void Invalid_octal_is_ignored()                  // "9z" не меняет флажки
public async Task Apply_sets_mode_and_tags()
public async Task Apply_failure_sets_error_and_returns_false()
public void Mode_not_editable_without_capability()
```

- [ ] **Step 2–4:** Run `--filter "ShellCommands|PropertiesViewModel"` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): macOS integration in UI`.

---

### Task 23: Перетаскивание

**Files:**
- Create: `src/Tandem.App/ViewModels/DropHandler.cs`
- Modify: `src/Tandem.App/Views/FileListView.axaml.cs`
- Test: `tests/Tandem.App.Tests/ViewModels/DropHandlerTests.cs`

**Interfaces:**
- Consumes: `FileCommands` (общая логика запуска копирования/переноса), `IDialogService`.
- Produces:

```csharp
public sealed class DropHandler(WorkspaceViewModel workspace, FileCommands files) {
    public bool CanDrop(IReadOnlyList<string> localPaths, VfsPath targetDirectory);
    public Task DropAsync(IReadOnlyList<string> localPaths, VfsPath targetDirectory, bool move);
}
```

Решения: из панели наружу и между панелями тянутся `GetOperationTargets()` как список файлов (только локальный источник). Приём: список файлов из Finder или другой панели. Цель — папка под указателем (подсвечивается), иначе текущая папка панели. Cmd при отпускании — перенос, иначе копирование. Всегда показывается обычный диалог копирования/переноса с заполненным назначением. `CanDrop` = false, если все пути уже лежат в целевой папке или цель внутри перетаскиваемой папки.

- [ ] **Step 1: Тесты**

```csharp
public async Task Drop_copies_into_panel_directory()
public async Task Drop_on_folder_row_targets_that_folder()
public async Task Drop_with_move_flag_moves()
public void Cannot_drop_into_same_directory()
public void Cannot_drop_folder_into_itself()
public async Task Cancelled_dialog_does_nothing()
```

- [ ] **Step 2–4:** Run `--filter DropHandlerTests` → FAIL; реализовать модель и подключить события перетаскивания Avalonia; → PASS.
- [ ] **Step 5:** Вручную: перетащить файл из Finder в панель, из панели в Finder, между панелями. Commit: `feat(app): drag and drop`.

---

### Task 24: Командная строка

**Files:**
- Create: `src/Tandem.App/ViewModels/CommandLineViewModel.cs`, `Views/CommandLineView.axaml(.cs)`, `Views/Dialogs/OutputPane.axaml(.cs)`
- Test: `tests/Tandem.App.Tests/ViewModels/CommandLineTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `WorkspaceViewModel`.
- Produces:

```csharp
public sealed partial class CommandLineViewModel : ObservableObject {
    public CommandLineViewModel(WorkspaceViewModel workspace, IProcessRunner runner, string homeDirectory);
    public string Text { get; set; } public string Prompt { get; }   // путь активной панели
    public Task ExecuteAsync();
    public IReadOnlyList<string> History { get; }                     // до 100, стрелки вверх/вниз в поле
    public string Output { get; } public bool IsRunning { get; } public bool IsOutputVisible { get; set; }
    public void Kill();
}
```

Решения: `cd <путь>` — переход активной панели: `~` и `~/x`, `..`, относительные и абсолютные пути, кавычки вокруг пути; `cd` без аргумента — домашняя папка; несуществующий путь — сообщение в области вывода. Всё остальное — `/bin/zsh -lc <текст>` с рабочей папкой активной панели; вывод построчно дописывается в `Output` (хранится не более 5000 последних строк), область вывода открывается автоматически, по завершении дописывается код возврата, панель обновляется. Командная строка недоступна, если активная панель не на локальном источнике. `cmdline.focus` ставит фокус в поле, Esc возвращает фокус в панель. Пустая строка по Enter ничего не делает.

- [ ] **Step 1: Тесты**

```csharp
[Theory] [InlineData("cd ..", "/a")] [InlineData("cd c", "/a/b/c")] [InlineData("cd /x", "/x")]
[InlineData("cd ~", "/home")] [InlineData("cd \"my dir\"", "/a/b/my dir")] [InlineData("cd", "/home")]
public async Task Cd_navigates(string input, string expected)       // активная панель в /a/b
public async Task Cd_missing_reports_and_stays()
public async Task Other_command_runs_zsh_in_panel_directory()        // FileName "/bin/zsh", Arguments ["-lc", "ls -la"], WorkingDirectory
public async Task Output_is_appended_and_capped_at_5000_lines()
public async Task Kill_stops_running_process()
public async Task History_records_and_dedupes_consecutive()
public async Task Empty_input_does_nothing()
```

- [ ] **Step 2–4:** Run `--filter CommandLineTests` → FAIL; реализовать; → PASS.
- [ ] **Step 5:** Commit: `feat(app): command line`.

---

### Task 25: Сборка приложения и приёмка этапа

**Files:**
- Create: `build/make-app.sh`, `build/create-dev-cert.sh`, `build/Info.plist`, `docs/checklists/stage-1.md`
- Test: `build/verify-app.sh`

**Interfaces:**
- Produces: `dist/Tandem.app`.

Решения: `make-app.sh` — `dotnet publish src/Tandem.App -c Release -r osx-arm64 --self-contained`, сборка пакета `dist/Tandem.app` (`Contents/MacOS`, `Contents/Info.plist`, `Contents/Resources`). `Info.plist`: `CFBundleIdentifier` = `dev.tandem.Tandem`, `CFBundleName` = `Tandem`, `CFBundleExecutable` = `Tandem.App`, `LSMinimumSystemVersion` = `14.0`, `NSHighResolutionCapable` = true, `CFBundleShortVersionString` = `0.1.0`. Значка пока нет (этап 4). Подпись: если в связке ключей есть сертификат `Tandem Local Dev` — `codesign --force --deep -s "Tandem Local Dev"`, иначе подпись ad-hoc (`-s -`) с предупреждением в выводе о том, что разрешения на папки будут сбрасываться при пересборке. `create-dev-cert.sh` создаёт самоподписанный сертификат подписи кода `Tandem Local Dev` в связке ключей входа. **Этот скрипт меняет связку ключей заказчика: исполнитель его пишет, но не запускает — запускает заказчик.**

- [ ] **Step 1:** Написать `build/verify-app.sh`: проверяет наличие `dist/Tandem.app/Contents/MacOS/Tandem.App`, `plutil -lint` для `Info.plist`, `codesign --verify --deep dist/Tandem.app`, `lipo -archs` = `arm64`; выход 0 только если всё прошло.
- [ ] **Step 2:** Run `build/verify-app.sh` → FAIL (пакета нет).
- [ ] **Step 3:** Написать `make-app.sh`, `Info.plist`, `create-dev-cert.sh`.
- [ ] **Step 4:** Run `build/make-app.sh && build/verify-app.sh` → выход 0. Run `open dist/Tandem.app` → окно с двумя панелями.
- [ ] **Step 5:** Написать `docs/checklists/stage-1.md` — ручная приёмка по разделу 8 спецификации: день работы без Finder (навигация, копирование, перенос, удаление в корзину и восстановление из неё, вкладки, избранное, перетаскивание в обе стороны, Quick Look, «Открыть в», терминал, права, теги); папка на 100 000 файлов; отмена копирования большого файла не оставляет следов; отключение флешки во время копирования; перезапуск восстанавливает вкладки; оба языка; светлая и тёмная темы; каждый переключатель меню «Вид».
- [ ] **Step 6:** Run `dotnet test` → все тесты PASS. Commit: `build: app bundle and stage 1 checklist`.

---

## Самопроверка плана

- **Покрытие спецификации.** Группа A — задачи 2–10, 16, 17, 19–21, 24; группа B — 14, 18, 20; группа I — 11–13, 20 (меню, тема), 22, 23; 5.4 — задача 4 и кнопка настроек в 22, подпись — 25; 5.5 — 14; 5.6 — 15; 7.1 — 20, 24; 7.2 — 19.
- **Отложено осознанно:** панель кнопок, встроенные просмотр и редактор, редактор сочетаний, темы по типам файлов (этап 2); песочница и значок (этап 4).
- **Отступление от спецификации:** нет. Уточнения, принятые в плане: F3/F4 временно открывают Quick Look и системный редактор; Quick Look через `qlmanage`; смена языка требует перезапуска.
