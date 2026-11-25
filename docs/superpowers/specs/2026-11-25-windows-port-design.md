# Tandem for Windows x64 — port design

Date: 2026-11-25. Supplements `2026-11-19-dual-pane-file-manager-design.md`. That spec lists "Windows builds" as out of scope; this document lifts that for **Windows 10/11 x64**. Linux and Intel Mac stay out of scope.

## Intent

The customer wants to use Tandem on their own Windows machine. They gave no installer or code-signing requirement: a portable zip is enough, and an unsigned exe is acceptable (SmartScreen warns once). The customer delegated every decision below to the builder.

Success: the Windows build starts, works with local drives, the Recycle Bin, Explorer, archives, FTP/SFTP and the SSH console, and its shortcuts follow Windows conventions. The macOS build behaves exactly as before.

## Goals

- `win-x64` self-contained build (not single-file AOT; AOT for Windows needs a Windows host and is not required). It is built and tested on a GitHub Actions `windows-latest` runner, and the release zip comes from that runner.
- Local paths: drives `C:\`, `D:\`, and UNC `\\server\share`. The address bar shows and accepts Windows paths.
- A Windows platform layer that implements every `Tandem.Core.Platform` interface that has a Windows counterpart.
- Shortcuts: where macOS uses ⌘, Windows uses Ctrl. Menus are shown in the window.
- App data in `%APPDATA%\Tandem` (state and settings), `%LOCALAPPDATA%\Tandem\Logs` and `%LOCALAPPDATA%\Tandem\Temp`.

## Non-goals

- Installer, MSIX, code signing, auto-update.
- Finder tags, Quick Look (the built-in quick view, Ctrl+Q, remains), the file owner in the properties dialog, Full Disk Access.
- Browsing "Network" (a share is reachable by typing `\\server\share`).
- ACL and alternate-stream copying (Zone.Identifier is the only stream carried).
- Running the full GUI test matrix on Windows by hand. CI headless tests plus the customer's own use are the check.

## Anti-goals

- Any change in macOS behaviour, keymap, paths or on-disk formats. The macOS test suite must stay green with unchanged expectations.
- A build that crashes on start, or that silently overwrites a file on Windows (the spec's no-silent-overwrite rule holds on Windows as well).

## Approaches considered

1. **Unix-form virtual paths, mapped at the OS boundary (chosen).** A local `VfsPath` stays `/`-rooted. On Windows a drive is the first segment (`/C:/Users/x`), UNC is `/UNC/server/share/...`, and `/` itself is a virtual "This PC" folder that lists the drives. A single mapper converts to and from OS strings. Core, archives, the network providers and every test that uses `/` paths keep working. Cost: each place that hands `.Path` to the OS or shows it to the user must go through the mapper.
2. **Native Windows strings inside `VfsPath`.** This means rewriting `Normalize`, `Parent`, `Combine` and the comparisons for two grammars. Every provider (zip and remote paths are always `/`) would then need to know which grammar it uses. That is much more churn and risk to macOS.
3. **A separate Windows app over the same Core.** This duplicates the UI. Rejected.

## Design

### Paths (`Tandem.Core/Vfs/Local/LocalPath.cs`)

- `LocalPathStyle { Unix, Windows }`, with `LocalPath.Current` chosen once from `OperatingSystem.IsWindows()`. The conversion functions take the style as a parameter, so the macOS test suite can test Windows conversion as pure string logic.
- `string LocalPath.FromOs(string os, LocalPathStyle style)` (Windows):
  - `C:\a\b`, `c:/a/b` and `C:` + separator become `/C:/a/b`. The drive letter is upper-cased.
  - `\\srv\share\x` becomes `/UNC/srv/share/x`.
  - `\\?\` prefixes are stripped.
  - A string already in VFS form is returned unchanged.
  - A drive-relative `C:foo` or a relative path throws `ArgumentException`.
- `string LocalPath.ToOs(string vfs, LocalPathStyle style)` (Windows):
  - `/C:` becomes `C:\` (the trailing backslash is required, because `C:` alone means "current directory on C").
  - `/C:/a` becomes `C:\a`.
  - `/UNC/srv/share/x` becomes `\\srv\share\x`.
  - `/` has no OS path: it throws `VfsException` and is handled by the provider.
  - On Unix both functions return their input unchanged.
- `VfsPath.Local(string)` applies `FromOs` with `LocalPath.Current` before it normalizes. Every existing caller that passes an OS string (home, `DriveInfo`, dialogs, drag-drop) therefore works with no change. On macOS the call is the identity.
- `VfsPath.ToOsPath()` (an extension or member, local provider only) equals `LocalPath.ToOs(Path, Current)`. Every site in group B of the audit (System.IO, Process, platform services, clipboard, text shown to the user) uses it instead of `.Path`.
- `LocalFileSystemProvider` on Windows:
  - `ListAsync("/")` returns the ready drives as directories named `C:`, without size or time.
  - `StatAsync("/")` and `StatAsync("/C:")` return directories.
  - Any write, rename or delete of `/` or `/X:` throws `VfsAccessDeniedException`.
  - Watching `/` returns null (nothing to watch).
  - `GetVolumeKeyAsync` keeps using `DriveInfo` through `VfsPath.Local`.
- The entry identity behind `IsSameEntryAsync` and same-entry renames moves behind `Tandem.Core.Platform.IEntryIdentity` (`(ulong Volume, ulong FileId)? Get(string osPath)`). The existing `lstat` code becomes the internal `UnixEntryIdentity` in Core and stays the default, so macOS is unchanged. Windows passes its own implementation, based on `GetFileInformationByHandle` with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`.

