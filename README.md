# Rowfinch

[![Build and test](https://github.com/rezsoluka/rowfinch/actions/workflows/build.yml/badge.svg)](https://github.com/rezsoluka/rowfinch/actions/workflows/build.yml)

Your SQL workspace. An Ubuntu desktop client for Azure SQL Database, built with C#, .NET 10, Avalonia, and Microsoft.Data.SqlClient.

## Install on Ubuntu

The self-contained `.deb` includes .NET, a desktop launcher, and the Rowfinch icon. On Ubuntu 24.04 or newer, build and install the amd64 package from this directory. This requires the .NET 10 SDK and packaging tools; see the [packaging prerequisites](packaging/linux/README.md#build).

```bash
./scripts/build-deb.sh
sudo apt install ./artifacts/packages/rowfinch_0.3.0~beta.2_amd64.deb
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
- Virtualized results, selectable cell text, copying selected rows, and CSV/JSON export of displayed rows.
- C# classes with data annotations and JSON objects generated from SELECT result metadata, with preview, copy, and save actions.
- Optional ChatGPT SQL assistant with browser sign-in, request preview, opt-in schema inspection, approved read-only queries, and SQL suggestions for review.
- A first-launch safety/privacy notice, with About / Privacy always available in the header.
- A clearly labeled demo workspace and automated tests that do not require database credentials.
- A self-contained Ubuntu `.deb`, desktop launcher, application icon, and package lifecycle checks.

Each SQL session remains open between runs, so temporary tables and explicit transactions persist. Disconnecting closes the physical connection and rolls back any outstanding transaction. Cancellation does **not** imply rollback; Rowfinch refreshes `@@TRANCOUNT` after execution and reports an unknown state if it cannot verify it. Closing the application waits for a running operation to cancel before disposing its connection.

## Query tabs

Rowfinch is beta software. SQL you execute can change or permanently delete data. Review scripts, maintain appropriate backups, and protect exported/copied results. The first normal launch displays a safety/privacy notice before opening connections; its acknowledgement is saved locally. **About / Privacy** reopens the notice. `--demo` skips the first-launch notice for demonstrations and automated package checks. See [Safety and privacy](PRIVACY.md) and the [MIT license](LICENSE).

Choose **+ New query** or press `Ctrl+T` to create a tab. When the current tab is connected, new tabs automatically connect to the same server and database with the same authentication. Generated SQL, opened definitions, and opened SQL files use their source tab's connection too. Each tab opens its own database session, so temporary tables, transactions, results, and running operations remain independent. A disconnected source copies only connection settings; a demo source creates another demo tab. New documents never run automatically. Switching tabs does not interrupt execution. The tab subtitle shows its database or disconnected/demo state, and the header shows the active tab's full connection target.

SQL passwords for active connections are retained privately in memory so additional tabs can connect without prompting. They are not placed in new tabs' password fields or saved to profiles. Closing or disconnecting a tab releases its reference to those credentials; other tabs already using the same connection settings continue independently. Inheriting a connection does not retrieve or rewrite the desktop keyring. If an automatic connection fails, the SQL document stays open, Messages shows the failure, and you can retry through Connect.

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

Generated SQL and definitions open as unsaved documents and automatically connect using the source tab's active connection. Generation never executes the new document; once it finishes connecting, use Run when ready. Reading metadata preserves the original SQL and results, uses the current database session, and supports cancellation. Other tabs remain usable while metadata loads.

Objects and columns reflect the connected user's metadata visibility. Definitions may require `VIEW DEFINITION` permission; encrypted modules and CLR objects have no readable SQL definition. Missing metadata is reported in Messages with a retry/refresh path. See Microsoft's [column catalog](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-columns-transact-sql), [rowversion](https://learn.microsoft.com/en-us/sql/t-sql/data-types/rowversion-transact-sql), and [SQL module catalog](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-modules-transact-sql) documentation.

## Exporting results and generating models

Run a SELECT and choose its result set in the Results dropdown, then open **Export…**:

- **Rows as CSV…** and **Rows as JSON…** save all displayed rows of that result set. The display row limit applies; export does not execute the query again or fetch additional rows. JSON uses an array of objects, retaining numbers, booleans, and SQL NULL as JSON null. Dates use ISO strings, binary values use Base64 strings, and SQL time values use duration strings. Non-finite floating-point values and unsupported provider-specific values use their invariant display strings. An empty result exports as `[]`.
- **C# class with annotations…** opens a preview with an editable class name. Properties use SQL/CLR types and reported nullability, with `[Column]`, `[JsonPropertyName]`, `[Required]`, `[StringLength]`, `[MaxLength]`, and identity/timestamp annotations where metadata supports them. Decimal precision/scale is retained in the column type. Unknown nullability permits null. Keys and relationships are not guessed from names: a reported single-column result key can use `[Key]`; composite keys require explicit ORM configuration. Normal query execution does not request extra key-discovery metadata, so key annotations may be absent. The generated class describes the SELECT projection and needs review before use as a database entity.
- **JSON object…** opens a single object using the first displayed row. Uncheck **Use values from the first displayed row** for a template with null property values. Queries with no rows also produce this template.

Both generation options offer **Copy** and **Save…**, work with zero-row results, and preserve the SQL editor and existing results. Generation uses the selected result set from the most recent execution, so run the query again after editing its SELECT. It does not parse or execute editor text. Duplicate/unnamed result columns receive unique JSON names; C# identifiers are normalized and made unique, with JSON mappings matching the export. Column aliases remain in the column annotations. Generated C# targets nullable-aware .NET projects and uses only framework data annotation and JSON attributes.

## ChatGPT SQL assistant

Open **Assistant** in the header. Its window belongs to the query tab from which it was opened; changing query tabs does not change that source. Opening Assistant from another query closes the previous assistant, cancels any pending request, and starts with sharing off for the new source. The assistant offers SQL explanations, troubleshooting, and suggestions. Database access starts off; you can separately enable live schema inspection and approved read-only queries.

1. Choose **Continue with ChatGPT** and complete sign-in in your system browser. Eligible accounts can authorize ChatGPT plan usage without an API key. Account/workspace limits and current model availability apply. **Add account** creates another registration; the saved-account picker switches accounts. Use **Manage usage** to review access and limits in ChatGPT settings.
2. Choose an available model and enter a question in **Chat**, which keeps the conversation and streamed answers together. Follow-ups reuse earlier completed questions, answers, shared context and approved query results. Open **Context** to choose sharing and review the request preview. Sharing **Editor SQL**, **Loaded schema**, and **First 5 displayed rows** is off by default. Selecting an option captures that context; **Refresh context** captures newer data. Schema sharing includes up to 50 currently visible objects and already-loaded visible columns, without querying the database. Long row values are truncated, and requests over 60,000 characters require a smaller selection.
3. Review **Request preview** in **Context**, then Send. The assistant returns to **Chat** to show the answer alongside your question. That snapshot is sent with Rowfinch's SQL-assistance instructions, even if the source SQL changes afterwards. Separately enabled database access can share additional schema and approved query results during this question. Earlier completed exchanges and their previously shared data are also sent with follow-ups. Expand **Earlier messages and shared data** in Context to review that history. Turning off a sharing option prevents new snapshots from being attached; it does not remove data already shared in this chat. Review the preview for credentials embedded in SQL or other sensitive information. Password fields, connection credentials, and server connection strings are never automatically included.
4. Read the streamed answer, copy the latest answer, or choose **Open latest SQL in new tab** for completed fenced SQL suggestions from the latest answer. The new document is unsaved and inherits the source connection; it never runs automatically. Review it before Run. Cancelled, failed, or incomplete answers cannot open a suggestion through that action.

In **Context → Connection notes**, describe tables, relationships, business terms or rules once, then choose **Save notes**. The notes reload for future chats using the same server/database/user/authentication, including new query tabs that inherit that target. **New chat** keeps these notes. They are stored locally in `assistant-contexts.json`, unencrypted with owner-only permissions; do not put credentials in them. Checked **Include connection notes** attaches the current notes to each request preview. Uncheck it to omit them from new messages; history already sent still contains its earlier snapshots. Clear the notes and Save notes to remove that connection's saved context. Editing a connection target loads its own notes rather than reusing the previous target's context. Demo has no persistent notes.

**Open latest SQL in new tab** uses fenced SQL in the latest completed answer, or falls back to the most recent database query from the completed conversation. This also works when the assistant runs a query and replies with just a summary. SQL opens for review without running. Complete requested SQL remains available for manual review after a rejected query or failed answer; partial answer text is never treated as a completed SQL suggestion. If no SQL is available yet, the disabled button's tooltip explains this.

Use **Save chat** to save completed exchanges and your draft to a `.rowfinch-chat.json` file. **Open chat** restores the messages, selected model when available, draft, and follow-up context. Opening a file replaces the current chat after confirmation; save your current chat first if you want to retain it. Invalid files leave the current chat intact.

Chat files are **unencrypted** and include previously shared SQL/schema, bounded query results and opaque model state needed for follow-ups. Keep them private and open files you trust. Rowfinch does not add SQL passwords, OAuth tokens, connection strings, live sessions or access grants. Local files are created with owner-only permissions. No chats are saved automatically, and closing an unsaved chat still discards it.

You can view a saved chat without signing in. To send follow-ups, select the original saved ChatGPT sign-in (files are tied to that local account registration). Opening the file neither sends its content nor executes queries. The next **Send** shares the restored history with OpenAI. Database access starts off; you can ask about the saved results without enabling it. To fetch fresh data while retaining the restored chat, choose a connected tab with the original database/server/user, explicitly enable access again, and approve every new query. Historic results are snapshots and may be out of date. Reopening never reruns old tool calls. To use another account or database, choose **New chat**.

Use **New chat** to clear the conversation, its attached snapshots and the activity view. Any enabled database access remains available, with approval still required for each new query. Changing the ChatGPT account, source database connection or database access starts a fresh conversation. Closing the assistant clears its in-memory chat. Chats are saved only when you choose **Save chat**; no history is imported from ChatGPT. Cancelled/failed answers stay visibly marked in the chat but are excluded from follow-up context. Long chats are limited to 20 completed exchanges and approximately 1,000,000 serialized input characters; the app asks for New chat rather than silently discarding earlier context. The latest completed answer remains available when that limit is reached.

### Live schema and read-only queries

In **Assistant → Database access**:

1. Choose a connected query tab. Rowfinch opens a separate session using that tab's actual connected authentication, without borrowing its transaction or executing its editor SQL. To keep your normal writer connection, connect another tab with a dedicated SELECT-only database account and choose that tab here.
2. Select **Allow schema inspection**, **Allow read-only queries**, or both, then **Enable access**. Schema inspection can use a normal account; queries require a read-only identity. Access lasts only for this assistant window and is never saved as a grant. Browser object filters and expanded/selected columns do not restrict these tools: the chosen database identity's metadata visibility and SELECT permissions define their scope.
3. Ask questions such as “Which tables contain customer information?” or “Count orders by month.” The assistant can list up to 100 matching object names/types and fetch up to 100 visible columns (types, nullability and keys) for a table or view, even when it has not been expanded in the object browser. Schema responses are shared with OpenAI under the enabled permission.
4. Each query opens an approval dialog with the exact SQL, purpose, database/server and result-sharing limits. **Run and share result** executes it and sends the bounded result to OpenAI within that question. **Decline** executes nothing and blocks further queries for that question. The Database access activity view shows metadata requests, decisions and shared results; the activity log itself is not saved. Completed tool calls and their results are included if you choose Save chat.
5. **Disable access** cancels pending work and closes the independent session. Disconnecting/closing the chosen database tab, changing ChatGPT accounts, or closing the assistant also revokes access.

Ask your database administrator to provision a dedicated identity with `SELECT` only on approved objects or schemas. Optional `VIEW DEFINITION` can broaden visible metadata; it is not required for tables already visible through SELECT grants. Avoid writer/owner/admin roles and stored-procedure EXECUTE grants. Rowfinch refuses `dbo` and effective database/schema/object (including column) permissions outside a read-only allowlist, plus elevated server permissions on SQL Server. Permission checks run again immediately before every query; errors or unknown permissions block query access. Rowfinch does not create database users or alter permissions. See Microsoft's [effective permission reference](https://learn.microsoft.com/en-us/sql/relational-databases/system-functions/sys-fn-my-permissions-transact-sql).

Queries also pass Microsoft's T-SQL parser with a restricted grammar: one SELECT, optionally with CTEs, common joins, grouping and built-in functions. Writes, SELECT INTO, EXEC, local variables, table/query hints, user-defined functions, external sources and cross-database names are blocked. Date-part expressions in DATEADD/DATEDIFF/DATEPART/DATETRUNC, TRY_CAST/TRY_CONVERT, and the read-only @@DATEFIRST value are supported. A policy rejection executes nothing and returns feedback to the assistant, which can revise its query within the same eight-call budget. Every accepted query still requires approval; declining blocks further queries for that message. Some advanced valid SELECT syntax is deliberately unsupported and can be opened for manual review instead. Limits: **100 rows, 100 columns, 15 seconds**, 2000 text characters/1500 binary bytes per cell, and 24,000 characters per tool response. Truncation is reported. Each message permits at most eight database calls. Completed tool history and bounded results are retained in the current chat for follow-ups. Prior results are snapshots; a new query is needed for fresh data and requires a fresh approval.

ChatGPT access, refresh, and ID tokens are stored in a dedicated `org.rowfinch.ChatGptSession` desktop-keyring entry, separate from SQL passwords. An unlocked Secret Service keyring and browser are required. The host identifier and notice preferences live in `settings.json` beside the connection profiles. No API key, client secret, shell agent, or additional runtime is needed. JWT signature, issuer, audience, expiry, issued-at time, nonce, and returning-account identity are checked. Refreshes are serialized across assistant windows/processes. Sign out attempts remote revocation, clears that account's local tokens, and retains its registration for reauthorization; an unconfirmed revocation is reported explicitly. Closing the application keeps saved ChatGPT sessions for later use.

The implementation follows OpenAI's [open-source sign-in flow](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [plan usage requirements](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations), and [usage UI guidance](https://developers.openai.com/siwc/ui-ux-guidelines). Automated tests use local callbacks, signed test identities, simulated HTTP, and a fake keyring. A live browser sign-in and first inference with an eligible account remain manual acceptance checks.

## Password keyring

For SQL authentication, check **Remember password** and connect with your password. After authentication succeeds, Rowfinch saves the connection profile and stores the password in the desktop keyring. Saving a profile alone records the preference without storing an unverified password.

When connecting a saved profile from a disconnected tab, leave the password field blank to retrieve it from the keyring. Rowfinch does not populate the password field when you select a profile. New tabs created from a connected tab reuse its active authentication without accessing the keyring. An explicitly entered password takes precedence for a manual connection and updates the saved password after successful authentication.

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
