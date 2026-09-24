namespace VectorNNTP.StandaloneTxPoc;

internal static class RunStats
{
    public static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    public static double Mean(double[] values)
    {
        double sum = 0;
        for (var i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return values.Length == 0 ? 0 : sum / values.Length;
    }

    public static (double Min, double Max) MinMax(double[] values)
    {
        var min = values[0];
        var max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < min)
            {
                min = values[i];
            }

            if (values[i] > max)
            {
                max = values[i];
            }
        }

        return (min, max);
    }
}
