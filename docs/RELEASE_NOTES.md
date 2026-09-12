# Dupe Finder 0.1.0 preview

First portable Windows x64 preview, built in C# with WinUI 3 / Windows App SDK 2.4.0.

- Native folder selection and expandable file tree with live hashing status.
- SHA-256 duplicate cards, last-copy protection, and animated removal.
- Confirmed folder collections with exact file previews and non-recursive empty-folder cleanup.
- Recycle Bin by default, optional confirmed permanent deletion, and optional local settings.
- Core, native Windows, and end-to-end portable UI regression checks.

Extract the entire ZIP and run DupeFinder.exe. No installer is required. This preview is unsigned.

Recycling uses a temporary sibling folder to protect against path replacement. Windows Restore returns the file to that folder; move it back to its original parent afterward. Links, junctions, and cloud placeholders are skipped. See the README for recovery details and supported use.

The SHA256SUMS.txt asset contains the ZIP checksum. Additional clean-machine and broad hardware testing is still planned before 1.0.
