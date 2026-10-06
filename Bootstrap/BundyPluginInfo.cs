namespace Y4NGZCompany.Bootstrap
{
    public static class BundyPluginInfo
    {
        // #612 task 1.4: aliased to the Core table so every GUID this project publishes is
        // declared in one place. The literal value is unchanged — it is a shipped BepInPlugin
        // GUID, and Bundy stays with Contracted through the split.
        public const string PLUGIN_GUID = Y4NGZCore.Lifecycle.ModuleHarmonyIds.BundyPlugin;
        public const string PLUGIN_NAME = "Y4NGZ Bundy";
        public const string PLUGIN_VERSION = "0.1.0";
    }
}
