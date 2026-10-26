from pathlib import Path

import pytest

from analyzer.__main__ import main
from analyzer.diagnosis import Cause, Confidence, Status, describe_diagnosis, diagnose
from analyzer.limit import find_limit
from analyzer.result import Step, load_result

RESULTS = Path(__file__).resolve().parents[3] / "testdata" / "results"

DIAGNOSED = Status.DIAGNOSED
# Ожидаемый диагноз для каждого файла testdata/results/*: (статус, причина, уверенность).
# Строка с ValueError - файл, который find_limit отклоняет, диагноза для него нет
EXPECTED = {
    "aborted.json": (Status.NO_LIMIT, None, None),
    "decreasing-rps.json": ValueError,
    "empty-aborted.json": ValueError,
    "errors-below-threshold.json": (Status.NO_LIMIT, None, None),
    "fast-http-errors.json": (DIAGNOSED, Cause.REJECTING, Confidence.LOW),
    "first-step-failure.json": (Status.TOO_FEW_STEPS, None, None),
    "inconclusive.json": (DIAGNOSED, Cause.INCONCLUSIVE, Confidence.LOW),
    "latency-jump.json": (DIAGNOSED, Cause.SLOW_RESPONSES, Confidence.MEDIUM),
    "limit-found.json": (DIAGNOSED, Cause.SLOW_RESPONSES, Confidence.LOW),
    "no-limit.json": (Status.NO_LIMIT, None, None),
    "outlier.json": (Status.NO_LIMIT, None, None),
    "p99-threshold.json": (DIAGNOSED, Cause.SLOW_RESPONSES, Confidence.LOW),
    "rejecting.json": (DIAGNOSED, Cause.REJECTING, Confidence.HIGH),
    "saturation.json": (DIAGNOSED, Cause.SATURATION, Confidence.HIGH),
    "saturation-high-latency.json": (DIAGNOSED, Cause.SATURATION, Confidence.HIGH),
    "slow-responses.json": (DIAGNOSED, Cause.SLOW_RESPONSES, Confidence.HIGH),
    "throughput-stall.json": (DIAGNOSED, Cause.SATURATION, Confidence.LOW),
    "zero-requests.json": ValueError,
}


def analyze(rows: list[Step]):
    limit = find_limit(rows, p99_threshold_ms=500)
    return limit, diagnose(rows, limit, p99_threshold_ms=500)


def steps(*rows: tuple) -> list[Step]:
    """(target, throughput, p99[, error_percent]) по 10 с на ступень."""
    result = []
    for index, (target, throughput, p99, *rest) in enumerate(rows, 1):
        count = int(throughput * 10)
        error_percent = rest[0] if rest else 0
        result.append(Step(index, target, throughput, p99, count, int(count * error_percent / 100)))
    return result


def test_every_result_fixture_has_an_expected_diagnosis():
    assert sorted(EXPECTED) == sorted(path.name for path in RESULTS.glob("*.json"))


@pytest.mark.parametrize("name", sorted(EXPECTED))
def test_fixture_diagnosis(name):
    expected = EXPECTED[name]
    if expected is ValueError:
        with pytest.raises(ValueError):
            analyze(load_result(RESULTS / name))
        return

    status, cause, confidence = expected
    _, diagnosis = analyze(load_result(RESULTS / name))
    assert (diagnosis.status, diagnosis.cause, diagnosis.confidence) == (status, cause, confidence)
    assert diagnosis.explanation
    # Признаки есть ровно тогда, когда есть диагноз
    assert bool(diagnosis.evidence) is (status is DIAGNOSED)


def test_no_limit_is_explicit():
    _, diagnosis = analyze(load_result(RESULTS / "no-limit.json"))
    assert diagnosis.status is Status.NO_LIMIT
    assert diagnosis.cause is None and diagnosis.confidence is None
    assert diagnosis.explanation == "Диагноза нет: предел не найден."


def test_too_few_steps_before_failure():
    # Отказ подтверждён на ступенях 2-3, до него одна ступень - тренда не видно
    limit, diagnosis = analyze(steps((100, 100, 20), (200, 200, 900, 5), (300, 300, 950, 8)))
    assert limit.confirmed
    assert diagnosis.status is Status.TOO_FEW_STEPS
    assert "не с чем сравнить" in diagnosis.explanation


def test_rejecting_evidence_names_errors_and_low_latency():
    _, diagnosis = analyze(load_result(RESULTS / "rejecting.json"))
    assert diagnosis.explanation.startswith("Сервис отклоняет нагрузку")
    assert "ступень 3: ошибок 30.0 % (на ступени 2 - 0.0 %), выше порога 1 %" in diagnosis.evidence
    assert "ступень 3: p99 25 → 16 мс - ниже порога 500 мс" in diagnosis.evidence


def test_slow_responses_sees_latency_ceiling():
    _, diagnosis = analyze(load_result(RESULTS / "slow-responses.json"))
    assert "ступень 4: p99 60 → 5000 мс - скачок в 83.3 раза" in diagnosis.evidence
    assert (
        "ступень 5: p99 5000 → 5000 мс - держится у потолка, как на предыдущей ступени, "
        "запросы упираются в таймаут"
    ) in diagnosis.evidence


@pytest.mark.parametrize("last_p99", [225, 250])
def test_smooth_saturation_above_p99_threshold_is_not_a_ceiling(last_p99):
    # Случай из ревью #57: порог 200 мс, p99 120 -> 180 -> 210 -> 225/250, пропускная встала
    # на 210-212, ошибок нет. Без таймаутов плато p99 - насыщение, а не медленные ответы,
    # и разница в 15 или 40 мс прироста не должна менять диагноз
    rows = steps((100, 100, 120), (200, 200, 180), (300, 210, 210), (400, 212, last_p99))
    limit = find_limit(rows, p99_threshold_ms=200)
    diagnosis = diagnose(rows, limit, p99_threshold_ms=200)

    assert limit.confirmed
    assert (diagnosis.cause, diagnosis.confidence) == (Cause.SATURATION, Confidence.HIGH)
    assert not any("потолка" in line for line in diagnosis.evidence)


