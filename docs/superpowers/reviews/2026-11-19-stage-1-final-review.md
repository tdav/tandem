# Final whole-branch review — Tandem stage 1 (fd67775..e0ed5af)

State confirmed at HEAD e0ed5af: `dotnet build` 0 warnings / 0 errors; `dotnet test` 1135 passed (Core 326, Platform.Mac 66, App 743), 0 failed. Working tree unchanged by this review (only this file, under the git-ignored `.superpowers/`).

### Coverage of this review

- **Read in full:** Core — `VfsPath`, `IFileSystemProvider`, `LocalFileSystemProvider`, `FileEntry`, `ProviderRegistry`, exceptions, brokers, all of `Operations/` (CopyEngine, FileOperation, Copy/Move/Delete, ItemErrors, OperationQueue, interaction, progress, rejection), `Settings/`, `EntrySorter`, all `Platform/` interfaces. Platform.Mac — MacTrash, MacNativeCopy, LibC, MacShellIntegration, MacQuickLook, MacVolumeService, MacFileMetadataService, ProcessRunner, ProcessLaunch. App — Composition, App.axaml(.cs), Program, ErrorLog, MainViewModel, WorkspaceViewModel, PanelViewModel (+Selection), PanelTabsViewModel, TabViewModel, SideViewModel, FileCommands, ShellCommands, DropHandler, ContextMenuModel, CommandLineViewModel, PropertiesViewModel, InlineRenameViewModel, EntryRowViewModel, OperationsViewModel, DialogOperationInteraction, DialogService, IDialogService, OperationTexts, DestinationParser, KeyMap, CommandIds, CommandRegistry, RunCommand, KeyChord, KeyRouting, MainMenu, Loc, MainWindow.axaml(.cs), PanelView.axaml(.cs), FileListView.axaml(.cs), PanelListBox, DragGesture, SidebarView.axaml, ChoiceDialogs, DialogWindow, SimpleDialogViewModels, build/make-app.sh, Info.plist, csproj/props.
- **Sampled:** `ObjC.cs` (call shapes only), CommandLineView, ContextMenuView, `verify-app.sh`, `create-dev-cert.sh` (header and guards), the checklist (operations, keyboard, limitations sections plus keyword search), tests (names of all operation tests; every sleep / wall-clock site; the 100k tests), the resource files (mechanical parity and placeholder check of all 272 keys, not a wording review).
- **Not read:** `InMemoryFileSystem`, `FileMask`, `CdCommandParser`, `SizeFormatter`, `NavigationHistory`, `FavoritesViewModel`, `RowCollection`, `CopyMoveDialogViewModel`, `InputDialogs`, `InlineRenameEditor`, `OutputPane`, `FKeyBar`, `WindowPlacement`, `ProgressWindow`/`PropertiesDialog` XAML, README, most test bodies, the per-task diffs and reports.
- **Not done:** the GUI was not launched (per instructions). Everything about on-screen behaviour is from reading code. Two scratch experiments were run outside the repo with `copyfile(3)` and the production flags: a locked file, and an exFAT disk image.

### Strengths

- The data-loss core is careful and consistent: part file then rename, backup-and-restore on overwrite, per-file "copy, check size, then delete" for cross-volume moves, non-recursive deletes only (no `recursive: true` anywhere in `src/`), symlinks never followed on delete or trash, and the `IsSameEntryAsync` identity guard at every destructive point. I found no path that overwrites or deletes without a dialog.
- Every UI entry point (F5/F6, drop, inline rename, F8/Shift+F8) goes through one flow in `FileCommands`; the conflict dialog defaults to Skip and permanent delete defaults to Cancel.
- Threading discipline is uniform: every background callback posts to the UI context with a disposed check, and the versioned-load pattern in `PanelViewModel` handles superseded loads cleanly.
- Core is free of Avalonia and macOS frameworks; rejections and skip reasons are machine codes localized in the App. Resources are in exact parity (272/272 keys, placeholders match, no literals in XAML).
- The engine test suite is thorough and mostly deterministic (gates, scripted interaction); the checklist is unusually complete and already targets the riskiest unverified areas (menu key equivalents, exFAT, layout keys).

### Issues

#### Critical (Must Fix)

