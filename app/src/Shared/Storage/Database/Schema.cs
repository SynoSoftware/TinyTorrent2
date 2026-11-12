namespace Syno.TinyTorrent.Shared.Storage;

internal sealed partial class Database
{
    // Schema is revision 9, the oldest revision a saved database holds. A new
    // database runs Schema and then every migration, so one implementation of
    // each change serves new and saved databases.
    private const int Baseline = 9;

    // Migrations[n] upgrades a database at revision Baseline + n to the next.
    private static readonly string[] Migrations =
    [
        // 10: saved choices are keyed by FileName.TitleKey.
        """
        UPDATE video_choices SET name=title_key(title);
        UPDATE video_choices SET original_name=title_key(original) WHERE original IS NOT NULL;
        """,
        // 11: disk discovery's facts.
        """
            -- Its contributions come from disk instead of a torrent, and its
            -- files keep what gozler names them by: their
            -- volume, their file ID on it, and the time they were last written,
            -- as a FILETIME, or NULL while gozler has not reported it.
            ALTER TABLE contributions ADD COLUMN data_source TEXT NOT NULL DEFAULT 'torrent'
                CHECK(data_source IN ('torrent','disk'));
            CREATE TABLE disk_files (
                origin TEXT NOT NULL, file_index INTEGER NOT NULL,
                volume TEXT NOT NULL, file_id TEXT NOT NULL, modified INTEGER,
                PRIMARY KEY(origin,file_index),
                FOREIGN KEY(origin,file_index) REFERENCES source_files(origin,file_index) ON DELETE CASCADE
            );
            -- A location the person's own check found gone, though a torrent
            -- may still list it.
            ALTER TABLE locations ADD COLUMN absent INTEGER NOT NULL DEFAULT 0 CHECK(absent IN (0,1));
            ALTER TABLE library_settings ADD COLUMN discovery INTEGER NOT NULL DEFAULT 0
                CHECK(discovery IN (0,1));
            CREATE TABLE hidden_folders (path TEXT COLLATE PATH NOT NULL PRIMARY KEY);
            -- The number the next new disk file takes. Numbers are never
            -- used again, so facts still kept for a withdrawn file never pass
            -- to another.
            CREATE TABLE disk_numbers (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), next INTEGER NOT NULL
            );
            INSERT INTO disk_numbers VALUES(1,0);
            """,
    ];

    private static int Version => Baseline + Migrations.Length;

