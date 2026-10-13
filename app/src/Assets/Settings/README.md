# Settings navigation icons

The category color SVGs come from Microsoft's
[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons/tree/a347af95478f989038cccbf085978865264c048a/assets),
revision `a347af95478f989038cccbf085978865264c048a`. Apart from Transfers, their
contents are unchanged; the filenames match TinyTorrent's categories. The MIT
license is distributed beside them in [LICENSE](LICENSE).

| File | Upstream asset |
| --- | --- |
| home.svg | Home/SVG/ic_fluent_home_20_color.svg |
| general.svg | Settings/SVG/ic_fluent_settings_24_color.svg |
| transfers.svg | Arrow Sort/SVG/ic_fluent_arrow_sort_24_regular.svg |
| limits.svg | Gauge/SVG/ic_fluent_gauge_20_color.svg |
| schedule.svg | Calendar Clock/SVG/ic_fluent_calendar_clock_20_color.svg |
| appearance.svg | Paint Brush/SVG/ic_fluent_paint_brush_20_color.svg |
| network.svg | Globe/SVG/ic_fluent_globe_20_color.svg |
| library.svg | Library/SVG/ic_fluent_library_20_color.svg |
| advanced.svg | Wrench/SVG/ic_fluent_wrench_20_color.svg |

Transfers keeps Microsoft's paired-arrow geometry on a transparent background
and uses the teal gradient from the former Arrow Sync color asset.

SettingsPage owns the category-to-icon mapping. It uses native ImageIcon for the
SVGs and native FontIcon with inherited system colors in High Contrast.
The footer actions use neutral Lucide icons: checked and empty squares for
Advanced mode, and a left arrow for Back. The title-bar workspace switch reuses
library.svg. Other application icons retain the existing Lucide authority.
