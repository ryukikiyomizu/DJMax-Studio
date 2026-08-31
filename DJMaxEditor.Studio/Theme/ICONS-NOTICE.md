# Icon attribution

The `StreamGeometry` resources in `Icons.xaml` are derived from the **lucide** icon set.

- Project: <https://lucide.dev>
- Source repository: <https://github.com/lucide-icons/lucide>
- Package: **lucide-static 1.34.0** (`https://unpkg.com/lucide-static@1.34.0/icons/<name>.svg`)
- Licence: ISC

The 24x24 stroke-based SVG shapes (`path`, `line`, `circle`, `rect`) were converted to WPF
path geometry as text inside `Icons.xaml`; no lucide binary asset, SVG file, font, or other
packaged artefact is redistributed with this project.

## ISC License

```text
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Feather (MIT) subset

The lucide licence notes that some icons are derived from the Feather project and are
additionally covered by the MIT licence. Of the icons used here, the following 20 fall in
that list:

`check`, `chevron-down`, `chevron-left`, `chevron-right`, `chevron-up`, `circle`, `clock`,
`headphones`, `info`, `lock`, `minimize-2`, `minus`, `music`, `plus`, `search`, `square`,
`trash-2`, `x`, `zoom-in`, `zoom-out`

Those correspond to these resource keys in `Icons.xaml`, all of which carry the MIT notice
below in addition to the ISC notice above:

`Icon.Check`, `Icon.ChevronDown`, `Icon.ChevronLeft`, `Icon.ChevronRight`, `Icon.ChevronUp`,
`Icon.Circle`, `Icon.Clock`, `Icon.Headphones`, `Icon.Info`, `Icon.Lock`, `Icon.Minimize2`,
`Icon.Minus`, `Icon.Music`, `Icon.Plus`, `Icon.Search`, `Icon.Square`, `Icon.Trash2`,
`Icon.X`, `Icon.ZoomIn`, `Icon.ZoomOut`

Every other `Icon.*` key in `Icons.xaml` is lucide-original and is covered by the ISC notice
alone.

```text
The MIT License (MIT)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Rendering notes

lucide glyphs are stroke geometry, not filled shapes. Render each resource with:

- `Stroke` set to the desired brush, `Fill` left `null`
- `StrokeThickness` 2
- `StrokeStartLineCap` / `StrokeEndLineCap` / `StrokeDashCap` = `Round`
- `StrokeLineJoin` = `Round`
- inside a 24x24 coordinate space (e.g. a `Viewbox`, or a `Path` with `Stretch="Uniform"`)