    private const string Schema = """
            -- The person's own API key, protected by Credentials, replaces
            -- the key built into the release.
            CREATE TABLE provider_keys (provider_id TEXT NOT NULL PRIMARY KEY, secret BLOB NOT NULL);
            CREATE TABLE contributions (
                origin TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, stamp TEXT NOT NULL
            );
            CREATE TABLE locations (
                entry_id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT COLLATE PATH NOT NULL UNIQUE
            );
            CREATE TABLE source_files (
                origin TEXT NOT NULL, file_index INTEGER NOT NULL, entry_id INTEGER NOT NULL,
                name TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL,
                complete INTEGER NOT NULL, wanted INTEGER NOT NULL, kind INTEGER NOT NULL,
                title TEXT NOT NULL, type TEXT NOT NULL, year INTEGER, search TEXT NOT NULL,
                PRIMARY KEY (origin, file_index)
            );
            CREATE INDEX source_locations ON source_files(entry_id);
            CREATE TABLE request_settings (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), lookups INTEGER NOT NULL DEFAULT 5
            );
            INSERT INTO request_settings(singleton) VALUES(1);
            CREATE TABLE request_blocks (
                provider_id TEXT NOT NULL, question_id TEXT NOT NULL, reason INTEGER NOT NULL CHECK(reason IN (0,1,7)),
                checked INTEGER NOT NULL, PRIMARY KEY(provider_id,question_id)
            );
            CREATE TABLE request_counts (
                provider_id TEXT NOT NULL, question_id TEXT NOT NULL, total INTEGER NOT NULL CHECK(total>=0),
                PRIMARY KEY(provider_id,question_id)
            );
            CREATE TABLE request_holds (
                provider_id TEXT NOT NULL, identity TEXT NOT NULL, operation TEXT NOT NULL,
                reason INTEGER NOT NULL CHECK(reason IN (3,4,5)), retry_at INTEGER,
                failures INTEGER NOT NULL DEFAULT 0 CHECK(failures>=0), checked INTEGER NOT NULL,
                PRIMARY KEY(provider_id,identity,operation,reason),
                CHECK(reason=5 OR retry_at IS NOT NULL)
            );
            CREATE TABLE request_usage (
                provider_id TEXT NOT NULL, identity TEXT NOT NULL, started INTEGER NOT NULL
            );
            CREATE INDEX request_usage_scope ON request_usage(provider_id,identity,started);
            CREATE TABLE source_state (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                initialized INTEGER NOT NULL CHECK(initialized IN (0,1))
            );
            INSERT INTO source_state VALUES(1,0);
            CREATE TABLE known_torrents (origin TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE video_information (
                catalog_id TEXT NOT NULL, video_id TEXT NOT NULL, language TEXT NOT NULL,
                title TEXT NOT NULL, type TEXT NOT NULL, year INTEGER, genres TEXT NOT NULL,
                cast TEXT NOT NULL, synopsis TEXT NOT NULL, keywords TEXT NOT NULL, search TEXT NOT NULL,
                series_catalog TEXT, series_id TEXT, series_type TEXT, series_title TEXT, series_year INTEGER,
                ratings TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(ratings)), episodes TEXT, imdb_id INTEGER,
                PRIMARY KEY(catalog_id,video_id)
            );
            CREATE TABLE title_facts (
                imdb_id INTEGER PRIMARY KEY, ratings TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(ratings)),
                directors TEXT NOT NULL DEFAULT '', certification TEXT NOT NULL DEFAULT '', box_office INTEGER
            );
            CREATE TABLE identifications (
                origin TEXT NOT NULL, file_index INTEGER NOT NULL, catalog_id TEXT, video_id TEXT,
                explicit INTEGER NOT NULL, decided INTEGER NOT NULL, evidence TEXT NOT NULL,
                PRIMARY KEY(origin,file_index),
                CHECK((catalog_id IS NULL) = (video_id IS NULL)),
                FOREIGN KEY(catalog_id,video_id) REFERENCES video_information(catalog_id,video_id)
            );
            CREATE TABLE video_records (
                catalog_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
                content TEXT NOT NULL, retrieved INTEGER NOT NULL, checked INTEGER NOT NULL,
                PRIMARY KEY(catalog_id,path,language)
            );
            CREATE TABLE video_references (
                catalog_id TEXT NOT NULL, video_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
                PRIMARY KEY(catalog_id,video_id,path,language),
                FOREIGN KEY(catalog_id,video_id) REFERENCES video_information(catalog_id,video_id) ON DELETE CASCADE,
                FOREIGN KEY(catalog_id,path,language) REFERENCES video_records(catalog_id,path,language)
            );
            CREATE TABLE video_choices (
                catalog_id TEXT NOT NULL, video_id TEXT NOT NULL, type TEXT NOT NULL,
                title TEXT NOT NULL, original TEXT, year INTEGER,
                name TEXT NOT NULL, original_name TEXT,
                PRIMARY KEY(catalog_id,video_id)
            );
            CREATE INDEX video_choices_name ON video_choices(catalog_id,name);
            CREATE INDEX video_choices_original ON video_choices(catalog_id,original_name);
            CREATE TABLE file_facts (
                origin TEXT NOT NULL, file_index INTEGER NOT NULL, created INTEGER, modified INTEGER,
                title TEXT NOT NULL, artist TEXT NOT NULL, album TEXT NOT NULL, track INTEGER,
                year INTEGER, genre TEXT NOT NULL, duration INTEGER, search TEXT NOT NULL,
                bitrate INTEGER, width INTEGER, height INTEGER,
                PRIMARY KEY(origin,file_index)
            );
            CREATE TABLE library_settings (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                enabled INTEGER NOT NULL DEFAULT 0, source TEXT
            );
            INSERT INTO library_settings(singleton) VALUES(1);
            CREATE TABLE video_searches (
                catalog_id TEXT NOT NULL, question_id TEXT NOT NULL, choices TEXT NOT NULL,
                rejected INTEGER NOT NULL DEFAULT 0, checked INTEGER NOT NULL,
                PRIMARY KEY(catalog_id,question_id), CHECK(json_valid(choices))
            );
            CREATE INDEX identifications_video ON identifications(catalog_id,video_id);
            CREATE INDEX video_information_movie ON video_information(catalog_id,year,language) WHERE type='movie';
            CREATE INDEX video_information_imdb ON video_information(imdb_id) WHERE imdb_id IS NOT NULL;
            CREATE TABLE subtitle_settings (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), enabled INTEGER NOT NULL DEFAULT 0,
                supplier INTEGER NOT NULL DEFAULT 0, username TEXT NOT NULL DEFAULT '', secret BLOB,
                languages TEXT NOT NULL DEFAULT '[]', extras INTEGER NOT NULL DEFAULT 1
            );
            INSERT INTO subtitle_settings(singleton) VALUES(1);
            CREATE TABLE subtitle_outputs (
                path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL, created INTEGER NOT NULL, reference TEXT,
                PRIMARY KEY(path,language)
            );
            CREATE TABLE subtitle_jobs (
                path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL, stage INTEGER NOT NULL,
                outcome INTEGER NOT NULL, retry_at INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
                manual INTEGER NOT NULL DEFAULT 0, guesses INTEGER NOT NULL DEFAULT 2, match INTEGER, reference TEXT,
                fresh TEXT CHECK(fresh IS NULL OR json_valid(fresh)),
                PRIMARY KEY(path,language)
            );
            CREATE TABLE subtitle_output_sources (
                path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL,
                origin TEXT NOT NULL, file_index INTEGER NOT NULL,
                movie_path TEXT COLLATE PATH NOT NULL,
                PRIMARY KEY(path,language,origin,file_index),
                FOREIGN KEY(path,language) REFERENCES subtitle_outputs(path,language) ON DELETE CASCADE
            );
            CREATE INDEX subtitle_output_sources_file ON subtitle_output_sources(origin,file_index,language,path);
            CREATE TABLE subtitle_job_sources (
                path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL,
                origin TEXT NOT NULL, file_index INTEGER NOT NULL,
                PRIMARY KEY(path,language,origin,file_index),
                FOREIGN KEY(path,language) REFERENCES subtitle_jobs(path,language) ON DELETE CASCADE
            );
            CREATE TABLE subtitle_scans (
                origin TEXT NOT NULL, file_index INTEGER NOT NULL, path TEXT COLLATE PATH NOT NULL,
                stamp TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(origin,file_index)
            );
            CREATE TABLE subtitle_searches (
                provider_id TEXT NOT NULL, question_id TEXT NOT NULL, answer TEXT NOT NULL CHECK(json_valid(answer)),
                rejected INTEGER NOT NULL DEFAULT 0, reusable INTEGER NOT NULL DEFAULT 1, checked INTEGER NOT NULL,
                PRIMARY KEY(provider_id,question_id)
            );
            """;

