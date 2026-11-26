"""Synthetic local Codex records and independent positive checks for the Settings chart."""

from contextlib import closing
from datetime import datetime, timedelta, timezone
import json
import sqlite3


def seed(directory, anchor):
    expected = []
    for index, model in enumerate(("gpt-5.1", "gpt-5.2"), start=1):
        session = f"settings-fixture-{index}"
        records = [{"type": "session_meta", "payload": {"id": session, "model": model}}]
        cumulative = {"input_tokens": 0, "cached_input_tokens": 0, "output_tokens": 0}
        for day, weight in [(4, 1), (3, 4), (2, 2), (1, 3)]:
            date = datetime.combine(
                anchor - timedelta(days=day), datetime.min.time(), tzinfo=timezone.utc
            ).replace(hour=11 + index)
            weight *= index
            delta = {
                "input_tokens": 29 * weight,
                "cached_input_tokens": 7 * weight,
                "output_tokens": 11 * weight,
            }
            cumulative = {key: value + delta[key] for key, value in cumulative.items()}
            records.append(
                {
                    "type": "event_msg",
                    "timestamp": date.isoformat().replace("+00:00", "Z"),
                    "payload": {
                        "type": "token_count",
                        "info": {
                            "last_token_usage": delta,
                            "total_token_usage": cumulative,
                        },
                    },
                }
            )
            expected.append([date.timestamp(), model, *delta.values()])
        (directory / f"{session}.jsonl").write_text(
            "".join(json.dumps(row, separators=(",", ":")) + "\n" for row in records)
        )
    return sorted(expected)


def stored_rows(database):
    with closing(
        sqlite3.connect(database.as_uri() + "?mode=ro", uri=True, timeout=1)
    ) as db:
        return [
            list(row)
            for row in db.execute(
                "SELECT occurred_at, model, input_tokens, cached_input_tokens, output_tokens "
                "FROM usage_events ORDER BY occurred_at, model"
            )
        ]


def chart_visible(tree):
    return (
        all(
            text in tree
            for text in (
                "Usage analytics|Selected",
                "Daily token usage",
                "Share of tokens",
                "7-day total",
                "By token type",
                "Uncached input, 660 tokens",
                "Cached input, 210 tokens",
                "Output, 330 tokens",
            )
        )
        and "Usage unavailable" not in tree
    )
