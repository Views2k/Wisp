using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AmbientBackdropSceneTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3.5)]
    [InlineData(80)]
    [InlineData(239.9)]
    [InlineData(100_000)]
    public void SceneIsDeterministicAcrossInstances(double seconds)
    {
        var first = new AmbientBackdropScene(12345);
        var second = new AmbientBackdropScene(12345);
        first.Update(1280, 800, 0);
        first.Update(1280, 800, seconds);
        second.Update(1280, 800, seconds);

        Assert.Equal(first.Particles.ToArray(), second.Particles.ToArray());
    }

    [Fact]
    public void SeedChangesTheFieldAndItsIndependentTrajectories()
    {
        var first = new AmbientBackdropScene(12345);
        var second = new AmbientBackdropScene(67890);
        first.Update(1280, 800, 30);
        second.Update(1280, 800, 30);

        Assert.NotEqual(first.Particles[0], second.Particles[0]);
    }

    [Theory]
    [InlineData(480, 760)]
    [InlineData(1024, 640)]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void FramesStayFiniteAndBoundedAcrossViewports(double width, double height)
    {
        var scene = new AmbientBackdropScene();
        foreach (var seconds in new[] { 0d, 1d / 30, 12.5, 80, 239.9, 100_000 })
        {
            scene.Update(width, height, seconds);

            Assert.Equal(2048, scene.Particles.Length);
            foreach (var particle in scene.Particles)
            {
                Assert.InRange(particle.Position.X, -width, width * 2);
                Assert.InRange(particle.Position.Y, -height, height * 2);
                Assert.InRange(particle.Radius, 0.5, 21);
                Assert.InRange(particle.Opacity, 0, 0.4);
                Assert.InRange(particle.Softness, 0.18, 0.82);
                Assert.InRange(particle.Red, 0.25, 0.54);
                Assert.InRange(particle.Green, 0.40, 0.60);
                Assert.InRange(particle.Blue, 0.39, 0.60);
            }
        }
    }

    [Fact]
    public void FirstFrameHasMultiplePopulatedStreamsAndContinuousDepth()
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 900, 0);
        var particles = scene.Particles.ToArray();
        var visible = particles.Where(particle =>
            particle.Position.X >= 0 && particle.Position.X <= 1280 &&
            particle.Position.Y >= 0 && particle.Position.Y <= 900 &&
            particle.Opacity > 0.01).ToArray();
        var occupiedRows = visible.Select(particle => (int)(particle.Position.Y / 150)).Distinct().Count();

        Assert.InRange(visible.Length, 700, 2048);
        Assert.True(occupiedRows >= 5);
        Assert.True(visible.Count(particle => particle.Radius < 2.2) > 100);
        Assert.True(particles.Count(particle => particle.Radius > 4) > 50);
        Assert.True(particles.Count(particle => particle.Softness > 0.5) > 50);
        Assert.True(particles.Select(particle => Math.Round(particle.Softness, 3)).Distinct().Count() > 100);
    }

    [Fact]
    public void ProjectionUsesProductionPerspectiveAndDarkMaterial()
    {
        var particle = AmbientBackdropScene.Project(1, -1, -1.65, 0.5, 0, 1280, 900);
        var pointSize = 1.15 + Math.Pow(0.56, 1.38) * 31;

        Assert.Equal((1 + 1.7320508 / 6.65) * 640, particle.Position.X, 10);
        Assert.Equal((1 + 1.7320508 / 6.65) * 450, particle.Position.Y, 10);
        Assert.Equal(pointSize / 2, particle.Radius, 10);
        Assert.Equal(0.115 * 0.78 / Math.Sqrt(Math.Max(1, pointSize * 0.20)), particle.Opacity, 10);
        Assert.Equal(0.82, particle.Softness, 10);
        Assert.Equal(0.3776, particle.Red, 10);
        Assert.Equal(0.488, particle.Green, 10);
        Assert.Equal(0.4824, particle.Blue, 10);
    }

    [Fact]
    public void LifeFadesAtBothBoundariesAndOutputHeightControlsPointSize()
    {
        var born = AmbientBackdropScene.Project(0, 0, 0, 1, 0.5, 1280, 900);
        var mature = AmbientBackdropScene.Project(0, 0, 0, 0.5, 0.5, 1280, 900);
        var expired = AmbientBackdropScene.Project(0, 0, 0, 0, 0.5, 1280, 900);
        var larger = AmbientBackdropScene.Project(0, 0, 0, 0.5, 0.5, 2560, 1800);

        Assert.Equal(0, born.Opacity);
        Assert.Equal(0, expired.Opacity);
        Assert.True(mature.Opacity > 0.25);
        Assert.Equal(mature.Radius * 2, larger.Radius, 10);
        Assert.True(larger.Opacity <= mature.Opacity);
    }

    [Fact]
    public void PointerInfluenceIsBoundedAndLocal()
    {
        var pointer = new AmbientPoint(0.49, 0.5);
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 40);
        var neutral = scene.Particles.ToArray();
        scene.Update(1280, 800, 40, pointer, 1);
        var changed = 0;

        for (var index = 0; index < neutral.Length; index++)
        {
            var next = scene.Particles[index];
            var displacement = Distance(neutral[index].Position, next.Position);
            Assert.InRange(displacement, 0, 800 * 0.013);
            Assert.Equal(neutral[index] with { Position = next.Position }, next);
            if (Distance(neutral[index].Position, new AmbientPoint(pointer.X * 1280, pointer.Y * 800)) >= 800 * 0.22)
                Assert.Equal(0, displacement);
            if (displacement > 0)
                changed++;
        }
        Assert.True(changed > 20);
    }

    [Fact]
    public void InvalidPointerInputIsNeutral()
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 20);
        var neutral = scene.Particles.ToArray();

        scene.Update(1280, 800, 20, new AmbientPoint(double.NaN, 0.5), 1);

        Assert.Equal(neutral, scene.Particles.ToArray());
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(1280, 0)]
    [InlineData(-1, 800)]
    [InlineData(double.NaN, 800)]
    [InlineData(1280, double.PositiveInfinity)]
    public void InvalidViewportClearsTheFrame(double width, double height)
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 20);
        scene.Update(width, height, 20);

        Assert.True(scene.Particles.IsEmpty);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTimeUsesTheStaticComposition(double seconds)
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 0);
        var particles = scene.Particles.ToArray();
        scene.Update(1280, 800, seconds);

        Assert.Equal(particles, scene.Particles.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31.99)]
    [InlineData(79.99)]
    [InlineData(500)]
    public void VisibleParticlesMoveContinuouslyWithoutSynchronizedResets(double seconds)
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, seconds);
        var particles = scene.Particles.ToArray();
        scene.Update(1280, 800, seconds + 1d / AmbientBackdropClock.FramesPerSecond);
        var compared = 0;

        for (var index = 0; index < particles.Length; index++)
        {
            var next = scene.Particles[index];
            if (particles[index].Opacity <= 0.01 || next.Opacity <= 0.01)
                continue;
            Assert.InRange(Distance(particles[index].Position, next.Position), 0, 4);
            Assert.InRange(Math.Abs(particles[index].Opacity - next.Opacity), 0, 0.05);
            compared++;
        }
        Assert.True(compared > 1800);
        Assert.Equal(seconds, AmbientBackdropScene.NormalizeTime(seconds));
    }

    [Fact]
    public void ResizePreservesNormalizedProjectionAndReturningToZeroRestoresFrame()
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 0);
        var particles = scene.Particles.ToArray();
        scene.Update(640, 400, 0);
        for (var index = 0; index < particles.Length; index++)
        {
            Assert.Equal(particles[index].Position.X / 2, scene.Particles[index].Position.X, 10);
            Assert.Equal(particles[index].Position.Y / 2, scene.Particles[index].Position.Y, 10);
        }
        scene.Update(480, 760, 60);
        scene.Update(1280, 800, 0);

        Assert.Equal(particles, scene.Particles.ToArray());
    }

    [Fact]
    public void SceneUpdateWithPointerDoesNotAllocatePerFrame()
    {
        var scene = new AmbientBackdropScene();
        var pointer = new AmbientPoint(0.49, 0.5);
        for (var index = 0; index < 100; index++)
            scene.Update(1280, 800, index / 30d, pointer, index % 2);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 200; index++)
            scene.Update(1280, 800, index / 30d, pointer, index % 2);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.Equal(0L, allocated);
        Assert.Equal(AmbientBackdropScene.ParticleCount, scene.Particles.Length);
    }

    private static double Distance(AmbientPoint a, AmbientPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
