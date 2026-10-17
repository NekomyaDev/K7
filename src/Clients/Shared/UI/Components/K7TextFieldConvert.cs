using System.Globalization;

namespace K7.Clients.Shared.UI.Components;

internal static class K7TextFieldConvert
{
    /// <summary>
    /// Parses input text into <typeparamref name="TValue"/>.
    /// <see cref="System.Convert.ChangeType(object, Type)"/> rejects <c>Nullable&lt;T&gt;</c>,
    /// so number fields bound as <c>int?</c>/<c>long?</c> must unwrap first.
    /// </summary>
    internal static TValue? To<TValue>(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        var target = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        try
        {
            return (TValue?)System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
        }
        catch
        {
            return default;
        }
    }
}
