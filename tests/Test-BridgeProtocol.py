"""Exercise the real published stdio executable without modifying any host model."""
import argparse
import json
import pathlib
import subprocess


def check(bridge, target, version):
    requests = [
        {"id": 1, "method": "initialize", "params": {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "BimOnProtocolTest", "version": "1"}}},
        {"method": "notifications/initialized"},
        {"method": "ping"},
        {"id": None, "method": "ping"},
        {"id": 0, "method": "ping"},
        {"id": "한글-ทดสอบ", "method": "ping"},
        {"id": 3, "method": "tools/list", "params": {}},
        {"id": 4, "method": "ping"},
        {"id": 5, "method": "bimon_test_unknown_method"},
    ]
    for request in requests:
        request["jsonrpc"] = "2.0"
    process = subprocess.Popen(
        [str(bridge), "--target", target], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        stdout, stderr = process.communicate(
            ("\n".join(json.dumps(r, ensure_ascii=False) for r in requests) + "\n").encode("utf-8"),
            timeout=20)
    except subprocess.TimeoutExpired:
        process.kill()
        process.communicate()
        raise AssertionError(f"{target}: stdin EOF did not terminate bridge")
    assert process.returncode == 0, f"{target}: exit {process.returncode}"
    replies = [json.loads(line) for line in stdout.decode("utf-8-sig").splitlines() if line.strip()]
    assert len(replies) == 6, f"{target}: unexpected response/notification count"
    by_id = {r["id"]: r for r in replies}
    assert len(by_id) == 6 and all(r["jsonrpc"] == "2.0" for r in replies)
    assert by_id[1]["result"]["serverInfo"]["version"] == version
    assert by_id[1]["result"]["serverInfo"]["name"] == "BimOn-" + target
    for request_id in (0, "한글-ทดสอบ", 4):
        assert by_id[request_id] == {"jsonrpc": "2.0", "id": request_id, "result": {}}, by_id[request_id]
    assert by_id[5]["error"]["code"] == -32601
    names = {t["name"] for t in by_id[3]["result"]["tools"]}
    assert ("forma_bridge_status" if target == "forma" else "list_instances") in names
    if target in ("autocad", "navisworks"):
        mutations = {"execute_script", "execute_saved_script", "save_script", "delete_script",
                     "submit_job", "cancel_job", "set_entity_property" if target == "autocad" else "approve_clash"}
        catalog = by_id[3]["result"]["tools"]
        assert len(catalog) == (25 if target == "autocad" else 23)
        assert mutations <= names
        for tool in catalog:
            assert tool.get("annotations", {}).get("readOnlyHint") is (tool["name"] not in mutations), tool["name"]
    return {"target": target, "version": version, "toolCount": len(names), "passed": True,
            "desktopAnnotationsVerified": target in ("autocad", "navisworks"),
            "checks": ["ping-numeric-zero", "ping-unicode-id", "ping-after-tools", "no-notification-reply",
                       "assembly-version", "unknown-method-error", "json-only-stdout", "clean-eof-exit"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bridge", type=pathlib.Path, required=True)
    parser.add_argument("--expected-version", required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--targets", nargs="+", default=["revit", "autocad", "navisworks"],
                        help="Desktop targets by default; pass additional targets explicitly for a full-suite build.")
    args = parser.parse_args()
    results = [check(args.bridge.resolve(), target, args.expected_version) for target in args.targets]
    args.report.write_text(json.dumps(results, indent=2), encoding="utf-8")
    for result in results:
        print(f"PASS {result['target']}: version={result['version']}, {result['toolCount']} tools, 8 protocol checks")
