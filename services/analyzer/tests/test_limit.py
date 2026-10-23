import json
from pathlib import Path

import pytest

from analyzer.__main__ import main
from analyzer.limit import find_limit
from analyzer.result import Step, load_result, load_run_result, parse_steps

RESULTS = Path(__file__).resolve().parents[3] / "testdata" / "results"
EXAMPLE = RESULTS / "limit-found.json"


def read_fixture(name: str) -> dict:
    return json.loads((RESULTS / name).read_text(encoding="utf-8"))


def steps(*rows: tuple) -> list[Step]:
    """Compact inputs for unit checks at exact algorithm thresholds."""
    result = []
    for index, (target, throughput, p99, *rest) in enumerate(rows, 1):
        count = int(throughput * 10)
        error_percent = rest[0] if rest else 0
        result.append(Step(index, target, throughput, p99, count, int(count * error_percent / 100)))
    return result


def raw_step(**overrides) -> dict:
    # A fresh fixture read keeps mutation tests independent without copying the format.
    step = read_fixture("limit-found.json")["steps"][0]
    step.update(overrides)
    return step


def test_limit_found_fixture_is_350():
    limit = find_limit(load_result(EXAMPLE), p99_threshold_ms=500)

    assert limit.limit_rps == 350
    assert limit.failed_step.index == 7
    assert len(limit.reasons) == 3
    # Рампа кончилась на первой провальной ступени: второй, подтверждающей, нет
    assert not limit.confirmed


def test_cli_prints_limit(capsys):
    assert main([str(EXAMPLE)]) == 0
    assert "Предел: 350 запр/с" in capsys.readouterr().out


def test_p99_over_threshold_while_throughput_grows():
    limit = find_limit(load_result(RESULTS / "p99-threshold.json"), p99_threshold_ms=500)

    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert limit.reasons == ("ступень 3: p99 600 мс выше порога 500 мс",)


def test_throughput_stalls_while_p99_is_fine():
    limit = find_limit(load_result(RESULTS / "throughput-stall.json"), p99_threshold_ms=500)

    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert limit.reasons[0].startswith("ступень 3: пропускная выросла на 20")


def test_fast_errors_while_throughput_grows():
    limit = find_limit(load_result(RESULTS / "fast-http-errors.json"), p99_threshold_ms=500)

    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert limit.reasons == ("ступень 3: ошибок 30.0 % при пороге 1 %",)


def test_errors_below_threshold_are_not_failure():
    limit = find_limit(load_result(RESULTS / "errors-below-threshold.json"), p99_threshold_ms=500)

    assert not limit.found


def test_failure_on_first_step():
    limit = find_limit(load_result(RESULTS / "first-step-failure.json"), p99_threshold_ms=500)

    assert limit.found
    assert limit.confirmed
    assert limit.limit_rps is None
    assert limit.failed_step.index == 1


# Отказ подтверждает вторая подряд провальная ступень

def test_two_failures_in_a_row_confirm_the_limit():
    limit = find_limit(load_result(RESULTS / "rejecting.json"), p99_threshold_ms=500)

    assert limit.found
    assert limit.confirmed
    assert limit.limit_rps == 200
    assert limit.failed_step.index == 3
    assert [step.index for step in limit.failed_steps] == [3, 4]
    assert limit.reasons == (
        "ступень 3: ошибок 30.0 % при пороге 1 %",
        "ступень 4: ошибок 45.0 % при пороге 1 %",
    )


def test_single_outlier_mid_run_is_not_a_limit():
    limit = find_limit(load_result(RESULTS / "outlier.json"), p99_threshold_ms=500)

    assert not limit.found
    assert not limit.confirmed
    assert limit.failed_step is None
    assert limit.limit_rps == 400
    assert len(limit.reasons) == 1
    assert limit.reasons[0].startswith("предупреждение: ступень 2 - одиночный отказ")
    assert "p99 700 мс выше порога 500 мс" in limit.reasons[0]


def test_single_failure_on_last_step_is_unconfirmed_limit():
    limit = find_limit(load_result(RESULTS / "p99-threshold.json"), p99_threshold_ms=500)

    assert limit.found
    assert not limit.confirmed
    assert limit.limit_rps == 200
    assert [step.index for step in limit.failed_steps] == [3]


def test_outlier_before_confirmed_limit_stays_a_warning():
    # 2 - выброс, 3 в норме, 4 и 5 - подтверждённый отказ: предел - ступень 3
    limit = find_limit(steps((100, 100, 20), (200, 200, 700), (300, 300, 30), (400, 400, 800), (500, 500, 900)),
                       p99_threshold_ms=500)

    assert limit.confirmed
    assert limit.limit_rps == 300
    assert limit.failed_step.index == 4
    assert limit.reasons[:2] == ("ступень 4: p99 800 мс выше порога 500 мс", "ступень 5: p99 900 мс выше порога 500 мс")
    assert limit.reasons[2].startswith("предупреждение: ступень 2")


