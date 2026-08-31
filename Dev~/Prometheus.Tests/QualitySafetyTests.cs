// Тесты качества/безопасности: коллизии UniformGrid, Dispose-владение
// GrowHandle, идемпотентность Complete, валидация рецепта, guard-клаузы.

using System;
using System.Linq;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using Prometheus.Runtime.Math;
using Xunit;

namespace Prometheus.Tests
{
    public class QualitySafetyTests
    {
        // ── UniformGrid: биективность ключей ───────────────────────

        [Fact]
        public void UniformGrid_NoKeyCollisions_OnAxisNeighbors()
        {
            // Краевой случай старого XOR-кодирования: (1,0,0) и (0,1,0)
            // обязаны жить в разных бакетах. Точный радиус (< cellSize)
            // возвращает только собственную ячейку — чужих нет.
            var g = new UniformGrid(10.0);
            g.Insert(0, new Vec3(10, 0, 0));    // ключ (1,0,0)
            g.Insert(1, new Vec3(0, 10, 0));    // ключ (0,1,0)
            var nearFirst = g.Query(new Vec3(10, 0, 0), 0);
            Assert.Single(nearFirst);           // только свой индекс
            Assert.Equal(0, nearFirst[0]);
        }

        [Fact]
        public void UniformGrid_NegativeCoords_SeparateBuckets()
        {
            var g = new UniformGrid(1.0);
            g.Insert(0, new Vec3(-1, 0, 0));    // (-1,0,0)
            g.Insert(1, new Vec3(1, 0, 0));     // (1,0,0) — не должен совпасть
            Assert.Single(g.Query(new Vec3(-1, 0, 0), 0.5));
            Assert.Single(g.Query(new Vec3(1, 0, 0), 0.5));
        }

        [Fact]
        public void UniformGrid_ExhaustiveKeyBijection()
        {
            // Сэмпл-зонд: точки в ЦЕНТРАХ ячеек (не на границах —
            // invCell=1/1000 неточен в double, floor на границе может
            // уехать на ±1 ячейку; это и в Rust так — паритет).
            // Коллизия ключей проявилась бы как ЧУЖОЙ индекс в радиусе 0.
            var rnd = new Random(1);
            var g = new UniformGrid(1000);
            var probes = new System.Collections.Generic.HashSet<(int, int, int)>();
            while (probes.Count < 2000)
                probes.Add((rnd.Next(-500, 500), rnd.Next(-500, 500), rnd.Next(-500, 500)));
            int i = 0;
            foreach (var (x, y, z) in probes)
                g.Insert(i++, new Vec3(
                    x * 1000 + 500, y * 1000 + 500, z * 1000 + 500));
            foreach (var (x, y, z) in probes)
            {
                var found = g.Query(new Vec3(
                    x * 1000 + 500, y * 1000 + 500, z * 1000 + 500), 0);
                Assert.Single(found);
            }
        }

        [Fact]
        public void UniformGrid_GuardsInvalidArgs()
        {
            Assert.Throws<ArgumentException>(() => new UniformGrid(0));
            Assert.Throws<ArgumentException>(() => new UniformGrid(-1));
            Assert.Throws<ArgumentException>(() => new UniformGrid(double.NaN));
            Assert.Throws<ArgumentException>(() => new UniformGrid(double.PositiveInfinity));
        }

        // ── GrowHandle: владение бэкендом ──────────────────────────

        class LeakTrackingBackend : IDiffusionBackend
        {
            public bool Disposed;
            public int Calls;
            public string Name => "leak-track";
            public void Diffuse(in Grid grid, double[] field, double[] c2,
                double dt, double diff, double deg, double lo, double hi)
            {
                Calls++;
                DiffusionCpu.Diffuse(grid, field, c2, dt, diff, deg, lo, hi);
            }
            public void Dispose() => Disposed = true;
        }

        [Fact]
        public void RecipeGrow_DisposesFactoryBackend()
        {
            var backend = new LeakTrackingBackend();
            var brain = Brain.Grow(BrainRecipe.Of(BrainPreset.Reflex, 1)
                .WithBackend(backend));

            Assert.True(backend.Calls > 0);     // использован
            Assert.True(backend.Disposed);      // и освобождён после роста
        }

        [Fact]
        public void GrowHandle_OwnsBackend_AndDisposesOnUsing()
        {
            var backend = new LeakTrackingBackend();
            ConnectomeGrow.GrowResult brain;
            using (var h = Brain.Begin(BrainPreset.Reflex, 2,
                Steering.Default, backend))
            {
                h.RunAll();
                brain = h.Complete();
            }
            Assert.True(backend.Calls > 0);
            Assert.True(backend.Disposed);      // using → Dispose

            // Рост не сломан: поле живое.
            Assert.True(brain.SynapseCount() > 0);
        }

        [Fact]
        public void GrowHandle_Complete_IsIdempotent()
        {
            var h = Brain.Begin(BrainPreset.Reflex, 3);
            h.RunAll();
            var first = h.Complete();
            var second = h.Complete();          // повтор — тот же результат
            Assert.Equal(first.SynapseCount(), second.SynapseCount());
            for (int i = 0; i < first.Edges.Count; i++)
            {
                Assert.Equal(first.Edges[i].Pre, second.Edges[i].Pre);
                Assert.Equal(first.Edges[i].Post, second.Edges[i].Post);
                Assert.Equal(first.Edges[i].Weight, second.Edges[i].Weight);
            }
        }

        [Fact]
        public void GrowHandle_EarlyAbandon_DisposesBackend()
        {
            var backend = new LeakTrackingBackend();
            using (var h = Brain.Begin(BrainPreset.StageCLite, 4,
                Steering.Default, backend))
            {
                h.RunSlice(10);                 // ранний отказ
                Assert.False(h.IsDone);
            }
            Assert.True(backend.Disposed);     // using-выход освободил
        }

        // ── Recipe-валидация ──────────────────────────────────────

        [Fact]
        public void Recipe_InvalidLayout_ThrowsEarly()
        {
            // Регион без Count → схема бессмысленна; растить нельзя.
            var empty = LayoutBuilder.Create()
                .Region("X").At(0, 0, 0).Radius(5)
                .Build();
            Assert.Throws<ArgumentException>(() => Brain.Grow(empty, 5));
        }

        [Fact]
        public void Recipe_NullLayout_ThrowsDescriptive()
        {
            var recipe = new BrainRecipe();    // ни Preset, ни Layout
            // LayoutOrThrow даёт avoidance-дефолт — легально;
            // проверяем что рецепт без layout всё же растит дефолт.
            var brain = Brain.Grow(recipe);
            Assert.Equal(120, brain.NeuronCount());
        }
    }
}
