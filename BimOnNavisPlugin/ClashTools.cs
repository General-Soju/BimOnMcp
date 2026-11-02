using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Newtonsoft.Json.Linq;

namespace BimOnNavisPlugin
{
    public partial class NavisApp
    {
        // TestsRoot is available in v22/v23/v24. The old Tests shortcut was removed in v24.
        private static IEnumerable<ClashTest> ClashTests(GroupItem root)
        {
            foreach (var item in root.Children)
                if (item is ClashTest test) yield return test;
                else if (item is GroupItem folder)
                    foreach (var nested in ClashTests(folder)) yield return nested;
        }
        private static IEnumerable<SavedItem> ClashItems(GroupItem root)
        {
            foreach (var item in root.Children)
            {
                if (item is IClashResult) yield return item;
                if (item is GroupItem group)
                    foreach (var nested in ClashItems(group)) yield return nested;
            }
        }
        private static bool ClashMatch(SavedItem item, string selector) => Guid.TryParse(selector, out var id)
            ? item.Guid == id : item.DisplayName.Equals(selector, StringComparison.OrdinalIgnoreCase);
        private static string GetClashTests(Document? doc)
        {
            if (doc == null) return "[Error] No document open";
            try
            {
                var clash = DocumentExtensions.GetClash(doc);
                if (clash == null) return "[Error] Clash Detective not available.";
                var sb = new StringBuilder("Clash tests:\n");
                foreach (var test in ClashTests(clash.TestsData.Value.TestsRoot))
                    sb.AppendLine($"  • {test.DisplayName}  id={test.Guid}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return "[Error] Clash query failed: " + ex.Message; }
        }
        private static string GetClashResults(Document? doc, JObject? args)
        {
            if (doc == null) return "[Error] No document open";
            string selector = args?["testName"]?.ToString() ?? "";
            string status = args?["statusFilter"]?.ToString() ?? "All";
            int max = int.TryParse(args?["maxCount"]?.ToString(), out var value) ? Math.Max(1, Math.Min(value, 1000)) : 100;
            try
            {
                var clash = DocumentExtensions.GetClash(doc);
                if (clash == null) return "[Error] Clash Detective not available.";
                var tests = ClashTests(clash.TestsData.Value.TestsRoot).Where(t => ClashMatch(t, selector)).ToList();
                if (tests.Count != 1) return "[Error] Test not found or ambiguous; select its GUID from get_clash_tests.";
                var matches = ClashItems(tests[0]).Where(i => status.Equals("All", StringComparison.OrdinalIgnoreCase)
                    || ((IClashResult)i).Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)).Take(max + 1).ToList();
                var sb = new StringBuilder($"[{tests[0].DisplayName}] Clash items:\n");
                foreach (var item in matches.Take(max))
                    sb.AppendLine($"  • {item.DisplayName}  id={item.Guid}  status={((IClashResult)item).Status}  distance={((IClashResult)item).Distance:F3}");
                if (matches.Count > max) sb.AppendLine("  truncated=true; narrow the status/test filter.");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return "[Error] Clash results failed: " + ex.Message; }
        }
        private static string ApproveClash(Document? doc, JObject? args)
        {
            if (doc == null) return "[Error] No document open";
            string testSelector = args?["testName"]?.ToString() ?? "", resultSelector = args?["clashName"]?.ToString() ?? "";
            if (!Enum.TryParse(args?["status"]?.ToString() ?? "Approved", true, out ClashResultStatus status)
                || !Enum.IsDefined(typeof(ClashResultStatus), status)) return "[Error] Invalid clash status.";
            try
            {
                var clash = DocumentExtensions.GetClash(doc);
                if (clash == null) return "[Error] Clash Detective not available.";
                var tests = ClashTests(clash.TestsData.Value.TestsRoot).Where(t => ClashMatch(t, testSelector)).ToList();
                if (tests.Count != 1) return "[Error] Test not found or ambiguous; use its GUID.";
                var items = ClashItems(tests[0]).Where(i => ClashMatch(i, resultSelector)).ToList();
                if (items.Count != 1) return "[Error] Clash not found or ambiguous; use its GUID. Nothing changed.";
#if NAVIS_2026_PLUS
                using (var actor = new Assignee("BimOn MCP"))
                    clash.TestsData.TestsEditResultStatus((IClashResult)items[0], status, actor);
#else
                clash.TestsData.TestsEditResultStatus((IClashResult)items[0], status);
#endif
                return $"'{items[0].DisplayName}' id={items[0].Guid} → {status} processed";
            }
            catch (Exception ex) { return "[Error] Clash update failed: " + ex.Message; }
        }
    }
}
