namespace CustomerMgnt.Core.Analytics;

/// <summary>ผลพยากรณ์อนุกรมเวลา</summary>
/// <param name="Fitted">ค่าที่โมเดลอธิบายย้อนหลัง (ยาวเท่าข้อมูล)</param>
/// <param name="Forecast">ค่าพยากรณ์ล่วงหน้า</param>
/// <param name="Seasonal">ดัชนีฤดูกาลแบบบวก (ยาว = season length)</param>
/// <param name="ResidualStdDev">ส่วนเบี่ยงเบนมาตรฐานของ residual (ใช้ทำช่วงความเชื่อมั่น)</param>
/// <param name="Mape">Mean absolute percentage error (%)</param>
/// <param name="Method">ชื่อวิธี</param>
public sealed record TimeSeriesResult(double[] Fitted, double[] Forecast, double[] Seasonal, double ResidualStdDev, double Mape, string Method);

/// <summary>
/// พยากรณ์อนุกรมเวลารายเดือนแบบ Pure C#:
/// <list type="bullet">
/// <item>≥ 2 ฤดูกาล (24 เดือน): Holt-Winters additive (level + trend + seasonality) เลือก α/β/γ ด้วย grid search ที่ให้ SSE ต่ำสุด (ค้นหาขนานได้)</item>
/// <item>น้อยกว่านั้น: Holt linear trend (ไม่มีฤดูกาล)</item>
/// </list>
/// </summary>
public static class TimeSeriesForecaster
{
    private static readonly double[] Grid = [0.05, 0.1, 0.2, 0.3, 0.5, 0.7];

    /// <summary>พยากรณ์</summary>
    /// <param name="series">ข้อมูลรายเดือนเรียงตามเวลา</param>
    /// <param name="horizon">จำนวนงวดล่วงหน้า</param>
    /// <param name="seasonLength">ความยาวฤดูกาล (12 = รายปีของข้อมูลรายเดือน)</param>
    /// <param name="parallel">ค้นหาพารามิเตอร์แบบขนาน</param>
    /// <returns>ผลพยากรณ์</returns>
    public static TimeSeriesResult Forecast(IReadOnlyList<double> series, int horizon, int seasonLength = 12, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(horizon);
        double[] y = series.ToArray();

        // Step 1: ข้อมูลน้อยมาก → ค่าเฉลี่ยคงที่
        if (y.Length < 3)
        {
            double avg = y.Length == 0 ? 0 : y.Average();
            return new TimeSeriesResult(y.Select(_ => avg).ToArray(), Enumerable.Repeat(avg, horizon).ToArray(), new double[seasonLength], 0, 0, "Mean (insufficient history)");
        }

        // Step 2: เลือกวิธีตามปริมาณข้อมูล แล้ว grid search พารามิเตอร์
        bool seasonal = y.Length >= 2 * seasonLength;
        var candidates = (from a in Grid from b in Grid from g in seasonal ? Grid : [0.0] select (a, b, g)).ToArray();
        var results = new (double Sse, double A, double B, double G)[candidates.Length];
        void Evaluate(int i)
        {
            (double a, double b, double g) = candidates[i];
            double sse = seasonal ? HoltWinters(y, seasonLength, a, b, g, 0, out _, out _, out _) : Holt(y, a, b, 0, out _, out _);
            results[i] = (sse, a, b, g);
        }

        if (parallel)
        {
            Parallel.For(0, candidates.Length, Evaluate);
        }
        else
        {
            for (int i = 0; i < candidates.Length; i++) Evaluate(i);
        }

        (double _, double bestA, double bestB, double bestG) = results.MinBy(r => r.Sse);

        // Step 3: ฝึกด้วยพารามิเตอร์ที่ดีที่สุดแล้วพยากรณ์
        double[] fitted, forecast, season;
        if (seasonal)
        {
            HoltWinters(y, seasonLength, bestA, bestB, bestG, horizon, out fitted, out forecast, out season);
        }
        else
        {
            Holt(y, bestA, bestB, horizon, out fitted, out forecast);
            season = new double[seasonLength];
        }

        // Step 4: วัดความคลาดเคลื่อน (เริ่มหลังช่วง warm-up)
        int warm = seasonal ? seasonLength : 1;
        double sumSq = 0, sumApe = 0;
        int apeCount = 0;
        for (int t = warm; t < y.Length; t++)
        {
            double e = y[t] - fitted[t];
            sumSq += e * e;
            if (Math.Abs(y[t]) > 1e-9)
            {
                sumApe += Math.Abs(e / y[t]);
                apeCount++;
            }
        }

        double sd = Math.Sqrt(sumSq / Math.Max(1, y.Length - warm));
        double mape = apeCount == 0 ? 0 : 100 * sumApe / apeCount;
        for (int h = 0; h < forecast.Length; h++) forecast[h] = Math.Max(0, forecast[h]); // ยอดขาย/งบไม่ติดลบ
        return new TimeSeriesResult(fitted, forecast, season, sd, Math.Round(mape, 2), seasonal ? $"Holt-Winters additive (α={bestA}, β={bestB}, γ={bestG})" : $"Holt linear trend (α={bestA}, β={bestB})");
    }

