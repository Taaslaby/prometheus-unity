// GPU-бэкенд диффузии №1: Burst-джоба как IDiffusionBackend.
// Подключение: world.SetDiffusionBackend(new BurstDiffusionBackend()) —
// тот же контракт, что world.set_diffusion_backend(DiffusionBackend::Gpu)
// в Rust. Один бэкенд можно шарить на много миров (джоба без состояния
// общего — буферы создаются на вызов).

using Prometheus.Runtime.Math;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Prometheus.Runtime.Jobs
{
    /// Burst-реализация шага диффузии (бит-паритет с DiffusionCpu:
    /// FloatMode.Strict, та же скобочная структура сумм). FloatPrecision.High
    /// — самый строгий уровень в Burst (Strict там нет, есть только у
    /// FloatMode); на double он всё равно не влияет, паритет держит FloatMode.
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
    public struct DiffusionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<double> FieldIn;
        // Джоба пишет по декодированному idx (внутренность сетки), а не по
        // линейному job-index — без снятия параллельного ограничения safety
        // роняет её с IndexOutOfRange (замерено пробой на сетке 8³).
        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<double> FieldOut;

        public int Nx, Ny, Nz;
        public double CellSize;
        public double Dt, Diffusion, Degradation, Lo, Hi;

        public void Execute(int lin)
        {
            int ny2 = math.max(Ny - 2, 0);
            int nz2 = math.max(Nz - 2, 0);
            if (ny2 == 0 || nz2 == 0 || Nx < 3) return;

            int i = lin / (ny2 * nz2) + 1;
            int r = lin % (ny2 * nz2);
            int j = r / nz2 + 1;
            int k = r % nz2 + 1;

            int idx = (i * Ny + j) * Nz + k;
            double dx2 = CellSize * CellSize;
            double lap = (FieldIn[((i - 1) * Ny + j) * Nz + k]
                         + FieldIn[((i + 1) * Ny + j) * Nz + k]
                         + FieldIn[(i * Ny + (j - 1)) * Nz + k]
                         + FieldIn[(i * Ny + (j + 1)) * Nz + k]
                         + FieldIn[(i * Ny + j) * Nz + (k - 1)]
                         + FieldIn[(i * Ny + j) * Nz + (k + 1)]
                         - 6.0 * FieldIn[idx]) / dx2;
            double v = FieldIn[idx] + Dt * Diffusion * lap - Dt * Degradation * FieldIn[idx];
            FieldOut[idx] = math.clamp(v, Lo, Hi);
        }
    }

    /// IDiffusionBackend поверх Burst-джобы.
    public class BurstDiffusionBackend : IDiffusionBackend
    {
        public string Name => "burst";

        public void Diffuse(in Grid grid, double[] field, double[] c2,
            double dt, double diff, double deg, double lo, double hi)
        {
            int innerN = System.Math.Max(grid.Nx - 2, 0)
                       * System.Math.Max(grid.Ny - 2, 0)
                       * System.Math.Max(grid.Nz - 2, 0);
            if (innerN == 0) return;

            using var fieldIn = new NativeArray<double>(field, Allocator.TempJob);
            using var fieldOut = new NativeArray<double>(field.Length, Allocator.TempJob);
            // Джоба пишет только внутренность; границы скопируем из входа
            // (World всё равно перезапишет их по boundary-условию).
            fieldOut.CopyFrom(field);

            var job = new DiffusionJob
            {
                FieldIn = fieldIn,
                FieldOut = fieldOut,
                Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz,
                CellSize = grid.Cell,
                Dt = dt, Diffusion = diff, Degradation = deg, Lo = lo, Hi = hi,
            };
            job.Schedule(innerN, 512).Complete();
            fieldOut.CopyTo(c2);
        }
    }
}