def test_saturation_above_threshold_fixture_is_not_a_ceiling():
    _, diagnosis = analyze(load_result(RESULTS / "saturation-high-latency.json"))
    assert "ступень 4: p99 520 → 560 мс - плавный рост выше порога 500 мс" in diagnosis.evidence


@pytest.mark.parametrize("error_percent, cause", [(0.9, Cause.SATURATION), (1.0, Cause.SLOW_RESPONSES)])
def test_ceiling_needs_at_least_one_percent_timeouts(error_percent, cause):
    # p99 у таймаута значит, что таймаутом кончился хотя бы 1 % запросов
    # 2000 запросов на ступени 4: 0.9 и 1.0 % - ровно 18 и 20 ошибок
    rows = steps((100, 100, 120), (200, 200, 180), (300, 210, 5000, 0.5), (400, 200, 5000, error_percent))
    limit = find_limit(rows, p99_threshold_ms=500, max_error_percent=5)
    diagnosis = diagnose(rows, limit, p99_threshold_ms=500, max_error_percent=5)

    assert limit.confirmed
    # Ступень 3 - скачок p99, ступень 4 - потолок или плавный рост
    expected = Cause.SLOW_RESPONSES if cause is Cause.SLOW_RESPONSES else Cause.INCONCLUSIVE
    assert diagnosis.cause is expected


def test_latency_jump_without_errors_is_medium():
    # Задержка растёт скачками, но таймаутов (ошибок) нет: признаки неполные
    _, diagnosis = analyze(load_result(RESULTS / "latency-jump.json"))
    assert diagnosis.confidence is Confidence.MEDIUM
    assert not any("ошибок" in line for line in diagnosis.evidence)


def test_saturation_evidence_names_throughput_ceiling():
    _, diagnosis = analyze(load_result(RESULTS / "saturation.json"))
    assert diagnosis.explanation.startswith("Насыщение")
    assert any(line.startswith("ступень 5: пропускная 285 запр/с при цели 500") for line in diagnosis.evidence)


def test_contradicting_failed_steps_are_inconclusive():
    _, diagnosis = analyze(load_result(RESULTS / "inconclusive.json"))
    assert diagnosis.explanation.startswith("Причину определить не удалось")
    assert diagnosis.evidence[-1] == (
        "ступени с отказом указывают на разное: ступень 3 - отклонение нагрузки, ступень 4 - насыщение"
    )


def test_unconfirmed_limit_gives_probable_cause_with_low_confidence():
    _, diagnosis = analyze(load_result(RESULTS / "fast-http-errors.json"))
    assert diagnosis.cause is Cause.REJECTING
    assert diagnosis.confidence is Confidence.LOW
    assert diagnosis.explanation.startswith("Вероятно, сервис отклоняет нагрузку")
    assert "Предел не подтверждён: прогон закончился на ступени 3" in diagnosis.explanation


def test_throughput_stall_without_latency_growth_matches_no_rule():
    # Пропускная встала, а p99 не растёт и ошибок нет: ни одно правило не подходит
    _, diagnosis = analyze(steps((100, 100, 30), (200, 200, 30), (300, 300, 30), (400, 310, 30), (500, 315, 30)))
    assert diagnosis.cause is Cause.INCONCLUSIVE
    assert diagnosis.confidence is Confidence.LOW


def test_rejecting_with_stalled_throughput_is_medium():
    # Быстрые ошибки, но пропускная тоже встала - признаки неполные
    _, diagnosis = analyze(steps((100, 100, 20), (200, 200, 22), (300, 220, 18, 30), (400, 230, 17, 40)))
    assert (diagnosis.cause, diagnosis.confidence) == (Cause.REJECTING, Confidence.MEDIUM)


def test_smooth_latency_growth_with_errors_is_medium_slow_responses():
    _, diagnosis = analyze(steps((100, 100, 200), (200, 200, 400), (300, 300, 600, 2), (400, 400, 800, 3)))
    assert (diagnosis.cause, diagnosis.confidence) == (Cause.SLOW_RESPONSES, Confidence.MEDIUM)
    assert "ступень 3: p99 400 → 600 мс - плавный рост выше порога 500 мс" in diagnosis.evidence


def test_cli_prints_the_same_diagnosis_as_the_service(capsys):
    rows = load_result(RESULTS / "saturation.json")
    _, diagnosis = analyze(rows)

    assert main([str(RESULTS / "saturation.json")]) == 0
    output = capsys.readouterr().out
    assert describe_diagnosis(diagnosis) in output
    assert "Диагноз (уверенность высокая): Насыщение" in output
    assert "Отказ на ступенях 4 и 5 (400 и 500 запр/с), подтверждён второй ступенью" in output


def test_cli_reports_unconfirmed_limit(capsys):
    assert main([str(RESULTS / "limit-found.json")]) == 0
    output = capsys.readouterr().out
    assert "Предел: 350 запр/с" in output
    assert "не подтверждён" in output
    assert "Диагноз (уверенность низкая): Вероятно" in output


def test_cli_reports_missing_diagnosis(capsys):
    assert main([str(RESULTS / "first-step-failure.json")]) == 0
    assert "Диагноза нет: до первой ступени с отказом" in capsys.readouterr().out
