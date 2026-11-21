namespace Lanyard.Shared.Audio;

/// <summary>
/// One second-order filter, with coefficients from the RBJ Audio EQ Cookbook. Kept free of NAudio
/// so the maths can be unit tested; the kiosk wraps it in a sample provider.
/// </summary>
/// <remarks>
/// The state is kept in doubles: a 100 Hz shelf at 48 kHz has poles very close to the unit circle,
/// and float state there drifts into audible noise. Changing the coefficients keeps the state, so
/// moving a slider while music plays doesn't click.
/// </remarks>
public sealed class BiQuadFilter
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public static BiQuadFilter LowShelf(double sampleRate, double frequency, double gainDb)
    {
        BiQuadFilter filter = new();
        filter.SetLowShelf(sampleRate, frequency, gainDb);
        return filter;
    }

    public static BiQuadFilter HighShelf(double sampleRate, double frequency, double gainDb)
    {
        BiQuadFilter filter = new();
        filter.SetHighShelf(sampleRate, frequency, gainDb);
        return filter;
    }

    public static BiQuadFilter Peaking(double sampleRate, double frequency, double q, double gainDb)
    {
        BiQuadFilter filter = new();
        filter.SetPeaking(sampleRate, frequency, q, gainDb);
        return filter;
    }

    public void SetLowShelf(double sampleRate, double frequency, double gainDb)
    {
        (double a, double cos, double alpha) = Prepare(sampleRate, frequency, gainDb, q: Math.Sqrt(0.5));
        double sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;

        Set(
            b0: a * ((a + 1) - (a - 1) * cos + sqrtA2Alpha),
            b1: 2 * a * ((a - 1) - (a + 1) * cos),
            b2: a * ((a + 1) - (a - 1) * cos - sqrtA2Alpha),
            a0: (a + 1) + (a - 1) * cos + sqrtA2Alpha,
            a1: -2 * ((a - 1) + (a + 1) * cos),
            a2: (a + 1) + (a - 1) * cos - sqrtA2Alpha);
    }

    public void SetHighShelf(double sampleRate, double frequency, double gainDb)
    {
        (double a, double cos, double alpha) = Prepare(sampleRate, frequency, gainDb, q: Math.Sqrt(0.5));
        double sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;

        Set(
            b0: a * ((a + 1) + (a - 1) * cos + sqrtA2Alpha),
            b1: -2 * a * ((a - 1) + (a + 1) * cos),
            b2: a * ((a + 1) + (a - 1) * cos - sqrtA2Alpha),
            a0: (a + 1) - (a - 1) * cos + sqrtA2Alpha,
            a1: 2 * ((a - 1) - (a + 1) * cos),
            a2: (a + 1) - (a - 1) * cos - sqrtA2Alpha);
    }

    public void SetPeaking(double sampleRate, double frequency, double q, double gainDb)
    {
        (double a, double cos, double alpha) = Prepare(sampleRate, frequency, gainDb, q);

        Set(
            b0: 1 + alpha * a,
            b1: -2 * cos,
            b2: 1 - alpha * a,
            a0: 1 + alpha / a,
            a1: -2 * cos,
            a2: 1 - alpha / a);
    }

    public float Process(float input)
    {
        double output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;

        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;

        return (float)output;
    }

    private static (double A, double Cos, double Alpha) Prepare(double sampleRate, double frequency, double gainDb, double q)
    {
        // A low sample rate (22.05 kHz) would put the treble shelf near Nyquist, where the
        // cookbook formulas break down, so keep the corner safely below it.
        double safeFrequency = Math.Min(frequency, sampleRate * 0.45);
        double w0 = 2 * Math.PI * safeFrequency / sampleRate;

        return (Math.Pow(10, gainDb / 40), Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }
}
