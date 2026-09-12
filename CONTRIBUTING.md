# Contributing

Open an issue describing the user-visible problem before a substantial change. Small fixes are welcome as pull requests.

Use C# and the existing WinUI/Core separation. Run both executable test projects and a Release build on Windows. Add regression coverage for changes to scan grouping or deletion policy. Never test destructive behavior against personal files.

Deletion changes must preserve these invariants: only explicitly scanned duplicates are candidates; at least one unchanged copy is locked throughout an action; each target is verified again; directory removal is non-recursive; unsupported recycling must fail safely. Report partial completion honestly.

Do not commit generated builds, settings, scan results, personal paths, or credentials. Follow the MIT license.
