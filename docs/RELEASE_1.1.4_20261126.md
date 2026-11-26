# BimOn MCP 1.1.4 — client registration repair

Released 2026-11-26. The public edition includes Revit, AutoCAD, Navisworks and Dynamo.

> **Connection scope:** BimOn 1.1.4 supports Claude Desktop, Claude Code CLI and local Codex CLI/Desktop. It does **not** provide ordinary ChatGPT web/app-plugin integration. A local Codex host in ChatGPT desktop is distinct from that remote connection. No ChatGPT account plugin, remote MCP relay or authentication service is included. See [AI client support](../README.md#ai-client-support-114).

## Fixed

- The installer offers registration for Claude Desktop, Claude Code CLI and Codex CLI/Desktop. New settings are created for selected clients; Codex CLI is not required to register the desktop app.
- Codex CLI/Desktop share one user TOML configuration. Registration uses a bundled parser, not text replacement or an external CLI.
- Existing modern Claude Desktop profiles receive a config even if the JSON file is absent. Claude Code is registered before first launch. Custom CODEX_HOME and CLAUDE_CONFIG_DIR are honored.
- Each config is processed independently. Invalid files and conflicting HTTP server entries are preserved; other clients still proceed. Installer exit code 10 means some registration needs attention.
- Existing servers, preferences, disabled states and approval policies are preserved. Modified files receive unique adjacent backups. Unchanged files remain byte-identical on rerun. No blanket tool approvals are added.
- Start > BimOn AI > Repair MCP registration handles clients/profiles initialized after installation. The per-client report is `%APPDATA%\BimOnAI\McpRegistration.json`. The bridge also supports a read-only `--register-clients --diagnose` mode.

A Desktop profile that does not exist yet cannot be selected by path. If an AI app creates a profile after setup, use the repair action. App restart is still required. Registration is not a guarantee that a disabled tool, organization policy or a closed Autodesk application will permit model operations.

## Validation

- Six release build targets per edition completed, keeping net8 Revit/AutoCAD and version-specific net48 Navisworks builds.
- Registration regression: 22 public and 21 private checks passed using temporary Windows user roots. Covers first use, modern empty profiles, custom homes, client/target selection, disabled-policy preservation, backups, comments/multiline TOML, malformed/duplicate data, partial failure, HTTP conflicts, repair, diagnostics and report/config path collision.
- Existing bridge/session regression: 20 checks per edition. Queue regression: 20 net8 and 20 net48 checks per edition. Fixed Revit-query Python suite: 18 checks on release source.
- Windows PowerShell 5.1 compatibility wrapper passed for both editions.
- Actual compiled bridge protocol: 24 public and 32 private checks passed (version 1.1.4, catalogs, ping, IDs, JSON stdout, EOF).
- Both real CLIs discovered all three desktop servers from the newly generated isolated user configs. Authenticated Claude Code and Codex CLI tests then used generated entries against each edition's final 1.1.4 bridge: 12 actual read-only `list_instances` calls, no call errors. Navisworks returned an empty list because no instance was running.
- Installer extraction: 88 public and 91 private payload files hash-matched the corresponding release builds, including duplicate per-version DLL destinations and the repair launcher. Public payload contains no private extension assets.

Scope: Four client surfaces (Claude Code, Claude Desktop, Codex CLI and Codex desktop) were verified live with the already-installed bridge during the preceding audit. Final 1.1.4 live calls above were CLI tests; desktop apps were not reinstalled/restarted against 1.1.4 in this release step. A fresh physical remote PC/full installer UI and all Autodesk versions/model writes were not retested. No claim of that broader validation is made.

Public installer: `BimOnMcp_Setup_v1.1.4.exe` (30,381,621 bytes).

SHA-256: `16F143C8C90140837BF96A510F3F871B7235AAAE39E04FE1E83DC9CD984979E7`.

[Download installer and checksum](https://github.com/General-Soju/BimOnMcp/releases/tag/v1.1.4).

## Installation and repair

Run as your normal Windows user after closing Autodesk hosts and AI clients. Leave the desired AI registration checkboxes selected. Restart the AI app and inspect its MCP list. Dynamo tools are included under BimOn-Revit. If a client/profile was initialized later, run the Start-menu repair action. Existing disabled entries stay disabled; registration does not silently re-enable them. Do not share config backups or raw logs that may include credentials.
