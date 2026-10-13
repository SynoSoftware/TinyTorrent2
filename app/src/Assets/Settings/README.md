# Settings category icons

These nine 20-pixel color SVGs come from Microsoft's
[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons/tree/a347af95478f989038cccbf085978865264c048a/assets),
revision `a347af95478f989038cccbf085978865264c048a`. Their contents are unchanged;
the filenames match TinyTorrent's categories. The MIT license is distributed
beside them in [LICENSE](LICENSE).

| File | Upstream asset |
| --- | --- |
| home.svg | Home/SVG/ic_fluent_home_20_color.svg |
| general.svg | Settings/SVG/ic_fluent_settings_20_color.svg |
| transfers.svg | Arrow Sync/SVG/ic_fluent_arrow_sync_20_color.svg |
| limits.svg | Gauge/SVG/ic_fluent_gauge_20_color.svg |
| schedule.svg | Calendar Clock/SVG/ic_fluent_calendar_clock_20_color.svg |
| appearance.svg | Paint Brush/SVG/ic_fluent_paint_brush_20_color.svg |
| network.svg | Globe/SVG/ic_fluent_globe_20_color.svg |
| library.svg | Library/SVG/ic_fluent_library_20_color.svg |
| advanced.svg | Wrench/SVG/ic_fluent_wrench_20_color.svg |

SettingsPage owns the category-to-icon mapping. It uses native ImageIcon for the
SVGs and native FontIcon with inherited system colors in High Contrast.
Other application icons retain the existing Lucide authority.
