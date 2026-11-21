# Tandem

Tandem — двухпанельный файловый менеджер для macOS: две панели с вкладками, управление с клавиатуры, копирование
и перенос через очередь операций с окном прогресса, корзина, Quick Look, перетаскивание из Finder и в Finder,
права и теги, встроенная командная строка. Этап 1 (ядро, навигация, интеграция с macOS) завершён; сейчас на
приёмке этап 2 (версия 0.2.0): поиск, просмотр и правка, архивы, настройки.

Возможности этапа 2:

- поиск файлов (Alt+F7, Cmd+F) по маскам, регулярным выражениям и содержимому в разных кодировках, с фильтрами по
  размеру, дате и глубине; результаты по ходу поиска и в панели как обычная папка (F3/F4/F5/F6/F8 оттуда);
- встроенный просмотр (F3): текст в разных кодировках, hex, изображения, файлы любого размера (несколько ГБ
  открываются сразу); панель просмотра (Ctrl+Q); встроенный редактор (F4) с сохранением кодировки или внешний
  редактор из настроек;
- архивы zip, 7z, tar, tar.gz, tar.bz2, tar.xz, rar: вход как в папку (Enter, Cmd+↓), копирование из них, архив в
  архиве, пароли; правка zip (копирование внутрь, удаление, переименование, новая папка, F4); упаковка в zip, tar и
  tar.gz (Alt+F5) и распаковка (Alt+F9);
- настройки (Cmd+,): переназначение клавиш, темы (светлая, тёмная, как в системе), цвета по типам файлов, столбцы
  отдельно для каждой панели, шрифт и высота строк, панель кнопок с пользовательскими командами.

Чтение RAR и 7z с зашифрованным списком файлов не проверено автоматическими тестами: таких тестовых архивов в
проекте нет, их можно проверить только на своих файлах. Остальные ограничения — в конце `docs/checklists/stage-2.md`.

## Требования

- Mac с Apple Silicon (arm64), macOS 14 или новее;
- .NET 11 SDK (`dotnet --version` → 11.x).

## Сборка, тесты, пакет

```sh
dotnet build Tandem.slnx          # сборка
dotnet test Tandem.slnx           # все тесты
build/make-app.sh                 # пакет dist/Tandem.app (Release, osx-arm64, самодостаточный)
build/verify-app.sh               # проверка пакета: структура, Info.plist, архитектура, подпись, пробный запуск
open dist/Tandem.app              # запуск
```

`build/create-dev-cert.sh` — необязательный разовый шаг: создаёт в связке ключей входа самоподписанный сертификат
«Tandem Local Dev», чтобы разрешения macOS на папки не сбрасывались после каждой пересборки. Без него пакет
подписывается ad-hoc и всё работает. Скрипт объясняет, что добавит, и спрашивает подтверждение.

Настройки: `~/Library/Application Support/Tandem/` — `state.json` (вкладки, избранное, окно, меню «Вид»; формат
этапа 1) и `settings.json` (клавиши, тема, цвета, столбцы, шрифт, панель кнопок, внешний редактор). Временные файлы:
`~/Library/Caches/Tandem/tmp`. Журнал ошибок: `~/Library/Logs/Tandem/tandem.log`.

## Документы

- Спецификация: `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md`
- План этапа 1: `docs/superpowers/plans/2026-11-19-stage-1-core-navigation-macos.md`
- План этапа 2: `docs/superpowers/plans/2026-11-19-stage-2-search-view-archives-settings.md`
- Ручная приёмка этапа 1: `docs/checklists/stage-1.md`
- Ручная приёмка этапа 2: `docs/checklists/stage-2.md`

## Структура

- `src/Tandem.Core` — источники файлов, движок операций, маски, сортировка, поиск, кодировки, настройки (без
  интерфейса и macOS API);
- `src/Tandem.Archives` — архивы как источники файлов (чтение, правка zip, упаковка) на библиотеке SharpCompress;
- `src/Tandem.Platform.Mac` — корзина, копирование средствами системы, тома, Quick Look, запуск программ;
- `src/Tandem.App` — интерфейс на Avalonia (просмотр, редактор на AvaloniaEdit, поиск, настройки);
- `tests/` — тесты для каждого проекта;
- `build/` — скрипты сборки пакета.

---

## English

Tandem is a dual-pane file manager for macOS (C#, .NET 11, Avalonia). Stage 1 covers the core panels, tabs,
favourites and history, the operation queue (copy, move, delete to Trash), drag and drop with Finder, Quick Look,
permissions and Finder tags, and a built-in command line. Stage 2 (version 0.2.0, in acceptance testing) adds file
search with results shown in a panel; a built-in viewer (text in several encodings, hex, images, multi-gigabyte
files), a quick-view panel and a text editor; archives (zip, 7z, tar, tar.gz/bz2/xz, rar) browsed as folders,
zip editing, packing into zip/tar/tar.gz and unpacking; and settings: rebindable shortcuts, light/dark/system themes,
file-type colours, per-panel columns, font and row height, and a user button bar. The UI is available in English and
Russian. Reading RAR archives and 7z archives with encrypted headers is untested: the project has no such test
archives, so only your own files can check it.

Requirements: Apple Silicon Mac, macOS 14+, .NET 11 SDK.

```sh
dotnet test Tandem.slnx     # run all tests
build/make-app.sh           # build dist/Tandem.app (self-contained, osx-arm64, signed)
build/verify-app.sh         # verify the bundle
open dist/Tandem.app
```

`build/create-dev-cert.sh` optionally creates a self-signed code-signing certificate in your login keychain so
that macOS folder permissions survive rebuilds; without it the app is signed ad-hoc and works the same.

Settings live in `~/Library/Application Support/Tandem/`: `state.json` (tabs, favourites, window; stage 1 format) and
`settings.json` (stage 2 settings). Projects: `src/Tandem.Core`, `src/Tandem.Archives` (archives over SharpCompress),
`src/Tandem.Platform.Mac`, `src/Tandem.App`.

Design: `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md`;
plans: `docs/superpowers/plans/2026-11-19-stage-1-core-navigation-macos.md`,
`docs/superpowers/plans/2026-11-19-stage-2-search-view-archives-settings.md`;
acceptance checklists (Russian): `docs/checklists/stage-1.md`, `docs/checklists/stage-2.md`.
