using System.IO;
using System.Reflection;

namespace Il2CppInterop.Runtime
{
    public static class GeneratedDatabasesUtil
    {
        public static string? DatabasesLocationOverride { get; set; } = null;

        public static string GetDatabasePath(string databaseName) => Path.Combine(
            (DatabasesLocationOverride ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))!,
            databaseName);
    }
}