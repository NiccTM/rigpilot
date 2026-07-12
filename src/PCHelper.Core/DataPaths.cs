namespace PCHelper.Core;

public static class DataPaths
{
    public static string GetDefaultDataDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PCHelper");
    }

    public static string GetDefaultDatabasePath() => Path.Combine(GetDefaultDataDirectory(), "state.db");
}
