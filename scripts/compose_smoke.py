"""Сквозной прогон по поднятому Compose-стенду. Только стандартная библиотека Python."""

import argparse
import json
import sys
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from uuid import UUID


class SmokeError(Exception):
    pass


def request_json(base_url, method, path, deadline, *, payload=None, expected_status=200):
    url = base_url.rstrip("/") + path
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise SmokeError(f"Smoke timeout expired before {method} {url}")

    request = Request(
        url,
        data=json.dumps(payload).encode("utf-8") if payload is not None else None,
        headers={"Content-Type": "application/json"},
        method=method,
    )
    try:
        try:
            response = urlopen(request, timeout=min(5, remaining))
        except HTTPError as error:
            response = error
        with response:
            status = response.code
            body = response.read().decode("utf-8")
    except (URLError, OSError) as error:
        raise SmokeError(f"{method} {url}: {error}") from error

    require(status == expected_status, f"{method} {url}: expected HTTP {expected_status}, got {status}: {body}")
    try:
        data = json.loads(body)
    except ValueError as error:
        raise SmokeError(f"{method} {url}: response is not JSON: {body}") from error
    require(isinstance(data, dict), f"{method} {url}: expected a JSON object, got {body}")
    return data


def require(condition, message):
    if not condition:
        raise SmokeError(message)


def wait_for_analysis(base_url, run_id, deadline):
    previous = None
    while True:
        run = request_json(base_url, "GET", f"/runs/{run_id}", deadline)
        status = run.get("status")
        analysis = run.get("analysis") or {}
        analysis_status = analysis.get("status")
        progress = (status, len(run.get("steps", [])), analysis_status)
        if progress != previous:
            print(f"  run={run_id}: status={status}, steps={progress[1]}, analysis={analysis_status}", flush=True)
            previous = progress

        require(status in {"created", "running", "completed"}, f"Run did not complete successfully: {json.dumps(run)}")
        if status == "completed" and analysis_status not in {None, "pending"}:
            require(analysis_status in {"found", "not_found"}, f"Analysis failed: {json.dumps(run)}")
            return run
        remaining = deadline - time.monotonic()
        require(remaining > 0, f"Timed out waiting for run and analysis: {json.dumps(run)}")
        time.sleep(min(0.25, remaining))


def check_run(base_url, scenario, deadline):
    print(f"Checking {base_url}", flush=True)
    created = request_json(base_url, "POST", "/runs", deadline, payload=scenario, expected_status=201)
    run_id = created.get("id")
    try:
        UUID(str(run_id))
    except ValueError as error:
        raise SmokeError(f"POST /runs returned an invalid id: {json.dumps(created)}") from error

    run = wait_for_analysis(base_url, run_id, deadline)
    steps = run.get("steps", [])
    planned = scenario["steps"]
    require(len(steps) == len(planned), f"Expected {len(planned)} steps, got {json.dumps(run)}")
    for index, (step, target) in enumerate(zip(steps, planned), start=1):
        require(step.get("index") == index, f"Unexpected step index: {step}")
        require(step.get("target_rps") == target["target_rps"], f"Unexpected target RPS: {step}")
        require(step.get("duration_seconds") == target["duration_seconds"], f"Unexpected step duration: {step}")
        require(step.get("request_count", 0) > 0, f"Step has no completed requests: {step}")
        require(step.get("skipped_count") == 0, f"Agent skipped requests: {step}")
        require(step.get("errors", {}).get("count") == 0, f"Requests to ABStock failed: {step}")
    require(run["analysis"].get("analyzed_steps") == len(planned), f"Analysis did not cover all steps: {json.dumps(run)}")

    result = request_json(base_url, "GET", f"/runs/{run_id}/result", deadline)
    require(result.get("status") == "completed", f"Unexpected exported status: {result}")
    require(result.get("scenario_name") == scenario["name"], f"Unexpected exported scenario: {result}")
    require(result.get("steps") == steps, f"Exported steps differ from the run: {result}")
    request_json(base_url, "POST", f"/runs/{run_id}/stop", deadline, expected_status=409)
    print(f"  OK: {len(steps)} steps, analysis={run['analysis']['status']}, result=200, stop=409", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", nargs="+", default=["http://localhost:5080", "http://localhost:5173/api"])
    parser.add_argument("--scenario", type=Path, default=Path(__file__).resolve().parents[1] / "testdata/scenarios/smoke.json")
    parser.add_argument("--timeout", type=int, default=45, help="Общий срок всех проверок, в секундах (по умолчанию 45)")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")

    started = time.monotonic()
    try:
        scenario = json.loads(args.scenario.read_text(encoding="utf-8"))
        for base_url in args.base_url:
            check_run(base_url, scenario, started + args.timeout)
    except (SmokeError, OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(f"Compose smoke passed in {time.monotonic() - started:.1f}s", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
