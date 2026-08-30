// Бенчмарк качества/скорости: диффузия (размеры сеток), рост пресетов,
// синаптогенез. Прогоны с прогревом, медиана из N итераций, Guard от
// Google-style ошибок измерения не претендует — относительные числа.

using System;
using System.Diagnostics;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using Prometheus.Runtime.Math;

class Bench
{
    static void Main(string[] args)
    {
        int iters = args.Length > 0 ? int.Parse(args[0]) : 5;
        Console.WriteLine($"итераций на замер: {iters} (после 1 прогрева)\n");

        // ── 1. Диффузия: сетки роста dioma ──────────────────────────
        foreach (var n in new[] { 32, 61, 96 })
        {
            var grid = new Grid(n, n, n, 5.0, new Vec3(-100, -100, -100));
            var field = new double[grid.Volume];
            var c2 = new double[grid.Volume];
            // Пятно в центре — реалистичная нагрузка.
            var (ci, cj, ck) = grid.Nearest(new Vec3(0, 0, 0));
            field[grid.Flat(ci, cj, ck)] = 50.0;

            double ms = Measure(iters, () =>
                DiffusionCpu.Diffuse(grid, field, c2, 1.0, 0.2, 0.02, 0, 1e15));
            double msPar = Measure(iters, () =>
                DiffusionCpu.Diffuse(grid, field, c2, 1.0, 0.2, 0.02, 0, 1e15, parallel: true));
            Console.WriteLine($"диффузия {n}³ ({grid.Volume} ячеек): {ms:F3} мс/шаг " +
                $"({grid.Volume / (ms / 1000.0) / 1e6:F0} млн ячеек/с), " +
                $"пул {msPar:F3} мс (×{ms / msPar:F1})");
        }

        // ── 2. Рост пресетов ──────────────────────────────────────
        foreach (var preset in new[] { BrainPreset.Avoidance, BrainPreset.StageCLite })
        {
            double ms = Measure(iters, () => Brain.Grow(preset, 777));
            double msPar = Measure(iters, () => GrowParallel(preset, 777));
            var brain = Brain.Grow(preset, 777);
            Console.WriteLine($"рост {preset} ({brain.NeuronCount()} нейронов, " +
                $"{brain.SynapseCount()} синапсов): {ms:F0} мс, пул {msPar:F0} мс " +
                $"(×{ms / msPar:F2})");
        }

        // ── 3. Синаптогенез tagged (финальная фаза StageC) ─────────
        {
            var layout = Brain.LayoutOf(BrainPreset.StageCLite);
            var h = Brain.Begin(BrainPreset.StageCLite, 42);
            h.RunAll();
            double ms = Measure(iters, () =>
            {
                // Полный финализатор: contact-grid + tagged-проводка + prune.
                var h2 = Brain.Begin(BrainPreset.StageCLite, 42);
                h2.RunAll();
                h2.Complete();
            });
            Console.WriteLine($"StageCLite рост+финализация: {ms:F0} мс");
        }
    }

    /// Тот же рост, но с многопоточными фазами. Результат обязан быть
    /// бит-в-бит тем же — это стережёт ParallelCpu_Grows_Bit_Identical_Connectome.
    static void GrowParallel(BrainPreset preset, ulong seed)
    {
        var h = Brain.Begin(preset, seed);
        h.World.UseParallelCpu();
        h.RunAll();
        h.Complete();
    }

    static double Measure(int iters, Action action)
    {
        action(); // прогрев (JIT, кэши)
        var samples = new double[iters];
        for (int i = 0; i < iters; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[iters / 2]; // медиана
    }
}