    private const string Projection = """
        CREATE TEMP TABLE new_origins (origin TEXT NOT NULL PRIMARY KEY);
        CREATE TEMP TABLE file_reads (
            origin TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, complete INTEGER NOT NULL,
            settled INTEGER NOT NULL, collected INTEGER NOT NULL DEFAULT 0,
            save_path TEXT NOT NULL, stamp TEXT NOT NULL, checked INTEGER NOT NULL DEFAULT 0
        );
        -- The disk contribution once this connection accepted an answer from
        -- gozler: its File confirmation. The torrents' is in file_reads.
        CREATE TEMP TABLE disk_reads (origin TEXT NOT NULL PRIMARY KEY);
        CREATE TEMP VIEW collected_files AS
        SELECT f.* FROM source_files f JOIN file_reads c USING (origin)
        WHERE c.collected=1
        UNION ALL
        SELECT f.* FROM source_files f JOIN disk_reads USING (origin);
        -- Every contribution this connection has not confirmed, so cleanup
        -- keeps its facts: also a torrent the engine has not listed yet.
        CREATE TEMP VIEW pending_origins AS
        SELECT origin FROM file_reads WHERE collected=0
        UNION ALL
        SELECT origin FROM contributions WHERE origin NOT IN (SELECT origin FROM file_reads)
            AND origin NOT IN (SELECT origin FROM disk_reads);
        -- A location that a torrent lists follows the torrent: a disk file
        -- counts as finished, but a torrent's file may not be. A disk file is
        -- readable once confirmed, unless it is a cloud placeholder.
        CREATE TEMP VIEW current_files AS
        SELECT l.entry_id, l.path, f.name, f.size, f.kind, f.title, f.type, f.year, f.search,
            COALESCE(MAX(CASE WHEN s.data_source='torrent' THEN f.complete END),1) AS complete,
            COALESCE(MAX(CASE WHEN s.data_source='torrent'
                    THEN f.complete AND COALESCE(c.complete AND c.settled AND c.collected,0) END),
                MAX(f.complete AND d.origin IS NOT NULL)) AS readable
        FROM locations l JOIN source_files f USING (entry_id)
        JOIN contributions s USING (origin)
        LEFT JOIN file_reads c USING (origin)
        LEFT JOIN disk_reads d USING (origin)
        WHERE l.absent=0
        GROUP BY l.entry_id;
        -- The identification that decides each Library entry: the latest
        -- explicit choice of its files, including Clear, else the latest
        -- provider match whose file still has the name it was matched under.
        CREATE TEMP VIEW entry_identifications AS
        SELECT l.entry_id, i.catalog_id, i.video_id, i.explicit
        FROM locations l JOIN identifications i ON (i.origin,i.file_index) = (
            SELECT d.origin,d.file_index FROM identifications d
            JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
            WHERE s.entry_id=l.entry_id AND (d.explicit=1 OR d.evidence=s.name)
            ORDER BY d.explicit DESC,d.decided DESC LIMIT 1);
        CREATE TEMP TABLE present (origin TEXT NOT NULL PRIMARY KEY);
        CREATE TEMP TABLE received (file_index INTEGER PRIMARY KEY);
        CREATE TEMP TABLE video_attempts (
            origin TEXT NOT NULL, file_index INTEGER NOT NULL, catalog_id TEXT NOT NULL,
            evidence TEXT NOT NULL, request TEXT NOT NULL, outcome INTEGER, question_id TEXT,
            PRIMARY KEY(origin,file_index,catalog_id)
        );
        CREATE INDEX video_attempts_request ON video_attempts(catalog_id,request) WHERE outcome IS NULL;
        CREATE TEMP TABLE fact_attempts (entry_id INTEGER PRIMARY KEY, path TEXT COLLATE PATH NOT NULL);
        CREATE TEMP VIEW subtitle_languages AS
        SELECT DISTINCT s.origin,s.file_index,s.language FROM subtitle_output_sources s
        JOIN subtitle_outputs o USING(path,language)
        JOIN source_files f ON f.origin=s.origin AND f.file_index=s.file_index
        JOIN locations l ON l.entry_id=f.entry_id
        WHERE s.movie_path=l.path COLLATE PATH;
        CREATE TEMP VIEW library_entries AS
        SELECT f.entry_id, f.path, f.name, f.size, f.kind,
            COALESCE(NULLIF(v.title,''), NULLIF(p.title,''), f.title) AS title,
            COALESCE(v.type,f.type) AS type, COALESCE(v.year,p.year,f.year) AS year,
            COALESCE(v.genres,p.genre,'') AS genres,
            CASE WHEN length(v.cast)>160 THEN substr(v.cast,1,160)||'…' ELSE COALESCE(v.cast,'') END AS cast,
            COALESCE(p.artist,'') AS artist, COALESCE(p.album,'') AS album,
            p.track, p.duration, p.created, p.modified,
            f.search AS file_search,
            v.video_id, COALESCE(p.search,'') AS music_search, v.catalog_id,
            COALESCE(t.ratings,v.ratings) AS ratings, v.imdb_id,
            v.series_catalog, v.series_id, v.series_type, v.series_title, v.episodes,
            p.bitrate, p.width, p.height,
            COALESCE((SELECT group_concat(DISTINCT a.language) FROM subtitle_languages a
                JOIN source_files s USING(origin,file_index) WHERE s.entry_id=f.entry_id),'') AS subtitles
        FROM current_files f
        LEFT JOIN video_information v ON (v.catalog_id,v.video_id) = (
            SELECT i.catalog_id,i.video_id FROM entry_identifications i WHERE i.entry_id=f.entry_id)
        LEFT JOIN title_facts t ON t.imdb_id=v.imdb_id
        LEFT JOIN file_facts p ON (p.origin,p.file_index) = (
            SELECT d.origin,d.file_index FROM file_facts d
            JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
            WHERE s.entry_id=f.entry_id LIMIT 1)
        WHERE f.complete=1;
        -- The search snapshot: a copy of library_entries, so a search does not
        -- join every entry again. Its columns follow library_entries in order.
        -- The entry key keeps each row in entry order when it is copied again.
        CREATE TEMP TABLE library_rows (
            entry_id INTEGER PRIMARY KEY, path, name, size, kind, title, type, year, genres, cast,
            artist, album, track, duration, created, modified, file_search, video_id, music_search,
            catalog_id, ratings, imdb_id, series_catalog, series_id, series_type, series_title,
            episodes, bitrate, width, height, subtitles
        );
        CREATE TEMP TABLE library_matches (row_id INTEGER PRIMARY KEY);
        -- The entries whose rows the open transaction has made out of date, each
        -- once for every row written. It has no key: a trigger's statement
        -- takes the conflict rule of the upsert that fired it, so a key here
        -- fails the upsert.
        CREATE TEMP TABLE library_changes (entry_id INTEGER);
        INSERT INTO library_rows SELECT * FROM library_entries;
        """;

