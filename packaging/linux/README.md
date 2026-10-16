# Ubuntu packages

Rowfinch packages include the .NET runtime, the app, a `rowfinch` command, a desktop launcher with a **Try demo** action, and scalable/PNG icons. They target Ubuntu 24.04 and newer. The first built and tested artifact is `amd64`; the build script also accepts `linux-arm64`, which needs separate hardware testing.

## Install, launch, and remove

From the repository root:

```bash
sudo apt install ./artifacts/packages/rowfinch_0.3.0~beta.1_amd64.deb
rowfinch
```

Or select **Rowfinch** in the desktop application menu. `rowfinch --demo` opens sample data without a database; `rowfinch --version` and `rowfinch --help` work without a display.

To update, install the newer `.deb` with `sudo apt install ./path/to/new-package.deb`. To remove the app:

```bash
sudo apt remove rowfinch
```

Removal and `apt purge rowfinch` leave profiles and desktop-keyring entries in place. Use **Forget saved password** in the app if you want to remove a remembered password. Profiles remain at `$XDG_CONFIG_HOME/rowfinch/connections.json`, or `~/.config/rowfinch/connections.json` when that variable is unset or relative.

APT resolves native dependencies, including WebKitGTK and libcurl for the bundled Microsoft identity native helper. A graphical desktop and X11/XWayland are needed to open the UI. Entra sign-in also needs a working browser; optional SQL-password storage needs an unlocked Secret Service keyring. The optional ChatGPT assistant requires a working browser and an unlocked Secret Service keyring for account credentials. The package recommends GNOME Keyring and desktop file-picker portals. See [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/platform-specific-guides/linux) and [Microsoft's Ubuntu dependencies](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-decision#dependencies).

## Build

Install the .NET 10 SDK and packaging tools (`dpkg-dev`, `desktop-file-utils`, `python3`, and standard coreutils). Then:

```bash
./scripts/build-deb.sh
```

The version defaults to `Directory.Build.props`. Outputs go to `artifacts/packages/`, with a matching SHA-256 checksum. Builds run without sudo, use an isolated temporary staging directory, and do not install the package. NuGet access is needed for restore and runtime packs on the first build. The publish is self-contained and untrimmed to retain Avalonia and SqlClient behavior.

Options:

```bash
./scripts/build-deb.sh --runtime linux-arm64
./scripts/build-deb.sh --version 0.2.0-beta.1 --output /tmp/rowfinch-packages
```

Debian prerelease versions use `~`, so `0.2.0~beta.1` sorts before `0.2.0`. The app still reports `0.2.0-beta.1`.

Files install under `/usr/lib/rowfinch`, `/usr/bin`, `/usr/share/applications`, `/usr/share/icons/hicolor`, and `/usr/share/doc/rowfinch`. No maintainer scripts run SQL or alter user data. Rowfinch uses the MIT license; the package includes its license, NuGet license metadata, and available third-party notices. A self-contained package receives runtime fixes when it is rebuilt with the updated .NET SDK/runtime packs.

The editable icon source is `assets/rowfinch.svg`. To regenerate the checked-in embedded PNG, install the optional developer tools `python3-gi` and `gir1.2-rsvg-2.0`, then run `python3 scripts/render-icon.py`. Those tools are not required to build or run the app.

## Clean Ubuntu checks

The Docker harness installs the package into a fresh Ubuntu image, checks the bundled runtime and installed files, starts its demo on Xvfb, verifies the window class and icon, then tests reinstallation, upgrade, removal, and purge while retaining a synthetic user-data file. It mounts no host profile directories and needs no database credentials.

```bash
docker build --progress=plain -f packaging/linux/Dockerfile -t rowfinch-package-test .
```

By default it reinstalls the same version. To exercise an actual version upgrade, first build a temporary next-version package, then provide it:

```bash
./scripts/build-deb.sh --version 0.3.0-beta.2
docker build --progress=plain -f packaging/linux/Dockerfile \
  --build-arg UPGRADE_PACKAGE=rowfinch_0.3.0~beta.2_amd64.deb \
  -t rowfinch-package-test .
```

`--build-arg UBUNTU_VERSION=26.04` selects a newer Ubuntu base. The host's Docker daemon must be accessible. Alternatively, on a disposable Ubuntu VM, install the package and `xvfb xauth dbus-daemon dbus-x11 x11-utils desktop-file-utils`, then run `bash packaging/linux/test-installed.sh`. This helper creates only its own temporary demo configuration and never contacts Azure.

### Validation recorded on 2026-10-15

- `.deb` metadata, root ownership, file modes, checksum, embedded runtime, and desktop entry checked.
- Installed successfully with APT in an isolated official Ubuntu Base 24.04.5 amd64 filesystem. No system .NET installation was present. Native dependencies resolved with recommendations disabled.
- Native application and identity-helper dependencies checked with `ldd` (excluding the optional .NET LTTng diagnostic provider).
- Demo window launched on Xvfb; `WM_CLASS=Rowfinch` and the embedded window icon verified after initialization.
- Reinstallation, upgrade from 0.1.0 to a temporary 0.1.1 build, removal, and purge checked; a synthetic saved profile survived byte-for-byte.
- APT's install simulation on the Ubuntu 26.04 development desktop found all dependencies already satisfied.
- Application regression suite: 68 passed, one opt-in native-keyring integration test skipped.

Docker access was unavailable on the development machine; the clean installation and lifecycle checks ran using an unprivileged Bubblewrap namespace instead. A full GNOME desktop menu/favorite check, browser sign-in, and a live Azure metadata smoke test remain manual checks.

## Live desktop and Azure smoke test

1. Install the package on Ubuntu and launch it from the application menu. Check the icon and that a pinned launcher groups the running window correctly.
2. Connect to a test Azure database. Expand a known table and compare column types, nullability, and composite primary-key positions with its schema. Try **Refresh columns**.
3. Right-click **Select top 100**. Check the result and confirm the editor is unchanged. Generate a SELECT, verify the new tab's database settings, connect it, and run it.
4. Open definitions for a view, procedure, and function. Switch to another tab while a definition loads and check that the new document keeps the source connection settings. Restricted definitions should produce a useful message.
5. Run `WAITFOR DELAY '00:00:15'; SELECT 1;`, cancel with Escape, and confirm that a later SELECT still works. Validate Entra MFA separately if you use it.

These checks do not require changing database objects. Use a database/account appropriate for the data you intend to preview.

## Preview acceptance checks (0.2.0-beta.2)

1. Switch between the vertical Connections and Objects tabs. Collapse connection details to use the saved-profile list's full height; select a profile to open its details. Check the form and Connect/Disconnect buttons at your preferred window size.
2. In Objects, select Tables, then add Views. Enter a text filter and check that both constraints apply. All restores every type while keeping the text filter.
3. Expand a table, uncheck a few columns, and generate SELECT. Check that only the selected columns appear. Try Select all and Unselect all, including generating with an empty selection.
4. Generate INSERT and UPDATE. Check the selected writable columns, omitted identity/generated fields, and UPDATE primary-key predicate. For a view without keys, check the required row-filter placeholder. Replace placeholders before executing any draft.
5. Filter an expanded object out and back in, switch query tabs, and refresh its columns. Verify column choices are retained. Full object-list Refresh intentionally resets loaded metadata and choices.
6. In Results, choose Export → Rows as JSON and check numeric, boolean, null, date, and binary values. A display-limited result exports only the displayed rows; a zero-row result exports an empty array.
7. Choose Export → C# class with annotations, edit the class name, then copy or save the preview. Verify property types, nullable columns, and lengths against your SELECT. Repeat with a SELECT returning no rows.
8. Choose Export → JSON object. Check the first row's values, toggle the first-row option to get null placeholders, and save/copy the result. Test a SELECT with duplicate aliases and unnamed expressions.
9. Connect a tab, then create tabs through New query, Ctrl+T, Generate SELECT/INSERT/UPDATE, Open definition, and Open SQL. Each should connect automatically to the source database without executing its document. Verify that disconnecting or cancelling in one tab leaves the others usable. A tab created from a disconnected source should stay disconnected.

Use demo mode for initial UI/template checks, then verify metadata and generated SQL against your intended Azure schema.

## Assistant and privacy acceptance checks (0.3.0-beta.1)

1. Launch normally with a disposable configuration directory. Confirm the safety/privacy notice appears before profiles load. Close app leaves it unacknowledged; Continue remembers it on the next launch. About / Privacy remains available.
2. Open Assistant, choose Continue with ChatGPT, and finish browser sign-in with an eligible account. Confirm the plan-usage welcome appears once, the account/model picker loads, and Manage usage opens ChatGPT usage settings. Check identity-only or declined-plan access produces guidance rather than a model request.
3. Ask a harmless SQL question with all sharing options off. Then opt into SQL, loaded schema, and a small displayed result sample. Verify the preview, change the source editor, and confirm the sent snapshot remains the reviewed one until Refresh context.
4. Open a completed SQL suggestion in a new tab. Confirm it is unsaved, connected using the source target, and has not run. Cancel another answer and verify that incomplete suggestions cannot be opened with that action.
5. Try restoring a saved account after restarting, switching accounts, a usage-limit response, and Sign out. Confirm tokens stay in the dedicated keyring entry and an unconfirmed remote revocation is clearly reported. Browser authentication and live inference are manual checks; automated tests use simulated OpenAI responses.

6. Enable schema inspection on a connected tab. Confirm the assistant can discover object/column metadata, including objects not expanded in the browser. Query access must require a dedicated read-only identity and explicit approval of each restricted SELECT before execution and sharing. Decline a query and confirm it does not run.
7. Ask a follow-up in Chat and review the earlier messages/shared data in Context. Check New chat clears history while keeping explicitly enabled database access; account or active database changes clear the conversation. Disconnecting the selected database tab must revoke assistant access.
8. Save a completed chat with a draft, close and reopen Assistant, then Open chat. Confirm messages, context and the draft return without sending a request or enabling database access. Send a follow-up with the original saved ChatGPT account. Re-enable the original database explicitly for fresh data; confirm every new query still needs approval and saved calls never rerun. Invalid files must leave the current chat intact. Protect saved files: they are unencrypted and can include shared results.
