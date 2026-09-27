import argparse
import sys

from .limit import LimitResult, find_limit
from .result import load_result


def describe(limit: LimitResult) -> str:
    if not limit.found:
        return f"Предел не найден: все ступени без отказа, предел не ниже {limit.limit_rps:g} запр/с"
    step = limit.failed_step
    failure = f"Отказ на ступени {step.index} ({step.target_rps:g} запр/с): " + "; ".join(limit.reasons)
    if limit.limit_rps is None:
        return f"Предел ниже первой ступени ({step.target_rps:g} запр/с)\n{failure}"
    return f"Предел: {limit.limit_rps:g} запр/с\n{failure}"


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
    args = parser.parse_args(argv)

    try:
        steps = load_result(args.result)
        limit = find_limit(steps, args.p99_ms, args.min_gain)
    except (OSError, ValueError) as e:
        print(f"Ошибка: {e}", file=sys.stderr)
        return 1

    print(describe(limit))
    return 0


if __name__ == "__main__":
    sys.exit(main())
