# Bundled prototype fonts

These assets are copied into build and publish output. No font service is contacted at runtime.

- Plus Jakarta Sans: interface text and headings; Google Fonts, SIL Open Font License (see PlusJakartaSans-LICENSE.txt).
- JetBrains Mono: technical labels and data; Google Fonts, SIL Open Font License (see JetBrainsMono-LICENSE.txt).
- Material Symbols Outlined: interface icons; google/material-design-icons, Apache 2.0 (see MaterialSymbols-LICENSE.txt).

Sources:
- https://github.com/google/fonts/tree/main/ofl/plusjakartasans
- https://github.com/google/fonts/tree/main/ofl/jetbrainsmono
- https://github.com/google/material-design-icons/tree/master/variablefont

The codepoints file records the icon names and Unicode values. Use explicit Unicode glyphs in WinUI instead of web ligatures.
