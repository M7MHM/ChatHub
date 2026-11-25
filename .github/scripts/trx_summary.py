import os
import sys
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_FAILURES = 50
MAX_TEXT = 4000


def seconds(duration):
    if not duration:
        return 0.0
    hours, minutes, rest = duration.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(rest)


def timestamp(value):
    return datetime.fromisoformat(value[:26] + value[-6:] if len(value) > 32 else value)


def fmt(total):
    minutes, secs = divmod(int(round(total)), 60)
    return f"{minutes} m {secs:02d} s" if minutes else f"{total:.1f} s"


def text(node, path):
    found = node.find(path, NS)
    return (found.text or "").strip() if found is not None else ""


def clip(value):
    return value if len(value) <= MAX_TEXT else value[:MAX_TEXT] + "\n... (truncated)"


def escape_annotation(value):
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(value):
    return escape_annotation(value).replace(":", "%3A").replace(",", "%2C")


def read(path):
    root = ET.parse(path).getroot()
    code_base = next((m.get("codeBase", "") for m in root.iterfind("t:TestDefinitions/t:UnitTest/t:TestMethod", NS)), "")
    name = Path(code_base.replace("\\", "/")).stem or path.stem
    counters = root.find("t:ResultSummary/t:Counters", NS)
    count = lambda key: int(counters.get(key, "0")) if counters is not None else 0
    times = root.find("t:Times", NS)
    try:
        elapsed = (timestamp(times.get("finish")) - timestamp(times.get("start"))).total_seconds()
    except (AttributeError, TypeError, ValueError):
        elapsed = 0.0

    results = []
    for result in root.iterfind("t:Results/t:UnitTestResult", NS):
        results.append({
            "name": result.get("testName", "?"),
            "outcome": result.get("outcome", "?"),
            "seconds": seconds(result.get("duration")),
            "message": text(result, "t:Output/t:ErrorInfo/t:Message"),
            "stack": text(result, "t:Output/t:ErrorInfo/t:StackTrace"),
        })

    run_errors = [text(info, "t:Text") for info in root.iterfind("t:ResultSummary/t:RunInfos/t:RunInfo", NS)
                  if info.get("outcome") in ("Error", "Failed")]

    failed = count("failed") + count("error") + count("timeout") + count("aborted")
    return {
        "name": name,
        "outcome": root.find("t:ResultSummary", NS).get("outcome", "?"),
        "total": count("total"),
        "passed": count("passed"),
        "failed": failed,
        "skipped": count("total") - count("executed"),
        "elapsed": elapsed,
        "results": results,
        "run_errors": run_errors,
    }


def main():
    directory = Path(sys.argv[1] if len(sys.argv) > 1 else "TestResults")
    runs = sorted((read(p) for p in directory.rglob("*.trx")), key=lambda r: r["name"])
    out = []

    if not runs:
        out.append("## Tests\n\nNo test results were found. Check the **Test** step log.\n")
    else:
        failed = sum(r["failed"] for r in runs)
        errors = sum(len(r["run_errors"]) for r in runs)
        verdict = "passed" if failed == 0 and errors == 0 else "FAILED"
        icon = ":white_check_mark:" if verdict == "passed" else ":x:"
        out.append(f"## Tests {icon} {verdict}\n")
        out.append("| Project | Total | Passed | Failed | Skipped | Duration |")
        out.append("|---|---:|---:|---:|---:|---:|")
        for r in runs:
            mark = "" if r["failed"] == 0 and not r["run_errors"] else " :x:"
            out.append(f"| {r['name']}{mark} | {r['total']} | {r['passed']} | {r['failed']} | {r['skipped']} | {fmt(r['elapsed'])} |")
        out.append(
            f"| **Total** | **{sum(r['total'] for r in runs)}** | **{sum(r['passed'] for r in runs)}** | **{failed}** "
            f"| **{sum(r['skipped'] for r in runs)}** | |\n")

        failures = [(r["name"], t) for r in runs for t in r["results"] if t["outcome"] not in ("Passed", "NotExecuted")]
        if failures:
            out.append(f"### Failed tests ({len(failures)})\n")
            for project, t in failures[:MAX_FAILURES]:
                body = clip(f"{t['message']}\n\n{t['stack']}".strip())
                out.append(f"<details><summary><code>{t['name']}</code> ({project}, {t['outcome']})</summary>\n")
                out.append(f"```text\n{body}\n```\n</details>\n")
                if os.environ.get("GITHUB_ACTIONS") == "true":
                    print(f"::error title={escape_property('Test failed: ' + t['name'])}::{escape_annotation(t['message'][:1000])}")
            if len(failures) > MAX_FAILURES:
                out.append(f"... and {len(failures) - MAX_FAILURES} more; see the test-results artifact.\n")

        run_errors = [(r["name"], e) for r in runs for e in r["run_errors"]]
        if run_errors:
            out.append("### Errors outside tests (fixture cleanup, test host)\n")
            for project, error in run_errors:
                out.append(f"<details><summary>{project}</summary>\n\n```text\n{clip(error)}\n```\n</details>\n")
                if os.environ.get("GITHUB_ACTIONS") == "true":
                    print(f"::error title={escape_property('Test run error in ' + project)}::{escape_annotation(error[:1000])}")

        slowest = sorted(((r["name"], t) for r in runs for t in r["results"]), key=lambda x: -x[1]["seconds"])[:10]
        if slowest:
            out.append("<details><summary>Slowest tests</summary>\n")
            out.append("| Test | Duration |")
            out.append("|---|---:|")
            for _, t in slowest:
                out.append(f"| `{t['name']}` | {t['seconds']:.1f} s |")
            out.append("\n</details>\n")

    report = "\n".join(out) + "\n"
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    else:
        sys.stdout.write(report)


if __name__ == "__main__":
    main()
