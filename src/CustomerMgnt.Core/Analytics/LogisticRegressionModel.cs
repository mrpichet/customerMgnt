using Common.Core.Compute;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// Logistic regression (binary classification) แบบ Pure C# — ใช้กับ Lead scoring, Churn และ Renewal
/// <list type="bullet">
/// <item>Standardize feature (z-score) ก่อนฝึก → learning rate เดียวใช้ได้กับทุก feature</item>
/// <item>Full-batch gradient descent + L2 regularization + class weighting (ข้อมูลไม่สมดุลเช่น churn มีน้อย)</item>
/// <item>คำนวณ gradient แบบขนาน (แบ่ง partition ต่อ thread แล้วรวม) และ dot product ด้วย SIMD (<see cref="VectorMath"/>) — เลือกเปิด/ปิดได้ผ่าน <see cref="ComputeOptions"/></item>
/// <item>อธิบายผลได้: contribution ต่อ feature = weight × ค่า standardized (log-odds)</item>
/// </list>
/// Immutable หลังฝึก → ใช้พร้อมกันหลาย thread ได้
/// </summary>
public sealed class LogisticRegressionModel
{
    private readonly float[] _weights;
    private readonly float[] _mean;
    private readonly float[] _std;
    private readonly bool _useSimd;

    private LogisticRegressionModel(string[] featureNames, float[] weights, float bias, float[] mean, float[] std, bool useSimd, int samples, double trainLogLoss)
    {
        FeatureNames = featureNames;
        _weights = weights;
        Bias = bias;
        _mean = mean;
        _std = std;
        _useSimd = useSimd;
        TrainingSamples = samples;
        TrainingLogLoss = trainLogLoss;
    }

    /// <summary>ชื่อ feature ตามลำดับ</summary>
    public IReadOnlyList<string> FeatureNames { get; }

    /// <summary>ค่า intercept</summary>
    public float Bias { get; }

    /// <summary>จำนวนตัวอย่างที่ใช้ฝึก (0 = โมเดลตั้งต้นจากผู้เชี่ยวชาญ)</summary>
    public int TrainingSamples { get; }

    /// <summary>log loss บนชุดฝึก (ยิ่งต่ำยิ่งดี)</summary>
    public double TrainingLogLoss { get; }

    /// <summary>น้ำหนักของ feature (หลัง standardize)</summary>
    public IReadOnlyList<float> Weights => _weights;

