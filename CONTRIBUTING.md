# Contributing to Rowfinch

Issues and pull requests are welcome. Describe the concrete problem or behavior you want to change. For a larger feature, open an issue first so its scope can be discussed.

## Development

Use the .NET 10 SDK on Ubuntu. From the repository root:

```bash
dotnet restore Rowfinch.slnx
dotnet build Rowfinch.slnx --no-restore
dotnet test Rowfinch.slnx --no-build
dotnet run --project src/Rowfinch.Desktop -- --demo
```

Demo mode and the default tests work without database credentials. The native-keyring test is opt-in; see the main README before running it. See `packaging/linux/README.md` for package tests and the live Azure checklist.

Keep changes focused. Add or update tests when behavior changes in SQL parsing, connections, credentials, tab/session isolation, or metadata loading. Describe what changed and how you verified it in your pull request.

## Reporting problems

Include your Ubuntu and Rowfinch versions, reproduction steps, expected behavior, and relevant error messages. Remove passwords, access tokens, connection strings, and private server/database details from logs and screenshots before posting them. Use synthetic data in examples and tests.
