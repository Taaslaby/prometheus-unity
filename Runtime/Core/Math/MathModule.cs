// Vec3 + Grid: порт math.rs/substance.rs из prometheus-grow.
// f64 во всей ростовой математике — паритет с Rust бит-в-бит.

using System;

namespace Prometheus.Runtime.Math
{
    [Serializable]
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public double Len() => Math2.Sqrt(Dot(this));
        public double Dist(Vec3 o) => (this - o).Len();

        public Vec3 Normalized()
        {
            var l = Len();
            return l <= 1e-12 ? Zero : this * (1.0 / l);
        }

        public Vec3 Cross(Vec3 o) => new Vec3(
            Y * o.Z - Z * o.Y,
            Z * o.X - X * o.Z,
            X * o.Y - Y * o.X);

        public double Axis(int i) => i == 0 ? X : i == 1 ? Y : Z;

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    public static class Math2
    {
        public static double Sqrt(double x) => System.Math.Sqrt(x);
        public static double Pow(double x, double e) => System.Math.Pow(x, e);
        public static double Exp(double x) => System.Math.Exp(x);
        public static double Cos(double x) => System.Math.Cos(x);
        public static double Sin(double x) => System.Math.Sin(x);
        public static double Atan(double x) => System.Math.Atan(x);
        public static double Acos(double x) => System.Math.Acos(x);
        public static double Ln(double x) => System.Math.Log(x);
        public const double PI = System.Math.PI;
        public const double TAU = System.Math.PI * 2.0;

        /// Rodrigues: поворот v вокруг нормализованной оси.
        public static Vec3 RotateVec(Vec3 v, Vec3 axis, double angle)
        {
            var c = Cos(angle);
            var s = Sin(angle);
            var t = 1.0 - c;
            double x = axis.X, y = axis.Y, z = axis.Z;
            double vx = v.X, vy = v.Y, vz = v.Z;
            return new Vec3(
                (t * x * x + c) * vx + (t * x * y - s * z) * vy + (t * x * z + s * y) * vz,
                (t * x * y + s * z) * vx + (t * y * y + c) * vy + (t * y * z - s * x) * vz,
                (t * x * z - s * y) * vx + (t * y * z + s * x) * vy + (t * z * z + c) * vz);
        }

        /// Расстояние от точки p до отрезка ab (фасцикуляция).
        public static double DistPointSegment(Vec3 p, Vec3 a, Vec3 b)
        {
            var ab = b - a;
            var len2 = ab.Dot(ab);
            if (len2 <= 1e-12) return p.Dist(a);
            var t = Vec3.Clamp((p - a).Dot(ab) / len2, 0.0, 1.0);
            return p.Dist(a + ab * t);
        }
    }

