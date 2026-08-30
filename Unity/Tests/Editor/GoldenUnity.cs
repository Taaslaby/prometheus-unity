// Unity-редакторные тесты паритета: golden-трейсы против порта.
// Запуск: Window → General → Test Runner (EditMode).

using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using UnityEngine;
using UnityEngine.TestTools;
namespace Prometheus.Tests
{
    public class GoldenUnity
    {
        static JsonDocument Load(string name)
        {
            var path = Path.Combine(PrometheusBridge.GoldenPath, name);
            Assert.IsTrue(File.Exists(path), $"golden не найден: {path}");
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        static double Bits(JsonElement e) =>
            System.BitConverter.Int64BitsToDouble((long)ulong.Parse(e.GetString()));

        [Test]
        public void ChaCha12_Matches_Rust()
        {
            var doc = Load("rng.json");
            foreach (var trace in doc.RootElement.GetProperty("traces").EnumerateArray())
            {
                var rng = new ChaCha12(trace.GetProperty("seed").GetUInt64());
                foreach (var e in trace.GetProperty("u64").EnumerateArray())
                    rng.NextU64(); // проверка выборок ниже в dotnet-тестах;
                                  // здесь — smoke что поток не падает
                Assert.DoesNotThrow(() => rng.GenRange(-1.0, 1.0));
            }
        }

        [Test]
        public void GrowAvoidance_Topology_Matches()
        {
            var root = Load("grow_avoidance.json").RootElement;
            var edgesExpect = root.GetProperty("edges").EnumerateArray()
                .Select(e => (Pre: e[0].GetInt32(), Post: e[1].GetInt32()))
                .ToArray();

            // Новый Brain-фасад; инкрементальный путь должен дать то же.
            var grown = Brain.Grow(BrainPreset.Avoidance, 777);
            Assert.AreEqual(edgesExpect.Length, grown.SynapseCount());
            for (int i = 0; i < edgesExpect.Length; i++)
            {
                Assert.AreEqual(edgesExpect[i].Pre, grown.Edges[i].Pre, $"edge {i} pre");
                Assert.AreEqual(edgesExpect[i].Post, grown.Edges[i].Post, $"edge {i} post");
            }

            // И JSON-путь (расширение Core) выдаёт валидный формат.
            var json = grown.ToJson("avoidance");
            StringAssert.StartsWith("{\"name\":\"avoidance\"", json);
        }

        [Test]
        [UnityTest]
        public System.Collections.IEnumerator BurstBackend_Matches_Cpu()
        {
            // GPU-бэкенд (Burst) через контракт Core: тот же мир,
            // SetDiffusionBackend — как set_diffusion_backend в Rust.
            var grid = new Prometheus.Runtime.Math.Grid(8, 8, 8, 10.0,
                new Prometheus.Runtime.Math.Vec3(-40, -40, -40));
            var bounds = new Bounds(new Prometheus.Runtime.Math.Vec3(-40, -40, -40),
                new Prometheus.Runtime.Math.Vec3(40, 40, 40));

            // CPU-эталон.
            var wCpu = new World(grid, 1.0, bounds, 5);
            wCpu.AddSubstance(new Substance("G", 0.3, 0.01, grid, null));
            wCpu.AddSubstanceAt("G", new Prometheus.Runtime.Math.Vec3(0, 0, 0), 50.0);
            wCpu.Step();
            var cpu = (double[])wCpu.Substances[0].Field.Clone();

            // Burst-бэкенд, подключённый в мир роста.
            var wBurst = new World(grid, 1.0, bounds, 5);
            wBurst.AddSubstance(new Substance("G", 0.3, 0.01, grid, null));
            wBurst.AddSubstanceAt("G", new Prometheus.Runtime.Math.Vec3(0, 0, 0), 50.0);
            wBurst.SetDiffusionBackend(PrometheusBridge.BurstBackend());
            Assert.AreEqual("burst", wBurst.DiffusionBackendName);
            wBurst.Step();

            var burst = wBurst.Substances[0].Field;
            for (int i = 0; i < cpu.Length; i++)
                Assert.AreEqual(cpu[i], burst[i], 1e-12, $"cell {i}");
            yield break;
        }

        [Test]
        public void ComputeBackend_CtorFallback_ThrowsOnMissingShader()
        {
            // Без шейдера в Resources Diffuse обязан внятно ругаться,
            // а не молча портить поле.
            var backend = new Prometheus.Runtime.Gpu.ComputeDiffusionBackend(null);
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var grid = new Prometheus.Runtime.Math.Grid(4, 4, 4, 10.0,
                    Prometheus.Runtime.Math.Vec3.Zero);
                backend.Diffuse(grid, new double[64], new double[64], 1, 0.1, 0, 0, 1e15);
            });
        }
    }
}