    /// <summary>Holt-Winters additive</summary>
    /// <returns>SSE ของ one-step-ahead</returns>
    private static double HoltWinters(double[] y, int m, double alpha, double beta, double gamma, int horizon, out double[] fitted, out double[] forecast, out double[] season)
    {
        // Step 1: ค่าเริ่มต้น — level = ค่าเฉลี่ยฤดูแรก, trend = ค่าต่างเฉลี่ยระหว่างฤดูที่ 1 กับ 2, season = ส่วนต่างจากค่าเฉลี่ยฤดูแรก
        double level = y.Take(m).Average();
        double trend = (y.Skip(m).Take(m).Average() - level) / m;
        double[] s = new double[m];
        for (int i = 0; i < m; i++) s[i] = y[i] - level;

        fitted = new double[y.Length];
        double sse = 0;

        // Step 2: อัปเดต level/trend/season ทีละงวด
        for (int t = 0; t < y.Length; t++)
        {
            double f = level + trend + s[t % m];
            fitted[t] = f;
            if (t >= m)
            {
                sse += (y[t] - f) * (y[t] - f);
            }

            double prevLevel = level;
            level = (alpha * (y[t] - s[t % m])) + ((1 - alpha) * (level + trend));
            trend = (beta * (level - prevLevel)) + ((1 - beta) * trend);
            s[t % m] = (gamma * (y[t] - level)) + ((1 - gamma) * s[t % m]);
        }

        // Step 3: พยากรณ์ h งวด
        forecast = new double[horizon];
        for (int h = 1; h <= horizon; h++)
        {
            forecast[h - 1] = level + (h * trend) + s[(y.Length + h - 1) % m];
        }

        // Step 4: ดัชนีฤดูกาลเรียงตามตำแหน่งเดือนของข้อมูล
        season = new double[m];
        for (int i = 0; i < m; i++) season[i] = s[i];
        return sse;
    }

    /// <summary>Holt linear trend</summary>
    /// <returns>SSE</returns>
    private static double Holt(double[] y, double alpha, double beta, int horizon, out double[] fitted, out double[] forecast)
    {
        double level = y[0], trend = y[1] - y[0], sse = 0;
        fitted = new double[y.Length];
        fitted[0] = y[0];
        for (int t = 1; t < y.Length; t++)
        {
            double f = level + trend;
            fitted[t] = f;
            sse += (y[t] - f) * (y[t] - f);
            double prev = level;
            level = (alpha * y[t]) + ((1 - alpha) * (level + trend));
            trend = (beta * (level - prev)) + ((1 - beta) * trend);
        }

        forecast = new double[horizon];
        for (int h = 1; h <= horizon; h++) forecast[h - 1] = level + (h * trend);
        return sse;
    }
}
