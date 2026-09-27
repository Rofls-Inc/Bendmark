import json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Step:
    """Ступень прогона - только поля, которые нужны для поиска предела"""
    index: int
    target_rps: float
    throughput_rps: float
    p99_ms: float


def parse_steps(data: dict) -> list[Step]:
    """Превращает result.json в список ступеней"""
    try:
        return [
            Step(
                index=raw["index"],
                target_rps=raw["target_rps"],
                throughput_rps=raw["throughput_rps"],
                p99_ms=raw["latency_ms"]["p99"],
            )
            for raw in data["steps"]
        ]
    except KeyError as e:
        raise ValueError(f"в result.json нет поля {e}") from e
    except TypeError as e:
        raise ValueError(f"неожиданная структура result.json: {e}") from e


def load_result(path: str | Path) -> list[Step]:
    """Чтение result.json с диска"""
    text = Path(path).read_text(encoding="utf-8")
    return parse_steps(json.loads(text))
