namespace Y4NGZCompany.Core
{
    /// <summary>
    /// A config-shaped constant. Exposes <c>.Value</c> with the same shape as
    /// <c>BepInEx.Configuration.ConfigEntry&lt;T&gt;</c> so a setting retired from the
    /// player-facing config surface (#694) keeps every call site unchanged while the
    /// value itself becomes a fixed tuning constant.
    /// </summary>
    public sealed class FixedSetting<T>
    {
        public FixedSetting(T value) { Value = value; }
        public T Value { get; set; }
    }

    public static class FixedSetting
    {
        public static FixedSetting<T> Of<T>(T value) => new FixedSetting<T>(value);
    }

    /// <summary>
    /// A computed read-through view with the same <c>.Value</c> shape as
    /// <c>ConfigEntry&lt;T&gt;</c>, for values derived from other entries (a master
    /// switch AND a per-feature switch, a mode enum read as booleans, a tier table
    /// read for one risk tier) without changing their call sites.
    /// </summary>
    public sealed class DerivedSetting<T>
    {
        private readonly System.Func<T> _get;
        public DerivedSetting(System.Func<T> get) { _get = get; }
        public T Value => _get();
    }
}
