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
    public void FramesStayFiniteLayeredAndFormAGroupedRibbon(double width, double height)
    {
        var scene = new AmbientBackdropScene();
        foreach (var seconds in new[] { 0d, 1d / AmbientBackdropClock.FramesPerSecond, 12.5, 80, 239.9, 100_000 })
        {
            scene.Update(width, height, seconds);

            Assert.Equal(AmbientBackdropScene.ParticleCount, scene.Particles.Length);
            var particles = scene.Particles.ToArray();
            Assert.Equal(AmbientBackdropScene.FarParticleCount,
                particles.Count(particle => particle.Layer == AmbientParticleLayer.Far));
            Assert.Equal(AmbientBackdropScene.MiddleParticleCount,
                particles.Count(particle => particle.Layer == AmbientParticleLayer.Middle));
            Assert.Equal(AmbientBackdropScene.NearParticleCount,
                particles.Count(particle => particle.Layer == AmbientParticleLayer.Near));
            foreach (var particle in particles)
            {
                AssertPoint(particle.Position, width, height);
                Assert.InRange(particle.Shade, 0, AmbientBackdropScene.PaletteSize - 1);
                AssertRadius(particle);
                var along = particle.Position.X / width;
                var verticalDistance = Math.Abs(
                    particle.Position.Y / height - AmbientBackdropScene.RibbonCenter(along, seconds));
                var envelope = particle.Layer switch
                {
                    AmbientParticleLayer.Far => 0.24,
                    AmbientParticleLayer.Middle => 0.16,
                    _ => 0.10
                };
                Assert.InRange(verticalDistance, 0, envelope);
            }
            Assert.True(particles.Min(particle => particle.Position.X) < width * 0.08);
            Assert.True(particles.Max(particle => particle.Position.X) > width * 0.92);
            AssertGroupedOccupancy(particles, width, height);
        }
    }

    [Fact]
    public void FirstFrameIsAlreadyAMatureRibbonWithoutWarmup()
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 0);

        var particles = scene.Particles.ToArray();
        Assert.Equal(240, particles.Length);
        AssertGroupedOccupancy(particles, 1280, 800);
        var near = particles.Where(particle => particle.Layer == AmbientParticleLayer.Near).ToArray();
        Assert.True(near.Count(particle =>
            Math.Abs(particle.Position.Y / 800 -
                AmbientBackdropScene.RibbonCenter(particle.Position.X / 1280, 0)) <= 0.08) >= 116);
    }

    [Fact]
    public void PointerInfluenceIsBoundedLocalAndDepthOrdered()
    {
        const double width = 1280;
        const double height = 800;
        const double seconds = 40;
        var pointer = new AmbientPoint(0.49, AmbientBackdropScene.RibbonCenter(0.49, seconds));
        var scene = new AmbientBackdropScene();
        scene.Update(width, height, seconds);
        var neutral = scene.Particles.ToArray();
        scene.Update(width, height, seconds, pointer, 1);
        var influenced = scene.Particles.ToArray();

        var maximumByLayer = new Dictionary<AmbientParticleLayer, double>();
        foreach (var layer in Enum.GetValues<AmbientParticleLayer>())
            maximumByLayer[layer] = 0;
        var localEnergy = 0d;
        var remoteEnergy = 0d;
        for (var index = 0; index < neutral.Length; index++)
        {
            var displacement = Distance(neutral[index].Position, influenced[index].Position);
            maximumByLayer[neutral[index].Layer] = Math.Max(maximumByLayer[neutral[index].Layer], displacement);
            Assert.InRange(displacement, 0, Math.Min(width, height) * 0.013);
            var pointerDistance = Distance(neutral[index].Position,
                new AmbientPoint(pointer.X * width, pointer.Y * height));
            if (pointerDistance <= Math.Min(width, height) * 0.22)
                localEnergy += displacement * displacement;
            else
                remoteEnergy += displacement * displacement;
        }

        Assert.True(maximumByLayer[AmbientParticleLayer.Near] > maximumByLayer[AmbientParticleLayer.Middle]);
        Assert.True(maximumByLayer[AmbientParticleLayer.Middle] > maximumByLayer[AmbientParticleLayer.Far]);
        Assert.True(localEnergy > 0);
        Assert.Equal(0, remoteEnergy, 8);
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

    [Fact]
    public void FieldEvolvesWithoutAShortSynchronizedReset()
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 0);
        var particles = scene.Particles.ToArray();

        scene.Update(1280, 800, 80);

        Assert.True(particles.Zip(scene.Particles.ToArray())
            .Count(pair => pair.First.Position != pair.Second.Position) >= AmbientBackdropScene.ParticleCount - 2);
        Assert.Equal(80, AmbientBackdropScene.NormalizeTime(80));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31.99)]
    [InlineData(79.99)]
    [InlineData(500)]
    public void FrameMotionIsSlowContinuousAndCoherent(double seconds)
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, seconds);
        var particles = scene.Particles.ToArray();

        scene.Update(1280, 800, seconds + 1d / AmbientBackdropClock.FramesPerSecond);

        var moving = 0;
        for (var index = 0; index < particles.Length; index++)
        {
            var next = scene.Particles[index];
            var displacement = Distance(particles[index].Position, next.Position);
            Assert.InRange(displacement, 0, 0.5);
            if (displacement > 0.000001)
                moving++;
            Assert.Equal(particles[index].Radius, next.Radius);
            Assert.Equal(particles[index].Layer, next.Layer);
        }
        Assert.True(moving >= AmbientBackdropScene.ParticleCount - 2);
    }

    [Fact]
    public void ResizeAndReturningToZeroRestoresTheStaticFrame()
    {
        var scene = new AmbientBackdropScene();
        scene.Update(1280, 800, 0);
        var particles = scene.Particles.ToArray();

        scene.Update(480, 760, 60);
        Assert.Equal(AmbientBackdropScene.ParticleCount, scene.Particles.Length);
        scene.Update(1280, 800, 0);

        Assert.Equal(particles, scene.Particles.ToArray());
    }

    [Fact]
    public void SceneUpdateWithPointerDoesNotAllocatePerFrame()
    {
        var scene = new AmbientBackdropScene();
        var pointer = new AmbientPoint(0.49, 0.5);
        for (var index = 0; index < 200; index++)
            scene.Update(1280, 800, index / 24d, pointer, index % 2);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            scene.Update(1280, 800, index / 24d, pointer, index % 2);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.Equal(0L, allocated);
        Assert.Equal(40, AmbientBackdropScene.FarParticleCount);
        Assert.Equal(72, AmbientBackdropScene.MiddleParticleCount);
        Assert.Equal(128, AmbientBackdropScene.NearParticleCount);
        Assert.Equal(240, AmbientBackdropScene.ParticleCount);
    }

    private static void AssertGroupedOccupancy(
        IReadOnlyCollection<AmbientParticle> particles,
        double width,
        double height)
    {
        var horizontalBins = new int[20];
        var occupancy = new bool[10, 8];
        foreach (var particle in particles)
        {
            var x = Math.Clamp((int)(particle.Position.X / width * horizontalBins.Length), 0, horizontalBins.Length - 1);
            horizontalBins[x]++;
            var gridX = Math.Clamp((int)(particle.Position.X / width * 10), 0, 9);
            var gridY = Math.Clamp((int)(particle.Position.Y / height * 8), 0, 7);
            occupancy[gridX, gridY] = true;
        }

        Assert.True(horizontalBins.Count(count => count >= 14) >= 4);
        Assert.True(horizontalBins.Count(count => count <= 6) >= 3);
        Assert.True(occupancy.Cast<bool>().Count(occupied => !occupied) >= 24);
    }

    private static void AssertRadius(AmbientParticle particle)
    {
        var limits = particle.Layer switch
        {
            AmbientParticleLayer.Far => (2.16, 14.18),
            AmbientParticleLayer.Middle => (1.00, 5.68),
            _ => (0.39, 2.98)
        };
        Assert.InRange(particle.Radius, limits.Item1, limits.Item2);
    }

    private static double Distance(AmbientPoint a, AmbientPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static void AssertPoint(AmbientPoint point, double width, double height)
    {
        Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
        // Far-field centers may sit just outside the clipped viewport. Bound them by the
        // physical ribbon width instead of clamping them into visible edge-aligned rows.
        var padding = Math.Min(width, height) * 0.20;
        Assert.InRange(point.X, -padding, width + padding);
        Assert.InRange(point.Y, -padding, height + padding);
    }
}