    /// Регулярная 3D-сетка концентраций (порт Grid из substance.rs).
    public readonly struct Grid
    {
        public readonly int Nx, Ny, Nz;
        public readonly double Cell;
        public readonly Vec3 Origin;

        public Grid(int nx, int ny, int nz, double cell, Vec3 origin)
        {
            Nx = nx; Ny = ny; Nz = nz; Cell = cell; Origin = origin;
        }

        public int Volume => Nx * Ny * Nz;

        public int Flat(int i, int j, int k) => (i * Ny + j) * Nz + k;

        public int FlatClamp(int i, int j, int k)
        {
            i = Vec3.Clamp(i, 0, Nx - 1);
            j = Vec3.Clamp(j, 0, Ny - 1);
            k = Vec3.Clamp(k, 0, Nz - 1);
            return Flat(i, j, k);
        }

        public double Trilinear(double[] field, Vec3 p)
        {
            double gx = (p.X - Origin.X) / Cell;
            double gy = (p.Y - Origin.Y) / Cell;
            double gz = (p.Z - Origin.Z) / Cell;

            int i0 = (int)Vec3.Clamp(System.Math.Floor(gx), 0, System.Math.Max(Nx - 2, 0));
            int j0 = (int)Vec3.Clamp(System.Math.Floor(gy), 0, System.Math.Max(Ny - 2, 0));
            int k0 = (int)Vec3.Clamp(System.Math.Floor(gz), 0, System.Math.Max(Nz - 2, 0));

            double fx = Vec3.Clamp(gx - i0, 0, 1);
            double fy = Vec3.Clamp(gy - j0, 0, 1);
            double fz = Vec3.Clamp(gz - k0, 0, 1);

            double c000 = field[Flat(i0, j0, k0)];
            double c100 = field[Flat(i0 + 1, j0, k0)];
            double c010 = field[Flat(i0, j0 + 1, k0)];
            double c110 = field[Flat(i0 + 1, j0 + 1, k0)];
            double c001 = field[Flat(i0, j0, k0 + 1)];
            double c101 = field[Flat(i0 + 1, j0, k0 + 1)];
            double c011 = field[Flat(i0, j0 + 1, k0 + 1)];
            double c111 = field[Flat(i0 + 1, j0 + 1, k0 + 1)];

            double c00 = c000 * (1 - fx) + c100 * fx;
            double c10 = c010 * (1 - fx) + c110 * fx;
            double c01 = c001 * (1 - fx) + c101 * fx;
            double c11 = c011 * (1 - fx) + c111 * fx;
            double c0 = c00 * (1 - fy) + c10 * fy;
            double c1 = c01 * (1 - fy) + c11 * fy;
            return c0 * (1 - fz) + c1 * fz;
        }

        /// Аналитический градиент трилинейного интерполянта (8 углов).
        public Vec3 TrilinearGradient(double[] field, Vec3 p)
        {
            double gx = (p.X - Origin.X) / Cell;
            double gy = (p.Y - Origin.Y) / Cell;
            double gz = (p.Z - Origin.Z) / Cell;

            int i0 = (int)Vec3.Clamp(System.Math.Floor(gx), 0, System.Math.Max(Nx - 2, 0));
            int j0 = (int)Vec3.Clamp(System.Math.Floor(gy), 0, System.Math.Max(Ny - 2, 0));
            int k0 = (int)Vec3.Clamp(System.Math.Floor(gz), 0, System.Math.Max(Nz - 2, 0));

            double fx = Vec3.Clamp(gx - i0, 0, 1);
            double fy = Vec3.Clamp(gy - j0, 0, 1);
            double fz = Vec3.Clamp(gz - k0, 0, 1);

            double c000 = field[Flat(i0, j0, k0)];
            double c100 = field[Flat(i0 + 1, j0, k0)];
            double c010 = field[Flat(i0, j0 + 1, k0)];
            double c110 = field[Flat(i0 + 1, j0 + 1, k0)];
            double c001 = field[Flat(i0, j0, k0 + 1)];
            double c101 = field[Flat(i0 + 1, j0, k0 + 1)];
            double c011 = field[Flat(i0, j0 + 1, k0 + 1)];
            double c111 = field[Flat(i0 + 1, j0 + 1, k0 + 1)];

            double invH = 1.0 / Cell;
            double w0x = 1 - fx, w1x = fx, w0y = 1 - fy, w1y = fy, w0z = 1 - fz, w1z = fz;

            double dx = ((c100 - c000) * w0y + (c110 - c010) * w1y) * w0z
                      + ((c101 - c001) * w0y + (c111 - c011) * w1y) * w1z;
            double dy = ((c010 - c000) * w0z + (c011 - c001) * w1z) * w0x
                      + ((c110 - c100) * w0z + (c111 - c101) * w1z) * w1x;
            double dz = ((c001 - c000) * w0y + (c011 - c010) * w1y) * w0x
                      + ((c101 - c100) * w0y + (c111 - c110) * w1y) * w1x;

            return new Vec3(dx * invH, dy * invH, dz * invH);
        }

        public double Laplacian(double[] field, int i, int j, int k)
        {
            double c = field[Flat(i, j, k)];
            double s = field[Flat(i - 1, j, k)] + field[Flat(i + 1, j, k)]
                     + field[Flat(i, j - 1, k)] + field[Flat(i, j + 1, k)]
                     + field[Flat(i, j, k - 1)] + field[Flat(i, j, k + 1)];
            return s - 6.0 * c;
        }

        public (int, int, int) Nearest(Vec3 p)
        {
            int gx = (int)Vec3.Clamp(System.Math.Round((p.X - Origin.X) / Cell), 0, Nx - 1);
            int gy = (int)Vec3.Clamp(System.Math.Round((p.Y - Origin.Y) / Cell), 0, Ny - 1);
            int gz = (int)Vec3.Clamp(System.Math.Round((p.Z - Origin.Z) / Cell), 0, Nz - 1);
            return (gx, gy, gz);
        }
    }
}
