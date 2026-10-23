"""Диагноз v1: почему сервис упёрся в предел, только по внешним признакам из StepResult.

Метрик самого сервиса нет, поэтому правила смотрят на ступени с отказом и
сравнивают их с предыдущей ступенью: ошибки, форму роста p99 и прирост пропускной.
"""

from dataclasses import dataclass
from enum import Enum

from .limit import LimitResult
from .result import Step

# Во сколько раз p99 должен вырасти за одну ступень, чтобы рост считался скачком,
# а не плавным: при плавном насыщении очередь растёт постепенно
P99_JUMP_RATIO = 3.0
# p99 двух провальных ступеней выше порога и отличается не больше чем на эту долю:
# задержка упёрлась в потолок (таймаут агента)
P99_CEILING_TOLERANCE = 0.1
# Сколько ступеней нужно до первой провальной, чтобы было с чем сравнивать
MIN_STEPS_BEFORE_FAILURE = 2


class Status(Enum):
    DIAGNOSED = "diagnosed"
    NO_LIMIT = "no_limit"
    TOO_FEW_STEPS = "too_few_steps"


class Cause(Enum):
    REJECTING = "rejecting"
    SLOW_RESPONSES = "slow_responses"
    SATURATION = "saturation"
    INCONCLUSIVE = "inconclusive"


