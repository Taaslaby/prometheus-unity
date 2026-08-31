// CPU-диффузия (порт diffuse_cpu из gpu/mod.rs): внутренность сетки,
// явный Эйлер, лапласиан /dx², клампы.
//
// Производительность: без промежуточного буфера — читает field, пишет
// прямо в c2 (c2 никогда не читается как вход, double-buffer безопасен).
// Порядок суммирования лапласиана сохранён бит-в-бит (Rust-паритет).
//
// Каждая ячейка c2[idx] считается одним выражением из неизменного field,
// поэтому распараллеливание по срезам x бит-в-бит совпадает с однопоточным
// прогоном при любом числе потоков — результат от расписания не зависит.

using System.Threading.Tasks;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    public static class DiffusionCpu
    {
        /// Сетки мельче этого объёма считаются в один поток: накладные на
        /// запуск задач съедают выигрыш.
        public const int ParallelMinVolume = 32 * 32 * 32;

        public static void Diffuse(in Grid grid, double[] field, double[] c2,
            double dt, double diff, double deg, double lo, double hi) =>
            Diffuse(grid, field, c2, dt, diff, deg, lo, hi, parallel: false);

        public static void Diffuse(in Grid grid, double[] field, double[] c2,
            double dt, double diff, double deg, double lo, double hi, bool parallel)
        {
            int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
            double dx2 = grid.Cell * grid.Cell;
            if (nx < 3 || ny < 3 || nz < 3) return;

            int strideJ = nz;
            int strideI = ny * nz;

            if (parallel && grid.Volume >= ParallelMinVolume)
            {
                Parallel.For(1, nx - 1, i =>
                    Slice(i, ny, nz, strideI, strideJ, dx2, field, c2, dt, diff, deg, lo, hi));
                return;
            }

            for (int i = 1; i < nx - 1; i++)
                Slice(i, ny, nz, strideI, strideJ, dx2, field, c2, dt, diff, deg, lo, hi);
        }

        /// Один срез по x. Пишем прямо в c2 (аллокаций нет).
        static void Slice(int i, int ny, int nz, int strideI, int strideJ, double dx2,
            double[] field, double[] c2, double dt, double diff, double deg, double lo, double hi)
        {
            int baseI = i * strideI;
            for (int j = 1; j < ny - 1; j++)
            {
                int idx = baseI + j * strideJ + 1;
                int rowEnd = idx + (nz - 2);
                for (; idx <= rowEnd; idx++)
                {
                    double c = field[idx];
                    double lap = (field[idx - strideI]
                                + field[idx + strideI]
                                + field[idx - strideJ]
                                + field[idx + strideJ]
                                + field[idx - 1]
                                + field[idx + 1]
                                - 6.0 * c) / dx2;
                    double v = c + dt * diff * lap - dt * deg * c;
                    c2[idx] = v < lo ? lo : (v > hi ? hi : v);
                }
            }
        }
    }
}
