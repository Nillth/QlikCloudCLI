# Engine contract tests

These tests exercise a built PowerShell module with the real bundled Qlik .NET
SDK against a local WebSocket Engine fixture. They require Windows PowerShell
5.1, PowerShell 7, and the .NET 8 SDK on the development/test machine. Module
consumers do not need the .NET SDK or this fixture.

```powershell
./tests/QlikEngine.Tests/Invoke-EngineTests.ps1 `
    -ModulePath '<built-module>/<module-name>.psd1' `
    -OutputDirectory './artifacts/engine-tests'
```

Use the packaged `QlikCloudCLI.psd1` or `QlikSenseCLI.psd1` manifest.

To compare both module implementations, add `-PeerModulePath` pointing to the
other built module's manifest. Add `-PeerRepositoryRoot` to check that the
vendored `shared/Qlik.Engine.PowerShell` sources are byte-identical too. The
runner executes each module in fresh Desktop and Core processes, compares all
16 commands' parameters, aliases, parameter sets, pipeline binding, defaults
and output metadata, and writes JSON reports. `-HostPath` can select specific
PowerShell executables. The default runs both editions; a missing executable
fails the run.

When both module paths are supplied, additional fresh processes import them in
both orders and verify module-qualified open/read/close commands, rejection of
foreign app handles, and compatible SDK assembly loading.

The same `Common-EngineScript.ps1` runs for both modules. It opens a native SDK
app, calls its direct `GetScript()` method, exercises all inspection commands,
and closes the app in `finally`. Additional cases cover session isolation,
connection replacement, `-NoData`, list snapshots and native lists, explicit
disconnect, repeated disposal, error propagation, timeouts, pipeline
cancellation, pending-open cleanup and runspace teardown. A deterministic
provider fixture covers identity/ownership checks and concurrency races that
are difficult to reproduce reliably over a socket.

The server binds only to an ephemeral IPv4 loopback port and uses invented
apps, values and credentials. Cloud tests use the actual API-key connector and
Engine adapter. Sense tests construct a connection configured for the fixture,
then exercise the actual Engine adapter; they skip the remote QRS handshake.
These tests do not validate real tenant/server authentication, permissions,
network policies, every Engine response shape, or server-version compatibility.
No remote tenant is contacted and no persistent Engine mutation command is
implemented by the fixture.

The runner also creates disposable package copies without Engine DLLs and
verifies that module import, command discovery and connection construction work
until the first Engine command reports the missing capability. Copies and
fixture traffic remain under the chosen output directory for diagnosis. The
fixture process is stopped in `finally`.
