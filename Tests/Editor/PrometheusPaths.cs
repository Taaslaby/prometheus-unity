// Где лежат golden-данные пакета: он может быть embedded или подключён по
// file:/git — путь спрашиваем у Unity Package Manager. Тот же приём, что
// TestPaths в com.dioma.engine.

using System.IO;

namespace Prometheus.Tests
{
    public static class PrometheusPaths
    {
        public const string PackageName = "com.dioma.prometheus";

        public static string GoldenDir
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName(PackageName);
                string root = info != null && !string.IsNullOrEmpty(info.resolvedPath)
                    ? info.resolvedPath
                    : Path.GetFullPath(Path.Combine("Packages", PackageName));
                return Path.Combine(root, "Tests", "Editor", "Data");
            }
        }
    }
}
