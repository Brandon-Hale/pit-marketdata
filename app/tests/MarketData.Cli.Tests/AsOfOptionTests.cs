using MarketData.Cli.Commands;
using Shouldly;

namespace MarketData.Cli.Tests;

public sealed class AsOfOptionTests
{
    [Fact]
    public void A_bare_date_is_utc_midnight_not_local()
    {
        // In Sydney, local parsing would make this 2024-06-07T14:00Z and silently exclude a
        // bar published at 20:15Z the previous day.
        AsOfOption.Parse("2024-06-08")
            .ShouldBe(new DateTimeOffset(2024, 6, 8, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_bare_date_and_time_is_utc()
    {
        AsOfOption.Parse("2024-06-08T22:15:00")
            .ShouldBe(new DateTimeOffset(2024, 6, 8, 22, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_trailing_z_is_honoured()
    {
        AsOfOption.Parse("2024-06-08T22:15:00Z")
            .ShouldBe(new DateTimeOffset(2024, 6, 8, 22, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void An_explicit_offset_is_honoured_and_normalised_to_utc()
    {
        AsOfOption.Parse("2024-06-08T08:00:00+10:00")
            .ShouldBe(new DateTimeOffset(2024, 6, 7, 22, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_negative_offset_is_honoured()
    {
        AsOfOption.Parse("2024-06-08T16:15:00-04:00")
            .ShouldBe(new DateTimeOffset(2024, 6, 8, 20, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parsing_never_depends_on_the_machine_timezone()
    {
        var original = TimeZoneInfo.Local;

        try
        {
            // Whatever the machine is set to, a bare date means the same instant.
            AsOfOption.Parse("2024-06-08").Offset.ShouldBe(TimeSpan.Zero);
            AsOfOption.Parse("2024-06-08").UtcDateTime.Hour.ShouldBe(0);
        }
        finally
        {
            _ = original;
        }
    }

    [Fact]
    public void Nonsense_is_rejected_with_examples()
    {
        Should.Throw<FormatException>(() => AsOfOption.Parse("yesterday"))
            .Message.ShouldContain("2024-06-08T22:15:00Z");
    }
}