### Windows platform layer (`src/Tandem.Platform.Windows`, `[SupportedOSPlatform("windows")]`, `LibraryImport`)

| Interface | Windows implementation |
|---|---|
| `IExclusiveRename` | `MoveFileExW(src, dst, 0)`. Without `MOVEFILE_REPLACE_EXISTING` it refuses an existing destination atomically: `ERROR_ALREADY_EXISTS` / `ERROR_FILE_EXISTS` → `VfsAlreadyExistsException`. Without `COPY_ALLOWED`, a move to another volume → `IOException` (`ERROR_NOT_SAME_DEVICE`). `RenameSameEntry` makes the same call (a case-only change of the same entry is allowed). |
| `IEntryIdentity` | `CreateFileW` (`0` access, reparse point + backup semantics) + `GetFileInformationByHandle` → (volume serial, file index). |
| `ITrash` | `SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO \| FOF_NOCONFIRMATION \| FOF_SILENT \| FOF_NOERRORUI \| FOF_NOCONFIRMMKDIR)` on a background thread. A non-zero result or `fAnyOperationsAborted` → `IOException`. |
| `INativeCopy` | The portable managed copy (below). |
| `IVolumeService` | `DriveInfo.GetDrives()` (ready drives). The name is `"{label} ({C:})"`, or just `C:` when there is no label. `IsRemovable` for Removable/CDRom. The system drive comes first, the rest by letter. `Changed` comes from a 2-second poll that compares the set of drive letters with ready flags. |
| `IShellIntegration` | `OpenDefault`: `ShellExecute` (`UseShellExecute = true`). `OpenInTextEditor`: `notepad.exe`. `GetAppsFor`: empty. `OpenWith`: starts the app's exe with the path. `RevealInFinder`: `explorer.exe /select,"path"`. `OpenTerminal`: `wt.exe -d dir` when on PATH, else `cmd.exe` with the working directory. `OpenFullDiskAccessSettings`: completes and does nothing. A `Win32Exception` → `IOException`. |
| `IQuickLook` | No-op, `IsOpen = false`. |
| `IQuarantine` | The NTFS stream `path:Zone.Identifier`. `Get` returns its bytes or null; `Set` writes them. Errors on volumes without streams are swallowed as null / no-op. |
| `ISecretStore` | Credential Manager (`CredReadW` / `CredWriteW` / `CredDeleteW`), `CRED_TYPE_GENERIC`, target `Tandem/{key}`, `CRED_PERSIST_LOCAL_MACHINE`, UTF-8 blob. Errors → `SecretStoreException`. |
| `IFileMetadataService` | Not provided (null): tags and owner show as unavailable. |
| `IProcessRunner` | Reuses `Tandem.Platform.Mac.ProcessRunner` if it is pure managed; otherwise its code moves to Core unchanged. |

