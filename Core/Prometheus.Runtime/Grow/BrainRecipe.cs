// BrainRecipe — рецепт мозга в одном объекте: что растить (пресет или своя
// схема), чем (сид + steering + GPU-бэкенд), куда сохранять.
//
//   // минимальный — всё по умолчанию:
//   var brain = Brain.Grow(BrainRecipe.Default());
//
//   // настроенный — читается сверху вниз, без россыпи параметров:
//   var brain = Brain.Grow(new BrainRecipe
//   {
//       Preset = BrainPreset.StageCLite,
//       Seed = 42,
//       Steering = SteeringPresets.Careful,
//       Backend = () => PrometheusBridge.BurstBackend(), // Unity-слой
//   });
//
//   // или fluent:
//   var brain = Brain.Grow(BrainRecipe.Preset(BrainPreset.Agent)
//       .WithSeed(42)
//       .WithSteering(SteeringPresets.Fast)
//       .SaveTo("Assets/Data", "agent_42"));

using System;

namespace Prometheus.Runtime.Grow
{
    public class BrainRecipe
    {
        // ── что растить ────────────────────────────────────────────

        /// Пресет схемы (если Layout не задан явно).
        public BrainPreset? Preset { get; set; }

        /// Своя схема (приоритетнее Preset).
        public Layout Layout { get; set; }

        public Layout LayoutOrThrow =>
            Layout ?? Brain.LayoutOf(Preset ?? BrainPreset.Avoidance);

        // ── как растить ───────────────────────────────────────────

        /// Сид (детерминизм; дефолт 42).
        public ulong Seed { get; set; } = 42;

        /// Геном роста (null → Steering.Default).
        public Steering? Steering { get; set; }

        /// GPU-бэкенд диффузии (null → CPU). Фабрика, а не экземпляр:
        /// каждый рост получает свой бэкенд (пул-шаринг — вручную).
        public Func<IDiffusionBackend> Backend { get; set; }

        // ── куда сохранить ────────────────────────────────────────

        /// Каталог для автосохранения JSON (null → не сохранять).
        public string SaveDirectory { get; set; }

        /// Имя файла (и "name" в JSON) при автосохранении.
        public string SaveName { get; set; } = "brain";

        // ── фабрики ──────────────────────────────────────────────

        /// Дефолтный рецепт: avoidance, сид 42, CPU.
        public static BrainRecipe Default() => new BrainRecipe();

        /// Рецепт от пресета.
        public static BrainRecipe Of(BrainPreset preset, ulong seed = 42) =>
            new BrainRecipe { Preset = preset, Seed = seed };

        /// Рецепт от своей схемы.
        public static BrainRecipe Of(Layout layout, ulong seed = 42) =>
            new BrainRecipe { Layout = layout, Seed = seed };

        // ── fluent ────────────────────────────────────────────────

        public BrainRecipe WithSeed(ulong seed) { Seed = seed; return this; }
        public BrainRecipe WithSteering(Steering st) { Steering = st; return this; }
        public BrainRecipe WithBackend(IDiffusionBackend backend) { Backend = () => backend; return this; }
        public BrainRecipe WithBackend(Func<IDiffusionBackend> factory) { Backend = factory; return this; }
        public BrainRecipe SaveTo(string directory, string name = "brain")
        { SaveDirectory = directory; SaveName = name; return this; }

        /// Мутация рецепта: новый сид ± дрейф steering — потомок.
        public BrainRecipe Mutate(ulong childSeed, double sigma = 0.08)
        {
            var st = Steering ?? Prometheus.Runtime.Grow.Steering.Default;
            return new BrainRecipe
            {
                Preset = Preset,
                Layout = Layout,
                Seed = childSeed,
                Steering = st.MutatedFrom(childSeed, sigma),
                Backend = Backend,
                SaveDirectory = SaveDirectory,
                SaveName = SaveName,
            };
        }

        public override string ToString() =>
            $"{(Layout != null ? "custom" : (Preset?.ToString() ?? BrainPreset.Avoidance.ToString()))}" +
            $" seed={Seed} steps={(Steering.HasValue ? Steering.Value.GrowSteps : Prometheus.Runtime.Grow.Steering.Default.GrowSteps)}" +
            (Backend != null ? " gpu" : " cpu");
    }
}
