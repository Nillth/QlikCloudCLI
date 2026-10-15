# Shared Engine command contract

This directory contains the Engine implementation compiled into QlikCloudCLI and
QlikSenseCLI. Each repository carries the same source so it can build independently.
Changes to shared files must be copied to the peer repository and pass the same
Engine tests there. `commands.json` is the exact command inventory; classify commands
by full name, not by a prefix that can also match generated REST commands.

The platform adapter supplies only connection resolution and authenticated app
opening. The common implementation owns command parameters, output snapshots,
session/runspace ownership, timeout and cancellation handling, and cleanup. Both
modules pin QlikSense.NetSDK 16.11.0. Desktop and Core runtime assemblies are bundled
separately and selected on first Engine use; consumers install no extra SDK.

`Open-QSEApp -AppId <id> [-Connection <connection>] [-NoData]` returns a native
`Qlik.Engine.IApp`. `QlikClient` is an alias for `Connection`. Omission resolves
the module's existing default; `Connect-* -PassThru` does not replace that default.
Each open creates an isolated session, loading data unless `NoData` is selected.
Commands after opening use the exact `-App` object (or pipeline input), independent
of later default changes. All Engine commands accept `TimeoutSeconds`, default 120.

The 16-command initial surface covers lifecycle, script/layout/data-model reads,
expression diagnostics/evaluation, and seven list types. Script validation checks
the current script only. Expression output preserves `Text`, `Number`, `IsNumeric`.
List output is detached by default; `AsSdkObject` retains the native list until
caller disposal or app close. No persistent mutation wrappers are part of this
contract. Native SDK methods remain available and retain their own mutation and
lifetime semantics.

`Close-QSEApp` is idempotent; connection disposal closes owned app sessions too.
Cleanup does not roll back native mutations. A timeout or pipeline stop bounds
the local wait and can close an interrupted session; it does not prove that the
server cancelled an operation. Direct SDK calls must be sequenced by their caller.
Live objects cannot be used in another runspace or after serialization/remoting.

Command names intentionally match in both modules. When both are imported,
module-qualify Engine commands and keep each handle with its owning module.
Authentication, REST discovery, app IDs, and server capabilities remain platform
specific. The portable promise applies to the common Engine contract, not every
QRS/Cloud REST command or every server-version-specific SDK method.

Run the `tests/QlikEngine.Tests` suite against both built modules in fresh Windows
PowerShell 5.1 and PowerShell 7 processes. It uses an offline Engine fixture;
authenticated live deployment coverage must be recorded separately.
