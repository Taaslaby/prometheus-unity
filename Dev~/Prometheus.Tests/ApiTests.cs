// Тесты нового удобного API: LayoutBuilder, Brain-фасад, GrowHandle
// (инкрементальный рост), fluent-Steering, расширения GrowResult.

using System.Linq;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using Xunit;

namespace Prometheus.Tests
{
    public class ApiTests
    {
        [Fact]
        public void LayoutBuilder_Creates_Working_Scheme()
        {
            // Полная схема рефлекса через билдер.
            var layout = LayoutBuilder.Create()
                .Region("S").At(-60, 0, 0).Radius(18).Count(40)
                    .Axon("M", 170).Axon("M", 170)
                .Region("I").At(0, 0, 0).Radius(18).Count(20).Inhibitory()
                    .Axon("M", 80)
                .Region("M").At(60, 0, 0).Radius(18).Count(40).Secrete("M")
                .Allow("S", "S").Allow("S", "I").Allow("S", "M").Allow("I", "M")
                .Build();

            Assert.Equal(3, layout.Regions.Count);
            Assert.Equal(100, layout.N());
            Assert.True(layout.Regions[1].Inhibitory);
            Assert.Equal("M", layout.Regions[2].Secrete);
            Assert.Equal(2, layout.Regions[0].Axons.Count);
            Assert.Contains(("S", "I"), layout.AllowedPairs);

            // Билдер реально растит коннектом (S до M дорастают).
            var grown = Brain.Grow(layout, seed: 42);
            Assert.NotEmpty(grown.Edges);
            Assert.All(grown.Edges, e =>
            {
                string pre = layout.Regions[grown.RegionOf[e.Pre]].Tag;
                string post = layout.Regions[grown.RegionOf[e.Post]].Tag;
                Assert.Contains((pre, post), layout.AllowedPairs);
            });
        }

        [Fact]
        public void LayoutBuilder_Matches_Preset_Layout()
        {
            // Билдер должен уметь воспроизвести avoidance-пресет.
            var built = LayoutBuilder.Create()
                .Region("SL").At(-60, -60, 0).Radius(15).Count(20)
                    .Axon("ML", 170).Axon("ML", 170)
                .Region("SR").At(60, -60, 0).Radius(15).Count(20)
                    .Axon("MR", 170).Axon("MR", 170)
                .Region("WL").At(-30, -75, 0).Radius(12).Count(12).Axon("MR", 240)
                .Region("WR").At(30, -75, 0).Radius(12).Count(12).Axon("ML", 240)
                .Region("I").At(0, -30, 0).Radius(12).Count(16).Inhibitory()
                    .Axon("ML", 140).Axon("MR", 140)
                .Region("ML").At(-60, 60, 0).Radius(15).Count(20).Secrete("ML")
                .Region("MR").At(60, 60, 0).Radius(15).Count(20).Secrete("MR")
                .Allow("SL", "ML").Allow("SL", "I")
                .Allow("SR", "MR").Allow("SR", "I")
                .Allow("WL", "MR").Allow("WR", "ML")
                .Allow("I", "ML").Allow("I", "MR")
                .Build();

            var preset = Layouts.Avoidance();
            Assert.Equal(preset.N(), built.N());
            for (int i = 0; i < preset.Regions.Count; i++)
            {
                Assert.Equal(preset.Regions[i].Tag, built.Regions[i].Tag);
                Assert.Equal(preset.Regions[i].Count, built.Regions[i].Count);
                Assert.Equal(preset.Regions[i].Inhibitory, built.Regions[i].Inhibitory);
                Assert.Equal(preset.Regions[i].Secrete, built.Regions[i].Secrete);
            }

            // И расти одинаково: тот же сид → те же рёбра.
            var a = Brain.Grow(built, 777);
            var b = Brain.Grow(preset, 777);
            Assert.Equal(b.SynapseCount(), a.SynapseCount());
            for (int i = 0; i < a.Edges.Count; i++)
            {
                Assert.Equal(b.Edges[i].Pre, a.Edges[i].Pre);
                Assert.Equal(b.Edges[i].Post, a.Edges[i].Post);
            }
        }

        [Fact]
        public void Brain_Facade_OneLine_Grow()
        {
            var brain = Brain.GrowDefault(777);
            Assert.Equal(120, brain.NeuronCount());
            Assert.True(brain.SynapseCount() > 0);

            var stageC = Brain.Grow(BrainPreset.StageCLite, 42);
            Assert.Equal(760, stageC.NeuronCount());
        }

