import argparse
import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from uuid import UUID, uuid4

from compose_smoke import SmokeError, request_json, require, wait_for_analysis


ROOT = Path(__file__).resolve().parents[1]
PROJECT = "bendmark-ml"
API = "http://127.0.0.1:5080"
TARGET_ORIGIN = "http://emulator:8080"
MODES = ("healthy", "reject", "saturation", "slow")


@dataclass(frozen=True)
class Configuration:
    name: str
    split: str
    workers: int
    service_time_ms: int
    slow_delay_ms: int
    rates: tuple[float, ...]


CONFIGURATIONS = (
    Configuration("pool-3x250ms", "train", 3, 250, 600, (6, 9, 13.5, 18)),
    Configuration("pool-4x400ms", "train", 4, 400, 450, (5, 7.5, 12, 15)),
    Configuration("pool-2x150ms", "test", 2, 150, 700, (6, 9, 15, 20)),
)

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


def make_scenario(name, rates, duration):
    return {
        "name": name,
        "target": {"url": f"{TARGET_ORIGIN}/work", "method": "GET"},
        "steps": [{"target_rps": rate, "duration_seconds": duration} for rate in rates],
    }


def start_stand(emulator, *, build):
    env = {
        **os.environ,
        **{f"EMULATOR_{key.upper()}": str(value) for key, value in emulator.items()},
        "Telemetry__Url": f"{TARGET_ORIGIN}/telemetry",
        "Telemetry__TargetOrigin": TARGET_ORIGIN,
    }
    command = [
        "docker", "compose", "-p", PROJECT, "--profile", "emulator",
        "up", "-d", "--wait", "--wait-timeout", "180",
    ]
    if build:
        command.extend(["--build", "coordinator", "emulator"])
    else:
        command.extend(["--no-build", "--no-deps", "--force-recreate", "emulator"])
    subprocess.run(command, cwd=ROOT, env=env, check=True)


def check_run(run, planned, emulator, *, slow_delay_ms=None):
    steps = run["steps"]
    require(
        run["scenario"]["name"] == planned["name"]
        and run["scenario"]["steps"] == planned["steps"]
        and len(steps) == len(planned["steps"]),
        "Координатор вернул отчёт другого сценария или неполный отчёт",
    )
    require(
        all(step["request_count"] > 0 and step["skipped_count"] == 0 for step in steps),
        "В отчёте есть ступень без замеров или с пропусками агента",
    )

    telemetry = run["telemetry"]
    samples = telemetry["snapshots"]
    require(
        telemetry["status"] == "complete" and len(samples) >= 2,
        "Нужны снимки телеметрии без пропусков",
    )
    require(all(sample["run_id"] == run["id"] for sample in samples), "Снимки относятся к другому прогону")
    snapshots = [sample["snapshot"] for sample in samples]
    require(len({item["started_at"] for item in snapshots}) == 1, "Источник перезапустился во время прогона")

    mode = emulator["mode"]
    errors = sum(step["errors"]["count"] for step in steps)
    has_queue = mode in {"slow", "saturation"}
    workers = emulator["workers"] if mode != "healthy" else None
    capacity = emulator["queue_size"] if has_queue else None
    require(
        all(item["workers_total"] == workers and item["queue_capacity"] == capacity for item in snapshots),
        "Размеры пула и очереди в телеметрии расходятся с экспериментом",
    )

    rejected = snapshots[-1]["rejected_total"] - snapshots[0]["rejected_total"]
    if mode == "reject":
        require(rejected > 0 and errors > 0, "Прогон не зафиксировал отказов эмулятора")
    else:
        require(rejected == 0, "Эмулятор отклонял запросы в эксперименте без отказов")
    if mode in {"healthy", "saturation"}:
        require(errors == 0, "В отчёте есть ошибки запросов")
    if has_queue:
        require(
            any(item["workers_busy"] == workers and (item["queue_length"] or 0) > 0 for item in snapshots),
            "Телеметрия не показала занятого пула с запросами в очереди",
        )
    if mode == "slow":
        require(
            max(step["latency_ms"]["p99"] for step in steps) >= slow_delay_ms,
            "Замедление не отразилось в задержке p99",
        )


def save_json(destination, record):
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(".tmp")
    try:
        temporary.write_text(json.dumps(record, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def collect_run(planned, emulator, *, slow_delay_ms=None):
    deadline = time.monotonic() + 120
    created = request_json(API, "POST", "/runs", deadline, payload=planned, expected_status=201)
    run_id = str(UUID(str(created.get("id"))))
    run = wait_for_analysis(API, run_id, deadline)
    require(run["id"] == run_id, "Координатор вернул отчёт другого прогона")
    check_run(run, planned, emulator, slow_delay_ms=slow_delay_ms)
    return {"schema_version": 1, "label": emulator["mode"], "emulator": emulator, "run": run}


def collect_matrix(output_dir, repeats):
    dataset_dir = output_dir / f"dataset-{uuid4().hex}"
    records = []
    for config in CONFIGURATIONS:
        for mode in MODES:
            emulator = EMULATOR | {
                "mode": mode,
                "workers": config.workers,
                "service_time": f"{config.service_time_ms}ms",
                "slow_delay": f"{config.slow_delay_ms}ms",
            }
            planned = make_scenario(f"ml-{mode}-{config.name}", config.rates, 3)
            print(f"{config.split}/{config.name} · {mode}", flush=True)
            start_stand(emulator, build=not records)
            for _ in range(repeats):
                record = collect_run(planned, emulator, slow_delay_ms=config.slow_delay_ms)
                record.update(split=config.split, configuration=config.name)
                path = Path(config.split) / f"{record['run']['id']}.json"
                save_json(dataset_dir / path, record)
                records.append({
                    "split": config.split,
                    "configuration": config.name,
                    "label": mode,
                    "path": path.as_posix(),
                })
    manifest = dataset_dir / "manifest.json"
    save_json(manifest, {"schema_version": 1, "records": records})
    print(f"Сохранено {len(records)} прогонов. {manifest}", flush=True)


def main():
    parser = argparse.ArgumentParser(description="Собирает размеченные прогоны эмулятора для ML")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/ml-runs")
    parser.add_argument("--matrix", action="store_true", help="Собрать набор из четырёх режимов")
    parser.add_argument("--repeats", type=int, default=1, help="Число повторов каждого эксперимента в наборе")
    args = parser.parse_args()
    if args.repeats < 1 or (args.repeats != 1 and not args.matrix):
        parser.error("--repeats должен быть положительным и применяется вместе с --matrix")

    try:
        if args.matrix:
            collect_matrix(args.output_dir, args.repeats)
        else:
            start_stand(EMULATOR, build=True)
            planned = make_scenario("ml-saturation", (6, 9, 10.5, 13.5, 15, 18), 5)
            record = collect_run(planned, EMULATOR)
            destination = args.output_dir / f"{record['run']['id']}.json"
            save_json(destination, record)
            print(f"Сохранён прогон {record['run']['id']}. {destination}", flush=True)
    except (OSError, ValueError, KeyError, SmokeError, subprocess.CalledProcessError) as error:
        print(f"Сбор прерван. {error}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