**C1. Pause does not work inside a file for any local copy, so the checklist's own pause step fails.**
`src/Tandem.Core/Operations/CopyEngine.cs:461-467`, `src/Tandem.Platform.Mac/MacNativeCopy.cs:94-129`, `src/Tandem.App/ViewModels/OperationsViewModel.cs:225-226`.
Every local-to-local file goes through `copyfile`, whose status callback checks cancellation but has no pause checkpoint. For one large file (the checklist scenario: a 4 GB file to another volume, then "Приостановить → копирование стоит") the copy keeps running to the end. The state stays "Running", the Pause button stays enabled and does nothing, and Resume is disabled, so the request cannot even be withdrawn. Pause only takes effect between files. Spec 5.3 requires pause; big files are exactly where it matters. Not data loss and not a crash. The "known limitation" about open handles while paused describes the stream path, which stage 1 never uses for local files.
Fix: give `INativeCopy.CopyFileAsync` a pause checkpoint (a callback or wait handle) and block in `OnStatus` at `COPYFILE_PROGRESS` while paused, reporting Paused through the operation. Add a test next to `Cancel_reaches_an_in_flight_native_copy`, and correct the limitation text.

#### Important (Should Fix before acceptance)

**I1. Closing the window or quitting kills running operations silently, and leftovers are never cleaned.**
`src/Tandem.App/Views/MainWindow.axaml.cs:78-83`, `src/Tandem.App/App.axaml.cs:34-39`, `src/Tandem.App/ViewModels/MainViewModel.cs:243-268`.
`Closing` saves state and closes pending questions, then the process exits (`ShutdownMode.OnMainWindowClose`). Nothing checks `OperationQueue.Operations`, asks the user, cancels, or waits. A copy sent to the background dies mid-file and leaves a hidden `.name.tandem-part`. Killed between the two renames of an overwrite, the destination name is gone and the old content sits in a hidden `.name.tandem-old`. Nothing sweeps either at next start (the only references to these suffixes outside `CopyEngine` are comments). No file is lost: each item of a move is in at least one place, and no damaged file is left under a real name. But a reasonable user expects "operations are running — quit anyway?".
Fix: in `Closing`, if the queue is non-empty, ask; on confirmation cancel all operations and await their completions (bounded) before letting the window close. Also add a checklist step and state what `.tandem-part` / `.tandem-old` files mean. The comment at `CopyEngine.cs:583` ("the destination it backed up is still in place") is not true after a crash between the renames; reword it.

**I2. Copying a Finder-locked file fails and leaves a part file the app cannot delete (reproduced).**
`MacNativeCopy.cs:25-27` (`COPYFILE_ALL` copies `st_flags`), `CopyEngine.cs:481, 559-565`.
With the production flags, the part file inherits `uchg`. Then setting the time, renaming and deleting all fail with EPERM (experiment: `utime`, `rename`, `remove` each returned "Operation not permitted"). The user gets an error; Retry fails again because `COPYFILE_UNLINK` cannot remove the locked leftover; a locked hidden file stays in the destination. The same applies to a cross-volume move (source stays safe).
Fix: clear `UF_IMMUTABLE`/`UF_APPEND` on the part file after `copyfile` and re-apply the source flags after the rename; test in `Tandem.Platform.Mac.Tests`.

**I3. Folder metadata is not copied; a cross-volume move loses it for good.**
`CopyEngine.cs:266` (plain `CreateDirectoryAsync`), `:435-439, 656-664` (time and mode only).
Files keep tags, xattrs, ACLs and flags through `copyfile`; directories get only mtime and mode. Finder tags, comments, custom icons and ACLs on folders vanish on copy, and on a move between volumes the source folder is then deleted. Spec 5.2 promises the local fast path keeps metadata and extended attributes.
Fix: for local-to-local, apply `copyfile(src, dst, COPYFILE_METADATA)` (no data, non-recursive) to created directories where `ApplyDirectoryMetadataAsync` runs now, behind `INativeCopy`.

**I4. Errors from the native copy lose their errno, so "disk full" and "access denied" wording is unreachable for local copies.**
`MacNativeCopy.cs:82` throws `new IOException(strerror)` with the default HResult; `OperationTexts.cs:28-37` classifies by exception type and HResult 28.
A full disk mid-file (Review Focus 2) shows the generic I/O text with an English system detail instead of `OperationError_DiskFull`. EACCES/EPERM shows as generic I/O instead of access denied, so the Full Disk Access guidance of spec 5.4 is missed for copies. The behaviour (part removed, source kept) is right; only the message is wrong.
Fix: map errno in `MacNativeCopy` (ENOSPC to `IOException(msg, 28)`, EACCES/EPERM to `UnauthorizedAccessException`, ENOENT to `FileNotFoundException`) and test.

