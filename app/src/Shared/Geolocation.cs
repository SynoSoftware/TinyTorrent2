using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using MaxMind.Db;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// Where a peer's address is: its country, from DB-IP's country data
/// installed with the app, and its city once the person downloads DB-IP's
/// city data. Both files are read in place; the city data is the replaceable
/// copy that <see cref="IDataset"/> describes, kept beside the Library
/// database. Lookups fail closed: an address the data lacks has no location.
/// </summary>
internal sealed class Geolocation : IDataset, IDisposable
{
    private const string CityFileName = "dbip-city-lite.mmdb";
    private static readonly string CountryPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "dbip-country-lite.mmdb"
    );
    private static readonly ProviderAccess Access = new(
        "dbip",
        RequestKey.Digest(string.Empty),
        "dataset"
    );
    private readonly ProviderHttp _http;
    private Reader? _countries = Open(CountryPath);
    private Reader? _cities;
    private string? _directory;
    private RequestPolicy? _policy;

    // Lookups share the files; Replace changes the city file alone, because
    // Windows refuses to replace an open file.
    private readonly ReaderWriterLockSlim _file = new();

    // Every lookup decodes new places with their names in every language, and
    // thousands of peers share a few hundred places, so peers in one place
    // share one location and one country. Cleared when the data changes.
    private readonly ConcurrentDictionary<string, Place> _countryPlaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Code, GeoPoint? Point, string? City), Location> _locations = new();

    internal Geolocation(ProviderHttp http, BackgroundWork work)
    {
        _http = http;
        Download = new DataDownload(this, work);
    }

    internal DataDownload Download { get; }

    /// <summary>Changes when the city data is installed, deleted or detached, so a kept location is looked up again.</summary>
    internal int Version { get; private set; }

    public string Section => "city";

    public string Name => "DB-IP";

    public string? FilePath =>
        _directory is { } directory ? Path.Combine(directory, CityFileName) : null;

    public WorkDescription Downloading { get; } = new WorkDescription.City(CityPhase.Downloading);

    public WorkDescription Installing { get; } = new WorkDescription.City(CityPhase.Installing);

    public Task<long>? Count => null;

    public IReadOnlyList<string> Leftovers => [];

    /// <summary>DB-IP publishes its data once a month, so a copy from this month is the current one.</summary>
    public bool IsCurrent(DateTime written) =>
        written.Year == DateTime.UtcNow.Year && written.Month == DateTime.UtcNow.Month;

    /// <summary>
    /// Serves the city data from <paramref name="directory"/>, downloading it
    /// through <paramref name="policy"/>, until <see cref="Detach"/>.
    /// </summary>
    internal void Attach(string directory, RequestPolicy policy)
    {
        _file.EnterWriteLock();
        try
        {
            _directory = directory;
            _policy = policy;
            _cities = Open(Path.Combine(directory, CityFileName));
            NewVersion();
        }
        finally
        {
            _file.ExitWriteLock();
        }
        Download.Refresh();
    }

    /// <summary>Ends a running download and closes the city data, and completes when both are done.</summary>
    internal async Task Detach()
    {
        await Download.Cancel();
        _file.EnterWriteLock();
        try
        {
            _cities?.Dispose();
            _cities = null;
            _directory = null;
            _policy = null;
            NewVersion();
        }
        finally
        {
            _file.ExitWriteLock();
        }
        Download.Refresh();
    }

    /// <summary>The location of <paramref name="address"/>, or null when the data lacks it.</summary>
    internal Location? Find(IPAddress address)
    {
        _file.EnterReadLock();
        try
        {
            var country = _countries?.Find<Record>(address);
            var city = _cities?.Find<Record>(address);
            var place = country?.Country ?? city?.Country;
            if (place?.Code is not { } code)
            {
                return null;
            }
            var point = city?.Coordinates?.Point;
            return _locations.GetOrAdd(
                (code, point, city?.City?.Name("en")),
                static (key, found) => new Location(key.Code, found.Country, found.City, key.Point),
                (Country: _countryPlaces.GetOrAdd(code, place), City: city?.City)
            );
        }
        // A damaged file gives no locations, as a missing one does.
        catch (InvalidDatabaseException)
        {
            return null;
        }
        finally
        {
            _file.ExitReadLock();
        }
    }

    public void Dispose()
    {
        _file.EnterWriteLock();
        try
        {
            _cities?.Dispose();
            _cities = null;
            _countries?.Dispose();
            _countries = null;
        }
        finally
        {
            _file.ExitWriteLock();
        }
    }

    // Downloads the month's archive and unpacks it to path, beside the
    // installed file, which stays in use until the replacement.
    public async Task Build(
        string path,
        Action<WorkDescription, WorkProgress> report,
        CancellationToken cancellation
    )
    {
        if (_directory is not { } directory || _policy is not { } policy)
            throw new InvalidOperationException("The city data serves no folder.");
        var archive = Path.Combine(directory, CityFileName + ".gz");
        var fresh = FreshRequest.Once(Access);
        try
        {
            await Fetch(
                archive,
                policy,
                fresh,
                progress => report(Downloading, progress),
                cancellation
            );
            await Task.Run(
                () =>
                    Unpack(
                        archive,
                        path,
                        percent =>
                            report(
                                new WorkDescription.City(CityPhase.Unpacking),
                                new WorkProgress.Fraction(percent, 100, WorkUnit.Percent, true)
                            ),
                        cancellation
                    ),
                cancellation
            );
        }
        finally
        {
            File.Delete(archive);
        }
    }

    // DB-IP publishes a month's file during its first hours, so the previous
    // month's file stands in until then.
    private async Task Fetch(
        string archive,
        RequestPolicy policy,
        FreshRequest fresh,
        Action<WorkProgress> progress,
        CancellationToken cancellation
    )
    {
        var now = DateTime.UtcNow;
        try
        {
            await Get(now).ConfigureAwait(false);
        }
        catch (ReplyException.Missing)
        {
            await Get(now.AddMonths(-1)).ConfigureAwait(false);
        }

        async Task Get(DateTime month)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://download.db-ip.com/free/dbip-city-lite-{month:yyyy-MM}.mmdb.gz")
            );
            var call = new ProviderCall(request, policy, new(Access))
            {
                Fresh = fresh,
                NamesResource = true,
            };
            await DataDownload.Fetch(_http, call, archive, progress, cancellation).ConfigureAwait(false);
        }
    }

    // Writes the file that replaces the installed copy, and checks first that
    // it is a readable database.
    private static void Unpack(
        string archive,
        string building,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using (var packed = File.OpenRead(archive))
        using (var content = new GZipStream(packed, CompressionMode.Decompress))
        using (var unpacked = new FileStream(building, FileMode.Create, FileAccess.ReadWrite))
        {
            var buffer = new byte[1 << 16];
            int count;
            while ((count = content.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                unpacked.Write(buffer, 0, count);
                report((int)Math.Min(99, packed.Position * 100 / packed.Length));
            }
            // A power cut after the move must not leave a file whose data
            // never reached the disk.
            unpacked.Flush(flushToDisk: true);
        }
        // The reader's exception belongs to the MaxMind library, so it is
        // translated here.
        try
        {
            using var reader = new Reader(building);
        }
        catch (InvalidDatabaseException error)
        {
            throw new DownloadException(DownloadFailure.Damaged, error);
        }
    }

    // Closes the city data around a change of its file, and serves the file
    // that remains afterwards.
    public void Replace(Action change)
    {
        _file.EnterWriteLock();
        try
        {
            _cities?.Dispose();
            _cities = null;
            change();
        }
        finally
        {
            if (_directory is { } directory)
                _cities = Open(Path.Combine(directory, CityFileName));
            NewVersion();
            _file.ExitWriteLock();
        }
    }

    // Called under the write lock, when the data lookups read has changed.
    private void NewVersion()
    {
        _countryPlaces.Clear();
        _locations.Clear();
        Version++;
    }

    // A missing or damaged file gives no locations; a damaged city file is
    // replaced by the next download.
    private static Reader? Open(string path)
    {
        try
        {
            return File.Exists(path) ? new Reader(path) : null;
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or InvalidDatabaseException)
        {
            return null;
        }
    }

    // The parts of a DB-IP record that the app reads; the reader fills the
    // constructor from the record's keys and leaves absent keys null.
    internal sealed class Record
    {
        [Constructor]
        public Record(
            [MapKey("country")] Place? country,
            [MapKey("city")] Place? city,
            [MapKey("location")] Coordinates? coordinates
        )
        {
            Country = country;
            City = city;
            Coordinates = coordinates;
        }

        internal Place? Country { get; }
        internal Place? City { get; }
        internal Coordinates? Coordinates { get; }
    }

    internal sealed class Coordinates
    {
        [Constructor]
        public Coordinates(
            [MapKey("latitude")] double? latitude,
            [MapKey("longitude")] double? longitude
        )
        {
            if (latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
                Point = new GeoPoint(longitude.Value, latitude.Value);
        }

        internal GeoPoint? Point { get; }
    }
}

/// <summary>A country or city with its names in the languages DB-IP ships.</summary>
internal sealed class Place
{
    private readonly IReadOnlyDictionary<string, string>? _names;

    [Constructor]
    public Place(
        [MapKey("iso_code")] string? code,
        [MapKey("names")] IReadOnlyDictionary<string, string>? names
    )
    {
        Code = code;
        _names = names;
    }

    /// <summary>The ISO 3166-1 alpha-2 code of a country; a city has none.</summary>
    internal string? Code { get; }

    /// <summary>The name in <paramref name="language"/>, in English when that language is missing, or null without names.</summary>
    internal string? Name(string language) =>
        _names is not null
        && (_names.TryGetValue(language, out var name) || _names.TryGetValue("en", out name))
            ? name
            : null;
}

/// <summary>Where an address is: its country, whose <paramref name="Code"/> also names the flag, and with city data its city.</summary>
internal sealed record Location(string Code, Place Country, Place? City, GeoPoint? Point)
{
    /// <summary>The country's name in <paramref name="language"/>, or its code when the data names it in no language.</summary>
    internal string CountryName(string language) => Country.Name(language) ?? Code;
}

/// <summary>
/// A place the peer map groups peers into. With Countries detail it is a whole
/// country; with Cities detail it is one coordinate, or the country's peers
/// that have none.
/// </summary>
internal readonly record struct PeerPlace(string Code, GeoPoint? Point, bool Cities)
{
    internal static PeerPlace Of(Location location, bool cities) =>
        new(location.Code, cities ? location.Point : null, cities);

    internal bool Contains(Location? location) =>
        location is not null && Of(location, Cities) == this;
}
