import json
import math
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Step:
    """Ступень прогона - только поля, которые нужны для поиска предела"""
    index: int
    target_rps: float
    throughput_rps: float
    p99_ms: float
    request_count: int
    error_count: int


def parse_steps(data: dict) -> list[Step]:
    """Превращает result.json в список ступеней"""
    if not isinstance(data, dict) or not isinstance(data.get("steps"), list):
        raise ValueError("в result.json нет списка steps")
    return [_parse_step(raw, position) for position, raw in enumerate(data["steps"], 1)]

def load_result(path: str | Path) -> list[Step]:
    """Чтение result.json с диска"""
    text = Path(path).read_text(encoding="utf-8")
    return parse_steps(json.loads(text))


def _parse_step(raw: object, position: int) -> Step:
    where = f"ступень {position}"
    if not isinstance(raw, dict):
        raise ValueError(f"{where}: ожидался объект")
    # Legacy result files omit this field; new agents must export it explicitly.
    if "skipped_count" in raw:
        skipped = _number(raw, where, "skipped_count", integer=True)
        if skipped > 0:
            raise ValueError(
                f"{where}: агент пропустил {skipped} запросов; эти измерения не определяют предел сервиса"
            )
    step = Step(
        index=_number(raw, where, "index", integer=True),
        target_rps=_number(raw, where, "target_rps"),
        throughput_rps=_number(raw, where, "throughput_rps"),
        p99_ms=_number(raw, where, "latency_ms", "p99"),
        request_count=_number(raw, where, "request_count", integer=True),
        error_count=_number(raw, where, "errors", "count", integer=True),
    )
    if step.error_count > step.request_count:
        raise ValueError(f"{where}: errors.count больше request_count")
    return step


def _number(raw: dict, where: str, *path: str, integer: bool = False) -> float:
    name = ".".join(path)
    value: object = raw
    for key in path:
        if not isinstance(value, dict) or key not in value:
            raise ValueError(f"{where}: нет поля {name}")
        value = value[key]

    expected = int if integer else (int, float)
    if isinstance(value, bool) or not isinstance(value, expected):
        kind = "целым числом" if integer else "числом"
        shown = json.dumps(value, ensure_ascii=False)
        raise ValueError(f"{where}: поле {name} должно быть {kind}, а не {shown}")
    if not math.isfinite(value) or value < 0:
        raise ValueError(f"{where}: поле {name} должно быть неотрицательным конечным числом, а не {value}")
    return value
