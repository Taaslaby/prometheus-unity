// Тесты контракта диффузионных бэкендов: переключение CPU ↔ custom,
// паритет роста при подключении через SetDiffusionBackend.

using System;
using System.Linq;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using Prometheus.Runtime.Math;
using Xunit;

namespace Prometheus.Tests
{
    public class DiffusionBackendTests
    {
        /// Прозрачный ручной бэкенд: делегирует в DiffusionCpu (проверяем
        /// сам контракт — World вызывает подключённый бэкенд).
        class CountingCpuBackend : IDiffusionBackend
        {
            public int Calls;
            public Grid LastGrid;
            public string Name => "counting-cpu";
            public void Diffuse(in Grid grid, double[] field, double[] c2,
                double dt, double diff, double deg, double lo, double hi)
            {
                Calls++;
                LastGrid = grid;
                DiffusionCpu.Diffuse(grid, field, c2, dt, diff, deg, lo, hi);
            }
        }

        [Fact]
        public void World_Uses_Connected_Backend()
        {
            var grid = new Grid(8, 8, 8, 10.0, new Vec3(-40, -40, -40));
            var bounds = new Bounds(new Vec3(-40, -40, -40), new Vec3(40, 40, 40));

            var refWorld = new World(grid, 1.0, bounds, 5);
            refWorld.AddSubstance(new Substance("G", 0.3, 0.01, grid, null));
            refWorld.AddSubstanceAt("G", new Vec3(0, 0, 0), 50.0);
            refWorld.Step();

            var backend = new CountingCpuBackend();
            var world = new World(grid, 1.0, bounds, 5);
            Assert.Equal("cpu", world.DiffusionBackendName);
            world.SetDiffusionBackend(backend);
            Assert.Equal("counting-cpu", world.DiffusionBackendName);

            world.AddSubstance(new Substance("G", 0.3, 0.01, grid, null));
            world.AddSubstanceAt("G", new Vec3(0, 0, 0), 50.0);
            world.Step();

            Assert.Equal(1, backend.Calls);       // одно вещество × один шаг
            Assert.Equal(8, backend.LastGrid.Nx); // та же сетка

            // Поле идентично CPU-миру: контракт не сломал математику.
            var a = refWorld.Substances[0].Field;
            var b = world.Substances[0].Field;
            for (int i = 0; i < a.Length; i++)
                Assert.Equal(a[i], b[i]);
        }

        [Fact]
        public void SetDiffusionBackend_Null_FallsBackToCpu()
        {
            var grid = new Grid(4, 4, 4, 10.0, Vec3.Zero);
            var world = new World(grid, 1.0,
                new Bounds(Vec3.Zero, new Vec3(40, 40, 40)), 1);
            world.SetDiffusionBackend(null);
            Assert.Equal("cpu", world.DiffusionBackendName);
        }

        [Fact]
        public void Grow_With_Custom_Backend_Matches_Cpu()
        {
            // Рост avoidance с подключённым (CPU-делегирующим) бэкендом
            // обязан дать тот же коннектом, что дефолтный путь.
            var backend = new CountingCpuBackend();

            var handle = Brain.Begin(BrainPreset.Avoidance, seed: 777);
            handle.World.SetDiffusionBackend(backend);
            handle.RunAll();
            var withBackend = handle.Complete();

            var plain = Brain.GrowDefault(777);

            Assert.Equal(plain.SynapseCount(), withBackend.SynapseCount());
            for (int i = 0; i < withBackend.Edges.Count; i++)
            {
                Assert.Equal(plain.Edges[i].Pre, withBackend.Edges[i].Pre);
                Assert.Equal(plain.Edges[i].Post, withBackend.Edges[i].Post);
                Assert.Equal(plain.Edges[i].Weight, withBackend.Edges[i].Weight);
            }
            // 400 шагов × 2 вещества (ML, MR).
            Assert.Equal(800, backend.Calls);
        }

        [Fact]
        public void ParallelCpu_Grows_Bit_Identical_Connectome()
        {
            // Многопоточность обязана быть бесплатной по смыслу: параллелятся
            // только проходы, где выход зависит лишь от неизменного входа.
            // Если это когда-нибудь перестанет быть правдой, тест поймает —
            // мозг разойдётся по весам или по топологии.
            var handle = Brain.Begin(BrainPreset.Avoidance, seed: 777);
            handle.World.UseParallelCpu();
            Assert.Equal("cpu-parallel", handle.World.DiffusionBackendName);
            handle.RunAll();
            var parallel = handle.Complete();

            var sequential = Brain.GrowDefault(777);

            Assert.Equal(sequential.SynapseCount(), parallel.SynapseCount());
            for (int i = 0; i < parallel.Edges.Count; i++)
            {
                Assert.Equal(sequential.Edges[i].Pre, parallel.Edges[i].Pre);
                Assert.Equal(sequential.Edges[i].Post, parallel.Edges[i].Post);
                Assert.Equal(sequential.Edges[i].Weight, parallel.Edges[i].Weight);
            }
            for (int i = 0; i < parallel.SomaPositions.Length; i++)
            {
                Assert.Equal(sequential.SomaPositions[i].X, parallel.SomaPositions[i].X);
                Assert.Equal(sequential.SomaPositions[i].Y, parallel.SomaPositions[i].Y);
                Assert.Equal(sequential.SomaPositions[i].Z, parallel.SomaPositions[i].Z);
            }
        }

        [Fact]
        public void ParallelDiffusion_Matches_Sequential_Bit_For_Bit()
        {
            // Прямая проверка ядра на сетке крупнее порога параллелизации.
            var grid = new Grid(40, 40, 40, 5.0, new Vec3(-100, -100, -100));
            var field = new double[grid.Volume];
            var (ci, cj, ck) = grid.Nearest(Vec3.Zero);
            field[grid.Flat(ci, cj, ck)] = 50.0;

            var seq = new double[grid.Volume];
            var par = new double[grid.Volume];
            DiffusionCpu.Diffuse(grid, field, seq, 1.0, 0.2, 0.02, 0, 1e15, parallel: false);
            DiffusionCpu.Diffuse(grid, field, par, 1.0, 0.2, 0.02, 0, 1e15, parallel: true);

            Assert.True(grid.Volume >= DiffusionCpu.ParallelMinVolume,
                "сетка должна быть крупнее порога, иначе тест проверяет тот же код");
            for (int i = 0; i < seq.Length; i++)
                Assert.Equal(seq[i], par[i]);
        }
    }
}
