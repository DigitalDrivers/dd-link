using System.Numerics;
using DDLink.Core;

namespace DDLink.Core.Tests;

public class BrakeTestsTests
{
    // A lap of 5 km, driven along z: the spline position is the distance over 5000 m, heading 0 points along +z.
    private const float Lap = 5000f;
    private const int Anna = 1; // in front
    private const int Ben = 2;  // 24 m behind her
    private const long RunUp = 100_000;

    /// <summary>Drives a car from a distance, changing its speed by <paramref name="acceleration"/> m/s², 20 updates a second.</summary>
    private static (float Distance, float Speed) Drive(BrakeTests tests, int car, long fromMs, long toMs, float distance, float speed,
        float acceleration = 0f, Func<long, float>? heading = null)
    {
        for (var t = fromMs; t <= toMs; t += 50)
        {
            tests.Moved(car, t, new Vector3(0, 0, distance), new Vector3(0, 0, speed), heading?.Invoke(t) ?? 0f, distance / Lap % 1f);
            distance += speed * 0.05f;
            speed = MathF.Max(0f, speed + acceleration * 0.05f);
        }
        return (distance, speed);
    }

    /// <summary>Five cars that went round before, at 60 m/s; when <paramref name="brakeAt1000"/>, they brake there for a corner.</summary>
    private static BrakeTests WithField(bool brakeAt1000 = false)
    {
        var tests = new BrakeTests();
        for (var car = 10; car < 15; car++)
        {
            var (distance, speed) = Drive(tests, car, 0, 16_650, 0f, 60f);
            if (brakeAt1000)
                Drive(tests, car, 16_700, 30_000, distance, speed, acceleration: -12f);
            else
                Drive(tests, car, 16_700, 30_000, distance, speed);
        }
        return tests;
    }

    /// <summary>
    /// Anna and Ben run up to the 1000 m mark at 60 m/s, Ben 24 m behind; then Anna slows at <paramref name="braking"/>
    /// m/s² for <paramref name="brakingMs"/>, Ben keeps his speed. Returns the moment he runs into her (after two seconds
    /// of 12 m/s² the gap is gone).
    /// </summary>
    private static long RunUpAndBrake(BrakeTests tests, float braking = 12f, long brakingMs = 2000, Func<long, float>? heading = null)
    {
        var (anna, annaSpeed) = Drive(tests, Anna, RunUp, RunUp + 16_650, 0f, 60f);
        var (ben, benSpeed) = Drive(tests, Ben, RunUp, RunUp + 16_650, -24f, 60f);
        var from = RunUp + 16_700;
        var contact = from + brakingMs;
        Drive(tests, Anna, from, contact, anna, annaSpeed, acceleration: -braking, heading);
        Drive(tests, Ben, from, contact, ben, benSpeed);
        return contact;
    }

    [Fact]
    public void Braking_hard_where_nobody_brakes_is_a_brake_test()
    {
        var tests = WithField();
        var contact = RunUpAndBrake(tests);

        Assert.True(tests.BrakedWithoutReason(Anna, Ben, contact));
        // Ben kept his speed: he did not brake at all.
        Assert.False(tests.BrakedWithoutReason(Ben, Anna, contact));
    }

    [Fact]
    public void Braking_where_the_field_brakes_is_racing()
    {
        var tests = WithField(brakeAt1000: true);
        var contact = RunUpAndBrake(tests);

        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void Slowing_down_steadily_gives_the_car_behind_time_to_react()
    {
        var tests = WithField();
        // From 60 to 36 m/s like the brake test, but over six seconds instead of two.
        var contact = RunUpAndBrake(tests, braking: 4f, brakingMs: 6000);

        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void Braking_for_a_car_that_is_stopped_ahead_has_a_reason()
    {
        var tests = WithField();
        // A car stands 120 m ahead of where Anna is when Ben runs into her.
        Drive(tests, 3, RunUp, RunUp + 20_000, 1220f, 0f);
        var contact = RunUpAndBrake(tests);

        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void A_car_ahead_at_the_usual_speed_is_no_reason_to_brake()
    {
        var tests = WithField();
        var (distance, speed) = Drive(tests, 3, RunUp, RunUp + 16_650, 100f, 60f);
        Drive(tests, 3, RunUp + 16_700, RunUp + 20_000, distance, speed);
        var contact = RunUpAndBrake(tests);

        Assert.True(tests.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void A_car_that_spins_is_not_braking()
    {
        var tests = WithField();
        // Anna's car turns away from where it goes, 40 degrees a second.
        var from = RunUp + 16_700;
        var contact = RunUpAndBrake(tests, heading: t => (t - from) / 1000f * 40f * MathF.PI / 180f);

        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void Without_a_field_to_compare_with_nothing_is_a_brake_test()
    {
        var tests = new BrakeTests();
        var contact = RunUpAndBrake(tests);

        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void An_earlier_contact_with_a_wall_or_a_third_car_is_a_reason_to_slow_down()
    {
        var wall = WithField();
        var contact = RunUpAndBrake(wall);
        wall.Collided(Anna, null, contact - 1500);
        Assert.False(wall.BrakedWithoutReason(Anna, Ben, contact));

        var thirdCar = WithField();
        contact = RunUpAndBrake(thirdCar);
        thirdCar.Collided(3, Anna, contact - 1500);
        Assert.False(thirdCar.BrakedWithoutReason(Anna, Ben, contact));

        // A first touch of the same two cars is part of the same brake test.
        var sameCars = WithField();
        contact = RunUpAndBrake(sameCars);
        sameCars.Collided(Ben, Anna, contact - 300);
        Assert.True(sameCars.BrakedWithoutReason(Anna, Ben, contact));
    }

    [Fact]
    public void Knows_who_went_into_the_pit_lane_around_a_moment()
    {
        var tests = new BrakeTests();
        tests.InPitLane(Anna, 108_000);
        tests.InPitLane(Anna, 109_000);

        Assert.True(tests.UsedThePitLane(Anna, 95_000, 120_000));
        Assert.False(tests.UsedThePitLane(Anna, 110_000, 130_000));
        Assert.False(tests.UsedThePitLane(Ben, 95_000, 120_000));
    }

    [Fact]
    public void Judges_only_what_it_has_just_seen()
    {
        var tests = WithField();
        var contact = RunUpAndBrake(tests);

        // Long after, or for a car that has left, there is nothing to judge.
        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact + 5000));
        tests.Forget(Anna);
        Assert.False(tests.BrakedWithoutReason(Anna, Ben, contact));
    }
}
