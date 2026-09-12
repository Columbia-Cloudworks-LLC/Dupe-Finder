# Validation

Verified on Windows 11 x64 with .NET SDK 10.0.401 and Windows App SDK 2.4.0.

## Automated coverage

- 13 core regression scenarios: content hashes, empty files, folder coverage, matching trees, scattered copies, final-copy protection, modified/missing targets and survivors, batch preflight, partial failures, locked survivors, new unscanned files, serialized overlapping requests, cancellation, corrupt settings, and path boundaries.
- Native Windows checks: permanent deletion by verified handle, recycling through typed shell COM interfaces, and rejection of a non-recyclable operation in the pre-delete callback.
- Release UI integration: launch from the portable publish directory, load a generated folder, confirm initially empty results, scan, render duplicate cards and matching folders, recycle a generated file, await the actual strike/fade/collapse animations, retain the resolved card, and remove its last-copy deletion controls.

The UI integration check creates its own unique temporary fixture and isolated settings store. It does not scan or delete personal files. The native and UI tests leave their recycled fixture files in the Recycle Bin.

## Release regressions fixed

The generic .NET publish pipeline omitted the generated application PRI. The project now includes it explicitly, and the portable UI test detects its absence and startup failure.

Windows can return an aggregate shell completion error after confirming a successful per-file recycle. Completion now requires the successful per-file recycle receipt and absence of the source; a successful receipt remains authoritative if later shell bookkeeping fails. The callback refuses a delete that is not marked recyclable.

The hosted Windows runner exposed a missing explicit terminator in the native rename buffer. The buffer now includes a terminator and sufficient space; both core and native deletion suites pass on that runner.

## Manual inspection

The startup window and current native Windows folder picker were visually inspected. The executable integration check covers the main scan and deletion view behavior.

## Additional platform testing

Before a stable 1.0 release, test clean Windows 10 and Windows 11 machines without development tools, removable drives with unsupported or disabled Recycle Bins, very large trees, assistive technology, multiple DPI settings, and abrupt process/power failure during staging. The current release is an unsigned x64 preview; it is not represented as having passed those additional platform checks.
