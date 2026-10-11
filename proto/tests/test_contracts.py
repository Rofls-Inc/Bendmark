"""Wire/code-generation checks; these servicers are not service implementations."""

from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import sys
import unittest

import grpc
from google.protobuf.json_format import MessageToDict

from bendmark.v1 import agent_pb2, agent_pb2_grpc, analyzer_pb2, analyzer_pb2_grpc, common_pb2
from proto.result_json import run_to_json, step_to_json

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "services" / "analyzer"))
from analyzer.result import parse_steps


def measured_step(index=1, count=1000, skipped=0):
    return common_pb2.StepResult(
        index=index,
        target_rps=100 * index,
        duration_seconds=10,
        request_count=count,
        throughput_rps=count / 10,
        latency_ms=common_pb2.LatencyPercentiles(p50=12, p90=18, p99=22),
        errors=common_pb2.RequestErrors(count=0, rate_percent=0),
        skipped_count=skipped,
    )


class EchoAgent(agent_pb2_grpc.AgentServiceServicer):
    def Run(self, request, context):
        for index, _ in enumerate(request.scenario.steps, 1):
            yield agent_pb2.RunResponse(run_id=request.run_id, step_result=measured_step(index, skipped=3))

    def Stop(self, request, context):
        return agent_pb2.StopResponse(was_running=False)


class EchoAnalyzer(analyzer_pb2_grpc.AnalyzerServiceServicer):
    def FindLimit(self, request, context):
        if not request.options.HasField("max_error_percent") or request.options.max_error_percent != 0:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "Explicit zero threshold did not survive the RPC")
        return analyzer_pb2.FindLimitResponse(
            limit=analyzer_pb2.Limit(found=True, failed_step_index=request.steps[0].index, reasons=["test"])
        )


class ContractsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = grpc.server(ThreadPoolExecutor(max_workers=2))
        agent_pb2_grpc.add_AgentServiceServicer_to_server(EchoAgent(), cls.server)
        analyzer_pb2_grpc.add_AnalyzerServiceServicer_to_server(EchoAnalyzer(), cls.server)
        port = cls.server.add_insecure_port("127.0.0.1:0")
        if not port:
            raise RuntimeError("Could not bind the contract test server")
        cls.server.start()
        cls.channel = grpc.insecure_channel(f"127.0.0.1:{port}")
        grpc.channel_ready_future(cls.channel).result(timeout=5)

    @classmethod
    def tearDownClass(cls):
        cls.channel.close()
        cls.server.stop(0).wait(timeout=5)

    def test_run_stream_and_stop_rpc(self):
        stub = agent_pb2_grpc.AgentServiceStub(self.channel)
        request = agent_pb2.RunRequest(
            run_id="d01f3307-262a-4f1b-9412-1456d5a983fb",
            scenario=common_pb2.Scenario(
                name="wire-test",
                target=common_pb2.Target(url="http://localhost/", method="GET"),
                steps=[common_pb2.Step(target_rps=100, duration_seconds=10),
                       common_pb2.Step(target_rps=200, duration_seconds=10)],
            ),
        )
        responses = list(stub.Run(request, timeout=5))
        self.assertEqual([response.step_result.index for response in responses], [1, 2])
        self.assertTrue(all(response.run_id == request.run_id for response in responses))
        self.assertTrue(all(response.step_result.skipped_count == 3 for response in responses))
        self.assertFalse(stub.Stop(agent_pb2.StopRequest(run_id=request.run_id), timeout=5).was_running)

    def test_find_limit_rpc_preserves_optional_zero_and_one_based_index(self):
        stub = analyzer_pb2_grpc.AnalyzerServiceStub(self.channel)
        response = stub.FindLimit(analyzer_pb2.FindLimitRequest(
            steps=[measured_step(1)],
            options=analyzer_pb2.LimitOptions(max_error_percent=0),
        ), timeout=5)
        self.assertTrue(response.limit.found)
        self.assertEqual(response.limit.failed_step_index, 1)
        self.assertTrue(response.limit.HasField("failed_step_index"))
        self.assertFalse(response.limit.HasField("limit_rps"))

    def test_absent_options_are_distinct_from_explicit_zero(self):
        options = analyzer_pb2.LimitOptions()
        self.assertFalse(options.HasField("max_error_percent"))
        options.max_error_percent = 0
        restored = analyzer_pb2.LimitOptions.FromString(options.SerializeToString())
        self.assertTrue(restored.HasField("max_error_percent"))
        self.assertEqual(restored.max_error_percent, 0)

    def test_result_json_keeps_snake_case_and_integer_counters(self):
        step = measured_step(count=2**53 + 1)
        proto_json = MessageToDict(step)
        self.assertIsInstance(proto_json["requestCount"], str)
        result = json.loads(json.dumps(run_to_json("wire-test", "completed", [step]), allow_nan=False))
        exported = result["steps"][0]
        self.assertIsInstance(exported["request_count"], int)
        self.assertEqual(exported["request_count"], 2**53 + 1)
        self.assertEqual(exported["skipped_count"], 0)
        self.assertIn("rate_percent", exported["errors"])
        self.assertNotIn("requestCount", exported)
        self.assertEqual(parse_steps(result)[0].request_count, step.request_count)

    def test_skipped_requests_are_not_interpreted_as_a_service_limit(self):
        result = run_to_json("generator-overload", "completed", [measured_step(skipped=2)])
        self.assertEqual(result["steps"][0]["skipped_count"], 2)
        with self.assertRaisesRegex(ValueError, "агент пропустил 2 запросов"):
            parse_steps(result)

    def test_zero_request_count_exports_zero_error_rate(self):
        result = step_to_json(common_pb2.StepResult(index=1, target_rps=100, duration_seconds=10))
        self.assertEqual(result["request_count"], 0)
        self.assertEqual(result["skipped_count"], 0)
        self.assertEqual(result["errors"], {"count": 0, "rate_percent": 0.0})
        self.assertEqual(result["latency_ms"], {"p50": 0.0, "p90": 0.0, "p99": 0.0})


if __name__ == "__main__":
    unittest.main()
