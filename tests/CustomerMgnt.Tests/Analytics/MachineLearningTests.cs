using Common.Core.Compute;
using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Tests.Analytics;

[TestClass]
public sealed class MachineLearningTests
{
    private static readonly ComputeOptions Simd = new() { UseSimd = true, UseParallel = true, ParallelThreshold = 16 };
    private static readonly ComputeOptions Scalar = new() { UseSimd = false, UseParallel = false };

    [TestMethod]
    public void LogisticRegression_LearnsSeparableData_SimdMatchesScalar()
    {
        // Step 1: ข้อมูลสังเคราะห์ y = 1 เมื่อ 2x0 - x1 > 0
        var rnd = new Random(7);
        var rows = new List<float[]>();
        var labels = new List<bool>();
        for (int i = 0; i < 600; i++)
        {
            float a = (float)(rnd.NextDouble() * 2 - 1), b = (float)(rnd.NextDouble() * 2 - 1), noise = (float)rnd.NextDouble();
            rows.Add([a, b, noise, a * 0.1f, b * 0.1f, noise, a, b, 0.5f]);
            labels.Add((2 * a) - b > 0);
        }

        string[] names = Enumerable.Range(0, 9).Select(i => "f" + i).ToArray();
        LogisticRegressionModel simd = LogisticRegressionModel.Train(names, rows, labels, Simd);
        LogisticRegressionModel scalar = LogisticRegressionModel.Train(names, rows, labels, Scalar);

        // Step 2: แม่นยำ > 90% และผล SIMD ≈ scalar
        int correct = rows.Select((r, i) => simd.Predict(r) >= 0.5 == labels[i]).Count(ok => ok);
        Assert.IsGreaterThan(0.9, correct / (double)rows.Count);
        foreach (float[] r in rows.Take(50))
        {
            Assert.AreEqual(scalar.Predict(r), simd.Predict(r), 1e-3);
        }

        (double p, IReadOnlyList<FactorDTO> factors) = simd.Explain(rows[0], 3);
        Assert.AreEqual(simd.Predict(rows[0]), p, 1e-9);
        Assert.HasCount(3, factors);
    }

    [TestMethod]
    public void KMeans_FindsSeparatedClusters()
    {
        var rnd = new Random(1);
        float[][] centers = [[0, 0, 0, 0, 0, 0, 0, 0, 0], [10, 10, 10, 10, 10, 10, 10, 10, 10], [-10, 5, -10, 5, -10, 5, -10, 5, -10]];
        var rows = new List<float[]>();
        for (int i = 0; i < 900; i++)
        {
            float[] c = centers[i % 3];
            rows.Add(c.Select(v => v + (float)(rnd.NextDouble() - 0.5)).ToArray());
        }

        KMeansResult r = KMeansClusterer.Cluster(rows, 3, Simd);
        // แต่ละจุดในกลุ่มต้นทางเดียวกันต้องได้ cluster เดียวกัน
        for (int g = 0; g < 3; g++)
        {
            int expected = r.Assignments[g];
            for (int i = g; i < rows.Count; i += 3) Assert.AreEqual(expected, r.Assignments[i]);
        }

        Assert.AreEqual(3, r.Assignments.Distinct().Count());
    }

    [TestMethod]
    public void SquaredDistance_SimdEqualsScalar()
    {
        float[] a = Enumerable.Range(0, 37).Select(i => i * 0.37f).ToArray();
        float[] b = Enumerable.Range(0, 37).Select(i => 5 - (i * 0.11f)).ToArray();
        Assert.AreEqual(KMeansClusterer.SquaredDistance(a, b, false), KMeansClusterer.SquaredDistance(a, b, true), 1e-3);
    }

    [TestMethod]
    public void HoltWinters_CapturesSeasonality()
    {
        // ยอดขายมีฤดูกาล: ธ.ค. สูงกว่าปกติ 2 เท่า + แนวโน้มขึ้น
        double[] series = Enumerable.Range(0, 48).Select(i => (100 + i) * (i % 12 == 11 ? 2.0 : 1.0)).ToArray();
        TimeSeriesResult r = TimeSeriesForecaster.Forecast(series, 12);
        Assert.HasCount(12, r.Forecast);
        double december = r.Forecast[11], november = r.Forecast[10];
        Assert.IsGreaterThan(1.5, december / november, $"December {december:N0} vs November {november:N0}");
        Assert.IsLessThan(15, r.Mape, "MAPE is a percentage");
    }