**Portable copy** (`Tandem.Core/Platform/Portable/PortableNativeCopy.cs`, tested on macOS):
- Deletes whatever is at the destination first (a link itself is deleted, never its target).
- Copies 1 MiB blocks with `FileStream`, reports running totals and awaits `pauseCheckpoint` between blocks.
- Then copies the creation and last-write times and the attributes, minus `ReadOnly`.
- `CopyFlagsAsync` adds `ReadOnly` when the source has it.
- `CopyDirectoryMetadataAsync` copies the times and attributes.
- `CopyFileMetadataAsync` copies the attributes except `ReadOnly`; on Windows it also copies the `Zone.Identifier` stream.

### Composition and app data

- `Composition.CreatePlatformServices(log, home)` dispatches to `CreateMacServices` (today's body, unchanged) or `CreateWindowsServices`.
- `AppDirectories` (App) returns the state, log and temp directories per OS. On macOS they are exactly today's paths.
- `App.axaml.cs` refuses only operating systems that are neither macOS nor Windows.

### Keys and menus

- `PlatformKeys.Command` is `Meta` on macOS and `Control` on Windows. Hard-coded `KeyModifiers.Meta` checks in views use it.
- `KeyMap.Default` is the macOS table. `KeyMap.DefaultFor(Windows)` comes from it by translation:
  - `Cmd` becomes `Ctrl`.
  - A chord that already has `Ctrl` plus `Cmd` takes Ctrl+Alt (sort chords Ctrl+Cmd+1…5 become Ctrl+F3…F7, as in classic two-pane managers).
  - An explicit override table resolves the remaining collisions. A test asserts that no two commands share a chord on Windows and none is a Windows-reserved chord.
  - Windows-reserved chords: Alt+F4, Alt+Tab, Ctrl+Esc, Ctrl+Alt+Del, Win+any key.
- `KeyChordText` shows `Ctrl+`, `Alt+` and `Shift+` words on Windows (no ⌘ glyphs).
- Terminal copy and paste on Windows: Ctrl+Shift+C / Ctrl+Shift+V. Ctrl+C goes to the remote shell.
- Menus: `MainWindow` hosts a `NativeMenuBar`, which Avalonia shows only where there is no native menu. On Windows the application menu's items (About, Settings, Quit = Alt+F4) are merged as a leading "Tandem" submenu.

### Tests and CI

- `.github/workflows/windows.yml` runs on `windows-latest`. It installs SDK 11.0.100, then:
  - `dotnet test` over every test project except `Tandem.Platform.Mac.Tests`;
  - `Tandem.Platform.Windows.Tests`, real Windows API tests that are skipped elsewhere;
  - a headless start test, `Composition.CreateWindowsServices` + `StartAsync` on a temp home, asserting that the main view model loads with tabs on `C:\`;
  - the `win-x64` publish, uploaded as an artifact.
- Tests that rely on POSIX behaviour (Unix modes, `/tmp`, symlinks without privileges, case-sensitive volumes, macOS-only providers) are skipped on Windows with `Assert.SkipUnless(OperatingSystem.IsMacOS(), "...")`. No assertion is weakened on macOS.
- The macOS run of `dotnet test Tandem.slnx` stays green after every task.

## Left to the builder

- Exact override chords for collisions, within the rules above.
- The internal layout of `Tandem.Platform.Windows`.
- How the "This PC" root renders the drive label (the name stays `C:`; a label column is optional).
