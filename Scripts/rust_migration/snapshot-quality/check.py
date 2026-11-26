"""Compare the public and candidate usageSnapshot after each commit to a synthetic profile."""
import argparse
import importlib.util
import json
from pathlib import Path
import platform
import random
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("database_fixture", HERE.parent / "analytics-db-contract/check.py")
contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contract)
START = 1_704_067_200  # 2024-01-01 00:00:00 UTC, Monday


def cases(include_corrupt_counters):
    seed = random.Random(32814)
    past = f"""INSERT INTO usage_events(event_key, occurred_at, input_tokens, cached_input_tokens, output_tokens,
            source_generation, source_path, source_position) VALUES('old',{START},12,3,1,0,'fixture',1)"""
    future = past.replace("'old'", "'future'").replace("'fixture',1", "'fixture',2").replace(str(START), str(START + 86400 * 10))
    commands = ["", future, past,
                "INSERT INTO analytics_backfill_sources VALUES('older')", "", "DELETE FROM analytics_backfill_sources"]
    for _ in range(160):
        statements = ["DELETE FROM app_metadata", "DELETE FROM partial_evidence", "DELETE FROM session_counters",
                      "DELETE FROM analytics_backfill_sources"]
        if seed.randrange(4) == 0:
            statements.append("INSERT INTO analytics_backfill_sources VALUES('file')")
        if seed.randrange(6) == 0:
            statements.append("INSERT INTO app_metadata VALUES('partial_source_quality:file','')")
        if seed.randrange(3) == 0:
            statements.append("INSERT INTO app_metadata VALUES('legacy_partial_quality','1')")
        if seed.randrange(2) == 0:
            through = seed.choice([str(START - 1), str(START), str(START + 1), "absent"])
            statements.append(f"INSERT INTO app_metadata VALUES('legacy_partial_quality_through','{through}')")
        if seed.randrange(3) == 0:
            at = START + seed.choice([-10, 0, 5])
            statements.append(f"INSERT INTO partial_evidence VALUES('file',1,{at})")
        for j in range(seed.randrange(5)):
            at = seed.choice(["NULL", str(START - 1), str(START), str(START + 1)])
            quality = seed.choice(["exact", "exact", "partial"])
            written = seed.choice(["NULL", "0", "-1" if include_corrupt_counters else "1"])
            statements.append("INSERT INTO session_counters(session_id, input_tokens, cached_input_tokens, "
                              "cache_write_input_tokens, output_tokens, quality, last_observed_at) "
                              f"VALUES('s{j}',2,0,{written},1,'{quality}',{at})")
        commands.append("BEGIN;" + ";".join(statements) + ";COMMIT")
    # Restored views and generated columns may throw while evaluating a row.
    # Preserve the original short-circuit order between counters and evidence.
    commands.extend([
        "DELETE FROM app_metadata; DELETE FROM analytics_backfill_sources; DELETE FROM partial_evidence; "
        "DELETE FROM session_counters; INSERT INTO session_counters(session_id,input_tokens,cached_input_tokens,"
        "output_tokens,quality,last_observed_at) VALUES('broken',1,0,1,'partial',null)",
        "DROP TABLE partial_evidence; CREATE VIEW Partial_Evidence AS "
        "SELECT 'f' AS source, 1 AS position, abs(-9223372036854775808) AS observed_at",
        "UPDATE session_counters SET quality='exact'",
        "DROP VIEW Partial_Evidence; CREATE TABLE partial_evidence(raw_observed_at); "
        "INSERT INTO partial_evidence VALUES(1); ALTER TABLE partial_evidence ADD COLUMN observed_at "
        "GENERATED ALWAYS AS (CASE WHEN raw_observed_at=1 THEN abs(-9223372036854775808) ELSE raw_observed_at END) VIRTUAL",
        "UPDATE session_counters SET quality='partial'",
        "DROP TABLE partial_evidence", "", "DELETE FROM usage_events", ""
    ])
    return commands


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--archive", type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-snapshot-quality-") as tmp:
        work = Path(tmp)
        published = contract.build(work / "published", args.arch, driver=HERE / "main.swift")
        # This host already marks negative stored counters as partial (3a835896).
        previous = contract.build(work / "previous", args.arch, args.archive.resolve(strict=True),
                                  revision="e6af546998b2314b778e2617628bfd8c02723bae", driver=HERE / "main.swift")
        candidate = contract.build(work / "candidate", args.arch, args.archive.resolve(strict=True),
                                   driver=HERE / "main.swift")
        base = work / "base.sqlite"
        subprocess.run([published, base, "init"], check=True)
        def trace(name, binary, commands):
            path = work / f"{name}.sqlite"
            shutil.copyfile(base, path)
            return subprocess.check_output([binary, path, "compare"], text=True,
                input="".join(json.dumps(s) + "\n" for s in commands)).splitlines()
        for corrupt in [False, True]:
            commands = cases(corrupt)
            reference = previous if corrupt else published
            old = trace(f"reference-{corrupt}", reference, commands)
            new = trace(f"candidate-{corrupt}", candidate, commands)
            assert len(old) == len(new) == len(commands)
            for i, (before, after) in enumerate(zip(old, new)):
                if before != after:
                    raise ValueError((i, commands[i], before, after))
            states = {tuple(row.split()[:2]) for row in old}
            assert {("exact", "false"), ("exact", "true"), ("partial", "true"), ("unavailable", "false")} <= states
            assert "no such table" in old[-4] and old[-1].startswith("unavailable false")
    print(args.arch, 2 * len(commands), "matching full usageSnapshot observations")


if __name__ == "__main__":
    main()
