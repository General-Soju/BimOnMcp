using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnMcpBridge
{
    // Fixed, reviewed read-only implementation. Caller data is base64 JSON, never source code.
    // Existing pinned job hosts execute this resource without a transaction; no plugin replacement required.
    internal static class RevitQuery
    {
        internal static bool IsTool(string name) => name == "get_query_options" || name == "query_elements";
        internal static JObject Translate(string name, JObject args)
        {
            var allowed = new[] { "targetInstanceId", "documentId", "criteria", "elementIds" };
            if (!IsTool(name) || args.Properties().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("INVALID_QUERY: Unknown argument.");
            foreach (var field in new[] { "targetInstanceId", "documentId" })
                if (args[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)args[field])) throw new ArgumentException("INVALID_QUERY: Explicit target required.");
            var c = args["criteria"] as JObject ?? throw new ArgumentException("INVALID_QUERY: criteria object required.");
            if (c.Properties().Any(p => !new[] { "categoryId", "levelId", "buildingMode", "buildingParameter", "buildingValue" }.Contains(p.Name)))
                throw new ArgumentException("INVALID_QUERY: Unknown criterion.");
            foreach (var field in new[] { "categoryId", "levelId" }) {
                var value = c[field];
                if (value != null && value.Type != JTokenType.Null && (value.Type != JTokenType.Integer ||
                    (field == "categoryId" ? value.Value<long>() == 0 : value.Value<long>() < 0))) throw new ArgumentException("INVALID_QUERY: Invalid category/level.");
            }
            var mode = c["buildingMode"]?.ToString();
            if (mode != "project" && mode != "parameter") throw new ArgumentException("INVALID_QUERY: Building source must be explicit.");
            foreach (var field in new[] { "buildingParameter", "buildingValue" })
                if (c[field] != null && c[field]!.Type != JTokenType.Null && (c[field]!.Type != JTokenType.String || c[field]!.ToString().Length > 160))
                    throw new ArgumentException("INVALID_QUERY: Invalid building string.");
            if (mode == "parameter" && string.IsNullOrWhiteSpace((string?)c["buildingParameter"])) throw new ArgumentException("INVALID_QUERY: Parameter name required.");
            if (args["elementIds"] != null && args["elementIds"]!.Type != JTokenType.Null) {
                if (name != "query_elements" || args["elementIds"] is not JArray ids || ids.Count < 1 || ids.Count > 20 ||
                    ids.Any(x => x.Type != JTokenType.Integer || x.Value<long>() <= 0) || ids.Values<long>().Distinct().Count() != ids.Count)
                    throw new ArgumentException("INVALID_QUERY: Invalid selected elements.");
            }
            var payload = new JObject { ["action"] = name, ["criteria"] = c.DeepClone(), ["elementIds"] = args["elementIds"]?.DeepClone() };
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
            using var resource = typeof(RevitQuery).Assembly.GetManifestResourceStream("BimOnMcpBridge.RevitQuery.py") ?? throw new InvalidOperationException("Missing query implementation.");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            return new JObject { ["name"] = "execute_script", ["arguments"] = new JObject {
                ["targetInstanceId"] = args["targetInstanceId"], ["documentId"] = args["documentId"],
                ["transactionMode"] = "manual", ["code"] = "import base64, json\nq = json.loads(base64.b64decode('" + encoded + "').decode('utf-8'))\n" + reader.ReadToEnd() } };
        }

        internal static JObject Definition(string name) => new JObject {
            ["name"] = name,
            ["description"] = name == "get_query_options"
                ? "Reads model categories, real levels and explicit building values. Host document only; no linked contents. Fixed read-only implementation; no caller script. Requires pinned target."
                : "AND query over model instances by category, associated level and explicit building source. Exact trimmed building match. Returns total, up to 50 candidates, hasMore and unresolved counts. Optional 1-20 elementIds revalidates a selection. No geometry-based floor guessing. No linked contents.",
            ["inputSchema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject {
                    ["targetInstanceId"] = new JObject { ["type"] = "string" }, ["documentId"] = new JObject { ["type"] = "string" },
                    ["criteria"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JObject {
                            ["categoryId"] = new JObject { ["type"] = new JArray("integer", "null"), ["not"] = new JObject { ["const"] = 0 } },
                            ["levelId"] = new JObject { ["type"] = new JArray("integer", "null"), ["minimum"] = 0, ["description"] = "null=all; 0=unresolved; positive=actual level ID" },
                            ["buildingMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("project", "parameter") },
                            ["buildingParameter"] = new JObject { ["type"] = "string", ["maxLength"] = 160 },
                            ["buildingValue"] = new JObject { ["type"] = new JArray("string", "null"), ["maxLength"] = 160, ["description"] = "null=all; empty=missing; otherwise exact trimmed value; ambiguous parameters excluded from value filters" }
                        }, ["required"] = new JArray("buildingMode") },
                    ["elementIds"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 20, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 1 } }
                }, ["required"] = new JArray("targetInstanceId", "documentId", "criteria") }
        };
    }
}
