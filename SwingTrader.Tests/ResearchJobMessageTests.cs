using System.Text.Json;
using FluentAssertions;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The midday-rescore job shares the research queue with the morning run and
// is distinguished only by ResearchJobMessage.JobType - these pin the wire
// contract the scheduler and consumer both rely on.
public class ResearchJobMessageTests
{
    [Fact]
    public void Deserialize_LegacyPayloadWithoutJobType_DefaultsToResearch()
    {
        // Messages already sitting in the queue when this deploys were
        // serialized before JobType existed - they must land as the morning
        // run, not null (the consumer keys its job-log marks off this).
        const string legacy = """{"AccountId":1,"JobId":"abc","TradeDate":"2026-07-10","ScheduledFor":"2026-07-10T11:30:00"}""";

        var message = JsonSerializer.Deserialize<ResearchJobMessage>(legacy)!;

        message.JobType.Should().Be("Research");
    }

    [Fact]
    public void SerializeDeserialize_MiddayJobType_RoundTrips()
    {
        var midday = new ResearchJobMessage(1, "abc", new DateOnly(2026, 7, 10), DateTime.UtcNow, "ResearchMidday");

        var roundTripped = JsonSerializer.Deserialize<ResearchJobMessage>(JsonSerializer.Serialize(midday))!;

        roundTripped.JobType.Should().Be("ResearchMidday");
    }
}