**I5. A failed `open` is never reported: Enter on a file with no default application does nothing.**
`MacShellIntegration.cs:86-87`, `ProcessLaunch.cs:26-45`, `ShellCommands.cs:230-234`.
The exit code and stderr of `/usr/bin/open` are discarded. Enter on a file with an unknown type, Reveal on a vanished file, or Open With a broken bundle fail silently. Deferred from Task 22; I would not leave it, because it happens in ordinary use and looks like a dead key.
Fix: when `open` exits non-zero, show `Shell_OpenFailed` with its stderr as detail.

**I6. Underscores disappear from paths and names in menus and buttons.**
`MainWindow.axaml.cs:194` (favourites), `PanelView.axaml.cs:134-139` (history), `SidebarView.axaml:17, 28`, `PanelView.axaml:54` (volume buttons).
String headers and button content go through Avalonia's access-key parsing; `ContextMenuView.cs:26` documents this and works around it only for application names. `/Users/me/my_project` is shown as `/Users/me/myproject`. Not seen on screen by me; it follows from the template behaviour the code itself describes.
Fix: wrap these in a `TextBlock`, as `IsLiteralLabel` does.

**I7. Two spec bullets have no UI — rulings needed, same class as the two gaps fixed in Task 26.**
- Spec 6.A "Сортировка по любому [столбцу]": the permissions column cannot be sorted. `SortColumn` has no mode value and the header is a plain `TextBlock` (`PanelView.axaml:169`). Implement, or record the customer's waiver.
- Spec 5.3 lists "сравнить" among the conflict answers; `ConflictAction` has none and the plan never mentions it. This is a plan omission, not an implementer miss. The dialog shows size and date of both files, which may be enough until stage 3; it should be an explicit deferral, not a silent one.

**I8. Quick Look through `qlmanage -p`: toggling and cursor-following probably do not work as the checklist expects. Not verified.**
`MacQuickLook.cs:34`, `ShellCommands.cs:290-355`.
`qlmanage` is a separate process that becomes the active application, so a second Cmd+Y and the arrow keys go to it, not to Tandem. If so, the checklist step "повторное Cmd+Y → закрывается" fails and the follow-the-cursor logic never fires. This is the plan's design choice (Task 12), tested only at model level. Check on screen before the customer does. If confirmed, either reword the checklist (Esc or Space in the preview closes it) or move to `QLPreviewPanel` now rather than in stage 4.

**I9. The production combination has no automated test, and the final bundle has never been launched.**
No test runs `CopyOperation`/`MoveOperation` over `LocalFileSystemProvider` with the real `MacNativeCopy`: Core tests use fakes or null, Mac tests exercise `MacNativeCopy` alone, App tests use fakes. I2 slipped through this gap. Add a handful of integration tests in `Tandem.Platform.Mac.Tests`: plain file, read-only file, locked file, overwrite, cancel, tagged folder. Also, the ledger still lists `build/verify-app.sh` with the display awake and `open dist/Tandem.app` as never done by anyone: a smoke launch must precede the handover.

#### Minor (Can wait)