    // The tables whose rows decide what library_entries shows, with the
    // entries that one of their rows belongs to. A row written to one adds
    // those entries to library_changes. The view reads others that need no
    // trigger: subtitle_outputs reaches it only through its cascade to
    // subtitle_output_sources; file_reads, disk_reads and contributions
    // decide no selected column, and a contribution is written together with
    // its source_files.
    private static readonly (string Table, Func<string, string> Entries)[] Inputs =
    [
        ("locations", row => $"SELECT {row}.entry_id"),
        ("source_files", row => $"SELECT {row}.entry_id"),
        ("identifications", FileEntry),
        ("file_facts", FileEntry),
        ("subtitle_output_sources", FileEntry),
        (
            "video_information",
            row => $"""
                SELECT s.entry_id FROM identifications i JOIN source_files s USING(origin,file_index)
                WHERE i.catalog_id={row}.catalog_id AND i.video_id={row}.video_id
                """
        ),
        (
            "title_facts",
            row => $"""
                SELECT s.entry_id FROM video_information v
                JOIN identifications i USING(catalog_id,video_id)
                JOIN source_files s USING(origin,file_index) WHERE v.imdb_id={row}.imdb_id
                """
        ),
    ];

    private static string FileEntry(string row) =>
        $"SELECT entry_id FROM source_files WHERE origin={row}.origin AND file_index={row}.file_index";

    private static readonly string Triggers = string.Concat(
        Inputs.Select(input => $"""
            CREATE TEMP TRIGGER {input.Table}_added AFTER INSERT ON {input.Table} BEGIN
                INSERT INTO library_changes {input.Entries("NEW")}; END;
            CREATE TEMP TRIGGER {input.Table}_changed AFTER UPDATE ON {input.Table} BEGIN
                INSERT INTO library_changes {input.Entries("OLD")} UNION {input.Entries("NEW")}; END;
            CREATE TEMP TRIGGER {input.Table}_removed AFTER DELETE ON {input.Table} BEGIN
                INSERT INTO library_changes {input.Entries("OLD")}; END;

            """)
    );
}
