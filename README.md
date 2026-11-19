# Tandem

Tandem — двухпанельный файловый менеджер для macOS: две панели с вкладками, управление с клавиатуры, копирование
и перенос через очередь операций с окном прогресса, корзина, Quick Look, перетаскивание из Finder и в Finder,
права и теги, встроенная командная строка. Сейчас готов этап 1 (ядро, навигация, интеграция с macOS).

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

Настройки: `~/Library/Application Support/Tandem/`, журнал ошибок: `~/Library/Logs/Tandem/tandem.log`.

## Документы

- Спецификация: `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md`
- План этапа 1: `docs/superpowers/plans/2026-11-19-stage-1-core-navigation-macos.md`
- Ручная приёмка этапа 1: `docs/checklists/stage-1.md`

## Структура

- `src/Tandem.Core` — источники файлов, движок операций, маски, сортировка, настройки (без интерфейса и macOS API);
- `src/Tandem.Platform.Mac` — корзина, копирование средствами системы, тома, Quick Look, запуск программ;
- `src/Tandem.App` — интерфейс на Avalonia;
- `tests/` — тесты для каждого проекта;
- `build/` — скрипты сборки пакета.

---

## English

Tandem is a dual-pane file manager for macOS (C#, .NET 11, Avalonia). Stage 1 covers the core panels, tabs,
favourites and history, the operation queue (copy, move, delete to Trash), drag and drop with Finder, Quick Look,
permissions and Finder tags, and a built-in command line. The UI is available in English and Russian.

Requirements: Apple Silicon Mac, macOS 14+, .NET 11 SDK.

```sh
dotnet test Tandem.slnx     # run all tests
build/make-app.sh           # build dist/Tandem.app (self-contained, osx-arm64, signed)
build/verify-app.sh         # verify the bundle
open dist/Tandem.app
```

`build/create-dev-cert.sh` optionally creates a self-signed code-signing certificate in your login keychain so
that macOS folder permissions survive rebuilds; without it the app is signed ad-hoc and works the same.

Design: `docs/superpowers/specs/2026-11-19-dual-pane-file-manager-design.md`;
plan: `docs/superpowers/plans/2026-11-19-stage-1-core-navigation-macos.md`;
stage 1 acceptance checklist (Russian): `docs/checklists/stage-1.md`.
