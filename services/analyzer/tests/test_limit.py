import json
from pathlib import Path

import pytest

from analyzer.__main__ import main
from analyzer.limit import find_limit
from analyzer.result import Step, load_result, parse_steps

EXAMPLE = Path(__file__).resolve().parents[3] / "examples" / "result.json"


def steps(*rows: tuple) -> list[Step]:
    result = []
    for i, (target, throughput, p99, *rest) in enumerate(rows, 1):
        request_count = int(throughput * 10)
        error_percent = rest[0] if rest else 0
        result.append(
            Step(
                index=i,
                target_rps=target,
                throughput_rps=throughput,
                p99_ms=p99,
                request_count=request_count,
                error_count=int(request_count * error_percent / 100),
            )
        )
    return result


def raw_step(**overrides) -> dict:
    step = {
        "index": 1,
        "target_rps": 100,
        "duration_seconds": 45,
        "request_count": 4500,
        "throughput_rps": 100.0,
        "latency_ms": {"p50": 12, "p90": 18, "p99": 22},
        "errors": {"count": 0, "rate_percent": 0.0},
    }
    step.update(overrides)
    return step

def test_mockup_limit_is_350():
    limit = find_limit(load_result(EXAMPLE), p99_threshold_ms=500)

    assert limit.limit_rps == 350
    assert limit.failed_step.index == 7
    assert len(limit.reasons) == 3


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


def test_fast_errors_while_throughput_grows():
    limit = find_limit(steps((100, 100, 20), (200, 200, 25), (300, 300, 15, 30)), p99_threshold_ms=500)

    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert limit.reasons == ("ошибок 30.0 % при пороге 1 %",)


def test_errors_below_threshold_are_not_failure():
    limit = find_limit(steps((100, 100, 20), (200, 200, 25, 0.5)), p99_threshold_ms=500)

    assert not limit.found

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
    with pytest.raises(ValueError, match="нет поля latency_ms.p99"):
        parse_steps({"steps": [raw_step(latency_ms={})]})


@pytest.mark.parametrize("bad", [None, "812", True, float("nan"), -5])
def test_p99_must_be_number(bad):
    with pytest.raises(ValueError, match="ступень 1: поле latency_ms.p99"):
        parse_steps({"steps": [raw_step(latency_ms={"p99": bad})]})


def test_request_count_must_be_integer():
    with pytest.raises(ValueError, match="request_count должно быть целым числом"):
        parse_steps({"steps": [raw_step(request_count=4500.5)]})


def test_more_errors_than_requests():
    with pytest.raises(ValueError, match="errors.count больше request_count"):
        parse_steps({"steps": [raw_step(errors={"count": 5000, "rate_percent": 100.0})]})


def test_no_steps_list():
    with pytest.raises(ValueError, match="нет списка steps"):
        parse_steps({"steps": None})


def test_cli_reports_null_without_traceback(tmp_path, capsys):
    broken = tmp_path / "result.json"
    broken.write_text(json.dumps({"steps": [raw_step(latency_ms={"p99": None})]}), encoding="utf-8")

    assert main([str(broken)]) == 1
    assert "поле latency_ms.p99 должно быть числом, а не null" in capsys.readouterr().err
