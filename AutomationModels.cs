using System.Numerics;

namespace ARankHuntTourAssistant;

internal enum HunterState
{
    Stopped, PreparingZone, Travelling, Patrol, Found, ApproachingTarget, Landing, PreparingCompanion, Combat,
    Remounting, ChildWaiting, ChildApproach, Complete, Error
}

internal sealed record MobNotice(long Seq, string Type, uint Territory, string Mob, float X, float Y, float Z)
{
    public Vector3 Position => new(X, Y, Z);
}