class Confidence(Enum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"


@dataclass(frozen=True)
class Diagnosis:
    status: Status
    explanation: str
    # Только при status=DIAGNOSED
    cause: Cause | None = None
    confidence: Confidence | None = None
    evidence: tuple[str, ...] = ()


CAUSE_TEXT = {
    Cause.REJECTING: "сервис отклоняет нагрузку: ошибки растут, а задержка остаётся невысокой (5xx или лимиты)",
    Cause.SLOW_RESPONSES: "сервис не успевает отвечать: задержка резко растёт или упирается в потолок, "
    "запросы копятся в очереди или не укладываются в таймаут",
    Cause.SATURATION: "насыщение: пропускная упёрлась в потолок, а p99 растёт плавно (CPU или пул соединений)",
    Cause.INCONCLUSIVE: "причину определить не удалось: признаки противоречат друг другу",
}

CONFIDENCE_TEXT = {Confidence.LOW: "низкая", Confidence.MEDIUM: "средняя", Confidence.HIGH: "высокая"}


@dataclass(frozen=True)
class _Signs:
    """Признаки одной провальной ступени по сравнению с предыдущей."""
    step: Step
    previous: Step
    error_percent: float
    previous_error_percent: float
    errors_high: bool
    latency_high: bool
    stall: bool
    jump: bool
    # p99 вместе с предыдущей провальной ступенью упёрся в потолок
    ceiling: bool


def diagnose(
    steps: list[Step], limit: LimitResult, p99_threshold_ms: float, min_gain: float = 0.5, max_error_percent: float = 1.0
) -> Diagnosis:
    """Диагноз по ступеням и найденному пределу; пороги те же, что у find_limit."""
    if not limit.found:
        return Diagnosis(Status.NO_LIMIT, "Диагноза нет: предел не найден.")

    position = steps.index(limit.failed_step)
    if position < MIN_STEPS_BEFORE_FAILURE:
        return Diagnosis(
            Status.TOO_FEW_STEPS,
            f"Диагноза нет: до первой ступени с отказом меньше {MIN_STEPS_BEFORE_FAILURE} ступеней, "
            "не с чем сравнить.",
        )

    signs: list[_Signs] = []
    for offset, step in enumerate(limit.failed_steps):
        previous = steps[position + offset - 1]
        signs.append(_signs(step, previous, signs[-1] if signs else None,
                            p99_threshold_ms, min_gain, max_error_percent))
    verdicts = [_classify(sign) for sign in signs]

    causes = {cause for cause, _ in verdicts}
    if None in causes or len(causes) > 1:
        cause, confidence = Cause.INCONCLUSIVE, Confidence.LOW
    else:
        cause = causes.pop()
        strong = all(strong for _, strong in verdicts)
        if not limit.confirmed:
            confidence = Confidence.LOW
        else:
            confidence = Confidence.HIGH if strong else Confidence.MEDIUM

    evidence = [line for sign in signs for line in _evidence(sign, p99_threshold_ms, max_error_percent)]
    if cause is Cause.INCONCLUSIVE and len(verdicts) > 1:
        evidence.append("ступени с отказом указывают на разное: " + ", ".join(
            f"ступень {sign.step.index} - {_label(found)}" for sign, (found, _) in zip(signs, verdicts)
        ))

    return Diagnosis(
        Status.DIAGNOSED,
        _explanation(cause, limit),
        cause=cause,
        confidence=confidence,
        evidence=tuple(evidence),
    )


def describe_diagnosis(diagnosis: Diagnosis) -> str:
    """Текст диагноза для CLI: те же explanation и evidence, что отдаёт сервис."""
    if diagnosis.status is not Status.DIAGNOSED:
        return diagnosis.explanation
    lines = [f"Диагноз (уверенность {CONFIDENCE_TEXT[diagnosis.confidence]}): {diagnosis.explanation}"]
    lines += [f"  - {line}" for line in diagnosis.evidence]
    return "\n".join(lines)


def _signs(
    step: Step, previous: Step, previous_failed: _Signs | None,
    p99_threshold_ms: float, min_gain: float, max_error_percent: float,
) -> _Signs:
    error_percent = step.error_count / step.request_count * 100
    gain = (step.throughput_rps - previous.throughput_rps) / (step.target_rps - previous.target_rps)
    latency_high = step.p99_ms > p99_threshold_ms
    ceiling = (
        previous_failed is not None
        and latency_high
        and previous_failed.latency_high
        and abs(step.p99_ms - previous.p99_ms) <= P99_CEILING_TOLERANCE * previous.p99_ms
    )
    return _Signs(
        step=step,
        previous=previous,
        error_percent=error_percent,
        previous_error_percent=previous.error_count / previous.request_count * 100,
        errors_high=error_percent > max_error_percent,
        latency_high=latency_high,
        stall=gain < min_gain,
        jump=previous.p99_ms > 0 and step.p99_ms >= P99_JUMP_RATIO * previous.p99_ms,
        ceiling=ceiling,
    )


def _classify(sign: _Signs) -> tuple[Cause | None, bool]:
    """Причина по признакам одной ступени и полный ли у неё набор признаков.

    None - признаки не подходят ни под одно правило (противоречат друг другу).
    """
    if sign.errors_high and not sign.latency_high:
        # Быстрые ошибки обычно не мешают пропускной расти; если она встала, признаки неполные
        return Cause.REJECTING, not sign.stall
    if sign.latency_high:
        if sign.jump or sign.ceiling:
            # Полный набор - вместе с задержкой растут и ошибки (таймауты)
            return Cause.SLOW_RESPONSES, sign.errors_high
        if sign.stall and not sign.errors_high:
            return Cause.SATURATION, True
        # p99 плавно перевалил за порог: очередь растёт, но без скачка
        return Cause.SLOW_RESPONSES, False
    if sign.stall and not sign.errors_high:
        # Пропускная встала при умеренной задержке: насыщение, если p99 при этом растёт
        if sign.step.p99_ms > sign.previous.p99_ms:
            return Cause.SATURATION, True
        return None, False
    return None, False


def _evidence(sign: _Signs, p99_threshold_ms: float, max_error_percent: float) -> list[str]:
    step, previous = sign.step, sign.previous
    lines = []
    if sign.step.error_count or sign.errors_high:
        limit = "выше" if sign.errors_high else "в пределах"
        lines.append(
            f"ступень {step.index}: ошибок {sign.error_percent:.1f} % "
            f"(на ступени {previous.index} - {sign.previous_error_percent:.1f} %), {limit} порога {max_error_percent:g} %"
        )
    latency = f"ступень {step.index}: p99 {previous.p99_ms:g} → {step.p99_ms:g} мс"
    if sign.ceiling:
        latency += " - держится у потолка, как на предыдущей ступени"
    elif sign.jump:
        latency += f" - скачок в {step.p99_ms / previous.p99_ms:.1f} раза"
    elif sign.latency_high:
        latency += f" - плавный рост выше порога {p99_threshold_ms:g} мс"
    else:
        latency += f" - ниже порога {p99_threshold_ms:g} мс"
    lines.append(latency)
    if sign.stall:
        lines.append(
            f"ступень {step.index}: пропускная {step.throughput_rps:g} запр/с при цели {step.target_rps:g}, "
            f"прирост {step.throughput_rps - previous.throughput_rps:g} при росте нагрузки "
            f"на {step.target_rps - previous.target_rps:g} запр/с"
        )
    return lines


def _label(cause: Cause | None) -> str:
    return {
        Cause.REJECTING: "отклонение нагрузки",
        Cause.SLOW_RESPONSES: "медленные ответы",
        Cause.SATURATION: "насыщение",
        None: "ни одно правило",
    }[cause]


def _explanation(cause: Cause, limit: LimitResult) -> str:
    text = CAUSE_TEXT[cause]
    if limit.confirmed:
        return text[0].upper() + text[1:] + "."
    if cause is Cause.INCONCLUSIVE:
        text = text[0].upper() + text[1:] + "."
    else:
        text = "Вероятно, " + text + "."
    return text + (
        f" Предел не подтверждён: прогон закончился на ступени {limit.failed_step.index}, первой с отказом. "
        "Повтор с ещё одной ступенью выше даст уверенный диагноз."
    )
