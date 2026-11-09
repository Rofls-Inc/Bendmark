using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Grpc.Core;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public class MultiAgentRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Busy_reply_does_not_replay_a_peer_step(bool deliverAfterCancellation)
    {
        await using var factory = new CoordinatorFactory { AgentCount = 2 };
        var observed = new BufferedStepRunner(factory.Agents[0], deliverAfterCancellation);
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new AgentGroup([
                new AgentEndpoint(1, CoordinatorFactory.AgentAddress(1), observed),
                new AgentEndpoint(2, CoordinatorFactory.AgentAddress(2), factory.Agents[1]),
            ]))));
        var client = host.CreateClient();
        var id = await Api.CreateRunAsync(client);
        foreach (var agent in factory.Agents)
            Assert.Equal(id, await agent.WaitStartedAsync());

        factory.Agents[0].SendStep(TestData.Step(1));
        // В первом случае часть уже принята координатором, во втором она придёт при отмене.
        await (deliverAfterCancellation ? observed.Buffered.Task : observed.Delivered.Task)
            .WaitAsync(FakeAgentRunner.Timeout);
        factory.Agents[1].Fail(StatusCode.ResourceExhausted, "agent is busy");

        var failed = Api.WaitForStatusAsync(client, id, "failed");
        Assert.Same(failed, await Task.WhenAny(failed, observed.Repeated.Task));
        var run = await failed;
        Assert.Equal(0, Api.StepCount(run));
        Assert.Contains("Агент 2 из 2", run.GetProperty("error").GetString());
        Assert.All(factory.Agents, agent => Assert.Equal(new[] { id }, agent.RunCalls));
    }

    private sealed class BufferedStepRunner(FakeAgentRunner inner, bool deliverAfterCancellation) : IAgentRunner
    {
        private int _calls;

        public TaskCompletionSource Buffered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Repeated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<StepResultDto> RunAsync(Guid runId, Scenario scenario,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 1)
                Repeated.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await foreach (var step in inner.RunAsync(runId, scenario, cancellationToken))
            {
                Buffered.TrySetResult();
                if (deliverAfterCancellation)
                    await cancelled.Task.WaitAsync(FakeAgentRunner.Timeout);
                yield return step;
                // Потребитель уже обработал yield и запросил следующую ступень.
                Delivered.TrySetResult();
            }
        }

        public Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken) =>
            inner.StopAsync(runId, cancellationToken);
    }
}
