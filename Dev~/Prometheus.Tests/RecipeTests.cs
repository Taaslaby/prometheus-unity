// Тесты BrainRecipe и теговых запросов: рецепт — единая точка конфигурации,
// запросы по тегам без индексов, Describe — читаемая сводка.

using System;
using System.Linq;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using Xunit;

namespace Prometheus.Tests
{
    public class RecipeTests
    {
        [Fact]
        public void Recipe_Default_GrowsAvoidance()
        {
            var brain = Brain.Grow(BrainRecipe.Default());

            Assert.Equal(120, brain.NeuronCount());
            Assert.Equal(42ul, brain.Seed);            // сид сохранён в результате
            Assert.True(brain.SynapseCount() > 0);
        }

        [Fact]
        public void Recipe_Fluent_Matches_Pointed_Overloads()
        {
            // Один и тот же мозг через рецепт и через перегрузку.
            var viaRecipe = Brain.Grow(BrainRecipe.Of(BrainPreset.Avoidance, 777));
            var viaOverload = Brain.Grow(BrainPreset.Avoidance, 777);

            Assert.Equal(viaOverload.SynapseCount(), viaRecipe.SynapseCount());
            for (int i = 0; i < viaRecipe.Edges.Count; i++)
            {
                Assert.Equal(viaOverload.Edges[i].Pre, viaRecipe.Edges[i].Pre);
                Assert.Equal(viaOverload.Edges[i].Post, viaRecipe.Edges[i].Post);
                Assert.Equal(viaOverload.Edges[i].Weight, viaRecipe.Edges[i].Weight);
            }
        }

        [Fact]
        public void Recipe_CustomLayout_AndSteering()
        {
            var layout = LayoutBuilder.Create()
                .Region("S").At(-60, 0, 0).Radius(18).Count(40).Axon("M", 170)
                .Region("M").At(60, 0, 0).Radius(18).Count(40).Secrete("M")
                .Allow("S", "M")
                .Build();

            var brain = Brain.Grow(new BrainRecipe
            {
                Layout = layout,
                Seed = 7,
                Steering = SteeringPresets.Fast,
            });

            Assert.Equal(80, brain.NeuronCount());
            Assert.Equal(200, brain.SteeringUsed.GrowSteps); // steering записан
            Assert.True(brain.SynapseCount() > 0);
            // Все дуги — внутри разрешённых пар (S→M единственная).
            Assert.All(brain.Edges, e =>
            {
                string pre = brain.RegionTags[System.Array.IndexOf(
                    brain.RegionOf, e.Pre) == -1 ? 0 : brain.RegionOf[e.Pre]];
                string post = brain.RegionTags[brain.RegionOf[e.Post]];
                Assert.True(pre == "S" && post == "M");
            });
        }

        [Fact]
        public void Recipe_Mutate_ProducesChild()
        {
            var parent = BrainRecipe.Of(BrainPreset.Agent, seed: 42);
            var child = parent.Mutate(childSeed: 99);

            // Потомок: другая схема та же, сид новый, steering дрейфован.
            Assert.Equal(parent.Preset, child.Preset);
            Assert.Equal(99ul, child.Seed);
            Assert.NotEqual(parent.Steering, child.Steering);

            // Мутация детерминирована от сида.
            var child2 = parent.Mutate(99);
            Assert.Equal(child.Steering.Value.GrowSteps, child2.Steering.Value.GrowSteps);
            Assert.Equal(child.Steering.Value.NoiseWeight, child2.Steering.Value.NoiseWeight);

            // Родитель не изменён.
            Assert.Null(parent.Steering);
        }

        [Fact]
        public void Recipe_Backend_Injected_Into_World()
        {
            var backend = new CountingBackend();
            var brain = Brain.Grow(BrainRecipe.Of(BrainPreset.Reflex, 1)
                .WithBackend(backend));

            Assert.True(backend.Calls > 0); // бэкенд реально использован
            Assert.Equal(400, backend.Calls); // 400 шагов × 1 вещество (M)
        }

        class CountingBackend : IDiffusionBackend
        {
            public int Calls;
            public string Name => "counting";
            public void Diffuse(in Prometheus.Runtime.Math.Grid grid, double[] field,
                double[] c2, double dt, double diff, double deg, double lo, double hi)
            {
                Calls++;
                DiffusionCpu.Diffuse(grid, field, c2, dt, diff, deg, lo, hi);
            }
        }

        [Fact]
        public void TagQueries_NoIndexKnowledge()
        {
            var brain = Brain.GrowDefault(777);

            // Теги пришли из схемы.
            Assert.NotNull(brain.RegionTags);
            Assert.Equal(new[] { "SL", "SR", "WL", "WR", "I", "ML", "MR" }, brain.RegionTags);

            // RegionIndex / RegionBounds по тегу.
            Assert.Equal(0, brain.RegionIndex("SL"));
            Assert.Equal(-1, brain.RegionIndex("NOPE"));
            Assert.Equal(0, brain.RegionBounds("SL").Start);
            Assert.Equal(20, brain.RegionBounds("SL").End - brain.RegionBounds("SL").Start);

            // NeuronsOf: 20 сенсоров SL.
            Assert.Equal(20, brain.NeuronsOf("SL").Length);

            // EdgesFrom: только SL-нейроны как pre.
            foreach (var (pre, _, _) in brain.EdgesFrom("SL"))
            {
                int ri = brain.RegionOf[pre];
                Assert.Equal("SL", brain.RegionTags[ri]);
            }

            // EdgesBetween: направленный запрос SL → ML.
            var slToMl = brain.EdgesBetween("SL", "ML");
            Assert.NotEmpty(slToMl);
            foreach (var (pre, post, _) in slToMl)
            {
                Assert.Equal("SL", brain.RegionTags[brain.RegionOf[pre]]);
                Assert.Equal("ML", brain.RegionTags[brain.RegionOf[post]]);
            }

            // EdgeCountBetween согласован со списком.
            Assert.Equal(slToMl.Count, brain.EdgeCountBetween("SL", "ML"));

            // Обратное направление — другое число (или ноль).
            Assert.Equal(0, brain.EdgeCountBetween("ML", "SL"));
        }

        [Fact]
        public void Describe_IsHumanReadable()
        {
            var brain = Brain.GrowDefault(777);
            var text = brain.Describe();

            Assert.Contains("n=120", text);
            Assert.Contains("synapses=", text);
            Assert.Contains("SL→ML", text); // теги в топе пар
            Assert.DoesNotContain("CORTEX", text); // схема avoidance
        }

        [Fact]
        public void Begin_Recipe_Incremental()
        {
            var h = Brain.Begin(BrainRecipe.Of(BrainPreset.Reflex, 3));
            while (!h.RunSlice(50)) { }
            var brain = h.Complete();

            Assert.Equal(100, brain.NeuronCount()); // Reflex: S40+I20+M40
            Assert.Equal(3ul, brain.Seed);
            Assert.True(brain.SynapseCount() > 0);
        }
    }
}
