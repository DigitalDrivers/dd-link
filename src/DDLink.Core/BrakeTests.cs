using System.Numerics;

namespace DDLink.Core;

/// <summary>
/// Tells a brake test from a car that had to slow down. The platform lets a car that is run into from behind off,
/// which is only fair while that car did not brake hard for no reason just before. Fed with every position update of
/// every car, this knows how fast the field usually goes at each point of the lap; at a contact it looks back at the
/// last seconds of a car. A brake test is all of: the car lost a lot of speed, braking hard; it ended up far slower than
/// the field usually is at that point; it pointed where it went all the while (a car that spins is not braking); no car
/// ahead was stopped or far slower than usual; and it had not touched a wall or a third car just before. Whoever goes
/// into the pit lane right after was slowing down for it (<see cref="UsedThePitLane"/>). Anything in doubt is no brake
/// test: then the platform's rule for the rear holds, and the stewards decide on a protest.
/// </summary>
public sealed class BrakeTests
{
    /// <summary>The lap is cut into this many stretches, each with the speeds of the field's last passes.</summary>
    public const int Stretches = 1000;
    private const int PassesKept = 15;
    private const int PassesNeeded = 5;
    /// <summary>How far back a contact is looked at.</summary>
    public const long WindowMs = 2500;
    private const long KeptMs = 3000;
    private const long FreshMs = 500;
    // Lost at least 30 km/h, at 0.8 g or more over half a second.
    private const float SpeedLost = 30f / 3.6f;
    private const float HardBraking = 8f;
    private const long BrakingOverMs = 500;
    // Far slower than usual: below 70 per cent of the field's usual speed there.
    private const float UsualShare = 0.7f;
    private const float Stopped = 5f;
    private static readonly float MaxSlip = 25f * MathF.PI / 180f;
    private const float LookAhead = 200f;
    private static readonly float AheadCone = MathF.Cos(45f * MathF.PI / 180f);
    private const long EarlierContactMs = 5000;
    // The game reports the pit lane once a second.
    private const long PitLaneGapMs = 3000;

