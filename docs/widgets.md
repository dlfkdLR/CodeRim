# macOS widgets

On macOS 14 or later, open Notification Center or right-click the desktop and choose **Edit Widgets**. Search for **CodeRim**:

- **CodeRim Usage** — session/weekly or provider-specific quota bars, remaining values and reset countdowns. Small, medium and large.
- **CodeRim History** — daily token bars with today's/latest and 30-day token totals plus available estimated API costs. Medium and large.
- **CodeRim Metric** — one compact value: automatic, today's tokens, today's estimated API cost, 30-day estimated API cost, or credits. Small.
- **CodeRim Overview** — local tokens when available, otherwise provider quota/status. Small, medium and large. Existing Overview and Limits widgets retain their saved identities and provider choices.

Right-click an added widget, choose **Edit Widget**, and select any catalog provider. Metric also offers a metric selector. Each widget remembers its own choice. Clicking opens CodeRim Usage. History is currently available only for Codex/Claude local sessions; the other providers retain their own quota/status readings without fabricated history.

The layouts follow the supplied CodexBar reference: compact provider headers, restrained native colors, horizontal usage bars, daily history and readable totals. Percentage, count-only and currency readings remain distinct. Stale readings say **Last known**; expired daily cost says **Latest**. Unknown ceilings never become 100% remaining. Light and dark appearances use system colors.

WidgetKit controls scheduling. The app saves a snapshot before asking for a reload, throttles normal reload requests, and invalidates account/state changes promptly. Timeline entries advance stale status between reloads. An app refresh is not a guarantee of an immediate desktop redraw.

## Missing or stale widgets

Launch the installed CodeRim app once, confirm the provider is connected, and keep it running for updates. If the app has closed or a provider refresh failed, last-known readings are expected. WidgetKit can delay a desktop redraw after an app refresh.

If the widget still does not appear or update, see the [installation diagnostics](../Documentation/CLI_WIDGETS.md#if-widgets-are-absent-or-stale). Preview images alone do not confirm that an installed widget can access its snapshot.

[CLI](cli.md) · [Providers](providers.md) · [Docs](README.md)
