// Unity-редакторные тесты пакета: то, что умеет проверить ТОЛЬКО редактор.
// Запуск: Window → General → Test Runner (EditMode).
//
// Байтовый паритет с Rust по golden-следам живёт в Dev~/Prometheus.Tests
// (xunit, `scripts/verify.sh`): там есть System.Text.Json, а в Unity его нет.
// Здесь — Burst-бэкенд против CPU, поведение compute-бэкенда без шейдера и
// главный инвариант игрового пути: нарезка роста по кадрам обязана давать
// тот же коннектом, что рост одним куском.

using System.IO;
using NUnit.Framework;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using UnityEngine;
// UnityEngine несёт свои Bounds и Grid — закрепляем типы роста псевдонимами.
using Bounds = Prometheus.Runtime.Bounds;
using Grid = Prometheus.Runtime.Math.Grid;
using Vec3 = Prometheus.Runtime.Math.Vec3;

namespace Prometheus.Tests
{
    public class GoldenUnity
    {
        [Test]
        public void GoldenData_Ships_With_Package()
        {
            // Данные золотых следов должны быть на месте: их читает
            // xunit-набор, и пропажа обязана всплыть в редакторе тоже.
            var dir = PrometheusPaths.GoldenDir;
            Assert.IsTrue(Directory.Exists(dir), $"нет папки golden-данных: {dir}");
            foreach (var name in new[] { "rng.json", "diffusion.json",
                                         "grow_avoidance.json",
                                         "dioma_connectome_fingerprint.json" })
                Assert.IsTrue(File.Exists(Path.Combine(dir, name)), $"нет {name}");
        }

        [Test]
        public void GrowAvoidance_Is_Reproducible()
        {
            // Один сид → один коннектом. Без этого наследование гена роста
            // не имеет смысла: потомок не воспроизводился бы.
            var a = Brain.Grow(BrainPreset.Avoidance, 777);
            var b = Brain.Grow(BrainPreset.Avoidance, 777);
            Assert.AreEqual(a.SynapseCount(), b.SynapseCount());
            for (int i = 0; i < a.Edges.Count; i++)
                Assert.AreEqual(a.Edges[i], b.Edges[i], $"ребро {i}");

            var json = a.ToJson("avoidance");
            StringAssert.StartsWith("{\"name\":\"avoidance\"", json);
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(20)]
        [TestCase(10000)]
        public void Sliced_Growth_Equals_Whole(int stepsPerSlice)
        {
            // ГЛАВНЫЙ инвариант игрового пути: аквариум растит мозги
            // нарезкой по кадрам, значит нарезка обязана совпадать с ростом
            // одним куском — при любом размере слайса.
            var whole = Brain.Grow(BrainPreset.Fish, 4242);

            var handle = Brain.Begin(BrainPreset.Fish, 4242);
            while (!handle.IsDone) handle.RunSlice(stepsPerSlice);
            var sliced = handle.Complete();
            handle.Dispose();

            Assert.AreEqual(whole.NeuronCount(), sliced.NeuronCount(), "нейронов");
            Assert.AreEqual(whole.SynapseCount(), sliced.SynapseCount(), "синапсов");
            for (int i = 0; i < whole.Edges.Count; i++)
                Assert.AreEqual(whole.Edges[i], sliced.Edges[i], $"ребро {i}");
        }

        [Test]
        public void Mutated_Steering_Is_Deterministic_And_Bounded()
        {
            // Ген роста наследуется и мутирует — от сида, иначе поколения не
            // воспроизвести. Границы GrowSteps держат латентность рождения.
            var parent = Steering.Default;
            var a = parent.MutatedFrom(9001);
            var b = parent.MutatedFrom(9001);
            Assert.AreEqual(a.GrowSteps, b.GrowSteps);
            Assert.AreEqual(a.TurnRateDeg, b.TurnRateDeg, 1e-12);
            Assert.AreEqual(a.SynapseDist, b.SynapseDist, 1e-12);

            for (ulong seed = 1; seed <= 64; seed++)
            {
                var m = parent.MutatedFrom(seed);
                Assert.GreaterOrEqual(m.GrowSteps, 50, $"сид {seed}");
                Assert.LessOrEqual(m.GrowSteps, 1000, $"сид {seed}");
                Assert.LessOrEqual(m.TurnRateDeg, 90.0, $"сид {seed}");
                Assert.Greater(m.GradientWeight, 0.0, $"сид {seed}");
            }
        }

        [Test]
        public void Mutated_Steering_Changes_Topology()
        {
            // Смысл мутации гена роста: разные потомки — разная проводка.
            // Если бы дрейф не доходил до топологии, отбору было бы не из
            // чего выбирать.
            var baseline = Brain.Grow(BrainPreset.Fish, 4242);
            int different = 0;
            for (ulong seed = 1; seed <= 8; seed++)
            {
                var st = Steering.Default.MutatedFrom(seed * 31);
                var grown = ConnectomeGrow.Grow(Brain.LayoutOf(BrainPreset.Fish), st, 4242);
                if (grown.SynapseCount() != baseline.SynapseCount()) different++;
            }
            Assert.Greater(different, 0,
                "мутация Steering не изменила ни одной топологии из восьми");
        }

        [Test]
        public void BurstBackend_Matches_Cpu()
        {
            // GPU-бэкенд (Burst) через контракт Core: тот же мир,
            // SetDiffusionBackend — как set_diffusion_backend в Rust.
            var grid = new Grid(8, 8, 8, 10.0, new Vec3(-40, -40, -40));
            var bounds = new Bounds(new Vec3(-40, -40, -40), new Vec3(40, 40, 40));

            // CPU-эталон.
            var wCpu = new World(grid, 1.0, bounds, 5);
            wCpu.AddSubstance(new Substance("G", 0.3, 0.01, grid, null));
            wCpu.AddSubstanceAt("G", new Vec3(0, 0, 0), 50.0);
            wCpu.Step();
            var cpu = (double[])wCpu.Substances[0].Field.Clone();

            // Burst-бэкенд, подключённый в мир роста.
            var wBurst = new World(grid, 1.0, bounds, 5);
            wBurst.AddSubstance(new Substance("G", 0.3, 0.01, grid, null));
            wBurst.AddSubstanceAt("G", new Vec3(0, 0, 0), 50.0);
            wBurst.SetDiffusionBackend(PrometheusBridge.BurstBackend());
            Assert.AreEqual("burst", wBurst.DiffusionBackendName);
            wBurst.Step();

            var burst = wBurst.Substances[0].Field;
            for (int i = 0; i < cpu.Length; i++)
                Assert.AreEqual(cpu[i], burst[i], 1e-12, $"cell {i}");
        }

        [Test]
        public void ComputeBackend_CtorFallback_ThrowsOnMissingShader()
        {
            // Без шейдера в Resources Diffuse обязан внятно ругаться,
            // а не молча портить поле.
            var backend = new Prometheus.Runtime.Gpu.ComputeDiffusionBackend(null);
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var grid = new Grid(4, 4, 4, 10.0, Vec3.Zero);
                backend.Diffuse(grid, new double[64], new double[64], 1, 0.1, 0, 0, 1e15);
            });
        }
    }
}
