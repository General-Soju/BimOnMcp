using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tomlyn;
using Tomlyn.Model;

namespace BimOnMcpBridge
{
    // Explicit, local registration mode only; never invoked by an MCP request.
    internal static class ClientRegistration
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static string EnvPath(string name, string fallback) =>
            Path.GetFullPath(Environment.GetEnvironmentVariable(name) is string s && !string.IsNullOrWhiteSpace(s) ? s : fallback);
        private static string Name(string target) => target switch {
            "revit" => "BimOn-Revit", "autocad" => "BimOn-AutoCAD", "navisworks" => "BimOn-Navisworks",
            _ => "BimOn-" + char.ToUpperInvariant(target[0]) + target.Substring(1)
        };

        public static int Run(string[] args, string[] supported)
        {
            var results = new JArray();
            string? report = null;
            bool diagnose = args.Contains("--diagnose");
            try
            {
                var opts = new Dictionary<string, string>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--register-clients" || args[i] == "--diagnose") continue;
                    if (!new[] { "--clients", "--targets", "--bridge-path", "--report" }.Contains(args[i]) || i + 1 >= args.Length)
                        throw new InvalidOperationException("INVALID_ARGUMENTS");
                    if (!opts.TryAdd(args[i], args[++i])) throw new InvalidOperationException("DUPLICATE_ARGUMENT");
                }
                report = opts.TryGetValue("--report", out var rp) ? Path.GetFullPath(rp) : null;
                var clients = (opts.TryGetValue("--clients", out var cl) ? cl : "desktop,code,codex").Split(',');
                if (clients.Any(c => !new[] { "desktop", "code", "codex", "none" }.Contains(c)) || (clients.Contains("none") && clients.Length != 1))
                    throw new InvalidOperationException("INVALID_CLIENTS");
                var targets = opts.TryGetValue("--targets", out var ts) ? ts.Split(',').Distinct().ToArray() : supported;
                if (targets.Any(t => !supported.Contains(t))) throw new InvalidOperationException("UNSUPPORTED_TARGET");
                var bridge = Path.GetFullPath(opts.TryGetValue("--bridge-path", out var bp) ? bp : Environment.ProcessPath!);
                if (!File.Exists(bridge)) throw new InvalidOperationException("BRIDGE_NOT_FOUND");
                var home = EnvPath("USERPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var roaming = EnvPath("APPDATA", Path.Combine(home, "AppData", "Roaming"));
                var local = EnvPath("LOCALAPPDATA", Path.Combine(home, "AppData", "Local"));
                var paths = new List<(string Client, string Path, bool Toml)>();
                if (clients.Contains("desktop"))
                {
                    paths.Add(("Claude Desktop", Path.Combine(roaming, "Claude", "claude_desktop_config.json"), false));
                    var data = Path.Combine(local, "Claude-Data");
                    try
                    {
                        if (Directory.Exists(data))
                            foreach (var profile in Directory.GetDirectories(data, "claude_*"))
                                paths.Add(("Claude Desktop profile", Path.Combine(profile, "claude_desktop_config.json"), false));
                    }
                    catch { results.Add(Row("Claude Desktop profiles", data, "failed", "PROFILE_DISCOVERY_FAILED")); }
                }
                if (clients.Contains("code"))
                {
                    var custom = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                    paths.Add(("Claude Code", Path.Combine(string.IsNullOrWhiteSpace(custom) ? home : Path.GetFullPath(custom), ".claude.json"), false));
                }
                if (clients.Contains("codex"))
                    paths.Add(("Codex CLI / Desktop", Path.Combine(EnvPath("CODEX_HOME", Path.Combine(home, ".codex")), "config.toml"), true));
                if (report != null && paths.Any(p => string.Equals(Path.GetFullPath(p.Path), report, StringComparison.OrdinalIgnoreCase)))
                {
                    report = null;
                    throw new InvalidOperationException("REPORT_CONFIG_CONFLICT");
                }
                foreach (var item in paths.Distinct())
                {
                    try
                    {
                        var original = File.Exists(item.Path) ? File.ReadAllBytes(item.Path) : null;
                        var text = original == null ? "" : Utf8.GetString(original).TrimStart('\uFEFF');
                        var updated = item.Toml ? MergeToml(text, bridge, targets) : MergeJson(text, bridge, targets, original != null);
                        // Returning null denotes semantic equality: do not reformat or back up unchanged files.
                        if (updated == null) results.Add(Row(item.Client, item.Path, "unchanged", "REGISTERED"));
                        else if (diagnose) results.Add(Row(item.Client, item.Path, "needs-registration", original == null ? "MISSING_CONFIG" : "OUTDATED_ENTRY"));
                        else
                        {
                            var backup = WriteAtomic(item.Path, updated, original);
                            var row = Row(item.Client, item.Path, "updated", "REGISTERED");
                            if (backup != null) row["backup"] = backup;
                            results.Add(row);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Parser messages may contain credentials. Only allow our fixed error codes into diagnostics.
                        var safe = new[] { "CONFIG_INVALID", "ENTRY_CONFLICT", "CONFIG_CHANGED" }.Contains(ex.Message) ? ex.Message : "CONFIG_ACCESS_OR_FORMAT_ERROR";
                        results.Add(Row(item.Client, item.Path, "failed", safe));
                    }
                }
                if (clients.Contains("none")) results.Add(Row("All clients", "", "skipped", "USER_NOT_SELECTED"));
            }
            catch (Exception ex)
            {
                var safe = new[] { "INVALID_ARGUMENTS", "DUPLICATE_ARGUMENT", "INVALID_CLIENTS", "UNSUPPORTED_TARGET", "BRIDGE_NOT_FOUND", "REPORT_CONFIG_CONFLICT" }.Contains(ex.Message) ? ex.Message : "REGISTRATION_SETUP_FAILED";
                results.Add(Row("Registration", "", "failed", safe));
            }
            int exit = results.Any(r => (string?)r["status"] == "failed" || (string?)r["status"] == "needs-registration") ? 10 : 0;
            var output = new JObject { ["schemaVersion"] = 1, ["diagnosticOnly"] = diagnose, ["exitCode"] = exit, ["results"] = results,
                ["note"] = "Registration is not a live connection test. Restart clients. If a desktop profile is created later, run Repair MCP registration again." };
            if (report != null)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, output.ToString(), Utf8); }
                catch { exit = 10; output["exitCode"] = exit; output["reportError"] = "REPORT_WRITE_FAILED"; }
            }
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine(output.ToString(Formatting.Indented));
            return exit;
        }

        private static JObject Row(string client, string path, string status, string code) =>
            new JObject { ["client"] = client, ["path"] = path, ["status"] = status, ["code"] = code };

        private static string? MergeJson(string text, string bridge, string[] targets, bool exists)
        {
            JObject root;
            try
            {
                root = exists ? JObject.Parse(text, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) : new JObject();
            }
            catch { throw new InvalidOperationException("CONFIG_INVALID"); }
            var before = root.DeepClone();
            if (root["mcpServers"] != null && root["mcpServers"]!.Type != JTokenType.Null && root["mcpServers"] is not JObject)
                throw new InvalidOperationException("CONFIG_INVALID");
            var servers = root["mcpServers"] as JObject;
            if (servers == null) { servers = new JObject(); root["mcpServers"] = servers; }
            foreach (var target in targets)
            {
                var key = Name(target);
                if (servers[key] != null && servers[key] is not JObject) throw new InvalidOperationException("ENTRY_CONFLICT");
                var entry = servers[key] as JObject ?? new JObject();
                if (entry["url"] != null || (entry["type"] != null && (string?)entry["type"] != "stdio")) throw new InvalidOperationException("ENTRY_CONFLICT");
                entry["command"] = bridge; entry["args"] = new JArray("--target", target); servers[key] = entry;
            }
            return JToken.DeepEquals(before, root) ? null : root.ToString(Formatting.Indented) + Environment.NewLine;
        }

        private static string? MergeToml(string text, string bridge, string[] targets)
        {
            TomlTable root;
            try { root = Toml.ToModel(text); } catch { throw new InvalidOperationException("CONFIG_INVALID"); }
            if (root.TryGetValue("mcp_servers", out var existing) && existing is not TomlTable) throw new InvalidOperationException("CONFIG_INVALID");
            var servers = existing as TomlTable ?? new TomlTable();
            bool changed = false;
            foreach (var target in targets)
            {
                var key = Name(target);
                if (servers.TryGetValue(key, out var value) && value is not TomlTable) throw new InvalidOperationException("ENTRY_CONFLICT");
                var entry = value as TomlTable ?? new TomlTable();
                if (entry.ContainsKey("url")) throw new InvalidOperationException("ENTRY_CONFLICT");
                bool same = entry.TryGetValue("command", out var command) && command is string cmd && cmd == bridge
                    && entry.TryGetValue("args", out var arguments) && arguments is TomlArray array && array.Count == 2
                    && Equals(array[0], "--target") && Equals(array[1], target);
                if (!same) { entry["command"] = bridge; entry["args"] = new TomlArray { "--target", target }; servers[key] = entry; changed = true; }
            }
            if (!changed) return null;
            root["mcp_servers"] = servers;
            var output = Toml.FromModel(root);
            // Validate our serialized output before changing the user's file.
            Toml.ToModel(output);
            return output;
        }

        private static string? WriteAtomic(string path, string text, byte[]? original)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, Utf8);
                if (original == null) { File.Move(tmp, path); return null; }
                if (!File.ReadAllBytes(path).SequenceEqual(original)) throw new InvalidOperationException("CONFIG_CHANGED");
                var backup = path + ".bimon-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
                File.Replace(tmp, path, backup);
                return backup;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
