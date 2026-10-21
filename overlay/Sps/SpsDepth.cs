namespace ShockWave.Overlay.Sps;

// Original implementation of the documented contact geometry: learn the root
// offset while the tip is outside, then measure insertion from the root when the
// capsule tip contact saturates. OGB contacts have a one-metre receiver radius;
// the root sender contributes its 1 cm radius. No geometry is persisted.
internal sealed class SpsDepth
{
    public double? Length { get; private set; }
    public double Value { get; private set; }
    public double Rate { get; private set; }
    public double RateFrom { get; private set; } = double.NegativeInfinity;
    private double received = double.NegativeInfinity;
    public void Update(SpsSignal root, SpsSignal tip)
    {
        var at = Math.Max(root.ReceivedAt, tip.ReceivedAt); if (at <= received) return;
        if (tip.Value <= 0) Length = null;
        else if (tip.Value < 1 - .000001 && root.Value > 0 && tip.Value > root.Value)
        {
            var length = tip.Value - root.Value + .01;
            if (length is >= .02 and <= 1.01) Length = length;
        }
        var value = tip.Value >= 1 - .000001 && root.Value > 0 && Length is > 0
            ? Math.Clamp(1 - (1 - root.Value) / Length.Value, 0, 1) : 0;
        var dt = at - received; Rate = dt is >= .005 and <= .5 ? (value - Value) / dt : 0;
        RateFrom = received; received = at; Value = value;
    }
}
