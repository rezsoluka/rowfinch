# Rowfinch

[![Build and test](https://github.com/rezsoluka/rowfinch/actions/workflows/build.yml/badge.svg)](https://github.com/rezsoluka/rowfinch/actions/workflows/build.yml)

Your SQL workspace. An Ubuntu desktop client for Azure SQL Database, built with C#, .NET 10, Avalonia, and Microsoft.Data.SqlClient.

## Install on Ubuntu

The self-contained `.deb` includes .NET, a desktop launcher, and the Rowfinch icon. On Ubuntu 24.04 or newer, build and install the amd64 package from this directory. This requires the .NET 10 SDK and packaging tools; see the [packaging prerequisites](packaging/linux/README.md#build).

```bash
./scripts/build-deb.sh
sudo apt install ./artifacts/packages/rowfinch_0.2.0~beta.1_amd64.deb
rowfinch
```

Launch **Rowfinch** from the application menu, or run `rowfinch --demo` for sample data. Remove it with `sudo apt remove rowfinch`; saved profiles and remembered passwords remain intact.

Build a package with `./scripts/build-deb.sh`. See [Ubuntu packaging](packaging/linux/README.md) for build prerequisites, upgrade/removal instructions, clean-environment tests, and the live Azure checklist.

## Run

Install the .NET 10 SDK, clone the repository, then run:

```bash
git clone https://github.com/rezsoluka/rowfinch.git
cd rowfinch
dotnet restore Rowfinch.slnx
dotnet run --project src/Rowfinch.Desktop
```

On an Ubuntu Wayland desktop, the default Avalonia Linux backend uses XWayland. A working graphical session and the usual Avalonia Linux native dependencies are required. See [Avalonia's Linux guide](https://docs.avaloniaui.net/docs/platform-specific-guides/linux).

Choose **Try demo** to explore without connecting to a database. Demo mode returns fixed sample data; it does not interpret or execute your SQL.

To start directly in demo mode:

```bash
dotnet run --project src/Rowfinch.Desktop -- --demo
```

## Implemented

- Multiple query tabs with independent SQL sessions, SQL highlighting, selection execution, `Ctrl+Enter`/`F5`, and SQL file open/save.
- Per-tab editor undo/selection, results, messages, row limits, timeouts, execution status, cancellation, and transaction state.
- Save/discard/cancel decisions when closing unsaved tabs, and confirmation before cancelling operations or closing open transactions.
- SQL-password and Microsoft Entra browser authentication.
- Encrypted connections with certificate validation; no automatic reconnection or SQL retries.
- Saved connection profiles, with no passwords or tokens in the profile file.
- Optional SQL-password storage in Ubuntu's desktop keyring, just-in-time retrieval, and a Forget saved password action.
- A persistent database session, SQL-aware `GO` batches (including `GO n`, with a repeat limit of 100), and multiple result sets.
- Vertical Connections/Objects sidebar tabs, combined object-type and text filters, lazy column metadata, and selectable columns.
- Object actions for top-100 previews, SELECT/INSERT/UPDATE templates from selected columns, SQL definitions, copying qualified names, and individual column refresh.
- Cancellation, query timeouts, affected-row counts, SQL error messages, and transaction-state display with commit/rollback actions.
- Virtualized results, selectable cell text, copying selected rows, and CSV export of displayed rows.
- A clearly labeled demo workspace and automated tests that do not require database credentials.
- A self-contained Ubuntu `.deb`, desktop launcher, application icon, and package lifecycle checks.

Each SQL session remains open between runs, so temporary tables and explicit transactions persist. Disconnecting closes the physical connection and rolls back any outstanding transaction. Cancellation does **not** imply rollback; Rowfinch refreshes `@@TRANCOUNT` after execution and reports an unknown state if it cannot verify it. Closing the application waits for a running operation to cancel before disposing its connection.

## Query tabs

Choose **+ New query** or press `Ctrl+T` to create a tab. New tabs copy connection settings from the current tab and start disconnected; passwords are never copied. Connect each tab explicitly. Connections, temporary tables, transactions, results, and running operations belong only to that tab. Switching tabs does not interrupt execution. The tab subtitle shows its database or disconnected/demo state, and the header shows the active tab's full connection target.

- `Ctrl+T`: new query tab.
- `Ctrl+W`: close the current tab.
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: switch forward/backward.
- `Ctrl+Enter` / `F5`: execute the active tab's selection or script.
- `Escape`: cancel the active tab's running operation.
- `Ctrl+O`: open SQL files in new tabs, preserving existing documents.
- `Ctrl+S`: save the active tab's SQL.

Closing an unsaved tab offers **Save and close**, **Close without saving**, and **Cancel**. Cancelling the save dialog keeps the tab open. Closing the application collects every required decision before cancelling any queries or disposing any sessions. If you cancel a later tab's close prompt, all tabs and sessions remain open (documents already saved remain saved). Closing the final tab creates a new, empty disconnected document. Tabs are not restored across application restarts yet.

Connection profiles are stored at `$XDG_CONFIG_HOME/rowfinch/connections.json`, or `~/.config/rowfinch/connections.json` when XDG_CONFIG_HOME is unset or relative. The file contains connection metadata and the remember-password preference, never the password or access token. Existing profile files load with remembering disabled by default. Entra authentication requires your user to have database access and the server to be configured for it. The Microsoft identity provider handles browser sign-in.

## Sidebar and object browser

The left sidebar has vertical **Connections** and **Objects** tabs. Each page uses the full sidebar height, with its own scrolling list. Connection details are collapsible; when saved profiles are available, the list opens with details collapsed. Selecting a profile or choosing **+ New** opens the details. Connect/Disconnect stay visible below the form. Successful connection or demo startup opens Objects. Each query tab keeps its own sidebar page, filters, metadata, and column choices.

In Objects, choose **All** or combine **Tables**, **Views**, **Procedures**, and **Functions**. The text filter applies within the chosen types. Clearing the last type selection returns to All. Filtering preserves expanded objects and their selected columns.

Expand a table or view to load its columns. Each column shows its SQL type, nullability, primary-key position (including composite keys), and identity/computed/hidden/generated flags when applicable. Visible columns start selected; toggle their checkboxes or use **Select all** / **Unselect all** below the object name. Hidden columns are excluded from selection. **Refresh columns** reloads that object's metadata and preserves choices by column name; newly discovered visible columns start selected. The main **Refresh** reloads the whole object list and resets loaded metadata and column choices.

Right-click an object or one of its columns for the available actions:

- **Select top 100** runs a preview in the source tab's existing session, preserving the editor. It previews all visible columns regardless of the checkbox selection; the display row limit still applies. No ordering is implied.
- **Generate SELECT in new tab** includes only the checked visible columns, in schema order.
- **Generate INSERT in new tab** includes checked writable columns with `<value_n>` placeholders. Identity, computed, hidden, generated-always, and rowversion columns are omitted. Unselected columns rely on database defaults or nullability.
- **Generate UPDATE in new tab** places checked writable non-key columns in SET. All primary-key columns form the WHERE predicate, including keys that were not checked. When no primary key is available, the template requires a `<row_filter>` predicate. Key and server-managed columns are excluded from SET.
- **Open definition in new tab** reads a view, procedure, or function's SQL definition. Double-clicking a procedure or function opens its definition too.
- **Copy qualified name** copies the escaped `[schema].[object]` name.

Replace every `<...>` placeholder before running an INSERT or UPDATE template. These are draft scripts; generation never executes them. A view must be updatable for a write template to succeed. Selecting no columns, or only columns unsuitable for the requested write, produces guidance without opening an empty script.

Generated SQL and definitions open as unsaved documents with the source tab's connection settings and no copied password. They start disconnected; connect explicitly before running them. Reading metadata preserves the original SQL and results, uses the current database session, and supports cancellation. Other tabs remain usable while metadata loads.

Objects and columns reflect the connected user's metadata visibility. Definitions may require `VIEW DEFINITION` permission; encrypted modules and CLR objects have no readable SQL definition. Missing metadata is reported in Messages with a retry/refresh path. See Microsoft's [column catalog](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-columns-transact-sql), [rowversion](https://learn.microsoft.com/en-us/sql/t-sql/data-types/rowversion-transact-sql), and [SQL module catalog](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-modules-transact-sql) documentation.

## Password keyring

For SQL authentication, check **Remember password** and connect with your password. After authentication succeeds, Rowfinch saves the connection profile and stores the password in the desktop keyring. Saving a profile alone records the preference without storing an unverified password.

For subsequent connections or new tabs using that saved profile, leave the password field blank. Rowfinch requests the password from the keyring only when you connect; it does not populate the password field when you select a profile. An explicitly entered password takes precedence and updates the saved password after successful authentication.

Credentials are matched by profile ID plus the exact database and user settings and a case-normalized server name. Editing the target cannot reuse the old target's password automatically. New profiles have separate credential identities even when their settings are identical.

**Forget saved password** removes this profile's Rowfinch password entries, including entries for its former targets, and disables remembering in other open tabs using the same profile. It leaves database sessions connected. Forgetting is coordinated with pending saves so an in-flight connection cannot silently restore the forgotten password. A failed or cancelled removal is reported as unconfirmed. Unchecking **Remember password** stops automatic retrieval/saving for that connection; use **Forget saved password** to remove existing keyring entries.

The Linux backend uses [libsecret](https://gnome.pages.gitlab.gnome.org/libsecret/) directly. It requires the `libsecret-1-0` runtime and a Secret Service provider on the desktop session bus, such as GNOME Keyring. No `secret-tool` subprocess or password command-line arguments are used. Keyring work runs outside the UI thread, supports cancellation, and times out after 45 seconds. A locked, unavailable, or rejected keyring request produces guidance to unlock it or enter a session-only password. If remembering fails after login, the database connection stays open and the UI reports that password storage was not confirmed. Backend exception details and secret values are excluded from keyring diagnostics. Entra sign-in does not use this SQL-password store.

The row setting is a **display limit across all results**, not a server-side query limit. Extra rows are drained to preserve later result sets and statement execution; they are not retained or exported. Use SQL `TOP` or filters to reduce transferred data. CSV exports use UTF-8, invariant numeric formatting, and empty fields for SQL NULL. The grid renders null as `NULL`.

SQLCMD directives such as `:r`, `:setvar`, and shell commands are rejected before any batch executes. Full SQLCMD execution is planned as a separate script mode.

## Verify

```bash
dotnet build Rowfinch.slnx --no-restore
dotnet test Rowfinch.slnx --no-build
```

Tests cover quoted/multiline SQL and nested comments around `GO`, directive preflight, connection-string escaping and TLS options, credential-free profile persistence, CSV escaping, cancellation/transaction recovery, independent concurrent sessions and cancellation, closing background tabs, cancelling application shutdown, concurrent profile saves, and editor/result preservation with the Avalonia headless backend. Object-browser tests cover lazy loading and caching, per-object refresh, metadata failure/retry, cancellation and close, escaped SQL generation, background tab changes, stale object actions, and the actual tree/context menu bindings.

Keyring regression tests use an in-memory credential store and verify authentication-before-storage, profile-file privacy, exact-target matching, session-only fallback, failure-message redaction, and forgetting across tabs and concurrent operations. An additional opt-in integration test creates, reads, updates, and removes only a synthetic credential under a new random Rowfinch profile ID:

```bash
dotnet test Rowfinch.slnx --filter FullyQualifiedName~NativeKeyringStoresLooksUpUpdatesAndForgetsOnlyItsTestProfile --environment ROWFINCH_KEYRING_TEST=1
```

A live Azure connection and SELECT have been manually verified on the target Ubuntu desktop. The new metadata queries and object actions still need a live Azure smoke test; automated metadata tests use controlled sessions and demo mode. Entra MFA and server cancellation still require separate integration checks. No database credentials are bundled, and automated tests do not access Azure.

## Next milestones

1. Validate Entra MFA and real-server cancellation on the target Ubuntu desktop.
2. Smoke-test object metadata, definitions, and previews against Azure with the intended database permissions.
3. Validate the installed launcher and Entra sign-in on a full Ubuntu desktop; test ARM64 packaging when hardware is available.
4. Add optional query history and SQLCMD script execution.

The core models, SQL batch parser, and export code live in `Rowfinch.Core`; connection handling and profile persistence live in `Rowfinch.Infrastructure`; Avalonia views and view models live in `Rowfinch.Desktop`.

## Versioning

The application version is defined once in `Directory.Build.props`. The header badge, `rowfinch --version`, and default Ubuntu package version all use that value. Hover over the badge to see the full assembly build version, including SDK-supplied commit metadata when available. Prerelease labels such as `0.2.0-beta.1` stay visible.

We use [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While Rowfinch is in initial `0.x` development, minor bumps cover substantial features or compatibility changes; patch bumps cover fixes and small UI improvements. Version `1.0.0` will mark the first stable compatibility contract. See [CHANGELOG.md](CHANGELOG.md) for changes.

For a release, update the central version and changelog, build/test the app and package, and tag the release commit as `vX.Y.Z` (for example, `v0.1.1`). Keep released tags and packages unchanged; publish a new version for subsequent changes. Local builds can override the version with `dotnet build -p:Version=0.2.0-beta.1` or `./scripts/build-deb.sh --version 0.2.0-beta.1` without editing the source version. Debian packages represent the prerelease separator as `~`.

## Contributing and license

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for development and verification steps.

Rowfinch is licensed under the [MIT License](LICENSE). Third-party components retain their respective licenses; Ubuntu packages include a dependency inventory and available third-party notices.
