from dataclasses import dataclass

from .result import Step


@dataclass(frozen=True)
class LimitResult:
    # Нагрузка ступени перед первой из провальных. None - отказ уже на первой ступени.
    # Если отказа нет - нижняя оценка предела: нагрузка последней ступени.
    limit_rps: float | None
    # Первая из провальных ступеней. None - отказа не было.
    failed_step: Step | None
    # Причины отказа провальных ступеней, затем предупреждения о выбросах
    reasons: tuple[str, ...]
    # Отказ подтверждён второй подряд провальной ступенью. False, если ступени
    # закончились на единственной провальной: предел неподтверждённый
    confirmed: bool = False
    # Провальные ступени: две подряд, или одна - у неподтверждённого предела
    failed_steps: tuple[Step, ...] = ()

    @property
    def found(self) -> bool:
        return self.failed_step is not None


def step_failures(
    step: Step, previous: Step | None, p99_threshold_ms: float, min_gain: float, max_error_percent: float
) -> list[str]:
    """Какие критерии отказа сработали на ступени; пустой список - ступень в норме."""
    prev_target = previous.target_rps if previous else 0.0
    prev_throughput = previous.throughput_rps if previous else 0.0

    reasons = []
    if step.p99_ms > p99_threshold_ms:
        reasons.append(f"p99 {step.p99_ms:g} мс выше порога {p99_threshold_ms:g} мс")

    target_delta = step.target_rps - prev_target
    throughput_delta = step.throughput_rps - prev_throughput
    if throughput_delta / target_delta < min_gain:
        reasons.append(
            f"пропускная выросла на {throughput_delta:g} запр/с "
            f"при росте нагрузки на {target_delta:g} запр/с"
        )

    error_percent = step.error_count / step.request_count * 100
    if error_percent > max_error_percent:
        reasons.append(f"ошибок {error_percent:.1f} % при пороге {max_error_percent:g} %")
    return reasons


def find_limit(steps: list[Step], p99_threshold_ms: float, min_gain: float = 0.5, max_error_percent: float = 1.0) -> LimitResult:
    """Ищет предел: отказ подтверждает вторая подряд ступень с отказом.

    Одиночный отказ, после которого ступень снова в норме, - выброс: анализ идёт
    дальше, а выброс попадает в reasons как предупреждение. Если ступени
    закончились на единственной провальной, предел найден, но не подтверждён.
    """
    if not steps:
        raise ValueError("в результате нет ни одной ступени")
    if p99_threshold_ms <= 0:
        raise ValueError("порог p99 должен быть больше нуля")
    if min_gain <= 0:
        raise ValueError("min_gain должен быть больше нуля")
    if max_error_percent < 0:
        raise ValueError("порог ошибок не может быть отрицательным")

    previous: Step | None = None
    # Ступень с отказом, которую ещё не подтвердила следующая, и её причины
    suspect: tuple[Step, list[str]] | None = None
    # Ступень перед suspect: её нагрузка - предел, если отказ подтвердится
    before_suspect: Step | None = None
    warnings: list[str] = []

    for step in steps:
        if step.request_count == 0:
            raise ValueError(
                f"ступень {step.index}: нет завершившихся запросов, предел сервиса определить нельзя"
            )
        prev_target = previous.target_rps if previous else 0.0
        if step.target_rps <= prev_target:
            raise ValueError(
                f"целевая нагрузка должна расти от ступени к ступени, а на ступени {step.index} она {step.target_rps:g} после {prev_target:g}"
            )

        failures = step_failures(step, previous, p99_threshold_ms, min_gain, max_error_percent)
        if failures and suspect is not None:
            first, first_failures = suspect
            return LimitResult(
                limit_rps=before_suspect.target_rps if before_suspect else None,
                failed_step=first,
                reasons=(*_labelled(first, first_failures), *_labelled(step, failures), *warnings),
                confirmed=True,
                failed_steps=(first, step),
            )
        if failures:
            suspect, before_suspect = (step, failures), previous
        elif suspect is not None:
            outlier, outlier_failures = suspect
            warnings.append(
                f"предупреждение: ступень {outlier.index} - одиночный отказ, следующая ступень в норме, "
                f"это не предел ({'; '.join(outlier_failures)})"
            )
            suspect = None
        previous = step

    if suspect is not None:
        last, last_failures = suspect
        return LimitResult(
            limit_rps=before_suspect.target_rps if before_suspect else None,
            failed_step=last,
            reasons=(*_labelled(last, last_failures), *warnings),
            confirmed=False,
            failed_steps=(last,),
        )
    return LimitResult(limit_rps=previous.target_rps, failed_step=None, reasons=tuple(warnings))


def _labelled(step: Step, failures: list[str]) -> list[str]:
    return [f"ступень {step.index}: {failure}" for failure in failures]
