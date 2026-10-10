"""gRPC-сервис анализатора: FindLimit по контракту proto/bendmark/v1/analyzer.proto.

Запуск из services/analyzer (после python gen_proto.py):
    python -m analyzer.server --listen 0.0.0.0:50052
"""

import argparse
import math
import signal
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import grpc

from .limit import LimitResult, find_limit
from .result import Step

GENERATED = Path(__file__).resolve().parents[1] / "generated"
if not (GENERATED / "bendmark" / "v1" / "analyzer_pb2.py").exists():
    raise ImportError(f"Нет кода из proto в {GENERATED}: выполните python gen_proto.py в services/analyzer")
if str(GENERATED) not in sys.path:
    sys.path.insert(0, str(GENERATED))

from bendmark.v1 import analyzer_pb2, analyzer_pb2_grpc  # noqa: E402

DEFAULT_P99_MS = 500.0
DEFAULT_MIN_GAIN = 0.5
DEFAULT_MAX_ERROR_PERCENT = 1.0


class AnalysisError(Exception):
    """Запрос нельзя проанализировать; code - статус gRPC для ответа."""

    def __init__(self, code: grpc.StatusCode, message: str):
        super().__init__(message)
        self.code = code


def _invalid(message: str) -> AnalysisError:
    return AnalysisError(grpc.StatusCode.INVALID_ARGUMENT, message)


def _thresholds(options) -> dict:
    """Отсутствующее optional-поле - порог по умолчанию; явный ноль сохраняется."""
    p99 = options.p99_threshold_ms if options.HasField("p99_threshold_ms") else DEFAULT_P99_MS
    gain = options.min_gain if options.HasField("min_gain") else DEFAULT_MIN_GAIN
    errors = options.max_error_percent if options.HasField("max_error_percent") else DEFAULT_MAX_ERROR_PERCENT
    if not math.isfinite(p99) or p99 <= 0:
        raise _invalid(f"порог p99 должен быть конечным числом больше нуля, а не {p99:g}")
    if not math.isfinite(gain) or gain <= 0:
        raise _invalid(f"min_gain должен быть конечным числом больше нуля, а не {gain:g}")
    if not math.isfinite(errors) or not 0 <= errors <= 100:
        raise _invalid(f"порог ошибок должен быть от 0 до 100 %, а не {errors:g}")
    return {"p99_threshold_ms": p99, "min_gain": gain, "max_error_percent": errors}


def _to_steps(results) -> list[Step]:
    """StepResult -> Step с проверкой формы: всё, что ломает контракт, - INVALID_ARGUMENT."""
    if not results:
        raise _invalid("нет ни одной ступени")

    steps = []
    previous_target = 0.0
    for position, result in enumerate(results, 1):
        where = f"ступень {position}"
        if result.index != position:
            raise _invalid(f"{where}: индекс {result.index}, а ступени должны идти по порядку с индексами 1..N")
        values = {
            "target_rps": result.target_rps,
            "duration_seconds": result.duration_seconds,
            "throughput_rps": result.throughput_rps,
            "latency_ms.p99": result.latency_ms.p99,
        }
        for name, value in values.items():
            if not math.isfinite(value) or value < 0:
                raise _invalid(f"{where}: {name} должно быть неотрицательным конечным числом, а не {value:g}")
        if result.target_rps <= 0 or result.duration_seconds <= 0:
            raise _invalid(f"{where}: target_rps и duration_seconds должны быть больше нуля")
        if result.target_rps <= previous_target:
            raise _invalid(
                f"целевая нагрузка должна расти от ступени к ступени, а на ступени {position} "
                f"она {result.target_rps:g} после {previous_target:g}"
            )
        if result.errors.count > result.request_count:
            raise _invalid(f"{where}: errors.count больше request_count")
        previous_target = result.target_rps
        steps.append(Step(
            index=result.index,
            target_rps=result.target_rps,
            throughput_rps=result.throughput_rps,
            p99_ms=result.latency_ms.p99,
            request_count=result.request_count,
            error_count=result.errors.count,
        ))
    return steps


def _check_measurable(results) -> None:
    """Пропуски агента и ступени без ответов не определяют предел сервиса."""
    for result in results:
        if result.skipped_count > 0:
            raise AnalysisError(
                grpc.StatusCode.FAILED_PRECONDITION,
                f"ступень {result.index}: агент пропустил {result.skipped_count} запросов; "
                "эти измерения не определяют предел сервиса",
            )
        if result.request_count == 0:
            raise AnalysisError(
                grpc.StatusCode.FAILED_PRECONDITION,
                f"ступень {result.index}: нет завершившихся запросов, предел сервиса определить нельзя",
            )


def _to_limit(limit: LimitResult):
    fields: dict[str, object] = {"found": limit.found, "reasons": list(limit.reasons)}
    if limit.limit_rps is not None:
        fields["limit_rps"] = limit.limit_rps
    if limit.failed_step is not None:
        fields["failed_step_index"] = limit.failed_step.index
    return analyzer_pb2.Limit(**fields)


def analyze(request) -> "analyzer_pb2.FindLimitResponse":
    """Чистая логика FindLimit без gRPC; ошибки - AnalysisError с нужным статусом."""
    steps = _to_steps(request.steps)
    thresholds = _thresholds(request.options)
    # Проверяется до find_limit: сообщения ValueError не различаем
    _check_measurable(request.steps)
    try:
        limit = find_limit(steps, **thresholds)
    except ValueError as error:
        raise _invalid(str(error)) from error
    return analyzer_pb2.FindLimitResponse(limit=_to_limit(limit))


class AnalyzerService(analyzer_pb2_grpc.AnalyzerServiceServicer):
    def FindLimit(self, request, context):
        try:
            return analyze(request)
        except AnalysisError as error:
            context.abort(error.code, str(error))


def create_server(listen: str, max_workers: int = 4) -> tuple[grpc.Server, int]:
    server = grpc.server(ThreadPoolExecutor(max_workers=max_workers))
    analyzer_pb2_grpc.add_AnalyzerServiceServicer_to_server(AnalyzerService(), server)
    port = server.add_insecure_port(listen)
    if not port:
        raise SystemExit(f"Не удалось занять адрес {listen}")
    return server, port


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="analyzer.server", description="gRPC-сервис анализатора Bendmark")
    parser.add_argument("--listen", default="0.0.0.0:50052", help="адрес gRPC (по умолчанию 0.0.0.0:50052)")
    args = parser.parse_args(argv)

    server, _ = create_server(args.listen)
    server.start()
    # docker stop шлёт SIGTERM: даём текущим запросам завершиться
    def stop(*_) -> None:
        server.stop(grace=2)

    signal.signal(signal.SIGTERM, stop)
    print(f"Анализатор слушает {args.listen}", flush=True)
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        server.stop(grace=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
