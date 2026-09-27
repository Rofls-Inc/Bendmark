from dataclasses import dataclass

from .result import Step


@dataclass(frozen=True)
class LimitResult:
    # Нагрузка последней ступени без отказа. None - отказ уже на первой ступени.
    limit_rps: float | None
    # Первая ступень с отказом. None - отказа не было, и limit_rps - нижняя оценка предела.
    failed_step: Step | None
    reasons: tuple[str, ...]

    @property
    def found(self) -> bool:
        return self.failed_step is not None


def find_limit(steps: list[Step], p99_threshold_ms: float, min_gain: float = 0.5) -> LimitResult:

    """Находит предел сервиса

    Ступень считается отказом, если выполнено хотя бы одно:
    - p99 больше p99_threshold_ms
    - пропускная перестала расти: прирост пропускной, разделенный на прирост целевой нагрузки относительно предыдущей ступени, меньше min_gain. Для первой ступени сравнение идёт с нулём, т.е. проверяется throughput / target

    Предел - целевая нагрузка последней ступени перед первым отказом.
    """

    if not steps:
        raise ValueError("в результате нет ни одной ступени")
    if p99_threshold_ms <= 0:
        raise ValueError("порог p99 должен быть больше нуля")
    if min_gain <= 0:
        raise ValueError("min_gain должен быть больше нуля")

    prev_target, prev_throughput = 0.0, 0.0
    last_ok: Step | None = None

    for step in steps:
        if step.target_rps <= prev_target:
            raise ValueError(
                f"целевая нагрузка должна расти от ступени к ступени, а на ступени {step.index} она {step.target_rps:g} после {prev_target:g}"
            )

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

        if reasons:
            return LimitResult(
                limit_rps=last_ok.target_rps if last_ok else None,
                failed_step=step,
                reasons=tuple(reasons),
            )

        last_ok = step
        prev_target, prev_throughput = step.target_rps, step.throughput_rps

    return LimitResult(limit_rps=last_ok.target_rps, failed_step=None, reasons=())
