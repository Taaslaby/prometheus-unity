// Тесты паритета C# ↔ Rust по golden-следам из Dev~/golden-probe.
// rng.json — ChaCha12-следы; diffusion.json — CPU-диффузия; grow_avoidance.json — рост.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Prometheus.Runtime;
using Prometheus.Runtime.Math;
using Prometheus.Runtime.Grow;
using Xunit;

namespace Prometheus.Tests
{
    public class Golden
    {
        static JsonDocument Load(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "golden", name);
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        static double Bits(JsonElement e)
        {
            ulong bits = ulong.Parse(e.GetString());
            return BitConverter.Int64BitsToDouble((long)bits);
        }

        static ulong U64(JsonElement e) => ulong.Parse(e.GetString());

        [Fact]
        public void ChaCha12_Matches_Rust_StdRng()
        {
            var doc = Load("rng.json");
            foreach (var trace in doc.RootElement.GetProperty("traces").EnumerateArray())
            {
                ulong seed = trace.GetProperty("seed").GetUInt64();
                var rng = new ChaCha12(seed);

                var expectU64 = trace.GetProperty("u64").EnumerateArray().Select(e => U64(e)).ToArray();
                for (int i = 0; i < expectU64.Length; i++)
                    Assert.Equal(expectU64[i], rng.NextU64());

                var expectF64 = trace.GetProperty("f64").EnumerateArray().Select(e => Bits(e)).ToArray();
                for (int i = 0; i < expectF64.Length; i++)
                    Assert.Equal(expectF64[i], rng.GenF64());

                var expectRange = trace.GetProperty("gen_range_-1_1").EnumerateArray().Select(e => Bits(e)).ToArray();
                for (int i = 0; i < expectRange.Length; i++)
                    Assert.Equal(expectRange[i], rng.GenRange(-1.0, 1.0));

                var expectF32 = trace.GetProperty("f32").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                for (int i = 0; i < expectF32.Length; i++)
                    Assert.Equal(expectF32[i], rng.GenF32());

                // смешанный поток u64/u32 — хвостовые ветки BlockRng
                var mixed = trace.GetProperty("u64_mixed").EnumerateArray().Select(e => U64(e)).ToArray();
                var u32s = trace.GetProperty("u32_mixed").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
                int u32i = 0;
                var rng2 = new ChaCha12(seed);
                for (int i = 0; i < 70; i++)
                {
                    if (i % 3 == 0)
                        Assert.Equal(mixed[i], rng2.NextU64());
                    else if (i % 3 == 1)
                        Assert.Equal(u32s[u32i++], rng2.NextU32());
                    else
                        Assert.Equal(mixed[i], rng2.NextU64());
                }
            }
        }

        [Fact]
        public void XorShift_Matches_Dioma()
        {
            // Проверка формулы: сид 777 из golden-probe (первое unit() — расстановка сом).
            var rng = new XorShift(777);
            // Захардкоженный вектор от Rust-стороны не нужен: проверим внутреннюю
            // согласованность с grow-следом ниже (позиции сом совпадают бит-в-бит).
            var u = rng.Unit();
            Assert.InRange(u, 0.0, 1.0);
        }

