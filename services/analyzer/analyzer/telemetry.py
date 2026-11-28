"""Подтверждение причины отказа по снимкам нагружаемого сервиса."""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from statistics import median
from typing import TYPE_CHECKING

from .diagnosis import Cause, Confidence, Diagnosis, Status
from .limit import LimitResult
from .result import Step

if TYPE_CHECKING:
    from bendmark.v1.analyzer_pb2 import StepTelemetry, TargetSnapshot

# Допускаем колебания скорости между окнами замера.
RATE_TOLERANCE = 0.20


@dataclass(frozen=True)
class _Window:
    first: TargetSnapshot
    last: TargetSnapshot
    seconds: float
    rate: float

    @classmethod
    def measure(cls, samples: list[TargetSnapshot]) -> _Window | None:
        measured = [sample for sample in samples if sample.HasField("completed_total")]
        if len(measured) < 2:
            return None
        first, last = measured[0], measured[-1]
        if any(
            sample.started_unix_us != first.started_unix_us
            or not 0 < sample.started_unix_us <= sample.captured_unix_us
            for sample in samples
        ):
            return None
        if any(a.captured_unix_us >= b.captured_unix_us for a, b in zip(samples, samples[1:])):
            return None
        if first.completed_total < 0 or any(
            a.completed_total > b.completed_total for a, b in zip(measured, measured[1:])
        ):
            return None
        seconds = (last.captured_unix_us - first.captured_unix_us) / 1_000_000
        return cls(first, last, seconds, (last.completed_total - first.completed_total) / seconds)

    def describe_rate(self) -> str:
        return (
            f"{self.rate:.2f} запр/с "
            f"(completed_total {self.first.completed_total} → {self.last.completed_total} за {self.seconds:.3f} с)"
        )


def confirm_saturation(
    steps: list[Step], limit: LimitResult, diagnosis: Diagnosis, groups: list[StepTelemetry], min_gain: float
) -> Diagnosis:
    if not limit.found or not groups:
        return diagnosis

    position = steps.index(limit.failed_step)
    if position == 0:
        return diagnosis

    counts = Counter(group.step_index for group in groups)
    samples = {group.step_index: list(group.snapshots) for group in groups if counts[group.step_index] == 1}
    evidence = []
    for offset, step in enumerate(limit.failed_steps):
        previous = steps[position + offset - 1]
        lines = _compare(previous, step, samples.get(previous.index, []), samples.get(step.index, []), min_gain)
        if lines is None:
            return diagnosis
        evidence.extend(lines)

    signs = "Пул заполнен, очередь растёт или полна, скорость обработки вышла на плато."
    if limit.confirmed:
        explanation = f"Телеметрия подтверждает насыщение сервиса. {signs}"
    else:
        explanation = (
            f"Вероятно, сервис достиг насыщения. {signs} "
            f"Предел оценён по одной провальной ступени {limit.failed_step.index}."
        )
    return Diagnosis(
        Status.DIAGNOSED,
        explanation,
        cause=Cause.SATURATION,
        confidence=Confidence.HIGH if limit.confirmed else Confidence.LOW,
        evidence=tuple(evidence),
    )


def _compare(
    previous: Step, step: Step, baseline: list[TargetSnapshot], current: list[TargetSnapshot], min_gain: float
) -> tuple[str, str, str] | None:
    before, after = _Window.measure(baseline), _Window.measure(current)
    if before is None or after is None:
        return None
    if (
        before.last.started_unix_us != after.first.started_unix_us
        or before.last.captured_unix_us >= after.first.captured_unix_us
        or before.last.completed_total > after.first.completed_total
        or before.rate <= 0
        or after.rate <= 0
    ):
        return None

    load_gain = step.target_rps - previous.target_rps
    rate_gain = after.rate - before.rate
    if rate_gain / load_gain >= min_gain or abs(rate_gain) > before.rate * RATE_TOLERANCE:
        return None

    pool = _pool_evidence(current)
    queue = _queue_evidence(baseline, current)
    if pool is None or queue is None:
        return None
    return (
        f"Телеметрия, ступени {previous.index} → {step.index}: нагрузка {previous.target_rps:g} → "
        f"{step.target_rps:g} запр/с, скорость обработки {before.describe_rate()} → {after.describe_rate()}.",
        f"Телеметрия, ступень {step.index}: {pool}.",
        f"Телеметрия, ступень {step.index}: {queue}.",
    )


def _pool_evidence(samples: list[TargetSnapshot]) -> str | None:
    known = [
        sample for sample in samples
        if sample.HasField("workers_busy") and sample.HasField("workers_total")
        and 0 <= sample.workers_busy <= sample.workers_total and sample.workers_total > 0
    ]
    full = [sample.workers_total for sample in known if sample.workers_busy == sample.workers_total]
    if len(full) < 2 or len(full) * 3 < len(known) * 2:
        return None
    sizes = ", ".join(f"{size}/{size}" for size in sorted(set(full)))
    return f"пул заполнен ({sizes} обработчиков) в {len(full)} из {len(known)} снимков с замером пула"


def _queue_lengths(samples: list[TargetSnapshot]) -> list[int]:
    return [
        sample.queue_length for sample in samples
        if sample.HasField("queue_length") and sample.queue_length >= 0
        and (not sample.HasField("queue_capacity") or sample.queue_length <= sample.queue_capacity)
    ]


def _queue_evidence(baseline: list[TargetSnapshot], current: list[TargetSnapshot]) -> str | None:
    lengths = _queue_lengths(current)
    if len(lengths) < 2:
        return None
    if lengths[-1] > lengths[0]:
        return f"очередь выросла с {lengths[0]} до {lengths[-1]} запросов за ступень"

    previous_lengths = _queue_lengths(baseline)
    if len(previous_lengths) >= 2 and median(lengths) > median(previous_lengths):
        return f"медиана длины очереди выросла с {median(previous_lengths):g} до {median(lengths):g} запросов"

    full = [
        sample.queue_capacity for sample in current
        if sample.HasField("queue_length") and sample.HasField("queue_capacity")
        and sample.queue_length == sample.queue_capacity and sample.queue_capacity > 0
    ]
    if len(full) >= 2:
        sizes = ", ".join(f"{size}/{size}" for size in sorted(set(full)))
        return f"очередь заполнена ({sizes} запросов) в {len(full)} из {len(lengths)} снимков с замером очереди"
    return None
