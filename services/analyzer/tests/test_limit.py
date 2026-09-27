from pathlib import Path

import pytest

from analyzer.__main__ import main
from analyzer.limit import find_limit
from analyzer.result import Step, load_result, parse_steps

EXAMPLE = Path(__file__).resolve().parents[3] / "examples" / "result.json"


def steps(*rows: tuple[float, float, float]) -> list[Step]:
    """Ступени из кортежей (target_rps, throughput_rps, p99_ms)"""
    return [Step(index=i, target_rps=t, throughput_rps=thr, p99_ms=p99) for i, (t, thr, p99) in enumerate(rows, 1)]


def test_mockup_limit_is_350():
    limit = find_limit(load_result(EXAMPLE), p99_threshold_ms=500)

    assert limit.limit_rps == 350
    assert limit.failed_step.index == 7
    assert len(limit.reasons) == 2  # и p99 812 > 500, и пропускная 338 -> 341


def test_cli_prints_limit(capsys):
    assert main([str(EXAMPLE)]) == 0
    assert "Предел: 350 запр/с" in capsys.readouterr().out


def test_p99_over_threshold_while_throughput_grows():
    limit = find_limit(steps((100, 100, 20), (200, 200, 90), (300, 300, 600)), p99_threshold_ms=500)

    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert limit.reasons == ("p99 600 мс выше порога 500 мс",)


def test_throughput_stalls_while_p99_is_fine():
    limit = find_limit(steps((100, 100, 20), (200, 190, 30), (300, 210, 40)), p99_threshold_ms=500)

    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert "пропускная выросла на 20" in limit.reasons[0]


def test_failure_on_first_step():
    limit = find_limit(steps((100, 30, 20), (200, 40, 30)), p99_threshold_ms=500)

    assert limit.found
    assert limit.limit_rps is None
    assert limit.failed_step.index == 1


def test_no_failure_gives_lower_bound():
    limit = find_limit(steps((100, 100, 20), (200, 199, 30)), p99_threshold_ms=500)

    assert not limit.found
    assert limit.limit_rps == 200


def test_target_must_increase():
    with pytest.raises(ValueError, match="должна расти"):
        find_limit(steps((200, 200, 20), (100, 100, 20)), p99_threshold_ms=500)


def test_empty_result():
    with pytest.raises(ValueError, match="нет ни одной ступени"):
        find_limit([], p99_threshold_ms=500)


def test_missing_field():
    with pytest.raises(ValueError, match="p99"):
        parse_steps({"steps": [{"index": 1, "target_rps": 100, "throughput_rps": 100, "latency_ms": {}}]})
