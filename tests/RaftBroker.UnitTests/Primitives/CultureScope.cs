using System.Globalization;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// Временно подменяет культуру потока. Нужно, чтобы поймать форматы вида "1 234":
/// значения домена печатаются в лог и в провод, и зависеть от локали они не имеют права.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous;

    private CultureScope(CultureInfo culture)
    {
        _previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
    }

    /// <summary>Культура, в которой разделитель разрядов - пробел.</summary>
    internal static CultureScope Russian() => new(new CultureInfo("ru-RU"));

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