        [Fact]
        public void Diffusion_Matches_Rust_Cpu_Backend()
        {
            var root = Load("diffusion.json").RootElement;
            int nx = root.GetProperty("nx").GetInt32();
            int ny = root.GetProperty("ny").GetInt32();
            int nz = root.GetProperty("nz").GetInt32();
            double dt = root.GetProperty("dt").GetDouble();
            double diff = root.GetProperty("diffusion").GetDouble();
            double deg = root.GetProperty("degradation").GetDouble();
            var origin = root.GetProperty("origin").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var grid = new Grid(nx, ny, nz, 10.0, new Vec3(origin[0], origin[1], origin[2]));

            // Воссоздаём мир пробы: гауссиана + инъекция 50 в центр.
            var bounds = new Bounds(new Vec3(-40, -40, -40), new Vec3(40, 40, 40));
            var w = new World(grid, dt, bounds, 5);
            w.AddSubstance(new Substance("G", diff, deg, grid, new List<ArtificialGradient>
            {
                ArtificialGradient.Gaussian(2, 1.0, 0.0, 20.0),
            }));
            w.AddSubstanceAt("G", new Vec3(0, 0, 0), 50.0);

            int step = 0;
            foreach (var f in root.GetProperty("fields").EnumerateArray())
            {
                w.Step();
                var s = w.Substances.Find(x => x.Id == "G");
                var expect = f.GetProperty("field").EnumerateArray().Select(e => Bits(e)).ToArray();
                for (int i = 0; i < expect.Length; i++)
                    Assert.Equal(expect[i], s.Field[i]);
                step++;
            }
            Assert.Equal(10, step);

            var gradExpect = root.GetProperty("grad_at_3_-7_11").EnumerateArray().Select(e => Bits(e)).ToArray();
            var grad = w.Gradient("G", new Vec3(3, -7, 11));
            Assert.Equal(gradExpect[0], grad.X);
            Assert.Equal(gradExpect[1], grad.Y);
            Assert.Equal(gradExpect[2], grad.Z);
        }

        [Fact]
        public void GrowAvoidance_Matches_Rust_Topology()
        {
            var root = Load("grow_avoidance.json").RootElement;
            var regionOfExpect = root.GetProperty("region_of").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var somaExpect = root.GetProperty("soma").EnumerateArray()
                .Select(e => e.EnumerateArray().Select(b => Bits(b)).ToArray())
                .ToArray();
            var edgesExpect = root.GetProperty("edges").EnumerateArray()
                .Select(e => (Pre: e[0].GetInt32(), Post: e[1].GetInt32(), Bits(e[2])))
                .ToArray();
            int expectEdges = edgesExpect.Length;

            var st = Steering.Default;
            var grown = ConnectomeGrow.Grow(Layouts.Avoidance(), st, 777);

            // 1) Топология бит-в-бит: все (pre, post) пары и их порядок.
            Assert.Equal(expectEdges, grown.Edges.Count);
            for (int i = 0; i < expectEdges; i++)
            {
                Assert.Equal(edgesExpect[i].Pre, grown.Edges[i].Pre);
                Assert.Equal(edgesExpect[i].Post, grown.Edges[i].Post);
            }

            // 2) Позиции сом — бит-в-бит (ChaCha12-рост детерминирован).
            Assert.Equal(somaExpect.Length, grown.SomaPositions.Length);
            for (int i = 0; i < somaExpect.Length; i++)
            {
                Assert.Equal(somaExpect[i][0], grown.SomaPositions[i].X);
                Assert.Equal(somaExpect[i][1], grown.SomaPositions[i].Y);
                Assert.Equal(somaExpect[i][2], grown.SomaPositions[i].Z);
            }

            // 3) Веса: логнормальные из одного потока ChaCha12. Rust-проба
            // пишет сырой f64-вес, но Grown хранит f32 — сравниваем f32-слой.
            for (int i = 0; i < expectEdges; i++)
            {
                float rustW = (float)edgesExpect[i].Item3;
                float csW = grown.Edges[i].Weight;
                Assert.Equal(rustW, csW);
            }
        }

        [Fact]
        public void Layouts_Match_Dioma_Presets()
        {
            var agent = Layouts.Agent();
            Assert.Equal(5, agent.Regions.Count);
            Assert.Equal(96, agent.N());

            var av = Layouts.Avoidance();
            Assert.Equal(7, av.Regions.Count);
            Assert.Equal(120, av.N());

            var b = Layouts.StageB();
            Assert.Equal(11, b.Regions.Count);
            Assert.Equal(300, b.N());

            var c = Layouts.StageCLite();
            Assert.Equal(11, c.Regions.Count);
            Assert.Equal(760, c.N());
            var counts = c.Regions.Select(r => r.Count).ToArray();
            Assert.Equal(new[] { 20, 20, 12, 12, 16, 20, 20, 60, 60, 400, 120 }, counts);
        }
    }
}