    /// <summary>
    /// สร้างโมเดลตั้งต้น (cold start) จากน้ำหนักที่ผู้เชี่ยวชาญกำหนด — ใช้เมื่อยังมีข้อมูลประวัติไม่พอฝึก
    /// </summary>
    /// <param name="featureNames">ชื่อ feature</param>
    /// <param name="weights">น้ำหนักบน feature ดิบ (ไม่ standardize)</param>
    /// <param name="bias">intercept</param>
    /// <param name="useSimd">ใช้ SIMD</param>
    /// <returns>โมเดล</returns>
    public static LogisticRegressionModel FromPrior(string[] featureNames, float[] weights, float bias, bool useSimd = true)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNotEqual(weights.Length, featureNames.Length);
        float[] zero = new float[weights.Length];
        float[] one = Enumerable.Repeat(1f, weights.Length).ToArray();
        return new LogisticRegressionModel(featureNames, (float[])weights.Clone(), bias, zero, one, useSimd, 0, double.NaN);
    }

    /// <summary>ฝึกโมเดล</summary>
    /// <param name="featureNames">ชื่อ feature (ความยาว = จำนวนคอลัมน์)</param>
    /// <param name="rows">ข้อมูลแต่ละแถว</param>
    /// <param name="labels">ผลลัพธ์จริง (true = เกิดเหตุการณ์ เช่น ปิดการขายได้)</param>
    /// <param name="compute">ค่าตั้ง SIMD/Parallel</param>
    /// <param name="epochs">จำนวนรอบ</param>
    /// <param name="learningRate">อัตราการเรียนรู้</param>
    /// <param name="l2">ค่า regularization</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โมเดลที่ฝึกแล้ว</returns>
    /// <exception cref="ArgumentException">ข้อมูลไม่สอดคล้องกัน</exception>
    public static LogisticRegressionModel Train(string[] featureNames, IReadOnlyList<float[]> rows, IReadOnlyList<bool> labels, ComputeOptions compute,
        int epochs = 300, float learningRate = 0.3f, float l2 = 0.01f, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(compute);
        int n = rows.Count, d = featureNames.Length;
        if (n == 0 || n != labels.Count || rows.Any(r => r.Length != d))
        {
            throw new ArgumentException("Rows and labels must be non-empty and have consistent dimensions.");
        }

        // Step 1: standardize — คำนวณ mean/std ต่อคอลัมน์ แล้วสร้างเมทริกซ์แบบ row-major ต่อเนื่อง (cache-friendly)
        float[] mean = new float[d], std = new float[d];
        for (int j = 0; j < d; j++)
        {
            double m = 0;
            for (int i = 0; i < n; i++) m += rows[i][j];
            m /= n;
            double v = 0;
            for (int i = 0; i < n; i++) v += (rows[i][j] - m) * (rows[i][j] - m);
            mean[j] = (float)m;
            std[j] = (float)Math.Max(Math.Sqrt(v / n), 1e-6);
        }

        float[] x = new float[n * d];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < d; j++)
            {
                x[(i * d) + j] = (rows[i][j] - mean[j]) / std[j];
            }
        }

        // Step 2: class weight ให้คลาสที่น้อยกว่ามีน้ำหนักมากขึ้น
        int positives = labels.Count(l => l);
        float wPos = positives == 0 ? 1 : (float)n / (2 * positives);
        float wNeg = positives == n ? 1 : (float)n / (2 * (n - positives));
        float[] y = labels.Select(l => l ? 1f : 0f).ToArray();
        float[] sampleW = labels.Select(l => l ? wPos : wNeg).ToArray();

        // Step 3: gradient descent — gradient รวมจากหลาย partition แบบขนาน (แต่ละ partition มี buffer ของตัวเอง → ไม่ต้อง lock ต่อแถว)
        float[] w = new float[d];
        float b = 0;
        int partitions = compute.UseParallel && n >= compute.ParallelThreshold ? Math.Max(1, compute.MaxDegreeOfParallelism > 0 ? compute.MaxDegreeOfParallelism : Environment.ProcessorCount) : 1;
        int chunk = (n + partitions - 1) / partitions;
        var gradW = new float[partitions][];
        var gradB = new double[partitions];
        for (int p = 0; p < partitions; p++) gradW[p] = new float[d];

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            void Partition(int p)
            {
                // Step 3.1: gradient ของ partition p
                float[] g = gradW[p];
                Array.Clear(g);
                double gb = 0;
                int end = Math.Min(n, (p + 1) * chunk);
                for (int i = p * chunk; i < end; i++)
                {
                    ReadOnlySpan<float> xi = x.AsSpan(i * d, d);
                    float z = VectorMath.Dot(xi, w, compute.UseSimd) + b;
                    float err = (Sigmoid(z) - y[i]) * sampleW[i];
                    for (int j = 0; j < d; j++) g[j] += err * xi[j];
                    gb += err;
                }

                gradB[p] = gb;
            }

            // partition ถูกสร้างตาม ComputeOptions แล้ว: มากกว่า 1 = ข้อมูลเกิน threshold และเปิด Parallel
            if (partitions > 1)
            {
                Parallel.For(0, partitions, new ParallelOptions { MaxDegreeOfParallelism = partitions, CancellationToken = ct }, Partition);
            }
            else
            {
                Partition(0);
            }

            // Step 3.2: รวม gradient ทุก partition แล้วปรับ weight (+L2)
            for (int j = 0; j < d; j++)
            {
                float sum = 0;
                for (int p = 0; p < partitions; p++) sum += gradW[p][j];
                w[j] -= learningRate * ((sum / n) + (l2 * w[j]));
            }

            b -= learningRate * (float)(gradB.Sum() / n);
        }

        // Step 4: log loss ของชุดฝึก (บอกคุณภาพคร่าวๆ)
        double loss = 0;
        for (int i = 0; i < n; i++)
        {
            double pr = Math.Clamp(Sigmoid(VectorMath.Dot(x.AsSpan(i * d, d), w, compute.UseSimd) + b), 1e-7, 1 - 1e-7);
            loss -= (y[i] * Math.Log(pr)) + ((1 - y[i]) * Math.Log(1 - pr));
        }

        return new LogisticRegressionModel(featureNames, w, b, mean, std, compute.UseSimd, n, loss / n);
    }

    /// <summary>ทำนายความน่าจะเป็น</summary>
    /// <param name="features">feature ดิบ (ลำดับเดียวกับตอนฝึก)</param>
    /// <returns>ความน่าจะเป็น 0..1</returns>
    public double Predict(ReadOnlySpan<float> features)
    {
        // Step 1: standardize ลง buffer บน stack (ไม่ allocate)
        Span<float> z = features.Length <= 256 ? stackalloc float[features.Length] : new float[features.Length];
        Standardize(features, z);

        // Step 2: σ(w·z + b)
        return Sigmoid(VectorMath.Dot(z, _weights, _useSimd) + Bias);
    }

    /// <summary>ทำนายพร้อมอธิบายผล (contribution ต่อ feature เรียงตามขนาดผลกระทบ)</summary>
    /// <param name="features">feature ดิบ</param>
    /// <param name="top">จำนวนปัจจัยที่ต้องการ</param>
    /// <returns>(ความน่าจะเป็น, ปัจจัยหลัก)</returns>
    public (double Probability, IReadOnlyList<Domain.FactorDTO> Factors) Explain(ReadOnlySpan<float> features, int top = 5)
    {
        Span<float> z = features.Length <= 256 ? stackalloc float[features.Length] : new float[features.Length];
        Standardize(features, z);
        var factors = new Domain.FactorDTO[features.Length];
        for (int j = 0; j < features.Length; j++)
        {
            factors[j] = new Domain.FactorDTO(FeatureNames[j], Math.Round(features[j], 4), Math.Round(_weights[j] * z[j], 4));
        }

        double p = Sigmoid(VectorMath.Dot(z, _weights, _useSimd) + Bias);
        return (p, factors.OrderByDescending(f => Math.Abs(f.Contribution)).Take(top).ToList());
    }

    /// <summary>standardize feature ด้วย mean/std ของชุดฝึก</summary>
    private void Standardize(ReadOnlySpan<float> features, Span<float> destination)
    {
        if (features.Length != _weights.Length)
        {
            throw new ArgumentException($"Expected {_weights.Length} features but got {features.Length}.");
        }

        for (int j = 0; j < features.Length; j++)
        {
            destination[j] = (features[j] - _mean[j]) / _std[j];
        }
    }

    /// <summary>sigmoid ที่เสถียรเชิงตัวเลข</summary>
    private static float Sigmoid(float z) => z >= 0 ? 1f / (1f + MathF.Exp(-z)) : MathF.Exp(z) / (1f + MathF.Exp(z));
}
