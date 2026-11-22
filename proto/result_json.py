"""Explicit mapping from generated messages to Bendmark's result.json format."""


def step_to_json(step) -> dict:
    count = int(step.request_count)
    errors = int(step.errors.count)
    duration = float(step.duration_seconds)
    result = {
        "index": int(step.index),
        "target_rps": float(step.target_rps),
        "duration_seconds": duration,
        "request_count": count,
        "throughput_rps": float(step.throughput_rps),
        "latency_ms": {
            "p50": float(step.latency_ms.p50),
            "p90": float(step.latency_ms.p90),
            "p99": float(step.latency_ms.p99),
        },
        "errors": {
            "count": errors,
            "rate_percent": round(100 * errors / count, 1) if count else 0.0,
        },
        "skipped_count": int(step.skipped_count),
    }
    if step.HasField("successful_completed_in_step"):
        successful = int(step.successful_completed_in_step)
        result["successful_completed_in_step"] = successful
        if duration > 0:
            result["successful_rps_in_step"] = successful / duration
    return result


def run_to_json(scenario_name: str, status: str, steps) -> dict:
    if status not in ("completed", "aborted"):
        raise ValueError("Only completed or aborted runs can be exported")
    return {
        "scenario_name": scenario_name,
        "status": status,
        "steps": [step_to_json(step) for step in steps],
    }
