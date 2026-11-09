import argparse
import sys

from .diagnosis import Diagnosis, describe_diagnosis, diagnose
from .limit import LimitResult, find_limit
from .result import load_run_result


def describe(limit: LimitResult, diagnosis: Diagnosis, status: str = "completed") -> str:
    lines = ["Прогон прерван. Анализ только завершённых ступеней."] if status == "aborted" else []
    if not limit.found:
        lines.append(f"Предел не найден: подтверждённого отказа нет, предел не ниже {limit.limit_rps:g} запр/с")
    else:
        first = limit.failed_step
        if limit.limit_rps is None:
            lines.append(f"Предел ниже первой ступени ({first.target_rps:g} запр/с)")
        else:
            lines.append(f"Предел: {limit.limit_rps:g} запр/с")
        if limit.confirmed:
            second = limit.failed_steps[1]
            lines.append(
                f"Отказ на ступенях {first.index} и {second.index} "
                f"({first.target_rps:g} и {second.target_rps:g} запр/с), подтверждён второй ступенью"
            )
        else:
            lines.append(
                f"Отказ на ступени {first.index} ({first.target_rps:g} запр/с), не подтверждён: "
                "ступени закончились на первой с отказом"
            )
    lines += [f"  - {reason}" for reason in limit.reasons]
    lines.append(describe_diagnosis(diagnosis))
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="analyzer", description="Находит предел сервиса по result.json")
    parser.add_argument("result", help="путь к result.json")
    parser.add_argument("--p99-ms", type=float, default=500, help="порог p99 в мс (по умолчанию 500)")
    parser.add_argument(
        "--min-gain",
        type=float,
        default=0.5,
        help="минимальный прирост пропускной на единицу прироста нагрузки (по умолчанию 0.5)",
    )
    parser.add_argument(
        "--max-error-percent",
        type=float,
        default=1.0,
        help="порог доли ошибок в процентах (по умолчанию 1)",
    )
    args = parser.parse_args(argv)

    try:
        run = load_run_result(args.result)
        if run.status == "aborted" and not run.steps:
            print("Прогон прерван: нет завершённых ступеней, предел определить нельзя.")
            return 0
        thresholds = {
            "p99_threshold_ms": args.p99_ms,
            "min_gain": args.min_gain,
            "max_error_percent": args.max_error_percent,
        }
        limit = find_limit(run.steps, **thresholds)
        diagnosis = diagnose(run.steps, limit, **thresholds)
    except (OSError, ValueError) as e:
        print(f"Ошибка: {e}", file=sys.stderr)
        return 1

    print(describe(limit, diagnosis, run.status))
    return 0


if __name__ == "__main__":
    sys.exit(main())
