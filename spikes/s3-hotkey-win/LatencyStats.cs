namespace s3_hotkey_win;

/// <summary>Small percentile helper, same convention as spikes/s1b-audio's p50/p95 reporting.</summary>
internal static class LatencyStats
{
    internal static (double p50, double p95, double max, int n) Summarize(List<double> valuesMs)
    {
        if (valuesMs.Count == 0) return (double.NaN, double.NaN, double.NaN, 0);
        var sorted = valuesMs.OrderBy(v => v).ToList();
        double Percentile(double p)
        {
            double idx = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);
            if (lo == hi) return sorted[lo];
            double frac = idx - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }
        return (Percentile(0.50), Percentile(0.95), sorted[^1], sorted.Count);
    }
}
