using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Providers;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class DeepSeekApiServicePricingTests
{
    [Theory]
    [InlineData("deepseek-v4-flash", false, "USD", 0.15, 0.003, 0.6)]
    [InlineData("deepseek-v4-flash", true, "USD", 0.3, 0.006, 1.2)]
    [InlineData("deepseek-v4-flash", false, "CNY", 1.0, 0.02, 4.0)]
    [InlineData("deepseek-v4-flash", true, "CNY", 2.0, 0.04, 8.0)]
    [InlineData("deepseek-v4-pro", false, "USD", 0.66, 0.022, 1.98)]
    [InlineData("deepseek-v4-pro", true, "USD", 1.32, 0.044, 3.96)]
    [InlineData("deepseek-v4-pro", false, "CNY", 4.5, 0.15, 13.5)]
    [InlineData("deepseek-v4-pro", true, "CNY", 9.0, 0.30, 27.0)]
    public void GetPricing_UsesModelSpecificOfficialRates(
        string model,
        bool isPeak,
        string currency,
        double expectedCacheMiss,
        double expectedCacheHit,
        double expectedOutput)
    {
        var pricing = DeepSeekProvider.GetPricing(model, isPeak, currency);

        pricing.CacheMiss.Should().BeApproximately(expectedCacheMiss, 0.000_000_001);
        pricing.CacheHit.Should().BeApproximately(expectedCacheHit, 0.000_000_001);
        pricing.Output.Should().BeApproximately(expectedOutput, 0.000_000_001);
    }

    [Theory]
    [InlineData(2026, 9, 7, 0, 59, false)]  // 周一 北京 08:59，空闲
    [InlineData(2026, 9, 7, 1, 0, true)]    // 周一 北京 09:00，高峰
    [InlineData(2026, 9, 7, 3, 59, true)]   // 周一 北京 11:59，高峰
    [InlineData(2026, 9, 7, 4, 0, false)]   // 周一 北京 12:00，空闲
    [InlineData(2026, 9, 7, 6, 0, true)]    // 周一 北京 14:00，高峰
    [InlineData(2026, 9, 7, 9, 59, true)]   // 周一 北京 17:59，高峰
    [InlineData(2026, 9, 7, 10, 0, false)]  // 周一 北京 18:00，空闲
    [InlineData(2026, 9, 12, 4, 0, false)]  // 周六 北京 12:00，全天空闲
    [InlineData(2026, 9, 13, 6, 0, false)]  // 周日 北京 14:00，全天空闲
    public void IsBeijingPeakTime_UsesWeekdayScheduleAndWeekendOffPeak(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        bool expected)
    {
        var utcNow = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        DeepSeekProvider.IsBeijingPeakTime(utcNow).Should().Be(expected);
    }
}
