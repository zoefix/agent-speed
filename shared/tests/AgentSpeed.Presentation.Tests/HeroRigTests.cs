using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class HeroRigTests
{
    private static Vehicle Walker => HeroDriver.Art.Vehicles[0];

    private static WalkRigSpec WalkRig => Walker.Walk ?? throw new InvalidOperationException("The walker has no walk rig.");

    private static double Ground => Walker.GroundY ?? throw new InvalidOperationException("The walker has no ground.");

    private static PedalRigSpec PedalRig(string key)
    {
        var art = HeroDriver.Art;
        return art.Vehicles[art.IndexOf(key)].Pedal ?? throw new InvalidOperationException($"{key} has no pedal rig.");
    }

    [Theory]
    [InlineData(0, 0, 3, 10, 6, 6.5)]
    [InlineData(10, 10, 10, 30, 14.4, 14.4)]
    [InlineData(5, 5, -2, 18, 9, 8)]
    [InlineData(0, 0, 12, 1, 7, 7)]
    [InlineData(40, 36, 46, 68, 17.1, 16.64)]
    public void KneeKeepsBoneLengthsAndBendsForward(double hx, double hy, double fx, double fy, double l1, double l2)
    {
        var hip = new PointD(hx, hy);
        var foot = new PointD(fx, fy);
        Assert.True(Distance(hip, foot) < l1 + l2, "test case must be reachable");

        var knee = RigPose.Knee(hip, foot, l1, l2);

        Assert.Equal(l1, Distance(hip, knee), 9);
        Assert.Equal(l2, Distance(knee, foot), 9);
        Assert.True(Cross(hip, foot, knee) < 0, "The knee lies on the +x side of the hip–foot line (y down).");
    }

    [Fact]
    public void KneeOfAStraightDownLegPointsForward()
    {
        var knee = RigPose.Knee(new PointD(0, 0), new PointD(0, 20), 12, 12);
        Assert.True(knee.X > 0);
        Assert.Equal(10, knee.Y, 9);
    }

    [Fact]
    public void KneeOfAnUnreachableFootKeepsTheThighLength()
    {
        var hip = new PointD(0, 0);
        var knee = RigPose.Knee(hip, new PointD(40, 0), 10, 10);
        Assert.Equal(10, Distance(hip, knee), 9);
        Assert.True(Math.Abs(knee.Y) < 0.5, "A stretched leg lies almost on the hip–foot line.");

        var folded = RigPose.Knee(hip, hip, 10, 8);
        Assert.Equal(10, Distance(hip, folded), 9);
    }

    [Fact]
    public void WalkerHasFarLimbsBehindAndAHead()
    {
        var pose = new RigPose();
        pose.Walk(WalkRig, 3, Ground, 0);

        Assert.Equal(5, pose.Count);
        Assert.Equal(2, pose.FarCount);
        Assert.Equal([LimbInk.Far, LimbInk.Far, LimbInk.Near, LimbInk.Near, LimbInk.Near],
            Enumerable.Range(0, pose.Count).Select(i => pose[i].Ink));
        Assert.Equal([4, 3, 2, 4, 3], Enumerable.Range(0, pose.Count).Select(i => pose[i].Count));
        Assert.True(pose.HasHead);
        Assert.Equal(default, pose.RiderOffset);

        var hip = pose[2][0];
        var shoulder = pose[2][1];
        Assert.Equal(hip.X + WalkRig.Torso.X, shoulder.X, 9);
        Assert.Equal(hip.Y + WalkRig.Torso.Y, shoulder.Y, 9);
        Assert.Equal(new CircleD(shoulder.X + WalkRig.Head.X, shoulder.Y + WalkRig.Head.Y, WalkRig.Head.Radius), pose.Head);
    }

    [Fact]
    public void StanceFootRestsOnTheGroundAndMovesBackAtGroundSpeed()
    {
        var rig = WalkRig;
        var pose = new RigPose();
        var start = rig.Cycle * 0.05;
        var delta = rig.Cycle * (rig.Stance - 0.1);

        pose.Walk(rig, start, Ground, 0);
        var before = NearFoot(pose);
        pose.Walk(rig, start + delta, Ground, 0);
        var after = NearFoot(pose);

        Assert.Equal(Ground - 0.6, before.Y, 9);
        Assert.Equal(Ground - 0.6, after.Y, 9);
        Assert.Equal(before.X - delta, after.X, 9);

        for (var k = 0; k <= 20; k++)
        {
            var d = start + delta * k / 20;
            pose.Walk(rig, d, Ground, 0);
            Assert.Equal(before.X - (d - start), NearFoot(pose).X, 9);
            Assert.Equal(Ground - 0.6, NearFoot(pose).Y, 9);
        }
    }

    [Fact]
    public void StanceSlidesFromPlusAToMinusA()
    {
        var rig = WalkRig;
        var a = rig.Cycle * rig.Stance / 2;
        var pose = new RigPose();

        pose.Walk(rig, 0, Ground, 0);
        Assert.Equal(rig.Hip.X + a, NearFoot(pose).X, 9);

        pose.Walk(rig, rig.Cycle * rig.Stance * 0.999999, Ground, 0);
        Assert.Equal(rig.Hip.X - a, NearFoot(pose).X, 4);
    }

    [Fact]
    public void SwingLiftsTheFootAndCarriesItForward()
    {
        var rig = WalkRig;
        var pose = new RigPose();
        var mid = rig.Stance + (1 - rig.Stance) / 2;

        pose.Walk(rig, rig.Cycle * mid, Ground, 0);
        var foot = NearFoot(pose);

        Assert.Equal(Ground - 0.6 - rig.Lift, foot.Y, 9);
        Assert.Equal(rig.Hip.X, foot.X, 9);

        for (var s = 0.05; s < 1; s += 0.05)
        {
            pose.Walk(rig, rig.Cycle * (rig.Stance + s * (1 - rig.Stance)), Ground, 0);
            Assert.True(NearFoot(pose).Y < Ground - 0.6, $"swing {s:F2}: the foot leaves the ground");
        }
    }

    [Fact]
    public void FarLegRunsHalfACycleBehind()
    {
        var rig = WalkRig;
        var near = new RigPose();
        var far = new RigPose();
        for (var k = 0; k < 12; k++)
        {
            var d = rig.Cycle * k / 12;
            near.Walk(rig, d + rig.Cycle * 0.5, Ground, 0);
            far.Walk(rig, d, Ground, 0);
            var nearFoot = NearFoot(near);
            var farFoot = far[0][2];
            Assert.Equal(nearFoot.X, farFoot.X, 9);
            Assert.Equal(nearFoot.Y, farFoot.Y, 9);
        }
    }

    [Fact]
    public void HipBobsTwicePerCycle()
    {
        var rig = WalkRig;
        var pose = new RigPose();

        pose.Walk(rig, rig.Cycle * rig.Stance / 2, Ground, 0);
        Assert.Equal(rig.Hip.Y - rig.Bob, pose[2][0].Y, 9);

        pose.Walk(rig, rig.Cycle * (rig.Stance / 2 + 0.25), Ground, 0);
        Assert.Equal(rig.Hip.Y + rig.Bob, pose[2][0].Y, 9);

        pose.Walk(rig, rig.Cycle * (rig.Stance / 2 + 0.5), Ground, 0);
        Assert.Equal(rig.Hip.Y - rig.Bob, pose[2][0].Y, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7.3)]
    [InlineData(25)]
    public void StandingPosePutsTheFeetUnderTheHip(double distance)
    {
        var rig = WalkRig;
        var pose = new RigPose();
        pose.Walk(rig, distance, Ground, 1);

        var hip = pose[2][0];
        Assert.Equal(rig.Hip.X, hip.X, 9);
        Assert.Equal(Ground - 0.6 - 0.97 * (rig.Thigh + rig.Shin), hip.Y, 9);

        var near = NearFoot(pose);
        var far = pose[0][2];
        Assert.Equal(hip.X + 1.6, near.X, 9);
        Assert.Equal(Ground - 0.6, near.Y, 9);
        Assert.Equal(hip.X - 1.6, far.X, 9);
        Assert.Equal(Ground - 0.6, far.Y, 9);
        Assert.Equal(near.X + rig.Foot, pose[3][3].X, 9);
        Assert.Equal(near.Y, pose[3][3].Y, 9);

        var shoulder = pose[2][1];
        AssertArm(pose[4], shoulder, 4, rig.Elbow / 2);
        AssertArm(pose[1], shoulder, -4, rig.Elbow / 2);
    }

    [Fact]
    public void WalkingArmsSwingAgainstTheLegs()
    {
        var rig = WalkRig;
        var pose = new RigPose();
        pose.Walk(rig, 0, Ground, 0);
        var shoulder = pose[2][1];

        AssertArm(pose[4], shoulder, -rig.ArmSwing, rig.Elbow);
        AssertArm(pose[1], shoulder, rig.ArmSwing, rig.Elbow);
    }

    [Fact]
    public void WalkerLegsKeepTheirBoneLengths()
    {
        var rig = WalkRig;
        var pose = new RigPose();
        foreach (var stand in new[] { 0, 0.4, 1 })
        {
            for (var k = 0; k < 48; k++)
            {
                pose.Walk(rig, rig.Cycle * k / 48, Ground, stand);
                foreach (var leg in new[] { pose[0], pose[3] })
                {
                    Assert.Equal(rig.Thigh, Distance(leg[0], leg[1]), 9);
                    Assert.Equal(rig.Shin, Distance(leg[1], leg[2]), 9);
                    Assert.True(Cross(leg[0], leg[2], leg[1]) < 0, "knees bend forward");
                }
            }
        }
    }

    [Theory]
    [InlineData("bicycle")]
    [InlineData("ebike")]
    public void PedalsAreOppositeAtCrankLengthAndStayLevel(string key)
    {
        var rig = PedalRig(key);
        var pose = new RigPose();
        for (var k = 0; k < 36; k++)
        {
            var distance = 2 * Math.PI * rig.CrankRadius * k / 36 + 0.3;
            pose.Pedal(rig, distance, 0);

            Assert.Equal(4, pose.Count);
            Assert.Equal(2, pose.FarCount);
            Assert.False(pose.HasHead);
            Assert.Equal([LimbInk.Far, LimbInk.Far, LimbInk.Crank, LimbInk.Near], Enumerable.Range(0, 4).Select(i => pose[i].Ink));

            var far = Center(pose[1]);
            var near = Center(pose[2]);
            Assert.Equal(2 * rig.CrankPivot.X, near.X + far.X, 9);
            Assert.Equal(2 * rig.CrankPivot.Y, near.Y + far.Y, 9);
            Assert.Equal(rig.CrankLength, Distance(near, rig.CrankPivot), 9);

            var phi = distance / rig.CrankRadius;
            Assert.Equal(rig.CrankPivot.X - rig.CrankLength * Math.Sin(phi), near.X, 9);
            Assert.Equal(rig.CrankPivot.Y + rig.CrankLength * Math.Cos(phi), near.Y, 9);

            foreach (var pedal in new[] { pose[1], pose[2] })
            {
                Assert.Equal(pedal[0].Y, pedal[1].Y);
                Assert.Equal(rig.Pedal, pedal[1].X - pedal[0].X, 9);
            }

            Assert.Equal(far.X, pose[0][2].X, 9);
            Assert.Equal(far.Y, pose[0][2].Y, 9);
            Assert.Equal(near.X, pose[3][2].X, 9);
            Assert.Equal(near.Y, pose[3][2].Y, 9);
        }
    }

    [Theory]
    [InlineData("bicycle")]
    [InlineData("ebike")]
    public void RiderLegsKeepTheirBoneLengths(string key)
    {
        var rig = PedalRig(key);
        var pose = new RigPose();
        foreach (var lift in new[] { 0, 0.65, 1.3 })
        {
            for (var k = 0; k < 48; k++)
            {
                pose.Pedal(rig, 2 * Math.PI * rig.CrankRadius * k / 48, lift);
                foreach (var leg in new[] { pose[0], pose[3] })
                {
                    var reachable = Distance(leg[0], leg[2]) < rig.Thigh + rig.Shin - 1e-3;
                    Assert.True(reachable || lift > 0, "pedalling never overextends a leg");
                    Assert.Equal(rig.Thigh, Distance(leg[0], leg[1]), 9);
                    if (!reachable)
                    {
                        continue;
                    }

                    Assert.Equal(rig.Shin, Distance(leg[1], leg[2]), 9);
                    Assert.True(Cross(leg[0], leg[2], leg[1]) < 0, "knees bend forward");
                }
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(17.2, 1.3)]
    [InlineData(40, 0.7)]
    public void RiderOffsetFollowsTheBobAndTheStandUp(double distance, double lift)
    {
        var rig = PedalRig("bicycle");
        var pose = new RigPose();
        pose.Pedal(rig, distance, lift);

        var phi = distance / rig.CrankRadius;
        var expected = new PointD(0.6 * lift, rig.Bob * Math.Cos(2 * phi) - lift);
        Assert.Equal(expected.X, pose.RiderOffset.X, 12);
        Assert.Equal(expected.Y, pose.RiderOffset.Y, 12);

        var hip = pose[3][0];
        Assert.Equal(rig.Hip.X + expected.X, hip.X, 12);
        Assert.Equal(rig.Hip.Y + expected.Y, hip.Y, 12);
        Assert.Equal(hip, pose[0][0]);
    }

    [Fact]
    public void ReusedPoseSwitchesBetweenRigs()
    {
        var pose = new RigPose();
        pose.Walk(WalkRig, 0, Ground, 0);
        pose.Pedal(PedalRig("bicycle"), 0, 0);
        Assert.Equal(4, pose.Count);
        Assert.False(pose.HasHead);
        pose.Walk(WalkRig, 0, Ground, 1);
        Assert.Equal(5, pose.Count);
        Assert.True(pose.HasHead);
        Assert.Equal(default, pose.RiderOffset);
    }

    private static PointD NearFoot(RigPose pose) => pose[3][2];

    private static PointD Center(Limb limb) => new((limb[0].X + limb[1].X) / 2, (limb[0].Y + limb[1].Y) / 2);

    private static void AssertArm(Limb arm, PointD shoulder, double degrees, double elbowDegrees)
    {
        var rig = WalkRig;
        var theta = degrees * Math.PI / 180;
        var elbow = new PointD(shoulder.X + rig.UpperArm * Math.Sin(theta), shoulder.Y + rig.UpperArm * Math.Cos(theta));
        var t2 = theta + elbowDegrees * Math.PI / 180;
        var hand = new PointD(elbow.X + rig.Forearm * Math.Sin(t2), elbow.Y + rig.Forearm * Math.Cos(t2));
        Assert.Equal(shoulder, arm[0]);
        Assert.Equal(elbow.X, arm[1].X, 9);
        Assert.Equal(elbow.Y, arm[1].Y, 9);
        Assert.Equal(hand.X, arm[2].X, 9);
        Assert.Equal(hand.Y, arm[2].Y, 9);
    }

    private static double Cross(PointD hip, PointD foot, PointD knee) =>
        (foot.X - hip.X) * (knee.Y - hip.Y) - (foot.Y - hip.Y) * (knee.X - hip.X);

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
