namespace Syno.TinyTorrent.Services;

internal sealed partial class Database
{
    internal const string Invalidate = "UPDATE library_state SET valid=0 WHERE singleton=1;";

    internal const string PruneVideos = """
        DELETE FROM video_information WHERE NOT EXISTS (
            SELECT 1 FROM identifications i WHERE i.catalog_id=video_information.catalog_id
            AND i.video_id=video_information.video_id);
        DELETE FROM title_facts WHERE NOT EXISTS (
            SELECT 1 FROM video_information v WHERE v.imdb_id=title_facts.imdb_id);
        """;

    internal const string Cleanup =
        """
            DELETE FROM video_attempts WHERE NOT EXISTS (
                SELECT 1 FROM pending_origins p WHERE p.origin=video_attempts.origin)
            AND NOT EXISTS (SELECT 1 FROM collected_files f
                WHERE f.origin=video_attempts.origin AND f.file_index=video_attempts.file_index);
            DELETE FROM identifications WHERE NOT EXISTS (
                SELECT 1 FROM pending_origins p WHERE p.origin=identifications.origin)
            AND NOT EXISTS (SELECT 1 FROM collected_files f
                WHERE f.origin=identifications.origin AND f.file_index=identifications.file_index);
            DELETE FROM file_facts WHERE NOT EXISTS (
                SELECT 1 FROM pending_origins p WHERE p.origin=file_facts.origin)
            AND NOT EXISTS (SELECT 1 FROM collected_files f
                WHERE f.origin=file_facts.origin AND f.file_index=file_facts.file_index);
            DELETE FROM fact_attempts WHERE entry_id NOT IN (SELECT entry_id FROM locations);
            DELETE FROM subtitle_output_sources WHERE NOT EXISTS (
                SELECT 1 FROM pending_origins p WHERE p.origin=subtitle_output_sources.origin)
            AND NOT EXISTS (SELECT 1 FROM collected_files f
                WHERE f.origin=subtitle_output_sources.origin AND f.file_index=subtitle_output_sources.file_index);
            DELETE FROM subtitle_outputs WHERE NOT EXISTS (SELECT 1 FROM subtitle_output_sources s
                WHERE s.path=subtitle_outputs.path AND s.language=subtitle_outputs.language);
            DELETE FROM subtitle_job_sources WHERE NOT EXISTS (
                SELECT 1 FROM pending_origins p WHERE p.origin=subtitle_job_sources.origin)
            AND NOT EXISTS (SELECT 1 FROM collected_files f
                WHERE f.origin=subtitle_job_sources.origin AND f.file_index=subtitle_job_sources.file_index);
            DELETE FROM subtitle_jobs WHERE NOT EXISTS (SELECT 1 FROM subtitle_job_sources s
                WHERE s.path=subtitle_jobs.path AND s.language=subtitle_jobs.language);
            DELETE FROM subtitle_scans WHERE NOT EXISTS (SELECT 1 FROM pending_origins p
                WHERE p.origin=subtitle_scans.origin) AND NOT EXISTS (SELECT 1 FROM collected_files f
                WHERE f.origin=subtitle_scans.origin AND f.file_index=subtitle_scans.file_index);
            """ + PruneVideos;

    // Only confirmed files share a location's choices and ownership. A cached
    // file can still name a path its torrent has left, and a file that arrives
    // at that path must not inherit or hand over decisions through it.
    internal const string Reconcile = """
        WITH choices AS (
            SELECT f.entry_id,i.*,
                row_number() OVER (PARTITION BY f.entry_id ORDER BY i.explicit DESC,i.decided DESC) AS rank
            FROM collected_files f JOIN identifications i USING(origin,file_index)
        )
        INSERT INTO identifications(origin,file_index,catalog_id,video_id,explicit,decided,evidence)
        SELECT f.origin,f.file_index,c.catalog_id,c.video_id,c.explicit,c.decided,f.name
        FROM collected_files f JOIN choices c ON c.entry_id=f.entry_id AND c.rank=1
        WHERE c.explicit=1
        ON CONFLICT(origin,file_index) DO UPDATE SET catalog_id=excluded.catalog_id,video_id=excluded.video_id,
            explicit=excluded.explicit,decided=excluded.decided,evidence=excluded.evidence
        WHERE (catalog_id,video_id,explicit,decided,evidence) IS NOT
            (excluded.catalog_id,excluded.video_id,excluded.explicit,excluded.decided,excluded.evidence);
        WITH facts AS (
            SELECT s.entry_id,p.*,
                row_number() OVER (PARTITION BY s.entry_id ORDER BY p.origin,p.file_index) AS rank
            FROM collected_files s JOIN file_facts p USING(origin,file_index)
        )
        INSERT OR IGNORE INTO file_facts
        SELECT s.origin,s.file_index,p.created,p.modified,p.title,p.artist,p.album,
            p.track,p.year,p.genre,p.duration,p.search,p.bitrate,p.width,p.height
        FROM collected_files s JOIN facts p ON p.entry_id=s.entry_id AND p.rank=1;
        INSERT OR IGNORE INTO subtitle_output_sources(path,language,origin,file_index,movie_path)
        SELECT s.path,s.language,f.origin,f.file_index,s.movie_path
        FROM subtitle_output_sources s
        JOIN collected_files owner ON owner.origin=s.origin AND owner.file_index=s.file_index
        JOIN locations l ON l.entry_id=owner.entry_id AND l.path=s.movie_path COLLATE PATH
        JOIN collected_files f ON f.entry_id=owner.entry_id;
        INSERT OR IGNORE INTO subtitle_job_sources(path,language,origin,file_index)
        SELECT s.path,s.language,f.origin,f.file_index
        FROM subtitle_job_sources s
        JOIN collected_files owner ON owner.origin=s.origin AND owner.file_index=s.file_index
        JOIN collected_files f ON f.entry_id=owner.entry_id;
        """;
}
