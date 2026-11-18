namespace DynamicIsland;

/// <summary>
/// Damped spring using Apple's designer parameters (WWDC18 "Designing Fluid Interfaces"):
///   response     = how quickly it reaches the target, in seconds (not a duration)
///   dampingRatio = 1.0 no overshoot; below 1.0 overshoots (elastic)
/// Re-targeting keeps the current value and velocity, so every animation is interruptible
/// with no visible jump or "brick wall".
/// </summary>
public sealed class Spring
{
    private const double SubStep = 1.0 / 240.0;

    public double Value;
    public double Velocity;
    public double Target;

    private double _stiffness;
    private double _damping;

    public Spring(double value, double response = 0.45, double dampingRatio = 0.78)
    {
        Value = Target = value;
        Configure(response, dampingRatio);
    }

    public void Configure(double response, double dampingRatio)
    {
        double omega = 2 * Math.PI / response;
        _stiffness = omega * omega;
        _damping = 2 * dampingRatio * omega;
    }

    public bool IsMoving => Math.Abs(Value - Target) > 0.01 || Math.Abs(Velocity) > 0.01;

    /// <summary>Advances the spring. Returns true while it is still moving.</summary>
    public bool Step(double dt)
    {
        while (dt > 0)
        {
            double h = Math.Min(dt, SubStep);
            double accel = -_stiffness * (Value - Target) - _damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }

        if (Math.Abs(Value - Target) < 0.02 && Math.Abs(Velocity) < 0.02)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}
