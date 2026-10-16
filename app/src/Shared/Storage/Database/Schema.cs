namespace Syno.TinyTorrent.Shared.Storage;

internal sealed partial class Database
{
    private const int Version = 9;

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
        CREATE TEMP VIEW collected_files AS
        SELECT f.* FROM source_files f JOIN file_reads c USING (origin)
        WHERE c.collected=1;
        CREATE TEMP VIEW pending_origins AS
        SELECT origin FROM file_reads WHERE collected=0;
        CREATE TEMP VIEW current_files AS
        SELECT l.entry_id, l.path, f.name, f.size, f.kind, f.title, f.type, f.year, f.search,
            MAX(f.complete) AS complete,
            MAX(f.complete AND COALESCE(c.complete AND c.settled AND c.collected,0)) AS readable
        FROM locations l JOIN source_files f USING (entry_id)
        LEFT JOIN file_reads c USING (origin)
        GROUP BY l.entry_id;
        CREATE TEMP TABLE present (origin TEXT NOT NULL PRIMARY KEY);
        CREATE TEMP TABLE received (file_index INTEGER PRIMARY KEY);
        CREATE TEMP TABLE video_attempts (
            origin TEXT NOT NULL, file_index INTEGER NOT NULL, catalog_id TEXT NOT NULL,
            evidence TEXT NOT NULL, request TEXT NOT NULL, outcome TEXT, question_id TEXT,
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
        LEFT JOIN identifications i ON (i.origin,i.file_index) = (
            SELECT d.origin,d.file_index FROM identifications d
            JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
            WHERE s.entry_id=f.entry_id AND (d.explicit=1 OR d.evidence=s.name)
            ORDER BY d.explicit DESC,d.decided DESC LIMIT 1)
        LEFT JOIN video_information v ON v.catalog_id=i.catalog_id AND v.video_id=i.video_id
        LEFT JOIN title_facts t ON t.imdb_id=v.imdb_id
        LEFT JOIN file_facts p ON (p.origin,p.file_index) = (
            SELECT d.origin,d.file_index FROM file_facts d
            JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
            WHERE s.entry_id=f.entry_id LIMIT 1)
        WHERE f.complete=1;
        CREATE TEMP TABLE library_state (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            valid INTEGER NOT NULL CHECK(valid IN (0,1))
        );
        INSERT INTO library_state VALUES(1,0);
        CREATE TEMP TABLE library_rows AS SELECT * FROM library_entries WHERE 0;
        CREATE TEMP TABLE library_matches (row_id INTEGER PRIMARY KEY);
        """;
}
