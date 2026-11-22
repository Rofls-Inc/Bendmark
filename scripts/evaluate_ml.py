import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path
from statistics import fmean, pstdev

from collect_ml_runs import MODES, save_json
from compose_smoke import SmokeError, require


FEATURE_NAMES = ("log_peak_p99_ms", "error_fraction", "log_peak_queue_length", "rejected_fraction")
RULE_LABELS = {"rejecting": "reject", "saturation": "saturation", "slow_responses": "slow"}
UNDETERMINED = "undetermined"


@dataclass(frozen=True)
class Example:
    run_id: str
    configuration: str
    label: str
    features: tuple[float, ...]
    rules: str


def features_for(run):
    steps = run["steps"]
    attempts = sum(step["request_count"] for step in steps)
    require(run["status"] == "completed" and attempts > 0, "Нужен завершённый прогон с замерами")
    require(all(step["skipped_count"] == 0 for step in steps), "Есть пропуски генератора нагрузки")
    errors = sum(step["errors"]["count"] for step in steps)

    telemetry = run["telemetry"]
    samples = telemetry["snapshots"]
    require(telemetry["status"] == "complete" and len(samples) >= 2, "Нужна полная телеметрия прогона")
    snapshots = [sample["snapshot"] for sample in samples]
    rejected = snapshots[-1]["rejected_total"] - snapshots[0]["rejected_total"]
    require(0 <= errors <= attempts and rejected >= 0, "Некорректные счётчики прогона")
    peak_p99 = max(step["latency_ms"]["p99"] for step in steps)
    peak_queue = max(snapshot["queue_length"] or 0 for snapshot in snapshots)
    return math.log1p(peak_p99), errors / attempts, math.log1p(peak_queue), rejected / attempts


def rules_label(run):
    analysis = run["analysis"] or {}
    diagnosis = analysis.get("diagnosis") or {}
    if diagnosis.get("status") == "no_limit":
        return "healthy"
    return RULE_LABELS.get(diagnosis.get("cause"), UNDETERMINED)


def load_dataset(manifest):
    root = manifest.resolve().parent
    data = json.loads(manifest.read_text(encoding="utf-8"))
    require(data["schema_version"] == 1, "Неизвестная версия манифеста")
    splits = {"train": [], "test": []}
    configuration_splits = {}
    settings_splits = {}
    run_ids = set()

    for entry in data["records"]:
        split, label, configuration = entry["split"], entry["label"], entry["configuration"]
        require(split in splits and label in MODES, "Неизвестная выборка или метка")
        require(configuration_splits.setdefault(configuration, split) == split,
                f"Конфигурация {configuration} есть и в train, и в test")
        path = (root / entry["path"]).resolve()
        require(path.is_relative_to(root / split), f"Файл не входит в выборку {split}: {path}")
        record = json.loads(path.read_text(encoding="utf-8"))
        require(record["schema_version"] == 1, f"Неизвестная версия записи {path.name}")
        require(all(entry[key] == record[key] for key in ("split", "label", "configuration")),
                f"Запись {path.name} расходится с манифестом")

        run = record["run"]
        run_id = run["id"]
        require(run_id not in run_ids, f"Повторный ID прогона {run_id}")
        run_ids.add(run_id)
        emulator = {key: value for key, value in record["emulator"].items() if key not in {"mode", "seed"}}
        signature = json.dumps([emulator, run["scenario"]["steps"]], sort_keys=True)
        require(settings_splits.setdefault(signature, split) == split,
                "Одинаковые параметры эксперимента попали в train и test")

        features = features_for(run)
        require(all(math.isfinite(value) and value >= 0 for value in features), f"Некорректные признаки {path.name}")
        splits[split].append(Example(run_id, configuration, label, features, rules_label(run)))

    require({example.label for example in splits["train"]} == set(MODES), "В train нужны все четыре метки")
    require(splits["test"], "Выборка test пуста")
    return splits["train"], splits["test"]


def normalize(features, means, scales):
    return tuple((value - mean) / scale for value, mean, scale in zip(features, means, scales, strict=True))


def fit(train):
    columns = list(zip(*(example.features for example in train), strict=True))
    means = tuple(fmean(column) for column in columns)
    scales = tuple(pstdev(column) or 1.0 for column in columns)
    centroids = {}
    for label in MODES:
        group = [row.features for row in train if row.label == label]
        center = tuple(fmean(column) for column in zip(*group, strict=True))
        centroids[label] = normalize(center, means, scales)
    return means, scales, centroids


def scores(rows, prediction):
    counts = {label: dict.fromkeys((*MODES, UNDETERMINED), 0) for label in MODES}
    for row in rows:
        counts[row["label"]][row[prediction]] += 1
    correct = sum(counts[label][label] for label in MODES)
    return {"correct": correct, "total": len(rows), "accuracy": correct / len(rows), "confusion": counts}


def evaluate(train, test):
    means, scales, centroids = fit(train)
    rows = []
    for example in test:
        normalized = normalize(example.features, means, scales)
        prediction = min(MODES, key=lambda label: math.dist(normalized, centroids[label]))
        rows.append({
            "run_id": example.run_id,
            "configuration": example.configuration,
            "label": example.label,
            "classifier": prediction,
            "rules": example.rules,
        })
    return {
        "schema_version": 1,
        "model": {
            "method": "nearest_centroid",
            "features": FEATURE_NAMES,
            "means": means,
            "scales": scales,
            "centroids": centroids,
        },
        "train_size": len(train),
        "test_size": len(test),
        "classifier": scores(rows, "classifier"),
        "rules": scores(rows, "rules"),
        "runs": rows,
    }


def main():
    parser = argparse.ArgumentParser(description="Сравнивает классификатор с правилами на отложенных прогонах")
    parser.add_argument("manifest", type=Path, help="Путь к manifest.json, созданному сборщиком #94")
    args = parser.parse_args()

    try:
        train, test = load_dataset(args.manifest)
        report = evaluate(train, test)
        destination = args.manifest.with_name("evaluation.json")
        save_json(destination, report)
    except (OSError, KeyError, TypeError, ValueError, SmokeError) as error:
        print(f"Оценка прервана. {error}", file=sys.stderr)
        return 1

    print(f"{'Метка':<14} {'Классификатор':<16} {'Правила':<16} Конфигурация")
    for row in report["runs"]:
        print(f"{row['label']:<14} {row['classifier']:<16} {row['rules']:<16} {row['configuration']}")
    print(f"Классификатор: {report['classifier']['correct']}/{len(test)}")
    print(f"Правила: {report['rules']['correct']}/{len(test)}")
    print(f"Отчёт сохранён в {destination}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