    private readonly record struct Sample(long TimeMs, Vector3 Position, Vector3 Velocity, float Heading, float Spline)
    {
        public float Speed => new Vector2(Velocity.X, Velocity.Z).Length();
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, List<Sample>> _history = new();
    private readonly Dictionary<int, int> _stretchOf = new();
    private readonly List<float>[] _passes = Enumerable.Range(0, Stretches).Select(_ => new List<float>()).ToArray();
    private readonly Dictionary<int, List<(long From, long To)>> _pitLane = new();
    private readonly List<(int Car, int? Other, long TimeMs)> _contacts = [];

    /// <summary>
    /// A position update. <paramref name="heading"/> is the game's: the car points along (-sin, 0, cos) of it.
    /// <paramref name="spline"/> is the position on the lap, 0 to 1.
    /// </summary>
    public void Moved(int car, long timeMs, Vector3 position, Vector3 velocity, float heading, float spline)
    {
        var sample = new Sample(timeMs, position, velocity, heading, spline);
        lock (_lock)
        {
            if (!_history.TryGetValue(car, out var history))
                _history[car] = history = [];
            history.Add(sample);
            history.RemoveAll(s => s.TimeMs < timeMs - KeptMs);

            // A car counts once per stretch it enters, unless it is in the pit lane or sliding.
            var stretch = StretchOf(spline);
            if (_stretchOf.TryGetValue(car, out var before) && before != stretch && !InPitLaneAt(car, timeMs) && Slip(sample) <= MaxSlip)
            {
                var passes = _passes[stretch];
                passes.Add(sample.Speed);
                if (passes.Count > PassesKept)
                    passes.RemoveAt(0);
            }
            _stretchOf[car] = stretch;
        }
    }

    /// <summary>A contact, with another car or (other null) with a wall.</summary>
    public void Collided(int car, int? other, long timeMs)
    {
        lock (_lock)
        {
            _contacts.Add((car, other, timeMs));
            _contacts.RemoveAll(c => c.TimeMs < timeMs - EarlierContactMs);
        }
    }

    /// <summary>The car's game says it is in the pit lane.</summary>
    public void InPitLane(int car, long timeMs)
    {
        lock (_lock)
        {
            if (!_pitLane.TryGetValue(car, out var visits))
                _pitLane[car] = visits = [];
            if (visits.Count > 0 && timeMs - visits[^1].To <= PitLaneGapMs)
                visits[^1] = (visits[^1].From, timeMs);
            else
                visits.Add((timeMs, timeMs));
        }
    }

    /// <summary>Whether the car was in the pit lane at some moment between the two.</summary>
    public bool UsedThePitLane(int car, long fromMs, long toMs)
    {
        lock (_lock)
            return _pitLane.TryGetValue(car, out var visits) && visits.Any(v => v.From <= toMs && v.To >= fromMs);
    }

    /// <summary>The driver left: a new one in the car starts without a past.</summary>
    public void Forget(int car)
    {
        lock (_lock)
        {
            _history.Remove(car);
            _stretchOf.Remove(car);
        }
    }

    /// <summary>Whether the car braked hard for no reason in the seconds before a contact with the other car at this moment.</summary>
    public bool BrakedWithoutReason(int car, int other, long timeMs)
    {
        lock (_lock)
        {
            var window = Window(car, timeMs);
            if (window.Count < 5 || timeMs - window[^1].TimeMs > FreshMs)
                return false;
            var now = window[^1];

            if (window.Max(s => s.Speed) - now.Speed < SpeedLost || !BrakedHard(window))
                return false;
            if (window.Any(s => s.Speed > Stopped && Slip(s) > MaxSlip))
                return false;
            if (Usual(now.Spline) is not { } usual || now.Speed >= UsualShare * usual)
                return false;
            if (_history.Keys.Any(id => id != car && SlowAhead(now, Window(id, timeMs))))
                return false;
            var earlier = _contacts.Any(c => c.TimeMs >= timeMs - EarlierContactMs && c.TimeMs < timeMs
                && (c.Car == car || c.Other == car) && !(c.Car == other || c.Other == other));
            return !earlier;
        }
    }

    private List<Sample> Window(int car, long timeMs) => _history.TryGetValue(car, out var history)
        ? history.Where(s => s.TimeMs >= timeMs - WindowMs && s.TimeMs <= timeMs).ToList()
        : [];

    private static int StretchOf(float spline) => Math.Clamp((int)(spline * Stretches), 0, Stretches - 1);

    private float? Usual(float spline)
    {
        var passes = _passes[StretchOf(spline)];
        if (passes.Count < PassesNeeded)
            return null;
        var sorted = passes.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private bool InPitLaneAt(int car, long timeMs) =>
        _pitLane.TryGetValue(car, out var visits) && visits.Count > 0 && timeMs - visits[^1].To <= PitLaneGapMs && timeMs >= visits[^1].From;

    private static Vector3 Forward(float heading) => new(-MathF.Sin(heading), 0f, MathF.Cos(heading));

    /// <summary>The angle between where the car points and where it goes.</summary>
    private static float Slip(Sample s)
    {
        var going = new Vector3(s.Velocity.X, 0f, s.Velocity.Z);
        if (going.Length() < 0.1f)
            return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(Forward(s.Heading), Vector3.Normalize(going)), -1f, 1f));
    }

    // At least 0.8 g over some half second of the window.
    private static bool BrakedHard(List<Sample> window)
    {
        var i = 0;
        for (var j = 0; j < window.Count; j++)
        {
            while (i + 1 < j && window[j].TimeMs - window[i + 1].TimeMs >= BrakingOverMs)
                i++;
            var seconds = (window[j].TimeMs - window[i].TimeMs) / 1000f;
            if (seconds * 1000f >= BrakingOverMs && (window[i].Speed - window[j].Speed) / seconds >= HardBraking)
                return true;
        }
        return false;
    }

    // Another car within 200 m in front that was stopped or far slower than usual at some moment of the window.
    private bool SlowAhead(Sample car, List<Sample> other)
    {
        if (other.Count == 0)
            return false;
        var toOther = other[^1].Position - car.Position;
        toOther.Y = 0f;
        var distance = toOther.Length();
        if (distance > LookAhead || distance < 0.1f || Vector3.Dot(toOther / distance, Forward(car.Heading)) < AheadCone)
            return false;
        return other.Any(s => s.Speed < Stopped || (Usual(s.Spline) is { } usual && s.Speed < UsualShare * usual));
    }
}
