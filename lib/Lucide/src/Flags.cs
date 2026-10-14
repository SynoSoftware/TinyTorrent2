using Microsoft.UI.Xaml.Media;

namespace Syno;

/// <summary>
/// The Twemoji country-flag font vendored at Fonts/twemoji.ttf, and the text that shows a flag in it.
/// </summary>
/// <remarks>
/// A flags-only subset of Mozilla's Twemoji colour font, version 0.6.0, cut by the
/// country-flag-emoji-polyfill project at 0.1.10; LICENSE.Twemoji records the source.
/// </remarks>
public static class Flags
{
    /// <summary>The vendored font, for a <c>TextBlock</c> that shows one <see cref="Glyph"/>.</summary>
    public static FontFamily Font { get; } = new("ms-appx:///Lucide/Fonts/twemoji.ttf#Twemoji Mozilla");

    /// <summary>
    /// The regional indicator pair that <see cref="Font"/> joins into the flag of the ISO 3166-1
    /// alpha-2 <paramref name="code"/>, or empty when the code is not two ASCII letters.
    /// </summary>
    public static string Glyph(string code) =>
        code.Length == 2 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1])
            ? char.ConvertFromUtf32(Indicator(code[0])) + char.ConvertFromUtf32(Indicator(code[1]))
            : string.Empty;

    // The regional indicator symbols are the letters A to Z from U+1F1E6.
    private static int Indicator(char letter) => 0x1F1E6 + char.ToUpperInvariant(letter) - 'A';
}
