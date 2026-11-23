using Common.Core.Compute;

namespace CustomerMgnt.Core.Analytics;

/// <summary>ผล k-means</summary>
/// <param name="Assignments">กลุ่มของแต่ละแถว</param>
/// <param name="Centroids">จุดศูนย์กลาง (k × d)</param>
/// <param name="Iterations">จำนวนรอบ</param>
/// <param name="Inertia">ผลรวมระยะทางกำลังสองภายในกลุ่ม</param>
public sealed record KMeansResult(int[] Assignments, float[][] Centroids, int Iterations, double Inertia);

/// <summary>
/// k-means++ แบบ Pure C# สำหรับ Dynamic customer segmentation
/// ขั้น assignment (หนักที่สุด: n × k × d) ทำขนานด้วย <see cref="ParallelBatch"/> และคำนวณระยะทางด้วย SIMD
/// ใช้ seed คงที่ → ผลซ้ำได้ (deterministic) ทำให้กลุ่มลูกค้าไม่สลับไปมาระหว่างการคำนวณแต่ละครั้งโดยไม่จำเป็น
/// </summary>
public static class KMeansClusterer
{
    /// <summary>จัดกลุ่ม</summary>
    /// <param name="rows">ข้อมูล (ควร standardize แล้ว)</param>
    /// <param name="k">จำนวนกลุ่ม</param>
    /// <param name="compute">ค่าตั้ง SIMD/Parallel</param>
    /// <param name="maxIterations">รอบสูงสุด</param>
    /// <param name="seed">seed ของการสุ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลการจัดกลุ่ม</returns>
    public static KMeansResult Cluster(IReadOnlyList<float[]> rows, int k, ComputeOptions compute, int maxIterations = 100, int seed = 42, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(compute);
        int n = rows.Count;
        if (n == 0)
        {
            return new KMeansResult([], [], 0, 0);
        }

        k = Math.Clamp(k, 1, n);
        int d = rows[0].Length;
        var rng = new Random(seed);

        // Step 1: k-means++ init — เลือกจุดเริ่มที่อยู่ห่างกัน (ลดโอกาสได้คำตอบที่แย่)
        var centroids = new float[k][];
        centroids[0] = (float[])rows[rng.Next(n)].Clone();
        double[] dist = new double[n];
        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < c; j++) best = Math.Min(best, SquaredDistance(rows[i], centroids[j], compute.UseSimd));
                dist[i] = best;
                total += best;
            }

            double r = rng.NextDouble() * total;
            int pick = 0;
            for (double acc = 0; pick < n - 1; pick++)
            {
                acc += dist[pick];
                if (acc >= r) break;
            }

            centroids[c] = (float[])rows[pick].Clone();
        }

        // Step 2: Lloyd iterations
        int[] assign = new int[n];
        int iter = 0;
        bool changed = true;
        while (changed && iter < maxIterations)
        {
            ct.ThrowIfCancellationRequested();
            iter++;
            int changes = 0;

            // Step 2.1: assignment (ขนาน + SIMD)
            ParallelBatch.For(n, compute, i =>
            {
                int bestC = 0;
                double best = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double dd = SquaredDistance(rows[i], centroids[c], compute.UseSimd);
                    if (dd < best)
                    {
                        best = dd;
                        bestC = c;
                    }
                }

                if (assign[i] != bestC || iter == 1)
                {
                    assign[i] = bestC;
                    Interlocked.Increment(ref changes);
                }
            }, ct);

            // Step 2.2: update centroid = ค่าเฉลี่ยของสมาชิก (กลุ่มว่าง → คงค่าเดิม)
            var sums = new double[k, d];
            int[] counts = new int[k];
            for (int i = 0; i < n; i++)
            {
                counts[assign[i]]++;
                for (int j = 0; j < d; j++) sums[assign[i], j] += rows[i][j];
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0) continue;
                for (int j = 0; j < d; j++) centroids[c][j] = (float)(sums[c, j] / counts[c]);
            }

            changed = changes > 0;
        }

        // Step 3: inertia
        double inertia = 0;
        for (int i = 0; i < n; i++) inertia += SquaredDistance(rows[i], centroids[assign[i]], compute.UseSimd);
        return new KMeansResult(assign, centroids, iter, inertia);
    }

    /// <summary>ระยะทางกำลังสอง ‖a−b‖² = a·a − 2a·b + b·b (ใช้ dot product แบบ SIMD)</summary>
    internal static double SquaredDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b, bool simd) =>
        Math.Max(0, VectorMath.Dot(a, a, simd) - (2 * VectorMath.Dot(a, b, simd)) + VectorMath.Dot(b, b, simd));
}
