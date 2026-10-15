# Changelog

## 0.2.0-beta.1 (unreleased preview)

- Separate Connections and Objects into vertical sidebar tabs with scrolling lists and collapsible connection details.
- Combine object-type and text filters; select/unselect columns individually or in bulk.
- Generate SELECT, INSERT, and UPDATE drafts from selected columns, respecting server-managed fields and primary keys.
- Preserve column choices during filtering, tab switching, and individual metadata refresh; reject stale objects after a failed reconnect discovery.

## 0.1.1 (local preview)

- Show the application version in the header, with full build metadata in its tooltip.
- Share version information between the header and `rowfinch --version`.
- Document version bumps and release tags; check package/app version agreement during installation tests.

## 0.1.0 — 2026-10-15

- Initial public version: independent query tabs, SQL and Entra authentication, optional keyring storage, schema browsing, SQL definitions, query results, and CSV export.
- Ubuntu package, desktop launcher, application icon, MIT license, and build/test CI.