- `LocalFileSystemProvider.cs:259`: entry kind is derived from `LinkTarget != null`, not from the `ReparsePoint` attribute. If `readlink` ever returned null for a link to a directory, the entry would be reported as a real directory and a permanent delete would walk into the link's target. `DeleteAsync` itself uses the safe test (`IsRealDirectory`). Use the same test in `ToEntry`.
- `LocalFileSystemProvider.cs:95-106`: existence check then `rename(2)` is not atomic (parked). Fine for stage 1; use `renamex_np(RENAME_EXCL)` through the platform seam before network providers arrive.
- `LocalFileSystemProvider.cs:242-254`: `IsCaseOnlyRename` is ordinal-ignore-case, so a rename that differs only in Unicode normalization (NFC to NFD) is refused as "already exists". Use identity (same inode) instead.
- `LocalFileSystemProvider.cs:139-145`: the volume key compares mount paths ordinally and resolves no links. A path typed in another case (`/volumes/USB/...`) or through a link to another volume gets key `/`; a move then tries `rename`, gets EXDEV, and ends in an error dialog. Safe but confusing. A slow or dead mount also stalls the queue (parked).
- `CopyEngine.cs:601`: `.{name}.tandem-part` adds 13 bytes; names near the 255-byte limit cannot be copied.
- `Composition.cs:94-99`: the window is not shown until every tab on both sides has loaded in full; a saved tab on a hung network mount means no window at all. Show the window first, or bound the wait.
- `PanelViewModel.cs:130`: Enter on an `.app` bundle (or any package) navigates into it; nothing in the UI launches an application. A Finder replacement should open packages on Enter.
- `MainViewModel.cs:324-328`: free space is re-read only on volume change and Cmd+R, not after an operation ends.
- `WorkspaceViewModel.cs:204-207`: `LastSaveError` is set but never shown or logged; a failing settings save is silent.
- `PanelViewModel.cs:230-240` (parked): the catch-all reports programming errors as "cannot open the folder" without logging.
- Watcher load: each change burst re-reads and re-sorts the whole folder; a panel on `~` or `/` receives events for the entire subtree (FSEvents is recursive, .NET filters). Watch CPU when idle and when copying many files into a shown 100k folder.
- `App.axaml`: no application-level `NativeMenu`, so the "Tandem" application menu is Avalonia's default, probably English regardless of language and possibly with an "About Avalonia" item. Check on screen.
- `CommandLineView.axaml.cs:69-73`: Esc clears the text first and returns focus only on a second press; spec says Esc returns focus.
- `KeyMap.cs`: Shift+Up/Down and the cursor keys are handled outside the key map (`MainViewModel.PanelKey`), so the stage 2 shortcut editor will not see them; the Insert key itself is not bound.
- Dead code: `Composition.Build(string, string)` (`Composition.cs:59-61`) is unused; resource keys `Button_Overwrite`, `Button_Rename`, `Button_Retry`, `Button_Skip`, `View_Favorites` are unused; `tab.new` and `tab.duplicate` run the same method (`MainViewModel.cs:339-340`).
- `ProgressWindow`: a second Pause click does nothing and a requested pause cannot be withdrawn (follows from C1; fix together).

### Triage of deferred and parked items

Numbers are the line numbers of `deferred-and-rulings.md`.

- 18 (pause may never take effect; UI must not assume) — **fix now**: it is C1.
- 55 (`open` exiting non-zero never reported; underscore as access key) — **fix now**: I5 and I6 (wider than recorded).
- 64 (verify-app with display awake, `open dist/Tandem.app`, cert script never run) — **do now**: smoke launch before handover (I9). The cert script can stay the customer's optional step.
- 47, 46 (native-menu key equivalents stealing keys or double-executing) — **check first, on screen**: if Cmd+A/C/V are taken from text fields, every dialog is affected. The checklist covers it (lines 218-220); move it to the top.
- 28 (COPYFILE_ERR on exFAT/FAT) — **later, largely de-risked**: on an exFAT image a file with quarantine and tag xattrs copied with the production flags without error (xattrs went to `._name`), and the following `utime`/`chmod`/`rename` succeeded. SMB remains untested; keep that checklist line.
- 7, 9 (non-atomic Move) — later (before stage 3).
- 14 (unresolved links in volume keys and into-itself check) — later; not destructive, since the scan completes before any write.
- 13, 17, 21, 23, 24 (progress details, missing top-level source, emptied folders after a cancelled move, FIFO stall, multicast invoke) — later.
- 39 (catch-all in load) — later, but add a log line when touching the file.
- 42, 43, 48, 51, 58, 59, 62 — later.
- 46 (crash when the display is asleep) — later; documented as a known limitation. Revisit if the customer wants "open at login".
- 66 (pin availability not tracked) — later; I traced it and the menu item is enabled in practice, because the menu is built after load and the first availability pass always raises the change.
- 30, 33, 37, 40, 45 (weak or timing-sensitive tests) — later; see question 8.
- Rulings I agree with: 20 (identity guard — the right layer), 16 (safe overwrite), 25 (broker in MacTrash/MacNativeCopy), 32 (volume service dispose does not wait), 34 (state store without broker), 12 (machine codes from Core), 57 (a drop always asks copy or move — safer than trusting modifier state), 61 (command line does not kill `&` jobs), 52 (symlink mode read-only), 50 (own-name rule stays case-sensitive).
- Rulings I disagree with:
  - Plan Task 12 / line 53 context — Quick Look through `qlmanage`: acceptable only if I8 checks out on screen; otherwise it does not meet the stage's stated Quick Look criterion.
  - Line 4 (ordinal `VfsPath` comparison) — agree for destructive decisions now that the identity guard exists, but its cost was understated: it also drives volume keys and "already there" checks (see Minor).
  - Line 55 — leaving `open` failures unreported should not have been deferred.

