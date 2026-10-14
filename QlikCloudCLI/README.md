# PowerShell module project

This directory builds the compiled QlikCloudCLI distribution. Customers install a Gallery package or ZIP and use the [consumer README](docs/consumer/README.md), [operator guide](docs/consumer/HOW-TO.md), and [LLM scripting contract](docs/consumer/LLM.txt). The build copies those documents into each module package.

From the repository root:

```powershell
pwsh -NoProfile -File ./QlikCloudCLI/Build.ps1 -Configuration Release -Test -Package
```

The build prints the staged manifest and package paths. Both DLLs target .NET Standard 2.0, and the Windows test step runs in clean Windows PowerShell 5.1 and PowerShell 7 processes. Dependency DLLs remain separate in `bin`; the package does not use Fody/Costura embedding.

See [maintainer instructions](../docs/MAINTAINING.md) for development requirements, schema updates, release versioning, signing, and publication. The generated `commands.json` and `COMMANDS.md` describe the exact command inventory for each package; counts can change with the schema snapshot.