def test_outlier_followed_by_unconfirmed_failure():
    # Выброс и неподтверждённый отказ на последней ступени: предел - ступень перед последней
    limit = find_limit(steps((100, 100, 20), (200, 200, 700), (300, 300, 30), (400, 400, 800)), p99_threshold_ms=500)

    assert limit.found
    assert not limit.confirmed
    assert limit.limit_rps == 300
    assert limit.failed_step.index == 4
    assert limit.reasons[-1].startswith("предупреждение: ступень 2")


def test_failed_first_step_then_normal_is_an_outlier():
    limit = find_limit(steps((100, 100, 700), (200, 200, 20)), p99_threshold_ms=500)

    assert not limit.found
    assert limit.limit_rps == 200
    assert limit.reasons[0].startswith("предупреждение: ступень 1")


def test_single_failed_step_only_is_unconfirmed_below_first_step():
    limit = find_limit(steps((100, 100, 700)), p99_threshold_ms=500)

    assert limit.found
    assert not limit.confirmed
    assert limit.limit_rps is None


def test_no_failure_gives_lower_bound():
    limit = find_limit(load_result(RESULTS / "no-limit.json"), p99_threshold_ms=500)

    assert not limit.found
    assert limit.limit_rps == 200


def test_target_must_increase():
    with pytest.raises(ValueError, match="должна расти"):
        find_limit(load_result(RESULTS / "decreasing-rps.json"), p99_threshold_ms=500)


def test_empty_result():
    with pytest.raises(ValueError, match="нет ни одной ступени"):
        find_limit(load_result(RESULTS / "empty-aborted.json"), p99_threshold_ms=500)


def test_aborted_run_is_reported_as_incomplete(capsys):
    run = load_run_result(RESULTS / "aborted.json")
    assert run.status == "aborted"
    limit = find_limit(run.steps, p99_threshold_ms=500)

    assert not limit.found
    assert limit.limit_rps == 150
    assert limit.failed_step is None
    assert main([str(RESULTS / "aborted.json")]) == 0
    output = capsys.readouterr().out
    assert "Прогон прерван" in output
    assert "только завершённых ступеней" in output
    assert "предел не ниже 150" in output
    assert "все ступени" not in output
    assert "Диагноза нет: предел не найден" in output


def test_empty_aborted_run_has_no_limit_estimate(capsys):
    assert main([str(RESULTS / "empty-aborted.json")]) == 0
    assert "нет завершённых ступеней, предел определить нельзя" in capsys.readouterr().out


@pytest.mark.parametrize("error_percent, failure", [(1.0, False), (1.1, True)])
def test_error_threshold_boundary(error_percent, failure):
    limit = find_limit(steps((100, 100, 20, error_percent)), p99_threshold_ms=500)
    assert limit.found is failure


@pytest.mark.parametrize("throughput, failure", [(50, False), (49, True)])
def test_gain_threshold_boundary(throughput, failure):
    limit = find_limit(steps((100, throughput, 20)), p99_threshold_ms=500, min_gain=0.5)
    assert limit.found is failure


@pytest.mark.parametrize("p99, failure", [(500, False), (501, True)])
def test_p99_threshold_boundary(p99, failure):
    limit = find_limit(steps((100, 100, p99)), p99_threshold_ms=500)
    assert limit.found is failure


def test_zero_requests_are_not_reported_as_a_service_limit(capsys):
    assert main([str(RESULTS / "zero-requests.json")]) == 1
    assert "нет завершившихся запросов, предел сервиса определить нельзя" in capsys.readouterr().err


@pytest.mark.parametrize("path", sorted(RESULTS.glob("*.json")), ids=lambda path: path.name)
def test_result_fixtures_have_consistent_metrics(path):
    data = json.loads(path.read_text(encoding="utf-8"))
    assert data["status"] in {"completed", "aborted"}
    for step in data["steps"]:
        count = step["request_count"]
        assert isinstance(count, int) and not isinstance(count, bool) and count >= 0
        assert step["throughput_rps"] == pytest.approx(count / step["duration_seconds"])
        assert 0 <= step["errors"]["count"] <= count
        # Example files round the error percentage to one decimal place.
        error_percent = 100 * step["errors"]["count"] / count if count else 0
        assert step["errors"]["rate_percent"] == pytest.approx(
            error_percent, abs=0.05
        )
        latency = step["latency_ms"]
        assert 0 <= latency["p50"] <= latency["p90"] <= latency["p99"]


def test_unknown_run_status_is_rejected(tmp_path):
    data = read_fixture("no-limit.json")
    data["status"] = "running"
    path = tmp_path / "result.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ValueError, match="поле status должно быть completed или aborted"):
        load_run_result(path)


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