### Answers to the eight cross-cutting questions

**1. Data-loss surface.** No path overwrites, deletes or moves without the user confirming. Copy, move and drop always show the dialog and ask per conflict (default Skip); inline rename onto an existing name becomes a conflict question; delete always confirms; trash failures become an error question, never a silent permanent delete; drag-out hands the files to the receiving application under its own rules. On cancel or error the part file is removed and the source is kept; a move deletes a source only after its copy is in place and size-checked. The exceptions I found are metadata, not content: folder tags/xattrs/ACLs (I3). On window close or kill: operations are abandoned without warning; `.tandem-part` and, in a narrow window, `.tandem-old` files remain; nothing cleans or reports them at next start (I1). A locked source leaves a part file that cannot be removed (I2).

**2. Threading.** Background arrivals: `FileOperation.Changed`, `OperationQueue.Changed`, `IVolumeService.Changed`, the directory watch callback, interaction questions, process output lines, the Quick Look exit callback. Each is marshalled: operations and queue post with coalescing (`OperationsViewModel`), volumes post with a disposed check, the watch posts and identifies its subscription, questions post into a one-at-a-time UI queue, output goes through a locked buffer and a throttled post, and the Quick Look callback touches only lock-guarded state. I found no view-model state touched off the UI thread. Shutdown order is safe against crashes (every post is wrapped; late events are dropped by disposed flags; watchers are disposed in the background; Quick Look and the command line are killed). The one ordering problem is the absence of any step for running operations (I1).

