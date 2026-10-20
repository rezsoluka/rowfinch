# Changelog

## 0.5.0-beta.3 — 2026-10-20

- Replace generic assistant database-tool argument errors with the operation, offending field/type or size limit, and corrective guidance. Return validation feedback to ChatGPT/Claude for correction within the existing tool-call budget, without executing rejected requests or bypassing query approval.
- Keep failure/cancellation messages with their chat exchanges and expose the last database request issue in an expandable feedback panel, automatically expanded on failure.
- Include SQL syntax line/column locations and SQL Server error numbers/lines in assistant query failures; advertise argument length limits to both providers.

## 0.5.0-beta.2 — 2026-10-20

- Expand local SQL completion with T-SQL commands, common built-in functions and data types, and phrases such as CREATE TABLE, INSERT INTO, ORDER BY, and BEGIN TRANSACTION.
- Suggest missing command words after a space, including CREATE OR ALTER and transaction/SET clauses. Prioritize these continuations and insert only the missing words without repeating text already typed.

## 0.5.0-beta.1 — 2026-10-20

- Add local SQL editor completion for database objects, schemas, keywords, and table/view columns, including aliases declared later in the current query.
- Show suggestions while typing or with Ctrl+Space; use arrows to select, Enter/Tab to insert, and Escape to dismiss. Insert escaped SQL identifiers and preserve editor undo.
- Reuse each tab's unfiltered metadata cache and lazily load visible columns through its existing session without changing SQL or results. Invalidate suggestions when switching tabs or disconnecting.
- Keep suggestions out of strings/comments and avoid guessing columns for ambiguous names, CTEs, derived tables, or cross-database references.

## 0.4.0-beta.1 — 2026-10-16

- Add Claude alongside ChatGPT, using a user-supplied Anthropic API key, optional workspace ID, live model discovery and streamed answers.
- Store remembered Claude credentials in their own desktop-keyring schema, with a session-only alternative and explicit Console billing guidance.
- Keep provider chats and drafts separate in memory; switching providers disables database access and clears sharing choices. Saved chats record the provider and preserve native tool/thinking state, with support for older ChatGPT files.
- Reuse connection notes, schema inspection and approved read-only queries with Claude, including SQL-policy feedback, cancellation and bounded tool output. Show the selected data recipient in the assistant and query approvals.

## 0.3.0-beta.2 — 2026-10-16

- Fix false rejection of date-part expressions in weekly/date-filtered SELECTs, TRY_CAST/TRY_CONVERT, and read-only @@DATEFIRST. Preserve write, local-variable, cross-database and unknown-function restrictions.
- Return SQL-policy rejections as bounded tool feedback so the assistant can revise its query within the existing call limit. Keep fresh approval for each accepted query and expose complete requested SQL for manual review even after a failed answer.
- Save reusable connection notes in Context, scoped to server/database/user/authentication. Reload them for future chats, include checked notes in the request preview, and preserve them when starting a new chat.
- Accept and preserve supported assistant message phases and reasoning content when saving/reopening chats. Replace the misleading file/size-only error with a validation explanation.
- Allow Open latest SQL in new tab to use the most recent database-tool query when the answer contains no SQL code block. Continue opening unsaved drafts without executing them.

## 0.3.0-beta.1 — 2026-10-15

- Show a one-time safety/privacy notice on first normal launch, with About / Privacy always available.
- Add an optional SQL assistant using native Sign in with ChatGPT, eligible plan usage, a model picker, streamed answers, and usage controls.
- Store ChatGPT accounts/tokens in a dedicated desktop-keyring entry, validate OpenID identity and granted scopes, serialize rotating token use, and revoke renewable sessions on sign-out.
- Share SQL, loaded schema, and a bounded displayed-row sample only when selected, with the exact request visible before Send.
- Add explicit Save chat/Open chat files to resume completed conversations and drafts, with bounded file validation, private atomic local saves, no persisted credentials/access grants, and fresh database enablement/query approval.
- Support follow-up conversations with in-memory message/schema/query-result history, a visible transcript and history preview, and New chat. Clear history on account/database changes and exclude failed/cancelled exchanges; retain per-query approvals.
- Combine assistant questions and answers in a Chat tab, with sharing options and request preview in a separate Context tab.
- Preserve finalized assistant text and database tool calls from streaming events, including responses whose terminal event contains no output. Avoid duplicate answer text and provide content-free diagnostics for genuinely empty responses.
- Add opt-in live schema inspection and read-only query tools through independent database sessions. Require restricted SELECT syntax, effective read-only permissions, approval before running and sharing each query, bounded results/timeouts, and visible in-memory activity. Revoke access on disable, source disconnection/closure, account change or window closure.
- Open completed SQL suggestions as unsaved query tabs for review without executing them. Cancel incomplete answers and close assistants with their source tab.

## 0.2.0-beta.2 — 2026-10-15

- Automatically connect new query tabs, generated scripts, definitions, and opened SQL files using the source tab's active connection, with independent sessions and in-memory credential reuse.
- Export displayed result rows as typed JSON arrays, including nulls, dates, and Base64 binary values.
- Generate and preview C# classes with column mappings, nullability, and available validation annotations from result metadata, including empty results.
- Generate a single JSON object from the first displayed row or a template with null property values; copy or save generated C# and JSON.
- Preserve duplicate and unnamed SELECT columns with unique JSON and C# property names.

## 0.2.0-beta.1 — 2026-10-15

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
