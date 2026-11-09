import json
import math
from pathlib import Path

import grpc
import pytest

from analyzer import server
from analyzer.diagnosis import diagnose
from analyzer.limit import find_limit
from analyzer.result import load_result
from bendmark.v1 import analyzer_pb2, analyzer_pb2_grpc, common_pb2

RESULTS = Path(__file__).resolve().parents[3] / "testdata" / "results"

# Файлы, на которых CLI завершается ошибкой, и ожидаемый статус gRPC
ERRORS = {
    "empty-aborted.json": grpc.StatusCode.INVALID_ARGUMENT,
    "decreasing-rps.json": grpc.StatusCode.INVALID_ARGUMENT,
    "zero-requests.json": grpc.StatusCode.FAILED_PRECONDITION,
}


@pytest.fixture(scope="module")
def stub():
    grpc_server, port = server.create_server("127.0.0.1:0")
    grpc_server.start()
    channel = grpc.insecure_channel(f"127.0.0.1:{port}")
    grpc.channel_ready_future(channel).result(timeout=5)
    yield analyzer_pb2_grpc.AnalyzerServiceStub(channel)
    channel.close()
    grpc_server.stop(0).wait(timeout=5)


def step_message(raw: dict) -> common_pb2.StepResult:
    latency = raw["latency_ms"]
    return common_pb2.StepResult(
        index=raw["index"],
        target_rps=raw["target_rps"],
        duration_seconds=raw["duration_seconds"],
        request_count=raw["request_count"],
        throughput_rps=raw["throughput_rps"],
        latency_ms=common_pb2.LatencyPercentiles(p50=latency["p50"], p90=latency["p90"], p99=latency["p99"]),
        errors=common_pb2.RequestErrors(count=raw["errors"]["count"], rate_percent=raw["errors"]["rate_percent"]),
        skipped_count=raw.get("skipped_count", 0),
    )


def fixture_steps(name: str) -> list[common_pb2.StepResult]:
    data = json.loads((RESULTS / name).read_text(encoding="utf-8"))
    return [step_message(raw) for raw in data["steps"]]


def request(steps, **options) -> analyzer_pb2.FindLimitRequest:
    if options:
        return analyzer_pb2.FindLimitRequest(steps=steps, options=analyzer_pb2.LimitOptions(**options))
    return analyzer_pb2.FindLimitRequest(steps=steps)


def call_error(stub, req) -> grpc.RpcError:
    with pytest.raises(grpc.RpcError) as error:
        stub.FindLimit(req, timeout=5)
    return error.value


@pytest.mark.parametrize("path", sorted(RESULTS.glob("*.json")), ids=lambda p: p.name)
def test_every_fixture_matches_cli(stub, path):
    req = request(fixture_steps(path.name))
    if path.name in ERRORS:
        assert call_error(stub, req).code() == ERRORS[path.name]
        return

    steps = load_result(path)
    expected = find_limit(steps, p99_threshold_ms=500)
    response = stub.FindLimit(req, timeout=5)
    limit = response.limit
    assert limit.found == expected.found
    assert limit.confirmed == expected.confirmed
    assert list(limit.reasons) == list(expected.reasons)
    assert limit.HasField("limit_rps") == (expected.limit_rps is not None)
    if expected.limit_rps is not None:
        assert limit.limit_rps == expected.limit_rps
    assert limit.HasField("failed_step_index") == expected.found
    if expected.found:
        assert limit.failed_step_index == expected.failed_step.index

    # Сервис отдаёт тот же диагноз, что и CLI
    diagnosis = diagnose(steps, expected, p99_threshold_ms=500)
    assert response.HasField("diagnosis")
    assert analyzer_pb2.DiagnosisStatus.Name(response.diagnosis.status) == f"DIAGNOSIS_STATUS_{diagnosis.status.name}"
    cause = f"CAUSE_{diagnosis.cause.name}" if diagnosis.cause else "CAUSE_UNSPECIFIED"
    assert analyzer_pb2.Cause.Name(response.diagnosis.cause) == cause
    confidence = f"CONFIDENCE_{diagnosis.confidence.name}" if diagnosis.confidence else "CONFIDENCE_UNSPECIFIED"
    assert analyzer_pb2.Confidence.Name(response.diagnosis.confidence) == confidence
    assert response.diagnosis.explanation == diagnosis.explanation
    assert list(response.diagnosis.evidence) == list(diagnosis.evidence)


def test_limit_found_is_350_at_step_7(stub):
    limit = stub.FindLimit(request(fixture_steps("limit-found.json")), timeout=5).limit
    assert (limit.found, limit.limit_rps, limit.failed_step_index, len(limit.reasons)) == (True, 350, 7, 3)


def test_no_limit_gives_lower_bound_without_failed_step(stub):
    limit = stub.FindLimit(request(fixture_steps("no-limit.json")), timeout=5).limit
    assert not limit.found
    assert limit.limit_rps == 200
    assert not limit.HasField("failed_step_index")
    assert list(limit.reasons) == []