**3. Access outside the broker.** User-file access not under `IFileAccessBroker`: `FileListView.axaml.cs:226-227` (Avalonia storage items for drag-out, and the hand-off of those files to other applications); dropped paths arriving as plain strings (`FileListView.LocalPathsOf`), with no way to hand the broker the grant that came with the drop; `MacShellIntegration.GetAppsFor` (`Path.Exists`, accepted) and all `open` launches; `MacQuickLook` (`qlmanage`); `CommandLineViewModel` (zsh in the panel's folder, to be hidden in the sandbox per spec); `MacVolumeService` (watch on `/Volumes`, `DriveInfo`, `NSFileManager`); `DriveInfo.GetDrives()` inside the provider's volume key. `JsonStateStore` and `ErrorLog` touch only the app's own folders. Verdict: the promise holds. The provider, trash, native copy and metadata service are all inside scopes, and the listing and watch keep their scope open. Stage 4 needs additions, not a rewrite: a way to register grants from drops and open panels (the interface takes only a path string today), and replacements for `open`, `qlmanage` and the command line behind their existing interfaces.

**4. Strings.** Both resource files have the same 272 keys with matching placeholders. No user-visible literal in XAML or code beyond the product name "Tandem", glyphs, and the `path> command` echo. Exception messages reach the user only as secondary detail (error dialog, failure message, summary) or embedded in a localized sentence (`Destination_Unreadable`, `CommandLine_StartFailed`, `CommandLine_Failed`, `Error_Unexpected`); they are English from Core and from `strerror`, and system-localized from NSError. Gaps: I4 (the right localized sentence is not selected for native-copy errors) and the default application menu (Minor).

**5. Ordinal comparisons after the identity guard.** None still decides something destructive. `IsSameOrDescendantOf` (into-itself rejection, drop target check), the `kept` set in delete, the name comparison in `SplitAsync`, and selection restore all either compare paths from one consistently spelled enumeration or are backed by `IsSameEntryAsync` before anything is written or deleted. I traced both panels on one folder under different spellings: copy and move are rejected as "onto itself" by identity. Non-destructive leftovers: volume keys, normalization-only renames, the parser's own-name rule, `CanDrop` (all in Minor).

**6. Spec coverage.** Groups A, B and I have working UI for every bullet except: sorting by the permissions column (I7); the "compare" conflict answer of 5.3 (I7); pause inside a file (C1). Quick Look exists but its toggle is in doubt (I8). Weaker than a user would expect though the spec is silent: opening application bundles, free space after operations, the application menu (Minor). Section 7.2 against `KeyMap.Default`: every stage 1 row is present with both shortcuts (F3–F8 with Cmd+3–7 and Cmd+Backspace, Shift+F8 with Cmd+Option+Backspace, Shift+F6 with F2, Tab, Space, Cmd+Y, Cmd+T/W, Cmd+D, Cmd+Shift+., Cmd+R); Shift+Down is implemented outside the map; rows for search, quick view, pack/unpack and multi-rename belong to later stages and are absent, as expected. I found no clash with a system shortcut in the defaults.

**7. Dead code, duplication, large files.** Dead: see Minor. Duplication: the "watch active and passive panel, diff availability, raise CanExecuteChanged" block exists three times (`FileCommands`, `ShellCommands`, `MainViewModel.UpdateTabAvailability`); `~` expansion and absolute-path parsing three times (`SideViewModel.ParsePath`, `DestinationParser`, `CdCommandParser`); the "is this a real directory, not a link" probe three times in Platform.Mac; the `provider:volume` key three times in the operations; menu-opening code in `PanelView` and `FileListView`; a `WaitUntil` helper in five test files. Largest source files: `CopyEngine.cs` 685, `PanelViewModel.cs` 538 (+267 Selection), `InMemoryFileSystem.cs` 527 (a test double shipped in Core, by plan), `FileCommands.cs` 496, `CommandLineViewModel.cs` 484, `MainViewModel.cs` 429, `SideViewModel.cs` 397, `ShellCommands.cs` 395, `LocalFileSystemProvider.cs` 388. None is out of hand; `CopyEngine` is the one to split before stage 2 adds archive paths. Largest tests: `CommandLineTests` 739, `FileCommandsTests` 705, `MoveOperationTests` 676, `MainWindowTests` 641, `ShellCommandsTests` 640.

**8. Test suite health.** Healthy overall; the run was green in about 15 s. Most likely to flake:
- `LocalProviderTests.cs:95-101` — fixed sleeps around the real watcher, asserting 1–3 callbacks.
- `MacVolumeServiceTests.cs:74, 102` — debounce multiples.
- `PanelSelectionTests.cs:347-351` — `SelectAll` under 200 ms wall clock.
- `PanelListingTests.cs:401-420` — first rows under 1 s, full load under 3 s.
- `EntrySorterTests.cs:131-135` — sort under 1 s.
- `OperationsViewModelTests.cs:28, 94-97` — 30 ms settle and 150 ms throttle sleeps.
- `CopyControlTests.cs:56, 136` and `MoveOperationTests.cs:635` — `Thread.Sleep(150)` to widen a window.

Negative assertions bounded by a short sleep, which pass vacuously on a slow machine: `DialogOperationInteractionTests.cs:71, 117, 124, 151, 255`, `MainViewModelTests.cs:94, 110`, `ShellCommandsTests.cs:229, 258`, `ShellWindowTests.cs:242, 283`, `DragDropWindowTests.cs:189, 260`, `OperationQueueTests.cs:308`.
Misleading by scope: the 100k tests use the in-memory provider, not a disk; no test covers the engine with the real native copy (I9) or a real OS drag; Quick Look is tested only against a recording runner.

### Declined to judge

- Clipboard copy/paste of files (Cmd+C / Cmd+V) — not in the spec or plan for any stage; the genre uses F5/F6.
- Ejecting volumes from the volume list — spec asks only for mount tracking and free space.
- An application icon and "About" window — stage 4.
- Trimming, single-file publish, bundle size, and `.dll` signatures stored in xattrs — packaging is stage 4.
- Behaviour on case-sensitive APFS, on Intel, and on macOS 14–26 — only the development machine (macOS 27, arm64) could be exercised.
- Russian wording quality of the resource strings — parity was checked mechanically; wording was ruled per task.
- Accessibility (VoiceOver, keyboard reach of every control) — not in the spec.
- Performance on a real 100k-file directory and scrolling smoothness — needs the GUI; in the checklist.
- Security of the command line (arbitrary zsh) — by design; hidden in the sandbox build.
- Correctness of every `objc_msgSend` shape in `ObjC.cs` — sampled only; covered by passing interop tests on this machine.

### Assessment

**Ready for customer acceptance?** With fixes

**Reasoning:** The data-loss core is sound and I found no unconfirmed overwrite or delete, but pause does not work on a single large local file (the checklist's own step fails), quitting during an operation is silent, and locked files and folder metadata are mishandled. Fix C1 and I1–I6, rule on I7, and smoke-launch the bundle (I8, I9) before the customer starts.
