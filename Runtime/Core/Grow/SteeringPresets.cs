// Fluent-обёртка Steering: подстройка генома роста без знания всех полей.
//
//   var st = SteeringPresets.Default
//       .WithGrowSteps(200)
//       .WithSynapseDist(10);

using System;

namespace Prometheus.Runtime.Grow
{
    public static class SteeringPresets
    {
        /// Строгий gnw_poc (дефолт dioma): градиенты доминируют, стоп в яблочке.
        public static Steering Default => Steering.Default;

        /// Быстрый рост для прототипов: вдвое меньше шагов.
        public static Steering Fast => Steering.Default.WithGrowSteps(200);

        /// Тщательный: больше шагов, шире радиус синапса.
        public static Steering Careful => Steering.Default
            .WithGrowSteps(800)
            .WithSynapseDist(16);

        /// Мутация генома роста от сида (машина наследования dioma).
        public static Steering MutatedFrom(this Steering st, ulong seed, double sigma = 0.08)
        {
            var rng = new XorShift(seed);
            return st.Mutate(rng, sigma);
        }
    }

    public static class SteeringExtensions
    {
        public static Steering WithGrowSteps(this Steering s, int v) =>
            new Steering { NoiseWeight = s.NoiseWeight, GradientWeight = s.GradientWeight,
                OldDirWeight = s.OldDirWeight, TurnRateDeg = s.TurnRateDeg,
                StopConcentration = s.StopConcentration, BumpMax = s.BumpMax,
                BumpSigma = s.BumpSigma, SynapseDist = s.SynapseDist, GrowSteps = v };

        public static Steering WithSynapseDist(this Steering s, double v) =>
            new Steering { NoiseWeight = s.NoiseWeight, GradientWeight = s.GradientWeight,
                OldDirWeight = s.OldDirWeight, TurnRateDeg = s.TurnRateDeg,
                StopConcentration = s.StopConcentration, BumpMax = s.BumpMax,
                BumpSigma = s.BumpSigma, SynapseDist = v, GrowSteps = s.GrowSteps };

        public static Steering WithNoise(this Steering s, double v) =>
            new Steering { NoiseWeight = v, GradientWeight = s.GradientWeight,
                OldDirWeight = s.OldDirWeight, TurnRateDeg = s.TurnRateDeg,
                StopConcentration = s.StopConcentration, BumpMax = s.BumpMax,
                BumpSigma = s.BumpSigma, SynapseDist = s.SynapseDist, GrowSteps = s.GrowSteps };

        public static Steering WithTurnRate(this Steering s, double deg) =>
            new Steering { NoiseWeight = s.NoiseWeight, GradientWeight = s.GradientWeight,
                OldDirWeight = s.OldDirWeight, TurnRateDeg = deg,
                StopConcentration = s.StopConcentration, BumpMax = s.BumpMax,
                BumpSigma = s.BumpSigma, SynapseDist = s.SynapseDist, GrowSteps = s.GrowSteps };
    }
}
