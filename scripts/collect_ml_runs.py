import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from uuid import UUID

from compose_smoke import SmokeError, request_json, require, wait_for_analysis


ROOT = Path(__file__).resolve().parents[1]
PROJECT = "bendmark-ml"
API = "http://127.0.0.1:5080"
TARGET_ORIGIN = "http://emulator:8080"

EMULATOR = {
    "mode": "saturation",
    "workers": 3,
    "queue_size": 128,
    "service_time": "250ms",
    "slow_delay": "5s",
    "jitter": "0s",
    "outlier_rate": 0,
    "outlier_delay": "0s",
    "seed": 1,
}
SCENARIO = {
    "name": "ml-saturation",
    "target": {"url": f"{TARGET_ORIGIN}/work", "method": "GET"},
    "steps": [
        {"target_rps": rate, "duration_seconds": 5}
        for rate in (6, 9, 10.5, 13.5, 15, 18)
    ],
}


def start_stand():
    env = {
        **os.environ,
        **{f"EMULATOR_{key.upper()}": str(value) for key, value in EMULATOR.items()},
        "Telemetry__Url": f"{TARGET_ORIGIN}/telemetry",
        "Telemetry__TargetOrigin": TARGET_ORIGIN,
    }
    subprocess.run(
        [
            "docker", "compose", "-p", PROJECT, "--profile", "emulator",
            "up", "--build", "-d", "--wait", "--wait-timeout", "180", "coordinator", "emulator",
        ],
        cwd=ROOT,
        env=env,
        check=True,
    )


def check_saturation(run):
    steps = run["steps"]
    require(len(steps) == len(SCENARIO["steps"]), "В отчёте есть не все ступени сценария")
    require(
        all(step["request_count"] > 0 and step["skipped_count"] == 0 for step in steps),
        "В отчёте есть ступень без замеров или с пропусками агента",
    )
    require(
        all(step["errors"]["count"] == 0 for step in steps),
        "Для этого сценария насыщения нужны ступени без ошибок запросов",
    )

    telemetry = run["telemetry"]
    samples = telemetry["snapshots"]
    require(telemetry["status"] == "complete" and samples, "Нужны снимки телеметрии без пропусков")
    require(all(sample["run_id"] == run["id"] for sample in samples), "Снимки относятся к другому прогону")

    snapshots = [sample["snapshot"] for sample in samples]
    require(len({item["started_at"] for item in snapshots}) == 1, "Источник перезапустился во время прогона")
    require(
        all(item["workers_total"] == EMULATOR["workers"] and item["queue_capacity"] == EMULATOR["queue_size"]
            for item in snapshots),
        "Размеры пула и очереди в телеметрии расходятся с экспериментом",
    )
    require(
        any(item["workers_busy"] == EMULATOR["workers"] and (item["queue_length"] or 0) > 0
            for item in snapshots),
        "Телеметрия не показала занятого пула с запросами в очереди",
    )


def save_run(run, output_dir):
    output_dir.mkdir(parents=True, exist_ok=True)
    destination = output_dir / f"{run['id']}.json"
    temporary = destination.with_suffix(".tmp")
    record = {"schema_version": 1, "label": EMULATOR["mode"], "emulator": EMULATOR, "run": run}
    try:
        temporary.write_text(json.dumps(record, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/ml-runs")
    args = parser.parse_args()

    try:
        start_stand()
        deadline = time.monotonic() + 120
        created = request_json(API, "POST", "/runs", deadline, payload=SCENARIO, expected_status=201)
        run_id = str(UUID(str(created.get("id"))))
        run = wait_for_analysis(API, run_id, deadline)
        require(run["id"] == run_id, "Координатор вернул отчёт другого прогона")
        check_saturation(run)
        destination = save_run(run, args.output_dir)
    except (OSError, ValueError, KeyError, SmokeError, subprocess.CalledProcessError) as error:
        print(f"Прогон не сохранён: {error}", file=sys.stderr)
        return 1

    print(f"Сохранён прогон {run_id}: {destination}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
