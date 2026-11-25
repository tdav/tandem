# Native AOT publishing

Both scripts publish `src/Tandem.App` in Release as Native AOT: one native executable plus the native Avalonia,
Skia, HarfBuzz (and on Windows ANGLE) libraries, no .NET runtime to install. Native AOT compiles with the host's
own toolchain and does not cross-compile between operating systems, so the Windows build runs on Windows and the
macOS build on macOS. Outputs go to `publish/` and `dist/` (both git-ignored); each run recreates them.

## Windows — `build\publish-windows.bat`

```bat
build\publish-windows.bat            :: win-x64 (default)
build\publish-windows.bat arm64      :: win-arm64
build\publish-windows.bat all        :: both
```

Result: `publish\win-<arch>\Tandem.exe` with `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` and `av_libglesv2.dll`
beside it (the `.pdb` files of the native libraries are removed). Copy the whole folder. Exit code 0 on success,
1 on any error.

Needs the .NET 11 SDK and the MSVC linker: Visual Studio 2022+ or "Build Tools for Visual Studio" with the workload
**Desktop development with C++**; for arm64 also the component **MSVC … ARM64/ARM64EC build tools**. The script
finds them with `vswhere` the same way the ILCompiler does and says what to install when they are missing. When an
MSVC toolset is on disk but the workload is not registered with the installer (a partial install), it points
`PATH`/`LIB` at that linker, its CRT and the newest Windows SDK and publishes with `IlcUseEnvironmentalTools=true`.

## macOS — `build/publish-macos.sh`

```sh
build/publish-macos.sh               # universal (default): arm64 + x64 joined with lipo
build/publish-macos.sh arm64         # Apple silicon only
build/publish-macos.sh x64           # Intel only
build/publish-macos.sh all           # all three bundles
```

Result: `dist/macos-<arm64|x64|universal>/Tandem.app` and `dist/Tandem-<version>-macOS-<arch>.zip`; the version is
`CFBundleShortVersionString` from `build/Info.plist`. The bundle gets `build/Info.plist`, `build/Tandem.icns` and
`build/ru.lproj`. For the universal bundle the executable is joined with `lipo -create`; native libraries that NuGet
already ships universal are copied as they are.

Signing: the "Tandem Local Dev" identity if the keychain has it (`build/create-dev-cert.sh`), otherwise ad-hoc
(`codesign --sign -`); `SIGN_IDENTITY=<name or ->` overrides. The script checks `codesign --verify --deep --strict`
and the architectures of the executable. The bundles are not notarized, so Gatekeeper asks before the first launch
of a downloaded zip (right-click → Open).

Needs macOS with the .NET 11 SDK and the Xcode command line tools (`xcode-select --install`). Either kind of Mac
builds both architectures.

The older scripts stay as they were: `build/make-aot-app.sh` (Native AOT, arm64, into `dist/Tandem.app`),
`build/make-app.sh` (ReadyToRun) and `build/verify-app.sh` (checks the `make-app.sh` bundle).
