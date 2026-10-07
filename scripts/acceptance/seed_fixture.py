import argparse
import hashlib
import json
import os
import sqlite3
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

NAMESPACE = uuid.UUID("0f64dd2a-2bd2-4a6b-9828-e4364a432a36")
ROOT_ID = uuid.uuid5(NAMESPACE, "acceptance-root")
SCAN_ID = uuid.uuid5(NAMESPACE, "acceptance-scan")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def fingerprint(path: Path) -> dict:
    stat = path.stat()
    return {"sha256": sha256(path), "size_bytes": stat.st_size, "mtime_ns": stat.st_mtime_ns}


def windows_normalized(path: Path) -> str:
    return os.path.normcase(str(path.resolve())).upper()


def main() -> int:
    parser = argparse.ArgumentParser(description="Create the isolated Phase 2 Acceptance Closure 10k media fixture.")
    parser.add_argument("--database", required=True)
    parser.add_argument("--media-root", required=True)
    parser.add_argument("--cache-root", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--count", type=int, default=10_000)
    args = parser.parse_args()

    database = Path(args.database).resolve()
    media_root = Path(args.media_root).resolve()
    cache_root = Path(args.cache_root).resolve()
    summary_path = Path(args.summary).resolve()
    if args.count != 10_000:
        raise ValueError("Acceptance fixture must contain exactly 10,000 rows.")
    if not database.is_file():
        raise FileNotFoundError(f"Database must be initialized by the portable app first: {database}")

    media_root.mkdir(parents=True, exist_ok=True)
    cache_thumbnail = cache_root / "thumbnails" / "acceptance"
    cache_thumbnail.mkdir(parents=True, exist_ok=True)

    originals = {
        "row-00000.mp4": b"Local Media AI Acceptance Closure original fixture 00000\n",
        "row-00001.mp4": b"Local Media AI Acceptance Closure original fixture 00001\n",
    }
    for name, content in originals.items():
        target = media_root / name
        target.write_bytes(content)
    before = {name: fingerprint(media_root / name) for name in originals}

    # 1x1 PNG is sufficient to exercise WPF image decoding; the artifact is local cache, never original media.
    thumbnail = cache_thumbnail / "fixture-thumbnail.png"
    thumbnail.write_bytes(bytes.fromhex(
        "89504E470D0A1A0A0000000D49484452000000010000000108060000001F15C489"
        "0000000D4944415478DA6360F8CFF00F00050102A7B3C3F40000000049454E44AE426082"))

    now = datetime.now(timezone.utc).isoformat()
    connection = sqlite3.connect(database)
    try:
        connection.execute("PRAGMA foreign_keys=ON")
        with connection:
            for table in ("job_event", "job", "artifact", "analysis", "media_metadata", "media_file", "media_content", "library_scan", "library_root"):
                connection.execute(f"DELETE FROM {table}")

            connection.execute(
                "INSERT INTO library_root(library_root_id,configured_path,normalized_path,display_name,volume_identity,availability,created_at,last_scan_at) VALUES(?,?,?,?,?,?,?,?)",
                (str(ROOT_ID), str(media_root), windows_normalized(media_root), "Acceptance 10k Fixture", None, "Online", now, now),
            )
            connection.execute(
                "INSERT INTO library_scan(scan_id,library_root_id,state,started_at,completed_at,files_seen,files_added,files_updated,files_unavailable,error_count) VALUES(?,?,?,?,?,?,?,?,?,?)",
                (str(SCAN_ID), str(ROOT_ID), "Completed", now, now, args.count, args.count, 0, 0, 0),
            )

            contents = []
            files = []
            metadata = []
            for index in range(args.count):
                name = f"row-{index:05d}.mp4"
                media_id = uuid.uuid5(NAMESPACE, f"media-{index}")
                content_id = uuid.uuid5(NAMESPACE, f"content-{index}")
                path = media_root / name
                contents.append((str(content_id), None, 1024 + index, "QuickFingerprint", now))
                files.append((
                    str(media_id), str(ROOT_ID), str(content_id), str(path), windows_normalized(path), name, ".mp4",
                    1024 + index, now, now, None, f"acceptance-file-{index}", f"acceptance-fingerprint-{index}", None,
                    "Present", now, now, str(SCAN_ID),
                ))
                metadata.append((str(media_id), "mov,mp4,m4a,3gp,3g2,mj2", 1.0, 64, 64, "h264", "30", 1000, 0, 0, "{}", now))

            connection.executemany(
                "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES(?,?,?,?,?)", contents)
            connection.executemany(
                "INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", files)
            connection.executemany(
                "INSERT INTO media_metadata(media_file_id,container_format,duration_seconds,width,height,video_codec,fps_text,bitrate,audio_stream_count,subtitle_stream_count,raw_summary_json,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)", metadata)

            completed_analysis = uuid.uuid5(NAMESPACE, "thumbnail-completed")
            failed_analysis = uuid.uuid5(NAMESPACE, "thumbnail-failed")
            completed_content = str(uuid.uuid5(NAMESPACE, "content-0"))
            failed_content = str(uuid.uuid5(NAMESPACE, "content-1"))
            connection.execute(
                "INSERT INTO analysis(analysis_id,media_content_id,analysis_type,status,processor_name,processor_version,model_name,model_version,config_json,config_hash,input_identity_hash,processing_version,started_at,completed_at,error_code,error_message) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (str(completed_analysis), completed_content, "Thumbnail", "Completed", "fixture", "1", None, None, "{}", "fixture", "fixture", "fixture", now, now, None, None),
            )
            connection.execute(
                "INSERT INTO artifact(artifact_id,analysis_id,artifact_kind,relative_path,sha256,byte_size,mime_type,durability_class,created_at) VALUES(?,?,?,?,?,?,?,?,?)",
                (str(uuid.uuid5(NAMESPACE, "thumbnail-artifact")), str(completed_analysis), "Thumbnail", "thumbnails/acceptance/fixture-thumbnail.png", sha256(thumbnail), thumbnail.stat().st_size, "image/png", "RebuildableCache", now),
            )
            connection.execute(
                "INSERT INTO analysis(analysis_id,media_content_id,analysis_type,status,processor_name,processor_version,model_name,model_version,config_json,config_hash,input_identity_hash,processing_version,started_at,completed_at,error_code,error_message) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (str(failed_analysis), failed_content, "Thumbnail", "Failed", "fixture", "1", None, None, "{}", "fixture", "fixture", "fixture", now, now, "TEST_THUMBNAIL_FAILED", "Acceptance fixture failure placeholder"),
            )
            connection.execute(
                "INSERT INTO job(job_id,job_type,state,phase,reason,payload_json,attempt,max_attempts,not_before,started_at,completed_at,heartbeat_at,progress_current,progress_total,progress_unit,progress_indeterminate,last_error_code,last_error_message,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (str(uuid.uuid5(NAMESPACE, "failed-thumbnail-job")), "Thumbnail", "Failed", "Persisted", "None", json.dumps({"MediaFileId": str(uuid.uuid5(NAMESPACE, "media-1"))}), 1, 1, None, now, now, now, 1, 1, "file", 0, "TEST_THUMBNAIL_FAILED", "Acceptance fixture failure placeholder", now, now),
            )
        connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    finally:
        connection.close()

    after = {name: fingerprint(media_root / name) for name in originals}
    if before != after:
        raise RuntimeError("Fixture original media changed during fixture seeding.")
    summary = {
        "fixture_row_count": args.count,
        "library_root_id": str(ROOT_ID),
        "first_media_file_id": str(uuid.uuid5(NAMESPACE, "media-0")),
        "failed_media_file_id": str(uuid.uuid5(NAMESPACE, "media-1")),
        "media_root": str(media_root),
        "originals_before": before,
        "originals_after": after,
        "thumbnail_path": str(thumbnail),
    }
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