    [TestMethod]
    public void ForecastBuild_TrimsLeadingZeros_AndAddsPipeline()
    {
        var start = new DateOnly(2023, 1, 1);
        var current = new DateOnly(2026, 11, 1);
        var history = new Dictionary<DateOnly, decimal>();
        for (DateOnly m = new(2025, 1, 1); m < current; m = m.AddMonths(1)) history[m] = 1000;
        var pipeline = new Dictionary<DateOnly, double> { [current] = 5000 };

        ForecastDTO f = ForecastService.Build(history, start, current, 3, pipeline);
        Assert.AreEqual(new DateOnly(2025, 1, 1), f.History[0].Period, "leading all-zero years must be trimmed");
        Assert.HasCount(3, f.Points);
        Assert.IsGreaterThan(f.Points[1].Forecast, f.Points[0].Forecast, "pipeline should lift the month it is expected to close");
        Assert.IsTrue(f.Points.All(p => p.Lower <= p.Forecast && p.Forecast <= p.Upper));
    }

    [TestMethod]
    [DataRow(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 1.5f, 0 }, "Needs Care")]
    [DataRow(new float[] { -1, 1, 1, 0, 0, 0, 0, 0, 0, 0 }, "Champions")]
    [DataRow(new float[] { 1, 0, 0.5f, 0, 0, 0, 0, 0, 0, 0 }, "At Risk")]
    [DataRow(new float[] { 1, -0.5f, -0.5f, -1, -1, -1, 0, 0, 0, 0 }, "Hibernating")]
    public void Segmentation_NamesFromCentroid(float[] raw, string expected)
    {
        // DataRow ข้างบนเรียงตาม Dimensions: recency, frequency, monetary, open rate, visits, content, complaints, sentiment
        float[] z = [raw[0], raw[1], raw[2], raw[3], raw[4], raw[5], raw[8], raw[9]];
        Assert.AreEqual(expected, SegmentationService.Name(z));
    }

    [TestMethod]
    public void SendTime_FindsCustomerHabit_InLocalTimeZone()
    {
        // ลูกค้าเปิดอีเมลวันอังคาร 20:00 เวลาไทย (13:00Z) สม่ำเสมอ
        var customer = new CustomerDTO { Id = 1, TimeZoneId = "Asia/Bangkok" };
        var events = new List<EngagementEventDTO>();
        for (int w = 0; w < 30; w++)
        {
            DateTimeOffset tue = new DateTimeOffset(2026, 6, 2, 13, 5, 0, TimeSpan.Zero).AddDays(7 * w);
            events.Add(new EngagementEventDTO { CustomerId = 1, Kind = EngagementKind.EmailSent, OccurredAt = tue.AddHours(-3) });
            events.Add(new EngagementEventDTO { CustomerId = 1, Kind = EngagementKind.EmailOpen, OccurredAt = tue });
        }

        var now = new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero); // จันทร์ 10:00 ไทย
        SendTimeDTO r = SendTimeOptimizationService.Compute(customer, ChannelKind.Email, events, now);
        Assert.AreEqual(DayOfWeek.Tuesday, r.BestDayOfWeek);
        Assert.AreEqual(20, r.BestHourLocal);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 24, 13, 0, 0, TimeSpan.Zero), r.NextSendAt);
        Assert.AreEqual(30, r.Observations);
    }

    [TestMethod]
    public void SendTime_WithoutData_UsesBusinessHoursPrior()
    {
        SendTimeDTO r = SendTimeOptimizationService.Compute(new CustomerDTO { Id = 1, TimeZoneId = "Asia/Bangkok" }, ChannelKind.Email, [], DateTimeOffset.UtcNow);
        Assert.IsTrue(r.BestDayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday);
        Assert.IsTrue(r.BestHourLocal is >= 9 and <= 15);
    }
}