        [Fact]
        public void GrowHandle_Incremental_Matches_SingleShot()
        {
            // Инкрементальный рост по 17 шагов = один вызов Grow.
            var handle = Brain.Begin(BrainPreset.Avoidance, seed: 777);
            Assert.Equal(400, handle.TotalSteps);
            Assert.False(handle.IsDone);

            while (!handle.RunSlice(17)) { }
            Assert.True(handle.IsDone);
            Assert.Equal(1.0f, handle.Progress, 3);

            var grown = handle.Complete();
            var oneshot = Brain.GrowDefault(777);

            Assert.Equal(oneshot.Edges.Count, grown.Edges.Count);
            for (int i = 0; i < grown.Edges.Count; i++)
            {
                Assert.Equal(oneshot.Edges[i].Pre, grown.Edges[i].Pre);
                Assert.Equal(oneshot.Edges[i].Post, grown.Edges[i].Post);
                Assert.Equal(oneshot.Edges[i].Weight, grown.Edges[i].Weight);
            }
        }

        [Fact]
        public void GrowHandle_Slice_Cannot_Overrun()
        {
            var handle = Brain.Begin(BrainPreset.Reflex, seed: 1);
            handle.RunAll();
            Assert.True(handle.IsDone);
            // Лишние шаги игнорируются.
            Assert.True(handle.RunSlice(100));
            Assert.Equal(handle.TotalSteps, handle.StepsDone);
        }

        [Fact]
        public void SteeringPresets_Fluent()
        {
            var st = SteeringPresets.Fast.WithSynapseDist(10);
            Assert.Equal(200, st.GrowSteps);
            Assert.Equal(10, st.SynapseDist);

            // Мутация от сида детерминирована.
            var m1 = Steering.Default.MutatedFrom(99);
            var m2 = Steering.Default.MutatedFrom(99);
            Assert.Equal(m1.GrowSteps, m2.GrowSteps);
            Assert.Equal(m1.NoiseWeight, m2.NoiseWeight);
        }

        [Fact]
        public void GrowResult_Statistics()
        {
            var g = Brain.GrowDefault(777);

            Assert.Equal(120, g.NeuronCount());
            var (exc, inh) = g.TypeSplit();
            Assert.Equal(g.NeuronCount(), exc + inh);
            Assert.True(inh > 0); // регион I — тормозной

            var bounds = g.RegionBounds(2); // WL
            Assert.True(bounds.End > bounds.Start);

            var pairs = g.RegionPairCounts();
            Assert.NotEmpty(pairs);
            // Все дуги — между разрешёнными регионами.
            var layout = Brain.LayoutOf(BrainPreset.Avoidance);
            foreach (var ((from, to), _) in pairs)
                Assert.Contains((layout.Regions[from].Tag, layout.Regions[to].Tag),
                    layout.AllowedPairs);

            Assert.True(g.MeanWeight() > 0);
            Assert.True(g.InterRegionFraction() > 0);

            var json = g.ToJson("test");
            Assert.StartsWith("{\"name\":\"test\"", json);
            Assert.Contains("\"n\":120", json);
        }

        [Fact]
        public void WorldExtensions_AddTaggedAndQuery()
        {
            var grid = new Prometheus.Runtime.Math.Grid(8, 8, 8, 10.0,
                new Prometheus.Runtime.Math.Vec3(-40, -40, -40));
            var w = new World(grid, 1.0,
                new Bounds(new Prometheus.Runtime.Math.Vec3(-40, -40, -40),
                    new Prometheus.Runtime.Math.Vec3(40, 40, 40)), 3);

            int a = w.AddTagged("A", Prometheus.Runtime.Math.Vec3.Zero, CellType.Excitatory);
            int b = w.AddTagged("B", new Prometheus.Runtime.Math.Vec3(5, 0, 0), CellType.Inhibitory);
            w.GrowDendrite(a);

            Assert.Equal(2, w.AliveCells().Count());
            Assert.Single(w.CellsWithTag("B"));
            Assert.Equal(1, w.NeuriteCount());

            // Морфоген-средняя по сомам = 0 без вещества.
            Assert.Equal(0, w.MeanConcentration("X"));
        }
    }
}
