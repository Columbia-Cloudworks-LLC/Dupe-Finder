# Dupe Finder

A small, portable Windows app that finds duplicate files by SHA-256 and helps you remove extra copies with confidence. Built in C# with WinUI 3, by Columbia Cloudworks LLC. Free and open source under the MIT license.

## Run

Download the Windows x64 ZIP from [Releases](https://github.com/Columbia-Cloudworks-LLC/Dupe-Finder/releases), extract the **entire ZIP** into a folder, and open `DupeFinder.exe`. No installer, administrator rights, .NET installation, or Windows App SDK installation is required. Keep the runtime files beside the EXE.

Requires Windows 10 version 2004 or later, or Windows 11 (x64). The initial release is unsigned. ARM64 release packaging is not yet provided.

1. Choose a folder with the native Windows picker.
2. Leave **Delete permanently** off to use the Recycle Bin. Enable **Remember settings** if you want this folder and these preferences saved.
3. Click **OK**, then **Scan**. The tree shows spinning indicators during hashing, green indicators for success, and red indicators with error tooltips.
4. Review cards of identical files. Use a red trash icon to remove an extra copy. The final copy cannot be deleted from its card.
5. Resolved cards stay visible with a green checkmark. Deleted rows turn red, strike through, remain for one second, then fade and collapse.

The app reads only the selected tree. It does not search the rest of your device, upload file contents, or use telemetry. The right pane stays empty until a scan completes. You can cancel a scan and start again. Deletion is disabled during scanning and during another deletion.

## Folder collections

A folder is offered as a collection only when **every file has a verified duplicate outside that entire folder**. Copies inside the candidate alone do not qualify.

- **Matching tree** means another qualifying directory has the same relative names, content hashes, and directory structure.
- **Copies in other folders** means every file has a copy somewhere else within the selected scan, but those copies do not form an identical tree.

Expand **Folder collections** to review a candidate. The confirmation shows every selected file and an example surviving copy. The app rehashes and locks a survivor for each distinct content hash, verifies every target before starting, then deletes only those explicit files. It never recursively deletes a selected directory. Empty directories from the scanned manifest are removed individually afterward; any new or unscanned content prevents removal. Large collections may take time to reverify.

## Safety and recovery

- Recycle Bin is the default. If Windows cannot recycle a file, the operation is aborted, not silently changed to permanent deletion.
- Permanent deletion always requires confirmation. Collection deletion always requires confirmation, in either mode.
- Changed files, missing survivors, locked files, and access failures stop the action. A partly completed collection reports completed files and retains the rest.
- Junctions, symbolic links, and reparse points (including cloud placeholders) are skipped. Select a regular local folder; copy cloud files to a local non-synced folder to scan them. Hard links may appear as identical content but deletion can be refused because the surviving file is locked.
- SHA-256 compares the primary file data stream. Names, timestamps, permissions, and alternate data streams are not used to establish equality. Review metadata-sensitive files separately.
- Recycling first moves the verified file by handle into a unique `.dupefinder-...` sibling folder, retaining its filename. This prevents the shell from deleting a replacement at the old path. **Recycle Bin Restore returns it to that temporary folder**, from which you can move it to its original parent. A failed recycle restores the original path; if restoration is blocked, the app displays the retained file's exact location. A crash or power failure during staging can also leave that temporary folder; the content remains there. Do not remove such folders without checking them.
- Recycle Bin files continue using disk space until the Bin is emptied. This app does not empty it.
- No filesystem tool can promise protection against a malicious administrator, disk failure, or other software deliberately tampering with its private staging files. Keep backups of important data.

## Settings

When **Remember settings** is enabled, clicking **OK** saves the selected folder and toggles to `%LOCALAPPDATA%\Columbia Cloudworks\Dupe Finder\settings.json`. Disabling it and clicking OK deletes that settings file. Corrupt or missing settings fall back to safe defaults. Files and scan results are never cached.

## Build and test

Install the .NET 10 SDK and Visual Studio's Windows application development tools. The project pins stable `Microsoft.WindowsAppSDK` **2.4.0** (WinUI 3) and Windows SDK build tools **10.0.28000.2526**.

```powershell
dotnet build src/DupeFinder.App/DupeFinder.App.csproj -p:Platform=x64
dotnet run --project tests/DupeFinder.Tests
dotnet run --project tests/DupeFinder.WindowsTests
./scripts/publish.ps1
```

The native tests create and delete their own temporary fixtures, including a Recycle Bin entry. Publishing also runs an end-to-end WinUI fixture test before producing the ZIP. They never operate on a user-selected directory. The core tests cover last-copy protection, changes since scanning, batch preflight, partial failures, concurrency, survivor locks, folder equivalence, new unscanned files, cancellation, and settings.

`artifacts/DupeFinder-win-x64.zip` is the portable distribution. GitHub Actions runs the tests and produces a build artifact for each push and PR; version tags publish release assets.

## Layout

- `src/DupeFinder.Core`: scanner, duplicate/folder grouping, deletion policy, settings.
- `src/DupeFinder.App`: native WinUI interface, animations, Windows handle and shell interop.
- `tests`: executable regression suites with no third-party test runner dependency.
- `docs/VALIDATION.md`: verification results and remaining platform checks.

See [CONTRIBUTING.md](CONTRIBUTING.md) for contributions and [SECURITY.md](SECURITY.md) for reporting a file-safety issue.