def test_limit_found_is_unconfirmed_with_low_confidence(stub):
    response = stub.FindLimit(request(fixture_steps("limit-found.json")), timeout=5)
    assert response.limit.found
    assert not response.limit.confirmed
    assert response.diagnosis.status == analyzer_pb2.DIAGNOSIS_STATUS_DIAGNOSED
    assert response.diagnosis.confidence == analyzer_pb2.CONFIDENCE_LOW


def test_confirmed_saturation(stub):
    response = stub.FindLimit(request(fixture_steps("saturation.json")), timeout=5)
    assert (response.limit.confirmed, response.limit.limit_rps, response.limit.failed_step_index) == (True, 300, 4)
    assert response.diagnosis.cause == analyzer_pb2.CAUSE_SATURATION
    assert response.diagnosis.confidence == analyzer_pb2.CONFIDENCE_HIGH
    assert response.diagnosis.evidence


def test_no_limit_has_explicit_no_diagnosis(stub):
    diagnosis = stub.FindLimit(request(fixture_steps("outlier.json")), timeout=5).diagnosis
    assert diagnosis.status == analyzer_pb2.DIAGNOSIS_STATUS_NO_LIMIT
    assert diagnosis.cause == analyzer_pb2.CAUSE_UNSPECIFIED
    assert diagnosis.confidence == analyzer_pb2.CONFIDENCE_UNSPECIFIED
    assert diagnosis.explanation == "Диагноза нет: предел не найден."
    assert list(diagnosis.evidence) == []


def test_first_step_failure_has_no_limit_rps(stub):
    limit = stub.FindLimit(request(fixture_steps("first-step-failure.json")), timeout=5).limit
    assert limit.found
    assert not limit.HasField("limit_rps")
    assert limit.failed_step_index == 1
    assert limit.confirmed


def test_skipped_requests_are_failed_precondition(stub):
    steps = fixture_steps("limit-found.json")
    steps[1].skipped_count = 3
    error = call_error(stub, request(steps))
    assert error.code() == grpc.StatusCode.FAILED_PRECONDITION
    assert "ступень 2" in error.details()


def test_invalid_shape_wins_over_skipped_requests(stub):
    steps = fixture_steps("decreasing-rps.json")
    steps[0].skipped_count = 3
    assert call_error(stub, request(steps)).code() == grpc.StatusCode.INVALID_ARGUMENT


def test_explicit_zero_error_threshold_is_kept(stub):
    steps = fixture_steps("errors-below-threshold.json")
    assert not stub.FindLimit(request(steps), timeout=5).limit.found

    limit = stub.FindLimit(request(steps, max_error_percent=0), timeout=5).limit
    assert limit.found
    assert any("ошибок" in reason for reason in limit.reasons)


def test_omitted_thresholds_use_defaults(stub):
    steps = fixture_steps("p99-threshold.json")
    default = stub.FindLimit(request(steps), timeout=5).limit
    explicit = stub.FindLimit(request(steps, p99_threshold_ms=500, min_gain=0.5, max_error_percent=1), timeout=5).limit
    assert default == explicit

    relaxed = stub.FindLimit(request(steps, p99_threshold_ms=1000), timeout=5).limit
    assert not relaxed.found


@pytest.mark.parametrize("options", [
    {"p99_threshold_ms": 0},
    {"p99_threshold_ms": -1},
    {"p99_threshold_ms": math.nan},
    {"min_gain": 0},
    {"min_gain": math.inf},
    {"max_error_percent": -0.1},
    {"max_error_percent": 100.5},
], ids=str)
def test_invalid_thresholds(stub, options):
    error = call_error(stub, request(fixture_steps("no-limit.json"), **options))
    assert error.code() == grpc.StatusCode.INVALID_ARGUMENT


@pytest.mark.parametrize("indexes", [(2, 3), (1, 3), (0, 1), (1, 1)], ids=str)
def test_indexes_must_be_one_to_n(stub, indexes):
    steps = fixture_steps("no-limit.json")
    for step, index in zip(steps, indexes):
        step.index = index
    assert call_error(stub, request(steps)).code() == grpc.StatusCode.INVALID_ARGUMENT


def test_constant_load_is_invalid(stub):
    steps = fixture_steps("no-limit.json")
    steps[1].target_rps = steps[0].target_rps
    error = call_error(stub, request(steps))
    assert error.code() == grpc.StatusCode.INVALID_ARGUMENT
    assert "должна расти" in error.details()


def test_more_errors_than_requests_is_invalid(stub):
    steps = fixture_steps("no-limit.json")
    steps[0].errors.count = steps[0].request_count + 1
    assert call_error(stub, request(steps)).code() == grpc.StatusCode.INVALID_ARGUMENT


def test_non_finite_metrics_are_invalid(stub):
    steps = fixture_steps("no-limit.json")
    steps[1].latency_ms.p99 = math.inf
    assert call_error(stub, request(steps)).code() == grpc.StatusCode.INVALID_ARGUMENT
